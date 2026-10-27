using System.ClientModel;
using MatchLens.Decisions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MatchLens.Agents;

/// <summary>
/// Connection to a generative model deployed in Microsoft Foundry, reached through the resource's
/// OpenAI-compatible endpoint (https://&lt;resource&gt;.services.ai.azure.com/openai/v1/).
/// </summary>
public sealed class FoundryChatOptions
{
    public required Uri Endpoint { get; init; }
    public required string ApiKey { get; init; }
    public required string Deployment { get; init; }

    /// <summary>
    /// Reads MATCHLENS_CHAT_DEPLOYMENT (required), and MATCHLENS_CHAT_ENDPOINT / MATCHLENS_CHAT_KEY, which default to
    /// the Decision-1 resource (MATCHLENS_DECISION_ENDPOINT + /openai/v1/, MATCHLENS_DECISION_KEY) since both models
    /// live in the same Foundry resource.
    /// </summary>
    public static FoundryChatOptions? FromEnvironment(out IReadOnlyList<string> missing)
    {
        string? deployment = Environment.GetEnvironmentVariable("MATCHLENS_CHAT_DEPLOYMENT");
        string? endpoint = Environment.GetEnvironmentVariable("MATCHLENS_CHAT_ENDPOINT");
        string? key = Environment.GetEnvironmentVariable("MATCHLENS_CHAT_KEY")
                      ?? Environment.GetEnvironmentVariable("MATCHLENS_DECISION_KEY");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            string? decisionEndpoint = Environment.GetEnvironmentVariable("MATCHLENS_DECISION_ENDPOINT");
            if (Uri.TryCreate(decisionEndpoint, UriKind.Absolute, out Uri? root))
                endpoint = root.GetLeftPart(UriPartial.Authority) + "/openai/v1/";
        }

        var gaps = new List<string>();
        if (string.IsNullOrWhiteSpace(deployment)) gaps.Add("MATCHLENS_CHAT_DEPLOYMENT");
        if (string.IsNullOrWhiteSpace(endpoint)) gaps.Add("MATCHLENS_CHAT_ENDPOINT (or MATCHLENS_DECISION_ENDPOINT)");
        if (string.IsNullOrWhiteSpace(key)) gaps.Add("MATCHLENS_CHAT_KEY (or MATCHLENS_DECISION_KEY)");
        missing = gaps;
        if (gaps.Count > 0) return null;

        return new FoundryChatOptions { Endpoint = new Uri(endpoint!), ApiKey = key!, Deployment = deployment! };
    }

    public IChatClient CreateChatClient() =>
        new OpenAIClient(new ApiKeyCredential(ApiKey), new OpenAIClientOptions { Endpoint = Endpoint })
            .GetChatClient(Deployment)
            .AsIChatClient();
}

/// <summary>Everything the live pipeline needs to reach Foundry. Null members mean "run that step offline".</summary>
public sealed record FoundryConnections(IChatClient? Chat, IDecisionClient? Decisions)
{
    public static FoundryConnections Offline { get; } = new(null, null);

    public bool IsOffline => Chat is null && Decisions is null;
}
