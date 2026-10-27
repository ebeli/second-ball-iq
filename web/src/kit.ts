import { useEffect, type CSSProperties } from 'react';
import type { MatchInfo, TeamInfo } from './types';

/**
 * A club's kit, worked out from its primary colour: the shirt itself, a darker trim for collar and cuffs, the
 * number colour that reads on the shirt, and a brighter version of the same hue that reads on the dark board.
 */
export interface Kit { shirt: string; trim: string; ink: string; board: string; glow: string }

export function kitOf(team: Pick<TeamInfo, 'primaryColor'>): Kit {
  const [h, s, l] = hsl(team.primaryColor);
  return {
    shirt: team.primaryColor,
    trim: css(h, s, Math.max(0.12, l * 0.62)),
    ink: luminance(team.primaryColor) > 0.42 ? '#10171c' : '#ffffff',
    // Keep the hue and most of the saturation; only lift the lightness, so two dark kits stay tellable apart.
    board: css(h, Math.min(0.9, Math.max(0.5, s * 1.15)), 0.64),
    // The club's "energy" colour for edge lights on the dark arena: the same hue, fully lit.
    glow: css(h, 0.95, 0.62),
  };
}

/** CSS variables for both kits; every component reads team colour from these. */
export function kitVars(info: MatchInfo): CSSProperties {
  const home = kitOf(info.home), away = kitOf(info.away);
  return {
    '--home-kit': home.shirt, '--home-trim': home.trim, '--home-ink': home.ink, '--home': home.board, '--home-glow': home.glow,
    '--away-kit': away.shirt, '--away-trim': away.trim, '--away-ink': away.ink, '--away': away.board, '--away-glow': away.glow,
  } as CSSProperties;
}

export const sideOf = (info: MatchInfo, teamId?: string): 'home' | 'away' => (teamId === info.away.teamId ? 'away' : 'home');

/** The team a player belongs to, and which side it is. */
export function playerTeam(info: MatchInfo, playerId?: string) {
  const home = info.home.players.find((p) => p.playerId === playerId);
  if (home) return { team: info.home, side: 'home' as const, player: home };
  const away = info.away.players.find((p) => p.playerId === playerId);
  return away ? { team: info.away, side: 'away' as const, player: away } : undefined;
}

function hsl(hex: string): [number, number, number] {
  const [r, g, b] = rgb(hex);
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [h * 60, s, l];
}

function rgb(hex: string): [number, number, number] {
  const m = hex.replace('#', '');
  const full = m.length === 3 ? m.split('').map((c) => c + c).join('') : m.padEnd(6, '0');
  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255) as [number, number, number];
}

function luminance(hex: string) {
  const lin = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  const [r, g, b] = rgb(hex).map(lin);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

const css = (h: number, s: number, l: number) => `hsl(${h.toFixed(0)} ${(s * 100).toFixed(0)}% ${(l * 100).toFixed(0)}%)`;

/** Also put the kits on <html>, so panels and menus that open outside the page tree (portals) wear them too. */
export function useKitOnRoot(info?: MatchInfo) {
  useEffect(() => {
    if (!info) return;
    const vars = kitVars(info) as Record<string, string>;
    const root = document.documentElement.style;
    for (const [k, v] of Object.entries(vars)) root.setProperty(k, v);
  }, [info]);
}
