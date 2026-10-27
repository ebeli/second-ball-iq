import type { PitchEvent } from './types';

/**
 * Pass quality and ball speed on screen. The numbers come from the server (BallMetrics in the engine): pass length
 * in metres, a difficulty from 0 to 100, ball speed in km/h. The thresholds below match the engine's, so the tag on
 * the pitch, the commentator's call and the numbers in the panel agree.
 */
export const FAST_SHOT_KMH = 110;
export const LONG_PASS_M = 40;
export const HARD_PASS = 60;
export const MEDIUM_PASS = 35;

export const isFastShot = (e: PitchEvent) => e.type === 'shot' && (e.ballSpeedKmh ?? 0) >= FAST_SHOT_KMH;
export const isLongPass = (e: PitchEvent) => e.type === 'pass' && e.outcome === 'success' && (e.passMetres ?? 0) >= LONG_PASS_M;
export const isHardPass = (e: PitchEvent) => (e.passDifficulty ?? 0) >= HARD_PASS;

export type Band = 'easy' | 'medium' | 'hard';
export const band = (d: number): Band => (d >= HARD_PASS ? 'hard' : d >= MEDIUM_PASS ? 'medium' : 'easy');
export const bandLabel = (d: number, fi?: boolean) =>
  ({ easy: fi ? 'helppo' : 'easy', medium: fi ? 'keskivaikea' : 'medium', hard: fi ? 'vaikea' : 'hard' })[band(d)];

/** What a player's on-screen tag says when a threshold is reached, or undefined. */
export function metricTag(e: PitchEvent, fi?: boolean): string | undefined {
  if (isFastShot(e)) return `${Math.round(e.ballSpeedKmh!)} km/h`;
  if (isLongPass(e)) return `${Math.round(e.passMetres!)} m ${fi ? 'syöttö' : 'pass'}`;
  return undefined;
}

/** One line about a pass or shot for captions: "45 m pass, difficulty 72 (hard), 78 km/h". */
export function describeMetrics(e: PitchEvent, fi?: boolean): string {
  const parts: string[] = [];
  if (e.type === 'pass' && e.passMetres !== undefined) {
    parts.push(`${Math.round(e.passMetres)} m`);
    if (e.passDifficulty !== undefined) parts.push(`${fi ? 'vaikeus' : 'difficulty'} ${e.passDifficulty} (${bandLabel(e.passDifficulty, fi)})`);
  }
  if ((e.type === 'shot' || e.type === 'pass') && e.ballSpeedKmh !== undefined) parts.push(`${Math.round(e.ballSpeedKmh)} km/h`);
  return parts.join(' · ');
}
