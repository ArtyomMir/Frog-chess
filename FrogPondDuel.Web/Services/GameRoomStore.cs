using System.Collections.Concurrent;
using FrogPondDuel.Core;

namespace FrogPondDuel.Web.Services;

public sealed class GameRoomStore(ILogger<GameRoomStore> logger)
{
    public static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<Guid, FrogMatch> _matches = [];

    public JoinMatchResult CreateGame(string playerName)
    {
        var match = new FrogMatch(Guid.NewGuid(), playerName);
        if (!_matches.TryAdd(match.MatchId, match))
        {
            throw new InvalidOperationException("Could not register a new match.");
        }

        logger.LogInformation("Match {MatchId} created by {PlayerName}", match.MatchId, match.CreatorTicket.Name);
        return new JoinMatchResult(match.CreatorTicket, match.Snapshot());
    }

    public JoinMatchResult JoinGame(Guid matchId, string playerName)
    {
        var result = GetMatch(matchId).Join(playerName);
        logger.LogInformation("Player {PlayerName} joined match {MatchId}", result.Player.Name, matchId);
        return result;
    }

    public MatchSnapshot GetState(Guid matchId, Guid playerToken)
    {
        var match = GetMatch(matchId);
        match.Identify(playerToken);
        return match.Snapshot();
    }

    public MatchSnapshot RemoveOpeningFrog(Guid matchId, Guid playerToken, BoardPoint position)
    {
        var snapshot = GetMatch(matchId).RemoveOpeningFrog(playerToken, position);
        logger.LogInformation("Opening frog removed in match {MatchId} by token {PlayerToken}", matchId, playerToken);
        return snapshot;
    }

    public MatchSnapshot Jump(Guid matchId, Guid playerToken, IReadOnlyList<BoardPoint> path)
    {
        var snapshot = GetMatch(matchId).Jump(playerToken, path);
        logger.LogInformation("Jump accepted in match {MatchId} by token {PlayerToken}", matchId, playerToken);
        return snapshot;
    }

    public MatchSnapshot Pass(Guid matchId, Guid playerToken)
    {
        var snapshot = GetMatch(matchId).Pass(playerToken);
        logger.LogInformation("Pass accepted in match {MatchId} by token {PlayerToken}", matchId, playerToken);
        return snapshot;
    }

    public MatchSnapshot MarkConnected(Guid matchId, Guid playerToken)
    {
        var snapshot = GetMatch(matchId).MarkConnected(playerToken);
        logger.LogInformation("Player token {PlayerToken} connected to match {MatchId}", playerToken, matchId);
        return snapshot;
    }

    public MatchSnapshot MarkDisconnected(Guid matchId, Guid playerToken)
    {
        var snapshot = GetMatch(matchId).MarkDisconnected(playerToken, DateTimeOffset.UtcNow);
        logger.LogInformation("Player token {PlayerToken} disconnected from match {MatchId}", playerToken, matchId);
        return snapshot;
    }

    public (bool Changed, MatchSnapshot Snapshot) ForfeitIfStillDisconnected(Guid matchId, Guid playerToken)
    {
        var result = GetMatch(matchId).ForfeitIfStillDisconnected(playerToken, ReconnectWindow, DateTimeOffset.UtcNow);
        if (result.Changed)
        {
            logger.LogWarning("Match {MatchId} ended by disconnect timeout for token {PlayerToken}", matchId, playerToken);
        }

        return result;
    }

    private FrogMatch GetMatch(Guid matchId)
    {
        if (_matches.TryGetValue(matchId, out var match))
        {
            return match;
        }

        throw new KeyNotFoundException("Match not found.");
    }
}
