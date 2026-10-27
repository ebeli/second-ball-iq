import { useEffect, useState } from 'react';
import { clock } from '../format';
import { useArrivals } from '../hooks/useArrivals';
import type { Call, MatchInfo } from '../types';

/** The commentator's line on the board: the latest call large, the two before it fading underneath. */
export function CallTicker({ calls, fi, matchKey }: { calls: Call[]; fi?: boolean; matchKey: string }) {
  const [fresh, setFresh] = useState<string>();
  useArrivals(calls, (c) => c.callId, matchKey, (c) => setFresh(c.callId));
  const last = calls.slice(-3).reverse();
  if (last.length === 0) return null;
  return (
    <ol className="ticker" aria-live="polite" aria-label={fi ? 'Selostaja' : 'Commentator'}>
      {last.map((c, i) => (
        <li key={c.callId} className={`tk tk-${c.kind} tk-i${c.intensity} ${i === 0 ? 'tk-now' : ''} ${c.callId === fresh ? 'tk-fresh' : ''}`}>
          <span className="tk-clock num">{clock(c.clockSec)}</span>
          <span className="tk-text">{fi ? c.textFi : c.text}</span>
        </li>
      ))}
    </ol>
  );
}

/** A goal takes over the board for a few seconds: GOAL, the scorer's team colour, the new score. */
export function GoalBurst({ calls, info, fi, matchKey }: { calls: Call[]; info: MatchInfo; fi?: boolean; matchKey: string }) {
  const [goal, setGoal] = useState<Call>();
  useArrivals(calls, (c) => c.callId, matchKey, (c) => { if (c.kind === 'goal') setGoal(c); });
  useEffect(() => {
    if (!goal) return;
    const t = window.setTimeout(() => setGoal(undefined), 4200);
    return () => window.clearTimeout(t);
  }, [goal]);
  if (!goal) return null;
  const side = goal.teamId === info.home.teamId ? 'home' : 'away';
  const team = side === 'home' ? info.home : info.away;
  // The call's text ends with the score line; the first sentence names the scorer.
  const text = fi ? goal.textFi : goal.text;
  return (
    <div className={`goal-burst gb-${side}`} role="alert">
      <span className="gb-word">{fi ? 'MAALI!' : 'GOAL!'}</span>
      <span className="gb-team">{team.name}</span>
      <span className="gb-line">{text.replace(/^(GOAL!|MAALI!)\s*/, '')}</span>
    </div>
  );
}
