using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// The analytics engine for one match. Feed it events in order with <see cref="Ingest"/>; it keeps the running
/// state and returns any candidates the detectors raise. Event detectors run on every event; window detectors
/// run every <see cref="DetectorSettings.EvaluateEverySec"/> seconds of match time, never per event.
/// Deterministic: replaying the same events gives the same candidates.
/// </summary>
public sealed class LiveMatch
{
    private readonly List<MatchEvent> _events = [];
    private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _goals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerTally> _players = new(StringComparer.Ordinal);
    private readonly List<Candidate> _candidates = [];
    private readonly List<MatchEvent> _substitutions = [];
    private readonly IReadOnlyList<IDetector> _detectors;
    private int _nextEvaluationSec;

    /// <param name="detectors">Detectors keep per-match state (cooldowns, counts): give each match its own set.</param>
    public LiveMatch(MatchInfo info, DetectorSettings? settings = null, IReadOnlyList<IDetector>? detectors = null)
    {
        Info = info;
        Settings = settings ?? DetectorSettings.Default;
        _detectors = detectors ?? Detectors.CreateDefault();
        _goals[info.Home.TeamId] = 0;
        _goals[info.Away.TeamId] = 0;
        _nextEvaluationSec = Settings.EvaluateEverySec;
        Text = new NarrativeText(this);
    }

    public MatchInfo Info { get; }

    public DetectorSettings Settings { get; }

    public NarrativeText Text { get; }

    public IReadOnlyList<MatchEvent> Events => _events;

    public IReadOnlyList<Candidate> Candidates => _candidates;

    /// <summary>Clock of the latest event, or 0 before kick-off.</summary>
    public int ClockSec => _events.Count == 0 ? 0 : _events[^1].MatchClockSec;

    public int Goals(string teamId) => _goals.TryGetValue(teamId, out int goals) ? goals : 0;

    public PlayerTally? Player(string playerId) => _players.GetValueOrDefault(playerId);

    public IEnumerable<PlayerTally> Players => _players.Values;

    public MatchEvent? Event(string eventId) =>
        _indexById.TryGetValue(eventId, out int index) ? _events[index] : null;

    /// <summary>
    /// Adds one event and returns the candidates it caused. Events must arrive in order; an event whose clock
    /// goes backwards or whose id repeats is rejected rather than silently corrupting the state.
    /// </summary>
    public IReadOnlyList<Candidate> Ingest(MatchEvent e)
    {
        if (_indexById.ContainsKey(e.EventId))
            throw new InvalidDataException($"Duplicate event id {e.EventId}.");
        if (_events.Count > 0 && e.MatchClockSec < ClockSec)
            throw new InvalidDataException($"Event {e.EventId} at {e.MatchClockSec}s arrived after {ClockSec}s.");

        var raised = new List<Candidate>();

        // Window detectors see the match up to each evaluation point, excluding the event that crossed it.
        while (e.MatchClockSec >= _nextEvaluationSec)
        {
            foreach (IDetector detector in _detectors)
                raised.AddRange(detector.OnTick(this, _nextEvaluationSec));
            _nextEvaluationSec += Settings.EvaluateEverySec;
        }

        _indexById[e.EventId] = _events.Count;
        _events.Add(e);
        Track(e);

        foreach (IDetector detector in _detectors)
            raised.AddRange(detector.OnEvent(this, e));

        // Detectors leave the id empty; ids are assigned here so they are unique and in order.
        for (int i = 0; i < raised.Count; i++)
            raised[i] = raised[i] with { CandidateId = $"cand-{_candidates.Count + i + 1:D4}" };
        _candidates.AddRange(raised);
        return raised;
    }

    /// <summary>
    /// Candidates raised outside the event stream (the spatial detectors on tracking): gives them ids in the same
    /// sequence and records them with the rest.
    /// </summary>
    public IReadOnlyList<Candidate> Raise(IReadOnlyList<Candidate> raised)
    {
        var list = raised.Select((c, i) => c with { CandidateId = $"cand-{_candidates.Count + i + 1:D4}" }).ToList();
        _candidates.AddRange(list);
        return list;
    }

    /// <summary>Events with a clock in [fromSec, toSec), in order. Found by binary search, so windows are cheap.</summary>
    public IEnumerable<MatchEvent> Between(int fromSec, int toSec)
    {
        int lo = 0, hi = _events.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_events[mid].MatchClockSec < fromSec) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _events.Count && _events[i].MatchClockSec < toSec; i++) yield return _events[i];
    }

    /// <summary>Substitutions so far: the player going off is <see cref="MatchEvent.PlayerId"/>, the one coming on <see cref="MatchEvent.RecipientId"/>.</summary>
    public IReadOnlyList<MatchEvent> Substitutions => _substitutions;

    /// <summary>The team's players on the pitch at a match second: the starters, adjusted for substitutions made by then.</summary>
    public HashSet<string> OnPitch(string teamId, int atSec)
    {
        var on = Info.Team(teamId).Players.Where(p => p.Starter).Select(p => p.PlayerId).ToHashSet();
        foreach (MatchEvent sub in _substitutions.Where(s => s.TeamId == teamId && s.MatchClockSec <= atSec))
        {
            if (sub.PlayerId is not null) on.Remove(sub.PlayerId);
            if (sub.RecipientId is not null) on.Add(sub.RecipientId);
        }
        return on;
    }

    /// <summary>Numbers for one team over [fromSec, toSec).</summary>
    public TeamWindow Window(string teamId, int fromSec, int toSec) =>
        TeamWindow.Measure(this, teamId, fromSec, toSec, Settings.ChanceMaxSecondsAfterRecovery);

    /// <summary>
    /// The shot that followed a high recovery by the same team within <paramref name="maxSeconds"/>,
    /// with no change of possession to the opponent in between; null if there was none.
    /// </summary>
    public RecoveryShot? ShotAfterRecovery(MatchEvent recovery, int maxSeconds)
    {
        if (!_indexById.TryGetValue(recovery.EventId, out int start)) return null;
        var chain = new List<MatchEvent> { recovery };
        for (int i = start + 1; i < _events.Count; i++)
        {
            MatchEvent e = _events[i];
            if (e.MatchClockSec - recovery.MatchClockSec > maxSeconds) return null;
            if (e.Type == MatchEventType.PossessionChange && e.TeamId != recovery.TeamId) return null;
            if (e.TeamId == recovery.TeamId && e.Type is MatchEventType.Pass or MatchEventType.Shot) chain.Add(e);
            if (e.Type == MatchEventType.Shot && e.TeamId == recovery.TeamId) return new RecoveryShot(recovery, e, chain);
        }
        return null;
    }

    private void Track(MatchEvent e)
    {
        if (e.Type == MatchEventType.Goal) _goals[e.TeamId] = Goals(e.TeamId) + 1;
        if (e.Type == MatchEventType.Substitution) _substitutions.Add(e);

        if (e.PlayerId is null || e.Type == MatchEventType.Substitution) return;
        if (!_players.TryGetValue(e.PlayerId, out PlayerTally? tally))
        {
            tally = new PlayerTally(e.PlayerId, e.TeamId);
            _players[e.PlayerId] = tally;
        }
        tally.Add(e);
    }
}

/// <summary>Running per-player counts for this match.</summary>
public sealed class PlayerTally(string playerId, string teamId)
{
    public string PlayerId { get; } = playerId;
    public string TeamId { get; } = teamId;
    public int Passes { get; private set; }
    public int CompletedPasses { get; private set; }
    public int Shots { get; private set; }
    public int Goals { get; private set; }
    public int HighRecoveries { get; private set; }
    public double Xg { get; private set; }

    /// <summary>Total length of the player's passes, metres (see <see cref="BallMetrics"/>).</summary>
    public double PassMetres { get; private set; }
    /// <summary>Passes with a difficulty of <see cref="BallMetrics.HardPass"/> or more, and how many were completed.</summary>
    public int HardPasses { get; private set; }
    public int HardPassesCompleted { get; private set; }
    public double LongestCompletedPassMetres { get; private set; }
    public double FastestShotKmh { get; private set; }

    public double PassAccuracy => Passes == 0 ? 0 : (double)CompletedPasses / Passes;
    public double AveragePassMetres => Passes == 0 ? 0 : PassMetres / Passes;

    internal void Add(MatchEvent e)
    {
        switch (e.Type)
        {
            case MatchEventType.Pass:
                Passes++;
                bool completed = e.Outcome == EventOutcome.Success;
                if (completed) CompletedPasses++;
                if (BallMetrics.PassMetres(e) is { } metres)
                {
                    PassMetres += metres;
                    if (completed) LongestCompletedPassMetres = Math.Max(LongestCompletedPassMetres, metres);
                    if (BallMetrics.PassDifficulty(e) >= BallMetrics.HardPass)
                    {
                        HardPasses++;
                        if (completed) HardPassesCompleted++;
                    }
                }
                break;
            case MatchEventType.Shot:
                Shots++;
                Xg += e.Xg ?? 0;
                if (BallMetrics.ShotKmh(e) is { } kmh) FastestShotKmh = Math.Max(FastestShotKmh, kmh);
                break;
            case MatchEventType.Goal:
                Goals++;
                break;
        }
        if (Pitch.IsHighRecovery(e)) HighRecoveries++;
    }
}
