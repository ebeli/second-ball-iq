using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>Where one player has been on the ball over a window, and the role that suggests.</summary>
/// <param name="X">Average depth of their touches, 0..100 from their own goal.</param>
/// <param name="Y">Average width, 0..100 from their left touchline.</param>
/// <param name="BoxShare">Share of touches inside the opponent's penalty area.</param>
public sealed record PlayerShape(
    string PlayerId, string Name, int ShirtNumber, PlayerPosition Listed, int Touches, double X, double Y, double BoxShare,
    PlayerRole? Role);

/// <summary>
/// A team's shape over a window: average positions, the formation they fit best, and each player's role. Positions
/// are in the team's own frame.
/// </summary>
/// <param name="Formation">Best-fitting formation, or null when too few players had enough touches to tell.</param>
/// <param name="FitMargin">How much better the best formation fits than the runner-up (0 = a toss-up).</param>
public sealed record ShapeReport(
    string TeamId, int FromSec, int ToSec, string? Formation, double FitMargin, IReadOnlyDictionary<string, double> FitCosts,
    IReadOnlyList<PlayerShape> Players, TeamStyleNumbers Style);

/// <summary>How a team played over a window, as counts and rates.</summary>
/// <param name="HighPressures">Pressures in the opponent's half.</param>
/// <param name="OpponentPassesInOwnHalf">The opponent's passes from its own half: the chances to press high.</param>
/// <param name="Pressures">All pressures.</param>
/// <param name="OpponentPasses">All the opponent's passes.</param>
/// <param name="LongPasses">Passes the data marks as long balls.</param>
public sealed record TeamStyleNumbers(
    int HighPressures, int OpponentPassesInOwnHalf, int Pressures, int OpponentPasses, int LongPasses, int Passes,
    double AverageDefensiveX)
{
    public double HighPressRate => OpponentPassesInOwnHalf == 0 ? 0 : (double)HighPressures / OpponentPassesInOwnHalf;
    public double PressRate => OpponentPasses == 0 ? 0 : (double)Pressures / OpponentPasses;
    public double LongBallShare => Passes == 0 ? 0 : (double)LongPasses / Passes;
}

/// <summary>
/// Reads a team's shape from its touches. A touch is an action on the ball (pass, shot, pressure, tackle,
/// interception, foul) and is located where it happened, so a player's average touch position says where they
/// play. The formation is the template whose positions match the players' averages best after both are
/// standardised, with an optimal one-to-one assignment of players to positions. Tuned on 30 seeds per shape in the
/// Python prototype (Oct 17): the right formation 97-98% of the time from 30-minute windows.
/// </summary>
public static class ShapeAnalyzer
{
    /// <summary>Touches a player needs in the window before a role is given.</summary>
    public const int MinTouchesForRole = 12;

    /// <summary>Touches a player needs to count towards the formation.</summary>
    public const int MinTouchesForFormation = 3;

    /// <summary>
    /// Templates in the team's frame (depth, width). Variants that differ only in how deep the wide players sit
    /// (4-3-3 and 4-5-1) look the same once standardised, so only one of each family is kept.
    /// </summary>
    public static IReadOnlyDictionary<string, (double X, double Y)[]> Templates { get; } = new Dictionary<string, (double, double)[]>
    {
        ["4-3-3"] = [(25, 12), (20, 37), (20, 63), (25, 88), (42, 50), (52, 32), (52, 68), (72, 15), (80, 50), (72, 85)],
        ["4-2-3-1"] = [(25, 12), (20, 37), (20, 63), (25, 88), (42, 38), (42, 62), (64, 50), (70, 12), (82, 50), (70, 88)],
        ["4-4-2"] = [(25, 12), (20, 37), (20, 63), (25, 88), (50, 15), (46, 40), (46, 60), (50, 85), (78, 40), (78, 60)],
        ["3-5-2"] = [(20, 28), (17, 50), (20, 72), (55, 10), (55, 90), (45, 35), (45, 65), (58, 50), (80, 42), (80, 58)],
        ["3-4-1-2"] = [(20, 28), (17, 50), (20, 72), (55, 10), (55, 90), (45, 40), (45, 60), (64, 50), (82, 44), (76, 58)],
        ["3-4-3"] = [(20, 28), (17, 50), (20, 72), (52, 10), (52, 90), (45, 40), (45, 60), (72, 18), (82, 50), (72, 82)],
    };

    private static readonly Dictionary<string, (double X, double Y)[]> StandardTemplates =
        Templates.ToDictionary(t => t.Key, t => Standardise(t.Value));

    public static bool IsTouch(MatchEvent e) =>
        e.Start is not null && e.PlayerId is not null && e.Type is MatchEventType.Pass or MatchEventType.Shot
            or MatchEventType.Pressure or MatchEventType.Tackle or MatchEventType.Interception or MatchEventType.Foul;

    public static ShapeReport Measure(LiveMatch match, string teamId, int fromSec, int toSec)
    {
        TeamInfo team = match.Info.Team(teamId);
        string opponent = match.Info.OpponentOf(teamId);
        var touches = new Dictionary<string, List<PitchPoint>>();
        int highPressures = 0, pressures = 0, oppOwnHalf = 0, oppPasses = 0, longPasses = 0, passes = 0;
        double defX = 0;
        int defN = 0;

        foreach (MatchEvent e in match.Between(fromSec, toSec))
        {
            if (e.TeamId == teamId)
            {
                if (IsTouch(e))
                {
                    if (!touches.TryGetValue(e.PlayerId!, out List<PitchPoint>? list)) touches[e.PlayerId!] = list = [];
                    list.Add(e.Start!.Value);
                }
                if (e.Type == MatchEventType.Pressure)
                {
                    pressures++;
                    if (e.Start!.Value.X >= 50) highPressures++;
                }
                if (e.Type is MatchEventType.Pressure or MatchEventType.Tackle or MatchEventType.Interception && e.Start is { } d)
                {
                    defX += d.X;
                    defN++;
                }
                if (e.Type == MatchEventType.Pass)
                {
                    passes++;
                    if (e.LongBall == true) longPasses++;
                }
            }
            else if (e.TeamId == opponent && e.Type == MatchEventType.Pass)
            {
                oppPasses++;
                if (e.Start is { } s && s.X < 50) oppOwnHalf++;
            }
        }

        HashSet<string> onPitch = match.OnPitch(teamId, toSec);
        var players = new List<PlayerShape>();
        foreach ((string id, List<PitchPoint> pts) in touches)
        {
            PlayerInfo? info = team.Players.FirstOrDefault(p => p.PlayerId == id);
            if (info is null) continue;
            double x = pts.Average(p => p.X), y = pts.Average(p => p.Y);
            double box = (double)pts.Count(p => p.X >= Pitch.BoxStartX && p.Y >= Pitch.BoxMinY && p.Y <= Pitch.BoxMaxY) / pts.Count;
            players.Add(new PlayerShape(id, info.Name, info.ShirtNumber, info.Position, pts.Count,
                Math.Round(x, 1), Math.Round(y, 1), Math.Round(box, 3), null));
        }

        // Roles need the other central forwards (a second striker is defined against the main one).
        players = players.Select(p => p with { Role = p.Touches >= MinTouchesForRole ? Classify(p, players) : null }).ToList();

        List<PlayerShape> outfield = players
            .Where(p => p.Listed != PlayerPosition.Goalkeeper && onPitch.Contains(p.PlayerId) && p.Touches >= MinTouchesForFormation)
            .ToList();
        string? formation = null;
        double margin = 0;
        var costs = new Dictionary<string, double>();
        if (outfield.Count >= 8)
        {
            (double X, double Y)[] cents = Standardise(outfield.Select(p => (p.X, p.Y)).ToArray());
            foreach ((string name, (double X, double Y)[] t) in StandardTemplates)
                costs[name] = Math.Round(Assignment.Cost(cents, t) * 10.0 / cents.Length, 3);
            List<KeyValuePair<string, double>> sorted = costs.OrderBy(c => c.Value).ToList();
            formation = sorted[0].Key;
            margin = Math.Round(sorted[1].Value - sorted[0].Value, 3);
        }

        var style = new TeamStyleNumbers(highPressures, oppOwnHalf, pressures, oppPasses, longPasses, passes,
            defN == 0 ? 0 : Math.Round(defX / defN, 1));
        return new ShapeReport(teamId, fromSec, toSec, formation, margin, costs,
            players.OrderBy(p => p.Listed).ThenBy(p => p.X).ToList(), style);
    }

    /// <summary>
    /// The role a player's touches suggest, given their listed position. Thresholds from the prototype; centre-backs
    /// in a back three sit wider than in a back four, so "central" for a defender is within 25 of the middle.
    /// </summary>
    public static PlayerRole Classify(PlayerShape p, IReadOnlyList<PlayerShape> team)
    {
        double wide = Math.Abs(p.Y - 50);
        switch (p.Listed)
        {
            case PlayerPosition.Goalkeeper:
                return PlayerRole.Goalkeeper;
            case PlayerPosition.Defender:
                if (wide < 25 && p.X < 35) return PlayerRole.CentreBack;
                return p.X >= 45 ? PlayerRole.WingBack : PlayerRole.FullBack;
            case PlayerPosition.Midfielder:
                if (wide >= 20) return p.X >= 60 ? PlayerRole.Winger : PlayerRole.WideMidfielder;
                if (p.BoxShare >= 0.25 && p.X >= 66) return PlayerRole.ShadowStriker;
                if (p.X < 46) return PlayerRole.HoldingMidfielder;
                return p.X >= 58 ? PlayerRole.AttackingMidfielder : PlayerRole.CentralMidfielder;
            default:
                if (wide >= 24) return p.X >= 60 ? PlayerRole.Winger : p.X >= 45 ? PlayerRole.WingBack : PlayerRole.FullBack;
                if (wide >= 13) return p.X >= 58 ? PlayerRole.InvertedWinger : PlayerRole.WideMidfielder;
                // Only a genuinely central forward ahead of this one makes it a second striker; an inverted winger
                // drifting inside does not.
                bool strikerAhead = team.Any(o => o.PlayerId != p.PlayerId && o.Listed == PlayerPosition.Forward && o.Touches >= 8 &&
                                                  Math.Abs(o.Y - 50) < 10 && o.X - p.X >= 4);
                if (strikerAhead) return PlayerRole.SecondStriker;
                return p.X < 69 ? PlayerRole.FalseNine : PlayerRole.Striker;
        }
    }

    private static (double X, double Y)[] Standardise((double X, double Y)[] points)
    {
        double mx = points.Average(p => p.X), my = points.Average(p => p.Y);
        double sx = Math.Sqrt(points.Average(p => (p.X - mx) * (p.X - mx))) + 1e-9;
        double sy = Math.Sqrt(points.Average(p => (p.Y - my) * (p.Y - my))) + 1e-9;
        return points.Select(p => ((p.X - mx) / sx, (p.Y - my) / sy)).ToArray();
    }
}

/// <summary>Minimum-cost one-to-one assignment (Hungarian algorithm), for up to a few dozen points.</summary>
public static class Assignment
{
    /// <summary>Total squared distance of the best matching of every point to a different template position.</summary>
    public static double Cost((double X, double Y)[] points, (double X, double Y)[] template)
    {
        int n = Math.Max(points.Length, template.Length);
        var cost = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        {
            // Missing players (fewer points than positions) match any position for free.
            if (i >= points.Length || j >= template.Length) { cost[i, j] = 0; continue; }
            double dx = points[i].X - template[j].X, dy = points[i].Y - template[j].Y;
            cost[i, j] = dx * dx + dy * dy;
        }
        int[] match = Solve(cost, n);
        double total = 0;
        for (int i = 0; i < n; i++) total += cost[i, match[i]];
        return total;
    }

    /// <summary>Classic O(n³) Hungarian method with potentials; returns the column assigned to each row.</summary>
    private static int[] Solve(double[,] a, int n)
    {
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = new double[n + 1];
            var used = new bool[n + 1];
            Array.Fill(minv, double.PositiveInfinity);
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.PositiveInfinity;
                for (int j = 1; j <= n; j++)
                {
                    if (used[j]) continue;
                    double cur = a[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (int j = 0; j <= n; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }
        var result = new int[n];
        for (int j = 1; j <= n; j++) if (p[j] > 0) result[p[j] - 1] = j - 1;
        return result;
    }
}
