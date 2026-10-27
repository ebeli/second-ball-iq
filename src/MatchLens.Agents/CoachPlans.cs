using System.Globalization;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;

namespace MatchLens.Agents;

/// <summary>The plans a coach can propose. Each one comes with a criterion the next ten minutes can settle.</summary>
public enum CoachPlanKind { PressHigher, SitDeeper, GoDirect, KeepTheBall, TakeTheFinalThird }

/// <summary>A plan on the menu for one team at one moment: what it asks for and the line that decides it.</summary>
public sealed record CoachPlanOption(
    CoachPlanKind Kind, string Title, string TitleFi, string Rule, double Target,
    string Criterion, string CriterionFi, string Limits, string LimitsFi);

/// <summary>One coach's proposal: which plan, and why in one sentence.</summary>
public sealed record CoachProposal(string Coach, string CoachName, CoachPlanKind Plan, string Rationale, bool FromModel);

/// <summary>
/// The coach's corner for one team at one moment: two proposals, Decision-1's pick between them with probabilities,
/// and the tactical tests that settle both. <see cref="Verdict"/> says whether the pick was the plan that held.
/// </summary>
public sealed record CoachDecision(
    string DecisionId, string TeamId, int AtSec, string Brief, IReadOnlyList<CoachPlanOption> Menu,
    IReadOnlyList<CoachProposal> Proposals, string? Picked, IReadOnlyDictionary<string, double>? Probabilities,
    string Source, IReadOnlyDictionary<string, string> TestIds, string? Verdict = null);

/// <summary>What the coach's agents return.</summary>
public sealed class CoachPick
{
    public string Plan { get; set; } = "";
    public string Rationale { get; set; } = "";
}

/// <summary>
/// The coach's corner without the language models: the plan menu with its targets, the brief, the plain-rule coaches,
/// Decision-1 as the arbiter, and the verdict once both plans are settled. <see cref="CoachCorner"/> adds the agents.
/// </summary>
public static class CoachPlans
{
    public const int WindowSec = TacticalTests.WindowSec;

    public static readonly (string Id, string Name, string NameFi, string Instincts)[] Coaches =
    [
        ("frontFoot", "Front-foot coach", "Hyökkäävä valmentaja",
            "You are an attacking, front-foot football coach. You want your team to take the initiative: win the ball high, get into the final third, play forward."),
        ("gameManager", "Game manager", "Pelin hallitsija",
            "You are a pragmatic, risk-aware football coach. You manage the game: protect what you have, control the tempo, take fewer risks, and only chase when you must."),
    ];

    /// <summary>
    /// The menu for a team now: every plan with a target set from the last ten minutes, so it asks for a real change
    /// but one the team can reach. "Take the final third" needs tracking (pitch control); without it it is left out.
    /// </summary>
    public static IReadOnlyList<CoachPlanOption> Menu(LiveMatch match, string teamId, int nowSec, double? finalThirdControl)
    {
        MatchInfo info = match.Info;
        string team = info.Team(teamId).Name, opp = info.Team(info.OpponentOf(teamId)).Name;
        int from = Math.Max(0, nowSec - WindowSec);
        TeamWindow us = match.Window(teamId, from, nowSec);
        string by = NarrativeText.Clock(nowSec + WindowSec);
        double longShare = ShapeAnalyzer.Measure(match, teamId, from, nowSec) is { } shape ? shape.Style.LongBallShare : 0.12;
        int press = Math.Max(2, us.HighRecoveries.Count + 1);
        double direct = Math.Round(Math.Clamp(longShare + 0.08, 0.15, 0.45), 2);
        string pct(double v) => $"{Math.Round(v * 100):0}%";
        string P(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        var menu = new List<CoachPlanOption>
        {
            new(CoachPlanKind.PressHigher, "Press higher", "Prässää korkeammalta", $"highRecoveries>={press}", press,
                $"{team} win the ball high at least {press} times by {by}.",
                $"{team} voittaa pallon korkealla vähintään {press} kertaa {by} mennessä.",
                "Counts the high regains; it does not show whether they led anywhere.",
                "Laskee korkeat riistot; ei kerro, johtivatko ne mihinkään."),
            new(CoachPlanKind.SitDeeper, "Sit deeper and protect", "Vetäydy ja suojaa", "opponentXg<=0.25", 0.25,
                $"{opp} create no more than 0.25 xG until {by}.",
                $"{opp} luo enintään 0,25 xG:tä {by} asti.",
                "Shows what the opponent created, not whether sitting deep caused it.",
                "Kertoo, mitä vastustaja sai aikaan, ei sitä, johtuiko se syvään vetäytymisestä."),
            new(CoachPlanKind.GoDirect, "Go direct", "Pelaa suoraan", $"longBallShare>={P(direct)}", direct,
                $"At least {pct(direct)} of {team}'s passes are long until {by}.",
                $"Vähintään {pct(direct)} {team}n syötöistä on pitkiä {by} asti.",
                "Shows whether the team kept going long, not whether it worked.",
                "Kertoo, jatkoiko joukkue pitkillä palloilla, ei sitä, toimiko se."),
            new(CoachPlanKind.KeepTheBall, "Keep the ball", "Pidä pallo", "possession>=0.55", 0.55,
                $"{team} keep at least 55% of the ball until {by}.",
                $"{team} pitää vähintään 55 % pallosta {by} asti.",
                "Possession by completed passes; it does not show what the ball was used for.",
                "Pallonhallinta onnistuneiden syöttöjen mukaan; ei kerro, mihin palloa käytettiin."),
        };
        if (finalThirdControl is { } control)
        {
            double target = Math.Round(Math.Clamp(control + 0.05, 0.28, 0.5), 2);
            menu.Add(new(CoachPlanKind.TakeTheFinalThird, "Take the final third", "Ota hyökkäyskolmannes", $"finalThirdControl>={P(target)}", target,
                $"{team} control at least {pct(target)} of the final third until {by}.",
                $"{team} hallitsee vähintään {pct(target)} hyökkäyskolmanneksesta {by} asti.",
                "Pitch control from tracking: who would reach each spot first. It shows the territory, not the chances.",
                "Kenttähallinta seurantadatasta: kuka ehtisi kuhunkin kohtaan ensin. Kertoo alueesta, ei maalipaikoista."));
        }
        return menu;
    }

    /// <summary>The plain description of the match both coaches and Decision-1 read.</summary>
    public static string Brief(LiveMatch match, string teamId, int nowSec, double? finalThirdControl, double? opponentFinalThirdControl)
    {
        MatchInfo info = match.Info;
        string oppId = info.OpponentOf(teamId);
        string team = info.Team(teamId).Name, opp = info.Team(oppId).Name;
        int from = Math.Max(0, nowSec - WindowSec);
        TeamWindow us = match.Window(teamId, from, nowSec), them = match.Window(oppId, from, nowSec);
        int gf = match.Goals(teamId), ga = match.Goals(oppId);
        string score = gf > ga ? $"{team} lead {gf}-{ga}" : gf < ga ? $"{team} trail {gf}-{ga}" : $"it is {gf}-{ga}";
        string control = finalThirdControl is { } c && opponentFinalThirdControl is { } oc
            ? $" From tracking, {team} control {Math.Round(c * 100):0}% of the final third they attack and {opp} {Math.Round(oc * 100):0}% of theirs."
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"Football, minute {nowSec / 60}: {score} against {opp}. Over the last ten minutes {team} had {Math.Round(us.PossessionShare * 100):0}% of the completed passes, " +
            $"{us.Shots.Count} shots worth {us.Xg:0.00} xG and {us.HighRecoveries.Count} high regains; {opp} had {them.Shots.Count} shots worth {them.Xg:0.00} xG " +
            $"and {them.HighRecoveries.Count} high regains.{control}");
    }

    public static TimeSpan ArbiterTimeout { get; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Decision-1 as the arbiter: a choice question between the two proposals, each described by the criterion that
    /// will settle it; returns the pick, a probability for each, and where it came from ("none" without Decision-1,
    /// "unavailable" when it did not answer in time).
    /// </summary>
    public static async Task<(string? Picked, IReadOnlyDictionary<string, double>? Probabilities, string Source)> ArbitrateAsync(
        IDecisionClient? decisions, string team, IReadOnlyList<CoachPlanOption> menu, IReadOnlyList<CoachProposal> proposals, string brief, CancellationToken ct)
    {
        if (decisions is null) return (null, null, "none");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ArbiterTimeout);
            CoachPlanOption a = menu.First(m => m.Kind == proposals[0].Plan), b = menu.First(m => m.Kind == proposals[1].Plan);
            string state = brief + $" The {Coaches[0].Name.ToLowerInvariant()} proposes: {a.Title.ToLowerInvariant()} ({proposals[0].Rationale}). " +
                           $"The {Coaches[1].Name.ToLowerInvariant()} proposes: {b.Title.ToLowerInvariant()} ({proposals[1].Rationale}).";
            var options = new Dictionary<string, string> { [proposals[0].Coach] = a.Criterion, [proposals[1].Coach] = b.Criterion };
            DecisionResult result = await decisions.DecideAsync(state, new Dictionary<string, DecisionQuestion>
            {
                ["plan"] = new ChoiceQuestion($"Which of these will hold for {team} over the next ten minutes?", options),
            }, timeout.Token);
            ChoiceAnswer answer = result.Get<ChoiceAnswer>("plan");
            return (answer.Choice, answer.Probabilities.ToDictionary(p => p.Key, p => Math.Round(p.Value, 2)), "Decision-1");
        }
        catch (Exception ex) when (ex is DecisionException or HttpRequestException or OperationCanceledException or InvalidOperationException or KeyNotFoundException && !ct.IsCancellationRequested)
        {
            return (null, null, "unavailable");
        }
    }

    /// <summary>Plain rules when there is no model: the front-foot coach pushes, the game manager protects or controls.</summary>
    public static CoachProposal Fallback((string Id, string Name, string NameFi, string Instincts) coach, IReadOnlyList<CoachPlanOption> menu, bool ahead, CoachPlanKind? taken)
    {
        CoachPlanKind[] order = coach.Id == "frontFoot"
            ? [CoachPlanKind.PressHigher, CoachPlanKind.TakeTheFinalThird, CoachPlanKind.GoDirect, CoachPlanKind.KeepTheBall]
            : ahead ? [CoachPlanKind.SitDeeper, CoachPlanKind.KeepTheBall, CoachPlanKind.GoDirect] : [CoachPlanKind.KeepTheBall, CoachPlanKind.SitDeeper, CoachPlanKind.GoDirect];
        CoachPlanKind kind = order.First(k => k != taken && menu.Any(m => m.Kind == k));
        return new CoachProposal(coach.Id, coach.Name, kind, FallbackReason(kind), FromModel: false);
    }

    public static string FallbackReason(CoachPlanKind kind) => kind switch
    {
        CoachPlanKind.PressHigher => "Win it back near their goal and they have no time to organise.",
        CoachPlanKind.SitDeeper => "Protect the shape and give them nothing in behind.",
        CoachPlanKind.GoDirect => "Skip the press and play into the space behind their line.",
        CoachPlanKind.KeepTheBall => "Slow it down and make them chase the ball.",
        _ => "Push the team up and own the space in front of their box.",
    };

    /// <summary>
    /// Whether Decision-1's pick was the plan that held, once both tests are settled: right (the pick held, the other
    /// did not), wrong (the other held, the pick did not), or level (both held or neither did, or one was undecided).
    /// </summary>
    public static string? Judge(CoachDecision d, Func<string, TacticalTest?> test)
    {
        if (d.Picked is null || d.TestIds.Count < 2) return null;
        var tests = d.TestIds.ToDictionary(k => k.Key, k => test(k.Value));
        if (tests.Values.Any(t => t is null || t.Status == TestStatus.Open)) return null;
        bool pickHeld = tests[d.Picked]!.Status == TestStatus.Met;
        bool otherHeld = tests.Where(k => k.Key != d.Picked).Any(k => k.Value!.Status == TestStatus.Met);
        if (tests.Values.Any(t => t!.Status == TestStatus.Inconclusive)) return "level";
        return pickHeld && !otherHeld ? "right" : !pickHeld && otherHeld ? "wrong" : "level";
    }
}
