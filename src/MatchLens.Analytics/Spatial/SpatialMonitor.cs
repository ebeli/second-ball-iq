using System.Globalization;
using MatchLens.Contracts;

namespace MatchLens.Analytics.Spatial;

/// <summary>
/// Spatial analytics on the tracking as it arrives, for one match: a pitch-control map every match second, every pass
/// judged at the moment it was played (its lane, difficulty and the better option, if there was one), and two
/// detectors built on them that raise candidates for the agents like the event detectors do:
/// <list type="bullet">
/// <item><b>Territory</b>: a team has taken over the final third: it controls at least 34% of it over five minutes (about the top sixth of five-minute spells),
/// 8 points up on the ten minutes before (pitch control, not possession).</item>
/// <item><b>Better pass</b>: a player keeps missing a better-placed teammate: at least three passes in ten minutes
/// where another option would have delivered clearly more expected threat, 0.06 xT or more in all.</item>
/// </list>
/// </summary>
public sealed class SpatialMonitor
{
    public const int TerritoryWindowSec = 5 * 60, TerritoryBeforeSec = 10 * 60, TerritoryCooldownSec = 12 * 60;
    public const double TerritoryMinShare = 0.34, TerritoryMinRise = 0.08;
    public const int BetterWindowSec = 10 * 60, BetterMinCount = 3, BetterCooldownSec = 15 * 60;
    public const double BetterMinXt = 0.06;
    public const int DetectEverySec = 30;

    /// <summary>The final third, in each team's own attacking direction (percent of the length).</summary>
    public const double FinalThirdFrom = 200.0 / 3;

    private readonly MatchInfo _info;
    private readonly Queue<TrackingFrame> _recent = new();
    private readonly List<(int Sec, double Home, double Away)> _samples = [];
    private readonly Dictionary<string, PassChoice> _byEvent = new(StringComparer.Ordinal);
    private readonly List<PassChoice> _choices = [];
    private readonly Dictionary<string, int> _fired = new(StringComparer.Ordinal);
    private int _nextSample = int.MinValue;
    private int _nextDetect;

    public SpatialMonitor(MatchInfo info) => _info = info;

    /// <summary>The latest pitch-control map (home team's control, <see cref="PitchControl"/> grid) and its match time.</summary>
    public float[]? Control { get; private set; }
    public double ControlT { get; private set; }

    public IReadOnlyList<PassChoice> Choices => _choices;
    public PassChoice? Choice(string eventId) => _byEvent.GetValueOrDefault(eventId);

    /// <summary>Adds a frame (in order). Once per match second it computes the pitch-control map.</summary>
    public void Push(TrackingFrame f)
    {
        _recent.Enqueue(f);
        while (_recent.Count > 1 && f.T - _recent.Peek().T > 0.6) _recent.Dequeue();
        int sec = (int)Math.Floor(f.T);
        if (sec < _nextSample) return;
        _nextSample = sec + 1;
        // Velocities over about half a second, so position noise does not read as running.
        TrackingFrame? before = _recent.Count > 1 && f.T - _recent.Peek().T > 0.3 ? _recent.Peek() : null;
        float[] grid = PitchControl.Compute(PitchControl.Movers(f, before), _info.Home.TeamId);
        Control = grid;
        ControlT = f.T;
        _samples.Add((sec, PitchControl.Share(grid, true, FinalThirdFrom, 100), PitchControl.Share(grid, false, FinalThirdFrom, 100)));
    }

    /// <summary>Judges a pass with the frame at the moment it was played.</summary>
    public PassChoice? OnPass(MatchEvent e, TrackingFrame atStart)
    {
        if (_byEvent.ContainsKey(e.EventId)) return _byEvent[e.EventId];
        PassChoice? c = PassLanes.Evaluate(e, atStart, _info);
        if (c is null) return null;
        _byEvent[e.EventId] = c;
        _choices.Add(c);
        return c;
    }

    /// <summary>A team's average control of the final third (0 to 1) between two match seconds; null with under a minute of data.</summary>
    public double? FinalThirdShare(string teamId, int fromSec, int toSec)
    {
        bool home = teamId == _info.Home.TeamId;
        double sum = 0;
        int n = 0;
        foreach ((int sec, double h, double a) in _samples)
        {
            if (sec < fromSec || sec >= toSec) continue;
            sum += home ? h : a;
            n++;
        }
        return n < 60 ? null : sum / n;
    }

    /// <summary>A team's average control of the whole pitch (0 to 1) is not kept; this is its final third over the match so far.</summary>
    public double? FinalThirdShare(string teamId) => FinalThirdShare(teamId, 0, int.MaxValue);

    /// <summary>
    /// Runs the detectors (every 30 match seconds) and returns their candidates, with ids from the match so they sit
    /// in one sequence with the event detectors' candidates.
    /// </summary>
    public IReadOnlyList<Candidate> Detect(LiveMatch match, int nowSec)
    {
        if (nowSec < _nextDetect) return [];
        _nextDetect = nowSec + DetectEverySec;
        var raised = new List<Candidate>();
        foreach (TeamInfo team in new[] { _info.Home, _info.Away })
            if (Territory(match, team, nowSec) is { } c) raised.Add(c);
        raised.AddRange(BetterPasses(match, nowSec));
        return raised.Count == 0 ? [] : match.Raise(raised);
    }

    private Candidate? Territory(LiveMatch match, TeamInfo team, int nowSec)
    {
        int from = nowSec - TerritoryWindowSec, beforeFrom = from - TerritoryBeforeSec;
        if (beforeFrom < 0) return null;
        if (FinalThirdShare(team.TeamId, from, nowSec) is not { } after || FinalThirdShare(team.TeamId, beforeFrom, from) is not { } before) return null;
        if (after < TerritoryMinShare || after - before < TerritoryMinRise) return null;
        if (!TryFire("territory:" + team.TeamId, nowSec, TerritoryCooldownSec)) return null;

        TeamWindow w = match.Window(team.TeamId, from, nowSec);
        var into = match.Between(from, nowSec)
            .Where(e => e.TeamId == team.TeamId && e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success && e.End is { } end && end.X >= FinalThirdFrom)
            .TakeLast(6).ToList();
        var facts = new Dictionary<string, double>
        {
            ["finalThirdControl"] = Math.Round(after, 3),
            ["finalThirdControlBefore"] = Math.Round(before, 3),
            ["windowMinutes"] = TerritoryWindowSec / 60,
            ["shots"] = w.Shots.Count,
            ["finalThirdPasses"] = into.Count,
        };
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{team.Name} controlled {after * 100:0}% of the final third over the last {TerritoryWindowSec / 60} minutes (pitch control from tracking: " +
            $"who would reach each spot first), up from {before * 100:0}% in the {TerritoryBeforeSec / 60} minutes before. " +
            $"{team.Name} had {w.Shots.Count} shots in that time and completed {into.Count} passes into the final third.");
        return Detectors.Create(match, InsightKind.Territory, team.TeamId, null, nowSec, from, nowSec, into, facts, text,
            new Dictionary<string, string> { ["source"] = "tracking" });
    }

    private IEnumerable<Candidate> BetterPasses(LiveMatch match, int nowSec)
    {
        int from = nowSec - BetterWindowSec;
        foreach (var g in _choices.Where(c => c.Sec >= from && c.Sec < nowSec && c.PlayerId is not null).GroupBy(c => c.PlayerId!))
        {
            var missed = g.Where(c => c.Better is not null).ToList();
            double left = missed.Sum(c => c.Gain);
            if (missed.Count < BetterMinCount || left < BetterMinXt) continue;
            if (!TryFire("better:" + g.Key, nowSec, BetterCooldownSec)) continue;
            string teamId = g.First().TeamId;
            string player = _info.Player(g.Key)?.Name ?? g.Key;
            string? receiver = missed.GroupBy(c => c.Better!.PlayerId).OrderByDescending(x => x.Count()).First().Key;
            string receiverName = _info.Player(receiver)?.Name ?? "a teammate";
            var events = missed.Select(c => match.Event(c.EventId)).Where(e => e is not null).Select(e => e!).ToList();
            var facts = new Dictionary<string, double>
            {
                ["missedOptions"] = missed.Count,
                ["passes"] = g.Count(),
                ["xtLeft"] = Math.Round(left, 2),
                ["bestGain"] = Math.Round(missed.Max(c => c.Gain), 2),
                ["windowMinutes"] = BetterWindowSec / 60,
            };
            string text = string.Create(CultureInfo.InvariantCulture,
                $"{player} had a better-placed teammate on {missed.Count} of {g.Count()} passes in the last {BetterWindowSec / 60} minutes " +
                $"(judged from tracking at the moment of each pass: the chance each pass would arrive, times the expected threat where it would arrive). " +
                $"Those options would have carried {left:0.00} more expected threat in all; the best was worth {missed.Max(c => c.Gain):0.00} more. " +
                $"The teammate most often left unfound was {receiverName}.");
            yield return Detectors.Create(match, InsightKind.BetterPass, teamId, g.Key, nowSec, from, nowSec, events, facts, text,
                new Dictionary<string, string> { ["receiver"] = receiver ?? "", ["receiverName"] = receiverName, ["source"] = "tracking" });
        }
    }

    private bool TryFire(string key, int nowSec, int cooldown)
    {
        if (_fired.TryGetValue(key, out int last) && nowSec - last < cooldown) return false;
        _fired[key] = nowSec;
        return true;
    }
}
