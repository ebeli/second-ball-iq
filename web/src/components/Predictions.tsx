import { useEffect, useState } from 'react';
import { clock } from '../format';
import { useArrivals } from '../hooks/useArrivals';
import type { MatchInfo, Prediction, PredictionKind } from '../types';

const kindLabel: Record<PredictionKind, [string, string]> = {
  corner: ['Corner', 'Kulmapotku'],
  freeKick: ['Free kick', 'Vapaapotku'],
  penalty: ['Penalty', 'Rangaistuspotku'],
  nextGoal: ['Next goal', 'Seuraava maali'],
};

interface CardProps {
  p: Prediction;
  info: MatchInfo;
  fi?: boolean;
  onPlayer?: (playerId: string) => void;
  /** On the pitch: no evidence text, larger diagram. */
  overlay?: boolean;
}

/** One prediction: the question, the odds drawn where the ball would go, and once it is taken, whether we were right. */
export function PredictionCard({ p, info, fi, onPlayer, overlay }: CardProps) {
  const side = p.teamId === info.home.teamId ? 'home' : p.teamId === info.away.teamId ? 'away' : 'none';
  const team = side === 'home' ? info.home : side === 'away' ? info.away : undefined;
  const settled = p.outcome !== undefined && p.outcome !== null;
  const pick = p.options.find((o) => o.id === p.pick);
  const actual = p.options.find((o) => o.id === p.outcome);
  const label = (o: { label: string; labelFi: string }) => (fi ? o.labelFi : o.label);
  const sorted = [...p.options].sort((a, b) => b.probability - a.probability);

  return (
    <article className={`pred side-${side} ${overlay ? 'pred-overlay' : ''} ${settled ? (p.correct ? 'pred-right' : 'pred-wrong') : 'pred-open'}`}>
      <header className="pred-head">
        <span className="pred-kind">{kindLabel[p.kind][fi ? 1 : 0]}</span>
        {team && <span className={`chip chip-${side}`}>{team.shortName}</span>}
        <span className="pred-clock num">{clock(p.clockSec)}</span>
        <span className={`pred-source ${p.source === 'Decision-1' ? 'src-model' : ''}`} title={fi ? 'Kuka ennusti' : 'Who made the call'}>
          {p.source}
        </span>
      </header>
      {p.playerId && p.playerName && (
        <p className="pred-taker">
          {onPlayer
            ? <button type="button" className="linkish" onClick={() => onPlayer(p.playerId!)}>{p.playerName}</button>
            : p.playerName}
          <span className="muted"> {fi ? 'potkaisijana' : 'to take'}</span>
        </p>
      )}
      <p className="pred-question">{fi ? p.questionFi : p.question}</p>

      <div className="pred-body">
        <Diagram p={p} fi={fi} />
        <ol className="pred-bars">
          {sorted.map((o) => (
            <li key={o.id} className={`${o.id === p.pick ? 'is-pick' : ''} ${o.id === p.outcome ? 'is-outcome' : ''}`}>
              <span className="pb-label">{label(o)}</span>
              <span className="pb-track"><span className="pb-fill" style={{ width: `${Math.round(o.probability * 100)}%` }} /></span>
              <span className="pb-pct num">{Math.round(o.probability * 100)}%</span>
            </li>
          ))}
        </ol>
      </div>

      <p className="pred-verdict" role="status">
        {!settled && (p.kind === 'nextGoal'
          ? (fi ? `Ennuste: ${pick ? label(pick) : ''}. Ratkeaa ${clock(p.settleAtSec ?? p.clockSec)} mennessä.` : `Our call: ${pick ? label(pick) : ''}. Settled by ${clock(p.settleAtSec ?? p.clockSec)}.`)
          : (fi ? `Ennuste: ${pick ? label(pick) : ''}. Odotetaan potkua…` : `Our call: ${pick ? label(pick) : ''}. Waiting for the kick…`))}
        {settled && p.correct && <><Tick /> {fi ? `Osui! ${actual ? label(actual) : p.outcome}.` : `Called it! ${actual ? label(actual) : p.outcome}.`}</>}
        {settled && !p.correct && <><Cross /> {fi ? `Meni toisin: ${actual ? label(actual) : p.outcome}.` : `Not this time: ${actual ? label(actual) : p.outcome}.`}</>}
      </p>
      {!overlay && <p className="pred-basis">{fi ? p.basisFi : p.basis}</p>}
    </article>
  );
}

/** Where the ball would go. Corners: a top-down goal mouth. Penalties and free kicks: the goal as the shooter sees it. */
function Diagram({ p, fi }: { p: Prediction; fi?: boolean }) {
  const prob = (id: string) => p.options.find((o) => o.id === id)?.probability ?? 0;
  const fill = (id: string) => `rgba(245, 197, 24, ${0.1 + prob(id) * 0.85})`;
  const ring = (id: string) => (id === p.pick ? 'var(--chalk)' : 'none');

  if (p.kind === 'corner') {
    // Goal line along the top; the taker is in the top-left corner.
    const zones: { id: string; x: number; y: number; w: number; h: number }[] = [
      { id: 'short', x: 2, y: 2, w: 20, h: 22 },
      { id: 'near', x: 30, y: 4, w: 24, h: 26 },
      { id: 'central', x: 54, y: 4, w: 24, h: 26 },
      { id: 'far', x: 78, y: 4, w: 24, h: 26 },
    ];
    return (
      <svg className="pred-diagram" viewBox="0 0 110 60" role="img" aria-label={fi ? 'Kulmapotkun alueet' : 'Corner delivery zones'}>
        <rect x="0" y="0" width="110" height="60" fill="var(--board)" />
        <line x1="0" y1="2" x2="110" y2="2" stroke="var(--chalk-line)" strokeWidth="0.6" />
        <rect x="26" y="2" width="80" height="44" fill="none" stroke="var(--chalk-line)" strokeWidth="0.6" />
        <rect x="48" y="2" width="36" height="14" fill="none" stroke="var(--chalk-line)" strokeWidth="0.6" />
        <rect x="58" y="0" width="16" height="2" fill="var(--chalk)" />
        {zones.map((z) => (
          <rect key={z.id} x={z.x} y={z.y} width={z.w} height={z.h} rx="2" fill={fill(z.id)} stroke={ring(z.id)} strokeWidth="0.9" />
        ))}
        {zones.map((z) => (
          <text key={`t-${z.id}`} x={z.x + z.w / 2} y={z.y + z.h / 2 + 2} className="pd-pct" textAnchor="middle">
            {Math.round(prob(z.id) * 100)}%
          </text>
        ))}
        <circle cx="2" cy="2" r="1.8" fill="var(--chalk)" />
        {p.outcome && zones.find((z) => z.id === p.outcome) && (() => {
          const z = zones.find((x) => x.id === p.outcome)!;
          return <Ball x={z.x + z.w / 2} y={z.y + z.h - 5} />;
        })()}
        <text x="2" y="57" className="pd-note">{fi ? 'Potkaisija' : 'Taker'} ↖</text>
      </svg>
    );
  }

  if (p.kind === 'nextGoal') return null;

  // Goal frame from the shooter's point of view: left, centre, right.
  const cols = [{ id: 'left', x: 6 }, { id: 'centre', x: 39.33 }, { id: 'right', x: 72.67 }];
  return (
    <svg className="pred-diagram" viewBox="0 0 110 50" role="img" aria-label={fi ? 'Maalin alueet laukojan näkökulmasta' : "Goal zones, from the shooter's view"}>
      <rect x="0" y="0" width="110" height="50" fill="var(--board)" />
      {cols.map((c) => (
        <rect key={c.id} x={c.x} y="8" width="31.33" height="34" fill={fill(c.id)} stroke={ring(c.id)} strokeWidth="0.9" />
      ))}
      <path d="M6 42 V8 H104 V42" fill="none" stroke="var(--chalk)" strokeWidth="1.6" />
      <line x1="0" y1="42" x2="110" y2="42" stroke="var(--chalk-line)" strokeWidth="0.6" />
      {cols.map((c) => (
        <text key={`t-${c.id}`} x={c.x + 15.67} y="28" className="pd-pct" textAnchor="middle">{Math.round(prob(c.id) * 100)}%</text>
      ))}
      {p.outcome && cols.find((c) => c.id === p.outcome) && <Ball x={cols.find((c) => c.id === p.outcome)!.x + 15.67} y={36} />}
      <text x="55" y="48.5" className="pd-note" textAnchor="middle">{fi ? 'laukojan näkökulmasta' : "from the shooter's view"}</text>
    </svg>
  );
}

function Ball({ x, y }: { x: number; y: number }) {
  return (
    <g className="pd-ball" transform={`translate(${x} ${y})`}>
      <circle r="3.2" fill="var(--chalk)" stroke="#16212b" strokeWidth="0.6" />
      <path d="M-1.2 -1 L0 -1.8 L1.2 -1 L0.8 0.5 L-0.8 0.5 Z" fill="#16212b" />
    </g>
  );
}

/** How often the calls have been right this match. */
export function TrackRecord({ predictions, fi }: { predictions: Prediction[]; fi?: boolean }) {
  const settled = predictions.filter((p) => p.correct !== undefined && p.correct !== null);
  const right = settled.filter((p) => p.correct).length;
  if (predictions.length === 0) return null;
  return (
    <p className="track">
      <span className="track-big num">{right}/{settled.length}</span>
      <span>{fi ? 'ennustetta osui' : 'calls right so far'}</span>
      <span className="track-dots" aria-hidden="true">
        {settled.map((p) => <span key={p.predictionId} className={p.correct ? 'dot-right' : 'dot-wrong'} />)}
        {predictions.filter((p) => p.outcome === undefined || p.outcome === null).map((p) => <span key={p.predictionId} className="dot-open" />)}
      </span>
    </p>
  );
}

/**
 * The prediction on the pitch: a set piece's call appears as soon as it is made and stays until the kick is taken
 * and a few seconds after; a "next goal" call shows briefly when made and again when it settles.
 */
export function PredictionOverlay({ predictions, info, fi, matchKey, onPlayer }:
  { predictions: Prediction[]; info: MatchInfo; fi?: boolean; matchKey: string; onPlayer?: (id: string) => void }) {
  const [shownId, setShownId] = useState<string>();
  const [until, setUntil] = useState(0);

  useArrivals(predictions, (p) => `${p.predictionId}/${p.outcome ?? ''}`, matchKey, (p) => {
    const settled = p.outcome !== undefined && p.outcome !== null;
    // An open set-piece call waits for the kick (the server holds the replay for it); a safety cap of 25 s.
    const ms = settled ? 6000 : p.kind === 'nextGoal' ? 9000 : 25000;
    setShownId(p.predictionId);
    setUntil(Date.now() + ms);
  });

  useEffect(() => {
    if (!shownId) return;
    const t = window.setTimeout(() => setShownId(undefined), Math.max(0, until - Date.now()));
    return () => window.clearTimeout(t);
  }, [shownId, until]);

  const p = predictions.find((x) => x.predictionId === shownId);
  if (!p) return null;
  return (
    <div className="pred-float" aria-live="polite">
      <PredictionCard p={p} info={info} fi={fi} overlay onPlayer={onPlayer} />
    </div>
  );
}

/** Every prediction this match, newest first, under the track record. */
export function PredictionList({ predictions, info, fi, onPlayer }:
  { predictions: Prediction[]; info: MatchInfo; fi?: boolean; onPlayer?: (id: string) => void }) {
  const list = [...predictions].sort((a, b) => b.clockSec - a.clockSec);
  return (
    <div className="pred-list">
      <TrackRecord predictions={predictions} fi={fi} />
      {list.length === 0 && (
        <p className="empty">
          {fi
            ? 'Ennen kulmapotkuja, vapaapotkuja ja rangaistuspotkuja Second Ball IQ ennustaa potkaisijan historian perusteella, minne pallo menee. Ennusteet näkyvät tässä.'
            : 'Before corners, free kicks and penalties Second Ball IQ predicts where the ball goes, from the taker’s history. Every call and how it turned out shows up here.'}
        </p>
      )}
      <div className="pred-grid">
        {list.map((p) => <PredictionCard key={p.predictionId} p={p} info={info} fi={fi} onPlayer={onPlayer} />)}
      </div>
    </div>
  );
}

function Tick() {
  return <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M2.5 8.5l3.5 3.5 7.5-8" fill="none" stroke="currentColor" strokeWidth="2.2" /></svg>;
}
function Cross() {
  return <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true"><path d="M3.5 3.5l9 9M12.5 3.5l-9 9" fill="none" stroke="currentColor" strokeWidth="2.2" /></svg>;
}
