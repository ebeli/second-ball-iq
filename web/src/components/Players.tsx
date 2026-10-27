import { useEffect, useMemo, useRef, useState } from 'react';
import { isHardPass } from '../metrics';
import { api } from '../api';
import { roleLabel } from '../format';
import { Pitch } from './Pitch';
import { HeatLayer } from './Tactics';
import { Shirt } from './Shirt';
import type { MatchInfo, PitchEvent, PlayerInfo, PlayerSeason, ShapeReport, RunnerView } from '../types';

export interface MatchLine {
  passes: number; completed: number; shots: number; xg: number; goals: number; regains: number; tackles: number;
  /** Pass quality and speed, from the engine's per-event numbers (metrics.ts). */
  passMetres: number; hard: number; hardCompleted: number; longest: number; fastestShot: number;
}

/** This match's numbers for every player, counted from the event stream the board already has. */
export function useMatchLines(events: PitchEvent[]): Map<string, MatchLine> {
  return useMemo(() => {
    const lines = new Map<string, MatchLine>();
    const get = (id: string) => {
      let l = lines.get(id);
      if (!l) {
        l = { passes: 0, completed: 0, shots: 0, xg: 0, goals: 0, regains: 0, tackles: 0, passMetres: 0, hard: 0, hardCompleted: 0, longest: 0, fastestShot: 0 };
        lines.set(id, l);
      }
      return l;
    };
    for (const e of events) {
      if (!e.playerId) continue;
      const l = get(e.playerId);
      if (e.type === 'pass') {
        l.passes++;
        const ok = e.outcome === 'success';
        if (ok) l.completed++;
        l.passMetres += e.passMetres ?? 0;
        if (ok && (e.passMetres ?? 0) > l.longest) l.longest = e.passMetres!;
        if (isHardPass(e)) { l.hard++; if (ok) l.hardCompleted++; }
      }
      if (e.type === 'shot') {
        l.shots++; l.xg += e.xg ?? 0; if (e.shotResult === 'goal') l.goals++;
        l.fastestShot = Math.max(l.fastestShot, e.ballSpeedKmh ?? 0);
      }
      if (e.type === 'tackle' || e.type === 'interception') l.tackles++;
      if (e.highRecovery) l.regains++;
    }
    return lines;
  }, [events]);
}

const POS: Record<string, [string, string]> = {
  goalkeeper: ['GK', 'MV'], defender: ['DF', 'PP'], midfielder: ['MF', 'KK'], forward: ['FW', 'HY'],
};

/** Both squads with their numbers so far; click a player for the full card. */
export function PlayersPanel({ info, events, fi, onPlayer, running }:
  { info: MatchInfo; events: PitchEvent[]; fi?: boolean; onPlayer: (id: string) => void; running?: RunnerView[] }) {
  const lines = useMatchLines(events);
  const km = new Map((running ?? []).map((r) => [r.playerId, r.distanceKm]));
  return (
    <div className="squads">
      {[info.home, info.away].map((team) => (
        <table key={team.teamId} className="squad">
          <caption><span className={`chip chip-${team.teamId === info.home.teamId ? 'home' : 'away'}`}>{team.shortName}</span>{team.name}</caption>
          <thead>
            <tr>
              <th scope="col">{fi ? 'Pelaaja' : 'Player'}</th>
              <th scope="col" title={fi ? 'Onnistuneet / syötöt' : 'Completed / passes'}>{fi ? 'Syötöt' : 'Passes'}</th>
              <th scope="col">{fi ? 'Laukaukset' : 'Shots'}</th>
              <th scope="col">xG</th>
              <th scope="col" title={fi ? 'Korkeat riistot' : 'High regains'}>{fi ? 'Riistot' : 'Regains'}</th>
              {km.size > 0 && <th scope="col" title={fi ? 'Juostu matka' : 'Distance covered'}>km</th>}
            </tr>
          </thead>
          <tbody>
            {team.players.map((p) => {
              const l = lines.get(p.playerId);
              return (
                <tr key={p.playerId}>
                  <th scope="row">
                    <button type="button" className="linkish" onClick={() => onPlayer(p.playerId)}>
                      <Shirt side={team.teamId === info.home.teamId ? 'home' : 'away'} number={p.shirtNumber} size={28} /> {p.name}
                    </button>
                    {l && l.goals > 0 && <span className="goal-tag" title={fi ? 'Maalit' : 'Goals'}>{'⚽'.repeat(Math.min(l.goals, 3))}</span>}
                  </th>
                  <td className="num">{l ? `${l.completed}/${l.passes}` : '–'}</td>
                  <td className="num">{l?.shots || '–'}</td>
                  <td className="num">{l && l.xg > 0 ? l.xg.toFixed(2) : '–'}</td>
                  <td className="num">{l?.regains || '–'}</td>
                  {km.size > 0 && <td className="num">{km.has(p.playerId) ? km.get(p.playerId)!.toFixed(1) : '–'}</td>}
                </tr>
              );
            })}
          </tbody>
        </table>
      ))}
    </div>
  );
}

/** A player's card: this match from the stream, the season from Match Forge's generated archive. */
export function PlayerCard({ playerId, info, events, shapes, fi, onClose, inline, running }:
  { playerId: string; info: MatchInfo; events: PitchEvent[]; shapes?: ShapeReport[]; fi?: boolean; onClose: () => void;
    /** In the fan view's right panel: no dialog, and the heat map is drawn on the match stage instead. */
    inline?: boolean;
    /** Running from tracking, when the match has it. */
    running?: RunnerView[] }) {
  const run = running?.find((r) => r.playerId === playerId);
  const team = info.home.players.some((p) => p.playerId === playerId) ? info.home : info.away;
  const side = team === info.home ? 'home' : 'away';
  const player: PlayerInfo | undefined = team.players.find((p) => p.playerId === playerId);
  const line = useMatchLines(events).get(playerId);
  const [season, setSeason] = useState<PlayerSeason | null>();
  const dialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    let live = true;
    setSeason(undefined);
    api.season(playerId).then((s) => { if (live) setSeason(s); }).catch(() => { if (live) setSeason(null); });
    return () => { live = false; };
  }, [playerId]);

  useEffect(() => {
    if (inline) return;
    const d = dialog.current;
    if (d && !d.open) { try { d.showModal(); } catch { d.setAttribute('open', ''); } }
  }, [inline]);

  if (!player) return null;
  const role = shapes?.flatMap((r) => r.players).find((p) => p.playerId === playerId)?.role;
  const clockNow = events.length ? events[events.length - 1].clockSec : 0;
  const pos = POS[player.position.toLowerCase()] ?? [player.position, player.position];
  const pct = (a: number, b: number) => (b > 0 ? `${Math.round((a / b) * 100)}%` : '–');

  const content = (
      <div className="pc-inner">
        <header className="pc-head">
          <Shirt side={side} number={player.shirtNumber} size={64} title={`${player.name}, ${player.shirtNumber}`} />
          <div>
            <h2 className="pc-name">{player.name}</h2>
            <p className="pc-meta">{team.name} · {pos[fi ? 1 : 0]}{player.starter === false ? (fi ? ' · vaihtopelaaja' : ' · substitute') : ''}</p>
            {role && <p className="pc-role">{fi ? 'Pelaa nyt: ' : 'Playing as: '}<strong>{fi ? roleLabel[role].fi : roleLabel[role].en}</strong></p>}
          </div>
          <button type="button" className="pc-close" onClick={() => (inline ? onClose() : dialog.current?.close())} aria-label={fi ? 'Sulje' : 'Close'}>×</button>
        </header>

        {!inline && <section>
          <h3>{fi ? 'Missä pelaaja on ollut pallossa' : 'Where they have had the ball'}</h3>
          <div className="pc-heat">
            <Pitch events={events} clockSec={clockNow} info={info} compact label={fi ? 'Lämpökartta' : 'Heat map'}>
              <HeatLayer events={events} playerId={playerId} fromSec={0} toSec={clockNow + 1} color={side === 'home' ? 'var(--home)' : 'var(--away)'} />
            </Pitch>
          </div>
        </section>}

        <section>
          <h3>{fi ? 'Tässä ottelussa' : 'This match'}</h3>
          <dl className="pc-stats">
            <Stat label={fi ? 'Maalit' : 'Goals'} value={line?.goals ?? 0} big />
            <Stat label={fi ? 'Laukaukset' : 'Shots'} value={line?.shots ?? 0} />
            <Stat label="xG" value={(line?.xg ?? 0).toFixed(2)} />
            <Stat label={fi ? 'Syötöt' : 'Passes'} value={line ? `${line.completed}/${line.passes}` : '0/0'} />
            <Stat label={fi ? 'Syöttötarkkuus' : 'Pass accuracy'} value={line ? pct(line.completed, line.passes) : '–'} />
            <Stat label={fi ? 'Korkeat riistot' : 'High regains'} value={line?.regains ?? 0} />
            <Stat label={fi ? 'Syötön keskipituus' : 'Average pass'} value={line && line.passes ? `${Math.round(line.passMetres / line.passes)} m` : '–'} />
            <Stat label={fi ? 'Vaikeat syötöt perille' : 'Hard passes completed'} value={line ? `${line.hardCompleted}/${line.hard}` : '0/0'} />
            <Stat label={fi ? 'Pisin onnistunut syöttö' : 'Longest completed pass'} value={line && line.longest ? `${Math.round(line.longest)} m` : '–'} />
            <Stat label={fi ? 'Kovin laukaus' : 'Hardest shot'} value={line && line.fastestShot ? `${Math.round(line.fastestShot)} km/h` : '–'} />
            {run && <Stat label={fi ? 'Juostu matka' : 'Distance covered'} value={`${run.distanceKm.toFixed(1)} km`} />}
            {run && <Stat label={fi ? 'Huippunopeus' : 'Top speed'} value={`${run.topSpeedKmh.toFixed(1)} km/h`} />}
            {run && <Stat label={fi ? 'Spurtit' : 'Sprints'} value={run.sprints} />}
          </dl>
        </section>

        <section>
          <h3>{fi ? 'Kausi tähän asti' : 'Season so far'}</h3>
          {season === undefined && <p className="muted">{fi ? 'Ladataan…' : 'Loading…'}</p>}
          {season === null && <p className="muted">{fi ? 'Ei kausitietoja.' : 'No season numbers.'}</p>}
          {season && (
            <>
              <dl className="pc-stats">
                <Stat label={fi ? 'Ottelut' : 'Matches'} value={season.matches} />
                <Stat label={fi ? 'Maalit' : 'Goals'} value={season.goals} big />
                <Stat label={fi ? 'Syötöt maaliin' : 'Assists'} value={season.assists} />
                <Stat label={fi ? 'Laukaukset' : 'Shots'} value={season.shots} />
                <Stat label="xG" value={season.xg.toFixed(1)} />
                <Stat label={fi ? 'Syöttötarkkuus' : 'Pass accuracy'} value={pct(season.completedPasses, season.passes)} />
                {season.penaltiesTaken > 0 && <Stat label={fi ? 'Rangaistuspotkut' : 'Penalties'} value={`${season.penaltiesScored}/${season.penaltiesTaken}`} />}
              </dl>
              {season.goals > 0 && season.xg > 0 && (
                <p className="pc-note">
                  {season.goals - season.penaltiesScored > season.xg * 1.15
                    ? (fi ? 'Viimeistelee paremmin kuin paikkojen laatu ennustaa.' : 'Finishing above what the chances were worth.')
                    : season.goals - season.penaltiesScored < season.xg * 0.85
                      ? (fi ? 'Tekee vähemmän maaleja kuin paikkojen laatu ennustaa.' : 'Scoring fewer than the chances were worth.')
                      : (fi ? 'Maalit vastaavat paikkojen laatua.' : 'Scoring about what the chances were worth.')}
                </p>
              )}
            </>
          )}
          <p className="pc-foot">{fi ? 'Kausi on Match Forgen tuottama, kuvitteellinen.' : 'The season is generated by Match Forge; every player is fictional.'}</p>
        </section>
      </div>
  );
  if (inline) return <section className={`player-card inline pc-${side}`}>{content}</section>;
  return (
    <dialog ref={dialog} className={`player-card pc-${side}`} onClose={onClose} onClick={(e) => { if (e.target === dialog.current) dialog.current?.close(); }}>
      {content}
    </dialog>
  );
}

function Stat({ label, value, big }: { label: string; value: string | number; big?: boolean }) {
  return <div className={big ? 'big' : undefined}><dt>{label}</dt><dd className="num">{value}</dd></div>;
}
