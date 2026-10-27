namespace DecisionSpike;

/// <summary>
/// A hand-written match window with its expected triage answer. Each window is written twice:
/// as plain-language prose and as compact numbers, so the spike can measure which format the
/// decision model reads better. All clubs and players are fictional.
/// </summary>
/// <param name="Accepted">Answers counted as correct. Ambiguous windows accept more than one.</param>
/// <param name="ExpectReview">True when the honest outcome is "send to editor", not an automatic call.</param>
public sealed record TriageCase(string Id, string[] Accepted, bool ExpectReview, string Prose, string Numbers);

/// <summary>A drafted claim checked against its evidence, with the expected yes/no answer.</summary>
public sealed record GroundingCase(string Id, string Claim, bool ExpectedSupported, string Why);

public static class Scenarios
{
    public static readonly TriageCase[] Triage =
    [
        new("possession-illusion", ["possessionIllusion"], false,
            Prose: """
                Match minute 34. Score: Harbor City 0, Northvale 0.
                In the last 10 minutes Harbor City had 71% of the ball and completed 112 passes, but only 2 of them
                reached the final third and they have not had a shot since minute 19. Northvale defended deep and
                had 1 shot. Neither side won the ball back in the opponent's half. No player did anything unusual.
                """,
            Numbers: """
                minute=34 score=HAR 0-0 NOR window=10min
                HAR: possession=0.71 passes=112 final_third_entries=2 shots=0 xg=0.00 high_turnovers_won=0
                NOR: possession=0.29 passes=41 final_third_entries=3 shots=1 xg=0.04 high_turnovers_won=0
                player_events=none
                """),

        new("pressing-shift", ["pressingShift"], false,
            Prose: """
                Match minute 52. Score: Harbor City 1, Northvale 1.
                In the last 6 minutes Northvale won the ball in Harbor City's defensive third 4 times, compared with
                once in the whole first half. Two of those recoveries were followed by shots that missed. Harbor City
                still had 58% of the ball but kept losing it while building from the back. No goals in the window.
                """,
            Numbers: """
                minute=52 score=HAR 1-1 NOR window=6min
                HAR: possession=0.58 shots=0 xg=0.00 turnovers_in_own_third=4
                NOR: possession=0.42 shots=2 xg=0.19 high_turnovers_won=4 high_turnovers_won_first_half=1 shots_after_recovery=2
                player_events=none
                """),

        new("turnover-to-goal", ["turnoverToChance"], false,
            Prose: """
                Match minute 61. Score: Harbor City 1, Northvale 2.
                Ten seconds ago Northvale's midfielder intercepted a Harbor City pass just outside Harbor City's penalty
                area and passed to the striker, who scored 9 seconds after the interception. Apart from that move the
                last 5 minutes were even, with each side having about half of the ball.
                """,
            Numbers: """
                minute=61 score=HAR 1-2 NOR window=5min
                event: NOR interception at x=78 y=44 t=60:41 -> pass t=60:44 -> goal t=60:50 (recovery_to_goal_s=9)
                HAR: possession=0.49 shots=1 high_turnovers_won=0
                NOR: possession=0.51 shots=1 high_turnovers_won=1
                player_events=none
                """),

        new("player-milestone", ["playerMilestone"], false,
            Prose: """
                Match minute 74. Score: Harbor City 2, Northvale 2.
                Harbor City's captain Mira Okafor just completed her 80th pass of the match, the most by any player
                for either club this season and a career high. Her pass accuracy today is 94%. The rest of the window
                was quiet: no shots, possession split evenly, no team changes in approach.
                """,
            Numbers: """
                minute=74 score=HAR 2-2 NOR window=5min
                HAR: possession=0.50 shots=0 high_turnovers_won=0
                NOR: possession=0.50 shots=0 high_turnovers_won=0
                player_events: HAR #6 Okafor passes_completed=80 (career_high=true, season_high_both_clubs=true) pass_accuracy=0.94
                """),

        new("quiet-window", ["none"], false,
            Prose: """
                Match minute 23. Score: Harbor City 0, Northvale 0.
                The last 5 minutes were even and uneventful. Possession was split 52% to 48%, there were no shots,
                no turnovers in dangerous areas, and no player did anything out of the ordinary.
                """,
            Numbers: """
                minute=23 score=HAR 0-0 NOR window=5min
                HAR: possession=0.52 shots=0 xg=0.00 high_turnovers_won=0
                NOR: possession=0.48 shots=0 xg=0.00 high_turnovers_won=0
                player_events=none
                """),

        new("ambiguous-press-vs-possession", ["pressingShift", "possessionIllusion"], true,
            Prose: """
                Match minute 40. Score: Harbor City 0, Northvale 0.
                In the last 8 minutes Harbor City had 64% of the ball but no shots. Northvale won the ball high up the
                pitch twice, up from none earlier, but did not create a shot from either recovery.
                """,
            Numbers: """
                minute=40 score=HAR 0-0 NOR window=8min
                HAR: possession=0.64 shots=0 xg=0.00
                NOR: possession=0.36 shots=0 xg=0.00 high_turnovers_won=2 high_turnovers_won_before=0 shots_after_recovery=0
                player_events=none
                """),
    ];

    /// <summary>v1 evidence: a raw event list. The model has to infer "high up the pitch" and recovery-to-shot links.</summary>
    public const string GroundingEvidence = """
        Events 101-112, match minutes 46 to 52, Harbor City vs Northvale, score 1-1.
        - evt-104: Northvale win the ball in Harbor City's defensive third.
        - evt-105: Northvale shot, off target, xG 0.08.
        - evt-107: Northvale win the ball in Harbor City's defensive third.
        - evt-109: Northvale win the ball in Harbor City's defensive third.
        - evt-110: Northvale shot, saved, xG 0.21.
        Harbor City possession in this window: 68%. Harbor City shots in this window: 0.
        """;

    /// <summary>
    /// v2 evidence: the same events, plus the derived facts the analytics engine already computes,
    /// stated in plain words (counts, what "high" means, which recovery led to which shot, what did not happen).
    /// </summary>
    public const string GroundingEvidenceV2 = """
        Window: match minutes 46 to 52, the first seven minutes of the second half. Score: Harbor City 1, Northvale 1.
        High turnovers (ball won in the opponent's defensive third, i.e. high up the pitch):
        - Northvale: 3, at evt-104 (46:50), evt-107 (48:30) and evt-109 (51:12). Harbor City: 0.
        Shots:
        - Northvale: 2. evt-105, off target, xG 0.08, 6 seconds after recovery evt-104.
          evt-110, saved by the goalkeeper, xG 0.21, 5 seconds after recovery evt-109.
        - Recovery evt-107 did not lead to a shot.
        - Harbor City: 0.
        Possession in the window: Harbor City 68%, Northvale 32%.
        Goals, penalties and cards in the window: none.
        """;

    public static readonly GroundingCase[] Grounding =
    [
        new("supported", "Northvale have won the ball high up the pitch three times since half-time and turned two of those recoveries into shots.",
            true, "3 recoveries (104, 107, 109); shots 105 and 110 follow recoveries."),
        new("supported-interpretive", "Harbor City still have most of the ball, but they have not managed a shot since the break.",
            true, "68% possession and 0 shots in the window."),
        new("wrong-number", "Northvale have won the ball high up the pitch five times in six minutes.",
            false, "Evidence shows 3, not 5."),
        new("invented-event", "Northvale's pressing has just forced a penalty.",
            false, "No penalty in the evidence."),
        new("wrong-outcome", "Northvale's second recovery led to a goal.",
            false, "Shots missed or were saved; no goal."),
        new("supported-paraphrase", "Since the restart Northvale keep winning the ball near Harbor City's goal, and two of those turnovers produced shots.",
            true, "Same facts as 'supported', different words."),
        new("supported-plain", "Harbor City have had 68% of the ball in this spell but no shots.",
            true, "Both figures are in the evidence."),
        new("misleading-framing", "Northvale have dominated possession since half-time.",
            false, "Northvale had 32% of the ball."),
    ];

    /// <summary>The v1 baseline: one strict question. Kept only to compare against.</summary>
    public static readonly IReadOnlyDictionary<string, MatchLens.Decisions.DecisionQuestion> GroundingV1Questions =
        new Dictionary<string, MatchLens.Decisions.DecisionQuestion>
        {
            ["grounded"] = new MatchLens.Decisions.YesNoQuestion(
                "Is every factual statement in the CLAIM directly supported by the EVIDENCE, with no invented events or numbers?"),
        };
}
