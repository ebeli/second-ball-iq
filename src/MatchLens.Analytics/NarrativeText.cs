using System.Globalization;
using System.Text;
using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// Writes the plain-language texts the decision gates read. The Decision-1 spike showed both matter:
/// triage scored 18/18 on prose versus 15/18 on raw numbers, and grounding only worked once the evidence stated
/// derived facts (counts, which recovery led to which shot, what did not happen) instead of a raw event list.
/// </summary>
public sealed class NarrativeText(LiveMatch match)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Neutral description of both teams over [fromSec, toSec), for the triage gate. Identical whichever detector
    /// asked for it, so triage judges the match rather than echoing the detector.
    /// </summary>
    public string WindowSummary(int fromSec, int toSec)
    {
        MatchInfo info = match.Info;
        int minutes = Math.Max(1, (toSec - fromSec) / 60);
        var sb = new StringBuilder();
        sb.Append(Inv, $"Match minute {toSec / 60}. Score: {info.Home.Name} {match.Goals(info.Home.TeamId)}, ");
        sb.Append(Inv, $"{info.Away.Name} {match.Goals(info.Away.TeamId)}.\n");

        foreach (TeamInfo team in new[] { info.Home, info.Away })
        {
            TeamWindow now = match.Window(team.TeamId, fromSec, toSec);
            TeamWindow before = match.Window(team.TeamId, Math.Max(0, fromSec - (toSec - fromSec)), fromSec);
            TeamInfo opponent = info.Team(info.OpponentOf(team.TeamId));

            sb.Append(Inv, $"In the last {minutes} minutes {team.Name} had {Percent(now.PossessionShare)} of the completed passes ");
            sb.Append(Inv, $"({now.CompletedPasses}), made {Count(now.FinalThirdEntries, "entry", "entries")} into the final third, ");
            sb.Append(Inv, $"and had {Shots(now)}. ");
            sb.Append(Inv, $"They won the ball in {opponent.Name}'s defensive third {Times(now.HighRecoveries.Count)}");
            if (fromSec >= toSec - fromSec)   // only when a full previous window exists
                sb.Append(Inv, $" (previous {minutes} minutes: {Times(before.HighRecoveries.Count)})");
            sb.Append(now.RecoveryShots.Count > 0
                ? string.Create(Inv, $", and {Count(now.RecoveryShots.Count, "recovery", "recoveries")} led to a shot within {match.Settings.ChanceMaxSecondsAfterRecovery} seconds.\n")
                : ".\n");
        }

        var goals = match.Events.Where(e => e.Type == MatchEventType.Goal && e.MatchClockSec >= fromSec && e.MatchClockSec < toSec).ToList();
        sb.Append(goals.Count == 0
            ? "No goals in this window.\n"
            : "Goals in this window: " + string.Join("; ", goals.Select(g => $"{PlayerName(g.PlayerId)} ({TeamName(g.TeamId)}) at {Clock(g.MatchClockSec)}")) + ".\n");

        PlayerTally? standout = match.Players.OrderByDescending(p => p.CompletedPasses).FirstOrDefault();
        if (standout is not null && standout.CompletedPasses >= 40)
        {
            sb.Append(Inv, $"Most completed passes so far: {PlayerName(standout.PlayerId)} ({TeamName(standout.TeamId)}), ");
            sb.Append(Inv, $"{standout.CompletedPasses} at {Percent(standout.PassAccuracy)} accuracy.\n");
        }
        return sb.ToString().TrimEnd();
    }

    public string PressingEvidence(string teamId, TeamWindow now, TeamWindow before, TeamWindow opponent)
    {
        var sb = Header(now.FromSec, now.ToSec);
        string team = TeamName(teamId), opp = TeamName(opponent.TeamId);
        int minutes = (now.ToSec - now.FromSec) / 60;
        sb.Append("High turnovers (ball won in the opponent's defensive third, i.e. high up the pitch):\n");
        sb.Append(Inv, $"- {team}: {now.HighRecoveries.Count}");
        if (now.HighRecoveries.Count > 0)
            sb.Append(", at ").Append(string.Join(", ", now.HighRecoveries.Select(Ref)));
        sb.Append(Inv, $". {opp}: {opponent.HighRecoveries.Count}.\n");
        sb.Append(Inv, $"- {team} in the {minutes} minutes before: {before.HighRecoveries.Count}.\n");
        AppendRecoveryShots(sb, team, now);
        sb.Append(Inv, $"Possession in the window: {team} {Percent(now.PossessionShare)}, {opp} {Percent(opponent.PossessionShare)}.\n");
        AppendGoals(sb, now.FromSec, now.ToSec);
        return sb.ToString().TrimEnd();
    }

    public string PossessionEvidence(string teamId, TeamWindow now, TeamWindow opponent)
    {
        var sb = Header(now.FromSec, now.ToSec);
        string team = TeamName(teamId), opp = TeamName(opponent.TeamId);
        sb.Append(Inv, $"Completed passes: {team} {now.CompletedPasses}, {opp} {opponent.CompletedPasses}, ");
        sb.Append(Inv, $"so {team} had {Percent(now.PossessionShare)} of the possession.\n");
        sb.Append(Inv, $"{team} entries into the final third: {now.FinalThirdEntries}");
        if (now.FinalThirdEntryEvents.Count > 0)
            sb.Append(" (").Append(string.Join(", ", now.FinalThirdEntryEvents.Select(Ref))).Append(')');
        sb.Append(".\n");
        sb.Append(Inv, $"Shots: {team} {now.Shots.Count}");
        if (now.Shots.Count > 0) sb.Append(Inv, $" ({string.Join(", ", now.Shots.Select(ShotRef))})");
        sb.Append(Inv, $", total xG {now.Xg:0.00}. {opp} {opponent.Shots.Count}, total xG {opponent.Xg:0.00}.\n");
        AppendGoals(sb, now.FromSec, now.ToSec);
        return sb.ToString().TrimEnd();
    }

    public string ChanceEvidence(RecoveryShot chain)
    {
        var sb = Header(chain.Recovery.MatchClockSec, chain.Shot.MatchClockSec + 1);
        MatchEvent r = chain.Recovery, s = chain.Shot;
        string team = TeamName(r.TeamId), opp = TeamName(match.Info.OpponentOf(r.TeamId));
        int passes = chain.Chain.Count(e => e.Type == MatchEventType.Pass);
        string seconds = Count(chain.Seconds, "second", "seconds");
        string shooter = s.PlayerId == r.PlayerId ? "the same player" : PlayerName(s.PlayerId);
        sb.Append(Inv, $"{PlayerName(r.PlayerId)} ({team}) won the ball in {opp}'s defensive third, i.e. high up the pitch, at {Ref(r)}.\n");
        sb.Append(passes == 0
            ? string.Create(Inv, $"With no pass in between, {shooter} shot {seconds} after the recovery: ")
            : string.Create(Inv, $"After {Count(passes, "pass", "passes")}, {shooter} shot {seconds} after the recovery: "));
        sb.Append(Inv, $"{ShotRef(s)}.\n");
        sb.Append(Inv, $"{opp} did not regain possession in between.\n");
        // A goal is reported once its goal event has been counted, so the score here already includes it.
        sb.Append(s.ShotResult == ShotResult.Goal
            ? string.Create(Inv, $"It was a goal. Score now: {Score()}.\n")
            : string.Create(Inv, $"It was not a goal. Score: {Score()}.\n"));
        return sb.ToString().TrimEnd();
    }

    public string GoalEvidence(MatchEvent shot, MatchEvent goal, int moveStartSec, int passes, MatchEvent? assist)
    {
        var sb = Header(moveStartSec, goal.MatchClockSec + 1);
        string team = TeamName(goal.TeamId);
        int seconds = shot.MatchClockSec - moveStartSec;
        sb.Append(Inv, $"{PlayerName(shot.PlayerId)} ({team}) scored with {ShotRef(shot)}.\n");
        if (shot.SetPiece == SetPiece.Penalty) sb.Append("It was a penalty.\n");
        if (shot.SetPiece == SetPiece.FreeKick) sb.Append("It was a direct free kick.\n");
        sb.Append(passes == 0
            ? string.Create(Inv, $"There was no completed pass in the move; the shot came {Count(seconds, "second", "seconds")} after {team} had the ball.\n")
            : string.Create(Inv, $"{team} had the ball for {Count(seconds, "second", "seconds")} and completed {Count(passes, "pass", "passes")} before the shot.\n"));
        if (assist is not null)
            sb.Append(Inv, $"The final pass came from {PlayerName(assist.PlayerId)} at {Ref(assist)}.\n");
        sb.Append(Inv, $"The move did not start with a high recovery. Score now: {Score()}.\n");
        return sb.ToString().TrimEnd();
    }

    public string MilestoneEvidence(PlayerTally player, MatchEvent pass, PlayerTally? nextBest)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"Match minute {pass.MatchClockSec / 60}. Score: {Score()}.\n");
        sb.Append(Inv, $"{PlayerName(player.PlayerId)} ({TeamName(player.TeamId)}) completed pass number {player.CompletedPasses} ");
        sb.Append(Inv, $"of this match at {Ref(pass)}, from {player.Passes} attempted ({Percent(player.PassAccuracy)} accuracy).\n");
        if (nextBest is not null)
            sb.Append(Inv, $"Next most completed passes in this match: {PlayerName(nextBest.PlayerId)} ({TeamName(nextBest.TeamId)}), {nextBest.CompletedPasses}.\n");
        sb.Append("These counts cover this match only; no season or career records are available.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------

    private StringBuilder Header(int fromSec, int toSec)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"Window: match time {Clock(fromSec)} to {Clock(toSec)}. Score: {Score()}.\n");
        return sb;
    }

    private void AppendRecoveryShots(StringBuilder sb, string team, TeamWindow now)
    {
        sb.Append(Inv, $"Shots by {team} within {match.Settings.ChanceMaxSecondsAfterRecovery} seconds of a high turnover: {now.RecoveryShots.Count}.\n");
        foreach (RecoveryShot rs in now.RecoveryShots)
            sb.Append(Inv, $"- {ShotRef(rs.Shot)}, {rs.Seconds} seconds after recovery {rs.Recovery.EventId}.\n");
        var without = now.HighRecoveries.Where(r => now.RecoveryShots.All(rs => rs.Recovery.EventId != r.EventId)).ToList();
        if (without.Count > 0)
            sb.Append("- Recoveries that did not lead to a shot: ").Append(string.Join(", ", without.Select(r => r.EventId))).Append(".\n");
    }

    private void AppendGoals(StringBuilder sb, int fromSec, int toSec)
    {
        var goals = match.Events.Where(e => e.Type == MatchEventType.Goal && e.MatchClockSec >= fromSec && e.MatchClockSec < toSec).ToList();
        sb.Append(goals.Count == 0
            ? "Goals in the window: none.\n"
            : "Goals in the window: " + string.Join(", ", goals.Select(g => $"{TeamName(g.TeamId)} at {Ref(g)}")) + ".\n");
    }

    private string Score() =>
        $"{match.Info.Home.Name} {match.Goals(match.Info.Home.TeamId)}, {match.Info.Away.Name} {match.Goals(match.Info.Away.TeamId)}";

    private string TeamName(string teamId) => match.Info.Team(teamId).Name;

    private string PlayerName(string? playerId) => match.Info.Player(playerId)?.Name ?? "a player";

    private static string Ref(MatchEvent e) => $"{e.EventId} ({Clock(e.MatchClockSec)})";

    private static string ShotRef(MatchEvent s)
    {
        string result = s.ShotResult switch
        {
            ShotResult.Goal => "goal",
            ShotResult.Saved => "saved by the goalkeeper",
            ShotResult.OffTarget => "off target",
            ShotResult.Blocked => "blocked",
            _ => "shot",
        };
        return string.Create(Inv, $"{s.EventId} ({Clock(s.MatchClockSec)}), {result}, xG {s.Xg ?? 0:0.00}");
    }

    private static string Shots(TeamWindow w) =>
        w.Shots.Count == 0 ? "no shots" : string.Create(Inv, $"{Count(w.Shots.Count, "shot", "shots")} worth {w.Xg:0.00} xG");

    public static string Clock(int sec) => $"{sec / 60}:{sec % 60:D2}";

    private static string Percent(double share) => string.Create(Inv, $"{share * 100:0}%");

    private static string Times(int n) => n switch { 0 => "0 times", 1 => "once", _ => $"{n} times" };

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
