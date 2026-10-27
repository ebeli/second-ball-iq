using System.Globalization;
using System.Text;
using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// A team changes how it plays: a new structure (formation), dropping into a low block, or going direct. Shape is
/// read every few minutes over a sliding window; a new formation must beat the established one by a margin in two
/// checks running, and a style change must be a statistically clear break from the match so far. Each shift is
/// linked to what came just before it: a substitution, a goal or half-time.
/// </summary>
public sealed class TacticalShiftDetector : IDetector
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, TeamState> _teams = new(StringComparer.Ordinal);
    private int _nextCheckSec = -1;

    private sealed class TeamState
    {
        public ShapeReport? Established;
        public ShapeReport? First;
        public string? Pending;
        public int Streak;
        public int DeeperSince, DirectSince;
        public int LastDeeper = int.MinValue / 2, LastDirect = int.MinValue / 2;
    }

    public IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e) => [];

    public IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec)
    {
        DetectorSettings s = match.Settings;
        if (_nextCheckSec < 0) _nextCheckSec = s.ShapeWindowSec;
        if (nowSec < _nextCheckSec) yield break;
        _nextCheckSec = nowSec + s.ShapeCheckEverySec;

        foreach (string teamId in new[] { match.Info.Home.TeamId, match.Info.Away.TeamId })
        {
            if (!_teams.TryGetValue(teamId, out TeamState? st)) _teams[teamId] = st = new TeamState();
            ShapeReport now = ShapeAnalyzer.Measure(match, teamId, nowSec - s.ShapeWindowSec, nowSec);

            if (Formation(match, teamId, now, st, nowSec) is { } formation) yield return formation;
            if (Deeper(match, teamId, nowSec, st) is { } deeper) yield return deeper;
            if (Direct(match, teamId, nowSec, st) is { } direct) yield return direct;
        }
    }

    // --------------------------------------------------------------------------------------------------------
    // Formation
    // --------------------------------------------------------------------------------------------------------

    private static Candidate? Formation(LiveMatch match, string teamId, ShapeReport now, TeamState st, int nowSec)
    {
        DetectorSettings s = match.Settings;
        if (now.Formation is null) return null;
        // The starting shape is only settled once two windows running agree, so one noisy first read is not later
        // reported as a "change".
        if (st.Established is null)
        {
            if (st.First?.Formation == now.Formation) st.Established = now;
            st.First = now;
            return null;
        }

        string old = st.Established.Formation!;
        bool better = now.Formation != old && now.FitCosts[now.Formation] + s.FormationSwitchMargin < now.FitCosts[old];
        if (!better)
        {
            st.Streak = 0;
            if (now.Formation == old) st.Established = now;   // keep the latest confirming window as "before"
            return null;
        }
        st.Streak = now.Formation == st.Pending ? st.Streak + 1 : 1;
        st.Pending = now.Formation;
        if (st.Streak < s.FormationConfirmChecks) return null;

        ShapeReport before = st.Established;
        st.Established = now;
        st.Streak = 0;
        (int d0, int f0) = Lines(old);
        (int d1, int f1) = Lines(now.Formation);
        Cause cause = FindCause(match, teamId, before.ToSec - 5 * 60, nowSec);
        // Coaches change shape at a substitution, after a goal or at half-time. A change with none of those before
        // it is more likely noise in the averages: the new reading becomes the baseline without a report.
        if (cause.Kind == "none") return null;
        string team = match.Info.Team(teamId).Name;

        var facts = new Dictionary<string, double>
        {
            ["formationChange"] = 1,
            ["defendersBefore"] = d0,
            ["defendersAfter"] = d1,
            ["forwardsBefore"] = f0,
            ["forwardsAfter"] = f1,
            ["windowMinutes"] = s.ShapeWindowSec / 60,
        };
        var sb = new StringBuilder();
        sb.Append(Inv, $"{team}'s average positions from {Clock(now.FromSec)} to {Clock(now.ToSec)} match a {now.Formation} ");
        sb.Append(Inv, $"(fit {now.FitCosts[now.Formation]:0.0} against {now.FitCosts[old]:0.0} for {old}; lower is better). ");
        sb.Append(Inv, $"From {Clock(before.FromSec)} to {Clock(before.ToSec)} they matched a {old}.\n");
        sb.Append(Inv, $"That is {d0} defenders and {Count(f0, "forward", "forwards")} before, {d1} defenders and {Count(f1, "forward", "forwards")} now.\n");
        AppendMoves(sb, match, before, now);
        sb.Append(cause.Text);

        var tags = new Dictionary<string, string>
        {
            ["shift"] = "formation", ["formationBefore"] = old, ["formationAfter"] = now.Formation,
            ["beforeFromSec"] = before.FromSec.ToString(Inv), ["beforeToSec"] = before.ToSec.ToString(Inv),
        };
        AddCause(tags, cause);
        return Detectors.Create(match, InsightKind.TacticalShift, teamId, null, nowSec, before.FromSec, nowSec,
            cause.Events, facts, sb.ToString(), tags);
    }

    /// <summary>Players whose role changed between the two windows, in plain words.</summary>
    private static void AppendMoves(StringBuilder sb, LiveMatch match, ShapeReport before, ShapeReport now)
    {
        var moves = new List<string>();
        foreach (PlayerShape p in now.Players.Where(p => p.Role is not null && p.Listed != PlayerPosition.Goalkeeper))
        {
            PlayerShape? was = before.Players.FirstOrDefault(b => b.PlayerId == p.PlayerId);
            if (was?.Role is null)
                moves.Add($"{p.Name} came into the side as {Article(PlayerRoles.Label(p.Role!.Value))}");
            else if (was.Role != p.Role)
                moves.Add($"{p.Name} moved from {PlayerRoles.Label(was.Role.Value)} to {PlayerRoles.Label(p.Role!.Value)}");
        }
        if (moves.Count > 0) sb.Append("Role changes: ").Append(string.Join("; ", moves)).Append(".\n");
    }

    // --------------------------------------------------------------------------------------------------------
    // Style: a low block, and long balls
    // --------------------------------------------------------------------------------------------------------

    private static Candidate? Deeper(LiveMatch match, string teamId, int nowSec, TeamState st)
    {
        DetectorSettings s = match.Settings;
        int split = nowSec - s.ShapeWindowSec;
        if (nowSec < s.StyleFirstCheckSec || split - st.DeeperSince < s.StyleMinBaselineSec) return null;
        if (nowSec - st.LastDeeper < s.ShiftCooldownSec) return null;
        TeamStyleNumbers a = ShapeAnalyzer.Measure(match, teamId, st.DeeperSince, split).Style;
        TeamStyleNumbers b = ShapeAnalyzer.Measure(match, teamId, split, nowSec).Style;
        double zHigh = Stats.TwoProportionZ(a.HighPressures, a.OpponentPassesInOwnHalf, b.HighPressures, b.OpponentPassesInOwnHalf);
        double zAll = Stats.TwoProportionZ(a.Pressures, a.OpponentPasses, b.Pressures, b.OpponentPasses);
        if (zHigh < s.DeeperMinHighPressZ || zAll < s.DeeperMinPressZ) return null;

        int baselineFrom = st.DeeperSince;
        st.DeeperSince = split;
        Cause cause = FindCause(match, teamId, split - 5 * 60, nowSec);
        if (cause.Kind == "none") return null;
        st.LastDeeper = nowSec;
        string team = match.Info.Team(teamId).Name, opp = match.Info.Team(match.Info.OpponentOf(teamId)).Name;
        var facts = new Dictionary<string, double>
        {
            ["droppedDeeper"] = 1,
            ["highPressRateBefore"] = Math.Round(a.HighPressRate, 3),
            ["highPressRateAfter"] = Math.Round(b.HighPressRate, 3),
            ["highPressuresAfter"] = b.HighPressures,
            ["pressuresAfter"] = b.Pressures,
            ["pressuresBefore"] = a.Pressures,
            ["windowMinutes"] = s.ShapeWindowSec / 60,
        };
        var sb = new StringBuilder();
        sb.Append(Inv, $"From {Clock(baselineFrom)} to {Clock(split)} {team} pressed {Percent(a.HighPressRate)} of {opp}'s passes in {opp}'s own half ");
        sb.Append(Inv, $"({a.HighPressures} pressures in the opponent's half, {a.Pressures} in all).\n");
        sb.Append(Inv, $"From {Clock(split)} to {Clock(nowSec)} they pressed {Percent(b.HighPressRate)} of them ({b.HighPressures} pressures in the opponent's half, {b.Pressures} in all): ");
        sb.Append("they have stopped pressing up the pitch and are defending from their own half, a low block.\n");
        if (a.AverageDefensiveX > 0 && b.AverageDefensiveX > 0)
            sb.Append(Inv, $"Their defensive actions moved from an average of {Metres(a.AverageDefensiveX)} m to {Metres(b.AverageDefensiveX)} m from their own goal.\n");
        sb.Append(cause.Text);
        var tags = new Dictionary<string, string> { ["shift"] = "deeper", ["beforeFromSec"] = baselineFrom.ToString(Inv), ["beforeToSec"] = split.ToString(Inv) };
        AddCause(tags, cause);
        return Detectors.Create(match, InsightKind.TacticalShift, teamId, null, nowSec, split, nowSec, cause.Events.Where(e => e.MatchClockSec >= split),
            facts, sb.ToString(), tags);
    }

    private static Candidate? Direct(LiveMatch match, string teamId, int nowSec, TeamState st)
    {
        DetectorSettings s = match.Settings;
        int split = nowSec - s.ShapeWindowSec;
        if (nowSec < s.StyleFirstCheckSec || split - st.DirectSince < s.StyleMinBaselineSec) return null;
        if (nowSec - st.LastDirect < s.ShiftCooldownSec) return null;
        TeamStyleNumbers a = ShapeAnalyzer.Measure(match, teamId, st.DirectSince, split).Style;
        TeamStyleNumbers b = ShapeAnalyzer.Measure(match, teamId, split, nowSec).Style;
        double z = Stats.TwoProportionZ(b.LongPasses, b.Passes, a.LongPasses, a.Passes);
        if (z < s.DirectMinZ || b.LongBallShare < s.DirectMinLongBallShare) return null;

        int baselineFrom = st.DirectSince;
        st.DirectSince = split;
        Cause cause = FindCause(match, teamId, split - 5 * 60, nowSec);
        if (cause.Kind == "none") return null;
        st.LastDirect = nowSec;
        string team = match.Info.Team(teamId).Name;
        var facts = new Dictionary<string, double>
        {
            ["wentDirect"] = 1,
            ["longBallShareBefore"] = Math.Round(a.LongBallShare, 3),
            ["longBallShareAfter"] = Math.Round(b.LongBallShare, 3),
            ["longBallsAfter"] = b.LongPasses,
            ["passesAfter"] = b.Passes,
            ["windowMinutes"] = s.ShapeWindowSec / 60,
        };
        List<MatchEvent> longBalls = match.Between(split, nowSec).Where(e => e.TeamId == teamId && e.LongBall == true).Take(4).ToList();
        var sb = new StringBuilder();
        sb.Append(Inv, $"From {Clock(baselineFrom)} to {Clock(split)} {Percent(a.LongBallShare)} of {team}'s passes were long balls ({a.LongPasses} of {a.Passes}).\n");
        sb.Append(Inv, $"From {Clock(split)} to {Clock(nowSec)} it was {Percent(b.LongBallShare)} ({b.LongPasses} of {b.Passes}): they are going direct, ");
        sb.Append("playing it long over the midfield instead of building through it.\n");
        if (longBalls.Count > 0) sb.Append("Examples: ").Append(string.Join(", ", longBalls.Select(e => $"{e.EventId} ({Clock(e.MatchClockSec)})"))).Append(".\n");
        sb.Append(cause.Text);
        var tags = new Dictionary<string, string> { ["shift"] = "direct", ["beforeFromSec"] = baselineFrom.ToString(Inv), ["beforeToSec"] = split.ToString(Inv) };
        AddCause(tags, cause);
        return Detectors.Create(match, InsightKind.TacticalShift, teamId, null, nowSec, split, nowSec,
            cause.Events.Where(e => e.MatchClockSec >= split).Concat(longBalls), facts, sb.ToString(), tags);
    }

    // --------------------------------------------------------------------------------------------------------
    // Cause: what happened just before the change
    // --------------------------------------------------------------------------------------------------------

    private sealed record Cause(string Kind, int? AtSec, string Text, IReadOnlyList<MatchEvent> Events);

    /// <summary>
    /// The most likely trigger in [fromSec, toSec]: this team's substitution, then a goal (and whether the team was
    /// then ahead or behind), then half-time. Stated as what happened before, never as a proven reason.
    /// </summary>
    private static Cause FindCause(LiveMatch match, string teamId, int fromSec, int toSec)
    {
        string team = match.Info.Team(teamId).Name;
        List<MatchEvent> window = match.Between(Math.Max(0, fromSec), toSec + 1).ToList();
        List<MatchEvent> subs = window.Where(e => e.Type == MatchEventType.Substitution && e.TeamId == teamId).ToList();
        if (subs.Count > 0)
        {
            string list = string.Join(", ", subs.Select(e =>
                $"{match.Info.Player(e.RecipientId)?.Name} on for {match.Info.Player(e.PlayerId)?.Name} at {Clock(e.MatchClockSec)}"));
            return new Cause("substitution", subs[0].MatchClockSec, $"Before it {team} made a change: {list}. {ScoreLine(match, teamId, toSec)}\n", subs);
        }
        MatchEvent? goal = window.LastOrDefault(e => e.Type == MatchEventType.Goal);
        if (goal is not null)
        {
            bool ours = goal.TeamId == teamId;
            string scorer = match.Info.Team(goal.TeamId).Name;
            return new Cause(ours ? "goalFor" : "goalAgainst", goal.MatchClockSec,
                $"Before it, {scorer} scored at {Clock(goal.MatchClockSec)}. {ScoreLine(match, teamId, toSec)}\n", [goal]);
        }
        const int halfTime = 45 * 60;
        if (fromSec <= halfTime && halfTime <= toSec)
            return new Cause("halfTime", halfTime, $"The change came after half-time. {ScoreLine(match, teamId, toSec)}\n", []);
        return new Cause("none", null, $"No substitution or goal came just before it. {ScoreLine(match, teamId, toSec)}\n", []);
    }

    private static string ScoreLine(LiveMatch match, string teamId, int atSec)
    {
        int us = 0, them = 0;
        foreach (MatchEvent g in match.Between(0, atSec + 1).Where(e => e.Type == MatchEventType.Goal))
            if (g.TeamId == teamId) us++; else them++;
        string team = match.Info.Team(teamId).Name;
        return us > them ? $"{team} are leading {us}-{them}." : us < them ? $"{team} are trailing {us}-{them}." : $"The score is level at {us}-{them}.";
    }

    private static void AddCause(Dictionary<string, string> tags, Cause cause)
    {
        tags["cause"] = cause.Kind;
        if (cause.AtSec is { } at) tags["causeSec"] = at.ToString(Inv);
    }

    internal static (int Defenders, int Forwards) Lines(string formation)
    {
        string[] parts = formation.Split('-');
        return (int.Parse(parts[0], Inv), int.Parse(parts[^1], Inv));
    }

    private static string Clock(int sec) => NarrativeText.Clock(sec);
    private static string Percent(double share) => string.Create(Inv, $"{share * 100:0}%");
    private static string Metres(double x) => string.Create(Inv, $"{x / 100 * Pitch.LengthMetres:0}");
    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
    private static string Article(string noun) => ("aeiou".Contains(noun[0]) ? "an " : "a ") + noun;
}

/// <summary>
/// A player is playing a distinctive role: a false nine, a second or shadow striker, an inverted winger or a
/// wing-back. Read from the player's touches over the last 25 minutes, confirmed in two checks running, reported
/// once per player and role, and at most one new role per team every few minutes so the screen is not flooded.
/// </summary>
public sealed class PlayerRoleDetector : IDetector
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly HashSet<(string, PlayerRole)> _reported = [];
    private readonly Dictionary<string, PlayerRole> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _teamLast = new(StringComparer.Ordinal);
    private int _nextCheckSec = -1;

    public IEnumerable<Candidate> OnEvent(LiveMatch match, MatchEvent e) => [];

    public IEnumerable<Candidate> OnTick(LiveMatch match, int nowSec)
    {
        DetectorSettings s = match.Settings;
        if (_nextCheckSec < 0) _nextCheckSec = s.RoleFirstCheckSec;
        if (nowSec < _nextCheckSec) yield break;
        _nextCheckSec = nowSec + s.ShapeCheckEverySec;

        foreach (string teamId in new[] { match.Info.Home.TeamId, match.Info.Away.TeamId })
        {
            int from = Math.Max(0, nowSec - s.RoleWindowSec);
            ShapeReport report = ShapeAnalyzer.Measure(match, teamId, from, nowSec);
            HashSet<string> onPitch = match.OnPitch(teamId, nowSec);
            Candidate? best = null;
            foreach (PlayerShape p in report.Players.Where(p => p.Role is not null && onPitch.Contains(p.PlayerId)).OrderByDescending(p => p.Touches))
            {
                PlayerRole role = p.Role!.Value;
                bool confirmed = _lastSeen.TryGetValue(p.PlayerId, out PlayerRole last) && last == role;
                _lastSeen[p.PlayerId] = role;
                if (best is not null || !confirmed || !PlayerRoles.IsNotable(role) || p.Touches < s.RoleMinTouches) continue;
                if (_reported.Contains((p.PlayerId, role))) continue;
                if (_teamLast.TryGetValue(teamId, out int t) && nowSec - t < s.RoleTeamCooldownSec) continue;
                best = Make(match, teamId, p, report, from, nowSec);
                _reported.Add((p.PlayerId, role));
                _teamLast[teamId] = nowSec;
            }
            if (best is not null) yield return best;
        }
    }

    private static Candidate Make(LiveMatch match, string teamId, PlayerShape p, ShapeReport report, int from, int nowSec)
    {
        PlayerRole role = p.Role!.Value;
        string team = match.Info.Team(teamId).Name;
        double depthMetres = Math.Round(p.X / 100 * Pitch.LengthMetres);
        int boxTouches = (int)Math.Round(p.BoxShare * p.Touches);
        var facts = new Dictionary<string, double>
        {
            ["touches"] = p.Touches,
            ["averageDepthMetres"] = depthMetres,
            ["boxTouches"] = boxTouches,
            ["windowMinutes"] = (nowSec - from) / 60,
        };
        string listed = p.Listed.ToString().ToLowerInvariant();
        var sb = new StringBuilder();
        sb.Append(Inv, $"{p.Name} ({team}, number {p.ShirtNumber}) is listed as a {listed}.\n");
        sb.Append(Inv, $"From {NarrativeText.Clock(from)} to {NarrativeText.Clock(nowSec)}: {p.Touches} touches, on average {depthMetres:0} m from {team}'s own goal ");
        sb.Append(Inv, $"and {Side(p.Y)}; {boxTouches} of them in the opponent's penalty area.\n");
        sb.Append(Inv, $"That is the pattern of {Article(PlayerRoles.Label(role))}: {PlayerRoles.Meaning(role)}.\n");
        PlayerShape? reference = role switch
        {
            PlayerRole.FalseNine => report.Players.Where(o => o.Listed == PlayerPosition.Midfielder && o.Touches >= 10).OrderByDescending(o => o.X).FirstOrDefault(),
            PlayerRole.SecondStriker or PlayerRole.ShadowStriker => report.Players.Where(o => o.Role == PlayerRole.Striker).OrderByDescending(o => o.X).FirstOrDefault(),
            _ => null,
        };
        if (reference is not null)
            sb.Append(Inv, $"For comparison, {reference.Name} ({PlayerRoles.Label(reference.Role ?? PlayerRole.CentralMidfielder)}) averaged {Math.Round(reference.X / 100 * Pitch.LengthMetres):0} m.\n");
        if (report.Formation is not null) sb.Append(Inv, $"{team}'s shape in that time matches a {report.Formation}.\n");

        List<MatchEvent> sample = match.Between(from, nowSec)
            .Where(e => e.PlayerId == p.PlayerId && ShapeAnalyzer.IsTouch(e)).TakeLast(4).ToList();
        var tags = new Dictionary<string, string> { ["role"] = Camel(role.ToString()), ["listed"] = listed };
        if (report.Formation is not null) tags["formation"] = report.Formation;
        return Detectors.Create(match, InsightKind.PlayerRole, teamId, p.PlayerId, nowSec, from, nowSec, sample, facts, sb.ToString(), tags);
    }

    private static string Side(double y) => y < 35 ? "on the left" : y > 65 ? "on the right" : "through the middle";

    private static string Article(string noun) => ("aeiou".Contains(noun[0]) ? "an " : "a ") + noun;

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}

internal static class Stats
{
    /// <summary>
    /// z for "rate 1 is higher than rate 2" (x1 of n1 against x2 of n2), pooled; 0 when either sample is empty.
    /// </summary>
    public static double TwoProportionZ(int x1, int n1, int x2, int n2)
    {
        if (n1 == 0 || n2 == 0) return 0;
        double p = (double)(x1 + x2) / (n1 + n2);
        double se = Math.Sqrt(Math.Max(1e-9, p * (1 - p) * (1.0 / n1 + 1.0 / n2)));
        return ((double)x1 / n1 - (double)x2 / n2) / se;
    }
}
