using System.Collections.Concurrent;
using TS3ScreenShare.Shared;

namespace TS3ScreenShare.Server;

// Maintains the list of active streams and the connectionId → streamId mapping
public sealed class StreamRegistry
{
    private readonly ConcurrentDictionary<string, StreamEntry> _streams = new();
    private readonly ConcurrentDictionary<string, string> _streamerConnections = new(); // connectionId → streamId
    private readonly ConcurrentDictionary<string, string> _clientChannels = new();      // connectionId → channelId
    private readonly ConcurrentDictionary<string, string> _pendingChallenges = new();   // connectionId → challenge
    private readonly ConcurrentDictionary<string, string> _verifiedClients = new();     // connectionId → clientDbId

    public bool TryAdd(string connectionId, StreamInfo info)
    {
        var entry = new StreamEntry(info, connectionId);
        if (!_streams.TryAdd(info.StreamId, entry)) return false;
        _streamerConnections[connectionId] = info.StreamId;
        return true;
    }

    public bool TryRemoveByConnection(string connectionId, out StreamInfo? info)
    {
        info = null;
        if (!_streamerConnections.TryRemove(connectionId, out var streamId)) return false;
        if (!_streams.TryRemove(streamId, out var entry)) return false;
        info = entry.Info;
        return true;
    }

    public bool TryRemove(string streamId, string connectionId, out StreamInfo? info)
    {
        info = null;
        if (!_streams.TryGetValue(streamId, out var entry)) return false;
        if (entry.StreamerConnectionId != connectionId) return false;
        if (!_streams.TryRemove(streamId, out entry)) return false;
        _streamerConnections.TryRemove(connectionId, out _);
        info = entry.Info;
        return true;
    }

    public bool TryGet(string streamId, out StreamEntry? entry)
        => _streams.TryGetValue(streamId, out entry);

    public IReadOnlyList<StreamInfo> GetAll()
        => _streams.Values.Select(e => e.Info).ToList();

    /// <summary>
    /// Moves the stream owned by connectionId to a new channel.
    /// Returns the old and new StreamInfo, or null if no stream exists for that connection.
    /// </summary>
    public (StreamInfo Old, StreamInfo New)? MoveStreamToChannel(string connectionId, string newChannelId, string newChannelName)
    {
        if (!_streamerConnections.TryGetValue(connectionId, out var streamId)) return null;
        if (!_streams.TryGetValue(streamId, out var entry)) return null;

        var oldInfo = entry.Info;
        var newInfo = oldInfo with { ChannelId = newChannelId, ChannelName = newChannelName };
        var newEntry = new StreamEntry(newInfo, connectionId);

        _streams[streamId] = newEntry;
        return (oldInfo, newInfo);
    }

    public IReadOnlyList<StreamInfo> GetByChannel(string channelId)
        => _streams.Values.Where(e => e.Info.ChannelId == channelId).Select(e => e.Info).ToList();

    // Returns the old channelId (or null if the connection has no channel yet)
    public string? GetClientChannel(string connectionId)
        => _clientChannels.TryGetValue(connectionId, out var channelId) ? channelId : null;

    public string? SetClientChannel(string connectionId, string channelId)
    {
        _clientChannels.TryGetValue(connectionId, out var old);
        _clientChannels[connectionId] = channelId;
        return old;
    }

    public void RemoveClient(string connectionId)
    {
        _clientChannels.TryRemove(connectionId, out _);
        _pendingChallenges.TryRemove(connectionId, out _);
        _verifiedClients.TryRemove(connectionId, out _);
    }

    // ── Auth challenge ────────────────────────────────────────────────────────

    public void SetPendingChallenge(string connectionId, string challenge)
        => _pendingChallenges[connectionId] = challenge;

    public bool TryConsumePendingChallenge(string connectionId, out string challenge)
    {
        challenge = "";
        return _pendingChallenges.TryRemove(connectionId, out challenge!);
    }

    // ── Verified identity ────────────────────────────────────────────────────

    public void SetVerifiedClientDbId(string connectionId, string clientDbId)
        => _verifiedClients[connectionId] = clientDbId;

    public string? GetVerifiedClientDbId(string connectionId)
        => _verifiedClients.TryGetValue(connectionId, out var id) ? id : null;

    public bool IsStreamer(string connectionId)
        => _streamerConnections.ContainsKey(connectionId);

    public IReadOnlyList<(string ConnectionId, string ClientDbId)> GetVerifiedClients()
        => _verifiedClients.Select(kv => (kv.Key, kv.Value)).ToList();
}

public sealed class StreamEntry(StreamInfo info, string streamerConnectionId)
{
    public StreamInfo Info { get; } = info;
    public string StreamerConnectionId { get; } = streamerConnectionId;
    public ConcurrentDictionary<string, string> ViewerConnections { get; } = new(); // connectionId → peerId
}
