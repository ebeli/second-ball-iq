namespace MatchLens.Contracts;

/// <summary>
/// Insight contract (schema 1.0): what the agents publish and what the dashboard, the overlay renderer and the
/// recap all consume. A renderer never has to parse generated prose: timing, targets and evidence are fields.
/// </summary>
public sealed record Insight
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string InsightId { get; init; }

    public required string MatchId { get; init; }

    /// <summary>Match clock position the insight refers to; the overlay shows it from here.</summary>
    public required int MatchClockSec { get; init; }

    /// <summary>How long an overlay shows it.</summary>
    public int DisplayMs { get; init; } = 8000;

    public required InsightKind Kind { get; init; }

    public required Audience Audience { get; init; }

    /// <summary>BCP-47 language tag, e.g. "en" or "fi".</summary>
    public required string Language { get; init; }

    public required string Headline { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<string> TeamIds { get; init; } = [];

    public IReadOnlyList<string> PlayerIds { get; init; } = [];

    /// <summary>Every insight cites the events it rests on.</summary>
    public required IReadOnlyList<string> EvidenceEventIds { get; init; }

    public IReadOnlyList<InsightFigure> Figures { get; init; } = [];

    public InsightValidation? Validation { get; init; }

    public PublishDecision? Publish { get; init; }
}

/// <summary>The detector kinds; also the triage gate's option ids.</summary>
public enum InsightKind
{
    PressingShift,
    PossessionIllusion,
    TurnoverToChance,
    PlayerMilestone,

    /// <summary>Any goal that is not already a turnover to chance: the move behind it, from the regain to the finish.</summary>
    Goal,

    /// <summary>A team changes how it plays: a new formation, sitting deeper, or going direct.</summary>
    TacticalShift,

    /// <summary>A player is playing a distinctive role (false nine, second or shadow striker, inverted winger, wing-back).</summary>
    PlayerRole,

    /// <summary>From tracking: a team has taken over the final third (pitch control, not possession).</summary>
    Territory,

    /// <summary>From tracking: a player keeps missing a better-placed teammate (expected threat of his options).</summary>
    BetterPass,

    /// <summary>A coach's plan from the coach's corner, put to the test (tests only; never a moment on air).</summary>
    CoachPlan,
}

public enum Audience
{
    Casual,
    Analyst,
    Broadcast,

    /// <summary>Someone new to football: plain words, terms explained, and why the moment matters.</summary>
    Beginner,
}

/// <summary>A number shown with the insight, recomputed by the Validator before publishing.</summary>
public sealed record InsightFigure(string Label, double Value, string? Unit = null);

public enum CheckStatus
{
    NotRun,
    Pass,
    Fail,
}

/// <summary>Deterministic checks plus the grounding gate's lowest check probability.</summary>
public sealed record InsightValidation(CheckStatus Evidence, CheckStatus Numbers, double? GroundingP, string? Reason = null);

public enum PublishMode
{
    Auto,
    Editor,
    Drop,
}

/// <param name="By">"gate" for automatic decisions, otherwise the editor's id.</param>
public sealed record PublishDecision(PublishMode Decision, double? P, string By);
