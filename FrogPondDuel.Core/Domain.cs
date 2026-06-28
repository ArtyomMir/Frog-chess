namespace FrogPondDuel.Core;

public enum FrogSide
{
    Emerald,
    Amber
}

public enum MatchStatus
{
    WaitingForSecondPlayer,
    Playing,
    Finished
}

public enum MatchEventKind
{
    Created,
    Joined,
    BoardGenerated,
    FrogRemoved,
    JumpCompleted,
    TurnSkipped,
    Finished,
    Connected,
    Disconnected
}

public readonly record struct BoardPoint(int Row, int Col)
{
    public const int Size = 8;
    public const int InnerMin = 1;
    public const int InnerMax = 6;

    public bool IsInside => Row >= 0 && Row < Size && Col >= 0 && Col < Size;
    public bool IsInnerCell => Row is >= InnerMin and <= InnerMax && Col is >= InnerMin and <= InnerMax;
    public bool IsSwamp => IsInside && !IsInnerCell;

    public static BoardPoint Middle(BoardPoint first, BoardPoint second) =>
        new((first.Row + second.Row) / 2, (first.Col + second.Col) / 2);
}

public sealed record FrogPiece(Guid Id, Guid OwnerId, FrogSide Side, BoardPoint Position);

public sealed record PlayerTicket(Guid PlayerId, Guid PlayerToken, string Name, FrogSide Side);

public sealed record PlayerView(
    Guid PlayerId,
    string Name,
    FrogSide Side,
    bool RemovedOpeningFrog,
    bool MadeJump,
    bool IsOnline,
    DateTimeOffset? DisconnectedAt);

public sealed record MatchEvent(long Number, MatchEventKind Kind, string Text, DateTimeOffset At);

public sealed record MatchSnapshot(
    Guid MatchId,
    MatchStatus Status,
    PlayerView PlayerOne,
    PlayerView? PlayerTwo,
    Guid? TurnPlayerId,
    Guid? WinnerPlayerId,
    Guid? LastJumpPlayerId,
    int ConsecutiveSkips,
    long Version,
    IReadOnlyList<FrogPiece> Board,
    IReadOnlyList<MatchEvent> Events);

public sealed record JoinMatchResult(PlayerTicket Player, MatchSnapshot Match);

public sealed record JumpReport(
    BoardPoint From,
    BoardPoint To,
    int Captured,
    bool FinishedInSwamp);

public sealed class FrogRuleException(string message) : InvalidOperationException(message);
