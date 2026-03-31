using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using TS3ScreenShare.Shared;

namespace TS3ScreenShare.Services
{
    public sealed class RelayService
    {
        private HubConnection? _hub;
        private CancellationTokenSource? _cts;

        private TaskCompletionSource<string?>? _authTcs;

        public bool IsConnected => _hub?.State == HubConnectionState.Connected;

        public event Action<StreamInfo>? StreamAdded;
        public event Action<string>? StreamRemoved;
        public event Action<IReadOnlyList<StreamInfo>>? StreamsReset;
        public event Action<string, byte[]>? VideoFrameReceived;
        public event Action<string, byte[]>? AudioFrameReceived;
        public event Action<string>? StreamDenied;
        public event Action<string>? ConnectionDenied;
        public event Action? Disconnected;
        // Auth events
        public event Action<string>? AuthChallengeReceived;
        public event Action<string>? AuthSucceeded;
        public event Action<string>? AuthFailed;

        public async Task ConnectAsync(string serverUrl)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl($"{serverUrl.TrimEnd('/')}/hub")
                .WithAutomaticReconnect()
                .Build();

            _hub.On<StreamInfo>(HubEvents.StreamAdded, info => StreamAdded?.Invoke(info));
            _hub.On<string>(HubEvents.StreamRemoved, id => StreamRemoved?.Invoke(id));
            _hub.On<IReadOnlyList<StreamInfo>>(HubEvents.StreamsReset, list => StreamsReset?.Invoke(list));
            _hub.On<string, byte[]>(HubEvents.ReceiveVideoFrame, (sid, frame) => VideoFrameReceived?.Invoke(sid, frame));
            _hub.On<string, byte[]>(HubEvents.ReceiveAudioFrame, (sid, frame) => AudioFrameReceived?.Invoke(sid, frame));
            _hub.On<string>(HubEvents.StreamDenied, msg => StreamDenied?.Invoke(msg));
            _hub.On<string>(HubEvents.ConnectionDenied, msg => ConnectionDenied?.Invoke(msg));
            _hub.On<string>(HubEvents.AuthChallenge, token =>
            {
                AuthChallengeReceived?.Invoke(token);
            });
            _hub.On<string>(HubEvents.AuthSuccess, clientDbId =>
            {
                AuthSucceeded?.Invoke(clientDbId);
                _authTcs?.TrySetResult(clientDbId);
            });
            _hub.On<string>(HubEvents.AuthFailed, reason =>
            {
                AuthFailed?.Invoke(reason);
                _authTcs?.TrySetException(new Exception(reason));
            });

            _hub.Closed += _ => { Disconnected?.Invoke(); return Task.CompletedTask; };

            _cts = new CancellationTokenSource();
            await _hub.StartAsync(_cts.Token);
        }

        /// <summary>
        /// Initiates identity verification via TS3 away message.
        /// Returns server-verified clientDbId, or "" if ServerQuery is not configured.
        /// Caller must handle AuthChallengeReceived (set away message and call ConfirmAuthAsync).
        /// </summary>
        public async Task<string?> RequestAuthAsync()
        {
            EnsureConnected();
            _authTcs = new TaskCompletionSource<string?>();
            await _hub!.InvokeAsync(HubMethods.RequestAuth);
            return await _authTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async Task ConfirmAuthAsync(string challenge)
        {
            EnsureConnected();
            await _hub!.InvokeAsync(HubMethods.ConfirmAuth, challenge);
        }

        public async Task JoinChannelAsync(string channelId)
        {
            EnsureConnected();
            await _hub!.InvokeAsync(HubMethods.JoinChannel, channelId);
        }

        public async Task RegisterStreamAsync(string streamId, string channelId, string channelName,
            string username, int audioSampleRate = 0, int audioChannels = 0)
        {
            EnsureConnected();
            await _hub!.InvokeAsync(HubMethods.RegisterStream, streamId, channelId, channelName,
                username, audioSampleRate, audioChannels);
        }

        public async Task StopStreamAsync(string streamId)
        {
            EnsureConnected();
            await _hub!.InvokeAsync(HubMethods.StopStream, streamId);
        }

        public async Task WatchStreamAsync(string streamId)
        {
            EnsureConnected();
            await _hub!.InvokeAsync(HubMethods.WatchStream, streamId);
        }

        public async Task UnwatchStreamAsync(string streamId)
        {
            EnsureConnected();
            await _hub!.InvokeAsync(HubMethods.UnwatchStream, streamId);
        }

        public async Task SendVideoFrameAsync(string streamId, byte[] frameBytes)
        {
            if (_hub?.State != HubConnectionState.Connected) return;
            await _hub.InvokeAsync(HubMethods.SendVideoFrame, streamId, frameBytes);
        }

        public async Task SendAudioFrameAsync(string streamId, byte[] frameBytes)
        {
            if (_hub?.State != HubConnectionState.Connected) return;
            await _hub.InvokeAsync(HubMethods.SendAudioFrame, streamId, frameBytes);
        }

        public async Task DisconnectAsync()
        {
            _cts?.Cancel();
            if (_hub is not null)
                await _hub.DisposeAsync();
            _hub = null;
        }

        private void EnsureConnected()
        {
            if (_hub?.State != HubConnectionState.Connected)
                throw new InvalidOperationException("Relay server is not connected");
        }
    }
}
