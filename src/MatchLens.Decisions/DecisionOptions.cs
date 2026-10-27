namespace MatchLens.Decisions;

/// <summary>Connection settings for a Microsoft-Decision-1 deployment in Microsoft Foundry.</summary>
public sealed class DecisionOptions
{
    public const string SystemOnePath = "providers/microsoft/v1/systemone";

    /// <summary>
    /// The Foundry resource root, e.g. https://my-resource.services.ai.azure.com,
    /// or the full invocation URL ending in /providers/microsoft/v1/systemone.
    /// </summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The Foundry resource key, sent in the api-key header.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Your deployment name (not necessarily the catalog name "Microsoft-Decision-1").</summary>
    public required string Deployment { get; init; }

    /// <summary>
    /// Per attempt. Decision-1 normally answers in 0.2 to 1.5 s; the first live pipeline run saw one request hang for
    /// the full 10 s, so a hung request is abandoned early and tried once more.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Attempts per request: a timeout, a network error, HTTP 429 or a 5xx is retried once.</summary>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>The URL requests are posted to.</summary>
    public Uri InvocationUri =>
        Endpoint.AbsolutePath.TrimEnd('/').EndsWith("/v1/systemone", StringComparison.OrdinalIgnoreCase)
            ? Endpoint
            : new Uri(new Uri(Endpoint.GetLeftPart(UriPartial.Authority) + "/"), SystemOnePath);

    /// <summary>
    /// Reads MATCHLENS_DECISION_ENDPOINT, MATCHLENS_DECISION_KEY and MATCHLENS_DECISION_DEPLOYMENT.
    /// Returns null and lists what is missing when any are unset.
    /// </summary>
    public static DecisionOptions? FromEnvironment(out IReadOnlyList<string> missing)
    {
        string? endpoint = Environment.GetEnvironmentVariable("MATCHLENS_DECISION_ENDPOINT");
        string? key = Environment.GetEnvironmentVariable("MATCHLENS_DECISION_KEY");
        string? deployment = Environment.GetEnvironmentVariable("MATCHLENS_DECISION_DEPLOYMENT");

        var gaps = new List<string>();
        if (string.IsNullOrWhiteSpace(endpoint)) gaps.Add("MATCHLENS_DECISION_ENDPOINT");
        if (string.IsNullOrWhiteSpace(key)) gaps.Add("MATCHLENS_DECISION_KEY");
        if (string.IsNullOrWhiteSpace(deployment)) gaps.Add("MATCHLENS_DECISION_DEPLOYMENT");
        missing = gaps;
        if (gaps.Count > 0) return null;

        // https everywhere, except plain http to localhost for the offline mock server.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException("MATCHLENS_DECISION_ENDPOINT must be an absolute https URL (http only for localhost).");
        }

        return new DecisionOptions { Endpoint = uri, ApiKey = key!, Deployment = deployment! };
    }
}
