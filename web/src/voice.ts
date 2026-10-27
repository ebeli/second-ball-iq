import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';

/** Something to read aloud: the server's neural-voice audio, with the text for the browser's own voice as fallback. */
export interface Utterance {
  url: string;
  text: string;
  language: string;
  /** 1 analysis and recaps, 2 commentator calls, 3 goals. Higher goes first; 3 cuts in on anything lower. */
  priority?: number;
  /** Live calls go stale: skipped if they waited longer than this in the queue. */
  maxAgeMs?: number;
  queuedAt?: number;
}

export type VoiceSource = 'azure' | 'browser';

/**
 * Reads items aloud one after another. Audio comes from Azure AI Speech through the server; when that fails
 * (no Speech settings, network) the browser's built-in voice reads the same text, so the switch always does
 * something. Turning it on is a click, which also satisfies the browser's autoplay rule.
 */
export function useVoice() {
  const [on, setOn] = useState(false);
  const [source, setSource] = useState<VoiceSource>();
  const [speaking, setSpeaking] = useState(false);
  const onRef = useRef(false);
  const queue = useRef<Utterance[]>([]);
  const busy = useRef(false);
  const current = useRef<HTMLAudioElement | null>(null);
  const currentPriority = useRef(0);
  /** Bumped on every stop, so a playback that was cut off does not carry on with the queue. */
  const generation = useRef(0);
  /** After the server's voice fails once, go straight to the browser's voice instead of failing every time. */
  const serverFailed = useRef(false);

  const playNext = useCallback(async () => {
    if (busy.current) return;
    let item = queue.current.shift();
    while (item?.maxAgeMs && item.queuedAt && Date.now() - item.queuedAt > item.maxAgeMs) item = queue.current.shift();
    if (!item) { setSpeaking(false); currentPriority.current = 0; return; }
    currentPriority.current = item.priority ?? 1;
    busy.current = true;
    setSpeaking(true);
    const gen = generation.current;
    try {
      if (serverFailed.current) throw new Error('server voice unavailable');
      await playAudio(item.url, current);
      setSource('azure');
    } catch {
      if (gen !== generation.current) return;
      serverFailed.current = true;
      await speakInBrowser(item.text, item.language);
      setSource('browser');
    } finally {
      if (gen === generation.current) {
        busy.current = false;
        void playNext();
      }
    }
  }, []);

  /** Queue an item if the voice is on: ahead of anything less urgent, and a goal interrupts what is playing. */
  const say = useCallback((item: Utterance) => {
    if (!onRef.current) return;
    const p = item.priority ?? 1;
    const entry = { ...item, queuedAt: Date.now() };
    const at = queue.current.findIndex((q) => (q.priority ?? 1) < p);
    if (at < 0) queue.current.push(entry); else queue.current.splice(at, 0, entry);
    if (p >= 3 && busy.current && currentPriority.current < 3) {
      // Cut in: stop the current item but keep the queue.
      generation.current++;
      current.current?.pause();
      current.current = null;
      if ('speechSynthesis' in window) window.speechSynthesis.cancel();
      busy.current = false;
    }
    void playNext();
  }, [playNext]);

  const stop = useCallback(() => {
    generation.current++;
    stopAll(queue, current);
    busy.current = false;
    setSpeaking(false);
  }, []);

  /** Read one item now, whatever the switch says (a "Listen" button). */
  const sayNow = useCallback((item: Utterance) => {
    stop();
    queue.current.push(item);
    void playNext();
  }, [playNext, stop]);

  const toggle = useCallback(() => {
    const next = !onRef.current;
    onRef.current = next;
    setOn(next);
    if (!next) stop();
  }, [stop]);

  useEffect(() => () => stopAll(queue, current), []);

  return { on, toggle, say, sayNow, stop, source, speaking };
}

function playAudio(url: string, holder: MutableRefObject<HTMLAudioElement | null>): Promise<void> {
  return new Promise((resolve, reject) => {
    const audio = new Audio(url);
    holder.current = audio;
    audio.onended = () => resolve();
    audio.onerror = () => reject(new Error('audio failed'));
    audio.play().catch(reject);
  });
}

function speakInBrowser(text: string, language: string): Promise<void> {
  return new Promise((resolve) => {
    if (!('speechSynthesis' in window)) { resolve(); return; }
    const u = new SpeechSynthesisUtterance(text);
    u.lang = language === 'fi' ? 'fi-FI' : 'en-GB';
    const done = () => { window.clearTimeout(guard); resolve(); };
    const guard = window.setTimeout(done, 30000);
    u.onend = done;
    u.onerror = done;
    window.speechSynthesis.speak(u);
  });
}

function stopAll(queue: MutableRefObject<Utterance[]>, current: MutableRefObject<HTMLAudioElement | null>) {
  queue.current = [];
  current.current?.pause();
  current.current = null;
  if ('speechSynthesis' in window) window.speechSynthesis.cancel();
}

export const insightUtterance = (insightId: string, text: string, language: string): Utterance =>
  ({ url: `/api/speech/insight/${encodeURIComponent(insightId)}`, text, language });

export const recapUtterance = (recapId: string, text: string, language: string): Utterance =>
  ({ url: `/api/speech/recap/${encodeURIComponent(recapId)}`, text, language });

export const callUtterance = (callId: string, text: string, language: string, intensity: number): Utterance =>
  ({ url: `/api/speech/call/${encodeURIComponent(callId)}?lang=${language}`, text, language,
     priority: intensity >= 3 ? 3 : 2, maxAgeMs: 12000 });

export const askUtterance = (askId: string, text: string, language: string): Utterance =>
  ({ url: `/api/speech/ask/${encodeURIComponent(askId)}`, text, language, priority: 1 });
