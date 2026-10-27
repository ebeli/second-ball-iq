import { clock } from '../format';
import type { Call, MatchInfo, TacticalTest } from '../types';
import { statusText } from './TacticalTest';

/**
 * The perimeter board: an LED ribbon like the boards around a real pitch. It runs the commentator's latest calls and
 * the tactical tests (open ones with their deadline, settled ones with the result). The text scrolls; with reduced
 * motion it stands still and shows the newest items.
 */
/** Shape tests are only measured at their deadline; until then their progress text says just that. */
const measuredLater = (t: TacticalTest) => !t.progressText || t.progressText.startsWith('Measured at');

export function LedTicker({ calls, tests, info, fi, clockSec }:
  { calls: Call[]; tests: TacticalTest[]; info?: MatchInfo; fi?: boolean; clockSec?: number }) {
  const items: { key: string; at: number; tone: string; label: string; text: string }[] = [];
  for (const c of calls.slice(-6)) {
    items.push({ key: c.callId, at: c.clockSec, tone: c.kind === 'goal' ? 'goal' : 'call', label: clock(c.clockSec), text: fi ? c.textFi : c.text });
  }
  for (const t of tests) {
    const team = info ? (t.teamId === info.home.teamId ? info.home.shortName : info.away.shortName) : '';
    if (t.status === 'open') {
      items.push({ key: t.testId, at: t.opensAtSec, tone: 'test', label: `${fi ? 'Testi' : 'Test'} ${team}`,
        text: `${measuredLater(t) ? (fi ? t.criterionFi : t.criterion) : (fi ? t.progressTextFi : t.progressText)} · ${fi ? 'ratkeaa' : 'decides'} ${clock(t.deadlineSec)}` });
    } else if (t.settledAtSec !== undefined) {
      items.push({ key: t.testId + '-r', at: t.settledAtSec, tone: t.status, label: `${fi ? 'Testi' : 'Test'} ${team}`,
        text: `${statusText(t.status, fi)} ${clock(t.settledAtSec)} · ${fi ? t.progressTextFi : t.progressText}` });
    }
  }
  items.sort((a, b) => b.at - a.at);
  const shown = items.slice(0, 8);
  if (shown.length === 0) {
    return (
      <div className="led" aria-hidden="true">
        <div className="led-track static"><span className="led-item"><b className="led-label">{clockSec !== undefined ? clock(clockSec) : 'Second Ball IQ'}</b>{fi ? 'Ottelu käynnissä' : 'Match underway'}</span></div>
      </div>
    );
  }
  const row = shown.map((i) => (
    <span key={i.key} className={`led-item led-${i.tone}`}><b className="led-label">{i.label}</b>{i.text}</span>
  ));
  return (
    <div className="led" role="marquee" aria-label={fi ? 'Ottelun tapahtumat' : 'Match ticker'}>
      {/* Two copies make the loop seamless; the second is hidden from screen readers. */}
      <div className="led-track" style={{ animationDuration: `${Math.max(24, shown.length * 7)}s` }}>
        <span className="led-run">{row}</span>
        <span className="led-run" aria-hidden="true">{row}</span>
      </div>
    </div>
  );
}
