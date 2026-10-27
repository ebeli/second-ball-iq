import { useEffect, useState, type ReactNode } from 'react';
import { RoomPulse } from './RoomPulse';
import { clock } from '../format';
import { AskAnswer, AskVisual } from './Ask';
import type { Party } from '../party/useParty';
import { TifoControls } from './Tifo';
import { firstSeen } from '../firstSeen';
import { useStage } from './Stage';
import type { InsightView, MatchInfo, PartyPicks, PartyPoll, PitchEvent } from '../types';

const EMOJIS = ['🔥', '😱', '👏', '🤔'];

/** Create a party or join one with a code. */
export function PartyLobby({ party, info, fi, name: initialName, teamId, onName }:
  { party: Party; info?: MatchInfo; fi?: boolean; name: string; teamId?: string; onName: (n: string) => void }) {
  const [name, setName] = useState(initialName);
  const [team, setTeam] = useState(teamId ?? '');
  const [code, setCode] = useState(() => new URLSearchParams(window.location.search).get('party') ?? '');
  const [demo, setDemo] = useState(false);
  const [error, setError] = useState<string>();
  const ready = name.trim().length > 0;
  return (
    <section className="party-lobby">
      <h2>{fi ? 'Katsojajuhlat' : 'Watch Party'}</h2>
      <p className="fan-hint">
        {fi
          ? 'Katsokaa yhdessä: reagoikaa, äänestäkää siitä mitä Second Ball IQ löytää, veikatkaa erikoistilanteet Second Ball IQ:ta vastaan ja kysykää yhdessä.'
          : 'Watch together: react, vote on what Second Ball IQ finds, pick set pieces against Second Ball IQ, and ask it questions as a room.'}
      </p>
      <label className="party-field"><span>{fi ? 'Nimesi' : 'Your name'}</span>
        <input value={name} maxLength={20} onChange={(e) => setName(e.target.value)} placeholder={fi ? 'esim. Mikko' : 'e.g. Sam'} /></label>
      {info && (
        <label className="party-field"><span>{fi ? 'Kannatat' : 'You support'}</span>
          <select value={team} onChange={(e) => setTeam(e.target.value)}>
            <option value="">{fi ? 'Ei kumpaakaan' : 'Neither'}</option>
            <option value={info.home.teamId}>{info.home.name}</option>
            <option value={info.away.teamId}>{info.away.name}</option>
          </select></label>
      )}
      <div className="party-choices">
        <div className="party-box">
          <h3>{fi ? 'Liity koodilla' : 'Join with a code'}</h3>
          <div className="party-join">
            <input value={code} maxLength={4} onChange={(e) => setCode(e.target.value.toUpperCase())} placeholder="ABCD" aria-label={fi ? 'Koodi' : 'Code'} />
            <button type="button" className="primary" disabled={!ready || code.length !== 4}
              onClick={async () => { onName(name); setError(undefined); if (!(await party.join(code, name, team || undefined))) setError(fi ? 'Juhlia ei löytynyt.' : 'No party with that code.'); }}>
              {fi ? 'Liity' : 'Join'}
            </button>
          </div>
        </div>
        <div className="party-box">
          <h3>{fi ? 'Aloita uudet juhlat' : 'Start a party'}</h3>
          <label className="setup-toggle"><input type="checkbox" checked={demo} onChange={(e) => setDemo(e.target.checked)} />
            <span>{fi ? 'Esittelytila: 8 simuloitua fania' : 'Demo mode: 8 simulated fans'}</span></label>
          <button type="button" className="primary" disabled={!ready} onClick={() => { onName(name); void party.create(name, team || undefined, demo); }}>
            {fi ? 'Aloita' : 'Start'}
          </button>
        </div>
      </div>
      {error && <p className="ask-error">{error}</p>}
    </section>
  );
}

/**
 * The Room, in the right panel beside the match stage (the stage's stand is the room: everyone has a front-row seat,
 * their wristbands light with the vote, tifos go up over their end). Here: the pinned vote, the tifo controls, one feed
 * of everything the room sees in the order it arrives (match moments, results, picks, shares and fans' messages) with
 * chips to filter it, the room's questions to Second Ball IQ and the leaderboard.
 */
export function PartyRoom({ party, info, events, fi, onPlayer, feed, onOpenMoment, pinned }:
  { party: Party; info: MatchInfo; events: PitchEvent[]; fi?: boolean; onPlayer?: (id: string) => void;
    feed: InsightView[]; onOpenMoment: (insightId: string) => void; pinned?: ReactNode }) {
  const st = party.state!;
  const [text, setText] = useState('');
  const [note, setNote] = useState<string>();
  const [sending, setSending] = useState(false);
  const meName = st.members.find((m) => m.memberId === party.me)?.name;
  const isHost = meName === st.host;
  const invite = `${window.location.origin}/fan?party=${st.code}`;

  return (
    <section className="party-room">
      <header className="party-head">
        <div>
          <p className="party-code">{fi ? 'Juhlat' : 'Party'} <strong>{st.code}</strong></p>
          <button type="button" className="link" onClick={() => void navigator.clipboard?.writeText(invite)}>{fi ? 'Kopioi kutsulinkki' : 'Copy invite link'}</button>
        </div>
        <span className="party-count">{st.members.length} {fi ? 'paikalla' : 'here'}</span>
        <button type="button" onClick={() => void party.leave()}>{fi ? 'Poistu' : 'Leave'}</button>
      </header>
      <RoomPulse party={party} info={info} events={events} fi={fi} />
      {pinned}
      <TifoControls party={party} info={info} fi={fi} />
      <RoomFeed party={party} info={info} events={events} fi={fi} feed={feed} onOpenMoment={onOpenMoment} />
      {st.demo && <p className="fan-hint">{fi ? 'Esittelytila: osa faneista on simuloituja, ja ne on merkitty.' : 'Demo mode: some fans are simulated, and labelled as such.'}</p>}

      <details className="party-questions">
        <summary><h3>{fi ? 'Huoneen kysymykset Second Ball IQ:lle' : 'Room questions for Second Ball IQ'}</h3><span>{st.questions.length}</span></summary>
        <p className="fan-hint">{fi ? 'Eniten ääniä saanut kysymys menee Second Ball IQ:lle, ja vastaus näkyy kaikille.' : 'The most upvoted question goes to Second Ball IQ, and everyone sees the answer.'}</p>
        {st.questions.slice().reverse().map((q) => (
          <div key={q.questionId} className={`pq status-${q.status}`}>
            <div className="pq-head">
              <p><strong>{q.by}:</strong> {q.text}</p>
              {q.status === 'open' && (
                <div className="pq-actions">
                  <button type="button" aria-pressed={party.myVotes[q.questionId] === 'up'} onClick={() => party.upvote(q.questionId)}>▲ {q.votes}</button>
                  {isHost && <button type="button" className="link" onClick={() => party.investigate(q.questionId)}>{fi ? 'Kysy nyt' : 'Ask now'}</button>}
                </div>
              )}
              {q.status === 'investigating' && <span className="pq-working"><span className="ask-dots" aria-hidden="true" /> {fi ? 'Second Ball IQ tutkii…' : 'Second Ball IQ is looking…'}</span>}
            </div>
            {q.answer && <AskAnswer a={q.answer} info={info} events={events} fi={fi} onPlayer={onPlayer} />}
          </div>
        ))}
        <form className="ask-form" onSubmit={async (e) => {
          e.preventDefault();
          if (sending) return;
          setSending(true);
          const err = await party.ask(text, fi ? 'fi' : 'en');
          setSending(false);
          if (err) setNote(err); else { setNote(undefined); setText(''); }
        }}>
          <input value={text} maxLength={200} onChange={(e) => setText(e.target.value)}
            placeholder={fi ? 'Kysy huoneen puolesta…' : 'Ask for the room…'} aria-label={fi ? 'Kysymys' : 'Question'} />
          <button type="submit" className="primary" disabled={sending || text.trim().length < 5}>{fi ? 'Lähetä' : 'Post'}</button>
        </form>
        {note && <p className="ask-error">{note}</p>}
      </details>

      <details className="party-board">
        <summary><h3>{fi ? 'Pistetilanne' : 'Leaderboard'}</h3><span>{[...st.members].sort((a, b) => b.points - a.points)[0]?.name ?? ''}</span></summary>
        <ol>
          {[...st.members].sort((a, b) => b.points - a.points).slice(0, 8).map((m) => (
            <li key={m.memberId} className={m.memberId === party.me ? 'me' : ''}><span>{m.name}{m.simulated && <span className="pm-sim"> {fi ? 'simuloitu' : 'sim'}</span>}</span><span className="num">{m.points}</span></li>
          ))}
        </ol>
        <p className="fan-hint">{fi ? 'Veikkaukset ratkaisee itse tilanne: oikea veikkaus 3 pistettä, 5 jos Second Ball IQ:n veikkaus meni väärin, väärä 0. Taktiset testit: 2 pistettä, jos arvasit, pitääkö ehto; ottelu ratkaisee, ratkaisematon 0. Kyselyt: 1 piste, jos äänesi vastaa Second Ball IQ:n mitattuihin lukuihin perustuvaa ratkaisua.' : 'Picks are settled by what happens: right 3 points, 5 if Second Ball IQ got it wrong, wrong 0. Tactical tests: 2 points if you called whether the criterion holds; the match decides, inconclusive scores 0. Polls: 1 point if your vote matches Second Ball IQ\'s verdict, which comes from the measured numbers.'}</p>
      </details>
    </section>
  );
}

type FeedFilter = 'all' | 'match' | 'chat';
type FeedItem = { key: string; at: number; kind: 'chat' | 'match'; node: ReactNode };

/**
 * One feed for the room, newest first: fans' messages, match moments, settled tests, set-piece results and what people
 * shared. Each item is placed by when this screen first saw it (messages by when they were posted), so a message about
 * a result sits right after it. Chips filter to the match or the chat. Messages are plain text, screened on the server.
 */
function RoomFeed({ party, info, events, fi, feed, onOpenMoment }:
  { party: Party; info: MatchInfo; events: PitchEvent[]; fi?: boolean; feed: InsightView[]; onOpenMoment: (insightId: string) => void }) {
  const st = party.state!;
  const stage = useStage();
  const [filter, setFilter] = useState<FeedFilter>('all');
  const [text, setText] = useState('');
  const [note, setNote] = useState<string>();
  const [sending, setSending] = useState(false);
  // First time each item was seen. Items already there when the room opened are ordered by match time, before anything new.
  const when = (key: string, fallback: number) => firstSeen(`feed:${st.code}`, key, fallback);
  const base = Date.now() - 24 * 3600 * 1000;   // old items: before anything posted in this session

  const items: FeedItem[] = [];
  for (const m of st.messages ?? []) {
    const side = m.teamId === info.home.teamId ? 'home' : m.teamId === info.away.teamId ? 'away' : 'neutral';
    items.push({ key: m.messageId, at: m.atMs - party.skewMs, kind: 'chat', node: (
      <li className={`rf-msg side-${side} ${m.memberId === party.me ? 'me' : ''}`}>
        <span className={`rf-name ${m.simulated ? 'sim' : ''}`}>{m.name}</span>
        <span className="rf-text">{m.text}</span>
      </li>
    ) });
  }
  for (const v of feed) {
    const i = v.insight;
    const side = i.teamIds[0] === info.away.teamId ? 'away' : 'home';
    items.push({ key: i.insightId, at: when(i.insightId, base + i.matchClockSec * 1000), kind: 'match', node: (
      <li className={`rf-moment side-${side} ${i.kind === 'goal' ? 'goal' : ''}`}>
        <button type="button" onClick={() => onOpenMoment(i.insightId)}>
          <b className="num">{Math.floor(i.matchClockSec / 60) + 1}′</b>
          <span>{i.headline}</span>
        </button>
      </li>
    ) });
  }
  for (const p of st.polls.filter((x) => x.testId && x.outcome)) {
    const total = p.options.reduce((a, o) => a + o.votes, 0);
    const yes = p.options.find((o) => o.id === 'yes')?.votes ?? 0;
    const mine = party.me ? p.votes?.[party.me] : undefined;
    const right = p.outcome === 'met' ? 'yes' : p.outcome === 'notMet' ? 'no' : undefined;
    items.push({ key: `${p.pollId}-r`, at: when(`${p.pollId}-r`, base + (p.deadlineSec ?? p.clockSec) * 1000), kind: 'match', node: (
      <li className={`rf-result v-${p.outcome}`}>
        <span className="rf-tag">{p.outcome === 'met' ? (fi ? 'Testi täyttyi' : 'Test met') : p.outcome === 'notMet' ? (fi ? 'Testi ei täyttynyt' : 'Test not met') : (fi ? 'Ratkaisematon' : 'Inconclusive')}</span>
        <span className="rf-text">{fi ? p.questionFi : p.question}</span>
        <span className="rf-meta">
          {total > 0 ? `${Math.round((100 * yes) / total)}% ${fi ? 'sanoi pitää' : 'said it holds'}` : ''}
          {mine && right && <b className={mine === right ? 'won' : ''}>{mine === right ? ' · +2' : ' · 0'}</b>}
        </span>
      </li>
    ) });
  }
  for (const p of st.picks.filter((x) => x.outcome)) {
    const label = (id?: string) => { const o = p.options.find((x) => x.id === id); return o ? (fi ? o.labelFi : o.label) : '–'; };
    items.push({ key: `${p.predictionId}-r`, at: when(`${p.predictionId}-r`, base), kind: 'match', node: (
      <li className="rf-result v-pick">
        <span className="rf-tag">{p.kind === 'NextGoal' ? (fi ? 'Seuraava maali' : 'Next goal') : (fi ? 'Erikoistilanne' : 'Set piece')}</span>
        <span className="rf-text">{fi ? 'Tulos' : 'Result'}: {label(p.outcome)} · Second Ball IQ: {label(p.matchLensPick)} · {fi ? 'Huone' : 'Room'}: {label(p.crowdPick)}</span>
        {p.rightNames.length > 0 && <span className="rf-meta">{fi ? 'Osuivat' : 'Called it'}: {p.rightNames.join(', ')}</span>}
      </li>
    ) });
  }
  party.shares.forEach((sh) => {
    items.push({ key: `share-${sh.atMs}-${sh.name}`, at: sh.atMs - party.skewMs, kind: 'chat', node: (
      <li className="rf-share">
        <span className="rf-name">{sh.name}</span>
        <span className="rf-text">{fi ? 'jakoi' : 'shared'}: {sh.label}</span>
        {stage.show && (
          <button type="button" className="link" onClick={() => stage.show!({ key: `share-${sh.atMs}`, label: sh.label,
            node: <AskVisual action={{ kind: 'rewind', label: sh.label, eventIds: sh.eventIds }} info={info} events={events} fi={fi} /> })}>
            {fi ? 'Näytä kentällä' : 'Show on the pitch'}
          </button>
        )}
      </li>
    ) });
  });
  const shown = items.filter((x) => filter === 'all' || x.kind === filter).sort((a, b) => b.at - a.at).slice(0, 80);

  return (
    <section className="room-feed" aria-label={fi ? 'Huoneen syöte' : 'Room feed'}>
      <div className="rf-chips" role="group" aria-label={fi ? 'Suodatin' : 'Filter'}>
        {([['all', fi ? 'Kaikki' : 'All'], ['match', fi ? 'Ottelu' : 'Match'], ['chat', fi ? 'Juttelu' : 'Chat']] as [FeedFilter, string][]).map(([k, l]) => (
          <button key={k} type="button" aria-pressed={filter === k} onClick={() => setFilter(k)}>{l}</button>
        ))}
      </div>
      <form className="rf-compose" onSubmit={async (e) => {
        e.preventDefault();
        if (sending || !text.trim()) return;
        setSending(true);
        const err = await party.say(text, fi ? 'fi' : 'en');
        setSending(false);
        if (err) setNote(err); else { setNote(undefined); setText(''); }
      }}>
        <input value={text} maxLength={160} onChange={(e) => setText(e.target.value)}
          placeholder={fi ? 'Kirjoita huoneelle…' : 'Say something to the room…'} aria-label={fi ? 'Viesti' : 'Message'} />
        <button type="submit" className="primary" disabled={sending || !text.trim()}>{fi ? 'Lähetä' : 'Send'}</button>
      </form>
      {note && <p className="ask-error">{note}</p>}
      <ol className="rf-list" aria-live="polite">
        {shown.map((x) => <FeedRow key={x.key}>{x.node}</FeedRow>)}
        {shown.length === 0 && <li className="rf-empty">{filter === 'chat' ? (fi ? 'Ei viestejä vielä. Aloita sinä.' : 'No messages yet. Start it off.') : (fi ? 'Ottelun hetket näkyvät tässä.' : 'Match moments show up here.')}</li>}
      </ol>
    </section>
  );
}

function FeedRow({ children }: { children: ReactNode }) {
  return <>{children}</>;
}

export function PollCard({ p, party, fi }: { p: PartyPoll; party: Party; fi?: boolean }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (p.closed) return;
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [p.closed]);
  const total = p.options.reduce((a, o) => a + o.votes, 0);
  const mine = party.myVotes[p.pollId];
  const left = Math.max(0, Math.round((p.closesAtMs - now) / 1000));
  const test = !!p.testId;
  // For a test the highlighted answer is what the match said; for an opinion poll, Second Ball IQ's view.
  const right = test ? (p.outcome === 'met' ? 'yes' : p.outcome === 'notMet' ? 'no' : undefined) : p.matchLensPick;
  const outcomeText = p.outcome === 'met' ? (fi ? 'Ehto täyttyi' : 'Criterion met')
    : p.outcome === 'notMet' ? (fi ? 'Ehto ei täyttynyt' : 'Criterion not met')
    : p.outcome === 'inconclusive' ? (fi ? 'Ratkaisematon' : 'Inconclusive') : undefined;
  return (
    <article className={`party-poll ${p.closed ? 'closed' : ''} ${test ? `is-test test-${p.outcome ?? 'open'}` : ''}`}>
      <p className="pp-kicker">
        {test ? (fi ? 'Taktinen testi' : 'Tactical test') : (fi ? 'Kysely' : 'Room poll')} · <span className="num">{clock(p.clockSec)}</span>
        {test && p.deadlineSec !== undefined && <span className="num"> → {clock(p.deadlineSec)}</span>}
        {!p.closed && <Countdown left={left} total={Math.max(left, 30)} />}
        {test && p.closed && !p.outcome && <span className="pp-left"> · {fi ? 'äänet lukittu' : 'votes locked'}</span>}
      </p>
      {test && <p className="pp-test-ask">{fi ? 'Pitääkö tämä?' : 'Will it hold?'}</p>}
      <p className={`pp-q ${test ? 'pp-criterion' : ''}`}>{fi ? p.questionFi : p.question}</p>
      <ul className="pp-options">
        {p.options.map((o) => {
          const share = total ? Math.round((100 * o.votes) / total) : 0;
          return (
            <li key={o.id}>
              <button type="button" disabled={p.closed} aria-pressed={mine === o.id} onClick={() => party.vote(p.pollId, o.id)}
                className={(test ? !!p.outcome : p.closed) && o.id === right ? 'is-ml' : ''}>
                <span className="pp-bar" style={{ width: `${share}%` }} />
                <span className="pp-label">{fi ? o.labelFi : o.label}</span>
                <span className="pp-share num">{share}%</span>
              </button>
            </li>
          );
        })}
      </ul>
      {test && p.estimate !== undefined && p.estimate !== null && !p.outcome && (
        <p className="pp-estimate muted">
          Decision-1: <span className="num">{Math.round(p.estimate * 100)}%</span> · {fi ? 'mallin arvio, ei kalibroitu todennäköisyys' : 'a model estimate, not a calibrated probability'}
        </p>
      )}
      {test && p.outcome && p.outcome !== 'inconclusive' && (
        <span className={`pp-stamp s-${p.outcome}`} aria-hidden="true">{p.outcome === 'met' ? (fi ? 'PITI' : 'HELD') : (fi ? 'MURTUI' : 'BROKEN')}</span>
      )}
      {p.closed && p.verdict && (
        <p className="pp-verdict">
          <strong>{test ? (outcomeText ?? (fi ? 'Odottaa ottelua' : 'Waiting for the match')) : 'Second Ball IQ'}:</strong> {fi ? p.verdictFi : p.verdict}
        </p>
      )}
    </article>
  );
}

/** Seconds left to vote, as a ring that empties. */
function Countdown({ left, total }: { left: number; total: number }) {
  const c = 2 * Math.PI * 9;
  return (
    <span className={`pp-ring ${left <= 5 ? 'urgent' : ''}`} aria-label={`${left} s`}>
      <svg viewBox="0 0 22 22" aria-hidden="true">
        <circle cx="11" cy="11" r="9" className="pr-track" />
        <circle cx="11" cy="11" r="9" className="pr-fill" style={{ strokeDasharray: c, strokeDashoffset: c * (1 - left / Math.max(1, total)) }} />
      </svg>
      <b className="num">{left}</b>
    </span>
  );
}

export function PicksCard({ p, party, fi, info }: { p: PartyPicks; party: Party; fi?: boolean; info: MatchInfo }) {
  const total = p.options.reduce((a, o) => a + o.votes, 0);
  const mine = party.myVotes[p.predictionId];
  const label = (id?: string) => { const o = p.options.find((x) => x.id === id); return o ? (fi ? o.labelFi : o.label) : '–'; };
  const kind = p.kind.toLowerCase();
  const title = kind === 'corner' ? (fi ? 'Kulmapotku' : 'Corner') : kind === 'penalty' ? (fi ? 'Rangaistuspotku' : 'Penalty')
    : kind === 'freekick' ? (fi ? 'Vapaapotku' : 'Free kick') : (fi ? 'Seuraava maali' : 'Next goal');
  void info;
  return (
    <article className={`party-picks ${p.outcome ? 'settled' : ''}`}>
      <p className="pp-kicker">{title}{p.playerName ? ` · ${p.playerName}` : ''} · {fi ? 'veikkaa ennen Second Ball IQ:ta' : 'your pick against Second Ball IQ'}</p>
      <div className="pk-options">
        {p.options.map((o) => (
          <button key={o.id} type="button" disabled={!!p.outcome} aria-pressed={mine === o.id} onClick={() => party.pick(p.predictionId, o.id)}
            className={`${p.outcome === o.id ? 'is-outcome' : ''}`}>
            <span>{fi ? o.labelFi : o.label}</span>
            <span className="num">{total ? Math.round((100 * o.votes) / total) : 0}%</span>
          </button>
        ))}
      </div>
      <dl className="pk-compare">
        <div><dt>{fi ? 'Huone' : 'The room'}</dt><dd>{label(p.crowdPick)}</dd></div>
        <div><dt>Second Ball IQ</dt><dd>{label(p.matchLensPick)}</dd></div>
        <div><dt>{fi ? 'Tulos' : 'Result'}</dt><dd>{p.outcome ? label(p.outcome) : '…'}</dd></div>
      </dl>
      {p.outcome && (
        <p className="pp-verdict">
          {p.rightNames.length
            ? (fi ? `Oikein: ${p.rightNames.join(', ')}.` : `Got it right: ${p.rightNames.join(', ')}.`)
            : (fi ? 'Kukaan ei osunut oikeaan.' : 'Nobody in the room got it.')}
          {' '}{p.crowdPick === p.outcome && p.matchLensPick !== p.outcome ? (fi ? 'Huone voitti Second Ball IQ:n!' : 'The room beat Second Ball IQ!') : ''}
        </p>
      )}
    </article>
  );
}

/** Reactions from the room float up the side of the screen. */
export function PartyReactions({ party }: { party: Party }) {
  if (!party.state) return null;
  return (
    <>
      <div className="party-float" aria-hidden="true">
        {party.reactions.map((r, i) => (
          <span key={r.key} className="pf-item" style={{ right: `${8 + ((i * 37) % 60)}px` }}>
            {r.emoji}<small>{r.name}</small>
          </span>
        ))}
      </div>
      <div className="party-react" role="group" aria-label="React">
        {EMOJIS.map((e) => <button key={e} type="button" onClick={() => party.react(e)}>{e}</button>)}
      </div>
    </>
  );
}
