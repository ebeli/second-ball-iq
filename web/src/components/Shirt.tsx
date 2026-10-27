/**
 * A player's shirt in the club's kit with the squad number on it. Sizes in px; the colours come from the
 * --home-* / --away-* kit variables, so a shirt always matches the rest of the page.
 */
export function Shirt({ side, number, size = 32, star, title }:
  { side: 'home' | 'away'; number?: number | string; size?: number; star?: boolean; title?: string }) {
  const n = number === undefined ? '' : String(number);
  return (
    <svg className={`shirt-svg shirt-${side}`} width={size} height={size} viewBox="0 0 40 40" role={title ? 'img' : undefined}
      aria-label={title} aria-hidden={title ? undefined : true}>
      {title && <title>{title}</title>}
      <path className="shirt-body"
        d="M14 4 L8 6.5 L2 13 L6.5 18.5 L10 16 L10 36 Q10 37 11 37 L29 37 Q30 37 30 36 L30 16 L33.5 18.5 L38 13 L32 6.5 L26 4 Q20 9 14 4 Z" />
      <path className="shirt-trim" d="M14 4 Q20 9 26 4 L24.4 3.4 Q20 7 15.6 3.4 Z" />
      <path className="shirt-trim" d="M2 13 L6.5 18.5 L7.9 17.4 L3.5 12 Z M38 13 L33.5 18.5 L32.1 17.4 L36.5 12 Z" />
      {n && (
        <text className="shirt-num" x="20" y="28.5" textAnchor="middle" fontSize={n.length > 1 ? 15 : 17}>{n}</text>
      )}
      {star && <circle className="shirt-star" cx="33" cy="33" r="5.5" />}
      {star && <text className="shirt-star-mark" x="33" y="35.6" textAnchor="middle" fontSize="8">★</text>}
    </svg>
  );
}

/** The same kit on the board: a disc in the shirt colour with the number, ringed in the bright board colour. */
export function Token({ side, number, x, y, r = 2.3, focus, label }:
  { side: 'home' | 'away'; number: number | string; x: number; y: number; r?: number; focus?: boolean; label?: string }) {
  return (
    <g className={`token token-${side} ${focus ? 'is-focus' : ''}`} transform={`translate(${x} ${y})`}>
      <circle r={r} className="token-disc" />
      <text className="token-num" textAnchor="middle" dy={r * 0.36} fontSize={r * 1.05}>{number}</text>
      {label && <text className="token-label" textAnchor="middle" dy={r + 2.4}>{label}</text>}
    </g>
  );
}
