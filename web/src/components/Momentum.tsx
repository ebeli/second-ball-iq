import { useMemo } from 'react';
import type { MatchInfo, PitchEvent, InsightView } from '../types';

interface Props {
  events: PitchEvent[];
  info: MatchInfo;
  endClockSec: number;
  onAir: InsightView[];
}

/**
 * Who had the ball, minute by minute: bars above the line are the home side's share of completed passes, below
 * the away side's. Shots sit on the line, goals as rings, and each published insight is a yellow tick on top,
 * so the story the agents told can be read against the run of play.
 */
export function Momentum({ events, info, endClockSec, onAir }: Props) {
  const minutes = Math.max(90, Math.ceil(endClockSec / 60));
  const bins = useMemo(() => {
    const home = new Array(minutes + 1).fill(0);
    const away = new Array(minutes + 1).fill(0);
    const shots: { m: number; home: boolean; goal: boolean; xg: number }[] = [];
    for (const e of events) {
      const m = Math.min(minutes, Math.floor(e.clockSec / 60));
      if (e.type === 'pass' && e.outcome === 'success') (e.teamId === info.home.teamId ? home : away)[m]++;
      if (e.type === 'shot') shots.push({ m, home: e.teamId === info.home.teamId, goal: e.shotResult === 'goal', xg: e.xg ?? 0 });
    }
    return { home, away, shots };
  }, [events, info, minutes]);

  const w = 600, h = 64, mid = h / 2, bw = w / (minutes + 1);
  const ticks = [...new Set(onAir.map((i) => Math.floor(i.insight.matchClockSec / 60)))];

  return (
    <svg className="momentum" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" role="img"
      aria-label="Share of completed passes per minute, home above the line and away below">
      {bins.home.map((hm, m) => {
        const total = hm + bins.away[m];
        if (!total) return null;
        const share = hm / total - 0.5; // -0.5 .. 0.5
        const bar = Math.abs(share) * 2 * (mid - 4);
        return (
          <rect key={m} x={m * bw + 0.5} width={Math.max(1, bw - 1)} y={share >= 0 ? mid - bar : mid} height={bar}
            fill={share >= 0 ? 'var(--home)' : 'var(--away)'} opacity={0.75} />
        );
      })}
      <line x1={0} x2={w} y1={mid} y2={mid} stroke="var(--chalk-line)" strokeWidth={1} />
      <line x1={45 * bw} x2={45 * bw} y1={2} y2={h - 2} stroke="var(--chalk-line)" strokeDasharray="2 3" />
      {bins.shots.map((s, i) => (
        <circle key={i} cx={s.m * bw + bw / 2} cy={s.home ? mid - 3 : mid + 3} r={s.goal ? 4 : 1.6 + s.xg * 4}
          fill={s.goal ? 'none' : 'var(--chalk)'} stroke={s.goal ? 'var(--on-air)' : 'none'} strokeWidth={2} />
      ))}
      {ticks.map((m) => (
        <rect key={`t${m}`} x={m * bw} y={0} width={Math.max(2, bw)} height={3} fill="var(--on-air)" />
      ))}
    </svg>
  );
}
