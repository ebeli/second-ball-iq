import { useMemo } from 'react';
import * as THREE from 'three';
import type { Action } from './rig';

/**
 * A player you can see run: pelvis, torso, head, two arms (shoulder and elbow) and two legs (hip and knee), in the
 * club's kit with the number on the back and above the head. The scene moves the player; `pose` makes the body do
 * what the movement and the events say:
 *
 * - a gait whose stride follows the distance actually covered on screen, its size and the forward lean from the
 *   measured running speed (a jog is upright, a sprint leans in);
 * - a kick on the player's pass or shot, a lunge on a tackle or interception;
 * - a keeper's set stance when the ball is near his goal, and a dive on a saved shot.
 */

export interface AvatarKit { id: string; number: number; shirt: string; shorts: string; socks: string; ink: string; skin: string; keeper: boolean }

export interface PoseInput {
  now: number;
  dt: number;
  /** Metres moved on screen this frame (drives the stride, so feet keep up at any replay speed). */
  moved: number;
  /** Measured running speed, km/h (drives the stride's size and the lean). */
  kmh: number;
  action?: Action;
  /** For a keeper: how far the ball is from him, metres. */
  ballDist: number;
}

export interface AvatarRig {
  root: THREE.Group;
  pose: (p: PoseInput) => void;
}

const smooth = (cur: number, target: number, k: number) => cur + (target - cur) * k;

export function useAvatar(kit: AvatarKit): AvatarRig {
  return useMemo(() => buildAvatar(kit), [kit]);
}

/** The articulated body, built once per player, plus its pose function. */
export function buildAvatar(kit: AvatarKit): AvatarRig {
  const mat = (c: string) => new THREE.MeshLambertMaterial({ color: c });
  const shirt = mat(kit.shirt), shorts = mat(kit.shorts), socks = mat(kit.socks), skin = mat(kit.skin), boot = mat('#0d0f12');
  const hair = mat(['#2b1d14', '#1b1b1b', '#5a3b22', '#3b2a1c'][kit.number % 4]);
  const cyl = (rt: number, rb: number, h: number, m: THREE.Material, seg = 10) => {
    const mesh = new THREE.Mesh(new THREE.CylinderGeometry(rt, rb, h, seg), m);
    mesh.position.y = -h / 2;
    return mesh;
  };

  const root = new THREE.Group();
  const body = new THREE.Group();          // whole body: bob, crouch, dive roll
  root.add(body);
  const pelvis = new THREE.Group();
  pelvis.position.y = 0.95;
  body.add(pelvis);

  // Shorts round the hips.
  const hips = new THREE.Mesh(new THREE.CylinderGeometry(0.19, 0.2, 0.24, 12), shorts);
  hips.position.y = -0.02;
  pelvis.add(hips);

  // Torso, pivoting at the pelvis (the lean).
  const torso = new THREE.Group();
  pelvis.add(torso);
  const chest = new THREE.Mesh(new THREE.CylinderGeometry(0.25, 0.19, 0.58, 12), shirt);
  chest.position.y = 0.31;
  chest.scale.z = 0.72;
  torso.add(chest);
  const neck = new THREE.Mesh(new THREE.CylinderGeometry(0.055, 0.06, 0.1, 8), skin);
  neck.position.y = 0.64;
  torso.add(neck);
  const head = new THREE.Mesh(new THREE.SphereGeometry(0.115, 14, 12), skin);
  head.position.y = 0.78;
  head.scale.set(0.92, 1.05, 1);
  torso.add(head);
  const cap = new THREE.Mesh(new THREE.SphereGeometry(0.12, 12, 8, 0, Math.PI * 2, 0, Math.PI * 0.5), hair);
  cap.position.y = 0.795;
  cap.rotation.x = -0.25;
  torso.add(cap);

  // The number on the back.
  const back = new THREE.Mesh(new THREE.PlaneGeometry(0.3, 0.3), new THREE.MeshBasicMaterial({ map: digitTexture(kit.number, kit.ink), transparent: true, depthWrite: false }));
  back.position.set(0, 0.36, -0.145);
  back.rotation.y = Math.PI;
  torso.add(back);

  // Arms: shoulder → upper arm (sleeve) → elbow → forearm.
  const arm = (side: number) => {
    const shoulder = new THREE.Group();
    shoulder.position.set(side * 0.27, 0.54, 0);
    torso.add(shoulder);
    const sleeve = cyl(0.06, 0.05, 0.16, shirt);
    shoulder.add(sleeve);
    shoulder.add(cyl(0.045, 0.042, 0.28, skin));
    const elbow = new THREE.Group();
    elbow.position.y = -0.28;
    shoulder.add(elbow);
    elbow.add(cyl(0.04, 0.034, 0.26, kit.keeper ? shirt : skin));
    const hand = new THREE.Mesh(new THREE.SphereGeometry(kit.keeper ? 0.06 : 0.04, 8, 6), kit.keeper ? mat('#f2f2f2') : skin);
    hand.position.y = -0.28;
    elbow.add(hand);
    return { shoulder, elbow };
  };
  const armL = arm(1), armR = arm(-1);

  // Legs: hip → thigh → knee → shin (sock) → boot.
  const leg = (side: number) => {
    const hip = new THREE.Group();
    hip.position.set(side * 0.1, -0.06, 0);
    pelvis.add(hip);
    hip.add(cyl(0.085, 0.07, 0.2, shorts));
    hip.add(cyl(0.075, 0.06, 0.44, skin));
    const knee = new THREE.Group();
    knee.position.y = -0.44;
    hip.add(knee);
    knee.add(cyl(0.058, 0.045, 0.42, socks));
    const foot = new THREE.Mesh(new THREE.BoxGeometry(0.1, 0.07, 0.25), boot);
    foot.position.set(0, -0.44, 0.05);
    knee.add(foot);
    return { hip, knee };
  };
  const legL = leg(1), legR = leg(-1);

  // Pose state, eased between frames.
  const s = { phase: Math.random() * Math.PI * 2, amp: 0, lean: 0, set: 0 };

  const pose = (p: PoseInput) => {
    const k = 1 - Math.exp(-p.dt * 10);
    const v = p.kmh / 3.6;
    // Stride: about 1.2 m at a jog, 2 m flat out; the phase follows the distance covered on screen.
    const stride = 1.1 + Math.min(v, 9) * 0.11;
    s.phase += (p.moved / stride) * Math.PI * 2;
    s.amp = smooth(s.amp, Math.min(1, v / 7.5), k);
    s.lean = smooth(s.lean, 0.06 + 0.3 * THREE.MathUtils.clamp((v - 3.5) / 5, 0, 1), k);
    // A keeper sets himself when the ball is near and he is not running.
    s.set = smooth(s.set, kit.keeper && p.ballDist < 28 && v < 2.5 ? 1 : 0, k * 0.6);

    const a = s.amp, ph = s.phase;
    const swing = Math.sin(ph) * 0.8 * a;
    let hipL = -swing, hipR = swing;
    let kneeL = Math.max(0, -Math.sin(ph + 0.9)) * 1.25 * a + 0.04, kneeR = Math.max(0, Math.sin(ph + 0.9)) * 1.25 * a + 0.04;
    let shL = swing * 0.85, shR = -swing * 0.85, shZL = 0.08, shZR = -0.08;
    let elL = -(0.25 + 1.0 * a), elR = elL;
    let lean = s.lean, bodyY = Math.abs(Math.sin(ph)) * 0.07 * a, roll = 0;

    // The keeper's stance: knees bent, weight forward, hands out.
    if (s.set > 0.01) {
      const w = s.set;
      hipL = THREE.MathUtils.lerp(hipL, -0.55, w); hipR = THREE.MathUtils.lerp(hipR, -0.55, w);
      kneeL = THREE.MathUtils.lerp(kneeL, 0.95, w); kneeR = THREE.MathUtils.lerp(kneeR, 0.95, w);
      lean = THREE.MathUtils.lerp(lean, 0.42, w);
      bodyY = THREE.MathUtils.lerp(bodyY, -0.17, w);
      shL = THREE.MathUtils.lerp(shL, -0.7, w); shR = THREE.MathUtils.lerp(shR, -0.7, w);
      shZL = THREE.MathUtils.lerp(shZL, 0.55, w); shZR = THREE.MathUtils.lerp(shZR, -0.55, w);
      elL = elR = THREE.MathUtils.lerp(elL, -0.5, w);
    }

    // Actions override the gait while they last.
    const act = p.action;
    if (act) {
      const t = (p.now - act.at) / 1000;
      if (act.kind === 'kick' && t >= 0 && t < 0.75) {
        // Plant, swing the right leg back with the knee bent, strike through, follow through.
        const back = t < 0.28 ? t / 0.28 : 0;
        const through = t >= 0.28 ? Math.min(1, (t - 0.28) / 0.17) : 0;
        const settle = t > 0.5 ? (t - 0.5) / 0.25 : 0;
        hipR = THREE.MathUtils.lerp(THREE.MathUtils.lerp(0.75 * back, -1.25, through), 0, settle);
        kneeR = THREE.MathUtils.lerp(1.3 * back + 0.1, 0.1, through) * (1 - settle);
        hipL = -0.15; kneeL = 0.25;
        shZL = 0.75 * (1 - settle); shZR = -0.75 * (1 - settle); shL = 0.3; shR = -0.4;
        lean = THREE.MathUtils.lerp(-0.12, lean, settle);
      } else if (act.kind === 'tackle' && t >= 0 && t < 0.8) {
        // A lunge: low, the lead leg stretched out to the ball.
        const w = Math.sin(Math.min(1, t / 0.8) * Math.PI);
        hipR = THREE.MathUtils.lerp(hipR, -1.25, w); kneeR = THREE.MathUtils.lerp(kneeR, 0.1, w);
        hipL = THREE.MathUtils.lerp(hipL, 0.5, w); kneeL = THREE.MathUtils.lerp(kneeL, 1.3, w);
        bodyY -= 0.38 * w; lean = THREE.MathUtils.lerp(lean, 0.1, w);
        shZL = 0.6 * w; shZR = -0.6 * w;
      } else if (act.kind === 'dive' && t >= 0 && t < 1.4) {
        // Launch sideways, arms stretched past the head, land, get up.
        const out = Math.min(1, t / 0.35), up = t > 0.9 ? (t - 0.9) / 0.5 : 0;
        const w = out * (1 - up);
        roll = act.dir * 1.35 * w;
        bodyY = 0.35 * Math.sin(Math.min(1, t / 0.6) * Math.PI) * (1 - up) - 0.25 * w;
        shZL = THREE.MathUtils.lerp(shZL, 2.7, w); shZR = THREE.MathUtils.lerp(shZR, -2.7, w);
        elL = elR = -0.1;
        hipL = -0.2 * w; hipR = 0.2 * w; kneeL = kneeR = 0.3 * w;
        lean = THREE.MathUtils.lerp(lean, 0, w);
      }
    }

    legL.hip.rotation.x = hipL; legR.hip.rotation.x = hipR;
    legL.knee.rotation.x = kneeL; legR.knee.rotation.x = kneeR;
    armL.shoulder.rotation.x = shL; armR.shoulder.rotation.x = shR;
    armL.shoulder.rotation.z = shZL; armR.shoulder.rotation.z = shZR;
    armL.elbow.rotation.x = elL; armR.elbow.rotation.x = elR;
    torso.rotation.x = lean;
    body.position.y = bodyY;
    body.rotation.z = roll;
  };

  return { root, pose };
}

function digitTexture(n: number, ink: string) {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d')!;
  g.fillStyle = ink;
  g.font = '800 46px "Big Shoulders Display", "Barlow Condensed", sans-serif';
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(String(n), 32, 36);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}
