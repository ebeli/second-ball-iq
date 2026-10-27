// Records a test run of Second Ball IQ for review: screenshots of the studio, the fan view (phone and desktop) and the
// broadcast overlay, the match state as the server sees it, the overlay's graphics as they come and go, and any
// browser errors. Everything goes into captures/<date-time>/ at the repository root.
//
//   cd scripts/capture
//   npm install
//   npx playwright install chromium        (once; downloads the browser)
//   node capture.mjs --judge --exercise    (with the server already running: dotnet run --project src/MatchLens.Server)
//
// Options:
//   --base <url>      server address (default http://localhost:5080)
//   --judge           start the demo match at 38′ first (POST /api/judge; joins it if it is already playing)
//   --exercise        once the match is running, ask the coaches for both teams and try a what-if
//   --minutes <n>     how long to record, in real minutes (default: until the match finishes, at most 20)
//   --every <s>       seconds between rounds of screenshots (default 20)
//   --headed          show the browser windows

import { chromium } from 'playwright';
import { mkdir, writeFile, appendFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const opt = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 && args[i + 1] ? args[i + 1] : fallback; };

const base = opt('base', 'http://localhost:5080').replace(/\/$/, '');
const every = Math.max(5, Number(opt('every', 20))) * 1000;
const maxMinutes = Number(opt('minutes', 20));
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const out = join(root, 'captures', stamp);
const shots = join(out, 'shots');
await mkdir(shots, { recursive: true });
await mkdir(join(out, 'state'), { recursive: true });

const started = Date.now();
const since = () => ((Date.now() - started) / 1000).toFixed(1).padStart(6);
const note = async (line) => { const text = `${new Date().toISOString()} [${since()}s] ${line}`; console.log(text); await appendFile(join(out, 'run.log'), text + '\n'); };

async function api(method, path, body) {
  try {
    const res = await fetch(base + path, { method, headers: body ? { 'Content-Type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
    const text = await res.text();
    let data; try { data = text ? JSON.parse(text) : undefined; } catch { data = text.slice(0, 200); }
    return { status: res.status, data };
  } catch (e) { return { status: 0, data: String(e.message ?? e) }; }
}

const health = await api('GET', '/api/health');
await note(`server ${base}: ${health.status} ${JSON.stringify(health.data)}`);
if (health.status !== 200) { await note('The server is not answering. Start it first: dotnet run --project src/MatchLens.Server'); process.exit(1); }
await writeFile(join(out, 'health.json'), JSON.stringify(health.data, null, 2));

if (flag('judge')) {
  const r = await api('POST', '/api/judge');
  await note(`judge: ${r.status} ${JSON.stringify(r.data)?.slice(0, 200)}`);
}

const browser = await chromium.launch({ headless: !flag('headed') });
const errors = [];
async function open(name, path, viewport, init) {
  const ctx = await browser.newContext({ viewport, deviceScaleFactor: 1 });
  if (init) await ctx.addInitScript(init);
  const page = await ctx.newPage();
  page.on('pageerror', (e) => { errors.push(`${name}: ${e.message}`); void note(`PAGE ERROR ${name}: ${e.message}`); });
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') void appendFile(join(out, 'console.log'), `${new Date().toISOString()} ${name} ${m.type()}: ${m.text()}\n`); });
  await page.goto(base + path, { waitUntil: 'domcontentloaded' });
  return page;
}

// The studio at a desktop size, the fan view at a phone size (past the first-run setup), the overlay at 1080p.
const studio = await open('studio', '/', { width: 1600, height: 1000 });
const fan = await open('fan', '/fan?judge=1', { width: 390, height: 844 },
  () => { try { localStorage.setItem('matchlens-fan', JSON.stringify({ v: 2, setupDone: true, level: 'hardcore', depth: 'tactical', learn: false, focus: 'match', team: '', player: '' })); } catch { /* ignore */ } });
// The fan view on a desktop too (the 3D stage), visiting the same views a round later than the phone.
const fanWide = await open('fan-desktop', '/fan?judge=1', { width: 1440, height: 900 },
  () => { try { localStorage.setItem('matchlens-fan', JSON.stringify({ v: 2, setupDone: true, level: 'hardcore', depth: 'tactical', learn: false, focus: 'match', team: '', player: '' })); } catch { /* ignore */ } });
const overlay = await open('overlay', '/overlay?mode=preview', { width: 1920, height: 1080 });
await new Promise((r) => setTimeout(r, 4000));

let n = 0;
const shot = async (page, name, opts = {}) => {
  const file = `${String(++n).padStart(4, '0')}-${name}.png`;
  try { await page.screenshot({ path: join(shots, file), ...opts }); } catch (e) { await note(`screenshot ${name} failed: ${e.message}`); }
  return file;
};
const click = async (page, locator, what) => {
  try { await locator.first().click({ timeout: 3000 }); await page.waitForTimeout(700); return true; }
  catch { /* covered, or the page is too busy drawing the 3D stage to settle: send the click straight to it */ }
  try { await locator.first().dispatchEvent('click', undefined, { timeout: 2000 }); await page.waitForTimeout(900); return true; }
  catch { await note(`could not open ${what}`); return false; }
};

// Studio sections and fan views visited in turn, one per round, so every screen is seen several times.
const studioTabs = ['On air', 'Tactical tests', "Coach's corner", 'Agent trace', 'Evaluation', 'Tactics'];
const fanStops = [
  { view: 'Live' }, { view: 'Analysis', tab: "Coach's corner" }, { view: 'Live' }, { view: 'Analysis', tab: 'Space and threat' },
  { view: 'Live' }, { view: 'Analysis', tab: 'Tests and calls' }, { view: 'Room' },
];

// Overlay: watch what is on screen every second and take a picture whenever it changes (graphics are short-lived).
let overlayKey = '';
const overlayWatch = setInterval(async () => {
  try {
    const meta = await overlay.evaluate(() => window.__overlay);
    if (!meta) return;
    const key = meta.graphics.filter((g) => g.type !== 'scoreBug').map((g) => g.id).join('|');
    if (key !== overlayKey) {
      overlayKey = key;
      await appendFile(join(out, 'overlay.jsonl'), JSON.stringify({ at: new Date().toISOString(), ...meta }) + '\n');
      if (key) await shot(overlay, `overlay-${meta.graphics.filter((g) => g.type !== 'scoreBug').map((g) => g.type).join('+')}`);
    }
  } catch { /* the page may be reloading */ }
}, 1000);

// State: the full snapshot each round, and a line per change worth knowing (new insights, tests, coach decisions).
const known = new Map();
async function state(round) {
  const r = await api('GET', '/api/session');
  if (r.status !== 200 || typeof r.data !== 'object') { await note(`session: ${r.status}`); return undefined; }
  const s = r.data;
  await writeFile(join(out, 'state', `round-${String(round).padStart(3, '0')}.json`), JSON.stringify(s));
  const clock = (sec) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`;
  const seen = (id, line) => { if (known.get(id) !== line) { known.set(id, line); return note(line); } };
  await note(`clock ${clock(s.tick.clockSec)} ${s.tick.status} ${s.tick.homeGoals}-${s.tick.awayGoals}, insights ${s.insights.length}, tests ${s.tests?.length ?? 0}, coaches ${s.coaches?.length ?? 0}, queue ${s.tick.queueLength}`);
  for (const i of s.insights ?? []) if (i.personaKey?.startsWith('Broadcast-en')) await seen(`ins-${i.insight.insightId}`, `insight ${clock(i.insight.matchClockSec)} ${i.status}: ${i.insight.headline}`);
  for (const t of s.tests ?? []) await seen(`test-${t.testId}`, `test ${t.testId} ${t.status}: ${t.criterion} | ${t.progressText} | estimate ${t.estimate ?? '-'}`);
  for (const d of s.coaches ?? []) await seen(`coach-${d.decisionId}`, `coach ${d.decisionId}: ${d.proposals.map((p) => `${p.coach}=${p.plan}${p.fromModel ? '' : '(rule)'}`).join(' vs ')}, pick ${d.picked ?? 'none'} ${JSON.stringify(d.probabilities ?? {})} via ${d.source}, verdict ${d.verdict ?? '-'}`);
  return s;
}

let exercised = !flag('exercise');
const deadline = started + Math.min(maxMinutes, 20) * 60_000;
let round = 0;
let finishedRounds = 0;
while (Date.now() < deadline) {
  round++;
  const s = await state(round);

  const tab = studioTabs[(round - 1) % studioTabs.length];
  if (await click(studio, studio.getByRole('tab', { name: tab }), `studio ${tab}`)) {
    // The section itself (it sits below the live board), or the page if it cannot be found.
    const section = studio.locator('section.lower');
    if (await section.count()) await shot(section, `studio-${tab.replace(/\W+/g, '-').toLowerCase()}`);
    else await shot(studio, `studio-${tab.replace(/\W+/g, '-').toLowerCase()}`);
  }
  await shot(studio, 'studio-full', { fullPage: true });

  const stop = fanStops[(round - 1) % fanStops.length];
  await click(fan, fan.locator('button:visible, [role=tab]:visible').filter({ hasText: new RegExp(`^${stop.view}$`) }), `fan ${stop.view}`);
  if (stop.tab) await click(fan, fan.getByRole('tab', { name: stop.tab }), `fan ${stop.tab}`);
  await shot(fan, `fan-${(stop.tab ?? stop.view).replace(/\W+/g, '-').toLowerCase()}`, { fullPage: true });
  const wideStop = fanStops[round % fanStops.length];
  await click(fanWide, fanWide.locator('button:visible, [role=tab]:visible').filter({ hasText: new RegExp(`^${wideStop.view}$`) }), `fan desktop ${wideStop.view}`);
  if (wideStop.tab) await click(fanWide, fanWide.getByRole('tab', { name: wideStop.tab }), `fan desktop ${wideStop.tab}`);
  await shot(fanWide, `fandesk-${(wideStop.tab ?? wideStop.view).replace(/\W+/g, '-').toLowerCase()}`);

  // Once the match is under way: ask the coaches for both teams, try a what-if on the studio's sandbox, and record it.
  if (!exercised && s && s.tick.status === 'running' && s.tick.clockSec > 40 * 60) {
    exercised = true;
    for (const team of [s.info.match.home, s.info.match.away]) {
      const r = await api('POST', `/api/coach/${team.teamId}`);
      await note(`ask the coaches (${team.shortName}): ${r.status} ${r.status === 200 ? `${r.data.proposals.map((p) => p.plan).join(' vs ')}, pick ${r.data.picked ?? 'none'} via ${r.data.source}` : JSON.stringify(r.data)}`);
    }
    if (await click(studio, studio.getByRole('tab', { name: "Coach's corner" }), 'studio coach')) {
      await shot(studio, 'studio-coach-after-ask', { fullPage: true });
      for (const change of ['Push up', 'Press the ball']) {
        await click(studio, studio.getByRole('tab', { name: change }), `what-if ${change}`);
        if (await click(studio, studio.getByRole('button', { name: /Try it on the frame now/ }), 'what-if run')) {
          await studio.waitForTimeout(6500);
          await studio.locator('.cc-whatif').screenshot({ path: join(shots, `${String(++n).padStart(4, '0')}-whatif-${change.replace(/\W+/g, '-').toLowerCase()}.png`) }).catch(() => {});
        }
      }
    }
    const w = await api('POST', '/api/whatif', { teamId: s.info.match.home.teamId, change: 'pushUp', metres: 8 });
    await note(`what-if (API, ${s.info.match.home.shortName} push up 8 m): ${w.status} ${w.status === 200 ? `final third ${w.data.finalThirdBefore} -> ${w.data.finalThirdAfter}, own ${w.data.ownThirdBefore} -> ${w.data.ownThirdAfter}, help ${w.data.helpProbability ?? '-'} via ${w.data.source}` : JSON.stringify(w.data)}`);
  }

  // Stop a couple of rounds after full time, so the recaps and the last verdicts are in.
  if (s && (s.tick.status === 'finished' || s.tick.status === 'stopped')) { if (++finishedRounds >= 3) break; }
  await new Promise((r) => setTimeout(r, every));
}

clearInterval(overlayWatch);
const last = await state(round + 1);
await writeFile(join(out, 'summary.json'), JSON.stringify({
  base, started: new Date(started).toISOString(), seconds: Math.round((Date.now() - started) / 1000), rounds: round, screenshots: n,
  health: health.data, browserErrors: errors,
  final: last && { clockSec: last.tick.clockSec, status: last.tick.status, score: [last.tick.homeGoals, last.tick.awayGoals], insights: last.insights.length,
    tests: (last.tests ?? []).map((t) => ({ id: t.testId, status: t.status, estimate: t.estimate ?? null })),
    coaches: (last.coaches ?? []).map((d) => ({ id: d.decisionId, picked: d.picked ?? null, source: d.source, verdict: d.verdict ?? null })) },
}, null, 2));
await note(`done: ${n} screenshots, ${errors.length} browser errors, in ${out}`);
await browser.close();
