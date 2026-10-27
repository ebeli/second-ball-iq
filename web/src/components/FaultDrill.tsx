import { useState } from 'react';
import { Badge, Button } from '@fluentui/react-components';
import { api } from '../api';
import { kindLabel } from '../format';
import type { FaultDrillView } from '../types';

/**
 * Fault drill, in the studio's Evaluation view: plant an error in the next moment's Analyst draft and watch the checks
 * catch it. A wrong number is the Validator's job (code); an unsupported claim is Decision-1 grounding's. A planted
 * draft is never published: if nothing catches it, Publish drops it and the drill is recorded as missed.
 */
export function FaultDrill({ drills, armed, onTrace }: { drills: FaultDrillView[]; armed?: string; onTrace: (candidateId: string) => void }) {
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const plant = async (kind: 'number' | 'claim') => {
    setBusy(true);
    setError(undefined);
    try { await api.plant(kind); } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  };
  const caught = drills.filter((d) => d.caughtBy).length;
  return (
    <section className="fault-drill" aria-label="Fault drill">
      <header className="fd-head">
        <h3>Fault drill</h3>
        {drills.length > 0 && (
          <Badge appearance="tint" color={caught === drills.length ? 'success' : 'danger'}>{caught}/{drills.length} caught</Badge>
        )}
      </header>
      <p className="muted">
        Plant an error in the next moment&apos;s Analyst draft and follow it through the agent trace. A wrong number is
        the Validator&apos;s job; an unsupported claim needs Decision-1 grounding, so it is only caught in a live match.
        Planted drafts are never published.
      </p>
      <div className="fd-actions">
        <Button disabled={busy || !!armed} onClick={() => void plant('number')}>Plant a wrong number</Button>
        <Button disabled={busy || !!armed} onClick={() => void plant('claim')}>Plant an unsupported claim</Button>
        {armed && <Badge appearance="outline" color="warning">Armed: {armed === 'number' ? 'wrong number' : 'unsupported claim'} in the next moment</Badge>}
      </div>
      {error && <p className="tone-bad">{error}</p>}
      {drills.length > 0 && (
        <ol className="fd-list">
          {[...drills].reverse().map((d) => (
            <li key={d.candidateId + d.at} className={d.caughtBy ? 'fd-caught' : 'fd-missed'}>
              <span className="num">{d.clock}</span>
              <span>{kindLabel[d.kind] ?? d.kind}</span>
              <span className="fd-planted">“{d.planted}”</span>
              <strong>{d.caughtBy ? `Caught by ${d.caughtBy}` : 'Missed: dropped at publish'}</strong>
              <button type="button" className="link" onClick={() => onTrace(d.candidateId)}>Trace</button>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
