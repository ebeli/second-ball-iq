import * as signalR from '@microsoft/signalr';
import type { AskResult, CoachDecision, EditorResult, PlayerSeason, ScenarioView, SessionInfo, ShapeReport, WhatIfChange, WhatIfView } from './types';

/**
 * The operator key for a public site (MATCHLENS_ADMIN_KEY on the server): opened once as /?key=…, kept for this tab and
 * removed from the address bar. Match controls and the editor send it; fans and judge mode never need it.
 */
function operatorKey(): string | undefined {
  try {
    const url = new URL(window.location.href);
    const fromUrl = url.searchParams.get('key');
    if (fromUrl) {
      sessionStorage.setItem('matchlens-operator-key', fromUrl);
      url.searchParams.delete('key');
      window.history.replaceState(null, '', url.toString());
    }
    return sessionStorage.getItem('matchlens-operator-key') ?? undefined;
  } catch { return undefined; }
}
operatorKey();

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const key = operatorKey();
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (key) headers['X-MatchLens-Admin-Key'] = key;
  const res = await fetch(url, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  const data = text ? JSON.parse(text) : undefined;
  // The editor endpoints answer 422 with the Validator's problems; callers read them from the body.
  if (!res.ok && res.status !== 422) throw new Error(data?.error ?? `${res.status} ${res.statusText}`);
  return data as T;
}

export interface Health { status: string; live: boolean; models: string; session?: string; operatorKey?: boolean }

export const api = {
  health: () => call<Health>('GET', '/api/health'),
  scenarios: () => call<ScenarioView[]>('GET', '/api/scenarios'),
  start: (req: { scenarioId: string; seed?: number; speed: number; startMinute: number; offline: boolean; includeMilestones: boolean; presentationHolds?: boolean }) =>
    call<SessionInfo>('POST', '/api/session', req),
  /** Judge mode: starts the demo match at 38′, or joins the one already playing. */
  judge: (restart = false) => call<SessionInfo>('POST', restart ? '/api/judge?restart=true' : '/api/judge'),
  /** Fault drill: plant an error in the next moment's Analyst draft. */
  plant: (kind: 'number' | 'claim') => call<{ armed: string }>('POST', '/api/session/plant', { kind }),
  pause: () => call<void>('POST', '/api/session/pause'),
  resume: () => call<void>('POST', '/api/session/resume'),
  stop: () => call<void>('POST', '/api/session/stop'),
  speed: (speed: number) => call<void>('POST', '/api/session/speed', { speed }),
  approve: (id: string) => call<EditorResult>('POST', `/api/editor/${encodeURIComponent(id)}/approve`),
  reject: (id: string) => call<EditorResult>('POST', `/api/editor/${encodeURIComponent(id)}/reject`),
  shape: (teamId: string, fromSec: number, toSec: number) =>
    call<ShapeReport>('GET', `/api/shape/${encodeURIComponent(teamId)}?from=${Math.max(0, Math.floor(fromSec))}&to=${Math.ceil(toSec)}`),
  ask: (body: { question: string; audience: string; language: string; teamId?: string; playerId?: string;
    history?: { question: string; answer: string }[] }) =>
    askCall(body),
  season: (playerId: string) => call<PlayerSeason>('GET', `/api/players/${encodeURIComponent(playerId)}/season`),
  /** Coach's corner now for one team: two plans, Decision-1's pick, two tests. 409 while the last one is open. */
  coach: (teamId: string) => call<CoachDecision>('POST', `/api/coach/${encodeURIComponent(teamId)}`),
  /** What-if sandbox on the frame now; Decision-1 is asked whether the change would help. */
  whatIf: (teamId: string, change: WhatIfChange, metres: number) => call<WhatIfView>('POST', '/api/whatif', { teamId, change, metres }),
  edit: (id: string, headline: string, summary: string) =>
    call<EditorResult>('PUT', `/api/editor/${encodeURIComponent(id)}`, { headline, summary }),
};

/** A viewer id for rate limiting, kept in the browser. */
function viewerId(): string {
  try {
    let id = localStorage.getItem('matchlens-viewer');
    if (!id) { id = Math.random().toString(36).slice(2, 12); localStorage.setItem('matchlens-viewer', id); }
    return id;
  } catch { return 'anon'; }
}

async function askCall(body: unknown): Promise<AskResult> {
  const res = await fetch('/api/ask', {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-MatchLens-Viewer': viewerId() }, body: JSON.stringify(body),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data?.error ?? `${res.status}`);
  return data as AskResult;
}

export function connectHub(): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl('/hubs/match')
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}
