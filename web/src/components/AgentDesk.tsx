import { useState } from 'react';
import { clock, kindLabel, playerName, stages, teamName, tone, type Tone } from '../format';
import type { CandidateView, MatchInfo, TraceStep } from '../types';

interface Props {
  candidates: CandidateView[];
  info: MatchInfo;
  selected?: string;
  onSelect: (candidateId?: string) => void;
  /** Show only the newest few (the live panel); the full trace lives in its own section. */
  limit?: number;
  onMore?: () => void;
}

/**
 * Every moment the engine flagged, newest first, with the six workflow steps lighting up as the agents work.
 * Opening a card shows the analyst's reading, each step's result and the measured facts, and rings the cited
 * events on the pitch.
 */
export function AgentDesk({ candidates, info, selected, onSelect, limit, onMore }: Props) {
  const all = [...candidates].reverse();
  const list = limit ? all.slice(0, limit) : all;
  return (
    <aside className="desk" aria-label="Agent workflow">
      <div className="panel-head">
        <h2>Agent desk</h2>
        <span className="muted">{candidates.length ? `${candidates.length} ${candidates.length === 1 ? 'moment' : 'moments'} flagged` : ''}</span>
      </div>
      {list.length === 0 && (
        <p className="empty">
          The analytics engine is watching. When it flags a moment, the triage gate, the analyst, the narrators and
          the checks will work on it here.
        </p>
      )}
      <ol className="cards">
        {list.map((c) => (
          <CandidateCard key={c.candidateId} c={c} info={info} open={selected === c.candidateId}
            onToggle={() => onSelect(selected === c.candidateId ? undefined : c.candidateId)} />
        ))}
      </ol>
      {limit && all.length > limit && onMore && (
        <button type="button" className="link desk-more" onClick={onMore}>All {all.length} moments in the agent trace</button>
      )}
    </aside>
  );
}

function CandidateCard({ c, info, open, onToggle }: { c: CandidateView; info: MatchInfo; open: boolean; onToggle: () => void }) {
  const trace = c.trace ?? [];
  const player = playerName(info, c.playerId);
  const side = c.teamId === info.home.teamId ? 'home' : 'away';
  const [showText, setShowText] = useState(false);

  return (
    <li className={`card cand-${c.status} ${open ? 'open' : ''}`}>
      <button type="button" className="card-head" onClick={onToggle} aria-expanded={open}>
        <span className="card-clock">{clock(c.detectedAtSec)}</span>
        <span className="card-title">
          <span className={`chip chip-${side}`}>{teamName(info, c.teamId)}</span>
          {kindLabel[c.kind]}{player ? `, ${player}` : ''}
          {trace.some((t) => t.step === 'Planted error') && <span className="chip-drill">fault drill</span>}
        </span>
        {c.totalMs !== undefined && <span className="card-ms">{(c.totalMs / 1000).toFixed(1)} s</span>}
      </button>
      <Rail c={c} trace={trace} />
      {c.status === 'done' && c.outcome && <Outcome text={c.outcome} />}
      {c.status === 'failed' && <p className="card-outcome tone-bad">Failed: {c.outcome}</p>}

      {open && (
        <div className="card-body">
          {c.analysis && (
            <blockquote className="analysis">
              {c.analysis}
              <footer>{c.analysisFromTemplate ? 'Template analysis' : 'Tactical Analyst'}</footer>
            </blockquote>
          )}
          <table className="trace">
            <tbody>
              {trace.map((t, i) => (
                <tr key={i}>
                  <th scope="row">{stepLabel(t.step)}</th>
                  <td className={`tone-${tone(t.outcome)}`}>{t.outcome}</td>
                  <td className="num">{Math.round(t.milliseconds)} ms</td>
                  <td className="detail">{t.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <dl className="facts">
            {Object.entries(c.facts).map(([k, v]) => (
              <div key={k}><dt>{k}</dt><dd>{Number.isInteger(v) ? v : v.toFixed(3)}</dd></div>
            ))}
          </dl>
          <button type="button" className="link" onClick={() => setShowText(!showText)} aria-expanded={showText}>
            {showText ? 'Hide what the gates read' : 'Show what the gates read'}
          </button>
          {showText && (
            <div className="gate-texts">
              <h4>Triage reads</h4>
              <pre>{c.windowSummary}</pre>
              <h4>Grounding checks against</h4>
              <pre>{c.evidence}</pre>
            </div>
          )}
        </div>
      )}
    </li>
  );
}

/** Six segments; each takes the worst result among its steps (eight narrators, eight groundings). */
function Rail({ c, trace }: { c: CandidateView; trace: TraceStep[] }) {
  const stoppedEarly = trace.some((t) => t.step === 'Triage' && (t.outcome === 'drop' || t.outcome === 'fold'));
  let activeFound = false;
  return (
    <ol className="rail" aria-label="Workflow steps">
      {stages.map((s) => {
        const steps = trace.filter((t) => t.step === s.step);
        let state: Tone | 'idle' | 'active' | 'skip' = 'idle';
        if (steps.length) state = stoppedEarly && s.step === 'Publish' ? 'quiet' : worst(steps.map((t) => tone(t.outcome)));
        else if (stoppedEarly && s.step !== 'Publish') state = 'skip';
        else if (c.status === 'processing' && !activeFound) { state = 'active'; activeFound = true; }
        if (steps.length && s.step === 'Narrator' && c.status === 'processing' && steps.length < 8 && !activeFound) {
          state = 'active';
          activeFound = true;
        }
        return (
          <li key={s.step} className={`seg seg-${state}`} title={steps.map((t) => `${t.outcome}: ${t.detail ?? ''}`).join('\n') || s.who}>
            <span className="seg-label">{s.label}</span>
            <span className="seg-who">{s.who}</span>
          </li>
        );
      })}
    </ol>
  );
}

function worst(tones: Tone[]): Tone {
  if (tones.includes('bad')) return 'bad';
  if (tones.includes('warn')) return 'warn';
  if (tones.includes('quiet')) return 'quiet';
  return 'good';
}

const STORY: Record<string, string> = {
  pressingShift: 'the pressing story',
  possessionIllusion: 'the possession story',
  turnoverToChance: 'the chance',
  playerMilestone: 'the player story',
  goal: 'the goal',
};

/** The server's outcome line, said the way an editor would say it. */
function Outcome({ text }: { text: string }) {
  const fold = text.match(/^part of a bigger story: triage chose (\w+) \((.*)\)$/);
  if (fold) {
    return (
      <p className="card-outcome tone-quiet">
        Folded into {STORY[fold[1]] ?? fold[1]}, which has its own insight. <span className="muted">Triage: {fold[2]}</span>
      </p>
    );
  }
  if (text.startsWith('triage:')) return <p className="card-outcome tone-quiet">Not shown: {text.slice(8)}</p>;
  const parts = [...text.matchAll(/(\d+) (Auto|Editor|Drop)/g)].map((m) => ({ n: Number(m[1]), mode: m[2] }));
  if (parts.length === 0) return <p className="card-outcome">{text}</p>;
  const words: Record<string, string> = { Auto: 'on air', Editor: 'to the editor', Drop: 'dropped' };
  const t: Tone = parts.some((p) => p.mode === 'Drop') ? 'bad' : parts.some((p) => p.mode === 'Editor') ? 'warn' : 'good';
  return <p className={`card-outcome tone-${t}`}>{parts.map((p) => `${p.n} ${words[p.mode]}`).join(', ')}</p>;
}

function stepLabel(step: string): string {
  return stages.find((s) => s.step === step)?.label ?? step;
}
