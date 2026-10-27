import { PassInfoContext } from './spatial';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgentDesk } from './components/AgentDesk';
import { EditorQueue, GroundTruth, OnAir } from './components/Feed';
import { LowerThird } from './components/LowerThird';
import { kitVars, useKitOnRoot } from './kit';
import { Momentum } from './components/Momentum';
import { NewMatch } from './components/NewMatch';
import { Pitch } from './components/Pitch';
import { RecapCard } from './components/RecapCard';
import { NeedsReview, OnAirPreview, OutputControls, Panel, StudioBar, StudioNav, type StudioTab } from './components/StudioParts';
import { FluentProvider, Tab, TabList } from '@fluentui/react-components';
import { CoachCorner } from './components/Coach';
import { arenaTheme } from './theme';
import { CallTicker, GoalBurst } from './components/Commentary';
import { PredictionList, PredictionOverlay, TrackRecord } from './components/Predictions';
import { TestCard, TestRecord } from './components/TacticalTest';
import { FaultDrill } from './components/FaultDrill';
import { AutoEvents } from './components/AutoEvents';
import { LedTicker } from './components/LedTicker';
import { PlayerCard, PlayersPanel } from './components/Players';
import { comparisonFor, rewindEvents, RewindBar, useRewind } from './components/Rewind';
import { HeatLayer, ShapeBar, ShapeDefs, ShapeLayer, TacticsPanel, useShiftCompare } from './components/Tactics';
import { kindLabel, clock } from './format';
import { useArrivals } from './hooks/useArrivals';
import { AskPanel } from './components/Ask';
import { useMatch } from './store';
import { callUtterance, insightUtterance, recapUtterance, useVoice } from './voice';
import type { InsightView } from './types';
import { TAKE, useAutopilot } from './take/autopilot';
import { studioTake2 } from './take/scripts';

// Recording take 2 (/judge?take=2 restarts the demo match and opens /?take=2&started=1): the studio plays itself.
// Only on the studio's own page: this module also loads under /judge, where the redirect would loop.
const ON_STUDIO = !['/judge', '/fan', '/overlay'].some((p) => window.location.pathname.startsWith(p));
const STUDIO_TAKE = TAKE === 2 && ON_STUDIO;
if (STUDIO_TAKE && !new URLSearchParams(window.location.search).has('started')) window.location.replace('/judge?take=2');

type BoardMode = 'live' | 'shape' | 'heat';

export function App() {
  const { state, connection } = useMatch();
  const { info, tick } = state;
  const [persona, setPersona] = useState('Casual-en');
  const [tab, setTab] = useState<StudioTab>('air');
  const [selected, setSelected] = useState<string>();
  const [choosing, setChoosing] = useState(false);
  const takeCues = useMemo(() => (STUDIO_TAKE ? studioTake2() : undefined), []);
  useAutopilot(takeCues, tick?.clockSec, info?.matchId ?? '');

  // Insights go to air in the order they were published; each shows for its display time.
  const onAir = useMemo(
    () => state.insights.filter((i) => i.personaKey === persona && (i.status === 'live' || i.status === 'approved')),
    [state.insights, persona],
  );
  const [showing, setShowing] = useState<InsightView>();
  const queue = useRef<InsightView[]>([]);
  const seen = useRef<Set<string>>(new Set());
  const primed = useRef<string>();

  useEffect(() => {
    // On load or persona switch, what is already on air is history, not news.
    const key = `${info?.matchId}/${persona}`;
    if (primed.current !== key) {
      primed.current = key;
      seen.current = new Set(onAir.map((i) => i.insight.insightId));
      queue.current = [];
      setShowing(undefined);
      // The preview's "last graphic" is then the same moment as written for the new persona.
      setLastShown(undefined);
      return;
    }
    for (const i of onAir) {
      if (!seen.current.has(i.insight.insightId)) {
        seen.current.add(i.insight.insightId);
        queue.current.push(i);
      }
    }
    setShowing((current) => current ?? queue.current.shift());
  }, [onAir, info?.matchId, persona]);

  const next = useCallback(() => setShowing(queue.current.shift()), []);
  // When the graphic on screen started, for the preview's countdown; and the last one that aired.
  const [shownAt, setShownAt] = useState<number>();
  const [lastShown, setLastShown] = useState<InsightView>();
  useEffect(() => { if (showing) { setShownAt(Date.now()); setLastShown(showing); } }, [showing]);

  // Commentary: each graphic is read aloud as it goes to air, when the voice is on.
  const voice = useVoice();
  const { say } = voice;
  useEffect(() => {
    if (showing) say(insightUtterance(showing.insight.insightId, `${showing.insight.headline}. ${showing.insight.summary}`, showing.insight.language));
  }, [showing, say]);

  useEffect(() => { if (info && !info.personas.includes(persona)) setPersona(info.personas[0]); }, [info, persona]);

  // The commentator: every call is spoken (goals cut in), in the persona's language.
  const fi = persona.endsWith('-fi');
  const matchKey = info?.matchId ?? '';
  useArrivals(state.calls, (c) => c.callId, matchKey, (c) =>
    say(callUtterance(c.callId, fi ? c.textFi : c.text, fi ? 'fi' : 'en', c.intensity)));

  const [player, setPlayer] = useState<string>();

  // Moments waiting for the editor (one moment can hold a draft per persona).
  const pending = new Set(state.insights.filter((i) => i.status === 'pending').map((i) => i.candidateId)).size;
  const chosen = state.candidates.find((x) => x.candidateId === selected);
  const highlight = useMemo(() => (chosen ? new Set(chosen.evidenceEventIds) : undefined), [chosen]);

  // Tactical Rewind: selecting a card on the desk or a line on air replays its move on the board. Shape moments
  // (a tactical shift, a player's role) show the shape instead: before and after, or the player's heat map.
  const shapeMoment = chosen && (chosen.kind === 'tacticalShift' || chosen.kind === 'playerRole') ? chosen : undefined;
  const moves = useMemo(() => (chosen && !shapeMoment ? rewindEvents(state.events, chosen.evidenceEventIds) : []),
    [chosen, shapeMoment, state.events]);
  const rewind = useRewind(moves);
  const rewinding = moves.length > 0 && !!chosen;
  // A finished replay hands the board back to live after 8 seconds, unless someone scrubs or plays it again.
  useEffect(() => {
    if (!rewinding || rewind.playing || rewind.step < moves.length - 1) return;
    const t = window.setTimeout(() => setSelected(undefined), 8000);
    return () => window.clearTimeout(t);
  }, [rewinding, rewind.playing, rewind.step, moves.length]);
  const pair = useShiftCompare(shapeMoment);
  const [boardMode, setBoardMode] = useState<BoardMode>('live');
  const [heatTeam, setHeatTeam] = useState<'home' | 'away'>('home');

  const kit = info ? kitVars(info.match) : undefined;
  useKitOnRoot(info?.match);

  const showPicker = choosing || (!info && connection === 'connected');

  return (
    <PassInfoContext.Provider value={state.passes}>
    <div className="app studio" style={kit}>
      <FluentProvider theme={arenaTheme} style={{ background: 'transparent' }} className="sbar-provider">
        <StudioBar info={info} tick={tick} connection={connection} onNewMatch={() => setChoosing(true)} />
        {info && <LedTicker calls={state.calls} tests={state.tests} info={info.match} clockSec={tick?.clockSec} />}
      </FluentProvider>

      <div className="studio-body">
        {info && tick && <StudioNav tab={tab} onTab={setTab} pending={pending} predictions={trackRecord(state.predictions)}
          openTests={state.tests.filter((t) => t.status === 'open').length} />}

        <main className="studio-main">
          {showPicker || !info || !tick ? (
            <div className="board-pane">
              {connection === 'connected' || choosing
                ? <NewMatch onStarted={() => setChoosing(false)} onCancel={info ? () => setChoosing(false) : undefined} />
                : <p className="empty">Connecting to the Second Ball IQ server…</p>}
            </div>
          ) : (
            <>
              <div className="studio-top">
                <Panel title="Live match board" className="board-panel"
                  aside={
                    <FluentProvider theme={arenaTheme} style={{ background: 'transparent' }}>
                      <TabList size="small" selectedValue={chosen ? '' : boardMode} onTabSelect={(_, d) => { setSelected(undefined); setBoardMode(d.value as BoardMode); }}>
                        <Tab value="live">Live</Tab><Tab value="shape">Shape</Tab><Tab value="heat">Heat map</Tab>
                      </TabList>
                    </FluentProvider>
                  }>
                  <section className="board" aria-label="Match">
                    <div className="pitch-wrap">
                      <Pitch events={state.events} clockSec={tick.clockSec} info={info.match} highlight={highlight} carrier tags={state.tags}
                        rewind={rewinding ? { events: moves, step: rewind.step } : undefined}>
                        {shapeMoment ? (
                          <>
                            <ShapeDefs />
                            {shapeMoment.kind === 'playerRole' && (
                              <HeatLayer events={state.events} playerId={shapeMoment.playerId} fromSec={shapeMoment.fromSec} toSec={shapeMoment.toSec + 1}
                                color={shapeMoment.teamId === info.match.home.teamId ? 'var(--home)' : 'var(--away)'} />
                            )}
                            {pair && <ShapeLayer reports={[pair.after]} ghost={[pair.before]} info={info.match} fi={fi} />}
                            {shapeMoment.kind === 'playerRole' && (
                              <ShapeLayer reports={state.shapes.filter((r) => r.teamId === shapeMoment.teamId)} info={info.match} fi={fi}
                                highlight={shapeMoment.playerId} />
                            )}
                          </>
                        ) : boardMode === 'shape' ? (
                          <><ShapeDefs /><ShapeLayer reports={state.shapes} info={info.match} fi={fi} onPlayer={setPlayer} /></>
                        ) : boardMode === 'heat' ? (
                          <>
                            <ShapeDefs />
                            <HeatLayer events={state.events} teamId={heatTeam === 'home' ? info.match.home.teamId : info.match.away.teamId}
                              fromSec={tick.clockSec - 15 * 60} toSec={tick.clockSec + 1} color={heatTeam === 'home' ? 'var(--home)' : 'var(--away)'} />
                          </>
                        ) : null}
                      </Pitch>
                      {!rewinding && !shapeMoment && <CallTicker calls={state.calls} fi={fi} matchKey={matchKey} />}
                      <PredictionOverlay predictions={state.predictions} info={info.match} fi={fi} matchKey={matchKey} onPlayer={setPlayer} />
                      <GoalBurst calls={state.calls} info={info.match} fi={fi} matchKey={matchKey} />
                      {!rewinding && !shapeMoment && <LowerThird item={showing} info={info.match} onDone={next} />}
                    </div>
                    {shapeMoment && <ShapeBar c={shapeMoment} pair={pair} info={info.match} fi={fi} onClose={() => setSelected(undefined)} />}
                    {rewinding && chosen && (
                      <RewindBar moves={moves} info={info.match} rewind={rewind} fi={fi}
                        title={`${kindLabel[chosen.kind]} · ${clock(moves[0].clockSec)}–${clock(moves[moves.length - 1].clockSec)}`}
                        comparison={comparisonFor(chosen.kind, chosen.facts, fi)}
                        onClose={() => setSelected(undefined)} />
                    )}
                    <Momentum events={state.events} info={info.match} endClockSec={info.endClockSec} onAir={onAir} />
                    <p className="legend">
                      <span className="lg lg-home">{info.match.home.name}</span>
                      <span className="lg lg-away">{info.match.away.name}</span>
                      <span className="lg lg-shot">Shot, sized by xG</span>
                      <span className="lg lg-rec">High regain</span>
                      <span className="lg lg-cite">Cited evidence</span>
                      {info.attribution && <span className="credit-board">{info.attribution}</span>}
                    </p>
                    {boardMode !== 'live' && !chosen && (
                      <div className="board-mode">
                        {boardMode === 'heat' && (
                          <span className="board-mode-teams">
                            {(['home', 'away'] as const).map((t) => (
                              <button key={t} type="button" aria-pressed={heatTeam === t} onClick={() => setHeatTeam(t)}>
                                {t === 'home' ? info.match.home.shortName : info.match.away.shortName}
                              </button>
                            ))}
                          </span>
                        )}
                        <span className="board-mode-note">
                          {boardMode === 'shape' ? 'Average positions, last 15 minutes. Yellow ring: a distinctive role.' : 'Where the team had the ball, last 15 minutes.'}
                        </span>
                      </div>
                    )}
                  </section>
                </Panel>

                <div className="studio-side">
                  <OnAirPreview showing={showing} shownAt={shownAt} last={lastShown ?? onAir[onAir.length - 1]} info={info.match} queued={queue.current.length}
                    controls={<OutputControls personas={info.personas} persona={persona} onPersona={setPersona} voiceOn={voice.on} onVoice={voice.toggle} />} />
                  <NeedsReview insights={state.insights} info={info.match} persona={persona} onReview={() => setTab('editor')}
                    onTrace={(cid) => { setSelected(cid); setTab('trace'); }} />
                  <Panel title="Agents at work" className="agents-panel"
                    aside={<span className="muted">{state.candidates.length} flagged</span>}>
                    <AgentDesk candidates={state.candidates} info={info.match} selected={selected} onSelect={setSelected}
                      limit={3} onMore={() => setTab('trace')} />
                  </Panel>
                </div>
              </div>

              <section className="lower" aria-label={SECTION_TITLES[tab]}>
                <h2 className="lower-title">{SECTION_TITLES[tab]}</h2>
              {tab === 'air' && (
                <>
                  {state.recaps.filter((r) => r.personaKey === persona).sort((a, b) => Number(b.fullTime) - Number(a.fullTime))
                    .map((r) => (
                      <RecapCard key={r.recapId} recap={r} info={info.match} fi={persona.endsWith('-fi')}
                        onListen={() => voice.sayNow(recapUtterance(r.recapId, [r.headline, ...r.paragraphs].join(' '), persona.endsWith('-fi') ? 'fi' : 'en'))} />
                    ))}
                  <OnAir insights={onAir} info={info.match} onSelect={setSelected} />
                </>
              )}
              {tab === 'tests' && (
                state.tests.length === 0
                  ? <p className="muted">When a pressing, possession or tactical read goes to air, its test is locked here: a measurable criterion, a deadline and Decision-1's estimate. The match settles it.</p>
                  : (
                    <>
                      <TestRecord tests={state.tests} />
                      <div className="test-list wide">
                        {[...state.tests].reverse().map((t) => <TestCard key={t.testId} test={t} clockSec={tick.clockSec} events={state.events} />)}
                      </div>
                    </>
                  )
              )}
              {tab === 'coach' && (
                <CoachCorner info={info.match} coaches={state.coaches} tests={state.tests} events={state.events} clockSec={tick.clockSec} fi={fi}
                  live={tick.status === 'running' || tick.status === 'paused'} />
              )}
              {tab === 'predictions' && <PredictionList predictions={state.predictions} info={info.match} fi={fi} onPlayer={setPlayer} />}
              {tab === 'ask' && (
                <AskPanel info={info.match} events={state.events} candidates={state.candidates} clockSec={tick.clockSec}
                  audience={persona.split('-')[0].toLowerCase()} language={fi ? 'fi' : 'en'} onPlayer={setPlayer} />
              )}
              {tab === 'tactics' && (
                <TacticsPanel shapes={state.shapes} events={state.events} info={info.match} clockSec={tick.clockSec} fi={fi} onPlayer={setPlayer} />
              )}
              {tab === 'players' && <PlayersPanel info={info.match} events={state.events} fi={fi} onPlayer={setPlayer} running={state.running} />}
              {tab === 'editor' && <EditorQueue insights={state.insights} info={info.match} />}
              {tab === 'trace' && <AgentDesk candidates={state.candidates} info={info.match} selected={selected} onSelect={setSelected} />}
              {tab === 'truth' && (
                <FaultDrill drills={state.drills} armed={state.armed} onTrace={(cid) => { setSelected(cid); setTab('trace'); }} />
              )}
              {tab === 'truth' && <AutoEvents view={state.autoEvents} info={info.match} />}
              {tab === 'truth' && (
                <GroundTruth expectations={state.groundTruth} candidates={state.candidates} insights={state.insights}
                  tick={tick} info={info.match} />
              )}
              </section>
            </>
          )}
        </main>
      </div>

      {info && player && <PlayerCard key={player} playerId={player} info={info.match} events={state.events} shapes={state.shapes} fi={fi} onClose={() => setPlayer(undefined)} running={state.running} />}
    </div>
    </PassInfoContext.Provider>
  );
}

function trackRecord(predictions: Parameters<typeof TrackRecord>[0]['predictions']): string {
  const settled = predictions.filter((p) => p.correct !== undefined && p.correct !== null);
  return settled.length ? `${settled.filter((p) => p.correct).length}/${settled.length}` : '';
}

const SECTION_TITLES: Record<StudioTab, string> = {
  air: 'On air', tests: 'Tactical tests: locked before, settled by the match', coach: "Coach's corner: two plans, Decision-1 picks, the match decides", trace: 'Agent trace', editor: 'Editor queue', truth: 'Evaluation against ground truth', tactics: 'Tactics',
  players: 'Players', predictions: 'Predictions', ask: 'Ask Second Ball IQ',
};
