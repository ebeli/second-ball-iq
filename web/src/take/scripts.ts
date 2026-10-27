import { api } from '../api';
import type { MatchInfo } from '../types';
import { find, mmss, press, scrollToSel, scrollTop, sleep, type Cue, type as typeText, waitFor } from './autopilot';

/**
 * The three takes of the demo video, timed to the demo match (judge mode: 38′ at 8×, 7.5 s per match minute).
 * docs/DEMO.md has the same tables in words.
 */

const fanNav = async (label: string) => { await press('.fan-bottom button', label); await scrollTop(); };
const camTo = (mode: string) => window.dispatchEvent(new CustomEvent('s3d-cam', { detail: mode }));

/** Opens Personalise from the "…" menu (English or Finnish). */
async function openPersonalise() {
  await press('button[aria-label="More"], button[aria-label="Lisää"]');
  await sleep(300);
  await press('[role="menuitem"]', /Personalise|Omat asetukset/);
  await sleep(700);
}
const save = async () => { await sleep(500); await press('.setup-foot .primary'); await scrollTop(); };

/** The prefs a fan take starts from: tactics fan, English, whole match, commentator on. */
export const TAKE_FAN_PREFS = {
  v: 2, setupDone: true, level: 'hardcore', depth: 'tactical', learn: false, focus: 'match', team: '', player: '',
  read: true, listen: true, ask: false, social: 'solo', name: '', language: 'en',
};

/** Take 1: the fan app, 38′ to full time. */
export function fanTake1(): Cue[] {
  return [
    { at: 0, always: true, label: 'broadcast camera, commentator on', run: async () => {
      await waitFor(() => document.querySelector('.s3d'), 30000).catch(() => undefined);
      camTo('auto');
      await sleep(800);
      if (find('button[aria-label="Listen"]')) await press('button[aria-label="Listen"]');
    } },
    { at: mmss(39, 30), label: 'close the judge-mode note', run: () => press('.judge-banner button', /Close|Sulje/) },
    { at: mmss(42, 10), label: 'Personalise: New to football', run: async () => {
      await openPersonalise();
      await press('.setup-card', 'New to football');
      await save();
    } },
    { at: mmss(43, 40), label: 'Personalise: Suomi', run: async () => {
      await openPersonalise();
      await press('.setup-seg button', 'Suomi');
      await save();
    } },
    { at: mmss(45, 20), label: 'Personalise: tactics, English, My player Arlo Quinn', run: async () => {
      await openPersonalise();
      await press('.setup-seg button', 'English');
      await sleep(300);
      await press('.setup-card', 'I live for tactics');
      await press('.setup-seg button', 'My player');
      await press('.setup-players button', 'Arlo Quinn');
      await save();
    } },
    { at: mmss(47, 0), label: 'player cam', run: () => press('.cam-btn') },
    { at: mmss(48, 40), label: 'camera back to auto', run: () => press('.s3d-cams button', /^(Auto|Ohjaaja)$/) },
    { at: mmss(49, 10), label: 'Room', run: () => fanNav('Room') },
    { at: mmss(53, 10), label: 'vote on the pressing test', run: () =>
      press('.party-poll.is-test:not(.closed) .pp-options button', undefined, 40000) },
    { at: mmss(62, 10), label: "Analysis › Coach's corner", run: async () => {
      await fanNav('Analysis');
      await sleep(800);
      await press('.an-tabs button', "Coach's corner");
    } },
    { at: mmss(63, 40), label: 'Analysis › Space and threat', run: () => press('.an-tabs button', 'Space and threat') },
    { at: mmss(65, 0), label: 'Ask: how did that goal happen?', run: async () => {
      await fanNav('Ask');
      await sleep(600);
      await typeText('.ask-form input', 'How did that goal happen?');
      await press('.ask-form button[type="submit"]');
    } },
    // Live analytics: the panel's boards and the 3D telestrator on the same play, both updating as the match runs.
    { at: mmss(68, 30), label: 'Analysis › Formations + 3D tactical camera with the shape', run: async () => {
      await fanNav('Analysis');
      await sleep(600);
      await press('.an-tabs button', 'Formations');
      await press('.tactics-controls .tseg button', 'Both');
      await press('.tactics-controls .tseg button', 'Last 15');
      await press('.s3d-cams button', /^(Tactical|Taktinen)$/);
      await press('.s3d-dvr button', /^(Shape|Muoto)$/);
    } },
    { at: mmss(70, 0), label: 'Heat maps (HAR) + 3D pitch control', run: async () => {
      await press('.an-tabs button', 'Heat maps');
      await press('.tactics-controls .tseg button', /^HAR$/);
      await press('.s3d-dvr button', /^(Shape|Muoto)$/);
      await press('.s3d-dvr button', /^(Space|Tila)$/);
    } },
    { at: mmss(70, 50), label: 'Heat maps: compare NOR', run: () => press('.tactics-controls .tseg button', /^NOR$/) },
    { at: mmss(71, 40), label: 'Tests and calls (3-4-1-2 confirmed at 72:01) + 3D runs', run: async () => {
      await press('.an-tabs button', 'Tests and calls');
      await press('.s3d-dvr button', /^(Space|Tila)$/);
      await press('.s3d-dvr button', /^(Runs|Juoksut)$/);
    } },
    { at: mmss(73, 10), label: 'Running, passes, speed', run: () => press('.an-tabs button', 'Running, passes, speed') },
    { at: mmss(74, 30), label: 'back to Live, broadcast camera', run: async () => {
      await press('.s3d-dvr button', /^(Runs|Juoksut)$/);
      await press('.s3d-cams button', /^(Auto|Ohjaaja)$/);
      await fanNav('Live');
    } },
  ];
}

/** Take 3: the broadcast graphics over the match picture; the coaches are asked so their card goes to air. */
export function fanTake3(info: () => MatchInfo | undefined): Cue[] {
  return [
    { at: 0, always: true, label: 'clean feed, auto camera, commentator on', run: async () => {
      // The 3D stage loads after the page; wait for it before asking for the clean feed.
      await waitFor(() => document.querySelector('.s3d'), 30000);
      await sleep(500);
      camTo('auto');
      window.dispatchEvent(new CustomEvent('s3d-clean', { detail: true }));
      await sleep(800);
      // The header is hidden in this take; the voice is switched on through its (hidden) button.
      (document.querySelector('button[aria-label="Listen"]') as HTMLButtonElement | null)?.click();
    } },
    { at: mmss(43, 0), label: 'ask the coaches (both teams)', run: async () => {
      const m = info();
      if (!m) return;
      await api.coach(m.home.teamId).catch(() => undefined);
      await api.coach(m.away.teamId).catch(() => undefined);
    } },
  ];
}

const studioTab = async (label: string) => { await press('.snav [role="tab"]', label); await sleep(400); };
const lower = () => scrollToSel('.lower', 'start');

/** Take 2: the studio. */
export function studioTake2(): Cue[] {
  return [
    { at: mmss(38, 10), label: 'Evaluation › plant a wrong number', run: async () => {
      await studioTab('Evaluation');
      await lower();
      await press('button', 'Plant a wrong number');
    } },
    { at: mmss(40, 45), label: 'On air', run: async () => { await scrollTop(); await studioTab('On air'); } },
    { at: mmss(41, 15), label: 'preview for the analyst', run: () => persona('Analyst (English)') },
    { at: mmss(41, 30), label: 'preview for the Finnish commentator', run: () => persona('Broadcast (Suomi)') },
    { at: mmss(41, 50), label: 'preview back to casual', run: () => persona('Casual (English)') },
    { at: mmss(42, 5), label: 'Agent trace of the goal', run: async () => {
      await studioTab('Agent trace');
      await lower();
      await press('.lower .card-head', /Goal,/, 20000);
    } },
    { at: mmss(43, 20), label: 'Evaluation: the planted number caught', run: async () => {
      await scrollTop();
      await studioTab('Evaluation');
      await lower();
    } },
    { at: mmss(44, 40), label: "Coach's corner: ask both teams' coaches", run: async () => {
      await scrollTop();
      await studioTab("Coach's corner");
      await lower();
      await press('.lower .cc-ask', 'Ask the coaches now');
      await sleep(400);
      await press('.lower .cc-ask', 'Ask the coaches now');
    } },
    { at: mmss(46, 40), label: 'What if: push up 8 m', run: async () => {
      await scrollToSel('.cc-whatif', 'center');
      await press('.cc-whatif .an-tabs button', 'Push up');
      await press('.cc-whatif .cc-ask', 'Try it on the frame now');
      await waitFor(() => find('.cc-whatif .cc-ask', 'Try it on the frame now', { enabled: true }), 20000).catch(() => undefined);
      await sleep(300);
      await scrollToSel('.cc-whatif', 'start');
    } },
    { at: mmss(47, 40), label: 'Tactical tests', run: async () => { await scrollTop(); await studioTab('Tactical tests'); await lower(); } },
    { at: mmss(55, 0), label: 'On air', run: async () => { await scrollTop(); await studioTab('On air'); } },
    // The live board in Shape mode across Northvale's switch to a back three (57:00), then the heat map.
    { at: mmss(55, 40), label: 'board: Shape', run: () => press('.board-panel [role="tab"]', 'Shape') },
    { at: mmss(58, 10), label: 'board: Heat map, NOR', run: async () => {
      await press('.board-panel [role="tab"]', 'Heat map');
      await press('.board-mode-teams button', /^NOR$/);
      await scrollTop();
    } },
    { at: mmss(59, 40), label: 'board: Live', run: () => press('.board-panel [role="tab"]', 'Live') },
    { at: mmss(60, 20), label: "Coach's corner: the verdicts", run: async () => {
      await studioTab("Coach's corner");
      await lower();
    } },
  ];
}

async function persona(label: string) {
  await press('.out-persona [role="combobox"], .out-persona button');
  await sleep(350);
  await press('[role="option"]', label);
}
