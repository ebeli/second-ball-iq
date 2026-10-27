using MatchLens.Analytics;
using MatchLens.Agents;
using MatchLens.Contracts;

namespace MatchLens.Server;

/// <summary>What the browser sends to start a match.</summary>
/// <param name="Speed">Match seconds per real second; 10 plays 90 minutes in 9.</param>
/// <param name="StartMinute">Events before this minute are replayed instantly and their candidates skipped.</param>
/// <param name="Offline">Templates instead of models, gates skipped; also used when no Foundry settings exist.</param>
public sealed record StartRequest(
    string ScenarioId,
    int? Seed = null,
    double Speed = 10,
    int StartMinute = 0,
    bool Offline = false,
    bool IncludeMilestones = true,
    bool PresentationHolds = true);

public sealed record ScenarioView(string Id, string Description, int DefaultSeed, bool Real = false, string? Title = null, string? Attribution = null);

public enum SessionStatus
{
    Running,
    Paused,
    Finishing,
    Finished,
    Stopped,
}

public sealed record SessionInfo(
    string MatchId,
    string ScenarioId,
    string Description,
    int Seed,
    MatchInfo Match,
    bool Live,
    string Models,
    IReadOnlyList<string> Personas,
    int StartMinute,
    int EndClockSec,
    string? Attribution = null);

/// <summary>Sent a few times a second while the match runs.</summary>
/// <param name="ModelsPaused">A live match whose model budget is spent for now: new moments use offline templates.</param>
public sealed record TickView(int ClockSec, int Period, int HomeGoals, int AwayGoals, SessionStatus Status, double Speed, int QueueLength, bool ModelsPaused = false);

/// <summary>
/// A match event for drawing. Coordinates are converted to the home team's frame (home attacks towards X = 100),
/// so the browser never has to know about acting-team frames. Pass length and difficulty come from
/// <c>BallMetrics</c>, so the browser shows the engine's numbers rather than working out its own.
/// </summary>
public sealed record PitchEvent(
    string Id,
    int Seq,
    int ClockSec,
    int Period,
    MatchEventType Type,
    string TeamId,
    string? PlayerId,
    double? X,
    double? Y,
    double? EndX,
    double? EndY,
    EventOutcome? Outcome,
    double? Xg,
    ShotResult? ShotResult,
    bool HighRecovery,
    SetPiece? SetPiece = null,
    double? BallSpeedKmh = null,
    double? PassMetres = null,
    int? PassDifficulty = null);

public enum CandidateStatus
{
    Queued,
    Processing,
    Done,
    Failed,
}

/// <summary>A detector candidate and what the agents did with it.</summary>
public sealed record CandidateView(
    string CandidateId,
    InsightKind Kind,
    string TeamId,
    string? PlayerId,
    int DetectedAtSec,
    int FromSec,
    int ToSec,
    IReadOnlyDictionary<string, double> Facts,
    IReadOnlyList<string> EvidenceEventIds,
    string Evidence,
    string WindowSummary,
    CandidateStatus Status,
    string? Outcome = null,
    string? TriageChoice = null,
    string? Analysis = null,
    bool AnalysisFromTemplate = false,
    IReadOnlyList<TraceStep>? Trace = null,
    double? TotalMs = null,
    IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>A pipeline step as it happens.</summary>
public sealed record StepView(string CandidateId, TraceStep Step);

public enum InsightStatus
{
    /// <summary>Published automatically: every gate passed.</summary>
    Live,

    /// <summary>Waiting in the editor queue.</summary>
    Pending,

    /// <summary>Published by an editor, as written or edited.</summary>
    Approved,

    Rejected,

    /// <summary>Failed validation or grounding; kept for the audit view.</summary>
    Dropped,
}

public sealed record InsightView(
    Insight Insight,
    string CandidateId,
    string PersonaKey,
    InsightStatus Status,
    string? Note,
    DateTimeOffset UpdatedAt,
    string? OriginalHeadline = null,
    string? OriginalSummary = null);

/// <summary>A ground-truth expectation and the candidate that found it, if any.</summary>
public sealed record ExpectationView(
    InsightKind Kind,
    string TeamId,
    string? PlayerId,
    int FromSec,
    int ToSec,
    string Rule,
    bool Verified,
    string? HitCandidateId);

public sealed record SessionSnapshot(
    SessionInfo Info,
    TickView Tick,
    IReadOnlyList<PitchEvent> Events,
    IReadOnlyList<CandidateView> Candidates,
    IReadOnlyList<InsightView> Insights,
    IReadOnlyList<ExpectationView> GroundTruth,
    IReadOnlyList<RecapView> Recaps,
    IReadOnlyList<Prediction> Predictions,
    IReadOnlyList<Call> Calls,
    IReadOnlyList<ShapeReport>? Shapes = null,
    IReadOnlyList<TacticalTest>? Tests = null,
    IReadOnlyList<FaultDrillView>? Drills = null,
    IReadOnlyList<RunnerView>? Running = null,
    AutoEventView? AutoEvents = null,
    IReadOnlyList<TagView>? Tags = null,
    IReadOnlyList<PassView>? Passes = null,
    ControlView? Control = null,
    XtView? Xt = null,
    IReadOnlyList<CoachDecision>? Coaches = null);

/// <summary>A what-if: the team's pitch control before and after the change, both maps, the players moved (board units), and Decision-1's view.</summary>
public sealed record WhatIfView(
    string TeamId, MatchLens.Analytics.Spatial.WhatIfChange Change, double Metres, double T,
    double ControlBefore, double ControlAfter, double FinalThirdBefore, double FinalThirdAfter, double OwnThirdBefore, double OwnThirdAfter,
    int Cols, int Rows, double[] HomeBefore, double[] HomeAfter, IReadOnlyList<MovedView> Moved, double? HelpProbability, string Source,
    IReadOnlyList<MovedView> Others);

public sealed record MovedView(string PlayerId, double FromX, double FromY, double ToX, double ToY);

public sealed record WhatIfRequest(string TeamId, MatchLens.Analytics.Spatial.WhatIfChange Change, double? Metres);

/// <summary>The expected-threat grid (attacking frame, row by row from y 0), for drawing the threat map.</summary>
public sealed record XtView(int Cols, int Rows, IReadOnlyList<double> Values, string FittedOn)
{
    public static XtView Current { get; } = new(MatchLens.Analytics.Spatial.ExpectedThreat.Cols, MatchLens.Analytics.Spatial.ExpectedThreat.Rows,
        MatchLens.Analytics.Spatial.ExpectedThreat.Grid.Select(v => Math.Round(v, 4)).ToList(), MatchLens.Analytics.Spatial.XtGrid.FittedOn);
}

/// <summary>
/// A fault drill's result: the error planted in one draft of a moment and the check that caught it (null: none did,
/// and the draft was dropped at publish).
/// </summary>
public sealed record FaultDrillView(
    string CandidateId, InsightKind Kind, string Clock, string PlantKind, string PersonaKey, string Planted, string? CaughtBy,
    DateTimeOffset At);

public sealed record PlantRequest(string Kind);

public sealed record RecapMomentView(string InsightId, int ClockSec, InsightKind Kind, string TeamId, string Text);

/// <summary>A half-time or full-time recap for one persona, written by the Recap Writer agent or its template.</summary>
public sealed record RecapView(
    string RecapId,
    string PersonaKey,
    bool FullTime,
    int ClockSec,
    int HomeGoals,
    int AwayGoals,
    string Headline,
    IReadOnlyList<string> Paragraphs,
    IReadOnlyList<RecapMomentView> KeyMoments,
    bool FromTemplate,
    IReadOnlyList<string> Problems,
    double Milliseconds);

public sealed record EditRequest(string Headline, string Summary, string? Editor = null);

public sealed record EditorResult(bool Ok, InsightView? Insight, IReadOnlyList<string> Problems);

public sealed record SpeedRequest(double Speed);
