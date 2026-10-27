using System.ComponentModel;
using System.Text.Json.Serialization;
using MatchLens.Contracts;

namespace MatchLens.Agents;

/// <summary>Who an insight is written for.</summary>
/// <param name="FavoriteTeamId">Optional: the viewer's club; the narrative takes that club's perspective.</param>
/// <param name="FocusPlayerId">Optional: player-focus mode; the narrative centres on this player when involved.</param>
public sealed record Persona(Audience Audience, string Language, string? FavoriteTeamId = null, string? FocusPlayerId = null)
{
    public static IReadOnlyList<Persona> DemoSet { get; } =
    [
        new(Audience.Casual, "en"),
        new(Audience.Analyst, "en"),
        new(Audience.Broadcast, "en"),
        new(Audience.Casual, "fi"),
        new(Audience.Analyst, "fi"),
        new(Audience.Broadcast, "fi"),
        new(Audience.Beginner, "en"),
        new(Audience.Beginner, "fi"),
    ];

    public string Key => $"{Audience}-{Language}" +
                         (FavoriteTeamId is null ? "" : $"-fav:{FavoriteTeamId}") +
                         (FocusPlayerId is null ? "" : $"-focus:{FocusPlayerId}");
}

/// <summary>
/// Tactical Analyst output. It interprets the candidate in words and chooses which measured facts matter.
/// It never introduces numbers of its own: figures can only come from the candidate's facts.
/// </summary>
[Description("A tactical reading of one match moment, grounded in the candidate's measured facts.")]
public sealed class AnalysisResult
{
    [JsonPropertyName("whyItMatters")]
    [Description("One or two sentences on why this moment matters tactically. No numbers that are not in the facts.")]
    public string WhyItMatters { get; set; } = "";

    [JsonPropertyName("keyFactKeys")]
    [Description("Keys of the candidate facts that best support the reading, most important first (2 to 4).")]
    public List<string> KeyFactKeys { get; set; } = [];

    [JsonPropertyName("citedEventIds")]
    [Description("Event ids from the candidate evidence that a viewer should see, most important first (1 to 5).")]
    public List<string> CitedEventIds { get; set; } = [];

    [JsonPropertyName("playerIds")]
    [Description("Ids of players central to the moment, if any.")]
    public List<string> PlayerIds { get; set; } = [];
}

/// <summary>Narrator output for one persona. Figures are fact keys; code fills in the values.</summary>
[Description("An on-screen insight for one audience and language.")]
public sealed class NarrativeDraft
{
    [JsonPropertyName("headline")]
    [Description("Short on-screen headline, at most 60 characters.")]
    public string Headline { get; set; } = "";

    [JsonPropertyName("summary")]
    [Description("One or two sentences for the audience. Any number used must appear in the facts.")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("figureKeys")]
    [Description("Keys of facts to show as figures next to the text (0 to 3).")]
    public List<string> FigureKeys { get; set; } = [];
}

/// <summary>One step of the pipeline, kept for the editor view and the audit log.</summary>
public sealed record TraceStep(string Step, string Outcome, double Milliseconds, string? Detail = null);
