using FrogPondDuel.Core;

var tests = new (string Name, Action Body)[]
{
    ("starting board generation", StartingBoardGeneration),
    ("opening removal rules", OpeningRemovalRules),
    ("valid jumps over own and opponent frogs", ValidJumps),
    ("chain jump captures two frogs", ChainJump),
    ("invalid jump validation", InvalidJumpValidation),
    ("wrong turn is rejected", WrongTurnRejected),
    ("swamp removes final frog", SwampRemovesFinalFrog),
    ("frog can jump from swamp back to inner cell", FrogCanReturnFromSwamp),
    ("automatic pass and game end", AutomaticPassAndGameEnd),
    ("concurrent moves keep state consistent", ConcurrentMoves)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine($"{tests.Length} test(s) passed.");
}

static void StartingBoardGeneration()
{
    var firstPlayer = Guid.NewGuid();
    var secondPlayer = Guid.NewGuid();
    var first = FrogMatch.GenerateStartingBoard(firstPlayer, secondPlayer, new Random(1));
    var second = FrogMatch.GenerateStartingBoard(firstPlayer, secondPlayer, new Random(2));

    Assert(first.Count == 36, "board must contain 36 frogs");
    Assert(first.Count(piece => piece.Side == FrogSide.Emerald) == 18, "emerald count must be 18");
    Assert(first.Count(piece => piece.Side == FrogSide.Amber) == 18, "amber count must be 18");
    Assert(first.Select(piece => piece.Position).Distinct().Count() == 36, "positions must be unique");
    Assert(first.All(piece => piece.Position.IsInnerCell), "all starting frogs must be on inner cells");
    Assert(!first.Select(piece => piece.Position).SequenceEqual(second.Select(piece => piece.Position)), "different seeds should produce different order");
}

static void OpeningRemovalRules()
{
    var match = new FrogMatch(Guid.NewGuid(), "A");
    match.Join("B", new Random(3));
    var firstCell = match.Snapshot().Board[0].Position;
    var afterRemoval = match.RemoveOpeningFrog(match.PlayerOneToken, firstCell);

    Assert(afterRemoval.Board.Count == 35, "one frog must be removed");
    AssertThrows<FrogRuleException>(() => match.RemoveOpeningFrog(match.PlayerOneToken, afterRemoval.Board[0].Position));

    var jumpMatch = FrogMatch.CreateTestingMatch(openingRemoved: false);
    jumpMatch.LoadBoardForTesting(
        [
            Piece(jumpMatch.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(jumpMatch.PlayerOneId, FrogSide.Emerald, 3, 2),
            Piece(jumpMatch.PlayerOneId, FrogSide.Emerald, 1, 1),
            Piece(jumpMatch.PlayerOneId, FrogSide.Emerald, 1, 2),
            Piece(jumpMatch.PlayerTwoId!.Value, FrogSide.Amber, 5, 5)
        ],
        turnPlayerId: jumpMatch.PlayerOneId,
        openingRemoved: false);

    jumpMatch.RemoveOpeningFrog(jumpMatch.PlayerOneToken, new BoardPoint(5, 5));
    jumpMatch.Jump(jumpMatch.PlayerOneToken, [new BoardPoint(3, 1), new BoardPoint(3, 3)]);
    AssertThrows<FrogRuleException>(() => jumpMatch.RemoveOpeningFrog(jumpMatch.PlayerOneToken, new BoardPoint(1, 2)));
}

static void ValidJumps()
{
    var own = FrogMatch.CreateTestingMatch();
    own.LoadBoardForTesting(
        [
            Piece(own.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(own.PlayerOneId, FrogSide.Emerald, 3, 2)
        ],
        turnPlayerId: own.PlayerOneId);
    var ownResult = own.Jump(own.PlayerOneToken, [new BoardPoint(3, 1), new BoardPoint(3, 3)]);
    Assert(ownResult.Board.Any(piece => piece.OwnerId == own.PlayerOneId && piece.Position == new BoardPoint(3, 3)), "jump over own frog should be accepted");

    var enemy = FrogMatch.CreateTestingMatch();
    enemy.LoadBoardForTesting(
        [
            Piece(enemy.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(enemy.PlayerTwoId!.Value, FrogSide.Amber, 3, 2)
        ],
        turnPlayerId: enemy.PlayerOneId);
    var enemyResult = enemy.Jump(enemy.PlayerOneToken, [new BoardPoint(3, 1), new BoardPoint(3, 3)]);
    Assert(enemyResult.Board.Any(piece => piece.OwnerId == enemy.PlayerOneId && piece.Position == new BoardPoint(3, 3)), "jump over opponent frog should be accepted");
}

static void ChainJump()
{
    var match = FrogMatch.CreateTestingMatch();
    match.LoadBoardForTesting(
        [
            Piece(match.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(match.PlayerTwoId!.Value, FrogSide.Amber, 3, 2),
            Piece(match.PlayerOneId, FrogSide.Emerald, 3, 4)
        ],
        turnPlayerId: match.PlayerOneId);

    var snapshot = match.Jump(match.PlayerOneToken, [new BoardPoint(3, 1), new BoardPoint(3, 3), new BoardPoint(3, 5)]);

    Assert(snapshot.Board.Count == 1, "two jumped frogs must be removed");
    Assert(snapshot.Board[0].Position == new BoardPoint(3, 5), "moving frog must finish at the last chain point");
}

static void InvalidJumpValidation()
{
    AssertInvalidJump(
        [Piece(Guid.Empty, FrogSide.Emerald, 3, 1), Piece(Guid.NewGuid(), FrogSide.Amber, 4, 2)],
        [new BoardPoint(3, 1), new BoardPoint(5, 4)],
        "non-straight jump must fail");

    AssertInvalidJump(
        [Piece(Guid.Empty, FrogSide.Emerald, 3, 1), Piece(Guid.NewGuid(), FrogSide.Amber, 3, 2), Piece(Guid.Empty, FrogSide.Emerald, 3, 3)],
        [new BoardPoint(3, 1), new BoardPoint(3, 3)],
        "occupied landing must fail");

    AssertInvalidJump(
        [Piece(Guid.Empty, FrogSide.Emerald, 3, 1), Piece(Guid.NewGuid(), FrogSide.Amber, 3, 2)],
        [new BoardPoint(3, 1), new BoardPoint(3, 5)],
        "jump over several cells must fail");
}

static void WrongTurnRejected()
{
    var match = FrogMatch.CreateTestingMatch();
    match.LoadBoardForTesting(
        [
            Piece(match.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(match.PlayerTwoId!.Value, FrogSide.Amber, 3, 2)
        ],
        turnPlayerId: match.PlayerOneId);

    AssertThrows<FrogRuleException>(() =>
        match.Jump(match.PlayerTwoToken!.Value, [new BoardPoint(3, 2), new BoardPoint(3, 4)]));
}

static void SwampRemovesFinalFrog()
{
    var match = FrogMatch.CreateTestingMatch();
    match.LoadBoardForTesting(
        [
            Piece(match.PlayerOneId, FrogSide.Emerald, 2, 1),
            Piece(match.PlayerTwoId!.Value, FrogSide.Amber, 1, 1)
        ],
        turnPlayerId: match.PlayerOneId);

    var snapshot = match.Jump(match.PlayerOneToken, [new BoardPoint(2, 1), new BoardPoint(0, 1)]);

    Assert(snapshot.Board.All(piece => piece.OwnerId != match.PlayerOneId), "frog finishing in swamp must leave the board");
}

static void FrogCanReturnFromSwamp()
{
    var match = FrogMatch.CreateTestingMatch();
    match.LoadBoardForTesting(
        [
            Piece(match.PlayerOneId, FrogSide.Emerald, 0, 1),
            Piece(match.PlayerTwoId!.Value, FrogSide.Amber, 1, 1)
        ],
        turnPlayerId: match.PlayerOneId);

    var snapshot = match.Jump(match.PlayerOneToken, [new BoardPoint(0, 1), new BoardPoint(2, 1)]);

    Assert(snapshot.Board.Any(piece => piece.OwnerId == match.PlayerOneId && piece.Position == new BoardPoint(2, 1)), "frog returning from swamp to inner cell must remain");
}

static void AutomaticPassAndGameEnd()
{
    var autoPass = FrogMatch.CreateTestingMatch();
    autoPass.LoadBoardForTesting(
        [
            Piece(autoPass.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(autoPass.PlayerOneId, FrogSide.Emerald, 3, 2),
            Piece(autoPass.PlayerOneId, FrogSide.Emerald, 1, 1),
            Piece(autoPass.PlayerOneId, FrogSide.Emerald, 1, 2)
        ],
        turnPlayerId: autoPass.PlayerOneId);

    var afterJump = autoPass.Jump(autoPass.PlayerOneToken, [new BoardPoint(3, 1), new BoardPoint(3, 3)]);
    Assert(afterJump.TurnPlayerId == autoPass.PlayerOneId, "opponent without moves should be skipped automatically");

    var ending = FrogMatch.CreateTestingMatch();
    ending.LoadBoardForTesting(
        [
            Piece(ending.PlayerOneId, FrogSide.Emerald, 1, 1),
            Piece(ending.PlayerTwoId!.Value, FrogSide.Amber, 6, 6)
        ],
        turnPlayerId: ending.PlayerOneId,
        lastJumpPlayerId: ending.PlayerTwoId,
        consecutiveSkips: 1);

    var finished = ending.Pass(ending.PlayerOneToken);
    Assert(finished.Status == MatchStatus.Finished, "two consecutive skips must finish the game");
    Assert(finished.WinnerPlayerId == ending.PlayerTwoId, "last jumper must win");
}

static void ConcurrentMoves()
{
    var match = FrogMatch.CreateTestingMatch();
    match.LoadBoardForTesting(
        [
            Piece(match.PlayerOneId, FrogSide.Emerald, 3, 1),
            Piece(match.PlayerTwoId!.Value, FrogSide.Amber, 3, 2)
        ],
        turnPlayerId: match.PlayerOneId);

    var path = new[] { new BoardPoint(3, 1), new BoardPoint(3, 3) };
    var attempts = Enumerable.Range(0, 2)
        .Select(_ => Task.Run(() =>
        {
            try
            {
                match.Jump(match.PlayerOneToken, path);
                return true;
            }
            catch
            {
                return false;
            }
        }))
        .ToArray();

    Task.WaitAll(attempts);
    var successes = attempts.Count(task => task.Result);
    var snapshot = match.Snapshot();

    Assert(successes == 1, "only one concurrent move should be accepted");
    Assert(snapshot.Board.Select(piece => piece.Position).Distinct().Count() == snapshot.Board.Count, "board positions must remain unique");
}

static void AssertInvalidJump(FrogPiece[] rawPieces, BoardPoint[] path, string message)
{
    var match = FrogMatch.CreateTestingMatch();
    var pieces = rawPieces.Select(piece =>
    {
        var ownerId = piece.OwnerId == Guid.Empty ? match.PlayerOneId : match.PlayerTwoId!.Value;
        var side = ownerId == match.PlayerOneId ? FrogSide.Emerald : FrogSide.Amber;
        return Piece(ownerId, side, piece.Position.Row, piece.Position.Col);
    });

    match.LoadBoardForTesting(pieces, turnPlayerId: match.PlayerOneId);
    AssertThrows<FrogRuleException>(() => match.Jump(match.PlayerOneToken, path), message);
}

static FrogPiece Piece(Guid ownerId, FrogSide side, int row, int col) =>
    new(Guid.NewGuid(), ownerId, side, new BoardPoint(row, col));

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string? message = null)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message ?? $"Expected exception {typeof(TException).Name}.");
}
