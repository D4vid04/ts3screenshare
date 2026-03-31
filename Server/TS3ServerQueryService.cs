using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace TS3ScreenShare.Server;

/// <summary>
/// Connects to the TS3 ServerQuery (port 10011) and verifies users' server groups.
/// Each query opens its own TCP connection — simple, no keepalive concerns.
/// </summary>
public sealed class TS3ServerQueryService(IOptionsMonitor<TS3ServerQueryOptions> opts, ILogger<TS3ServerQueryService> logger)
{
    private readonly IOptionsMonitor<TS3ServerQueryOptions> _opts = opts;

    /// <summary>
    /// Returns server group IDs for the client with the given client_database_id.
    /// Returns null if ServerQuery is not configured (= skip verification).
    /// </summary>
    public async Task<IReadOnlyList<string>?> GetServerGroupsByDbIdAsync(string clientDbId)
    {
        var o = _opts.CurrentValue;
        if (!o.IsConfigured)
            return null;

        try
        {
            using var conn = new ServerQueryConnection(o.Host, o.Port);
            await conn.ConnectAsync();
            await conn.LoginAsync(o.Username, o.Password);
            await conn.UseAsync(o.VirtualServerId);

            var groups = await conn.GetServerGroupsAsync(clientDbId);
            logger.LogInformation("ServerQuery: cldbid={Cldbid} groups=[{Groups}]",
                clientDbId, string.Join(",", groups));
            return groups;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ServerQuery: error verifying cldbid={Cldbid}", clientDbId);
            throw;
        }
    }

    /// <summary>
    /// Finds the client whose away message contains the token "TS3SS-{token}".
    /// Retries up to retries times with delayMs delay (away message may propagate with a delay).
    /// Returns client_database_id, or null if the client was not found.
    /// </summary>
    public async Task<string?> FindClientByAwayMessageAsync(
        string token, int retries = 8, int delayMs = 500)
    {
        var o = _opts.CurrentValue;
        if (!o.IsConfigured)
            return null;

        var expected = $"TS3SS-{token}";

        for (int attempt = 0; attempt < retries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(delayMs);

            try
            {
                using var conn = new ServerQueryConnection(o.Host, o.Port);
                await conn.ConnectAsync();
                await conn.LoginAsync(o.Username, o.Password);
                await conn.UseAsync(o.VirtualServerId);

                var clientDbId = await conn.FindByAwayMessageAsync(expected);
                if (clientDbId != null)
                {
                    logger.LogInformation("Auth: client found with token, cldbid={DbId}", clientDbId);
                    return clientDbId;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auth: error searching for client (attempt {Attempt})", attempt + 1);
            }
        }

        logger.LogWarning("Auth: client with token {Token} not found after {Retries} attempts", expected, retries);
        return null;
    }

    // ── Internal TCP wrapper ─────────────────────────────────────────────────

    private sealed class ServerQueryConnection(string host, int port) : IDisposable
    {
        private TcpClient? _tcp;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async Task ConnectAsync()
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(host, port);
            var stream = _tcp.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

            // Read banner (2 lines: "TS3" + info)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _reader.ReadLineAsync(cts.Token); // "TS3"
            await _reader.ReadLineAsync(cts.Token); // "Welcome to the..."
        }

        public async Task LoginAsync(string username, string password)
            => await SendAsync($"login client_login_name={Escape(username)} client_login_password={Escape(password)}");

        public async Task UseAsync(int virtualServerId)
            => await SendAsync($"use sid={virtualServerId}");

        public async Task<string?> FindByAwayMessageAsync(string expectedAwayMessage)
        {
            var raw = await SendAsync("clientlist -away");
            foreach (var entry in raw.Split('|'))
            {
                var d = ParseLine(entry);
                // Ignore ServerQuery clients (type=1)
                if (d.GetValueOrDefault("client_type") == "1") continue;
                var awayMsg = d.GetValueOrDefault("client_away_message") ?? "";
                if (awayMsg.Equals(expectedAwayMessage, StringComparison.Ordinal))
                    return d.GetValueOrDefault("client_database_id");
            }
            return null;
        }

        public async Task<IReadOnlyList<string>> GetServerGroupsAsync(string cldbid)
        {
            var raw = await SendAsync($"servergroupsbyclientid cldbid={cldbid}");
            var groups = new List<string>();
            foreach (var entry in raw.Split('|'))
            {
                var d = ParseLine(entry);
                var sgid = d.GetValueOrDefault("sgid");
                if (!string.IsNullOrEmpty(sgid)) groups.Add(sgid);
            }
            return groups;
        }

        private async Task<string> SendAsync(string cmd)
        {
            await _lock.WaitAsync();
            try
            {
                await _writer!.WriteLineAsync(cmd);
                string response = "";
                while (true)
                {
                    var line = await _reader!.ReadLineAsync()
                        ?? throw new Exception("ServerQuery: connection interrupted");

                    if (line.StartsWith("error "))
                    {
                        var err = ParseLine(line["error ".Length..]);
                        var id = err.GetValueOrDefault("id");
                        if (id != "0")
                        {
                            var msg = err.GetValueOrDefault("msg") ?? "unknown error";
                            throw new Exception($"TS3 ServerQuery error {id}: {Unescape(msg)}");
                        }
                        break;
                    }

                    if (!string.IsNullOrEmpty(line))
                        response = line;
                }
                return response;
            }
            finally { _lock.Release(); }
        }

        private static Dictionary<string, string> ParseLine(string line)
        {
            var result = new Dictionary<string, string>();
            foreach (var pair in line.Trim().Split(' '))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                result[pair[..eq]] = pair[(eq + 1)..];
            }
            return result;
        }

        private static string Escape(string s)
            => s.Replace("\\", "\\\\").Replace(" ", "\\s").Replace("|", "\\p");

        private static string Unescape(string s)
            => s.Replace("\\s", " ").Replace("\\p", "|").Replace("\\\\", "\\");

        public void Dispose()
        {
            _lock.Dispose();
            _reader?.Dispose();
            _writer?.Dispose();
            _tcp?.Dispose();
        }
    }
}
