using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using TS3ScreenShare.Shared;

namespace TS3ScreenShare.Server;

/// <summary>
/// Periodically checks that all authenticated relay clients are still connected to the TS3 server.
/// Disconnects any client that has left the TS3 server.
/// </summary>
public sealed class TS3PresenceWatcher(
    StreamRegistry registry,
    TS3ServerQueryService ts3Query,
    IHubContext<StreamHub> hubContext,
    IOptionsMonitor<TS3ServerQueryOptions> queryOpts,
    ILogger<TS3PresenceWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queryOpts.CurrentValue.IsConfigured)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            var verified = registry.GetVerifiedClients();
            if (verified.Count == 0) continue;

            IReadOnlySet<string> online;
            try
            {
                online = await ts3Query.GetOnlineClientDbIdsAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PresenceWatcher: failed to query TS3 online clients");
                continue;
            }

            foreach (var (connectionId, clientDbId) in verified)
            {
                if (online.Contains(clientDbId)) continue;

                logger.LogInformation("PresenceWatcher: cldbid={DbId} left TS3, disconnecting from relay", clientDbId);

                // Notify client
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(HubEvents.AuthFailed, "You have disconnected from the TeamSpeak server.", stoppingToken);
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(HubEvents.ForceDisconnect, stoppingToken);

                // Server-side cleanup — stop any active stream and remove from registry
                if (registry.TryRemoveByConnection(connectionId, out var streamInfo) && streamInfo != null)
                {
                    await hubContext.Clients.Group($"stream-{streamInfo.StreamId}")
                        .SendAsync(HubEvents.StreamRemoved, streamInfo.StreamId, stoppingToken);
                    logger.LogInformation("PresenceWatcher: stopped stream {StreamId} for cldbid={DbId}", streamInfo.StreamId, clientDbId);
                }
                registry.RemoveClient(connectionId);
            }
        }
    }
}
