using MatchLens.Contracts;

namespace MatchLens.Forge;

/// <summary>Full-match totals per team, used to check that generated matches look like real football.</summary>
public sealed record TeamTotals(
    string TeamId, int Goals, int Passes, double PassCompletion, double PossessionShare, int Shots, double Xg,
    int HighRecoveries, int Possessions);

public static class MatchStats
{
    /// <summary>
    /// Approximate per-team ranges for a men's top-flight match, used only as a sanity check on the generator.
    /// They are rough rules of thumb, not sourced league averages.
    /// </summary>
    public static IReadOnlyDictionary<string, (double Low, double High)> TypicalRanges { get; } =
        new Dictionary<string, (double, double)>
        {
            ["passes"] = (300, 700),
            ["passCompletion"] = (0.70, 0.90),
            ["shots"] = (5, 22),
            ["xg"] = (0.3, 3.0),
            ["possessions"] = (80, 130),
        };

    public static IReadOnlyList<TeamTotals> Totals(MatchRecording recording)
    {
        IReadOnlyList<MatchEvent> events = recording.Events;
        string home = recording.Match.Home.TeamId, away = recording.Match.Away.TeamId;
        int homeCompleted = CompletedPasses(events, home), awayCompleted = CompletedPasses(events, away);
        int total = Math.Max(1, homeCompleted + awayCompleted);

        return [Team(home, homeCompleted), Team(away, awayCompleted)];

        TeamTotals Team(string teamId, int completed)
        {
            int passes = events.Count(e => e.Type == MatchEventType.Pass && e.TeamId == teamId);
            return new TeamTotals(
                teamId,
                Goals: events.Count(e => e.Type == MatchEventType.Goal && e.TeamId == teamId),
                Passes: passes,
                PassCompletion: passes == 0 ? 0 : (double)completed / passes,
                PossessionShare: (double)completed / total,
                Shots: events.Count(e => e.Type == MatchEventType.Shot && e.TeamId == teamId),
                Xg: events.Where(e => e.Type == MatchEventType.Shot && e.TeamId == teamId).Sum(e => e.Xg ?? 0),
                HighRecoveries: events.Count(e => e.TeamId == teamId && Pitch.IsHighRecovery(e)),
                Possessions: events.Count(e =>
                    (e.Type is MatchEventType.PossessionChange or MatchEventType.KickOff && e.TeamId == teamId)));
        }
    }

    private static int CompletedPasses(IReadOnlyList<MatchEvent> events, string teamId) =>
        events.Count(e => e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success && e.TeamId == teamId);
}
