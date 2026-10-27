import { Component, createContext, lazy, Suspense, useContext, useState, type ReactNode } from 'react';
import { ArrowLeft16Regular } from '@fluentui/react-icons';
import { Stands } from './Stands';
import type { Party } from '../party/useParty';
import type { Call, MatchInfo, PitchEvent, RunnerView, TagView, TickView } from '../types';

/**
 * The match stage: the part of the fan view that never leaves the screen. A stand of supporters along the far
 * touchline and the pitch in perspective (the LED ticker runs under the score header). What is drawn on the pitch depends
 * on the tab (the live ball, a shape or heat map, a player) unless something was put on it to look at: a moment's
 * replay, the events that decided a test, an answer's evidence. That is a "visual", with a way back to the tab's view.
 * <p>
 * On a wide screen with WebGL the stage is 3D (`Stage3D`, loaded only then): the ground, the players from tracking, a
 * broadcast camera; in the Room the watch party's stand runs as a slim band along the top of it. The 2D stage stays
 * the phone view and the fallback when WebGL is missing or fails; the fan can switch between them.
 * </p>
 */
export interface StageVisual { key: string; label: string; node: ReactNode }

const StageContext = createContext<{ show?: (v: StageVisual | undefined) => void; current?: string }>({});
export const StageProvider = StageContext.Provider;
/** Components deep in the right panel put their evidence on the stage with this; without a stage it is undefined. */
export const useStage = () => useContext(StageContext);

const Stage3D = lazy(() => import('./Stage3D'));
const PREF = 'ml-stage3d';

function webgl(): boolean {
  try { return !!document.createElement('canvas').getContext('webgl2'); } catch { return false; }
}

interface Props {
  info: MatchInfo; tick: TickView; party?: Party; roomMode?: boolean; fi?: boolean;
  base: ReactNode; visual?: StageVisual; onBack: () => void; overlay?: ReactNode;
  /** For the 3D stage: whether `base` is the live match (drawn in 3D) or a board of its own (drawn on the 3D grass). */
  liveBase?: boolean; wide?: boolean; events?: PitchEvent[]; calls?: Call[]; tags?: TagView[]; running?: RunnerView[]; pinned?: ReactNode; focusPlayer?: string;
}

export function MatchStage(props: Props) {
  const { info, tick, party, roomMode, fi, base, visual, onBack, overlay, wide } = props;
  const [can] = useState(webgl);
  const [want, setWant] = useState(() => { try { return localStorage.getItem(PREF) !== 'off'; } catch { return true; } });
  const [failed, setFailed] = useState(false);
  const toggle = () => setWant((w) => { try { localStorage.setItem(PREF, w ? 'off' : 'on'); } catch { /* private mode */ } return !w; });
  const offer = can && !!wide && !failed;
  const use3d = offer && want;

  const chip = visual && (
    <div className="stage-chip">
      <span className="stage-chip-label">{visual.label}</span>
      <button type="button" onClick={onBack}><ArrowLeft16Regular aria-hidden="true" /> {fi ? 'Takaisin otteluun' : 'Back to the match'}</button>
    </div>
  );
  const switcher = offer && (
    <button type="button" className="stage-mode" aria-pressed={use3d} onClick={toggle}
      title={use3d ? (fi ? 'Vaihda 2D-näkymään' : 'Switch to the 2D board') : (fi ? 'Vaihda 3D-näkymään' : 'Switch to the 3D stage')}>
      {use3d ? '2D' : '3D'}
    </button>
  );

  const flat = (
    <>
      <div className="stage-stands">
        <Stands info={info} homeGoals={tick.homeGoals} awayGoals={tick.awayGoals} party={party} roomMode={roomMode} fi={fi} />
      </div>
      <div className="stage-field">
        {chip}
        <div className="stage-pitch">{visual ? visual.node : base}</div>
        {overlay}
      </div>
    </>
  );

  if (use3d) {
    return (
      <section className={`stage stage-3d ${roomMode ? 'stage-room' : ''}`} aria-label={fi ? 'Ottelu' : 'The match'}>
        <Fallback onFail={() => setFailed(true)}>
          <Suspense fallback={<div className="s3d s3d-loading"><span>{fi ? 'Ladataan stadionia…' : 'Opening the ground…'}</span></div>}>
            <Stage3D info={info} tick={tick} events={props.events ?? []} calls={props.calls ?? []} tags={props.tags ?? []} running={props.running} fi={fi}
              visualNode={visual?.node} boardBase={props.liveBase ? undefined : base} pinned={props.pinned} focusPlayer={props.focusPlayer} />
          </Suspense>
        </Fallback>
        {roomMode && (
          <div className="s3d-room-stand">
            <Stands info={info} homeGoals={tick.homeGoals} awayGoals={tick.awayGoals} party={party} roomMode fi={fi} />
          </div>
        )}
        {chip}
        {overlay}
        {switcher}
      </section>
    );
  }
  return (
    <section className={`stage ${roomMode ? 'stage-room' : ''}`} aria-label={fi ? 'Ottelu' : 'The match'}>
      {flat}
      {switcher}
    </section>
  );
}

/** If the 3D stage cannot start (no WebGL context, a lost GPU), the 2D stage takes over for this visit. */
class Fallback extends Component<{ onFail: () => void; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch() { this.props.onFail(); }
  render() { return this.state.failed ? null : this.props.children; }
}
