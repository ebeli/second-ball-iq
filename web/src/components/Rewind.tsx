import { useEffect, useMemo, useState } from 'react';
import { describeMetrics } from '../metrics';
import { describeLane, usePassInfo } from '../spatial';
import { clock, playerName } from '../format';
import type { MatchInfo, PitchEvent } from '../types';

/** Longest stretch replayed in full; beyond this only the cited events are shown. */
const MAX_MOVE = 40;
const STEP_MS = 750;

/**
 * The events to replay for a set of cited ids. A move (a chain of passes into a shot, a goal) is replayed in full,
 * every touch between the first and last cited event; a pattern spread over minutes (pressing) replays just the
 * cited moments.
 */
export function rewindEvents(events: PitchEvent[], ids: string[]): PitchEvent[] {
  if (ids.length === 0) return [];
  const wanted = new Set(ids);
  const cited = events.filter((e) => wanted.has(e.id));
  if (cited.length === 0) return [];
  const from = moveStart(events, cited), to = cited[cited.length - 1].seq;
  const span = events.filter((e) => e.seq >= from && e.seq <= to && e.x !== undefined && e.type !== 'pressure' && e.type !== 'substitution');
  return (span.length <= MAX_MOVE ? span : cited).filter((e) => e.x !== undefined);
}

/** Touches a replay reaches back for when it ends in a shot. */
const MAX_LEAD_UP = 12;
const LEAD_UP_SEC = 30;

/**
 * Where the replay starts. A move that ends in a shot or a goal is shown from where that team won or started the
 * possession (at most 12 touches or 30 seconds back), even when only the last touches were cited, so "the 6-pass move"
 * really shows six passes.
 */
function moveStart(events: PitchEvent[], cited: PitchEvent[]): number {
  const first = cited[0], last = cited[cited.length - 1];
  if (last.type !== 'shot' && last.type !== 'goal') return first.seq;
  let i = events.findIndex((e) => e.seq === first.seq);
  let start = first.seq, touches = 0;
  for (i = i - 1; i >= 0 && touches < MAX_LEAD_UP; i--) {
    const e = events[i];
    if (e.type === 'pressure' || e.type === 'substitution') continue;
    if (e.teamId !== last.teamId || e.type === 'possessionChange' || e.type === 'kickOff' || e.type === 'periodStart') break;
    if (last.clockSec - e.clockSec > LEAD_UP_SEC) break;
    start = e.seq;
    touches++;
  }
  return start;
}

/** Plays a rewind: a step every 750 ms, with play/pause and a scrubber. Restarts whenever the move changes. */
export function useRewind(moves: PitchEvent[]) {
  const [step, setStep] = useState(0);
  const [playing, setPlaying] = useState(true);
  const key = moves.map((e) => e.id).join(',');

  useEffect(() => { setStep(0); setPlaying(true); }, [key]);
  useEffect(() => {
    if (!playing || moves.length === 0) return;
    if (step >= moves.length - 1) { setPlaying(false); return; }
    const t = window.setTimeout(() => setStep((s) => s + 1), STEP_MS);
    return () => window.clearTimeout(t);
  }, [playing, step, moves.length]);

  return {
    step,
    playing,
    setStep: (s: number) => { setPlaying(false); setStep(s); },
    toggle: () => {
      if (!playing && step >= moves.length - 1) setStep(0);
      setPlaying(!playing);
    },
  };
}

interface BarProps {
  title: string;
  moves: PitchEvent[];
  info: MatchInfo;
  rewind: ReturnType<typeof useRewind>;
  comparison?: { label: string; before: string; after: string }[];
  fi?: boolean;
  onClose: () => void;
  closeLabel?: string;
}

/** The controls under the pitch while a rewind runs: what is being replayed, the current touch, and a scrubber. */
export function RewindBar({ title, moves, info, rewind, comparison, fi, onClose, closeLabel }: BarProps) {
  const e = moves[Math.min(rewind.step, moves.length - 1)];
  const passes = usePassInfo();
  const lane = e ? passes[e.id] : undefined;
  const caption = useMemo(() => {
    if (!e) return '';
    const m = [describeMetrics(e, fi), lane ? describeLane(lane, info, fi) : ''].filter(Boolean).join(' · ');
    return m ? `${describe(e, info, fi)} ${m}` : describe(e, info, fi);
  }, [e, info, fi, lane]);
  if (!e) return null;
  return (
    <div className="rewind-bar" role="group" aria-label={fi ? 'Taktinen kelaus' : 'Tactical Rewind'}>
      <div className="rw-head">
        <span className="rw-tag">{fi ? 'Kelaus' : 'Rewind'}</span>
        <span className="rw-title">{title}</span>
        <button type="button" className="rw-close" onClick={onClose}>{closeLabel ?? (fi ? 'Takaisin liveen' : 'Back to live')}</button>
      </div>
      <div className="rw-controls">
        <button type="button" className="rw-play" onClick={rewind.toggle} aria-label={rewind.playing ? 'Pause' : 'Play'}>
          {rewind.playing ? <PauseIcon /> : <PlayIcon />}
        </button>
        <input type="range" min={0} max={moves.length - 1} value={rewind.step} aria-label={fi ? 'Vaihe' : 'Step'}
          onChange={(ev) => rewind.setStep(Number(ev.target.value))} />
        <span className="rw-step num">{rewind.step + 1}/{moves.length}</span>
      </div>
      <p className="rw-caption"><span className="num">{clock(e.clockSec)}</span> {caption}</p>
      {comparison && comparison.length > 0 && (
        <dl className="rw-compare">
          {comparison.map((c) => (
            <div key={c.label}>
              <dt>{c.label}</dt>
              <dd><span className="rw-before">{c.before}</span> → <strong>{c.after}</strong></dd>
            </div>
          ))}
        </dl>
      )}
    </div>
  );
}

function describe(e: PitchEvent, info: MatchInfo, fi?: boolean): string {
  const who = playerName(info, e.playerId) ?? (e.teamId === info.home.teamId ? info.home.name : info.away.name);
  const sp = e.setPiece ? (fi ? { corner: ' kulmapotkusta', freeKick: ' vapaapotkusta', penalty: ' pilkulta' } : { corner: ' from a corner', freeKick: ' from a free kick', penalty: ' from the spot' })[e.setPiece] : '';
  switch (e.type) {
    case 'pass':
      return fi
        ? `${who} syöttää${sp}${e.outcome === 'fail' ? ', mutta syöttö ei mene perille' : ''}.`
        : `${who} passes${sp}${e.outcome === 'fail' ? ', and it goes astray' : ''}.`;
    case 'shot': {
      const xg = e.xg !== undefined ? ` (xG ${e.xg.toFixed(2)})` : '';
      const res = fi
        ? { goal: 'MAALI!', saved: 'torjutaan.', offTarget: 'ohi.', blocked: 'blokataan.' }
        : { goal: 'GOAL!', saved: 'saved.', offTarget: 'off target.', blocked: 'blocked.' };
      return fi ? `${who} laukoo${sp}${xg}: ${res[e.shotResult ?? 'offTarget']}` : `${who} shoots${sp}${xg}: ${res[e.shotResult ?? 'offTarget']}`;
    }
    case 'goal': return fi ? `${who}: maali!` : `${who} scores!`;
    case 'tackle': return fi ? `${who} taklaa${e.highRecovery ? ' korkealla' : ''}.` : `${who} wins a tackle${e.highRecovery ? ' high up the pitch' : ''}.`;
    case 'interception': return fi ? `${who} katkaisee${e.highRecovery ? ' korkealla' : ''}.` : `${who} intercepts${e.highRecovery ? ' high up the pitch' : ''}.`;
    case 'foul': return fi ? `${who} rikkoo.` : `Foul by ${who}.`;
    default:
      return e.highRecovery ? (fi ? `${who} riistää pallon korkealla.` : `${who} wins the ball high up the pitch.`) : (fi ? `${who} pallossa.` : `${who} on the ball.`);
  }
}

function PlayIcon() {
  return <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M4 2.5v11l9-5.5z" fill="currentColor" /></svg>;
}
function PauseIcon() {
  return <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M4 2.5h3v11H4zM9 2.5h3v11H9z" fill="currentColor" /></svg>;
}

/** Before → after numbers for the patterns that are a change over time. */
export function comparisonFor(kind: string, facts: Record<string, number>, fi?: boolean) {
  const out: { label: string; before: string; after: string }[] = [];
  if (kind === 'pressingShift' && facts.highRecoveries !== undefined && facts.highRecoveriesBefore !== undefined)
    out.push({ label: fi ? 'Korkeat riistot' : 'High regains', before: String(facts.highRecoveriesBefore), after: String(facts.highRecoveries) });
  if (kind === 'possessionIllusion' && facts.possessionShare !== undefined)
    out.push({ label: fi ? 'Pallonhallinta → laukaukset' : 'Possession → shots', before: `${Math.round(facts.possessionShare * 100)}%`, after: String(facts.shots ?? 0) });
  return out;
}
