// Runs a generated match through the whole Second Ball IQ pipeline: analytics engine → triage → Tactical Analyst →
// Narrator → Validator → grounding → publish, and prints every insight with its trace.
//
//   dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match --offline        no models: templates, no gates
//   dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match                  live: Foundry chat model + Decision-1
//   dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match --from 45 --to 65 --personas casual-en,casual-fi
//   dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match --ask --at 70     Ask Second Ball IQ: a fixed question set
//   dotnet run --project tools/MatchLens.Pipeline.Cli -- demo-match --plant number    fault drill: an error in every moment's Analyst draft
//
// Live settings: MATCHLENS_DECISION_ENDPOINT, MATCHLENS_DECISION_KEY, MATCHLENS_DECISION_DEPLOYMENT (Decision-1) and
// MATCHLENS_CHAT_DEPLOYMENT (a chat model in the same Foundry resource). Insights are written to out/<matchId>-insights.jsonl.

using System.Globalization;
using System.Text.Json;
using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using MatchLens.Forge;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0].StartsWith("--"))
{
    Console.WriteLine("Usage: pipeline <scenario> [--seed N] [--offline] [--from MIN] [--to MIN] [--personas casual-en,analyst-en,broadcast-en,casual-fi] [--include-milestones] [--plant number|claim]");
    return 2;
}

Scenario? scenario = Scenarios.Find(args[0]);
if (scenario is null)
{
    Console.Error.WriteLine($"Unknown scenario '{args[0]}'. Scenarios: {string.Join(", ", Scenarios.All.Select(s => s.Id))}");
    return 2;
}

int seed = IntArg("--seed") ?? scenario.DefaultSeed;
int fromSec = (IntArg("--from") ?? 0) * 60;
int toSec = (IntArg("--to") ?? 200) * 60;
bool offline = args.Contains("--offline");
bool includeMilestones = args.Contains("--include-milestones");
IReadOnlyList<Persona> personas = ParsePersonas(StringArg("--personas")) ?? Persona.DemoSet;
// Fault drill: plant an error in every non-goal moment's Analyst draft and report which check caught it.
string? plantKind = StringArg("--plant");
if (plantKind is not null && !PlantedError.IsKnown(plantKind))
{
    Console.Error.WriteLine("--plant takes 'number' (caught by the Validator) or 'claim' (caught by Decision-1 grounding).");
    return 1;
}

// --- Connections -------------------------------------------------------------------------------------------------
using var http = new HttpClient();
FoundryConnections connections = FoundryConnections.Offline;
if (!offline)
{
    FoundryChatOptions? chat = FoundryChatOptions.FromEnvironment(out IReadOnlyList<string> chatMissing);
    DecisionOptions? decisions = DecisionOptions.FromEnvironment(out IReadOnlyList<string> decisionMissing);
    if (chat is null || decisions is null)
    {
        Console.Error.WriteLine("Missing settings: " + string.Join(", ", chatMissing.Concat(decisionMissing)));
        Console.Error.WriteLine("Set them, or run with --offline to use templates and skip the gates.");
        return 2;
    }
    connections = new FoundryConnections(chat.CreateChatClient(), new FoundryDecisionClient(http, decisions));
    Console.WriteLine($"Live: chat model '{chat.Deployment}' at {chat.Endpoint}, Decision-1 '{decisions.Deployment}'.");
}
else
{
    Console.WriteLine("Offline: template analyst and narrator, gates skipped.");
}

// --- Ask Second Ball IQ evaluation ------------------------------------------------------------------------------------
// pipeline demo-match --ask [--at 70] [--lang fi]: plays the match to the given minute, asks a fixed set of questions
// (and an off-topic one), and reports how many were answered by the model, checked by Decision-1, or fell back.
if (args.Contains("--ask"))
{
    MatchRecording rec = MatchSimulator.Generate(scenario, seed);
    var frozen = new LiveMatch(rec.Match);
    int at = (IntArg("--at") ?? 70) * 60;
    foreach (MatchEvent e in rec.Events.TakeWhile(e => e.MatchClockSec < at)) frozen.Ingest(e);
    string lang = StringArg("--lang") == "fi" ? "fi" : "en";
    string[] questions = lang == "fi"
        ? ["Kumpi joukkue hallitsee nyt?", "Miksi Northvale vaihtoi pelimuotoa?", "Miten Ravi Sharma on pelannut?",
           "Vertaa viimeistä 10 minuuttia sitä edeltäviin 10 minuuttiin.", "Miten maalit syntyivät?", "Mikä on Ranskan pääkaupunki?"]
        : ["Who is on top right now?", "Why did Northvale change formation?", "How is Ravi Sharma playing?",
           "Compare the last 10 minutes with the 10 before.", "How did the goals happen?", "Has Harbor City sat deeper since scoring?",
           "Which player has won the ball high most often?", "What is the capital of France?"];
    var analyst = new MatchAnalyst(connections.Chat, connections.Decisions);
    var asked = new List<AskResult>();
    Console.WriteLine($"Ask Second Ball IQ on {rec.Match.MatchId} at {NarrativeText.Clock(frozen.ClockSec)} ({lang}):\n");
    foreach (string q in questions)
    {
        AskResult r = await analyst.AskAsync(new AskRequest(q, Audience.Analyst, lang), frozen, [], [], CancellationToken.None);
        asked.Add(r);
        Console.WriteLine($"Q: {q}");
        Console.WriteLine($"   {r.Status}, {r.Check}, {(r.FromModel ? "model" : "fallback")}, {r.Milliseconds / 1000:0.0} s, tools {string.Join("+", r.ToolsUsed)}");
        if (r.Problems.Count > 0) Console.WriteLine($"   problems: {string.Join("; ", r.Problems)}");
        Console.WriteLine($"   A: {r.Answer}");
        if (r.Actions.Count > 0) Console.WriteLine($"   actions: {string.Join(", ", r.Actions.Select(x => x.Kind))}");
        Console.WriteLine();
    }
    Console.WriteLine($"Answered by the model: {asked.Count(a => a.FromModel)}/{asked.Count}; checked by Decision-1: {asked.Count(a => a.Check == "checked")}; " +
                      $"fallbacks: {asked.Count(a => !a.FromModel)}; average {asked.Average(a => a.Milliseconds) / 1000:0.0} s.");
    return 0;
}

// --- Replay ------------------------------------------------------------------------------------------------------
MatchRecording recording = MatchSimulator.Generate(scenario, seed);
var match = new LiveMatch(recording.Match);
var pipeline = new InsightPipeline(match, connections);
var drillPipeline = plantKind is null || !personas.Any(p => p.Key == "Analyst-en")
    ? null : new InsightPipeline(match, connections, new PipelineSettings { Plant = new PlantedError(plantKind) });
var results = new List<InsightWork>();
Console.WriteLine($"{recording.Match.MatchId}: {recording.Match.Home.Name} vs {recording.Match.Away.Name}, " +
                  $"personas {string.Join(", ", personas.Select(p => p.Key))}\n");

foreach (MatchEvent e in recording.Events)
{
    foreach (Candidate c in match.Ingest(e))
    {
        if (c.DetectedAtSec < fromSec || c.DetectedAtSec >= toSec) continue;
        if (c.Kind == InsightKind.PlayerMilestone && !includeMilestones) continue;

        InsightWork work = await (c.Kind != InsightKind.Goal && drillPipeline is not null ? drillPipeline : pipeline).ProcessAsync(c, personas);
        results.Add(work);
        Print(work, recording.Match);
    }
}

// --- Summary -----------------------------------------------------------------------------------------------------
List<Insight> insights = results.SelectMany(r => r.Insights).ToList();
Console.WriteLine(new string('=', 100));
Console.WriteLine($"{results.Count} candidates → {insights.Count} insights: " +
                  string.Join(", ", insights.GroupBy(i => i.Publish!.Decision).Select(g => $"{g.Count()} {g.Key}")) +
                  $"; {results.Count(r => r.Stopped)} dropped by triage.");
Console.WriteLine($"Template fallbacks: analyst {results.Count(r => r.AnalysisFromTemplate)}/{results.Count}, " +
                  $"narrator {results.Sum(r => r.TemplateDrafts.Count)}/{results.Sum(r => r.Personas.Count)}.");
Console.WriteLine($"Validator rejections: {results.Sum(r => r.Trace.Count(t => t.Step == "Validator" && t.Outcome == "rejected"))}; " +
                  $"grounding rejections: {results.Sum(r => r.Trace.Count(t => t.Step == "Grounding" && t.Outcome == "rejected"))}.");
List<PlantedRecord> drills = results.Select(r => r.Planted).OfType<PlantedRecord>().ToList();
if (drills.Count > 0)
{
    Console.WriteLine($"Fault drill ({plantKind}): {drills.Count} planted, {drills.Count(d => d.CaughtBy is not null)} caught " +
                      $"({string.Join(", ", drills.Where(d => d.CaughtBy is not null).GroupBy(d => d.CaughtBy).Select(g => $"{g.Count()} by {g.Key}"))}); " +
                      $"{drills.Count(d => d.CaughtBy is null)} missed and dropped at publish. Published planted drafts: " +
                      $"{results.Count(r => r.Planted is { } d && r.Insights.Any(i => i.Audience == Audience.Analyst && i.Language == "en" && i.Publish?.Decision == PublishMode.Auto && i.Summary.Contains(d.Planted, StringComparison.Ordinal)))}.");
}
foreach (IGrouping<string, TraceStep> step in results.SelectMany(r => r.Trace).GroupBy(t => t.Step))
{
    List<double> ms = step.Select(t => t.Milliseconds).OrderBy(x => x).ToList();
    Console.WriteLine($"  {step.Key,-16} p50 {ms[ms.Count / 2],7:0} ms   max {ms[^1],7:0} ms");
}

Directory.CreateDirectory("out");
string path = Path.Combine("out", $"{recording.Match.MatchId}-insights.jsonl");
await File.WriteAllLinesAsync(path, insights.Select(i => JsonSerializer.Serialize(i, ContractJson.Compact)));
Console.WriteLine($"\nWrote {insights.Count} insights to {path}");
return 0;

// --- Helpers -----------------------------------------------------------------------------------------------------
static void Print(InsightWork work, MatchInfo info)
{
    Candidate c = work.Candidate;
    Console.WriteLine(new string('-', 100));
    Console.WriteLine($"{NarrativeText.Clock(c.DetectedAtSec)}  {c.Kind}  {info.Team(c.TeamId).Name}  ({c.CandidateId})");
    foreach (TraceStep t in work.Trace)
        Console.WriteLine($"   · {t.Step,-16} {t.Outcome,-10} {t.Milliseconds,7:0} ms  {Shorten(t.Detail, 120)}");
    if (work.Analysis is { } a && !work.AnalysisFromTemplate)
        Console.WriteLine($"   Analyst: {a.WhyItMatters}");
    foreach (Insight i in work.Insights)
    {
        string figures = string.Join(", ", i.Figures.Select(f => $"{f.Label} {f.Value:0.##}{f.Unit}"));
        Console.WriteLine($"   [{i.Publish!.Decision,-6}] {i.Audience}-{i.Language}: \"{i.Headline}\" — {i.Summary}" +
                          (figures.Length > 0 ? $"  [{figures}]" : ""));
    }
}

static string Shorten(string? text, int max) =>
    text is null ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";

static IReadOnlyList<Persona>? ParsePersonas(string? spec)
{
    if (string.IsNullOrWhiteSpace(spec)) return null;
    return spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part =>
        {
            string[] bits = part.Split('-');
            Audience audience = Enum.Parse<Audience>(bits[0], ignoreCase: true);
            return new Persona(audience, bits.Length > 1 ? bits[1] : "en");
        })
        .ToList();
}

int? IntArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : null;
}

string? StringArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
