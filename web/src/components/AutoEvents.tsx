import { clock } from '../format';
import { playerTeam } from '../kit';
import type { AutoEventView, KindScore, MatchInfo } from '../types';

/**
 * Studio › Evaluation: auto-eventing. The same events found again from the tracking positions alone (no event feed),
 * and scored against the feed as the match goes: how many of the real events were found, how many of the found
 * ones were real, and how far off the measurements are.
 */
export function AutoEvents({ view, info }: { view?: AutoEventView; info: MatchInfo }) {
  if (!view) {
    return (
      <section className="auto-events" aria-label="Auto-eventing">
        <header className="fd-head"><h3>Auto-eventing from tracking</h3></header>
        <p className="muted">Waiting for the tracking data (it is built when the match starts).</p>
      </section>
    );
  }
  const r = view.report;
  const pct = (v: number) => `${(v * 100).toFixed(1)}%`;
  const row = (label: string, k: KindScore, extra: string) => (
    <tr>
      <th scope="row">{label}</th>
      <td className="num">{k.matched}/{k.true}</td>
      <td className="num">{pct(k.recall)}</td>
      <td className="num">{pct(k.precision)}</td>
      <td>{extra}</td>
    </tr>
  );
  const name = (id?: string) => (id ? playerTeam(info, id)?.player.name ?? id : '');
  return (
    <section className="auto-events" aria-label="Auto-eventing">
      <header className="fd-head">
        <h3>Auto-eventing from tracking</h3>
        <span className="muted num">scored to {clock(Math.floor(view.upToSec))}</span>
      </header>
      <p className="muted">
        The auto-eventer sees only player and ball positions, ten times a second, the way a vision pipeline would deliver
        them. It finds passes, shots and ball wins again, and is scored here against the event feed it never saw.
      </p>
      <table className="ae-table">
        <thead>
          <tr><th scope="col">Event</th><th scope="col">Found</th><th scope="col">Recall</th><th scope="col">Precision</th><th scope="col">Measurements</th></tr>
        </thead>
        <tbody>
          {row('Passes', r.passes, `outcome ${pct(r.passOutcomeAgreement)} · receiver ${pct(r.receiverAgreement)} · length ±${r.passLengthErrorMetres.toFixed(1)} m · speed ±${r.passSpeedErrorKmh.toFixed(1)} km/h`)}
          {row('Shots', r.shots, `result ${pct(r.shotResultAgreement)} · speed ±${r.shotSpeedErrorKmh.toFixed(1)} km/h · goals ${r.goalsFound}/${r.goalsTrue}`)}
          {row('Ball wins', r.ballWins, 'tackles and interceptions')}
        </tbody>
      </table>
      {view.recent.length > 0 && (
        <ol className="ae-recent">
          {[...view.recent].reverse().map((d) => (
            <li key={`${d.kind}-${d.t}-${d.playerId}`}>
              <span className="num">{clock(Math.floor(d.t))}</span>
              <span>{d.kind === 'pass' ? (d.completed ? 'Pass' : 'Pass, lost') : d.kind === 'shot' ? `Shot, ${d.shotResult}` : 'Ball won'}</span>
              <span>{name(d.playerId)}{d.kind === 'pass' && d.toPlayerId ? ` → ${name(d.toPlayerId)}` : ''}</span>
              <span className="num muted">
                {d.metres !== undefined && d.metres !== null && d.kind !== 'ballWin' ? `${Math.round(d.metres)} m` : ''}
                {d.speedKmh ? ` · ${Math.round(d.speedKmh)} km/h` : ''}
              </span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
