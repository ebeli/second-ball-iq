using System.Diagnostics;
using System.Threading.Channels;
using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Analytics.Spatial;
using MatchLens.Contracts;
using MatchLens.Decisions;
using MatchLens.Forge;

namespace MatchLens.Server;

/// <summary>What a session plays: a Match Forge scenario or a real match, with the credit it needs.</summary>
/// <param name="History">Earlier matches of both clubs (real matches only): set-piece and player history for predictions.</param>
public sealed record MatchSource(string Id, string Description, MatchRecording Recording, string? Attribution = null, MatchHistory? History = null);

/// <summary>Sends one message to every connected browser.</summary>
public delegate Task Broadcast(string message, object payload);

/// <summary>
/// One match played on a clock. The replay loop feeds events to the analytics engine at the chosen speed and
/// streams them to the browser; candidates go into a queue that pipeline workers drain in the background, so a
/// slow model never holds up the match clock.
/// </summary>
/// <remarks>
/// Threading: only the replay loop touches the live <see cref="LiveMatch"/>. Each candidate is processed against a
/// frozen copy of the match as it stood when the candidate was raised, so the agents can neither race the replay
/// nor see the future. Everything the browser reads is kept in views guarded by one lock.
/// </remarks>
public sealed class MatchSession : IAsyncDisposable
{
    /// <summary>Candidates processed at the same time. Two keeps a goal from waiting behind a slow narration.</summary>
    public const int Workers = 2;

    /// <summary>A detection counts for the ground truth inside the expected window or up to this long after it.</summary>
    public const int LateToleranceSec = 180;

    private readonly MatchRecording _recording;
    private readonly LiveMatch _live;
    private readonly StartRequest _request;
    private readonly FoundryConnections _connections;
    private readonly ModelBudget? _budget;

    /// <summary>The connections for new work: offline templates while the model budget is spent (a public site's cap).</summary>
    private FoundryConnections Models => _budget?.Paused == true ? FoundryConnections.Offline : _connections;
    private readonly Broadcast _broadcast;
    private readonly ILogger _log;
    private readonly IReadOnlyList<Persona> _personas = Persona.DemoSet;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>();

    private readonly object _sync = new();
    private readonly List<PitchEvent> _events = [];
    private readonly List<CandidateView> _candidates = [];
    private readonly List<InsightView> _insights = [];
    private readonly List<ExpectationView> _groundTruth;
    private readonly Dictionary<string, (LiveMatch Frozen, InsightWork Work)> _done = new();
    private readonly List<RecapView> _recaps = [];
    private readonly RecapWriter _recapWriter;
    private Task _halfTime = Task.CompletedTask;

    // Predictions and live calls. The replay thread owns the commentator and the goal log; the lists are under _sync.
    private readonly MatchHistory _history;
    private readonly Predictor _predictor;
    private readonly Commentator _commentator;
    private readonly TrackingFeed _tracking;
    private readonly List<TagView> _tags = [];
    private readonly List<Prediction> _predictions = [];
    private readonly List<Call> _calls = [];
    private readonly Dictionary<string, Task> _pendingPredictions = new();
    /// <summary>Set pieces already taken: a prediction that is still being made when its kick happens is dropped.</summary>
    private readonly HashSet<string> _takenSetPieces = new(StringComparer.Ordinal);
    private readonly List<MatchEvent> _goalLog = [];
    private int _nextGoalCheckSec = 15 * 60;
    private readonly List<ShapeReport> _shapes = [];
    private readonly Dictionary<string, (AskResult, AskRequest)> _asks = new(StringComparer.Ordinal);
    private int _nextShapeSec = 5 * 60;

    // Tactical tests: opened when a read goes to air (by a worker or an editor's approval), measured and settled on the
    // replay thread against the live match. The definition is fixed when it opens; only progress and result change.
    private readonly List<TacticalTest> _tests = [];
    /// <summary>The coach's corner: each decision (proposals, Decision-1's pick, the two tests) and when the next runs.</summary>
    private readonly List<CoachDecision> _coaches = [];
    private int _nextCoachSec = -1;
    private const int CoachEverySec = 15 * 60;

    // Fault drills: an operator arms one, the next moment the workers take carries it, and the result is kept.
    private string? _armedPlant;
    private readonly List<FaultDrillView> _drills = [];
    private const int ShapeEverySec = 3 * 60;

    /// <summary>A set piece is predicted this many match seconds before it is taken, like the pause before a corner.</summary>
    public const int SetPieceLeadSec = 20;

    /// <summary>Real time a set-piece prediction stays on screen before the kick.</summary>
    public const int SetPieceHoldMs = 2500;
    private SessionStatus _status = SessionStatus.Running;
    private double _speed;
    private double _clockSec;
    private int _queueLength;
    private int _ingested;
    private bool _closed;

    private Task? _replay;
    private Task[] _workers = [];

    private sealed record Pending(Candidate Candidate, MatchEvent[] EventsSoFar);

    /// <param name="source">What is being played: a generated scenario or a real match.</param>
    public MatchSession(MatchSource source, StartRequest request, FoundryConnections connections, string models, ModelBudget? budget, Broadcast send, ILogger log)
    {
        _request = request;
        _connections = connections;
        _budget = budget;
        _broadcast = send;
        _log = log;
        _speed = Math.Clamp(request.Speed, 0.5, 120);
        _recording = source.Recording;
        _live = new LiveMatch(_recording.Match);
        _recapWriter = new RecapWriter(connections.Chat);
        _history = source.History ?? MatchHistory.Empty;
        _predictor = new Predictor(connections.Decisions, _history);
        _commentator = new Commentator(_recording.Match);
        _tracking = new TrackingFeed(_recording);
        _groundTruth = _recording.GroundTruth.Expectations
            .Select(x => new ExpectationView(x.Kind, x.TeamId, x.PlayerId, x.FromSec, x.ToSec, x.Rule, x.Verified, null))
            .ToList();

        Info = new SessionInfo(
            _recording.Match.MatchId, source.Id, source.Description, _recording.Match.Seed, _recording.Match,
            Live: !connections.IsOffline, Models: models, _personas.Select(p => p.Key).ToList(),
            Math.Max(0, request.StartMinute), _recording.Events[^1].MatchClockSec, source.Attribution);
    }

    public SessionInfo Info { get; }

    public void Start()
    {
        _workers = Enumerable.Range(0, Workers).Select(_ => Task.Run(() => WorkAsync(_cts.Token))).ToArray();
        _replay = Task.Run(() => ReplayAsync(_cts.Token));
    }

    // --------------------------------------------------------------------------------------------------------------
    // Controls
    // --------------------------------------------------------------------------------------------------------------

    public void Pause()
    {
        lock (_sync) if (_status == SessionStatus.Running) _status = SessionStatus.Paused;
        _ = Send("tick", Tick());
    }

    public void Resume()
    {
        lock (_sync) if (_status == SessionStatus.Paused) _status = SessionStatus.Running;
        _ = Send("tick", Tick());
    }

    public void SetSpeed(double speed)
    {
        lock (_sync) _speed = Math.Clamp(speed, 0.5, 120);
        _ = Send("tick", Tick());
    }

    /// <summary>Stops the match; safe to call more than once. A stopped session stays readable but sends nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            if (_status is not SessionStatus.Finished) _status = SessionStatus.Stopped;
        }
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_workers.Append(_replay ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Cancelled model calls end with exceptions; nothing to keep.
        }
    }

    /// <summary>
    /// Broadcasts unless the session was stopped: a model call that was already running when a new match started
    /// must not leak its results into the new match's screens.
    /// </summary>
    private Task Send(string message, object payload)
    {
        lock (_sync) if (_closed) return Task.CompletedTask;
        try { Sent?.Invoke(message, payload); }
        catch (Exception ex) { _log.LogWarning(ex, "A listener failed on {Message}", message); }
        return _broadcast(message, payload);
    }

    /// <summary>Every message the session broadcasts, for listeners on the server (the watch parties).</summary>
    public event Action<string, object>? Sent;

    // --------------------------------------------------------------------------------------------------------------
    // Views
    // --------------------------------------------------------------------------------------------------------------

    public SessionSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new SessionSnapshot(Info, TickLocked(), _events.ToList(), _candidates.ToList(), _insights.ToList(),
                _groundTruth.ToList(), _recaps.ToList(), _predictions.ToList(), _calls.ToList(), _shapes.ToList(), _tests.ToList(), _drills.ToList(),
                _tracking.Running(), _tracking.Report(), _tags.TakeLast(20).ToList(), _tracking.Passes(), _tracking.Control(), XtView.Current, _coaches.ToList());
        }
    }

    /// <summary>
    /// The match as it stands now, as a private copy that outside readers (the MCP tools) can query without racing
    /// the replay. The recording is immutable, so the copy is just the events played so far, replayed.
    /// </summary>
    public LiveMatch FreezeNow()
    {
        int count;
        lock (_sync) count = _ingested;
        return Freeze(_recording.Events.Take(count).ToList());
    }

    /// <summary>A team's shape over a window of what has been played so far (seconds; clamped to now).</summary>
    public ShapeReport Shape(string teamId, int fromSec, int toSec)
    {
        int count;
        lock (_sync) count = _ingested;
        var copy = new LiveMatch(_recording.Match, detectors: Array.Empty<IDetector>());
        foreach (MatchEvent e in _recording.Events.Take(count)) copy.Ingest(e);
        return ShapeAnalyzer.Measure(copy, teamId, Math.Max(0, fromSec), Math.Min(toSec, copy.ClockSec + 1));
    }

    /// <summary>
    /// Ask Second Ball IQ: answers a fan's question against the match frozen at this second, with what has been published
    /// and predicted so far. The Analyst's English moments stand for what went to air.
    /// </summary>
    public async Task<AskResult> AskAsync(AskRequest request, CancellationToken ct)
    {
        LiveMatch frozen = FreezeNow();
        List<PublishedMoment> moments;
        List<Prediction> predictions;
        lock (_sync)
        {
            moments = _insights
                .Where(i => i.PersonaKey == "Analyst-en" && i.Status is InsightStatus.Live or InsightStatus.Approved)
                .OrderBy(i => i.Insight.MatchClockSec)
                .Select(i => new PublishedMoment(NarrativeText.Clock(i.Insight.MatchClockSec), i.Insight.Kind.ToString(),
                    _recording.Match.Team(i.Insight.TeamIds.FirstOrDefault() ?? _recording.Match.Home.TeamId).Name,
                    i.Insight.Headline, i.Insight.Summary, i.Insight.EvidenceEventIds))
                .ToList();
            predictions = _predictions.ToList();
        }
        var analyst = new MatchAnalyst(Models.Chat, Models.Decisions);
        List<RunningLine> running = _tracking.Running()
            .Select(r => new RunningLine(r.PlayerId, r.Name, r.TeamId, _recording.Match.Team(r.TeamId).Name, r.DistanceKm, r.TopSpeedKmh, r.Sprints, r.HighIntensityKm))
            .ToList();
        AskResult result = await analyst.AskAsync(request, frozen, moments, predictions, ct, running, _tracking.Ready ? _tracking.Summary(frozen.ClockSec) : null);
        lock (_sync)
        {
            _asks[result.AskId] = (result, request);
            if (_asks.Count > 300) _asks.Remove(_asks.Keys.First());
        }
        _log.LogInformation("{Clock} ask \"{Question}\": {Status}, {Check}, tools {Tools}, {Ms:0} ms{Problems}",
            NarrativeText.Clock(result.ClockSec), request.Question, result.Status, result.Check, string.Join("+", result.ToolsUsed),
            result.Milliseconds, result.Problems.Count == 0 ? "" : " (" + string.Join("; ", result.Problems) + ")");
        return result;
    }

    public (AskResult Result, AskRequest Request)? Ask(string askId)
    {
        lock (_sync) return _asks.TryGetValue(askId, out var a) ? a : null;
    }

    /// <summary>Every player's running so far, from tracking (empty until the tracking is ready).</summary>
    public IReadOnlyList<RunnerView> Running() => _tracking.Running();

    /// <summary>Pitch control of the final third and missed better passes so far (null until tracking is ready).</summary>
    public SpatialSummary? Space() => _tracking.Ready ? _tracking.Summary(_live.ClockSec) : null;

    /// <summary>Auto-eventing from tracking alone, scored against the event feed so far.</summary>
    public AutoEventView? AutoEvents() => _tracking.Report();

    public IReadOnlyList<Prediction> Predictions()
    {
        lock (_sync) return _predictions.ToList();
    }

    public IReadOnlyList<Call> Calls()
    {
        lock (_sync) return _calls.ToList();
    }

    /// <summary>A player's numbers over the earlier matches of the season (real matches only).</summary>
    public PlayerSeason? SeasonOf(string playerId) => _history.Player(playerId);

    public IReadOnlyList<RecapView> Recaps()
    {
        lock (_sync) return _recaps.ToList();
    }

    public IReadOnlyList<CandidateView> Candidates()
    {
        lock (_sync) return _candidates.ToList();
    }

    public IReadOnlyList<InsightView> Insights()
    {
        lock (_sync) return _insights.ToList();
    }

    private TickView Tick()
    {
        lock (_sync) return TickLocked();
    }

    private TickView TickLocked()
    {
        int clock = (int)Math.Min(_clockSec, Info.EndClockSec);
        int home = 0, away = 0, period = 1;
        // Score and period come from the streamed events, which the lock already guards.
        foreach (PitchEvent e in _events)
        {
            if (e.Type == MatchEventType.Goal) { if (e.TeamId == Info.Match.Home.TeamId) home++; else away++; }
            period = e.Period;
        }
        return new TickView(clock, period, home, away, _status, _speed, _queueLength, ModelsPaused: !_connections.IsOffline && _budget?.Paused == true);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Replay
    // --------------------------------------------------------------------------------------------------------------

    private async Task ReplayAsync(CancellationToken ct)
    {
        IReadOnlyList<MatchEvent> all = _recording.Events;
        int next = 0;
        try
        {
            // Fast-forward: everything before the start minute is replayed at once, without agents.
            int startSec = Info.StartMinute * 60;
            var batch = new List<PitchEvent>();
            while (next < all.Count && all[next].MatchClockSec < startSec)
            {
                _live.Ingest(all[next]);
                batch.Add(View(all[next]));
                next++;
            }
            lock (_sync)
            {
                _events.AddRange(batch);
                _ingested = next;
                _clockSec = startSec;
            }
            if (batch.Count > 0) await Send("events", batch);
            await Send("tick", Tick());

            var watch = Stopwatch.StartNew();
            double last = 0;
            double lastTickClock = -1;
            bool trackingLive = false;
            double nextRunningAt = 0, nextReportAt = 0, nextControlAt = 0;
            while (next < all.Count && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                double now = watch.Elapsed.TotalSeconds;
                double elapsed = now - last;
                last = now;

                double clock;
                lock (_sync)
                {
                    if (_status != SessionStatus.Running) continue;
                    _clockSec += elapsed * _speed;
                    clock = _clockSec;
                }

                batch = [];
                var raised = new List<CandidateView>();
                var calls = new List<Call>();
                PrepareSetPiecePredictions(all, next, clock);
                while (next < all.Count && all[next].MatchClockSec <= clock)
                {
                    MatchEvent e = all[next];
                    // Presentation only: in a replay a set piece can wait (at most 4 s) until its prediction is on screen,
                    // then hold 2.5 s so viewers can read it, like the pause before a real corner. The hold does not move
                    // the match clock. With holds off, nothing on this path ever waits for a model: a prediction that is
                    // not ready by the kick is dropped, and the analytics, agents and gates all run off this loop.
                    if (e.SetPiece is not null && !_pendingPredictions.ContainsKey(e.EventId))
                        PrepareSetPiecePredictions(all, next, e.MatchClockSec);
                    if (_pendingPredictions.Remove(e.EventId, out Task? pending) && _request.PresentationHolds)
                    {
                        if (batch.Count > 0)
                        {
                            lock (_sync) { _events.AddRange(batch); _ingested = next; }
                            await Send("events", batch);
                            batch = [];
                        }
                        foreach (Call c in calls) await PublishCall(c);
                        calls.Clear();
                        try { await pending.WaitAsync(TimeSpan.FromSeconds(4), ct); }
                        catch (TimeoutException) { }
                        await Task.Delay(SetPieceHoldMs, ct);
                        last = watch.Elapsed.TotalSeconds;
                    }
                    next++;
                    if (e.SetPiece is not null) lock (_sync) _takenSetPieces.Add(e.EventId);
                    IReadOnlyList<Candidate> candidates = _live.Ingest(e);
                    batch.Add(View(e));
                    calls.AddRange(_commentator.OnEvent(e, _live));
                    calls.AddRange(SettleSetPiece(e));
                    if (e.Type == MatchEventType.Goal) _goalLog.Add(e);
                    if (candidates.Any(c => c.Kind is InsightKind.PressingShift or InsightKind.PossessionIllusion))
                        StartNextGoalPrediction(e.MatchClockSec);
                    if (e.Type == MatchEventType.PeriodEnd && e.Period == 1)
                    {
                        int eventsAtHalfTime = next, halfTimeSec = e.MatchClockSec;
                        _halfTime = Task.Run(() => HalfTimeAsync(eventsAtHalfTime, halfTimeSec, ct));
                    }
                    foreach (Candidate c in candidates)
                    {
                        if (c.Kind == InsightKind.PlayerMilestone && !_request.IncludeMilestones) continue;
                        raised.Add(Enqueue(c));
                    }
                }

                if (batch.Count > 0)
                {
                    lock (_sync)
                    {
                        _events.AddRange(batch);
                        _ingested = next;
                    }
                    await Send("events", batch);
                }
                foreach (CandidateView view in raised) await Send("candidate", view);

                // Tracking: positions to the browsers every tick; running stats and auto-eventing as the clock moves.
                if (_tracking.Advance(clock, quiet: !trackingLive) is { } tracked)
                {
                    trackingLive = true;
                    await Send("frame", tracked.Frame);
                    if (tracked.Passes.Count > 0) await Send("passinfo", tracked.Passes);
                    // Spatial detectors (territory, better pass): their candidates join the event detectors' queue.
                    foreach (Candidate sc in _tracking.Detect(_live, (int)clock)) await Send("candidate", Enqueue(sc));
                    if (now >= nextControlAt && _tracking.Control() is { } control)
                    {
                        nextControlAt = now + 1;
                        await Send("control", control);
                    }
                    foreach (KinematicAlert a in tracked.Alerts)
                    {
                        var tag = new TagView(a.PlayerId, a.TeamId, Math.Round(a.AtSec, 1), a.Kind == KinematicAlertKind.TopSpeed ? "speed" : "distance",
                            a.SpeedKmh, Math.Round(a.DistanceMetres / 1000, 1),
                            Math.Round(a.X / Pitch.LengthMetres * 100, 1), Math.Round(a.Y / Pitch.WidthMetres * 100, 1));
                        lock (_sync) { _tags.Add(tag); if (_tags.Count > 100) _tags.RemoveAt(0); }
                        await Send("tag", tag);
                        if (_commentator.OnRunning(a) is { } runCall) calls.Add(runCall);
                    }
                    if (now >= nextRunningAt)
                    {
                        nextRunningAt = now + 2;
                        await Send("running", _tracking.Running());
                    }
                    if (now >= nextReportAt && _tracking.Report() is { } report)
                    {
                        nextReportAt = now + 8;
                        await Send("autoevent", report);
                    }
                }
                await MeasureTests((int)clock, fullTime: false);
                calls.AddRange(SettleNextGoals((int)clock));
                if (clock >= _nextGoalCheckSec)
                {
                    StartNextGoalPrediction((int)clock);
                    _nextGoalCheckSec += 15 * 60;
                }
                foreach (Call call in calls) await PublishCall(call);
                if (clock >= _nextShapeSec)
                {
                    _nextShapeSec = (int)clock + ShapeEverySec;
                    await PublishShapes((int)clock);
                }
                // The coach's corner, for both teams, about every fifteen minutes of play (first one two minutes in).
                if (_nextCoachSec < 0) _nextCoachSec = ((int)clock + 120 + CoachEverySec - 1) / CoachEverySec * CoachEverySec;
                if (clock >= _nextCoachSec)
                {
                    int at = (int)clock;
                    _nextCoachSec = at + CoachEverySec;
                    if (at <= TacticalTests.LastOpenSec)
                        foreach (string teamId in new[] { Info.Match.Home.TeamId, Info.Match.Away.TeamId })
                            _ = Task.Run(() => RunCoachAsync(teamId, at, ct), ct);
                }
                if (clock - lastTickClock >= 1 || batch.Count > 0)
                {
                    lastTickClock = clock;
                    await Send("tick", Tick());
                }
            }

            // Full time: let the workers finish what is queued, then report.
            lock (_sync)
            {
                _clockSec = Info.EndClockSec;
                if (_status is SessionStatus.Running or SessionStatus.Paused) _status = SessionStatus.Finishing;
            }
            _queue.Writer.TryComplete();
            await Send("tick", Tick());
            await Task.WhenAll(_workers);
            await _halfTime;
            await MeasureTests(Info.EndClockSec, fullTime: true);
            foreach (Call call in SettleNextGoals(Info.EndClockSec + NextGoalGraceSec)) await PublishCall(call);
            await WriteRecapsAsync(fullTime: true, _recording.Events.Count, Info.EndClockSec, ct);
            lock (_sync) if (_status == SessionStatus.Finishing) _status = SessionStatus.Finished;
            await Send("tick", Tick());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Replay of {MatchId} failed", Info.MatchId);
        }
    }

    // --------------------------------------------------------------------------------------------------------------
    // Predictions and calls
    // --------------------------------------------------------------------------------------------------------------

    /// <summary>At full time an open "who scores next" settles as no goal.</summary>
    private const int NextGoalGraceSec = Predictor.NextGoalWindowSec;

    /// <summary>
    /// Starts the prediction for the next event when it is a set piece. It starts only once everything before the
    /// set piece has happened (the shot that won the corner, the foul that gave the penalty), so it never sees ahead.
    /// </summary>
    private void PrepareSetPiecePredictions(IReadOnlyList<MatchEvent> all, int next, double clock)
    {
        if (next >= all.Count) return;
        MatchEvent e = all[next];
        if (e.SetPiece is null || e.MatchClockSec > clock + SetPieceLeadSec || _pendingPredictions.ContainsKey(e.EventId)) return;
        lock (_sync) if (_predictions.Any(p => p.EventId == e.EventId)) return;
        PredictionRequest? request = _predictor.PrepareSetPiece(e, all, _recording.Match,
            _live.Goals(_recording.Match.Home.TeamId), _live.Goals(_recording.Match.Away.TeamId));
        if (request is not null) _pendingPredictions[e.EventId] = PredictAndPublishAsync(request, _cts.Token);
    }

    /// <summary>"Who scores in the next 10 minutes", unless one is already open or the match is nearly over.</summary>
    private void StartNextGoalPrediction(int clockSec)
    {
        if (clockSec > Info.EndClockSec - Predictor.NextGoalWindowSec) return;
        lock (_sync)
            if (_predictions.Any(p => p.Kind == PredictionKind.NextGoal && p.Outcome is null)) return;
        PredictionRequest request = _predictor.PrepareNextGoal(_live, clockSec);
        _ = PredictAndPublishAsync(request, _cts.Token);
    }

    private async Task PredictAndPublishAsync(PredictionRequest request, CancellationToken ct)
    {
        try
        {
            Prediction prediction = await _predictor.PredictAsync(request, _recording.Match, ct);
            lock (_sync)
            {
                if (_predictions.Any(p => p.PredictionId == prediction.PredictionId)) return;
                // Too late: the kick was already taken while the prediction was being made.
                if (prediction.EventId is { } kick && _takenSetPieces.Contains(kick)) return;
                _predictions.Add(prediction);
            }
            await Send("prediction", prediction);
            await PublishCall(_commentator.PredictionMade(prediction));
            _log.LogInformation("{Clock} prediction {Kind}: {Pick} ({Source})", NarrativeText.Clock(prediction.ClockSec),
                prediction.Kind, prediction.Pick, prediction.Source);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Prediction failed");
        }
    }

    private IEnumerable<Call> SettleSetPiece(MatchEvent e)
    {
        if (e.SetPiece is null) yield break;
        Prediction? settled = null;
        lock (_sync)
        {
            int i = _predictions.FindIndex(p => p.EventId == e.EventId && p.Outcome is null);
            if (i >= 0) settled = _predictions[i] = Predictor.Settle(_predictions[i], e);
        }
        if (settled?.Outcome is null) yield break;
        _ = Send("prediction", settled);
        if (_commentator.PredictionSettled(settled, e.MatchClockSec) is { } call) yield return call;
    }

    private List<Call> SettleNextGoals(int clockSec)
    {
        var calls = new List<Call>();
        var settled = new List<Prediction>();
        lock (_sync)
        {
            for (int i = 0; i < _predictions.Count; i++)
            {
                Prediction p = _predictions[i];
                if (p.Kind != PredictionKind.NextGoal || p.Outcome is not null || p.SettleAtSec is not { } end) continue;
                MatchEvent? goal = _goalLog.FirstOrDefault(g => g.MatchClockSec > p.ClockSec && g.MatchClockSec <= end);
                if (goal is null && clockSec < end) continue;
                _predictions[i] = Predictor.SettleNextGoal(p, _recording.Match, goal);
                settled.Add(_predictions[i]);
            }
        }
        foreach (Prediction p in settled)
        {
            _ = Send("prediction", p);
            if (_commentator.PredictionSettled(p, clockSec) is { } call) calls.Add(call);
        }
        return calls;
    }

    /// <summary>Both teams' shape over the last 15 minutes, for the board's shape view.</summary>
    private async Task PublishShapes(int clockSec)
    {
        int from = Math.Max(0, clockSec - _live.Settings.ShapeWindowSec);
        var reports = new List<ShapeReport>
        {
            ShapeAnalyzer.Measure(_live, _recording.Match.Home.TeamId, from, clockSec),
            ShapeAnalyzer.Measure(_live, _recording.Match.Away.TeamId, from, clockSec),
        };
        lock (_sync)
        {
            _shapes.Clear();
            _shapes.AddRange(reports);
        }
        await Send("shape", reports);
    }

    private async Task PublishCall(Call call)
    {
        lock (_sync) _calls.Add(call);
        await Send("call", call);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Recaps
    // --------------------------------------------------------------------------------------------------------------

    /// <summary>Waits briefly for first-half moments still with the agents, then writes the half-time recaps.</summary>
    private async Task HalfTimeAsync(int eventCount, int halfTimeSec, CancellationToken ct)
    {
        try
        {
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(60) && FirstHalfStillProcessing(halfTimeSec))
                await Task.Delay(250, ct);
            await WriteRecapsAsync(fullTime: false, eventCount, halfTimeSec, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Half-time recap failed");
        }
    }

    private bool FirstHalfStillProcessing(int halfTimeSec)
    {
        lock (_sync)
            return _candidates.Any(c => c.DetectedAtSec <= halfTimeSec && c.Status is CandidateStatus.Queued or CandidateStatus.Processing);
    }

    /// <summary>
    /// One recap per persona, in parallel. The Recap Writer sees the match as it stood at <paramref name="clockSec"/>:
    /// the score, the goals, each team's measured stats and the insights published up to then.
    /// </summary>
    private async Task WriteRecapsAsync(bool fullTime, int eventCount, int clockSec, CancellationToken ct)
    {
        LiveMatch frozen = Freeze(_recording.Events.Take(eventCount).ToList());
        var queries = new MatchQueries(frozen);
        MatchInfo info = frozen.Info;
        int toMinute = clockSec / 60 + 1;
        var stats = new[] { info.Home.TeamId, info.Away.TeamId }.Select(t => queries.GetWindowStats(t, 0, toMinute)).ToList();

        // Goals with their scorers: the goal event follows the scoring shot.
        var goals = new List<RecapGoal>();
        var lastShot = new Dictionary<string, MatchEvent>();
        foreach (MatchEvent e in frozen.Events)
        {
            if (e.Type == MatchEventType.Shot) lastShot[e.TeamId] = e;
            if (e.Type == MatchEventType.Goal)
                goals.Add(new RecapGoal(e.MatchClockSec, e.TeamId, e.PlayerId ?? lastShot.GetValueOrDefault(e.TeamId)?.PlayerId));
        }

        List<InsightView> published;
        Dictionary<string, Candidate> candidates;
        lock (_sync)
        {
            published = _insights.Where(i => i.Status is InsightStatus.Live or InsightStatus.Approved && i.Insight.MatchClockSec <= clockSec).ToList();
            candidates = _done.ToDictionary(d => d.Key, d => d.Value.Work.Candidate);
        }

        var tasks = _personas.Select(async persona =>
        {
            // Each moment in this persona's own words when it went to air for them, otherwise as it went to air in English.
            var moments = published.GroupBy(i => i.CandidateId)
                .Where(g => candidates.ContainsKey(g.Key))
                .Select(g => g.FirstOrDefault(i => i.PersonaKey == persona.Key)
                             ?? g.FirstOrDefault(i => i.Insight.Language == persona.Language)
                             ?? g.First())
                .OrderBy(i => i.Insight.MatchClockSec)
                .Select(i => new RecapMoment(i.Insight.InsightId, i.Insight.MatchClockSec, i.Insight.Kind, i.Insight.TeamIds[0],
                    i.Insight.Headline, i.Insight.Summary, candidates[i.CandidateId].Facts))
                .ToList();
            var input = new RecapInput(info, fullTime, clockSec, frozen.Goals(info.Home.TeamId), frozen.Goals(info.Away.TeamId),
                goals, stats, moments);

            var watch = Stopwatch.StartNew();
            (RecapDraft draft, bool fromTemplate, IReadOnlyList<string> problems) = await _recapWriter.WriteAsync(input, persona, ct);
            var byId = moments.ToDictionary(m => m.InsightId);
            var view = new RecapView(
                $"recap-{(fullTime ? "ft" : "ht")}-{persona.Key}", persona.Key, fullTime, clockSec, input.HomeGoals, input.AwayGoals,
                draft.Headline, draft.Paragraphs,
                draft.KeyMoments.Where(k => byId.ContainsKey(k.InsightId))
                    .Select(k => new RecapMomentView(k.InsightId, byId[k.InsightId].ClockSec, byId[k.InsightId].Kind, byId[k.InsightId].TeamId, k.Text))
                    .ToList(),
                fromTemplate, problems, Math.Round(watch.Elapsed.TotalMilliseconds));
            lock (_sync) _recaps.Add(view);
            await Send("recap", view);
            _log.LogInformation("{When} recap {Persona}: {Source} in {Seconds:0.0} s{Problems}",
                fullTime ? "Full-time" : "Half-time", persona.Key, fromTemplate ? "template" : "Recap Writer",
                watch.Elapsed.TotalSeconds, problems.Count == 0 ? "" : " (" + string.Join("; ", problems) + ")");
        });
        await Task.WhenAll(tasks);
    }

    private CandidateView Enqueue(Candidate c)
    {
        // The agents work on the match as it stood at this moment.
        var pending = new Pending(c, _live.Events.ToArray());
        var view = new CandidateView(
            c.CandidateId, c.Kind, c.TeamId, c.PlayerId, c.DetectedAtSec, c.FromSec, c.ToSec, c.Facts,
            c.EvidenceEventIds, c.Evidence, c.WindowSummary, CandidateStatus.Queued, Tags: c.Tags);
        lock (_sync)
        {
            _candidates.Add(view);
            _queueLength++;
            for (int i = 0; i < _groundTruth.Count; i++)
            {
                ExpectationView x = _groundTruth[i];
                if (x.HitCandidateId is null && x.Kind == c.Kind && x.TeamId == c.TeamId &&
                    (x.PlayerId is null || x.PlayerId == c.PlayerId) &&
                    c.DetectedAtSec >= x.FromSec && c.DetectedAtSec <= x.ToSec + LateToleranceSec)
                {
                    _groundTruth[i] = x with { HitCandidateId = c.CandidateId };
                    _ = Send("groundTruth", _groundTruth[i]);
                    break;
                }
            }
        }
        _queue.Writer.TryWrite(pending);
        return view;
    }

    private PitchEvent View(MatchEvent e)
    {
        bool away = e.TeamId == _recording.Match.Away.TeamId;
        PitchPoint? start = e.Start is { } s ? (away ? Pitch.Mirror(s) : s) : null;
        PitchPoint? end = e.End is { } t ? (away ? Pitch.Mirror(t) : t) : null;
        return new PitchEvent(
            e.EventId, e.Seq, e.MatchClockSec, e.Period, e.Type, e.TeamId, e.PlayerId,
            Round(start?.X), Round(start?.Y), Round(end?.X), Round(end?.Y),
            e.Outcome, e.Xg is { } xg ? Math.Round(xg, 3) : null, e.ShotResult, Pitch.IsHighRecovery(e), e.SetPiece,
            e.BallSpeedKmh, BallMetrics.PassMetres(e), BallMetrics.PassDifficulty(e));

        static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
    }

    // --------------------------------------------------------------------------------------------------------------
    // Pipeline workers
    // --------------------------------------------------------------------------------------------------------------

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (Pending pending in _queue.Reader.ReadAllAsync(ct))
            {
                await ProcessAsync(pending, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(Pending pending, CancellationToken ct)
    {
        Candidate c = pending.Candidate;
        await Update(c.CandidateId, v => v with { Status = CandidateStatus.Processing });
        var watch = Stopwatch.StartNew();
        try
        {
            LiveMatch frozen = Freeze(pending.EventsSoFar);
            // A goal is never used for a drill: it must always reach the screen first time.
            PlantedError? plant = null;
            if (c.Kind != InsightKind.Goal)
                lock (_sync)
                    if (_armedPlant is { } kind) { plant = new PlantedError(kind); _armedPlant = null; }
            var pipeline = new InsightPipeline(frozen, Models, new PipelineSettings { Plant = plant });
            InsightWork work = await pipeline.ProcessAsync(c, _personas, ct,
                (_, step) => Send("step", new StepView(c.CandidateId, step)));

            var published = new List<InsightView>();
            lock (_sync)
            {
                _done[c.CandidateId] = (frozen, work);
                foreach ((Insight insight, Persona persona) in work.Insights.Zip(_personas))
                {
                    PublishMode mode = insight.Publish?.Decision ?? PublishMode.Editor;
                    var view = new InsightView(
                        insight, c.CandidateId, persona.Key,
                        mode switch
                        {
                            PublishMode.Auto => InsightStatus.Live,
                            PublishMode.Editor => InsightStatus.Pending,
                            _ => InsightStatus.Dropped,
                        },
                        mode == PublishMode.Editor ? EditorNote(work, persona) : mode == PublishMode.Drop ? insight.Validation?.Reason : null,
                        DateTimeOffset.UtcNow);
                    _insights.Add(view);
                    published.Add(view);
                }
            }

            string outcome = work.Stopped
                ? work.StopReason ?? "stopped"
                : string.Join(", ", work.Insights.GroupBy(i => i.Publish!.Decision).Select(g => $"{g.Count()} {g.Key}"));
            List<TraceStep> trace;
            lock (work.Trace) trace = work.Trace.ToList();
            await Update(c.CandidateId, v => v with
            {
                Status = CandidateStatus.Done,
                Outcome = outcome,
                TriageChoice = work.TriageChoice,
                Analysis = work.Analysis?.WhyItMatters,
                AnalysisFromTemplate = work.AnalysisFromTemplate,
                Trace = trace,
                TotalMs = Math.Round(watch.Elapsed.TotalMilliseconds),
            });
            if (work.Planted is { } drill)
            {
                var drillView = new FaultDrillView(c.CandidateId, c.Kind, NarrativeText.Clock(c.DetectedAtSec), drill.Kind, drill.PersonaKey,
                    drill.Planted, drill.CaughtBy, DateTimeOffset.UtcNow);
                lock (_sync) _drills.Add(drillView);
                await Send("drill", drillView);
            }
            foreach (InsightView view in published) await Send("insight", view);
            if (published.Any(v => v.Status == InsightStatus.Live)) await OpenTestAsync(c.CandidateId, ct);
            _log.LogInformation("{Clock} {Kind} {CandidateId}: {Outcome} in {Seconds:0.0} s",
                NarrativeText.Clock(c.DetectedAtSec), c.Kind, c.CandidateId, outcome, watch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Candidate {CandidateId} failed", c.CandidateId);
            await Update(c.CandidateId, v => v with { Status = CandidateStatus.Failed, Outcome = ex.Message });
        }
        finally
        {
            lock (_sync) _queueLength--;
        }
    }

    /// <summary>Why the editor is being asked: the gate that was unsure.</summary>
    private static string EditorNote(InsightWork work, Persona persona)
    {
        var reasons = new List<string>();
        if (work.NeedsEditor)
            reasons.Add(work.Triage is { } t ? "triage unsure: " + t.Reason : "triage unavailable");
        if (work.Grounding.GetValueOrDefault(persona.Key) is { Outcome: not GateOutcome.Proceed } g)
            reasons.Add("grounding: " + g.Reason);
        if (work.TemplateDrafts.Contains(persona.Key)) reasons.Add("template text");
        if (work.Planted is { } p && p.PersonaKey == persona.Key && !work.TemplateDrafts.Contains(persona.Key))
            reasons.Insert(0, "FAULT DRILL: this draft carries a planted error, reject it");
        return reasons.Count == 0 ? "review" : string.Join("; ", reasons);
    }

    /// <summary>A private copy of the match up to the candidate; cheap (a few ms) and free of races.</summary>
    private LiveMatch Freeze(IReadOnlyList<MatchEvent> events)
    {
        var frozen = new LiveMatch(_recording.Match);
        foreach (MatchEvent e in events) frozen.Ingest(e);
        return frozen;
    }

    private async Task Update(string candidateId, Func<CandidateView, CandidateView> change)
    {
        CandidateView? updated = null;
        lock (_sync)
        {
            int i = _candidates.FindIndex(v => v.CandidateId == candidateId);
            if (i >= 0) updated = _candidates[i] = change(_candidates[i]);
        }
        if (updated is not null) await Send("candidate", updated);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Editor
    // --------------------------------------------------------------------------------------------------------------

    public async Task<EditorResult> ApproveAsync(string insightId, string editor) =>
        await DecideAsync(insightId, editor, InsightStatus.Approved, null, null);

    public async Task<EditorResult> RejectAsync(string insightId, string editor) =>
        await DecideAsync(insightId, editor, InsightStatus.Rejected, null, null);

    /// <summary>
    /// Publishes an edited text. The edit goes through the same deterministic Validator as the agents' drafts, so
    /// not even an editor can put a number on screen that the engine did not measure.
    /// </summary>
    public async Task<EditorResult> EditAsync(string insightId, EditRequest edit)
    {
        string headline = edit.Headline.Trim(), summary = edit.Summary.Trim();
        var problems = new List<string>();
        if (headline.Length == 0 || summary.Length == 0) problems.Add("headline and summary are required");
        if (headline.Length > Validator.MaxHeadlineChars) problems.Add($"headline is longer than {Validator.MaxHeadlineChars} characters");

        lock (_sync)
        {
            InsightView? view = _insights.FirstOrDefault(i => i.Insight.InsightId == insightId);
            if (view is null) return new EditorResult(false, null, ["unknown insight"]);
            if (!_done.TryGetValue(view.CandidateId, out var done)) return new EditorResult(false, null, ["candidate not processed"]);

            AnalysisResult analysis = done.Work.Analysis ?? Templates.Analysis(done.Work.Candidate);
            var draft = new NarrativeDraft { Headline = headline, Summary = summary };
            ValidationResult result = Validator.Check(done.Work.Candidate, analysis, draft, done.Frozen);
            problems.AddRange(result.Problems);
            if (problems.Count > 0) return new EditorResult(false, view, problems);
        }
        return await DecideAsync(insightId, edit.Editor ?? "editor", InsightStatus.Approved, headline, summary);
    }

    private async Task<EditorResult> DecideAsync(string insightId, string editor, InsightStatus status, string? headline, string? summary)
    {
        InsightView updated;
        lock (_sync)
        {
            int i = _insights.FindIndex(v => v.Insight.InsightId == insightId);
            if (i < 0) return new EditorResult(false, null, ["unknown insight"]);
            InsightView view = _insights[i];
            if (view.Status is not (InsightStatus.Pending or InsightStatus.Live or InsightStatus.Approved))
                return new EditorResult(false, view, [$"insight is {view.Status.ToString().ToLowerInvariant()}"]);

            bool edited = headline is not null && (headline != view.Insight.Headline || summary != view.Insight.Summary);
            Insight insight = view.Insight with
            {
                Headline = headline ?? view.Insight.Headline,
                Summary = summary ?? view.Insight.Summary,
                Publish = new PublishDecision(view.Insight.Publish?.Decision ?? PublishMode.Editor, view.Insight.Publish?.P,
                    edited ? editor + ":edited" : editor),
            };
            updated = _insights[i] = view with
            {
                Insight = insight,
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow,
                OriginalHeadline = edited ? view.OriginalHeadline ?? view.Insight.Headline : view.OriginalHeadline,
                OriginalSummary = edited ? view.OriginalSummary ?? view.Insight.Summary : view.OriginalSummary,
            };
        }
        await Send("insight", updated);
        if (status == InsightStatus.Approved) await OpenTestAsync(updated.CandidateId, _cts.Token);
        return new EditorResult(true, updated, []);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Tactical tests
    // --------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<TacticalTest> Tests()
    {
        lock (_sync) return _tests.ToList();
    }

    /// <summary>
    /// Locks a test for a read that just went to air: the criterion, the event rule and the deadline are fixed here and
    /// never change. Decision-1's estimate is asked for afterwards from the match as it stood at the lock, so it cannot
    /// see anything that happened later; it either arrives within three seconds or the test has no estimate.
    /// </summary>
    private async Task OpenTestAsync(string candidateId, CancellationToken ct)
    {
        TacticalTest? test;
        lock (_sync)
        {
            if (_status is not (SessionStatus.Running or SessionStatus.Paused)) return;
            if (_tests.Any(t => t.CandidateId == candidateId) || !_done.TryGetValue(candidateId, out var done)) return;
            Candidate c = done.Work.Candidate;
            // One open test per team and kind: a second read of the same thing waits for the first to settle.
            if (_tests.Any(t => t.Status == TestStatus.Open && t.Kind == c.Kind && t.TeamId == c.TeamId)) return;
            var aired = _insights.Where(i => i.CandidateId == candidateId && i.Status is InsightStatus.Live or InsightStatus.Approved).ToList();
            InsightView? read = aired.FirstOrDefault(i => i.PersonaKey == "Casual-en")
                ?? aired.FirstOrDefault(i => i.PersonaKey == "Analyst-en") ?? aired.FirstOrDefault();
            if (read is null) return;
            test = TacticalTests.Define(c, Info.Match, (int)_clockSec, read.Insight.Headline);
            if (test is null) return;
            _tests.Add(test);
        }
        await Send("test", test);
        _log.LogInformation("{Clock} test {TestId} opened: {Criterion}", NarrativeText.Clock(test.OpensAtSec), test.TestId, test.Criterion);

        LiveMatch atLock = Freeze(_recording.Events.TakeWhile(e => e.MatchClockSec <= test.OpensAtSec).ToList());
        TacticalTest estimated = await TacticalTests.EstimateAsync(test, atLock, Models.Decisions, ct);
        if (estimated.Estimate is null) return;
        TacticalTest? updated = null;
        lock (_sync)
        {
            int i = _tests.FindIndex(t => t.TestId == test.TestId);
            if (i >= 0) updated = _tests[i] = _tests[i] with { Estimate = estimated.Estimate, EstimateSource = estimated.EstimateSource };
        }
        if (updated is not null) await Send("test", updated);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Coach's corner and the what-if sandbox
    // --------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<CoachDecision> Coaches()
    {
        lock (_sync) return _coaches.ToList();
    }

    /// <summary>Runs the coach's corner for a team now (the studio or a fan asked); null when one is still being settled.</summary>
    public Task<CoachDecision?> RunCoachNowAsync(string teamId) => RunCoachAsync(teamId, (int)_clockSec, _cts.Token);

    /// <summary>
    /// Two coach agents propose plans for one team, Decision-1 picks between them, and both plans are locked as
    /// tactical tests. One decision per team at a time: a new one waits until both of the last one's tests settle.
    /// </summary>
    private async Task<CoachDecision?> RunCoachAsync(string teamId, int atSec, CancellationToken ct)
    {
        string decisionId = $"coach-{teamId}-{atSec}";
        lock (_sync)
        {
            if (_status is not (SessionStatus.Running or SessionStatus.Paused) || atSec > TacticalTests.LastOpenSec) return null;
            if (_coaches.Any(d => d.TeamId == teamId && d.TestIds.Values.Any(id => _tests.Any(t => t.TestId == id && t.Status == TestStatus.Open)))) return null;
            if (_coaches.Any(d => d.DecisionId == decisionId)) return null;
        }
        try
        {
            LiveMatch atLock = Freeze(_recording.Events.TakeWhile(e => e.MatchClockSec <= atSec).ToList());
            string opp = Info.Match.OpponentOf(teamId);
            double? control = _tracking.FinalThirdShare(teamId, Math.Max(0, atSec - 600), atSec);
            double? oppControl = _tracking.FinalThirdShare(opp, Math.Max(0, atSec - 600), atSec);
            var corner = new CoachCorner(Models.Chat, Models.Decisions);
            CoachDecision d = await corner.RunAsync(decisionId, atLock, teamId, atSec, control, oppControl, ct);

            var tests = new List<TacticalTest>();
            var ids = new Dictionary<string, string>();
            foreach (CoachProposal p in d.Proposals)
            {
                CoachPlanOption plan = d.Menu.First(m => m.Kind == p.Plan);
                TacticalTest t = TacticalTests.ForCoachPlan(decisionId, p.Coach, p.CoachName, plan, teamId, atSec, d.Probabilities?.GetValueOrDefault(p.Coach));
                tests.Add(t);
                ids[p.Coach] = t.TestId;
            }
            d = d with { TestIds = ids };
            lock (_sync)
            {
                _tests.AddRange(tests);
                _coaches.Add(d);
            }
            foreach (TacticalTest t in tests) await Send("test", t);
            await Send("coach", d);
            _log.LogInformation("{Clock} coach's corner {Team}: {A} vs {B}, Decision-1 picked {Pick}", NarrativeText.Clock(atSec),
                Info.Match.Team(teamId).Name, d.Proposals[0].Plan, d.Proposals[1].Plan, d.Picked ?? "none");
            return d;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Coach's corner failed for {Team}", teamId);
            return null;
        }
    }

    /// <summary>
    /// What-if: move one team on the tracking frame now and measure what it does to pitch control; Decision-1 is
    /// asked whether the change would help. Null before tracking is ready.
    /// </summary>
    public async Task<WhatIfView?> WhatIfAsync(string teamId, WhatIfChange change, double metres, CancellationToken ct)
    {
        if (_tracking.FramesAt(_clockSec) is not { } frames) return null;
        WhatIfResult r = WhatIf.Run(frames.Now, frames.Before, Info.Match, teamId, change, Math.Clamp(metres, 1, 20));
        string team = Info.Match.Team(teamId).Name;
        double? p = null;
        string source = "none";
        if (Models.Decisions is { } decisions)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                LiveMatch now = Freeze(_recording.Events.TakeWhile(e => e.MatchClockSec <= (int)r.T).ToList());
                string what = change switch
                {
                    WhatIfChange.PushUp => $"pushed every outfield player {metres:0} metres further up the pitch",
                    WhatIfChange.DropDeep => $"dropped every outfield player {metres:0} metres deeper",
                    WhatIfChange.Widen => "spread out wider across the pitch",
                    WhatIfChange.Narrow => "squeezed narrower across the pitch",
                    _ => $"sent their three nearest players {metres:0} metres towards the ball",
                };
                string state = CoachPlans.Brief(now, teamId, (int)r.T, null, null) +
                    $" If {team} {what}, their share of the final third they attack would go from {r.FinalThirdBefore * 100:0}% to {r.FinalThirdAfter * 100:0}%, " +
                    $"and of the third they defend from {r.OwnThirdBefore * 100:0}% to {r.OwnThirdAfter * 100:0}% (pitch control: who would reach each spot first).";
                DecisionResult result = await decisions.DecideAsync(state, new Dictionary<string, DecisionQuestion>
                {
                    ["help"] = new YesNoQuestion($"Would this change help {team} over the next ten minutes?"),
                }, timeout.Token);
                p = Math.Round(result.Get<YesNoAnswer>("help").Probability, 2);
                source = "Decision-1";
            }
            catch (Exception ex) when (ex is DecisionException or HttpRequestException or OperationCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                source = "unavailable";
            }
        }
        _log.LogInformation("{Clock} what-if {Team} {Change} {Metres:0} m: final third {F0:P0} -> {F1:P0}, own third {O0:P0} -> {O1:P0}, would help {Help} ({Source})",
            NarrativeText.Clock((int)r.T), team, change, metres, r.FinalThirdBefore, r.FinalThirdAfter, r.OwnThirdBefore, r.OwnThirdAfter, p?.ToString("0.00") ?? "-", source);
        static double[] G(float[] g) => g.Select(v => Math.Round((double)v, 2)).ToArray();
        return new WhatIfView(r.TeamId, r.Change, r.Metres, Math.Round(r.T, 1), r.ControlBefore, r.ControlAfter, r.FinalThirdBefore, r.FinalThirdAfter,
            r.OwnThirdBefore, r.OwnThirdAfter, PitchControl.Cols, PitchControl.Rows, G(r.HomeBefore), G(r.HomeAfter), r.Moved
                .Select(m => new MovedView(m.PlayerId, Pct(m.FromX, Pitch.LengthMetres), Pct(m.FromY, Pitch.WidthMetres), Pct(m.ToX, Pitch.LengthMetres), Pct(m.ToY, Pitch.WidthMetres))).ToList(),
            p, source,
            // Everyone the change left where they were (both teams), so the maps show the whole picture.
            frames.Now.Players.Where(q => r.Moved.All(m => m.PlayerId != q.PlayerId))
                .Select(q => new MovedView(q.PlayerId, Pct(q.X, Pitch.LengthMetres), Pct(q.Y, Pitch.WidthMetres), Pct(q.X, Pitch.LengthMetres), Pct(q.Y, Pitch.WidthMetres))).ToList());
    }

    private static double Pct(double metres, double size) => Math.Round(metres / size * 100, 1);

    /// <summary>Once both of a decision's tests are settled: was Decision-1's pick the plan that held?</summary>
    private async Task JudgeCoaches()
    {
        List<CoachDecision> judged = [];
        lock (_sync)
        {
            for (int i = 0; i < _coaches.Count; i++)
            {
                if (_coaches[i].Verdict is not null) continue;
                string? verdict = CoachPlans.Judge(_coaches[i], id => _tests.FirstOrDefault(t => t.TestId == id));
                if (verdict is null) continue;
                _coaches[i] = _coaches[i] with { Verdict = verdict };
                judged.Add(_coaches[i]);
            }
        }
        foreach (CoachDecision d in judged) await Send("coach", d);
    }

    /// <summary>Measures every open test against the live match (replay thread only) and sends the ones that moved.</summary>
    private async Task MeasureTests(int clockSec, bool fullTime)
    {
        List<TacticalTest> open;
        lock (_sync) open = _tests.Where(t => t.Status == TestStatus.Open).ToList();
        foreach (TacticalTest before in open)
        {
            TacticalTest after = TacticalTests.Measure(before, _live, clockSec, fullTime, _tracking.FinalThirdShare);
            if (after.Status == before.Status && after.ProgressText == before.ProgressText) continue;
            TacticalTest? current = null;
            lock (_sync)
            {
                int i = _tests.FindIndex(t => t.TestId == before.TestId);
                if (i < 0) continue;
                // The estimate may have arrived while measuring; keep it.
                current = _tests[i] = after with { Estimate = _tests[i].Estimate, EstimateSource = _tests[i].EstimateSource };
            }
            await Send("test", current);
            if (current.Status != TestStatus.Open)
                _log.LogInformation("{Clock} test {TestId}: {Status}", NarrativeText.Clock(clockSec), current.TestId, current.Status);
        }
        await JudgeCoaches();
    }

    // --------------------------------------------------------------------------------------------------------------
    // Fault drills
    // --------------------------------------------------------------------------------------------------------------

    /// <summary>Arms a planted error for the next moment the workers take (not a goal). Returns false for an unknown kind.</summary>
    public bool ArmPlant(string kind)
    {
        if (!PlantedError.IsKnown(kind)) return false;
        lock (_sync) _armedPlant = kind;
        _ = Send("drillArmed", new { kind });
        return true;
    }

    public string? ArmedPlant
    {
        get { lock (_sync) return _armedPlant; }
    }
}
