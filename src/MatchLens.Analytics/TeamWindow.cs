using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>One team's numbers over a window of the match. Possession share = share of completed passes.</summary>
public sealed record TeamWindow(
    string TeamId,
    int FromSec,
    int ToSec,
    int Passes,
    int CompletedPasses,
    double PossessionShare,
    int FinalThirdEntries,
    IReadOnlyList<MatchEvent> FinalThirdEntryEvents,
    IReadOnlyList<MatchEvent> Shots,
    double Xg,
    IReadOnlyList<MatchEvent> HighRecoveries,
    IReadOnlyList<RecoveryShot> RecoveryShots,
    IReadOnlyList<MatchEvent> Goals)
{
    public double PassAccuracy => Passes == 0 ? 0 : (double)CompletedPasses / Passes;

    public static TeamWindow Measure(LiveMatch match, string teamId, int fromSec, int toSec, int chanceMaxSec)
    {
        IReadOnlyList<MatchEvent> all = match.Events;
        string opponentId = match.Info.OpponentOf(teamId);
        var window = all.Where(e => e.MatchClockSec >= fromSec && e.MatchClockSec < toSec).ToList();

        int completed = window.Count(e => IsCompletedPass(e) && e.TeamId == teamId);
        int opponentCompleted = window.Count(e => IsCompletedPass(e) && e.TeamId == opponentId);
        var recoveries = window.Where(e => e.TeamId == teamId && Pitch.IsHighRecovery(e)).ToList();
        var entries = window.Where(e => e.TeamId == teamId && IsFinalThirdEntry(e)).ToList();
        var shots = window.Where(e => e.Type == MatchEventType.Shot && e.TeamId == teamId).ToList();

        return new TeamWindow(
            teamId, fromSec, toSec,
            Passes: window.Count(e => e.Type == MatchEventType.Pass && e.TeamId == teamId),
            CompletedPasses: completed,
            PossessionShare: completed + opponentCompleted == 0 ? 0.5 : (double)completed / (completed + opponentCompleted),
            FinalThirdEntries: entries.Count,
            FinalThirdEntryEvents: entries,
            Shots: shots,
            Xg: shots.Sum(e => e.Xg ?? 0),
            HighRecoveries: recoveries,
            RecoveryShots: recoveries.Select(r => match.ShotAfterRecovery(r, chanceMaxSec)).OfType<RecoveryShot>().ToList(),
            Goals: window.Where(e => e.Type == MatchEventType.Goal && e.TeamId == teamId).ToList());
    }

    public static bool IsCompletedPass(MatchEvent e) => e.Type == MatchEventType.Pass && e.Outcome == EventOutcome.Success;

    /// <summary>A completed pass from outside into the attacking third.</summary>
    public static bool IsFinalThirdEntry(MatchEvent e) =>
        IsCompletedPass(e) && e.Start is { } s && e.End is { } t &&
        s.X < Pitch.AttackingThirdStart && t.X >= Pitch.AttackingThirdStart;
}

/// <summary>A high recovery and the shot that followed it, with no opponent possession in between.</summary>
public sealed record RecoveryShot(MatchEvent Recovery, MatchEvent Shot, IReadOnlyList<MatchEvent> Chain)
{
    public int Seconds => Shot.MatchClockSec - Recovery.MatchClockSec;
}
