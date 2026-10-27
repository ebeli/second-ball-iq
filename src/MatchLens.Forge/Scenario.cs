using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>
/// How a team plays during a phase. Defaults give realistic balanced play: about 490 passes per team,
/// 81% completion, 13 shots and 1.3 xG per match (measured on 40 seeds in the Python prototype).
/// </summary>
public sealed record TeamStyle
{
    public static TeamStyle Balanced { get; } = new();

    /// <summary>Base pass completion before pressure, length and zone adjustments.</summary>
    public double PassAccuracy { get; init; } = 0.86;

    /// <summary>Mean forward progress per short pass, in pitch units (0..100).</summary>
    public double Progression { get; init; } = 6.0;

    /// <summary>Share of passes that are long balls.</summary>
    public double Directness { get; init; } = 0.15;

    /// <summary>Chance per action of shooting once well inside the final third.</summary>
    public double ShotPropensity { get; init; } = 0.10;

    /// <summary>Chance per opponent action of pressing it; scaled up when the opponent is deep in its own half.</summary>
    public double Press { get; init; } = 0.18;

    /// <summary>Chance that a press wins the ball outright.</summary>
    public double Tackle { get; init; } = 0.06;

    /// <summary>Multiplier on forward progress once past 60; below 1 means the team struggles to enter the box.</summary>
    public double FinalThirdEntry { get; init; } = 1.0;

    /// <summary>
    /// Multiplier on pressing while the opponent has the ball in its own half. Below 1 is a low block: the team
    /// waits in its own half instead of pressing up the pitch.
    /// </summary>
    public double BlockHeight { get; init; } = 1.0;
}

public enum Side
{
    Home,
    Away,
}

/// <summary>A stretch of the match where one or both teams play a different style.</summary>
public sealed record Phase(int FromSec, int ToSec, TeamStyle? Home = null, TeamStyle? Away = null);

public enum MomentKind
{
    /// <summary>The team wins the ball in the opponent's defensive third, optionally followed by a shot.</summary>
    HighTurnover,

    /// <summary>A short build-up ending in a shot (usually a goal), independent of the run of play.</summary>
    BuildUpShot,

    /// <summary>A foul in the box and a penalty, taken by the club's penalty taker unless a player is given.</summary>
    Penalty,

    /// <summary>A foul just outside the box and a direct free kick at goal.</summary>
    FreeKick,
}

/// <summary>An event chain injected at a fixed time, so the demo's key moments always happen.</summary>
/// <param name="Follow">Result of the shot that follows; null means no shot.</param>
/// <param name="PlayerId">Optional player who wins the ball or shoots.</param>
public sealed record ScriptedMoment(int AtSec, Side Side, MomentKind Kind, ShotResult? Follow = null, string? PlayerId = null);

/// <summary>A pattern the scenario intends Second Ball IQ to find, checked against the generated events.</summary>
public sealed record ExpectationSpec(InsightKind Kind, Side Side, int FromSec, int ToSec, string? PlayerId = null);

/// <param name="Home">Home shape at kick-off; Harbor City's 4-3-3 when null.</param>
/// <param name="Away">Away shape at kick-off; Northvale's 4-2-3-1 when null.</param>
/// <param name="Changes">The coaches' decisions during the match.</param>
/// <param name="Reactions">How the teams react to the score.</param>
public sealed record Scenario(
    string Id,
    string Description,
    int DefaultSeed,
    IReadOnlyList<Phase> Phases,
    IReadOnlyList<ScriptedMoment> Moments,
    IReadOnlyList<ExpectationSpec> Expectations,
    Formation? Home = null,
    Formation? Away = null,
    IReadOnlyList<CoachChange>? Changes = null,
    IReadOnlyList<GameStateReaction>? Reactions = null)
{
    public Formation HomeLineup => Home ?? Lineups.HarborCity433;

    public Formation AwayLineup => Away ?? Lineups.Northvale4231;

    public IReadOnlyList<CoachChange> CoachChanges => Changes ?? [];

    public IReadOnlyList<GameStateReaction> ScoreReactions => Reactions ?? [];
}

/// <summary>Built-in scenarios. Times are match seconds; the second half starts at 2700.</summary>
public static class Scenarios
{
    private const int Min = 60;

    public static IReadOnlyList<Scenario> All { get; } =
    [
        Balanced(),
        PossessionIllusion(),
        PressingShift(),
        GameState(),
        DemoMatch(),
    ];

    public static Scenario? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Home dominates the ball from minute 15 to 40 against a deep block but creates almost nothing.
    /// Tuned for about 77% of completed passes in the window; harsher settings gave a fake-looking 88%.
    /// </summary>
    private static TeamStyle Dominant => TeamStyle.Balanced with
    {
        PassAccuracy = 0.90, Progression = 2.5, Directness = 0.03, ShotPropensity = 0.03, FinalThirdEntry = 0.4,
    };

    private static TeamStyle DeepBlock => TeamStyle.Balanced with
    {
        Press = 0.08, Tackle = 0.04,
    };

    /// <summary>Away presses high from minute 46 to 60.</summary>
    private static TeamStyle HighPress => TeamStyle.Balanced with { Press = 0.32, Tackle = 0.09 };

    private static TeamStyle PressedSide => TeamStyle.Balanced with { PassAccuracy = 0.84 };

    /// <summary>Protecting a lead: a deep block, no pressing up the pitch.</summary>
    private static TeamStyle SitDeep(TeamStyle s) => s with { Press = s.Press * 0.4, BlockHeight = 0.15 };

    /// <summary>Chasing the game: more long balls into the strikers.</summary>
    private static TeamStyle GoDirect(TeamStyle s) => s with { Directness = 0.33 };

    private static GameStateReaction HarborSitOnLead() =>
        new(Side.Home, ScoreState.Leading, 2 * Min, "protecting the lead", Lineups.HarborCityBlock, SitDeep);

    private static ScriptedMoment[] PressMoments() =>
    [
        new(47 * Min + 10, Side.Away, MomentKind.HighTurnover),
        new(49 * Min + 30, Side.Away, MomentKind.HighTurnover, ShotResult.OffTarget),
        new(52 * Min + 5, Side.Away, MomentKind.HighTurnover),
        new(55 * Min + 40, Side.Away, MomentKind.HighTurnover, ShotResult.Saved),
    ];

    private static Scenario Balanced() => new(
        "balanced", "Two evenly matched teams; no scripted moments. Baseline for false-positive checks.",
        DefaultSeed: 1, Phases: [], Moments: [], Expectations: []);

    private static Scenario PossessionIllusion() => new(
        "possession-illusion",
        "Harbor City keep the ball from minute 15 to 40 but cannot get into the box against Northvale's deep block.",
        DefaultSeed: 1,
        Phases: [new(15 * Min, 40 * Min, Dominant, DeepBlock)],
        Moments: [],
        Expectations: [new(InsightKind.PossessionIllusion, Side.Home, 20 * Min, 35 * Min)]);

    private static Scenario PressingShift() => new(
        "pressing-shift",
        "Northvale switch to a high press after half-time and win the ball high four times in nine minutes, two leading to shots.",
        DefaultSeed: 1,
        Phases: [new(46 * Min, 60 * Min, PressedSide, HighPress)],
        Moments: PressMoments(),
        Expectations: [new(InsightKind.PressingShift, Side.Away, 46 * Min, 58 * Min)]);

    /// <summary>
    /// The score changes how both teams play: Harbor City go ahead at 28' and drop into a deep 4-5-1; Northvale's
    /// coach answers at 40' with a second striker and a 4-4-2, and they start going long.
    /// </summary>
    private static Scenario GameState() => new(
        "game-state",
        "Harbor City score at 28' and sit deep to protect the lead; at 40' Northvale switch to 4-4-2 with a second " +
        "striker and go direct.",
        DefaultSeed: 1,
        Phases: [],
        Moments: [new(28 * Min, Side.Home, MomentKind.BuildUpShot, ShotResult.Goal, "har-09")],
        Expectations: [],
        Changes:
        [
            new(40 * Min, Side.Away, "chasing the game", Lineups.Northvale442, Lineups.VargaForMoreau, GoDirect),
        ],
        Reactions: [HarborSitOnLead()]);

    /// <summary>The demo storyline: misleading possession, a home goal, the press, and a turnover goal.</summary>
    private static Scenario DemoMatch() => new(
        "demo-match",
        "The 2-minute demo story: Harbor City's sterile possession, a goal against the run of play, Northvale's press, " +
        "a turnover goal that levels it 1-1, then a free kick and a late penalty. Tactically: Harbor City's false nine and " +
        "inverted wingers, Harbor dropping deep once ahead, and Northvale's switch to a back three with two strikers at 52'.",
        DefaultSeed: 1,
        Phases:
        [
            new(15 * Min, 40 * Min, Dominant, DeepBlock),
            new(46 * Min, 62 * Min, PressedSide, HighPress),
        ],
        Moments:
        [
            new(41 * Min + 20, Side.Home, MomentKind.BuildUpShot, ShotResult.Goal, "har-09"),
            .. PressMoments(),
            new(61 * Min, Side.Away, MomentKind.HighTurnover, ShotResult.Goal, "nor-09"),
            // Set pieces for the live predictions; neither changes the score, so the ground truth stays as it was.
            new(66 * Min, Side.Home, MomentKind.FreeKick, ShotResult.Saved),
            new(79 * Min, Side.Away, MomentKind.Penalty, ShotResult.Saved),
        ],
        Expectations:
        [
            new(InsightKind.PossessionIllusion, Side.Home, 20 * Min, 35 * Min),
            new(InsightKind.PressingShift, Side.Away, 46 * Min, 58 * Min),
            new(InsightKind.TurnoverToChance, Side.Away, 61 * Min, 62 * Min),
        ],
        Changes:
        [
            new(52 * Min, Side.Away, "chasing the game", Lineups.Northvale3412, Lineups.VargaForDemir, GoDirect),
        ],
        Reactions: [HarborSitOnLead()]);
}
