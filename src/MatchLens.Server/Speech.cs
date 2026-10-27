using System.Collections.Concurrent;
using System.Security;
using System.Text;
using MatchLens.Contracts;

namespace MatchLens.Server;

/// <summary>
/// Reads insights and recaps aloud with Azure AI Speech neural voices. One voice per audience and language, so the
/// commentator sounds like a commentator; Finnish uses Finnish voices. Audio is cached per text, so every viewer
/// of the same insight shares one synthesis.
/// </summary>
/// <remarks>
/// Settings: MATCHLENS_SPEECH_REGION (e.g. swedencentral) uses the regional endpoint; MATCHLENS_SPEECH_ENDPOINT sets a
/// full URL instead. Without either, the Foundry resource's own domain is tried
/// (https://&lt;resource&gt;.cognitiveservices.azure.com/tts/cognitiveservices/v1). MATCHLENS_SPEECH_KEY defaults to
/// MATCHLENS_DECISION_KEY, since a Foundry resource includes Speech. When synthesis fails, the browser falls back to
/// its own voice.
/// </remarks>
public sealed class SpeechService
{
    private const int MaxCached = 500;

    private readonly HttpClient _http;
    private readonly ILogger<SpeechService> _log;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly ModelBudget? _budget;

    public SpeechService(IHttpClientFactory http, ILogger<SpeechService> log, ModelBudget? budget = null)
    {
        _budget = budget;
        _http = http.CreateClient("speech");
        _http.Timeout = TimeSpan.FromSeconds(15);
        _log = log;
        Key = Environment.GetEnvironmentVariable("MATCHLENS_SPEECH_KEY") ?? Environment.GetEnvironmentVariable("MATCHLENS_DECISION_KEY");
        Endpoint = ResolveEndpoint();
        if (Endpoint is not null) log.LogInformation("Speech: {Endpoint}", Endpoint);
    }

    public Uri? Endpoint { get; }

    private string? Key { get; }

    public bool Available => Endpoint is not null && !string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// The voice for an audience and language. The commentator persona uses an English voice that supports Azure's
    /// "excited" speaking style; Finnish voices have no styles, so their energy comes from pace and pitch.
    /// </summary>
    public static Voice VoiceFor(Audience audience, string language) => (language, audience) switch
    {
        ("fi", Audience.Broadcast) => new("fi-FI-HarriNeural", "fi-FI", "+10%", "+5%"),
        ("fi", Audience.Analyst) => new("fi-FI-SelmaNeural", "fi-FI", "+0%", "+0%"),
        ("fi", Audience.Beginner) => new("fi-FI-NooraNeural", "fi-FI", "-5%", "+0%"),
        ("fi", _) => new("fi-FI-NooraNeural", "fi-FI", "+0%", "+0%"),
        (_, Audience.Beginner) => new("en-US-AvaNeural", "en-US", "-5%", "+0%"),
        (_, Audience.Broadcast) => new("en-US-GuyNeural", "en-US", "+6%", "+0%", "excited", 1.2),
        (_, Audience.Analyst) => new("en-GB-SoniaNeural", "en-GB", "+0%", "+0%"),
        _ => new("en-US-AndrewNeural", "en-US", "+0%", "+0%"),
    };

    /// <summary>The live commentator: the more that is happening, the more excited the voice.</summary>
    public static Voice CallVoice(string language, int intensity) => language == "fi"
        ? new("fi-FI-HarriNeural", "fi-FI", intensity >= 3 ? "+18%" : "+12%", intensity >= 3 ? "+12%" : "+6%")
        : new("en-US-GuyNeural", "en-US", intensity >= 3 ? "+12%" : "+8%", intensity >= 3 ? "+8%" : "+2%",
            intensity >= 3 ? "shouting" : "excited", intensity >= 3 ? 1.6 : 1.3);

    public sealed record Voice(string Name, string Locale, string Rate, string Pitch, string? Style = null, double StyleDegree = 1);

    public Task<(byte[]? Audio, string? Error)> SynthesizeAsync(string text, Audience audience, string language, CancellationToken ct) =>
        SynthesizeAsync(text, VoiceFor(audience, language), ct);

    /// <summary>MP3 audio, or null with the reason when synthesis is not possible.</summary>
    public async Task<(byte[]? Audio, string? Error)> SynthesizeAsync(string text, Voice voice, CancellationToken ct)
    {
        if (!Available) return (null, "Speech is not configured.");
        string cacheKey = $"{voice}|{text}";
        if (_cache.TryGetValue(cacheKey, out byte[]? cached)) return (cached, null);
        // Cached audio is free; a new synthesis counts against the speech budget (the browser's own voice takes over).
        if (_budget is not null && !_budget.TryTake(ModelKind.Speech)) return (null, "The voice budget is spent for now.");

        string body = $"<prosody rate='{voice.Rate}' pitch='{voice.Pitch}'>{SecurityElement.Escape(text)}</prosody>";
        if (voice.Style is not null)
            body = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"<mstts:express-as style='{voice.Style}' styledegree='{voice.StyleDegree:0.0}'>{body}</mstts:express-as>");
        string ssml =
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='{voice.Locale}'>" +
            $"<voice name='{voice.Name}'>{body}</voice></speak>";
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ssml+xml");
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", Key);
        request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        request.Headers.TryAddWithoutValidation("User-Agent", "SecondBallIQ");

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                string reply = await response.Content.ReadAsStringAsync(ct);
                string error = $"Speech returned HTTP {(int)response.StatusCode} {reply[..Math.Min(reply.Length, 200)]}".Trim();
                _log.LogWarning("{Error}", error);
                return (null, error);
            }
            byte[] audio = await response.Content.ReadAsByteArrayAsync(ct);
            if (_cache.Count >= MaxCached) _cache.Clear();
            _cache[cacheKey] = audio;
            return (audio, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Speech failed: {Message}", ex.Message);
            return (null, "Speech failed: " + ex.Message);
        }
    }

    private static Uri? ResolveEndpoint()
    {
        string? endpoint = Environment.GetEnvironmentVariable("MATCHLENS_SPEECH_ENDPOINT");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? explicitUri)) return explicitUri;

        string? region = Environment.GetEnvironmentVariable("MATCHLENS_SPEECH_REGION");
        if (!string.IsNullOrWhiteSpace(region))
            return new Uri($"https://{region.Trim().ToLowerInvariant()}.tts.speech.microsoft.com/cognitiveservices/v1");

        // A Foundry resource (name.services.ai.azure.com) also answers on name.cognitiveservices.azure.com.
        if (Uri.TryCreate(Environment.GetEnvironmentVariable("MATCHLENS_DECISION_ENDPOINT"), UriKind.Absolute, out Uri? foundry))
        {
            string name = foundry.Host.Split('.')[0];
            return new Uri($"https://{name}.cognitiveservices.azure.com/tts/cognitiveservices/v1");
        }
        return null;
    }
}
