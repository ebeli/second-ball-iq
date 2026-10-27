using MatchLens.Contracts;

namespace MatchLens.Analytics.Spatial;

/// <summary>
/// Expected threat (xT): how likely a team is to score within its next few actions from where it has the ball. The
/// pitch is a 16 × 12 grid in the attacking team's frame (x 0 at its own goal line, 100 at the opponent's; y 0 to 100
/// across). Each cell's value is the chance of scoring from there by shooting now, or by moving the ball on and
/// scoring from where it goes:
/// <c>xT(z) = s(z)·g(z) + m(z)·Σ T(z→z')·xT(z')</c>, solved by iteration (the standard formulation of expected threat).
/// <para>
/// The grid is fitted from our own simulated matches (<see cref="Fit"/>, written to <see cref="XtGrid"/> by
/// <c>forge xt</c>), not borrowed from published real-football values: with a real event feed the same fit runs on a
/// season of real matches.
/// </para>
/// </summary>
public static class ExpectedThreat
{
    public const int Cols = 16, Rows = 12;

    /// <summary>The fitted grid, row by row from y 0 (each row x 0 → 100).</summary>
    public static IReadOnlyList<double> Grid => XtGrid.Values;

    /// <summary>The cell a point falls in (attacking frame, percent).</summary>
    public static (int Col, int Row) Cell(double x, double y) =>
        (Math.Clamp((int)(x / 100 * Cols), 0, Cols - 1), Math.Clamp((int)(y / 100 * Rows), 0, Rows - 1));

    /// <summary>xT at a point (attacking frame, percent), interpolated between cell centres.</summary>
    public static double At(double x, double y, IReadOnlyList<double>? grid = null)
    {
        grid ??= Grid;
        double fx = Math.Clamp(x / 100 * Cols - 0.5, 0, Cols - 1), fy = Math.Clamp(y / 100 * Rows - 0.5, 0, Rows - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(Cols - 1, x0 + 1), y1 = Math.Min(Rows - 1, y0 + 1);
        double u = fx - x0, v = fy - y0;
        double a = grid[y0 * Cols + x0], b = grid[y0 * Cols + x1], c = grid[y1 * Cols + x0], d = grid[y1 * Cols + x1];
        return a * (1 - u) * (1 - v) + b * u * (1 - v) + c * (1 - u) * v + d * u * v;
    }

    /// <summary>xT added by moving the ball from one point to another (attacking frame, percent).</summary>
    public static double Gain(PitchPoint from, PitchPoint to) => At(to.X, to.Y) - At(from.X, from.Y);

    /// <summary>
    /// Fits the grid from matches' events (positions in each acting team's frame): per cell, how often the team shoots
    /// rather than moves the ball, the chance a shot from there scores (mean xG, steadier than goals), and where its
    /// completed passes take the ball (a failed pass ends the possession, so it moves to nothing). Then iterates the
    /// equation above until it settles.
    /// </summary>
    public static double[] Fit(IEnumerable<IReadOnlyList<MatchEvent>> matches, int iterations = 40)
    {
        int n = Cols * Rows;
        var shots = new double[n];
        var moves = new double[n];
        var xgSum = new double[n];
        var trans = new double[n, n];
        foreach (IReadOnlyList<MatchEvent> events in matches)
        {
            foreach (MatchEvent e in events)
            {
                if (e.Start is not { } s) continue;
                (int c, int r) = Cell(s.X, s.Y);
                int z = r * Cols + c;
                if (e.Type == MatchEventType.Shot)
                {
                    shots[z]++;
                    xgSum[z] += e.Xg ?? 0;
                }
                else if (e.Type == MatchEventType.Pass)
                {
                    moves[z]++;
                    if (e.Outcome == EventOutcome.Success && e.End is { } t)
                    {
                        (int c2, int r2) = Cell(t.X, t.Y);
                        trans[z, r2 * Cols + c2]++;
                    }
                }
            }
        }

        var xt = new double[n];
        for (int it = 0; it < iterations; it++)
        {
            var next = new double[n];
            for (int z = 0; z < n; z++)
            {
                double total = shots[z] + moves[z];
                if (total < 1) continue;
                double s = shots[z] / total, m = moves[z] / total;
                double g = shots[z] > 0 ? xgSum[z] / shots[z] : 0;
                double move = 0;
                if (moves[z] > 0)
                    for (int z2 = 0; z2 < n; z2++)
                        if (trans[z, z2] > 0) move += trans[z, z2] / moves[z] * xt[z2];
                next[z] = s * g + m * move;
            }
            xt = next;
        }
        // Mirror-average across the pitch's width: the two flanks are the same football, and it halves the noise.
        var sym = new double[n];
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                sym[r * Cols + c] = (xt[r * Cols + c] + xt[(Rows - 1 - r) * Cols + c]) / 2;
        return sym;
    }
}
