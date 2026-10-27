import { SpacePanel } from './components/Space';
import { CoachCorner } from './components/Coach';
import { PassInfoContext } from './spatial';
import { useEffect, useMemo, useRef, useState } from 'react';
import { TAKE, useAutopilot } from './take/autopilot';
import { fanTake1, fanTake3 } from './take/scripts';
import { Button, FluentProvider, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { RecapCard } from './components/RecapCard';
import { askUtterance, callUtterance, insightUtterance, recapUtterance, useVoice } from './voice';
import { useMatch } from './store';
import { LedTicker } from './components/LedTicker';
import { Momentum } from './components/Momentum';
import { PassQualityPanel } from './components/PassQuality';
import { GoalBurst } from './components/Commentary';
import { PredictionCard, PredictionList, PredictionOverlay, TrackRecord } from './components/Predictions';
import { TestCard, TestRecord, testFor } from './components/TacticalTest';
import { MatchStage, StageProvider, type StageVisual } from './components/Stage';
import { Pinned } from './components/Pinned';
import { Pitch } from './components/Pitch';
import { PlayerCard, PlayersPanel } from './components/Players';
import { defaultTactics, HeatLayer, ShapeDefs, ShapeLayer, TacticsPanel, type TacticsState } from './components/Tactics';
import { useArrivals } from './hooks/useArrivals';
import { Setup } from './components/Setup';
import { AskPanel, suggest } from './components/Ask';
import { useParty } from './party/useParty';
import { PartyLobby, PartyReactions, PartyRoom } from './components/Party';
import { PlayerAlerts, PlayerSpotlight } from './components/Focus';
import { ConceptCard, LearnChips, LearnTab } from './components/Learn';
import { DuelStats, useScorers } from './components/MatchCentre';
import {
  AskBox, FanBottomNav, FanHeader, FollowPlayer, KeyMoment, MomentDrawer, MomentRow, ScreenTitle, Section, SidePanel,
  type View,
} from './components/FanParts';
import { audienceFor, loadPrefs, savePrefs, type Prefs } from './fan/prefs';
import { conceptById, conceptsFor, conceptsForPrediction } from './fan/glossary';
import { kitVars, useKitOnRoot } from './kit';
import { arenaTheme, useWide } from './theme';
import type { InsightView, MatchInfo, Prediction, ShapeReport } from './types';

/**
 * The fan's companion to the match. Watch on TV; this shows what is happening underneath it, written the way you
 * like to read football, in your language, around your club or your player. The story first; the evidence and the
 * machinery one tap away. Only what went to air is shown.
 */
/** Until this match second (55:00) the half-time recap leads the live view. */
const RECAP_LEADS_UNTIL = 55 * 60;

type AnalysisTab = 'shape' | 'heat' | 'possession' | 'passes' | 'space' | 'coach' | 'tests';

export function FanApp() {
  const { state, connection, hub } = useMatch();
  const party = useParty(hub, connection === 'connected');
  const { info, tick } = state;
  const [prefs, setPrefsState] = useState<Prefs>(loadPrefs);
  const setPrefs = (p: Prefs) => { setPrefsState(p); savePrefs(p); };
  const [settings, setSettings] = useState(false);
  const wide = useWide();

  // Each persona the server writes for is "<Audience>-<language>"; pick the closest one it actually has.
  const personaKey = useMemo(() => {
    const want = `${cap(audienceFor(prefs))}-${prefs.language}`;
    if (!info) return want;
    const fallback = audienceFor(prefs) === 'beginner' ? `Casual-${prefs.language}` : undefined;
    return info.personas.includes(want) ? want
      : fallback && info.personas.includes(fallback) ? fallback
      : info.personas.find((p) => p.endsWith(`-${prefs.language}`)) ?? info.personas[0];
  }, [info, prefs]);
  const fi = prefs.language === 'fi';
  const voice = useVoice();
  // The watch party is its own destination (Room); an invite link or a party preference opens straight into it.
  const [view, setView] = useState<View>(() => (new URLSearchParams(window.location.search).get('party') || loadPrefs().social === 'party'
    ? 'room' : loadPrefs().ask ? 'ask' : 'live'));
  const [glossaryOpen, setGlossaryOpen] = useState(false);
  const [introClosed, setIntroClosed] = useState(false);
  // Judge mode (/judge → /fan?judge=1): no setup, a demo watch party, and a short guide to what to watch for.
  const [judge] = useState(() => new URLSearchParams(window.location.search).has('judge'));
  const [judgeClosed, setJudgeClosed] = useState(false);
  const judgeParty = useRef(false);
  useEffect(() => {
    if (!judge || judgeParty.current || connection !== 'connected' || party.state || party.saved) return;
    judgeParty.current = true;
    void party.create(fi ? 'Tuomari' : 'Judge', undefined, true);
  }, [judge, connection, party, fi]);
  const [player, setPlayer] = useState<string>();
  const [opened, setOpened] = useState<{ id: string; replay: boolean }>();
  const [askSeed, setAskSeed] = useState<{ id: number; question: string; about?: string }>();
  // The match stage: something put on the pitch to look at (a replay, an answer's evidence), the Analysis layer,
  // and the player picked in Players.
  const [visual, setVisual] = useState<StageVisual>();
  const [tactics, setTactics] = useState<TacticsState>(defaultTactics);
  const [aTab, setATab] = useState<AnalysisTab>('shape');
  // The LED ticker under the score can be hidden; the choice is remembered on this device.
  const [tickerOn, setTickerOn] = useState(() => { try { return localStorage.getItem('ml-ticker') !== 'off'; } catch { return true; } });
  const toggleTicker = () => setTickerOn((on) => { try { localStorage.setItem('ml-ticker', on ? 'off' : 'on'); } catch { /* private mode */ } return !on; });
  const [stagePlayer, setStagePlayer] = useState<string>();
  const matchKey = info?.matchId ?? '';
  const myTeam = prefs.focus === 'club' || prefs.focus === 'player' ? prefs.team : '';
  const myPlayer = prefs.focus === 'player' ? prefs.player : '';

  // Recording takes (/judge?take=1 or 3): the page plays the take by itself at the right match times.
  const infoRef = useRef(info);
  infoRef.current = info;
  const takeCues = useMemo(() => (TAKE === 1 ? fanTake1() : TAKE === 3 ? fanTake3(() => infoRef.current?.match) : undefined), []);
  useAutopilot(takeCues, tick?.clockSec, matchKey);
  const onAirTake = TAKE === 3;

  // The commentator's calls are read out too, ahead of the analysis; a goal cuts in. Following one club or one
  // player, only their calls are spoken, plus every goal and the whistles.
  useArrivals(state.calls, (c) => c.callId, `${matchKey}/${prefs.language}`, (c) => {
    const always = c.kind === 'goal' || c.kind === 'kickOff' || c.kind === 'halfTime' || c.kind === 'fullTime';
    if (myPlayer && !always && c.playerId !== myPlayer) return;
    if (myTeam && !myPlayer && !always && c.teamId !== myTeam) return;
    voice.say(callUtterance(c.callId, fi ? c.textFi : c.text, prefs.language, c.intensity));
  });


  const [bothTeams, setBothTeams] = useState(false);
  const feed = useMemo(
    () => state.insights
      .filter((i) => i.personaKey === personaKey && (i.status === 'live' || i.status === 'approved'))
      .filter((i) => prefs.focus !== 'club' || !myTeam || bothTeams || i.insight.kind === 'goal' || i.insight.teamIds.includes(myTeam))
      .sort((a, b) => b.insight.matchClockSec - a.insight.matchClockSec),
    [state.insights, personaKey, prefs.focus, myTeam, bothTeams],
  );

  // Learn the Game: which concepts have come up in this match so far.
  const seenConcepts = useMemo(() => {
    const ids = new Set<string>(['basics']);
    for (const v of feed) for (const id of conceptsFor(v.insight.kind, state.candidates.find((c) => c.candidateId === v.candidateId))) ids.add(id);
    for (const p of state.predictions) for (const id of conceptsForPrediction(p.kind)) ids.add(id);
    return ids;
  }, [feed, state.candidates, state.predictions]);
  const recaps = state.recaps.filter((r) => r.personaKey === personaKey).sort((a, b) => Number(b.fullTime) - Number(a.fullTime));

  // The newest card gets one brief highlight when it arrives; history on load does not.
  const seen = useRef<{ key: string; ids: Set<string> }>();
  const [fresh, setFresh] = useState<string>();
  useEffect(() => {
    const ids = feed.map((i) => i.insight.insightId);
    if (seen.current?.key !== personaKey) { seen.current = { key: personaKey, ids: new Set(ids) }; return; }
    const added = ids.find((id) => !seen.current!.ids.has(id));
    ids.forEach((id) => seen.current!.ids.add(id));
    if (added) {
      setFresh(added);
      const v = feed.find((i) => i.insight.insightId === added)!;
      voice.say(insightUtterance(added, `${v.insight.headline}. ${v.insight.summary}`, v.insight.language));
    }
  }, [feed, personaKey, voice.say]);

  // A recap is read aloud when it arrives, if the voice is on.
  const recapSeen = useRef<{ key: string; ids: Set<string> }>();
  useEffect(() => {
    const ids = recaps.map((r) => r.recapId);
    if (recapSeen.current?.key !== personaKey) { recapSeen.current = { key: personaKey, ids: new Set(ids) }; return; }
    for (const r of recaps) {
      if (recapSeen.current.ids.has(r.recapId)) continue;
      recapSeen.current.ids.add(r.recapId);
      voice.say(recapUtterance(r.recapId, [r.headline, ...r.paragraphs].join(' '), prefs.language));
    }
  }, [recaps, personaKey, voice.say, prefs.language]);

  // Switching style or language stops what is being read in the old one.
  const { stop } = voice;
  useEffect(() => { stop(); }, [personaKey, stop]);

  const kit = info ? kitVars(info.match) : undefined;
  useKitOnRoot(info?.match);
  const scorers = useScorers(state.events, info?.match ?? EMPTY_INFO);
  // Changing tab clears what was put on the stage, and any open moment.
  const go = (v: View) => {
    setView(v); setVisual(undefined); setOpened(undefined);
    window.scrollTo({ top: 0, behavior: 'smooth' });
    // On a wide screen the panel scrolls on its own: a new section starts at its top.
    document.querySelector('.fan-panel')?.scrollTo({ top: 0 });
  };
  // A player picked anywhere: in Players the stage shows them and the card sits in the panel; elsewhere the card opens over the page.
  const selectPlayer = (id: string) => { if (view === 'players') { setStagePlayer(id); setVisual(undefined); } else setPlayer(id); };
  const latestShapes = useMemo(() => (info ? [info.match.home.teamId, info.match.away.teamId]
    .map((t) => [...state.shapes].reverse().find((r) => r.teamId === t)).filter((r): r is ShapeReport => !!r) : []), [state.shapes, info]);

  const askAbout = (v: InsightView) => {
    setOpened(undefined);
    setVisual(undefined);
    setAskSeed({
      id: Date.now(),
      question: fi
        ? `Miksi näin kävi: "${v.insight.headline}" (${clockText(v.insight.matchClockSec)})? Mitkä luvut ja pelaajat liittyvät siihen?`
        : `Why did this happen: "${v.insight.headline}" (${clockText(v.insight.matchClockSec)})? Which numbers and players were involved?`,
      about: `${clockText(v.insight.matchClockSec)} · ${v.insight.headline}`,
    });
    go('ask');
  };
  const askText = (q: string) => { setAskSeed({ id: Date.now(), question: q }); go('ask'); };

  const depth = { quick: fi ? 'Lyhyt' : 'Quick', explain: fi ? 'Selitä' : 'Explain', tactical: fi ? 'Taktinen' : 'Tactical' }[prefs.depth];
  const styleLabel = `${prefs.language.toUpperCase()} · ${depth}${prefs.learn ? (fi ? ' · Opi' : ' · Learn') : ''}`;
  const connectionText = connection === 'connected' ? (fi ? 'Ei ottelua käynnissä' : 'No match on right now') : (fi ? 'Yhdistetään…' : 'Connecting…');

  const latest = feed[0];
  const earlier = feed.slice(latest ? 1 : 0);
  const openedView = opened ? feed.find((v) => v.insight.insightId === opened.id) ?? state.insights.find((v) => v.insight.insightId === opened.id) : undefined;
  const suggestion = useMemo(() => (info ? suggest(info.match, state.candidates, fi, myPlayer || undefined, myTeam || undefined)[0] : undefined),
    [info, state.candidates, fi, myPlayer, myTeam]);

  // The stage: what is drawn on the pitch for this tab, unless something was put on it to look at.
  const stageBase = info && tick ? (
    view === 'players' ? (
      <Pitch events={state.events} clockSec={tick.clockSec} info={info.match} compact
        label={fi ? 'Pelaajien sijainnit' : 'Where the players are'}>
        <ShapeDefs />
        {stagePlayer && (
          <HeatLayer events={state.events} playerId={stagePlayer} fromSec={0} toSec={tick.clockSec + 1}
            color={info.match.home.players.some((p) => p.playerId === stagePlayer) ? 'var(--home)' : 'var(--away)'} />
        )}
        <ShapeLayer reports={latestShapes} info={info.match} fi={fi} highlight={stagePlayer} onPlayer={selectPlayer} />
      </Pitch>
    ) : (
      <Pitch events={state.events} clockSec={tick.clockSec} info={info.match} compact carrier fi={fi} tags={state.tags} focusPlayer={myPlayer || undefined}
        label={fi ? 'Pallo ja viimeiset tilanteet' : 'The ball and the last moves'} />
    )
  ) : null;

  // The stage sticks under the header, whose height changes with the screen and the score line.
  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const root = rootRef.current, head = root?.querySelector('.fh-provider');
    if (!root || !head) return;
    const ro = new ResizeObserver(() => root.style.setProperty('--fh-h', `${Math.round(head.getBoundingClientRect().height)}px`));
    ro.observe(head);
    return () => ro.disconnect();
  }, []);

  const pinned = info && tick ? (
    <Pinned party={party.state ? party : undefined} predictions={state.predictions} tests={state.tests} info={info.match} fi={fi}
      clockSec={tick.clockSec} events={state.events} onPlayer={selectPlayer} />
  ) : null;

  return (
    <PassInfoContext.Provider value={state.passes}>
    <StageProvider value={{ show: setVisual, current: visual?.key }}>
    <div ref={rootRef} className={`fan arena-fan ${wide ? 'fan-wide' : 'fan-narrow'} ${onAirTake ? 'take-air' : ''}`} style={kit}>
      {/* Take 3: the broadcast overlay, transparent, over the full-window match picture, as a vision mixer would key it. */}
      {onAirTake && <iframe className="take-overlay" src="/overlay" title="Broadcast graphics" />}
      <FluentProvider theme={arenaTheme} style={{ background: 'transparent' }} className="fh-provider">
        <FanHeader info={info?.match} tick={tick} scorers={scorers} myTeam={myTeam} fi={fi} styleLabel={styleLabel}
          connectionText={connectionText} voiceOn={voice.on} onVoice={voice.toggle} partyCode={party.state?.code}
          onParty={() => go('room')} onPersonalise={() => setSettings(true)} onGlossary={() => setGlossaryOpen(true)}
          tickerOn={tickerOn} onTicker={toggleTicker}
          ticker={info && tick ? <LedTicker calls={state.calls} tests={state.tests} info={info.match} fi={fi} clockSec={tick.clockSec} /> : undefined} />
      </FluentProvider>

      {((!prefs.setupDone && !judge) || settings) && (
        <Setup prefs={prefs} info={info?.match} first={!prefs.setupDone}
          onCancel={prefs.setupDone ? () => setSettings(false) : undefined}
          onDone={(p, listen) => {
            setPrefs(p);
            setSettings(false);
            if (p.social === 'party' && !party.state) go('room');
            // The button press is the user gesture browsers need before playing audio.
            if (listen && !voice.on) voice.toggle();
            if (!listen && voice.on) voice.toggle();
          }} />
      )}

      <div className="fan-shell">
        {info && tick ? (
          <MatchStage info={info.match} tick={tick} party={party} roomMode={view === 'room' && !!party.state}
            fi={fi} base={stageBase} visual={visual} onBack={() => setVisual(undefined)}
            wide={wide} liveBase={view !== 'players'} events={state.events} calls={state.calls} tags={state.tags} running={state.running}
            pinned={pinned} focusPlayer={myPlayer || undefined}
            overlay={<PredictionOverlay predictions={state.predictions} info={info.match} fi={fi} matchKey={matchKey} onPlayer={selectPlayer} />} />
        ) : (
          <section className="stage stage-empty"><p className="fan-empty">{connectionText}</p></section>
        )}

        <main className="fan-panel">
          {connection !== 'connected' && info && (
            <MessageBar intent="warning"><MessageBarBody>{fi ? 'Yhteys katkesi, yhdistetään uudelleen…' : 'Connection lost, reconnecting…'}</MessageBarBody></MessageBar>
          )}

          {info && tick && view === 'live' && (
            openedView ? (
              <MomentDrawer inline v={openedView} candidate={state.candidates.find((c) => c.candidateId === openedView.candidateId)}
                info={info.match} events={state.events} shapes={state.shapes} fi={fi} wide={wide} learn={prefs.learn} mine={myTeam}
                replayFirst={!!opened?.replay} onClose={() => setOpened(undefined)} onAsk={askAbout}
                test={testFor(state.tests, openedView.candidateId)} clockSec={tick.clockSec} />
            ) : (
            <>
              <ScreenTitle view="live" fi={fi} live={tick.status === 'running'} />
              {judge && !judgeClosed && (
                <aside className="judge-banner" aria-label={fi ? 'Tuomaritila' : 'Judge mode'}>
                  <div>
                    <strong>{fi ? 'Tuomaritila' : 'Judge mode'}</strong> · {fi ? 'esittelyottelu 38. minuutista, 8× nopeudella. Kaikki seurat ja pelaajat ovat keksittyjä.' : 'the demo match from 38′ at 8× speed. All clubs and players are fictional.'}
                    <ol>
                      <li>{fi ? 'Kun taktinen havainto menee ilmaan, sen testi lukitaan: mitattava ehto, takaraja ja Decision-1:n arvio.' : 'When a tactical read goes to air, its test locks: a measurable criterion, a deadline and Decision-1’s estimate.'}</li>
                      <li>{fi ? 'Katsomo äänestää, pitääkö se. Ottelun tapahtumat ratkaisevat tuloksen.' : 'The Room votes on whether it will hold. The match events settle it.'}{' '}
                        <button type="button" className="link" onClick={() => go('room')}>{fi ? 'Avaa katsomo' : 'Open the Room'}</button></li>
                      <li>{fi ? 'Studio näyttää agentit, tarkistukset ja testien tuloshistorian:' : 'The studio shows the agents, the checks and the test record:'}{' '}
                        <a href="/" target="_blank" rel="noreferrer">{fi ? 'avaa studio' : 'open the studio'}</a></li>
                    </ol>
                  </div>
                  <Button appearance="subtle" size="small" onClick={() => setJudgeClosed(true)}>{fi ? 'Sulje' : 'Close'}</Button>
                </aside>
              )}
              {pinned}
              {prefs.learn && prefs.level === 'beginner' && !introClosed && (
                <div className="intro-card"><ConceptCard c={conceptById.basics} fi={fi} onClose={() => setIntroClosed(true)} /></div>
              )}
              {latest ? (
                <KeyMoment v={latest} info={info.match} mine={myTeam} fresh={latest.insight.insightId === fresh} fi={fi}
                  onOpen={() => setOpened({ id: latest.insight.insightId, replay: false })}
                  onReplay={() => setOpened({ id: latest.insight.insightId, replay: true })}
                  onAsk={() => askAbout(latest)}
                  test={testFor(state.tests, latest.candidateId)} clockSec={tick.clockSec} events={state.events} />
              ) : (
                <p className="fan-empty">
                  {fi ? 'Kun ottelussa tapahtuu jotain merkittävää, se näkyy tässä muutamassa sekunnissa.' : 'When something in the match matters, it shows up here within seconds.'}
                </p>
              )}
              {latest && prefs.learn && <LearnChips ids={conceptsFor(latest.insight.kind, state.candidates.find((c) => c.candidateId === latest.candidateId))} fi={fi}
                onSeeIt={() => setOpened({ id: latest.insight.insightId, replay: true })} />}
              {prefs.focus === 'club' && myTeam && (
                <p className="focus-note">
                  {bothTeams ? (fi ? 'Näytetään molemmat joukkueet.' : 'Showing both teams.') : (fi ? 'Näytetään joukkueesi hetket ja kaikki maalit.' : "Showing your club's moments and every goal.")}
                  {' '}<button type="button" className="link" onClick={() => setBothTeams(!bothTeams)}>{bothTeams ? (fi ? 'Vain joukkueeni' : 'Only my club') : (fi ? 'Näytä molemmat' : 'Show both teams')}</button>
                </p>
              )}
              {/* The half-time recap leads during the break and the first minutes after it, then gives way to the
                  live moments and waits below them; the full-time recap always leads. */}
              {recaps.filter((r) => r.fullTime || tick.clockSec < RECAP_LEADS_UNTIL).map((r) => (
                <RecapCard key={r.recapId} recap={r} info={info.match} fi={fi}
                  onListen={() => voice.sayNow(recapUtterance(r.recapId, [r.headline, ...r.paragraphs].join(' '), prefs.language))} />
              ))}
              {(earlier.length > 0 || state.predictions.length > 0) && (
                <Section title={fi ? 'Aiemmin' : 'Earlier'}>
                  <div className="moment-list">
                    {mixed(earlier, state.predictions.filter((p) => p.outcome !== undefined && p.outcome !== null)).map((item) => 'insight' in item
                      ? <MomentRow key={item.insight.insightId} v={item} info={info.match} mine={myTeam} focusPlayer={myPlayer}
                          fresh={item.insight.insightId === fresh} fi={fi} onOpen={() => setOpened({ id: item.insight.insightId, replay: false })}
                          test={testFor(state.tests, item.candidateId)} />
                      : <PredictionCard key={item.predictionId} p={item} info={info.match} fi={fi} onPlayer={selectPlayer} />)}
                  </div>
                </Section>
              )}
              {recaps.filter((r) => !r.fullTime && tick.clockSec >= RECAP_LEADS_UNTIL).map((r) => (
                <RecapCard key={r.recapId} recap={r} info={info.match} fi={fi}
                  onListen={() => voice.sayNow(recapUtterance(r.recapId, [r.headline, ...r.paragraphs].join(' '), prefs.language))} />
              ))}
              {myPlayer && <FollowPlayer playerId={myPlayer} info={info.match} events={state.events} shapes={state.shapes} fi={fi} onOpen={() => go('players')} running={state.running} wide={wide} />}
              <AskBox suggestion={suggestion} fi={fi} onAsk={askText} />
            </>
            )
          )}

          {info && tick && view === 'room' && (
            openedView ? (
              <MomentDrawer inline v={openedView} candidate={state.candidates.find((c) => c.candidateId === openedView.candidateId)}
                info={info.match} events={state.events} shapes={state.shapes} fi={fi} wide={wide} learn={prefs.learn} mine={myTeam}
                replayFirst={!!opened?.replay} onClose={() => setOpened(undefined)} onAsk={askAbout}
                test={testFor(state.tests, openedView.candidateId)} clockSec={tick.clockSec} />
            ) : (
            <>
              <ScreenTitle view="room" fi={fi} live={tick.status === 'running'} />
              {party.state
                ? <PartyRoom party={party} info={info.match} events={state.events} fi={fi} onPlayer={selectPlayer}
                    feed={feed} onOpenMoment={(id) => setOpened({ id, replay: false })} pinned={pinned} />
                : <PartyLobby party={party} info={info.match} fi={fi} name={prefs.name} teamId={myTeam || undefined}
                    onName={(n) => setPrefs({ ...prefs, name: n })} />}
            </>
            )
          )}

          {info && tick && view === 'analysis' && (
            <>
              <ScreenTitle view="analysis" fi={fi} live={tick.status === 'running'} />
              <div className="an-tabs" role="tablist" aria-label={fi ? 'Analyysi' : 'Analysis'}>
                {([['shape', fi ? 'Muodostelmat' : 'Formations'], ['heat', fi ? 'Lämpökartat' : 'Heat maps'],
                   ['possession', fi ? 'Pallonhallinta' : 'Possession'], ['passes', fi ? 'Juoksu, syötöt, nopeus' : 'Running, passes, speed'], ['space', fi ? 'Tila ja uhka' : 'Space and threat'], ['coach', fi ? 'Valmentajien nurkka' : "Coach's corner"],
                   ['tests', fi ? 'Testit ja ennusteet' : 'Tests and calls']] as [AnalysisTab, string][])
                  .map(([k, l]) => (
                    <button key={k} type="button" role="tab" aria-selected={aTab === k} className={aTab === k ? 'on' : ''}
                      onClick={() => { setATab(k); if (k === 'shape' || k === 'heat') setTactics({ ...tactics, view: k }); }}>{l}</button>
                  ))}
              </div>
              {(aTab === 'shape' || aTab === 'heat') && (
                <div className="analysis-board">
                  <TacticsPanel shapes={state.shapes} events={state.events} info={info.match} clockSec={tick.clockSec} fi={fi} onPlayer={selectPlayer}
                    state={{ ...tactics, view: aTab }} onState={setTactics} board hideView />
                </div>
              )}
              {aTab === 'possession' && (
                <>
                  <Section title={fi ? 'Ottelun luvut' : 'Match numbers'}>
                    <DuelStats info={info.match} events={state.events} fi={fi} running={state.running} />
                  </Section>
                  <Section title={fi ? 'Pallonhallinta minuuteittain' : 'Possession minute by minute'}
                    aside={`${info.match.home.shortName} ↑ · ${info.match.away.shortName} ↓`}>
                    <div className="an-momentum">
                      <Momentum events={state.events} info={info.match} endClockSec={Math.max(info.endClockSec, tick.clockSec)} onAir={feed} />
                    </div>
                    <p className="fan-hint">{fi ? 'Palkit: kummalla oli pallo (onnistuneet syötöt) kunakin minuuttina. Pisteet viivalla ovat laukauksia, renkaat maaleja.' : 'Bars: who had the ball (completed passes) each minute. Dots on the line are shots, rings goals.'}</p>
                  </Section>
                </>
              )}
              {aTab === 'passes' && <PassQualityPanel info={info.match} events={state.events} fi={fi} running={state.running} />}
              {aTab === 'coach' && (
                <CoachCorner info={info.match} coaches={state.coaches} tests={state.tests} events={state.events} clockSec={tick.clockSec} fi={fi}
                  live={tick.status === 'running' || tick.status === 'paused'} />
              )}
              {aTab === 'space' && <SpacePanel info={info.match} events={state.events} passes={state.passes} xt={state.xt} fi={fi} clockSec={tick.clockSec} />}
              {aTab === 'tests' && (
                <>
                  {state.tests.length > 0 && (
                    <Section title={fi ? 'Taktiset testit' : 'Tactical tests'} aside={fi ? 'Ottelu ratkaisee' : 'The match decides'}>
                      <TestRecord tests={state.tests} fi={fi} />
                      <div className="test-list">
                        {[...state.tests].reverse().map((t) => <TestCard key={t.testId} test={t} clockSec={tick.clockSec} fi={fi} compact events={state.events} />)}
                      </div>
                    </Section>
                  )}
                  <Section title={fi ? 'Erikoistilanteet ja ennusteet' : 'Set pieces and predictions'}>
                    <TrackRecord predictions={state.predictions} fi={fi} />
                    {state.predictions.length === 0
                      ? <p className="fan-empty">{fi ? 'Ennen kulmapotkuja, vapaapotkuja ja rangaistuspotkuja näet tässä, minne pallo todennäköisesti menee.' : 'Before corners, free kicks and penalties you will see here where the ball is likely to go.'}</p>
                      : <PredictionList predictions={state.predictions} info={info.match} fi={fi} onPlayer={selectPlayer} />}
                  </Section>
                </>
              )}
            </>
          )}

          {info && view === 'players' && (
            <>
              <ScreenTitle view="players" fi={fi} />
              {stagePlayer ? (
                <PlayerCard key={stagePlayer} inline playerId={stagePlayer} info={info.match} events={state.events} shapes={state.shapes} fi={fi}
                  running={state.running}
                  onClose={() => setStagePlayer(undefined)} />
              ) : myPlayer ? (
                <PlayerSpotlight playerId={myPlayer} info={info.match} events={state.events} shapes={state.shapes} fi={fi} onOpen={() => selectPlayer(myPlayer)} running={state.running} wide={wide} />
              ) : (
                <p className="fan-hint">{fi ? 'Valitse pelaaja: kentällä näkyy, missä hän on ollut pallossa.' : 'Pick a player: the pitch shows where they have had the ball.'}</p>
              )}
              <Section title={fi ? 'Joukkueet' : 'Squads'}>
                <PlayersPanel info={info.match} events={state.events} fi={fi} onPlayer={selectPlayer} running={state.running} />
              </Section>
            </>
          )}

          {/* Ask stays mounted so the conversation survives a look at another section. */}
          {info && tick && (
            <div hidden={view !== 'ask'}>
              <ScreenTitle view="ask" fi={fi} />
              <AskPanel info={info.match} events={state.events} candidates={state.candidates} clockSec={tick.clockSec}
                audience={audienceFor(prefs)} language={prefs.language} teamId={myTeam || undefined} playerId={myPlayer || undefined}
                onPlayer={selectPlayer} seed={askSeed} bare
                onAnswer={(a) => voice.say(askUtterance(a.askId, a.answer, prefs.language))}
                onShare={party.state ? party.share : undefined} />
            </div>
          )}

          <footer className="fan-foot">
            {fi ? 'Jokainen luku on otteluanalytiikan mittaama ja tarkistettu ennen julkaisua. Kaikki seurat ja pelaajat ovat kuvitteellisia.'
              : 'Every number here was measured by the match engine and checked before it reached you. All clubs and players are fictional.'}
          </footer>
        </main>
      </div>

      {info && <FanBottomNav view={view} onView={go} fi={fi} />}

      <SidePanel open={glossaryOpen} title={fi ? 'Opi peli' : 'Learn the Game'} wide={wide} fi={fi} onClose={() => setGlossaryOpen(false)}>
        <LearnTab seen={seenConcepts} fi={fi} />
      </SidePanel>

      {info && myPlayer && <PlayerAlerts playerId={myPlayer} info={info.match} events={state.events} fi={fi} matchKey={matchKey} />}
      {/* Reactions float over every view while you are in a party. */}
      {party.state && <PartyReactions party={party} />}
      {info && <GoalBurst calls={state.calls} info={info.match} fi={fi} matchKey={matchKey} />}
      {info && player && view !== 'players' && (
        <PlayerCard key={player} playerId={player} info={info.match} events={state.events} shapes={state.shapes} fi={fi} onClose={() => setPlayer(undefined)}
          running={state.running} />
      )}
    </div>
    </StageProvider>
    </PassInfoContext.Provider>
  );
}

/** Insights and predictions in one feed, newest first. */
function mixed(feed: InsightView[], predictions: Prediction[]): (InsightView | Prediction)[] {
  const at = (x: InsightView | Prediction) => ('insight' in x ? x.insight.matchClockSec : x.clockSec);
  return [...feed, ...predictions].sort((a, b) => at(b) - at(a));
}

const clockText = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`;

const EMPTY_INFO: MatchInfo = {
  matchId: '', scenarioId: '', seed: 0, competition: '',
  home: { teamId: '', name: '', shortName: '', primaryColor: '#000000', players: [] },
  away: { teamId: '', name: '', shortName: '', primaryColor: '#000000', players: [] },
};

function cap(s: string) {
  return s.charAt(0).toUpperCase() + s.slice(1);
}
