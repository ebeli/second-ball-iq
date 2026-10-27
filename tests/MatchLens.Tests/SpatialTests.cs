using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Analytics.Spatial;
using MatchLens.Contracts;
using Xunit;

namespace MatchLens.Tests;

public class SpatialTests
{
    [Fact]
    public void Expected_threat_grows_towards_goal_and_is_the_same_on_both_flanks()
    {
        double own = ExpectedThreat.At(10, 50), half = ExpectedThreat.At(50, 50), edge = ExpectedThreat.At(82, 50), spot = ExpectedThreat.At(90, 50);
        Assert.True(own < half && half < edge && edge < spot, $"{own:0.000} {half:0.000} {edge:0.000} {spot:0.000}");
        Assert.Equal(ExpectedThreat.At(80, 20), ExpectedThreat.At(80, 80), 6);
        Assert.True(ExpectedThreat.At(90, 50) > ExpectedThreat.At(90, 8), "central is more dangerous than the corner of the box");
    }

    [Fact]
    public void Pitch_control_goes_to_whoever_gets_there_first()
    {
        const string home = "h", away = "a";
        var players = new List<PitchControl.Mover>
        {
            new("h1", home, 30, 34, 0, 0),
            new("a1", away, 75, 34, 0, 0),
        };
        Assert.True(PitchControl.HomeControlAt(players, home, 31, 34) > 0.95);
        Assert.True(PitchControl.HomeControlAt(players, home, 74, 34) < 0.05);
        Assert.InRange(PitchControl.HomeControlAt(players, home, 52.5, 34), 0.45, 0.55);
        // Running towards a spot wins it: the same home player sprinting forward takes the middle.
        players[0] = new("h1", home, 30, 34, 7, 0);
        Assert.True(PitchControl.HomeControlAt(players, home, 52.5, 34) > 0.8);
    }

    [Fact]
    public void A_lane_counts_the_defenders_who_can_cut_the_pass_out()
    {
        var inLane = new[] { (35.0, 34.5) };
        var wide = new[] { (35.0, 46.0) };
        PassLane a = PassLanes.Measure(20, 34, 50, 34, inLane, 15, lofted: false);
        Assert.Equal(1, a.LaneDefenders);
        Assert.Equal(1, a.Interceptable);
        PassLane b = PassLanes.Measure(20, 34, 50, 34, wide, 15, lofted: false);
        Assert.Equal(0, b.LaneDefenders);
        Assert.Equal(0, b.Interceptable);
        // Over a lofted ball the defender in the middle of the path cannot reach it.
        PassLane c = PassLanes.Measure(20, 34, 50, 34, inLane, 15, lofted: true);
        Assert.Equal(1, c.LaneDefenders);
        Assert.Equal(0, c.Interceptable);
        Assert.True(PassLanes.Difficulty(new(20, 50), new(48, 50), false, false, a) > PassLanes.Difficulty(new(20, 50), new(48, 50), false, false, b));
    }

    [Fact]
    public void Lane_difficulty_tracks_how_often_passes_are_completed()
    {
        var played = Matches.Play("demo-match");
        var synth = Matches.TrackingOf("demo-match");
        var bands = new Dictionary<string, (int Ok, int All)> { ["easy"] = (0, 0), ["medium"] = (0, 0), ["hard"] = (0, 0) };
        int better = 0, judged = 0;
        foreach (MatchEvent e in played.Recording.Events.Where(e => e.Type == MatchEventType.Pass && e.End is not null))
        {
            PassChoice? c = PassLanes.Evaluate(e, synth.Frame(synth.EventTimes[e.EventId]), played.Recording.Match);
            Assert.NotNull(c);
            judged++;
            string band = BallMetrics.Band(c!.Chosen.Difficulty);
            var (ok, all) = bands[band];
            bands[band] = (ok + (e.Outcome == EventOutcome.Success ? 1 : 0), all + 1);
            if (c.Better is { } b)
            {
                better++;
                Assert.True(c.Gain >= PassLanes.BetterGain && b.Completion >= PassLanes.BetterMinCompletion);
                Assert.NotEqual(e.RecipientId, b.PlayerId);
            }
        }
        double Rate(string k) => bands[k].Ok / (double)Math.Max(1, bands[k].All);
        Assert.True(Rate("easy") > Rate("medium") && Rate("medium") > Rate("hard"),
            $"easy {Rate("easy"):P0}, medium {Rate("medium"):P0}, hard {Rate("hard"):P0}");
        Assert.InRange(better / (double)judged, 0.002, 0.10);
    }

    [Fact]
    public void The_spatial_detectors_raise_candidates_the_agents_and_tests_can_use()
    {
        var played = Matches.Play("demo-match");
        var synth = Matches.TrackingOf("demo-match");
        var info = played.Recording.Match;
        var live = new LiveMatch(info);
        var monitor = new SpatialMonitor(info);
        var events = played.Recording.Events.ToList();
        var passes = events.Where(e => e.Type == MatchEventType.Pass && synth.EventTimes.ContainsKey(e.EventId)).OrderBy(e => synth.EventTimes[e.EventId]).ToList();
        var raised = new List<Candidate>();
        int ei = 0, pi = 0;
        foreach (TrackingFrame f in synth.Frames(synth.StartT, synth.EndT))
        {
            while (ei < events.Count && events[ei].MatchClockSec <= f.T) live.Ingest(events[ei++]);
            monitor.Push(f);
            while (pi < passes.Count && synth.EventTimes[passes[pi].EventId] <= f.T) monitor.OnPass(passes[pi++], f);
            raised.AddRange(monitor.Detect(live, (int)f.T));
        }
        Assert.NotEmpty(raised);
        Assert.Equal(raised.Count, raised.Select(c => c.CandidateId).Distinct().Count());
        foreach (Candidate c in raised)
        {
            Assert.NotEmpty(c.Evidence);
            Assert.All(c.EvidenceEventIds, id => Assert.NotNull(live.Event(id)));
            if (c.Kind == InsightKind.Territory)
            {
                Assert.True(c.Facts["finalThirdControl"] >= SpatialMonitor.TerritoryMinShare);
                TacticalTest? t = TacticalTests.Define(c, info, c.DetectedAtSec, "They have taken over the final third.");
                if (t is null) continue;   // too late in the match for a test
                TacticalTest settled = TacticalTests.Measure(t, live, t.DeadlineSec, false, monitor.FinalThirdShare);
                Assert.NotEqual(TestStatus.Open, settled.Status);
            }
            else Assert.Equal(InsightKind.BetterPass, c.Kind);
        }
    }
}
