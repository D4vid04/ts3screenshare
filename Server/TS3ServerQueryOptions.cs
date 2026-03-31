namespace TS3ScreenShare.Server;

public sealed class TS3ServerQueryOptions
{
    public const string Section = "TS3ServerQuery";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 10011;
    public string Username { get; set; } = "serveradmin";
    public string Password { get; set; } = "";
    public int VirtualServerId { get; set; } = 1;

    // ── Streaming permissions ────────────────────────────────────────────────

    /// <summary>Server groups allowed to start a stream. Empty = everyone.</summary>
    public List<string> StreamingAllowedGroupIds { get; set; } = [];

    /// <summary>Channels where streaming is allowed. Empty = all channels.</summary>
    public List<string> StreamingAllowedChannelIds { get; set; } = [];

    /// <summary>Channels where streaming is NOT allowed. Takes precedence over StreamingAllowedChannelIds.</summary>
    public List<string> StreamingBlockedChannelIds { get; set; } = [];

    // ── Relay connection blocking ────────────────────────────────────────────

    /// <summary>Server groups allowed to connect to the relay. Empty = everyone is allowed.</summary>
    public List<string> ConnectionAllowedGroupIds { get; set; } = [];

    /// <summary>Server groups that cannot connect to the relay at all. Empty = nobody is blocked.</summary>
    public List<string> ConnectionBlockedGroupIds { get; set; } = [];

    // ── Computed properties ───────────────────────────────────────────────────

    /// <summary>ServerQuery is active only if a password is configured.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Password);

    /// <summary>Whether to check allowed server groups for streaming.</summary>
    public bool StreamingGroupCheckEnabled => IsConfigured && StreamingAllowedGroupIds.Count > 0;

    /// <summary>Whether to check allowed server groups for relay connections.</summary>
    public bool ConnectionAllowCheckEnabled => IsConfigured && ConnectionAllowedGroupIds.Count > 0;

    /// <summary>Whether to check blocked server groups for relay connections.</summary>
    public bool ConnectionBlockEnabled => IsConfigured && ConnectionBlockedGroupIds.Count > 0;

    /// <summary>Returns true if the channel is allowed for streaming.</summary>
    public bool IsChannelAllowed(string channelId)
    {
        if (StreamingBlockedChannelIds.Contains(channelId)) return false;
        if (StreamingAllowedChannelIds.Count > 0 && !StreamingAllowedChannelIds.Contains(channelId)) return false;
        return true;
    }
}
