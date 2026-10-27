using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatchLens.Decisions;

/// <summary>Evaluates typed questions against a state description.</summary>
public interface IDecisionClient
{
    /// <param name="state">What the model judges: a short plain-language description, not raw numbers.</param>
    /// <param name="questions">Named questions; all are answered from the same state in one request.</param>
    Task<DecisionResult> DecideAsync(
        string state,
        IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls Microsoft-Decision-1 through the Foundry System One endpoint:
/// POST {resource}/providers/microsoft/v1/systemone with {model, state, questions}.
/// </summary>
public sealed class FoundryDecisionClient : IDecisionClient
{
    private const int MaxErrorBodyChars = 600;

    private readonly HttpClient _http;
    private readonly DecisionOptions _options;

    public FoundryDecisionClient(HttpClient http, DecisionOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The exact JSON body that would be sent. Used by the spike's dry run and by logs.</summary>
    public string BuildRequestBody(string state, IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        var questionsJson = new JsonObject();
        foreach (KeyValuePair<string, DecisionQuestion> question in questions)
        {
            questionsJson[question.Key] = question.Value.ToJson();
        }

        return new JsonObject
        {
            ["model"] = _options.Deployment,
            ["state"] = state,
            ["questions"] = questionsJson,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<DecisionResult> DecideAsync(
        string state,
        IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state)) throw new ArgumentException("State is required.", nameof(state));
        if (questions.Count == 0) throw new ArgumentException("At least one question is required.", nameof(questions));

        string requestBody = BuildRequestBody(state, questions);
        var stopwatch = Stopwatch.StartNew();
        string body = "";
        int attempts = Math.Max(1, _options.MaxAttempts);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                body = await SendOnceAsync(requestBody, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (DecisionException ex) when (ex.Transient && attempt < attempts && !cancellationToken.IsCancellationRequested)
            {
                // Hung or overloaded: one fresh attempt usually answers in well under a second.
            }
        }
        stopwatch.Stop();

        (Dictionary<string, DecisionAnswer> answers, DecisionUsage? usage) = Parse(body, questions);
        return new DecisionResult(answers, usage, stopwatch.Elapsed, body);
    }

    private async Task<string> SendOnceAsync(string requestBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.InvocationUri)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string excerpt = body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] + "..." : body;
                throw new DecisionException($"Decision endpoint returned HTTP {status}: {excerpt}")
                {
                    StatusCode = status,
                    Transient = status == 429 || status >= 500,
                };
            }
            return body;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DecisionException($"Decision request timed out after {_options.Timeout.TotalSeconds:0.#} s.") { Transient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new DecisionException("Decision request failed: " + ex.Message, ex) { Transient = true };
        }
    }

    internal static (Dictionary<string, DecisionAnswer> Answers, DecisionUsage? Usage) Parse(
        string body, IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("answers", out JsonElement answers) ||
                answers.ValueKind != JsonValueKind.Object)
            {
                throw new DecisionException("Response has no 'answers' object.");
            }

            var result = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DecisionQuestion> question in questions)
            {
                if (!answers.TryGetProperty(question.Key, out JsonElement answer) || answer.ValueKind != JsonValueKind.Object)
                {
                    throw new DecisionException($"Response has no answer for question '{question.Key}'.");
                }
                result[question.Key] = ParseAnswer(question.Key, question.Value, answer);
            }

            return (result, ParseUsage(root));
        }
        catch (JsonException ex)
        {
            throw new DecisionException("Response was not valid JSON.", ex);
        }
    }

    private static DecisionAnswer ParseAnswer(string name, DecisionQuestion question, JsonElement answer)
    {
        string type = RequiredString(answer, "type", name);
        if (type == "refusal") return new RefusalAnswer(name);
        if (type != question.Type)
        {
            throw new DecisionException($"Answer '{name}' has type '{type}', expected '{question.Type}'.");
        }

        switch (question)
        {
            case ChoiceQuestion choice:
            {
                Dictionary<string, double> probabilities = Distribution(answer, name);
                foreach (string key in probabilities.Keys)
                {
                    if (!choice.Options.ContainsKey(key))
                        throw new DecisionException($"Answer '{name}' returned unknown option '{key}'.");
                }
                string selected = RequiredString(answer, "choice", name);
                if (!choice.Options.ContainsKey(selected))
                    throw new DecisionException($"Answer '{name}' selected unknown option '{selected}'.");
                return new ChoiceAnswer(name, selected, probabilities, OptionalNumber(answer, "confidence"));
            }

            case ScoreQuestion score:
            {
                Dictionary<string, double> byIndex = Distribution(answer, name);
                var levels = new List<double>(score.Levels.Count);
                for (int i = 0; i < score.Levels.Count; i++)
                {
                    levels.Add(byIndex.TryGetValue(i.ToString(CultureInfo.InvariantCulture), out double p) ? p : 0);
                }
                double value = OptionalNumber(answer, "score")
                    ?? levels.Select((p, i) => p * i).Sum();
                return new ScoreAnswer(name, value, levels, OptionalNumber(answer, "confidence"));
            }

            case YesNoQuestion:
                return new YesNoAnswer(name, OptionalNumber(answer, "noul")
                    ?? throw new DecisionException($"Answer '{name}' has no 'noul' probability."));

            default:
                throw new DecisionException($"Unsupported question type {question.GetType().Name}.");
        }
    }

    private static Dictionary<string, double> Distribution(JsonElement answer, string name)
    {
        if (!answer.TryGetProperty("probabilities", out JsonElement entries) || entries.ValueKind != JsonValueKind.Object)
        {
            throw new DecisionException($"Answer '{name}' has no 'probabilities' object.");
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (JsonProperty entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetDouble(out double p) || p < 0 || p > 1)
            {
                throw new DecisionException($"Answer '{name}' has an invalid probability for '{entry.Name}'.");
            }
            result[entry.Name] = p;
        }

        if (result.Count == 0 || Math.Abs(result.Values.Sum() - 1) > 1e-3)
        {
            throw new DecisionException($"Answer '{name}' probabilities do not sum to 1.");
        }
        return result;
    }

    private static string RequiredString(JsonElement element, string property, string name) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new DecisionException($"Answer '{name}' is missing '{property}'.");

    private static double? OptionalNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number)
            ? number
            : null;

    private static DecisionUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return new DecisionUsage(Count(usage, "input_tokens"), Count(usage, "output_tokens"));

        static long? Count(JsonElement container, string property) =>
            container.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long count)
                ? count
                : null;
    }
}
