using System.Text.Json.Serialization;
using FrogPondDuel.Core;
using FrogPondDuel.Web.Hubs;
using FrogPondDuel.Web.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<GameRoomStore>();
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy =>
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowAnyOrigin());
});

var app = builder.Build();

app.UseCors("Development");
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<GameHub>("/gameHub");
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapPost("/api/games", async (CreateGameRequest request, GameRoomStore store, IHubContext<GameHub> hub) =>
    await ExecuteAsync(async () =>
    {
        var result = store.CreateGame(request.PlayerName);
        await BroadcastAsync(hub, result.Match);
        return Results.Created($"/api/games/{result.Match.MatchId}", result);
    }));

app.MapPost("/api/games/{matchId:guid}/join", async (Guid matchId, JoinGameRequest request, GameRoomStore store, IHubContext<GameHub> hub) =>
    await ExecuteAsync(async () =>
    {
        var result = store.JoinGame(matchId, request.PlayerName);
        await BroadcastAsync(hub, result.Match);
        return Results.Ok(result);
    }));

app.MapGet("/api/games/{matchId:guid}", (Guid matchId, Guid playerToken, GameRoomStore store) =>
    Execute(() => Results.Ok(store.GetState(matchId, playerToken))));

app.MapPost("/api/games/{matchId:guid}/remove", async (Guid matchId, RemoveFrogRequest request, GameRoomStore store, IHubContext<GameHub> hub) =>
    await ExecuteAsync(async () =>
    {
        var snapshot = store.RemoveOpeningFrog(matchId, request.PlayerToken, request.ToPoint());
        await BroadcastAsync(hub, snapshot);
        return Results.Ok(snapshot);
    }));

app.MapPost("/api/games/{matchId:guid}/jumps", async (Guid matchId, JumpRequest request, GameRoomStore store, IHubContext<GameHub> hub) =>
    await ExecuteAsync(async () =>
    {
        var path = request.Path.Select(step => step.ToPoint()).ToArray();
        var snapshot = store.Jump(matchId, request.PlayerToken, path);
        await BroadcastAsync(hub, snapshot);
        return Results.Ok(snapshot);
    }));

app.MapPost("/api/games/{matchId:guid}/pass", async (Guid matchId, PlayerCommandRequest request, GameRoomStore store, IHubContext<GameHub> hub) =>
    await ExecuteAsync(async () =>
    {
        var snapshot = store.Pass(matchId, request.PlayerToken);
        await BroadcastAsync(hub, snapshot);
        return Results.Ok(snapshot);
    }));

app.MapFallbackToFile("index.html");

app.Run();

static Task BroadcastAsync(IHubContext<GameHub> hub, MatchSnapshot snapshot) =>
    hub.Clients.Group(GameHub.GroupName(snapshot.MatchId)).SendAsync("stateChanged", snapshot);

static IResult Execute(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (Exception ex) when (ToHttpError(ex) is { } error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
}

static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
{
    try
    {
        return await action();
    }
    catch (Exception ex) when (ToHttpError(ex) is { } error)
    {
        return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
    }
}

static HttpError? ToHttpError(Exception ex) =>
    ex switch
    {
        KeyNotFoundException => new(404, ex.Message),
        UnauthorizedAccessException => new(403, ex.Message),
        ArgumentException => new(400, ex.Message),
        FrogRuleException => new(409, ex.Message),
        _ => null
    };

public sealed record HttpError(int StatusCode, string Message);

public sealed record CreateGameRequest(string PlayerName);

public sealed record JoinGameRequest(string PlayerName);

public sealed record PlayerCommandRequest(Guid PlayerToken);

public sealed record RemoveFrogRequest(Guid PlayerToken, int Row, int Col)
{
    public BoardPoint ToPoint() => new(Row, Col);
}

public sealed record JumpRequest(Guid PlayerToken, BoardStep[] Path);

public sealed record BoardStep(int Row, int Col)
{
    public BoardPoint ToPoint() => new(Row, Col);
}
