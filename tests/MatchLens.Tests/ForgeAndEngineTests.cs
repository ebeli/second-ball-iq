using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Forge;
using Xunit;

namespace MatchLens.Tests;

public class ForgeTests
{
    [Fact]
    public void Same_seed_generates_the_same_match()
    {
        Scenario s = Scenarios.Find("demo-match")!;
        MatchRecording a = MatchSimulator.Generate(s, 7), b = MatchSimulator.Generate(s, 7);
        Assert.Equal(a.Events.Count, b.Events.Count);
        Assert.Equal(a.Events.Select(e => (e.EventId, e.MatchClockSec, e.Type)), b.Events.Select(e => (e.EventId, e.MatchClockSec, e.Type)));
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("possession-illusion")]
    [InlineData("pressing-shift")]
    [InlineData("game-state")]
    [InlineData("demo-match")]
    public void Clock_never_runs_backwards_and_ids_are_unique(string scenarioId)
    {
        IReadOnlyList<MatchEvent> events = Matches.Play(scenarioId).Recording.Events;
        Assert.True(events.Zip(events.Skip(1)).All(p => p.Second.MatchClockSec >= p.First.MatchClockSec));
        Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public void Demo_match_has_the_scripted_penalty_and_it_is_saved()
    {
        IReadOnlyList<MatchEvent> events = Matches.Play("demo-match").Recording.Events;
        MatchEvent penalty = Assert.Single(events, e => e.SetPiece == SetPiece.Penalty && e.Type == MatchEventType.Shot);
        Assert.Equal(ShotResult.Saved, penalty.ShotResult);
        Assert.InRange(penalty.MatchClockSec, 78 * 60, 81 * 60);
    }
}

public class EngineTests
{
    [Fact]
    public void Every_candidate_cites_events_that_exist()
    {
        Matches.Played m = Matches.Play("demo-match");
        Assert.NotEmpty(m.Candidates);
        foreach ((Candidate c, _) in m.Candidates)
            Assert.All(c.EvidenceEventIds, id => Assert.NotNull(m.Live.Event(id)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Pressing_shift_scenario_raises_a_pressing_shift_in_the_second_half(int seed)
    {
        Matches.Played m = Matches.Play("pressing-shift", seed);
        Assert.Contains(m.Candidates, x => x.Candidate.Kind == InsightKind.PressingShift && x.Candidate.DetectedAtSec >= 45 * 60);
    }

    [Fact]
    public void Demo_match_sees_Northvales_change_of_shape()
    {
        Matches.Played m = Matches.Play("demo-match");
        Assert.Contains(m.Candidates, x => x.Candidate.Kind == InsightKind.TacticalShift
            && x.Candidate.Tags?.GetValueOrDefault("formationAfter") == "3-4-1-2");
    }

    [Fact]
    public void A_window_counts_only_its_own_team_and_time()
    {
        Matches.Played m = Matches.Play("demo-match");
        string home = m.Recording.Match.Home.TeamId;
        TeamWindow w = m.Live.Window(home, 10 * 60, 20 * 60);
        Assert.All(w.Shots, e => { Assert.Equal(home, e.TeamId); Assert.InRange(e.MatchClockSec, 10 * 60, 20 * 60 - 1); });
        Assert.InRange(w.PossessionShare, 0, 1);
        Assert.Equal(w.Shots.Sum(e => e.Xg ?? 0), w.Xg, 6);
    }
}
