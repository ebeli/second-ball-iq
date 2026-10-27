// Second Ball IQ live server: plays a Match Forge match on a clock, runs every candidate through the multi-agent
// workflow, and streams events, agent steps and insights to the browser over SignalR. The editor queue and the
// match controls are plain HTTP. The engine's queries are also an MCP server at /mcp.
//
//   dotnet run --project src/MatchLens.Server            then open http://localhost:5080
//
// Live mode needs the same settings as the pipeline CLI (MATCHLENS_DECISION_* and MATCHLENS_CHAT_DEPLOYMENT);
// without them every match runs offline on templates.

using System.Text.Json;
using System.Text.Json.Serialization;
using MatchLens.Agents;
using MatchLens.Contracts;
using MatchLens.Forge;
using MatchLens.Server;

// Numbers are written the same way on every machine ("0.16 xG", not "0,16 xG" on a Finnish Windows); Finnish
// texts put their decimal commas in explicitly.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The log also goes to a file: logs/ at the repository root (or MATCHLENS_LOG_DIR).
var fileLog = new FileLoggerProvider(Environment.GetEnvironmentVariable("MATCHLENS_LOG_DIR") is { Length: > 0 } logDir ? logDir : FileLoggerProvider.DefaultDirectory());
builder.Logging.AddProvider(fileLog);

builder.Services.ConfigureHttpJsonOptions(o => Configure(o.SerializerOptions));
builder.Services.AddSignalR().AddJsonProtocol(o => Configure(o.PayloadSerializerOptions));
builder.Services.AddHttpClient("decision");
builder.Services.AddHttpClient("speech");
// A public site caps what the models may spend: per hour and per day, chat, Decision-1 and speech (ModelBudget).
builder.Services.AddSingleton(_ => new ModelBudget());
builder.Services.AddSingleton<MatchHost>();
builder.Services.AddSingleton<PartyHost>();
builder.Services.AddSingleton<SpeechService>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<MatchTools>();

// Per-visitor limits on the endpoints that call a model (ask, what-if, coach's corner, voices). The global ModelBudget
// is the hard cap; these keep one visitor from spending it. Behind a proxy (Container Apps) the visitor is the
// forwarded client address.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests from here; try again in a minute." }, ct);
    o.AddPolicy("models", http => PerVisitor(http, Limit("MATCHLENS_MODEL_CALLS_PER_IP_MINUTE", 12)));
    o.AddPolicy("voice", http => PerVisitor(http, Limit("MATCHLENS_VOICE_CALLS_PER_IP_MINUTE", 60)));
});

// One fixed window per visitor address; this machine itself (a local run, the recording) is not limited.
static System.Threading.RateLimiting.RateLimitPartition<string> PerVisitor(HttpContext http, int perMinute)
{
    System.Net.IPAddress? ip = http.Connection.RemoteIpAddress;
    if (ip is not null && System.Net.IPAddress.IsLoopback(ip)) return System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("local");
    return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ip?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
}

// The operator key: when MATCHLENS_ADMIN_KEY is set, starting, stopping and steering a match, the fault drill and the
// editor need it (header X-MatchLens-Admin-Key, or ?key= once in the studio). Judge mode and everything a fan does
// stay open.
string? adminKey = Environment.GetEnvironmentVariable("MATCHLENS_ADMIN_KEY") is { Length: > 0 } k ? k : null;

WebApplication app = builder.Build();
app.Logger.LogInformation("Log file: {Path}", fileLog.Path);
app.Services.GetRequiredService<PartyHost>();   // listen to matches from the start

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<MatchHub>("/hubs/match");
app.MapMcp("/mcp");

RouteGroupBuilder api = app.MapGroup("/api");

api.MapGet("/health", (MatchHost host, SpeechService speech) => Results.Ok(new
{
    status = "ok",
    speech = speech.Available,
    live = host.LiveAvailable,
    models = host.Models,
    session = host.Current?.Info.MatchId,
    operatorKey = adminKey is not null,
    budget = host.Budget.Report(),
}));

// Tells the studio whether its operator key is right, without doing anything.
api.MapGet("/operator", () => Results.Ok(new { required = adminKey is not null })).AddEndpointFilter(OperatorOnly);

api.MapGet("/scenarios", () =>
    Scenarios.All.Select(s => new ScenarioView(s.Id, s.Description, s.DefaultSeed)));

api.MapGet("/session", (MatchHost host) =>
    host.Current is { } session ? Results.Ok(session.Snapshot()) : Results.NoContent());

api.MapPost("/session", async (StartRequest request, MatchHost host) =>
{
    if (Scenarios.Find(request.ScenarioId) is not { } scenario)
        return Results.BadRequest(new { error = $"Unknown scenario '{request.ScenarioId}'." });
    int seed = request.Seed ?? scenario.DefaultSeed;
    // Every match is synthetic: Match Forge generates the events and the players' season history behind them.
    var source = new MatchSource(scenario.Id, scenario.Description, MatchSimulator.Generate(scenario, seed),
        History: SeasonArchive.Generate(scenario, seed));
    MatchSession session = await host.StartAsync(source, request);
    return Results.Ok(session.Info);
}).AddEndpointFilter(OperatorOnly);

// Judge mode: one link that shows the product working. Starts the demo match at 38' (a pressing shift and its tactical
// test follow within a few minutes, a goal, a coach's change and a saved penalty after that), or joins the demo match
// that is already playing, so judges arriving together do not restart each other's match.
// ?restart=true always starts it again from 38′ (the scripted demo takes, /judge?take=N): with an operator key set, it needs the key.
api.MapPost("/judge", async (MatchHost host, HttpContext http, bool? restart) =>
{
    if (restart == true && !OperatorOk(http.Request))
        return Results.Json(new { error = "Restarting the demo match needs the operator key." }, statusCode: 401);
    if (restart != true && host.Current is { } current && current.Info.ScenarioId == JudgeMode.ScenarioId)
    {
        TickView tick = current.Snapshot().Tick;
        if (tick.Status is SessionStatus.Running or SessionStatus.Paused && tick.ClockSec < JudgeMode.JoinBeforeSec)
        {
            if (tick.Status == SessionStatus.Paused) current.Resume();
            return Results.Ok(current.Info);
        }
    }
    Scenario scenario = Scenarios.Find(JudgeMode.ScenarioId)!;
    var source = new MatchSource(scenario.Id, scenario.Description, MatchSimulator.Generate(scenario, scenario.DefaultSeed),
        History: SeasonArchive.Generate(scenario, scenario.DefaultSeed));
    MatchSession session = await host.StartAsync(source, new StartRequest(scenario.Id, scenario.DefaultSeed, JudgeMode.Speed, JudgeMode.StartMinute));
    return Results.Ok(session.Info);
});

// Fault drill: plant an error in the next moment's Analyst draft ("number" for the Validator, "claim" for Decision-1
// grounding) and watch the checks catch it in the agent trace.
api.MapPost("/session/plant", (PlantRequest request, MatchHost host) =>
    host.Current is not { } session ? Results.NotFound()
    : session.ArmPlant(request.Kind) ? Results.Ok(new { armed = request.Kind })
    : Results.BadRequest(new { error = "kind must be 'number' or 'claim'" })).AddEndpointFilter(OperatorOnly);

api.MapPost("/session/pause", (MatchHost host) => Control(host, s => s.Pause())).AddEndpointFilter(OperatorOnly);
api.MapPost("/session/resume", (MatchHost host) => Control(host, s => s.Resume())).AddEndpointFilter(OperatorOnly);
api.MapPost("/session/speed", (SpeedRequest request, MatchHost host) => Control(host, s => s.SetSpeed(request.Speed))).AddEndpointFilter(OperatorOnly);
api.MapPost("/session/stop", async (MatchHost host) =>
{
    await host.StopAsync();
    return Results.Ok();
}).AddEndpointFilter(OperatorOnly);

api.MapGet("/insights", (MatchHost host, string? status) =>
{
    if (host.Current is not { } session) return Results.Ok(Array.Empty<InsightView>());
    IEnumerable<InsightView> insights = session.Insights();
    if (Enum.TryParse(status, ignoreCase: true, out InsightStatus wanted)) insights = insights.Where(i => i.Status == wanted);
    return Results.Ok(insights);
});

api.MapPost("/editor/{insightId}/approve", async (string insightId, MatchHost host) =>
    host.Current is { } s ? Editor(await s.ApproveAsync(insightId, "editor")) : NoSession()).AddEndpointFilter(OperatorOnly);

api.MapPost("/editor/{insightId}/reject", async (string insightId, MatchHost host) =>
    host.Current is { } s ? Editor(await s.RejectAsync(insightId, "editor")) : NoSession()).AddEndpointFilter(OperatorOnly);

api.MapPut("/editor/{insightId}", async (string insightId, EditRequest edit, MatchHost host) =>
    host.Current is { } s ? Editor(await s.EditAsync(insightId, edit)) : NoSession()).AddEndpointFilter(OperatorOnly);

// Voice: an insight or recap read aloud by an Azure neural voice that fits its audience and language.
api.MapGet("/speech/insight/{insightId}", async (string insightId, MatchHost host, SpeechService speech, CancellationToken ct) =>
{
    if (host.Current is not { } session) return NoSession();
    InsightView? view = session.Insights().FirstOrDefault(i => i.Insight.InsightId == insightId);
    if (view is null) return Results.NotFound();
    string text = Sentence(view.Insight.Headline) + " " + view.Insight.Summary;
    return Audio(await speech.SynthesizeAsync(text, view.Insight.Audience, view.Insight.Language, ct));
}).RequireRateLimiting("voice");

api.MapGet("/speech/recap/{recapId}", async (string recapId, MatchHost host, SpeechService speech, CancellationToken ct) =>
{
    if (host.Current is not { } session) return NoSession();
    RecapView? recap = session.Recaps().FirstOrDefault(r => r.RecapId == recapId);
    if (recap is null) return Results.NotFound();
    string[] persona = recap.PersonaKey.Split('-');
    Audience audience = Enum.TryParse(persona[0], ignoreCase: true, out Audience a) ? a : Audience.Casual;
    string language = persona.Length > 1 ? persona[1] : "en";
    string text = Sentence(recap.Headline) + " " + string.Join(" ", recap.Paragraphs);
    return Audio(await speech.SynthesizeAsync(text, audience, language, ct));
}).RequireRateLimiting("voice");

api.MapGet("/speech/call/{callId}", async (string callId, string? lang, MatchHost host, SpeechService speech, CancellationToken ct) =>
{
    if (host.Current is not { } session) return NoSession();
    if (session.Calls().FirstOrDefault(c => c.CallId == callId) is not { } call) return Results.NotFound();
    string language = lang == "fi" ? "fi" : "en";
    return Audio(await speech.SynthesizeAsync(language == "fi" ? call.TextFi : call.Text, SpeechService.CallVoice(language, call.Intensity), ct));
}).RequireRateLimiting("voice");

// Player cards: this match's numbers come from the stream in the browser; the season comes from Match Forge's archive.
api.MapGet("/players/{playerId}/season", (string playerId, MatchHost host) =>
    host.Current?.SeasonOf(playerId) is { } season ? Results.Ok(season) : Results.NotFound());

// Shape over any window played so far: formation, average positions, roles and style (seconds).
// Decision-1 coach's corner: run it now for a team (it also runs on its own about every fifteen minutes of play).
api.MapPost("/coach/{teamId}", async (string teamId, MatchHost host) =>
{
    if (host.Current is not { } session) return Results.NoContent();
    if (teamId != session.Info.Match.Home.TeamId && teamId != session.Info.Match.Away.TeamId) return Results.BadRequest(new { error = "Unknown team." });
    return await session.RunCoachNowAsync(teamId) is { } d
        ? Results.Ok(d)
        : Results.Conflict(new { error = "This team's last plans are still being settled, or the match is not running." });
}).RequireRateLimiting("models");

// What-if sandbox: move one team on the tracking frame now and measure the pitch control; Decision-1 says if it would help.
api.MapPost("/whatif", async (WhatIfRequest request, MatchHost host, CancellationToken ct) =>
{
    if (host.Current is not { } session) return Results.NoContent();
    return await session.WhatIfAsync(request.TeamId, request.Change, request.Metres ?? 8, ct) is { } r
        ? Results.Ok(r)
        : Results.Conflict(new { error = "Tracking is not ready yet." });
}).RequireRateLimiting("models");

api.MapGet("/shape/{teamId}", (string teamId, int from, int to, MatchHost host) =>
{
    if (host.Current is not { } session) return NoSession();
    if (teamId != session.Info.Match.Home.TeamId && teamId != session.Info.Match.Away.TeamId) return Results.NotFound();
    return Results.Ok(session.Shape(teamId, from, to));
});

api.MapGet("/speech/ask/{askId}", async (string askId, MatchHost host, SpeechService speech, CancellationToken ct) =>
{
    if (host.Current?.Ask(askId) is not { } ask) return Results.NotFound();
    return Audio(await speech.SynthesizeAsync(ask.Result.Answer, ask.Request.Audience, ask.Request.Language, ct));
}).RequireRateLimiting("voice");

// Ask Second Ball IQ. One question per viewer every few seconds, and a cap per match, to keep model costs bounded.
var lastAsk = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();
int asksThisMatch = 0;
string? askMatch = null;
api.MapPost("/ask", async (AskBody body, HttpContext http, MatchHost host, CancellationToken ct) =>
{
    if (host.Current is not { } session) return NoSession();
    string question = (body.Question ?? "").Trim();
    if (question.Length is < 3 or > 300) return Results.BadRequest(new { error = "Ask a question of 3 to 300 characters." });
    string viewer = http.Request.Headers["X-MatchLens-Viewer"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString() ?? "anon";
    DateTimeOffset now = DateTimeOffset.UtcNow;
    if (lastAsk.TryGetValue(viewer, out DateTimeOffset last) && now - last < TimeSpan.FromSeconds(5))
        return Results.Json(new { error = "One question every few seconds, please." }, statusCode: 429);
    lastAsk[viewer] = now;
    if (askMatch != session.Info.MatchId) { askMatch = session.Info.MatchId; asksThisMatch = 0; }
    if (Interlocked.Increment(ref asksThisMatch) > 400)
        return Results.Json(new { error = "Question limit reached for this match." }, statusCode: 429);

    Audience audience = Enum.TryParse(body.Audience, ignoreCase: true, out Audience a) ? a : Audience.Casual;
    var request = new AskRequest(question, audience, body.Language == "fi" ? "fi" : "en", body.TeamId, body.PlayerId,
        body.History?.Take(3).Select(h => new AskTurn(h.Question ?? "", h.Answer ?? "")).ToList());
    return Results.Ok(await session.AskAsync(request, ct));
}).RequireRateLimiting("models");

app.MapFallbackToFile("index.html");

app.Run();

// The operator key check (see MATCHLENS_ADMIN_KEY above). Without a key set, everything is open, as when run locally.
async ValueTask<object?> OperatorOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    return OperatorOk(context.HttpContext.Request) ? await next(context)
        : Results.Json(new { error = "This control needs the operator key. Judge mode (/judge) is open to everyone." }, statusCode: 401);
}

bool OperatorOk(HttpRequest request)
{
    if (adminKey is null) return true;
    string given = request.Headers["X-MatchLens-Admin-Key"].FirstOrDefault() ?? request.Query["key"].FirstOrDefault() ?? "";
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(adminKey));
}

static int Limit(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;

static void Configure(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
}

static IResult Control(MatchHost host, Action<MatchSession> action)
{
    if (host.Current is not { } session) return NoSession();
    action(session);
    return Results.Ok();
}

static string Sentence(string headline) =>
    headline.TrimEnd().EndsWith('.') || headline.TrimEnd().EndsWith('!') || headline.TrimEnd().EndsWith('?') ? headline.Trim() : headline.Trim() + ".";

static IResult Audio((byte[]? Audio, string? Error) result) =>
    result.Audio is { } audio ? Results.File(audio, "audio/mpeg") : Results.Json(new { error = result.Error }, statusCode: 503);

static IResult NoSession() => Results.Conflict(new { error = "No match is playing." });

static IResult Editor(EditorResult result) =>
    result.Ok ? Results.Ok(result) : Results.UnprocessableEntity(result);

record AskBody(string? Question, string? Audience, string? Language, string? TeamId, string? PlayerId, List<AskTurnBody>? History);

record AskTurnBody(string? Question, string? Answer);

/// <summary>The judge link: the demo match from 38', at a speed that reaches the first tactical test in about two minutes.</summary>
static class JudgeMode
{
    public const string ScenarioId = "demo-match";
    public const int StartMinute = 38;
    public const double Speed = 8;
    /// <summary>A judge arriving before this joins the match in progress; later, a fresh one starts.</summary>
    public const int JoinBeforeSec = 80 * 60;
}
