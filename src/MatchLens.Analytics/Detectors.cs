using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>Finds one kind of pattern. Instances hold per-match state, so each match gets its own set.</summary>
public interface IDetector
{
    /// <summary>Called after every event is added.</summary>
    IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e);

    /// <summary>Called every <see cref="DetectorSettings.EvaluateEverySec"/> seconds of match time.</summary>
    IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec);
}

public static class Detectors
{
    public static IReadOnlyList<IDetector> CreateDefault() =>
    [
        new PressingShiftDetector(),
        new PossessionIllusionDetector(),
        new TurnoverToChanceDetector(),
        new GoalDetector(),
        new PlayerMilestoneDetector(),
        new TacticalShiftDetector(),
        new PlayerRoleDetector(),
    ];

    internal static Candidate Create(
        LiveMatch match, InsightKind kind, string teamId, string? playerId, int detectedAt, int fromSec, int toSec,
        IEnumerable<MatchEvent> evidence, Dictionary<string, double> facts, string evidenceText,
        IReadOnlyDictionary<string, string>? tags = null) =>
        new(CandidateId: "",
            kind, teamId, playerId, detectedAt, fromSec, toSec,
            evidence.Select(e => e.EventId).Distinct().ToList(),
            facts,
            match.Text.WindowSummary(Math.Max(0, detectedAt - match.Settings.PressWindowSec), detectedAt),
            evidenceText,
            tags);
}

/// <summary>Base for detectors that only run on ticks.</summary>
public abstract class WindowDetector : IDetector
{
    private readonly Dictionary<string, int> _lastFired = new(StringComparer.Ordinal);

    public IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e) => [];

    public abstract IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec);

    /// <summary>True (and records the time) if this team has not fired within the cooldown.</summary>
    protected bool TryFire(string teamId, int nowSec, int cooldownSec)
    {
        if (_lastFired.TryGetValue(teamId, out int last) && nowSec - last < cooldownSec) return false;
        _lastFired[teamId] = nowSec;
        return true;
    }
}

/// <summary>
/// A team has started winning the ball high much more often than just before, and it is already producing shots.
/// </summary>
public sealed class PressingShiftDetector : WindowDetector
{
    public override IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec)
    {
        DetectorSettings s = match.Settings;
        int w = s.PressWindowSec;
        if (nowSec < w) yield break;

        foreach (string teamId in new[] { match.Info.Home.TeamId, match.Info.Away.TeamId })
        {
            TeamWindow now = match.Window(teamId, nowSec - w, nowSec);
            TeamWindow before = match.Window(teamId, Math.Max(0, nowSec - 2 * w), nowSec - w);
            int n = now.HighRecoveries.Count, previous = before.HighRecoveries.Count;

            if (n < s.PressMinRecoveries || n - previous < s.PressMinRiseOverPrevious ||
                now.RecoveryShots.Count < s.PressMinShotsAfterRecovery) continue;
            if (!TryFire(teamId, nowSec, s.PressCooldownSec)) continue;

            TeamWindow opponent = match.Window(match.Info.OpponentOf(teamId), nowSec - w, nowSec);
            var facts = new Dictionary<string, double>
            {
                ["highRecoveries"] = n,
                ["highRecoveriesBefore"] = previous,
                ["shotsAfterRecovery"] = now.RecoveryShots.Count,
                ["xgAfterRecovery"] = Math.Round(now.RecoveryShots.Sum(r => r.Shot.Xg ?? 0), 2),
                ["windowMinutes"] = w / 60,
                ["opponentPossessionShare"] = Math.Round(opponent.PossessionShare, 3),
            };
            yield return Detectors.Create(match, InsightKind.PressingShift, teamId, null, nowSec, nowSec - w, nowSec,
                now.HighRecoveries.Concat(now.RecoveryShots.Select(r => r.Shot)), facts,
                match.Text.PressingEvidence(teamId, now, before, opponent));
        }
    }
}

/// <summary>A team has most of the ball but is creating little or nothing with it.</summary>
public sealed class PossessionIllusionDetector : WindowDetector
{
    public override IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec)
    {
        DetectorSettings s = match.Settings;
        int w = s.PossessionWindowSec;
        if (nowSec < w) yield break;

        foreach (string teamId in new[] { match.Info.Home.TeamId, match.Info.Away.TeamId })
        {
            TeamWindow now = match.Window(teamId, nowSec - w, nowSec);
            if (now.PossessionShare < s.PossessionMinShare || now.CompletedPasses < s.PossessionMinCompletedPasses ||
                now.Shots.Count > s.PossessionMaxShots || now.Xg > s.PossessionMaxXg) continue;
            if (!TryFire(teamId, nowSec, s.PossessionCooldownSec)) continue;

            TeamWindow opponent = match.Window(match.Info.OpponentOf(teamId), nowSec - w, nowSec);
            var facts = new Dictionary<string, double>
            {
                ["possessionShare"] = Math.Round(now.PossessionShare, 3),
                ["completedPasses"] = now.CompletedPasses,
                ["opponentCompletedPasses"] = opponent.CompletedPasses,
                ["finalThirdEntries"] = now.FinalThirdEntries,
                ["shots"] = now.Shots.Count,
                ["xg"] = Math.Round(now.Xg, 2),
                ["opponentShots"] = opponent.Shots.Count,
                ["windowMinutes"] = w / 60,
            };
            yield return Detectors.Create(match, InsightKind.PossessionIllusion, teamId, null, nowSec, nowSec - w, nowSec,
                now.FinalThirdEntryEvents.Concat(now.Shots), facts,
                match.Text.PossessionEvidence(teamId, now, opponent));
        }
    }
}

/// <summary>
/// A shot soon after winning the ball high, with no opponent possession in between. Fires per occurrence.
/// When the shot is a goal it fires on the goal event instead, so the score in its texts already includes it.
/// </summary>
public sealed class TurnoverToChanceDetector : IDetector
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private RecoveryShot? _awaitingGoal;

    public IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec) => [];

    public IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e)
    {
        if (e.Type == MatchEventType.Goal)
        {
            RecoveryShot? pending = _awaitingGoal;
            _awaitingGoal = null;
            if (pending is not null && pending.Shot.TeamId == e.TeamId)
                yield return Build(match, pending, e);
            yield break;
        }
        _awaitingGoal = null;
        if (e.Type != MatchEventType.Shot) yield break;

        RecoveryShot? chain = FindChain(match, e);
        if (chain is null || !_reported.Add(e.EventId)) yield break;

        if (e.ShotResult == ShotResult.Goal)
        {
            _awaitingGoal = chain;   // reported when the goal event arrives
            yield break;
        }
        if ((e.Xg ?? 0) < match.Settings.ChanceMinXg) yield break;
        yield return Build(match, chain, null);
    }

    /// <summary>The high-recovery chain that led to this shot, or null if it did not come from one.</summary>
    internal static RecoveryShot? FindChain(LiveMatch match, MatchEvent shot)
    {
        int maxSec = match.Settings.ChanceMaxSecondsAfterRecovery;
        for (int i = match.Events.Count - 1; i >= 0; i--)
        {
            MatchEvent r = match.Events[i];
            if (r.Seq >= shot.Seq) continue;
            if (shot.MatchClockSec - r.MatchClockSec > maxSec) return null;
            if (r.Type == MatchEventType.PossessionChange && r.TeamId != shot.TeamId) return null;
            if (r.TeamId != shot.TeamId || !Pitch.IsHighRecovery(r)) continue;
            RecoveryShot? chain = match.ShotAfterRecovery(r, maxSec);
            return chain is not null && chain.Shot.EventId == shot.EventId ? chain : null;
        }
        return null;
    }

    private static Candidate Build(LiveMatch match, RecoveryShot chain, MatchEvent? goal)
    {
        MatchEvent shot = chain.Shot;
        var facts = new Dictionary<string, double>
        {
            ["secondsToShot"] = chain.Seconds,
            ["passesBeforeShot"] = chain.Chain.Count(x => x.Type == MatchEventType.Pass),
            ["xg"] = shot.Xg ?? 0,
            ["goal"] = goal is null ? 0 : 1,
        };
        IEnumerable<MatchEvent> evidence = goal is null ? chain.Chain : chain.Chain.Append(goal);
        int at = goal?.MatchClockSec ?? shot.MatchClockSec;
        return Detectors.Create(match, InsightKind.TurnoverToChance, shot.TeamId, shot.PlayerId, at,
            chain.Recovery.MatchClockSec, at, evidence, facts, match.Text.ChanceEvidence(chain));
    }
}

/// <summary>
/// Every goal gets airtime. Goals that came straight from a high recovery are reported as turnover to chance; this
/// reports all the others, with the move behind them: how long the scoring team had the ball and how many passes it
/// took. Fires on the goal event, so the score in its texts already includes it.
/// </summary>
public sealed class GoalDetector : IDetector
{
    /// <summary>A move longer than this is reported as starting this long before the shot.</summary>
    public const int MaxMoveSec = 120;

    public IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec) => [];

    public IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e)
    {
        if (e.Type != MatchEventType.Goal) yield break;

        // The shot that scored: the scoring team's last shot just before the goal event.
        int shotIndex = -1;
        for (int i = match.Events.Count - 2; i >= 0 && e.MatchClockSec - match.Events[i].MatchClockSec <= 30; i--)
        {
            if (match.Events[i].Type == MatchEventType.Shot && match.Events[i].TeamId == e.TeamId)
            {
                shotIndex = i;
                break;
            }
        }
        if (shotIndex < 0) yield break;
        MatchEvent shot = match.Events[shotIndex];
        if (TurnoverToChanceDetector.FindChain(match, shot) is not null) yield break;   // reported as turnover to chance

        // The move: back from the shot to where the scoring team won the ball (or the kick-off).
        int startSec = shot.MatchClockSec;
        var passes = new List<MatchEvent>();
        for (int j = shotIndex - 1; j >= 0; j--)
        {
            MatchEvent m = match.Events[j];
            if (shot.MatchClockSec - m.MatchClockSec > MaxMoveSec) break;
            if (m.Type is MatchEventType.PeriodStart or MatchEventType.KickOff or MatchEventType.Goal) break;
            if (m.TeamId != e.TeamId)
            {
                if (m.Type is MatchEventType.Pass or MatchEventType.Shot or MatchEventType.PossessionChange) break;
                continue;   // an opponent's pressure or foul does not end the move
            }
            startSec = m.MatchClockSec;
            if (m.Type == MatchEventType.PossessionChange) break;
            if (m.Type == MatchEventType.Pass && m.Outcome == EventOutcome.Success) passes.Insert(0, m);
        }

        MatchEvent? assist = passes.LastOrDefault(p => p.RecipientId == shot.PlayerId && p.PlayerId != shot.PlayerId);
        var facts = new Dictionary<string, double>
        {
            ["passesInMove"] = passes.Count,
            ["moveSeconds"] = shot.MatchClockSec - startSec,
            ["xg"] = shot.Xg ?? 0,
            ["goal"] = 1,
        };
        if (shot.SetPiece == SetPiece.Penalty) facts["penalty"] = 1;
        if (shot.SetPiece == SetPiece.FreeKick) facts["directFreeKick"] = 1;
        IEnumerable<MatchEvent> evidence = passes.TakeLast(4).Append(shot).Append(e);
        yield return Detectors.Create(match, InsightKind.Goal, e.TeamId, shot.PlayerId, e.MatchClockSec,
            startSec, e.MatchClockSec, evidence, facts,
            match.Text.GoalEvidence(shot, e, startSec, passes.Count, assist));
    }
}

/// <summary>A player reaches a completed-pass milestone in this match.</summary>
public sealed class PlayerMilestoneDetector : IDetector
{
    public IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec) => [];

    public IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e)
    {
        if (!TeamWindow.IsCompletedPass(e) || e.PlayerId is null) yield break;
        PlayerTally? tally = match.Player(e.PlayerId);
        if (tally is null || !match.Settings.PassMilestones.Contains(tally.CompletedPasses)) yield break;

        // Only the match's leading passer: everyone in midfield reaches 50 eventually, which is not a story.
        PlayerTally? nextBest = match.Players
            .Where(p => p.PlayerId != tally.PlayerId)
            .OrderByDescending(p => p.CompletedPasses)
            .FirstOrDefault();
        if (nextBest is not null && nextBest.CompletedPasses >= tally.CompletedPasses) yield break;
        var facts = new Dictionary<string, double>
        {
            ["completedPasses"] = tally.CompletedPasses,
            ["passes"] = tally.Passes,
            ["passAccuracy"] = Math.Round(tally.PassAccuracy, 3),
            ["nextBestCompletedPasses"] = nextBest?.CompletedPasses ?? 0,
        };
        yield return Detectors.Create(match, InsightKind.PlayerMilestone, e.TeamId, e.PlayerId, e.MatchClockSec,
            0, e.MatchClockSec, [e], facts, match.Text.MilestoneEvidence(tally, e, nextBest));
    }
}
