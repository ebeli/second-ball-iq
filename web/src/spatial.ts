import { createContext, useContext } from 'react';
import { playerTeam } from './kit';
import type { MatchInfo, PassView } from './types';

/**
 * Passes judged from tracking at the moment they were played (lane, difficulty with the lane, the better option), by
 * event id. Provided once at the top of each app, so replays and captions anywhere can show them.
 */
export const PassInfoContext = createContext<Record<string, PassView>>({});
export const usePassInfo = () => useContext(PassInfoContext);

/** A caption line about the lane and the better option: "with the lane 79 · 1 could cut it out · better: Quinn +0.04 xT". */
export function describeLane(p: PassView, info: MatchInfo, fi?: boolean): string {
  const parts = [`${fi ? 'kaistan kanssa' : 'with the lane'} ${p.laneDifficulty}`];
  if (p.interceptable > 0) parts.push(fi ? `${p.interceptable} ehtisi katkaisemaan` : `${p.interceptable} could cut it out`);
  if (p.receiverMarked) parts.push(fi ? 'vastaanottaja vartioitu' : 'target marked');
  if (p.betterTo && p.betterGain !== undefined) {
    const who = playerTeam(info, p.betterTo)?.player.name.split(' ').slice(-1)[0] ?? '';
    parts.push(`${fi ? 'parempi' : 'better'}: ${who} +${p.betterGain.toFixed(2)} xT`);
  }
  return parts.join(' · ');
}
