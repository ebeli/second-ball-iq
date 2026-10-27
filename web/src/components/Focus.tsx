import { useEffect, useState } from 'react';
import { roleLabel } from '../format';
import { useArrivals } from '../hooks/useArrivals';
import { Pitch } from './Pitch';
import { useMatchLines } from './Players';
import { HeatLayer } from './Tactics';
import { Shirt } from './Shirt';
import type { MatchInfo, PitchEvent, RunnerView, ShapeReport } from '../types';

/** Asks the 3D stage to switch to the player cam (it follows the locked or followed player). */
export function watchWithPlayerCam() {
  window.dispatchEvent(new CustomEvent('s3d-cam', { detail: 'player' }));
}

/** The followed player's running from tracking: distance, top speed, now, sprints. Nothing until tracking starts. */
export function RunningStrip({ run, fi }: { run?: RunnerView; fi?: boolean }) {
  if (!run) return null;
  return (
    <dl className="run-strip" aria-label={fi ? 'Juoksu seurantadatasta' : 'Running, from tracking'}>
      <div><dt>{fi ? 'Matka' : 'Distance'}</dt><dd className="num">{run.distanceKm.toFixed(1)}<small> km</small></dd></div>
      <div><dt>{fi ? 'Huippu' : 'Top speed'}</dt><dd className="num">{run.topSpeedKmh.toFixed(1)}<small> km/h</small></dd></div>
      <div><dt>{fi ? 'Nyt' : 'Now'}</dt><dd className="num live">{run.speedKmh.toFixed(0)}<small> km/h</small></dd></div>
      <div><dt>{fi ? 'Spurtit' : 'Sprints'}</dt><dd className="num">{run.sprints}</dd></div>
    </dl>
  );
}

/** "My player" mode: the player's live numbers, role and heat map, pinned above the feed. */
export function PlayerSpotlight({ playerId, info, events, shapes, fi, onOpen, running, wide }:
  { playerId: string; info: MatchInfo; events: PitchEvent[]; shapes: ShapeReport[]; fi?: boolean; onOpen: () => void; running?: RunnerView[]; wide?: boolean }) {
  const team = info.home.players.some((p) => p.playerId === playerId) ? info.home : info.away;
  const side = team === info.home ? 'home' : 'away';
  const player = team.players.find((p) => p.playerId === playerId);
  const line = useMatchLines(events).get(playerId);
  const role = shapes.flatMap((r) => r.players).find((p) => p.playerId === playerId)?.role;
  const now = events.length ? events[events.length - 1].clockSec : 0;
  if (!player) return null;
  return (
    <section className={`spotlight side-${side}`} aria-label={fi ? 'Pelaajasi' : 'Your player'}>
      <div className="spot-head">
        <Shirt side={side} number={player.shirtNumber} size={52} star title={`${player.name}, ${player.shirtNumber}`} />
        <div>
          <p className="spot-name">{player.name}</p>
          <p className="spot-meta">{team.shortName}{role ? ` · ${fi ? roleLabel[role].fi : roleLabel[role].en}` : ''}</p>
        </div>
        <button type="button" className="link" onClick={onOpen}>{fi ? 'Pelaajakortti' : 'Player card'}</button>
      </div>
      <dl className="spot-stats">
        <div><dt>{fi ? 'Maalit' : 'Goals'}</dt><dd>{line?.goals ?? 0}</dd></div>
        <div><dt>{fi ? 'Laukaukset' : 'Shots'}</dt><dd>{line?.shots ?? 0}</dd></div>
        <div><dt>xG</dt><dd>{(line?.xg ?? 0).toFixed(2)}</dd></div>
        <div><dt>{fi ? 'Syötöt' : 'Passes'}</dt><dd>{line ? `${line.completed}/${line.passes}` : '0/0'}</dd></div>
        <div><dt>{fi ? 'Riistot' : 'Regains'}</dt><dd>{line?.regains ?? 0}</dd></div>
      </dl>
      <RunningStrip run={running?.find((r) => r.playerId === playerId)} fi={fi} />
      {wide && <button type="button" className="cam-btn" onClick={watchWithPlayerCam}>{fi ? '◎ Katso pelaajakameralla' : '◎ Watch with the player cam'}</button>}
      <div className="spot-heat">
        <Pitch events={events} clockSec={now} info={info} compact label={fi ? 'Lämpökartta' : 'Heat map'}>
          <HeatLayer events={events} playerId={playerId} fromSec={0} toSec={now + 1} color={side === 'home' ? 'var(--home)' : 'var(--away)'} />
        </Pitch>
      </div>
    </section>
  );
}

/** A short toast whenever the focused player does something that matters: a shot, a goal, a regain high up. */
export function PlayerAlerts({ playerId, info, events, fi, matchKey }:
  { playerId: string; info: MatchInfo; events: PitchEvent[]; fi?: boolean; matchKey: string }) {
  const [alert, setAlert] = useState<{ id: string; text: string }>();
  const name = [...info.home.players, ...info.away.players].find((p) => p.playerId === playerId)?.name ?? '';
  const mine = events.filter((e) => e.playerId === playerId && (e.type === 'shot' || e.type === 'goal' || e.highRecovery));
  useArrivals(mine, (e) => e.id, `${matchKey}/${playerId}`, (e) => {
    const result = { goal: fi ? 'MAALI!' : 'GOAL!', saved: fi ? 'torjuttu' : 'saved', offTarget: fi ? 'ohi' : 'off target', blocked: fi ? 'blokattu' : 'blocked' };
    const text = e.type === 'goal' ? `${name}: ${result.goal}`
      : e.type === 'shot' ? `${name} ${fi ? 'laukoo' : 'shoots'}: ${result[e.shotResult ?? 'offTarget']}${e.xg !== undefined ? ` (xG ${e.xg.toFixed(2)})` : ''}`
      : `${name} ${fi ? 'riistää pallon korkealla' : 'wins the ball high up the pitch'}`;
    setAlert({ id: e.id, text });
  });
  useEffect(() => {
    if (!alert) return;
    const t = window.setTimeout(() => setAlert(undefined), 5000);
    return () => window.clearTimeout(t);
  }, [alert]);
  if (!alert) return null;
  return <div key={alert.id} className="player-alert" role="status"><span aria-hidden="true">★</span> {alert.text}</div>;
}
