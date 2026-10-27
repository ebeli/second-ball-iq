import { useMemo, useState } from 'react';
import { api } from '../api';
import { clock } from '../format';
import { playerTeam } from '../kit';
import { ControlLayer } from './Space';
import { Pitch } from './Pitch';
import { TestChip } from './TacticalTest';
import type { CoachDecision, CoachProposal, MatchInfo, PitchEvent, TacticalTest, WhatIfChange, WhatIfView } from '../types';

const L = 105, W = 68;

/**
 * The Decision-1 coach's corner. For each team, two coach agents with opposite instincts propose a plan from a menu
 * of measurable plans; Decision-1 picks between them as a choice question with a probability for each; both plans
 * are locked as tactical tests and the match settles them. The record counts how often the pick was the plan that
 * held. Below, the what-if sandbox: move a team on the frame now, see what it does to pitch control, and ask the
 * same arbiter whether it would help.
 */
export function CoachCorner({ info, coaches, tests, events, clockSec, fi, live }:
  { info: MatchInfo; coaches: CoachDecision[]; tests: TacticalTest[]; events: PitchEvent[]; clockSec: number; fi?: boolean; live: boolean }) {
  const testById = useMemo(() => new Map(tests.map((t) => [t.testId, t])), [tests]);
  const record = useMemo(() => {
    const r = { right: 0, wrong: 0, level: 0, open: 0 };
    for (const d of coaches) {
      if (!d.picked) continue;
      if (d.verdict) r[d.verdict]++; else r.open++;
    }
    return r;
  }, [coaches]);
  const judged = record.right + record.wrong;

  return (
    <div className="cc">
      <section className="pq-block cc-record" aria-label={fi ? 'Decision-1:n ennätys' : "Decision-1's record"}>
        <div className="cc-record-main">
          <b className="num">{judged ? `${record.right}/${judged}` : '–'}</b>
          <span>{fi ? 'Decision-1 valitsi suunnitelman, joka piti' : 'Decision-1 picked the plan that held'}</span>
        </div>
        <div className="cc-record-cells">
          <span className="cc-right"><b className="num">{record.right}</b> {fi ? 'oikein' : 'right'}</span>
          <span className="cc-wrong"><b className="num">{record.wrong}</b> {fi ? 'väärin' : 'wrong'}</span>
          <span className="cc-level"><b className="num">{record.level}</b> {fi ? 'tasan' : 'level'}</span>
          <span className="cc-open"><b className="num">{record.open}</b> {fi ? 'käynnissä' : 'open'}</span>
        </div>
        <p className="fan-hint">
          {fi ? 'Oikein: valittu suunnitelma piti ja toinen ei. Väärin: päinvastoin. Tasan: molemmat pitivät tai kumpikaan ei. Määrä, ei kalibrointi.'
              : 'Right: the pick held and the other plan did not. Wrong: the other way round. Level: both held or neither did. A count, not a calibration.'}
        </p>
      </section>

      <div className="cc-teams">
        {[info.home, info.away].map((team, i) => (
          <TeamCorner key={team.teamId} side={i === 0 ? 'home' : 'away'} teamId={team.teamId} name={team.shortName}
            decisions={coaches.filter((d) => d.teamId === team.teamId)} testById={testById} clockSec={clockSec} fi={fi} live={live} />
        ))}
      </div>

      <WhatIfSandbox info={info} events={events} clockSec={clockSec} fi={fi} live={live} />
    </div>
  );
}

function TeamCorner({ side, teamId, name, decisions, testById, clockSec, fi, live }:
  { side: 'home' | 'away'; teamId: string; name: string; decisions: CoachDecision[]; testById: Map<string, TacticalTest>; clockSec: number; fi?: boolean; live: boolean }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string>();
  const sorted = [...decisions].sort((a, b) => b.atSec - a.atSec);
  const latest = sorted[0];
  const openNow = latest && Object.values(latest.testIds).some((id) => testById.get(id)?.status === 'open');

  const ask = async () => {
    setBusy(true); setNote(undefined);
    try { await api.coach(teamId); }
    catch (e) { setNote(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  return (
    <section className={`pq-block cc-team side-${side}`} aria-label={`${fi ? 'Valmentajien nurkka' : "Coach's corner"}: ${name}`}>
      <header className="cc-team-head">
        <h3><i className="cc-swatch" aria-hidden="true" />{name}</h3>
        {latest && <span className="cc-when num">{fi ? 'Lukittu' : 'Locked'} {clock(latest.atSec)}</span>}
      </header>
      {latest
        ? <Decision d={latest} testById={testById} clockSec={clockSec} fi={fi} />
        : <p className="fan-empty">{fi ? 'Valmentajat ehdottavat suunnitelmaa vartin välein. Voit myös kysyä heti.' : 'The coaches propose a plan every fifteen minutes. You can also ask now.'}</p>}
      <div className="cc-actions">
        <button type="button" className="cc-ask" disabled={!live || busy || !!openNow} onClick={() => void ask()}>
          {busy ? (fi ? 'Valmentajat miettivät…' : 'The coaches are thinking…') : (fi ? 'Kysy valmentajilta nyt' : 'Ask the coaches now')}
        </button>
        {openNow && <span className="cc-wait">{fi ? 'Edelliset suunnitelmat ovat vielä testissä.' : 'The last plans are still being tested.'}</span>}
        {note && <span className="cc-wait">{note}</span>}
      </div>
      {sorted.length > 1 && (
        <ol className="cc-history">
          {sorted.slice(1).map((d) => {
            const pick = d.proposals.find((p) => p.coach === d.picked);
            const title = (p?: CoachProposal) => (p ? d.menu.find((m) => m.kind === p.plan)?.[fi ? 'titleFi' : 'title'] ?? p.plan : '–');
            return (
              <li key={d.decisionId}>
                <span className="num">{clock(d.atSec)}</span>
                <span>{d.proposals.map((p) => title(p)).join(' / ')}</span>
                <span className="cc-hist-pick">{pick ? `→ ${title(pick)}` : (fi ? 'ei valintaa' : 'no pick')}</span>
                {d.verdict && <span className={`cc-verdict cc-${d.verdict}`}>{verdictText(d.verdict, fi)}</span>}
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}

function verdictText(v: 'right' | 'wrong' | 'level', fi?: boolean) {
  return v === 'right' ? (fi ? 'Decision-1 oikeassa' : 'Decision-1 right') : v === 'wrong' ? (fi ? 'Decision-1 väärässä' : 'Decision-1 wrong') : (fi ? 'Tasan' : 'Level');
}

function Decision({ d, testById, clockSec, fi }: { d: CoachDecision; testById: Map<string, TacticalTest>; clockSec: number; fi?: boolean }) {
  return (
    <>
      <div className="cc-plans">
        {d.proposals.map((p) => {
          const plan = d.menu.find((m) => m.kind === p.plan);
          const test = testById.get(d.testIds[p.coach]);
          const prob = d.probabilities?.[p.coach];
          const picked = d.picked === p.coach;
          return (
            <article key={p.coach} className={`cc-plan ${picked ? 'picked' : ''} ${test ? `test-${test.status}` : ''}`}>
              <header>
                <span className="cc-coach">{p.coachName}{!p.fromModel && <small> · {fi ? 'sääntö' : 'rule'}</small>}</span>
                {picked && <span className="cc-pick">{fi ? 'VALINTA' : 'PICK'}</span>}
              </header>
              <h4>{plan ? (fi ? plan.titleFi : plan.title) : p.plan}</h4>
              <p className="cc-why">“{p.rationale}”</p>
              {prob !== undefined && prob !== null && (
                <div className="cc-prob" aria-label={fi ? 'Decision-1:n todennäköisyys' : 'Decision-1 probability'}>
                  <span className="cc-bar"><i style={{ width: `${Math.round(prob * 100)}%` }} /></span>
                  <b className="num">{Math.round(prob * 100)}%</b>
                </div>
              )}
              {plan && <p className="cc-criterion">{fi ? plan.criterionFi : plan.criterion}</p>}
              {test && (
                <div className="cc-test">
                  <TestChip test={test} fi={fi} />
                  <span className="cc-progress">{fi ? test.progressTextFi : test.progressText}</span>
                  {test.status === 'open' && <span className="cc-left num">{Math.max(0, Math.ceil((test.deadlineSec - clockSec) / 60))}′</span>}
                </div>
              )}
            </article>
          );
        })}
      </div>
      <p className="cc-source">
        {d.source === 'Decision-1'
          ? (fi ? 'Decision-1 valitsi valintakysymyksellä: kumpi suunnitelma pitää seuraavat kymmenen minuuttia? Mallin arvio, ei kalibroitu.'
                : 'Decision-1 chose with a choice question: which plan will hold for the next ten minutes? A model estimate, not calibrated.')
          : d.source === 'unavailable'
            ? (fi ? 'Decision-1 ei vastannut ajoissa: ei valintaa, mutta molemmat suunnitelmat testataan.' : 'Decision-1 did not answer in time: no pick, but both plans are still tested.')
            : (fi ? 'Ilman Decision-1:tä ei valintaa; molemmat suunnitelmat testataan silti.' : 'Without Decision-1 there is no pick; both plans are still tested.')}
        {d.verdict && <> <span className={`cc-verdict cc-${d.verdict}`}>{verdictText(d.verdict, fi)}</span></>}
      </p>
    </>
  );
}

const CHANGES: [WhatIfChange, string, string][] = [
  ['pushUp', 'Push up', 'Nouse ylemmäs'],
  ['dropDeep', 'Drop deep', 'Vetäydy'],
  ['widen', 'Widen', 'Levennä'],
  ['narrow', 'Narrow', 'Kavenna'],
  ['pressTheBall', 'Press the ball', 'Prässää palloa'],
];

function WhatIfSandbox({ info, events, clockSec, fi, live }: { info: MatchInfo; events: PitchEvent[]; clockSec: number; fi?: boolean; live: boolean }) {
  const [teamId, setTeamId] = useState(info.home.teamId);
  const [change, setChange] = useState<WhatIfChange>('pushUp');
  const [metres, setMetres] = useState(8);
  const [result, setResult] = useState<WhatIfView>();
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string>();

  const run = async () => {
    setBusy(true); setNote(undefined);
    try {
      const r = await api.whatIf(teamId, change, metres);
      if (!r) setNote(fi ? 'Seurantadata ei ole vielä valmis.' : 'Tracking is not ready yet.');
      setResult(r ?? undefined);
    } catch (e) { setNote(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  const team = (id: string) => (id === info.home.teamId ? info.home : info.away);
  const pct = (v: number) => `${Math.round(v * 100)}%`;
  const delta = (a: number, b: number) => {
    const d = Math.round(b * 100) - Math.round(a * 100);
    return <span className={`cc-delta ${d > 0 ? 'up' : d < 0 ? 'down' : ''}`}>{d > 0 ? '+' : ''}{d} pts</span>;
  };
  const side = result ? (result.teamId === info.home.teamId ? 'home' : 'away') : 'home';

  return (
    <section className="pq-block cc-whatif" aria-label={fi ? 'Entä jos -hiekkalaatikko' : 'What-if sandbox'}>
      <h3>{fi ? 'Entä jos?' : 'What if?'}</h3>
      <div className="cc-controls">
        <div className="an-tabs small" role="tablist" aria-label={fi ? 'Joukkue' : 'Team'}>
          {[info.home, info.away].map((t) => (
            <button key={t.teamId} type="button" role="tab" aria-selected={teamId === t.teamId} className={teamId === t.teamId ? 'on' : ''} onClick={() => setTeamId(t.teamId)}>{t.shortName}</button>
          ))}
        </div>
        <div className="an-tabs small" role="tablist" aria-label={fi ? 'Muutos' : 'Change'}>
          {CHANGES.map(([k, en, fiLabel]) => (
            <button key={k} type="button" role="tab" aria-selected={change === k} className={change === k ? 'on' : ''} onClick={() => setChange(k)}>{fi ? fiLabel : en}</button>
          ))}
        </div>
        <label className="cc-metres">
          <span>{change === 'widen' || change === 'narrow' ? (fi ? 'Määrä' : 'Amount') : (fi ? 'Metriä' : 'Metres')}</span>
          <input type="range" min={2} max={15} step={1} value={metres} onChange={(e) => setMetres(Number(e.target.value))} />
          <b className="num">{metres}</b>
        </label>
        <button type="button" className="cc-ask" disabled={!live || busy} onClick={() => void run()}>
          {busy ? (fi ? 'Lasketaan…' : 'Working it out…') : (fi ? 'Kokeile tilannetta nyt' : 'Try it on the frame now')}
        </button>
        {note && <span className="cc-wait">{note}</span>}
      </div>

      {result && (
        <>
          <div className="cc-maps">
            {(['before', 'after'] as const).map((k) => (
              <figure key={k}>
                <figcaption>{k === 'before' ? (fi ? 'Nyt' : 'Now') : (fi ? 'Muutoksen jälkeen' : 'After the change')} · <span className="num">{clock(Math.floor(result.t))}</span></figcaption>
                <Pitch events={events} clockSec={clockSec} info={info} compact label={fi ? 'Kenttähallinta' : 'Pitch control'}>
                  <ControlLayer grid={k === 'before' ? result.homeBefore : result.homeAfter} cols={result.cols} rows={result.rows} />
                  {(result.others ?? []).map((m) => (
                    <circle key={m.playerId} cx={(m.fromX / 100) * L} cy={(m.fromY / 100) * W} r={0.95} className={`cc-dot still side-${playerTeam(info, m.playerId)?.side ?? 'home'}`} />
                  ))}
                  {result.moved.map((m) => {
                    const x0 = (m.fromX / 100) * L, y0 = (m.fromY / 100) * W, x1 = (m.toX / 100) * L, y1 = (m.toY / 100) * W;
                    return k === 'before'
                      ? <circle key={m.playerId} cx={x0} cy={y0} r={1.1} className={`cc-dot side-${side}`} />
                      : (
                        <g key={m.playerId}>
                          <line x1={x0} y1={y0} x2={x1} y2={y1} className="cc-move" />
                          <circle cx={x0} cy={y0} r={0.7} className="cc-ghost" />
                          <circle cx={x1} cy={y1} r={1.1} className={`cc-dot side-${side}`}>
                            <title>{playerTeam(info, m.playerId)?.player.name ?? m.playerId}</title>
                          </circle>
                        </g>
                      );
                  })}
                </Pitch>
              </figure>
            ))}
          </div>
          <div className="cc-deltas">
            <div><span>{fi ? 'Hyökkäyskolmannes' : 'Final third they attack'}</span><b className="num">{pct(result.finalThirdBefore)} → {pct(result.finalThirdAfter)}</b>{delta(result.finalThirdBefore, result.finalThirdAfter)}</div>
            <div><span>{fi ? 'Puolustuskolmannes' : 'Third they defend'}</span><b className="num">{pct(result.ownThirdBefore)} → {pct(result.ownThirdAfter)}</b>{delta(result.ownThirdBefore, result.ownThirdAfter)}</div>
            <div><span>{fi ? 'Koko kenttä' : 'Whole pitch'}</span><b className="num">{pct(result.controlBefore)} → {pct(result.controlAfter)}</b>{delta(result.controlBefore, result.controlAfter)}</div>
            <div className="cc-help">
              <span>{fi ? `Auttaisiko tämä ${team(result.teamId).shortName}a? Decision-1` : `Would this help ${team(result.teamId).shortName}? Decision-1`}</span>
              <b className="num">{result.helpProbability !== undefined && result.helpProbability !== null ? pct(result.helpProbability) : '–'}</b>
              <small>{result.source === 'Decision-1' ? (fi ? 'kyllä/ei-kysymys, mallin arvio' : 'yes/no question, model estimate')
                : result.source === 'unavailable' ? (fi ? 'ei vastannut ajoissa' : 'did not answer in time') : (fi ? 'ei käytössä' : 'not connected')}</small>
            </div>
          </div>
        </>
      )}
      <p className="fan-hint">
        {fi ? 'Pysäytyskuva: valittu joukkue siirretään tämän hetken seurantakuvassa, kaikki muut pysyvät paikoillaan ja pelaajat säilyttävät nopeutensa. Näyttää, miten tila muuttuu, ei sitä, mitä seuraavaksi tapahtuisi.'
            : 'A still picture: the team is moved on the tracking frame now; everyone else stays put and players keep their speed. It shows how the space changes, not what would happen next.'}
      </p>
    </section>
  );
}
