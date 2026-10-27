// Preview only (vite.preview.config.ts aliases '@microsoft/signalr' here): a hub that answers from a fixture of a
// real simulated demo match, so the real apps can be rendered and screenshotted without the .NET server.
import fixture from './fixture.json';

type Handler = (...args: unknown[]) => void;
export enum LogLevel { Warning = 3 }
export type HubConnection = FakeHub;

const me = 'me';
function party() {
  const info = (fixture as any).info.match;
  const now = Date.now();
  const sims = [['Aada', true], ['Mikko', true], ['Sofia', false], ['Leo', true], ['Noor', false], ['Jussi', false], ['Emma', true], ['Kai', false]] as const;
  const members = [
    ...sims.map(([n, home], i) => ({ memberId: 'sim-' + n.toLowerCase(), name: n, teamId: home ? info.home.teamId : info.away.teamId, points: 7 - i, simulated: true })),
    { memberId: me, name: 'Judge', points: 4, simulated: false },
  ];
  const test = ((fixture as any).tests as any[]).find((t) => t.kind === 'pressingShift');
  const votes: Record<string, string> = { 'sim-aada': 'no', 'sim-mikko': 'no', 'sim-sofia': 'yes', 'sim-noor': 'yes', 'sim-jussi': 'yes', 'sim-kai': 'yes', me: 'yes' };
  const poll = test && {
    pollId: 'poll-' + test.testId, candidateId: test.candidateId, clockSec: test.opensAtSec, question: test.criterion, questionFi: test.criterionFi,
    options: [{ id: 'yes', label: 'It will hold', labelFi: 'Pitää', votes: 5 }, { id: 'no', label: "It won't", labelFi: 'Ei pidä', votes: 2 }],
    closesAtMs: now + 22000, closed: false, matchLensPick: 'yes', votes, testId: test.testId, deadlineSec: test.deadlineSec, estimate: 0.64,
  };
  const said: [string, string, number][] = [['sim-mikko', 'That press is relentless, we can\'t get out', 260], ['sim-sofia', 'Come on Northvale, one more!', 170],
    ['sim-aada', 'Back three now? Bold.', 95], ['sim-noor', 'Varga is everywhere tonight', 40]];
  const messages = said.map(([id, text, ago], i) => {
    const m = members.find((x) => x.memberId === id)! as any;
    return { messageId: 'msg-' + i, memberId: id, name: m.name, teamId: m.teamId, text, atMs: now - ago * 1000, simulated: true };
  });
  return { code: 'KQ7M', host: 'Judge', members, polls: poll ? [poll] : [], picks: [], questions: [], demo: true, tifo: null, messages };
}

// The coach's corner and the what-if sandbox answer from the fixture (probabilities there are stand-ins, not Decision-1).
const realFetch = window.fetch.bind(window);
window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
  if (url.includes('/api/whatif')) { await new Promise((r) => setTimeout(r, 400)); return json((fixture as any).whatIf); }
  if (url.includes('/api/judge')) return json((fixture as any).info);
  if (url.includes('/api/coach/')) return json({ error: 'The last plans are still being tested.' }, 409);
  return realFetch(input, init);
};

class FakeHub {
  private handlers = new Map<string, Handler[]>();
  on(name: string, h: Handler) { this.handlers.set(name, [...(this.handlers.get(name) ?? []), h]); }
  off(name: string) { this.handlers.delete(name); }
  onreconnecting() {} onreconnected() {} onclose() {}
  async start() {
    // ?script=goal: a goal 2 s after loading (score roll, room cheer). ?script=settle: the open test settles.
    // ?script=coach: a coach's corner decision, then its verdict (studio, fan and /overlay).
    const script = new URLSearchParams(window.location.search).get('script');
    const emit = (name: string, payload: unknown) => (this.handlers.get(name) ?? []).forEach((h) => h(payload));
    const f = fixture as any;
    // Tracking: replay the fixture's last seconds of frames at 10 per second, in a loop.
    if (f.frames?.length) {
      let i = 0;
      window.setInterval(() => { emit('frame', f.frames[i % f.frames.length]); i++; }, 100);
    }
    // ?script=clock&from=38&rate=8: the match clock runs from that minute at that rate (recording takes, /fan?take=1).
    if (script === 'clock') {
      const q = new URLSearchParams(window.location.search);
      let clock = Number(q.get('from') ?? 38) * 60;
      const rate = Number(q.get('rate') ?? 8);
      window.setInterval(() => { clock += rate / 4; emit('tick', { ...f.tick, clockSec: Math.floor(clock), status: 'running' }); }, 250);
    }
    if (script === 'goal') {
      window.setTimeout(() => {
        const nor = f.info.match.away;
        emit('call', { callId: 'call-preview-goal', clockSec: f.tick.clockSec + 30, kind: 'goal', teamId: nor.teamId, playerId: nor.players[9].playerId,
          intensity: 3, text: 'GOAL!', textFi: 'MAALI!' });
        emit('events', [{ id: 'evt-preview-goal', seq: 99998, clockSec: f.tick.clockSec + 30, period: 2, type: 'goal', teamId: nor.teamId,
          playerId: nor.players[9].playerId, x: 92, y: 50, outcome: 'success', xg: 0.31, highRecovery: false }]);
        emit('tick', { ...f.tick, clockSec: f.tick.clockSec + 30, homeGoals: f.tick.homeGoals, awayGoals: f.tick.awayGoals + 1 });
      }, 6000);
    }
    if (script === 'chance') {
      window.setTimeout(() => emit('call', { callId: 'call-preview-chance', clockSec: f.tick.clockSec + 5, kind: 'bigChance', teamId: f.info.match.home.teamId,
        intensity: 2, text: 'What a chance!', textFi: 'Mikä paikka!' }), 6000);
    }
    if (script === 'shot' || script === 'pass') {
      // A fast shot (or a long completed pass) 2 s after loading, to show the player's speed or distance tag.
      window.setTimeout(() => {
        const t = f.tick.clockSec + 5, nor = f.info.match.away;
        const ev = script === 'shot'
          ? { id: 'evt-preview-1', seq: 99999, clockSec: t, period: 2, type: 'shot', teamId: nor.teamId, playerId: nor.players[9].playerId,
              x: 22, y: 44, outcome: 'fail', xg: 0.08, shotResult: 'saved', highRecovery: false, ballSpeedKmh: 113.4 }
          : { id: 'evt-preview-1', seq: 99999, clockSec: t, period: 2, type: 'pass', teamId: nor.teamId, playerId: nor.players[3].playerId,
              x: 70, y: 20, endX: 30, endY: 60, outcome: 'success', highRecovery: false, ballSpeedKmh: 88, passMetres: 46.8, passDifficulty: 74 };
        emit('events', [ev]);
        emit('tick', { ...f.tick, clockSec: t });
      }, 2000);
    }
    if (script === 'overlay') {
      // The broadcast overlay's graphics, one after another: a checked insight, a running tag, a test locking, a goal.
      const ins = [...(f.insights as any[])].reverse().find((i) => i.personaKey.startsWith('Broadcast-en'));
      if (ins) window.setTimeout(() => emit('insight', { ...ins, insight: { ...ins.insight, insightId: 'ins-preview-1', displayMs: 12000 } }), 1500);
      const t0 = (f.tests as any[])[0];
      if (t0) window.setTimeout(() => emit('test', { ...t0, testId: 'test-preview-1', status: 'open', estimate: 0.64 }), 2500);
      const run = (f.running as any[])?.[0];
      if (run) window.setTimeout(() => emit('tag', { playerId: run.playerId, teamId: run.teamId, atSec: f.tick.clockSec, kind: 'speed', speedKmh: 34.2, distanceKm: run.distanceKm, x: 50, y: 50 }), 3000);
      window.setTimeout(() => {
        const nor = f.info.match.away;
        emit('call', { callId: 'call-preview-goal2', clockSec: f.tick.clockSec + 30, kind: 'goal', teamId: nor.teamId, playerId: nor.players[9].playerId, intensity: 3, text: 'GOAL! Arlo Quinn makes it 1-1!', textFi: 'MAALI! Arlo Quinn tasoittaa 1-1!' });
        emit('tick', { ...f.tick, clockSec: f.tick.clockSec + 30, awayGoals: f.tick.awayGoals + 1 });
      }, 9000);
    }
    if (script === 'coach') {
      // The coach's corner: Decision-1 picks at 2 s (both tests lock), the match settles both and the verdict lands at 9 s.
      const d = (f.coaches as any[])?.find((x) => x.verdict === 'right');
      if (d) {
        const id = 'coach-preview-1', ids = Object.fromEntries(Object.entries(d.testIds).map(([k]) => [k, `test-${id}-${k}`]));
        const tests = (f.tests as any[]).filter((t) => Object.values(d.testIds).includes(t.testId));
        window.setTimeout(() => {
          for (const t of tests) emit('test', { ...t, testId: `test-${id}-${t.testId.split('-').pop()}`, candidateId: id, status: 'open', settledAtSec: undefined, result: undefined });
          emit('coach', { ...d, decisionId: id, testIds: ids, verdict: undefined });
        }, 2000);
        window.setTimeout(() => {
          for (const t of tests) emit('test', { ...t, testId: `test-${id}-${t.testId.split('-').pop()}`, candidateId: id });
          emit('coach', { ...d, decisionId: id, testIds: ids });
        }, 9000);
      }
    }
    if (script === 'settle') {
      window.setTimeout(() => {
        const p = party();
        const poll = p.polls[0] as any;
        emit('party', { ...p, polls: [{ ...poll, closed: true, outcome: 'met', verdict: '2 of 2 shots after high regains, the second at 59:53.' }] });
      }, 5000);
    }
  }
  async stop() {}
  async invoke(method: string): Promise<unknown> {
    switch (method) {
      case 'Snapshot': {
        const q = new URLSearchParams(window.location.search);
        // With a running clock, the snapshot starts at that minute too.
        return q.get('script') === 'clock' ? { ...(fixture as any), tick: { ...(fixture as any).tick, clockSec: Number(q.get('from') ?? 38) * 60, status: 'running' } } : fixture;
      }
      case 'CreateParty': case 'JoinParty': return party();
      case 'PartyMemberId': return me;
      default: return null;
    }
  }
  async send() {}
}

export class HubConnectionBuilder {
  withUrl() { return this; }
  withAutomaticReconnect() { return this; }
  configureLogging() { return this; }
  build() { return new FakeHub(); }
}
