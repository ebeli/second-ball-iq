import { useCallback, useEffect, useRef, useState } from 'react';
import type { HubConnection } from '@microsoft/signalr';
import type { PartyReaction, PartyShare, PartyState } from '../types';

const KEY = 'matchlens-party';

interface Saved { code: string; name: string; teamId?: string }

function load(): Saved | undefined {
  try { const raw = localStorage.getItem(KEY); return raw ? JSON.parse(raw) : undefined; } catch { return undefined; }
}
function save(s?: Saved) {
  try { if (s) localStorage.setItem(KEY, JSON.stringify(s)); else localStorage.removeItem(KEY); } catch { /* ignore */ }
}

/** The watch party over the match's SignalR connection: join, leave, and everything a member can do. */
export function useParty(hub?: HubConnection, connected?: boolean) {
  const [state, setState] = useState<PartyState>();
  const [me, setMe] = useState<string>();
  const [reactions, setReactions] = useState<(PartyReaction & { key: string })[]>([]);
  const [shares, setShares] = useState<PartyShare[]>([]);
  const [myVotes, setMyVotes] = useState<Record<string, string>>({});
  // Server clock minus ours, so a tifo reveals at the same moment on every screen.
  const [skewMs, setSkewMs] = useState(0);
  const saved = useRef<Saved | undefined>(load());

  useEffect(() => {
    if (!hub) return;
    const onState = (s: PartyState) => {
      setState(s);
      if (s.tifo) setSkewMs(s.tifo.nowMs - Date.now());
    };
    const onReaction = (r: PartyReaction) => {
      const key = `${r.atMs}-${r.name}-${Math.random()}`;
      setReactions((list) => [...list.slice(-14), { ...r, key }]);
      window.setTimeout(() => setReactions((list) => list.filter((x) => x.key !== key)), 3200);
    };
    const onShare = (s: PartyShare) => setShares((list) => [...list.slice(-4), s]);
    hub.on('party', onState);
    hub.on('partyReaction', onReaction);
    hub.on('partyShare', onShare);
    return () => { hub.off('party', onState); hub.off('partyReaction', onReaction); hub.off('partyShare', onShare); };
  }, [hub]);

  // Rejoin after a reload or a reconnect.
  useEffect(() => {
    if (!hub || !connected || !saved.current) return;
    const s = saved.current;
    void hub.invoke<PartyState | null>('JoinParty', s.code, s.name, s.teamId ?? null).then(async (st) => {
      if (st) { setState(st); setMe((await hub.invoke<string | null>('PartyMemberId')) ?? undefined); } else { save(undefined); saved.current = undefined; }
    }).catch(() => undefined);
  }, [hub, connected]);

  const create = useCallback(async (name: string, teamId: string | undefined, demo: boolean) => {
    if (!hub) return;
    const st = await hub.invoke<PartyState>('CreateParty', name, teamId ?? null, demo);
    setState(st);
    setMe((await hub.invoke<string | null>('PartyMemberId')) ?? undefined);
    saved.current = { code: st.code, name, teamId };
    save(saved.current);
  }, [hub]);

  const join = useCallback(async (code: string, name: string, teamId?: string) => {
    if (!hub) return false;
    const st = await hub.invoke<PartyState | null>('JoinParty', code.toUpperCase(), name, teamId ?? null);
    if (!st) return false;
    setState(st);
    setMe((await hub.invoke<string | null>('PartyMemberId')) ?? undefined);
    saved.current = { code: st.code, name, teamId };
    save(saved.current);
    return true;
  }, [hub]);

  const leave = useCallback(async () => {
    await hub?.invoke('LeaveParty').catch(() => undefined);
    setState(undefined);
    setMe(undefined);
    saved.current = undefined;
    save(undefined);
  }, [hub]);

  const react = useCallback((emoji: string) => void hub?.invoke('React', emoji).catch(() => undefined), [hub]);
  const vote = useCallback((pollId: string, optionId: string) => {
    setMyVotes((v) => ({ ...v, [pollId]: optionId }));
    void hub?.invoke('Vote', pollId, optionId).catch(() => undefined);
  }, [hub]);
  const pick = useCallback((predictionId: string, optionId: string) => {
    setMyVotes((v) => ({ ...v, [predictionId]: optionId }));
    void hub?.invoke('Pick', predictionId, optionId).catch(() => undefined);
  }, [hub]);
  const ask = useCallback(async (text: string, language: string) => (hub ? hub.invoke<string | null>('AskRoom', text, language) : 'offline'), [hub]);
  const upvote = useCallback((id: string) => {
    setMyVotes((v) => ({ ...v, [id]: 'up' }));
    void hub?.invoke('Upvote', id).catch(() => undefined);
  }, [hub]);
  const investigate = useCallback((id: string) => void hub?.invoke('Investigate', id).catch(() => undefined), [hub]);
  const share = useCallback((label: string, eventIds: string[]) => void hub?.invoke('Share', label, eventIds).catch(() => undefined), [hub]);
  const startTifo = useCallback(async (design: string, language: string) =>
    (hub ? hub.invoke<string | null>('StartTifo', design, language).catch(() => 'offline') : 'offline'), [hub]);
  const holdTifo = useCallback(() => void hub?.invoke('HoldTifo').catch(() => undefined), [hub]);
  /** A chat message to the room; resolves to the reason it was refused, or null when posted. */
  const say = useCallback(async (text: string, language: string) =>
    (hub ? hub.invoke<string | null>('Say', text, language).catch(() => 'offline') : 'offline'), [hub]);

  return { state, me, reactions, shares, myVotes, skewMs, saved: saved.current, create, join, leave, react, vote, pick, ask, upvote, investigate, share, startTifo, holdTifo, say };
}

export type Party = ReturnType<typeof useParty>;
