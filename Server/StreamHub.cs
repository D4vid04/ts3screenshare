using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TS3ScreenShare.Shared;

namespace TS3ScreenShare.Server;

public sealed class StreamHub(
    StreamRegistry registry,
    TS3ServerQueryService ts3Query,
    IOptionsMonitor<TS3ServerQueryOptions> queryOpts,
    ILogger<StreamHub> logger) : Hub
{
    // ── Identity verification via TS3 away message ──────────────────────────

    public async Task RequestAuth()
    {
        // If ServerQuery is not configured, skip verification
        if (!queryOpts.CurrentValue.IsConfigured)
        {
            await Clients.Caller.SendAsync(HubEvents.AuthSuccess, "");
            return;
        }

        var challenge = Guid.NewGuid().ToString("N");
        registry.SetPendingChallenge(Context.ConnectionId, challenge);
        await Clients.Caller.SendAsync(HubEvents.AuthChallenge, challenge);

        logger.LogDebug("Auth challenge sent: connId={ConnId}", Context.ConnectionId[..8]);
    }

    public async Task ConfirmAuth(string challenge)
    {
        if (!registry.TryConsumePendingChallenge(Context.ConnectionId, out var expected)
            || expected != challenge)
        {
            await Clients.Caller.SendAsync(HubEvents.AuthFailed, "Invalid or expired token.");
            return;
        }

        string? clientDbId;
        try
        {
            clientDbId = await ts3Query.FindClientByAwayMessageAsync(challenge);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ConfirmAuth: ServerQuery failed for connId={ConnId}", Context.ConnectionId[..8]);
            await Clients.Caller.SendAsync(HubEvents.AuthFailed, $"Verification error: {ex.Message}");
            return;
        }

        if (clientDbId == null)
        {
            await Clients.Caller.SendAsync(HubEvents.AuthFailed,
                "No TS3 client found with this token. Make sure you are connected to TeamSpeak.");
            return;
        }

        registry.SetVerifiedClientDbId(Context.ConnectionId, clientDbId);
        logger.LogInformation("Auth verified: connId={ConnId} → cldbid={DbId}",
            Context.ConnectionId[..8], clientDbId);

        await Clients.Caller.SendAsync(HubEvents.AuthSuccess, clientDbId);
    }

    // ── Client announces its current TS3 channel ────────────────────────────
    public async Task JoinChannel(string channelId, string channelName = "")
    {
        // Must be authenticated
        var clientDbId = registry.GetVerifiedClientDbId(Context.ConnectionId);
        if (clientDbId == null)
        {
            await Clients.Caller.SendAsync(HubEvents.ConnectionDenied, "Not authenticated.");
            Context.Abort();
            return;
        }

        // Check server groups if allow or block lists are configured
        var opts = queryOpts.CurrentValue;
        if (opts.ConnectionAllowCheckEnabled || opts.ConnectionBlockEnabled)
        {
            IReadOnlyList<string>? groups = null;
            try { groups = await ts3Query.GetServerGroupsByDbIdAsync(clientDbId); }
            catch (Exception ex)
            {
                logger.LogError(ex, "JoinChannel: ServerQuery failed for cldbid={Cldbid}", clientDbId);
            }

            if (groups != null)
            {
                if (opts.ConnectionBlockEnabled && groups.Any(g => opts.ConnectionBlockedGroupIds.Contains(g)))
                {
                    logger.LogWarning("JoinChannel denied: cldbid={Cldbid} has blocked group [{Groups}]",
                        clientDbId, string.Join(",", groups));
                    await Clients.Caller.SendAsync(HubEvents.ConnectionDenied, "You are not allowed to connect.");
                    Context.Abort();
                    return;
                }

                if (opts.ConnectionAllowCheckEnabled && !groups.Any(g => opts.ConnectionAllowedGroupIds.Contains(g)))
                {
                    logger.LogWarning("JoinChannel denied: cldbid={Cldbid} lacks required group [{Groups}]",
                        clientDbId, string.Join(",", groups));
                    await Clients.Caller.SendAsync(HubEvents.ConnectionDenied, "You are not allowed to connect.");
                    Context.Abort();
                    return;
                }
            }
        }

        var oldChannelId = registry.SetClientChannel(Context.ConnectionId, channelId);

        if (oldChannelId != null)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channel-{oldChannelId}");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channel-{channelId}");

        // If this client has an active stream, move it to the new channel
        if (oldChannelId != null && oldChannelId != channelId)
        {
            var moved = registry.MoveStreamToChannel(Context.ConnectionId, channelId, string.IsNullOrEmpty(channelName) ? channelId : channelName);
            if (moved.HasValue)
            {
                var (oldInfo, newInfo) = moved.Value;
                await Clients.Group($"channel-{oldInfo.ChannelId}").SendAsync(HubEvents.StreamRemoved, oldInfo.StreamId);
                await Clients.Group($"channel-{newInfo.ChannelId}").SendAsync(HubEvents.StreamAdded, newInfo);
                logger.LogInformation("Stream {StreamId} moved from channel {Old} to {New}", newInfo.StreamId, oldInfo.ChannelId, newInfo.ChannelId);
            }
        }

        var visible = registry.GetByChannel(channelId);
        await Clients.Caller.SendAsync(HubEvents.StreamsReset, visible);

        logger.LogInformation("Client {ConnId} joined channel {ChannelId} ({Count} streams visible)",
            Context.ConnectionId[..8], channelId, visible.Count);
    }

    // ── Streamer registers a stream ─────────────────────────────────────────
    public async Task RegisterStream(string streamId, string channelId, string channelName,
        string username, int audioSampleRate = 0, int audioChannels = 0)
    {
        // Must be authenticated — always required regardless of group config
        var clientDbId = registry.GetVerifiedClientDbId(Context.ConnectionId);
        if (clientDbId == null)
        {
            logger.LogWarning("RegisterStream denied: {Username} has no verified identity", username);
            await Clients.Caller.SendAsync(HubEvents.StreamDenied, "Identity not verified. Reconnect and try again.");
            return;
        }

        // Check server groups if configured
        if (queryOpts.CurrentValue.StreamingGroupCheckEnabled)
        {

            IReadOnlyList<string>? groups;
            try { groups = await ts3Query.GetServerGroupsByDbIdAsync(clientDbId); }
            catch (Exception ex)
            {
                logger.LogError(ex, "RegisterStream denied: ServerQuery failed for cldbid={Cldbid}", clientDbId);
                await Clients.Caller.SendAsync(HubEvents.StreamDenied, $"Verification error: {ex.Message}");
                return;
            }

            var allowed = queryOpts.CurrentValue.StreamingAllowedGroupIds;
            bool hasPermission = groups != null && groups.Any(g => allowed.Contains(g));
            if (!hasPermission)
            {
                logger.LogWarning("RegisterStream denied: {Username} (cldbid={Cldbid}) lacks required group. Groups: [{Groups}]",
                    username, clientDbId, groups == null ? "?" : string.Join(",", groups));
                await Clients.Caller.SendAsync(HubEvents.StreamDenied, "You are not allowed to stream.");
                return;
            }
        }

        // Check channel restrictions
        if (!queryOpts.CurrentValue.IsChannelAllowed(channelId))
        {
            logger.LogWarning("RegisterStream denied: channel {ChannelId} is not allowed", channelId);
            await Clients.Caller.SendAsync(HubEvents.StreamDenied, "Streaming is not allowed in this channel.");
            return;
        }

        var info = new StreamInfo(streamId, username, channelId, channelName, DateTimeOffset.UtcNow,
            audioSampleRate, audioChannels);

        if (!registry.TryAdd(Context.ConnectionId, info))
        {
            logger.LogWarning("Stream {StreamId} already exists", streamId);
            return;
        }

        logger.LogInformation("Stream registered: {StreamId} by {Username} in channel {ChannelId}", streamId, username, channelId);

        // Notify only clients in the same channel
        await Clients.Group($"channel-{channelId}").SendAsync(HubEvents.StreamAdded, info);
    }


    // ── Streamer stops the stream ────────────────────────────────────────────
    public async Task StopStream(string streamId)
    {
        if (!registry.TryRemove(streamId, Context.ConnectionId, out var info) || info is null)
            return;

        logger.LogInformation("Stream stopped: {StreamId}", streamId);

        // StreamRemoved is broadcast to all — safe, clients ignore unknown stream IDs
        await Clients.All.SendAsync(HubEvents.StreamRemoved, streamId);
    }

    // ── Viewer subscribes to a stream ───────────────────────────────────────
    public async Task WatchStream(string streamId)
    {
        if (string.IsNullOrEmpty(streamId)) return;

        // Must be authenticated
        if (registry.GetVerifiedClientDbId(Context.ConnectionId) == null)
        {
            await Clients.Caller.SendAsync(HubEvents.AuthFailed, "Not authenticated.");
            return;
        }

        // Stream must exist
        if (!registry.TryGet(streamId, out var streamEntry) || streamEntry is null)
            return;

        // Viewer must be in the same channel as the stream
        var viewerChannel = registry.GetClientChannel(Context.ConnectionId);
        if (viewerChannel != streamEntry.Info.ChannelId)
        {
            logger.LogWarning("WatchStream denied: viewer {ConnId} in channel {ViewerChannel} tried to watch stream in channel {StreamChannel}",
                Context.ConnectionId[..8], viewerChannel, streamEntry.Info.ChannelId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"stream-{streamId}");
        logger.LogInformation("Viewer {ConnId} watching {StreamId}", Context.ConnectionId[..8], streamId);
    }

    // ── Viewer unsubscribes from a stream ───────────────────────────────────
    public async Task UnwatchStream(string streamId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"stream-{streamId}");
    }

    // ── Streamer sends a video frame (relayed to all viewers) ───────────────
    public async Task SendVideoFrame(string streamId, byte[] frame)
    {
        if (string.IsNullOrEmpty(streamId) || frame is null)
            return;
        if (!registry.TryGet(streamId, out var entry) || entry?.StreamerConnectionId != Context.ConnectionId)
            return;
        await Clients.Group($"stream-{streamId}").SendAsync(HubEvents.ReceiveVideoFrame, streamId, frame);
    }

    // ── Streamer sends an audio frame (relayed to all viewers) ──────────────
    public async Task SendAudioFrame(string streamId, byte[] frame)
    {
        if (string.IsNullOrEmpty(streamId) || frame is null)
            return;
        if (!registry.TryGet(streamId, out var entry) || entry?.StreamerConnectionId != Context.ConnectionId)
            return;
        await Clients.Group($"stream-{streamId}").SendAsync(HubEvents.ReceiveAudioFrame, streamId, frame);
    }

    // ── Connection lifecycle ─────────────────────────────────────────────────
    public override async Task OnConnectedAsync()
    {
        // Waits for JoinChannel — client sees no streams until then
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveClient(Context.ConnectionId);

        if (registry.TryRemoveByConnection(Context.ConnectionId, out var info) && info is not null)
        {
            logger.LogInformation("Streamer disconnected, removing stream {StreamId}", info.StreamId);
            await Clients.All.SendAsync(HubEvents.StreamRemoved, info.StreamId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
