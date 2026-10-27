using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Xunit;

namespace MatchLens.Tests;

public class TacticalTestTests
{
    private static Matches.Played Demo => Matches.Play("demo-match");

    private static (Candidate Candidate, int AtSec) First(Func<Candidate, bool> match) => Demo.Candidates.First(x => match(x.Candidate));

    /// <summary>Opens a test the way the server does (at the second it airs) and plays the match through it.</summary>
    private static TacticalTest Run(Candidate c, int opensAtSec)
    {
        TacticalTest t = TacticalTests.Define(c, Demo.Recording.Match, opensAtSec, "read")!;
        var live = new LiveMatch(Demo.Recording.Match);
        foreach (MatchEvent e in Demo.Recording.Events)
        {
            live.Ingest(e);
            if (e.MatchClockSec >= opensAtSec) t = TacticalTests.Measure(t, live, live.ClockSec);
        }
        return TacticalTests.Measure(t, live, live.ClockSec, fullTime: true);
    }

    [Fact]
    public void Goals_and_player_roles_get_no_test()
    {
        Assert.False(TacticalTests.Supports(InsightKind.Goal));
        Assert.False(TacticalTests.Supports(InsightKind.PlayerRole));
        Assert.True(TacticalTests.Supports(InsightKind.PressingShift));
    }

    [Fact]
    public void No_test_opens_too_late_to_play_its_window()
    {
        (Candidate c, _) = First(c => c.Kind == InsightKind.PressingShift);
        Assert.Null(TacticalTests.Define(c, Demo.Recording.Match, TacticalTests.LastOpenSec + 1, "read"));
    }

    [Fact]
    public void The_definition_is_locked_with_its_deadline()
    {
        (Candidate press, int at) = First(c => c.Kind == InsightKind.PressingShift);
        TacticalTest t = TacticalTests.Define(press, Demo.Recording.Match, at, "Northvale's press is biting")!;
        Assert.Equal(at + TacticalTests.WindowSec, t.DeadlineSec);
        Assert.Equal("shotsAfterHighRegain>=2", t.Rule);
        Assert.Equal(TestStatus.Open, t.Status);
        Assert.Null(t.Estimate);

        (Candidate shape, int at2) = First(c => c.Tags?.GetValueOrDefault("shift") == "formation");
        Assert.Equal(at2 + TacticalTests.ShapeWindowSec, TacticalTests.Define(shape, Demo.Recording.Match, at2, "read")!.DeadlineSec);
    }

    [Fact]
    public void Demo_pressing_read_is_met_before_its_deadline_with_its_events()
    {
        (Candidate c, int at) = First(c => c.Kind == InsightKind.PressingShift);
        TacticalTest t = Run(c, at);
        Assert.Equal(TestStatus.Met, t.Status);
        Assert.True(t.SettledAtSec < t.DeadlineSec);
        Assert.Equal(4, t.EventIds.Count);   // two regains, two shots
        Assert.All(t.EventIds, id => Assert.NotNull(Demo.Live.Event(id)));
    }

    [Fact]
    public void Demo_possession_read_before_the_goal_is_broken_early()
    {
        (Candidate c, int at) = Demo.Candidates.Last(x => x.Candidate.Kind == InsightKind.PossessionIllusion && x.AtSec < 45 * 60);
        TacticalTest t = Run(c, at);
        Assert.Equal(TestStatus.NotMet, t.Status);
        Assert.True(t.SettledAtSec < t.DeadlineSec);
    }

    [Fact]
    public void Demo_change_of_shape_is_confirmed()
    {
        (Candidate c, int at) = First(c => c.Tags?.GetValueOrDefault("formationAfter") == "3-4-1-2");
        Assert.Equal(TestStatus.Met, Run(c, at).Status);
    }

    [Fact]
    public void A_settled_test_never_changes_again()
    {
        (Candidate c, int at) = First(c => c.Kind == InsightKind.PressingShift);
        TacticalTest settled = Run(c, at);
        Assert.Same(settled, TacticalTests.Measure(settled, Demo.Live, Demo.Live.ClockSec, fullTime: true));
    }

    [Fact]
    public void Full_time_after_too_short_a_window_is_inconclusive()
    {
        (Candidate c, _) = First(c => c.Kind == InsightKind.PressingShift);
        int opens = TacticalTests.LastOpenSec;
        TacticalTest t = TacticalTests.Define(c, Demo.Recording.Match, opens, "read")!;
        LiveMatch live = Matches.Until(Demo.Recording, opens + 3 * 60);
        TacticalTest end = TacticalTests.Measure(t, live, opens + 3 * 60, fullTime: true);
        Assert.True(end.Status is TestStatus.Inconclusive or TestStatus.Met);
        if (end.Status == TestStatus.Inconclusive) Assert.Contains("ended", end.Result);
    }

    [Fact]
    public void The_record_counts_results_and_the_estimate_side_of_50()
    {
        TacticalTest Make(TestStatus s, double? estimate) => new(
            "t", "c", InsightKind.PressingShift, "team", null, "read", "", "", "", 0, 600, DateTimeOffset.UtcNow, estimate, "x",
            2, 0, "", "", s, 600, "", "", "", "", []);
        var r = TacticalTests.Record([Make(TestStatus.Met, 0.7), Make(TestStatus.NotMet, 0.8), Make(TestStatus.Inconclusive, 0.4), Make(TestStatus.Open, null), Make(TestStatus.NotMet, null)]);
        Assert.Equal((1, 2, 1, 1), (r.Met, r.NotMet, r.Inconclusive, r.Open));
        Assert.Equal((1, 2), (r.EstimateRight, r.EstimateCounted));
    }

    [Fact]
    public async Task The_estimate_comes_from_Decision_1_and_is_labelled()
    {
        (Candidate c, int at) = First(c => c.Kind == InsightKind.PressingShift);
        TacticalTest t = TacticalTests.Define(c, Demo.Recording.Match, at, "read")!;
        TacticalTest e = await TacticalTests.EstimateAsync(t, Matches.Until(Demo.Recording, at), new FakeDecisions(0.73), CancellationToken.None);
        Assert.Equal(0.73, e.Estimate);
        Assert.Equal("Decision-1", e.EstimateSource);
    }

    [Fact]
    public async Task A_slow_or_failing_model_leaves_no_estimate()
    {
        (Candidate c, int at) = First(c => c.Kind == InsightKind.PressingShift);
        TacticalTest t = TacticalTests.Define(c, Demo.Recording.Match, at, "read")!;
        LiveMatch live = Matches.Until(Demo.Recording, at);
        Assert.Null((await TacticalTests.EstimateAsync(t, live, new FakeDecisions(0.5, delay: TimeSpan.FromSeconds(10)), CancellationToken.None)).Estimate);
        Assert.Null((await TacticalTests.EstimateAsync(t, live, new FakeDecisions(0.5, fail: true), CancellationToken.None)).Estimate);
        Assert.Null((await TacticalTests.EstimateAsync(t, live, null, CancellationToken.None)).Estimate);
    }

    private sealed class FakeDecisions(double p, TimeSpan? delay = null, bool fail = false) : IDecisionClient
    {
        public async Task<DecisionResult> DecideAsync(string state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken = default)
        {
            if (delay is { } d) await Task.Delay(d, cancellationToken);
            if (fail) throw new DecisionException("down");
            return new DecisionResult(questions.Keys.ToDictionary(k => k, k => (DecisionAnswer)new YesNoAnswer(k, p)), null, TimeSpan.Zero, "{}");
        }
    }
}
