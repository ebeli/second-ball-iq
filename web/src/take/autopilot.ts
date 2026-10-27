import { useEffect, useRef } from 'react';

/**
 * Scripted demo takes for recording the video: open /judge?take=1 (fan app), /?take=2 (studio) or /judge?take=3
 * (broadcast graphics over the match picture), start the screen recorder, and the page plays the take by itself.
 * Cues run at match times (the demo match is the same every run), and press the real buttons with a visible pointer,
 * so the recording looks like someone using the app.
 */
export const TAKE: number | undefined = (() => {
  try {
    const n = Number(new URLSearchParams(window.location.search).get('take'));
    return n >= 1 && n <= 3 ? n : undefined;
  } catch { return undefined; }
})();

export interface Cue {
  /** Match clock, seconds. */
  at: number;
  label: string;
  run: () => Promise<unknown> | unknown;
  /** Runs even when the page joins the match after this time (setup steps). */
  always?: boolean;
}

export const mmss = (m: number, s = 0) => m * 60 + s;
export const sleep = (ms: number) => new Promise<void>((r) => window.setTimeout(r, ms));

/** Plays the cues in order as the match clock passes them, one at a time. */
export function useAutopilot(cues: Cue[] | undefined, clockSec: number | undefined, matchKey: string) {
  const state = useRef<{ key: string; done: Set<number>; chain: Promise<unknown> }>();
  useEffect(() => {
    if (!cues || clockSec === undefined || !matchKey) return;
    if (!state.current || state.current.key !== matchKey) {
      // Joining late (a reload in the middle of a take): what has passed is not replayed, except setup steps.
      const done = new Set<number>();
      cues.forEach((c, i) => { if (!c.always && c.at < clockSec - 3) done.add(i); });
      state.current = { key: matchKey, done, chain: Promise.resolve() };
      installStyle();
    }
    const s = state.current;
    cues.forEach((c, i) => {
      if (s.done.has(i) || clockSec < c.at) return;
      s.done.add(i);
      s.chain = s.chain
        .then(() => { console.info(`[take ${TAKE}] ${clockText(clockSec)} ${c.label}`); return c.run(); })
        .catch((e: unknown) => console.warn(`[take ${TAKE}] ${c.label} skipped:`, e));
    });
  }, [cues, clockSec, matchKey]);
}

const clockText = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`;

// ----------------------------------------------------------------------------------------------------------------
// Finding and pressing things on the page
// ----------------------------------------------------------------------------------------------------------------

function shown(el: Element): el is HTMLElement {
  const r = (el as HTMLElement).getBoundingClientRect?.();
  return !!r && r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
}

/** The first visible element for the selector whose text matches (substring, or a pattern). */
export function find(selector: string, text?: string | RegExp, opts: { enabled?: boolean } = {}): HTMLElement | undefined {
  for (const el of Array.from(document.querySelectorAll(selector))) {
    if (!shown(el)) continue;
    if (opts.enabled && (el as HTMLButtonElement).disabled) continue;
    if (text !== undefined) {
      const t = (el.textContent ?? '').replace(/\s+/g, ' ').trim();
      if (typeof text === 'string' ? !t.toLowerCase().includes(text.toLowerCase()) : !text.test(t)) continue;
    }
    return el;
  }
  return undefined;
}

export async function waitFor<T>(fn: () => T | undefined | null | false, timeoutMs = 10000): Promise<T> {
  const until = Date.now() + timeoutMs;
  for (;;) {
    const v = fn();
    if (v) return v;
    if (Date.now() > until) throw new Error('not found in time');
    await sleep(150);
  }
}

/** Scrolls an element into the middle of the view when it is not fully visible. */
export async function reveal(el: Element, block: ScrollLogicalPosition = 'center') {
  const r = el.getBoundingClientRect();
  if (r.top >= 0 && r.bottom <= window.innerHeight) return;
  el.scrollIntoView({ behavior: 'smooth', block });
  await sleep(700);
}

export async function scrollToSel(selector: string, block: ScrollLogicalPosition = 'start') {
  const el = document.querySelector(selector);
  if (!el) return;
  el.scrollIntoView({ behavior: 'smooth', block });
  await sleep(700);
}

export async function scrollTop() {
  window.scrollTo({ top: 0, behavior: 'smooth' });
  document.querySelector('.fan-panel')?.scrollTo({ top: 0, behavior: 'smooth' });
  await sleep(600);
}

/** Moves the pointer to the button and presses it, as a person would. */
export async function press(selector: string, text?: string | RegExp, timeoutMs = 10000) {
  const el = await waitFor(() => find(selector, text, { enabled: true }), timeoutMs);
  await reveal(el);
  await pointAt(el);
  ripple();
  el.click();
  await sleep(350);
}

/** Types into an input letter by letter (React sees each keystroke). */
export async function type(selector: string, text: string) {
  const el = await waitFor(() => find(selector) as HTMLInputElement | undefined);
  await reveal(el);
  await pointAt(el);
  ripple();
  el.focus();
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
  for (let i = 1; i <= text.length; i++) {
    setter.call(el, text.slice(0, i));
    el.dispatchEvent(new Event('input', { bubbles: true }));
    await sleep(55 + Math.random() * 45);
  }
  await sleep(300);
}

// ----------------------------------------------------------------------------------------------------------------
// The pointer
// ----------------------------------------------------------------------------------------------------------------

let pointer: HTMLDivElement | undefined;
let hideTimer = 0;
let at = { x: 0, y: 0 };

function ensurePointer(): HTMLDivElement {
  if (pointer) return pointer;
  pointer = document.createElement('div');
  pointer.className = 'take-pointer';
  pointer.innerHTML = '<svg viewBox="0 0 24 24" width="30" height="30" aria-hidden="true"><path d="M4 2.5 L4 19 L8.6 14.9 L11.6 21.5 L14.6 20.2 L11.7 13.7 L18 13.4 Z" fill="#fff" stroke="#0b1530" stroke-width="1.6" stroke-linejoin="round"/></svg>';
  at = { x: window.innerWidth * 0.62, y: window.innerHeight + 40 };
  pointer.style.transform = `translate(${at.x}px, ${at.y}px)`;
  document.body.appendChild(pointer);
  return pointer;
}

async function pointAt(el: HTMLElement) {
  const p = ensurePointer();
  window.clearTimeout(hideTimer);
  p.classList.add('on');
  const r = el.getBoundingClientRect();
  const x = r.left + Math.min(r.width / 2, 60), y = r.top + r.height / 2;
  const dist = Math.hypot(x - at.x, y - at.y);
  const ms = Math.round(Math.min(900, Math.max(350, dist * 0.7)));
  p.style.transitionDuration = `${ms}ms`;
  p.style.transform = `translate(${x}px, ${y}px)`;
  at = { x, y };
  await sleep(ms + 120);
  hideTimer = window.setTimeout(() => p.classList.remove('on'), 2600);
}

function ripple() {
  const r = document.createElement('span');
  r.className = 'take-ripple';
  r.style.left = `${at.x}px`;
  r.style.top = `${at.y}px`;
  document.body.appendChild(r);
  window.setTimeout(() => r.remove(), 700);
}

function installStyle() {
  if (document.getElementById('take-style')) return;
  document.documentElement.classList.add('take-mode');
  const s = document.createElement('style');
  s.id = 'take-style';
  s.textContent = `
html.take-mode, html.take-mode * { cursor: none !important; }
.take-pointer { position: fixed; left: 0; top: 0; z-index: 2147483647; pointer-events: none; opacity: 0;
  transition-property: transform, opacity; transition-timing-function: cubic-bezier(.45,.05,.25,1), ease; transition-duration: 600ms, 250ms;
  filter: drop-shadow(0 3px 6px rgba(0,0,0,.55)); margin: -3px 0 0 -5px; }
.take-pointer.on { opacity: 1; }
.take-ripple { position: fixed; z-index: 2147483646; pointer-events: none; width: 46px; height: 46px; margin: -23px 0 0 -23px; border-radius: 50%;
  border: 3px solid rgba(80, 230, 170, .95); box-shadow: 0 0 18px rgba(80,230,170,.6); animation: take-ripple 650ms ease-out forwards; }
@keyframes take-ripple { from { transform: scale(.35); opacity: 1; } to { transform: scale(1.5); opacity: 0; } }
`;
  document.head.appendChild(s);
}
