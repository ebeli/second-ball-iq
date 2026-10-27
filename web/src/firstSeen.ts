/**
 * When each feed item (or each version of it) was first seen in this browser tab. It lives outside the components so
 * that switching tabs, which unmounts the panel, does not make old items look new or new ones look old.
 * Items that are already there when a scope (a room, the solo feed) is first opened count as old: they get the
 * fallback time, so they sort by match time below anything that arrives later.
 */
const scopes = new Map<string, { opened: number; seen: Map<string, number> }>();
const SETTLE_MS = 1500;

export function firstSeen(scope: string, key: string, fallback = 0): number {
  let s = scopes.get(scope);
  if (!s) { s = { opened: Date.now(), seen: new Map() }; scopes.set(scope, s); }
  let t = s.seen.get(key);
  if (t === undefined) {
    t = Date.now() - s.opened < SETTLE_MS ? fallback : Date.now();
    s.seen.set(key, t);
  }
  return t;
}

/** Cards the viewer closed, kept for the whole visit. */
export const closedCards = new Set<string>();
