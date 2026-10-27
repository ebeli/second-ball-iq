/**
 * Our own mark: the second ball. A ball goes up for the first contest (the ring at the start of the arc) and drops
 * where the second ball lands (the solid ball), the loose ball the game is often decided on. Chalk green on the
 * pitch-dark tile, no third-party marks. Also used as the favicon (index.html).
 */
export function Mark({ size = 26 }: { size?: number }) {
  return (
    <svg className="brand-mark" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
      <rect x="1.5" y="1.5" width="29" height="29" rx="4" fill="#0b1118" stroke="#2d4538" />
      <line x1="5" y1="25.5" x2="27" y2="25.5" stroke="#2d4538" strokeWidth="1.4" />
      <path d="M8 21.5 Q14.5 4 22.5 18.5" fill="none" stroke="#42e2a2" strokeWidth="1.6" strokeDasharray="2.2 2" strokeLinecap="round" />
      <circle cx="8" cy="21.5" r="2.4" fill="none" stroke="#42e2a2" strokeWidth="1.3" opacity="0.7" />
      <circle cx="23.5" cy="21.5" r="3.8" fill="#42e2a2" />
    </svg>
  );
}

/** The name, with IQ picked out in chalk green. */
export function Wordmark() {
  return <>Second Ball <b className="wm-iq">IQ</b></>;
}

/** A score digit that rolls in when it changes (a goal). Remounts on a new value, so the CSS animation replays. */
export function Roll({ value, className }: { value: number | string; className?: string }) {
  return (
    <span className={`roll-box ${className ?? ''}`}>
      <span className="roll" key={String(value)}>{value}</span>
    </span>
  );
}
