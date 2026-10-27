import type { InsightKind, MatchInfo } from './types';

export function clock(sec: number): string {
  const s = Math.max(0, Math.floor(sec));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export const kindLabel: Record<InsightKind, string> = {
  pressingShift: 'Pressing shift',
  possessionIllusion: 'Possession illusion',
  turnoverToChance: 'Turnover to chance',
  playerMilestone: 'Player milestone',
  goal: 'Goal',
  tacticalShift: 'Tactical shift',
  playerRole: 'Player role',
  territory: 'Territory',
  betterPass: 'Better pass',
  coachPlan: "Coach's plan",
};

/** Role names as a commentator would say them, in English and Finnish, with a two-letter tag for the board. */
export const roleLabel: Record<string, { en: string; fi: string; tag: string }> = {
  goalkeeper: { en: 'Goalkeeper', fi: 'Maalivahti', tag: 'GK' },
  centreBack: { en: 'Centre-back', fi: 'Keskuspuolustaja', tag: 'CB' },
  fullBack: { en: 'Full-back', fi: 'Laitapuolustaja', tag: 'FB' },
  wingBack: { en: 'Wing-back', fi: 'Laitatukimies', tag: 'WB' },
  holdingMidfielder: { en: 'Holding midfielder', fi: 'Puolustava keskikenttä', tag: 'DM' },
  centralMidfielder: { en: 'Central midfielder', fi: 'Keskikenttäpelaaja', tag: 'CM' },
  attackingMidfielder: { en: 'Attacking midfielder', fi: 'Hyökkäävä keskikenttä', tag: 'AM' },
  wideMidfielder: { en: 'Wide midfielder', fi: 'Laitakeskikenttä', tag: 'WM' },
  winger: { en: 'Winger', fi: 'Laituri', tag: 'W' },
  invertedWinger: { en: 'Inverted winger', fi: 'Sisäänpäin hakeutuva laituri', tag: 'IW' },
  striker: { en: 'Striker', fi: 'Kärkihyökkääjä', tag: 'ST' },
  falseNine: { en: 'False nine', fi: 'Valeysi', tag: 'F9' },
  secondStriker: { en: 'Second striker', fi: 'Toinen kärki', tag: 'SS' },
  shadowStriker: { en: 'Shadow striker', fi: 'Varjokärki', tag: 'SH' },
};

/** "Casual-en" → "Casual (English)". */
export function personaLabel(key: string): string {
  const [audience, language] = key.split('-');
  const lang = language === 'fi' ? 'Suomi' : 'English';
  return `${audience} (${lang})`;
}

export function teamName(info: MatchInfo | undefined, teamId: string): string {
  if (!info) return teamId;
  return info.home.teamId === teamId ? info.home.name : info.away.teamId === teamId ? info.away.name : teamId;
}

export function playerName(info: MatchInfo | undefined, playerId?: string): string | undefined {
  if (!info || !playerId) return undefined;
  return [...info.home.players, ...info.away.players].find((p) => p.playerId === playerId)?.name;
}

/** The six workflow steps, in order, with the names the trace uses. */
export const stages = [
  { step: 'Triage', label: 'Triage', who: 'Decision-1' },
  { step: 'TacticalAnalyst', label: 'Analyst', who: 'Agent' },
  { step: 'Narrator', label: 'Narrator', who: 'Agent ×8' },
  { step: 'Validator', label: 'Validator', who: 'Code' },
  { step: 'Grounding', label: 'Grounding', who: 'Decision-1' },
  { step: 'Publish', label: 'Publish', who: 'Code' },
] as const;

export type Tone = 'good' | 'warn' | 'bad' | 'quiet';

export function tone(outcome: string): Tone {
  const o = outcome.toLowerCase();
  if (['ok', 'pass', 'proceed', 'caught'].includes(o) || o.includes('auto')) return 'good';
  if (['drop', 'fail', 'rejected', 'executor failed', 'missed'].includes(o)) return 'bad';
  if (['fold', 'skipped'].includes(o)) return 'quiet';
  return 'warn';
}
