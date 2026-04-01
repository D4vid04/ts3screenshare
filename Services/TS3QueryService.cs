using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TS3ScreenShare.Services
{
    public sealed record TS3ChannelInfo(string Id, string Name, string ParentId);
    public sealed record TS3ClientInfo(string Id, string Nickname, string ChannelId, bool IsSelf);

    class TS3QueryService
    {
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // ReadLoopAsync is the sole reader of _reader.
        // SendAsync reads responses from this channel instead of directly from _reader.
        private Channel<string> _responseQueue = Channel.CreateUnbounded<string>();

        // Async log writer — avoids synchronous file I/O on the hot read path
        private readonly Channel<string> _logChannel = Channel.CreateUnbounded<string>();

        private readonly ConcurrentDictionary<string, TS3ChannelInfo> _channels = new();
        private readonly ConcurrentDictionary<string, TS3ClientInfo> _clients = new();

        public bool IsConnected { get; private set; }
        public string? MyClientId { get; private set; }
        public string? MyClientDbId { get; private set; }
        public string? MyChannelId { get; private set; }
        public string? MyUsername { get; private set; }

        public event Action<string, string>? ChannelChanged; // oldId, newId
        public event Action<IReadOnlyList<TS3ChannelInfo>, IReadOnlyList<TS3ClientInfo>>? RosterUpdated;
        public event Action? Disconnected;

        private const string Host = "127.0.0.1";
        private const int Port = 25639;

        public TS3QueryService()
        {
            // Start the background log writer for the lifetime of this service
            _ = Task.Run(LogWriterAsync);
        }

        public async Task ConnectAsync(string? apiKey = null)
        {
            // Fresh queue for each connection
            _responseQueue = Channel.CreateUnbounded<string>();

            _client = new TcpClient();
            await _client.ConnectAsync(Host, Port);

            var stream = _client.GetStream();
            _reader = new StreamReader(stream);
            _writer = new StreamWriter(stream) { AutoFlush = true };

            // Read greeting manually — loop is not running yet
            using var greetingCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string? line;
            try
            {
                while ((line = await _reader.ReadLineAsync(greetingCts.Token)) != null)
                {
                    Log($"Greeting: {line}");
                    if (line.StartsWith("selected schandlerid=")) break;
                }
            }
            catch (OperationCanceledException) { Log("Greeting timeout — continuing"); }

            // Start loop BEFORE the first command — it is the sole reader from now on
            _cts = new CancellationTokenSource();
            _ = ReadLoopAsync(_cts.Token);

            if (!string.IsNullOrEmpty(apiKey))
            {
                await SendAsync($"auth apikey={apiKey}");
                Log("Auth OK");
            }

            IsConnected = true;
            Log("TS3 ClientQuery ready");
        }

        public async Task InitializeAsync()
        {
            try
            {
                await SendAsync("clientnotifyregister schandlerid=1 event=any");
                Log("Notifications registered");
            }
            catch (Exception ex)
            {
                Log($"WARN: Notification registration failed: {ex.Message}");
            }

            var whoamiRaw = await SendAsync("whoami");
            Log($"whoami: {whoamiRaw}");

            var whoami = ParseLine(whoamiRaw);
            MyClientId = whoami.GetValueOrDefault("clid");
            MyClientDbId = whoami.GetValueOrDefault("client_database_id");
            MyChannelId = whoami.GetValueOrDefault("cid") ?? whoami.GetValueOrDefault("channel_id");

            if (string.IsNullOrEmpty(MyClientId))
                throw new Exception($"whoami did not return clid. Response: {whoamiRaw}");

            var listRaw = await SendAsync("clientlist");
            Log($"clientlist: {listRaw[..Math.Min(200, listRaw.Length)]}");

            foreach (var entry in listRaw.Split('|'))
            {
                var data = ParseLine(entry);
                if (data.GetValueOrDefault("clid") == MyClientId)
                {
                    MyUsername = data.GetValueOrDefault("client_nickname");
                    MyClientDbId = data.GetValueOrDefault("client_database_id");
                    break;
                }
            }

            if (string.IsNullOrEmpty(MyUsername))
                MyUsername = $"ts3_{MyClientId}";

            Log($"Done: username={MyUsername}, channelId={MyChannelId}");

            var channelRaw = await SendAsync("channellist");
            ParseChannelList(channelRaw);
            ParseClientList(listRaw);
            FireRosterUpdated();
        }

        private void ParseChannelList(string raw)
        {
            _channels.Clear();
            foreach (var entry in raw.Split('|'))
            {
                var d = ParseLine(entry);
                var id = d.GetValueOrDefault("cid") ?? "";
                var name = d.GetValueOrDefault("channel_name") ?? id;
                var pid = d.GetValueOrDefault("pid") ?? "0";
                if (!string.IsNullOrEmpty(id))
                    _channels[id] = new TS3ChannelInfo(id, name, pid);
            }
            Log($"Loaded {_channels.Count} channels");
        }

        private void ParseClientList(string raw)
        {
            _clients.Clear();
            foreach (var entry in raw.Split('|'))
            {
                var d = ParseLine(entry);
                var id = d.GetValueOrDefault("clid") ?? "";
                var nick = d.GetValueOrDefault("client_nickname") ?? id;
                var cid = d.GetValueOrDefault("cid") ?? "";
                if (!string.IsNullOrEmpty(id))
                    _clients[id] = new TS3ClientInfo(id, nick, cid, id == MyClientId);
            }
            Log($"Loaded {_clients.Count} clients");
        }

        private void FireRosterUpdated()
            => RosterUpdated?.Invoke(_channels.Values.ToList(), _clients.Values.ToList());

        private async Task<string> SendAsync(string cmd)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (_writer == null)
                    throw new Exception("Not connected");

                Log($"→ {cmd}");
                await _writer.WriteLineAsync(cmd);

                string response = "";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    string line;
                    try
                    {
                        line = await _responseQueue.Reader.ReadAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new Exception($"Timeout waiting for TS3 response (command: {cmd[..Math.Min(40, cmd.Length)]})");
                    }
                    catch (ChannelClosedException)
                    {
                        throw new Exception("TS3 connection was interrupted");
                    }

                    Log($"← {line}");

                    if (line.StartsWith("error "))
                    {
                        var err = ParseLine(line["error ".Length..]);
                        var id = err.GetValueOrDefault("id");
                        if (id != "0")
                        {
                            var msg = err.GetValueOrDefault("msg") ?? "unknown error";
                            throw new Exception($"TS3 error {id}: {msg}");
                        }
                        break;
                    }

                    if (!string.IsNullOrEmpty(line))
                        response = line;
                }

                return response;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // Sole reader of _reader — feeds lines into _responseQueue or handles notifications
        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _reader != null)
                {
                    var line = await _reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (line.StartsWith("notify"))
                        HandleNotification(line);
                    else if (!string.IsNullOrEmpty(line))
                        await _responseQueue.Writer.WriteAsync(line, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"ReadLoop error: {ex.Message}"); }
            finally
            {
                _responseQueue.Writer.TryComplete();
                IsConnected = false;
                Disconnected?.Invoke();
            }
        }

        private void HandleNotification(string line)
        {
            var spaceIdx = line.IndexOf(' ');
            var eventName = spaceIdx >= 0 ? line[..spaceIdx] : line;
            var payload = spaceIdx >= 0 ? line[(spaceIdx + 1)..] : "";
            var data = ParseLine(payload);

            switch (eventName)
            {
                case "notifycliententerview":
                {
                    var clid = data.GetValueOrDefault("clid") ?? "";
                    var nick = data.GetValueOrDefault("client_nickname") ?? clid;
                    var cid = data.GetValueOrDefault("ctid") ?? "";
                    if (!string.IsNullOrEmpty(clid))
                    {
                        _clients[clid] = new TS3ClientInfo(clid, nick, cid, clid == MyClientId);
                        FireRosterUpdated();
                    }
                    break;
                }
                case "notifyclientleftview":
                {
                    var clid = data.GetValueOrDefault("clid") ?? "";
                    if (_clients.TryRemove(clid, out _))
                        FireRosterUpdated();
                    break;
                }
                case "notifyclientmoved":
                {
                    var clid = data.GetValueOrDefault("clid") ?? "";
                    var newCid = data.GetValueOrDefault("ctid") ?? "";
                    if (_clients.TryGetValue(clid, out var existing))
                    {
                        _clients[clid] = existing with { ChannelId = newCid };
                        FireRosterUpdated();
                    }
                    if (clid == MyClientId && !string.IsNullOrEmpty(newCid))
                    {
                        var old = MyChannelId ?? "";
                        MyChannelId = newCid;
                        ChannelChanged?.Invoke(old, newCid);
                    }
                    break;
                }
                case "notifyclientupdated":
                {
                    var clid = data.GetValueOrDefault("clid") ?? "";
                    var nick = data.GetValueOrDefault("client_nickname");
                    if (!string.IsNullOrEmpty(clid) && nick != null && _clients.TryGetValue(clid, out var existing))
                    {
                        _clients[clid] = existing with { Nickname = nick };
                        FireRosterUpdated();
                    }
                    break;
                }
                case "notifychannelcreated":
                {
                    var cid  = data.GetValueOrDefault("cid")  ?? "";
                    var name = data.GetValueOrDefault("channel_name") ?? cid;
                    var pid  = data.GetValueOrDefault("cpid") ?? "0";
                    if (!string.IsNullOrEmpty(cid))
                    {
                        _channels[cid] = new TS3ChannelInfo(cid, name, pid);
                        FireRosterUpdated();
                    }
                    break;
                }
                case "notifychanneldeleted":
                {
                    var cid = data.GetValueOrDefault("cid") ?? "";
                    if (_channels.TryRemove(cid, out _))
                        FireRosterUpdated();
                    break;
                }
                case "notifychanneledited":
                {
                    var cid  = data.GetValueOrDefault("cid") ?? "";
                    var name = data.GetValueOrDefault("channel_name");
                    if (!string.IsNullOrEmpty(cid) && name != null && _channels.TryGetValue(cid, out var ch))
                    {
                        _channels[cid] = ch with { Name = name };
                        FireRosterUpdated();
                    }
                    break;
                }
                default:
                    Log($"Notification: {line[..Math.Min(80, line.Length)]}");
                    break;
            }
        }

        public static Dictionary<string, string> ParseLine(string line)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(line)) return result;

            foreach (var pair in line.Trim().Split(' '))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair[..eq];
                var val = pair[(eq + 1)..];
                result[key] = DecodeTS3String(val);
            }
            return result;
        }

        private static string DecodeTS3String(string s)
            => s.Replace("\\s", " ").Replace("\\p", "|").Replace("\\n", "\n").Replace("\\\\", "\\");

        public async Task SetAwayMessageAsync(string? token)
        {
            if (token != null)
                await SendAsync($"clientupdate client_away=1 client_away_message={EncodeTs3String(token)}");
            else
                await SendAsync("clientupdate client_away=0");
        }

        private static string EncodeTs3String(string s)
            => s.Replace("\\", "\\\\").Replace(" ", "\\s").Replace("|", "\\p").Replace("\n", "\\n");

        public void Disconnect()
        {
            _cts?.Cancel();
            _writer?.Close();
            _reader?.Close();
            _client?.Close();
            IsConnected = false;
        }

        private static readonly string LogFile = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ts3query.log");

        private void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            _logChannel.Writer.TryWrite(line);
        }

        private async Task LogWriterAsync()
        {
            using var writer = new StreamWriter(LogFile, append: true) { AutoFlush = false };
            await foreach (var line in _logChannel.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        }
    }
}
