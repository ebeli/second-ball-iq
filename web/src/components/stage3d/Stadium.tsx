import { useEffect, useMemo, useRef, type MutableRefObject, type RefObject } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import { kitOf } from '../../kit';
import type { MatchInfo } from '../../types';
import { hashOf, L, SKIN, W } from './math';
import type { Rig } from './rig';

/**
 * The ground, all procedural: the pitch with its markings, goals with nets, the stands full of supporters, the roof,
 * floodlight masts with their beams, the LED boards, two big screens in the far corners, camera flashes in the
 * crowd, and the ball's trail.
 */

// ---------------------------------------------------------------------------------------------------------------------
// Pitch

/** Grass with mowing stripes and the markings, drawn once onto a texture. */
export function Grass() {
  const tex = useMemo(() => {
    const px = 16, M = 6, cw = (L + 2 * M) * px, ch = (W + 2 * M) * px;
    const c = document.createElement('canvas');
    c.width = cw; c.height = ch;
    const g = c.getContext('2d')!;
    g.fillStyle = '#1e5a2c';
    g.fillRect(0, 0, cw, ch);
    for (let i = 0; i < 20; i++) {
      g.fillStyle = i % 2 ? '#1f5f2e' : '#23692f';
      g.fillRect((M + (i * L) / 20) * px, 0, (L / 20) * px + 1, ch);
    }
    // A little wear in the goalmouths and the centre circle.
    const wear = (x: number, y: number, r: number) => {
      const grd = g.createRadialGradient((x + M) * px, (y + M) * px, 0, (x + M) * px, (y + M) * px, r * px);
      grd.addColorStop(0, 'rgba(90, 80, 40, 0.18)'); grd.addColorStop(1, 'rgba(90, 80, 40, 0)');
      g.fillStyle = grd; g.fillRect((x - r + M) * px, (y - r + M) * px, 2 * r * px, 2 * r * px);
    };
    wear(3, W / 2, 6); wear(L - 3, W / 2, 6); wear(L / 2, W / 2, 5);
    g.strokeStyle = 'rgba(240, 246, 240, 0.92)';
    g.lineWidth = 0.12 * px;
    const X = (x: number) => (x + M) * px, Y = (y: number) => (y + M) * px;
    g.strokeRect(X(0), Y(0), L * px, W * px);
    g.beginPath(); g.moveTo(X(L / 2), Y(0)); g.lineTo(X(L / 2), Y(W)); g.stroke();
    g.beginPath(); g.arc(X(L / 2), Y(W / 2), 9.15 * px, 0, Math.PI * 2); g.stroke();
    for (const side of [0, 1]) {
      const gx = side ? L : 0, dir = side ? -1 : 1;
      g.strokeRect(X(side ? L - 16.5 : 0), Y(W / 2 - 20.16), 16.5 * px, 40.32 * px);
      g.strokeRect(X(side ? L - 5.5 : 0), Y(W / 2 - 9.16), 5.5 * px, 18.32 * px);
      g.beginPath();
      const a = Math.acos(5.5 / 9.15);
      g.arc(X(gx + dir * 11), Y(W / 2), 9.15 * px, side ? Math.PI - a : -a, side ? Math.PI + a : a);
      g.stroke();
      g.fillStyle = 'rgba(240, 246, 240, 0.92)';
      g.beginPath(); g.arc(X(gx + dir * 11), Y(W / 2), 0.2 * px, 0, Math.PI * 2); g.fill();
      // Corner arcs.
      for (const cy of [0, W]) {
        g.beginPath();
        g.arc(X(gx), Y(cy), 1 * px, cy === 0 ? (side ? Math.PI / 2 : 0) : (side ? Math.PI : -Math.PI / 2), cy === 0 ? (side ? Math.PI : Math.PI / 2) : (side ? Math.PI * 1.5 : 0));
        g.stroke();
      }
    }
    g.beginPath(); g.arc(X(L / 2), Y(W / 2), 0.2 * px, 0, Math.PI * 2); g.fill();
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    t.anisotropy = 8;
    return { t, w: L + 2 * M, h: W + 2 * M };
  }, []);
  return (
    <>
      <mesh rotation={[-Math.PI / 2, 0, 0]}>
        <planeGeometry args={[tex.w, tex.h]} />
        <meshLambertMaterial map={tex.t} />
      </mesh>
      <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, -0.05, 0]}>
        <planeGeometry args={[400, 400]} />
        <meshLambertMaterial color="#0b1410" />
      </mesh>
      {/* Corner flags. */}
      {[-1, 1].map((sx) => [-1, 1].map((sz) => (
        <group key={`cf${sx}${sz}`} position={[sx * L / 2, 0, sz * W / 2]}>
          <mesh position={[0, 0.75, 0]}><cylinderGeometry args={[0.025, 0.025, 1.5, 6]} /><meshLambertMaterial color="#f4f7fa" /></mesh>
          <mesh position={[sx * 0.22, 1.35, 0]}><boxGeometry args={[0.42, 0.3, 0.01]} /><meshBasicMaterial color="#f5a524" /></mesh>
        </group>
      )))}
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Goals

const NET_CELL = 0.13;
const POST_R = 0.06, GOAL_W = 7.32, GOAL_H = 2.44;
const TOP_D = 0.9, BASE_D = 2.1;

function netTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d')!;
  g.strokeStyle = 'rgba(255, 255, 255, 1)';
  g.lineWidth = 5;
  // A diamond mesh reads as a real net from a distance better than a square grid.
  g.beginPath();
  g.moveTo(0, 32); g.lineTo(32, 0); g.lineTo(64, 32); g.lineTo(32, 64); g.closePath();
  g.stroke();
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.anisotropy = 8;
  return t;
}

/** A flat net panel through four corners (in the goal's own frame: x out of the pitch, y up, z across), UVs in metres. */
function netQuad(p: [number, number, number][], segU = 1, segV = 1) {
  const g = new THREE.BufferGeometry();
  const pos: number[] = [], uv: number[] = [], idx: number[] = [];
  const lenU = new THREE.Vector3(...p[1]).distanceTo(new THREE.Vector3(...p[0]));
  const lenV = new THREE.Vector3(...p[3]).distanceTo(new THREE.Vector3(...p[0]));
  for (let j = 0; j <= segV; j++) {
    for (let i = 0; i <= segU; i++) {
      const u = i / segU, v = j / segV;
      // Bilinear between the four corners: 0 → 1 along u, 0 → 3 along v.
      const a = p[0].map((c, k) => c + (p[1][k] - c) * u);
      const b = p[3].map((c, k) => c + (p[2][k] - c) * u);
      pos.push(...a.map((c, k) => c + (b[k] - c) * v));
      uv.push((u * lenU) / NET_CELL, (v * lenV) / NET_CELL);
    }
  }
  for (let j = 0; j < segV; j++) for (let i = 0; i < segU; i++) {
    const a = j * (segU + 1) + i, b = a + 1, c = a + segU + 1, d = c + 1;
    idx.push(a, c, b, b, c, d);
  }
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setIndex(idx);
  g.computeVertexNormals();
  return g;
}

/**
 * Goals: round posts and crossbar, the back frame and stanchions, and a diamond net on the roof, back and sides. A
 * goal ripples the back net of that goal.
 */
export function Goals({ rig }: { rig: MutableRefObject<Rig> }) {
  const net = useMemo(netTexture, []);
  const parts = useMemo(() => {
    const hw = GOAL_W / 2 + POST_R;
    return {
      roof: netQuad([[0, GOAL_H, -hw], [0, GOAL_H, hw], [TOP_D, GOAL_H - 0.08, hw], [TOP_D, GOAL_H - 0.08, -hw]]),
      back: netQuad([[TOP_D, GOAL_H - 0.08, -hw], [TOP_D, GOAL_H - 0.08, hw], [BASE_D, 0, hw], [BASE_D, 0, -hw]], 24, 8),
      sides: [-1, 1].map((s) => netQuad([[0, 0, s * hw], [0, GOAL_H, s * hw], [TOP_D, GOAL_H - 0.08, s * hw], [BASE_D, 0, s * hw]])),
    };
  }, []);
  const backs = useRef<(THREE.Mesh | null)[]>([]);
  const rest = useMemo(() => Float32Array.from(parts.back.attributes.position.array as Float32Array), [parts]);

  useFrame(() => {
    const r = rig.current;
    const now = performance.now();
    backs.current.forEach((m, i) => {
      if (!m) return;
      const side = i === 0 ? -1 : 1;
      const pos = m.geometry.attributes.position as THREE.BufferAttribute;
      const hit = r.netHit && r.netHit.side === side && !r.reduced ? (now - r.netHit.at) / 1000 : -1;
      const arr = pos.array as Float32Array;
      if (hit < 0 || hit > 2.2) {
        if (arr[0] !== rest[0] || arr[3 * 13] !== rest[3 * 13]) { arr.set(rest); pos.needsUpdate = true; }
        return;
      }
      // A bulge where the ball hit (the middle of the net), rippling out and settling.
      for (let k = 0; k < arr.length; k += 3) {
        const z = rest[k + 2], y = rest[k + 1];
        const d = Math.hypot(z / 3.7, (y - 0.9) / 1.4);
        const wave = Math.exp(-hit * 2.2) * Math.max(0, 1 - d) * 0.55 * Math.cos(hit * 9 - d * 4);
        arr[k] = rest[k] + wave;
      }
      pos.needsUpdate = true;
    });
  });

  const white = <meshStandardMaterial color="#f7f9fb" roughness={0.35} metalness={0.1} />;
  const frame = <meshLambertMaterial color="#c9d1d8" />;
  const netMat = <meshBasicMaterial map={net} color="#eef3f6" transparent opacity={0.75} alphaTest={0.25} side={THREE.DoubleSide} depthWrite={false} />;
  const hw = GOAL_W / 2 + POST_R;
  return (
    <>
      {[-1, 1].map((s, gi) => (
        <group key={s} position={[s * (L / 2), 0, 0]} scale={[s, 1, 1]}>
          {/* Posts and crossbar, on the goal line. */}
          {[-hw, hw].map((z) => (
            <mesh key={z} position={[POST_R, GOAL_H / 2, z]}><cylinderGeometry args={[POST_R, POST_R, GOAL_H + POST_R, 14]} />{white}</mesh>
          ))}
          <mesh position={[POST_R, GOAL_H, 0]} rotation={[Math.PI / 2, 0, 0]}><cylinderGeometry args={[POST_R, POST_R, 2 * hw + 2 * POST_R, 14]} />{white}</mesh>
          {/* The back frame: top bar, ground bar, and the stanchions down each side. */}
          <mesh position={[TOP_D, GOAL_H - 0.08, 0]} rotation={[Math.PI / 2, 0, 0]}><cylinderGeometry args={[0.025, 0.025, 2 * hw, 8]} />{frame}</mesh>
          <mesh position={[BASE_D, 0.03, 0]} rotation={[Math.PI / 2, 0, 0]}><cylinderGeometry args={[0.03, 0.03, 2 * hw, 8]} />{frame}</mesh>
          {[-hw, hw].map((z) => (
            <group key={`st${z}`}>
              <Bar from={[POST_R, GOAL_H, z]} to={[TOP_D, GOAL_H - 0.08, z]} r={0.025} />
              <Bar from={[TOP_D, GOAL_H - 0.08, z]} to={[BASE_D, 0.03, z]} r={0.025} />
              <Bar from={[POST_R, 0.03, z]} to={[BASE_D, 0.03, z]} r={0.025} />
            </group>
          ))}
          {/* The net. */}
          <mesh geometry={parts.roof}>{netMat}</mesh>
          <mesh geometry={parts.back} ref={(m) => { backs.current[gi] = m; }}>{netMat}</mesh>
          {parts.sides.map((g, i) => <mesh key={i} geometry={g}>{netMat}</mesh>)}
          {/* The goal's shadow on the grass. */}
          <mesh rotation={[-Math.PI / 2, 0, 0]} position={[BASE_D / 2, 0.015, 0.4]}>
            <planeGeometry args={[BASE_D + 0.6, GOAL_W + 0.6]} />
            <meshBasicMaterial color="#000" transparent opacity={0.22} depthWrite={false} />
          </mesh>
        </group>
      ))}
    </>
  );
}

function Bar({ from, to, r }: { from: [number, number, number]; to: [number, number, number]; r: number }) {
  const { pos, quat, len } = useMemo(() => {
    const a = new THREE.Vector3(...from), b = new THREE.Vector3(...to);
    const d = b.clone().sub(a);
    return { pos: a.clone().add(b).multiplyScalar(0.5), len: d.length(), quat: new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), d.normalize()) };
  }, [from, to]);
  return (
    <mesh position={pos} quaternion={quat}>
      <cylinderGeometry args={[r, r, len, 6]} />
      <meshLambertMaterial color="#c9d1d8" />
    </mesh>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// LED boards

function ledTexture(bits: { text: string; color: string }[], w = 2048) {
  const c = document.createElement('canvas');
  c.width = w; c.height = 64;
  const g = c.getContext('2d')!;
  g.fillStyle = '#07090c'; g.fillRect(0, 0, c.width, c.height);
  g.font = '700 40px "Big Shoulders Display", "Barlow Condensed", sans-serif';
  g.textBaseline = 'middle';
  let x = 20;
  for (let i = 0; x < c.width; i++) {
    const b = bits[i % bits.length];
    g.fillStyle = b.color;
    g.fillText(b.text, x, 34);
    x += g.measureText(b.text).width + 60;
  }
  // LED pixels: a faint dark grid over the text.
  g.fillStyle = 'rgba(0, 0, 0, 0.35)';
  for (let y = 0; y < 64; y += 4) g.fillRect(0, y, c.width, 1);
  for (let xx = 0; xx < c.width; xx += 4) g.fillRect(xx, 0, 1, 64);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.wrapS = THREE.RepeatWrapping;
  return t;
}

/**
 * The LED boards: along the far touchline and behind each goal, scrolling the fixture and the brand; on a goal they
 * all switch to the scoring club's GOAL and flash.
 */
export function LedBoards({ info, rig }: { info: MatchInfo; rig: MutableRefObject<Rig> }) {
  const tex = useMemo(() => {
    const home = kitOf(info.home).glow, away = kitOf(info.away).glow;
    const normal = ledTexture([{ text: info.home.name.toUpperCase(), color: home }, { text: 'SECOND BALL IQ', color: '#f5a524' },
      { text: info.away.name.toUpperCase(), color: away }, { text: 'INSIDE THE GAME', color: '#f5a524' }]);
    const goal = (team: string, color: string) => ledTexture([{ text: 'GOAL', color: '#ffffff' }, { text: team.toUpperCase(), color }]);
    return { normal, home: goal(info.home.name, home), away: goal(info.away.name, away) };
  }, [info]);
  // Each board has its own copies of the textures (sharing the canvases), so each can repeat to its own length.
  const boards = useMemo(() => ([
    { key: 'far', pos: [0, 0.55, -W / 2 - 3.2], rotY: 0, len: 100 },
    { key: 'l1', pos: [-L / 2 - 4.6, 0.55, -18], rotY: Math.PI / 2, len: 26 },
    { key: 'l2', pos: [-L / 2 - 4.6, 0.55, 18], rotY: Math.PI / 2, len: 26 },
    { key: 'r1', pos: [L / 2 + 4.6, 0.55, -18], rotY: -Math.PI / 2, len: 26 },
    { key: 'r2', pos: [L / 2 + 4.6, 0.55, 18], rotY: -Math.PI / 2, len: 26 },
  ] as const).map((b) => {
    const copy = (t: THREE.Texture) => { const c = t.clone(); c.repeat.set(b.len / 50, 1); c.needsUpdate = true; return c; };
    return { ...b, maps: { normal: copy(tex.normal), home: copy(tex.home), away: copy(tex.away) },
      mat: new THREE.MeshBasicMaterial({ toneMapped: false }) };
  }), [tex]);
  useFrame((_, dt) => {
    const now = performance.now();
    const cheer = rig.current.cheer && rig.current.cheer.until > now ? rig.current.cheer.side : undefined;
    const on = !cheer || rig.current.reduced || Math.floor(now / 180) % 2 === 0;
    for (const b of boards) {
      const map = cheer ? b.maps[cheer] : b.maps.normal;
      map.offset.x = (map.offset.x + dt * (cheer ? 0.08 : 0.02) * (50 / b.len)) % 1;
      if (b.mat.map !== map) { b.mat.map = map; b.mat.needsUpdate = true; }
      b.mat.color.setScalar(on ? 1 : 0.35);
    }
  });
  return (
    <>
      {boards.map((b) => (
        <mesh key={b.key} position={b.pos as unknown as [number, number, number]} rotation={[0, b.rotY, 0]} material={b.mat}>
          <boxGeometry args={[b.len, 1, 0.2]} />
        </mesh>
      ))}
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Floodlights

const MASTS: [number, number][] = [[-1, -1], [1, -1], [-1, 1], [1, 1]];
const mastAt = (s: number, e: number) => new THREE.Vector3(s * (L / 2 + 22), 34.5, e * (W / 2 + 18));

/** A soft round glow, for lamp halos and the camera flashes. */
function glowTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d')!;
  const grd = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  grd.addColorStop(0, 'rgba(255,255,255,1)'); grd.addColorStop(0.25, 'rgba(255,250,230,0.55)'); grd.addColorStop(1, 'rgba(255,250,230,0)');
  g.fillStyle = grd; g.fillRect(0, 0, 64, 64);
  return new THREE.CanvasTexture(c);
}

const beamShader = {
  vertexShader: `
    varying float vH; varying float vFacing;
    uniform float uHeight;
    void main() {
      vH = clamp(0.5 - position.y / uHeight, 0.0, 1.0);
      vec4 mv = modelViewMatrix * vec4(position, 1.0);
      vec3 n = normalize(normalMatrix * normal);
      vFacing = abs(dot(n, normalize(-mv.xyz)));
      gl_Position = projectionMatrix * mv;
    }`,
  fragmentShader: `
    varying float vH; varying float vFacing;
    uniform vec3 uColor; uniform float uOpacity;
    void main() {
      float a = uOpacity * pow(1.0 - vH, 1.1) * pow(vFacing, 1.3);
      gl_FragColor = vec4(uColor, a);
    }`,
};

/**
 * The four masts: lamp banks with a halo and a beam of light through the night haze down onto the pitch. The beams
 * drift a little, and pulse with the crowd on a goal.
 */
export function Floodlights({ rig }: { rig: MutableRefObject<Rig> }) {
  const glow = useMemo(glowTexture, []);
  const beams = useRef<(THREE.Mesh | null)[]>([]);
  const mats = useMemo(() => MASTS.map(() => new THREE.ShaderMaterial({
    uniforms: { uColor: { value: new THREE.Color('#fff3d6') }, uOpacity: { value: 0.16 }, uHeight: { value: 1 } },
    vertexShader: beamShader.vertexShader, fragmentShader: beamShader.fragmentShader,
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
  })), []);
  const tmp = useMemo(() => ({ a: new THREE.Vector3(), t: new THREE.Vector3(), d: new THREE.Vector3(), up: new THREE.Vector3(0, 1, 0), q: new THREE.Quaternion() }), []);

  useFrame(() => {
    const now = performance.now(), t = now / 1000;
    const r = rig.current;
    const cheer = r.cheer && r.cheer.until > now && !r.reduced;
    MASTS.forEach(([s, e], i) => {
      const m = beams.current[i];
      if (!m) return;
      const head = tmp.a.copy(mastAt(s, e));
      // Each bank lights its own quarter, drifting slowly.
      const drift = r.reduced ? 0 : Math.sin(t * 0.21 + i * 1.7) * 3;
      tmp.t.set(s * 22 + drift, 0, e * 12 + Math.cos(t * 0.17 + i) * 2);
      tmp.d.copy(head).sub(tmp.t);
      const len = tmp.d.length();
      m.position.copy(head).add(tmp.t).multiplyScalar(0.5);
      tmp.q.setFromUnitVectors(tmp.up, tmp.d.normalize());
      m.quaternion.copy(tmp.q);
      m.scale.set(1, len, 1);
      mats[i].uniforms.uHeight.value = 1;
      mats[i].uniforms.uOpacity.value = cheer ? 0.45 + 0.4 * (0.5 + 0.5 * Math.sin(t * 14 + i)) : 0.42 + 0.04 * Math.sin(t * 2.3 + i * 2);
    });
  });

  return (
    <>
      {MASTS.map(([s, e], i) => {
        const head = mastAt(s, e);
        return (
          <group key={`m${i}`}>
            <mesh position={[head.x, 17, head.z]}><cylinderGeometry args={[0.35, 0.55, 34, 8]} /><meshLambertMaterial color="#1a2028" /></mesh>
            <group position={head} rotation={[0, Math.atan2(-s, -e), 0]}>
              <mesh rotation={[-0.35, 0, 0]}><boxGeometry args={[7, 3.6, 0.5]} /><meshLambertMaterial color="#141a21" /></mesh>
              {Array.from({ length: 12 }, (_, k) => (
                <mesh key={k} position={[-2.6 + (k % 6) * 1.04, 0.75 - Math.floor(k / 6) * 1.5, 0.3]} rotation={[-0.35, 0, 0]}>
                  <circleGeometry args={[0.38, 12]} />
                  <meshBasicMaterial color="#fffbe8" toneMapped={false} />
                </mesh>
              ))}
              <sprite scale={[22, 22, 1]} position={[0, 0, 1]}>
                <spriteMaterial map={glow} color="#fff4dc" transparent opacity={0.55} blending={THREE.AdditiveBlending} depthWrite={false} />
              </sprite>
            </group>
            {/* The beam: an open cone, apex at the lamps, fading towards the grass. */}
            <mesh ref={(m) => { beams.current[i] = m; }} material={mats[i]}>
              <cylinderGeometry args={[0.6, 26, 1, 40, 1, true]} />
            </mesh>
          </group>
        );
      })}
      {/* The roof's own strip of lights over the far stand. */}
      <mesh position={[0, 15.45, -W / 2 - 5.6]}><boxGeometry args={[124, 0.25, 0.6]} /><meshBasicMaterial color="#fff6dc" toneMapped={false} /></mesh>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Big screens

/** Where the two big screens hang: the far corners, angled at the centre spot. */
export const SCREENS = [-1, 1].map((s) => ({ s, pos: new THREE.Vector3(s * 46, 12.6, -W / 2 - 9.5), rotY: -s * 0.42, w: 14, h: 7.8 }));

/** The two screens' frames and dark panels; the page's DOM content is laid onto `screen` planes each frame. */
export function BigScreens({ onScreen }: { onScreen: (i: number, o: THREE.Object3D | null) => void }) {
  return (
    <>
      {SCREENS.map((sc, i) => (
        <group key={i} position={sc.pos} rotation={[0, sc.rotY, 0]}>
          <mesh><boxGeometry args={[sc.w + 0.8, sc.h + 0.8, 0.6]} /><meshLambertMaterial color="#0d1116" /></mesh>
          <mesh position={[0, 0, 0.31]} ref={(o) => onScreen(i, o)}>
            <planeGeometry args={[sc.w, sc.h]} />
            <meshBasicMaterial color="#020406" />
          </mesh>
          {/* The truss it hangs from. */}
          <mesh position={[0, sc.h / 2 + 1.8, -0.2]}><boxGeometry args={[sc.w * 0.6, 2.8, 0.3]} /><meshLambertMaterial color="#141a21" /></mesh>
        </group>
      ))}
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// The crowd

interface Fan { x: number; y: number; z: number; ry: number; side: 'home' | 'away'; color: THREE.Color; skin: THREE.Color; phase: number }

/**
 * The ground: the far stand and an end behind each goal, stepped, under a roof, full of supporters (home on the left
 * and at the left end, away on the right). They sway while the match runs, the scoring end jumps on a goal, and phone
 * cameras flash: now and then during play, in a burst on a shot, and all round the ground on a goal.
 */
export function Ground({ info, rig }: { info: MatchInfo; rig: MutableRefObject<Rig> }) {
  const fans = useMemo(() => {
    const out: Fan[] = [];
    const home = new THREE.Color(info.home.primaryColor), away = new THREE.Color(info.away.primaryColor);
    const white = new THREE.Color('#e9edf0'), dark = new THREE.Color('#1b2128');
    const pick = (side: 'home' | 'away', h: number) => (h % 10 < 7 ? (side === 'home' ? home : away) : h % 10 < 9 ? white : dark);
    for (let r = 0; r < 12; r++) {
      for (let x = -58; x <= 58; x += 0.85) {
        if (Math.abs(((x + 58) % 14) - 7) < 0.6) continue;
        const h = hashOf(`f${r}:${x.toFixed(2)}`);
        if (h % 17 === 0) continue;
        const side = x < 0 ? 'home' : 'away';
        out.push({ x: x + ((h >> 4) % 5 - 2) * 0.05, y: 1.1 + r * 0.95, z: -W / 2 - 6.5 - r * 1.25, ry: 0, side, color: pick(side, h).clone(), skin: new THREE.Color(SKIN[h % SKIN.length]), phase: (h % 628) / 100 });
      }
    }
    for (const s of [-1, 1] as const) {
      for (let r = 0; r < 9; r++) {
        for (let z = -36; z <= 36; z += 0.85) {
          const h = hashOf(`e${s}${r}:${z.toFixed(2)}`);
          if (h % 13 === 0 || Math.abs(z) < 0.5) continue;
          const side = s < 0 ? 'home' : 'away';
          out.push({ x: s * (L / 2 + 8 + r * 1.25), y: 1.1 + r * 0.95, z, ry: s * Math.PI / 2, side, color: pick(side, h).clone(), skin: new THREE.Color(SKIN[h % SKIN.length]), phase: (h % 628) / 100 });
        }
      }
    }
    return out;
  }, [info]);

  const bodies = useRef<THREE.InstancedMesh>(null);
  const heads = useRef<THREE.InstancedMesh>(null);
  const m = useMemo(() => new THREE.Matrix4(), []);
  const q = useMemo(() => new THREE.Quaternion(), []);
  const one = useMemo(() => new THREE.Vector3(1, 1, 1), []);
  const v = useMemo(() => new THREE.Vector3(), []);

  useEffect(() => {
    const b = bodies.current, h = heads.current;
    if (!b || !h) return;
    fans.forEach((f, i) => { b.setColorAt(i, f.color); h.setColorAt(i, f.skin); });
    b.instanceColor!.needsUpdate = true;
    h.instanceColor!.needsUpdate = true;
  }, [fans]);

  // Flashes: a few hundred phones spread through the crowd, each with the time of its next flash.
  const flash = useMemo(() => {
    const n = 320;
    const pos = new Float32Array(n * 3), col = new Float32Array(n * 3), next = new Float64Array(n);
    for (let i = 0; i < n; i++) {
      const f = fans[(hashOf(`ph${i}`) % fans.length)];
      pos.set([f.x, f.y + 1.35, f.z], i * 3);
      next[i] = performance.now() + 4000 + (hashOf(`pt${i}`) % 60000);
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
    return { n, geo, col, next, seen: 0 };
  }, [fans]);
  const glow = useMemo(glowTexture, []);

  const frameNo = useRef(0);
  useFrame(() => {
    const b = bodies.current, h = heads.current;
    const now = performance.now();
    const r = rig.current;

    // New bursts: send that many phones off at random moments within the burst.
    while (flash.seen < r.flashes.length) {
      const burst = r.flashes[flash.seen++];
      if (r.reduced) continue;
      for (let k = 0; k < burst.n; k++) {
        const i = Math.floor(Math.random() * flash.n);
        flash.next[i] = Math.min(flash.next[i], burst.at + Math.random() * burst.dur);
      }
    }
    const col = flash.col;
    for (let i = 0; i < flash.n; i++) {
      const age = now - flash.next[i];
      const lit = age >= 0 && age < 140 ? 1 - age / 140 : 0;
      col[i * 3] = col[i * 3 + 1] = col[i * 3 + 2] = lit * 1.6;
      if (age >= 140) flash.next[i] = now + 8000 + Math.random() * 70000;
    }
    (flash.geo.attributes.color as THREE.BufferAttribute).needsUpdate = true;

    if (!b || !h) return;
    // Every other frame is plenty for a crowd.
    if (frameNo.current++ % 2) return;
    const cheer = r.cheer && r.cheer.until > now ? r.cheer.side : undefined;
    const t = now / 1000;
    const calm = r.reduced ? 0 : 1;
    for (let i = 0; i < fans.length; i++) {
      const f = fans[i];
      const jump = cheer === f.side && !r.reduced ? Math.abs(Math.sin(t * 7 + f.phase)) * 0.55 : 0;
      const sway = calm * Math.sin(t * 1.3 + f.phase) * 0.04;
      q.setFromAxisAngle(v.set(0, 1, 0), f.ry);
      m.compose(v.set(f.x, f.y + 0.45 + jump + sway, f.z), q, one);
      b.setMatrixAt(i, m);
      m.compose(v.set(f.x, f.y + 1.02 + jump + sway, f.z), q, one);
      h.setMatrixAt(i, m);
    }
    b.instanceMatrix.needsUpdate = true;
    h.instanceMatrix.needsUpdate = true;
  });

  return (
    <>
      <instancedMesh ref={bodies} args={[undefined, undefined, fans.length]} frustumCulled={false}>
        <boxGeometry args={[0.5, 0.75, 0.32]} />
        <meshLambertMaterial />
      </instancedMesh>
      <instancedMesh ref={heads} args={[undefined, undefined, fans.length]} frustumCulled={false}>
        <sphereGeometry args={[0.16, 8, 6]} />
        <meshLambertMaterial />
      </instancedMesh>
      <points geometry={flash.geo} frustumCulled={false}>
        <pointsMaterial map={glow} size={1.5} sizeAttenuation vertexColors transparent depthWrite={false} blending={THREE.AdditiveBlending} />
      </points>

      {/* The terraces: steps under the far stand and the ends. */}
      {Array.from({ length: 12 }, (_, r) => (
        <mesh key={`fs${r}`} position={[0, 0.05 + r * 0.95, -W / 2 - 6.5 - r * 1.25]}>
          <boxGeometry args={[122, 1.0, 1.3]} />
          <meshLambertMaterial color={r % 2 ? '#20262e' : '#262d36'} />
        </mesh>
      ))}
      {[-1, 1].map((s) => Array.from({ length: 9 }, (_, r) => (
        <mesh key={`es${s}${r}`} position={[s * (L / 2 + 8 + r * 1.25), 0.05 + r * 0.95, 0]}>
          <boxGeometry args={[1.3, 1.0, 76]} />
          <meshLambertMaterial color={r % 2 ? '#20262e' : '#262d36'} />
        </mesh>
      )))}
      <mesh position={[0, 0.6, -W / 2 - 5.4]}><boxGeometry args={[122, 1.2, 0.3]} /><meshLambertMaterial color="#11161c" /></mesh>
      <mesh position={[0, 15.8, -W / 2 - 15]}><boxGeometry args={[128, 0.5, 20]} /><meshLambertMaterial color="#0d1116" /></mesh>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// The ball's trail

const TRAIL = 36;

/**
 * A ribbon behind the ball when it travels: the last positions, fading out and narrowing towards the tail. White for
 * passes; on-air gold for a shot.
 */
export function BallTrail({ ball, rig }: { ball: RefObject<THREE.Group>; rig: MutableRefObject<Rig> }) {
  const st = useMemo(() => {
    const pos = new Float32Array(TRAIL * 2 * 3), col = new Float32Array(TRAIL * 2 * 3);
    const idx: number[] = [];
    for (let i = 0; i < TRAIL - 1; i++) { const a = i * 2; idx.push(a, a + 1, a + 2, a + 1, a + 3, a + 2); }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
    geo.setIndex(idx);
    return { geo, pos, col, pts: [] as THREE.Vector3[], hot: new THREE.Color('#f5c518'), cool: new THREE.Color('#ffffff') };
  }, []);
  useFrame((state) => {
    const b = ball.current;
    if (!b) return;
    const pts = st.pts;
    const head = b.position.clone();
    if (!b.visible) { pts.length = 0; }
    else if (!pts.length || pts[pts.length - 1].distanceTo(head) > 0.25) { pts.push(head); if (pts.length > TRAIL) pts.shift(); }
    // Fade the trail out when the ball has stopped (no new points for a while).
    if (pts.length && pts[pts.length - 1].distanceTo(head) < 0.01 && Math.random() < 0.25) pts.shift();
    const cam = state.camera.position;
    const color = (rig.current.trailHot ?? 0) > performance.now() ? st.hot : st.cool;
    const n = pts.length;
    for (let i = 0; i < TRAIL; i++) {
      const p = pts[Math.max(0, n - TRAIL + i)] ?? head;
      const prev = pts[Math.max(0, n - TRAIL + i - 1)] ?? p;
      const live = i >= TRAIL - n;
      const u = live ? (i - (TRAIL - n)) / Math.max(1, n - 1) : 0;
      // Width across the direction of travel, facing the camera.
      const dir = p.clone().sub(prev);
      const side = dir.lengthSq() > 1e-6 ? dir.cross(p.clone().sub(cam)).normalize().multiplyScalar(0.18 * u) : new THREE.Vector3();
      st.pos.set([p.x + side.x, p.y + side.y, p.z + side.z], i * 6);
      st.pos.set([p.x - side.x, p.y - side.y, p.z - side.z], i * 6 + 3);
      const a = live ? u * u * 0.85 : 0;
      st.col.set([color.r * a, color.g * a, color.b * a, color.r * a, color.g * a, color.b * a], i * 6);
    }
    st.geo.attributes.position.needsUpdate = true;
    st.geo.attributes.color.needsUpdate = true;
    st.geo.computeBoundingSphere();
  });
  return (
    <mesh geometry={st.geo} frustumCulled={false}>
      <meshBasicMaterial vertexColors transparent depthWrite={false} blending={THREE.AdditiveBlending} side={THREE.DoubleSide} toneMapped={false} />
    </mesh>
  );
}
