import { Wordmark } from './Brand';
import { Shirt } from './Shirt';
import { useState } from 'react';
import { forLevel, type Depth, type Focus, type Level, type Prefs } from '../fan/prefs';
import type { MatchInfo } from '../types';

interface Props {
  prefs: Prefs;
  info?: MatchInfo;
  /** First run: three short steps. Later: everything on one sheet. */
  first: boolean;
  onDone: (p: Prefs, startListening: boolean) => void;
  onCancel?: () => void;
}

const LEVELS: { id: Level; en: [string, string]; fi: [string, string] }[] = [
  { id: 'beginner', en: ['New to football', 'Plain words, and every term explained as it comes up.'],
    fi: ['Uusi jalkapallossa', 'Selkeät sanat, ja jokainen termi selitetään kun se tulee vastaan.'] },
  { id: 'casual', en: ['I watch now and then', 'The story of the match, the big moments, no jargon.'],
    fi: ['Katson silloin tällöin', 'Ottelun tarina ja isot hetket, ilman ammattitermejä.'] },
  { id: 'hardcore', en: ['I live for tactics', 'Pressing, xG, shapes and roles, with the evidence.'],
    fi: ['Elän taktiikalle', 'Prässi, xG, muodot ja roolit, todisteineen.'] },
];

const DEPTHS: { id: Depth; en: string; fi: string; hint: [string, string] }[] = [
  { id: 'quick', en: 'Quick', fi: 'Lyhyt', hint: ['Headlines and big moments.', 'Otsikot ja isot hetket.'] },
  { id: 'explain', en: 'Explain', fi: 'Selitä', hint: ['Plain language and context.', 'Selkeä kieli ja taustat.'] },
  { id: 'tactical', en: 'Tactical', fi: 'Taktinen', hint: ['Metrics, evidence and analysis.', 'Luvut, todisteet ja analyysi.'] },
];

/** Choosing how to follow the match: the first-run setup, and the settings sheet afterwards. */
export function Setup({ prefs, info, first, onDone, onCancel }: Props) {
  const [p, setP] = useState<Prefs>(prefs);
  const [step, setStep] = useState(0);
  const fi = p.language === 'fi';
  const players = info ? [info.home, info.away] : [];

  const level = (
    <section className="setup-sec">
      <h2>{fi ? 'Kuinka hyvin tunnet jalkapallon?' : 'How well do you know football?'}</h2>
      <div className="setup-cards">
        {LEVELS.map((l) => (
          <button key={l.id} type="button" className="setup-card" aria-pressed={p.level === l.id} onClick={() => setP(forLevel(p, l.id))}>
            <strong>{(fi ? l.fi : l.en)[0]}</strong>
            <span>{(fi ? l.fi : l.en)[1]}</span>
          </button>
        ))}
      </div>
      {!first && (
        <>
          <h3>{fi ? 'Selitystaso' : 'Explain level'}</h3>
          <div className="setup-seg">
            {DEPTHS.map((d) => (
              <button key={d.id} type="button" aria-pressed={p.depth === d.id} onClick={() => setP({ ...p, depth: d.id })}>
                {fi ? d.fi : d.en}
              </button>
            ))}
          </div>
          <p className="fan-hint">{DEPTHS.find((d) => d.id === p.depth)!.hint[fi ? 1 : 0]}</p>
          <label className="setup-toggle">
            <input type="checkbox" checked={p.learn} onChange={(e) => setP({ ...p, learn: e.target.checked })} />
            <span><strong>{fi ? 'Opi peli' : 'Learn the Game'}</strong> · {fi ? 'jalkapallon käsitteet selitetään korteissa' : 'football terms explained on every card'}</span>
          </label>
        </>
      )}
    </section>
  );

  const focus = (
    <section className="setup-sec">
      <h2>{fi ? 'Mikä sinua kiinnostaa?' : 'What do you care about?'}</h2>
      <div className="setup-seg wide">
        {([['match', 'Whole match', 'Koko ottelu'], ['club', 'My club', 'Joukkueeni'], ['player', 'My player', 'Pelaajani']] as [Focus, string, string][]).map(([id, en, fiL]) => (
          <button key={id} type="button" aria-pressed={p.focus === id} onClick={() => setP({ ...p, focus: id })}>{fi ? fiL : en}</button>
        ))}
      </div>
      {p.focus === 'club' && info && (
        <div className="setup-cards two">
          {[info.home, info.away].map((t) => (
            <button key={t.teamId} type="button" className={`setup-card club side-${t.teamId === info.home.teamId ? 'home' : 'away'}`}
              aria-pressed={p.team === t.teamId} onClick={() => setP({ ...p, team: t.teamId })}>
              <strong>{t.name}</strong>
            </button>
          ))}
        </div>
      )}
      {p.focus === 'player' && info && (
        <div className="setup-players">
          {players.map((t) => (
            <div key={t.teamId}>
              <p className="setup-team">{t.name}</p>
              {t.players.filter((pl) => pl.position !== 'goalkeeper').map((pl) => (
                <button key={pl.playerId} type="button" aria-pressed={p.player === pl.playerId}
                  onClick={() => setP({ ...p, player: pl.playerId, team: t.teamId })}>
                  <Shirt side={t.teamId === info.home.teamId ? 'home' : 'away'} number={pl.shirtNumber} size={24} /> {pl.name}
                </button>
              ))}
            </div>
          ))}
        </div>
      )}
      {!info && p.focus !== 'match' && <p className="fan-hint">{fi ? 'Valitse, kun ottelu alkaa.' : 'You can pick once a match starts.'}</p>}
    </section>
  );

  const follow = (
    <section className="setup-sec">
      <h2>{fi ? 'Miten haluat seurata?' : 'How do you want to follow?'}</h2>
      <div className="setup-cards">
        <label className="setup-card check"><input type="checkbox" checked disabled /><strong>{fi ? 'Lue' : 'Read'}</strong><span>{fi ? 'Tapahtumat korteiksi kirjoitettuna.' : 'The moments written as cards.'}</span></label>
        <label className="setup-card check"><input type="checkbox" checked={p.listen} onChange={(e) => setP({ ...p, listen: e.target.checked })} />
          <strong>{fi ? 'Kuuntele' : 'Listen'}</strong><span>{fi ? 'Selostaja ja analyysi ääneen.' : 'The commentator and the analysis read aloud.'}</span></label>
        <label className="setup-card check"><input type="checkbox" checked={p.ask} onChange={(e) => setP({ ...p, ask: e.target.checked })} />
          <strong>{fi ? 'Kysy' : 'Ask questions'}</strong><span>{fi ? 'Kysy Second Ball IQ:lta mitä tahansa ottelusta.' : 'Ask Second Ball IQ anything about the match.'}</span></label>
      </div>
      <h3>{fi ? 'Katsotko yksin vai yhdessä?' : 'Watching alone or with friends?'}</h3>
      <div className="setup-seg">
        <button type="button" aria-pressed={p.social === 'solo'} onClick={() => setP({ ...p, social: 'solo' })}>{fi ? 'Yksin' : 'Alone'}</button>
        <button type="button" aria-pressed={p.social === 'party'} onClick={() => setP({ ...p, social: 'party' })}>{fi ? 'Juhlissa ystävien kanssa' : 'Watch party with friends'}</button>
      </div>
      <h3>{fi ? 'Kieli' : 'Language'}</h3>
      <div className="setup-seg">
        {(['en', 'fi'] as const).map((l) => (
          <button key={l} type="button" aria-pressed={p.language === l} onClick={() => setP({ ...p, language: l })}>{l === 'en' ? 'English' : 'Suomi'}</button>
        ))}
      </div>
    </section>
  );

  const steps = [level, focus, follow];
  const done = () => onDone({ ...p, setupDone: true, focus: p.focus === 'club' && !p.team ? 'match' : p.focus === 'player' && !p.player ? 'match' : p.focus }, p.listen);

  return (
    <div className="setup" role="dialog" aria-modal="true" aria-label={fi ? 'Seuraa omalla tavallasi' : 'Follow the match your way'}>
      <div className="setup-inner">
        <header className="setup-head">
          <span className="brand-small"><Wordmark /></span>
          {first
            ? <span className="setup-step">{step + 1} / {steps.length}</span>
            : onCancel && <button type="button" className="link" onClick={onCancel}>{fi ? 'Peruuta' : 'Cancel'}</button>}
        </header>
        {first ? steps[step] : <>{level}{focus}{follow}</>}
        <footer className="setup-foot">
          {first && step > 0 && <button type="button" onClick={() => setStep(step - 1)}>{fi ? 'Takaisin' : 'Back'}</button>}
          {first && step < steps.length - 1
            ? <button type="button" className="primary" onClick={() => setStep(step + 1)}>{fi ? 'Seuraava' : 'Next'}</button>
            : <button type="button" className="primary" onClick={done}>{first ? (fi ? 'Aloita' : 'Start watching') : (fi ? 'Tallenna' : 'Save')}</button>}
        </footer>
      </div>
    </div>
  );
}
