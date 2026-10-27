namespace MatchLens.Decisions;

/// <summary>One typed answer, keyed by the question name it answers.</summary>
public abstract record DecisionAnswer(string Name);

/// <summary>Answer to a <see cref="ChoiceQuestion"/>.</summary>
/// <param name="Choice">The option the model selected.</param>
/// <param name="Probabilities">Probability per option; sums to 1.</param>
/// <param name="Confidence">
/// Model-reported confidence. Distinct from the selected option's probability, so both are kept
/// until calibration on our scenarios shows which one predicts correctness better.
/// </param>
public sealed record ChoiceAnswer(
    string Name,
    string Choice,
    IReadOnlyDictionary<string, double> Probabilities,
    double? Confidence) : DecisionAnswer(Name)
{
    /// <summary>Probability of the most likely option.</summary>
    public double TopProbability => Probabilities.Values.Max();

    /// <summary>Gap between the most likely and second most likely option.</summary>
    public double Margin
    {
        get
        {
            double[] ordered = Probabilities.Values.OrderByDescending(p => p).ToArray();
            return ordered.Length < 2 ? ordered[0] : ordered[0] - ordered[1];
        }
    }

    /// <summary>The option with the second highest probability.</summary>
    public string RunnerUp => Probabilities.OrderByDescending(p => p.Value).Skip(1).Select(p => p.Key).FirstOrDefault() ?? "";
}

/// <summary>Answer to a <see cref="ScoreQuestion"/>.</summary>
/// <param name="Score">Model-reported score on the 0..(levels-1) scale.</param>
/// <param name="LevelProbabilities">Probability of each level, lowest first.</param>
public sealed record ScoreAnswer(
    string Name,
    double Score,
    IReadOnlyList<double> LevelProbabilities,
    double? Confidence) : DecisionAnswer(Name);

/// <summary>Answer to a <see cref="YesNoQuestion"/>: probability that the statement is true.</summary>
public sealed record YesNoAnswer(string Name, double Probability) : DecisionAnswer(Name);

/// <summary>The model declined to answer this question.</summary>
public sealed record RefusalAnswer(string Name) : DecisionAnswer(Name);

/// <summary>Token counts the service reported. Missing counts are unknown, not zero.</summary>
public sealed record DecisionUsage(long? InputTokens, long? OutputTokens);

/// <summary>Everything one decision request returned.</summary>
public sealed record DecisionResult(
    IReadOnlyDictionary<string, DecisionAnswer> Answers,
    DecisionUsage? Usage,
    TimeSpan Latency,
    string RawResponse)
{
    public T Get<T>(string name) where T : DecisionAnswer =>
        Answers.TryGetValue(name, out DecisionAnswer? answer) && answer is T typed
            ? typed
            : throw new DecisionException(
                $"Answer '{name}' is missing or is {Answers.GetValueOrDefault(name)?.GetType().Name ?? "absent"}, not {typeof(T).Name}.");
}

/// <summary>Any failure talking to, or reading from, the decision model.</summary>
public sealed class DecisionException : Exception
{
    public DecisionException(string message, Exception? inner = null) : base(message, inner) { }

    public int? StatusCode { get; init; }

    /// <summary>A timeout, network error, HTTP 429 or 5xx: worth one more attempt.</summary>
    public bool Transient { get; init; }
}
