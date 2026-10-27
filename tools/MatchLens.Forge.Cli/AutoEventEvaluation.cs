using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Forge;

namespace MatchLens.Forge.Cli;

/// <summary>
/// `forge autoevent`: auto-eventing scored against the event feed over many matches. Each match's synthetic tracking
/// is generated, the auto-eventer reads only the positions, and its passes, shots and ball wins are compared with the
/// events the match really had; running distances measured from the noisy frames are compared with the noise-free truth.
/// </summary>
internal static class AutoEventEvaluation
{
    public static int Report(int seeds)
    {
        var rows = new List<(string Scenario, int Seed, AutoEventReport R, double DistanceError, double TopSpeed)>();
        foreach (Scenario scenario in Scenarios.All)
        {
            for (int seed = 1; seed <= seeds; seed++)
            {
                MatchRecording recording = MatchSimulator.Generate(scenario, seed);
                var synth = new TrackingSynth(recording);
                var eventer = new AutoEventer(recording.Match);
                var measured = new Kinematics();
                foreach (TrackingFrame f in synth.Frames(synth.StartT, synth.EndT))
                {
                    eventer.Push(f);
                    measured.Add(f);
                }
                var truth = new Kinematics();
                foreach (TrackingFrame f in synth.Frames(synth.StartT, synth.EndT, observed: false)) truth.Add(f);
                double error = measured.Runners
                    .Select(r => (r, t: truth.Of(r.PlayerId)))
                    .Where(x => x.t is { DistanceMetres: > 500 })
                    .Average(x => Math.Abs(x.r.DistanceMetres - x.t!.DistanceMetres) / x.t!.DistanceMetres);
                AutoEventReport report = AutoEventScore.Score(recording.Events, synth.EventTimes, eventer.Detected);
                rows.Add((scenario.Id, seed, report, error, measured.Runners.Max(r => r.TopSpeedKmh)));
                Console.WriteLine($"{scenario.Id,-20} seed {seed,-3} passes {Pct(report.Passes)}  shots {Pct(report.Shots)}  " +
                                  $"ball wins {Pct(report.BallWins)}  speed ±{report.PassSpeedErrorKmh:0.0}/{report.ShotSpeedErrorKmh:0.0} km/h  distance ±{error:P1}");
            }
        }

        KindScore Sum(Func<AutoEventReport, KindScore> pick) =>
            new(rows.Sum(r => pick(r.R).True), rows.Sum(r => pick(r.R).Detected), rows.Sum(r => pick(r.R).Matched));
        Console.WriteLine(new string('-', 100));
        Console.WriteLine($"{rows.Count} matches, from tracking positions alone (found = recall, real = precision):");
        Console.WriteLine($"  passes      {Pct(Sum(r => r.Passes))}   complete/failed right {rows.Average(r => r.R.PassOutcomeAgreement):P1}, receiver right {rows.Average(r => r.R.ReceiverAgreement):P1}");
        Console.WriteLine($"              length ±{rows.Average(r => r.R.PassLengthErrorMetres):0.00} m, ball speed ±{rows.Average(r => r.R.PassSpeedErrorKmh):0.00} km/h, under pressure right {rows.Average(r => r.R.PressureAgreement):P1}");
        Console.WriteLine($"  shots       {Pct(Sum(r => r.Shots))}   result right {rows.Average(r => r.R.ShotResultAgreement):P1}, speed ±{rows.Average(r => r.R.ShotSpeedErrorKmh):0.00} km/h, goals {rows.Sum(r => r.R.GoalsFound)}/{rows.Sum(r => r.R.GoalsTrue)}");
        Console.WriteLine($"  ball wins   {Pct(Sum(r => r.BallWins))}");
        Console.WriteLine($"  running     distance ±{rows.Average(r => r.DistanceError):P1} against the noise-free truth, top speed {rows.Max(r => r.TopSpeed):0.0} km/h");
        return 0;
    }

    private static string Pct(KindScore s) => $"{s.Matched}/{s.True} found ({s.Recall:P1}), {s.Precision:P1} real";
}
