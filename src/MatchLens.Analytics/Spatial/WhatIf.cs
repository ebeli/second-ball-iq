using MatchLens.Contracts;

namespace MatchLens.Analytics.Spatial;

/// <summary>A change to try on one team's positions in the what-if sandbox.</summary>
public enum WhatIfChange { PushUp, DropDeep, Widen, Narrow, PressTheBall }

/// <summary>
/// The result of a what-if: the team's pitch control before and after, over the whole pitch, the final third it
/// attacks and the third it defends; both control maps; and where its players were moved.
/// </summary>
public sealed record WhatIfResult(
    string TeamId, WhatIfChange Change, double Metres, double T,
    double ControlBefore, double ControlAfter, double FinalThirdBefore, double FinalThirdAfter, double OwnThirdBefore, double OwnThirdAfter,
    float[] HomeBefore, float[] HomeAfter, IReadOnlyList<MovedPlayer> Moved);

/// <summary>A player moved by the what-if, from and to (tracking metres).</summary>
public sealed record MovedPlayer(string PlayerId, double FromX, double FromY, double ToX, double ToY);

/// <summary>
/// The what-if sandbox: take the tracking frame on screen, move one team as a coach might ask (push the line up, drop
/// deep, widen, narrow, press the ball), and measure what it does to pitch control. A static picture: everyone else
/// stays where they were, and players keep their velocities. It shows how the space changes, not what would happen next.
/// </summary>
public static class WhatIf
{
    public static WhatIfResult Run(TrackingFrame frame, TrackingFrame? before, MatchInfo info, string teamId, WhatIfChange change, double metres)
    {
        bool home = teamId == info.Home.TeamId;
        double dir = home ? 1 : -1;   // the direction the team attacks along x
        List<PitchControl.Mover> movers = PitchControl.Movers(frame, before);
        float[] gridBefore = PitchControl.Compute(movers, info.Home.TeamId);

        var outfield = movers.Where(m => m.TeamId == teamId && info.Player(m.PlayerId)?.Position != PlayerPosition.Goalkeeper).ToList();
        double cy = outfield.Count > 0 ? outfield.Average(m => m.Y) : Pitch.WidthMetres / 2;
        var near = movers.Count > 0
            ? outfield.OrderBy(m => (m.X - frame.Ball.X) * (m.X - frame.Ball.X) + (m.Y - frame.Ball.Y) * (m.Y - frame.Ball.Y)).Take(3).Select(m => m.PlayerId).ToHashSet()
            : [];
        var moved = new List<MovedPlayer>();
        var after = movers.Select(m =>
        {
            if (m.TeamId != teamId || !outfield.Any(o => o.PlayerId == m.PlayerId)) return m;
            (double x, double y) = change switch
            {
                WhatIfChange.PushUp => (m.X + dir * metres, m.Y),
                WhatIfChange.DropDeep => (m.X - dir * metres, m.Y),
                WhatIfChange.Widen => (m.X, cy + (m.Y - cy) * (1 + metres / 20)),
                WhatIfChange.Narrow => (m.X, cy + (m.Y - cy) * Math.Max(0.3, 1 - metres / 20)),
                _ => near.Contains(m.PlayerId) ? Toward(m.X, m.Y, frame.Ball.X, frame.Ball.Y, metres) : (m.X, m.Y),
            };
            if (Math.Abs(x - m.X) < 0.01 && Math.Abs(y - m.Y) < 0.01) return m;   // untouched (a player off the pitch stays off it)
            x = Math.Clamp(x, 0.5, Pitch.LengthMetres - 0.5);
            y = Math.Clamp(y, 0.5, Pitch.WidthMetres - 0.5);
            if (Math.Abs(x - m.X) > 0.01 || Math.Abs(y - m.Y) > 0.01)
                moved.Add(new MovedPlayer(m.PlayerId, Math.Round(m.X, 1), Math.Round(m.Y, 1), Math.Round(x, 1), Math.Round(y, 1)));
            return m with { X = x, Y = y };
        }).ToList();
        float[] gridAfter = PitchControl.Compute(after, info.Home.TeamId);

        double Whole(float[] g) => PitchControl.Share(g, home, 0, 100);
        double Final(float[] g) => PitchControl.Share(g, home, 200.0 / 3, 100);
        double Own(float[] g) => PitchControl.Share(g, home, 0, 100.0 / 3);
        return new WhatIfResult(teamId, change, metres, frame.T,
            R(Whole(gridBefore)), R(Whole(gridAfter)), R(Final(gridBefore)), R(Final(gridAfter)), R(Own(gridBefore)), R(Own(gridAfter)),
            gridBefore, gridAfter, moved);
    }

    private static double R(double v) => Math.Round(v, 3);

    private static (double, double) Toward(double x, double y, double tx, double ty, double metres)
    {
        double dx = tx - x, dy = ty - y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 0.5) return (x, y);
        double step = Math.Min(metres, d - 1);
        return (x + dx / d * step, y + dy / d * step);
    }
}
