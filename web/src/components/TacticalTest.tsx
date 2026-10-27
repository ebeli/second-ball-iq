import { Button } from '@fluentui/react-components';
import { LockClosed16Regular, Play24Regular } from '@fluentui/react-icons';
import { clock } from '../format';
import type { PitchEvent, TacticalTest, TestStatus } from '../types';

/**
 * Tactical tests in the UI. A read that went to air is put to a measurable test: the criterion, rule and deadline were
 * locked when it aired, and the match settles it. Three things stay apart on screen: the criterion (fixed), the
 * result (from the events), and Decision-1's estimate (a model estimate, not a calibrated probability).
 *
 * The test is drawn as a match line: from the lock to the deadline, with the events that count placed on it as they
 * happen and a marker for now. When the match settles it, the line completes and its end fills.
 */

export const testFor = (tests: TacticalTest[] | undefined, candidateId: string | undefined) =>
  candidateId ? tests?.find((t) => t.candidateId === candidateId) : undefined;

const STATUS: Record<TestStatus, { en: string; fi: string }> = {
  open: { en: 'Testing', fi: 'Testissä' },
  met: { en: 'Met', fi: 'Täyttyi' },
  notMet: { en: 'Not met', fi: 'Ei täyttynyt' },
  inconclusive: { en: 'Inconclusive', fi: 'Ratkaisematon' },
};

export function statusText(status: TestStatus, fi?: boolean) {
  return fi ? STATUS[status].fi : STATUS[status].en;
}

/** A small result chip for a moment row: an LED diode and the state. */
export function TestChip({ test, fi }: { test: TacticalTest; fi?: boolean }) {
  const text = test.status === 'open'
    ? `${fi ? 'Testi' : 'Test'} ${clock(test.deadlineSec)}`
    : `${statusText(test.status, fi)}${test.settledAtSec !== undefined && test.status !== 'inconclusive' ? ` ${clock(test.settledAtSec)}` : ''}`;
  return <span className={`test-chip test-${test.status}`}><i aria-hidden="true" />{text}</span>;
}

/** The match line: lock → deadline, events on it, now, and the result at its end. */
function MatchLine({ test, clockSec, events }: { test: TacticalTest; clockSec: number; events?: PitchEvent[] }) {
  const W = 520, x0 = 10, x1 = W - 12, y = 20;
  const span = Math.max(1, test.deadlineSec - test.opensAtSec);
  const at = (sec: number) => x0 + ((Math.min(Math.max(sec, test.opensAtSec), test.deadlineSec) - test.opensAtSec) / span) * (x1 - x0);
  const open = test.status === 'open';
  const end = open ? Math.min(clockSec, test.deadlineSec) : test.settledAtSec ?? test.deadlineSec;
  const marks = (test.eventIds ?? [])
    .map((id) => events?.find((e) => e.id === id))
    .filter((e): e is PitchEvent => !!e)
    .map((e) => ({ id: e.id, x: at(e.clockSec), shot: e.type === 'shot' || e.type === 'goal' }));
  const minutes = Math.round(span / 60);
  return (
    <svg className="ml-line" viewBox={`0 0 ${W} 40`} preserveAspectRatio="none" role="img"
      aria-label={`${clock(test.opensAtSec)} to ${clock(test.deadlineSec)}`}>
      <line x1={x0} y1={y} x2={x1} y2={y} className="ml-track" />
      {Array.from({ length: minutes + 1 }, (_, i) => (
        <line key={i} x1={x0 + (i / minutes) * (x1 - x0)} x2={x0 + (i / minutes) * (x1 - x0)} y1={y - 5} y2={y + 5} className="ml-tick" />
      ))}
      <line x1={x0} y1={y} x2={at(end)} y2={y} className="ml-run" />
      <circle cx={x0} cy={y} r={6} className="ml-start" />
      {marks.map((m) => <circle key={m.id} cx={m.x} cy={y} r={m.shot ? 6.5 : 4.5} className={m.shot ? 'ml-shot' : 'ml-event'} />)}
      {open && <line x1={at(end)} x2={at(end)} y1={y - 13} y2={y + 13} className="ml-now" />}
      {open
        ? <circle cx={x1} cy={y} r={7} className="ml-goalpost" />
        : (
          <g className="ml-end">
            <circle cx={at(end)} cy={y} r={13} className="ml-halo" />
            <circle cx={at(end)} cy={y} r={7.5} className="ml-result" />
          </g>
        )}
    </svg>
  );
}

/**
 * The test itself. Compact: the criterion, the match line and the state (key moment, lists). Full: also the read it
 * tests, what the result can and cannot show, and the events that decided it (drawer).
 */
export function TestCard({ test, clockSec, fi, compact, events, onEvents }:
  { test: TacticalTest; clockSec: number; fi?: boolean; compact?: boolean; events?: PitchEvent[]; onEvents?: (ids: string[]) => void }) {
  const open = test.status === 'open';
  // Shape tests are read once, at the deadline; until then there is no running count to show.
  const later = !test.progressText || test.progressText.startsWith('Measured at');
  const progress = later && open ? (fi ? 'Muoto luetaan takarajalla' : 'Shape read at the deadline') : fi ? test.progressTextFi : test.progressText;
  const left = Math.max(0, test.deadlineSec - clockSec);
  return (
    <section className={`tactical-test test-${test.status} ${compact ? 'compact' : ''}`} aria-label={fi ? 'Taktinen testi' : 'Tactical test'}>
      <header className="tt-head">
        <span className="tt-title"><LockClosed16Regular aria-hidden="true" />{fi ? 'Taktinen testi' : 'Tactical test'}</span>
        <span className="tt-deadline">
          {open
            ? <>{fi ? 'Ratkeaa' : 'Decides by'} <b className="num">{clock(test.deadlineSec)}</b></>
            : <>{fi ? 'Lukittu' : 'Locked'} <b className="num">{clock(test.opensAtSec)}</b></>}
        </span>
      </header>
      {!compact && <p className="tt-read">“{test.read}”</p>}
      <p className="tt-criterion">{fi ? test.criterionFi : test.criterion}</p>
      <MatchLine test={test} clockSec={clockSec} events={events} />
      <div className="tt-scale num">
        <span>{clock(test.opensAtSec)} {fi ? 'lukittu' : 'locked'}</span>
        {open && progress && <span className="tt-progress">{progress}</span>}
        <span>{clock(test.deadlineSec)}</span>
      </div>
      {!open && (
        <p className="tt-result">
          <span className="tt-verdict">{statusText(test.status, fi)}{test.settledAtSec !== undefined && test.status !== 'inconclusive' ? ` ${fi ? 'klo' : 'at'} ${clock(test.settledAtSec)}` : ''}</span>
          <span className="tt-result-text">{progress}</span>
        </p>
      )}
      <div className="tt-tiles">
        <div className="tt-tile">
          {test.estimate !== undefined && test.estimate !== null
            ? <><b className="num">{Math.round(test.estimate * 100)}%</b><span>{fi ? 'Decision-1:n mallin arvio, ei kalibroitu' : 'Decision-1 model estimate, not calibrated'}</span></>
            : <><b className="num">–</b><span>{fi ? 'Ei mallin arviota' : 'No model estimate'}</span></>}
        </div>
        <div className="tt-tile">
          {open
            ? <><b className="num">{Math.floor(left / 60)}:{String(left % 60).padStart(2, '0')}</b><span>{fi ? 'Aikaa jäljellä' : 'Match time left'}</span></>
            : <><b className="num">{(test.eventIds ?? []).length}</b><span>{fi ? 'Ratkaisseet tilanteet' : 'Deciding events'}</span></>}
        </div>
      </div>
      {!compact && (
        <>
          <p className="tt-limits">{fi ? test.limitsFi : test.limits}</p>
          {onEvents && test.eventIds.length > 0 && (
            <Button size="small" appearance="outline" icon={<Play24Regular />} onClick={() => onEvents(test.eventIds)}>
              {fi ? 'Näytä ratkaisseet tilanteet' : 'Show the deciding events'}
            </Button>
          )}
        </>
      )}
    </section>
  );
}

/**
 * The public record: every settled test, and how often Decision-1's estimate was on the right side of 50%. A record is
 * a count, not a calibration; with a handful of tests it shows honesty, not accuracy.
 */
export function TestRecord({ tests, fi }: { tests: TacticalTest[]; fi?: boolean }) {
  if (tests.length === 0) return null;
  const n = (s: TestStatus) => tests.filter((t) => t.status === s).length;
  const decided = tests.filter((t) => (t.status === 'met' || t.status === 'notMet') && t.estimate !== undefined && t.estimate !== null);
  const right = decided.filter((t) => (t.estimate! >= 0.5) === (t.status === 'met')).length;
  const cells: [string, number, string][] = [
    ['met', n('met'), fi ? 'Täyttyi' : 'Met'],
    ['notMet', n('notMet'), fi ? 'Ei täyttynyt' : 'Not met'],
    ['inconclusive', n('inconclusive'), fi ? 'Ratkaisematon' : 'Inconclusive'],
    ['open', n('open'), fi ? 'Käynnissä' : 'Open'],
  ];
  return (
    <div className="test-record">
      {cells.map(([k, v, label]) => (
        <div key={k} className={`tr-cell tr-${k}`}><b className="num">{v}</b><span>{label}</span></div>
      ))}
      <div className="tr-cell tr-estimate">
        <b className="num">{decided.length ? `${right}/${decided.length}` : '–'}</b>
        <span>{fi ? 'Decision-1 oikealla puolella 50 %:a' : 'Decision-1 on the right side of 50%'}</span>
      </div>
      <div className="tr-dots" aria-hidden="true">
        {[...tests].sort((a, b) => a.opensAtSec - b.opensAtSec).map((t) => <span key={t.testId} className={`dot-${t.status}`} />)}
      </div>
    </div>
  );
}
