import { useState } from 'react';
import { api } from '../api';
import { clock, kindLabel, personaLabel, teamName } from '../format';
import { formatFigure } from './LowerThird';
import type { ExpectationView, CandidateView, InsightView, MatchInfo, TickView } from '../types';

/** What went to air for the chosen viewer, newest first. */
export function OnAir({ insights, info, onSelect }: { insights: InsightView[]; info: MatchInfo; onSelect: (cid: string) => void }) {
  if (insights.length === 0) return <p className="empty">Nothing has gone to air for this viewer yet.</p>;
  return (
    <ol className="feed">
      {[...insights].reverse().map((v) => {
        const i = v.insight;
        const by = v.status === 'live' ? 'Auto-published' : v.insight.publish?.by.endsWith(':edited') ? 'Edited by editor' : 'Approved by editor';
        return (
          <li key={i.insightId} className={`feed-item side-${i.teamIds[0] === info.home.teamId ? 'home' : 'away'}`}>
            <button type="button" className="feed-clock" onClick={() => onSelect(v.candidateId)} title="Show on the agent desk">
              {clock(i.matchClockSec)}
            </button>
            <div>
              <p className="feed-headline">{i.headline}</p>
              <p className="feed-summary">{i.summary}</p>
              <p className="feed-meta">
                <span>{kindLabel[i.kind]}</span>
                <span>{by}</span>
                {i.validation?.groundingP != null && <span>Grounding p {i.validation.groundingP.toFixed(2)}</span>}
                {i.figures.map((f) => <span key={f.label} className="fig">{f.label} {formatFigure(f.value, f.unit)}</span>)}
              </p>
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/** Insights a gate was unsure about. Edits are re-checked by the same Validator the agents face. */
export function EditorQueue({ insights, info }: { insights: InsightView[]; info: MatchInfo }) {
  const pending = insights.filter((i) => i.status === 'pending');
  const decided = insights.filter((i) => i.status === 'approved' || i.status === 'rejected').slice(-6).reverse();
  return (
    <div className="editor">
      {pending.length === 0 && <p className="empty">The queue is clear. Insights land here when a gate is unsure.</p>}
      <ol className="queue">
        {pending.map((v) => <QueueItem key={v.insight.insightId} v={v} info={info} />)}
      </ol>
      {decided.length > 0 && (
        <>
          <h3 className="subhead">Recently decided</h3>
          <ul className="decided">
            {decided.map((v) => (
              <li key={v.insight.insightId}>
                <span className={`badge badge-${v.status}`}>{v.status === 'approved' ? 'Published' : 'Rejected'}</span>
                {clock(v.insight.matchClockSec)} {personaLabel(v.personaKey)}: {v.insight.headline}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function QueueItem({ v, info }: { v: InsightView; info: MatchInfo }) {
  const [headline, setHeadline] = useState(v.insight.headline);
  const [summary, setSummary] = useState(v.insight.summary);
  const [problems, setProblems] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const edited = headline !== v.insight.headline || summary !== v.insight.summary;
  const id = v.insight.insightId;

  const run = async (action: () => Promise<{ ok: boolean; problems: string[] }>) => {
    setBusy(true);
    try {
      const result = await action();
      setProblems(result.ok ? [] : result.problems);
    } catch (e) {
      setProblems([(e as Error).message]);
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="queue-item">
      <div className="qi-meta">
        <strong>{clock(v.insight.matchClockSec)}</strong>
        <span>{kindLabel[v.insight.kind]}, {teamName(info, v.insight.teamIds[0])}</span>
        <span className="persona-tag">{personaLabel(v.personaKey)}</span>
      </div>
      {v.note && <p className="qi-note">Held because {v.note}</p>}
      <label className="field">
        <span>Headline</span>
        <input value={headline} maxLength={70} onChange={(e) => setHeadline(e.target.value)} />
      </label>
      <label className="field">
        <span>Summary</span>
        <textarea value={summary} rows={3} onChange={(e) => setSummary(e.target.value)} />
      </label>
      {problems.length > 0 && (
        <ul className="problems" role="alert">
          {problems.map((p) => <li key={p}>{p}</li>)}
        </ul>
      )}
      <div className="qi-actions">
        <button type="button" className="primary" disabled={busy}
          onClick={() => void run(() => (edited ? api.edit(id, headline, summary) : api.approve(id)))}>
          {edited ? 'Publish edit' : 'Publish'}
        </button>
        <button type="button" disabled={busy} onClick={() => void run(() => api.reject(id))}>Reject</button>
        {edited && (
          <button type="button" className="link" disabled={busy}
            onClick={() => { setHeadline(v.insight.headline); setSummary(v.insight.summary); setProblems([]); }}>
            Undo changes
          </button>
        )}
      </div>
    </li>
  );
}

/** The scenario's known truth, revealed as the clock reaches each window, against what the engine found. */
export function GroundTruth({ expectations, candidates, insights, tick, info }: {
  expectations: ExpectationView[]; candidates: CandidateView[]; insights: InsightView[]; tick: TickView; info: MatchInfo;
}) {
  const shown = expectations.filter((x) => x.fromSec <= tick.clockSec);
  const done = candidates.filter((c) => c.status === 'done');
  const count = (s: InsightView['status']) => insights.filter((i) => i.status === s).length;
  const total = insights.length || 1;
  const times = done.map((c) => c.totalMs ?? 0).sort((a, b) => a - b);
  const median = times.length ? times[Math.floor(times.length / 2)] / 1000 : 0;

  return (
    <div className="truth">
      <dl className="stats">
        <div><dt>Moments flagged</dt><dd>{candidates.length}</dd></div>
        <div><dt>Auto-published</dt><dd>{Math.round((count('live') / total) * 100)}%</dd></div>
        <div><dt>Sent to editor</dt><dd>{count('pending') + count('approved') + count('rejected')}</dd></div>
        <div><dt>Dropped by checks</dt><dd>{count('dropped')}</dd></div>
        <div><dt>Median time to air</dt><dd>{median.toFixed(1)} s</dd></div>
      </dl>
      {shown.length === 0 ? (
        <p className="empty">
          {expectations.length === 0
            ? 'A real match has no scripted truth to score against, so only the publishing numbers above apply.'
            : "This scenario's scripted patterns appear here as the clock reaches them."}
        </p>
      ) : (
        <table className="truth-table">
          <thead>
            <tr><th scope="col">Expected</th><th scope="col">Window</th><th scope="col">Result</th></tr>
          </thead>
          <tbody>
            {shown.map((x, i) => {
              const hit = candidates.find((c) => c.candidateId === x.hitCandidateId);
              const late = tick.clockSec > x.toSec + 180;
              const result = !x.verified ? 'Not in this seed' : hit ? `Found at ${clock(hit.detectedAtSec)}` : late ? 'Missed' : 'Watching';
              return (
                <tr key={i}>
                  <td>{kindLabel[x.kind]}, {teamName(info, x.teamId)}</td>
                  <td className="num">{clock(x.fromSec)} to {clock(x.toSec)}</td>
                  <td className={hit ? 'tone-good' : late && x.verified ? 'tone-bad' : 'muted'}>{result}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
