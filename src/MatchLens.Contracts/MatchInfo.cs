namespace MatchLens.Contracts;

/// <summary>Match metadata (schema 1.0). All clubs and players are fictional.</summary>
public sealed record MatchInfo
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string MatchId { get; init; }

    /// <summary>The Match Forge scenario that produced this match.</summary>
    public required string ScenarioId { get; init; }

    /// <summary>Random seed; the same scenario and seed always produce the same events.</summary>
    public required int Seed { get; init; }

    public required string Competition { get; init; }

    public required TeamInfo Home { get; init; }

    public required TeamInfo Away { get; init; }

    public TeamInfo Team(string teamId) =>
        Home.TeamId == teamId ? Home : Away.TeamId == teamId ? Away
            : throw new ArgumentException($"Unknown team '{teamId}'.", nameof(teamId));

    public PlayerInfo? Player(string? playerId) =>
        playerId is null ? null : Home.Players.Concat(Away.Players).FirstOrDefault(p => p.PlayerId == playerId);

    public string OpponentOf(string teamId) => Home.TeamId == teamId ? Away.TeamId : Home.TeamId;
}

public sealed record TeamInfo(
    string TeamId,
    string Name,
    string ShortName,
    string PrimaryColor,
    IReadOnlyList<PlayerInfo> Players);

/// <param name="Position">The listed position on the team sheet. How the player actually plays is measured separately
/// (<see cref="PlayerRole"/>).</param>
/// <param name="Starter">False for substitutes on the bench at kick-off.</param>
public sealed record PlayerInfo(
    string PlayerId,
    string Name,
    int ShirtNumber,
    PlayerPosition Position,
    bool Starter = true);

public enum PlayerPosition
{
    Goalkeeper,
    Defender,
    Midfielder,
    Forward,
}

/// <summary>
/// The role a player actually plays, read from where they touch the ball. Shared by Match Forge (the role a scenario
/// gives a player) and the engine (the role it recognises).
/// </summary>
public enum PlayerRole
{
    Goalkeeper,
    CentreBack,
    FullBack,
    WingBack,
    HoldingMidfielder,
    CentralMidfielder,
    AttackingMidfielder,
    WideMidfielder,
    Winger,
    InvertedWinger,
    Striker,
    FalseNine,
    SecondStriker,
    ShadowStriker,
}

public static class PlayerRoles
{
    /// <summary>Roles worth telling a viewer about: they say something about the coach's plan.</summary>
    public static bool IsNotable(PlayerRole role) =>
        role is PlayerRole.FalseNine or PlayerRole.SecondStriker or PlayerRole.ShadowStriker or PlayerRole.InvertedWinger
            or PlayerRole.WingBack;

    public static string Label(PlayerRole role, string language = "en") => (language, role) switch
    {
        ("fi", PlayerRole.Goalkeeper) => "maalivahti",
        ("fi", PlayerRole.CentreBack) => "keskuspuolustaja",
        ("fi", PlayerRole.FullBack) => "laitapuolustaja",
        ("fi", PlayerRole.WingBack) => "laitatukimies",
        ("fi", PlayerRole.HoldingMidfielder) => "puolustava keskikenttä",
        ("fi", PlayerRole.CentralMidfielder) => "keskikenttäpelaaja",
        ("fi", PlayerRole.AttackingMidfielder) => "hyökkäävä keskikenttä",
        ("fi", PlayerRole.WideMidfielder) => "laitakeskikenttä",
        ("fi", PlayerRole.Winger) => "laituri",
        ("fi", PlayerRole.InvertedWinger) => "sisäänpäin hakeutuva laituri",
        ("fi", PlayerRole.Striker) => "kärkihyökkääjä",
        ("fi", PlayerRole.FalseNine) => "valeysi",
        ("fi", PlayerRole.SecondStriker) => "toinen kärki",
        ("fi", PlayerRole.ShadowStriker) => "varjokärki",
        (_, PlayerRole.CentreBack) => "centre-back",
        (_, PlayerRole.FullBack) => "full-back",
        (_, PlayerRole.WingBack) => "wing-back",
        (_, PlayerRole.HoldingMidfielder) => "holding midfielder",
        (_, PlayerRole.CentralMidfielder) => "central midfielder",
        (_, PlayerRole.AttackingMidfielder) => "attacking midfielder",
        (_, PlayerRole.WideMidfielder) => "wide midfielder",
        (_, PlayerRole.InvertedWinger) => "inverted winger",
        (_, PlayerRole.FalseNine) => "false nine",
        (_, PlayerRole.SecondStriker) => "second striker",
        (_, PlayerRole.ShadowStriker) => "shadow striker",
        _ => role.ToString().ToLowerInvariant(),
    };

    /// <summary>What the role means on the pitch, in one line, for the evidence the agents and gates read.</summary>
    public static string Meaning(PlayerRole role) => role switch
    {
        PlayerRole.FalseNine => "a centre-forward who drops into midfield instead of staying up against the defenders",
        PlayerRole.SecondStriker => "a forward playing just behind and alongside the main striker",
        PlayerRole.ShadowStriker => "a midfielder who keeps arriving in the box from behind the strikers",
        PlayerRole.InvertedWinger => "a winger who comes inside from the flank rather than staying on the touchline",
        PlayerRole.WingBack => "a wide player covering the whole flank, as high as midfield, usually with a back three",
        _ => Label(role),
    };
}
