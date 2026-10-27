using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// Pass quality and ball speed, measured from each event as it arrives.
/// <list type="bullet">
/// <item><b>Pass distance</b>: metres between where the pass was played and where it was aimed, on a 105 × 68 m pitch.</item>
/// <item><b>Pass difficulty</b>: 0 to 100, from the engine's own rule (below), not from the generator. It adds up what
/// makes a pass hard to complete: length, a defender pressing the passer, how far it moves the ball forward, where it
/// is aimed (the final third, the box) and a lofted long ball. Easy under 35, medium 35 to 59, hard from 60.</item>
/// <item><b>Ball speed</b>: km/h for passes and shots, as the event feed reports it (a ball-tracking provider in a live
/// deployment; Match Forge here).</item>
/// </list>
/// The thresholds that tag a player on screen (a shot from 110 km/h, a completed pass from 40 m) are here too, so the
/// engine, the commentator and the UI agree on them.
/// </summary>
public static class BallMetrics
{
    /// <summary>A shot at or above this speed is tagged on screen and called by the commentator.</summary>
    public const double FastShotKmh = 110;

    /// <summary>A completed pass at least this long is tagged on screen.</summary>
    public const double LongPassMetres = 40;

    /// <summary>Difficulty from which a pass counts as hard.</summary>
    public const int HardPass = 60;

    /// <summary>Difficulty from which a pass counts as medium.</summary>
    public const int MediumPass = 35;

    public static bool IsPass(MatchEvent e) => e.Type == MatchEventType.Pass && e.Start is not null && e.End is not null;

    /// <summary>Length of a pass in metres, one decimal; null for anything that is not a pass with both ends.</summary>
    public static double? PassMetres(MatchEvent e) =>
        IsPass(e) ? Math.Round(Pitch.DistanceMetres(e.Start!.Value, e.End!.Value), 1) : null;

    /// <summary>Difficulty of a pass, 0 (trivial) to 100 (very hard); null for anything that is not a pass.</summary>
    public static int? PassDifficulty(MatchEvent e)
    {
        if (!IsPass(e)) return null;
        PitchPoint s = e.Start!.Value, t = e.End!.Value;
        double metres = Pitch.DistanceMetres(s, t);
        double forward = (t.X - s.X) / 100 * Pitch.LengthMetres;

        double score = 5;
        score += Math.Min(metres, 60) / 60 * 40;                        // length: up to 40
        if (e.UnderPressure == true) score += 20;                       // a defender on the passer
        score += Math.Clamp(forward, 0, 30) / 30 * 15;                  // moving the ball forward: up to 15
        if (InBox(t)) score += 15;                                      // into the box
        else if (t.X >= Pitch.AttackingThirdStart) score += 8;          // into the final third
        if (e.LongBall == true) score += 10;                            // lofted over the midfield
        return (int)Math.Round(Math.Clamp(score, 0, 100));
    }

    public static string Band(int difficulty) => difficulty >= HardPass ? "hard" : difficulty >= MediumPass ? "medium" : "easy";

    /// <summary>Ball speed of a shot, km/h; null when the event is not a shot or the feed has no speed.</summary>
    public static double? ShotKmh(MatchEvent e) => e.Type == MatchEventType.Shot ? e.BallSpeedKmh : null;

    /// <summary>A shot fast enough to tag the player on screen.</summary>
    public static bool IsFastShot(MatchEvent e) => ShotKmh(e) is { } kmh && kmh >= FastShotKmh;

    /// <summary>A completed pass long enough to tag the player on screen.</summary>
    public static bool IsLongCompletedPass(MatchEvent e) =>
        e.Outcome == EventOutcome.Success && PassMetres(e) is { } m && m >= LongPassMetres;

    /// <summary>Pass quality and speed over a set of events (one team or one player in a window).</summary>
    public static PassQuality Summarize(IEnumerable<MatchEvent> events)
    {
        int passes = 0, completed = 0, hard = 0, hardCompleted = 0;
        double metres = 0, longest = 0, hardest = 0;
        string? longestId = null, hardestId = null;
        foreach (MatchEvent e in events)
        {
            if (PassMetres(e) is { } m)
            {
                passes++;
                metres += m;
                bool ok = e.Outcome == EventOutcome.Success;
                if (ok) completed++;
                if (PassDifficulty(e) >= HardPass)
                {
                    hard++;
                    if (ok) hardCompleted++;
                }
                if (ok && m > longest) { longest = m; longestId = e.EventId; }
            }
            if (ShotKmh(e) is { } kmh && kmh > hardest) { hardest = kmh; hardestId = e.EventId; }
        }
        return new PassQuality(passes, completed, passes == 0 ? 0 : Math.Round(metres / passes, 1),
            hard, hardCompleted, Math.Round(longest, 1), longestId, Math.Round(hardest, 1), hardestId);
    }

    private static bool InBox(PitchPoint p) => p.X >= Pitch.BoxStartX && p.Y >= Pitch.BoxMinY && p.Y <= Pitch.BoxMaxY;
}

/// <summary>Pass quality and the hardest shot over some events. Zero (and null ids) where there is nothing to measure.</summary>
public sealed record PassQuality(
    int Passes, int CompletedPasses, double AveragePassMetres, int HardPasses, int HardPassesCompleted,
    double LongestCompletedPassMetres, string? LongestCompletedPassEventId, double FastestShotKmh, string? FastestShotEventId);
