import type { Audience } from '../types';

/**
 * How one fan wants to follow the match, as four independent choices: how deep, what about, how (read, listen,
 * ask) and with whom. Kept in the browser only.
 */
export type Level = 'beginner' | 'casual' | 'hardcore';
export type Depth = 'quick' | 'explain' | 'tactical';
export type Focus = 'match' | 'club' | 'player';

export interface Prefs {
  v: 2;
  setupDone: boolean;
  level: Level;
  depth: Depth;
  /** Learn the Game: concept explanations on every card. */
  learn: boolean;
  focus: Focus;
  team: string;
  player: string;
  read: boolean;
  listen: boolean;
  ask: boolean;
  /** Watching alone, or in a watch party with friends. */
  social: 'solo' | 'party';
  name: string;
  language: 'en' | 'fi';
}

const KEY = 'matchlens-fan';

export const defaults: Prefs = {
  v: 2, setupDone: false, level: 'casual', depth: 'explain', learn: false, focus: 'match', team: '', player: '',
  read: true, listen: false, ask: false, social: 'solo', name: '', language: 'en',
};

/** A level sets sensible defaults for depth and Learn the Game; both can still be changed afterwards. */
export function forLevel(p: Prefs, level: Level): Prefs {
  return {
    ...p, level,
    depth: level === 'hardcore' ? 'tactical' : 'explain',
    learn: level === 'beginner',
  };
}

/** Which of the server's narrations fits: Quick = commentator, Tactical = analyst, Explain = simple or beginner. */
export function audienceFor(p: Prefs): Audience {
  if (p.depth === 'quick') return 'broadcast';
  if (p.depth === 'tactical') return 'analyst';
  return p.learn ? 'beginner' : 'casual';
}

export function loadPrefs(): Prefs {
  try {
    const raw = localStorage.getItem(KEY);
    if (raw) {
      const old = JSON.parse(raw);
      if (old.v === 2) return { ...defaults, ...old };
      // The first version stored a style; map it onto the depth.
      const depth: Depth = old.audience === 'analyst' ? 'tactical' : old.audience === 'broadcast' ? 'quick' : 'explain';
      return { ...defaults, depth, language: old.language ?? 'en', team: old.team ?? '', focus: old.team ? 'club' : 'match', setupDone: true };
    }
  } catch { /* storage unavailable: defaults */ }
  return defaults;
}

export function savePrefs(p: Prefs) {
  try { localStorage.setItem(KEY, JSON.stringify(p)); } catch { /* ignore */ }
}
