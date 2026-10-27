import { useSyncExternalStore } from 'react';
import type { ControlView, TrackingView } from './types';

/**
 * The latest tracking frame, outside React state: frames arrive ten times a second, and only the layer that draws
 * the players needs them, so the rest of the app does not re-render with every frame. The last 15 seconds (as they
 * arrived, in real time) are kept too, for the 3D stage's DVR.
 */
let latest: TrackingView | undefined;
const listeners = new Set<() => void>();

/** Seconds of real time the DVR keeps. */
export const DVR_SEC = 15;
export interface Recorded { at: number; frame: TrackingView }
const history: Recorded[] = [];

export function setFrame(frame: TrackingView | undefined) {
  latest = frame;
  if (!frame) history.length = 0;
  else {
    const now = performance.now();
    // A jump back in match time (a new match, a restart) starts the recording again.
    if (history.length && frame.t < history[history.length - 1].frame.t - 1) history.length = 0;
    history.push({ at: now, frame });
    while (history.length && now - history[0].at > DVR_SEC * 1000) history.shift();
  }
  listeners.forEach((l) => l());
}

/** The latest frame without subscribing: the 3D stage reads it in its own render loop. */
export const getFrame = (): TrackingView | undefined => latest;

/** What the DVR has: frames with the real time they arrived (performance.now()), oldest first. */
export const getHistory = (): readonly Recorded[] => history;

/**
 * The frame on screen at real time `at` (performance.now()), between the two recorded frames around it. Players and
 * the ball are interpolated, so a scrub or a slow replay moves smoothly.
 */
export function frameAt(at: number): TrackingView | undefined {
  if (!history.length) return undefined;
  if (at <= history[0].at) return history[0].frame;
  const lastRec = history[history.length - 1];
  if (at >= lastRec.at) return lastRec.frame;
  let i = history.length - 1;
  while (i > 0 && history[i - 1].at > at) i--;
  const a = history[i - 1], b = history[i];
  const u = (at - a.at) / Math.max(1, b.at - a.at);
  const pa = new Map(a.frame.players.map((p) => [p.id, p]));
  return {
    t: a.frame.t + (b.frame.t - a.frame.t) * u,
    ballX: a.frame.ballX + (b.frame.ballX - a.frame.ballX) * u,
    ballY: a.frame.ballY + (b.frame.ballY - a.frame.ballY) * u,
    ballZ: a.frame.ballZ + (b.frame.ballZ - a.frame.ballZ) * u,
    players: b.frame.players.map((p) => {
      const q = pa.get(p.id);
      return q ? { id: p.id, x: q.x + (p.x - q.x) * u, y: q.y + (p.y - q.y) * u } : p;
    }),
  };
}

export function useFrame(): TrackingView | undefined {
  return useSyncExternalStore(
    (l) => { listeners.add(l); return () => listeners.delete(l); },
    () => latest,
    () => latest,
  );
}

/** Whether tracking frames are arriving at all (re-renders only when that changes). */
export function useHasFrame(): boolean {
  return useSyncExternalStore(
    (l) => { listeners.add(l); return () => listeners.delete(l); },
    () => latest !== undefined,
    () => latest !== undefined,
  );
}

/** The latest pitch-control map (about once a second), kept outside React state like the frames. */
let control: ControlView | undefined;
const controlListeners = new Set<() => void>();
export function setControl(c: ControlView | undefined) { control = c; controlListeners.forEach((l) => l()); }
export const getControl = (): ControlView | undefined => control;
export function useControl(): ControlView | undefined {
  return useSyncExternalStore(
    (l) => { controlListeners.add(l); return () => controlListeners.delete(l); },
    () => control,
    () => control,
  );
}
