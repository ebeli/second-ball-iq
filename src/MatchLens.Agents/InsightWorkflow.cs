using System.Diagnostics;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Microsoft.Agents.AI.Workflows;

namespace MatchLens.Agents;

/// <summary>
/// The shared state that flows through the workflow for one candidate. Every executor reads it, adds its own result
/// and a trace step, and hands it on; the trace is what the editor view and the audit log show.
/// </summary>
public sealed class InsightWork(Candidate candidate, IReadOnlyList<Persona> personas)
{
    public Candidate Candidate { get; } = candidate;
    public IReadOnlyList<Persona> Personas { get; } = personas;

    public GateVerdict? Triage { get; set; }
    public string? TriageChoice { get; set; }
    public bool NeedsEditor { get; set; }
    public bool Stopped { get; set; }
    public string? StopReason { get; set; }

    public AnalysisResult? Analysis { get; set; }
    public bool AnalysisFromTemplate { get; set; }

    public Dictionary<string, NarrativeDraft> Drafts { get; } = new();
    public HashSet<string> TemplateDrafts { get; } = [];
    public Dictionary<string, ValidationResult> Validation { get; } = new();
    public Dictionary<string, GateVerdict> Grounding { get; } = new();
    public Dictionary<string, double?> GroundingLowestP { get; } = new();

    public List<Insight> Insights { get; } = [];
    public List<TraceStep> Trace { get; } = [];

    /// <summary>A fault drill in this moment: what was planted, in which draft, and which check caught it.</summary>
    public PlantedRecord? Planted { get; set; }

    internal void Caught(string personaKey, string by)
    {
        if (Planted is { CaughtBy: null } p && p.PersonaKey == personaKey) Planted = p with { CaughtBy = by };
    }

    /// <summary>Called as each step finishes, so a live view can show the agents at work. Must be thread-safe.</summary>
    public Action<InsightWork, TraceStep>? OnStep { get; init; }

    internal void Log(string step, string outcome, Stopwatch sw, string? detail = null) =>
        Add(new TraceStep(step, outcome, Math.Round(sw.Elapsed.TotalMilliseconds, 1), detail));

    internal void Add(TraceStep step)
    {
        lock (Trace) Trace.Add(step);
        OnStep?.Invoke(this, step);
    }
}

/// <summary>Builds and runs the Second Ball IQ multi-agent workflow on Microsoft Agent Framework.</summary>
/// <remarks>
/// triage (Decision-1) → Tactical Analyst (agent + engine tools) → Narrator (agent, per persona) →
/// Validator (code) → grounding (Decision-1) → publish. A triage drop short-circuits straight to publish.
/// Every model step falls back to a deterministic template, so a failing model never loses a true moment.
/// </remarks>
public sealed class InsightPipeline
{
    private readonly Workflow _workflow;

    public InsightPipeline(LiveMatch match, FoundryConnections connections, PipelineSettings? settings = null)
    {
        settings ??= new PipelineSettings();
        TacticalAnalyst? analyst = connections.Chat is null ? null : new TacticalAnalyst(connections.Chat, new AnalystTools(match));
        Narrator? narrator = connections.Chat is null ? null : new Narrator(connections.Chat);
        TriageGate? triageGate = connections.Decisions is null ? null : new TriageGate(connections.Decisions);
        GroundingGate? groundingGate = connections.Decisions is null ? null : new GroundingGate(connections.Decisions);

        var triage = new TriageExecutor(triageGate);
        var analyse = new AnalystExecutor(analyst, match, settings);
        var narrate = new NarratorExecutor(narrator, match, settings);
        var validate = new ValidatorExecutor(match);
        var ground = new GroundingExecutor(groundingGate, match);
        var publish = new PublishExecutor(match);

        _workflow = new WorkflowBuilder(triage)
            .AddEdge<InsightWork>(triage, analyse, condition: w => w is not null && !w.Stopped)
            .AddEdge<InsightWork>(triage, publish, condition: w => w is not null && w.Stopped)
            .AddEdge(analyse, narrate)
            .AddEdge(narrate, validate)
            .AddEdge(validate, ground)
            .AddEdge(ground, publish)
            .WithOutputFrom(publish)
            .Build();
    }

    public async Task<InsightWork> ProcessAsync(
        Candidate candidate, IReadOnlyList<Persona> personas, CancellationToken cancellationToken = default,
        Action<InsightWork, TraceStep>? onStep = null)
    {
        var work = new InsightWork(candidate, personas) { OnStep = onStep };
        await using Run run = await InProcessExecution.RunAsync(_workflow, work);
        foreach (WorkflowEvent evt in run.NewEvents)
        {
            if (evt is WorkflowOutputEvent output && output.As<InsightWork>() is { } result) return result;
            if (evt is ExecutorFailedEvent failed)
                work.Add(new TraceStep(failed.ExecutorId, "executor failed", 0, failed.Data?.ToString()));
        }
        return work;
    }
}

public sealed record PipelineSettings
{
    public TimeSpan AnalystTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan NarratorTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A fault drill: an error to plant in one narrator draft (see <see cref="FaultDrill"/>).</summary>
    public PlantedError? Plant { get; init; }
}

// ----------------------------------------------------------------------------------------------------
// Executors
// ----------------------------------------------------------------------------------------------------

/// <summary>Decision-1 triage on the neutral window summary. Goals always proceed.</summary>
internal sealed class TriageExecutor(TriageGate? gate) : Executor<InsightWork, InsightWork>("Triage")
{
    /// <summary>
    /// When triage names this very moment's kind, clearly ahead of the rest, it agrees the moment is the story; being
    /// below τ then is doubt about airtime, not about the read, which grounding still checks. In the first full live
    /// run (Oct 26) a turnover-to-chance at 0.67 with a 0.48 margin went to an editor nobody was watching.
    /// </summary>
    public const double AgreeMinTop = 0.55;

    public override async ValueTask<InsightWork> HandleAsync(InsightWork work, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Candidate c = work.Candidate;
        if (c.Facts.TryGetValue("goal", out double goal) && goal == 1)
        {
            work.Log("Triage", "proceed", sw, "goals always get airtime");
            return work;
        }
        if (c.Kind is InsightKind.TacticalShift or InsightKind.PlayerRole or InsightKind.BetterPass or InsightKind.Territory)
        {
            // Shape, roles, missed passes and control of space are measured over 10 to 25 minutes, not a moment;
            // triage's window summary does not describe them and its options do not include them. These are
            // rate-limited by their detectors and still pass the Validator and grounding.
            work.Log("Triage", "proceed", sw, "read measured over minutes; triage judges moments");
            return work;
        }
        if (gate is null)
        {
            work.Log("Triage", "skipped", sw, "offline");
            return work;
        }

        try
        {
            var (answer, verdict, _) = await gate.EvaluateAsync(c.WindowSummary, cancellationToken);
            work.Triage = verdict;
            work.TriageChoice = answer.Choice;
            string expected = TriageOptionId(c.Kind);
            if (verdict.Outcome == GateOutcome.Drop)
            {
                work.Stopped = true;
                work.StopReason = "triage: nothing worth airtime (" + verdict.Reason + ")";
            }
            else if (verdict.Outcome == GateOutcome.Proceed && answer.Choice != expected && answer.Choice != TriageGate.Nothing &&
                     c.Kind == InsightKind.TurnoverToChance)
            {
                // Triage is confident a bigger story is running (e.g. a chance inside a pressing spell): this moment
                // folds into that story, which has its own candidate, instead of crowding the screen.
                work.Stopped = true;
                work.StopReason = $"part of a bigger story: triage chose {answer.Choice} ({verdict.Reason})";
            }
            // A different kind of story (a milestone during a press) is not part of that story: it goes ahead, and
            // a confident "none" has already dropped it above.
            else if (verdict.Outcome == GateOutcome.Review &&
                     !(answer.Choice == expected && answer.TopProbability >= AgreeMinTop && answer.Margin >= new GateThresholds().MinMargin))
            {
                work.NeedsEditor = true;   // unsure: a human decides
            }
            work.Log("Triage", work.Stopped ? (answer.Choice == expected || answer.Choice == TriageGate.Nothing ? "drop" : "fold") : work.NeedsEditor ? "review" : "proceed", sw,
                $"{answer.Choice} ({verdict.Reason})");
        }
        catch (DecisionException ex)
        {
            work.NeedsEditor = true;
            work.Log("Triage", "unavailable", sw, ex.Message);
        }
        return work;
    }

    internal static string TriageOptionId(InsightKind kind) =>
        char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];
}

internal sealed class AnalystExecutor(TacticalAnalyst? analyst, LiveMatch match, PipelineSettings settings)
    : Executor<InsightWork, InsightWork>("TacticalAnalyst")
{
    public override async ValueTask<InsightWork> HandleAsync(InsightWork work, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (analyst is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.AnalystTimeout);
            try
            {
                AnalysisResult raw = await analyst.AnalyseAsync(work.Candidate, match.Info, timeout.Token);
                (work.Analysis, string? cleaned) = Sanitise(raw, work.Candidate);
                work.Log("TacticalAnalyst", "ok", sw, (cleaned is null ? "" : $"[{cleaned}] ") + work.Analysis.WhyItMatters);
                return work;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                work.Log("TacticalAnalyst", "fallback", sw, ex.GetType().Name + ": " + ex.Message);
            }
        }
        work.Analysis = Templates.Analysis(work.Candidate);
        work.AnalysisFromTemplate = true;
        if (analyst is null) work.Log("TacticalAnalyst", "template", sw, "offline");
        return work;
    }

    /// <summary>
    /// Removes what the analyst cannot back up (unknown players, unknown or distant events, fact keys that do not
    /// exist) instead of failing the whole moment for it. Returns what was removed, for the trace.
    /// </summary>
    private (AnalysisResult Clean, string? Removed) Sanitise(AnalysisResult a, Candidate c)
    {
        var removed = new List<string>();
        var players = a.PlayerIds.Where(p => match.Info.Player(p) is not null).Distinct().ToList();
        removed.AddRange(a.PlayerIds.Except(players).Select(p => "player " + p));
        var events = a.CitedEventIds.Where(id => match.Event(id) is { } e &&
            (c.EvidenceEventIds.Contains(id) ||
             (e.MatchClockSec >= c.FromSec - Validator.CitationSlackSec && e.MatchClockSec <= c.ToSec + Validator.CitationSlackSec)))
            .Distinct().ToList();
        removed.AddRange(a.CitedEventIds.Except(events).Select(id => "event " + id));
        var facts = a.KeyFactKeys.Where(c.Facts.ContainsKey).Distinct().ToList();
        removed.AddRange(a.KeyFactKeys.Except(facts).Select(k => "fact " + k));
        if (events.Count == 0) events = c.EvidenceEventIds.Take(5).ToList();

        var clean = new AnalysisResult { WhyItMatters = a.WhyItMatters, PlayerIds = players, CitedEventIds = events, KeyFactKeys = facts };
        return (clean, removed.Count == 0 ? null : "removed " + string.Join(", ", removed));
    }
}

internal sealed class NarratorExecutor(Narrator? narrator, LiveMatch match, PipelineSettings settings)
    : Executor<InsightWork, InsightWork>("Narrator")
{
    public override async ValueTask<InsightWork> HandleAsync(InsightWork work, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        AnalysisResult analysis = work.Analysis ?? Templates.Analysis(work.Candidate);

        // One call per persona, in parallel.
        var tasks = work.Personas.Select(async persona =>
        {
            NarrativeDraft? draft = null;
            string? error = "offline";
            if (narrator is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.NarratorTimeout);
                try
                {
                    draft = await narrator.WriteAsync(work.Candidate, analysis, persona, match.Info, timeout.Token);
                    error = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
            }
            // Logged as each persona finishes, so the live view shows the drafts arriving one by one.
            work.Log("Narrator", draft is null ? "template" : "ok", sw, $"{persona.Key}{(error is null ? "" : ": " + error)}");
            return (persona, draft);
        }).ToList();

        foreach (var (persona, draft) in await Task.WhenAll(tasks))
        {
            if (draft is not null)
            {
                work.Drafts[persona.Key] = Validator.Tidy(draft, work.Candidate);
            }
            else
            {
                work.Drafts[persona.Key] = Templates.Draft(work.Candidate, persona, match.Info);
                work.TemplateDrafts.Add(persona.Key);
            }
        }

        // Fault drill: the error goes into one finished draft (a model draft or, offline, the template), which then
        // faces the same checks as any other draft.
        if (settings.Plant is { } plant && work.Drafts.TryGetValue(plant.PersonaKey, out NarrativeDraft? target))
        {
            (NarrativeDraft planted, string what) = FaultDrill.Plant(plant, target, work.Candidate, match);
            work.Drafts[plant.PersonaKey] = planted;
            work.TemplateDrafts.Remove(plant.PersonaKey);
            work.Planted = new PlantedRecord(plant.Kind, plant.PersonaKey, what, null);
            work.Log("Planted error", plant.Kind, sw, $"{plant.PersonaKey}: \"{what}\"");
        }
        return work;
    }
}

/// <summary>Deterministic checks; a draft that fails is replaced by the template, which is checked again.</summary>
internal sealed class ValidatorExecutor(LiveMatch match) : Executor<InsightWork, InsightWork>("Validator")
{
    public override ValueTask<InsightWork> HandleAsync(InsightWork work, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        AnalysisResult analysis = work.Analysis ?? Templates.Analysis(work.Candidate);
        foreach (Persona persona in work.Personas)
        {
            ValidationResult result = Validator.Check(work.Candidate, analysis, work.Drafts[persona.Key], match);
            if (!result.Passed && !work.TemplateDrafts.Contains(persona.Key))
            {
                work.Log("Validator", "rejected", sw, $"{persona.Key}: {string.Join("; ", result.Problems)}");
                work.Caught(persona.Key, "Validator");
                work.Drafts[persona.Key] = Templates.Draft(work.Candidate, persona, match.Info);
                work.TemplateDrafts.Add(persona.Key);
                // The analyst's citations may be what failed; templates cite the candidate's own evidence.
                result = Validator.Check(work.Candidate, Templates.Analysis(work.Candidate), work.Drafts[persona.Key], match);
            }
            work.Validation[persona.Key] = result;
            work.Log("Validator", result.Passed ? "pass" : "fail", sw,
                $"{persona.Key}{(result.Problems.Count == 0 ? "" : ": " + string.Join("; ", result.Problems))}");
        }
        return ValueTask.FromResult(work);
    }
}

/// <summary>
/// Decision-1 grounding of each draft against the evidence. A model draft that is dropped is replaced by the
/// template, which is grounded again, so a bad narration never loses a true moment.
/// </summary>
internal sealed class GroundingExecutor(GroundingGate? gate, LiveMatch match) : Executor<InsightWork, InsightWork>("Grounding")
{
    public override async ValueTask<InsightWork> HandleAsync(InsightWork work, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (gate is null)
        {
            work.Log("Grounding", "skipped", sw, "offline");
            return work;
        }

        // Reads measured over minutes, and a milestone counted over the match ("50 passes"), describe a pattern, not
        // single events (see GroundingGate.PatternQuestions).
        bool pattern = work.Candidate.Kind is InsightKind.TacticalShift or InsightKind.PlayerRole or InsightKind.BetterPass or InsightKind.Territory
            or InsightKind.PlayerMilestone;
        foreach (Persona persona in work.Personas)
        {
            NarrativeDraft draft = work.Drafts[persona.Key];
            var (checks, verdict, _) = await gate.EvaluateAsync(work.Candidate.Evidence, draft.Headline + " " + draft.Summary, pattern, cancellationToken);
            if (verdict.Outcome == GateOutcome.Drop && !work.TemplateDrafts.Contains(persona.Key))
            {
                work.Log("Grounding", "rejected", sw, $"{persona.Key}: {verdict.Reason}");
                work.Caught(persona.Key, "Decision-1 grounding");
                draft = Templates.Draft(work.Candidate, persona, match.Info);
                work.Drafts[persona.Key] = draft;
                work.TemplateDrafts.Add(persona.Key);
                (checks, verdict, _) = await gate.EvaluateAsync(work.Candidate.Evidence, draft.Headline + " " + draft.Summary, pattern, cancellationToken);
            }
            if (verdict.Outcome == GateOutcome.Review) work.Caught(persona.Key, "Decision-1 grounding (held for the editor)");
            work.Grounding[persona.Key] = verdict;
            work.GroundingLowestP[persona.Key] = checks.Count == 0 ? null : checks.Values.Min(c => c.Probability);
            work.Log("Grounding", verdict.Outcome.ToString().ToLowerInvariant(), sw, $"{persona.Key}: {verdict.Reason}");
        }
        return work;
    }
}

internal sealed class PublishExecutor(LiveMatch match) : Executor<InsightWork, InsightWork>("Publish")
{
    public override ValueTask<InsightWork> HandleAsync(InsightWork work, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Candidate c = work.Candidate;
        if (work.Stopped)
        {
            work.Log("Publish", "drop", sw, work.StopReason);
            return ValueTask.FromResult(work);
        }

        AnalysisResult analysis = work.Analysis ?? Templates.Analysis(c);
        foreach (Persona persona in work.Personas)
        {
            NarrativeDraft draft = work.Drafts[persona.Key];
            ValidationResult validation = work.Validation[persona.Key];
            GateVerdict? grounding = work.Grounding.GetValueOrDefault(persona.Key);

            // A template is built only from verified facts: if grounding still objects, a human decides. It is never
            // silently dropped, so a true moment (a goal above all) cannot disappear because of a model's doubt.
            bool template = work.TemplateDrafts.Contains(persona.Key);
            // A planted error that no check caught is still never published.
            bool plantedMissed = work.Planted is { CaughtBy: null } p && p.PersonaKey == persona.Key;
            PublishMode mode =
                !validation.Passed || plantedMissed ? PublishMode.Drop
                : grounding?.Outcome == GateOutcome.Drop ? (template ? PublishMode.Editor : PublishMode.Drop)
                : work.NeedsEditor || grounding?.Outcome == GateOutcome.Review ? PublishMode.Editor
                : PublishMode.Auto;

            work.Insights.Add(new Insight
            {
                InsightId = $"ins-{c.CandidateId}-{persona.Key}",
                MatchId = match.Info.MatchId,
                MatchClockSec = c.DetectedAtSec,
                Kind = c.Kind,
                Audience = persona.Audience,
                Language = persona.Language,
                Headline = draft.Headline,
                Summary = draft.Summary,
                TeamIds = [c.TeamId],
                PlayerIds = analysis.PlayerIds.Where(p => match.Info.Player(p) is not null).DefaultIfEmpty(c.PlayerId).OfType<string>().ToList(),
                EvidenceEventIds = (work.AnalysisFromTemplate ? c.EvidenceEventIds : analysis.CitedEventIds.Where(id => match.Event(id) is not null))
                    .DefaultIfEmpty(c.EvidenceEventIds.FirstOrDefault() ?? "").Where(id => id.Length > 0).ToList(),
                Figures = draft.FigureKeys.Select(key => Figure(c, key, persona.Language)).ToList(),
                Validation = new InsightValidation(validation.Evidence, validation.Numbers, work.GroundingLowestP.GetValueOrDefault(persona.Key),
                    validation.Problems.Count == 0 ? null : string.Join("; ", validation.Problems)),
                Publish = new PublishDecision(mode, work.GroundingLowestP.GetValueOrDefault(persona.Key),
                    work.TemplateDrafts.Contains(persona.Key) ? "gate:template" : "gate"),
            });
        }
        if (work.Planted is { } drill)
            work.Log("Fault drill", drill.CaughtBy is null ? "missed" : "caught", sw,
                drill.CaughtBy is null ? $"{drill.PersonaKey}: no check caught it; dropped at publish" : $"{drill.PersonaKey}: caught by {drill.CaughtBy}");
        work.Log("Publish", string.Join(", ", work.Insights.GroupBy(i => i.Publish!.Decision).Select(g => $"{g.Count()} {g.Key}")), sw);
        return ValueTask.FromResult(work);
    }

    /// <summary>Figure values come from the measured facts, never from the model.</summary>
    private static InsightFigure Figure(Candidate c, string key, string language)
    {
        double value = c.Facts[key];
        string? unit = Templates.FigureUnit(key);
        if (unit == "%") value = Math.Round(value * 100, 0);
        return new InsightFigure(Templates.FigureLabel(key, language), Math.Round(value, 2), unit);
    }
}
