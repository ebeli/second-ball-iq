import { useEffect, useState, type ReactNode } from 'react';
import { closedCards, firstSeen } from '../firstSeen';
import { Dismiss16Regular } from '@fluentui/react-icons';
import { PicksCard, PollCard } from './Party';
import { PredictionCard } from './Predictions';
import { TestCard } from './TacticalTest';
import type { Party } from '../party/useParty';
import type { MatchInfo, PitchEvent, Prediction, TacticalTest } from '../types';

/**
 * The pinned card at the top of the right panel: the newest poll, tactical test or set-piece call, with its result
 * once the match settles it. You can close it; anything new (a new poll, or the result of this one) pops up again.
 * In a watch party it is the room's vote or pick; without one, Second Ball IQ's own call and the test.
 */
export function Pinned({ party, predictions, tests, info, fi, clockSec, events, onPlayer }:
  { party?: Party; predictions: Prediction[]; tests: TacticalTest[]; info: MatchInfo; fi?: boolean; clockSec: number; events: PitchEvent[];
    onPlayer?: (id: string) => void }) {
  const st = party?.state;
  const items: { key: string; open: boolean; node: ReactNode }[] = [];
  if (st && party) {
    for (const p of st.polls) {
      const open = !p.closed || (!!p.testId && !p.outcome);
      items.push({ key: `${p.pollId}:${p.outcome ?? (p.closed ? 'closed' : 'open')}`, open, node: <PollCard p={p} party={party} fi={fi} /> });
    }
    for (const p of st.picks) items.push({ key: `${p.predictionId}:${p.outcome ?? 'open'}`, open: !p.outcome, node: <PicksCard p={p} party={party} fi={fi} info={info} /> });
  } else {
    for (const p of predictions.filter((x) => x.kind !== 'nextGoal'))
      items.push({ key: `${p.predictionId}:${p.outcome ?? 'open'}`, open: p.outcome === undefined || p.outcome === null,
        node: <PredictionCard p={p} info={info} fi={fi} onPlayer={onPlayer} /> });
    for (const t of tests) items.push({ key: `${t.testId}:${t.status}`, open: t.status === 'open', node: <TestCard test={t} clockSec={clockSec} fi={fi} compact events={events} /> });
  }

  // When each version of an item first appeared (what was already there when the panel first opened counts as old).
  const scope = `pinned:${st?.code ?? 'solo'}`;
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 5000); return () => window.clearInterval(t); }, []);
  const at = (key: string) => firstSeen(scope, key);
  const [, setClosedTick] = useState(0);
  for (const it of items) at(it.key);   // register every version as it appears, not only when first asked

  // Open items stay until closed; a result stays for 90 seconds after it arrives.
  const active = items.filter((it) => !closedCards.has(it.key) && (it.open || now - at(it.key) < 90_000));
  const top = active.sort((a, b) => at(b.key) - at(a.key))[0];
  if (!top) return null;
  return (
    <div className="pinned" key={top.key}>
      <button type="button" className="pinned-close" aria-label={fi ? 'Sulje' : 'Close'}
        onClick={() => { closedCards.add(top.key); setClosedTick((n) => n + 1); }}><Dismiss16Regular /></button>
      {top.node}
    </div>
  );
}
