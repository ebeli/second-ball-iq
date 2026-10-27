import { createContext } from 'react';
import type { PitchEvent } from './types';

/** A replay as the 2D board steps through it: the moves and the one on screen now. */
export interface Rewind3D { events: PitchEvent[]; step: number }

/**
 * How the 2D board hands its drawing to the 3D stage. Inside the 3D stage a `Pitch` does not draw its own flat pitch:
 * it puts its markings (arrows, heat, shapes) into `board`, an element the 3D stage lays on its grass, and reports the
 * replay step it is on, so the 3D stage can play that move with a ball and the player. `board` is null while the 3D
 * stage is still starting. Kept free of three.js so the 2D stage never loads it.
 */
export interface Stage3DBridgeValue { board: HTMLElement | null; onRewind: (r: Rewind3D | undefined) => void }

export const Stage3DBridge = createContext<Stage3DBridgeValue | undefined>(undefined);
