namespace MatchLens.Contracts;

/// <summary>
/// One frame of tracking data: where every player on the pitch and the ball were at one moment, the way an optical
/// tracking provider delivers it (typically 10 or 25 times a second).
/// <para>
/// Coordinates are metres in the home team's frame: X from 0 (the home goal line) to 105 (the away goal line), Y
/// from 0 to 68 across the pitch; the home team attacks towards X = 105 all match. Z is the ball's height in metres.
/// <see cref="T"/> is the match clock in seconds as a continuous value, so frames sit between the whole seconds of
/// the event feed.
/// </para>
/// </summary>
public sealed record TrackingFrame(double T, int Period, BallFix Ball, IReadOnlyList<PlayerFix> Players)
{
    public const string CurrentSchemaVersion = "1.0";
}

public readonly record struct BallFix(double X, double Y, double Z);

public readonly record struct PlayerFix(string PlayerId, string TeamId, double X, double Y);

/// <summary>Converts the event feed's acting-team frame (0..100, attacking towards 100) to tracking metres.</summary>
public static class TrackingFrameOf
{
    /// <summary>A point from an event of <paramref name="teamId"/> in the home frame, in metres.</summary>
    public static (double X, double Y) Metres(PitchPoint p, string teamId, MatchInfo info)
    {
        bool away = teamId == info.Away.TeamId;
        double x = away ? 100 - p.X : p.X, y = away ? 100 - p.Y : p.Y;
        return (x / 100 * Pitch.LengthMetres, y / 100 * Pitch.WidthMetres);
    }

    /// <summary>The same point back in a team's own frame (0..100, attacking towards 100).</summary>
    public static PitchPoint Own(double xMetres, double yMetres, string teamId, MatchInfo info)
    {
        double x = xMetres / Pitch.LengthMetres * 100, y = yMetres / Pitch.WidthMetres * 100;
        return teamId == info.Away.TeamId ? new PitchPoint(100 - x, 100 - y) : new PitchPoint(x, y);
    }
}
