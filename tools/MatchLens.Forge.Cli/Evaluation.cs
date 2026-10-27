using System.Diagnostics;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Forge;

namespace MatchLens.Forge.Cli;

/// <summary>
/// Replays generated matches event by event through the analytics engine and compares what the detectors raise
/// with the scenario ground truth. Only expectations the ground truth verified are counted.
/// </summary>
public static class Evaluation
{
    /// <summary>A detection counts if it fires inside the expected window or up to this long after it.</summary>
    public const int LateToleranceSec = 180;

    private static readonly InsightKind[] PatternKinds =
        [InsightKind.PressingShift, InsightKind.PossessionIllusion, InsightKind.TacticalShift, InsightKind.PlayerRole];

    /// <summary>
    /// Whether a candidate is the detection of an expectation: same kind and team, inside the window (plus
    /// tolerance), and for shape kinds the same player and role, or the same kind of shift.
    /// </summary>
    public static bool Matches(Expectation x, Candidate c)
    {
        if (c.Kind != x.Kind || c.TeamId != x.TeamId) return false;
        if (c.DetectedAtSec < x.FromSec || c.DetectedAtSec > x.ToSec + LateToleranceSec) return false;
        string? tag(string key) => c.Tags?.GetValueOrDefault(key);
        return x.Kind switch
        {
            InsightKind.PlayerRole => c.PlayerId == x.PlayerId && x.Measured.TryGetValue("role", out double role) &&
                                      tag("role") == Camel(((PlayerRole)(int)role).ToString()),
            InsightKind.TacticalShift => tag("shift") == (x.Measured.ContainsKey("formationChange") ? "formation"
                                         : x.Measured.ContainsKey("droppedDeeper") ? "deeper" : "direct"),
            _ => true,
        };
    }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>The label a shape expectation is reported under: the kind, or the kind of shift.</summary>
    private static string Label(Expectation x) => x.Kind switch
    {
        InsightKind.TacticalShift => "Shift: " + (x.Measured.ContainsKey("formationChange") ? "formation"
                                     : x.Measured.ContainsKey("droppedDeeper") ? "deeper" : "direct"),
        InsightKind.PlayerRole => "Role: " + PlayerRoles.Label((PlayerRole)(int)x.Measured["role"]),
        _ => x.Kind.ToString(),
    };

    public sealed record MatchResult(
        string ScenarioId, int Seed, IReadOnlyList<Candidate> Candidates, IReadOnlyList<(Expectation Expectation, Candidate? Hit)> Expectations,
        IReadOnlyList<Candidate> FalseAlarms, IReadOnlyList<string> IntegrityErrors, IReadOnlyList<double> IngestMicros);

    public static MatchResult Run(Scenario scenario, int seed)
    {
        MatchRecording recording = MatchSimulator.Generate(scenario, seed);
        var match = new LiveMatch(recording.Match);
        var micros = new List<double>(recording.Events.Count);
        var sw = new Stopwatch();
        foreach (MatchEvent e in recording.Events)
        {
            sw.Restart();
            match.Ingest(e);
            micros.Add(sw.Elapsed.TotalMilliseconds * 1000);
        }

        var results = new List<(Expectation, Candidate?)>();
        var matched = new HashSet<string>();
        foreach (Expectation expectation in recording.GroundTruth.Expectations.Where(x => x.Verified))
        {
            Candidate? hit = match.Candidates.FirstOrDefault(c => Matches(expectation, c));
            if (hit is not null) matched.Add(hit.CandidateId);
            results.Add((expectation, hit));
        }

        // Pattern detections that match no expectation. Other kinds are facts about single events and are
        // checked for integrity instead.
        var falseAlarms = match.Candidates
            .Where(c => PatternKinds.Contains(c.Kind) && !matched.Contains(c.CandidateId) &&
                        !recording.GroundTruth.Expectations.Any(x => Matches(x, c) ||
                            // A correct role seen again after its spell is not a false alarm.
                            (c.Kind == InsightKind.PlayerRole && x.Kind == c.Kind && x.PlayerId == c.PlayerId &&
                             Matches(x with { ToSec = int.MaxValue / 2 }, c))))
            .ToList();

        return new MatchResult(scenario.Id, seed, match.Candidates, results, falseAlarms,
            CheckIntegrity(match, recording.Events), micros);
    }

    /// <summary>
    /// Independent checks on every candidate against the raw events: cited events exist and sit inside the
    /// candidate's window, recovery-to-shot chains really are unbroken, counts in the facts match a recount.
    /// </summary>
    private static List<string> CheckIntegrity(LiveMatch match, IReadOnlyList<MatchEvent> events)
    {
        var byId = events.ToDictionary(e => e.EventId);
        var errors = new List<string>();
        foreach (Candidate c in match.Candidates)
        {
            foreach (string id in c.EvidenceEventIds)
            {
                if (!byId.TryGetValue(id, out MatchEvent? e))
                    errors.Add($"{c.CandidateId} cites missing event {id}");
                else if (e.MatchClockSec < c.FromSec || e.MatchClockSec > c.ToSec)
                    errors.Add($"{c.CandidateId} cites {id} outside its window");
            }

            switch (c.Kind)
            {
                case InsightKind.PressingShift:
                {
                    int recount = events.Count(e => e.MatchClockSec >= c.FromSec && e.MatchClockSec < c.ToSec &&
                                                    e.TeamId == c.TeamId && Pitch.IsHighRecovery(e));
                    if (recount != (int)c.Facts["highRecoveries"])
                        errors.Add($"{c.CandidateId} says {c.Facts["highRecoveries"]} high recoveries, recount {recount}");
                    break;
                }
                case InsightKind.TurnoverToChance:
                {
                    List<MatchEvent> chain = c.EvidenceEventIds.Select(id => byId[id]).OrderBy(e => e.Seq).ToList();
                    MatchEvent first = chain[0];
                    MatchEvent? shot = chain.LastOrDefault(e => e.Type == MatchEventType.Shot);
                    bool broken = shot is null || events.Any(e => e.Seq > first.Seq && e.Seq < shot.Seq &&
                                                  e.Type == MatchEventType.PossessionChange && e.TeamId != c.TeamId);
                    bool goalOk = c.Facts["goal"] == 0 || chain.Any(e => e.Type == MatchEventType.Goal);
                    if (!Pitch.IsHighRecovery(first) || broken || !goalOk)
                        errors.Add($"{c.CandidateId} chain is not recovery -> shot without opponent possession");
                    if (shot is not null && match.Info.Player(shot.PlayerId)?.Position == PlayerPosition.Goalkeeper)
                        errors.Add($"{c.CandidateId} has a goalkeeper shooting in open play");
                    break;
                }
                case InsightKind.PlayerMilestone:
                {
                    MatchEvent pass = byId[c.EvidenceEventIds[0]];
                    int recount = events.Count(e => e.Seq <= pass.Seq && e.PlayerId == c.PlayerId &&
                                                    e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success);
                    if (recount != (int)c.Facts["completedPasses"])
                        errors.Add($"{c.CandidateId} says {c.Facts["completedPasses"]} passes, recount {recount}");
                    break;
                }
            }
        }
        return errors;
    }

    public static int Report(int seeds, bool verbose)
    {
        Console.WriteLine($"Replaying every scenario on seeds 1..{seeds} through the analytics engine.\n");
        Console.WriteLine($"{"scenario",-20} {"expectation",-34} {"found",9} {"avg detect",11}");
        var all = new List<MatchResult>();
        foreach (Scenario scenario in Scenarios.All)
        {
            List<MatchResult> runs = Enumerable.Range(1, seeds).Select(seed => Run(scenario, seed)).ToList();
            all.AddRange(runs);
            foreach (IGrouping<string, (Expectation Expectation, Candidate? Hit)> group in runs.SelectMany(r => r.Expectations)
                         .GroupBy(x => Label(x.Expectation) + (x.Expectation.PlayerId is { } p ? $" ({p})" : "")))
            {
                var rows = group.ToList();
                var hits = rows.Where(x => x.Hit is not null).ToList();
                string when = hits.Count == 0 ? "-" : NarrativeText.Clock((int)hits.Average(x => x.Hit!.DetectedAtSec));
                Console.WriteLine($"{scenario.Id,-20} {group.Key,-34} {hits.Count,4}/{rows.Count,-4} {when,11}");
            }
        }

        Console.WriteLine($"\n{"scenario",-20} {"false alarms/match",19} {"candidates/match by kind"}");
        foreach (IGrouping<string, MatchResult> g in all.GroupBy(r => r.ScenarioId))
        {
            double fa = g.Average(r => r.FalseAlarms.Count);
            string byKind = string.Join(", ", Enum.GetValues<InsightKind>().Select(k =>
                $"{k} {g.Average(r => r.Candidates.Count(c => c.Kind == k)):0.0}"));
            string faByKind = string.Join(", ", PatternKinds.Select(k => $"{k} {g.Average(r => r.FalseAlarms.Count(c => c.Kind == k)):0.00}"));
            Console.WriteLine($"{g.Key,-20} {fa,19:0.00} {byKind}");
            Console.WriteLine($"{"",-20} {"",19} false alarms: {faByKind}");
        }

        var errors = all.SelectMany(r => r.IntegrityErrors.Select(e => $"{r.ScenarioId}-{r.Seed}: {e}")).ToList();
        int candidates = all.Sum(r => r.Candidates.Count);
        Console.WriteLine($"\nIntegrity: {candidates - errors.Count}/{candidates} candidates pass every check" +
                          (errors.Count == 0 ? "." : ":"));
        foreach (string e in errors.Take(10)) Console.WriteLine("  " + e);

        List<double> micros = all.SelectMany(r => r.IngestMicros).OrderBy(x => x).ToList();
        Console.WriteLine($"Engine time per event: p50 {Pct(micros, 0.5):0} us, p95 {Pct(micros, 0.95):0} us, max {micros[^1]:0} us " +
                          $"over {micros.Count} events.");

        if (verbose)
        {
            MatchResult demo = all.First(r => r.ScenarioId == "demo-match");
            Console.WriteLine($"\nCandidates in demo-match-{demo.Seed}:");
            foreach (Candidate c in demo.Candidates.Where(c => c.Kind != InsightKind.PlayerMilestone))
                Console.WriteLine($"  {NarrativeText.Clock(c.DetectedAtSec),6} {c.Kind,-19} {c.TeamId} {c.PlayerId} " +
                                  string.Join(", ", c.Facts.Select(kv => $"{kv.Key} {kv.Value:0.##}")) +
                                  (c.Tags is null ? "" : "  [" + string.Join(", ", c.Tags.Select(t => $"{t.Key}={t.Value}")) + "]"));
        }

        return errors.Count == 0 ? 0 : 1;
    }

    private static double Pct(List<double> sorted, double q) =>
        sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
}
