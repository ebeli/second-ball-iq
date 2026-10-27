namespace MatchLens.Contracts;

/// <summary>
/// What a scenario was built to contain, measured from the generated events (schema 1.0).
/// The evaluation runner compares Second Ball IQ output against this file.
/// </summary>
public sealed record GroundTruth
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string MatchId { get; init; }

    public required string ScenarioId { get; init; }

    public required int Seed { get; init; }

    public required int HomeGoals { get; init; }

    public required int AwayGoals { get; init; }

    public required IReadOnlyList<Expectation> Expectations { get; init; }

    public required IReadOnlyList<ScriptedMomentRecord> Moments { get; init; }

    /// <summary>
    /// Every expectation the scenario relies on held. Player roles are left out: whether a wide player gets 20
    /// touches in a spell depends on the run of play, and the evaluation only counts the roles that were verified.
    /// </summary>
    public bool AllVerified => Expectations.Where(e => e.Kind != InsightKind.PlayerRole).All(e => e.Verified);
}

/// <summary>
/// One pattern the scenario intends Second Ball IQ to find.
/// <see cref="Verified"/> says whether the generated events actually satisfy <see cref="Rule"/>; scenarios
/// rely partly on random play, so a seed can fail and another seed is used.
/// </summary>
public sealed record Expectation(
    InsightKind Kind,
    string TeamId,
    string? PlayerId,
    int FromSec,
    int ToSec,
    string Rule,
    bool Verified,
    IReadOnlyDictionary<string, double> Measured,
    IReadOnlyList<string> EvidenceEventIds);

/// <summary>An event chain the script injected, with the ids of the events it produced.</summary>
public sealed record ScriptedMomentRecord(
    string Kind,
    string TeamId,
    int MatchClockSec,
    IReadOnlyList<string> EventIds);
