import { useEffect, useMemo, useRef, useState } from 'react';
import { useMatch } from './store';
import { kitVars, playerTeam } from './kit';
import { clock } from './format';
import { metricTag } from './metrics';
import type { Call, CoachDecision, InsightView, MatchInfo, PitchEvent, TacticalTest, TagView } from './types';

/**
 * The broadcast overlay (/overlay): the live graphics on their own, for a vision mixer or OBS to key over the match
 * picture. 1920 × 1080, scaled to the window.
 *
 * - Background: transparent by default (?mode=alpha, for a browser source with alpha), or a chroma key colour
 *   (?mode=key for green, ?mode=blue), or ?mode=preview for a dark pitch to judge it by eye.
 * - Graphics: the score bug; the agents' checked insight as a lower third (for its display time); the commentator's
 *   call on a goal or a big chance; a goal splash; a player's speed or distance tag from tracking; a tactical test as
 *   it locks and when the match settles it; the coach's corner as Decision-1 picks between the two coaches' plans,
 *   and again when the match has settled both (did the pick hold?). Tests and the coach's corner share one slot
 *   bottom right and take turns; a goal clears the slot and the player tag while it plays.
 * - SMPTE timecode (time of day, 25 fps, non-drop) in the corner (?tc=0 hides it), and machine-readable render
 *   metadata: a JSON block (#render-metadata), window.__overlay, and a postMessage to the parent frame on every
 *   change, saying what is on screen, since when, and where each graphic came from.
 * - ?persona=Broadcast-en picks the insights' style and language (Broadcast-fi for Finnish).
 */

const FPS = 25;
const LOWER_THIRD_MS = 9000, CALL_MS = 6000, GOAL_MS = 5500, TAG_MS = 6000, TEST_MS = 11000, COACH_MS = 12000;

export function timecode(ms = Date.now()): { text: string; frames: number } {
  const d = new Date(ms);
  const sinceMidnight = (d.getHours() * 3600 + d.getMinutes() * 60 + d.getSeconds()) * 1000 + d.getMilliseconds();
  const frames = Math.floor(sinceMidnight / (1000 / FPS));
  const ff = frames % FPS, s = Math.floor(frames / FPS);
  const pad = (n: number) => String(n).padStart(2, '0');
  return { text: `${pad(Math.floor(s / 3600))}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}:${pad(ff)}`, frames };
}

/** When each thing was first seen on this page; whatever was already there at load counts as old. */
function useFirstSeen<T>(items: T[], key: (t: T) => string, ready: boolean) {
  const seen = useRef<Map<string, number> | undefined>(undefined);
  if (ready && !seen.current) seen.current = new Map(items.map((t) => [key(t), -Infinity]));
  if (seen.current) for (const t of items) if (!seen.current.has(key(t))) seen.current.set(key(t), Date.now());
  return (t: T) => seen.current?.get(key(t)) ?? -Infinity;
}

interface Graphic { id: string; type: string; text: string; since: number; source: Record<string, unknown> }

export function Overlay() {
  const params = useMemo(() => new URLSearchParams(window.location.search), []);
  const mode = params.get('mode') ?? 'alpha';
  const showTc = params.get('tc') !== '0';
  const { state } = useMatch();
  const info = state.info?.match;
  const tick = state.tick;
  const fi = (params.get('persona') ?? '').endsWith('-fi');
  const persona = params.get('persona') ?? (state.info?.personas.find((p) => p.startsWith('Broadcast-en')) ?? state.info?.personas[0] ?? 'Broadcast-en');
  const ready = !!info;

  // Re-render a few times a second so graphics come and go on time.
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 250); return () => window.clearInterval(t); }, []);

  // A transparent page: nothing behind the graphics but the chosen key colour.
  useEffect(() => {
    const bg = mode === 'key' ? '#00b140' : mode === 'blue' ? '#0047bb' : mode === 'preview' ? '#0b1a10' : 'transparent';
    document.documentElement.style.background = bg;
    document.body.style.background = bg;
    document.documentElement.dataset.overlay = mode;
  }, [mode]);

  const insights = useMemo(() => state.insights.filter((v) => v.personaKey === persona && (v.status === 'live' || v.status === 'approved')), [state.insights, persona]);
  const seenInsight = useFirstSeen(insights, (v: InsightView) => v.insight.insightId, ready);
  const seenCall = useFirstSeen(state.calls, (c: Call) => c.callId, ready);
  const seenTag = useFirstSeen(state.tags, (t: TagView) => `${t.playerId}:${t.atSec}`, ready);
  const seenTest = useFirstSeen(state.tests, (t: TacticalTest) => `${t.testId}:${t.status}`, ready);
  const seenCoach = useFirstSeen(state.coaches, (d: CoachDecision) => `${d.decisionId}:${d.verdict ?? 'new'}`, ready);
  const metricEvents = useMemo(() => state.events.filter((e) => e.x !== undefined && metricTag(e)), [state.events]);
  const seenEvent = useFirstSeen(metricEvents, (e: PitchEvent) => e.id, ready);

  const fresh = <T,>(items: T[], seen: (t: T) => number, ms: number) =>
    [...items].filter((t) => now - seen(t) < ms).sort((a, b) => seen(b) - seen(a))[0];
  // A lower third stays up for the display time the insight carries (6 to 12 seconds).
  const lower = [...insights].filter((v) => now - seenInsight(v) < Math.min(12000, Math.max(6000, v.insight.displayMs || LOWER_THIRD_MS)))
    .sort((a, b) => seenInsight(b) - seenInsight(a))[0];
  const call = fresh(state.calls.filter((c) => c.kind === 'goal' || c.kind === 'bigChance'), seenCall, CALL_MS);
  const goal = call?.kind === 'goal' && now - seenCall(call) < GOAL_MS ? call : undefined;
  const tag = fresh(state.tags, seenTag, TAG_MS);
  const metric = fresh(metricEvents, seenEvent, TAG_MS);
  // Bottom right is one slot: tactical tests and the coach's corner take turns in the order they arrived, each for its
  // full time, instead of stacking on top of each other. The coach card speaks for its own two tests. Anything that
  // would have to wait more than 30 s is dropped as stale; a goal clears the slot while it plays.
  const slot = useMemo(() => {
    const items = [
      ...state.tests.filter((t) => t.kind !== 'coachPlan').map((t) => ({ seen: seenTest(t), ms: TEST_MS, test: t, coach: undefined as CoachDecision | undefined })),
      ...state.coaches.map((d) => ({ seen: seenCoach(d), ms: COACH_MS, test: undefined as TacticalTest | undefined, coach: d })),
    ].filter((x) => x.seen > -Infinity && now - x.seen < 120000).sort((a, b) => a.seen - b.seen);
    let free = -Infinity;
    let current: (typeof items)[number] | undefined;
    for (const x of items) {
      const start = Math.max(x.seen, free);
      if (start - x.seen > 30000) continue;
      if (now >= start && now < start + x.ms) current = x;
      free = start + x.ms;
    }
    return current;
  }, [state.tests, state.coaches, now]); // eslint-disable-line react-hooks/exhaustive-deps
  const coach = goal ? undefined : slot?.coach;
  const test = goal ? undefined : slot?.test;

  // What is on screen, for the metadata.
  const graphics: Graphic[] = [];
  if (info && tick) graphics.push({ id: 'scorebug', type: 'scoreBug', text: `${info.home.shortName} ${tick.homeGoals}-${tick.awayGoals} ${info.away.shortName} ${clock(tick.clockSec)}`, since: 0, source: { matchId: info.matchId } });
  if (lower) graphics.push({ id: `lt-${lower.insight.insightId}`, type: 'lowerThird', text: lower.insight.headline, since: seenInsight(lower),
    source: { insightId: lower.insight.insightId, candidateId: lower.candidateId, evidenceEventIds: lower.insight.evidenceEventIds, groundingP: lower.insight.validation?.groundingP ?? null, publish: lower.insight.publish?.decision ?? null } });
  if (goal) graphics.push({ id: `goal-${goal.callId}`, type: 'goalSplash', text: fi ? goal.textFi : goal.text, since: seenCall(goal), source: { callId: goal.callId } });
  else if (call) graphics.push({ id: `call-${call.callId}`, type: 'callStrip', text: fi ? call.textFi : call.text, since: seenCall(call), source: { callId: call.callId } });
  if (tag && !goal) graphics.push({ id: `tag-${tag.playerId}-${tag.atSec}`, type: 'playerTag', text: `${tag.playerId} ${tag.kind} ${tag.kind === 'speed' ? tag.speedKmh : tag.distanceKm}`, since: seenTag(tag), source: { tracking: true, playerId: tag.playerId } });
  else if (metric && !goal) graphics.push({ id: `tag-${metric.id}`, type: 'playerTag', text: `${metric.playerId} ${metricTag(metric)}`, since: seenEvent(metric), source: { eventId: metric.id } });
  if (test) graphics.push({ id: `test-${test.testId}-${test.status}`, type: test.status === 'open' ? 'testLocked' : 'testSettled', text: fi ? test.criterionFi : test.criterion, since: seenTest(test),
    source: { testId: test.testId, estimate: test.estimate ?? null, estimateSource: test.estimateSource, status: test.status } });

  if (coach) graphics.push({ id: `coach-${coach.decisionId}-${coach.verdict ?? 'new'}`, type: coach.verdict ? 'coachVerdict' : 'coachPick',
    text: coach.proposals.map((p) => `${p.coachName}: ${coach.menu.find((m) => m.kind === p.plan)?.title ?? p.plan}`).join(' / '), since: seenCoach(coach),
    source: { decisionId: coach.decisionId, teamId: coach.teamId, picked: coach.picked ?? null, probabilities: coach.probabilities ?? null, arbiter: coach.source, testIds: coach.testIds, verdict: coach.verdict ?? null } });

  // Metadata: the JSON block, window.__overlay and a postMessage to the parent, whenever what is on screen changes.
  const metaKey = graphics.map((g) => g.id).join('|') + `|${tick?.homeGoals}-${tick?.awayGoals}`;
  const metaRef = useRef<HTMLScriptElement>(null);
  useEffect(() => {
    const tc = timecode();
    const meta = {
      schema: 'secondballiq.overlay/1', mode, fps: FPS, timecode: tc.text, frame: tc.frames,
      match: info && tick ? { id: info.matchId, clockSec: tick.clockSec, period: tick.period, score: [tick.homeGoals, tick.awayGoals], home: info.home.name, away: info.away.name } : null,
      persona,
      graphics: graphics.map((g) => ({ ...g, since: g.since > 0 ? timecode(g.since).text : null })),
    };
    if (metaRef.current) metaRef.current.textContent = JSON.stringify(meta);
    (window as unknown as { __overlay: unknown }).__overlay = meta;
    if (window.parent !== window) window.parent.postMessage({ type: 'secondballiq.overlay', ...meta }, '*');
  }, [metaKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // The timecode runs every frame without re-rendering React.
  const tcRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!showTc) return;
    let raf = 0;
    const run = () => { if (tcRef.current) tcRef.current.textContent = timecode().text; raf = requestAnimationFrame(run); };
    raf = requestAnimationFrame(run);
    return () => cancelAnimationFrame(raf);
  }, [showTc]);

  // Scale the 1920 × 1080 canvas to the window.
  const [scale, setScale] = useState(1);
  useEffect(() => {
    const fit = () => setScale(Math.min(window.innerWidth / 1920, window.innerHeight / 1080));
    fit();
    window.addEventListener('resize', fit);
    return () => window.removeEventListener('resize', fit);
  }, []);

  return (
    <div className={`ovl ovl-${mode}`} style={info ? kitVars(info) : undefined}>
      <script ref={metaRef} id="render-metadata" type="application/json" />
      <div className="ovl-canvas" style={{ transform: `scale(${scale})` }}>
        {info && tick && <ScoreBug info={info} home={tick.homeGoals} away={tick.awayGoals} clockSec={tick.clockSec} />}
        {showTc && <div ref={tcRef} className="ovl-tc num" aria-hidden="true" />}
        {goal && info && <GoalSplash key={goal.callId} call={goal} info={info} fi={fi} />}
        {!goal && call && <div key={call.callId} className="ovl-call">{fi ? call.textFi : call.text}</div>}
        {lower && info && <LowerThird key={lower.insight.insightId} v={lower} info={info} fi={fi} />}
        {info && !goal && (tag || metric) && <PlayerTag key={tag ? `${tag.playerId}${tag.atSec}` : metric!.id} info={info} tag={tag} event={tag ? undefined : metric} fi={fi} />}
        {test && <TestCard key={`${test.testId}:${test.status}`} t={test} fi={fi} />}
        {coach && info && <CoachCard key={`${coach.decisionId}:${coach.verdict ?? 'new'}`} d={coach} info={info} fi={fi} tests={state.tests} />}
      </div>
    </div>
  );
}

function ScoreBug({ info, home, away, clockSec }: { info: MatchInfo; home: number; away: number; clockSec: number }) {
  return (
    <div className="ovl-bug">
      <span className="ovl-bug-brand">SB<b>IQ</b></span>
      <span className="ovl-team side-home"><i />{info.home.shortName}</span>
      <span className="ovl-score num">{home}<small>–</small>{away}</span>
      <span className="ovl-team side-away">{info.away.shortName}<i /></span>
      <span className="ovl-clock num">{clock(clockSec)}</span>
    </div>
  );
}

function LowerThird({ v, info, fi }: { v: InsightView; info: MatchInfo; fi: boolean }) {
  const team = v.insight.teamIds[0] === info.away.teamId ? 'away' : 'home';
  const p = v.insight.validation?.groundingP;
  return (
    <div className={`ovl-lt side-${team}`}>
      <div className="ovl-lt-kicker">{(team === 'home' ? info.home : info.away).name} · {clock(v.insight.matchClockSec)}</div>
      <div className="ovl-lt-head">{v.insight.headline}</div>
      <div className="ovl-lt-body">{v.insight.summary}</div>
      <div className="ovl-lt-check">
        {fi ? 'Mitattu ja tarkistettu' : 'Measured and checked'}{p != null ? ` · Decision-1 ${Math.round(p * 100)}%` : ''}
        {v.insight.figures.slice(0, 2).map((f) => <span key={f.label} className="num">{f.label} {formatValue(f.value, f.unit)}</span>)}
      </div>
    </div>
  );
}

function formatValue(v: number, unit?: string) {
  if (unit === '%') return `${Math.round(v * (v <= 1 ? 100 : 1))}%`;
  return Number.isInteger(v) ? String(v) : v.toFixed(2);
}

function GoalSplash({ call, info, fi }: { call: Call; info: MatchInfo; fi: boolean }) {
  const team = call.teamId === info.away.teamId ? info.away : info.home;
  const who = playerTeam(info, call.playerId);
  return (
    <div className={`ovl-goal side-${call.teamId === info.away.teamId ? 'away' : 'home'}`}>
      <div className="ovl-goal-word">{fi ? 'MAALI' : 'GOAL'}</div>
      <div className="ovl-goal-team">{team.name}{who ? ` · ${who.player.name}` : ''}</div>
    </div>
  );
}

function PlayerTag({ info, tag, event, fi }: { info: MatchInfo; tag?: TagView; event?: PitchEvent; fi: boolean }) {
  const id = tag?.playerId ?? event?.playerId;
  const who = playerTeam(info, id);
  if (!who) return null;
  const value = tag ? (tag.kind === 'speed' ? `${tag.speedKmh.toFixed(1)} km/h` : `${tag.distanceKm.toFixed(0)} km ${fi ? 'juostu' : 'covered'}`) : metricTag(event!, fi);
  return (
    <div className={`ovl-tag side-${who.side}`}>
      <b className="num">{who.player.shirtNumber}</b>
      <span>{who.player.name}</span>
      <em className="num">{value}</em>
      <small>{tag ? (fi ? 'seurantadata' : 'from tracking') : (fi ? 'pallon nopeus' : 'ball data')}</small>
    </div>
  );
}

function TestCard({ t, fi }: { t: TacticalTest; fi: boolean }) {
  const open = t.status === 'open';
  const verdict = { met: fi ? 'Ehto täyttyi' : 'Criterion met', notMet: fi ? 'Ehto ei täyttynyt' : 'Criterion not met', inconclusive: fi ? 'Ratkaisematon' : 'Inconclusive', open: '' }[t.status];
  return (
    <div className={`ovl-test ${open ? 'is-open' : `is-${t.status}`}`}>
      <div className="ovl-test-kicker">{open ? (fi ? 'Taktinen testi lukittu' : 'Tactical test locked') : verdict} · {fi ? 'ratkeaa' : 'decides by'} {clock(t.deadlineSec)}</div>
      <div className="ovl-test-body">{fi ? t.criterionFi : t.criterion}</div>
      <div className="ovl-test-foot">
        {open
          ? (t.estimate != null ? `Decision-1 ${fi ? 'arvio' : 'estimate'} ${Math.round(t.estimate * 100)}%` : (fi ? 'Ottelu ratkaisee' : 'The match decides'))
          : (fi ? t.resultFi : t.result)}
      </div>
    </div>
  );
}

/**
 * The coach's corner: when Decision-1 picks, both coaches' plans with its probability for each and the pick marked;
 * when the match has settled both, whether the pick was the plan that held.
 */
function CoachCard({ d, info, fi, tests }: { d: CoachDecision; info: MatchInfo; fi: boolean; tests: TacticalTest[] }) {
  const team = d.teamId === info.away.teamId ? info.away : info.home;
  const title = (kind: string) => { const m = d.menu.find((x) => x.kind === kind); return m ? (fi ? m.titleFi : m.title) : kind; };
  const verdict = d.verdict === 'right' ? (fi ? 'Decision-1 valitsi suunnitelman, joka piti' : 'Decision-1 picked the plan that held')
    : d.verdict === 'wrong' ? (fi ? 'Toinen suunnitelma piti' : 'The other plan held')
    : d.verdict === 'level' ? (fi ? 'Tasan: ottelu ei erottanut suunnitelmia' : 'Level: the match did not separate the plans') : undefined;
  return (
    <div className={`ovl-coach side-${d.teamId === info.away.teamId ? 'away' : 'home'} ${d.verdict ? `is-${d.verdict}` : ''}`}>
      <div className="ovl-coach-kicker">{fi ? 'Valmentajien nurkka' : "Coach's corner"} · {team.name} · {clock(d.atSec)}</div>
      <div className="ovl-coach-plans">
        {d.proposals.map((p) => {
          const prob = d.probabilities?.[p.coach];
          const picked = d.picked === p.coach;
          const status = d.verdict ? tests.find((t) => t.testId === d.testIds[p.coach])?.status : undefined;
          const held = status === 'met' ? (fi ? 'Piti' : 'Held') : status === 'notMet' ? (fi ? 'Ei pitänyt' : 'Did not hold') : status === 'inconclusive' ? (fi ? 'Ratkaisematon' : 'Inconclusive') : undefined;
          return (
            <div key={p.coach} className={`ovl-coach-plan ${picked ? 'picked' : ''}`}>
              <span className="ovl-coach-who">{p.coachName}{picked ? <b>{fi ? 'VALINTA' : 'PICK'}</b> : null}{held && <em className={`is-${status}`}>{held}</em>}</span>
              <span className="ovl-coach-title">{title(p.plan)}</span>
              {prob != null && <span className="ovl-coach-bar"><i style={{ width: `${Math.round(prob * 100)}%` }} /></span>}
              {prob != null && <span className="ovl-coach-p num">{Math.round(prob * 100)}%</span>}
            </div>
          );
        })}
      </div>
      <div className="ovl-coach-foot">
        {verdict ?? (d.picked
          ? (fi ? 'Decision-1:n valinta, mallin arvio · ottelu ratkaisee molemmat' : "Decision-1's pick, a model estimate · the match tests both")
          : (fi ? 'Ei valintaa · ottelu ratkaisee molemmat' : 'No pick · the match tests both'))}
      </div>
    </div>
  );
}
