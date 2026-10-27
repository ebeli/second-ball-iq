// Match Forge CLI: generates synthetic matches and checks them against their ground truth.
//
//   dotnet run --project tools/MatchLens.Forge.Cli -- list
//   dotnet run --project tools/MatchLens.Forge.Cli -- generate demo-match
//   dotnet run --project tools/MatchLens.Forge.Cli -- generate pressing-shift --seed 7 --out data
//   dotnet run --project tools/MatchLens.Forge.Cli -- generate demo-match --find-seed      first seed that verifies
//   dotnet run --project tools/MatchLens.Forge.Cli -- check --seeds 50                     every scenario on 50 seeds
//   dotnet run --project tools/MatchLens.Forge.Cli -- stats data/demo-match-1
//   dotnet run --project tools/MatchLens.Forge.Cli -- evaluate --seeds 30                  detectors vs ground truth
//   dotnet run --project tools/MatchLens.Forge.Cli -- replay demo-match [--seed N] [--all]  candidates with their texts
//   dotnet run --project tools/MatchLens.Forge.Cli -- autoevent --seeds 6                  auto-eventing from tracking vs the events
//   dotnet run --project tools/MatchLens.Forge.Cli -- xt                                   fit the xT grid and the pass-completion curve
//   dotnet run --project tools/MatchLens.Forge.Cli -- spatial --seeds 4                    pitch control, pass lanes, better passes, checked
//
// Exit code 0 = everything verified, 1 = an expectation failed, 2 = bad arguments.

using System.Globalization;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Forge;
using MatchLens.Forge.Cli;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0) return Usage();

switch (args[0])
{
    case "list":
        foreach (Scenario s in Scenarios.All)
        {
            Console.WriteLine($"{s.Id,-20} seed {s.DefaultSeed,-3} {s.Description}");
        }
        return 0;

    case "generate" when args.Length >= 2:
    {
        Scenario? scenario = Scenarios.Find(args[1]);
        if (scenario is null) return Error($"Unknown scenario '{args[1]}'. Run 'list'.");
        int seed = IntArg("--seed") ?? scenario.DefaultSeed;
        string outRoot = StringArg("--out") ?? "data";

        MatchRecording recording = MatchSimulator.Generate(scenario, seed);
        if (args.Contains("--find-seed"))
        {
            int tried = 1;
            while (!recording.GroundTruth.AllVerified && tried < 200)
            {
                seed++;
                tried++;
                recording = MatchSimulator.Generate(scenario, seed);
            }
            Console.WriteLine($"Tried {tried} seed(s).");
        }

        string folder = Path.Combine(outRoot, recording.Match.MatchId);
        await RecordingStore.WriteAsync(recording, folder);
        PrintSummary(recording);
        Console.WriteLine($"\nWrote {folder}{Path.DirectorySeparatorChar}{{{RecordingStore.MatchFile}, {RecordingStore.EventsFile}, {RecordingStore.GroundTruthFile}}}");
        return recording.GroundTruth.AllVerified ? 0 : 1;
    }

    case "stats" when args.Length >= 2:
    {
        MatchRecording recording = await RecordingStore.ReadAsync(args[1]);
        PrintSummary(recording);
        return recording.GroundTruth.AllVerified ? 0 : 1;
    }

    case "check":
    {
        int seeds = IntArg("--seeds") ?? 30;
        bool allDefaultsOk = true;
        Console.WriteLine($"{"scenario",-20} {"verified",9}  default seed  per expectation");
        foreach (Scenario scenario in Scenarios.All)
        {
            var perExpectation = new Dictionary<string, int>();
            int verified = 0;
            for (int seed = 1; seed <= seeds; seed++)
            {
                GroundTruth truth = MatchSimulator.Generate(scenario, seed).GroundTruth;
                if (truth.AllVerified) verified++;
                foreach (Expectation x in truth.Expectations)
                {
                    string key = x.Kind + (x.PlayerId is { } p ? $" {p}" : "");
                    perExpectation[key] = perExpectation.GetValueOrDefault(key) + (x.Verified ? 1 : 0);
                }
            }
            bool defaultOk = MatchSimulator.Generate(scenario, scenario.DefaultSeed).GroundTruth.AllVerified;
            allDefaultsOk &= defaultOk;
            string detail = string.Join(", ", perExpectation.Select(kv => $"{kv.Key} {kv.Value}/{seeds}"));
            Console.WriteLine($"{scenario.Id,-20} {verified,4}/{seeds,-4}  {(defaultOk ? "ok" : "FAILS"),-12}  {detail}");
        }

        // Realism: averages over the balanced scenario.
        var totals = Enumerable.Range(1, seeds)
            .SelectMany(seed => MatchStats.Totals(MatchSimulator.Generate(Scenarios.Find("balanced")!, seed)))
            .ToList();
        Console.WriteLine($"\nBalanced play, per team, {totals.Count} team-matches (rough typical range in brackets):");
        Row("passes", totals.Average(t => t.Passes), "0");
        Row("passCompletion", totals.Average(t => t.PassCompletion), "0.00");
        Row("shots", totals.Average(t => t.Shots), "0.0");
        Row("xg", totals.Average(t => t.Xg), "0.00");
        Row("possessions", totals.Average(t => t.Possessions), "0");
        Console.WriteLine($"  {"highRecoveries",-15} {totals.Average(t => t.HighRecoveries),8:0.0}");
        return allDefaultsOk ? 0 : 1;
    }

    case "evaluate":
        return Evaluation.Report(IntArg("--seeds") ?? 30, args.Contains("--verbose"));

    case "autoevent":
        return AutoEventEvaluation.Report(IntArg("--seeds") ?? 6);

    case "xt":
        return SpatialEvaluation.Fit(IntArg("--seeds") ?? 20, IntArg("--tracking-seeds") ?? 4,
            StringArg("--out") ?? Path.Combine("src", "MatchLens.Analytics", "Spatial", "XtGrid.cs"));

    case "spatial":
        return SpatialEvaluation.Report(IntArg("--seeds") ?? 4);

    case "replay" when args.Length >= 2:
    {
        Scenario? scenario = Scenarios.Find(args[1]);
        if (scenario is null) return Error($"Unknown scenario '{args[1]}'. Run 'list'.");
        int seed = IntArg("--seed") ?? scenario.DefaultSeed;
        bool includeMilestones = args.Contains("--all");
        MatchRecording recording = MatchSimulator.Generate(scenario, seed);
        var live = new LiveMatch(recording.Match);
        foreach (MatchEvent e in recording.Events)
        {
            foreach (Candidate c in live.Ingest(e))
            {
                if (c.Kind == InsightKind.PlayerMilestone && !includeMilestones) continue;
                Console.WriteLine(new string('-', 100));
                Console.WriteLine($"{c.CandidateId}  {NarrativeText.Clock(c.DetectedAtSec)}  {c.Kind}  {recording.Match.Team(c.TeamId).Name}" +
                                  (c.PlayerId is null ? "" : $"  {recording.Match.Player(c.PlayerId)?.Name}"));
                Console.WriteLine("facts: " + string.Join(", ", c.Facts.Select(kv => $"{kv.Key} {kv.Value:0.###}")));
                Console.WriteLine("evidence ids: " + string.Join(" ", c.EvidenceEventIds));
                Console.WriteLine("\n[triage input: window summary]\n" + c.WindowSummary);
                Console.WriteLine("\n[grounding input: evidence]\n" + c.Evidence + "\n");
            }
        }
        Console.WriteLine($"{live.Candidates.Count} candidates in {recording.Match.MatchId} " +
                          $"({live.Candidates.Count(c => c.Kind == InsightKind.PlayerMilestone)} player milestones" +
                          (includeMilestones ? ")." : ", hidden; add --all)."));
        return 0;
    }

    default:
        return Usage();
}

// ----------------------------------------------------------------------------------------------------

static void Row(string name, double value, string format)
{
    (double low, double high) = MatchStats.TypicalRanges[name];
    string flag = value < low || value > high ? "  OUTSIDE" : "";
    Console.WriteLine($"  {name,-15} {value.ToString(format),8}  [{low.ToString(format)}..{high.ToString(format)}]{flag}");
}

static void PrintSummary(MatchRecording recording)
{
    MatchInfo m = recording.Match;
    GroundTruth truth = recording.GroundTruth;
    Console.WriteLine($"{m.MatchId}: {m.Home.Name} {truth.HomeGoals} - {truth.AwayGoals} {m.Away.Name}, {recording.Events.Count} events");

    Console.WriteLine($"\n  {"team",-10} {"passes",6} {"compl",6} {"poss",5} {"shots",5} {"xg",5} {"highRec",7}");
    foreach (TeamTotals t in MatchStats.Totals(recording))
    {
        Console.WriteLine($"  {m.Team(t.TeamId).ShortName,-10} {t.Passes,6} {t.PassCompletion,6:0.00} {t.PossessionShare,5:0.00} " +
                          $"{t.Shots,5} {t.Xg,5:0.00} {t.HighRecoveries,7}");
    }

    if (truth.Moments.Count > 0)
    {
        Console.WriteLine("\n  Scripted moments:");
        foreach (ScriptedMomentRecord moment in truth.Moments)
        {
            Console.WriteLine($"  {Clock(moment.MatchClockSec),6}  {m.Team(moment.TeamId).ShortName} {moment.Kind,-13} {string.Join(" ", moment.EventIds)}");
        }
    }

    if (truth.Expectations.Count > 0)
    {
        Console.WriteLine("\n  Ground truth:");
        foreach (Expectation e in truth.Expectations)
        {
            string measured = string.Join(", ", e.Measured.Select(kv => $"{kv.Key} {kv.Value:0.###}"));
            Console.WriteLine($"  {(e.Verified ? "ok  " : "FAIL")} {e.Kind,-19} {m.Team(e.TeamId).ShortName} {Clock(e.FromSec)}-{Clock(e.ToSec)}  {measured}");
            Console.WriteLine($"       rule: {e.Rule}");
            Console.WriteLine($"       evidence: {e.EvidenceEventIds.Count} event(s){(e.EvidenceEventIds.Count > 0 ? ": " + string.Join(" ", e.EvidenceEventIds.Take(8)) + (e.EvidenceEventIds.Count > 8 ? " ..." : "") : "")}");
        }
    }
}

static string Clock(int sec) => $"{sec / 60}:{sec % 60:D2}";

int? IntArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : null;
}

string? StringArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Error(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static int Usage()
{
    Console.WriteLine("Usage: forge list | generate <scenario> [--seed N] [--out folder] [--find-seed] | check [--seeds N] | stats <folder>");
    Console.WriteLine("             evaluate [--seeds N] [--verbose] | replay <scenario> [--seed N] [--all] | autoevent [--seeds N]");
    Console.WriteLine("             xt [--seeds N] [--tracking-seeds N] [--out file] | spatial [--seeds N]");
    return 2;
}
