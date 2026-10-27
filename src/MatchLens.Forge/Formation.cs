using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>
/// Where one player plays: a role and a home position in the team's own frame (0..100 from their own goal, 0..100
/// from their left touchline). The simulator gives the ball to whoever's home is nearest the ball, so a player's
/// touches gather around their home and the engine can read the role back from them.
/// </summary>
public sealed record RoleSlot(string PlayerId, PlayerRole Role, double X, double Y);

/// <summary>A team's shape: who plays where. Exactly eleven slots, the goalkeeper first.</summary>
public sealed record Formation(string Name, IReadOnlyList<RoleSlot> Slots)
{
    public RoleSlot? Slot(string playerId) => Slots.FirstOrDefault(s => s.PlayerId == playerId);
}

/// <summary>A substitution: who comes off and who comes on.</summary>
public sealed record Substitution(string Off, string On);

/// <summary>
/// A coach's decision at a fixed time: a new shape, a substitution, a different way of playing, or all three.
/// </summary>
/// <param name="Adjust">Changes the team's style from this point on (for example, more long balls).</param>
public sealed record CoachChange(
    int AtSec, Side Side, string Reason, Formation? Formation = null, Substitution? Substitution = null,
    Func<TeamStyle, TeamStyle>? Adjust = null);

public enum ScoreState
{
    Leading,
    Level,
    Trailing,
}

/// <summary>
/// How a team reacts to the score: while the state holds (after a short delay) it plays this shape and style, and
/// it goes back when the score changes. This is how a leading team sits deeper or a trailing team pushes on.
/// </summary>
public sealed record GameStateReaction(
    Side Side, ScoreState When, int DelaySec, string Reason, Formation? Formation = null,
    Func<TeamStyle, TeamStyle>? Adjust = null);

/// <summary>The clubs' shapes. Harbor City: 4-3-3 with a false nine and inverted wingers. Northvale: 4-2-3-1.</summary>
public static class Lineups
{
    private static RoleSlot S(string id, PlayerRole role, double x, double y) => new(id, role, x, y);

    public static Formation HarborCity433 { get; } = new("4-3-3",
    [
        S("har-01", PlayerRole.Goalkeeper, 5, 50),
        S("har-03", PlayerRole.FullBack, 30, 12),
        S("har-05", PlayerRole.CentreBack, 20, 38),
        S("har-04", PlayerRole.CentreBack, 20, 62),
        S("har-02", PlayerRole.FullBack, 30, 88),
        S("har-06", PlayerRole.HoldingMidfielder, 38, 50),
        S("har-10", PlayerRole.CentralMidfielder, 52, 34),
        S("har-08", PlayerRole.CentralMidfielder, 52, 66),
        S("har-11", PlayerRole.InvertedWinger, 72, 28),
        S("har-09", PlayerRole.FalseNine, 57, 50),
        S("har-07", PlayerRole.InvertedWinger, 72, 72),
    ]);

    /// <summary>Harbor City protecting a lead: a deep 4-5-1, the wingers back in midfield, one striker.</summary>
    public static Formation HarborCityBlock { get; } = new("4-5-1",
    [
        S("har-01", PlayerRole.Goalkeeper, 5, 50),
        S("har-03", PlayerRole.FullBack, 24, 14),
        S("har-05", PlayerRole.CentreBack, 16, 40),
        S("har-04", PlayerRole.CentreBack, 16, 60),
        S("har-02", PlayerRole.FullBack, 24, 86),
        S("har-06", PlayerRole.HoldingMidfielder, 34, 50),
        S("har-10", PlayerRole.CentralMidfielder, 44, 36),
        S("har-08", PlayerRole.CentralMidfielder, 44, 64),
        S("har-11", PlayerRole.WideMidfielder, 54, 16),
        S("har-09", PlayerRole.Striker, 70, 50),
        S("har-07", PlayerRole.WideMidfielder, 54, 84),
    ]);

    public static Formation Northvale4231 { get; } = new("4-2-3-1",
    [
        S("nor-01", PlayerRole.Goalkeeper, 5, 50),
        S("nor-03", PlayerRole.FullBack, 30, 12),
        S("nor-04", PlayerRole.CentreBack, 20, 38),
        S("nor-05", PlayerRole.CentreBack, 20, 62),
        S("nor-02", PlayerRole.FullBack, 30, 88),
        S("nor-08", PlayerRole.HoldingMidfielder, 40, 38),
        S("nor-06", PlayerRole.HoldingMidfielder, 40, 62),
        S("nor-11", PlayerRole.Winger, 72, 10),
        S("nor-10", PlayerRole.AttackingMidfielder, 64, 50),
        S("nor-07", PlayerRole.Winger, 72, 90),
        S("nor-09", PlayerRole.Striker, 82, 50),
    ]);

    /// <summary>
    /// Northvale chasing the game: a back three, wing-backs, Hugo Laine as a shadow striker behind two forwards.
    /// Elias Varga replaces Yusuf Demir.
    /// </summary>
    public static Formation Northvale3412 { get; } = new("3-4-1-2",
    [
        S("nor-01", PlayerRole.Goalkeeper, 5, 50),
        S("nor-04", PlayerRole.CentreBack, 20, 28),
        S("nor-05", PlayerRole.CentreBack, 17, 50),
        S("nor-02", PlayerRole.CentreBack, 20, 72),
        S("nor-11", PlayerRole.WingBack, 54, 14),
        S("nor-08", PlayerRole.CentralMidfielder, 45, 40),
        S("nor-06", PlayerRole.CentralMidfielder, 45, 60),
        S("nor-07", PlayerRole.WingBack, 54, 86),
        S("nor-10", PlayerRole.ShadowStriker, 72, 50),
        S("nor-09", PlayerRole.Striker, 84, 44),
        S("nor-19", PlayerRole.SecondStriker, 75, 58),
    ]);

    /// <summary>Northvale going 4-4-2 with two up front: Elias Varga on for Felix Moreau.</summary>
    public static Formation Northvale442 { get; } = new("4-4-2",
    [
        S("nor-01", PlayerRole.Goalkeeper, 5, 50),
        S("nor-03", PlayerRole.FullBack, 30, 12),
        S("nor-04", PlayerRole.CentreBack, 20, 38),
        S("nor-05", PlayerRole.CentreBack, 20, 62),
        S("nor-02", PlayerRole.FullBack, 30, 88),
        S("nor-11", PlayerRole.WideMidfielder, 50, 15),
        S("nor-10", PlayerRole.CentralMidfielder, 46, 40),
        S("nor-06", PlayerRole.CentralMidfielder, 46, 60),
        S("nor-07", PlayerRole.WideMidfielder, 50, 85),
        S("nor-09", PlayerRole.Striker, 80, 42),
        S("nor-19", PlayerRole.SecondStriker, 74, 60),
    ]);

    public static Substitution VargaForDemir { get; } = new("nor-03", "nor-19");

    public static Substitution VargaForMoreau { get; } = new("nor-08", "nor-19");
}
