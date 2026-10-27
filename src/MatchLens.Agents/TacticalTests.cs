using System.Globalization;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;

namespace MatchLens.Agents;

public enum TestStatus { Open, Met, NotMet, Inconclusive }

/// <summary>
/// A tactical read put to the test. When a read goes to air, a measurable criterion over the next ten match minutes
/// is fixed together with Decision-1's estimate and the time it was locked; nothing about it changes afterwards. The
/// event stream then decides it in code: criterion met, not met, or inconclusive. A result says what the events show,
/// not that the read caused them.
/// </summary>
public sealed record TacticalTest(
    string TestId, string CandidateId, InsightKind Kind, string TeamId, string? PlayerId, string Read,
    string Criterion, string CriterionFi, string Rule, int OpensAtSec, int DeadlineSec, DateTimeOffset LockedAt,
    double? Estimate, string EstimateSource,
    double Target, double Progress, string ProgressText, string ProgressTextFi,
    TestStatus Status, int? SettledAtSec, string? Result, string? ResultFi,
    string Limits, string LimitsFi, IReadOnlyList<string> EventIds);

/// <summary>The test definitions, the estimate, and the code that measures and settles them.</summary>
public static class TacticalTests
{
    /// <summary>How long a test runs. Shapes are read from touches and need longer to settle than counts.</summary>
    public const int WindowSec = 10 * 60;
    public const int ShapeWindowSec = 15 * 60;
    /// <summary>A window cut short by full time below this is too short to decide.</summary>
    public const int MinWindowSec = 6 * 60;
    public const int LastOpenSec = 90 * 60 - MinWindowSec;
    public static TimeSpan EstimateTimeout { get; } = TimeSpan.FromSeconds(3);

    public static bool Supports(InsightKind kind) =>
        kind is InsightKind.PressingShift or InsightKind.PossessionIllusion or InsightKind.TacticalShift or InsightKind.Territory;
    // Player roles are not tested: ten or fifteen minutes rarely give a player the 12 touches a role read needs, so
    // nearly every role test would end inconclusive (50 of 54 in 24 simulated matches, Oct 21).

    /// <summary>The test for a read that just went to air, locked at <paramref name="nowSec"/>; null when there is none.</summary>
    public static TacticalTest? Define(Candidate c, MatchInfo info, int nowSec, string read)
    {
        // Too late to play enough of a window before full time: no test rather than one that can only be inconclusive.
        if (!Supports(c.Kind) || nowSec > LastOpenSec) return null;
        string team = info.Team(c.TeamId).Name;
        bool formation = c.Kind == InsightKind.TacticalShift && c.Tags?.GetValueOrDefault("shift") == "formation";
        int deadline = nowSec + (formation ? ShapeWindowSec : WindowSec);
        string by = NarrativeText.Clock(deadline);
        string? tag(string k) => c.Tags?.GetValueOrDefault(k);
        double fact(string k) => c.Facts.TryGetValue(k, out double v) ? v : 0;
        string pct(double share) => $"{Math.Round(share * 100):0}%";

        (string rule, double target, string en, string fi, string limits, string limitsFi)? spec = c.Kind switch
        {
            InsightKind.PressingShift => ("shotsAfterHighRegain>=2", 2d,
                $"{team} have at least 2 shots straight after winning the ball high by {by}.",
                $"{team} laukoo vähintään 2 kertaa heti korkean riiston jälkeen {by} mennessä.",
                "Shows whether the press kept producing chances, not that the press alone caused them.",
                "Kertoo, tuottiko prässi edelleen paikkoja, ei sitä, että paikat johtuivat vain prässistä."),
            InsightKind.PossessionIllusion => ("possession>=0.55&xg<=0.30", 0.30,
                $"{team} keep at least 55% of the ball but no more than 0.30 xG until {by}.",
                $"{team} pitää vähintään 55 % pallosta mutta enintään 0,30 xG:n {by} asti.",
                "Shows whether the sterile possession continued, not why.",
                "Kertoo, jatkuiko tehoton pallonhallinta, ei sitä, miksi."),
            InsightKind.TacticalShift when formation && tag("formationAfter") is { } after && tag("formationBefore") is { } before =>
                ($"fit:{after}<{before}", 1d,
                $"Over the fifteen minutes to {by}, {team}'s positions fit a {after} better than their old {before}.",
                $"{team}n sijainnit sopivat {by} asti paremmin muotoon {after} kuin vanhaan muotoon {before}.",
                "Positions come from where players touch the ball, not from tracking; the shape is a best fit.",
                "Sijainnit tulevat pallokosketuksista, eivät seurantadatasta; muoto on paras sovitus."),
            InsightKind.TacticalShift when tag("shift") == "deeper" =>
                ("highPressRate<=" + Mid(fact("highPressRateBefore"), fact("highPressRateAfter")).ToString("0.000", CultureInfo.InvariantCulture), Mid(fact("highPressRateBefore"), fact("highPressRateAfter")),
                $"{team} keep pressing high on no more than {pct(Mid(fact("highPressRateBefore"), fact("highPressRateAfter")))} of the opponent's passes from their own half until {by}.",
                $"{team} prässää korkealta enintään {pct(Mid(fact("highPressRateBefore"), fact("highPressRateAfter")))} vastustajan omalta puoliskolta lähtevistä syötöistä {by} asti.",
                "The line sits halfway between the old and the new level; it shows whether the team stayed deep, not the coach's reason.",
                "Raja on vanhan ja uuden tason puolivälissä; se kertoo, pysyikö joukkue syvällä, ei valmentajan syytä."),
            InsightKind.TacticalShift when tag("shift") == "direct" =>
                ("longBallShare>=" + Mid(fact("longBallShareBefore"), fact("longBallShareAfter")).ToString("0.000", CultureInfo.InvariantCulture), Mid(fact("longBallShareBefore"), fact("longBallShareAfter")),
                $"At least {pct(Mid(fact("longBallShareBefore"), fact("longBallShareAfter")))} of {team}'s passes stay long until {by}.",
                $"Vähintään {pct(Mid(fact("longBallShareBefore"), fact("longBallShareAfter")))} {team}n syötöistä on pitkiä {by} asti.",
                "The line sits halfway between the old and the new level; it shows whether the team kept going long, not whether it worked.",
                "Raja on vanhan ja uuden tason puolivälissä; se kertoo, jatkoiko joukkue pitkillä palloilla, ei sitä, toimiko se."),
            InsightKind.Territory =>
                ("finalThirdControl>=" + Mid(fact("finalThirdControlBefore"), fact("finalThirdControl")).ToString("0.000", CultureInfo.InvariantCulture), Mid(fact("finalThirdControlBefore"), fact("finalThirdControl")),
                $"{team} keep control of at least {pct(Mid(fact("finalThirdControlBefore"), fact("finalThirdControl")))} of the final third until {by}.",
                $"{team} hallitsee vähintään {pct(Mid(fact("finalThirdControlBefore"), fact("finalThirdControl")))} hyökkäyskolmanneksesta {by} asti.",
                "Pitch control from tracking: who would reach each spot first. It shows whether the team kept the territory, not whether it scored from it.",
                "Kenttähallinta seurantadatasta: kuka ehtisi kuhunkin kohtaan ensin. Kertoo, pysyikö alue joukkueen hallussa, ei sitä, syntyikö siitä maaleja."),
            _ => null,
        };
        if (spec is not { } s) return null;
        return new TacticalTest(
            $"test-{c.CandidateId}", c.CandidateId, c.Kind, c.TeamId, c.PlayerId, read,
            s.en, s.fi, s.rule, nowSec, deadline, DateTimeOffset.UtcNow, null, "none",
            s.target, 0, "", "", TestStatus.Open, null, null, null, s.limits, s.limitsFi, []);
    }

    /// <summary>
    /// A coach's plan from the coach's corner as a test: the plan's criterion and target, locked now, with Decision-1's
    /// probability for it (from its choice between the two plans) as the estimate.
    /// </summary>
    public static TacticalTest ForCoachPlan(string decisionId, string coach, string coachName, CoachPlanOption plan, string teamId, int nowSec, double? estimate) =>
        new($"test-{decisionId}-{coach}", decisionId, InsightKind.CoachPlan, teamId, null, $"{coachName}: {plan.Title}",
            plan.Criterion, plan.CriterionFi, plan.Rule, nowSec, nowSec + WindowSec, DateTimeOffset.UtcNow, estimate,
            estimate is null ? "none" : "Decision-1 choice", plan.Target, 0, "", "", TestStatus.Open, null, null, null,
            plan.Limits, plan.LimitsFi, []);

    /// <summary>
    /// Decision-1's estimate that the criterion will be met, from a plain description of the last ten minutes. It is a
    /// model estimate, not a calibrated probability; with no answer within three seconds there is no estimate.
    /// </summary>
    public static async Task<TacticalTest> EstimateAsync(TacticalTest test, LiveMatch match, IDecisionClient? decisions, CancellationToken ct)
    {
        if (decisions is null) return test;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EstimateTimeout);
            string state = DescribeState(test, match);
            var questions = new Dictionary<string, DecisionQuestion>
            {
                ["met"] = new YesNoQuestion($"Will this happen: {test.Criterion}"),
            };
            DecisionResult result = await decisions.DecideAsync(state, questions, timeout.Token);
            return test with { Estimate = Math.Round(result.Get<YesNoAnswer>("met").Probability, 2), EstimateSource = "Decision-1" };
        }
        catch (Exception ex) when (ex is DecisionException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return test;
        }
    }

    private static string DescribeState(TacticalTest test, LiveMatch match)
    {
        MatchInfo info = match.Info;
        string team = info.Team(test.TeamId).Name, opp = info.Team(info.OpponentOf(test.TeamId)).Name;
        int from = Math.Max(0, test.OpensAtSec - WindowSec);
        TeamWindow us = match.Window(test.TeamId, from, test.OpensAtSec), them = match.Window(info.OpponentOf(test.TeamId), from, test.OpensAtSec);
        int shotsAfter = us.RecoveryShots.Select(r => r.Shot.EventId).Distinct().Count();
        return string.Create(CultureInfo.InvariantCulture,
            $"Football match, {info.Home.Name} {match.Goals(info.Home.TeamId)}-{match.Goals(info.Away.TeamId)} {info.Away.Name}, minute {test.OpensAtSec / 60}. " +
            $"Over the last ten minutes {team} had {Math.Round(us.PossessionShare * 100):0}% of the completed passes, {us.Shots.Count} shots worth {us.Xg:0.00} xG, " +
            $"{us.HighRecoveries.Count} high recoveries and {shotsAfter} shots straight after one. {opp} had {them.Shots.Count} shots worth {them.Xg:0.00} xG. " +
            $"The analyst's read: {test.Read}");
    }

    /// <summary>
    /// Measures a test at <paramref name="nowSec"/> and settles it when its result is known: early when a count is
    /// reached (or can no longer stay within its limit), at the deadline otherwise, and at full time if the match ends
    /// first (inconclusive when less than six minutes of the window were played).
    /// </summary>
    /// <param name="finalThirdControl">For a territory test: a team's average final-third control between two match seconds (from tracking).</param>
    public static TacticalTest Measure(TacticalTest t, LiveMatch match, int nowSec, bool fullTime = false,
        Func<string, int, int, double?>? finalThirdControl = null)
    {
        if (t.Status != TestStatus.Open) return t;
        int to = Math.Min(nowSec, t.DeadlineSec);
        bool atDeadline = nowSec >= t.DeadlineSec;
        bool cutShort = fullTime && !atDeadline;
        string by = NarrativeText.Clock(t.DeadlineSec);
        TeamWindow w = match.Window(t.TeamId, t.OpensAtSec, Math.Max(t.OpensAtSec, to));
        TestStatus status = TestStatus.Open;
        string progress, progressFi;
        IReadOnlyList<string> ids = [];
        double value;

        switch (t.Kind)
        {
            case InsightKind.PressingShift:
            {
                var shots = w.RecoveryShots.GroupBy(r => r.Shot.EventId).Select(g => g.First()).ToList();
                value = shots.Count;
                ids = shots.SelectMany(r => new[] { r.Recovery.EventId, r.Shot.EventId }).Distinct().ToList();
                progress = $"{shots.Count} of 2 shots after high regains";
                progressFi = $"{shots.Count}/2 laukausta korkean riiston jälkeen";
                if (value >= 2) status = TestStatus.Met;
                else if (atDeadline || cutShort) status = TestStatus.NotMet;
                break;
            }
            case InsightKind.PossessionIllusion:
            {
                value = Math.Round(w.Xg, 2);
                ids = w.Shots.Select(e => e.EventId).ToList();
                progress = $"{Math.Round(w.PossessionShare * 100):0}% of the ball, {w.Xg:0.00} xG";
                progressFi = $"{Math.Round(w.PossessionShare * 100):0} % pallosta, xG {Fi(w.Xg)}";
                if (w.Xg > 0.30) status = TestStatus.NotMet;   // more than 0.30 xG can no longer be undone
                else if (atDeadline || cutShort) status = w.PossessionShare >= 0.55 ? TestStatus.Met : TestStatus.NotMet;
                break;
            }
            case InsightKind.CoachPlan:
            {
                (value, progress, progressFi, status, ids) = MeasurePlan(t, match, w, to, atDeadline || cutShort, finalThirdControl);
                break;
            }
            case InsightKind.Territory:
            {
                // Decided on the whole window at the deadline; until then the share so far is shown.
                double? share = finalThirdControl?.Invoke(t.TeamId, t.OpensAtSec, Math.Max(t.OpensAtSec, to));
                value = share is { } v ? Math.Round(v, 3) : 0;
                progress = share is { } p1 ? $"{Math.Round(p1 * 100):0}% of the final third so far" : "Measured from tracking";
                progressFi = share is { } p2 ? $"{Math.Round(p2 * 100):0} % hyökkäyskolmanneksesta tähän mennessä" : "Mitataan seurantadatasta";
                if (atDeadline || cutShort) status = share is null ? TestStatus.Inconclusive : share >= t.Target ? TestStatus.Met : TestStatus.NotMet;
                break;
            }
            default:
            {
                // Shapes need the whole window: decided at the deadline (or full time).
                ShapeReport? shape = atDeadline || cutShort ? ShapeAnalyzer.Measure(match, t.TeamId, t.OpensAtSec, to) : null;
                bool? met = null;
                value = 0;
                progress = "Measured at " + by;
                progressFi = "Mitataan " + by;
                if (shape is not null)
                {
                    if (t.Rule.StartsWith("fit:", StringComparison.Ordinal))
                    {
                        string[] pair = t.Rule["fit:".Length..].Split('<');
                        bool known = shape.FitCosts.TryGetValue(pair[0], out double newCost) & shape.FitCosts.TryGetValue(pair[1], out double oldCost);
                        progress = $"Best fit over the window: {shape.Formation ?? "unclear"}";
                        progressFi = $"Paras sovitus jaksolla: {shape.Formation ?? "epäselvä"}";
                        if (known && shape.Formation is not null) { met = newCost < oldCost; value = met.Value ? 1 : 0; }
                    }
                    else if (t.Rule.StartsWith("highPressRate<=", StringComparison.Ordinal))
                    {
                        double rate = shape.Style.HighPressRate;
                        value = Math.Round(rate, 3);
                        progress = $"Pressed high on {Math.Round(rate * 100):0}% of those passes";
                        progressFi = $"Prässäsi korkealta {Math.Round(rate * 100):0} %:ssa näistä syötöistä";
                        met = rate <= t.Target;
                    }
                    else if (t.Rule.StartsWith("longBallShare>=", StringComparison.Ordinal))
                    {
                        double share = shape.Style.LongBallShare;
                        value = Math.Round(share, 3);
                        progress = $"{Math.Round(share * 100):0}% of passes long";
                        progressFi = $"{Math.Round(share * 100):0} % syötöistä pitkiä";
                        met = share >= t.Target;
                    }
                }
                if (shape is not null) status = met is null ? TestStatus.Inconclusive : met.Value ? TestStatus.Met : TestStatus.NotMet;
                break;
            }
        }

        // A window cut short by full time decides nothing unless the count was already reached.
        if (cutShort && status != TestStatus.Met && to - t.OpensAtSec < MinWindowSec) status = TestStatus.Inconclusive;

        var measured = t with { Progress = value, ProgressText = progress, ProgressTextFi = progressFi, EventIds = ids };
        if (status == TestStatus.Open) return measured;
        int at = Math.Min(nowSec, t.DeadlineSec);
        (string en, string fi) = status switch
        {
            TestStatus.Met => ($"Criterion met at {NarrativeText.Clock(at)}: {progress}.", $"Ehto täyttyi {NarrativeText.Clock(at)}: {progressFi}."),
            TestStatus.NotMet => ($"Criterion not met {(at < t.DeadlineSec ? "at" : "by")} {NarrativeText.Clock(at)}: {progress}.", $"Ehto ei täyttynyt ({NarrativeText.Clock(at)}): {progressFi}."),
            _ => ($"Inconclusive: {(cutShort ? "the match ended before enough of the window was played" : "the data could not decide it")} ({progress}).",
                  $"Ratkaisematon: {(cutShort ? "ottelu päättyi ennen kuin jaksoa oli pelattu tarpeeksi" : "data ei riittänyt ratkaisuun")} ({progressFi})."),
        };
        return measured with { Status = status, SettledAtSec = at, Result = en, ResultFi = fi };
    }

    /// <summary>The public record: settled tests, and how often Decision-1's estimate was on the right side of 50%.</summary>
    public static (int Met, int NotMet, int Inconclusive, int Open, int EstimateRight, int EstimateCounted) Record(IEnumerable<TacticalTest> tests)
    {
        var list = tests.ToList();
        var decided = list.Where(t => t.Status is TestStatus.Met or TestStatus.NotMet && t.Estimate is not null).ToList();
        return (list.Count(t => t.Status == TestStatus.Met), list.Count(t => t.Status == TestStatus.NotMet),
            list.Count(t => t.Status == TestStatus.Inconclusive), list.Count(t => t.Status == TestStatus.Open),
            decided.Count(t => (t.Estimate >= 0.5) == (t.Status == TestStatus.Met)), decided.Count);
    }

    private static double Mid(double a, double b) => Math.Round((a + b) / 2, 3);

    /// <summary>A coach's plan, by its rule: settled early when it can no longer change, otherwise at the deadline.</summary>
    /// <summary>A decimal the Finnish way ("0,16"), whatever the server's culture.</summary>
    private static string Fi(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    private static (double Value, string Progress, string ProgressFi, TestStatus Status, IReadOnlyList<string> Ids) MeasurePlan(
        TacticalTest t, LiveMatch match, TeamWindow w, int to, bool end, Func<string, int, int, double?>? finalThirdControl)
    {
        string rule = t.Rule;
        if (rule.StartsWith("highRecoveries>=", StringComparison.Ordinal))
        {
            int n = w.HighRecoveries.Count;
            TestStatus s = n >= t.Target ? TestStatus.Met : end ? TestStatus.NotMet : TestStatus.Open;
            return (n, $"{n} of {t.Target:0} high regains", $"{n}/{t.Target:0} korkeaa riistoa", s, w.HighRecoveries.Select(e => e.EventId).ToList());
        }
        if (rule.StartsWith("opponentXg<=", StringComparison.Ordinal))
        {
            TeamWindow o = match.Window(match.Info.OpponentOf(t.TeamId), t.OpensAtSec, Math.Max(t.OpensAtSec, to));
            double xg = Math.Round(o.Xg, 2);
            TestStatus s = xg > t.Target ? TestStatus.NotMet : end ? TestStatus.Met : TestStatus.Open;
            return (xg, $"Opponent {xg:0.00} xG so far", $"Vastustaja {Fi(xg)} xG tähän mennessä", s, o.Shots.Select(e => e.EventId).ToList());
        }
        if (rule.StartsWith("possession>=", StringComparison.Ordinal))
        {
            double share = w.PossessionShare;
            TestStatus s = end ? (share >= t.Target ? TestStatus.Met : TestStatus.NotMet) : TestStatus.Open;
            return (Math.Round(share, 3), $"{Math.Round(share * 100):0}% of the ball so far", $"{Math.Round(share * 100):0} % pallosta tähän mennessä", s, []);
        }
        if (rule.StartsWith("longBallShare>=", StringComparison.Ordinal))
        {
            ShapeReport? shape = end ? ShapeAnalyzer.Measure(match, t.TeamId, t.OpensAtSec, to) : null;
            if (shape is null) return (0, "Measured at " + NarrativeText.Clock(t.DeadlineSec), "Mitataan " + NarrativeText.Clock(t.DeadlineSec), TestStatus.Open, []);
            double share = shape.Style.LongBallShare;
            return (Math.Round(share, 3), $"{Math.Round(share * 100):0}% of passes long", $"{Math.Round(share * 100):0} % syötöistä pitkiä",
                share >= t.Target ? TestStatus.Met : TestStatus.NotMet, []);
        }
        if (rule.StartsWith("finalThirdControl>=", StringComparison.Ordinal))
        {
            double? share = finalThirdControl?.Invoke(t.TeamId, t.OpensAtSec, Math.Max(t.OpensAtSec, to));
            TestStatus s = end ? (share is null ? TestStatus.Inconclusive : share >= t.Target ? TestStatus.Met : TestStatus.NotMet) : TestStatus.Open;
            return (share is { } v ? Math.Round(v, 3) : 0,
                share is { } p1 ? $"{Math.Round(p1 * 100):0}% of the final third so far" : "Measured from tracking",
                share is { } p2 ? $"{Math.Round(p2 * 100):0} % hyökkäyskolmanneksesta tähän mennessä" : "Mitataan seurantadatasta", s, []);
        }
        return (0, "Unknown rule", "Tuntematon sääntö", end ? TestStatus.Inconclusive : TestStatus.Open, []);
    }
}
