using System.Globalization;
using System.Text.RegularExpressions;
using MatchLens.Analytics;
using MatchLens.Contracts;

namespace MatchLens.Agents;

public sealed record ValidationResult(CheckStatus Evidence, CheckStatus Numbers, IReadOnlyList<string> Problems)
{
    public bool Passed => Evidence != CheckStatus.Fail && Numbers != CheckStatus.Fail;
}

/// <summary>
/// Deterministic checks between the agents and the screen. No model is involved, so these checks cannot be talked round:
/// every cited event must exist and belong to the moment, and every number in the text must be a measured fact.
/// </summary>
public static partial class Validator
{
    /// <summary>Events the analyst may cite beyond the candidate's own evidence: this close to its window.</summary>
    public const int CitationSlackSec = 10 * 60;

    public const int MaxHeadlineChars = 70;

    public static ValidationResult Check(Candidate candidate, AnalysisResult analysis, NarrativeDraft draft, LiveMatch match)
    {
        var problems = new List<string>();

        // Evidence: cited events exist and sit in or near the candidate's window.
        foreach (string id in analysis.CitedEventIds)
        {
            MatchEvent? e = match.Event(id);
            if (e is null)
                problems.Add($"cites unknown event {id}");
            else if (!candidate.EvidenceEventIds.Contains(id) &&
                     (e.MatchClockSec < candidate.FromSec - CitationSlackSec || e.MatchClockSec > candidate.ToSec + CitationSlackSec))
                problems.Add($"cites {id}, which is far from the moment");
        }
        // Players are fictional and their gender is not in any evidence: a he or she is a claim nothing backs up.
        // (Seen in the Oct 26 live run despite the instruction; Finnish "hän" is neutral and not affected.)
        foreach (string w in Gendered().Matches(draft.Headline + " " + draft.Summary).Select(m => m.Value.ToLowerInvariant()).Distinct())
            problems.Add($"writes \"{w}\": a player's gender is not known");
        CheckStatus evidence = problems.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail;

        // Numbers: every number written in the text must be an allowed value.
        HashSet<string> allowed = AllowedNumbers(candidate, match);
        var unsupported = Numbers().Matches(WithoutKnownFormations(draft.Headline + " " + draft.Summary, candidate))
            .Select(m => Normalise(m.Value))
            .Where(n => !allowed.Contains(n))
            .Distinct()
            .ToList();
        foreach (string n in unsupported) problems.Add($"number {n} is not a measured fact");
        CheckStatus numbers = unsupported.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail;

        if (string.IsNullOrWhiteSpace(draft.Headline) || string.IsNullOrWhiteSpace(draft.Summary))
        {
            problems.Add("empty headline or summary");
            numbers = CheckStatus.Fail;
        }

        return new ValidationResult(evidence, numbers, problems);
    }

    /// <summary>Facts that make sense in text but not as an on-screen figure.</summary>
    private static readonly HashSet<string> NotFigures =
        ["goal", "windowMinutes", "penalty", "directFreeKick", "formationChange", "droppedDeeper", "wentDirect"];

    /// <summary>
    /// A formation the engine measured ("3-4-1-2") is a name, not a number; it is taken out before the number check.
    /// Any other formation written in the text still fails.
    /// </summary>
    private static string WithoutKnownFormations(string text, Candidate c)
    {
        if (c.Tags is null) return text;
        foreach (string key in new[] { "formationBefore", "formationAfter", "formation" })
            if (c.Tags.TryGetValue(key, out string? f) && f.Length > 0) text = text.Replace(f, " ");
        return text;
    }

    /// <summary>Keeps only figure keys that are real, displayable facts, and trims an over-long headline.</summary>
    public static NarrativeDraft Tidy(NarrativeDraft draft, Candidate candidate) => new()
    {
        Headline = draft.Headline.Length <= MaxHeadlineChars ? draft.Headline.Trim() : draft.Headline[..(MaxHeadlineChars - 1)].TrimEnd() + "…",
        Summary = draft.Summary.Trim(),
        FigureKeys = draft.FigureKeys.Where(k => candidate.Facts.ContainsKey(k) && !NotFigures.Contains(k)).Distinct().Take(3).ToList(),
    };

    /// <summary>
    /// Facts in every reasonable written form (0.727 as 0.73, 0.7, 73, 72.7), the window length, the score, and the
    /// match minutes of the moment and its evidence (written either as 52 or as the 53rd minute).
    /// </summary>
    internal static HashSet<string> AllowedNumbers(Candidate c, LiveMatch match)
    {
        var values = new List<double>();
        foreach (KeyValuePair<string, double> fact in c.Facts)
        {
            values.Add(fact.Value);
            if (fact.Value is > 0 and <= 1) values.Add(fact.Value * 100);   // shares and accuracies as percentages
        }
        values.Add(match.Goals(match.Info.Home.TeamId));
        values.Add(match.Goals(match.Info.Away.TeamId));
        foreach (int sec in c.EvidenceEventIds.Select(id => match.Event(id)?.MatchClockSec ?? c.DetectedAtSec)
                     .Append(c.DetectedAtSec).Append(c.FromSec).Append(c.ToSec))
        {
            values.Add(sec / 60);
            values.Add(sec / 60 + 1);
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (double v in values)
        {
            allowed.Add(Format(v, 0));
            allowed.Add(Format(v, 1));
            allowed.Add(Format(v, 2));
            allowed.Add(Format(Math.Floor(v), 0));
        }
        return allowed;
    }

    /// <remarks>
    /// Rounded to nine places first: 0.285 × 100 is 28.499999999999996 in binary, which the templates print as "29"
    /// (as people would) but plain rounding makes 28, and a true "29%" then failed the number check.
    /// </remarks>
    private static string Format(double v, int decimals) =>
        Math.Round(Math.Round(v, 9), decimals, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"0,16" (Finnish) and "0.16" are the same number; "73" and "73.0" too.</summary>
    private static string Normalise(string raw) =>
        double.Parse(raw.Replace(',', '.'), CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\b(he|him|his|himself|she|her|hers|herself)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Gendered();

    [GeneratedRegex(@"(?<![\p{L}\d-])\d+(?:[.,]\d+)?")]
    private static partial Regex Numbers();
}
