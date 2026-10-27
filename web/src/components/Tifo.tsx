import { useEffect, useState, type ReactNode } from 'react';
import type { Party } from '../party/useParty';
import type { MatchInfo, PartyTifo } from '../types';

/**
 * Digital tifos: one end of the stand raises cards together and, at the reveal, shows a mosaic in the club's colours.
 * Designs are our own, drawn from the kit (stripes, a shirt, a star, the club's short name). The mosaic itself is drawn
 * over the stand on the match stage (Stands.tsx); this file has the controls and the designs.
 */

export const TIFO_COLS = 26, TIFO_ROWS = 12;
const COLS = TIFO_COLS, ROWS = TIFO_ROWS;

/** Start a tifo for your end, or raise your card for the one going up. */
export function TifoControls({ party, info, fi }: { party: Party; info: MatchInfo; fi?: boolean }) {
  const st = party.state!;
  const me = st.members.find((m) => m.memberId === party.me);
  const now = useNow(st.tifo ? 500 : 0) + party.skewMs;
  const tifo = st.tifo && now < st.tifo.endsAtMs ? st.tifo : undefined;
  const [note, setNote] = useState<string>();
  const designs: [PartyTifo['design'], string, string][] = [
    ['stripes', 'Stripes', 'Raidat'], ['shirt', 'Shirt', 'Paita'], ['star', 'Star', 'Tähti'], ['letters', 'Name', 'Nimi'],
  ];
  if (!me) return null;
  if (!me.teamId) {
    return <p className="tifo-note">{fi ? 'Tifot kuuluvat kannattajille: valitse joukkue, kun liityt juhliin.' : 'Tifos belong to one end: pick a club when you join to raise one.'}</p>;
  }
  const side = me.teamId === info.home.teamId ? 'home' : 'away';
  if (tifo) {
    const mine = tifo.teamId === me.teamId;
    const holding = tifo.holders.includes(me.memberId);
    const revealed = now >= tifo.revealAtMs;
    return (
      <div className={`tifo-bar side-${side}`}>
        <span>{revealed ? (fi ? 'Tifo on ylhäällä!' : 'The tifo is up!') : mine ? (fi ? 'Päätysi tekee tifoa' : 'Your end is building a tifo') : (fi ? 'Vastapuoli tekee tifoa' : 'The other end is building a tifo')}</span>
        {mine && !revealed && (
          <button type="button" className="primary" disabled={holding} onClick={party.holdTifo}>
            {holding ? (fi ? 'Korttisi on ylhäällä' : 'Your card is up') : (fi ? 'Nosta korttisi' : 'Raise your card')}
          </button>
        )}
      </div>
    );
  }
  return (
    <div className={`tifo-bar side-${side}`}>
      <span>{fi ? 'Digitaalinen tifo päätysi kanssa:' : 'Raise a digital tifo with your end:'}</span>
      <span className="tifo-designs">
        {designs.map(([d, en, fiLabel]) => (
          <button key={d} type="button" onClick={async () => setNote((await party.startTifo(d, fi ? 'fi' : 'en')) ?? undefined)}>
            <TifoSwatch design={d} side={side} info={info} /> {fi ? fiLabel : en}
          </button>
        ))}
      </span>
      {note && <span className="tifo-note">{note}</span>}
    </div>
  );
}


export function useNow(everyMs: number) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!everyMs) return;
    const t = window.setInterval(() => setNow(Date.now()), everyMs);
    return () => window.clearInterval(t);
  }, [everyMs]);
  return everyMs ? now : Date.now();
}


export function hash(s: string) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return h >>> 0;
}

export function TifoSwatch({ design, side, info }: { design: PartyTifo['design']; side: 'home' | 'away'; info: MatchInfo }) {
  const team = side === 'home' ? info.home : info.away;
  const cells: ReactNode[] = [];
  for (let r = 0; r < ROWS; r++)
    for (let c = 0; c < COLS; c++)
      cells.push(<rect key={`${r}-${c}`} x={c} y={r} width={1.02} height={1.02} fill={tifoColor(design, c, r, team.shortName, side)} />);
  return <svg className="tifo-swatch" viewBox={`0 0 ${COLS} ${ROWS}`} aria-hidden="true">{cells}</svg>;
}

/** Each design is a rule from a cell to one of the kit's colours; the letters come from a 3 × 5 pixel font. */
export function tifoColor(design: PartyTifo['design'], c: number, r: number, short: string, side: 'home' | 'away'): string {
  const shirt = `var(--${side}-kit)`, ink = `var(--${side}-ink)`, trim = `var(--${side}-trim)`;
  // Normalised: x from -1 (left) to 1 (right), y from 0 (top row) to 1 (bottom row).
  const x = (c + 0.5) / COLS * 2 - 1, y = (r + 0.5) / ROWS;
  switch (design) {
    case 'stripes':
      return Math.floor(c / 3) % 2 === 0 ? shirt : ink;
    case 'shirt': {
      const w = Math.abs(x);
      const collar = y < 0.12 && w < 0.12;
      const inShirt = !collar && ((y < 0.42 && w < 0.62) || (y >= 0.42 && y < 0.95 && w < 0.36));
      return inShirt ? (y < 0.16 && w < 0.22 ? trim : shirt) : ink;
    }
    case 'star': {
      const cy = (y - 0.5) * 2 * (ROWS / COLS) * 1.9;
      const ang = Math.atan2(cy, x), rad = Math.hypot(x, cy);
      const k = Math.cos(5 * (ang + Math.PI / 2)) * 0.5 + 0.5;
      return rad < 0.32 + 0.38 * k ? shirt : ink;
    }
    case 'letters': {
      // The club's short name in a 3 × 5 pixel font at double size, centred.
      const text = short.slice(0, 3).toUpperCase();
      const width = text.length * 8 - 2, left = Math.floor((COLS - width) / 2), top = Math.floor((ROWS - 10) / 2);
      const col = c - left, row = r - top;
      if (row >= 0 && row < 10 && col >= 0 && col < width && col % 8 < 6) {
        const glyph = FONT[text[Math.floor(col / 8)]] ?? FONT['?'];
        if (glyph[Math.floor(row / 2)][Math.floor((col % 8) / 2)] === '#') return ink;
      }
      return shirt;
    }
  }
}

const FONT: Record<string, string[]> = {
  A: ['.#.', '#.#', '###', '#.#', '#.#'], B: ['##.', '#.#', '##.', '#.#', '##.'], C: ['.##', '#..', '#..', '#..', '.##'],
  D: ['##.', '#.#', '#.#', '#.#', '##.'], E: ['###', '#..', '##.', '#..', '###'], F: ['###', '#..', '##.', '#..', '#..'],
  G: ['.##', '#..', '#.#', '#.#', '.##'], H: ['#.#', '#.#', '###', '#.#', '#.#'], I: ['###', '.#.', '.#.', '.#.', '###'],
  J: ['..#', '..#', '..#', '#.#', '.#.'], K: ['#.#', '#.#', '##.', '#.#', '#.#'], L: ['#..', '#..', '#..', '#..', '###'],
  M: ['#.#', '###', '###', '#.#', '#.#'], N: ['##.', '#.#', '#.#', '#.#', '#.#'], O: ['.#.', '#.#', '#.#', '#.#', '.#.'],
  P: ['##.', '#.#', '##.', '#..', '#..'], Q: ['.#.', '#.#', '#.#', '##.', '.##'], R: ['##.', '#.#', '##.', '#.#', '#.#'],
  S: ['.##', '#..', '.#.', '..#', '##.'], T: ['###', '.#.', '.#.', '.#.', '.#.'], U: ['#.#', '#.#', '#.#', '#.#', '###'],
  V: ['#.#', '#.#', '#.#', '#.#', '.#.'], W: ['#.#', '#.#', '###', '###', '#.#'], X: ['#.#', '#.#', '.#.', '#.#', '#.#'],
  Y: ['#.#', '#.#', '.#.', '.#.', '.#.'], Z: ['###', '..#', '.#.', '#..', '###'], '?': ['###', '..#', '.#.', '...', '.#.'],
};
