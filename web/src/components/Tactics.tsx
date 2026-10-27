import { useEffect, useMemo, useState } from 'react';
import { api } from '../api';
import { clock, roleLabel } from '../format';
import { Pitch } from './Pitch';
import type { CandidateView, MatchInfo, PitchEvent, PlayerShape, ShapeReport } from '../types';

const L = 105;
const W = 68;
const COLS = 21;
const ROWS = 14;
const TOUCH = new Set(['pass', 'shot', 'pressure', 'tackle', 'interception', 'foul']);

/** Team-frame point to the board (home attacks right, so away positions are mirrored). */
function onBoard(x: number, y: number, away: boolean) {
  const ax = away ? 100 - x : x, ay = away ? 100 - y : y;
  return { x: (ax / 100) * L, y: (ay / 100) * W };
}

/**
 * Where a team or a player had the ball: touches counted on a 5 m grid and smoothed, drawn in the team's colour,
 * the most-used areas strongest.
 */
export function HeatLayer({ events, teamId, playerId, fromSec, toSec, color }:
  { events: PitchEvent[]; teamId?: string; playerId?: string; fromSec: number; toSec: number; color: string }) {
  const cells = useMemo(() => {
    const grid = new Float64Array(COLS * ROWS);
    for (const e of events) {
      if (e.clockSec < fromSec || e.clockSec >= toSec || e.x === undefined || e.y === undefined || !TOUCH.has(e.type)) continue;
      if (teamId && e.teamId !== teamId) continue;
      if (playerId && e.playerId !== playerId) continue;
      const cx = Math.min(COLS - 1, Math.floor((e.x / 100) * COLS)), cy = Math.min(ROWS - 1, Math.floor((e.y / 100) * ROWS));
      // Spread each touch over its neighbours (a small Gaussian), so a handful of touches still reads as an area.
      for (let dx = -2; dx <= 2; dx++) for (let dy = -2; dy <= 2; dy++) {
        const gx = cx + dx, gy = cy + dy;
        if (gx < 0 || gy < 0 || gx >= COLS || gy >= ROWS) continue;
        grid[gy * COLS + gx] += Math.exp(-(dx * dx + dy * dy) / 2.2);
      }
    }
    let max = 0;
    for (const v of grid) max = Math.max(max, v);
    return { grid, max };
  }, [events, teamId, playerId, fromSec, toSec]);

  if (cells.max === 0) return null;
  const w = L / COLS, h = W / ROWS;
  const rects = [];
  for (let gy = 0; gy < ROWS; gy++) for (let gx = 0; gx < COLS; gx++) {
    const v = cells.grid[gy * COLS + gx] / cells.max;
    if (v < 0.12) continue;
    rects.push(<rect key={`${gx}-${gy}`} x={gx * w} y={gy * h} width={w + 0.05} height={h + 0.05} fill={color} opacity={Math.pow(v, 1.6) * 0.9} />);
  }
  return <g className="heat" filter="url(#heat-blur)">{rects}</g>;
}

/** Each player's average position as a numbered disc with their role underneath; formation label at the team's end. */
export function ShapeLayer({ reports, info, fi, ghost, highlight, onPlayer }:
  { reports: ShapeReport[]; info: MatchInfo; fi?: boolean; ghost?: ShapeReport[]; highlight?: string; onPlayer?: (id: string) => void }) {
  return (
    <g className="shape-layer">
      {ghost?.map((g) => {
        const away = g.teamId === info.away.teamId;
        const now = reports.find((r) => r.teamId === g.teamId);
        return g.players.filter(enough).map((p) => {
          const a = onBoard(p.x, p.y, away);
          const n = now?.players.find((q) => q.playerId === p.playerId && enough(q));
          const b = n ? onBoard(n.x, n.y, away) : undefined;
          return (
            <g key={`ghost-${p.playerId}`} className="shape-ghost">
              <circle cx={a.x} cy={a.y} r={1.9} />
              {b && Math.hypot(b.x - a.x, b.y - a.y) > 2.5 && (
                <line x1={a.x} y1={a.y} x2={b.x} y2={b.y} markerEnd="url(#shape-arrow)" />
              )}
            </g>
          );
        });
      })}
      {reports.map((r) => {
        const away = r.teamId === info.away.teamId;
        const kit = away ? 'var(--away)' : 'var(--home)';
        const label = onBoard(away ? 4 : 4, 4, away);
        return (
          <g key={r.teamId}>
            {r.formation && (
              <text x={label.x} y={away ? W - 1.2 : 3.6} className="shape-formation" textAnchor={away ? 'end' : 'start'} fill={kit}>
                {r.formation}
              </text>
            )}
            {r.players.filter(enough).map((p) => {
              const at = onBoard(p.x, p.y, away);
              const role = p.role ? roleLabel[p.role] : undefined;
              const notable = p.role && ['falseNine', 'secondStriker', 'shadowStriker', 'invertedWinger', 'wingBack'].includes(p.role);
              return (
                <g key={p.playerId} className={`shape-player ${highlight === p.playerId ? 'is-focus' : ''} ${onPlayer ? 'clickable' : ''}`}
                  transform={`translate(${at.x} ${at.y})`} onClick={onPlayer ? () => onPlayer(p.playerId) : undefined}>
                  <title>{`${p.name}: ${role ? (fi ? role.fi : role.en) : '?'} · ${p.touches} ${fi ? 'kosketusta' : 'touches'}`}</title>
                  <circle r={2.3} fill={away ? 'var(--away-kit)' : 'var(--home-kit)'} stroke={notable ? 'var(--on-air)' : kit} strokeWidth={notable ? 0.55 : 0.4} />
                  <text className="shape-num" textAnchor="middle" dy="0.8" fill={away ? 'var(--away-ink)' : 'var(--home-ink)'}>{p.shirtNumber}</text>
                  {role && <text className={`shape-role ${notable ? 'notable' : ''}`} textAnchor="middle" dy="5">{role.tag}</text>}
                </g>
              );
            })}
          </g>
        );
      })}
    </g>
  );
}

const enough = (p: PlayerShape) => p.touches >= 3 && p.listed !== 'goalkeeper';

/** Marker definition for the shape arrows; Pitch has its own defs, this adds one more inside the overlay. */
export function ShapeDefs() {
  return (
    <defs>
      <filter id="heat-blur" x="-5%" y="-5%" width="110%" height="110%"><feGaussianBlur stdDeviation="1.6" /></filter>
      <marker id="shape-arrow" viewBox="0 0 6 6" refX="5" refY="3" markerWidth="4" markerHeight="4" orient="auto-start-reverse">
        <path d="M0,0 L6,3 L0,6 z" fill="var(--on-air)" />
      </marker>
    </defs>
  );
}

/**
 * Before and after for a tactical shift: the team's average positions in the spell before (ghosts) and since
 * (solid), with arrows where players moved, from the engine's own windows.
 */
export function useShiftCompare(c?: CandidateView) {
  const [pair, setPair] = useState<{ before: ShapeReport; after: ShapeReport }>();
  const key = c ? `${c.candidateId}` : '';
  useEffect(() => {
    setPair(undefined);
    if (!c || c.kind !== 'tacticalShift') return;
    const bf = Number(c.tags?.beforeFromSec ?? c.fromSec), bt = Number(c.tags?.beforeToSec ?? c.fromSec);
    const af = c.tags?.shift === 'formation' ? c.toSec - 15 * 60 : Number(c.tags?.beforeToSec ?? c.fromSec);
    let live = true;
    Promise.all([api.shape(c.teamId, bf, bt), api.shape(c.teamId, af, c.toSec)])
      .then(([before, after]) => { if (live) setPair({ before, after }); })
      .catch(() => undefined);
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return pair;
}

/** The bar under the board while a shift or a role is shown. */
export function ShapeBar({ c, pair, info, fi, onClose }:
  { c: CandidateView; pair?: { before: ShapeReport; after: ShapeReport }; info: MatchInfo; fi?: boolean; onClose: () => void }) {
  const team = c.teamId === info.home.teamId ? info.home : info.away;
  const t = c.tags ?? {};
  const pct = (v: number) => `${Math.round(v * 100)}%`;
  let title = '';
  let rows: { label: string; before: string; after: string }[] = [];
  if (c.kind === 'tacticalShift' && t.shift === 'formation') {
    title = fi ? `${team.name}: ${t.formationBefore} → ${t.formationAfter}` : `${team.name}: ${t.formationBefore} to ${t.formationAfter}`;
    rows = [
      { label: fi ? 'Puolustajat' : 'Defenders', before: String(c.facts.defendersBefore), after: String(c.facts.defendersAfter) },
      { label: fi ? 'Hyökkääjät' : 'Forwards', before: String(c.facts.forwardsBefore), after: String(c.facts.forwardsAfter) },
    ];
  } else if (c.kind === 'tacticalShift') {
    title = t.shift === 'deeper' ? (fi ? `${team.name} vetäytyy syvemmälle` : `${team.name} sit deeper`) : (fi ? `${team.name} pelaa pitkää` : `${team.name} go direct`);
    rows = t.shift === 'deeper'
      ? [{ label: fi ? 'Korkea prässi' : 'High press', before: pct(c.facts.highPressRateBefore), after: pct(c.facts.highPressRateAfter) }]
      : [{ label: fi ? 'Pitkät pallot' : 'Long balls', before: pct(c.facts.longBallShareBefore), after: pct(c.facts.longBallShareAfter) }];
  } else {
    const role = t.role ? roleLabel[t.role] : undefined;
    const name = [...info.home.players, ...info.away.players].find((p) => p.playerId === c.playerId)?.name ?? '';
    title = `${name}: ${role ? (fi ? role.fi : role.en) : ''}`;
    rows = [{ label: fi ? 'Kosketukset' : 'Touches', before: '', after: String(c.facts.touches) },
            { label: fi ? 'Keskisyvyys' : 'Average depth', before: '', after: `${c.facts.averageDepthMetres} m` }];
  }
  const cause = t.cause && t.cause !== 'none'
    ? (fi ? { substitution: 'vaihdon jälkeen', goalFor: 'oman maalin jälkeen', goalAgainst: 'päästetyn maalin jälkeen', halfTime: 'tauon jälkeen' }
          : { substitution: 'after a substitution', goalFor: 'after their goal', goalAgainst: 'after conceding', halfTime: 'after the break' })[t.cause as 'substitution']
    : undefined;
  return (
    <div className="rewind-bar shape-bar" role="group" aria-label={fi ? 'Taktiikka' : 'Tactics'}>
      <div className="rw-head">
        <span className="rw-tag">{c.kind === 'playerRole' ? (fi ? 'Rooli' : 'Role') : (fi ? 'Muutos' : 'Shift')}</span>
        <span className="rw-title">{title}</span>
        <button type="button" className="rw-close" onClick={onClose}>{fi ? 'Takaisin liveen' : 'Back to live'}</button>
      </div>
      <p className="rw-caption">
        {c.kind === 'tacticalShift'
          ? (pair
              ? (fi ? `Haamut: ${clock(pair.before.fromSec)}–${clock(pair.before.toSec)}. Täydet: ${clock(pair.after.fromSec)}–${clock(pair.after.toSec)}.`
                    : `Ghosts: ${clock(pair.before.fromSec)}–${clock(pair.before.toSec)}. Solid: ${clock(pair.after.fromSec)}–${clock(pair.after.toSec)}.`)
              : (fi ? 'Haetaan muotoja…' : 'Measuring both spells…'))
          : (fi ? `Kosketukset ${clock(c.fromSec)}–${clock(c.toSec)}.` : `Touches ${clock(c.fromSec)}–${clock(c.toSec)}.`)}
        {cause && <> · {cause}{t.causeSec ? ` (${clock(Number(t.causeSec))})` : ''}</>}
      </p>
      <dl className="rw-compare">
        {rows.map((r) => (
          <div key={r.label}><dt>{r.label}</dt><dd>{r.before && <><span className="rw-before">{r.before}</span> → </>}<strong>{r.after}</strong></dd></div>
        ))}
      </dl>
    </div>
  );
}

type Window = 'recent' | 'half' | 'match';
type View = 'shape' | 'heat';

/** What the Analysis view shows on the pitch: shape or heat map, which team, and over which window. */
export interface TacticsState { view: View; team: 'both' | 'home' | 'away'; win: Window }
export const defaultTactics: TacticsState = { view: 'shape', team: 'both', win: 'recent' };

/** The shapes for the chosen window: the last 15 minutes come pushed from the server; other windows are measured on request. */
export function useTacticsReports(state: TacticsState, shapes: ShapeReport[], info: MatchInfo, clockSec: number) {
  const { win } = state;
  const from = win === 'recent' ? Math.max(0, clockSec - 15 * 60) : win === 'half' ? (clockSec >= 45 * 60 ? 45 * 60 : 0) : 0;
  const [reports, setReports] = useState<ShapeReport[]>(shapes);
  const bucket = Math.floor(clockSec / 180);
  useEffect(() => {
    if (win === 'recent') { setReports(shapes); return; }
    let live = true;
    Promise.all([api.shape(info.home.teamId, from, clockSec), api.shape(info.away.teamId, from, clockSec)])
      .then((r) => { if (live) setReports(r); }).catch(() => undefined);
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [win, bucket, shapes]);
  return { reports, from };
}

/** The pitch for the Analysis view: shapes or a heat map. Drawn on the match stage. */
export function TacticsBoard({ state, reports, from, events, info, clockSec, fi, onPlayer }:
  { state: TacticsState; reports: ShapeReport[]; from: number; events: PitchEvent[]; info: MatchInfo; clockSec: number; fi?: boolean;
    onPlayer?: (id: string) => void }) {
  const { view, team } = state;
  const show = reports.filter((r) => team === 'both' || r.teamId === (team === 'home' ? info.home.teamId : info.away.teamId));
  const teamFilter = team === 'both' ? undefined : team === 'home' ? info.home.teamId : info.away.teamId;
  return (
    <Pitch events={events} clockSec={clockSec} info={info} compact label={fi ? 'Joukkueiden muoto' : 'Team shape'}>
      <ShapeDefs />
      {view === 'heat'
        ? <HeatLayer events={events} teamId={teamFilter ?? info.home.teamId} fromSec={from} toSec={clockSec + 1}
            color={(teamFilter ?? info.home.teamId) === info.home.teamId ? 'var(--home)' : 'var(--away)'} />
        : <ShapeLayer reports={show} info={info} fi={fi} onPlayer={onPlayer} />}
    </Pitch>
  );
}

/**
 * The Tactics controls and team cards: shape or heat map, both teams or one, over the last 15 minutes, this half or the
 * match, with each team's style in numbers. With `state` given it is controlled and the board is drawn elsewhere (the
 * stage); without, it keeps its own state and draws its own board (the studio).
 */
export function TacticsPanel({ shapes, events, info, clockSec, fi, onPlayer, state: controlled, onState, board, hideView }:
  { shapes: ShapeReport[]; events: PitchEvent[]; info: MatchInfo; clockSec: number; fi?: boolean; onPlayer?: (id: string) => void;
    state?: TacticsState; onState?: (s: TacticsState) => void; board?: boolean; hideView?: boolean }) {
  const [own, setOwn] = useState<TacticsState>(defaultTactics);
  const state = controlled ?? own;
  const set = (patch: Partial<TacticsState>) => { const next = { ...state, ...patch }; if (onState) onState(next); else setOwn(next); };
  const { reports, from } = useTacticsReports(state, shapes, info, clockSec);
  const { view, team, win } = state;
  const show = reports.filter((r) => team === 'both' || r.teamId === (team === 'home' ? info.home.teamId : info.away.teamId));

  return (
    <div className="tactics">
      <div className="tactics-controls">
        {!hideView && <Seg value={view} onChange={(v) => set({ view: v })} options={[['shape', fi ? 'Muoto' : 'Shape'], ['heat', fi ? 'Lämpökartta' : 'Heat map']]} />}
        <Seg value={view === 'heat' && team === 'both' ? 'home' : team} onChange={(t) => set({ team: t })}
          options={view === 'heat' ? [['home', info.home.shortName], ['away', info.away.shortName]]
            : [['both', fi ? 'Molemmat' : 'Both'], ['home', info.home.shortName], ['away', info.away.shortName]]} />
        <Seg value={win} onChange={(w) => set({ win: w })} options={[['recent', fi ? '15 min' : 'Last 15'], ['half', fi ? 'Puoliaika' : 'This half'], ['match', fi ? 'Ottelu' : 'Match']]} />
      </div>
      {(!controlled || board) && (
        <div className="tactics-board">
          <TacticsBoard state={state} reports={reports} from={from} events={events} info={info} clockSec={clockSec} fi={fi} onPlayer={onPlayer} />
        </div>
      )}
      <div className="tactics-teams">
        {show.map((r) => <TeamCard key={r.teamId} r={r} info={info} fi={fi} onPlayer={onPlayer} />)}
        {show.length === 0 && <p className="empty">{fi ? 'Muoto näkyy, kun peliä on pelattu muutama minuutti.' : 'The shape appears after a few minutes of play.'}</p>}
      </div>
    </div>
  );
}

function TeamCard({ r, info, fi, onPlayer }: { r: ShapeReport; info: MatchInfo; fi?: boolean; onPlayer?: (id: string) => void }) {
  const side = r.teamId === info.home.teamId ? 'home' : 'away';
  const team = side === 'home' ? info.home : info.away;
  const pct = (v: number) => `${Math.round(v * 100)}%`;
  const notable = r.players.filter((p) => p.role && ['falseNine', 'secondStriker', 'shadowStriker', 'invertedWinger', 'wingBack'].includes(p.role));
  return (
    <section className={`team-shape side-${side}`}>
      <header>
        <span className={`chip chip-${side}`}>{team.shortName}</span>
        <h3>{team.name}</h3>
        <span className="ts-formation">{r.formation ?? '–'}</span>
      </header>
      <dl className="ts-style">
        <div><dt>{fi ? 'Korkea prässi' : 'High press'}</dt><dd>{pct(r.style.highPressRate)}</dd></div>
        <div><dt>{fi ? 'Pitkät pallot' : 'Long balls'}</dt><dd>{pct(r.style.longBallShare)}</dd></div>
        <div><dt>{fi ? 'Puolustuksen korkeus' : 'Defending at'}</dt><dd>{Math.round((r.style.averageDefensiveX / 100) * 105)} m</dd></div>
      </dl>
      {notable.length > 0 && (
        <ul className="ts-roles">
          {notable.map((p) => (
            <li key={p.playerId}>
              {onPlayer ? <button type="button" className="linkish" onClick={() => onPlayer(p.playerId)}>{p.name}</button> : p.name}
              {' '}<span className="role-tag">{fi ? roleLabel[p.role!].fi : roleLabel[p.role!].en}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function Seg<T extends string>({ value, onChange, options }: { value: T; onChange: (v: T) => void; options: [T, string][] }) {
  return (
    <div className="tseg" role="group">
      {options.map(([v, label]) => (
        <button key={v} type="button" aria-pressed={value === v} onClick={() => onChange(v)}>{label}</button>
      ))}
    </div>
  );
}
