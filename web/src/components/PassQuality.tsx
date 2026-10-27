import { useMemo } from 'react';
import { clock } from '../format';
import { playerTeam } from '../kit';
import { band, bandLabel, FAST_SHOT_KMH, HARD_PASS, LONG_PASS_M, MEDIUM_PASS, type Band } from '../metrics';
import { MoveReplay } from './FanParts';
import { useStage } from './Stage';
import type { MatchInfo, PitchEvent, RunnerView } from '../types';

/**
 * Analysis › Running, passes, speed: who has run furthest and fastest (from tracking), how hard each team's passing is
 * and how well it copes, the hardest passes that came off and the fastest shots. Every number comes from the engine
 * (tracking positions, pass length, a 0 to 100 difficulty rating, ball speed); a pass or shot opens on the pitch.
 */
export function PassQualityPanel({ info, events, fi, running }: { info: MatchInfo; events: PitchEvent[]; fi?: boolean; running?: RunnerView[] }) {
  const stage = useStage();
  const sides = [info.home, info.away];

  const table = useMemo(() => {
    const blank = () => ({ easy: [0, 0], medium: [0, 0], hard: [0, 0] } as Record<Band, [number, number]>);
    const out: Record<string, Record<Band, [number, number]>> = { [info.home.teamId]: blank(), [info.away.teamId]: blank() };
    for (const e of events) {
      if (e.type !== 'pass' || e.passDifficulty === undefined || !out[e.teamId]) continue;
      const cell = out[e.teamId][band(e.passDifficulty)];
      cell[1]++;
      if (e.outcome === 'success') cell[0]++;
    }
    return out;
  }, [events, info]);

  const hardest = useMemo(() => events
    .filter((e) => e.type === 'pass' && e.outcome === 'success' && e.passDifficulty !== undefined && !e.setPiece)
    .sort((a, b) => b.passDifficulty! - a.passDifficulty! || (b.passMetres ?? 0) - (a.passMetres ?? 0))
    .slice(0, 5), [events]);

  const fastest = useMemo(() => events
    .filter((e) => e.type === 'shot' && e.ballSpeedKmh !== undefined)
    .sort((a, b) => b.ballSpeedKmh! - a.ballSpeedKmh!)
    .slice(0, 5), [events]);

  const show = (e: PitchEvent, label: string) => stage.show?.({
    key: `pq-${e.id}`, label,
    node: <MoveReplay ids={[e.id]} events={events} info={info} fi={!!fi} title={label} onClose={() => stage.show?.(undefined)} />,
  });
  const name = (e: PitchEvent) => playerTeam(info, e.playerId)?.player.name ?? '';
  const pct = (c: [number, number]) => (c[1] ? `${Math.round((c[0] / c[1]) * 100)}%` : '–');
  const bands: [Band, string][] = [
    ['easy', `${fi ? 'Helpot' : 'Easy'} (0–${MEDIUM_PASS - 1})`],
    ['medium', `${fi ? 'Keskivaikeat' : 'Medium'} (${MEDIUM_PASS}–${HARD_PASS - 1})`],
    ['hard', `${fi ? 'Vaikeat' : 'Hard'} (${HARD_PASS}–100)`],
  ];

  const furthest = [...(running ?? [])].sort((a, b) => b.distanceKm - a.distanceKm).slice(0, 5);
  const quickest = [...(running ?? [])].sort((a, b) => b.topSpeedKmh - a.topSpeedKmh).slice(0, 5);
  const side = (teamId: string) => (teamId === info.home.teamId ? 'home' : 'away');

  return (
    <div className="pq">
      {furthest.length > 0 && (
        <section className="pq-block">
          <h3>{fi ? 'Juoksu' : 'Running'}</h3>
          <div className="pq-run">
            <ol className="pq-list">
              {furthest.map((r) => (
                <li key={r.playerId} className={`side-${side(r.teamId)}`}>
                  <span className="pq-row"><b className="num">{r.distanceKm.toFixed(1)}<small>km</small></b>
                    <span className="pq-who">{r.name}<small>{r.sprints} {fi ? 'spurttia' : 'sprints'}</small></span></span>
                </li>
              ))}
            </ol>
            <ol className="pq-list">
              {quickest.map((r) => (
                <li key={r.playerId} className={`side-${side(r.teamId)} ${r.topSpeedKmh >= 33.5 ? 'fast' : ''}`}>
                  <span className="pq-row"><b className="num">{r.topSpeedKmh.toFixed(1)}<small>km/h</small></b>
                    <span className="pq-who">{r.name}<small>{fi ? 'huippunopeus' : 'top speed'}</small></span></span>
                </li>
              ))}
            </ol>
          </div>
          <p className="fan-hint">
            {fi
              ? 'Mitattu seurantadatasta: jokaisen pelaajan sijainti kymmenen kertaa sekunnissa. Spurtti on vähintään 25,2 km/h sekunnin ajan; pelaaja merkitään kentällä, kun hän ylittää 33,5 km/h.'
              : 'Measured from tracking: every player’s position ten times a second. A sprint is 25.2 km/h or faster for a second; a player is tagged on the pitch above 33.5 km/h.'}
          </p>
        </section>
      )}

      <section className="pq-block">
        <h3>{fi ? 'Syöttöjen vaikeus' : 'Pass difficulty'}</h3>
        <table className="pq-table">
          <thead>
            <tr>
              <th scope="col">{fi ? 'Vaikeus' : 'Difficulty'}</th>
              {sides.map((t) => <th key={t.teamId} scope="col" colSpan={2}>{t.shortName}</th>)}
            </tr>
          </thead>
          <tbody>
            {bands.map(([b, label]) => (
              <tr key={b} className={`pq-${b}`}>
                <th scope="row">{label}</th>
                {sides.map((t) => {
                  const c = table[t.teamId][b];
                  return [
                    <td key={`${t.teamId}-n`} className="num">{c[0]}/{c[1]}</td>,
                    <td key={`${t.teamId}-p`} className="num pq-rate"><span style={{ width: c[1] ? `${(c[0] / c[1]) * 100}%` : 0 }} />{pct(c)}</td>,
                  ];
                })}
              </tr>
            ))}
          </tbody>
        </table>
        <p className="fan-hint">
          {fi
            ? 'Vaikeus 0–100 on moottorin oma arvio: syötön pituus, painostus, kuinka paljon se vie eteenpäin, minne se on suunnattu ja onko se nostettu pitkä pallo. Perille menneet / yritetyt.'
            : 'Difficulty 0–100 is the engine’s own rating: length, pressure on the passer, how far forward, where it was aimed, and a lofted long ball. Completed / attempted.'}
        </p>
      </section>

      <section className="pq-block">
        <h3>{fi ? 'Vaikeimmat onnistuneet syötöt' : 'Hardest passes completed'}</h3>
        <ol className="pq-list">
          {hardest.map((e) => {
            const label = `${clock(e.clockSec)} · ${name(e)} · ${Math.round(e.passMetres ?? 0)} m`;
            return (
              <li key={e.id}>
                <button type="button" onClick={() => show(e, label)}>
                  <b className="num">{e.passDifficulty}</b>
                  <span className="pq-who">{name(e)}<small>{clock(e.clockSec)} · {Math.round(e.passMetres ?? 0)} m · {bandLabel(e.passDifficulty!, fi)}</small></span>
                </button>
              </li>
            );
          })}
          {hardest.length === 0 && <li className="pq-empty">{fi ? 'Ei vielä syöttöjä.' : 'No passes yet.'}</li>}
        </ol>
      </section>

      <section className="pq-block">
        <h3>{fi ? 'Kovimmat laukaukset' : 'Fastest shots'}</h3>
        <ol className="pq-list">
          {fastest.map((e) => {
            const kmh = Math.round(e.ballSpeedKmh!);
            const label = `${clock(e.clockSec)} · ${name(e)} · ${kmh} km/h`;
            const res = fi
              ? { goal: 'maali', saved: 'torjuttu', offTarget: 'ohi', blocked: 'blokattu' }
              : { goal: 'goal', saved: 'saved', offTarget: 'off target', blocked: 'blocked' };
            return (
              <li key={e.id} className={kmh >= FAST_SHOT_KMH ? 'fast' : ''}>
                <button type="button" onClick={() => show(e, label)}>
                  <b className="num">{kmh}<small>km/h</small></b>
                  <span className="pq-who">{name(e)}<small>{clock(e.clockSec)} · {res[e.shotResult ?? 'offTarget']}</small></span>
                </button>
              </li>
            );
          })}
          {fastest.length === 0 && <li className="pq-empty">{fi ? 'Ei vielä laukauksia.' : 'No shots yet.'}</li>}
        </ol>
        <p className="fan-hint">
          {fi
            ? `Pelaaja merkitään kentällä nimellä, kun laukaus on vähintään ${FAST_SHOT_KMH} km/h tai onnistunut syöttö vähintään ${LONG_PASS_M} m.`
            : `On the pitch a player is tagged by name when a shot reaches ${FAST_SHOT_KMH} km/h or a completed pass ${LONG_PASS_M} m.`}
        </p>
      </section>
    </div>
  );
}
