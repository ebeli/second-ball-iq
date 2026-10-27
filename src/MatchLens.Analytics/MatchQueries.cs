using MatchLens.Contracts;

namespace MatchLens.Analytics;

/// <summary>
/// Read-only queries the agents use, shaped one-to-one like the MCP tools they will become: small typed inputs,
/// small typed outputs, event ids everywhere so every claim can be traced. The live server exposes them as MCP tools
/// at /mcp (src/MatchLens.Server/McpTools.cs).
/// </summary>
public sealed class MatchQueries(LiveMatch match)
{
    /// <summary>get_match_state: clock, score and teams.</summary>
    public MatchStateDto GetMatchState() => new(
        match.Info.MatchId,
        match.ClockSec,
        NarrativeText.Clock(match.ClockSec),
        new ScoreDto(match.Info.Home.TeamId, match.Info.Home.Name, match.Goals(match.Info.Home.TeamId)),
        new ScoreDto(match.Info.Away.TeamId, match.Info.Away.Name, match.Goals(match.Info.Away.TeamId)),
        match.Events.Count,
        match.Candidates.Count);

    /// <summary>get_window_stats: one team's numbers between two match minutes.</summary>
    public WindowStatsDto GetWindowStats(string teamId, int fromMinute, int toMinute)
    {
        ValidateTeam(teamId);
        TeamWindow w = match.Window(teamId, fromMinute * 60, toMinute * 60);
        PassQuality q = BallMetrics.Summarize(match.Events.Where(e =>
            e.TeamId == teamId && e.MatchClockSec >= fromMinute * 60 && e.MatchClockSec < toMinute * 60));
        return new WindowStatsDto(
            teamId, fromMinute, toMinute,
            w.Passes, w.CompletedPasses, Math.Round(w.PassAccuracy, 3), Math.Round(w.PossessionShare, 3),
            w.FinalThirdEntries, w.Shots.Count, Math.Round(w.Xg, 2), w.HighRecoveries.Count, w.RecoveryShots.Count,
            w.Goals.Count,
            w.HighRecoveries.Select(e => e.EventId).ToList(),
            w.Shots.Select(e => e.EventId).ToList(),
            q.AveragePassMetres, q.HardPasses, q.HardPassesCompleted, q.FastestShotKmh, q.FastestShotEventId);
    }

    /// <summary>describe_window: the neutral prose summary the triage gate reads.</summary>
    public string DescribeWindow(int fromMinute, int toMinute) => match.Text.WindowSummary(fromMinute * 60, toMinute * 60);

    /// <summary>get_events: the cited events, so any claim can be checked against the source.</summary>
    public IReadOnlyList<MatchEvent> GetEvents(IEnumerable<string> eventIds) =>
        eventIds.Select(id => match.Event(id)).OfType<MatchEvent>().ToList();

    /// <summary>get_candidates: what the detectors have raised since a given match second.</summary>
    public IReadOnlyList<Candidate> GetCandidates(int sinceSec = 0) =>
        match.Candidates.Where(c => c.DetectedAtSec >= sinceSec).ToList();

    /// <summary>get_player_stats: running numbers for one player in this match.</summary>
    public PlayerStatsDto? GetPlayerStats(string playerId)
    {
        PlayerInfo? info = match.Info.Player(playerId);
        if (info is null) return null;
        PlayerTally? t = match.Player(playerId);
        return new PlayerStatsDto(playerId, info.Name, info.ShirtNumber, info.Position,
            match.Info.Home.Players.Any(p => p.PlayerId == playerId) ? match.Info.Home.TeamId : match.Info.Away.TeamId,
            t?.Passes ?? 0, t?.CompletedPasses ?? 0, Math.Round(t?.PassAccuracy ?? 0, 3),
            t?.Shots ?? 0, Math.Round(t?.Xg ?? 0, 2), t?.Goals ?? 0, t?.HighRecoveries ?? 0,
            Math.Round(t?.AveragePassMetres ?? 0, 1), Math.Round(t?.LongestCompletedPassMetres ?? 0, 1),
            t?.HardPasses ?? 0, t?.HardPassesCompleted ?? 0, Math.Round(t?.FastestShotKmh ?? 0, 1));
    }

    /// <summary>get_team_shape: formation, average positions, roles and style between two match minutes.</summary>
    public TeamShapeDto GetTeamShape(string teamId, int fromMinute, int toMinute)
    {
        ValidateTeam(teamId);
        ShapeReport r = ShapeAnalyzer.Measure(match, teamId, fromMinute * 60, toMinute * 60);
        return new TeamShapeDto(
            teamId, fromMinute, toMinute, r.Formation, r.FitMargin,
            r.Players.Select(p => new PlayerShapeDto(p.PlayerId, p.Name, p.ShirtNumber, p.Listed.ToString(),
                p.Role is { } role ? PlayerRoles.Label(role) : null, p.Touches,
                Math.Round(p.X / 100 * Pitch.LengthMetres), p.Y < 35 ? "left" : p.Y > 65 ? "right" : "centre",
                Math.Round(p.BoxShare, 2))).ToList(),
            Math.Round(r.Style.HighPressRate, 3), Math.Round(r.Style.PressRate, 3), Math.Round(r.Style.LongBallShare, 3),
            Math.Round(r.Style.AverageDefensiveX / 100 * Pitch.LengthMetres));
    }

    private void ValidateTeam(string teamId)
    {
        if (teamId != match.Info.Home.TeamId && teamId != match.Info.Away.TeamId)
            throw new ArgumentException($"Unknown team '{teamId}'. Use {match.Info.Home.TeamId} or {match.Info.Away.TeamId}.", nameof(teamId));
    }
}

public sealed record ScoreDto(string TeamId, string Name, int Goals);

public sealed record MatchStateDto(
    string MatchId, int ClockSec, string Clock, ScoreDto Home, ScoreDto Away, int EventCount, int CandidateCount);

public sealed record WindowStatsDto(
    string TeamId, int FromMinute, int ToMinute,
    int Passes, int CompletedPasses, double PassAccuracy, double PossessionShare,
    int FinalThirdEntries, int Shots, double Xg, int HighRecoveries, int ShotsAfterHighRecovery, int Goals,
    IReadOnlyList<string> HighRecoveryEventIds, IReadOnlyList<string> ShotEventIds,
    double AveragePassMetres = 0, int HardPasses = 0, int HardPassesCompleted = 0, double FastestShotKmh = 0,
    string? FastestShotEventId = null);

public sealed record PlayerStatsDto(
    string PlayerId, string Name, int ShirtNumber, PlayerPosition Position, string TeamId,
    int Passes, int CompletedPasses, double PassAccuracy, int Shots, double Xg, int Goals, int HighRecoveries,
    double AveragePassMetres = 0, double LongestCompletedPassMetres = 0, int HardPasses = 0, int HardPassesCompleted = 0,
    double FastestShotKmh = 0);

/// <summary>A team's shape for agents and MCP clients: metres and plain words instead of pitch units.</summary>
public sealed record TeamShapeDto(
    string TeamId, int FromMinute, int ToMinute, string? Formation, double FitMargin, IReadOnlyList<PlayerShapeDto> Players,
    double HighPressRate, double PressRate, double LongBallShare, double AverageDefensiveActionMetres);

public sealed record PlayerShapeDto(
    string PlayerId, string Name, int ShirtNumber, string Listed, string? Role, int Touches, double AverageDepthMetres,
    string Side, double BoxShare);
