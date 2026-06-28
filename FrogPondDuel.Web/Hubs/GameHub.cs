using FrogPondDuel.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace FrogPondDuel.Web.Hubs;

public sealed class GameHub(
    GameRoomStore store,
    IHubContext<GameHub> hubContext,
    ILogger<GameHub> logger) : Hub
{
    private const string MatchIdKey = "matchId";
    private const string PlayerTokenKey = "playerToken";

    public static string GroupName(Guid matchId) => $"match:{matchId:N}";

    public async Task WatchGame(Guid matchId, Guid playerToken)
    {
        var snapshot = store.MarkConnected(matchId, playerToken);
        Context.Items[MatchIdKey] = matchId;
        Context.Items[PlayerTokenKey] = playerToken;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(matchId));
        await Clients.Group(GroupName(matchId)).SendAsync("stateChanged", snapshot);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(MatchIdKey, out var matchValue) &&
            Context.Items.TryGetValue(PlayerTokenKey, out var tokenValue) &&
            matchValue is Guid matchId &&
            tokenValue is Guid playerToken)
        {
            try
            {
                var snapshot = store.MarkDisconnected(matchId, playerToken);
                await hubContext.Clients.Group(GroupName(matchId)).SendAsync("stateChanged", snapshot);
                _ = WatchReconnectWindowAsync(matchId, playerToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not process disconnect for match {MatchId}", matchId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task WatchReconnectWindowAsync(Guid matchId, Guid playerToken)
    {
        try
        {
            await Task.Delay(GameRoomStore.ReconnectWindow);
            var result = store.ForfeitIfStillDisconnected(matchId, playerToken);
            if (result.Changed)
            {
                await hubContext.Clients.Group(GroupName(matchId)).SendAsync("stateChanged", result.Snapshot);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reconnect timer failed for match {MatchId}", matchId);
        }
    }
}
