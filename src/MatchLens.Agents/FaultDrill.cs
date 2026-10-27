using MatchLens.Analytics;

namespace MatchLens.Agents;

/// <summary>
/// A fault drill: an error planted on purpose in one narrator draft, to show (and measure) that the checks catch it.
/// <list type="bullet">
/// <item><c>number</c>: a figure the engine never measured is added to the text. The Validator (code) must reject it.</item>
/// <item><c>claim</c>: a sentence the evidence does not support is added. Decision-1 grounding must reject it or hold it.</item>
/// </list>
/// A planted draft is never published: if no check catches it, Publish drops it and the drill is recorded as missed.
/// </summary>
public sealed record PlantedError(string Kind, string PersonaKey = "Analyst-en")
{
    public const string Number = "number";
    public const string Claim = "claim";

    public static bool IsKnown(string? kind) => kind is Number or Claim;
}

/// <summary>What was planted in one moment and which check caught it (null: none did).</summary>
public sealed record PlantedRecord(string Kind, string PersonaKey, string Planted, string? CaughtBy);

public static class FaultDrill
{
    /// <summary>The draft with the error added, and a short description of what was planted.</summary>
    public static (NarrativeDraft Draft, string Planted) Plant(PlantedError error, NarrativeDraft draft, Candidate candidate, LiveMatch match)
    {
        string addition;
        if (error.Kind == PlantedError.Number)
        {
            // A whole number that no measured fact, score or minute of this moment allows.
            HashSet<string> allowed = Validator.AllowedNumbers(candidate, match);
            int n = 7 + (int)Math.Round(candidate.Facts.Values.Select(Math.Abs).DefaultIfEmpty(0).Max() * 3);
            while (allowed.Contains(n.ToString(System.Globalization.CultureInfo.InvariantCulture))) n++;
            addition = $"Second Ball IQ counts {n} of them in the last ten minutes.";
        }
        else
        {
            addition = "It began straight after the opposing captain was sent off.";
        }
        var planted = new NarrativeDraft
        {
            Headline = draft.Headline,
            Summary = (draft.Summary.TrimEnd() + " " + addition).Trim(),
            FigureKeys = draft.FigureKeys.ToList(),
        };
        return (planted, addition);
    }
}
