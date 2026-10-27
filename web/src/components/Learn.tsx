import { useState } from 'react';
import { conceptById, concepts, type Concept, type Diagram } from '../fan/glossary';

/** A tiny chalk-board drawing of a concept. Attackers (yellow-ringed) play left to right. */
export function ConceptDiagram({ kind }: { kind: Diagram }) {
  const A = (x: number, y: number, k?: string) => <circle key={k ?? `a${x}-${y}`} cx={x} cy={y} r={2.2} className="cd-att" />;
  const D = (x: number, y: number, k?: string) => <circle key={k ?? `d${x}-${y}`} cx={x} cy={y} r={2.2} className="cd-def" />;
  const B = (x: number, y: number) => <circle key={`b${x}-${y}`} cx={x} cy={y} r={1.2} className="cd-ball" />;
  const Arr = (x1: number, y1: number, x2: number, y2: number, dash?: boolean) =>
    <line key={`l${x1}-${y1}-${x2}-${y2}`} x1={x1} y1={y1} x2={x2} y2={y2} className={`cd-arrow ${dash ? 'dash' : ''}`} markerEnd="url(#cd-head)" />;
  const T = (x: number, y: number, s: string) => <text key={`t${x}-${y}`} x={x} y={y} className="cd-text" textAnchor="middle">{s}</text>;

  const art: Record<Diagram, JSX.Element[]> = {
    goal: [A(70, 30), B(74, 32), Arr(76, 33, 102, 34), D(98, 34)],
    xg: [A(92, 34, 'near'), T(92, 27, '0.40'), A(68, 20, 'far'), T(68, 13, '0.03'), Arr(94, 34, 103, 34), Arr(70, 21, 103, 32, true)],
    possession: [A(30, 20), A(40, 45), A(50, 25), A(45, 55), Arr(32, 21, 48, 25), Arr(50, 27, 41, 44), Arr(43, 46, 45, 53), D(80, 30), D(82, 45), D(78, 15), D(84, 55)],
    finalThird: [<rect key="ft" x={70} y={0} width={35} height={68} className="cd-zone" />, A(76, 30), Arr(55, 40, 74, 31), T(87, 64, 'final third')],
    press: [D(80, 30), A(84, 28), A(78, 38), A(74, 22), B(81, 30), Arr(70, 22, 78, 28), Arr(76, 40, 79, 33), Arr(88, 20, 83, 27)],
    regain: [<rect key="z" x={70} y={0} width={35} height={68} className="cd-zone" />, A(82, 34), B(84, 35), T(82, 27, 'won!')],
    counter: [A(50, 34), B(52, 35), Arr(54, 35, 88, 32), A(90, 31), D(60, 15), D(62, 55), Arr(60, 17, 75, 20, true), Arr(62, 53, 76, 48, true)],
    corner: [B(104, 1), Arr(103, 2, 96, 28), Arr(103, 2, 94, 42, true), A(96, 30), A(93, 40), D(99, 34)],
    freeKick: [B(76, 34), D(85, 30), D(85, 34), D(85, 38), Arr(77, 33, 104, 28)],
    penalty: [<circle key="spot" cx={94} cy={34} r={0.8} className="cd-ball" />, A(92, 34), Arr(95, 34, 104, 30), D(103, 34)],
    formation: [A(25, 10), A(20, 26), A(20, 42), A(25, 58), A(45, 20), A(42, 34), A(45, 48), A(70, 12), A(75, 34), A(70, 56), T(50, 66, '4 · 3 · 3')],
    falseNine: [A(75, 34, 'f9s'), Arr(73, 34, 58, 34), A(56, 34, 'f9e'), D(85, 28), D(85, 40), T(66, 28, 'drops back')],
    invertedWinger: [A(70, 6), Arr(72, 8, 86, 26), A(88, 28), D(95, 34)],
    wingBack: [A(20, 62), Arr(22, 62, 75, 62), A(78, 62), D(20, 30), D(18, 40), D(20, 50)],
    shadowStriker: [A(85, 30), A(85, 40), A(62, 34, 'ss'), Arr(64, 34, 90, 35), D(92, 28), D(92, 42)],
    secondStriker: [A(88, 34, 'st'), A(78, 42, 'ss'), D(94, 30), D(94, 40), T(78, 50, '2nd')],
    lowBlock: [D(20, 15), D(20, 29), D(20, 43), D(20, 57), D(32, 10), D(32, 26), D(32, 42), D(32, 58), A(55, 30), A(60, 45), Arr(40, 3, 40, 65, true)],
    direct: [A(20, 34), Arr(22, 34, 84, 30), A(86, 30), D(50, 30), D(52, 42)],
    substitution: [A(52, 66, 'on'), Arr(52, 64, 52, 50), D(58, 50, 'off'), Arr(58, 52, 58, 66, true), T(52, 46, 'on'), T(62, 46, 'off')],
    passing: [A(30, 30), A(45, 20), A(55, 40), Arr(32, 29, 43, 21), Arr(46, 22, 53, 38), Arr(56, 41, 75, 55, true), T(76, 62, 'lost')],
    shape: [A(25, 12), A(20, 30), A(20, 40), A(25, 56), A(45, 34), A(60, 22), A(60, 46), A(75, 34), A(72, 10), A(72, 58)],
    heat: [<ellipse key="h1" cx={70} cy={18} rx={16} ry={9} className="cd-heat" />, <ellipse key="h2" cx={55} cy={40} rx={11} ry={8} className="cd-heat dim" />],
    // Each side's space: the attackers own the spots they would reach first, the defenders the rest.
    pitchControl: [<rect key="pc1" x={60} y={0} width={45} height={34} className="cd-zone" />, <rect key="pc2" x={60} y={34} width={45} height={34} className="cd-heat dim" />,
      A(72, 20), A(85, 26), D(82, 46), D(94, 40), T(80, 10, 'ours'), T(88, 62, 'theirs')],
    threat: [A(50, 40), Arr(52, 39, 66, 30, true), A(68, 29, 'safe'), Arr(52, 41, 88, 38), A(90, 38, 'better'), D(78, 32), T(68, 23, '+0.01'), T(90, 31, '+0.05')],
  };

  return (
    <svg className="concept-diagram" viewBox="-2 -2 109 72" role="img" aria-hidden="true">
      <defs>
        <marker id="cd-head" viewBox="0 0 6 6" refX="5" refY="3" markerWidth="4" markerHeight="4" orient="auto-start-reverse">
          <path d="M0,0 L6,3 L0,6 z" fill="var(--on-air)" />
        </marker>
      </defs>
      <rect x={-2} y={-2} width={109} height={72} fill="var(--board)" />
      <g className="cd-lines">
        <rect x={0} y={0} width={105} height={68} />
        <line x1={52.5} y1={0} x2={52.5} y2={68} />
        <circle cx={52.5} cy={34} r={9.15} />
        <rect x={0} y={13.84} width={16.5} height={40.32} />
        <rect x={88.5} y={13.84} width={16.5} height={40.32} />
        <rect x={105} y={30.34} width={1.5} height={7.32} />
        <rect x={-1.5} y={30.34} width={1.5} height={7.32} />
      </g>
      {art[kind]}
    </svg>
  );
}

/** A concept's explanation, with its diagram and an optional link to see it in this match. */
export function ConceptCard({ c, fi, onSeeIt, onClose }: { c: Concept; fi?: boolean; onSeeIt?: () => void; onClose?: () => void }) {
  const t = fi ? c.fi : c.en;
  return (
    <div className="concept">
      <ConceptDiagram kind={c.diagram} />
      <div className="concept-text">
        <p className="concept-term">{t.term}</p>
        <p>{t.long}</p>
        <div className="concept-actions">
          {onSeeIt && <button type="button" className="fc-replay" onClick={onSeeIt}>{fi ? 'Katso tästä ottelusta' : 'See it in this match'}</button>}
          {onClose && <button type="button" className="link" onClick={onClose}>{fi ? 'Sulje' : 'Close'}</button>}
        </div>
      </div>
    </div>
  );
}

/** "Learn" chips under a card: tap one to open the explanation in place. */
export function LearnChips({ ids, fi, onSeeIt }: { ids: string[]; fi?: boolean; onSeeIt?: () => void }) {
  const [open, setOpen] = useState<string>();
  const list = ids.map((id) => conceptById[id]).filter(Boolean);
  if (list.length === 0) return null;
  const current = open ? conceptById[open] : undefined;
  return (
    <div className="learn">
      <div className="learn-chips">
        <span className="learn-label">{fi ? 'Opi' : 'Learn'}</span>
        {list.map((c) => (
          <button key={c.id} type="button" className="learn-chip" aria-pressed={open === c.id}
            onClick={() => setOpen(open === c.id ? undefined : c.id)}>
            {fi ? c.fi.term : c.en.term}
          </button>
        ))}
      </div>
      {current && <ConceptCard c={current} fi={fi} onSeeIt={onSeeIt} onClose={() => setOpen(undefined)} />}
    </div>
  );
}

/** The whole glossary, with what has already come up in this match marked. */
export function LearnTab({ seen, fi }: { seen: Set<string>; fi?: boolean }) {
  const [open, setOpen] = useState<string>('basics');
  const ordered = [...concepts].sort((a, b) => Number(seen.has(b.id)) - Number(seen.has(a.id)));
  return (
    <div className="learn-tab">
      <p className="fan-hint">
        {fi ? 'Jalkapallon käsitteet lyhyesti. Merkityt ovat jo näkyneet tässä ottelussa.' : 'Football explained in a few lines. The ones marked have already come up in this match.'}
      </p>
      {ordered.map((c) => (
        <div key={c.id} className={`learn-item ${open === c.id ? 'open' : ''}`}>
          <button type="button" className="learn-item-head" aria-expanded={open === c.id} onClick={() => setOpen(open === c.id ? '' : c.id)}>
            <span>{fi ? c.fi.term : c.en.term}</span>
            {seen.has(c.id) && <span className="learn-seen">{fi ? 'tässä ottelussa' : 'in this match'}</span>}
            <span className="learn-short">{fi ? c.fi.short : c.en.short}</span>
          </button>
          {open === c.id && <ConceptCard c={c} fi={fi} />}
        </div>
      ))}
    </div>
  );
}
