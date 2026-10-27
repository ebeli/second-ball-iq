// Mirrors src/MatchLens.Server/Dtos.cs and the Insight v1 contract (camelCase, enums as camelCase strings).

export type InsightKind = 'pressingShift' | 'possessionIllusion' | 'turnoverToChance' | 'playerMilestone' | 'goal'
  | 'tacticalShift' | 'playerRole' | 'territory' | 'betterPass' | 'coachPlan';
export type Audience = 'casual' | 'analyst' | 'broadcast' | 'beginner';
export type PublishMode = 'auto' | 'editor' | 'drop';
export type SessionStatus = 'running' | 'paused' | 'finishing' | 'finished' | 'stopped';
export type CandidateStatus = 'queued' | 'processing' | 'done' | 'failed';
export type InsightStatus = 'live' | 'pending' | 'approved' | 'rejected' | 'dropped';
export type EventType =
  | 'periodStart' | 'periodEnd' | 'kickOff' | 'pass' | 'shot' | 'goal' | 'pressure' | 'tackle'
  | 'interception' | 'possessionChange' | 'foul' | 'card' | 'substitution';

export interface PlayerInfo { playerId: string; name: string; shirtNumber: number; position: string; starter?: boolean }
export interface TeamInfo { teamId: string; name: string; shortName: string; primaryColor: string; players: PlayerInfo[] }
export interface MatchInfo { matchId: string; scenarioId: string; seed: number; competition: string; home: TeamInfo; away: TeamInfo }

export interface ScenarioView { id: string; description: string; defaultSeed: number; real?: boolean; title?: string; attribution?: string }

export interface SessionInfo {
  matchId: string;
  scenarioId: string;
  description: string;
  seed: number;
  match: MatchInfo;
  live: boolean;
  models: string;
  personas: string[];
  startMinute: number;
  endClockSec: number;
  attribution?: string;
}

export interface TickView {
  clockSec: number; period: number; homeGoals: number; awayGoals: number;
  status: SessionStatus; speed: number; queueLength: number;
  /** A live match whose model budget (the public site's spending cap) is spent for now: new moments use templates. */
  modelsPaused?: boolean;
}

export interface PitchEvent {
  id: string; seq: number; clockSec: number; period: number; type: EventType; teamId: string; playerId?: string;
  x?: number; y?: number; endX?: number; endY?: number; outcome?: 'success' | 'fail'; xg?: number;
  shotResult?: 'goal' | 'saved' | 'offTarget' | 'blocked'; highRecovery: boolean; setPiece?: SetPieceKind;
  /** Ball speed of a pass or shot, km/h. */
  ballSpeedKmh?: number;
  /** Pass length in metres and the engine's difficulty rating, 0 to 100 (hard from 60). */
  passMetres?: number; passDifficulty?: number;
}

export type SetPieceKind = 'corner' | 'freeKick' | 'penalty';
export type PredictionKind = SetPieceKind | 'nextGoal';
export interface PredictionOption { id: string; label: string; labelFi: string; probability: number }

/** A call made before something happens (Decision-1 over the taker's history), settled by the event itself. */
export interface Prediction {
  predictionId: string; kind: PredictionKind; clockSec: number; teamId: string; playerId?: string; playerName?: string;
  question: string; questionFi: string; options: PredictionOption[]; pick: string;
  basis: string; basisFi: string; source: string; eventId?: string; settleAtSec?: number;
  outcome?: string; correct?: boolean;
}

export type CallKind = 'kickOff' | 'halfTime' | 'fullTime' | 'goal' | 'bigChance' | 'setPiece' | 'predictionResult' | 'statFlash'
  | 'substitution';

export type PlayerRole = 'goalkeeper' | 'centreBack' | 'fullBack' | 'wingBack' | 'holdingMidfielder' | 'centralMidfielder'
  | 'attackingMidfielder' | 'wideMidfielder' | 'winger' | 'invertedWinger' | 'striker' | 'falseNine' | 'secondStriker'
  | 'shadowStriker';

/** One player's touches over a window, in the team's own frame (0..100 from their goal, 0..100 from their left). */
export interface PlayerShape {
  playerId: string; name: string; shirtNumber: number; listed: string; touches: number; x: number; y: number;
  boxShare: number; role?: PlayerRole;
}

export interface TeamStyleNumbers {
  highPressures: number; opponentPassesInOwnHalf: number; pressures: number; opponentPasses: number;
  longPasses: number; passes: number; averageDefensiveX: number; highPressRate: number; pressRate: number; longBallShare: number;
}

/** A team's shape over a window, measured by the engine. */
export interface ShapeReport {
  teamId: string; fromSec: number; toSec: number; formation?: string; fitMargin: number;
  fitCosts: Record<string, number>; players: PlayerShape[]; style: TeamStyleNumbers;
}

/** A live commentator call. Intensity 1 (aside) to 3 (goal). */
export interface Call {
  callId: string; clockSec: number; kind: CallKind; teamId: string; playerId?: string; intensity: number;
  text: string; textFi: string;
}

export interface PlayerSeason {
  playerId: string; name: string; team: string; matches: number; goals: number; assists: number; shots: number;
  xg: number; passes: number; completedPasses: number; penaltiesTaken: number; penaltiesScored: number;
}

export interface TraceStep { step: string; outcome: string; milliseconds: number; detail?: string }

export interface CandidateView {
  candidateId: string; kind: InsightKind; teamId: string; playerId?: string;
  detectedAtSec: number; fromSec: number; toSec: number;
  facts: Record<string, number>; evidenceEventIds: string[]; evidence: string; windowSummary: string;
  status: CandidateStatus; outcome?: string; triageChoice?: string; analysis?: string; analysisFromTemplate?: boolean;
  trace?: TraceStep[]; totalMs?: number; tags?: Record<string, string>;
}

export interface InsightFigure { label: string; value: number; unit?: string }
export interface Insight {
  insightId: string; matchId: string; matchClockSec: number; displayMs: number; kind: InsightKind;
  audience: Audience; language: string; headline: string; summary: string;
  teamIds: string[]; playerIds: string[]; evidenceEventIds: string[]; figures: InsightFigure[];
  validation?: { evidence: string; numbers: string; groundingP?: number; reason?: string };
  publish?: { decision: PublishMode; p?: number; by: string };
}

export interface InsightView {
  insight: Insight; candidateId: string; personaKey: string; status: InsightStatus; note?: string; updatedAt: string;
  originalHeadline?: string; originalSummary?: string;
}

export interface ExpectationView {
  kind: InsightKind; teamId: string; playerId?: string; fromSec: number; toSec: number; rule: string;
  verified: boolean; hitCandidateId?: string;
}

export interface RecapMomentView { insightId: string; clockSec: number; kind: InsightKind; teamId: string; text: string }

export interface RecapView {
  recapId: string; personaKey: string; fullTime: boolean; clockSec: number; homeGoals: number; awayGoals: number;
  headline: string; paragraphs: string[]; keyMoments: RecapMomentView[]; fromTemplate: boolean; problems: string[];
  milliseconds: number;
}

export interface SessionSnapshot {
  info: SessionInfo; tick: TickView; events: PitchEvent[]; candidates: CandidateView[];
  insights: InsightView[]; groundTruth: ExpectationView[]; recaps?: RecapView[];
  predictions?: Prediction[]; calls?: Call[]; shapes?: ShapeReport[]; tests?: TacticalTest[]; drills?: FaultDrillView[];
  running?: RunnerView[]; autoEvents?: AutoEventView; tags?: TagView[];
  passes?: PassView[]; control?: ControlView; xt?: XtView; coaches?: CoachDecision[];
}

/** Tracking: every player and the ball, in the board's units (percent of the pitch, home attacking towards 100). */
export interface TrackingView {
  t: number; ballX: number; ballY: number; ballZ: number; players: { id: string; x: number; y: number }[];
}

/** One player's running from tracking. */
export interface RunnerView {
  playerId: string; teamId: string; name: string; distanceKm: number; topSpeedKmh: number; speedKmh: number; sprints: number;
  highIntensityKm: number;
}

/** A threshold a player just reached (top speed or a distance milestone), for the tag on the pitch. */
export interface TagView {
  playerId: string; teamId: string; atSec: number; kind: 'speed' | 'distance'; speedKmh: number; distanceKm: number; x: number; y: number;
}

export interface KindScore { true: number; detected: number; matched: number; recall: number; precision: number }

/** Auto-eventing from tracking alone, scored against the event feed. */
export interface AutoEventReport {
  passes: KindScore; shots: KindScore; ballWins: KindScore;
  passOutcomeAgreement: number; receiverAgreement: number; passLengthErrorMetres: number; passSpeedErrorKmh: number;
  pressureAgreement: number; shotSpeedErrorKmh: number; shotResultAgreement: number; goalsTrue: number; goalsFound: number;
}

export interface DetectedEvent {
  kind: 'pass' | 'shot' | 'ballWin'; t: number; playerId: string; teamId: string; toPlayerId?: string; completed?: boolean;
  metres?: number; speedKmh?: number; underPressure?: boolean; shotResult?: string; x: number; y: number;
}

export interface AutoEventView { upToSec: number; report: AutoEventReport; detected: number; recent: DetectedEvent[] }

/** A fault drill: an error planted in one draft of a moment, and the check that caught it (none: dropped at publish). */
export interface FaultDrillView {
  candidateId: string; kind: InsightKind; clock: string; plantKind: 'number' | 'claim'; personaKey: string; planted: string;
  caughtBy?: string; at: string;
}

export type TestStatus = 'open' | 'met' | 'notMet' | 'inconclusive';
/**
 * A tactical read put to the test: the criterion, rule and deadline are locked when the read airs; the match settles
 * it. The estimate is Decision-1's model estimate, not a calibrated probability.
 */
export interface TacticalTest {
  testId: string; candidateId: string; kind: InsightKind; teamId: string; playerId?: string; read: string;
  criterion: string; criterionFi: string; rule: string; opensAtSec: number; deadlineSec: number; lockedAt: string;
  estimate?: number; estimateSource: string;
  target: number; progress: number; progressText: string; progressTextFi: string;
  status: TestStatus; settledAtSec?: number; result?: string; resultFi?: string;
  limits: string; limitsFi: string; eventIds: string[];
}

export interface EditorResult { ok: boolean; insight?: InsightView; problems: string[] }

export interface AskFact { label: string; value: string }
export type AskActionKind = 'rewind' | 'compareShape' | 'showShape' | 'heatMap' | 'openPlayer';
export interface AskAction {
  kind: AskActionKind; label: string; eventIds: string[]; teamId?: string; playerId?: string;
  fromMinute?: number; toMinute?: number; fromMinute2?: number; toMinute2?: number;
}

/** Ask Second Ball IQ: an answer about the match, frozen at the second the question was asked, checked before it is shown. */
export interface AskResult {
  askId: string; clockSec: number; question: string; answer: string;
  status: 'measured' | 'interpretation' | 'insufficientData';
  keyFacts: AskFact[]; citedEventIds: string[]; actions: AskAction[]; followUps: string[]; toolsUsed: string[];
  check: 'checked' | 'unsure' | 'unchecked'; checkNote?: string; problems: string[]; fromModel: boolean; milliseconds: number;
}

export interface PartyMember { memberId: string; name: string; teamId?: string; points: number; simulated: boolean }
export interface PartyOption { id: string; label: string; labelFi: string; votes: number }
export interface PartyPoll {
  pollId: string; candidateId: string; clockSec: number; question: string; questionFi: string; options: PartyOption[];
  closesAtMs: number; closed: boolean; matchLensPick: string; verdict?: string; verdictFi?: string;
  votes?: Record<string, string>;
  /** Tactical-test polls: the test, its deadline, the result once the match settles it, and Decision-1's estimate. */
  testId?: string; deadlineSec?: number; outcome?: 'met' | 'notMet' | 'inconclusive'; estimate?: number;
}
export interface PartyPicks {
  predictionId: string; kind: string; playerName?: string; options: PartyOption[]; matchLensPick: string;
  crowdPick?: string; outcome?: string; rightNames: string[];
  byMember?: Record<string, string>;
}
export interface PartyQuestion {
  questionId: string; text: string; by: string; language: string; votes: number;
  status: 'open' | 'investigating' | 'answered'; answer?: AskResult; atMs: number;
}
export interface PartyState {
  code: string; host: string; members: PartyMember[]; polls: PartyPoll[]; picks: PartyPicks[]; questions: PartyQuestion[]; demo: boolean;
  tifo?: PartyTifo | null;
  messages?: PartyMessage[];
}
/** A fan's chat message in the room feed (screened on the server; simulated fans are marked). */
export interface PartyMessage { messageId: string; memberId: string; name: string; teamId?: string; text: string; atMs: number; simulated: boolean }
export interface PartyReaction { name: string; emoji: string; atMs: number; memberId?: string }

/** A digital tifo: one end of the room raises cards, and at the reveal the stand shows a mosaic. */
export interface PartyTifo {
  tifoId: string; teamId: string; design: 'stripes' | 'shirt' | 'star' | 'letters'; by: string;
  startedAtMs: number; revealAtMs: number; endsAtMs: number; holders: string[]; nowMs: number;
}
export interface PartyShare { name: string; label: string; eventIds: string[]; atMs: number }

/**
 * A pass judged from tracking at the moment it was played: difficulty with the passing lane (0 to 100), opponents who
 * could cut it out and who were in its path, a marked target, its chance of arriving and the xT where it was aimed;
 * and the better option when there was one (teammate, where he was in board units, how much more xT, its chance).
 */
export interface PassView {
  eventId: string; laneDifficulty: number; interceptable: number; laneDefenders: number; receiverMarked: boolean;
  completion: number; xt: number; betterTo?: string; betterX?: number; betterY?: number; betterGain?: number; betterCompletion?: number;
}

/** Pitch control: the home team's control (0 to 1) per cell, row by row from y 0, home attacking towards x = 100. */
export interface ControlView { t: number; cols: number; rows: number; home: number[] }

/** The expected-threat grid, in the attacking team's frame, row by row from y 0. */
export interface XtView { cols: number; rows: number; values: number[]; fittedOn: string }

/** The coach's corner: plans a coach can propose, each settled by a criterion over the next ten minutes. */
export type CoachPlanKind = 'pressHigher' | 'sitDeeper' | 'goDirect' | 'keepTheBall' | 'takeTheFinalThird';
export interface CoachPlanOption {
  kind: CoachPlanKind; title: string; titleFi: string; rule: string; target: number;
  criterion: string; criterionFi: string; limits: string; limitsFi: string;
}
export interface CoachProposal { coach: string; coachName: string; plan: CoachPlanKind; rationale: string; fromModel: boolean }
/**
 * Two coach agents' proposals for one team, Decision-1's pick between them (a choice question with a probability for
 * each), and the two tactical tests that settle them. Verdict: was the pick the plan that held?
 */
export interface CoachDecision {
  decisionId: string; teamId: string; atSec: number; brief: string; menu: CoachPlanOption[]; proposals: CoachProposal[];
  picked?: string; probabilities?: Record<string, number>; source: string; testIds: Record<string, string>;
  verdict?: 'right' | 'wrong' | 'level';
}

/** What-if sandbox: move one team on the frame now and measure pitch control before and after. */
export type WhatIfChange = 'pushUp' | 'dropDeep' | 'widen' | 'narrow' | 'pressTheBall';
export interface WhatIfView {
  teamId: string; change: WhatIfChange; metres: number; t: number;
  controlBefore: number; controlAfter: number; finalThirdBefore: number; finalThirdAfter: number; ownThirdBefore: number; ownThirdAfter: number;
  cols: number; rows: number; homeBefore: number[]; homeAfter: number[];
  moved: { playerId: string; fromX: number; fromY: number; toX: number; toY: number }[];
  helpProbability?: number; source: string;
  /** Everyone the change left where they were, both teams. */
  others: { playerId: string; fromX: number; fromY: number; toX: number; toY: number }[];
}
