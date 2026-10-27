using MatchLens.Agents;
using System.ComponentModel;
using MatchLens.Analytics;
using MatchLens.Contracts;
using ModelContextProtocol.Server;

namespace MatchLens.Server;

/// <summary>
/// The analytics engine as MCP tools, at /mcp. These are the same read-only queries the Tactical Analyst calls
/// in-process, so any MCP client (an IDE agent, a Foundry agent, another broadcaster's system) can ask the live
/// match the questions our agents ask, and get the same measured numbers with event ids to check them against.
/// </summary>
[McpServerToolType]
public sealed class MatchTools(MatchHost host)
{
    [McpServerTool(Name = "get_match_state")]
    [Description("Clock, score, teams and how many events and candidate moments the engine has seen so far.")]
    public MatchStateDto GetMatchState() => Queries().GetMatchState();

    [McpServerTool(Name = "get_window_stats")]
    [Description("Numbers for one team between two match minutes: possession share, final-third entries, shots, xG, high recoveries, average pass length, hard passes (difficulty 60 or more) and the fastest shot in km/h, with event ids.")]
    public WindowStatsDto GetWindowStats(
        [Description("Team id, e.g. team-har or team-nor. get_match_state lists them.")] string teamId,
        [Description("Start minute (inclusive).")] int fromMinute,
        [Description("End minute (exclusive).")] int toMinute) =>
        Queries().GetWindowStats(teamId, fromMinute, toMinute);

    [McpServerTool(Name = "describe_window")]
    [Description("Neutral plain-language summary of both teams between two match minutes, as the triage gate reads it.")]
    public string DescribeWindow(
        [Description("Start minute (inclusive).")] int fromMinute,
        [Description("End minute (exclusive).")] int toMinute) =>
        Queries().DescribeWindow(fromMinute, toMinute);

    [McpServerTool(Name = "get_events")]
    [Description("Raw match events by id, so any claim can be checked against the source.")]
    public IReadOnlyList<MatchEvent> GetEvents(
        [Description("Event ids such as evt-000712.")] string[] eventIds) =>
        Queries().GetEvents(eventIds.Take(50));

    [McpServerTool(Name = "get_player_stats")]
    [Description("Running numbers for one player in this match: passes, accuracy, average and longest pass in metres, hard passes completed, shots, xG, goals, high recoveries and the hardest shot in km/h.")]
    public PlayerStatsDto? GetPlayerStats(
        [Description("Player id such as nor-09.")] string playerId) =>
        Queries().GetPlayerStats(playerId);

    [McpServerTool(Name = "get_candidates")]
    [Description("Moments the detectors flagged since a match minute, with their measured facts and what the agents did with them.")]
    public IReadOnlyList<CandidateView> GetCandidates(
        [Description("Only moments detected at or after this minute.")] int sinceMinute = 0) =>
        Session().Candidates().Where(c => c.DetectedAtSec >= sinceMinute * 60).ToList();

    [McpServerTool(Name = "get_insights")]
    [Description("Published and queued insights, with their validation and publish decision.")]
    public IReadOnlyList<InsightView> GetInsights(
        [Description("live, pending, approved, rejected or dropped; empty for all.")] string? status = null,
        [Description("Persona such as Casual-en or Analyst-en; empty for all.")] string? persona = null) =>
        Session().Insights()
            .Where(i => string.IsNullOrEmpty(status) || i.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase))
            .Where(i => string.IsNullOrEmpty(persona) || i.PersonaKey.Equals(persona, StringComparison.OrdinalIgnoreCase))
            .ToList();

    [McpServerTool(Name = "get_recaps")]
    [Description("The half-time and full-time recaps written by the Recap Writer agent, one per persona.")]
    public IReadOnlyList<RecapView> GetRecaps(
        [Description("Persona such as Casual-en or Analyst-en; empty for all.")] string? persona = null) =>
        Session().Recaps().Where(r => string.IsNullOrEmpty(persona) || r.PersonaKey.Equals(persona, StringComparison.OrdinalIgnoreCase)).ToList();

    [McpServerTool(Name = "get_predictions")]
    [Description("Predictions made before set pieces and for the next goal, with probabilities, the evidence and how they turned out.")]
    public IReadOnlyList<Prediction> GetPredictions() => Session().Predictions();

    [McpServerTool(Name = "get_tactical_tests")]
    [Description("Tactical tests: each read that went to air with the measurable criterion locked when it aired, its deadline, Decision-1's estimate (a model estimate, not a calibrated probability), progress, and the settled result (met, notMet, inconclusive) with the event ids behind it.")]
    public IReadOnlyList<TacticalTest> GetTacticalTests() => Session().Tests();

    [McpServerTool(Name = "get_player_season")]
    [Description("A player's numbers over the earlier rounds of the (generated) season: matches, goals, assists, shots, xG, passes, penalties.")]
    public PlayerSeason? GetPlayerSeason([Description("Player id such as nor-09.")] string playerId) => Session().SeasonOf(playerId);

    [McpServerTool(Name = "get_team_shape")]
    [Description("A team's shape between two match minutes: best-fitting formation, each player's average position, touches and role (false nine, second striker, wing-back...), and how high and how direct the team played.")]
    public TeamShapeDto GetTeamShape(
        [Description("Team id, e.g. team-har or team-nor.")] string teamId,
        [Description("Start minute (inclusive).")] int fromMinute,
        [Description("End minute (exclusive).")] int toMinute) =>
        Queries().GetTeamShape(teamId, fromMinute, toMinute);

    [McpServerTool(Name = "get_running")]
    [Description("Players' running so far, measured from the match's tracking data (every player's position ten times a second): distance covered (km), top and current speed (km/h), sprints and high-intensity distance.")]
    public IReadOnlyList<RunnerView> GetRunning() => Session().Running();

    [McpServerTool(Name = "get_space")]
    [Description("Space and passing from tracking: each team's control of the final third (time-to-intercept pitch control, last 10 minutes and the match so far) and the players who most often missed a better-placed teammate (by expected threat, xT, of each option at the moment of the pass).")]
    public SpatialSummary? GetSpace() => Session().Space();

    [McpServerTool(Name = "get_coach_corner")]
    [Description("Decision-1 coach's corner: for each team, the two coach agents' plans, Decision-1's pick between them with a probability for each, the tactical tests that settle both, and whether the pick was the plan that held.")]
    public IReadOnlyList<CoachDecision> GetCoachCorner() => Session().Coaches();

    [McpServerTool(Name = "get_auto_events")]
    [Description("Auto-eventing: passes, shots and ball wins found from the tracking positions alone (no event feed), scored against the event feed so far: how many were found, how many were real, and the errors in pass length and ball speed. Includes the latest detections.")]
    public AutoEventView? GetAutoEvents() => Session().AutoEvents();

    [McpServerTool(Name = "ask_matchlens")]
    [Description("Ask Second Ball IQ a question about the live match in plain language. A tool-using analyst agent investigates the match frozen at this second and answers with measured facts, cited event ids and a check by Decision-1.")]
    public Task<AskResult> AskMatchLens(
        [Description("The question, e.g. 'Why did Northvale switch to a back three?'")] string question,
        [Description("en or fi")] string language = "en",
        CancellationToken cancellationToken = default) =>
        Session().AskAsync(new AskRequest(question, Audience.Analyst, language == "fi" ? "fi" : "en"), cancellationToken);

    private MatchSession Session() =>
        host.Current ?? throw new InvalidOperationException("No match is playing. Start one in the Second Ball IQ app first.");

    /// <summary>Each call reads a private copy of the match as it stands, so it never races the replay.</summary>
    private MatchQueries Queries() => new(Session().FreezeNow());
}
