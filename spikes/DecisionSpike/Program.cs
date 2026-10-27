// Decision-1 spike: proves we can call Microsoft-Decision-1 from .NET, measures latency, and checks
// whether the triage and grounding gates give sensible, well-separated answers on known cases.
//
//   dotnet run -- --dry-run          print the exact request bodies, no network, no keys needed
//   dotnet run                       run every case once against your deployment
//   dotnet run -- --repeat 3         run each case 3 times (checks answer stability)
//   dotnet run -- --tau 0.6 --delta 0.15
//   dotnet run -- --skip-triage --repeat 3   grounding A/B only
//
// Settings come from MATCHLENS_DECISION_ENDPOINT, MATCHLENS_DECISION_KEY, MATCHLENS_DECISION_DEPLOYMENT.

using System.Globalization;
using System.Text.Json;
using DecisionSpike;
using MatchLens.Decisions;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

bool dryRun = args.Contains("--dry-run");
bool skipTriage = args.Contains("--skip-triage");
int repeat = IntArg("--repeat", 1);
var thresholds = new GateThresholds(MinTop: DoubleArg("--tau", 0.70), MinMargin: DoubleArg("--delta", 0.20));
var groundingThresholds = GroundingGate.DefaultThresholds with
{
    YesAbove = DoubleArg("--pass", GroundingGate.DefaultThresholds.YesAbove),
    NoBelow = DoubleArg("--fail", GroundingGate.DefaultThresholds.NoBelow),
};
string? onlyVariant = StringArg("--grounding");   // e.g. --grounding v2 to skip the v1 / v2-checks baselines

// ---------------------------------------------------------------------------------------------
// Dry run: show exactly what goes over the wire.
// ---------------------------------------------------------------------------------------------
if (dryRun)
{
    var demo = new FoundryDecisionClient(new HttpClient(), new DecisionOptions
    {
        Endpoint = new Uri("https://your-resource.services.ai.azure.com"),
        ApiKey = "<key>",
        Deployment = "<your-deployment-name>",
    });
    Console.WriteLine($"POST https://<your-resource>.services.ai.azure.com/{DecisionOptions.SystemOnePath}");
    Console.WriteLine("header: api-key: <key>\n");
    Console.WriteLine("--- triage request ---");
    Console.WriteLine(demo.BuildRequestBody(Scenarios.Triage[1].Prose, TriageGate.Questions));
    Console.WriteLine("\n--- grounding request ---");
    Console.WriteLine(demo.BuildRequestBody(
        GroundingGate.BuildState(Scenarios.GroundingEvidenceV2, Scenarios.Grounding[0].Claim), GroundingGate.Questions));
    Console.WriteLine("\n--- multi-question request ---");
    Console.WriteLine(demo.BuildRequestBody(Scenarios.Triage[1].Prose, MultiQuestions()));
    return 0;
}

DecisionOptions? options = DecisionOptions.FromEnvironment(out IReadOnlyList<string> missing);
if (options is null)
{
    Console.Error.WriteLine("Missing settings: " + string.Join(", ", missing));
    Console.Error.WriteLine("Set them, or run with --dry-run to see the requests without calling the service.");
    return 2;
}

using var http = new HttpClient();
var client = new FoundryDecisionClient(http, options);
var triage = new TriageGate(client, thresholds);

Console.WriteLine($"Endpoint:   {options.InvocationUri}");
Console.WriteLine($"Deployment: {options.Deployment}");
Console.WriteLine($"Triage:     tau={thresholds.MinTop:0.00} delta={thresholds.MinMargin:0.00}");
Console.WriteLine($"Grounding:  pass when every check >= {groundingThresholds.YesAbove:0.00}, drop when any check <= {groundingThresholds.NoBelow:0.00}");
Console.WriteLine($"Repeat:     {repeat}\n");

// ---------------------------------------------------------------------------------------------
// 1. Connectivity: one call, fail fast with the service's own error message.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("1. Connectivity check");
try
{
    var (answer, verdict, raw) = await triage.EvaluateAsync(Scenarios.Triage[1].Prose);
    Console.WriteLine($"   OK in {raw.Latency.TotalMilliseconds:0} ms -> {answer.Choice} ({verdict.Outcome}: {verdict.Reason})");
    Console.WriteLine($"   raw: {Squash(raw.RawResponse)}\n");
}
catch (DecisionException ex)
{
    Console.Error.WriteLine($"   FAILED: {ex.Message}");
    Console.Error.WriteLine("   Check the endpoint (resource root), the key, the deployment name and that the deployment has finished.");
    return 1;
}

var triageRuns = new List<TriageRun>();
var groundingRuns = new List<GroundingRun>();
var latencies = new List<double>();
long inputTokens = 0;

// ---------------------------------------------------------------------------------------------
// 2. Triage gate: every case, prose vs numbers.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("2. Triage gate (prose vs numbers)");
Console.WriteLine($"   {"case",-30} {"format",-8} {"choice",-19} {"top",5} {"margin",6} {"conf",5} {"verdict",-8} {"ms",5}  result");
foreach (TriageCase c in skipTriage ? Array.Empty<TriageCase>() : Scenarios.Triage)
{
    foreach ((string format, string state) in new[] { ("prose", c.Prose), ("numbers", c.Numbers) })
    {
        for (int i = 0; i < repeat; i++)
        {
            try
            {
                var (answer, verdict, raw) = await triage.EvaluateAsync(state);
                Track(raw);
                var run = new TriageRun(c.Id, format, answer.Choice, answer.TopProbability, answer.Margin, answer.Confidence,
                    answer.Probabilities, verdict.Outcome.ToString(), c.Accepted.Contains(answer.Choice), c.ExpectReview,
                    raw.Latency.TotalMilliseconds, raw.RawResponse);
                triageRuns.Add(run);
                Console.WriteLine($"   {c.Id,-30} {format,-8} {answer.Choice,-19} {answer.TopProbability,5:0.00} {answer.Margin,6:0.00} " +
                                  $"{Fmt(answer.Confidence),5} {verdict.Outcome,-8} {raw.Latency.TotalMilliseconds,5:0}  {Judge(run)}");
            }
            catch (DecisionException ex)
            {
                Console.WriteLine($"   {c.Id,-30} {format,-8} ERROR {ex.Message}");
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// 3. Grounding gate A/B: true claims should proceed, wrong or invented claims drop.
//    v1        = raw event list + one strict question (the first spike's setup)
//    v2-checks = raw event list + four narrow checks      (isolates the question change)
//    v2        = derived-facts evidence + four checks      (the design we intend to keep)
// ---------------------------------------------------------------------------------------------
var groundingVariants = new (string Name, string Evidence, IReadOnlyDictionary<string, DecisionQuestion> Questions)[]
{
    ("v1", Scenarios.GroundingEvidence, Scenarios.GroundingV1Questions),
    ("v2-checks", Scenarios.GroundingEvidence, GroundingGate.Questions),
    ("v2", Scenarios.GroundingEvidenceV2, GroundingGate.Questions),
};
Console.WriteLine("\n3. Grounding gate (e=eventsListed o=outcomesMatch c=countsMatch f=fairReading, g=v1 single question)");
Console.WriteLine($"   {"variant",-9} {"case",-22} {"exp",-4} {"p(yes) per check",-26} {"verdict",-8} {"ms",5}  result");
foreach (var variant in groundingVariants)
{
    if (onlyVariant is not null && variant.Name != onlyVariant) continue;
    foreach (GroundingCase g in Scenarios.Grounding)
    {
        for (int i = 0; i < repeat; i++)
        {
            try
            {
                DecisionResult raw = await client.DecideAsync(GroundingGate.BuildState(variant.Evidence, g.Claim), variant.Questions);
                Track(raw);
                var checks = variant.Questions.Keys.ToDictionary(name => name, name => raw.Get<YesNoAnswer>(name));
                GateVerdict verdict = GatePolicy.EvaluateAll(checks, groundingThresholds, GroundingGate.CheckDescriptions);
                bool correct = g.ExpectedSupported ? verdict.Outcome == GateOutcome.Proceed : verdict.Outcome == GateOutcome.Drop;
                groundingRuns.Add(new GroundingRun(variant.Name, g.Id, g.ExpectedSupported,
                    checks.ToDictionary(c => c.Key, c => c.Value.Probability), verdict.Outcome.ToString(), verdict.Reason, correct,
                    raw.Latency.TotalMilliseconds, raw.RawResponse));
                string perCheck = string.Join(" ", checks.Select(c => $"{c.Key[0]}{c.Value.Probability:0.00}"));
                string result = correct ? "ok" : verdict.Outcome == GateOutcome.Review ? "held for review" : "WRONG";
                Console.WriteLine($"   {variant.Name,-9} {g.Id,-22} {(g.ExpectedSupported ? "yes" : "no"),-4} {perCheck,-26} " +
                                  $"{verdict.Outcome,-8} {raw.Latency.TotalMilliseconds,5:0}  {result}");
            }
            catch (DecisionException ex)
            {
                Console.WriteLine($"   {variant.Name,-9} {g.Id,-22} ERROR {ex.Message}");
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// 4. Several questions in one request (needed for persona relevance later).
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\n4. Multi-question request");
string multiQuestionResult;
try
{
    DecisionResult multi = await client.DecideAsync(Scenarios.Triage[1].Prose, MultiQuestions());
    Track(multi);
    var t = multi.Get<ChoiceAnswer>("triage");
    var y = multi.Get<YesNoAnswer>("pressingTeamIsNorthvale");
    var s = multi.Get<ScoreAnswer>("analystInterest");
    multiQuestionResult = $"supported: triage={t.Choice} ({t.TopProbability:0.00}), pressingTeamIsNorthvale p={y.Probability:0.00}, " +
                          $"analystInterest={s.Score:0.00}/4 in {multi.Latency.TotalMilliseconds:0} ms";
}
catch (DecisionException ex)
{
    multiQuestionResult = "NOT supported, send one question per request: " + ex.Message;
}
Console.WriteLine("   " + multiQuestionResult);

// ---------------------------------------------------------------------------------------------
// Summary and threshold sweep.
// ---------------------------------------------------------------------------------------------
Console.WriteLine("\nSummary");
foreach (string format in new[] { "prose", "numbers" })
{
    List<TriageRun> runs = triageRuns.Where(r => r.Format == format).ToList();
    if (runs.Count == 0) continue;
    int right = runs.Count(r => r.Correct);
    int wrongButAutomatic = runs.Count(r => !r.Correct && r.Verdict != nameof(GateOutcome.Review));
    Console.WriteLine($"   triage {format,-7}: {right}/{runs.Count} correct top choice, {wrongButAutomatic} wrong AND acted on automatically");
}
foreach (var variant in groundingVariants)
{
    List<GroundingRun> runs = groundingRuns.Where(r => r.Variant == variant.Name).ToList();
    if (runs.Count == 0) continue;
    Console.WriteLine($"   grounding {variant.Name,-9}: {runs.Count(r => r.Correct)}/{runs.Count} correct, " +
                      $"true claims blocked {runs.Count(r => r.ExpectedSupported && r.Verdict == nameof(GateOutcome.Drop))}, " +
                      $"false claims let through {runs.Count(r => !r.ExpectedSupported && r.Verdict == nameof(GateOutcome.Proceed))}, " +
                      $"held for review {runs.Count(r => r.Verdict == nameof(GateOutcome.Review))}");
}
if (latencies.Count > 0)
{
    latencies.Sort();
    Console.WriteLine($"   latency       : p50 {Percentile(latencies, 0.50):0} ms, p95 {Percentile(latencies, 0.95):0} ms over {latencies.Count} calls");
    Console.WriteLine($"   input tokens  : {inputTokens} reported in total");
}

if (triageRuns.Count > 0)
{
    Console.WriteLine("\nThreshold sweep for triage (delta fixed at " + thresholds.MinMargin.ToString("0.00") + ")");
    Console.WriteLine($"   {"tau",5} {"acted on",9} {"of those wrong",15} {"held for review",16}");
    foreach (double tau in new[] { 0.5, 0.6, 0.7, 0.8, 0.9 })
    {
        List<TriageRun> acted = triageRuns.Where(r => r.Top >= tau && r.Margin >= thresholds.MinMargin).ToList();
        Console.WriteLine($"   {tau,5:0.00} {acted.Count,9} {acted.Count(r => !r.Correct),15} {triageRuns.Count - acted.Count,16}");
    }
}

// Grounding sweep on v2: the lowest check decides. Shows where true and false claims separate.
List<GroundingRun> v2Runs = groundingRuns.Where(r => r.Variant == "v2").ToList();
if (v2Runs.Count > 0)
{
    Console.WriteLine($"\nThreshold sweep for grounding v2 (drop fixed at <= {groundingThresholds.NoBelow:0.00})");
    Console.WriteLine($"   {"pass >=",7} {"true passed",12} {"true held",10} {"false passed",13}");
    foreach (double pass in new[] { 0.30, 0.40, 0.45, 0.50, 0.60, 0.70, 0.80 })
    {
        int truePassed = v2Runs.Count(r => r.ExpectedSupported && r.Checks.Values.Min() >= pass);
        int trueTotal = v2Runs.Count(r => r.ExpectedSupported);
        int falsePassed = v2Runs.Count(r => !r.ExpectedSupported && r.Checks.Values.Min() >= pass);
        Console.WriteLine($"   {pass,7:0.00} {truePassed,7}/{trueTotal,-4} {trueTotal - truePassed,10} {falsePassed,13}");
    }
    Console.WriteLine($"   lowest check, true claims: {v2Runs.Where(r => r.ExpectedSupported).Min(r => r.Checks.Values.Min()):0.00}; " +
                      $"false claims: highest {v2Runs.Where(r => !r.ExpectedSupported).Max(r => r.Checks.Values.Min()):0.00}");
}

// ---------------------------------------------------------------------------------------------
// Save everything, including raw responses, for the README and later calibration.
// ---------------------------------------------------------------------------------------------
Directory.CreateDirectory("spike-results");
string path = Path.Combine("spike-results", $"decision-spike-{DateTime.Now:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
{
    endpoint = options.InvocationUri.ToString(),
    deployment = options.Deployment,
    thresholds,
    groundingThresholds,
    repeat,
    multiQuestion = multiQuestionResult,
    triage = triageRuns,
    grounding = groundingRuns,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nSaved {path}");
return 0;

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------
void Track(DecisionResult result)
{
    latencies.Add(result.Latency.TotalMilliseconds);
    inputTokens += result.Usage?.InputTokens ?? 0;
}

static IReadOnlyDictionary<string, DecisionQuestion> MultiQuestions() => new Dictionary<string, DecisionQuestion>
{
    ["triage"] = TriageGate.Question,
    ["pressingTeamIsNorthvale"] = new YesNoQuestion("Is Northvale the team that is pressing and winning the ball high up the pitch?"),
    ["analystInterest"] = new ScoreQuestion("How interesting is this window to a tactical analyst?",
        ["not at all", "slightly", "moderately", "very", "extremely"]),
};

static string Judge(TriageRun run)
{
    if (run.ExpectReview) return run.Verdict == nameof(GateOutcome.Review) ? "ok (held for review)" : run.Correct ? "ok-ish (acted on, but plausible)" : "WRONG";
    if (run.Correct) return run.Verdict == nameof(GateOutcome.Review) ? "right but held (thresholds strict?)" : "ok";
    return run.Verdict == nameof(GateOutcome.Review) ? "wrong, safely held" : "WRONG";
}

static double Percentile(List<double> sorted, double q) =>
    sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

static string Fmt(double? value) => value is null ? "-" : value.Value.ToString("0.00");

static string Squash(string json) => json.Length > 400 ? json[..400].ReplaceLineEndings(" ") + "..." : json.ReplaceLineEndings(" ");

int IntArg(string name, int fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int value) && value > 0 ? value : fallback;
}

string? StringArg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

double DoubleArg(string name, double fallback)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length &&
           double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value
        : fallback;
}

namespace DecisionSpike
{
    internal sealed record TriageRun(
        string Case, string Format, string Choice, double Top, double Margin, double? Confidence,
        IReadOnlyDictionary<string, double> Probabilities, string Verdict, bool Correct, bool ExpectReview,
        double LatencyMs, string Raw);

    internal sealed record GroundingRun(
        string Variant, string Case, bool ExpectedSupported, IReadOnlyDictionary<string, double> Checks,
        string Verdict, string Reason, bool Correct, double LatencyMs, string Raw);
}
