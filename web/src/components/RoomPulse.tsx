import { useEffect, useMemo, useState } from 'react';
import type { Party } from '../party/useParty';
import type { MatchInfo, PitchEvent } from '../types';

/**
 * The Room's pulse, at the top of the Room: the running score of the room against Second Ball IQ (every settled pick
 * and every settled tactical test counts for whoever called it right), who is here, and the room's energy, which
 * rises with reactions, messages and what happens on the pitch and dies away when it is quiet. A goal sets off a
 * burst over the room in the scoring team's colour.
 */
const goalMemo: { lastId?: string; burst?: { key: number; side: 'home' | 'away'; team: string; until: number } } = {};

export function RoomPulse({ party, info, events, fi }: { party: Party; info: MatchInfo; events: PitchEvent[]; fi?: boolean }) {
  const st = party.state!;

  // Room against Second Ball IQ: picks by the room's majority against Second Ball IQ's pick; tests by the room's
  // majority vote against Decision-1's estimate (which side of 50%). Only what the match has settled counts.
  const score = useMemo(() => {
    let room = 0, sbiq = 0;
    for (const p of st.picks) {
      if (!p.outcome) continue;
      if (p.crowdPick === p.outcome) room++;
      if (p.matchLensPick === p.outcome) sbiq++;
    }
    for (const p of st.polls) {
      if (!p.testId || (p.outcome !== 'met' && p.outcome !== 'notMet')) continue;
      const right = p.outcome === 'met' ? 'yes' : 'no';
      const yes = p.options.find((o) => o.id === 'yes')?.votes ?? 0, no = p.options.find((o) => o.id === 'no')?.votes ?? 0;
      if (yes !== no && (yes > no ? 'yes' : 'no') === right) room++;
      if (p.estimate !== undefined && p.estimate !== null && (p.estimate >= 0.5 ? 'yes' : 'no') === right) sbiq++;
    }
    return { room, sbiq };
  }, [st.picks, st.polls]);

  // Energy: recent reactions and messages (by wall clock) and recent match action (by match clock), decaying.
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 500); return () => window.clearInterval(t); }, []);
  const matchNow = events.length ? events[events.length - 1].clockSec : 0;
  const energy = useMemo(() => {
    let e = 8;
    e += party.reactions.length * 9;
    for (const m of st.messages ?? []) { const age = (now - (m.atMs - party.skewMs)) / 1000; if (age >= 0 && age < 40) e += 7 * (1 - age / 40); }
    for (let i = events.length - 1; i >= 0 && matchNow - events[i].clockSec < 120; i--) {
      const ev = events[i], w = 1 - (matchNow - ev.clockSec) / 120;
      if (ev.type === 'goal') e += 70 * w;
      else if (ev.type === 'shot') e += 14 * w;
      else if (ev.highRecovery) e += 4 * w;
    }
    return Math.max(0, Math.min(100, Math.round(e)));
  }, [party.reactions.length, st.messages, now, events, matchNow, party.skewMs]);
  const mood = energy >= 80 ? (fi ? 'Sekasorto' : 'Bedlam') : energy >= 55 ? (fi ? 'Kuohuu' : 'Rocking') : energy >= 30 ? (fi ? 'Surisee' : 'Buzzing') : (fi ? 'Hiljaista' : 'Quiet');

  // A goal: a burst over the room for a few seconds, in the scorer's colour. Kept outside the component, so a
  // re-render or a remount of the room does not lose it or set it off again.
  const latestGoal = [...events].reverse().find((e) => e.type === 'goal');
  if (goalMemo.lastId === undefined) goalMemo.lastId = latestGoal?.id ?? '';
  else if (latestGoal && latestGoal.id !== goalMemo.lastId) {
    goalMemo.lastId = latestGoal.id;
    const side = latestGoal.teamId === info.away.teamId ? 'away' : 'home';
    goalMemo.burst = { key: Date.now(), side, team: (side === 'home' ? info.home : info.away).name, until: Date.now() + 4200 };
  }
  const burst = goalMemo.burst && goalMemo.burst.until > now ? goalMemo.burst : undefined;

  const lead = score.room === score.sbiq ? 'level' : score.room > score.sbiq ? 'room' : 'sbiq';
  const members = [...st.members].sort((a, b) => b.points - a.points);

  return (
    <section className={`room-pulse lead-${lead}`} aria-label={fi ? 'Huoneen tilanne' : 'The room right now'}>
      <div className="rp-score" role="status">
        <div className="rp-side rp-room"><span>{fi ? 'Huone' : 'The room'}</span><b key={`r${score.room}`} className="num rp-tick">{score.room}</b></div>
        <span className="rp-vs">{fi ? 'vastaan' : 'vs'}</span>
        <div className="rp-side rp-sbiq"><b key={`s${score.sbiq}`} className="num rp-tick">{score.sbiq}</b><span>Second Ball IQ</span></div>
      </div>
      <p className="rp-caption">
        {lead === 'level' ? (fi ? 'Tasoissa: veikkaukset ja testit ratkaisee ottelu.' : 'All square: picks and tests, settled by the match.')
          : lead === 'room' ? (fi ? 'Huone johtaa koneen ohi!' : 'The room is ahead of the machine!')
          : (fi ? 'Second Ball IQ johtaa. Haasta se seuraavassa veikkauksessa.' : 'Second Ball IQ leads. Beat it on the next pick.')}
      </p>
      <div className="rp-energy" aria-label={`${fi ? 'Tunnelma' : 'Energy'} ${energy}`}>
        <span className="rp-label">{fi ? 'Tunnelma' : 'Room energy'}</span>
        <span className="rp-meter"><i style={{ width: `${energy}%` }} /></span>
        <b className={`rp-mood m-${energy >= 80 ? 4 : energy >= 55 ? 3 : energy >= 30 ? 2 : 1}`}>{mood}</b>
      </div>
      <ul className="rp-members" aria-label={fi ? 'Paikalla' : 'Here'}>
        {members.slice(0, 10).map((m) => {
          const side = m.teamId === info.home.teamId ? 'home' : m.teamId === info.away.teamId ? 'away' : 'neutral';
          const reacting = party.reactions.find((r) => r.name === m.name);
          return (
            <li key={m.memberId} className={`side-${side} ${m.memberId === party.me ? 'me' : ''} ${reacting ? 'reacting' : ''}`} title={`${m.name}${m.simulated ? (fi ? ' (simuloitu)' : ' (simulated)') : ''} · ${m.points}`}>
              <span className="rp-avatar">{m.name.slice(0, 1).toUpperCase()}</span>
              {reacting && <span className="rp-react">{reacting.emoji}</span>}
              <small className="num">{m.points}</small>
            </li>
          );
        })}
      </ul>
      {burst && (
        <div key={burst.key} className={`rp-burst side-${burst.side}`} aria-hidden="true">
          {Array.from({ length: 22 }, (_, i) => (
            <i key={i} style={{ left: `${(i * 37) % 100}%`, animationDelay: `${(i % 7) * 60}ms`, ['--dx' as string]: `${((i * 53) % 120) - 60}px` }} />
          ))}
          <strong>{fi ? 'MAALI!' : 'GOAL!'}<small>{burst.team}</small></strong>
        </div>
      )}
    </section>
  );
}
