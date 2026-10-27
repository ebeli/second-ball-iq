import { TAKE } from '../take/autopilot';
import { useCallback, useEffect, useMemo, useRef, useState, type MutableRefObject, type ReactNode, type RefObject } from 'react';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import * as THREE from 'three';
import { frameAt, getFrame, getHistory, useHasFrame } from '../tracking';
import { kitOf, playerTeam } from '../kit';
import { metricTag } from '../metrics';
import { clock as clockText } from '../format';
import { Stage3DBridge, type Rewind3D } from '../stage3dBridge';
import type { Call, MatchInfo, PitchEvent, RunnerView, TagView, TickView, TrackingView } from '../types';
import { buildAvatar, type AvatarKit, type AvatarRig } from './stage3d/Avatar';
import { hashOf, L, lerpAngle, placeQuad, project, SKIN, W, wx, wz } from './stage3d/math';
import { CAM_MODES, type CamMode, type Cut, type Layers, type Rig } from './stage3d/rig';
import { BallTrail, BigScreens, Floodlights, Goals, Grass, Ground, LedBoards, SCREENS } from './stage3d/Stadium';
import { Telestrator, type HullLabel, type TeamSets } from './stage3d/Telestrator';

/**
 * The match stage in 3D (React Three Fiber): a procedural night stadium (stands and crowd in the clubs' colours,
 * floodlights with beams, LED boards, two big screens, camera flashes), 22 articulated players from the tracking
 * frames, and the ball with its trail.
 *
 * - Cameras: Auto (the director: follows the ball and cuts behind the goal on a goal or a big chance), Broadcast
 *   (follows, no cuts), Tactical (high and wide) and Drone (a slow orbit). The follow cameras widen as the ball
 *   travels faster; a goal shakes the picture.
 * - DVR: the last 15 seconds of tracking can be paused, scrubbed and played again, with the telestrator (offside
 *   line, team shapes, high-intensity runs, a locked player with his live speed) drawn from the frame on screen.
 * - H hides every control and graphic for a clean feed; F puts the stage full screen; 1–4 pick the camera.
 *
 * Everything that is text stays ordinary DOM in the page's own React tree (so it keeps the theme and stays crisp):
 * each frame the scene projects a plane or a point and moves the element there with a CSS perspective transform.
 * That is how tags sit over the players, the pinned card hangs on the far stand, the big screens show the score and
 * the commentator's latest call, and a replay or heat map from the 2D board is drawn onto the 3D grass.
 */

/** Seconds of match time a tag stays on a player (the same as the 2D board). */
const TAG_SEC = 40;
const KEEPER = { home: '#c8e63c', away: '#f08a24' };

export type KitStyle = 'club' | 'contrast' | 'classic';
const KIT_STYLES: KitStyle[] = ['club', 'contrast', 'classic'];
interface KitColours { shirt: string; shorts: string; socks: string; ink: string }

/** The kits the 3D players wear: the clubs' own colours, or one of two high-contrast sets for video. */
function kitsFor(info: MatchInfo, style: KitStyle): { home: KitColours; away: KitColours } {
  if (style === 'contrast') return {
    home: { shirt: '#d7262e', shorts: '#ffffff', socks: '#d7262e', ink: '#ffffff' },
    away: { shirt: '#0f66c8', shorts: '#0b2346', socks: '#ffffff', ink: '#ffffff' },
  };
  if (style === 'classic') return {
    home: { shirt: '#f3f5f7', shorts: '#14181d', socks: '#f3f5f7', ink: '#10171c' },
    away: { shirt: '#1c1f24', shorts: '#1c1f24', socks: '#f5c518', ink: '#f5c518' },
  };
  const club = (t: MatchInfo['home']): KitColours => ({
    shirt: t.primaryColor, shorts: '#' + new THREE.Color(t.primaryColor).multiplyScalar(0.28).getHexString(), socks: t.primaryColor, ink: kitOf(t).ink,
  });
  return { home: club(info.home), away: club(info.away) };
}

export interface Stage3DProps {
  info: MatchInfo;
  tick: TickView;
  events: PitchEvent[];
  calls: Call[];
  tags: TagView[];
  running?: RunnerView[];
  fi?: boolean;
  /** Something put on the stage to look at (a replay, an answer's evidence): its board goes on the grass. */
  visualNode?: ReactNode;
  /** The tab's own drawing when it is not the live match (the Players tab's shape and heat map). */
  boardBase?: ReactNode;
  pinned?: ReactNode;
  focusPlayer?: string;
}

const load = <T extends string>(key: string, ok: readonly T[], fallback: T): T => {
  try { const v = localStorage.getItem(key) as T | null; return v && ok.includes(v) ? v : fallback; } catch { return fallback; }
};
const save = (key: string, v: string) => { try { localStorage.setItem(key, v); } catch { /* private mode */ } };

export default function Stage3D({ info, tick, events, calls, tags, running, fi, visualNode, boardBase, pinned, focusPlayer }: Stage3DProps) {
  const hud = visualNode ?? boardBase;
  const reduced = useMemo(() => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false, []);
  const [cam, setCam] = useState<CamMode>(() => load('ml-cam', CAM_MODES, 'auto'));
  const [kitStyle, setKitStyle] = useState<KitStyle>(() => load('ml-kits', KIT_STYLES, 'club'));
  const [layers, setLayers] = useState<Layers>({ offside: false, shape: false, runs: false, control: false });
  const [lock, setLock] = useState<string | undefined>(focusPlayer);
  // Following a player (fan settings) locks them; a later change of the followed player moves the lock too.
  useEffect(() => { if (focusPlayer) setLock(focusPlayer); }, [focusPlayer]);
  // The recording take for the broadcast graphics (/judge?take=3) starts on the clean feed.
  const [clean, setClean] = useState(TAKE === 3);
  const [hint, setHint] = useState<string>();
  const rig = useRef<Rig>({
    mode: 'live', bar: false, cam, reduced, board: null, panel: null, screens: [null, null], lockEl: null, hullEls: [null, null], tags: [],
    flashes: [], actions: new Map(), dvr: { live: true, cursor: 0, playing: false }, layers, speeds: new Map(), vel: new Map(), at: new Map(),
  });
  const [board, setBoard] = useState<HTMLDivElement | null>(null);
  const [rewind, setRewind] = useState<Rewind3D>();
  const [cut, setCut] = useState<Cut>();
  const [hulls, setHulls] = useState<(HullLabel | undefined)[]>([]);
  const rootRef = useRef<HTMLDivElement>(null);
  const hasFrames = useHasFrame();

  const r = rig.current;
  r.mode = hud ? 'board' : 'live';
  r.bar = !!visualNode;
  r.board = board;
  r.cam = cam;
  r.layers = layers;
  r.lock = lock;
  r.rewind = rewind ? { ...rewind, since: r.rewind?.step === rewind.step && r.rewind.events === rewind.events ? r.rewind.since : performance.now() } : undefined;

  const teams = useMemo<TeamSets>(() => {
    const k = kitsFor(info, kitStyle);
    const glow = (c: string) => '#' + new THREE.Color(c).offsetHSL(0, 0.15, 0.16).getHexString();
    return {
      home: new Set(info.home.players.map((p) => p.playerId)), away: new Set(info.away.players.map((p) => p.playerId)),
      keepers: new Set([...info.home.players, ...info.away.players].filter((p) => p.position === 'goalkeeper').map((p) => p.playerId)),
      // three.js cannot read the CSS hsl() of the kit's glow colour, so the scene works from the shirt colour in hex.
      homeColor: glow(kitStyle === 'club' ? info.home.primaryColor : k.home.shirt),
      awayColor: glow(kitStyle === 'club' ? info.away.primaryColor : k.away.shirt),
    };
  }, [info, kitStyle]);

  // --- What happens in the match drives the scene: calls (cuts, net, flashes), events (kicks, tackles, dives).
  const seenCalls = useRef<Set<string>>();
  useEffect(() => {
    if (!seenCalls.current) { seenCalls.current = new Set(calls.map((c) => c.callId)); return; }
    for (const c of calls) {
      if (seenCalls.current.has(c.callId)) continue;
      seenCalls.current.add(c.callId);
      if (c.kind !== 'goal' && c.kind !== 'bigChance') continue;
      const now = performance.now();
      const goal = c.kind === 'goal';
      const away = c.teamId === info.away.teamId;
      const team = away ? info.away : info.home;
      const side: 1 | -1 = away ? -1 : 1;
      const next: Cut = { kind: c.kind, side, until: now + (goal ? 7000 : 4500),
        label: goal ? (fi ? `Maali · ${team.shortName}` : `Goal · ${team.shortName}`) : (fi ? 'Iso paikka' : 'Big chance') };
      rig.current.cut = next;
      setCut(next);
      rig.current.flashes.push({ at: now, n: goal ? 260 : 70, dur: goal ? 4000 : 1500 });
      if (goal) {
        rig.current.cheer = { side: away ? 'away' : 'home', until: now + 8000 };
        rig.current.netHit = { side, at: now };
        rig.current.shake = { from: now, until: now + 1200, amp: 0.6 };
      }
    }
  }, [calls, info, fi]);
  useEffect(() => {
    if (!cut) return;
    const t = window.setTimeout(() => setCut(undefined), Math.max(0, cut.until - performance.now()));
    return () => window.clearTimeout(t);
  }, [cut]);

  const lastSeq = useRef<number>();
  useEffect(() => {
    const latest = events.length ? events[events.length - 1].seq : 0;
    if (lastSeq.current === undefined) { lastSeq.current = latest; return; }
    const now = performance.now();
    const R = rig.current;
    for (let i = events.length - 1; i >= 0 && events[i].seq > lastSeq.current; i--) {
      const e = events[i];
      if (!e.playerId) continue;
      if (e.type === 'pass' || e.type === 'shot') R.actions.set(e.playerId, { kind: 'kick', at: now, dir: 0 });
      if (e.type === 'tackle' || e.type === 'interception') R.actions.set(e.playerId, { kind: 'tackle', at: now, dir: 0 });
      if (e.type === 'shot') {
        R.flashes.push({ at: now, n: 45, dur: 900 });
        R.trailHot = now + 2500;
        if (e.shotResult === 'saved') {
          const other = e.teamId === info.home.teamId ? info.away : info.home;
          const gk = other.players.find((p) => p.position === 'goalkeeper' && R.at.has(p.playerId)) ?? other.players.find((p) => p.position === 'goalkeeper');
          if (gk) R.actions.set(gk.playerId, { kind: 'dive', at: now + 120, dir: hashOf(e.id) % 2 ? 1 : -1 });
        }
      }
    }
    lastSeq.current = latest;
  }, [events, info]);

  // A goal in the score (also one that arrived before the call) makes that end jump and that net ripple.
  const goals = useRef({ h: tick.homeGoals, a: tick.awayGoals });
  useEffect(() => {
    const now = performance.now(), R = rig.current;
    const scored = (side: 'home' | 'away') => {
      R.cheer = { side, until: now + 8000 };
      if (!R.netHit || now - R.netHit.at > 5000) R.netHit = { side: side === 'home' ? 1 : -1, at: now };
    };
    if (tick.homeGoals > goals.current.h) scored('home');
    if (tick.awayGoals > goals.current.a) scored('away');
    goals.current = { h: tick.homeGoals, a: tick.awayGoals };
  }, [tick.homeGoals, tick.awayGoals]);

  // --- Tags: a fast shot or a long pass (from the event), a top speed or a distance (from tracking), the followed player.
  const tagRows = useMemo(() => {
    const out: { id: string; playerId: string; side: 'home' | 'away'; text: string; at?: { x: number; z: number }; kind: 'metric' | 'name' }[] = [];
    const now = tick.clockSec;
    for (let i = events.length - 1; i >= 0 && now - events[i].clockSec <= TAG_SEC; i--) {
      const e = events[i];
      const value = e.x !== undefined ? metricTag(e, fi) : undefined;
      const who = value ? playerTeam(info, e.playerId) : undefined;
      if (!value || !who) continue;
      out.push({ id: `m-${e.id}`, playerId: who.player.playerId, side: who.side, text: `${last(who.player.name)} · ${value}`, at: { x: wx(e.x!), z: wz(e.y!) }, kind: 'metric' });
      break;
    }
    const run = [...tags].reverse().find((t) => now - t.atSec <= TAG_SEC && now >= t.atSec - 5);
    const runner = run ? playerTeam(info, run.playerId) : undefined;
    if (run && runner) {
      out.push({ id: `r-${run.playerId}-${run.atSec}`, playerId: run.playerId, side: runner.side, kind: 'metric',
        text: `${last(runner.player.name)} · ${run.kind === 'speed' ? `${run.speedKmh.toFixed(1)} km/h` : `${run.distanceKm.toFixed(0)} km ${fi ? 'juostu' : 'run'}`}` });
    }
    const followed = focusPlayer ? playerTeam(info, focusPlayer) : undefined;
    if (followed && focusPlayer !== lock && !out.some((t) => t.playerId === focusPlayer))
      out.push({ id: `f-${focusPlayer}`, playerId: focusPlayer!, side: followed.side, text: last(followed.player.name), kind: 'name' });
    return out;
  }, [events, tags, tick.clockSec, info, fi, focusPlayer, lock]);
  const tagEls = useRef(new Map<string, HTMLElement | null>());
  r.tags = tagRows.map((t) => ({ id: t.playerId, el: tagEls.current.get(t.id) ?? null, at: t.at, lift: t.kind === 'name' ? 2.6 : 3.2 }));

  // Without tracking yet, the ball sits where the last event happened.
  r.fallbackBall = useMemo(() => {
    for (let i = events.length - 1; i >= 0; i--) {
      const e = events[i];
      if (e.x === undefined || e.y === undefined) continue;
      const ok = e.type === 'pass' && e.outcome === 'success' && e.endX !== undefined;
      return { x: wx(ok ? e.endX! : e.x), z: wz(ok ? e.endY! : e.y) };
    }
    return undefined;
  }, [events]);

  // --- DVR: its state is the rig's; the bar re-reads it a few times a second while it is not live.
  const [dvr, setDvr] = useState({ live: true, playing: false, offset: 0, span: 0 });
  const readDvr = useCallback(() => {
    const h = getHistory(), d = rig.current.dvr;
    const end = h.length ? h[h.length - 1].at : performance.now();
    setDvr({ live: d.live, playing: d.playing, offset: d.live ? 0 : (d.cursor - end) / 1000, span: h.length ? (end - h[0].at) / 1000 : 0 });
  }, []);
  useEffect(() => {
    const t = window.setInterval(readDvr, dvr.live ? 1000 : 150);
    return () => window.clearInterval(t);
  }, [dvr.live, readDvr]);
  const dvrDo = (what: 'pause' | 'play' | 'live' | 'back' | number) => {
    const d = rig.current.dvr, h = getHistory();
    if (!h.length) return;
    const end = h[h.length - 1].at;
    if (what === 'live') { d.live = true; d.playing = false; }
    else if (what === 'pause') { if (d.live) { d.live = false; d.cursor = end; } d.playing = false; }
    else if (what === 'play') { if (!d.live) d.playing = true; }
    else if (what === 'back') { d.live = false; d.cursor = h[0].at; d.playing = true; }
    else { d.live = false; d.playing = false; d.cursor = end + what * 1000; }
    readDvr();
  };

  // --- Keys: H clean feed, F full screen, 1–4 cameras (not while typing).
  const flashHint = (text: string) => { setHint(text); window.setTimeout(() => setHint((h) => (h === text ? undefined : h)), 1800); };
  const pickCam = useCallback((m: CamMode) => { setCam(m); save('ml-cam', m); }, []);
  // "Watch with the player cam" from the player cards elsewhere on the page.
  useEffect(() => {
    const on = (e: Event) => { const m = (e as CustomEvent<CamMode>).detail; if (CAM_MODES.includes(m)) pickCam(m); };
    window.addEventListener('s3d-cam', on);
    return () => window.removeEventListener('s3d-cam', on);
  }, [pickCam]);
  // The recording take for the broadcast graphics asks for the clean feed.
  useEffect(() => {
    const on = (e: Event) => setClean(!!(e as CustomEvent<boolean>).detail);
    window.addEventListener('s3d-clean', on);
    return () => window.removeEventListener('s3d-clean', on);
  }, []);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const t = e.target as HTMLElement | null;
      if (t && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName))) return;
      const k = e.key.toLowerCase();
      if (k === 'h') {
        setClean((c) => { flashHint(c ? (fi ? 'Ohjaimet näkyvissä' : 'Controls on') : (fi ? 'Puhdas kuva · H palauttaa' : 'Clean feed · press H to bring the controls back')); return !c; });
      } else if (k === 'f') {
        const stage = rootRef.current?.closest('.stage') as HTMLElement | null;
        if (document.fullscreenElement) void document.exitFullscreen();
        else void stage?.requestFullscreen?.();
      } else if (['1', '2', '3', '4', '5'].includes(k)) pickCam(CAM_MODES[+k - 1]);
      else return;
      e.preventDefault();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [fi, pickCam]);
  useEffect(() => {
    const stage = rootRef.current?.closest('.stage');
    stage?.classList.toggle('is-clean', clean);
    return () => stage?.classList.remove('is-clean');
  }, [clean]);

  const minute = Math.floor(tick.clockSec / 60) + 1;
  const bridge = useMemo(() => ({ board, onRewind: setRewind }), [board]);
  const latestCall = [...calls].reverse().find((c) => tick.clockSec - c.clockSec <= 120);
  const lockWho = lock ? playerTeam(info, lock) : undefined;
  const lockRun = lock ? running?.find((x) => x.playerId === lock) : undefined;
  const cheer = r.cheer && r.cheer.until > performance.now() ? r.cheer.side : undefined;
  const camLabel: Record<CamMode, string> = fi
    ? { auto: 'Ohjaaja', broadcast: 'Lähetys', tactical: 'Taktinen', drone: 'Drooni', player: 'Pelaajakamera' }
    : { auto: 'Auto', broadcast: 'Broadcast', tactical: 'Tactical', drone: 'Drone', player: 'Player cam' };
  const kitLabel: Record<KitStyle, string> = fi
    ? { club: 'Seurojen värit', contrast: 'Kontrasti', classic: 'Klassinen' }
    : { club: 'Club colours', contrast: 'High contrast', classic: 'Classic' };

  return (
    <div ref={rootRef} className={`s3d ${hud ? 's3d-has-hud' : ''} ${clean ? 's3d-clean' : ''}`}>
      <Canvas className="s3d-canvas" dpr={[1, 1.75]} gl={{ antialias: true, powerPreference: 'high-performance' }}
        camera={{ fov: 30, near: 0.5, far: 700, position: [0, 24, 66] }} frameloop="always"
        aria-label={fi ? 'Ottelu 3D:nä' : 'The match in 3D'}>
        <Scene info={info} rig={rig} kitStyle={kitStyle} teams={teams} onHulls={setHulls}
          onLock={(id) => setLock((cur) => (cur === id ? undefined : id))} />
      </Canvas>

      {/* Laid on the grass by the scene: the 2D board's markings (a replay's arrows, a heat map, a shape). */}
      <div ref={setBoard} className="s3d-board" aria-hidden={!hud} />

      {/* Hung on the far stand: the pinned card, or the score when nothing is pinned. */}
      <div ref={(el) => { rig.current.panel = el; }} className={`s3d-panel ${cut || hud ? 'is-hidden' : ''}`}>
        <div className="s3d-pin">{pinned}</div>
        <div className="s3d-score num">
          <span className="side-home">{info.home.shortName}</span>
          <b>{tick.homeGoals}–{tick.awayGoals}</b>
          <span className="side-away">{info.away.shortName}</span>
          <small>{minute}′</small>
        </div>
      </div>

      {/* The two big screens: the score and the clock; the commentator's latest call. */}
      <div ref={(el) => { rig.current.screens[0] = el; }} className={`s3d-screen ${cheer ? `is-goal side-${cheer}` : ''}`} aria-hidden="true">
        {cheer ? (
          <div className="scr-goal">{fi ? 'MAALI' : 'GOAL'}<small>{(cheer === 'home' ? info.home : info.away).name}</small></div>
        ) : (
          <>
            <div className="scr-row">
              <span className="side-home">{info.home.shortName}</span>
              <b className="num">{tick.homeGoals}–{tick.awayGoals}</b>
              <span className="side-away">{info.away.shortName}</span>
            </div>
            <div className="scr-clock num">{!dvr.live ? (fi ? 'UUSINTA' : 'REPLAY') : clockText(tick.clockSec)}</div>
          </>
        )}
      </div>
      <div ref={(el) => { rig.current.screens[1] = el; }} className="s3d-screen s3d-screen-call" aria-hidden="true">
        {latestCall ? (
          <><span className="scr-kicker num">{clockText(latestCall.clockSec)}</span><p>{fi ? latestCall.textFi : latestCall.text}</p></>
        ) : (
          <><span className="scr-kicker">Second Ball IQ</span><p>{fi ? 'Seuranta live: 22 pelaajaa, 10 kertaa sekunnissa' : 'Live tracking: 22 players, ten times a second'}</p></>
        )}
      </div>

      {tagRows.map((t) => (
        <div key={t.id} ref={(el) => { tagEls.current.set(t.id, el); }} className={`s3d-tag side-${t.side} s3d-tag-${t.kind} ${hud ? 'is-hidden' : ''}`}>
          {t.text}
        </div>
      ))}

      {/* The locked player: name and his live speed from tracking, the running so far. */}
      <div ref={(el) => { rig.current.lockEl = el; }} className={`s3d-lock ${lockWho && !hud ? '' : 'is-hidden'} ${lockWho ? `side-${lockWho.side}` : ''}`}>
        {lockWho && (
          <>
            <span className="lk-name"><b className="num">{lockWho.player.shirtNumber}</b> {lockWho.player.name}</span>
            <span className="lk-speed-row"><b className="lk-speed num">0.0</b><small>km/h</small></span>
            {lockRun && <span className="lk-run num">{lockRun.distanceKm.toFixed(1)} km · {fi ? 'huippu' : 'top'} {lockRun.topSpeedKmh.toFixed(1)} · {lockRun.sprints} {fi ? 'spurttia' : 'sprints'}</span>}
          </>
        )}
      </div>

      {hulls.map((h, i) => (
        <div key={i} ref={(el) => { rig.current.hullEls[i] = el; }} className={`s3d-hull side-${i ? 'away' : 'home'} ${h && !hud ? '' : 'is-hidden'}`}>
          {h && <>{(i ? info.away : info.home).shortName} · {h.text}</>}
        </div>
      ))}

      {cut && cam === 'auto' && !hud && <div className={`s3d-bug s3d-bug-${cut.kind}`} role="status">{cut.label}</div>}
      {rewind && <div className="s3d-bug s3d-bug-replay">{fi ? 'Uusinta' : 'Replay'}</div>}
      {!dvr.live && !hud && <div className="s3d-bug s3d-bug-dvr num">DVR {dvr.offset.toFixed(1)} s</div>}
      {hint && <div className="s3d-hint" role="status">{hint}</div>}

      {!hud && (
        <div className="s3d-cams" role="group" aria-label={fi ? 'Kamera' : 'Camera'}>
          {CAM_MODES.map((m, i) => (
            <button key={m} type="button" aria-pressed={cam === m} onClick={() => pickCam(m)} title={`${camLabel[m]} (${i + 1})`}>{camLabel[m]}</button>
          ))}
          <button type="button" className="s3d-kit" onClick={() => setKitStyle((k) => { const n = KIT_STYLES[(KIT_STYLES.indexOf(k) + 1) % KIT_STYLES.length]; save('ml-kits', n); return n; })}
            title={fi ? 'Pelipaidat' : 'Kits'}>{kitLabel[kitStyle]}</button>
          <span className="s3d-keys"><kbd>H</kbd> {fi ? 'puhdas' : 'clean'} <kbd>F</kbd> {fi ? 'koko näyttö' : 'full screen'}</span>
        </div>
      )}

      {!hud && hasFrames && (
        <div className="s3d-dvr" role="group" aria-label="DVR">
          <button type="button" onClick={() => dvrDo('back')} title={fi ? 'Takaisin 15 s' : 'Back 15 s'}>−15 s</button>
          <button type="button" onClick={() => dvrDo(dvr.live || !dvr.playing ? (dvr.live ? 'pause' : 'play') : 'pause')}
            aria-label={dvr.live || !dvr.playing ? (dvr.live ? 'Pause' : 'Play') : 'Pause'}>
            {dvr.live || dvr.playing ? '❚❚' : '▶'}
          </button>
          <input type="range" min={-Math.max(1, dvr.span)} max={0} step={0.1} value={dvr.offset}
            aria-label={fi ? 'Kelaa' : 'Scrub'} onChange={(e) => dvrDo(+e.target.value)} />
          <button type="button" className={`s3d-live ${dvr.live ? 'on' : ''}`} onClick={() => dvrDo('live')}>LIVE</button>
          <span className="s3d-sep" />
          {(['control', 'offside', 'shape', 'runs'] as (keyof Layers)[]).map((k) => (
            <button key={k} type="button" aria-pressed={layers[k]} onClick={() => setLayers((l) => ({ ...l, [k]: !l[k] }))}>
              {{ control: fi ? 'Tila' : 'Space', offside: fi ? 'Paitsio' : 'Offside', shape: fi ? 'Muoto' : 'Shape', runs: fi ? 'Juoksut' : 'Runs' }[k]}
            </button>
          ))}
          {lockWho
            ? <button type="button" className="s3d-lockchip" onClick={() => setLock(undefined)} title={fi ? 'Vapauta' : 'Unlock'}>◎ {last(lockWho.player.name)} ✕</button>
            : <span className="s3d-locktip">{fi ? 'Klikkaa pelaajaa' : 'Click a player to lock'}</span>}
        </div>
      )}

      {hud && (
        <Stage3DBridge.Provider value={bridge}>
          <div className="s3d-hud">{hud}</div>
        </Stage3DBridge.Provider>
      )}
    </div>
  );
}

const last = (name: string) => name.split(' ').slice(-1)[0];

// ---------------------------------------------------------------------------------------------------------------------
// The scene

function Scene({ info, rig, kitStyle, teams, onHulls, onLock }: { info: MatchInfo; rig: MutableRefObject<Rig>; kitStyle: KitStyle;
  teams: TeamSets; onHulls: (h: (HullLabel | undefined)[]) => void; onLock: (id: string) => void }) {
  const kits = useMemo(() => {
    const k = kitsFor(info, kitStyle);
    return [info.home, info.away].flatMap((team, t) => team.players.map((p): AvatarKit => {
      const side = t === 0 ? 'home' : 'away';
      const keeper = p.position === 'goalkeeper';
      const c = k[side];
      return { id: p.playerId, number: p.shirtNumber, keeper,
        shirt: keeper ? KEEPER[side] : c.shirt, shorts: keeper ? '#1b1f24' : c.shorts, socks: keeper ? KEEPER[side] : c.socks,
        ink: keeper ? '#10171c' : c.ink, skin: SKIN[hashOf(p.playerId) % SKIN.length] };
    }));
  }, [info, kitStyle]);
  const avatars = useRef(new Map<string, AvatarRig>());
  const ballRef = useRef<THREE.Group>(null);
  const shadowRef = useRef<THREE.Mesh>(null);
  const screenObjs = useRef<(THREE.Object3D | null)[]>([null, null]);

  return (
    <>
      <color attach="background" args={['#05080c']} />
      <fog attach="fog" args={['#05080c', 150, 340]} />
      <hemisphereLight args={['#dfe9ff', '#1a2a1a', 0.85]} />
      <directionalLight position={[-40, 80, 50]} intensity={1.6} />
      <directionalLight position={[50, 60, -40]} intensity={0.55} color="#cfe0ff" />

      {/* The director runs first each frame (positions, camera); the DOM placer last. */}
      <Director rig={rig} avatars={avatars.current} ball={ballRef} shadow={shadowRef} />

      <Grass />
      <Goals rig={rig} />
      <Ground info={info} rig={rig} />
      <LedBoards info={info} rig={rig} />
      <Floodlights rig={rig} />
      <BigScreens onScreen={(i, o) => { screenObjs.current[i] = o; }} />

      {kits.map((k) => <Player key={`${k.id}-${kitStyle}`} kit={k} reg={avatars.current} onLock={onLock} />)}
      <group ref={ballRef}>
        <mesh>
          <sphereGeometry args={[0.34, 20, 14]} />
          <meshStandardMaterial color="#ffffff" emissive="#ffffff" emissiveIntensity={0.35} roughness={0.5} />
        </mesh>
        <mesh>
          <sphereGeometry args={[0.65, 16, 10]} />
          <meshBasicMaterial color="#ffffff" transparent opacity={0.12} depthWrite={false} />
        </mesh>
      </group>
      <mesh ref={shadowRef} rotation={[-Math.PI / 2, 0, 0]}>
        <circleGeometry args={[0.32, 16]} />
        <meshBasicMaterial color="#000" transparent opacity={0.4} depthWrite={false} />
      </mesh>
      <BallTrail ball={ballRef} rig={rig} />
      <ReplayActor info={info} rig={rig} />
      <Telestrator rig={rig} teams={teams} onHulls={onHulls} />

      <DomPlacer rig={rig} screens={screenObjs} />
    </>
  );
}

/** One player: the articulated body in his kit, a number above the head, a shadow; click to lock on him. */
function Player({ kit, reg, onLock }: { kit: AvatarKit; reg: Map<string, AvatarRig>; onLock: (id: string) => void }) {
  const av = useMemo(() => buildAvatar(kit), [kit]);
  const number = useMemo(() => numberTexture(kit.number, kit.shirt, kit.ink), [kit]);
  useEffect(() => {
    av.root.visible = false;
    reg.set(kit.id, av);
    return () => { reg.delete(kit.id); };
  }, [av, kit.id, reg]);
  return (
    <primitive object={av.root}
      onClick={(e: { stopPropagation: () => void }) => { e.stopPropagation(); onLock(kit.id); }}
      onPointerOver={() => { document.body.style.cursor = 'pointer'; }}
      onPointerOut={() => { document.body.style.cursor = ''; }}>
      <sprite position={[0, 2.35, 0]} scale={[0.85, 0.85, 1]}>
        <spriteMaterial map={number} depthWrite={false} />
      </sprite>
      <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.02, 0]}>
        <circleGeometry args={[0.5, 18]} />
        <meshBasicMaterial color="#000" transparent opacity={0.35} depthWrite={false} />
      </mesh>
    </primitive>
  );
}

/**
 * The director: picks the frame to show (live, or from the DVR), measures speeds from it, moves and poses every
 * player and the ball (smoothed between frames), and runs the camera.
 */
function Director({ rig, avatars, ball, shadow }: { rig: MutableRefObject<Rig>; avatars: Map<string, AvatarRig>;
  ball: RefObject<THREE.Group>; shadow: RefObject<THREE.Mesh> }) {
  const { camera, size } = useThree();
  const st = useMemo(() => ({
    look: new THREE.Vector3(0, 0, -4), pos: new THREE.Vector3(0, 24, 66), follow: new THREE.Vector3(), camDir: new THREE.Vector3(1, 0, 0),
    lastCut: undefined as Cut | undefined, prev: undefined as TrackingView | undefined, ballKmh: 0, orbit: 0.6,
    target: new THREE.Vector3(), want: new THREE.Vector3(), shake: new THREE.Vector3(),
  }), []);
  const cam = camera as THREE.PerspectiveCamera;

  useFrame((_, dtRaw) => {
    const dt = Math.min(0.1, dtRaw);
    const r = rig.current;
    const now = performance.now();

    // --- The frame to show.
    let frame: TrackingView | undefined;
    if (r.dvr.live) frame = getFrame();
    else {
      const hist = getHistory();
      if (!hist.length) { r.dvr.live = true; frame = getFrame(); }
      else {
        if (r.dvr.playing) r.dvr.cursor += dtRaw * 1000;
        r.dvr.cursor = Math.max(r.dvr.cursor, hist[0].at);
        if (r.dvr.playing && r.dvr.cursor >= hist[hist.length - 1].at) { r.dvr.live = true; r.dvr.playing = false; frame = getFrame(); }
        else frame = frameAt(r.dvr.cursor);
      }
    }
    r.shown = frame;
    const board = r.mode === 'board';

    // --- Speeds, from successive frames in match time (so they are the players' real speeds at any replay speed).
    const prev = st.prev;
    if (frame && prev && frame.t !== prev.t) {
      const dtm = frame.t - prev.t;
      if (dtm > 0 && dtm < 4) {
        const a = 1 - Math.exp(-dtm * 1.6);
        const pm = new Map(prev.players.map((p) => [p.id, p]));
        for (const p of frame.players) {
          const q = pm.get(p.id);
          if (!q) continue;
          const vx = ((p.x - q.x) / 100) * L / dtm, vz = ((p.y - q.y) / 100) * W / dtm;
          const old = r.vel.get(p.id) ?? { vx, vz };
          const nv = { vx: old.vx + (vx - old.vx) * a, vz: old.vz + (vz - old.vz) * a };
          r.vel.set(p.id, nv);
          r.speeds.set(p.id, Math.hypot(nv.vx, nv.vz) * 3.6);
        }
        const bv = Math.hypot(((frame.ballX - prev.ballX) / 100) * L, ((frame.ballY - prev.ballY) / 100) * W) / dtm * 3.6;
        st.ballKmh += (bv - st.ballKmh) * a;
      }
    }
    if (frame) st.prev = frame;

    // --- The ball.
    const k = 1 - Math.exp(-dt * 9);
    const b = ball.current, s = shadow.current;
    let bx = st.follow.x, bz = st.follow.z;
    if (b && s) {
      const at = frame && !board ? { x: wx(frame.ballX), z: wz(frame.ballY), y: frame.ballZ } : !board && r.fallbackBall ? { ...r.fallbackBall, y: 0 } : undefined;
      b.visible = s.visible = !!at;
      if (at) {
        b.position.x += (at.x - b.position.x) * k;
        b.position.z += (at.z - b.position.z) * k;
        b.position.y = 0.34 + at.y;
        s.position.set(b.position.x, 0.02, b.position.z);
        st.follow.set(b.position.x, 0, b.position.z);
        bx = b.position.x; bz = b.position.z;
      }
    }

    // --- The players.
    const seen = new Set<string>();
    if (frame && !board) {
      const turn = 1 - Math.exp(-dt * 7);
      for (const p of frame.players) {
        const av = avatars.get(p.id);
        if (!av) continue;
        seen.add(p.id);
        const g = av.root;
        const tx = wx(p.x), tz = wz(p.y);
        if (!g.visible) { g.position.set(tx, 0, tz); g.visible = true; }
        const dx = (tx - g.position.x) * k, dz = (tz - g.position.z) * k;
        g.position.x += dx;
        g.position.z += dz;
        const moved = Math.hypot(dx, dz);
        // Face where he is running; standing, face the ball.
        const yaw = moved / dt > 0.9 ? Math.atan2(dx, dz) : Math.atan2(bx - g.position.x, bz - g.position.z);
        g.rotation.y = lerpAngle(g.rotation.y, yaw, turn);
        r.at.set(p.id, { x: g.position.x, z: g.position.z });
        let action = r.actions.get(p.id);
        if (action && now - action.at > 1600) { r.actions.delete(p.id); action = undefined; }
        av.pose({ now, dt, moved, kmh: r.speeds.get(p.id) ?? 0, action, ballDist: Math.hypot(bx - g.position.x, bz - g.position.z) });
      }
    }
    avatars.forEach((av, id) => { if (!seen.has(id)) { av.root.visible = false; r.at.delete(id); } });

    // --- The camera.
    const cut = r.cam === 'auto' && r.cut && r.cut.until > now ? r.cut : undefined;
    const aspect = size.width / Math.max(1, size.height);
    const fx = r.reduced ? 0 : THREE.MathUtils.clamp(st.follow.x, -34, 34);
    const fz = r.reduced ? 0 : THREE.MathUtils.clamp(st.follow.z, -20, 20);
    let fov: number, snap = false, rate = 2.2;
    if (board) {
      // High and wide, so the whole board on the grass is in the picture, above the controls along the bottom.
      fov = 34;
      const halfH = THREE.MathUtils.degToRad(fov / 2), halfW = Math.atan(Math.tan(halfH) * aspect);
      const back = Math.max(70, (L / 2 + 4) / Math.tan(halfW) * 0.98);
      st.want.set(0, back * 0.62, back * 0.86);
      st.target.set(0, 0, r.bar ? 9 : 3);
      snap = st.lastCut !== undefined;
      st.lastCut = undefined;
    } else if (cut) {
      // The reverse angle: low behind the goal, in front of the end stand, looking out at the box.
      fov = 42;
      st.want.set(cut.side * (L / 2 + 6.5), 6.2, cut.side * 3.5);
      st.target.set(cut.side * (L / 2 - 18), 0.4, 0);
      snap = st.lastCut !== cut;
      st.lastCut = cut;
    } else {
      snap = st.lastCut !== undefined;
      st.lastCut = undefined;
      if (r.cam === 'tactical') {
        fov = aspect < 1.3 ? 46 : 40;
        st.want.set(fx * 0.5, 66, 50);
        st.target.set(fx * 0.6, 0, 2);
      } else if (r.cam === 'player' && r.lock && r.at.get(r.lock)) {
        // Player cam: a chase camera behind the locked (or followed) player, looking past them towards the ball, so
        // the viewer sees what that player sees and where they run. Kept inside the ground.
        const p = r.at.get(r.lock)!;
        let dx = st.follow.x - p.x, dz = st.follow.z - p.z;
        const d = Math.hypot(dx, dz);
        if (d < 4) { dx = st.camDir.x; dz = st.camDir.z; } else { dx /= d; dz /= d; }
        st.camDir.set(st.camDir.x + (dx - st.camDir.x) * Math.min(1, dt * 1.2), 0, st.camDir.z + (dz - st.camDir.z) * Math.min(1, dt * 1.2)).normalize();
        // The player sits in the middle of the picture, above the controls along the bottom, with the play ahead.
        fov = 50;
        st.want.set(THREE.MathUtils.clamp(p.x - st.camDir.x * 13, -L / 2 - 6, L / 2 + 6), 10, THREE.MathUtils.clamp(p.z - st.camDir.z * 13, -W / 2 - 6, W / 2 + 6));
        st.target.set(p.x - st.camDir.x * 2, -2.5, p.z - st.camDir.z * 2);
        rate = 2.6;
      } else if (r.cam === 'drone') {
        if (!r.reduced) st.orbit += dt * 0.07;
        const R = 82, c = fx * 0.35;
        fov = 38;
        st.want.set(c + Math.sin(st.orbit) * R, 34, Math.cos(st.orbit) * R);
        st.target.set(c, 0, 0);
        rate = 3;
      } else {
        // Broadcast: the main camera on the halfway line, following the ball; it widens as the ball travels faster.
        const base = aspect < 1.3 ? 36 : 27;
        fov = base + THREE.MathUtils.clamp((st.ballKmh - 12) / 50, 0, 1) * 8;
        st.want.set(fx * 0.82, 23, 64);
        st.target.set(fx, 0, fz * 0.35 - 7);
      }
      if (r.reduced) snap = true;
    }
    const kc = snap ? 1 : 1 - Math.exp(-dt * rate);
    st.pos.lerp(st.want, kc);
    st.look.lerp(st.target, kc);
    // Goal impact: a short shake that dies away.
    st.shake.set(0, 0, 0);
    if (r.shake && now < r.shake.until && now >= r.shake.from && !r.reduced) {
      const u = (now - r.shake.from) / (r.shake.until - r.shake.from);
      const amp = r.shake.amp * (1 - u) * (1 - u);
      st.shake.set(Math.sin(now / 23) * amp, Math.sin(now / 31 + 1) * amp * 0.7, Math.sin(now / 27 + 2) * amp * 0.5);
    }
    cam.position.copy(st.pos).add(st.shake);
    if (Math.abs(cam.fov - fov) > 0.01) { cam.fov += (fov - cam.fov) * (snap ? 1 : 1 - Math.exp(-dt * 1.5)); cam.updateProjectionMatrix(); }
    cam.lookAt(st.look.x + st.shake.x * 0.5, st.look.y + st.shake.y * 0.5, st.look.z);
    cam.updateMatrixWorld();
  });
  return null;
}

/** Last each frame: puts the DOM pieces onto their 3D anchors with the camera as it is now. */
function DomPlacer({ rig, screens }: { rig: MutableRefObject<Rig>; screens: MutableRefObject<(THREE.Object3D | null)[]> }) {
  const { camera, size } = useThree();
  const q = useMemo(() => [new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3()], []);
  const v = useMemo(() => new THREE.Vector3(), []);
  useFrame(() => {
    const r = rig.current;
    const board = r.mode === 'board';
    const cam = camera;
    const hide = (el: HTMLElement | null) => { if (el) el.style.visibility = 'hidden'; };
    const at = (el: HTMLElement | null, x: number, y: number, z: number) => {
      if (!el) return;
      const p = project(v.set(x, y, z), cam, size);
      if (!p) { hide(el); return; }
      el.style.visibility = 'visible';
      el.style.transform = `translate(${p.x.toFixed(1)}px, ${p.y.toFixed(1)}px) translate(-50%, -100%)`;
    };

    if (r.board) placeQuad(r.board, [[-L / 2 - 3, 0.03, -W / 2 - 3], [L / 2 + 3, 0.03, -W / 2 - 3], [-L / 2 - 3, 0.03, W / 2 + 3], [L / 2 + 3, 0.03, W / 2 + 3]], cam, size, q);
    if (r.panel) {
      const w = r.panel.offsetWidth || 320, h = r.panel.offsetHeight || 120;
      const ww = 17, hh = ww * h / w;
      const cx = THREE.MathUtils.clamp(cam.position.x - 20, -28, 28), y0 = 1.4, z0 = -W / 2 - 4.2;
      placeQuad(r.panel, [[cx - ww / 2, y0 + hh, z0 - hh * 0.35], [cx + ww / 2, y0 + hh, z0 - hh * 0.35], [cx - ww / 2, y0, z0], [cx + ww / 2, y0, z0]], cam, size, q);
    }
    screens.current.forEach((o, i) => {
      const el = r.screens[i];
      if (!o || !el) return;
      const sc = SCREENS[i];
      const c = [[-sc.w / 2, sc.h / 2], [sc.w / 2, sc.h / 2], [-sc.w / 2, -sc.h / 2], [sc.w / 2, -sc.h / 2]]
        .map(([x, y]) => o.localToWorld(new THREE.Vector3(x, y, 0.02)).toArray() as [number, number, number]);
      // Seen from behind (the reverse angle, the drone), the screen is hidden.
      const normal = new THREE.Vector3(0, 0, 1).applyQuaternion(o.getWorldQuaternion(new THREE.Quaternion()));
      const toCam = cam.position.clone().sub(o.getWorldPosition(new THREE.Vector3()));
      if (normal.dot(toCam) <= 0) { hide(el); return; }
      placeQuad(el, c, cam, size, q);
    });
    for (const t of r.tags) {
      if (!t.el) continue;
      const p = r.at.get(t.id) ?? t.at;
      if (!p || board) { hide(t.el); continue; }
      at(t.el, p.x, t.lift, p.z);
    }
    const lp = r.lock ? r.at.get(r.lock) : undefined;
    if (r.lockEl) {
      if (!lp || board) hide(r.lockEl);
      else {
        at(r.lockEl, lp.x, 3.3, lp.z);
        const sp = r.lockEl.querySelector('.lk-speed');
        const kmh = (r.speeds.get(r.lock!) ?? 0).toFixed(1);
        if (sp && sp.textContent !== kmh) sp.textContent = kmh;
      }
    }
    r.hullEls.forEach((el, i) => {
      const h = r.hullLabels?.[i];
      if (!h || board || !r.layers.shape) hide(el);
      else at(el, h.x, 0.6, h.z);
    });
  });
  return null;
}

/** A replay in 3D: the player on the ball at the start of the move, and the ball travelling along it, over and over. */
function ReplayActor({ info, rig }: { info: MatchInfo; rig: MutableRefObject<Rig> }) {
  const actor = useRef<THREE.Group>(null);
  const ballRef = useRef<THREE.Mesh>(null);
  const kits = useMemo(() => ({ home: info.home.primaryColor, away: info.away.primaryColor }), [info]);
  const shirt = useRef<THREE.MeshLambertMaterial>(null);
  useFrame(() => {
    const r = rig.current.rewind;
    const a = actor.current, b = ballRef.current;
    if (!a || !b) return;
    const e = r?.events[Math.min(r.step, r.events.length - 1)];
    if (!r || !e || e.x === undefined || e.y === undefined) { a.visible = b.visible = false; return; }
    const sx = wx(e.x), sz = wz(e.y);
    let ex = sx, ez = sz, lofted = false;
    if (e.type === 'pass' && e.endX !== undefined && e.endY !== undefined) { ex = wx(e.endX); ez = wz(e.endY); lofted = (e.passMetres ?? 0) >= 30; }
    else if (e.type === 'shot') {
      const home = e.teamId === info.home.teamId;
      ex = e.endX !== undefined ? wx(e.endX) : (home ? L / 2 : -L / 2);
      ez = e.endY !== undefined ? wz(e.endY) : 0;
    }
    const dist = Math.hypot(ex - sx, ez - sz);
    const flight = Math.max(0.5, Math.min(2.2, dist / Math.max(8, ((e.ballSpeedKmh ?? 60) / 3.6))));
    const t = ((performance.now() - r.since) / 1000) % (flight + 0.9);
    const u = Math.min(1, Math.max(0, (t - 0.3) / flight));
    a.visible = b.visible = true;
    a.position.set(sx - 0.6, 0, sz + 0.4);
    a.rotation.y = Math.atan2(ex - sx, ez - sz);
    if (shirt.current) shirt.current.color.set(e.teamId === info.away.teamId ? kits.away : kits.home);
    b.position.set(sx + (ex - sx) * u, 0.24 + (lofted ? Math.sin(u * Math.PI) * dist * 0.12 : 0), sz + (ez - sz) * u);
  });
  return (
    <>
      <group ref={actor} visible={false}>
        <mesh position={[0, 0.42, 0]}><cylinderGeometry args={[0.2, 0.15, 0.84, 8]} /><meshLambertMaterial color="#14181d" /></mesh>
        <mesh position={[0, 1.2, 0]}><cylinderGeometry args={[0.26, 0.3, 0.8, 10]} /><meshLambertMaterial ref={shirt} color="#ffffff" /></mesh>
        <mesh position={[0, 1.82, 0]}><sphereGeometry args={[0.16, 12, 10]} /><meshLambertMaterial color={SKIN[1]} /></mesh>
        <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.03, 0]}>
          <ringGeometry args={[0.8, 1.05, 28]} />
          <meshBasicMaterial color="#f5c518" transparent opacity={0.9} depthWrite={false} />
        </mesh>
      </group>
      <mesh ref={ballRef} visible={false}>
        <sphereGeometry args={[0.3, 18, 12]} />
        <meshStandardMaterial color="#ffffff" emissive="#f5c518" emissiveIntensity={0.45} />
      </mesh>
    </>
  );
}

function numberTexture(n: number, shirt: string, ink: string) {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d')!;
  g.fillStyle = shirt;
  g.beginPath(); g.arc(32, 32, 28, 0, Math.PI * 2); g.fill();
  g.lineWidth = 4; g.strokeStyle = 'rgba(255,255,255,0.85)'; g.stroke();
  g.fillStyle = ink;
  g.font = '800 34px "Big Shoulders Display", "Barlow Condensed", sans-serif';
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(String(n), 32, 35);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
