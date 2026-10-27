import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  Badge, Button, Card, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, OverlayDrawer, DrawerBody, DrawerHeader,
  DrawerHeaderTitle, ToggleButton, Tooltip,
} from '@fluentui/react-components';
import {
  ArrowLeft16Regular, ArrowRight16Regular, Board24Filled, Board24Regular, BookOpen24Regular, Chat24Filled, Chat24Regular, ChevronRight20Regular,
  Dismiss24Regular, Live24Filled, Live24Regular, MoreHorizontal24Regular, News24Regular, People24Filled, People24Regular,
  PeopleCommunity24Filled, PeopleCommunity24Regular, Play24Regular, Send24Regular, Settings24Regular, ShieldCheckmark24Regular, Speaker224Regular,
  SpeakerOff24Regular, bundleIcon,
} from '@fluentui/react-icons';
import { clock, kindLabel, roleLabel } from '../format';
import { playerTeam, sideOf } from '../kit';
import { formatFigure } from './LowerThird';
import { Pitch } from './Pitch';
import { rewindEvents, RewindBar, useRewind } from './Rewind';
import { HeatLayer, ShapeBar, ShapeDefs, ShapeLayer, useShiftCompare } from './Tactics';
import { Shirt } from './Shirt';
import { LearnChips } from './Learn';
import { useMatchLines } from './Players';
import { conceptsFor } from '../fan/glossary';
import { TestCard, TestChip } from './TacticalTest';
import { Mark, Roll, Wordmark } from './Brand';
import { useStage } from './Stage';
import { RunningStrip, watchWithPlayerCam } from './Focus';
import type { CandidateView, InsightView, MatchInfo, PitchEvent, RunnerView, ShapeReport, TacticalTest, TickView } from '../types';

export type View = 'live' | 'room' | 'analysis' | 'players' | 'ask';

export const kindFi: Record<string, string> = {
  pressingShift: 'Prässi', possessionIllusion: 'Pallonhallinta', turnoverToChance: 'Riistosta paikka',
  playerMilestone: 'Pelaajan virstanpylväs', goal: 'Maali', tacticalShift: 'Taktinen muutos', playerRole: 'Pelaajan rooli',
  territory: 'Alueen hallinta', betterPass: 'Parempi syöttö', coachPlan: 'Valmentajan suunnitelma',
};
const kindText = (kind: string, fi?: boolean) => (fi ? kindFi[kind] : kindLabel[kind as keyof typeof kindLabel]) ?? kind;

const NAV: { id: View; en: string; fi: string; icon: ReturnType<typeof bundleIcon> }[] = [
  { id: 'live', en: 'Live', fi: 'Live', icon: bundleIcon(Live24Filled, Live24Regular) },
  { id: 'room', en: 'Room', fi: 'Katsomo', icon: bundleIcon(PeopleCommunity24Filled, PeopleCommunity24Regular) },
  { id: 'analysis', en: 'Analysis', fi: 'Analyysi', icon: bundleIcon(Board24Filled, Board24Regular) },
  { id: 'players', en: 'Players', fi: 'Pelaajat', icon: bundleIcon(People24Filled, People24Regular) },
  { id: 'ask', en: 'Ask', fi: 'Kysy', icon: bundleIcon(Chat24Filled, Chat24Regular) },
];

/** Keeps formations like 3-4-1-2 on one line in big headline type. */
function noBreak(text: string): ReactNode {
  const parts = text.split(/(\d(?:-\d)+)/);
  return parts.length === 1 ? text : parts.map((p, i) => (i % 2 ? <span key={i} className="nobr">{p}</span> : p));
}

/** The screen's title row: what you are looking at, and that the feed is live. */
export function ScreenTitle({ view, fi, aside, live }: { view: View; fi?: boolean; aside?: ReactNode; live?: boolean }) {
  const n = NAV.find((x) => x.id === view)!;
  return (
    <div className="screen-title">
      <h1>{fi ? n.fi : n.en}</h1>
      {aside}
      {live && <span className="diode-label live"><i aria-hidden="true" />{fi ? 'Suora syöte' : 'Feed live'}</span>}
    </div>
  );
}

/** The tabs: a bar at the bottom on every screen size (centred on desktop), with visible labels and thumb-sized targets. */
export function FanBottomNav({ view, onView, fi }: { view: View; onView: (v: View) => void; fi?: boolean }) {
  return (
    <nav className="fan-bottom" aria-label={fi ? 'Näkymät' : 'Sections'}>
      {NAV.map((n) => {
        const I = n.icon;
        const on = view === n.id;
        return (
          <button key={n.id} type="button" className={on ? 'on' : ''} aria-current={on ? 'page' : undefined} onClick={() => onView(n.id)}>
            <I filled={on} aria-hidden="true" />
            <span>{fi ? n.fi : n.en}</span>
          </button>
        );
      })}
    </nav>
  );
}

/** The navy match header: brand, live state, quick controls, and the score with both kits. */
export function FanHeader({ info, tick, scorers, myTeam, fi, styleLabel, connectionText, voiceOn, onVoice, partyCode,
  onParty, onPersonalise, onGlossary, ticker, tickerOn, onTicker }:
  { info?: MatchInfo; tick?: TickView; scorers: { home: string[]; away: string[] }; myTeam: string; fi: boolean; styleLabel: string;
    connectionText: string; voiceOn: boolean; onVoice: () => void; partyCode?: string; onParty: () => void; onPersonalise: () => void;
    onGlossary: () => void; ticker?: ReactNode; tickerOn?: boolean; onTicker?: () => void }) {
  const running = tick?.status === 'running';
  const finished = tick?.status === 'finished' || tick?.status === 'finishing';
  const period = tick ? (finished ? (fi ? 'Päättynyt' : 'Full time') : tick.period >= 2 ? (fi ? '2. puoliaika' : 'Second half') : (fi ? '1. puoliaika' : 'First half')) : '';
  return (
    <header className="fh">
      <div className="fh-row">
        <span className="fh-brand"><Mark /> <span className="fh-word"><Wordmark /></span></span>
        {running && <span className="diode-label live"><i aria-hidden="true" />{fi ? 'Live' : 'Live'}</span>}
        {tick?.status === 'paused' && <span className="diode-label paused"><i aria-hidden="true" />{fi ? 'Tauko' : 'Paused'}</span>}
        <span className="fh-style">{styleLabel}</span>
        <span className="fh-actions">
          {onTicker && (
            <Tooltip content={tickerOn ? (fi ? 'Piilota tapahtumanauha' : 'Hide the ticker') : (fi ? 'Näytä tapahtumanauha' : 'Show the ticker')} relationship="label">
              <ToggleButton appearance="subtle" checked={!!tickerOn} onClick={onTicker} icon={<News24Regular />}
                aria-label={fi ? 'Tapahtumanauha' : 'Ticker'} />
            </Tooltip>
          )}
          <Tooltip content={voiceOn ? (fi ? 'Lopeta kuuntelu' : 'Stop listening') : (fi ? 'Kuuntele' : 'Listen')} relationship="label">
            <ToggleButton appearance="subtle" checked={voiceOn} onClick={onVoice} icon={voiceOn ? <Speaker224Regular /> : <SpeakerOff24Regular />} />
          </Tooltip>
          <Tooltip content={fi ? 'Katsojajuhlat' : 'Watch party'} relationship="label">
            <Button appearance="subtle" icon={<PeopleCommunity24Regular />} onClick={onParty}>{partyCode}</Button>
          </Tooltip>
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Tooltip content={fi ? 'Lisää' : 'More'} relationship="label">
                <Button appearance="subtle" icon={<MoreHorizontal24Regular />} />
              </Tooltip>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem icon={<Settings24Regular />} onClick={onPersonalise}>{fi ? 'Omat asetukset' : 'Personalise'}</MenuItem>
                <MenuItem icon={<BookOpen24Regular />} onClick={onGlossary}>{fi ? 'Opi peli' : 'Learn the Game'}</MenuItem>
                <MenuItem icon={<PeopleCommunity24Regular />} onClick={onParty}>{fi ? 'Katsojajuhlat' : 'Watch party'}</MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        </span>
      </div>
      {info && tick ? (
        <div className="fh-score" aria-label={`${info.home.name} ${tick.homeGoals}, ${info.away.name} ${tick.awayGoals}`}>
          <ScoreTeam info={info} side="home" mine={myTeam === info.home.teamId} scorers={scorers.home} />
          <div className="fh-mid">
            <span className="fh-goals num"><Roll value={tick.homeGoals} /><span className="fh-dash">–</span><Roll value={tick.awayGoals} /></span>
            {!finished && <span className="fh-clock num"><i aria-hidden="true" />{clock(tick.clockSec)}</span>}
            <span className="fh-period">{period}</span>
          </div>
          <ScoreTeam info={info} side="away" mine={myTeam === info.away.teamId} scorers={scorers.away} />
        </div>
      ) : (
        <p className="fh-wait">{connectionText}</p>
      )}
      {ticker && tickerOn && <div className="fh-ticker">{ticker}</div>}
    </header>
  );
}

function ScoreTeam({ info, side, mine, scorers }: { info: MatchInfo; side: 'home' | 'away'; mine: boolean; scorers: string[] }) {
  const t = side === 'home' ? info.home : info.away;
  return (
    <div className={`fh-team fh-${side} ${mine ? 'mine' : ''}`}>
      <Shirt side={side} size={38} star={mine} />
      <div className="fh-team-text">
        <span className="fh-name"><span className="full">{t.name}</span><span className="short">{t.shortName}</span></span>
        {scorers.length > 0 && <span className="fh-scorers">{scorers.join(', ')}</span>}
      </div>
    </div>
  );
}

/** A titled block on the fan page. */
export function Section({ title, aside, children, className }: { title?: string; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`fsec ${className ?? ''}`}>
      {(title || aside) && (
        <div className="fsec-head">
          {title && <h2>{title}</h2>}
          {aside && <span className="fsec-aside">{aside}</span>}
        </div>
      )}
      {children}
    </section>
  );
}

/** The newest moment, large: what happened, and the two things you can do with it. */
export function KeyMoment({ v, info, mine, fresh, fi, onOpen, onReplay, onAsk, test, clockSec, events }:
  { v: InsightView; info: MatchInfo; mine: string; fresh: boolean; fi: boolean; onOpen: () => void; onReplay: () => void; onAsk: () => void;
    test?: TacticalTest; clockSec?: number; events?: PitchEvent[] }) {
  const i = v.insight;
  const side = sideOf(info, i.teamIds[0]);
  const lead = playerTeam(info, i.playerIds[0]);
  return (
    <Card className={`key-moment side-${side} ${fresh ? 'fresh' : ''} ${mine === i.teamIds[0] ? 'ours' : ''}`}>
      <div className="km-meta">
        <Badge appearance="tint" color="brand" shape="rounded">{(fi ? 'Avainhetki' : 'Key moment').toUpperCase()} · {clock(i.matchClockSec)}</Badge>
        <span className="km-kind">{kindText(i.kind, fi)}</span>
        {lead && <Shirt side={lead.side} number={lead.player.shirtNumber} size={28} title={lead.player.name} />}
      </div>
      <button type="button" className="km-text" onClick={onOpen}>
        <span className="km-headline">{noBreak(i.headline)}</span>
        <span className="km-summary">{i.summary}</span>
      </button>
      {i.figures.length > 0 && (
        <dl className="km-figures">
          {i.figures.slice(0, 3).map((f) => <div key={f.label}><dt>{f.label}</dt><dd className="num">{formatFigure(f.value, f.unit)}</dd></div>)}
        </dl>
      )}
      {test && <TestCard test={test} clockSec={clockSec ?? test.opensAtSec} fi={fi} compact events={events} />}
      <div className="km-actions">
        {(i.evidenceEventIds.length > 0 || i.kind === 'tacticalShift' || i.kind === 'playerRole') && (
          <Button appearance="primary" icon={<Play24Regular />} onClick={onReplay}>{fi ? 'Katso miten se tapahtui' : 'See how it happened'}</Button>
        )}
        <Button appearance="outline" icon={<Chat24Regular />} onClick={onAsk}>{fi ? 'Kysy miksi' : 'Ask why'}</Button>
      </div>
    </Card>
  );
}

/** An earlier moment in the feed: one line of what happened; the details open in the side panel. */
export function MomentRow({ v, info, mine, focusPlayer, fresh, fi, onOpen, test }:
  { v: InsightView; info: MatchInfo; mine: string; focusPlayer: string; fresh: boolean; fi: boolean; onOpen: () => void; test?: TacticalTest }) {
  const i = v.insight;
  const side = sideOf(info, i.teamIds[0]);
  const lead = playerTeam(info, i.playerIds[0]);
  const ours = mine !== '' && mine === i.teamIds[0];
  const starring = focusPlayer !== '' && i.playerIds.includes(focusPlayer);
  return (
    <button type="button" className={`moment-row side-${side} ${ours ? 'ours' : ''} ${fresh ? 'fresh' : ''}`} onClick={onOpen}>
      <span className="mr-clock num">{clock(i.matchClockSec)}</span>
      <span className="mr-body">
        <span className="mr-kind">
          {kindText(i.kind, fi)}
          {ours && <Badge size="small" appearance="tint" color="brand">{fi ? 'Sinun joukkueesi' : 'Your team'}</Badge>}
          {starring && <Badge size="small" appearance="tint" color="warning">★ {fi ? 'Pelaajasi' : 'Your player'}</Badge>}
          {test && <TestChip test={test} fi={fi} />}
        </span>
        <span className="mr-headline">{i.headline}</span>
        <span className="mr-summary">{i.summary}</span>
      </span>
      {lead && <Shirt side={lead.side} number={lead.player.shirtNumber} size={30} title={lead.player.name} />}
      <ChevronRight20Regular className="mr-chev" aria-hidden="true" />
    </button>
  );
}

/**
 * A moment opened: the explanation, the figures, the check it passed, and the evidence on the pitch. A side panel on
 * desktop (the pitch and timeline stay visible), a sheet from the bottom on a phone.
 */
export function MomentDrawer({ v, candidate, info, events, shapes, fi, wide, learn, replayFirst, mine, onClose, onAsk, test, clockSec, inline }:
  { v?: InsightView; candidate?: CandidateView; info: MatchInfo; events: PitchEvent[]; shapes: ShapeReport[]; fi: boolean; wide: boolean;
    learn: boolean; replayFirst: boolean; mine: string; onClose: () => void; onAsk: (v: InsightView) => void;
    test?: TacticalTest; clockSec?: number; inline?: boolean }) {
  // On the fan view the evidence plays on the match stage; without a stage (or in the drawer) it plays here.
  const stage = useStage();
  const onStage = inline && !!stage.show;
  const [replay, setReplay] = useState(replayFirst && !onStage);
  // The events that decided a tactical test, replayed on the pitch.
  const [testIds, setTestIds] = useState<string[]>();
  const key = v?.insight.insightId;
  const [lastKey, setLastKey] = useState(key);
  if (key !== lastKey) { setLastKey(key); setReplay(replayFirst && !onStage); setTestIds(undefined); }
  const i = v?.insight;
  const side = i ? sideOf(info, i.teamIds[0]) : 'home';
  const team = side === 'home' ? info.home : info.away;
  const shape = candidate && (candidate.kind === 'tacticalShift' || candidate.kind === 'playerRole');
  const back = () => stage.show?.(undefined);

  const showReplay = () => {
    if (!i) return;
    if (!onStage) { setReplay(true); return; }
    stage.show!({
      key: `m-${i.insightId}`, label: `${clock(i.matchClockSec)} · ${i.headline}`,
      node: shape && candidate
        ? <ShapeReplay c={candidate} events={events} info={info} fi={fi} shapes={shapes} onClose={back} />
        : <MoveReplay ids={i.evidenceEventIds} events={events} info={info} fi={fi} title={i.headline} onClose={back} />,
    });
  };
  const showTestEvents = (ids: string[]) => {
    if (!onStage) { setReplay(false); setTestIds(ids); return; }
    stage.show!({ key: `t-${ids.join(',')}`, label: fi ? 'Testin ratkaisseet tilanteet' : 'The events that decided the test',
      node: <MoveReplay ids={ids} events={events} info={info} fi={fi} title={fi ? 'Testin ratkaisseet tilanteet' : 'The events that decided the test'} onClose={back} /> });
  };
  // Opening a moment with "See how it happened" plays it on the stage straight away.
  useEffect(() => { if (onStage && replayFirst && key) showReplay(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [key]);

  const kicker = v && i && (
    <span className="md-kicker">
      <Shirt side={side} size={24} />
      <span>{team.shortName} · {clock(i.matchClockSec)} · {kindText(i.kind, fi)}</span>
      {mine === team.teamId && <Badge size="small" appearance="tint" color="brand">{fi ? 'Sinun joukkueesi' : 'Your team'}</Badge>}
    </span>
  );
  const body = v && i && (
    <>
      <h2 className="md-headline">{noBreak(i.headline)}</h2>
      <p className="md-summary">{i.summary}</p>
      {i.figures.length > 0 && (
        <dl className="md-figures">
          {i.figures.map((f) => <div key={f.label}><dt>{f.label}</dt><dd className="num">{formatFigure(f.value, f.unit)}</dd></div>)}
        </dl>
      )}
      <div className="md-actions">
        {!replay && (i.evidenceEventIds.length > 0 || shape) && (
          <Button appearance="primary" icon={<Play24Regular />} onClick={showReplay}>
            {shape ? (fi ? 'Näytä kentällä' : 'Show it on the pitch') : (fi ? 'Katso miten se tapahtui' : 'See how it happened')}
          </Button>
        )}
        <Button appearance="outline" icon={<Chat24Regular />} onClick={() => onAsk(v)}>{fi ? 'Kysy miksi' : 'Ask why'}</Button>
      </div>
      <p className="md-check">
        <ShieldCheckmark24Regular aria-hidden="true" />
        <span>
          {fi ? 'Tarkistettu: jokainen luku on mitattu ja todisteet ovat oikeita tilanteita.' : 'Checked: every number was measured and the evidence is real match events.'}
          {i.validation?.groundingP != null && <span className="muted"> {fi ? 'Varmuus' : 'Confidence'} {Math.round(i.validation.groundingP * 100)}%.</span>}
        </span>
      </p>
      {learn && <LearnChips ids={conceptsFor(i.kind, candidate)} fi={fi} onSeeIt={showReplay} />}
      {test && <TestCard test={test} clockSec={clockSec ?? test.opensAtSec} fi={fi} events={events} onEvents={showTestEvents} />}
      {testIds && (
        <MoveReplay ids={testIds} events={events} info={info} fi={fi}
          title={fi ? 'Testin ratkaisseet tilanteet' : 'The events that decided the test'} onClose={() => setTestIds(undefined)} />
      )}
      {replay && (
        shape && candidate
          ? <ShapeReplay c={candidate} events={events} info={info} fi={fi} shapes={shapes} onClose={() => setReplay(false)} />
          : <MoveReplay ids={i.evidenceEventIds} events={events} info={info} fi={fi} title={i.headline} onClose={() => setReplay(false)} />
      )}
    </>
  );

  if (inline) {
    if (!v || !i) return null;
    return (
      <section className={`moment-panel side-${side}`} aria-label={i.headline}>
        <header className="mp-head">
          <button type="button" className="mp-back" onClick={() => { back(); onClose(); }}>
            <ArrowLeft16Regular aria-hidden="true" /> {fi ? 'Takaisin' : 'Back'}
          </button>
          {kicker}
        </header>
        <div className="md-body">{body}</div>
      </section>
    );
  }
  return (
    <OverlayDrawer open={!!v} position={wide ? 'end' : 'bottom'} size={wide ? 'medium' : replay ? 'full' : 'large'}
      onOpenChange={(_, d) => { if (!d.open) onClose(); }} className="moment-drawer">
      {v && i && (
        <>
          <DrawerHeader>
            <DrawerHeaderTitle action={<Button appearance="subtle" aria-label={fi ? 'Sulje' : 'Close'} icon={<Dismiss24Regular />} onClick={onClose} />}>
              {kicker}
            </DrawerHeaderTitle>
          </DrawerHeader>
          <DrawerBody className="md-body">{body}</DrawerBody>
        </>
      )}
    </OverlayDrawer>
  );
}

export function MoveReplay({ ids, events, info, fi, title, onClose }:
  { ids: string[]; events: PitchEvent[]; info: MatchInfo; fi: boolean; title: string; onClose: () => void }) {
  const moves = useMemo(() => rewindEvents(events, ids), [events, ids]);
  const rewind = useRewind(moves);
  if (moves.length === 0) return <p className="muted">{fi ? 'Tilanteella ei ole kenttäsijainteja.' : 'No positions to replay for this one.'}</p>;
  return (
    <div className="md-replay">
      <Pitch events={events} clockSec={moves[moves.length - 1].clockSec} info={info} rewind={{ events: moves, step: rewind.step }} compact />
      <RewindBar moves={moves} info={info} rewind={rewind} fi={fi} title={title} onClose={onClose} closeLabel={fi ? 'Sulje kelaus' : 'Close replay'} />
    </div>
  );
}

function ShapeReplay({ c, events, info, fi, shapes, onClose }:
  { c: CandidateView; events: PitchEvent[]; info: MatchInfo; fi: boolean; shapes: ShapeReport[]; onClose: () => void }) {
  const pair = useShiftCompare(c);
  return (
    <div className="md-replay">
      <Pitch events={events} clockSec={c.toSec} info={info} compact>
        <ShapeDefs />
        {c.kind === 'playerRole' && (
          <HeatLayer events={events} playerId={c.playerId} fromSec={c.fromSec} toSec={c.toSec + 1}
            color={c.teamId === info.home.teamId ? 'var(--home)' : 'var(--away)'} />
        )}
        {pair && <ShapeLayer reports={[pair.after]} ghost={[pair.before]} info={info} fi={fi} />}
        {c.kind === 'playerRole' && <ShapeLayer reports={shapes.filter((r) => r.teamId === c.teamId)} info={info} fi={fi} highlight={c.playerId} />}
      </Pitch>
      <ShapeBar c={c} pair={pair} info={info} fi={fi} onClose={onClose} />
    </div>
  );
}

/** A side panel for things that sit beside the match: the watch party, the glossary. */
export function SidePanel({ open, title, wide, fi, onClose, children, large }:
  { open: boolean; title: string; wide: boolean; fi: boolean; onClose: () => void; children: ReactNode; large?: boolean }) {
  return (
    <OverlayDrawer open={open} position={wide ? 'end' : 'bottom'} size={wide ? (large ? 'large' : 'medium') : 'full'} onOpenChange={(_, d) => { if (!d.open) onClose(); }}>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" aria-label={fi ? 'Sulje' : 'Close'} icon={<Dismiss24Regular />} onClick={onClose} />}>
          {title}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody className="side-panel-body">{children}</DrawerBody>
    </OverlayDrawer>
  );
}

/** The followed player, in brief, with a way into everything about them. */
export function FollowPlayer({ playerId, info, events, shapes, fi, onOpen, running, wide }:
  { playerId: string; info: MatchInfo; events: PitchEvent[]; shapes: ShapeReport[]; fi: boolean; onOpen: () => void; running?: RunnerView[]; wide?: boolean }) {
  const who = playerTeam(info, playerId);
  const line = useMatchLines(events).get(playerId);
  const role = shapes.flatMap((r) => r.players).find((p) => p.playerId === playerId)?.role;
  if (!who) return null;
  return (
    <Card className={`follow side-${who.side}`}>
      <div className="follow-head">
        <Shirt side={who.side} number={who.player.shirtNumber} size={44} star />
        <div>
          <p className="follow-kicker">{fi ? 'Seuraamasi pelaaja' : 'Following'}</p>
          <p className="follow-name">{who.player.name}</p>
          <p className="follow-meta">{who.team.shortName}{role ? ` · ${fi ? roleLabel[role].fi : roleLabel[role].en}` : ''}</p>
        </div>
      </div>
      <dl className="follow-stats">
        <div><dt>{fi ? 'Maalit' : 'Goals'}</dt><dd className="num">{line?.goals ?? 0}</dd></div>
        <div><dt>{fi ? 'Laukaukset' : 'Shots'}</dt><dd className="num">{line?.shots ?? 0}</dd></div>
        <div><dt>{fi ? 'Syötöt' : 'Passes'}</dt><dd className="num">{line ? `${line.completed}/${line.passes}` : '0/0'}</dd></div>
        <div><dt>{fi ? 'Riistot' : 'Regains'}</dt><dd className="num">{line?.regains ?? 0}</dd></div>
      </dl>
      <RunningStrip run={running?.find((r) => r.playerId === playerId)} fi={fi} />
      {wide && <button type="button" className="cam-btn" onClick={watchWithPlayerCam}>{fi ? '◎ Katso pelaajakameralla' : '◎ Watch with the player cam'}</button>}
      <Button appearance="transparent" className="follow-link" icon={<ArrowRight16Regular />} iconPosition="after" onClick={onOpen}>
        {fi ? 'Pelaajan tapahtumat' : 'View player activity'}
      </Button>
    </Card>
  );
}

/** A question box on the Live page; asking takes you to Ask with the answer on its way. */
export function AskBox({ suggestion, fi, onAsk }: { suggestion?: string; fi: boolean; onAsk: (q: string) => void }) {
  const [q, setQ] = useState('');
  const send = (text: string) => { if (text.trim().length >= 3) { onAsk(text.trim()); setQ(''); } };
  return (
    <Card className="ask-box">
      <p className="ab-title"><Chat24Regular aria-hidden="true" /> Ask Second Ball IQ</p>
      {suggestion && (
        <button type="button" className="ab-suggest" onClick={() => send(suggestion)}>{suggestion}</button>
      )}
      <form className="ab-form" onSubmit={(e) => { e.preventDefault(); send(q); }}>
        <Input value={q} onChange={(_, d) => setQ(d.value)} placeholder={fi ? 'Kysy ottelusta…' : 'Ask about the match…'}
          aria-label={fi ? 'Kysymys' : 'Your question'} className="ab-input" />
        <Button type="submit" appearance="primary" icon={<Send24Regular />} aria-label={fi ? 'Kysy' : 'Ask'} disabled={q.trim().length < 3} />
      </form>
    </Card>
  );
}
