import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../api';
import { clock, roleLabel } from '../format';
import { useStage } from './Stage';
import { Pitch } from './Pitch';
import { rewindEvents, RewindBar, useRewind } from './Rewind';
import { HeatLayer, ShapeDefs, ShapeLayer } from './Tactics';
import type { AskAction, AskResult, CandidateView, MatchInfo, PitchEvent, ShapeReport } from '../types';

interface Props {
  info: MatchInfo;
  events: PitchEvent[];
  candidates: CandidateView[];
  clockSec: number;
  audience: string;
  language: 'en' | 'fi';
  teamId?: string;
  playerId?: string;
  onPlayer?: (id: string) => void;
  onAnswer?: (a: AskResult) => void;
  /** In a watch party: share an answer's evidence with the room. */
  onShare?: (label: string, eventIds: string[]) => void;
  /** "Ask why" from a moment: a question to ask straight away, about that moment. A new id asks again. */
  seed?: { id: number; question: string; about?: string };
  /** Hide the panel's own heading where the page already has one. */
  bare?: boolean;
}

/**
 * Ask Second Ball IQ: ask anything about the match and get an answer an analyst would give, built from the engine's
 * numbers, checked before it is shown, with the evidence one tap away on the pitch.
 */
export function AskPanel({ info, events, candidates, clockSec, audience, language, teamId, playerId, onPlayer, onAnswer, onShare, seed, bare }: Props) {
  const fi = language === 'fi';
  const [question, setQuestion] = useState('');
  const [turns, setTurns] = useState<AskResult[]>([]);
  const [pending, setPending] = useState<string>();
  const [error, setError] = useState<string>();
  const end = useRef<HTMLDivElement>(null);
  const suggestions = useMemo(() => suggest(info, candidates, fi, playerId, teamId), [info, candidates, fi, playerId, teamId]);

  useEffect(() => { end.current?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }, [turns.length, pending]);

  // Ask by voice: the browser's own speech recognition (Chrome, Edge, Safari); the question is sent when you stop.
  const speech = typeof window !== 'undefined' && ('SpeechRecognition' in window || 'webkitSpeechRecognition' in window);
  const [listening, setListening] = useState(false);
  const listen = () => {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const w = window as any;
    const Rec = w.SpeechRecognition ?? w.webkitSpeechRecognition;
    if (!Rec || listening) return;
    const rec = new Rec();
    rec.lang = fi ? 'fi-FI' : 'en-GB';
    rec.interimResults = true;
    rec.maxAlternatives = 1;
    let text = '';
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    rec.onresult = (e: any) => {
      text = Array.from(e.results).map((r: any) => r[0].transcript).join(' ');
      setQuestion(text);
    };
    rec.onerror = () => setListening(false);
    rec.onend = () => { setListening(false); if (text.trim().length >= 3) void ask(text); };
    setListening(true);
    rec.start();
  };

  const ask = async (q: string) => {
    const text = q.trim();
    if (text.length < 3 || pending) return;
    setPending(text);
    setError(undefined);
    setQuestion('');
    try {
      const a = await api.ask({
        question: text, audience, language, teamId: teamId || undefined, playerId: playerId || undefined,
        history: turns.slice(-3).map((t) => ({ question: t.question, answer: t.answer })),
      });
      setTurns((t) => [...t, a]);
      onAnswer?.(a);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setQuestion(text);
    } finally {
      setPending(undefined);
    }
  };

  // A question handed over from a moment ("Ask why") is asked once, as soon as it arrives.
  const asked = useRef<number>();
  const [about, setAbout] = useState<string>();
  useEffect(() => {
    if (!seed || asked.current === seed.id) return;
    asked.current = seed.id;
    setAbout(seed.about);
    void ask(seed.question);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seed?.id]);

  return (
    <section className="ask" aria-label="Ask Second Ball IQ">
      {!bare && (
        <header className="ask-head">
          <h2>Ask Second Ball IQ</h2>
          <span className="ask-clock num">{clock(clockSec)}</span>
        </header>
      )}
      {about && (
        <p className="ask-about">
          <span>{fi ? 'Hetkestä' : 'About'}</span> {about}
          <button type="button" className="link" onClick={() => setAbout(undefined)}>{fi ? 'Poista' : 'Clear'}</button>
        </p>
      )}
      {turns.length === 0 && !pending && (
        <p className="ask-intro">
          {fi
            ? 'Kysy mitä tahansa tästä ottelusta. Second Ball IQ tutkii luvut, muodot ja tilanteet ja näyttää todisteet kentällä.'
            : 'Ask anything about this match. Second Ball IQ looks at the numbers, the shapes and the events, and shows you the evidence on the pitch.'}
        </p>
      )}

      <div className="ask-thread">
        {turns.map((t) => (
          <AskAnswer key={t.askId} a={t} info={info} events={events} fi={fi} onPlayer={onPlayer} onShare={onShare} />
        ))}
        {pending && (
          <div className="ask-turn">
            <p className="ask-q">{pending}</p>
            <p className="ask-working"><span className="ask-dots" aria-hidden="true" /> {fi ? 'Tutkitaan ottelua…' : 'Looking into the match…'}</p>
          </div>
        )}
        <div ref={end} />
      </div>

      <div className="ask-suggest">
        {(turns.length ? turns[turns.length - 1].followUps : suggestions).slice(0, 4).map((s) => (
          <button key={s} type="button" className="ask-chip" disabled={!!pending} onClick={() => void ask(s)}>{s}</button>
        ))}
      </div>
      {error && <p className="ask-error" role="alert">{error}</p>}
      <form className="ask-form" onSubmit={(e) => { e.preventDefault(); void ask(question); }}>
        <input value={question} onChange={(e) => setQuestion(e.target.value)} maxLength={300}
          placeholder={fi ? 'Esim. Miksi Northvale vaihtoi kolmen topparin linjaan?' : 'e.g. Why did Northvale switch to a back three?'}
          aria-label={fi ? 'Kysymys' : 'Your question'} />
        {speech && (
          <button type="button" className={`ask-mic ${listening ? 'on' : ''}`} onClick={listen} disabled={!!pending}
            aria-label={fi ? 'Kysy äänellä' : 'Ask by voice'} title={fi ? 'Kysy äänellä' : 'Ask by voice'}>
            <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M8 1.5a2 2 0 0 0-2 2v4a2 2 0 0 0 4 0v-4a2 2 0 0 0-2-2zM4 7.5a4 4 0 0 0 8 0M8 11.5v3" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>
          </button>
        )}
        <button type="submit" className="primary" disabled={!!pending || question.trim().length < 3}>{fi ? 'Kysy' : 'Ask'}</button>
      </form>
    </section>
  );
}

const STATUS: Record<AskResult['status'], [string, string]> = {
  measured: ['Measured', 'Mitattu'],
  interpretation: ['Interpretation', 'Tulkinta'],
  insufficientData: ['Not enough data', 'Ei riittävästi dataa'],
};

/** One question and its answer, with the facts, the check, and the evidence to open. */
export function AskAnswer({ a, info, events, fi, onPlayer, onShare }:
  { a: AskResult; info: MatchInfo; events: PitchEvent[]; fi?: boolean; onPlayer?: (id: string) => void;
    onShare?: (label: string, eventIds: string[]) => void }) {
  const [shared, setShared] = useState(false);
  // On the fan view an answer's evidence is drawn on the match stage; elsewhere (the studio) under the answer.
  const stage = useStage();
  const shareIds = a.actions.find((x) => x.kind === 'rewind')?.eventIds ?? a.citedEventIds;
  const [open, setOpen] = useState<number | undefined>(a.actions.findIndex((x) => x.kind !== 'openPlayer') >= 0 ? a.actions.findIndex((x) => x.kind !== 'openPlayer') : undefined);
  return (
    <div className="ask-turn">
      <p className="ask-q">{a.question}</p>
      <article className={`ask-a status-${a.status}`}>
        <header className="ask-meta">
          <span className="ask-status">{STATUS[a.status][fi ? 1 : 0]}</span>
          {a.check === 'checked' && <span className="ask-check" title={a.checkNote}>✓ {fi ? 'Decision-1 tarkisti' : 'Checked by Decision-1'}</span>}
          {a.check === 'unsure' && <span className="ask-check unsure" title={a.checkNote}>{fi ? 'Tarkistus epävarma' : 'Check unsure'}</span>}
          <span className="ask-at num">{clock(a.clockSec)}</span>
        </header>
        <p className="ask-text">{a.answer}</p>
        {a.keyFacts.length > 0 && (
          <dl className="ask-facts">
            {a.keyFacts.map((f) => <div key={f.label}><dt>{f.label}</dt><dd>{f.value}</dd></div>)}
          </dl>
        )}
        {a.actions.length > 0 && (
          <div className="ask-actions">
            {a.actions.map((x, i) => (
              <button key={i} type="button" className="fc-replay" aria-pressed={open === i}
                onClick={() => (x.kind === 'openPlayer' && x.playerId ? onPlayer?.(x.playerId)
                  : stage.show ? stage.show({ key: `ask-${a.askId}-${i}`, label: x.label || actionLabel(x, fi), node: <AskVisual action={x} info={info} events={events} fi={fi} /> })
                  : setOpen(open === i ? undefined : i))}>
                {x.label || actionLabel(x, fi)}
              </button>
            ))}
          </div>
        )}
        {!stage.show && open !== undefined && a.actions[open] && a.actions[open].kind !== 'openPlayer' && (
          <AskVisual action={a.actions[open]} info={info} events={events} fi={fi} />
        )}
        {onShare && shareIds.length > 0 && (
          <button type="button" className="link ask-share" disabled={shared}
            onClick={() => { onShare(a.actions.find((x) => x.kind === 'rewind')?.label || a.question, shareIds); setShared(true); }}>
            {shared ? (fi ? 'Jaettu juhlille' : 'Shared with the party') : (fi ? 'Näytä kaikille juhlissa' : 'Show everyone in the party')}
          </button>
        )}
        <footer className="ask-foot">
          {a.toolsUsed.length > 0 && <span>{fi ? 'Työkalut' : 'Tools'}: {a.toolsUsed.join(', ')}</span>}
          <span className="num">{(a.milliseconds / 1000).toFixed(1)} s</span>
        </footer>
      </article>
    </div>
  );
}

function actionLabel(x: AskAction, fi?: boolean): string {
  switch (x.kind) {
    case 'rewind': return fi ? 'Toista tilanne' : 'Replay it';
    case 'compareShape': return fi ? 'Vertaa muotoa' : 'Compare the shape';
    case 'showShape': return fi ? 'Näytä muoto' : 'Show the shape';
    case 'heatMap': return fi ? 'Lämpökartta' : 'Heat map';
    default: return fi ? 'Pelaajakortti' : 'Player card';
  }
}

/** The evidence for an answer, drawn on a small pitch: a replay, a heat map, or a shape (before and after). */
export function AskVisual({ action, info, events, fi }: { action: AskAction; info: MatchInfo; events: PitchEvent[]; fi?: boolean }) {
  const moves = useMemo(() => (action.kind === 'rewind' ? rewindEvents(events, action.eventIds) : []), [action, events]);
  const rewind = useRewind(moves);
  const [shapes, setShapes] = useState<{ a?: ShapeReport; b?: ShapeReport }>({});
  const now = events.length ? events[events.length - 1].clockSec : 0;

  useEffect(() => {
    setShapes({});
    if (!action.teamId || (action.kind !== 'compareShape' && action.kind !== 'showShape')) return;
    const from = (action.fromMinute ?? Math.max(0, Math.floor(now / 60) - 15)) * 60;
    const to = (action.toMinute ?? Math.floor(now / 60) + 1) * 60;
    let live = true;
    const first = api.shape(action.teamId, from, to);
    const second = action.kind === 'compareShape' && action.fromMinute2 !== undefined && action.toMinute2 !== undefined
      ? api.shape(action.teamId, action.fromMinute2 * 60, action.toMinute2 * 60) : Promise.resolve(undefined);
    Promise.all([first, second]).then(([a, b]) => { if (live) setShapes({ a, b }); }).catch(() => undefined);
    return () => { live = false; };
  }, [action, now]);

  const color = (action.teamId ?? info.home.teamId) === info.home.teamId ? 'var(--home)' : 'var(--away)';
  const heatColor = action.playerId
    ? (info.home.players.some((p) => p.playerId === action.playerId) ? 'var(--home)' : 'var(--away)') : color;

  if (action.kind === 'rewind') {
    if (moves.length === 0) return <p className="muted">{fi ? 'Ei toistettavaa.' : 'Nothing to replay.'}</p>;
    return (
      <div className="fc-rewind">
        <Pitch events={events} clockSec={moves[moves.length - 1].clockSec} info={info} rewind={{ events: moves, step: rewind.step }} compact />
        <RewindBar moves={moves} info={info} rewind={rewind} fi={fi} title={action.label} onClose={() => rewind.setStep(0)}
          closeLabel={fi ? 'Alusta' : 'From the start'} />
      </div>
    );
  }
  return (
    <div className="fc-rewind">
      <Pitch events={events} clockSec={now} info={info} compact label={action.label}>
        <ShapeDefs />
        {action.kind === 'heatMap' && (
          <HeatLayer events={events} teamId={action.playerId ? undefined : action.teamId} playerId={action.playerId}
            fromSec={(action.fromMinute ?? 0) * 60} toSec={(action.toMinute ?? Math.floor(now / 60) + 1) * 60} color={heatColor} />
        )}
        {action.kind === 'showShape' && shapes.a && <ShapeLayer reports={[shapes.a]} info={info} fi={fi} />}
        {action.kind === 'compareShape' && shapes.a && shapes.b && <ShapeLayer reports={[shapes.b]} ghost={[shapes.a]} info={info} fi={fi} />}
      </Pitch>
      {action.kind === 'compareShape' && shapes.a && shapes.b && (
        <p className="ask-legend">
          {fi ? 'Haamut' : 'Ghosts'} {action.fromMinute}–{action.toMinute}′ ({shapes.a.formation ?? '?'}) → {fi ? 'täydet' : 'solid'} {action.fromMinute2}–{action.toMinute2}′ ({shapes.b.formation ?? '?'})
        </p>
      )}
    </div>
  );
}

/** Questions worth asking right now, from what the engine has just found, plus a couple that always make sense. */
export function suggest(info: MatchInfo, candidates: CandidateView[], fi: boolean, playerId?: string, teamId?: string): string[] {
  const team = (id: string) => (id === info.home.teamId ? info.home.name : info.away.name);
  const player = (id?: string) => [...info.home.players, ...info.away.players].find((p) => p.playerId === id)?.name;
  const out: string[] = [];
  for (const c of [...candidates].reverse()) {
    if (out.length >= 2) break;
    const t = team(c.teamId), tags = c.tags ?? {};
    let q: string | undefined;
    switch (c.kind) {
      case 'tacticalShift':
        q = tags.shift === 'formation'
          ? (fi ? `Miksi ${t} vaihtoi muotoon ${tags.formationAfter}?` : `Why did ${t} switch to a ${tags.formationAfter}?`)
          : tags.shift === 'deeper'
            ? (fi ? `Onko ${t} vetäytynyt liian syvälle?` : `Is ${t} sitting too deep now?`)
            : (fi ? `Toimivatko ${t}n pitkät pallot?` : `Are ${t}'s long balls working?`);
        break;
      case 'playerRole': {
        const r = tags.role ? roleLabel[tags.role] : undefined;
        q = r ? (fi ? `Miten ${player(c.playerId)} pelaa roolissa ${r.fi.toLowerCase()}?` : `What does ${player(c.playerId)} do as a ${r.en.toLowerCase()}?`) : undefined;
        break;
      }
      case 'pressingShift': q = fi ? `Toimiiko ${t}n prässi oikeasti?` : `Is ${t}'s press really working?`; break;
      case 'possessionIllusion': q = fi ? `Miksi ${t} ei saa pallonhallinnasta paikkoja?` : `Why can't ${t} turn possession into chances?`; break;
      case 'territory': q = fi ? `Miten ${t} otti hyökkäyskolmanneksen haltuunsa?` : `How did ${t} take over the final third?`; break;
      case 'betterPass': q = fi ? `Mitä parempia syöttöjä ${player(c.playerId)} jätti käyttämättä?` : `What better passes did ${player(c.playerId)} miss?`; break;
      case 'goal':
      case 'turnoverToChance': q = c.facts.goal === 1 ? (fi ? `Miten ${clock(c.detectedAtSec)} maali syntyi?` : `How did the goal at ${clock(c.detectedAtSec)} happen?`) : undefined; break;
    }
    if (q && !out.includes(q)) out.push(q);
  }
  const p = player(playerId);
  if (p) out.push(fi ? `Miten ${p} on pelannut?` : `How is ${p} playing?`);
  else if (teamId) out.push(fi ? `Miten ${team(teamId)} voisi kääntää pelin?` : `What should ${team(teamId)} change?`);
  out.push(fi ? 'Kumpi joukkue hallitsee nyt?' : 'Who is on top right now?');
  out.push(fi ? 'Vertaa viimeistä 10 minuuttia sitä edeltäviin 10 minuuttiin.' : 'Compare the last 10 minutes with the 10 before.');
  return out.slice(0, 4);
}
