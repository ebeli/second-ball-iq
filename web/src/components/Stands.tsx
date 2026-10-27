import { useEffect, useMemo, useRef, useState } from 'react';
import { hash, tifoColor, TIFO_COLS, TIFO_ROWS, useNow } from './Tifo';
import type { Party } from '../party/useParty';
import type { MatchInfo, PartyPoll } from '../types';

/**
 * The stand along the far touchline, drawn by us: five rows of supporters in four sections, the home end's two on the
 * left in its kit and the away end's two on the right. They breathe while the match runs, the scoring end jumps with
 * arms up on a goal, and in the Room the stand carries the watch party: everyone in the room has a front-row seat, the
 * wristbands light with the room's vote, they pulse when the match settles a test, and tifos go up over a club's end.
 */

const VW = 1000, VH = 190;
const ROWS = 5;
const SKIN = ['#f1c7a5', '#e0ab84', '#c68a5f', '#9a6440', '#6e4428'];
const HAIR = ['#2b1d14', '#5a3b22', '#1b1b1b', '#8a5a2b', '#d8b26a', '#3b2a1c'];
const BAND_YES = '#42e2a2', BAND_NO = '#f5a524';

interface Seat { key: string; x: number; y: number; s: number; side: 'home' | 'away'; row: number; section: number; memberId?: string; name?: string }

function rowGeometry(r: number) {
  // Back row is high and narrow, the front row low and wide: a simple perspective.
  const s = 0.66 + r * 0.085;
  const y = 44 + r * 31;
  const margin = 92 - r * 16;
  return { s, y, margin };
}

function layout(): { seats: Seat[]; sections: { x0: number; x1: number; side: 'home' | 'away' }[][] } {
  const seats: Seat[] = [];
  const sections: { x0: number; x1: number; side: 'home' | 'away' }[][] = [];
  for (let r = 0; r < ROWS; r++) {
    const { s, y, margin } = rowGeometry(r);
    const aisle = 26 * s;
    const w = (VW - 2 * margin - 3 * aisle) / 4;
    const rowSections: { x0: number; x1: number; side: 'home' | 'away' }[] = [];
    for (let k = 0; k < 4; k++) {
      const x0 = margin + k * (w + aisle), x1 = x0 + w;
      const side = k < 2 ? 'home' : 'away';
      rowSections.push({ x0, x1, side });
      const step = 19 * s;
      const n = Math.floor(w / step);
      const pad = (w - n * step) / 2 + step / 2;
      for (let i = 0; i < n; i++) {
        // A little jitter so the rows do not look printed.
        const h = hash(`${r}-${k}-${i}`);
        seats.push({ key: `${r}-${k}-${i}`, x: x0 + pad + i * step + ((h % 5) - 2) * 0.6, y: y + ((h >> 3) % 3) - 1, s, side, row: r, section: k });
      }
    }
    sections.push(rowSections);
  }
  return { seats, sections };
}

export function Stands({ info, homeGoals, awayGoals, party, roomMode, fi }:
  { info: MatchInfo; homeGoals: number; awayGoals: number; party?: Party; roomMode?: boolean; fi?: boolean }) {
  const base = useMemo(layout, []);
  const st = roomMode ? party?.state : undefined;
  const now = useNow(st?.tifo ? 250 : st?.polls.some((p) => p.testId) ? 1000 : 0) + (party?.skewMs ?? 0);

  // Room members take front-row seats near the middle of their own end; neutrals sit at the inner edges.
  const seats = useMemo(() => {
    const out = base.seats.map((s) => ({ ...s }));
    if (!st) return out;
    const front = (side: 'home' | 'away') => out.filter((s) => s.row === ROWS - 1 && s.side === side)
      .sort((a, b) => Math.abs(a.x - (side === 'home' ? 330 : 670)) - Math.abs(b.x - (side === 'home' ? 330 : 670)));
    // Every third seat, so the name labels under them have room.
    const spaced = (side: 'home' | 'away') => front(side).sort((a, b) => a.x - b.x).filter((_, i) => i % 3 === 1)
      .sort((a, b) => Math.abs(a.x - (side === 'home' ? 330 : 670)) - Math.abs(b.x - (side === 'home' ? 330 : 670)));
    const free = { home: spaced('home'), away: spaced('away') };
    for (const m of st.members) {
      const side = m.teamId === info.away.teamId ? 'away' : 'home';
      const seat = free[side].shift();
      if (seat) { seat.memberId = m.memberId; seat.name = m.name; }
    }
    return out;
  }, [base, st, info]);

  // A goal: the scoring end jumps for a few seconds.
  const [cheer, setCheer] = useState<'home' | 'away'>();
  const goals = useRef({ home: homeGoals, away: awayGoals });
  useEffect(() => {
    const prev = goals.current;
    goals.current = { home: homeGoals, away: awayGoals };
    const side = homeGoals > prev.home ? 'home' : awayGoals > prev.away ? 'away' : undefined;
    if (!side) return;
    setCheer(side);
    const t = window.setTimeout(() => setCheer(undefined), 3800);
    return () => window.clearTimeout(t);
  }, [homeGoals, awayGoals]);

  // The room's test vote lights the wristbands: room members show their own vote, the rest of the stand the room's split.
  const settledSeen = useRef<Record<string, number>>({});
  const testPoll: PartyPoll | undefined = st ? [...st.polls].reverse().find((p) => p.testId && (!p.outcome || now - (settledSeen.current[p.pollId] ??= now) < 7000)) : undefined;
  const yesShare = testPoll ? (() => {
    const total = testPoll.options.reduce((a, o) => a + o.votes, 0);
    return total ? (testPoll.options.find((o) => o.id === 'yes')?.votes ?? 0) / total : 0.5;
  })() : undefined;
  const right = testPoll?.outcome === 'met' ? 'yes' : testPoll?.outcome === 'notMet' ? 'no' : undefined;
  const bandOf = (s: Seat): { color: string; vote: string } | undefined => {
    if (!testPoll || yesShare === undefined) return undefined;
    if (s.memberId) {
      const v = testPoll.votes?.[s.memberId];
      return v ? { color: v === 'yes' ? BAND_YES : BAND_NO, vote: v } : undefined;
    }
    // Two fans in three in the stand join in; their colours follow the room's split.
    const h = hash(s.key + testPoll.pollId);
    if (h % 3 === 0) return undefined;
    const v = (h >> 4) % 100 < yesShare * 100 ? 'yes' : 'no';
    return { color: v === 'yes' ? BAND_YES : BAND_NO, vote: v };
  };

  const tifo = st?.tifo && now < st.tifo.endsAtMs ? st.tifo : undefined;
  const revealed = !!tifo && now >= tifo.revealAtMs;

  return (
    <svg className={`stands ${cheer ? `cheer-${cheer}` : ''} ${testPoll && !testPoll.outcome ? 'voting' : ''}`} viewBox={`0 0 ${VW} ${VH}`}
      preserveAspectRatio="xMidYMax meet" role="img"
      aria-label={roomMode ? (fi ? 'Katsomo: huoneen fanit eturivissä' : 'The stand: the room\'s fans in the front row') : (fi ? 'Katsomo' : 'The stand')}>
      <defs>
        <linearGradient id="st-roof" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#05080c" />
          <stop offset="1" stopColor="#0b1118" />
        </linearGradient>
        <filter id="st-glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="1.6" /></filter>
      </defs>
      {/* the stand's structure: roof, floodlight strip, back wall */}
      <polygon points={`70,8 ${VW - 70},8 ${VW - 10},${VH - 6} 10,${VH - 6}`} className="st-shell" />
      <rect x={70} y={4} width={VW - 140} height={10} fill="url(#st-roof)" />
      <line x1={78} x2={VW - 78} y1={15} y2={15} className="st-flood-glow" filter="url(#st-glow)" />
      <line x1={78} x2={VW - 78} y1={15} y2={15} className="st-flood" />
      {/* seat rows with a tint of the end's colour, and the aisles between sections */}
      {base.sections.map((row, r) => {
        const { s, y } = rowGeometry(r);
        return row.map((sec, k) => (
          <rect key={`${r}-${k}`} x={sec.x0 - 4 * s} y={y - 2 * s} width={sec.x1 - sec.x0 + 8 * s} height={12 * s} rx={1.5}
            className={`st-row st-row-${sec.side}`} />
        ));
      })}
      {seats.map((s) => {
        const h = hash(s.key);
        const band = bandOf(s);
        const won = !!right && band?.vote === right;
        const me = !!party?.me && s.memberId === party.me;
        // Most wear the kit; some a dark jacket.
        const shirt = h % 7 === 0 ? '#27313b' : s.side === 'home' ? 'var(--home-kit)' : 'var(--away-kit)';
        const scarf = s.side === 'home' ? 'var(--home-glow)' : 'var(--away-glow)';
        const scarved = h % 4 === 0 || !!s.memberId;
        return (
          <g key={s.key} transform={`translate(${s.x.toFixed(1)} ${s.y.toFixed(1)}) scale(${s.s.toFixed(3)})`}
            className={`st-fan side-${s.side} ${s.memberId ? 'member' : ''} ${me ? 'me' : ''} ${won ? 'won' : ''}`}>
            <g className="st-body" style={{ animationDelay: `${-(h % 2600)}ms` }}>
              <path d="M-8 2 Q-8.6 -8 -4.6 -10 L4.6 -10 Q8.6 -8 8 2 Z" fill={shirt} />
              <g className="st-arms" stroke={shirt} strokeWidth={2.6} strokeLinecap="round" fill="none">
                <path d="M-6.6 -8 Q-10 -14 -8.6 -21" />
                <path d="M6.6 -8 Q10 -14 8.6 -21" />
              </g>
              {scarved && <path d="M-4.6 -10.2 Q0 -6.6 4.6 -10.2 L4.2 -8.4 Q0 -5 -4.2 -8.4 Z" fill={scarf} />}
              <circle cx={0} cy={-14.4} r={4.7} fill={SKIN[h % SKIN.length]} />
              <path d={`M-4.7 -15.2 A4.7 4.7 0 0 1 4.7 -15.2 Q0 ${-17.4 - (h % 3)} -4.7 -15.2 Z`} fill={HAIR[(h >> 5) % HAIR.length]} />
              {band && (
                <g className="st-band" style={{ color: band.color, animationDelay: `${Math.round((s.x / VW) * 900)}ms` }}>
                  <circle cx={7.6} cy={-1} r={3.8} fill="currentColor" opacity={0.55} filter="url(#st-glow)" />
                  <rect x={5.8} y={-2.6} width={3.6} height={2.8} rx={0.8} fill="currentColor" />
                </g>
              )}
              {me && <ellipse cx={0} cy={3} rx={10} ry={3} className="st-me" />}
            </g>
            {s.name && <text y={13} textAnchor="middle" className={`st-name ${me ? 'me' : ''}`}>{s.name.length > 8 ? s.name.slice(0, 7) + '…' : s.name}</text>}
          </g>
        );
      })}
      {tifo && <TifoOverStand tifo={tifo} info={info} revealed={revealed} members={st?.members.filter((m) => m.teamId === tifo.teamId).length ?? 1} />}
    </svg>
  );
}

/** The mosaic over a club's two sections: cells appear as cards go up, all of it at the reveal. */
function TifoOverStand({ tifo, info, revealed, members }:
  { tifo: NonNullable<NonNullable<Party['state']>['tifo']>; info: MatchInfo; revealed: boolean; members: number }) {
  const side = tifo.teamId === info.home.teamId ? 'home' : 'away';
  const team = side === 'home' ? info.home : info.away;
  const total = TIFO_COLS * TIFO_ROWS;
  const order = useMemo(() => Array.from({ length: total }, (_, i) => i).sort((p, q) => hash(tifo.tifoId + p) - hash(tifo.tifoId + q)), [tifo.tifoId, total]);
  const shown = revealed ? total : Math.round(total * Math.min(1, tifo.holders.length / Math.max(1, members)) * 0.9);
  const visible = new Set(order.slice(0, shown));
  // The two sections of that end, from the back row to the front: a trapezoid, split into the mosaic's grid.
  const top = rowGeometry(0), bottom = rowGeometry(ROWS - 1);
  const span = (g: ReturnType<typeof rowGeometry>) => {
    const aisle = 26 * g.s, w = (VW - 2 * g.margin - 3 * aisle) / 4;
    return side === 'home' ? [g.margin, g.margin + 2 * w + aisle] : [g.margin + 2 * (w + aisle), VW - g.margin];
  };
  const [t0, t1] = span(top), [b0, b1] = span(bottom);
  const y0 = top.y - 22 * top.s, y1 = bottom.y + 4;
  const P = (u: number, v: number) => {
    const xl = t0 + (b0 - t0) * v, xr = t1 + (b1 - t1) * v;
    return `${(xl + (xr - xl) * u).toFixed(1)},${(y0 + (y1 - y0) * v).toFixed(1)}`;
  };
  const cells = [];
  for (let r = 0; r < TIFO_ROWS; r++)
    for (let c = 0; c < TIFO_COLS; c++) {
      const i = r * TIFO_COLS + c;
      if (!visible.has(i)) continue;
      const u0 = c / TIFO_COLS, u1 = (c + 1) / TIFO_COLS, v0 = r / TIFO_ROWS, v1 = (r + 1) / TIFO_ROWS;
      cells.push(<polygon key={i} points={`${P(u0, v0)} ${P(u1, v0)} ${P(u1, v1)} ${P(u0, v1)}`}
        fill={tifoColor(tifo.design, c, r, team.shortName, side)} className="st-tifo-cell" />);
    }
  return <g className={`st-tifo ${revealed ? 'revealed' : ''}`}>{cells}</g>;
}
