using System.Text.Json.Nodes;

namespace MatchLens.Decisions;

/// <summary>
/// One typed question sent to a System One decision model (Microsoft-Decision-1).
/// Several named questions can be evaluated against the same state in one request.
/// </summary>
public abstract record DecisionQuestion(string Instructions)
{
    /// <summary>Wire name of the question type.</summary>
    public abstract string Type { get; }

    internal virtual JsonObject ToJson() => new()
    {
        ["type"] = Type,
        ["instructions"] = Instructions,
    };
}

/// <summary>Pick one option from a fixed set. Keys are the option ids, values describe them.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Options)
    : DecisionQuestion(Instructions)
{
    public override string Type => "choice";

    internal override JsonObject ToJson()
    {
        JsonObject json = base.ToJson();
        var criteria = new JsonObject();
        foreach (KeyValuePair<string, string> option in Options)
        {
            criteria[option.Key] = option.Value;
        }
        json["criteria"] = criteria;
        return json;
    }
}

/// <summary>Rate against an ordered scale. Levels are listed from lowest (index 0) to highest.</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels)
    : DecisionQuestion(Instructions)
{
    public override string Type => "score";

    internal override JsonObject ToJson()
    {
        JsonObject json = base.ToJson();
        json["criteria"] = new JsonArray(Levels.Select(level => (JsonNode?)JsonValue.Create(level)).ToArray());
        return json;
    }
}

/// <summary>Probability that a statement about the state is true ("noul" on the wire).</summary>
public sealed record YesNoQuestion(string Instructions) : DecisionQuestion(Instructions)
{
    public override string Type => "noul";
}
