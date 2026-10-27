import { useEffect, useState, type ReactNode } from 'react';
import {
  Badge, Button, Card, CounterBadge, Dropdown, Option, Tab, TabList, ToggleButton, Tooltip,
} from '@fluentui/react-components';
import {
  Add24Regular, Beaker24Regular, PeopleTeam24Regular, Board24Regular, CheckmarkCircle24Regular, Chat24Regular, DocumentEdit24Regular, Flow24Regular, Live24Regular,
  Pause24Regular, People24Regular, Phone24Regular, Play24Regular, Speaker224Regular, SpeakerOff24Regular,
  Target24Regular,
} from '@fluentui/react-icons';
import { api } from '../api';
import { clock, kindLabel, personaLabel, teamName } from '../format';
import { sideOf } from '../kit';
import { formatFigure } from './LowerThird';
import { Shirt } from './Shirt';
import { Mark, Roll, Wordmark } from './Brand';
import type { Connection } from '../store';
import type { InsightView, MatchInfo, SessionInfo, TickView } from '../types';

export type StudioTab = 'air' | 'tests' | 'coach' | 'tactics' | 'players' | 'predictions' | 'ask' | 'editor' | 'trace' | 'truth';

const SPEEDS = [1, 5, 10, 30];

/** The operator's bar: what is playing, how fast, for whom, and the controls to change it. Always navy. */
export function StudioBar({ info, tick, connection, onNewMatch }:
  { info?: SessionInfo; tick?: TickView; connection: Connection; onNewMatch: () => void }) {
  const running = tick?.status === 'running';
  const playing = tick && (tick.status === 'running' || tick.status === 'paused');
  const m = info?.match;
  // On a public site the match controls need the operator key; say so instead of failing silently.
  const [denied, setDenied] = useState<string>();
  const control = (action: () => Promise<unknown>) => void action().then(() => setDenied(undefined), (e: unknown) => setDenied(e instanceof Error ? e.message : String(e)));
  return (
    <header className="sbar">
      <span className="sbar-brand"><Mark /> <span className="sbar-word"><Wordmark /></span> <span className="sbar-sub">Studio</span></span>

      {m && tick ? (
        <div className="sbar-score" aria-label={`${m.home.name} ${tick.homeGoals}, ${m.away.name} ${tick.awayGoals}`}>
          <Shirt side="home" size={26} />
          <span className="sbar-team">{m.home.shortName}</span>
          <span className="sbar-goals num"><Roll value={tick.homeGoals} /><span className="sbar-dash">–</span><Roll value={tick.awayGoals} /></span>
          <span className="sbar-team">{m.away.shortName}</span>
          <Shirt side="away" size={26} />
          <span className="sbar-clock num">{clock(tick.clockSec)}</span>
          <StatusBadge tick={tick} />
        </div>
      ) : (
        <span className="sbar-empty">{connection === 'connected' ? 'No match playing' : 'Connecting…'}</span>
      )}

      <div className="sbar-controls">
        {playing && (
          <>
            <Button appearance="subtle" icon={running ? <Pause24Regular /> : <Play24Regular />} onClick={() => control(running ? api.pause : api.resume)}>
              {running ? 'Pause' : 'Resume'}
            </Button>
            <div className="sbar-speeds" role="group" aria-label="Replay speed">
              {SPEEDS.map((s) => (
                <ToggleButton key={s} size="small" appearance="subtle" checked={tick?.speed === s} onClick={() => control(() => api.speed(s))}>{s}×</ToggleButton>
              ))}
            </div>
          </>
        )}
        {denied && (
          <Tooltip content={denied} relationship="description">
            <Badge appearance="filled" color="warning">Operator key needed</Badge>
          </Tooltip>
        )}
        <Button as="a" href="/fan" target="_blank" rel="noreferrer" appearance="subtle" icon={<Phone24Regular />}>Fan view</Button>
        <Button appearance="primary" icon={<Add24Regular />} onClick={onNewMatch}>New match</Button>
        <Tooltip content={tick?.modelsPaused ? 'The hourly or daily model budget is spent: new moments use offline templates until it turns' : info ? `Models: ${info.models}` : 'Server connection'} relationship="description">
          <Badge appearance="outline" color={connection !== 'connected' ? 'danger' : tick?.modelsPaused ? 'warning' : info?.live ? 'success' : 'informative'}>
            {connection !== 'connected' ? connection : tick?.modelsPaused ? 'Models paused' : info?.live ? 'Live models' : info ? 'Offline templates' : 'Connected'}
          </Badge>
        </Tooltip>
      </div>
    </header>
  );
}

function StatusBadge({ tick }: { tick: TickView }) {
  const [text, color] = ((): [string, 'success' | 'warning' | 'informative' | 'subtle'] => {
    switch (tick.status) {
      case 'running': return [tick.queueLength > 0 ? `Running · ${tick.queueLength} with the agents` : 'Replay running', 'success'];
      case 'paused': return ['Paused', 'warning'];
      case 'finishing': return [tick.queueLength > 0 ? `Full time · ${tick.queueLength} with the agents` : 'Full time · writing the recap', 'informative'];
      case 'finished': return ['Full time', 'informative'];
      default: return ['Stopped', 'subtle'];
    }
  })();
  return <Badge appearance="tint" color={color} className="sbar-status">{text}</Badge>;
}

/** Who the on-air graphics are written for, and whether they are read aloud: the output's own controls. */
export function OutputControls({ personas, persona, onPersona, voiceOn, onVoice }:
  { personas: string[]; persona: string; onPersona: (p: string) => void; voiceOn: boolean; onVoice: () => void }) {
  return (
    <div className="output-controls">
      <Dropdown aria-label="Viewer the graphics are written for" size="small" className="out-persona"
        value={personaLabel(persona)} selectedOptions={[persona]} onOptionSelect={(_, d) => d.optionValue && onPersona(d.optionValue)}>
        {personas.map((p) => <Option key={p} value={p}>{personaLabel(p)}</Option>)}
      </Dropdown>
      <Tooltip content="Read each graphic aloud as it goes to air" relationship="description">
        <ToggleButton size="small" appearance="subtle" checked={voiceOn} onClick={onVoice} icon={voiceOn ? <Speaker224Regular /> : <SpeakerOff24Regular />}>
          Voice
        </ToggleButton>
      </Tooltip>
    </div>
  );
}

/** The operator's sections. */
export function StudioNav({ tab, onTab, pending, predictions, openTests = 0 }:
  { tab: StudioTab; onTab: (t: StudioTab) => void; pending: number; predictions: string; openTests?: number }) {
  const items: [StudioTab, string, JSX.Element, ReactNode?][] = [
    ['air', 'On air', <Live24Regular />],
    ['tests', 'Tactical tests', <Beaker24Regular />, openTests > 0 ? <Badge size="small" appearance="tint" color="informative">{openTests} open</Badge> : undefined],
    ['coach', "Coach's corner", <PeopleTeam24Regular />],
    ['trace', 'Agent trace', <Flow24Regular />],
    ['editor', 'Editor queue', <DocumentEdit24Regular />, pending > 0 ? <CounterBadge count={pending} size="small" color="danger" /> : undefined],
    ['truth', 'Evaluation', <CheckmarkCircle24Regular />],
    ['tactics', 'Tactics', <Board24Regular />],
    ['players', 'Players', <People24Regular />],
    ['predictions', 'Predictions', <Target24Regular />, predictions ? <Badge size="small" appearance="tint" color="informative">{predictions}</Badge> : undefined],
    ['ask', 'Ask Second Ball IQ', <Chat24Regular />],
  ];
  return (
    <nav className="snav" aria-label="Studio sections">
      <TabList vertical selectedValue={tab} onTabSelect={(_, d) => onTab(d.value as StudioTab)}>
        {items.map(([id, label, icon, extra]) => (
          <Tab key={id} value={id} icon={icon}>
            <span className="snav-label">{label}{extra}</span>
          </Tab>
        ))}
      </TabList>
    </nav>
  );
}

/** A titled studio panel. */
export function Panel({ title, aside, children, className }: { title: string; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <Card className={`spanel ${className ?? ''}`}>
      <div className="spanel-head"><h2>{title}</h2>{aside && <span className="spanel-aside">{aside}</span>}</div>
      {children}
    </Card>
  );
}

/**
 * What the broadcast graphic is doing right now, with its timed-overlay contract: the clock it belongs to, how long
 * it stays on screen, who it is written for, and the evidence behind it. The graphic itself plays over the board.
 */
export function OnAirPreview({ showing, shownAt, last, info, queued, controls }:
  { showing?: InsightView; shownAt?: number; last?: InsightView; info: MatchInfo; queued: number; controls?: ReactNode }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!showing) return;
    const t = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(t);
  }, [showing]);
  const v = showing ?? last;
  if (!v) {
    return (
      <Panel title="On-air preview" aside={<Badge appearance="tint" color="subtle">Idle</Badge>}>
        {controls}
        <p className="muted">Nothing on screen yet. A graphic goes to air as soon as the agents publish a moment.</p>
      </Panel>
    );
  }
  const i = v.insight;
  const ms = Math.min(i.displayMs ?? 8000, 12000);
  const left = showing && shownAt ? Math.max(0, Math.ceil((ms - (now - shownAt)) / 1000)) : 0;
  const side = sideOf(info, i.teamIds[0]);
  return (
    <Panel title="On-air preview"
      aside={showing
        ? <span className="diode-label tally"><i aria-hidden="true" />On air {left} s</span>
        : <span className="diode-label idle"><i aria-hidden="true" />Last graphic</span>}>
      {controls}
      <div className={`oap-graphic side-${side} ${showing ? 'live' : ''}`}>
        <p className="oap-kicker"><span className="num">{clock(i.matchClockSec)}</span> · {kindLabel[i.kind].toUpperCase()}</p>
        <p className="oap-headline">{i.headline}</p>
        <p className="oap-summary">{i.summary}</p>
        {showing && <span className="oap-timer" style={{ width: `${(left * 1000 / ms) * 100}%` }} />}
      </div>
      <dl className="oap-contract">
        <div><dt>Match clock</dt><dd className="num">{clock(i.matchClockSec)}</dd></div>
        <div><dt>On screen</dt><dd className="num">{(ms / 1000).toFixed(0)} s</dd></div>
        <div><dt>Written for</dt><dd>{personaLabel(v.personaKey)}</dd></div>
        <div><dt>Evidence</dt><dd className="num">{i.evidenceEventIds.length} events</dd></div>
        {i.figures.slice(0, 2).map((f) => <div key={f.label}><dt>{f.label}</dt><dd className="num">{formatFigure(f.value, f.unit)}</dd></div>)}
        {i.validation?.groundingP != null && <div><dt>Grounding p</dt><dd className="num">{i.validation.groundingP.toFixed(2)}</dd></div>}
      </dl>
      {queued > 0 && <p className="muted oap-queue">{queued} more queued for air</p>}
    </Panel>
  );
}

/** "grounding: unsure: every event the claim describes appears in the evidence (p 0.29); every outcome …" →
 * "grounding unsure: events p 0.29, outcomes p 0.22". */
function shortReason(note: string): string {
  const ps = [...note.matchAll(/(event|outcome|neutral|fair|count)[^;]*?\(p ([\d.]+)\)/gi)].map((m) => {
    const word = m[1].toLowerCase();
    return `${word.startsWith('event') ? 'events' : word.startsWith('outcome') ? 'outcomes' : word.startsWith('count') ? 'counts' : 'reading'} p ${m[2]}`;
  });
  const head = note.split(':').slice(0, 2).map((x) => x.trim()).join(' ');
  if (ps.length > 0) return `${note.split(':')[0].trim()} unsure: ${ps.join(', ')}`;
  return head.length > 90 ? `${head.slice(0, 88)}…` : head;
}

/** Insights a gate held back, waiting for a person: why, and the way to the evidence or the editor. */
export function NeedsReview({ insights, info, persona, onReview, onTrace }:
  { insights: InsightView[]; info: MatchInfo; persona: string; onReview: () => void; onTrace: (candidateId: string) => void }) {
  const [full, setFull] = useState(false);
  const pending = insights.filter((i) => i.status === 'pending');
  // Each moment is written for every persona, so one unsure moment can hold several drafts: count moments.
  const moments = new Set(pending.map((i) => i.candidateId)).size;
  const first = [...pending].reverse().find((i) => i.personaKey === persona) ?? pending[pending.length - 1];
  const reason = first?.note ? shortReason(first.note) : undefined;
  return (
    <Panel title="Needs review" aside={pending.length > 0
      ? <Badge appearance="tint" color="warning">{moments} {moments === 1 ? 'moment' : 'moments'} · {pending.length} {pending.length === 1 ? 'draft' : 'drafts'}</Badge>
      : <Badge appearance="tint" color="success">Clear</Badge>}>
      {first ? (
        <div className="review">
          <p className="review-meta">
            <span className="num">{clock(first.insight.matchClockSec)}</span> · {kindLabel[first.insight.kind]}, {teamName(info, first.insight.teamIds[0])} · {personaLabel(first.personaKey)}
          </p>
          <p className="review-headline">{first.insight.headline}</p>
          {first.note && (
            <p className="review-why">
              Held because {full ? first.note : reason}
              {reason !== first.note && (
                <> <button type="button" className="link review-more" onClick={() => setFull(!full)}>{full ? 'Less' : 'Full reason'}</button></>
              )}
            </p>
          )}
          <div className="review-actions">
            <Button appearance="primary" onClick={onReview}>Review insight</Button>
            <Button appearance="outline" onClick={() => onTrace(first.candidateId)}>Inspect trace</Button>
          </div>
        </div>
      ) : (
        <p className="muted">Every moment so far passed the checks. When a gate is unsure, the insight waits here for a person.</p>
      )}
    </Panel>
  );
}
