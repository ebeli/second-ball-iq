import { useContext, useEffect, useMemo, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Stage3DBridge } from '../stage3dBridge';
import { usePassInfo } from '../spatial';
import type { MatchInfo, PitchEvent } from '../types';
import { Token } from './Shirt';
import { playerTeam } from '../kit';
import { metricTag } from '../metrics';
import { useFrame, useHasFrame } from '../tracking';
import type { TagView } from '../types';

const L = 105;
const W = 68;
/** Match seconds of play the board keeps on screen. */
const TRAIL_SEC = 150;
/** Match seconds a speed or distance tag stays on a player (about five seconds at the judge's 8×). */
const TAG_SEC = 40;

const mx = (x: number) => (x / 100) * L;
const my = (y: number) => (y / 100) * W;

interface Props {
  events: PitchEvent[];
  clockSec: number;
  info: MatchInfo;
  /** Events the selected agent card cites; drawn on top and outlined. */
  highlight?: Set<string>;
  /** Tactical Rewind: replay these events one by one instead of the live trail. */
  rewind?: { events: PitchEvent[]; step: number };
  compact?: boolean;
  /** Something drawn instead of the live trail (a heat map, the teams' shape), in metres: 105 × 68. */
  children?: ReactNode;
  label?: string;
  /** Show the player on the ball as a numbered shirt token. */
  carrier?: boolean;
  /** The fan's own player gets a ring whenever he is on the ball. */
  focusPlayer?: string;
  /** Finnish labels on the speed and distance tags. */
  fi?: boolean;
  /** Running tags from tracking (top speed, distance milestones); shown on the player for a few seconds. */
  tags?: TagView[];
}

/**
 * A chalk tactics board. Home attacks to the right. Recent passes fade with age, shots point at goal and grow with
 * xG, high recoveries are diamonds, and cited evidence is ringed in on-air yellow.
 */
export function Pitch({ events, clockSec, info, highlight, rewind, compact, children, label, carrier, focusPlayer, fi, tags }: Props) {
  const quiet = children !== undefined && children !== null && children !== false;
  const colors = useTeamColors(info);
  // Inside the 3D stage the markings go onto its grass, and a replay's step is passed on so it plays in 3D.
  const bridge = useContext(Stage3DBridge);
  const onRewind = bridge?.onRewind;
  useEffect(() => { if (onRewind && rewind) onRewind({ events: rewind.events, step: rewind.step }); }, [onRewind, rewind?.events, rewind?.step]);
  useEffect(() => () => onRewind?.(undefined), [onRewind]);

  const recent = useMemo(() => {
    const from = clockSec - TRAIL_SEC;
    const out: PitchEvent[] = [];
    for (let i = events.length - 1; i >= 0 && events[i].clockSec >= from; i--) out.push(events[i]);
    return out.reverse();
  }, [events, clockSec]);

  const cited = useMemo(
    () => (highlight && highlight.size ? events.filter((e) => highlight.has(e.id)) : []),
    [events, highlight],
  );

  const ball = rewind
    ? rewind.events[Math.min(rewind.step, rewind.events.length - 1)]
    : [...recent].reverse().find((e) => e.x !== undefined);
  const ballAt = ball ? (ball.endX !== undefined && ball.type === 'pass' && ball.outcome === 'success'
    ? { x: ball.endX!, y: ball.endY! } : { x: ball.x!, y: ball.y! }) : undefined;

  // With tracking, every player and the ball are drawn where they are; without it, the last event's player.
  const tracking = useHasFrame() && !!carrier && !rewind && !quiet;
  // The last event with a player and a position: who has (or just played) the ball.
  const actor = carrier && !rewind && !quiet && !tracking ? [...recent].reverse().find((e) => e.playerId && e.x !== undefined && e.type !== 'substitution') : undefined;
  const who = actor ? playerTeam(info, actor.playerId) : undefined;

  // Player identification with a speed or distance indicator: a shot from 110 km/h or a completed pass from 40 m
  // tags the player by name for a few seconds (thresholds in metrics.ts, the same as the engine's).
  const tagged = carrier && !rewind && !quiet
    ? [...recent].reverse().find((e) => clockSec - e.clockSec <= TAG_SEC && e.x !== undefined && metricTag(e))
    : undefined;
  const taggedWho = tagged ? playerTeam(info, tagged.playerId) : undefined;

  const body = (
    <>
      <defs>
        <pattern id="mow" width="10.5" height={W} patternUnits="userSpaceOnUse">
          <rect width="5.25" height={W} fill="var(--board-stripe)" />
        </pattern>
        <marker id="rw-arrow" viewBox="0 0 6 6" refX="5" refY="3" markerWidth="4" markerHeight="4" orient="auto-start-reverse">
          <path d="M0,0 L6,3 L0,6 z" fill="var(--on-air)" />
        </marker>
      </defs>
      <rect x={-3} y={-3} width={L + 6} height={W + 6} fill="var(--board)" className="pitch-grass" />
      <rect x={0} y={0} width={L} height={W} fill="url(#mow)" className="pitch-grass" />
      <Markings />

      {quiet && children}

      {!rewind && !quiet && recent.map((e) => {
        const age = (clockSec - e.clockSec) / TRAIL_SEC;
        const fade = Math.max(0.08, 1 - age);
        const c = colors[e.teamId];
        if (e.x === undefined || e.y === undefined) return null;
        const x = mx(e.x), y = my(e.y);
        if (e.type === 'pass' && e.endX !== undefined && e.endY !== undefined) {
          return (
            <line key={e.id} x1={x} y1={y} x2={mx(e.endX)} y2={my(e.endY)} stroke={c}
              className={e.outcome === 'success' ? 'pt-draw' : undefined} pathLength={e.outcome === 'success' ? 1 : undefined}
              strokeWidth={0.4} strokeOpacity={fade * (e.outcome === 'success' ? 0.75 : 0.35)}
              strokeDasharray={e.outcome === 'success' ? undefined : '0.8 0.8'} />
          );
        }
        if (e.type === 'shot') {
          const goalX = e.teamId === info.home.teamId ? L : 0;
          const r = 0.9 + (e.xg ?? 0) * 6;
          return (
            <g key={e.id} opacity={Math.max(0.25, fade)} className="pt-shot">
              <line x1={x} y1={y} x2={goalX} y2={W / 2} stroke={c} strokeWidth={0.3} strokeDasharray="0.6 0.6" />
              <circle cx={x} cy={y} r={r} fill={e.shotResult === 'goal' ? 'var(--on-air)' : c}
                stroke="var(--chalk)" strokeWidth={0.25} className="pt-pop" />
            </g>
          );
        }
        if (e.highRecovery) {
          return (
            <rect key={e.id} x={x - 1.1} y={y - 1.1} width={2.2} height={2.2} transform={`rotate(45 ${x} ${y})`}
              fill="none" stroke={c} strokeWidth={0.5} opacity={Math.max(0.3, fade)} className="pt-regain" />
          );
        }
        return null;
      })}

      {rewind && <RewindLayer rewind={rewind} colors={colors} info={info} />}

      {!rewind && !quiet && cited.map((e) => e.x !== undefined && e.y !== undefined && (
        <circle key={`cite-${e.id}`} cx={mx(e.x)} cy={my(e.y)} r={2.6} fill="none" stroke="var(--on-air)" strokeWidth={0.45} />
      ))}

      {tracking && <TrackingLayer info={info} focusPlayer={focusPlayer} tags={tags} clockSec={clockSec} fi={fi} />}

      {ballAt && !quiet && !tracking && (
        <g className="ball" transform={`translate(${mx(ballAt.x)} ${my(ballAt.y)})`}>
          <circle r={1.6} fill="var(--chalk)" opacity={0.18} className="ball-halo" />
          <circle r={0.75} fill="var(--chalk)" />
        </g>
      )}
      {actor && who && (
        <Token key={actor.id} side={who.side} number={who.player.shirtNumber} x={mx(actor.x!)} y={my(actor.y!)} r={compact ? 2.6 : 2.2}
          focus={who.player.playerId === focusPlayer} label={who.player.name.split(' ').slice(-1)[0]} />
      )}
      {tagged && taggedWho && (
        <MetricTag key={`tag-${tagged.id}`} x={mx(tagged.x!)} y={my(tagged.y!)} side={taggedWho.side}
          name={taggedWho.player.name.split(' ').slice(-1)[0]} value={metricTag(tagged, fi)!} />
      )}
    </>
  );
  const svg = (
    <svg className={`pitch ${compact ? 'pitch-compact' : ''} ${bridge ? 'on-3d' : ''}`} viewBox={`-3 -3 ${L + 6} ${W + 6}`} role="img"
      aria-label={label ?? `Pitch showing the last ${Math.round(TRAIL_SEC / 60)} minutes of play`}>
      {body}
    </svg>
  );
  if (bridge) return bridge.board ? createPortal(svg, bridge.board) : null;
  return svg;
}

/** The replayed move: every step so far drawn as a numbered arrow, the current one in on-air yellow. */
function RewindLayer({ rewind, colors, info }: { rewind: { events: PitchEvent[]; step: number }; colors: Record<string, string>; info: MatchInfo }) {
  const shown = rewind.events.slice(0, rewind.step + 1);
  const passes = usePassInfo();
  const cur = rewind.events[Math.min(rewind.step, rewind.events.length - 1)];
  const better = cur ? passes[cur.id] : undefined;
  const betterWho = better?.betterTo ? playerTeam(info, better.betterTo) : undefined;
  return (
    <g className="rewind-layer">
      {shown.map((e, i) => {
        if (e.x === undefined || e.y === undefined) return null;
        const now = i === rewind.step;
        const c = now ? 'var(--on-air)' : colors[e.teamId];
        const x = mx(e.x), y = my(e.y);
        const opacity = now ? 1 : 0.35 + 0.5 * (i / Math.max(1, rewind.step));
        let mark: JSX.Element | null = null;
        if (e.type === 'pass' && e.endX !== undefined && e.endY !== undefined) {
          mark = (
            <line x1={x} y1={y} x2={mx(e.endX)} y2={my(e.endY)} stroke={c} strokeWidth={now ? 0.7 : 0.5}
              strokeDasharray={e.outcome === 'fail' ? '1 0.8' : undefined}
              markerEnd={now ? 'url(#rw-arrow)' : undefined} className={now ? 'rw-draw' : undefined} />
          );
        } else if (e.type === 'shot') {
          const goalX = e.teamId === info.home.teamId ? L : 0;
          const tx = e.endX !== undefined ? mx(e.endX) : goalX, ty = e.endY !== undefined ? my(e.endY) : W / 2;
          mark = (
            <g>
              <line x1={x} y1={y} x2={tx} y2={ty} stroke={c} strokeWidth={0.6} markerEnd={now ? 'url(#rw-arrow)' : undefined}
                className={now ? 'rw-draw' : undefined} />
              <circle cx={x} cy={y} r={0.9 + (e.xg ?? 0) * 6} fill={e.shotResult === 'goal' ? 'var(--on-air)' : c} stroke="var(--chalk)" strokeWidth={0.25} />
            </g>
          );
        } else if (e.highRecovery || e.type === 'tackle' || e.type === 'interception') {
          mark = <rect x={x - 1.3} y={y - 1.3} width={2.6} height={2.6} transform={`rotate(45 ${x} ${y})`} fill="none" stroke={c} strokeWidth={0.6} />;
        } else {
          mark = <circle cx={x} cy={y} r={0.8} fill={c} />;
        }
        return (
          <g key={e.id} opacity={opacity}>
            {mark}
            <text x={x} y={y - 2} className="rw-num" textAnchor="middle">{i + 1}</text>
          </g>
        );
      })}
      {/* The better option the passer had (from tracking at the moment of the pass): a dashed gold arrow. */}
      {cur && better?.betterX !== undefined && better.betterY !== undefined && cur.x !== undefined && cur.y !== undefined && (
        <g className="rw-better">
          <line x1={mx(cur.x)} y1={my(cur.y)} x2={mx(better.betterX)} y2={my(better.betterY)} strokeDasharray="1.2 0.9" markerEnd="url(#rw-arrow)" />
          <circle cx={mx(better.betterX)} cy={my(better.betterY)} r={1.8} />
          <text x={mx(better.betterX)} y={my(better.betterY) - 2.6} textAnchor="middle">
            {betterWho ? `${betterWho.player.name.split(' ').slice(-1)[0]} ` : ''}+{(better.betterGain ?? 0).toFixed(2)} xT
          </text>
        </g>
      )}
    </g>
  );
}

function Markings() {
  const s = { fill: 'none', stroke: 'var(--chalk-line)', strokeWidth: 0.3 } as const;
  return (
    <g className="pitch-markings">
      <rect x={0} y={0} width={L} height={W} {...s} />
      <line x1={L / 2} y1={0} x2={L / 2} y2={W} {...s} />
      <circle cx={L / 2} cy={W / 2} r={9.15} {...s} />
      <circle cx={L / 2} cy={W / 2} r={0.4} fill="var(--chalk-line)" />
      {[0, L].map((gx) => {
        const dir = gx === 0 ? 1 : -1;
        return (
          <g key={gx}>
            <rect x={gx === 0 ? 0 : L - 16.5} y={W / 2 - 20.16} width={16.5} height={40.32} {...s} />
            <rect x={gx === 0 ? 0 : L - 5.5} y={W / 2 - 9.16} width={5.5} height={18.32} {...s} />
            <circle cx={gx + dir * 11} cy={W / 2} r={0.4} fill="var(--chalk-line)" />
            <path d={`M ${gx + dir * 16.5} ${W / 2 - 7.3} A 9.15 9.15 0 0 ${gx === 0 ? 1 : 0} ${gx + dir * 16.5} ${W / 2 + 7.3}`} {...s} />
            <rect x={gx === 0 ? -1.5 : L} y={W / 2 - 3.66} width={1.5} height={7.32} {...s} />
          </g>
        );
      })}
    </g>
  );
}

/** Team colours; App sets --home and --away from the kits, lightened to read on the dark board. */
export function useTeamColors(info: MatchInfo): Record<string, string> {
  return useMemo(() => ({
    [info.home.teamId]: 'var(--home)',
    [info.away.teamId]: 'var(--away)',
  }), [info]);
}

/** A name tag with the metric that triggered it, placed above the player and kept inside the pitch. */
function MetricTag({ x, y, side, name, value }: { x: number; y: number; side: 'home' | 'away'; name: string; value: string }) {
  const text = `${name} · ${value}`;
  const w = text.length * 1.05 + 2.4;
  const cx = Math.min(Math.max(x, w / 2 + 0.5), L - w / 2 - 0.5);
  const top = y - 8 < -2 ? y + 4 : y - 8;
  return (
    <g className={`metric-tag side-${side}`} role="img" aria-label={text}>
      <line x1={x} y1={y} x2={cx} y2={top + 2.2} className="metric-tag-stem" />
      <rect x={cx - w / 2} y={top - 2.1} width={w} height={4.3} rx={0.7} className="metric-tag-box" />
      <text x={cx} y={top + 0.85} textAnchor="middle" className="metric-tag-text">{text}</text>
    </g>
  );
}

/** Seconds of match time a running tag (top speed, distance) stays on the player. */
const RUN_TAG_SEC = 40;

/**
 * Every player and the ball from the latest tracking frame. Only this layer re-renders with each frame; the moves
 * between frames are smoothed by a short CSS transition.
 */
function TrackingLayer({ info, focusPlayer, tags, clockSec, fi }:
  { info: MatchInfo; focusPlayer?: string; tags?: TagView[]; clockSec: number; fi?: boolean }) {
  const frame = useFrame();
  const who = useMemo(() => {
    const m = new Map<string, { side: 'home' | 'away'; number: number; name: string }>();
    for (const [team, side] of [[info.home, 'home'], [info.away, 'away']] as const)
      for (const p of team.players) m.set(p.playerId, { side, number: p.shirtNumber, name: p.name.split(' ').slice(-1)[0] });
    return m;
  }, [info]);
  if (!frame) return null;
  const tag = tags ? [...tags].reverse().find((t) => clockSec - t.atSec <= RUN_TAG_SEC && clockSec >= t.atSec - 5) : undefined;
  const tagged = tag ? frame.players.find((p) => p.id === tag.playerId) : undefined;
  const bx = mx(frame.ballX), by = my(frame.ballY), lift = Math.min(3, frame.ballZ * 0.6);
  return (
    <g className="trk">
      {frame.players.map((p) => {
        const w = who.get(p.id);
        if (!w) return null;
        const focus = p.id === focusPlayer || p.id === tag?.playerId;
        return (
          <g key={p.id} className={`trk-player side-${w.side} ${focus ? 'is-focus' : ''}`}
            style={{ transform: `translate(${mx(p.x).toFixed(2)}px, ${my(p.y).toFixed(2)}px)` }}>
            <circle r={1.35} className="trk-disc" />
            <text className="trk-num" textAnchor="middle" dy={0.48}>{w.number}</text>
            {focus && <text className="token-label" textAnchor="middle" dy={3.6}>{w.name}</text>}
          </g>
        );
      })}
      <ellipse className="trk-ball-shadow" style={{ transform: `translate(${bx.toFixed(2)}px, ${by.toFixed(2)}px)` }} rx={0.8} ry={0.45} />
      <g className="trk-ball" style={{ transform: `translate(${bx.toFixed(2)}px, ${(by - lift).toFixed(2)}px)` }}>
        <circle r={1.5} className="ball-halo" fill="var(--chalk)" opacity={0.18} />
        <circle r={0.7} fill="var(--chalk)" />
      </g>
      {tag && tagged && who.get(tag.playerId) && (
        <MetricTag key={`run-${tag.playerId}-${tag.atSec}`} x={mx(tagged.x)} y={my(tagged.y)} side={who.get(tag.playerId)!.side}
          name={who.get(tag.playerId)!.name}
          value={tag.kind === 'speed' ? `${tag.speedKmh.toFixed(1)} km/h` : `${tag.distanceKm.toFixed(0)} km ${fi ? 'juostu' : 'run'}`} />
      )}
    </g>
  );
}
