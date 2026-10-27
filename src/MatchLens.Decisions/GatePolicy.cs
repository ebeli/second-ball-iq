namespace MatchLens.Decisions;

/// <summary>What a gate lets happen next.</summary>
public enum GateOutcome
{
    /// <summary>Confident enough to act automatically.</summary>
    Proceed,

    /// <summary>Not confident: hold for the broadcast editor. Never escalated to an LLM on the live path.</summary>
    Review,

    /// <summary>Confident that nothing should happen.</summary>
    Drop,
}

public sealed record GateVerdict(GateOutcome Outcome, string Reason);

/// <summary>
/// Thresholds for acting on a decision. Defaults are starting points only;
/// they get tuned against the ground-truth scenarios before the demo.
/// </summary>
/// <param name="MinTop">Minimum probability of the selected option (τ).</param>
/// <param name="MinMargin">Minimum gap to the runner-up option (δ).</param>
/// <param name="YesAbove">Yes/no probability at or above which a statement counts as true.</param>
/// <param name="NoBelow">Yes/no probability at or below which a statement counts as false.</param>
public sealed record GateThresholds(
    double MinTop = 0.70,
    double MinMargin = 0.20,
    double YesAbove = 0.80,
    double NoBelow = 0.20);

public static class GatePolicy
{
    /// <summary>
    /// A choice is acted on only when it is both likely (top ≥ τ) and clearly ahead (margin ≥ δ).
    /// A confident "nothing" option means drop; anything unclear goes to review.
    /// </summary>
    public static GateVerdict Evaluate(ChoiceAnswer answer, GateThresholds thresholds, string? nothingOption = null)
    {
        bool confident = answer.TopProbability >= thresholds.MinTop && answer.Margin >= thresholds.MinMargin;
        string numbers = $"top {answer.Choice} {answer.TopProbability:0.00}, margin {answer.Margin:0.00} over {answer.RunnerUp}";

        if (!confident)
        {
            return new GateVerdict(GateOutcome.Review,
                $"{numbers}; below τ={thresholds.MinTop:0.00} / δ={thresholds.MinMargin:0.00}");
        }

        return nothingOption is not null && answer.Choice == nothingOption
            ? new GateVerdict(GateOutcome.Drop, numbers)
            : new GateVerdict(GateOutcome.Proceed, numbers);
    }

    /// <summary>Yes means proceed, no means drop, the uncertain middle band goes to review.</summary>
    public static GateVerdict Evaluate(YesNoAnswer answer, GateThresholds thresholds)
    {
        string numbers = $"p(yes) {answer.Probability:0.00}";
        if (answer.Probability >= thresholds.YesAbove) return new GateVerdict(GateOutcome.Proceed, numbers);
        if (answer.Probability <= thresholds.NoBelow) return new GateVerdict(GateOutcome.Drop, numbers);
        return new GateVerdict(GateOutcome.Review,
            $"{numbers}; between {thresholds.NoBelow:0.00} and {thresholds.YesAbove:0.00}");
    }

    /// <summary>
    /// Several yes/no checks that must all pass: any clear "no" drops, all clear "yes" proceeds,
    /// anything else goes to review. The reason names the failing or uncertain checks.
    /// </summary>
    public static GateVerdict EvaluateAll(
        IReadOnlyDictionary<string, YesNoAnswer> checks,
        GateThresholds thresholds,
        IReadOnlyDictionary<string, string>? descriptions = null)
    {
        string Describe(string name) => descriptions is not null && descriptions.TryGetValue(name, out string? text) ? text : name;
        string List(IEnumerable<KeyValuePair<string, YesNoAnswer>> items) =>
            string.Join("; ", items.Select(c => $"{Describe(c.Key)} (p {c.Value.Probability:0.00})"));

        var failed = checks.Where(c => c.Value.Probability <= thresholds.NoBelow).ToList();
        if (failed.Count > 0) return new GateVerdict(GateOutcome.Drop, "failed: " + List(failed));

        var unsure = checks.Where(c => c.Value.Probability < thresholds.YesAbove).ToList();
        if (unsure.Count > 0) return new GateVerdict(GateOutcome.Review, "unsure: " + List(unsure));

        return new GateVerdict(GateOutcome.Proceed,
            $"all {checks.Count} checks passed, lowest p {checks.Values.Min(c => c.Probability):0.00}");
    }

    /// <summary>A refusal is never acted on automatically.</summary>
    public static GateVerdict Evaluate(DecisionAnswer answer, GateThresholds thresholds, string? nothingOption = null) =>
        answer switch
        {
            ChoiceAnswer choice => Evaluate(choice, thresholds, nothingOption),
            YesNoAnswer yesNo => Evaluate(yesNo, thresholds),
            RefusalAnswer => new GateVerdict(GateOutcome.Review, "model refused"),
            _ => new GateVerdict(GateOutcome.Review, $"no policy for {answer.GetType().Name}"),
        };
}
