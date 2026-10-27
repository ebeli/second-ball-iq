import { useEffect, useReducer, useState } from 'react';
import type { HubConnection } from '@microsoft/signalr';
import { connectHub } from './api';
import type {
  AutoEventView, Call, CandidateView, ExpectationView, Prediction, ShapeReport, InsightView, PitchEvent, RecapView, RunnerView, SessionInfo,
  SessionSnapshot, TacticalTest, FaultDrillView, TagView, TickView, TraceStep, TrackingView, PassView, ControlView, XtView, CoachDecision,
} from './types';
import { setControl, setFrame } from './tracking';

export interface MatchState {
  info?: SessionInfo;
  tick?: TickView;
  events: PitchEvent[];
  candidates: CandidateView[];
  insights: InsightView[];
  groundTruth: ExpectationView[];
  recaps: RecapView[];
  predictions: Prediction[];
  calls: Call[];
  shapes: ShapeReport[];
  tests: TacticalTest[];
  drills: FaultDrillView[];
  /** Running from tracking, the tags it raised, and auto-eventing's score (empty until tracking is ready). */
  running: RunnerView[];
  tags: TagView[];
  autoEvents?: AutoEventView;
  /** Passes judged from tracking, by event id; the expected-threat grid. */
  passes: Record<string, PassView>;
  xt?: XtView;
  /** The coach's corner: every decision so far, both teams. */
  coaches: CoachDecision[];
  /** A fault drill armed for the next moment, until its result arrives. */
  armed?: string;
}

type Action =
  | { type: 'snapshot'; snapshot: SessionSnapshot | null }
  | { type: 'session'; info: SessionInfo }
  | { type: 'tick'; tick: TickView }
  | { type: 'events'; events: PitchEvent[] }
  | { type: 'candidate'; candidate: CandidateView }
  | { type: 'step'; candidateId: string; step: TraceStep }
  | { type: 'insight'; insight: InsightView }
  | { type: 'groundTruth'; expectation: ExpectationView }
  | { type: 'recap'; recap: RecapView }
  | { type: 'prediction'; prediction: Prediction }
  | { type: 'call'; call: Call }
  | { type: 'shape'; shapes: ShapeReport[] }
  | { type: 'test'; test: TacticalTest }
  | { type: 'drill'; drill: FaultDrillView }
  | { type: 'armed'; kind?: string }
  | { type: 'running'; running: RunnerView[] }
  | { type: 'tag'; tag: TagView }
  | { type: 'autoevent'; report: AutoEventView }
  | { type: 'passinfo'; passes: PassView[] }
  | { type: 'coach'; decision: CoachDecision };

const empty: MatchState = { events: [], candidates: [], insights: [], groundTruth: [], recaps: [], predictions: [], calls: [], shapes: [], tests: [], drills: [],
  running: [], tags: [], passes: {}, coaches: [] };

function upsert<T>(list: T[], item: T, same: (a: T) => boolean): T[] {
  const i = list.findIndex(same);
  if (i < 0) return [...list, item];
  const copy = list.slice();
  copy[i] = item;
  return copy;
}

function reduce(state: MatchState, action: Action): MatchState {
  switch (action.type) {
    case 'snapshot':
      return action.snapshot
        ? { info: action.snapshot.info, tick: action.snapshot.tick, events: action.snapshot.events,
            candidates: action.snapshot.candidates, insights: action.snapshot.insights, groundTruth: action.snapshot.groundTruth,
            recaps: action.snapshot.recaps ?? [], predictions: action.snapshot.predictions ?? [],
            calls: action.snapshot.calls ?? [], shapes: action.snapshot.shapes ?? [], tests: action.snapshot.tests ?? [], drills: action.snapshot.drills ?? [],
            running: action.snapshot.running ?? [], tags: action.snapshot.tags ?? [], autoEvents: action.snapshot.autoEvents,
            passes: Object.fromEntries((action.snapshot.passes ?? []).map((p) => [p.eventId, p])), xt: action.snapshot.xt,
            coaches: action.snapshot.coaches ?? [] }
        : empty;
    case 'session':
      // A new match: everything from the previous one goes.
      return { ...empty, info: action.info };
    case 'tick':
      return { ...state, tick: action.tick };
    case 'events': {
      const known = state.events.length ? state.events[state.events.length - 1].seq : 0;
      const fresh = action.events.filter((e) => e.seq > known);
      return fresh.length ? { ...state, events: state.events.concat(fresh) } : state;
    }
    case 'candidate': {
      const prev = state.candidates.find((c) => c.candidateId === action.candidate.candidateId);
      // While processing, keep the live steps already received; the final message carries the full trace.
      const merged = action.candidate.trace ? action.candidate : { ...action.candidate, trace: prev?.trace };
      return { ...state, candidates: upsert(state.candidates, merged, (c) => c.candidateId === merged.candidateId) };
    }
    case 'step': {
      const candidates = state.candidates.map((c) =>
        c.candidateId === action.candidateId && c.status !== 'done'
          ? { ...c, trace: [...(c.trace ?? []), action.step] }
          : c);
      return { ...state, candidates };
    }
    case 'insight':
      return {
        ...state,
        insights: upsert(state.insights, action.insight, (i) => i.insight.insightId === action.insight.insight.insightId),
      };
    case 'recap':
      return { ...state, recaps: upsert(state.recaps, action.recap, (r) => r.recapId === action.recap.recapId) };
    case 'prediction':
      return {
        ...state,
        predictions: upsert(state.predictions, action.prediction, (p) => p.predictionId === action.prediction.predictionId),
      };
    case 'call':
      return { ...state, calls: upsert(state.calls, action.call, (c) => c.callId === action.call.callId) };
    case 'shape':
      return { ...state, shapes: action.shapes };
    case 'drill':
      return { ...state, drills: [...state.drills, action.drill], armed: undefined };
    case 'armed':
      return { ...state, armed: action.kind };
    case 'running':
      return { ...state, running: action.running };
    case 'tag':
      return { ...state, tags: [...state.tags.slice(-29), action.tag] };
    case 'autoevent':
      return { ...state, autoEvents: action.report };
    case 'passinfo': {
      const passes = { ...state.passes };
      for (const p of action.passes) passes[p.eventId] = p;
      return { ...state, passes };
    }
    case 'coach':
      return { ...state, coaches: upsert(state.coaches, action.decision, (d) => d.decisionId === action.decision.decisionId) };
    case 'test':
      return { ...state, tests: upsert(state.tests, action.test, (t) => t.testId === action.test.testId) };
    case 'groundTruth':
      return {
        ...state,
        groundTruth: state.groundTruth.map((x) =>
          x.kind === action.expectation.kind && x.teamId === action.expectation.teamId && x.fromSec === action.expectation.fromSec
            ? action.expectation : x),
      };
  }
}

export type Connection = 'connecting' | 'connected' | 'reconnecting' | 'offline';

/** The match as the server streams it. Messages are ignored until the snapshot has arrived. */
export function useMatch(): { state: MatchState; connection: Connection; hub?: HubConnection } {
  const [state, dispatch] = useReducer(reduce, empty);
  const [connection, setConnection] = useState<Connection>('connecting');
  const [hubRef, setHub] = useState<HubConnection>();

  useEffect(() => {
    const hub = connectHub();
    setHub(hub);
    const load = async () => {
      const snapshot = await hub.invoke<SessionSnapshot | null>('Snapshot');
      dispatch({ type: 'snapshot', snapshot }); setControl(snapshot?.control);
    };
    hub.on('session', (info: SessionInfo) => { setFrame(undefined); setControl(undefined); dispatch({ type: 'session', info }); });
    hub.on('tick', (tick: TickView) => dispatch({ type: 'tick', tick }));
    hub.on('events', (events: PitchEvent[]) => dispatch({ type: 'events', events }));
    hub.on('candidate', (candidate: CandidateView) => dispatch({ type: 'candidate', candidate }));
    hub.on('step', (m: { candidateId: string; step: TraceStep }) =>
      dispatch({ type: 'step', candidateId: m.candidateId, step: m.step }));
    hub.on('insight', (insight: InsightView) => dispatch({ type: 'insight', insight }));
    hub.on('groundTruth', (expectation: ExpectationView) => dispatch({ type: 'groundTruth', expectation }));
    hub.on('recap', (recap: RecapView) => dispatch({ type: 'recap', recap }));
    hub.on('prediction', (prediction: Prediction) => dispatch({ type: 'prediction', prediction }));
    hub.on('call', (call: Call) => dispatch({ type: 'call', call }));
    hub.on('shape', (shapes: ShapeReport[]) => dispatch({ type: 'shape', shapes }));
    hub.on('test', (test: TacticalTest) => dispatch({ type: 'test', test }));
    hub.on('drill', (drill: FaultDrillView) => dispatch({ type: 'drill', drill }));
    hub.on('drillArmed', (m: { kind: string }) => dispatch({ type: 'armed', kind: m.kind }));
    hub.on('frame', (frame: TrackingView) => setFrame(frame));
    hub.on('running', (running: RunnerView[]) => dispatch({ type: 'running', running }));
    hub.on('tag', (tag: TagView) => dispatch({ type: 'tag', tag }));
    hub.on('autoevent', (report: AutoEventView) => dispatch({ type: 'autoevent', report }));
    hub.on('passinfo', (passes: PassView[]) => dispatch({ type: 'passinfo', passes }));
    hub.on('control', (control: ControlView) => setControl(control));
    hub.on('coach', (decision: CoachDecision) => dispatch({ type: 'coach', decision }));
    hub.onreconnecting(() => setConnection('reconnecting'));
    hub.onreconnected(() => { setConnection('connected'); void load(); });
    hub.onclose(() => setConnection('offline'));

    let cancelled = false;
    hub.start()
      .then(async () => { if (!cancelled) { setConnection('connected'); await load(); } })
      .catch(() => setConnection('offline'));
    return () => { cancelled = true; void hub.stop(); };
  }, []);

  return { state, connection, hub: hubRef };
}
