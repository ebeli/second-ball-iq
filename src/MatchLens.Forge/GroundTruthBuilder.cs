using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>
/// Measures each scenario expectation from the generated events. Deliberately independent of the analytics
/// engine, so the evaluation compares Second Ball IQ against a second, simpler implementation of the same rules.
/// </summary>
public static class GroundTruthBuilder
{
    public const string PossessionIllusionRule =
        "team has >= 65% of completed passes in the window, <= 1 shot and <= 0.15 xG";

    public const string PressingShiftRule =
        "team wins the ball in the opponent's defensive third >= 5 times in the window, and at least twice as often " +
        "as in the same-length window before it";

    public const string TurnoverToChanceRule =
        "a scripted high turnover by the team is followed by a shot within the window";

    public const string FormationRule =
        "the coach changes the team's formation (not just how deep it sits) and it is kept for at least 15 minutes";

    public const string DeeperRule = "the team stops pressing in the opponent's half (low block) for at least 12 minutes";

    public const string DirectRule = "the team plays at least 12 percentage points more long balls, for at least 12 minutes";

    public const string RoleRule = "the player plays a distinctive role for at least 25 minutes with at least 20 touches";

    /// <summary>Spells shorter than this cannot be measured reliably.</summary>
    public const int MinFormationSpellSec = 15 * 60, MinStyleSpellSec = 12 * 60, MinRoleSpellSec = 25 * 60, MinRoleTouches = 20;

    /// <summary>How long after a change the detectors get to notice it.</summary>
    public const int ShiftWindowSec = 22 * 60;

    public static GroundTruth Build(
        Scenario scenario, int seed, MatchInfo match, IReadOnlyList<MatchEvent> events,
        IReadOnlyList<ScriptedMomentRecord> moments, int homeGoals, int awayGoals, IReadOnlyList<ShapeSpell>? spells = null)
    {
        var expectations = new List<Expectation>();
        foreach (ExpectationSpec spec in scenario.Expectations)
        {
            string teamId = spec.Side == Side.Home ? match.Home.TeamId : match.Away.TeamId;
            expectations.Add(spec.Kind switch
            {
                InsightKind.PossessionIllusion => PossessionIllusion(spec, teamId, match, events),
                InsightKind.PressingShift => PressingShift(spec, teamId, events),
                InsightKind.TurnoverToChance => TurnoverToChance(spec, teamId, events, moments),
                _ => new Expectation(spec.Kind, teamId, spec.PlayerId, spec.FromSec, spec.ToSec,
                    "not measured yet", false, new Dictionary<string, double>(), []),
            });
        }

        if (spells is not null)
        {
            expectations.AddRange(TacticalShifts(match, spells));
            expectations.AddRange(Roles(match, events, spells));
        }

        return new GroundTruth
        {
            MatchId = match.MatchId,
            ScenarioId = scenario.Id,
            Seed = seed,
            HomeGoals = homeGoals,
            AwayGoals = awayGoals,
            Expectations = expectations,
            Moments = moments,
        };
    }

    private static Expectation PossessionIllusion(ExpectationSpec spec, string teamId, MatchInfo match, IReadOnlyList<MatchEvent> events)
    {
        WindowFacts facts = WindowFacts.Measure(events, teamId, match.OpponentOf(teamId), spec.FromSec, spec.ToSec);
        bool ok = facts.PossessionShare >= 0.65 && facts.Shots <= 1 && facts.Xg <= 0.15;
        var measured = new Dictionary<string, double>
        {
            ["possessionShare"] = Math.Round(facts.PossessionShare, 3),
            ["shots"] = facts.Shots,
            ["xg"] = Math.Round(facts.Xg, 3),
            ["finalThirdEntries"] = facts.FinalThirdEntries,
            ["completedPasses"] = facts.CompletedPasses,
        };
        // Evidence: the team's few entries into the final third and any shots, i.e. what the possession produced.
        IReadOnlyList<string> evidence = InWindow(events, spec)
            .Where(e => e.TeamId == teamId &&
                        (e.Type == MatchEventType.Shot || WindowFacts.IsFinalThirdEntry(e)))
            .Select(e => e.EventId)
            .ToList();
        return new Expectation(spec.Kind, teamId, spec.PlayerId, spec.FromSec, spec.ToSec, PossessionIllusionRule, ok, measured, evidence);
    }

    private static Expectation PressingShift(ExpectationSpec spec, string teamId, IReadOnlyList<MatchEvent> events)
    {
        int span = spec.ToSec - spec.FromSec;
        List<MatchEvent> recoveries = InWindow(events, spec).Where(e => e.TeamId == teamId && Pitch.IsHighRecovery(e)).ToList();
        int before = events.Count(e => e.MatchClockSec >= spec.FromSec - span && e.MatchClockSec < spec.FromSec &&
                                       e.TeamId == teamId && Pitch.IsHighRecovery(e));
        bool ok = recoveries.Count >= 5 && recoveries.Count >= 2 * Math.Max(1, before);
        var measured = new Dictionary<string, double>
        {
            ["highRecoveries"] = recoveries.Count,
            ["highRecoveriesBefore"] = before,
            ["shotsAfterRecovery"] = ShotsAfter(events, recoveries, withinSec: 15),
        };
        return new Expectation(spec.Kind, teamId, spec.PlayerId, spec.FromSec, spec.ToSec, PressingShiftRule, ok, measured,
            recoveries.Select(e => e.EventId).ToList());
    }

    private static Expectation TurnoverToChance(
        ExpectationSpec spec, string teamId, IReadOnlyList<MatchEvent> events, IReadOnlyList<ScriptedMomentRecord> moments)
    {
        ScriptedMomentRecord? chain = moments.FirstOrDefault(m =>
            m.Kind == "highTurnover" && m.TeamId == teamId && m.MatchClockSec >= spec.FromSec && m.MatchClockSec <= spec.ToSec + 30 &&
            m.EventIds.Any(id => events.First(e => e.EventId == id).Type == MatchEventType.Shot));
        var measured = new Dictionary<string, double>();
        if (chain is not null)
        {
            List<MatchEvent> chainEvents = chain.EventIds.Select(id => events.First(e => e.EventId == id)).ToList();
            MatchEvent recovery = chainEvents.First(e => e.Type == MatchEventType.Interception);
            MatchEvent shot = chainEvents.First(e => e.Type == MatchEventType.Shot);
            measured["recoveryToShotSec"] = shot.MatchClockSec - recovery.MatchClockSec;
            measured["goal"] = shot.ShotResult == ShotResult.Goal ? 1 : 0;
            measured["xg"] = shot.Xg ?? 0;
        }
        return new Expectation(spec.Kind, teamId, spec.PlayerId, spec.FromSec, spec.ToSec, TurnoverToChanceRule,
            chain is not null, measured, chain?.EventIds ?? Array.Empty<string>());
    }

    /// <summary>
    /// One expectation per change the scenario made that a viewer could see: a new structure (back line or front
    /// line), a low block, or a switch to long balls. Changes that only move players around inside the same
    /// structure (4-3-3 to 4-5-1) are not expected to be recognised as a new formation.
    /// </summary>
    private static IEnumerable<Expectation> TacticalShifts(MatchInfo match, IReadOnlyList<ShapeSpell> spells)
    {
        foreach (IGrouping<Side, ShapeSpell> side in spells.GroupBy(x => x.Side))
        {
            string teamId = side.Key == Side.Home ? match.Home.TeamId : match.Away.TeamId;
            List<ShapeSpell> list = side.OrderBy(x => x.FromSec).ToList();
            for (int i = 1; i < list.Count; i++)
            {
                ShapeSpell before = list[i - 1], after = list[i];
                int length = after.ToSec - after.FromSec;
                (int d0, int f0) = Lines(before.Formation.Name);
                (int d1, int f1) = Lines(after.Formation.Name);
                if (Family(before.Formation.Name) != Family(after.Formation.Name))
                    yield return Shift(teamId, after, $"{FormationRule}: {before.Formation.Name} to {after.Formation.Name} ({after.Reason})",
                        length >= MinFormationSpellSec, new()
                        {
                            ["formationChange"] = 1, ["defendersBefore"] = d0, ["defendersAfter"] = d1,
                            ["forwardsBefore"] = f0, ["forwardsAfter"] = f1,
                        });
                if (after.Style.BlockHeight < 0.5 * before.Style.BlockHeight)
                    yield return Shift(teamId, after, $"{DeeperRule} ({after.Reason})", length >= MinStyleSpellSec,
                        new() { ["droppedDeeper"] = 1, ["blockHeight"] = after.Style.BlockHeight });
                if (after.Style.Directness - before.Style.Directness >= 0.12)
                    yield return Shift(teamId, after, $"{DirectRule} ({after.Reason})", length >= MinStyleSpellSec,
                        new() { ["wentDirect"] = 1, ["directnessBefore"] = before.Style.Directness, ["directnessAfter"] = after.Style.Directness });
            }
        }
    }

    private static Expectation Shift(string teamId, ShapeSpell after, string rule, bool ok, Dictionary<string, double> measured) =>
        new(InsightKind.TacticalShift, teamId, null, after.FromSec, Math.Min(after.ToSec, after.FromSec + ShiftWindowSec), rule, ok, measured, []);

    /// <summary>
    /// One expectation per player and distinctive role, for the first spell long enough to recognise it. The role
    /// number is in <c>Measured["role"]</c>.
    /// </summary>
    private static IEnumerable<Expectation> Roles(MatchInfo match, IReadOnlyList<MatchEvent> events, IReadOnlyList<ShapeSpell> spells)
    {
        var seen = new HashSet<(string, PlayerRole)>();
        foreach (ShapeSpell spell in spells.Where(x => x.ToSec - x.FromSec >= MinRoleSpellSec).OrderBy(x => x.FromSec))
        {
            string teamId = spell.Side == Side.Home ? match.Home.TeamId : match.Away.TeamId;
            foreach (RoleSlot slot in spell.Formation.Slots.Where(x => PlayerRoles.IsNotable(x.Role)))
            {
                if (!seen.Add((slot.PlayerId, slot.Role))) continue;
                List<MatchEvent> touches = events.Where(e => e.PlayerId == slot.PlayerId && IsTouch(e) &&
                    e.MatchClockSec >= spell.FromSec && e.MatchClockSec < spell.ToSec).ToList();
                var measured = new Dictionary<string, double>
                {
                    ["role"] = (int)slot.Role,
                    ["touches"] = touches.Count,
                    ["averageX"] = touches.Count == 0 ? 0 : Math.Round(touches.Average(e => e.Start!.Value.X), 1),
                    ["averageY"] = touches.Count == 0 ? 0 : Math.Round(touches.Average(e => e.Start!.Value.Y), 1),
                };
                yield return new Expectation(InsightKind.PlayerRole, teamId, slot.PlayerId, spell.FromSec, spell.ToSec,
                    $"{RoleRule}: {PlayerRoles.Label(slot.Role)}", touches.Count >= MinRoleTouches, measured, []);
            }
        }
    }

    /// <summary>
    /// Shapes that differ only in how deep the wide players sit look the same from average positions; a 4-5-1 block
    /// is a 4-3-3 that has dropped off, which the low-block expectation covers instead.
    /// </summary>
    private static string Family(string formation) => formation == "4-5-1" ? "4-3-3" : formation;

    /// <summary>"4-2-3-1" has 4 defenders and 1 forward.</summary>
    private static (int Defenders, int Forwards) Lines(string formation)
    {
        string[] parts = formation.Split('-');
        return (int.Parse(parts[0]), int.Parse(parts[^1]));
    }

    /// <summary>An action on the ball by the player (not a restart, a substitution or the goal record).</summary>
    public static bool IsTouch(MatchEvent e) =>
        e.Start is not null && e.Type is MatchEventType.Pass or MatchEventType.Shot or MatchEventType.Pressure
            or MatchEventType.Tackle or MatchEventType.Interception or MatchEventType.Foul;

    private static IEnumerable<MatchEvent> InWindow(IReadOnlyList<MatchEvent> events, ExpectationSpec spec) =>
        events.Where(e => e.MatchClockSec >= spec.FromSec && e.MatchClockSec < spec.ToSec);

    private static int ShotsAfter(IReadOnlyList<MatchEvent> events, List<MatchEvent> recoveries, int withinSec) =>
        recoveries.Count(r => events.Any(e =>
            e.Type == MatchEventType.Shot && e.TeamId == r.TeamId && e.Seq > r.Seq &&
            e.MatchClockSec - r.MatchClockSec <= withinSec &&
            !events.Any(x => x.Seq > r.Seq && x.Seq < e.Seq && x.Type == MatchEventType.PossessionChange && x.TeamId != r.TeamId)));
}

/// <summary>Simple per-window counts for one team. Possession share = share of completed passes.</summary>
public sealed record WindowFacts(
    double PossessionShare, int CompletedPasses, int Passes, int FinalThirdEntries, int Shots, double Xg,
    int HighRecoveries, int OpponentHighRecoveries)
{
    public static WindowFacts Measure(IReadOnlyList<MatchEvent> events, string teamId, string opponentId, int fromSec, int toSec)
    {
        List<MatchEvent> w = events.Where(e => e.MatchClockSec >= fromSec && e.MatchClockSec < toSec).ToList();
        int completed = w.Count(e => e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success && e.TeamId == teamId);
        int opponentCompleted = w.Count(e => e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success && e.TeamId == opponentId);
        int total = completed + opponentCompleted;
        return new WindowFacts(
            PossessionShare: total == 0 ? 0.5 : (double)completed / total,
            CompletedPasses: completed,
            Passes: w.Count(e => e.Type == MatchEventType.Pass && e.TeamId == teamId),
            FinalThirdEntries: w.Count(e => e.TeamId == teamId && IsFinalThirdEntry(e)),
            Shots: w.Count(e => e.Type == MatchEventType.Shot && e.TeamId == teamId),
            Xg: w.Where(e => e.Type == MatchEventType.Shot && e.TeamId == teamId).Sum(e => e.Xg ?? 0),
            HighRecoveries: w.Count(e => e.TeamId == teamId && Pitch.IsHighRecovery(e)),
            OpponentHighRecoveries: w.Count(e => e.TeamId == opponentId && Pitch.IsHighRecovery(e)));
    }

    /// <summary>A completed pass from outside into the attacking third.</summary>
    public static bool IsFinalThirdEntry(MatchEvent e) =>
        e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success &&
        e.Start is { } s && e.End is { } t && s.X < Pitch.AttackingThirdStart && t.X >= Pitch.AttackingThirdStart;
}
