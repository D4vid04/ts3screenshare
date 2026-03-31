namespace TS3ScreenShare.Shared;

// Active stream info (returned by REST endpoint and SignalR)
public sealed record StreamInfo(
    string StreamId,
    string StreamerUsername,
    string ChannelId,
    string ChannelName,
    DateTimeOffset StartedAt,
    int AudioSampleRate = 0,   // 0 = no audio
    int AudioChannels = 0);

// WebRTC signaling messages
public sealed record SdpMessage(string StreamId, string PeerId, string Sdp, string Type);
public sealed record IceCandidateMessage(string StreamId, string PeerId, string Candidate, string SdpMid, int SdpMLineIndex);

// Hub methods (client → server)
public static class HubMethods
{
    public const string RegisterStream   = nameof(RegisterStream);
    public const string StopStream       = nameof(StopStream);
    public const string WatchStream      = nameof(WatchStream);
    public const string UnwatchStream    = nameof(UnwatchStream);
    public const string SendVideoFrame   = nameof(SendVideoFrame);
    public const string SendAudioFrame   = nameof(SendAudioFrame);
    public const string JoinChannel      = nameof(JoinChannel);
    public const string RequestAuth      = nameof(RequestAuth);
    public const string ConfirmAuth      = nameof(ConfirmAuth);
}

// Hub events (server → client)
public static class HubEvents
{
    public const string StreamAdded       = nameof(StreamAdded);
    public const string StreamRemoved     = nameof(StreamRemoved);
    public const string ReceiveVideoFrame = nameof(ReceiveVideoFrame);
    public const string ReceiveAudioFrame = nameof(ReceiveAudioFrame);
    public const string StreamsReset      = nameof(StreamsReset);
    public const string StreamDenied      = nameof(StreamDenied);
    public const string ConnectionDenied  = nameof(ConnectionDenied);
    // Identity verification via TS3 away message
    public const string AuthChallenge     = nameof(AuthChallenge);
    public const string AuthSuccess       = nameof(AuthSuccess);
    public const string AuthFailed        = nameof(AuthFailed);
}
