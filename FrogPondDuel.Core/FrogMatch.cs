namespace FrogPondDuel.Core;

public sealed class FrogMatch
{
    private static readonly BoardPoint[] Directions =
    [
        new(-1, -1),
        new(-1, 0),
        new(-1, 1),
        new(0, -1),
        new(0, 1),
        new(1, -1),
        new(1, 0),
        new(1, 1)
    ];

    private readonly object _sync = new();
    private readonly Dictionary<BoardPoint, FrogPiece> _board = [];
    private readonly List<MatchEvent> _events = [];
    private readonly MatchPlayer _playerOne;
    private MatchPlayer? _playerTwo;
    private long _eventCounter;

    public FrogMatch(Guid matchId, string creatorName)
    {
        MatchId = matchId;
        _playerOne = MatchPlayer.Create(NormalizeName(creatorName), FrogSide.Emerald);
        TurnPlayerId = _playerOne.PlayerId;
        AddEvent(MatchEventKind.Created, $"{_playerOne.Name} created the match.");
    }

    public Guid MatchId { get; }
    public MatchStatus Status { get; private set; } = MatchStatus.WaitingForSecondPlayer;
    public Guid? TurnPlayerId { get; private set; }
    public Guid? WinnerPlayerId { get; private set; }
    public Guid? LastJumpPlayerId { get; private set; }
    public int ConsecutiveSkips { get; private set; }
    public long Version { get; private set; }

    public Guid PlayerOneId => _playerOne.PlayerId;
    public Guid PlayerOneToken => _playerOne.PlayerToken;
    public Guid? PlayerTwoId => _playerTwo?.PlayerId;
    public Guid? PlayerTwoToken => _playerTwo?.PlayerToken;

    public PlayerTicket CreatorTicket => _playerOne.ToTicket();

    public JoinMatchResult Join(string playerName, Random? random = null)
    {
        lock (_sync)
        {
            if (_playerTwo is not null)
            {
                throw new FrogRuleException("The match already has two players.");
            }

            _playerTwo = MatchPlayer.Create(NormalizeName(playerName), FrogSide.Amber);
            ReplaceBoard(GenerateStartingBoard(_playerOne.PlayerId, _playerTwo.PlayerId, random));
            Status = MatchStatus.Playing;
            TurnPlayerId = _playerOne.PlayerId;
            ConsecutiveSkips = 0;
            AddEvent(MatchEventKind.Joined, $"{_playerTwo.Name} joined the match.");
            AddEvent(MatchEventKind.BoardGenerated, "The server generated a random 36-frog starting board.");
            Version++;
            return new JoinMatchResult(_playerTwo.ToTicket(), SnapshotNoLock());
        }
    }

    public MatchSnapshot Snapshot()
    {
        lock (_sync)
        {
            return SnapshotNoLock();
        }
    }

    public PlayerView Identify(Guid playerToken)
    {
        lock (_sync)
        {
            return GetPlayerByToken(playerToken).ToView();
        }
    }

    public MatchSnapshot RemoveOpeningFrog(Guid playerToken, BoardPoint position)
    {
        lock (_sync)
        {
            EnsureActiveTurn(playerToken, out var player);

            if (player.MadeJump)
            {
                throw new FrogRuleException("The opening removal is not available after this player has jumped.");
            }

            if (player.RemovedOpeningFrog)
            {
                throw new FrogRuleException("This player has already used the opening removal.");
            }

            if (!_board.Remove(position, out var removed))
            {
                throw new FrogRuleException("The selected cell does not contain a frog.");
            }

            player.RemovedOpeningFrog = true;
            AddEvent(
                MatchEventKind.FrogRemoved,
                $"{player.Name} removed a {removed.Side.ToString().ToLowerInvariant()} frog at {Format(position)}.");
            Version++;
            ResolveForcedSkipsNoLock();
            return SnapshotNoLock();
        }
    }

    public MatchSnapshot Jump(Guid playerToken, IReadOnlyList<BoardPoint> path)
    {
        lock (_sync)
        {
            EnsureActiveTurn(playerToken, out var player);

            if (!player.RemovedOpeningFrog)
            {
                throw new FrogRuleException("Before the first jump, the player must remove exactly one frog.");
            }

            if (path.Count < 2)
            {
                throw new FrogRuleException("A jump path must include a start cell and at least one landing cell.");
            }

            if (!_board.TryGetValue(path[0], out var mover))
            {
                throw new FrogRuleException("The start cell is empty.");
            }

            if (mover.OwnerId != player.PlayerId)
            {
                throw new FrogRuleException("A player can move only their own frogs.");
            }

            var workingBoard = new Dictionary<BoardPoint, FrogPiece>(_board);
            workingBoard.Remove(path[0]);
            var captured = new List<FrogPiece>();
            var current = path[0];

            foreach (var landing in path.Skip(1))
            {
                var jumpedCell = ValidateJumpStep(current, landing, workingBoard);
                captured.Add(workingBoard[jumpedCell]);
                workingBoard.Remove(jumpedCell);
                current = landing;
            }

            var finishedInSwamp = current.IsSwamp;
            if (!finishedInSwamp)
            {
                workingBoard[current] = mover with { Position = current };
            }

            ReplaceBoard(workingBoard.Values);
            player.MadeJump = true;
            player.RemovedOpeningFrog = true;
            LastJumpPlayerId = player.PlayerId;
            ConsecutiveSkips = 0;
            TurnPlayerId = OpponentOf(player.PlayerId).PlayerId;

            var swampSuffix = finishedInSwamp ? " The moving frog finished in the swamp and left the board." : "";
            AddEvent(
                MatchEventKind.JumpCompleted,
                $"{player.Name} jumped from {Format(path[0])} to {Format(current)} and captured {captured.Count} frog(s).{swampSuffix}");
            Version++;
            ResolveForcedSkipsNoLock();
            return SnapshotNoLock();
        }
    }

    public MatchSnapshot Pass(Guid playerToken)
    {
        lock (_sync)
        {
            EnsureActiveTurn(playerToken, out var player);

            if (!player.RemovedOpeningFrog)
            {
                throw new FrogRuleException("The opening removal must happen before a pass is allowed.");
            }

            if (HasLegalJumpNoLock(player.PlayerId))
            {
                throw new FrogRuleException("A player may pass only when no legal jump exists.");
            }

            RegisterSkipNoLock(player, automatic: false);
            ResolveForcedSkipsNoLock();
            return SnapshotNoLock();
        }
    }

    public MatchSnapshot MarkConnected(Guid playerToken)
    {
        lock (_sync)
        {
            var player = GetPlayerByToken(playerToken);
            if (!player.IsOnline)
            {
                player.IsOnline = true;
                player.DisconnectedAt = null;
                AddEvent(MatchEventKind.Connected, $"{player.Name} reconnected.");
                Version++;
            }

            return SnapshotNoLock();
        }
    }

    public MatchSnapshot MarkDisconnected(Guid playerToken, DateTimeOffset at)
    {
        lock (_sync)
        {
            var player = GetPlayerByToken(playerToken);
            if (player.IsOnline)
            {
                player.IsOnline = false;
                player.DisconnectedAt = at;
                AddEvent(MatchEventKind.Disconnected, $"{player.Name} disconnected.");
                Version++;
            }

            return SnapshotNoLock();
        }
    }

    public (bool Changed, MatchSnapshot Snapshot) ForfeitIfStillDisconnected(Guid playerToken, TimeSpan gracePeriod, DateTimeOffset now)
    {
        lock (_sync)
        {
            var player = GetPlayerByToken(playerToken);

            if (Status == MatchStatus.Finished || _playerTwo is null || player.IsOnline || player.DisconnectedAt is null)
            {
                return (false, SnapshotNoLock());
            }

            if (now - player.DisconnectedAt < gracePeriod)
            {
                return (false, SnapshotNoLock());
            }

            var opponent = OpponentOf(player.PlayerId);
            Status = MatchStatus.Finished;
            WinnerPlayerId = opponent.PlayerId;
            TurnPlayerId = null;
            AddEvent(MatchEventKind.Finished, $"{opponent.Name} wins because {player.Name} did not reconnect.");
            Version++;
            return (true, SnapshotNoLock());
        }
    }

    public bool HasLegalJump(Guid playerToken)
    {
        lock (_sync)
        {
            var player = GetPlayerByToken(playerToken);
            return HasLegalJumpNoLock(player.PlayerId);
        }
    }

    public static IReadOnlyList<FrogPiece> GenerateStartingBoard(Guid emeraldPlayerId, Guid amberPlayerId, Random? random = null)
    {
        random ??= Random.Shared;
        var innerCells = Enumerable
            .Range(BoardPoint.InnerMin, BoardPoint.InnerMax)
            .SelectMany(row => Enumerable.Range(BoardPoint.InnerMin, BoardPoint.InnerMax), (row, col) => new BoardPoint(row, col))
            .ToArray();

        Shuffle(innerCells, random);

        return innerCells
            .Select((position, index) =>
            {
                var isEmerald = index < 18;
                return new FrogPiece(
                    Guid.NewGuid(),
                    isEmerald ? emeraldPlayerId : amberPlayerId,
                    isEmerald ? FrogSide.Emerald : FrogSide.Amber,
                    position);
            })
            .ToArray();
    }

    public static FrogMatch CreateTestingMatch(
        IEnumerable<FrogPiece> board,
        Guid? turnPlayerId = null,
        bool openingRemoved = true,
        string firstName = "First",
        string secondName = "Second")
    {
        var match = new FrogMatch(Guid.NewGuid(), firstName);
        match.Join(secondName, new Random(17));

        lock (match._sync)
        {
            match.ReplaceBoard(board);
            match._playerOne.RemovedOpeningFrog = openingRemoved;
            match._playerTwo!.RemovedOpeningFrog = openingRemoved;
            match.Status = MatchStatus.Playing;
            match.TurnPlayerId = turnPlayerId ?? match._playerOne.PlayerId;
            match.ConsecutiveSkips = 0;
            match.WinnerPlayerId = null;
            match.LastJumpPlayerId = null;
            match.Version++;
            return match;
        }
    }

    public static FrogMatch CreateTestingMatch(
        bool openingRemoved = true,
        string firstName = "First",
        string secondName = "Second") =>
        CreateTestingMatch([], openingRemoved: openingRemoved, firstName: firstName, secondName: secondName);

    public void LoadBoardForTesting(
        IEnumerable<FrogPiece> board,
        Guid? turnPlayerId = null,
        bool openingRemoved = true,
        Guid? lastJumpPlayerId = null,
        int consecutiveSkips = 0)
    {
        lock (_sync)
        {
            ReplaceBoard(board);
            _playerOne.RemovedOpeningFrog = openingRemoved;
            _playerTwo!.RemovedOpeningFrog = openingRemoved;
            Status = MatchStatus.Playing;
            TurnPlayerId = turnPlayerId ?? _playerOne.PlayerId;
            LastJumpPlayerId = lastJumpPlayerId;
            ConsecutiveSkips = consecutiveSkips;
            WinnerPlayerId = null;
            Version++;
        }
    }

    private void EnsureActiveTurn(Guid playerToken, out MatchPlayer player)
    {
        if (Status != MatchStatus.Playing)
        {
            throw new FrogRuleException("The match is not active.");
        }

        player = GetPlayerByToken(playerToken);
        if (TurnPlayerId != player.PlayerId)
        {
            throw new FrogRuleException("It is the other player's turn.");
        }
    }

    private BoardPoint ValidateJumpStep(BoardPoint from, BoardPoint landing, Dictionary<BoardPoint, FrogPiece> board)
    {
        if (!from.IsInside || !landing.IsInside)
        {
            throw new FrogRuleException("Every jump coordinate must stay inside the 8x8 board.");
        }

        var rowDelta = landing.Row - from.Row;
        var colDelta = landing.Col - from.Col;
        var rowAbs = Math.Abs(rowDelta);
        var colAbs = Math.Abs(colDelta);
        var straightLine = rowAbs == 0 || colAbs == 0 || rowAbs == colAbs;

        if (!straightLine || Math.Max(rowAbs, colAbs) != 2)
        {
            throw new FrogRuleException("A jump must move exactly two cells horizontally, vertically, or diagonally.");
        }

        if (board.ContainsKey(landing))
        {
            throw new FrogRuleException("A frog cannot land on an occupied cell.");
        }

        var jumpedCell = BoardPoint.Middle(from, landing);
        if (!board.ContainsKey(jumpedCell))
        {
            throw new FrogRuleException("A jump must pass over exactly one occupied adjacent cell.");
        }

        return jumpedCell;
    }

    private bool HasLegalJumpNoLock(Guid playerId) =>
        _board.Values
            .Where(piece => piece.OwnerId == playerId)
            .Any(piece => Directions.Any(direction =>
            {
                var jumped = new BoardPoint(piece.Position.Row + direction.Row, piece.Position.Col + direction.Col);
                var landing = new BoardPoint(piece.Position.Row + direction.Row * 2, piece.Position.Col + direction.Col * 2);
                return landing.IsInside && _board.ContainsKey(jumped) && !_board.ContainsKey(landing);
            }));

    private void ResolveForcedSkipsNoLock()
    {
        var guard = 0;
        while (Status == MatchStatus.Playing && TurnPlayerId is not null && guard++ < 4)
        {
            var currentPlayer = GetPlayerById(TurnPlayerId.Value);

            if (!currentPlayer.RemovedOpeningFrog || HasLegalJumpNoLock(currentPlayer.PlayerId))
            {
                return;
            }

            RegisterSkipNoLock(currentPlayer, automatic: true);
        }
    }

    private void RegisterSkipNoLock(MatchPlayer player, bool automatic)
    {
        ConsecutiveSkips++;
        AddEvent(
            MatchEventKind.TurnSkipped,
            automatic
                ? $"{player.Name} had no legal jumps; the server skipped the turn."
                : $"{player.Name} skipped the turn.");

        if (ConsecutiveSkips >= 2)
        {
            Status = MatchStatus.Finished;
            WinnerPlayerId = LastJumpPlayerId;
            TurnPlayerId = null;
            var winnerText = WinnerPlayerId is null ? "No winner" : GetPlayerById(WinnerPlayerId.Value).Name;
            AddEvent(MatchEventKind.Finished, $"The match ended. Winner: {winnerText}.");
            Version++;
            return;
        }

        TurnPlayerId = OpponentOf(player.PlayerId).PlayerId;
        Version++;
    }

    private MatchPlayer GetPlayerByToken(Guid playerToken)
    {
        if (_playerOne.PlayerToken == playerToken)
        {
            return _playerOne;
        }

        if (_playerTwo?.PlayerToken == playerToken)
        {
            return _playerTwo;
        }

        throw new UnauthorizedAccessException("Unknown player token.");
    }

    private MatchPlayer GetPlayerById(Guid playerId)
    {
        if (_playerOne.PlayerId == playerId)
        {
            return _playerOne;
        }

        if (_playerTwo?.PlayerId == playerId)
        {
            return _playerTwo;
        }

        throw new FrogRuleException("Unknown player.");
    }

    private MatchPlayer OpponentOf(Guid playerId)
    {
        if (_playerTwo is null)
        {
            throw new FrogRuleException("The second player has not joined yet.");
        }

        return _playerOne.PlayerId == playerId ? _playerTwo : _playerOne;
    }

    private void ReplaceBoard(IEnumerable<FrogPiece> pieces)
    {
        _board.Clear();
        foreach (var piece in pieces)
        {
            if (!piece.Position.IsInside)
            {
                throw new FrogRuleException("A frog cannot be placed outside the board.");
            }

            if (!_board.TryAdd(piece.Position, piece))
            {
                throw new FrogRuleException("Two frogs cannot occupy one cell.");
            }
        }
    }

    private MatchSnapshot SnapshotNoLock() =>
        new(
            MatchId,
            Status,
            _playerOne.ToView(),
            _playerTwo?.ToView(),
            TurnPlayerId,
            WinnerPlayerId,
            LastJumpPlayerId,
            ConsecutiveSkips,
            Version,
            _board.Values.OrderBy(piece => piece.Position.Row).ThenBy(piece => piece.Position.Col).ToArray(),
            _events.OrderBy(evt => evt.Number).ToArray());

    private void AddEvent(MatchEventKind kind, string text) =>
        _events.Add(new MatchEvent(++_eventCounter, kind, text, DateTimeOffset.UtcNow));

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static string NormalizeName(string name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        return clean[..Math.Min(clean.Length, 28)];
    }

    private static string Format(BoardPoint point) => $"r{point.Row}:c{point.Col}";

    private sealed class MatchPlayer
    {
        private MatchPlayer(Guid playerId, Guid playerToken, string name, FrogSide side)
        {
            PlayerId = playerId;
            PlayerToken = playerToken;
            Name = name;
            Side = side;
        }

        public Guid PlayerId { get; }
        public Guid PlayerToken { get; }
        public string Name { get; }
        public FrogSide Side { get; }
        public bool RemovedOpeningFrog { get; set; }
        public bool MadeJump { get; set; }
        public bool IsOnline { get; set; } = true;
        public DateTimeOffset? DisconnectedAt { get; set; }

        public static MatchPlayer Create(string name, FrogSide side) =>
            new(Guid.NewGuid(), Guid.NewGuid(), name, side);

        public PlayerTicket ToTicket() => new(PlayerId, PlayerToken, Name, Side);

        public PlayerView ToView() =>
            new(PlayerId, Name, Side, RemovedOpeningFrog, MadeJump, IsOnline, DisconnectedAt);
    }
}
