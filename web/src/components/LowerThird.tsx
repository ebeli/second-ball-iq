import { useEffect, useState } from 'react';
import { clock } from '../format';
import type { InsightView, MatchInfo } from '../types';

interface Props {
  item?: InsightView;
  info: MatchInfo;
  onDone: () => void;
}

/** The broadcast graphic: an insight slides in over the pitch for its display time, then hands over to the next. */
export function LowerThird({ item, info, onDone }: Props) {
  const [leaving, setLeaving] = useState(false);

  useEffect(() => {
    if (!item) return;
    setLeaving(false);
    const ms = Math.min(item.insight.displayMs ?? 8000, 12000);
    const out = window.setTimeout(() => setLeaving(true), ms - 400);
    const done = window.setTimeout(onDone, ms);
    return () => { window.clearTimeout(out); window.clearTimeout(done); };
  }, [item, onDone]);

  if (!item) return null;
  const i = item.insight;
  const team = i.teamIds[0] === info.home.teamId ? info.home : info.away;
  const side = i.teamIds[0] === info.home.teamId ? 'home' : 'away';

  return (
    <div className={`lower-third ${leaving ? 'leaving' : ''}`} role="status" aria-live="polite">
      <div className={`lt-team lt-${side}`}>
        <span className="lt-short">{team.shortName}</span>
        <span className="lt-clock">{clock(i.matchClockSec)}</span>
      </div>
      <div className="lt-body">
        <p className="lt-headline">{i.headline}</p>
        <p className="lt-summary">{i.summary}</p>
      </div>
      {i.figures.length > 0 && (
        <dl className="lt-figures">
          {i.figures.map((f) => (
            <div key={f.label}>
              <dt>{f.label}</dt>
              <dd>{formatFigure(f.value, f.unit)}</dd>
            </div>
          ))}
        </dl>
      )}
    </div>
  );
}

export function formatFigure(value: number, unit?: string): string {
  const v = Number.isInteger(value) ? String(value) : value.toFixed(2);
  return unit ? `${v}${unit}` : v;
}
