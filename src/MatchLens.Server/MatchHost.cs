using MatchLens.Agents;
using MatchLens.Decisions;
using MatchLens.Forge;
using Microsoft.AspNetCore.SignalR;

namespace MatchLens.Server;

/// <summary>
/// Pushes session updates to the browser. Match commands go through the HTTP API; the hub's own methods are the
/// watch party's (rooms are SignalR groups).
/// </summary>
public sealed class MatchHub(MatchHost host, PartyHost party) : Hub
{
    /// <summary>Everything so far, for a browser that has just connected or reconnected.</summary>
    public SessionSnapshot? Snapshot() => host.Current?.Snapshot();

    public PartyState CreateParty(string name, string? teamId, bool demo) => party.Create(Context.ConnectionId, name, teamId, demo);

    public PartyState? JoinParty(string code, string name, string? teamId) => party.Join(Context.ConnectionId, code, name, teamId);

    public void LeaveParty() => party.Leave(Context.ConnectionId);

    public string? PartyMemberId() => party.MemberOf(Context.ConnectionId);

    public Task React(string emoji) => party.React(Context.ConnectionId, emoji);

    public Task Vote(string pollId, string optionId) => party.Vote(Context.ConnectionId, pollId, optionId);

    public Task Pick(string predictionId, string optionId) => party.Pick(Context.ConnectionId, predictionId, optionId);

    public Task<string?> AskRoom(string text, string language) => party.AskRoom(Context.ConnectionId, text, language);

    public Task<string?> Say(string text, string language) => party.Say(Context.ConnectionId, text, language);

    public Task Upvote(string questionId) => party.Upvote(Context.ConnectionId, questionId);

    public Task Investigate(string questionId) => party.Investigate(Context.ConnectionId, questionId);

    public Task Share(string label, string[] eventIds) => party.Share(Context.ConnectionId, label, eventIds);

    public Task<string?> StartTifo(string design, string language) => party.StartTifo(Context.ConnectionId, design, language);

    public Task HoldTifo() => party.HoldTifo(Context.ConnectionId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        party.Leave(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Owns the one match currently playing and the connections to Foundry.</summary>
public sealed class MatchHost : IAsyncDisposable
{
    private readonly IHubContext<MatchHub> _hub;
    private readonly ILoggerFactory _logs;
    private readonly FoundryConnections _live;
    private readonly string _models;
    private readonly SemaphoreSlim _switch = new(1, 1);

    public MatchHost(IHubContext<MatchHub> hub, ILoggerFactory logs, IHttpClientFactory http, ModelBudget budget)
    {
        _hub = hub;
        _logs = logs;
        Budget = budget;
        ILogger log = logs.CreateLogger<MatchHost>();
        budget.PausedChanged += paused =>
        {
            if (paused) log.LogWarning("Model budget spent: new work runs on offline templates until the window turns");
            else log.LogInformation("Model budget available again: live models resume");
        };

        FoundryChatOptions? chat = FoundryChatOptions.FromEnvironment(out IReadOnlyList<string> chatMissing);
        DecisionOptions? decisions = DecisionOptions.FromEnvironment(out IReadOnlyList<string> decisionMissing);
        if (chat is not null && decisions is not null)
        {
            // Both clients log every call (time, tokens or answers), so a test run can be read back from the log file.
            _live = new FoundryConnections(
                new LoggingChatClient(chat.CreateChatClient(), logs.CreateLogger("Models.Chat"), budget),
                new LoggingDecisionClient(new FoundryDecisionClient(http.CreateClient("decision"), decisions), logs.CreateLogger("Models.Decision1"), budget));
            _models = $"{chat.Deployment} + {decisions.Deployment}";
            log.LogInformation("Live mode: chat model {Chat}, Decision-1 deployment {Decision}", chat.Deployment, decisions.Deployment);
        }
        else
        {
            _live = FoundryConnections.Offline;
            _models = "offline templates";
            log.LogWarning("Offline mode, missing {Missing}", string.Join(", ", chatMissing.Concat(decisionMissing)));
        }
    }

    public MatchSession? Current { get; private set; }

    /// <summary>Raised when a new match starts, after it is current.</summary>
    public event Action<MatchSession>? SessionStarted;

    /// <summary>The spending cap on model calls (public sites).</summary>
    public ModelBudget Budget { get; }

    /// <summary>Decision-1, when live and within budget: the watch parties use it to screen room questions.</summary>
    public IDecisionClient? Decisions => Budget.Paused ? null : _live.Decisions;

    /// <summary>True when Foundry settings exist; a session can still choose offline.</summary>
    public bool LiveAvailable => !_live.IsOffline;

    public string Models => _models;

    public async Task<MatchSession> StartAsync(MatchSource source, StartRequest request)
    {
        await _switch.WaitAsync();
        try
        {
            if (Current is { } previous)
            {
                Current = null;
                await previous.DisposeAsync();
            }

            bool live = LiveAvailable && !request.Offline;
            var session = new MatchSession(
                source, request, live ? _live : FoundryConnections.Offline, live ? _models : "offline templates", Budget,
                (message, payload) => _hub.Clients.All.SendAsync(message, payload),
                _logs.CreateLogger<MatchSession>());
            Current = session;
            SessionStarted?.Invoke(session);
            await _hub.Clients.All.SendAsync("session", session.Info);
            session.Start();
            return session;
        }
        finally
        {
            _switch.Release();
        }
    }

    public async Task StopAsync()
    {
        await _switch.WaitAsync();
        try
        {
            if (Current is { } session)
            {
                await session.DisposeAsync();
                await _hub.Clients.All.SendAsync("tick", session.Snapshot().Tick);
            }
        }
        finally
        {
            _switch.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Current is { } session) await session.DisposeAsync();
        _switch.Dispose();
    }
}
