using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MatchLens.Agents;

/// <summary>A fan's question, with how they like things explained and what they follow.</summary>
/// <param name="History">The last few questions and answers in this conversation, oldest first.</param>
public sealed record AskRequest(
    string Question, Audience Audience, string Language, string? TeamId = null, string? PlayerId = null,
    IReadOnlyList<AskTurn>? History = null);

public sealed record AskTurn(string Question, string Answer);

/// <summary>A moment that already went to air, as the analyst sees it.</summary>
public sealed record PublishedMoment(string Clock, string Kind, string Team, string Headline, string Summary, IReadOnlyList<string> EventIds);

[Description("A measured fact shown under the answer.")]
public sealed class AskFact
{
    [JsonPropertyName("label")] [Description("Short label, e.g. 'High regains, 46-56'.")] public string Label { get; set; } = "";
    [JsonPropertyName("value")] [Description("The value as a fan reads it, e.g. '5', '31%', '0.16 xG' or '4-2-3-1'. Never an event id.")] public string Value { get; set; } = "";
}

[Description("Something the fan can open on the pitch to see the evidence.")]
public sealed class AskAction
{
    [JsonPropertyName("kind")]
    [Description("rewind (replay events), compareShape (a team's shape in two spells), showShape (one spell), heatMap (where a team or player had the ball), openPlayer (player card).")]
    public string Kind { get; set; } = "";
    [JsonPropertyName("label")] [Description("Button text, e.g. 'Replay the three regains'.")] public string Label { get; set; } = "";
    [JsonPropertyName("eventIds")] [Description("For rewind: event ids in order.")] public List<string> EventIds { get; set; } = [];
    [JsonPropertyName("teamId")] public string? TeamId { get; set; }
    [JsonPropertyName("playerId")] public string? PlayerId { get; set; }
    [JsonPropertyName("fromMinute")] [Description("Start minute; for compareShape the first spell.")] public int? FromMinute { get; set; }
    [JsonPropertyName("toMinute")] public int? ToMinute { get; set; }
    [JsonPropertyName("fromMinute2")] [Description("compareShape: start of the second spell.")] public int? FromMinute2 { get; set; }
    [JsonPropertyName("toMinute2")] public int? ToMinute2 { get; set; }
}

/// <summary>The Match Analyst's structured answer.</summary>
[Description("An answer to a fan's question about the live match, grounded in tool results.")]
public sealed class AskDraft
{
    [JsonPropertyName("answer")] [Description("2 to 4 sentences in the requested language and style. Only numbers that a tool returned.")]
    public string Answer { get; set; } = "";

    [JsonPropertyName("status")] [Description("measured (the numbers show it), interpretation (a reading of measured facts), or insufficientData (the match data cannot answer it).")]
    public string Status { get; set; } = "measured";

    [JsonPropertyName("keyFacts")] [Description("1 to 4 facts behind the answer.")]
    public List<AskFact> KeyFacts { get; set; } = [];

    [JsonPropertyName("citedEventIds")] [Description("Event ids that show the answer, from tool results.")]
    public List<string> CitedEventIds { get; set; } = [];

    [JsonPropertyName("actions")] [Description("0 to 2 things to open on the pitch.")]
    public List<AskAction> Actions { get; set; } = [];

    [JsonPropertyName("followUps")] [Description("3 short follow-up questions the fan might ask next, in the requested language.")]
    public List<string> FollowUps { get; set; } = [];
}

/// <summary>What the fan gets back, after the checks.</summary>
/// <param name="Check">"checked" (Decision-1 agreed the answer follows from the tool results), "unsure", or "unchecked".</param>
public sealed record AskResult(
    string AskId, int ClockSec, string Question, string Answer, string Status, IReadOnlyList<AskFact> KeyFacts,
    IReadOnlyList<string> CitedEventIds, IReadOnlyList<AskAction> Actions, IReadOnlyList<string> FollowUps,
    IReadOnlyList<string> ToolsUsed, string Check, string? CheckNote, IReadOnlyList<string> Problems, bool FromModel,
    double Milliseconds);

/// <summary>
/// The read-only tools Ask Second Ball IQ may use. Every result is logged: the numbers in the answer are checked against
/// what the tools actually returned, and the grounding gate reads the same log as its evidence.
/// </summary>
public sealed class AskTools(LiveMatch match, IReadOnlyList<PublishedMoment> moments, IReadOnlyList<Prediction> predictions,
    IReadOnlyList<RunningLine>? running = null, object? space = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly MatchQueries _q = new(match);
    private readonly object _sync = new();

    public List<(string Tool, string Result)> Log { get; } = [];

    private T Record<T>(string tool, T result)
    {
        lock (_sync) Log.Add((tool, JsonSerializer.Serialize(result, Json)));
        return result;
    }

    [Description("Clock, score, and both teams with their ids.")]
    public MatchStateDto GetMatchState() => Record("get_match_state", _q.GetMatchState());

    [Description("One team's numbers between two minutes: possession share, passes, final-third entries, shots, xG, high recoveries (with event ids).")]
    public WindowStatsDto GetWindowStats([Description("team-har or team-nor")] string teamId, int fromMinute, int toMinute) =>
        Record("get_window_stats", _q.GetWindowStats(teamId, fromMinute, toMinute));

    [Description("The same team's numbers in two spells side by side, plus its shape (formation, press, long balls) in each.")]
    public CompareDto CompareWindows([Description("team-har or team-nor")] string teamId, int fromMinuteA, int toMinuteA, int fromMinuteB, int toMinuteB) =>
        Record("compare_windows", new CompareDto(
            _q.GetWindowStats(teamId, fromMinuteA, toMinuteA), _q.GetWindowStats(teamId, fromMinuteB, toMinuteB),
            _q.GetTeamShape(teamId, fromMinuteA, toMinuteA), _q.GetTeamShape(teamId, fromMinuteB, toMinuteB)));

    [Description("Neutral plain-language summary of both teams between two minutes.")]
    public string DescribeWindow(int fromMinute, int toMinute) => Record("describe_window", _q.DescribeWindow(fromMinute, toMinute));

    [Description("Find events by type (pass, shot, goal, tackle, interception, pressure, foul, substitution, highRecovery, longBall, hardPass, fastShot), team, player and minutes. Passes carry their length in metres and a difficulty from 0 to 100 (hard from 60); shots carry their speed in km/h (fast from 110). Returns at most 12, newest last.")]
    public IReadOnlyList<EventView> FindEvents(
        [Description("Event type, or highRecovery, longBall, hardPass or fastShot; empty for any.")] string? type, string? teamId, string? playerId, int fromMinute, int toMinute)
    {
        IEnumerable<MatchEvent> found = match.Between(fromMinute * 60, toMinute * 60);
        if (!string.IsNullOrWhiteSpace(teamId)) found = found.Where(e => e.TeamId == teamId);
        if (!string.IsNullOrWhiteSpace(playerId)) found = found.Where(e => e.PlayerId == playerId);
        string t = (type ?? "").Trim().ToLowerInvariant();
        if (t == "highrecovery") found = found.Where(Pitch.IsHighRecovery);
        else if (t == "longball") found = found.Where(e => e.LongBall == true);
        else if (t == "hardpass") found = found.Where(e => BallMetrics.PassDifficulty(e) >= BallMetrics.HardPass);
        else if (t == "fastshot") found = found.Where(BallMetrics.IsFastShot);
        else if (t.Length > 0 && Enum.TryParse(t, ignoreCase: true, out MatchEventType et)) found = found.Where(e => e.Type == et);
        return Record("find_events", found.TakeLast(12).Select(e => EventView.From(e, match.Info)).ToList());
    }

    [Description("Details of events by id.")]
    public IReadOnlyList<EventView> GetEvents(string[] eventIds) =>
        Record("get_events", _q.GetEvents(eventIds).Select(e => EventView.From(e, match.Info)).ToList());

    [Description("A player's numbers in this match so far.")]
    public PlayerStatsDto? GetPlayerStats([Description("e.g. har-09")] string playerId) => Record("get_player_stats", _q.GetPlayerStats(playerId));

    [Description("A team's shape between two minutes: formation, each player's average position, touches and role, how high it pressed and how direct it played.")]
    public TeamShapeDto GetTeamShape(string teamId, int fromMinute, int toMinute) => Record("get_team_shape", _q.GetTeamShape(teamId, fromMinute, toMinute));

    [Description("Players ranked by one stat in this match so far: highRecoveries, shots, xg, goals, completedPasses, passes, touches, hardPassesCompleted (difficulty 60 or more), longestPass (metres), averagePassLength (metres, at least 10 passes) or fastestShot (km/h). Optionally one team.")]
    public IReadOnlyList<TopPlayerDto> GetTopPlayers(
        [Description("highRecoveries, shots, xg, goals, completedPasses, passes, touches, hardPassesCompleted, longestPass, averagePassLength or fastestShot")] string stat,
        [Description("Optional team id")] string? teamId = null)
    {
        var touches = match.Events.Where(ShapeAnalyzer.IsTouch).GroupBy(e => e.PlayerId!).ToDictionary(g => g.Key, g => g.Count());
        var rows = match.Players
            .Where(p => string.IsNullOrWhiteSpace(teamId) || p.TeamId == teamId)
            .Select(p => new TopPlayerDto(p.PlayerId, match.Info.Player(p.PlayerId)?.Name ?? p.PlayerId, match.Info.Team(p.TeamId).Name,
                stat.ToLowerInvariant() switch
                {
                    "shots" => p.Shots,
                    "xg" => Math.Round(p.Xg, 2),
                    "goals" => p.Goals,
                    "completedpasses" => p.CompletedPasses,
                    "passes" => p.Passes,
                    "touches" => touches.GetValueOrDefault(p.PlayerId),
                    "hardpassescompleted" => p.HardPassesCompleted,
                    "longestpass" => Math.Round(p.LongestCompletedPassMetres, 1),
                    "averagepasslength" => p.Passes >= 10 ? Math.Round(p.AveragePassMetres, 1) : 0,
                    "fastestshot" => Math.Round(p.FastestShotKmh, 1),
                    _ => p.HighRecoveries,
                }))
            .OrderByDescending(r => r.Value).Take(5).ToList();
        return Record("get_top_players", rows);
    }

    [Description("Moments Second Ball IQ has already published in this match (headline, summary, event ids), newest last.")]
    public IReadOnlyList<PublishedMoment> GetPublishedMoments() => Record("get_published_moments", moments.TakeLast(15).ToList());

    [Description("Set-piece and next-goal predictions so far, with how they turned out.")]
    public IReadOnlyList<Prediction> GetPredictions() => Record("get_predictions", predictions.TakeLast(10).ToList());

    [Description("Players' running from tracking: distance covered (km), top speed (km/h), sprints (25.2 km/h or faster for a second) and high-intensity distance (19.8 km/h or faster, km), ranked by distance, topSpeed or sprints; optionally one team. Empty when tracking is not available.")]
    public IReadOnlyList<RunningLine> GetRunning(
        [Description("distance, topSpeed or sprints")] string? rankBy = null,
        [Description("Optional team id")] string? teamId = null)
    {
        IEnumerable<RunningLine> rows = (running ?? []).Where(r => string.IsNullOrWhiteSpace(teamId) || r.TeamId == teamId);
        rows = (rankBy ?? "distance").ToLowerInvariant() switch
        {
            "topspeed" => rows.OrderByDescending(r => r.TopSpeedKmh),
            "sprints" => rows.OrderByDescending(r => r.Sprints),
            _ => rows.OrderByDescending(r => r.DistanceKm),
        };
        return Record("get_running", rows.Take(6).ToList());
    }

    [Description("Space and passing from tracking: each team's control of the final third (pitch control: the share of it the team would reach first; last 10 minutes and the match so far), and the players who most often missed a better-placed teammate (how many times, of how many passes, and the expected threat, xT, those options would have added). Unavailable without tracking.")]
    public object GetSpace() => Record("get_space", space ?? new { available = false });

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(GetSpace, name: "get_space"),
        AIFunctionFactory.Create(GetRunning, name: "get_running"),
        AIFunctionFactory.Create(GetMatchState, name: "get_match_state"),
        AIFunctionFactory.Create(GetWindowStats, name: "get_window_stats"),
        AIFunctionFactory.Create(CompareWindows, name: "compare_windows"),
        AIFunctionFactory.Create(DescribeWindow, name: "describe_window"),
        AIFunctionFactory.Create(FindEvents, name: "find_events"),
        AIFunctionFactory.Create(GetEvents, name: "get_events"),
        AIFunctionFactory.Create(GetPlayerStats, name: "get_player_stats"),
        AIFunctionFactory.Create(GetTeamShape, name: "get_team_shape"),
        AIFunctionFactory.Create(GetPublishedMoments, name: "get_published_moments"),
        AIFunctionFactory.Create(GetTopPlayers, name: "get_top_players"),
        AIFunctionFactory.Create(GetPredictions, name: "get_predictions"),
    ];
}

public sealed record TopPlayerDto(string PlayerId, string Name, string Team, double Value);

/// <summary>One player's running from tracking, for the Match Analyst.</summary>
public sealed record RunningLine(string PlayerId, string Name, string TeamId, string Team, double DistanceKm, double TopSpeedKmh, int Sprints, double HighIntensityKm);

public sealed record CompareDto(WindowStatsDto A, WindowStatsDto B, TeamShapeDto ShapeA, TeamShapeDto ShapeB);

/// <summary>
/// Ask Second Ball IQ: a match-aware analyst a fan can question. It investigates with read-only tools against the match
/// frozen at the second of the question, answers in the fan's language and style, and returns evidence the fan can
/// open on the pitch. Code then checks the answer (every number from a tool, every event real and not in the
/// future), Decision-1 checks that it follows from the tool results, and a failed check gets one retry.
/// </summary>
public sealed partial class MatchAnalyst(IChatClient? chat, IDecisionClient? decisions)
{
    public const string Instructions = """
        You are Second Ball IQ, a football analyst watching a live match with a fan. Answer the fan's question about this
        match, using the tools: call them to look at the numbers, the shapes, the events and what was already
        published before you answer. The match is frozen at the clock in the request; nothing after it exists.
        Rules:
        - Every number you write must come from a tool result in this conversation (match minutes and the score are
          fine). Never estimate or round to a number no tool returned; percentages may be written from shares (0.73 as 73%).
        - Cite event ids from tool results that show the answer; add an action when seeing it helps (rewind for a
          move or a set of events, compareShape for a change in shape, heatMap for where someone played, openPlayer).
        - The data is synthetic: fictional clubs and players. Positions come from on-ball touches, not off-ball
          tracking: say so if a question needs off-ball movement. If the data cannot answer, set status
          insufficientData and say what you can see instead. Questions that are not about this match: politely say you
          can only see this match.
        - Players are fictional and their gender is unknown: use their names, never he or she.
        - Lead with the direct answer in the first sentence. Then at most two short sentences of evidence: 2 or 3
          numbers, not more. Match the requested language and style.
        - Write numbers the way a broadcaster would: shares as percentages (0.314 as 31%, 0.066 as 7%), xG with two
          decimals (0.159 as 0.16), distances in whole metres. Never write a raw share like 0.465.
        - Separate what was measured from what it might mean: a cause or a coach's intention is "probably" or "looks
          like", never stated as fact, and a change that only coincides with another event "came around the same
          time". Use status interpretation when the answer rests on such a reading.
        - "Who did X most" questions: use get_top_players, never a team total.
        - Pass difficulty is the engine's own rating from 0 to 100 (length, pressure on the passer, how far forward,
          where it was aimed, lofted or not); hard from 60. Write it as "a difficulty of 72 out of 100". Ball and shot
          speeds are in km/h, written whole. Players' running (distance covered, top speed, sprints) comes from
          tracking: use get_running; write distances with one decimal (9.4 km) and speeds whole. Territory and
          missed passes also come from tracking: use get_space; write control as a percentage of the final third and
          expected threat (xT) with two decimals.
        - Event ids (evt-000123) are for citedEventIds and actions only; never write one in the answer or a key fact.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ActionKinds = ["rewind", "compareShape", "showShape", "heatMap", "openPlayer"];

    public async Task<AskResult> AskAsync(
        AskRequest request, LiveMatch match, IReadOnlyList<PublishedMoment> moments, IReadOnlyList<Prediction> predictions,
        CancellationToken ct, IReadOnlyList<RunningLine>? running = null, object? space = null)
    {
        var sw = Stopwatch.StartNew();
        string askId = $"ask-{Guid.NewGuid():N}"[..12];
        int clock = match.ClockSec;
        var tools = new AskTools(match, moments, predictions, running, space);

        if (chat is null) return Offline(askId, request, match, tools, sw);

        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = "MatchAnalyst",
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                ResponseFormat = ChatResponseFormat.ForJsonSchema<AskDraft>(),
                Tools = tools.AsTools(),
            },
        });

        string prompt = Prompt(request, match);
        AskDraft? draft = null;
        List<string> problems = [];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                string text = attempt == 0 ? prompt
                    : prompt + "\n\nYOUR PREVIOUS ANSWER FAILED THESE CHECKS, fix them (use only numbers from tool results, only real event ids):\n- " +
                      string.Join("\n- ", problems) + "\nPrevious answer: " + JsonSerializer.Serialize(draft, Json);
                AgentResponse<AskDraft> response = await agent.RunAsync<AskDraft>(text, cancellationToken: timeout.Token);
                draft = response.Result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problems = [$"the analyst failed: {ex.GetType().Name}"];
                draft = null;
                break;
            }
            if (draft is null) { problems = ["the analyst returned nothing"]; break; }
            draft = Tidy(draft);
            (draft, problems) = Check(draft, match, tools, request.Question);
            if (problems.Count == 0) break;
        }

        if (draft is null || problems.Count > 0 && Strict(problems))
        {
            AskResult fallback = Offline(askId, request, match, tools, sw);
            return fallback with { Problems = problems, CheckNote = "the analyst's answer did not pass the checks, so only measured facts are shown" };
        }

        // Decision-1: does the answer follow from what the tools returned?
        string check = "unchecked";
        string? note = null;
        if (decisions is not null && draft.Status != "insufficientData")
        {
            var gate = new GroundingGate(decisions);
            string evidence = Evidence(tools);
            var (_, verdict, _) = await gate.EvaluateAsync(evidence, draft.Answer, ct);
            check = verdict.Outcome == GateOutcome.Proceed ? "checked" : "unsure";
            note = verdict.Reason;
        }

        return new AskResult(askId, clock, request.Question, draft.Answer.Trim(), Normalise(draft.Status), draft.KeyFacts.Take(4).ToList(),
            draft.CitedEventIds.Distinct().Take(8).ToList(), draft.Actions.Take(2).ToList(),
            draft.FollowUps.Where(f => !string.IsNullOrWhiteSpace(f)).Take(3).ToList(),
            tools.Log.Select(l => l.Tool).Distinct().ToList(), check, note, problems, FromModel: true, Math.Round(sw.Elapsed.TotalMilliseconds));
    }

    private static string Prompt(AskRequest r, LiveMatch match)
    {
        var sb = new StringBuilder();
        MatchInfo info = match.Info;
        sb.Append(CultureInfo.InvariantCulture,
            $"MATCH: {info.Home.Name} ({info.Home.TeamId}) {match.Goals(info.Home.TeamId)}-{match.Goals(info.Away.TeamId)} {info.Away.Name} ({info.Away.TeamId}), clock {NarrativeText.Clock(match.ClockSec)} (minute {match.ClockSec / 60 + 1}).\n");
        sb.Append("Players (id = name, listed position): ")
          .Append(string.Join(", ", info.Home.Players.Concat(info.Away.Players).Select(p => $"{p.PlayerId} = {p.Name} ({p.Position.ToString().ToLowerInvariant()})")))
          .Append(".\n");
        var persona = new Persona(r.Audience, r.Language, r.TeamId, r.PlayerId);
        sb.Append(Prompts.Persona(persona, info)).Append('\n');
        if (r.History is { Count: > 0 } history)
        {
            sb.Append("EARLIER IN THIS CONVERSATION:\n");
            foreach (AskTurn t in history.TakeLast(3)) sb.Append("Q: ").Append(t.Question).Append("\nA: ").Append(t.Answer).Append('\n');
        }
        sb.Append("QUESTION: ").Append(r.Question.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// The checks that cannot be talked round: numbers from tools, real and past event ids, sane actions. Bad
    /// citations and actions are dropped; unsupported numbers fail the answer.
    /// </summary>
    internal static (AskDraft Draft, List<string> Problems) Check(AskDraft d, LiveMatch match, AskTools tools, string question)
    {
        var problems = new List<string>();
        HashSet<string> allowed = Allowed(match, tools, question);
        string text = WithoutClockReferences(d.Answer + " " + string.Join(" ", d.KeyFacts.Select(f => f.Value)), match.ClockSec);
        foreach (Match m in Numbers().Matches(text))
        {
            string n = Normalise(m.Value, out bool ok);
            if (ok && !allowed.Contains(n)) problems.Add($"number {m.Value} did not come from a tool");
        }

        bool Valid(string id) => match.Event(id) is { } e && e.MatchClockSec <= match.ClockSec;
        d.CitedEventIds = d.CitedEventIds.Where(Valid).ToList();
        int maxMinute = match.ClockSec / 60 + 1;
        var teams = new[] { match.Info.Home.TeamId, match.Info.Away.TeamId };
        d.Actions = d.Actions.Where(a => ActionKinds.Contains(a.Kind)).Select(a =>
        {
            a.EventIds = a.EventIds.Where(Valid).ToList();
            if (a.TeamId is not null && !teams.Contains(a.TeamId)) a.TeamId = null;
            if (a.PlayerId is not null && match.Info.Player(a.PlayerId) is null) a.PlayerId = null;
            a.FromMinute = Clamp(a.FromMinute, maxMinute); a.ToMinute = Clamp(a.ToMinute, maxMinute);
            a.FromMinute2 = Clamp(a.FromMinute2, maxMinute); a.ToMinute2 = Clamp(a.ToMinute2, maxMinute);
            return a;
        }).Where(a => a.Kind switch
        {
            "rewind" => a.EventIds.Count > 0,
            "openPlayer" => a.PlayerId is not null,
            "heatMap" => a.PlayerId is not null || a.TeamId is not null,
            "showShape" => a.TeamId is not null,
            "compareShape" => a.TeamId is not null && a.FromMinute is not null && a.ToMinute is not null && a.FromMinute2 is not null && a.ToMinute2 is not null,
            _ => false,
        }).ToList();
        if (string.IsNullOrWhiteSpace(d.Answer)) problems.Add("empty answer");
        return (d, problems);
    }

    /// <summary>What a fan reads: no internal event ids, and long decimals (0.159) rounded to two places (0.16).</summary>
    internal static AskDraft Tidy(AskDraft d)
    {
        static string Clean(string text)
        {
            text = EventIds().Replace(text, "");
            text = LongDecimals().Replace(text, m => Math.Round(double.Parse(m.Value, CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero)
                .ToString("0.00", CultureInfo.InvariantCulture));
            text = Regex.Replace(text, @"\(\s*[,;]?\s*\)", "");
            text = Regex.Replace(text, @"^\s*[,;:]\s*|\s*[,;]\s*$", "");
            text = Regex.Replace(text, @"\s+([,.;:])", "$1");
            text = Regex.Replace(text, @"[,;]\s*[,;]", ",");
            return Regex.Replace(text, @"\s{2,}", " ").Trim();
        }
        d.Answer = Clean(d.Answer);
        foreach (AskFact f in d.KeyFacts) { f.Label = Clean(f.Label); f.Value = Clean(f.Value); }
        d.KeyFacts = d.KeyFacts.Where(f => f.Value.Length > 0).ToList();
        return d;
    }

    [GeneratedRegex(@"\bevt-\d+\b,?\s*")]
    private static partial Regex EventIds();

    [GeneratedRegex(@"(?<![\d.])\d+\.\d{3,}(?!\d)")]
    private static partial Regex LongDecimals();

    private static int? Clamp(int? m, int max) => m is null ? null : Math.Clamp(m.Value, 0, max);

    /// <summary>Unsupported numbers are the failures that matter; a fallback is better than an invented figure.</summary>
    private static bool Strict(List<string> problems) => problems.Count > 0;

    private static HashSet<string> Allowed(LiveMatch match, AskTools tools, string question)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        void Add(double v)
        {
            foreach (int dp in new[] { 0, 1, 2 }) allowed.Add(Format(Math.Round(v, dp, MidpointRounding.AwayFromZero)));
            allowed.Add(Format(Math.Floor(v)));
            if (v is > 0 and <= 1) foreach (int dp in new[] { 0, 1 }) allowed.Add(Format(Math.Round(v * 100, dp, MidpointRounding.AwayFromZero)));
        }
        foreach ((_, string result) in tools.Log)
            foreach (Match m in Numbers().Matches(result))
                if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) Add(v);
        foreach (Match m in Numbers().Matches(question))
            if (double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) Add(v);
        Add(match.Goals(match.Info.Home.TeamId));
        Add(match.Goals(match.Info.Away.TeamId));
        foreach (PlayerInfo p in match.Info.Home.Players.Concat(match.Info.Away.Players)) Add(p.ShirtNumber);
        // Formations ("4-2-3-1") are names.
        foreach (string f in ShapeAnalyzer.Templates.Keys.Append("4-5-1")) foreach (string part in f.Split('-')) allowed.Add(part);
        return allowed;
    }

    /// <summary>Clock references up to now ("52:10", "the 52nd minute", "52'", "52. minuutilla") are times, not figures.</summary>
    private static string WithoutClockReferences(string text, int clockSec)
    {
        int max = clockSec / 60 + 1;
        string Keep(Match m, Group g) => int.TryParse(g.Value, out int minute) && minute <= max ? " " : m.Value;
        text = ClockTimes().Replace(text, m => Keep(m, m.Groups[1]));
        text = Minutes().Replace(text, m => Keep(m, m.Groups["n"]));
        return text;
    }

    private static string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Normalise(string raw, out bool ok)
    {
        ok = double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        return ok ? Format(v) : raw;
    }

    private static string Normalise(string status) => status is "measured" or "interpretation" or "insufficientData" ? status : "interpretation";

    private static string Evidence(AskTools tools)
    {
        var sb = new StringBuilder();
        foreach ((string tool, string result) in tools.Log)
        {
            if (sb.Length > 7000) break;
            sb.Append(tool).Append(": ").Append(result.Length > 2000 ? result[..2000] + "…" : result).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// No model, or an answer that failed its checks: the last ten minutes in the engine's own measured words, and
    /// honest about why. Still useful, never invented.
    /// </summary>
    private static AskResult Offline(string askId, AskRequest r, LiveMatch match, AskTools tools, Stopwatch sw)
    {
        int now = match.ClockSec / 60 + 1;
        string summary = tools.DescribeWindow(Math.Max(0, now - 10), now);
        bool fi = r.Language == "fi";
        string lead = fi
            ? "En pysty vastaamaan tähän varmennetusti juuri nyt. Tässä viimeiset kymmenen minuuttia mitattuna (englanniksi):"
            : "I can't give a checked answer to that right now. Here are the last ten minutes, as measured:";
        return new AskResult(askId, match.ClockSec, r.Question, lead + " " + summary.Replace('\n', ' ').Trim(), "insufficientData",
            [], [], [], fi ? ["Kuka hallitsee peliä nyt?", "Miten pelaajani pelaa?", "Mikä muuttui toisella puoliajalla?"]
                          : ["Who is on top right now?", "How is my player doing?", "What changed after half-time?"],
            tools.Log.Select(l => l.Tool).Distinct().ToList(), "unchecked", null, [], FromModel: false, Math.Round(sw.Elapsed.TotalMilliseconds));
    }

    [GeneratedRegex(@"(?<![\p{L}\d-])\d+(?:[.,]\d+)?")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\b(\d{1,3}):(\d{2})\b")]
    private static partial Regex ClockTimes();

    [GeneratedRegex(@"(?:(?:minute|minuutti|minuutilla|min)\s*(?<n>\d{1,3}))|(?:(?<n>\d{1,3})(?:st|nd|rd|th|'|′|\.?\s*minuut\w*|\s*min\b))", RegexOptions.IgnoreCase)]
    private static partial Regex Minutes();
}
