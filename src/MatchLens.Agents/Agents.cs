using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MatchLens.Analytics;
using MatchLens.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MatchLens.Agents;

/// <summary>
/// Read-only engine queries exposed to the Tactical Analyst as function tools. The same methods become MCP tools
/// later; numbers always come from here, never from the model.
/// </summary>
public sealed class AnalystTools(LiveMatch match)
{
    private readonly MatchQueries _queries = new(match);

    [Description("Numbers for one team between two match minutes: possession share, final-third entries, shots, xG, high recoveries, with event ids.")]
    public WindowStatsDto GetWindowStats(
        [Description("Team id, e.g. team-har or team-nor.")] string teamId,
        [Description("Start minute (inclusive).")] int fromMinute,
        [Description("End minute (exclusive).")] int toMinute) =>
        _queries.GetWindowStats(teamId, fromMinute, toMinute);

    [Description("Neutral plain-language summary of both teams between two match minutes.")]
    public string DescribeWindow(
        [Description("Start minute (inclusive).")] int fromMinute,
        [Description("End minute (exclusive).")] int toMinute) =>
        _queries.DescribeWindow(fromMinute, toMinute);

    [Description("Details of specific events by id: time, type, team, player, outcome, shot result and xG.")]
    public IReadOnlyList<EventView> GetEvents([Description("Event ids such as evt-000712.")] string[] eventIds) =>
        _queries.GetEvents(eventIds).Select(e => EventView.From(e, match.Info)).ToList();

    [Description("A team's shape between two match minutes: the formation it fits best, each player's average position (metres from their own goal), touches and role, and how high and how direct it played.")]
    public TeamShapeDto GetTeamShape(
        [Description("Team id, e.g. team-har or team-nor.")] string teamId,
        [Description("Start minute (inclusive).")] int fromMinute,
        [Description("End minute (exclusive).")] int toMinute) =>
        _queries.GetTeamShape(teamId, fromMinute, toMinute);

    [Description("Running numbers for one player in this match.")]
    public PlayerStatsDto? GetPlayerStats([Description("Player id such as nor-09.")] string playerId) =>
        _queries.GetPlayerStats(playerId);

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(GetWindowStats, name: "get_window_stats"),
        AIFunctionFactory.Create(DescribeWindow, name: "describe_window"),
        AIFunctionFactory.Create(GetEvents, name: "get_events"),
        AIFunctionFactory.Create(GetPlayerStats, name: "get_player_stats"),
        AIFunctionFactory.Create(GetTeamShape, name: "get_team_shape"),
    ];
}

/// <summary>A match event in the shape an agent reads easily.</summary>
public sealed record EventView(
    string EventId, string Clock, string Type, string Team, string? Player, string? Outcome, string? ShotResult, double? Xg,
    double? BallSpeedKmh = null, double? PassMetres = null, int? PassDifficulty = null, string? PassDifficultyBand = null)
{
    public static EventView From(MatchEvent e, MatchInfo info)
    {
        int? difficulty = BallMetrics.PassDifficulty(e);
        return new(
            e.EventId, NarrativeText.Clock(e.MatchClockSec), e.Type.ToString(), info.Team(e.TeamId).Name,
            info.Player(e.PlayerId)?.Name, e.Outcome?.ToString(), e.ShotResult?.ToString(), e.Xg,
            e.BallSpeedKmh, BallMetrics.PassMetres(e), difficulty, difficulty is { } d ? BallMetrics.Band(d) : null);
    }
}

/// <summary>Tactical Analyst: explains why a candidate matters and picks the facts and events that show it.</summary>
public sealed class TacticalAnalyst
{
    public const string Instructions = """
        You are the tactical analyst for a live football broadcast. You receive one candidate moment found by a
        deterministic analytics engine, with its measured facts and evidence. Explain in one or two sentences why it
        matters tactically, choose the 2 to 4 facts that best support that reading, and cite the 1 to 5 most telling
        event ids from the evidence. You may call the tools to look at the wider match before deciding.
        Rules: never state a number that is not in the facts; never invent events; cite only event ids you were given
        or retrieved with a tool; if the moment is less meaningful than it looks, say so plainly.
        For a tactical shift or a player's role, say what the coach is probably trying to achieve (protect a lead,
        get more bodies into the box, overload the middle), framed as a likely intent, never as a known fact. You can
        call get_team_shape to compare the team's shape in two spells.
        """;

    private readonly AIAgent _agent;

    public TacticalAnalyst(IChatClient chat, AnalystTools tools)
    {
        _agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = "TacticalAnalyst",
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                ResponseFormat = ChatResponseFormat.ForJsonSchema<AnalysisResult>(),
                Tools = tools.AsTools(),
            },
        });
    }

    public async Task<AnalysisResult> AnalyseAsync(Candidate candidate, MatchInfo info, CancellationToken cancellationToken)
    {
        AgentResponse<AnalysisResult> response =
            await _agent.RunAsync<AnalysisResult>(Prompts.Candidate(candidate, info), cancellationToken: cancellationToken);
        return response.Result ?? throw new InvalidOperationException("The analyst returned no result.");
    }
}

/// <summary>Narrator: writes the on-screen insight for one persona, from the analyst's reading.</summary>
public sealed class Narrator
{
    public const string Instructions = """
        You write on-screen football insights for a live broadcast. You receive a candidate moment, its measured
        facts and evidence, the tactical analyst's reading, and the audience you write for.
        Write a headline of at most 60 characters and a summary of one or two sentences, in the requested language,
        in natural football language. Choose 0 to 3 fact keys to show as figures; their values are filled in for you.
        Rules: every number in the headline or summary must be one of the facts (a share like 0.73 may be written as
        73%); never invent events, players, records or history; do not mention event ids; keep team and player names
        exactly as given. Players are fictional and their gender is unknown: refer to them by name, never as he or she.
        """;

    private readonly AIAgent _agent;

    public Narrator(IChatClient chat)
    {
        _agent = new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Name = "Narrator",
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                ResponseFormat = ChatResponseFormat.ForJsonSchema<NarrativeDraft>(),
            },
        });
    }

    public async Task<NarrativeDraft> WriteAsync(
        Candidate candidate, AnalysisResult analysis, Persona persona, MatchInfo info, CancellationToken cancellationToken)
    {
        string prompt = Prompts.Candidate(candidate, info) + "\n\n" + Prompts.Analysis(analysis) + "\n\n" + Prompts.Persona(persona, info);
        AgentResponse<NarrativeDraft> response = await _agent.RunAsync<NarrativeDraft>(prompt, cancellationToken: cancellationToken);
        return response.Result ?? throw new InvalidOperationException("The narrator returned no result.");
    }
}

internal static class Prompts
{
    public static string Candidate(Candidate c, MatchInfo info)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"CANDIDATE: {c.Kind} for {info.Team(c.TeamId).Name}");
        if (info.Player(c.PlayerId) is { } player) sb.Append(CultureInfo.InvariantCulture, $", player {player.Name} ({player.PlayerId})");
        sb.Append(CultureInfo.InvariantCulture, $", detected at {NarrativeText.Clock(c.DetectedAtSec)}.\n\n");
        sb.Append("FACTS (key: value):\n");
        foreach (KeyValuePair<string, double> f in c.Facts)
            sb.Append(CultureInfo.InvariantCulture, $"- {f.Key}: {f.Value:0.###}\n");
        if (c.Tags is { Count: > 0 } tags)
            sb.Append("TAGS: ").Append(string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))).Append('\n');
        sb.Append("\nEVIDENCE:\n").Append(c.Evidence).Append("\n\nMATCH CONTEXT:\n").Append(c.WindowSummary);
        sb.Append("\n\nTeam ids: ").Append(info.Home.TeamId).Append(" = ").Append(info.Home.Name)
          .Append(", ").Append(info.Away.TeamId).Append(" = ").Append(info.Away.Name).Append('.');
        sb.Append("\nPlayers (id = name): ");
        sb.Append(string.Join(", ", info.Home.Players.Concat(info.Away.Players).Select(p => $"{p.PlayerId} = {p.Name}")));
        sb.Append(". Use only these ids.");
        return sb.ToString();
    }

    public static string Analysis(AnalysisResult a) =>
        "ANALYST READING:\n" + JsonSerializer.Serialize(a, new JsonSerializerOptions { WriteIndented = true });

    public static string Persona(Persona p, MatchInfo info)
    {
        string audience = p.Audience switch
        {
            Audience.Casual => "a casual fan: simple words, no jargon, the story in one breath",
            Audience.Analyst => "a tactical analyst: precise terms (high turnovers, final-third entries, xG), the mechanism behind the moment",
            Audience.Broadcast => "a broadcast graphic: a punchy headline that works over live pictures, and a one-sentence summary",
            Audience.Beginner => "someone watching football for the first time: everyday words, no jargon without a few-word explanation " +
                                 "(say 'won the ball back near the opponent's goal' rather than 'high turnover'), and end with one short " +
                                 "sentence on why this moment matters for the match",
            _ => "a general viewer",
        };
        string language = p.Language == "fi" ? "Finnish (suomi)" : "English";
        var sb = new StringBuilder($"WRITE FOR: {audience}. LANGUAGE: {language}.");
        if (p.FavoriteTeamId is not null)
            sb.Append(CultureInfo.InvariantCulture, $" The viewer supports {info.Team(p.FavoriteTeamId).Name}: frame it from their side, fairly.");
        if (info.Player(p.FocusPlayerId) is { } focus)
            sb.Append(CultureInfo.InvariantCulture, $" Player-focus mode: if {focus.Name} is involved, centre the story on them; otherwise mention them only if the facts do.");
        return sb.ToString();
    }
}
