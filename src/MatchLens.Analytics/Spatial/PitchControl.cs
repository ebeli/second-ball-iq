using MatchLens.Contracts;

namespace MatchLens.Analytics.Spatial;

/// <summary>
/// Pitch control from tracking: for every spot on the pitch, how likely each team is to get there first. A simplified
/// time-to-intercept model (after Spearman, and Fernández and Bornn): each player keeps running the way he is going
/// for a reaction time, then runs straight at the spot at top speed; the team whose quickest player arrives sooner
/// controls it, softened by the uncertainty in those arrival times:
/// <c>P(home) = 1 / (1 + exp(−π / (√3·σ) · (t_away − t_home)))</c>.
/// <para>
/// The grid is 32 × 21 cells (about 3.3 m), in the tracking frame (metres, home attacking towards x = 105). Values
/// are the home team's control, 0 to 1; the away team's is one minus that.
/// </para>
/// </summary>
public static class PitchControl
{
    public const int Cols = 32, Rows = 21;
    public const double ReactionSec = 0.7;
    public const double TopSpeed = 7.8;   // m/s, a realistic sprint for the run to a loose ball
    public const double Sigma = 0.45;     // s, uncertainty in arrival time
    private static readonly double K = Math.PI / (Math.Sqrt(3) * Sigma);

    /// <summary>A player with his velocity (metres, metres per second, tracking frame).</summary>
    public readonly record struct Mover(string PlayerId, string TeamId, double X, double Y, double Vx, double Vy);

    /// <summary>Players with velocities from two frames (the earlier one may be missing: then standing).</summary>
    public static List<Mover> Movers(TrackingFrame now, TrackingFrame? before)
    {
        Dictionary<string, PlayerFix>? prev = before?.Players.ToDictionary(p => p.PlayerId);
        double dt = before is null ? 0 : now.T - before.T;
        var list = new List<Mover>(now.Players.Count);
        foreach (PlayerFix p in now.Players)
        {
            double vx = 0, vy = 0;
            if (prev is not null && dt > 0.05 && dt < 3 && prev.TryGetValue(p.PlayerId, out PlayerFix q))
            {
                vx = (p.X - q.X) / dt;
                vy = (p.Y - q.Y) / dt;
                double v = Math.Sqrt(vx * vx + vy * vy);
                if (v > 10) { vx *= 10 / v; vy *= 10 / v; }   // a noise spike is not a 40 km/h run
            }
            list.Add(new Mover(p.PlayerId, p.TeamId, p.X, p.Y, vx, vy));
        }
        return list;
    }

    /// <summary>Time for a player to reach a spot, seconds.</summary>
    public static double Arrival(Mover p, double x, double y)
    {
        double rx = p.X + p.Vx * ReactionSec, ry = p.Y + p.Vy * ReactionSec;
        double dx = x - rx, dy = y - ry;
        return ReactionSec + Math.Sqrt(dx * dx + dy * dy) / TopSpeed;
    }

    /// <summary>The home team's control at a spot, 0 to 1.</summary>
    public static double HomeControlAt(IReadOnlyList<Mover> players, string homeTeamId, double x, double y)
    {
        double home = double.MaxValue, away = double.MaxValue;
        foreach (Mover p in players)
        {
            double t = Arrival(p, x, y);
            if (p.TeamId == homeTeamId) { if (t < home) home = t; }
            else if (t < away) away = t;
        }
        if (home == double.MaxValue) return 0;
        if (away == double.MaxValue) return 1;
        return 1 / (1 + Math.Exp(-K * (away - home)));
    }

    /// <summary>The home team's control over the whole grid, row by row from y = 0 (each row x 0 → 105).</summary>
    public static float[] Compute(IReadOnlyList<Mover> players, string homeTeamId, float[]? into = null)
    {
        float[] grid = into is { Length: Cols * Rows } ? into : new float[Cols * Rows];
        for (int r = 0; r < Rows; r++)
        {
            double y = (r + 0.5) * Pitch.WidthMetres / Rows;
            for (int c = 0; c < Cols; c++)
            {
                double x = (c + 0.5) * Pitch.LengthMetres / Cols;
                grid[r * Cols + c] = (float)HomeControlAt(players, homeTeamId, x, y);
            }
        }
        return grid;
    }

    /// <summary>
    /// A team's share of the pitch between two lines across it, in its own attacking direction: 0 to 100 percent of the
    /// length from its own goal line (so 66.7 to 100 is the final third whichever way it attacks).
    /// </summary>
    public static double Share(float[] grid, bool home, double fromPct, double toPct)
    {
        double sum = 0;
        int n = 0;
        for (int c = 0; c < Cols; c++)
        {
            double pct = (c + 0.5) / Cols * 100;
            double own = home ? pct : 100 - pct;
            if (own < fromPct || own > toPct) continue;
            for (int r = 0; r < Rows; r++) { double v = grid[r * Cols + c]; sum += home ? v : 1 - v; n++; }
        }
        return n == 0 ? 0 : sum / n;
    }
}
