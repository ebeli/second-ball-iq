namespace MatchLens.Decisions;

/// <summary>
/// Triage gate: given a plain-language summary of the current match window, which kind of insight
/// (if any) deserves airtime now? Runs per window or when a detector raises a candidate, never per event.
/// Spike (Oct 15): prose summaries 18/18 correct; compact numbers failed one window, so state is prose.
/// </summary>
public sealed class TriageGate
{
    public const string QuestionName = "triage";
    public const string Nothing = "none";

    /// <summary>Option ids match the detector kinds in the insight contract.</summary>
    public static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>
    {
        ["pressingShift"] = "One team has started winning the ball high up the pitch repeatedly, changing the flow of the match.",
        ["possessionIllusion"] = "One team has most of the ball but is creating little or no danger with it.",
        ["turnoverToChance"] = "A ball recovery has just led directly to a shot or a goal.",
        ["playerMilestone"] = "One player has just reached a notable personal milestone or standout performance.",
        [Nothing] = "Nothing in this window is worth interrupting the viewer for.",
    };

    public static readonly ChoiceQuestion Question = new(
        "Which development in this match window most deserves to be shown to viewers right now?",
        Options);

    public static IReadOnlyDictionary<string, DecisionQuestion> Questions { get; } =
        new Dictionary<string, DecisionQuestion> { [QuestionName] = Question };

    private readonly IDecisionClient _client;
    private readonly GateThresholds _thresholds;

    public TriageGate(IDecisionClient client, GateThresholds? thresholds = null)
    {
        _client = client;
        _thresholds = thresholds ?? new GateThresholds();
    }

    public async Task<(ChoiceAnswer Answer, GateVerdict Verdict, DecisionResult Raw)> EvaluateAsync(
        string windowSummary, CancellationToken cancellationToken = default)
    {
        DecisionResult result = await _client.DecideAsync(windowSummary, Questions, cancellationToken).ConfigureAwait(false);
        if (result.Answers[QuestionName] is RefusalAnswer)
        {
            throw new DecisionException("Triage question was refused.");
        }
        ChoiceAnswer answer = result.Get<ChoiceAnswer>(QuestionName);
        return (answer, GatePolicy.Evaluate(answer, _thresholds, Nothing), result);
    }
}

/// <summary>
/// Grounding gate: does a drafted insight follow from its evidence? Runs after the deterministic Validator.
/// Asks four narrow yes/no checks in one request instead of one broad question, so the editor sees which
/// check failed. Spike v1 (one strict question over raw events) rejected a true claim at p=0.06; the evidence
/// must state derived facts (counts, recovery-to-shot links, what did not happen) in plain language.
/// Run 2 confirmed it: with derived-facts evidence no true claim was blocked and no false claim passed;
/// the four checks without that evidence did worse than v1.
/// Oct 15, first live pipeline run: the countsMatch check rejected even fact-built templates (p 0.01 to 0.29), so it
/// was removed. Numbers belong to the deterministic Validator, which checks them exactly; in the spike every false
/// claim was also caught by another check (events 0.01 to 0.02, fair reading 0.00 to 0.04).
/// </summary>
public sealed class GroundingGate
{
    public static readonly IReadOnlyDictionary<string, string> CheckDescriptions = new Dictionary<string, string>
    {
        ["eventsListed"] = "every event the claim describes appears in the evidence",
        ["outcomesMatch"] = "every outcome (shot, save, goal, miss) is described as in the evidence",
        ["fairReading"] = "a neutral analyst would agree with the overall point",
    };

    public static IReadOnlyDictionary<string, DecisionQuestion> Questions { get; } = new Dictionary<string, DecisionQuestion>
    {
        ["eventsListed"] = new YesNoQuestion(
            "Does every event that the CLAIM describes (ball recoveries, shots, goals, penalties, cards, fouls) appear in the EVIDENCE?"),
        ["outcomesMatch"] = new YesNoQuestion(
            "Does the CLAIM describe the result of each event the same way the EVIDENCE does? For example, a saved or missed shot is not a goal."),
        ["fairReading"] = new YesNoQuestion(
            "Would a neutral football analyst who read only the EVIDENCE agree with the overall point of the CLAIM?"),
    };

    /// <summary>
    /// For reads measured over many minutes (a formation, a role, a style, missed passes, control of space) the claim
    /// describes a pattern, not single events, and "every event the claim describes" does not fit: in the first full
    /// live run (Oct 26) it scored 0.00 to 0.15 for true formation and role reads built from the engine's own facts,
    /// while the other two checks behaved as for moments. Those reads are checked on outcomes and fair reading only; so is a
    /// milestone ("50 passes" is a count over the match), which scored 0.01 to 0.15 in the second live run.
    /// </summary>
    public static IReadOnlyDictionary<string, DecisionQuestion> PatternQuestions { get; } = new Dictionary<string, DecisionQuestion>
    {
        ["outcomesMatch"] = Questions["outcomesMatch"],
        ["fairReading"] = Questions["fairReading"],
    };

    /// <summary>
    /// Spike run 2 (Oct 15, 8 hand-written claims x 3): every false claim had a check at or below 0.03, every true
    /// claim had its lowest check at or above 0.47. Pass at 0.40 (headroom below 0.47), drop at 0.15, review between;
    /// replaying run 2 at these values gives 24/24. Re-check on Oct 23 against claims the Narrator actually writes.
    /// </summary>
    public static readonly GateThresholds DefaultThresholds = new(YesAbove: 0.40, NoBelow: 0.15);

    /// <summary>Spike run 2 saw p95 about 1 s and one 4.4 s outlier; a slow check must not stall the live path.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2.5);

    private readonly IDecisionClient _client;
    private readonly GateThresholds _thresholds;
    private readonly TimeSpan _timeout;

    public GroundingGate(IDecisionClient client, GateThresholds? thresholds = null, TimeSpan? timeout = null)
    {
        _client = client;
        _thresholds = thresholds ?? DefaultThresholds;
        _timeout = timeout ?? DefaultTimeout;
    }

    public static string BuildState(string evidence, string claim) =>
        $"EVIDENCE:\n{evidence.Trim()}\n\nCLAIM:\n{claim.Trim()}";

    /// <summary>
    /// Never throws for service problems: a timeout, an error or a refusal becomes a Review verdict, so a claim is
    /// held for the editor rather than published unchecked or lost. Raw is null when the service gave no answer.
    /// </summary>
    public Task<(IReadOnlyDictionary<string, YesNoAnswer> Checks, GateVerdict Verdict, DecisionResult? Raw)> EvaluateAsync(
        string evidence, string claim, CancellationToken cancellationToken = default) =>
        EvaluateAsync(evidence, claim, pattern: false, cancellationToken);

    /// <param name="pattern">True for a read measured over minutes: checked without "every event listed" (see <see cref="PatternQuestions"/>).</param>
    public async Task<(IReadOnlyDictionary<string, YesNoAnswer> Checks, GateVerdict Verdict, DecisionResult? Raw)> EvaluateAsync(
        string evidence, string claim, bool pattern, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, DecisionQuestion> questions = pattern ? PatternQuestions : Questions;
        var none = new Dictionary<string, YesNoAnswer>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        DecisionResult result;
        try
        {
            result = await _client.DecideAsync(BuildState(evidence, claim), questions, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (none, new GateVerdict(GateOutcome.Review, $"grounding timed out after {_timeout.TotalSeconds:0.#} s"), null);
        }
        catch (DecisionException ex)
        {
            return (none, new GateVerdict(GateOutcome.Review, "grounding unavailable: " + ex.Message), null);
        }

        if (result.Answers.Values.Any(a => a is RefusalAnswer))
        {
            return (none, new GateVerdict(GateOutcome.Review, "a grounding check was refused"), result);
        }
        var checks = questions.Keys.ToDictionary(name => name, name => result.Get<YesNoAnswer>(name));
        return (checks, GatePolicy.EvaluateAll(checks, _thresholds, CheckDescriptions), result);
    }
}
