import { useMemo, useState } from 'react';
import { isHardPass } from '../metrics';
import { Pitch } from './Pitch';
import { ShapeDefs, ShapeLayer } from './Tactics';
import type { MatchInfo, PitchEvent, ShapeReport, RunnerView } from '../types';

type Mode = 'live' | 'lineups';

/**
 * The fan's match centre, on top of the Live feed: the ball moving on a small board, or both teams' shapes with the
 * numbers on their shirts, and the match numbers side by side in the two kits.
 */
export function MatchCentre({ info, events, shapes, clockSec, fi, focusPlayer, onPlayer }:
  { info: MatchInfo; events: PitchEvent[]; shapes: ShapeReport[]; clockSec: number; fi?: boolean; focusPlayer?: string;
    onPlayer: (id: string) => void }) {
  const [mode, setMode] = useState<Mode>('live');
  const [open, setOpen] = useState(true);
  const stats = useTeamStats(events, info);
  const latest = useMemo(() => [info.home.teamId, info.away.teamId]
    .map((t) => [...shapes].reverse().find((s) => s.teamId === t))
    .filter((s): s is ShapeReport => !!s), [shapes, info]);

  return (
    <section className="centre" aria-label={fi ? 'Ottelukeskus' : 'Match centre'}>
      <div className="centre-bar">
        <div className="seg" role="group" aria-label={fi ? 'Näkymä' : 'Board'}>
          <button type="button" aria-pressed={mode === 'live'} onClick={() => { setMode('live'); setOpen(true); }}>{fi ? 'Pallo' : 'Ball'}</button>
          <button type="button" aria-pressed={mode === 'lineups'} onClick={() => { setMode('lineups'); setOpen(true); }}>{fi ? 'Kokoonpanot' : 'Line-ups'}</button>
        </div>
        <button type="button" className="link centre-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? (fi ? 'Piilota kenttä' : 'Hide pitch') : (fi ? 'Näytä kenttä' : 'Show pitch')}
        </button>
      </div>

      {open && mode === 'live' && (
        <Pitch events={events} clockSec={clockSec} info={info} compact carrier fi={fi} focusPlayer={focusPlayer}
          label={fi ? 'Pallo ja viimeiset tilanteet' : 'The ball and the last moves'} />
      )}
      {open && mode === 'lineups' && (
        latest.length > 0 ? (
          <>
            <Pitch events={events} clockSec={clockSec} info={info} compact label={fi ? 'Joukkueiden muodot' : 'Both teams\' shapes'}>
              <ShapeDefs />
              <ShapeLayer reports={latest} info={info} fi={fi} highlight={focusPlayer} onPlayer={onPlayer} />
            </Pitch>
            <p className="centre-note">
              {fi ? 'Pelaajat siellä, missä he ovat olleet pallossa viimeisen vartin. Napauta pelaajaa.' : 'Players where they have been on the ball over the last 15 minutes. Tap a player.'}
            </p>
          </>
        ) : <p className="centre-note">{fi ? 'Muodot näkyvät, kun peliä on pelattu muutama minuutti.' : 'The shapes appear after a few minutes of play.'}</p>
      )}

      <dl className="duel" aria-label={fi ? 'Ottelun luvut' : 'Match numbers'}>
        <Duel label={fi ? 'Pallonhallinta' : 'Possession'} home={stats.home.passes} away={stats.away.passes} percent />
        <Duel label={fi ? 'Laukaukset' : 'Shots'} home={stats.home.shots} away={stats.away.shots} />
        <Duel label="xG" home={stats.home.xg} away={stats.away.xg} digits={2} />
        <Duel label={fi ? 'Korkeat riistot' : 'High regains'} home={stats.home.regains} away={stats.away.regains} />
      </dl>
    </section>
  );
}

/** The match numbers side by side in the two kits: possession, shots, xG, high regains. */
export function DuelStats({ info, events, fi, running }: { info: MatchInfo; events: PitchEvent[]; fi?: boolean; running?: RunnerView[] }) {
  const stats = useTeamStats(events, info);
  const ran = (teamId: string) => (running ?? []).filter((r) => r.teamId === teamId);
  const km = (teamId: string) => ran(teamId).reduce((a, r) => a + r.distanceKm, 0);
  const sprints = (teamId: string) => ran(teamId).reduce((a, r) => a + r.sprints, 0);
  return (
    <dl className="duel" aria-label={fi ? 'Ottelun luvut' : 'Match numbers'}>
      <div className="duel-teams" aria-hidden="true"><span>{info.home.shortName}</span><span>{info.away.shortName}</span></div>
      <Duel label={fi ? 'Pallonhallinta' : 'Possession'} home={stats.home.passes} away={stats.away.passes} percent />
      <Duel label={fi ? 'Laukaukset' : 'Shots'} home={stats.home.shots} away={stats.away.shots} />
      <Duel label="xG" home={stats.home.xg} away={stats.away.xg} digits={2} />
      <Duel label={fi ? 'Korkeat riistot' : 'High regains'} home={stats.home.regains} away={stats.away.regains} />
      <Duel label={fi ? 'Syöttötarkkuus' : 'Pass accuracy'} home={rate(stats.home.completed, stats.home.passes)} away={rate(stats.away.completed, stats.away.passes)}
        format={(v) => `${Math.round(v * 100)}%`} />
      <Duel label={fi ? 'Syötön keskipituus' : 'Average pass'} home={avg(stats.home.metres, stats.home.passes)} away={avg(stats.away.metres, stats.away.passes)}
        format={(v) => `${Math.round(v)} m`} />
      <Duel label={fi ? 'Vaikeat syötöt perille' : 'Hard passes completed'} home={stats.home.hardCompleted} away={stats.away.hardCompleted} />
      <Duel label={fi ? 'Kovin laukaus' : 'Hardest shot'} home={stats.home.fastestShot} away={stats.away.fastestShot}
        format={(v) => (v ? `${Math.round(v)} km/h` : '–')} />
      {running && running.length > 0 && (
        <>
          <Duel label={fi ? 'Juostu matka' : 'Distance covered'} home={km(info.home.teamId)} away={km(info.away.teamId)} format={(v) => `${v.toFixed(1)} km`} />
          <Duel label={fi ? 'Spurtit' : 'Sprints'} home={sprints(info.home.teamId)} away={sprints(info.away.teamId)} />
        </>
      )}
    </dl>
  );
}

const rate = (n: number, of: number) => (of ? n / of : 0);
const avg = (sum: number, n: number) => (n ? sum / n : 0);

function Duel({ label, home, away, percent, digits = 0, format }:
  { label: string; home: number; away: number; percent?: boolean; digits?: number; format?: (v: number) => string }) {
  const total = home + away;
  const share = total > 0 ? home / total : 0.5;
  const show = (v: number) => (format ? format(v) : percent ? `${Math.round((total > 0 ? v / total : 0.5) * 100)}%` : v.toFixed(digits));
  return (
    <div className="duel-row">
      <dd className="duel-v home num">{show(home)}</dd>
      <dt>{label}</dt>
      <dd className="duel-v away num">{show(away)}</dd>
      <div className="duel-bar" aria-hidden="true">
        <span className="duel-home" style={{ width: `${share * 100}%` }} />
        <span className="duel-away" style={{ width: `${(1 - share) * 100}%` }} />
      </div>
    </div>
  );
}

interface Side {
  passes: number; completed: number; shots: number; xg: number; regains: number;
  metres: number; hardCompleted: number; fastestShot: number;
}

/** Counted from the event stream: possession is the share of passes, the way broadcasters usually show it. */
function useTeamStats(events: PitchEvent[], info: MatchInfo) {
  return useMemo(() => {
    const blank = (): Side => ({ passes: 0, completed: 0, shots: 0, xg: 0, regains: 0, metres: 0, hardCompleted: 0, fastestShot: 0 });
    const out = { home: blank(), away: blank() };
    for (const e of events) {
      const s = e.teamId === info.home.teamId ? out.home : e.teamId === info.away.teamId ? out.away : undefined;
      if (!s) continue;
      if (e.type === 'pass') {
        s.passes++;
        s.metres += e.passMetres ?? 0;
        if (e.outcome === 'success') { s.completed++; if (isHardPass(e)) s.hardCompleted++; }
      }
      if (e.type === 'shot') { s.shots++; s.xg += e.xg ?? 0; s.fastestShot = Math.max(s.fastestShot, e.ballSpeedKmh ?? 0); }
      if (e.highRecovery) s.regains++;
    }
    return out;
  }, [events, info]);
}

/** Who scored and when, for the score header: "Sharma 41'". */
export function useScorers(events: PitchEvent[], info: MatchInfo) {
  return useMemo(() => {
    const names = new Map([...info.home.players, ...info.away.players].map((p) => [p.playerId, p.name.split(' ').slice(-1)[0]]));
    const out = { home: [] as string[], away: [] as string[] };
    for (const e of events) {
      if (e.type !== 'shot' || e.shotResult !== 'goal') continue;
      const line = `${names.get(e.playerId ?? '') ?? '?'} ${Math.floor(e.clockSec / 60) + 1}'`;
      (e.teamId === info.home.teamId ? out.home : out.away).push(line);
    }
    return out;
  }, [events, info]);
}
