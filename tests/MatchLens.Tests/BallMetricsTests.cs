using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Contracts;
using Xunit;

namespace MatchLens.Tests;

public class BallMetricsTests
{
    private static MatchEvent Pass(double x, double y, double toX, double toY, bool pressured = false, bool longBall = false,
        EventOutcome outcome = EventOutcome.Success) => new()
    {
        EventId = "evt-test", Seq = 1, MatchId = "m", Period = 1, MatchClockSec = 600, Type = MatchEventType.Pass,
        TeamId = "team-a", PlayerId = "a-08", Start = new PitchPoint(x, y), End = new PitchPoint(toX, toY),
        Outcome = outcome, UnderPressure = pressured, LongBall = longBall,
    };

    [Fact]
    public void Pass_length_is_measured_in_metres_on_a_105_by_68_pitch()
    {
        Assert.Equal(105.0, BallMetrics.PassMetres(Pass(0, 50, 100, 50))!.Value, 1);
        Assert.Equal(68.0, BallMetrics.PassMetres(Pass(50, 0, 50, 100))!.Value, 1);
        Assert.Null(BallMetrics.PassMetres(Pass(10, 10, 10, 10) with { Type = MatchEventType.Shot }));
    }

    [Fact]
    public void Difficulty_rises_with_length_pressure_and_the_target_and_stays_in_range()
    {
        int sideways = BallMetrics.PassDifficulty(Pass(40, 50, 44, 40))!.Value;
        int longer = BallMetrics.PassDifficulty(Pass(40, 50, 70, 40))!.Value;
        int pressured = BallMetrics.PassDifficulty(Pass(40, 50, 70, 40, pressured: true))!.Value;
        int intoTheBox = BallMetrics.PassDifficulty(Pass(60, 50, 90, 50, pressured: true))!.Value;
        int hardest = BallMetrics.PassDifficulty(Pass(5, 5, 95, 50, pressured: true, longBall: true))!.Value;

        Assert.True(sideways < BallMetrics.MediumPass, $"a short sideways pass should be easy, got {sideways}");
        Assert.True(longer > sideways);
        Assert.True(pressured > longer);
        Assert.True(intoTheBox >= BallMetrics.HardPass, $"a pressured pass into the box should be hard, got {intoTheBox}");
        Assert.Equal(100, hardest);
        Assert.Equal("easy", BallMetrics.Band(sideways));
        Assert.Equal("hard", BallMetrics.Band(intoTheBox));
    }

    [Fact]
    public void Harder_passes_are_completed_less_often_in_generated_matches()
    {
        // The engine's rating is not the generator's rule, so this checks that it measures something real.
        var passes = new[] { "demo-match", "balanced", "pressing-shift" }
            .SelectMany(s => Matches.Play(s).Recording.Events)
            .Where(e => BallMetrics.PassDifficulty(e) is not null).ToList();
        double Rate(Func<int, bool> band)
        {
            var group = passes.Where(e => band(BallMetrics.PassDifficulty(e)!.Value)).ToList();
            return (double)group.Count(e => e.Outcome == EventOutcome.Success) / group.Count;
        }
        double easy = Rate(d => d < BallMetrics.MediumPass), hard = Rate(d => d >= BallMetrics.HardPass);
        Assert.True(easy > hard + 0.10, $"easy {easy:0.00} vs hard {hard:0.00}");
        Assert.All(passes, e => Assert.InRange(BallMetrics.PassDifficulty(e)!.Value, 0, 100));
    }

    [Fact]
    public void Player_tallies_match_a_recount_of_the_events()
    {
        var played = Matches.Play("demo-match");
        foreach (PlayerTally t in played.Live.Players.Where(p => p.Passes > 0 || p.Shots > 0))
        {
            PassQuality q = BallMetrics.Summarize(played.Recording.Events.Where(e => e.PlayerId == t.PlayerId));
            Assert.Equal(q.HardPasses, t.HardPasses);
            Assert.Equal(q.HardPassesCompleted, t.HardPassesCompleted);
            Assert.Equal(q.LongestCompletedPassMetres, Math.Round(t.LongestCompletedPassMetres, 1), 1);
            Assert.Equal(q.FastestShotKmh, Math.Round(t.FastestShotKmh, 1), 1);
        }
    }

    [Fact]
    public void A_fast_shot_is_called_with_its_speed_and_a_long_hard_pass_with_its_length()
    {
        var played = Matches.Play("demo-match");
        var commentator = new Commentator(played.Recording.Match);
        var live = new LiveMatch(played.Recording.Match);
        var calls = new List<Call>();
        foreach (MatchEvent e in played.Recording.Events)
        {
            live.Ingest(e);
            calls.AddRange(commentator.OnEvent(e, live));
        }
        MatchEvent fast = played.Recording.Events.First(e => BallMetrics.IsFastShot(e));
        Assert.Contains(calls, c => c.CallId.EndsWith("-speed") && c.Text.Contains("km/h"));
        string speed = fast.BallSpeedKmh!.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(calls, c => c.Kind == CallKind.StatFlash && c.Text.Contains($"{speed} km/h"));

        var passCalls = calls.Where(c => c.CallId.EndsWith("-pass")).ToList();
        Assert.NotEmpty(passCalls);
        Assert.True(passCalls.Zip(passCalls.Skip(1)).All(p => p.Second.ClockSec - p.First.ClockSec >= Commentator.PassCallGapSec),
            "long-pass calls are spaced out");
        Assert.All(passCalls, c => Assert.Contains("metres", c.Text));
    }
}
