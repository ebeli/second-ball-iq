using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MatchLens.Analytics;
using MatchLens.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MatchLens.Agents;

/// <summary>A moment that went to air, as the Recap Writer sees it.</summary>
/// <param name="Facts">The candidate's measured facts; the only numbers the recap may use besides the stats and score.</param>
public sealed record RecapMoment(
    string InsightId, int ClockSec, InsightKind Kind, string TeamId, string Headline, string Summary,
    IReadOnlyDictionary<string, double> Facts);

public sealed record RecapGoal(int ClockSec, string TeamId, string? PlayerId);

/// <summary>Everything a recap may draw on: what was published, the measured stats and the score. Nothing else.</summary>
public sealed record RecapInput(
    MatchInfo Info, bool FullTime, int ClockSec, int HomeGoals, int AwayGoals,
    IReadOnlyList<RecapGoal> Goals, IReadOnlyList<WindowStatsDto> TeamStats, IReadOnlyList<RecapMoment> Moments);

/// <summary>Recap Writer output for one persona.</summary>
[Description("A half-time or full-time recap of the match for one audience and language.")]
public sealed class RecapDraft
{
    [JsonPropertyName("headline")]
    [Description("The story of the match so far in one line, at most 70 characters.")]
    public string Headline { get; set; } = "";

    [JsonPropertyName("paragraphs")]
    [Description("Two or three short paragraphs telling the story in order. Any number used must appear in the input.")]
    public List<string> Paragraphs { get; set; } = [];

    [JsonPropertyName("keyMoments")]
    [Description("The 2 to 5 moments that shaped the match, in match order.")]
    public List<RecapKeyMoment> KeyMoments { get; set; } = [];
}

public sealed class RecapKeyMoment
{
    [JsonPropertyName("insightId")]
    [Description("The insightId of a published moment from the input.")]
    public string InsightId { get; set; } = "";

    [JsonPropertyName("text")]
    [Description("One short sentence on why this moment mattered. No numbers that are not in the input.")]
    public string Text { get; set; } = "";
}

/// <summary>
/// The Recap Writer: at half time and full time it turns what went to air into the story of the match, for each
/// persona. It sees only published insights, the measured stats and the score, and its numbers are checked like the
/// Narrator's, so a recap cannot bring in anything the pipeline did not already stand behind.
/// </summary>
public sealed partial class RecapWriter(IChatClient? chat)
{
    public const string Instructions = """
        You write the half-time or full-time recap of a football match for one audience. You receive the score, the
        goals, each team's measured stats, and the insights that were published during the match, with their facts.
        Tell the story of the match in order: what shaped it, how it turned, where it stands. Pick the 2 to 5 key
        moments by their insightId.
        Rules: use only what is in the input; every number you write must appear in the input; write shares as
        percentages (possessionShare 0.73 is 73% of the ball), never as decimals, and expected goals as xG;
        never invent players, events, records or history; keep team and player names exactly as
        given; write in the requested language. Players are fictional and their gender is unknown: refer to them by
        name, never as he or she. Call high recoveries winning the ball high up the pitch, not loose balls.
        """;

    private readonly AIAgent? _agent = chat is null ? null : new ChatClientAgent(chat, new ChatClientAgentOptions
    {
        Name = "RecapWriter",
        ChatOptions = new ChatOptions
        {
            Instructions = Instructions,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<RecapDraft>(),
        },
    });

    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Writes one recap. A model draft that fails the checks, times out or errors is replaced by the template, so a
    /// recap always exists. Returns the problems that sent it to the template, if any.
    /// </summary>
    public async Task<(RecapDraft Draft, bool FromTemplate, IReadOnlyList<string> Problems)> WriteAsync(
        RecapInput input, Persona persona, CancellationToken cancellationToken)
    {
        if (_agent is null) return (Template(input, persona), true, []);
        IReadOnlyList<string> problems;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            AgentResponse<RecapDraft> response = await _agent.RunAsync<RecapDraft>(Prompt(input, persona), cancellationToken: timeout.Token);
            RecapDraft draft = response.Result ?? throw new InvalidOperationException("The recap writer returned no result.");
            problems = Check(draft, input);
            if (problems.Count == 0) return (Tidy(draft, input), false, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            problems = [ex.GetType().Name + ": " + ex.Message];
        }
        return (Template(input, persona), true, problems);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Prompt
    // ------------------------------------------------------------------------------------------------------------

    internal static string Prompt(RecapInput input, Persona persona)
    {
        var sb = new StringBuilder();
        MatchInfo info = input.Info;
        sb.Append(CultureInfo.InvariantCulture,
            $"RECAP: {(input.FullTime ? "full time" : "half time")}, match clock {NarrativeText.Clock(input.ClockSec)}.\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"SCORE: {info.Home.Name} {input.HomeGoals} - {input.AwayGoals} {info.Away.Name}.\n");
        sb.Append("GOALS:");
        if (input.Goals.Count == 0) sb.Append(" none");
        foreach (RecapGoal g in input.Goals)
            sb.Append(CultureInfo.InvariantCulture,
                $"\n- minute {g.ClockSec / 60 + 1}: {info.Player(g.PlayerId)?.Name ?? "unknown scorer"} ({info.Team(g.TeamId).Name})");
        sb.Append("\n\nSTATS (whole match so far):\n");
        foreach (WindowStatsDto s in input.TeamStats)
            sb.Append(CultureInfo.InvariantCulture,
                $"- {info.Team(s.TeamId).Name}: possessionShare {s.PossessionShare:0.##}, completedPasses {s.CompletedPasses}, " +
                $"shots {s.Shots}, xg {s.Xg:0.##}, highRecoveries {s.HighRecoveries}, finalThirdEntries {s.FinalThirdEntries}\n");
        sb.Append("\nPUBLISHED MOMENTS:\n");
        foreach (RecapMoment m in input.Moments)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"- insightId {m.InsightId}, {NarrativeText.Clock(m.ClockSec)}, {m.Kind}, {info.Team(m.TeamId).Name}: \"{m.Headline}\" {m.Summary}\n");
            sb.Append("  facts: ").Append(string.Join(", ", m.Facts.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Key} {f.Value:0.###}")))).Append('\n');
        }
        sb.Append('\n').Append(Prompts.Persona(persona, info));
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Checks
    // ------------------------------------------------------------------------------------------------------------

    internal static IReadOnlyList<string> Check(RecapDraft draft, RecapInput input)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.Headline) || draft.Paragraphs.Count == 0 || draft.Paragraphs.All(string.IsNullOrWhiteSpace))
            problems.Add("empty headline or paragraphs");

        var ids = input.Moments.Select(m => m.InsightId).ToHashSet(StringComparer.Ordinal);
        foreach (RecapKeyMoment k in draft.KeyMoments.Where(k => !ids.Contains(k.InsightId)))
            problems.Add($"key moment cites unknown insight {k.InsightId}");

        HashSet<string> allowed = AllowedNumbers(input);
        string text = string.Join(" ", new[] { draft.Headline }.Concat(draft.Paragraphs).Concat(draft.KeyMoments.Select(k => k.Text)));
        // Match clocks ("41:35", "41.35" as Finnish writes it, "41'35") are one value, not two numbers. Any clock
        // whose minute is the minute of a moment, a goal or the recap itself is accepted; its seconds are not checked.
        var minutes = input.Moments.Select(m => m.ClockSec).Concat(input.Goals.Select(g => g.ClockSec)).Append(input.ClockSec)
            .SelectMany(sec => new[] { sec / 60, sec / 60 + 1 }).ToHashSet();
        text = Clocks().Replace(text, m => minutes.Contains(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) ? " " : m.Value);
        foreach (Match m in Numbers().Matches(text))
        {
            string n = Normalise(m.Value);
            if (allowed.Contains(n)) continue;
            int from = Math.Max(0, m.Index - 25);
            string context = text.Substring(from, Math.Min(text.Length - from, m.Length + 50)).Replace('\n', ' ').Trim();
            problems.Add($"number {n} is not in the input (\"…{context}…\")");
        }
        return problems;
    }

    private static HashSet<string> AllowedNumbers(RecapInput input)
    {
        var values = new List<double> { input.HomeGoals, input.AwayGoals, 45, 90, input.ClockSec / 60, input.ClockSec / 60 + 1 };
        foreach (RecapGoal g in input.Goals) values.AddRange([g.ClockSec / 60, g.ClockSec / 60 + 1]);
        foreach (WindowStatsDto s in input.TeamStats)
            values.AddRange([s.PossessionShare, s.PossessionShare * 100, s.CompletedPasses, s.Passes, s.Shots, s.Xg,
                             s.HighRecoveries, s.FinalThirdEntries, s.Goals]);
        foreach (RecapMoment m in input.Moments)
        {
            values.AddRange([m.ClockSec / 60, m.ClockSec / 60 + 1]);
            foreach (double v in m.Facts.Values)
            {
                values.Add(v);
                if (v is > 0 and <= 1) values.Add(v * 100);
            }
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (double v in values)
        {
            foreach (int decimals in new[] { 0, 1, 2 })
                allowed.Add(Math.Round(v, decimals, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture));
            allowed.Add(Math.Floor(v).ToString("0", CultureInfo.InvariantCulture));
        }
        return allowed;
    }

    private static string Normalise(string raw) =>
        double.Parse(raw.Replace(',', '.'), CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![\p{L}\d-])\d+(?:[.,]\d+)?")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"(?<!\d)(\d{1,3})[:.'′](\d{2})(?!\d)")]
    private static partial Regex Clocks();

    private static RecapDraft Tidy(RecapDraft draft, RecapInput input)
    {
        var order = input.Moments.Select((m, i) => (m.InsightId, i)).ToDictionary(x => x.InsightId, x => x.i);
        return new RecapDraft
        {
            Headline = draft.Headline.Trim(),
            Paragraphs = draft.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Take(4).ToList(),
            KeyMoments = draft.KeyMoments.Where(k => order.ContainsKey(k.InsightId))
                .DistinctBy(k => k.InsightId).OrderBy(k => order[k.InsightId]).Take(5).ToList(),
        };
    }

    // ------------------------------------------------------------------------------------------------------------
    // Template
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Built only from the score, the goals and the published headlines; always passes the checks.</summary>
    public static RecapDraft Template(RecapInput input, Persona persona)
    {
        bool fi = persona.Language == "fi";
        MatchInfo info = input.Info;
        string score = $"{info.Home.Name} {input.HomeGoals}-{input.AwayGoals} {info.Away.Name}";
        string when = input.FullTime ? (fi ? "Lopputulos" : "Full time") : (fi ? "Puoliaika" : "Half time");

        var goals = input.Goals.Select(g =>
        {
            string who = info.Player(g.PlayerId)?.Name ?? info.Team(g.TeamId).Name;
            return fi ? $"{who} ({g.ClockSec / 60 + 1}.)" : $"{who} ({g.ClockSec / 60 + 1}')";
        }).ToList();
        string goalLine = goals.Count == 0
            ? (fi ? "Maaleja ei ole nähty." : "No goals so far.")
            : (fi ? "Maalit: " : "Goals: ") + string.Join(", ", goals) + ".";

        var paragraphs = new List<string> { $"{when}: {score}. {goalLine}" };
        if (input.Moments.Count > 0)
            paragraphs.Add((fi ? "Ottelun tarina: " : "The story so far: ") +
                           string.Join(" ", input.Moments.Take(5).Select(m => m.Headline.TrimEnd('.') + ".")));

        return new RecapDraft
        {
            Headline = $"{when}: {score}",
            Paragraphs = paragraphs,
            KeyMoments = input.Moments.Take(5).Select(m => new RecapKeyMoment { InsightId = m.InsightId, Text = m.Headline }).ToList(),
        };
    }
}
