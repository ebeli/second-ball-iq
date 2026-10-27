import { useMemo, useState } from 'react';
import { clock } from '../format';
import { playerTeam } from '../kit';
import { band, bandLabel, type Band } from '../metrics';
import { useControl } from '../tracking';
import { MoveReplay } from './FanParts';
import { Pitch } from './Pitch';
import { useStage } from './Stage';
import type { MatchInfo, PassView, PitchEvent, XtView } from '../types';

const L = 105, W = 68;

/**
 * Analysis › Space and threat, all from tracking: who controls which part of the pitch right now (pitch control),
 * how dangerous each part is (expected threat), each team's hold on the final third, the passes that missed a better
 * option (each replayable with the option drawn in), and pass difficulty with the lane checked against how often
 * passes are actually completed.
 */
export function SpacePanel({ info, events, passes, xt, fi, clockSec }:
  { info: MatchInfo; events: PitchEvent[]; passes: Record<string, PassView>; xt?: XtView; fi?: boolean; clockSec: number }) {
  const stage = useStage();
  const control = useControl();
  const [map, setMap] = useState<'control' | 'threat'>('control');

  // Each team's share of its attacking final third, from the live control map.
  const shares = useMemo(() => {
    if (!control) return undefined;
    let home = 0, away = 0, nh = 0, na = 0;
    for (let c = 0; c < control.cols; c++) {
      const pct = ((c + 0.5) / control.cols) * 100;
      for (let r = 0; r < control.rows; r++) {
        const v = control.home[r * control.cols + c];
        if (pct >= 200 / 3) { home += v; nh++; }
        if (pct <= 100 / 3) { away += 1 - v; na++; }
      }
    }
    return { home: nh ? home / nh : 0, away: na ? away / na : 0 };
  }, [control]);

  const byId = useMemo(() => new Map(events.map((e) => [e.id, e])), [events]);
  const missed = useMemo(() => Object.values(passes)
    .filter((p) => p.betterTo && byId.has(p.eventId))
    .sort((a, b) => byId.get(b.eventId)!.seq - byId.get(a.eventId)!.seq)
    .slice(0, 6), [passes, byId]);

  // Completion by difficulty with the lane, against what happened: the live check of the rating.
  const table = useMemo(() => {
    const out: Record<Band, [number, number]> = { easy: [0, 0], medium: [0, 0], hard: [0, 0] };
    for (const p of Object.values(passes)) {
      const e = byId.get(p.eventId);
      if (!e) continue;
      const cell = out[band(p.laneDifficulty)];
      cell[1]++;
      if (e.outcome === 'success') cell[0]++;
    }
    return out;
  }, [passes, byId]);

  const name = (id?: string) => (id ? playerTeam(info, id)?.player.name ?? '' : '');
  const show = (e: PitchEvent, label: string) => stage.show?.({
    key: `sp-${e.id}`, label,
    node: <MoveReplay ids={[e.id]} events={events} info={info} fi={!!fi} title={label} onClose={() => stage.show?.(undefined)} />,
  });
  const pct = (v: number) => `${Math.round(v * 100)}%`;
  const judged = Object.keys(passes).length;

  return (
    <div className="pq space">
      <section className="pq-block">
        <div className="space-head">
          <h3>{map === 'control' ? (fi ? 'Kenttähallinta' : 'Pitch control') : (fi ? 'Odotettu uhka (xT)' : 'Expected threat (xT)')}</h3>
          <div className="an-tabs small" role="tablist">
            <button type="button" role="tab" aria-selected={map === 'control'} className={map === 'control' ? 'on' : ''} onClick={() => setMap('control')}>{fi ? 'Hallinta' : 'Control'}</button>
            <button type="button" role="tab" aria-selected={map === 'threat'} className={map === 'threat' ? 'on' : ''} onClick={() => setMap('threat')}>xT</button>
          </div>
        </div>
        <div className="space-board">
          <Pitch events={events} clockSec={clockSec} info={info} compact label={fi ? 'Kenttähallinta' : 'Pitch control'}>
            {map === 'control' && control && <ControlLayer grid={control.home} cols={control.cols} rows={control.rows} />}
            {map === 'threat' && xt && <ThreatLayer xt={xt} />}
            {map === 'control' && !control && <text x={L / 2} y={W / 2} textAnchor="middle" className="space-wait">{fi ? 'Odotetaan seurantadataa…' : 'Waiting for tracking…'}</text>}
          </Pitch>
        </div>
        {map === 'control' && shares && (
          <div className="space-shares">
            <span className="side-home"><b className="num">{pct(shares.home)}</b> {info.home.shortName} {fi ? 'hyökkäyskolmanneksesta' : 'of their final third'}</span>
            <span className="side-away"><b className="num">{pct(shares.away)}</b> {info.away.shortName} {fi ? 'hyökkäyskolmanneksesta' : 'of their final third'}</span>
          </div>
        )}
        <p className="fan-hint">
          {map === 'control'
            ? (fi ? 'Jokaiselle kentän kohdalle: kumpi joukkue ehtisi sinne ensin, kun jokainen pelaaja jatkaa hetken suuntaansa ja juoksee sitten täyttä vauhtia. Kotijoukkue hyökkää oikealle.'
                  : 'For every spot on the pitch: which team would get there first, with every player carrying on the way he is going for a moment, then running flat out. Home attack to the right.')
            : (fi ? `Kuinka todennäköinen maali on muutaman siirron sisällä, kun pallo on kussakin kohdassa (hyökkäyssuunta oikealle). Sovitettu ${xt?.fittedOn ?? 'simuloituihin otteluihin'}, ei lainattu todellisista arvoista.`
                  : `How likely a goal is within the next few moves with the ball in each spot (attacking to the right). Fitted on ${xt?.fittedOn ?? 'simulated matches'}, not borrowed from real-football values.`)}
        </p>
      </section>

      <section className="pq-block">
        <h3>{fi ? 'Ohitetut paremmat syötöt' : 'Better passes missed'}</h3>
        <ol className="pq-list">
          {missed.map((p) => {
            const e = byId.get(p.eventId)!;
            const label = `${clock(e.clockSec)} · ${name(e.playerId)} → ${name(p.betterTo)}`;
            return (
              <li key={p.eventId} className={`side-${playerTeam(info, e.playerId)?.side ?? 'home'}`}>
                <button type="button" onClick={() => show(e, label)}>
                  <b className="num">+{(p.betterGain ?? 0).toFixed(2)}<small>xT</small></b>
                  <span className="pq-who">{name(e.playerId)}<small>{clock(e.clockSec)} · {fi ? 'parempi' : 'better'}: {name(p.betterTo)} · {Math.round((p.betterCompletion ?? 0) * 100)}% {fi ? 'perille' : 'to arrive'}</small></span>
                </button>
              </li>
            );
          })}
          {missed.length === 0 && <li className="pq-empty">{fi ? 'Ei vielä ohitettuja parempia vaihtoehtoja.' : 'No better option missed yet.'}</li>}
        </ol>
        <p className="fan-hint">
          {fi ? 'Jokaisen syötön hetkellä: jokaiselle joukkuetoverille syötön todennäköisyys mennä perille kertaa odotettu uhka siellä. Parempi vaihtoehto tuo vähintään 0,02 xT enemmän ja menee perille vähintään 55 % ajasta. Paitsiossa olevat eivät ole vaihtoehtoja.'
              : 'At the moment of each pass: for every teammate, the chance the pass would arrive times the expected threat where it would arrive. A better option adds at least 0.02 xT and arrives at least 55% of the time. Teammates in an offside position are not options.'}
        </p>
      </section>

      <section className="pq-block">
        <h3>{fi ? 'Vaikeus kaistan kanssa' : 'Difficulty with the passing lane'}</h3>
        <table className="pq-table">
          <thead><tr><th scope="col">{fi ? 'Vaikeus' : 'Difficulty'}</th><th scope="col">{fi ? 'Perille / yritetty' : 'Completed / tried'}</th><th scope="col">%</th></tr></thead>
          <tbody>
            {(['easy', 'medium', 'hard'] as Band[]).map((b) => {
              const c = table[b];
              return (
                <tr key={b} className={`pq-${b}`}>
                  <th scope="row">{bandLabel(b === 'easy' ? 0 : b === 'medium' ? 40 : 70, fi)}</th>
                  <td className="num">{c[0]}/{c[1]}</td>
                  <td className="num pq-rate"><span style={{ width: c[1] ? `${(c[0] / c[1]) * 100}%` : 0 }} />{c[1] ? pct(c[0] / c[1]) : '–'}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <p className="fan-hint">
          {fi ? `Seurantadatasta syötön hetkellä: vastustajat, jotka ehtisivät katkaista syötön (10 pistettä kukin), ja vartioitu vastaanottaja (10), pituuden, painostuksen ja suunnan lisäksi. ${judged} syöttöä arvioitu; mitä vaikeampi, sitä harvemmin perille.`
              : `From tracking at the moment of the pass: opponents who could cut it out (10 points each) and a marked target (10), on top of length, pressure and direction. ${judged} passes judged; the harder they are, the less often they arrive.`}
        </p>
      </section>
    </div>
  );
}

/** The home team's control per cell, in each team's colour, stronger where one side clearly owns the space. */
export function ControlLayer({ grid, cols, rows }: { grid: number[]; cols: number; rows: number }) {
  const cw = L / cols, ch = W / rows;
  return (
    <g className="control-layer">
      {grid.map((v, i) => {
        const c = i % cols, r = Math.floor(i / cols);
        const strength = Math.min(1, Math.abs(v - 0.5) * 2);
        return <rect key={i} x={c * cw} y={r * ch} width={cw + 0.05} height={ch + 0.05}
          fill={v >= 0.5 ? 'var(--home)' : 'var(--away)'} opacity={0.12 + 0.5 * strength} />;
      })}
    </g>
  );
}

/** The xT grid, brighter where the ball is more dangerous (attacking to the right). */
function ThreatLayer({ xt }: { xt: XtView }) {
  const max = Math.max(...xt.values);
  const cw = L / xt.cols, ch = W / xt.rows;
  return (
    <g className="threat-layer">
      {xt.values.map((v, i) => {
        const c = i % xt.cols, r = Math.floor(i / xt.cols);
        const u = Math.sqrt(v / max);
        return (
          <g key={i}>
            <rect x={c * cw} y={r * ch} width={cw + 0.05} height={ch + 0.05} fill="var(--on-air)" opacity={0.06 + 0.7 * u} />
            {r === Math.floor(xt.rows / 2) && <text x={c * cw + cw / 2} y={r * ch + ch / 2 + 0.8} textAnchor="middle" className="threat-num">{v.toFixed(2).replace(/^0/, '')}</text>}
          </g>
        );
      })}
    </g>
  );
}
