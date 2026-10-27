namespace MatchLens.Contracts;

/// <summary>
/// Where a set piece went, in a few zones a viewer understands. Corners: short, near post, central, far post.
/// Penalties and direct free-kick shots: the shooter's left, centre or right of the goal.
/// </summary>
public static class SetPieceZones
{
    public const string Short = "short";
    public const string NearPost = "near";
    public const string Central = "central";
    public const string FarPost = "far";
    public const string Left = "left";
    public const string Centre = "centre";
    public const string Right = "right";

    public static IReadOnlyList<string> CornerZones { get; } = [NearPost, Central, FarPost, Short];

    public static IReadOnlyList<string> ShotZones { get; } = [Left, Centre, Right];

    /// <summary>The zones a prediction chooses between for this kind of set piece.</summary>
    public static IReadOnlyList<string> For(SetPiece kind) => kind == SetPiece.Corner ? CornerZones : ShotZones;

    /// <summary>
    /// A corner's delivery zone from where it was taken and where it went, in the taking team's 0..100 frame.
    /// Short: it did not reach the box, or travelled under about 15 m. Otherwise near post, central or far post,
    /// measured across the goal mouth from the side the corner was taken.
    /// </summary>
    public static string Corner(PitchPoint start, PitchPoint end)
    {
        if (end.X < 85 || Pitch.DistanceMetres(start, end) < 15) return Short;
        double towardTaker = (end.Y - 50) * (start.Y < 50 ? -1 : 1);
        return towardTaker > 3 ? NearPost : towardTaker < -3 ? FarPost : Central;
    }

    /// <summary>
    /// A shot's placement across the goal from the shooter's point of view. The goal spans Y 45 to 55 in the 0..100
    /// frame; the attacking team's left is towards Y = 0.
    /// </summary>
    public static string Shot(PitchPoint end) =>
        end.Y < 48.33 ? Left : end.Y > 51.67 ? Right : Centre;

    public static string Label(string zone, string language) => (language, zone) switch
    {
        ("fi", Short) => "Lyhyt",
        ("fi", NearPost) => "Lähitolppa",
        ("fi", Central) => "Keskelle",
        ("fi", FarPost) => "Takatolppa",
        ("fi", Left) => "Vasen",
        ("fi", Centre) => "Keskelle",
        ("fi", Right) => "Oikea",
        (_, Short) => "Short",
        (_, NearPost) => "Near post",
        (_, Central) => "Central",
        (_, FarPost) => "Far post",
        (_, Left) => "Left",
        (_, Centre) => "Centre",
        (_, Right) => "Right",
        _ => zone,
    };
}

/// <summary>One set piece from earlier matches, used to predict where a taker goes next.</summary>
public sealed record SetPieceRecord(string PlayerId, string PlayerName, SetPiece Kind, string Zone, string? Outcome, string Date);

/// <summary>A player's numbers over the earlier matches of the season.</summary>
public sealed record PlayerSeason(
    string PlayerId, string Name, string Team, int Matches, int Goals, int Assists, int Shots, double Xg,
    int Passes, int CompletedPasses, int PenaltiesTaken, int PenaltiesScored);

/// <summary>What happened before the replayed match: both clubs' earlier matches that season.</summary>
public sealed record MatchHistory(
    string Description, int Matches, IReadOnlyList<SetPieceRecord> SetPieces, IReadOnlyList<PlayerSeason> Players)
{
    public static MatchHistory Empty { get; } = new("No history", 0, [], []);

    public PlayerSeason? Player(string? playerId) => Players.FirstOrDefault(p => p.PlayerId == playerId);

    public IEnumerable<SetPieceRecord> For(string? playerId, SetPiece kind) =>
        SetPieces.Where(s => s.PlayerId == playerId && s.Kind == kind);
}
