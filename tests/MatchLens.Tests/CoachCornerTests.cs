using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Analytics.Spatial;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Xunit;

namespace MatchLens.Tests;

public class CoachCornerTests
{
    private static Matches.Played Demo => Matches.Play("demo-match");

    /// <summary>Answers the plan question with fixed probabilities, the way Decision-1 would.</summary>
    private sealed class FixedArbiter(string pick, double p) : IDecisionClient
    {
        public string? LastState { get; private set; }
        public IReadOnlyDictionary<string, DecisionQuestion>? LastQuestions { get; private set; }

        public Task<DecisionResult> DecideAsync(string state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken = default)
        {
            LastState = state;
            LastQuestions = questions;
            var choice = (ChoiceQuestion)questions["plan"];
            var probs = choice.Options.Keys.ToDictionary(k => k, k => k == pick ? p : 1 - p);
            var answers = new Dictionary<string, DecisionAnswer> { ["plan"] = new ChoiceAnswer("plan", pick, probs, null) };
            return Task.FromResult(new DecisionResult(answers, null, TimeSpan.FromMilliseconds(5), "{}"));
        }
    }

    [Fact]
    public void The_menu_sets_reachable_targets_and_needs_tracking_for_territory()
    {
        LiveMatch live = Matches.Until(Demo.Recording, 30 * 60);
        string home = Demo.Recording.Match.Home.TeamId;
        var without = CoachPlans.Menu(live, home, 30 * 60, null);
        Assert.DoesNotContain(without, m => m.Kind == CoachPlanKind.TakeTheFinalThird);
        var with = CoachPlans.Menu(live, home, 30 * 60, 0.31);
        CoachPlanOption territory = Assert.Single(with, m => m.Kind == CoachPlanKind.TakeTheFinalThird);
        Assert.Equal(0.36, territory.Target, 2);
        Assert.All(with, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Criterion));
            Assert.False(string.IsNullOrWhiteSpace(m.CriterionFi));
            Assert.Contains(">=", m.Rule.Replace("<=", ">="));
        });
        Assert.Equal(with.Count, with.Select(m => m.Kind).Distinct().Count());
    }

    [Fact]
    public void The_two_plain_rule_coaches_always_propose_different_plans()
    {
        LiveMatch live = Matches.Until(Demo.Recording, 40 * 60);
        string home = Demo.Recording.Match.Home.TeamId;
        foreach (double? control in new double?[] { null, 0.3 })
            foreach (bool ahead in new[] { true, false })
            {
                var menu = CoachPlans.Menu(live, home, 40 * 60, control);
                CoachProposal a = CoachPlans.Fallback(CoachPlans.Coaches[0], menu, ahead, null);
                CoachProposal b = CoachPlans.Fallback(CoachPlans.Coaches[1], menu, ahead, a.Plan);
                Assert.NotEqual(a.Plan, b.Plan);
                Assert.Contains(menu, m => m.Kind == a.Plan);
                Assert.Contains(menu, m => m.Kind == b.Plan);
                Assert.False(a.FromModel || b.FromModel);
            }
    }

    [Fact]
    public async Task Decision1_picks_between_the_two_plans_by_their_criteria()
    {
        LiveMatch live = Matches.Until(Demo.Recording, 25 * 60);
        MatchInfo info = Demo.Recording.Match;
        string home = info.Home.TeamId;
        var menu = CoachPlans.Menu(live, home, 25 * 60, null);
        CoachProposal a = CoachPlans.Fallback(CoachPlans.Coaches[0], menu, false, null);
        CoachProposal b = CoachPlans.Fallback(CoachPlans.Coaches[1], menu, false, a.Plan);
        string brief = CoachPlans.Brief(live, home, 25 * 60, null, null);
        var arbiter = new FixedArbiter("gameManager", 0.64);

        var (picked, probs, source) = await CoachPlans.ArbitrateAsync(arbiter, info.Home.Name, menu, [a, b], brief, CancellationToken.None);
        Assert.Equal("gameManager", picked);
        Assert.Equal("Decision-1", source);
        Assert.Equal(0.64, probs!["gameManager"], 2);
        Assert.Equal(0.36, probs["frontFoot"], 2);
        var question = Assert.IsType<ChoiceQuestion>(arbiter.LastQuestions!["plan"]);
        Assert.Equal(menu.First(m => m.Kind == a.Plan).Criterion, question.Options["frontFoot"]);
        Assert.StartsWith(brief, arbiter.LastState);

        var none = await CoachPlans.ArbitrateAsync(null, info.Home.Name, menu, [a, b], brief, CancellationToken.None);
        Assert.Null(none.Picked);
        Assert.Equal("none", none.Source);
    }

    [Fact]
    public void Both_plans_settle_as_tests_and_the_verdict_follows_them()
    {
        const int at = 20 * 60;
        MatchInfo info = Demo.Recording.Match;
        string home = info.Home.TeamId;
        LiveMatch live = Matches.Until(Demo.Recording, at);
        var menu = CoachPlans.Menu(live, home, at, null);
        var tests = new List<TacticalTest>();
        foreach (CoachPlanOption plan in menu)
            tests.Add(TacticalTests.ForCoachPlan("cc-1", plan.Kind.ToString(), "Coach", plan, home, at, 0.5));
        var full = new LiveMatch(info);
        foreach (MatchEvent e in Demo.Recording.Events)
        {
            full.Ingest(e);
            if (e.MatchClockSec >= at) tests = tests.Select(t => TacticalTests.Measure(t, full, full.ClockSec)).ToList();
        }
        tests = tests.Select(t => TacticalTests.Measure(t, full, full.ClockSec, fullTime: true)).ToList();
        Assert.All(tests, t =>
        {
            Assert.NotEqual(TestStatus.Open, t.Status);
            Assert.NotNull(t.Result);
            Assert.Equal(InsightKind.CoachPlan, t.Kind);
        });

        TacticalTest Make(string id, TestStatus s) => tests[0] with { TestId = id, Status = s };
        var pool = new Dictionary<string, TacticalTest>();
        CoachDecision D(TestStatus picked, TestStatus other)
        {
            pool["t-a"] = Make("t-a", picked);
            pool["t-b"] = Make("t-b", other);
            return new CoachDecision("cc", home, at, "", menu, [], "frontFoot", null, "Decision-1",
                new Dictionary<string, string> { ["frontFoot"] = "t-a", ["gameManager"] = "t-b" });
        }
        TacticalTest? Find(string id) => pool.GetValueOrDefault(id);
        Assert.Equal("right", CoachPlans.Judge(D(TestStatus.Met, TestStatus.NotMet), Find));
        Assert.Equal("wrong", CoachPlans.Judge(D(TestStatus.NotMet, TestStatus.Met), Find));
        Assert.Equal("level", CoachPlans.Judge(D(TestStatus.Met, TestStatus.Met), Find));
        Assert.Equal("level", CoachPlans.Judge(D(TestStatus.Met, TestStatus.Inconclusive), Find));
        Assert.Null(CoachPlans.Judge(D(TestStatus.Met, TestStatus.Open), Find));
    }

    [Fact]
    public void Pushing_a_team_up_gives_it_more_of_the_final_third_and_less_of_its_own()
    {
        var synth = Matches.TrackingOf("demo-match");
        MatchInfo info = Demo.Recording.Match;
        double t = synth.StartT + 15 * 60;
        TrackingFrame now = synth.Frame(t), before = synth.Frame(t - 0.2);
        foreach (string team in new[] { info.Home.TeamId, info.Away.TeamId })
        {
            WhatIfResult up = WhatIf.Run(now, before, info, team, WhatIfChange.PushUp, 10);
            Assert.True(up.FinalThirdAfter > up.FinalThirdBefore, $"{team}: {up.FinalThirdBefore} -> {up.FinalThirdAfter}");
            Assert.True(up.OwnThirdAfter < up.OwnThirdBefore, $"{team}: own {up.OwnThirdBefore} -> {up.OwnThirdAfter}");
            Assert.InRange(up.Moved.Count, 8, 10);
            Assert.Equal(up.HomeBefore.Length, up.HomeAfter.Length);

            WhatIfResult deep = WhatIf.Run(now, before, info, team, WhatIfChange.DropDeep, 10);
            Assert.True(deep.OwnThirdAfter > deep.OwnThirdBefore);
            WhatIfResult press = WhatIf.Run(now, before, info, team, WhatIfChange.PressTheBall, 6);
            Assert.InRange(press.Moved.Count, 0, 3);
        }
    }
}
