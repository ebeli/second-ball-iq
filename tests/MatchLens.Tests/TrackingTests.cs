using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Forge;
using Xunit;

namespace MatchLens.Tests;

public class TrackingTests
{
    [Fact]
    public void Tracking_is_deterministic()
    {
        var a = Matches.TrackingOf("demo-match");
        var b = new TrackingSynth(Matches.Play("demo-match").Recording);
        foreach (double t in new[] { 600.0, 2500.3, 4200.7 })
        {
            TrackingFrame fa = a.Frame(t), fb = b.Frame(t);
            Assert.Equal(fa.Ball, fb.Ball);
            Assert.Equal(fa.Players.Select(p => (p.PlayerId, p.X, p.Y)), fb.Players.Select(p => (p.PlayerId, p.X, p.Y)));
        }
    }

    [Fact]
    public void The_ball_and_the_players_are_where_the_events_say()
    {
        var played = Matches.Play("demo-match");
        var synth = Matches.TrackingOf("demo-match");
        int passes = 0, atStart = 0, received = 0, completed = 0;
        foreach (MatchEvent e in played.Recording.Events.Where(e => e.Type == MatchEventType.Pass && e.End is not null))
        {
            double t = synth.EventTimes[e.EventId];
            Assert.True(t >= e.MatchClockSec && t - e.MatchClockSec < 4, $"{e.EventId} happens {t - e.MatchClockSec:0.0} s after its second");
            var (sx, sy) = TrackingFrameOf.Metres(e.Start!.Value, e.TeamId, played.Recording.Match);
            var (bx, by, _) = synth.BallAt(t);
            passes++;
            if (Math.Abs(bx - sx) < 0.5 && Math.Abs(by - sy) < 0.5) atStart++;
            if (e.Outcome != EventOutcome.Success || e.RecipientId is null) continue;
            completed++;
            var (ex, ey) = TrackingFrameOf.Metres(e.End!.Value, e.TeamId, played.Recording.Match);
            double metres = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
            double kmh = e.BallSpeedKmh ?? Math.Min(95, 25 + 1.6 * metres);
            double arrival = t + metres / (kmh / 3.6);
            PlayerFix r = synth.Frame(arrival, observed: false).Players.First(p => p.PlayerId == e.RecipientId);
            if (Math.Sqrt((r.X - ex) * (r.X - ex) + (r.Y - ey) * (r.Y - ey)) < 1.3) received++;
        }
        Assert.Equal(passes, atStart);
        Assert.True(received >= 0.93 * completed, $"{received} of {completed} receivers at the ball");
    }

    [Fact]
    public void Running_is_physically_plausible_and_measured_within_a_few_percent()
    {
        var synth = Matches.TrackingOf("demo-match");
        var measured = new Kinematics();
        var truth = new Kinematics();
        foreach (TrackingFrame f in synth.Frames(synth.StartT, synth.EndT)) measured.Add(f);
        foreach (TrackingFrame f in synth.Frames(synth.StartT, synth.EndT, observed: false)) truth.Add(f);
        var info = Matches.Play("demo-match").Recording.Match;
        foreach (Kinematics.Runner r in measured.Runners)
        {
            Kinematics.Runner t = truth.Of(r.PlayerId)!;
            Assert.InRange(r.TopSpeedKmh, 0, 37.0);
            bool keeper = info.Player(r.PlayerId)!.Position == PlayerPosition.Goalkeeper;
            bool fullMatch = t.DistanceMetres > 6000 || keeper;
            if (fullMatch && !keeper) Assert.InRange(t.DistanceMetres, 7000, 11500);
            if (keeper) Assert.InRange(t.DistanceMetres, 2500, 6000);
            // Measurement noise matters more for players who run little (keepers, late substitutes).
            if (t.DistanceMetres > 1000)
                Assert.InRange(Math.Abs(r.DistanceMetres - t.DistanceMetres) / t.DistanceMetres, 0, fullMatch && !keeper ? 0.04 : 0.10);
        }
        Assert.Contains(measured.Runners, r => r.Sprints > 5);
    }

    [Fact]
    public void Auto_eventing_finds_the_events_from_positions_alone()
    {
        var played = Matches.Play("demo-match");
        var synth = Matches.TrackingOf("demo-match");
        IReadOnlyList<DetectedEvent> found = AutoEventer.Run(played.Recording.Match, synth.Frames(synth.StartT, synth.EndT));
        AutoEventReport r = AutoEventScore.Score(played.Recording.Events, synth.EventTimes, found);
        Assert.True(r.Passes.Recall >= 0.93 && r.Passes.Precision >= 0.95, $"passes {r.Passes.Recall:P1} / {r.Passes.Precision:P1}");
        Assert.True(r.Shots.Recall >= 0.75 && r.Shots.Precision >= 0.75, $"shots {r.Shots.Recall:P1} / {r.Shots.Precision:P1}");
        Assert.True(r.BallWins.Recall >= 0.9 && r.BallWins.Precision >= 0.75, $"ball wins {r.BallWins.Recall:P1} / {r.BallWins.Precision:P1}");
        Assert.True(r.PassOutcomeAgreement >= 0.97);
        Assert.True(r.PassSpeedErrorKmh <= 4 && r.ShotSpeedErrorKmh <= 4, $"speed errors {r.PassSpeedErrorKmh} / {r.ShotSpeedErrorKmh}");
        Assert.True(r.PassLengthErrorMetres <= 2.5, $"length error {r.PassLengthErrorMetres}");
        Assert.Equal(r.GoalsTrue, r.GoalsFound);
    }
}
