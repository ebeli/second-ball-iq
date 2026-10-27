import type { Rewind3D } from '../../stage3dBridge';
import type { TrackingView } from '../../types';

/** The camera the fan picked. Auto is the director: follow the ball and cut behind the goal on goals and big chances. */
export type CamMode = 'auto' | 'broadcast' | 'tactical' | 'drone' | 'player';
export const CAM_MODES: CamMode[] = ['auto', 'broadcast', 'tactical', 'drone', 'player'];

export interface Cut { kind: 'goal' | 'bigChance'; side: 1 | -1; until: number; label: string }
export interface Anchor { id: string; el: HTMLElement | null; at?: { x: number; z: number }; lift: number }
/** Something a player does that the avatar acts out: a pass or shot (kick), a tackle, a keeper's dive. */
export interface Action { kind: 'kick' | 'tackle' | 'dive'; at: number; dir: number }

/** The telestrator's layers, all drawn from the tracking frame on screen. */
export interface Layers { offside: boolean; shape: boolean; runs: boolean; control: boolean }

/**
 * Everything the page tells the scene, read in the render loop without re-rendering it. The page writes it; the
 * scene's components read it each frame (times are performance.now() milliseconds).
 */
export interface Rig {
  mode: 'live' | 'board';
  /** Controls along the bottom (a replay's bar): the wide shot leaves room for them. */
  bar: boolean;
  cam: CamMode;
  cut?: Cut;
  shake?: { from: number; until: number; amp: number };
  rewind?: Rewind3D & { since: number };
  reduced: boolean;
  board: HTMLElement | null;
  panel: HTMLElement | null;
  screens: (HTMLElement | null)[];
  lockEl: HTMLElement | null;
  hullEls: (HTMLElement | null)[];
  tags: Anchor[];
  cheer?: { side: 'home' | 'away'; until: number };
  /** A goal: which goal's net bulges, and when. */
  netHit?: { side: 1 | -1; at: number };
  /** Until when the ball's trail is a shot's (gold). */
  trailHot?: number;
  fallbackBall?: { x: number; z: number };
  /** Camera flashes in the stands: bursts with a start, a count and a length. */
  flashes: { at: number; n: number; dur: number }[];
  actions: Map<string, Action>;
  /** The DVR: live, or a point in the last 15 seconds (real time) that is paused or playing. */
  dvr: { live: boolean; cursor: number; playing: boolean };
  layers: Layers;
  lock?: string;
  /** The team shapes' labels, written by the telestrator, placed by the DOM placer. */
  hullLabels?: ({ x: number; z: number; text: string } | undefined)[];
  /** The frame the scene is showing (live or from the DVR), for the page's readouts. */
  shown?: TrackingView;
  /** Speed of every player in the shown frame, km/h, smoothed, and their velocity (metres per match second). */
  speeds: Map<string, number>;
  vel: Map<string, { vx: number; vz: number }>;
  /** Where each player is in the scene now (after smoothing), for the telestrator and the tags. */
  at: Map<string, { x: number; z: number }>;
}
