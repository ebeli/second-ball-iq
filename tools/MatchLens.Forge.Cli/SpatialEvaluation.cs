using System.Globalization;
using System.Text;
using MatchLens.Analytics;
using MatchLens.Analytics.Spatial;
using MatchLens.Contracts;
using MatchLens.Forge;

namespace MatchLens.Forge.Cli;

/// <summary>
/// `forge xt` fits the expected-threat grid and the pass-completion curve on simulated matches and writes them to
/// <c>XtGrid.cs</c>. `forge spatial` checks the spatial layer on other seeds (held out from the fit): pass difficulty
/// with and without the lane against how often passes are actually completed, the better-pass check, pitch control
/// against shots, and how often the two spatial detectors fire.
/// </summary>
internal static class SpatialEvaluation
{
    /// <summary>Seeds used for fitting start here, so `forge spatial --seeds N` (seeds 1..N) never sees them.</summary>
    private const int FitSeedBase = 100;

    public static int Fit(int seeds, int trackingSeeds, string outPath)
    {
        // Expected threat: events only, many matches.
        var matches = new List<IReadOnlyList<MatchEvent>>();
        foreach (Scenario sc in Scenarios.All)
            for (int s = 1; s <= seeds; s++)
                matches.Add(MatchSimulator.Generate(sc, FitSeedBase + s).Events);
        double[] grid = ExpectedThreat.Fit(matches);
        Console.WriteLine($"xT fitted on {matches.Count} matches ({matches.Sum(m => m.Count):N0} events).");

        // Completion: the lane difficulty of every pass at the moment it was played, against whether it arrived.
        var samples = new List<(double D, bool Ok)>();
        foreach (Scenario sc in Scenarios.All)
            for (int s = 1; s <= trackingSeeds; s++)
                foreach (var p in Passes(MatchSimulator.Generate(sc, FitSeedBase + s))) samples.Add((p.Lane, p.Ok));
        (double b0, double b1) = Logistic(samples);
        Console.WriteLine($"Completion curve fitted on {samples.Count:N0} passes: p = 1 / (1 + e^-({b0:0.000} {b1:+0.0000;-0.0000}·difficulty)).");

        var sb = new StringBuilder();
        sb.AppendLine("namespace MatchLens.Analytics.Spatial;");
        sb.AppendLine();
        sb.AppendLine("// Written by `forge xt`: the expected-threat grid and the pass-completion curve, fitted on simulated matches");
        sb.AppendLine($"// (xT: {matches.Count} matches, seeds {FitSeedBase + 1}–{FitSeedBase + seeds} of every scenario; completion: {samples.Count:N0} passes with tracking).");
        sb.AppendLine("// Do not edit by hand; run `forge xt` again.");
        sb.AppendLine("public static class XtGrid");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string FittedOn = \"{matches.Count} simulated matches\";");
        sb.AppendLine($"    public const double CompletionIntercept = {b0.ToString("0.00000", CultureInfo.InvariantCulture)};");
        sb.AppendLine($"    public const double CompletionSlope = {b1.ToString("0.000000", CultureInfo.InvariantCulture)};");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>{ExpectedThreat.Cols} × {ExpectedThreat.Rows}, row by row from y 0 (each row x 0 → 100), attacking frame.</summary>");
        sb.AppendLine("    public static readonly double[] Values =");
        sb.AppendLine("    [");
        for (int r = 0; r < ExpectedThreat.Rows; r++)
            sb.AppendLine("        " + string.Join(", ", Enumerable.Range(0, ExpectedThreat.Cols).Select(c => grid[r * ExpectedThreat.Cols + c].ToString("0.00000", CultureInfo.InvariantCulture))) + ",");
        sb.AppendLine("    ];");
        sb.AppendLine("}");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"Wrote {outPath}");
        PrintGrid(grid);
        return 0;
    }

    public static int Report(int seeds)
    {
        var all = new List<PassSample>();
        int matchesRun = 0, territory = 0, better = 0;
        var windows = new List<(double Control, int Shots, double Xg)>();
        foreach (Scenario sc in Scenarios.All)
        {
            for (int s = 1; s <= seeds; s++)
            {
                MatchRecording rec = MatchSimulator.Generate(sc, s);
                var synth = new TrackingSynth(rec);
                var live = new LiveMatch(rec.Match);
                var monitor = new SpatialMonitor(rec.Match);
                var events = rec.Events.ToList();
                var passes = events.Where(e => e.Type == MatchEventType.Pass && synth.EventTimes.ContainsKey(e.EventId))
                    .OrderBy(e => synth.EventTimes[e.EventId]).ToList();
                int ei = 0, pi = 0;
                foreach (TrackingFrame f in synth.Frames(synth.StartT, synth.EndT))
                {
                    while (ei < events.Count && events[ei].MatchClockSec <= f.T) live.Ingest(events[ei++]);
                    monitor.Push(f);
                    while (pi < passes.Count && synth.EventTimes[passes[pi].EventId] <= f.T)
                    {
                        MatchEvent e = passes[pi++];
                        if (monitor.OnPass(e, f) is { } c)
                            all.Add(new PassSample(BallMetrics.PassDifficulty(e) ?? 0, c.Chosen.Difficulty, c.Lane.Interceptable, e.Outcome == EventOutcome.Success, c.Better is not null, c.Gain));
                    }
                    foreach (Candidate cand in monitor.Detect(live, (int)f.T))
                    {
                        if (cand.Kind == InsightKind.Territory) territory++;
                        if (cand.Kind == InsightKind.BetterPass) better++;
                    }
                }
                while (ei < events.Count) live.Ingest(events[ei++]);
                // Pitch control against chances: every 5-minute window, each team's final-third control and its shots.
                foreach (TeamInfo team in new[] { rec.Match.Home, rec.Match.Away })
                    for (int from = 0; from + 300 <= 90 * 60; from += 300)
                        if (monitor.FinalThirdShare(team.TeamId, from, from + 300) is { } share)
                        {
                            TeamWindow w = live.Window(team.TeamId, from, from + 300);
                            windows.Add((share, w.Shots.Count, w.Xg));
                        }
                matchesRun++;
                Console.WriteLine($"{sc.Id,-20} seed {s,-3} passes {passes.Count,4}  better options {all.Skip(all.Count - passes.Count).Count(p => p.Better),3}");
            }
        }

        Console.WriteLine(new string('-', 100));
        Console.WriteLine($"{matchesRun} matches (seeds 1–{seeds}, not used for fitting), {all.Count:N0} passes judged at the moment they were played:");
        Console.WriteLine();
        Console.WriteLine("Pass difficulty against how often passes were actually completed:");
        Console.WriteLine($"  {"",-24}{"easy (<35)",-16}{"medium (35-59)",-16}{"hard (60+)",-16}AUC (failure)");
        Bands("event-only rating", all.Select(p => (p.EventDifficulty, p.Ok)).ToList());
        Bands("with the passing lane", all.Select(p => (p.LaneDifficulty, p.Ok)).ToList());
        Console.WriteLine();
        Console.WriteLine("Opponents able to cut the pass out (from tracking at the moment of the pass):");
        foreach (int k in new[] { 0, 1, 2 })
        {
            var g = all.Where(p => k < 2 ? p.Interceptable == k : p.Interceptable >= 2).ToList();
            if (g.Count > 0) Console.WriteLine($"  {(k < 2 ? k.ToString() : "2+"),-4} {g.Count,7:N0} passes, {g.Count(p => p.Ok) * 100.0 / g.Count,5:0.0}% completed");
        }
        Console.WriteLine();
        int flagged = all.Count(p => p.Better);
        Console.WriteLine($"Better pass available: {flagged:N0} of {all.Count:N0} passes ({flagged * 100.0 / Math.Max(1, all.Count):0.0}%), " +
                          $"average {all.Where(p => p.Better).DefaultIfEmpty().Average(p => p?.Gain ?? 0):0.000} xT more on those.");
        if (windows.Count > 4)
        {
            double rho = Spearman(windows.Select(w => w.Control).ToList(), windows.Select(w => (double)w.Shots).ToList());
            double rhoXg = Spearman(windows.Select(w => w.Control).ToList(), windows.Select(w => w.Xg).ToList());
            var top = windows.OrderByDescending(w => w.Control).Take(windows.Count / 4).ToList();
            var bottom = windows.OrderBy(w => w.Control).Take(windows.Count / 4).ToList();
            Console.WriteLine($"Pitch control vs chances, {windows.Count} team 5-minute windows: Spearman ρ(final-third control, shots) = {rho:0.00}, (… , xG) = {rhoXg:0.00};");
            Console.WriteLine($"  the quarter with the most final-third control averaged {top.Average(w => w.Shots):0.00} shots, the quarter with the least {bottom.Average(w => w.Shots):0.00}.");
            var sortedControl = windows.Select(w => w.Control).OrderBy(x => x).ToList();
            Console.WriteLine($"  final-third control per team and 5 minutes: median {sortedControl[sortedControl.Count / 2]:0.00}, 75th percentile {sortedControl[sortedControl.Count * 3 / 4]:0.00}, 90th {sortedControl[sortedControl.Count * 9 / 10]:0.00}.");
        }
        Console.WriteLine($"Spatial detectors: territory {territory} ({territory / (double)matchesRun:0.0} per match), better pass {better} ({better / (double)matchesRun:0.0} per match).");
        Console.WriteLine();
        Console.WriteLine("xT at a few spots (attacking frame): " + string.Join(", ", new (string, double, double)[]
            { ("own box", 8, 50), ("halfway", 50, 50), ("wide final third", 80, 10), ("edge of the box", 82, 50), ("penalty spot", 89.5, 50), ("six-yard box", 96, 50) }
            .Select(p => $"{p.Item1} {ExpectedThreat.At(p.Item2, p.Item3):0.000}")));
        return 0;
    }

    private sealed record PassSample(int EventDifficulty, int LaneDifficulty, int Interceptable, bool Ok, bool Better, double Gain);

    /// <summary>Every pass of a match with its lane difficulty at the moment it was played.</summary>
    private static IEnumerable<(int Lane, bool Ok)> Passes(MatchRecording rec)
    {
        var synth = new TrackingSynth(rec);
        foreach (MatchEvent e in rec.Events)
        {
            if (e.Type != MatchEventType.Pass || !synth.EventTimes.TryGetValue(e.EventId, out double t)) continue;
            if (PassLanes.Evaluate(e, synth.Frame(t), rec.Match) is { } c) yield return (c.Chosen.Difficulty, e.Outcome == EventOutcome.Success);
        }
    }

    private static void Bands(string label, List<(int D, bool Ok)> xs)
    {
        string Cell(Func<int, bool> pick)
        {
            var g = xs.Where(x => pick(x.D)).ToList();
            return g.Count == 0 ? "–" : $"{g.Count(x => x.Ok) * 100.0 / g.Count:0.0}% of {g.Count:N0}";
        }
        double auc = Auc(xs.Select(x => ((double)x.D, !x.Ok)).ToList());
        Console.WriteLine($"  {label,-24}{Cell(d => d < 35),-16}{Cell(d => d >= 35 && d < 60),-16}{Cell(d => d >= 60),-16}{auc:0.000}");
    }

    /// <summary>Area under the ROC curve: how often a failed pass has a higher difficulty than a completed one.</summary>
    private static double Auc(List<(double Score, bool Positive)> xs)
    {
        var sorted = xs.OrderBy(x => x.Score).ToList();
        double rankSum = 0;
        int i = 0, n = sorted.Count, pos = sorted.Count(x => x.Positive);
        while (i < n)
        {
            int j = i;
            while (j < n && sorted[j].Score == sorted[i].Score) j++;
            double rank = (i + j + 1) / 2.0;   // average rank of the ties (1-based)
            for (int k = i; k < j; k++) if (sorted[k].Positive) rankSum += rank;
            i = j;
        }
        int neg = n - pos;
        return pos == 0 || neg == 0 ? 0.5 : (rankSum - pos * (pos + 1) / 2.0) / (pos * (double)neg);
    }

    private static (double B0, double B1) Logistic(List<(double X, bool Y)> xs)
    {
        double b0 = 0, b1 = 0;
        for (int it = 0; it < 30; it++)
        {
            double g0 = 0, g1 = 0, h00 = 0, h01 = 0, h11 = 0;
            foreach ((double x, bool y) in xs)
            {
                double p = 1 / (1 + Math.Exp(-(b0 + b1 * x)));
                double w = p * (1 - p), r = (y ? 1 : 0) - p;
                g0 += r; g1 += r * x;
                h00 += w; h01 += w * x; h11 += w * x * x;
            }
            double det = h00 * h11 - h01 * h01;
            if (Math.Abs(det) < 1e-12) break;
            double d0 = (h11 * g0 - h01 * g1) / det, d1 = (h00 * g1 - h01 * g0) / det;
            b0 += d0; b1 += d1;
            if (Math.Abs(d0) + Math.Abs(d1) < 1e-9) break;
        }
        return (b0, b1);
    }

    private static double Spearman(List<double> a, List<double> b)
    {
        double[] ra = Ranks(a), rb = Ranks(b);
        double ma = ra.Average(), mb = rb.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < ra.Length; i++) { sab += (ra[i] - ma) * (rb[i] - mb); saa += (ra[i] - ma) * (ra[i] - ma); sbb += (rb[i] - mb) * (rb[i] - mb); }
        return sab / Math.Sqrt(saa * sbb);
    }

    private static double[] Ranks(List<double> v)
    {
        var idx = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToList();
        var r = new double[v.Count];
        int k = 0;
        while (k < idx.Count)
        {
            int j = k;
            while (j < idx.Count && v[idx[j]] == v[idx[k]]) j++;
            for (int m = k; m < j; m++) r[idx[m]] = (k + j - 1) / 2.0;
            k = j;
        }
        return r;
    }

    private static void PrintGrid(double[] g)
    {
        Console.WriteLine("xT grid (rows across the pitch, own goal on the left):");
        for (int r = 0; r < ExpectedThreat.Rows; r++)
            Console.WriteLine("  " + string.Join(" ", Enumerable.Range(0, ExpectedThreat.Cols).Select(c => (g[r * ExpectedThreat.Cols + c] * 1000).ToString("000"))));
    }
}
