import { useMemo, useRef, type MutableRefObject } from 'react';
import { useFrame } from '@react-three/fiber';
import * as THREE from 'three';
import { getControl } from '../../tracking';
import { L, W, wx } from './math';
import type { Rig } from './rig';

/**
 * The telestrator: graphics drawn from the tracking frame on screen (live or from the DVR), so they show what the
 * positions measure, not an illustration.
 *
 * - Offside line: a wall at the defending team's offside line: the second-last defender, the ball or halfway,
 *   whichever is nearest their goal. Attackers beyond it are ringed (an offside position, not a call).
 * - Shape: each team's outfield players as a convex hull on the grass, with its width and depth in metres.
 * - Runs: an arrow ahead of every player running at high intensity (19.8 km/h and up), its length a second and a half
 *   of that run.
 * - Lock: a ring, a light column and a spotlight on the locked player.
 */

export interface TeamSets { home: Set<string>; away: Set<string>; keepers: Set<string>; homeColor: string; awayColor: string }

/** A hull label for the page: where it sits and what it says. */
export interface HullLabel { x: number; z: number; text: string }

const MAX_HULL = 12;
const RUN_KMH = 19.8;

const fadeShader = {
  vertexShader: `
    varying float vY;
    uniform float uHeight;
    void main() { vY = clamp(0.5 + position.y / uHeight, 0.0, 1.0); gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
  fragmentShader: `
    varying float vY;
    uniform vec3 uColor; uniform float uOpacity; uniform float uPower;
    void main() { float a = uOpacity * pow(1.0 - vY, uPower); gl_FragColor = vec4(uColor, a); }`,
};
const fadeMaterial = (color: string, opacity: number, height: number, power = 1.4) => new THREE.ShaderMaterial({
  uniforms: { uColor: { value: new THREE.Color(color) }, uOpacity: { value: opacity }, uHeight: { value: height }, uPower: { value: power } },
  vertexShader: fadeShader.vertexShader, fragmentShader: fadeShader.fragmentShader,
  transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
});

/** Convex hull (Andrew's monotone chain), counter-clockwise. */
export function hull(points: { x: number; z: number }[]) {
  const p = [...points].sort((a, b) => a.x - b.x || a.z - b.z);
  if (p.length < 3) return p;
  const cross = (o: { x: number; z: number }, a: { x: number; z: number }, b: { x: number; z: number }) => (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);
  const lower: typeof p = [], upper: typeof p = [];
  for (const q of p) { while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], q) <= 0) lower.pop(); lower.push(q); }
  for (let i = p.length - 1; i >= 0; i--) { const q = p[i]; while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], q) <= 0) upper.pop(); upper.push(q); }
  return lower.slice(0, -1).concat(upper.slice(0, -1));
}

/**
 * Pitch control on the grass: each cell in the colour of the team that would get there first, stronger where it
 * clearly owns the space. The map is the server's (live, about once a second); it is not drawn during a DVR replay.
 */
function ControlCarpet({ rig, teams }: { rig: MutableRefObject<Rig>; teams: TeamSets }) {
  const st = useMemo(() => {
    const cols = 32, rows = 21;
    const data = new Uint8Array(cols * rows * 4);
    const tex = new THREE.DataTexture(data, cols, rows, THREE.RGBAFormat);
    tex.magFilter = THREE.LinearFilter;
    tex.minFilter = THREE.LinearFilter;
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.needsUpdate = true;
    const srgb = (c: string) => ({ srgb: [1, 3, 5].map((i) => parseInt(c.slice(i, i + 2), 16)) });
    return { tex, data, cols, rows, last: undefined as unknown, home: srgb(teams.homeColor), away: srgb(teams.awayColor) };
  }, [teams]);
  const mesh = useRef<THREE.Mesh>(null);
  useFrame(() => {
    const r = rig.current, m = mesh.current;
    if (!m) return;
    const c = getControl();
    m.visible = r.mode === 'live' && r.layers.control && r.dvr.live && !!c;
    if (!c || c === st.last || c.cols !== st.cols || c.rows !== st.rows) return;
    st.last = c;
    for (let row = 0; row < c.rows; row++) {
      // Texture rows run from the near touchline up; the map's from the far one (y = 0).
      const tr = c.rows - 1 - row;
      for (let col = 0; col < c.cols; col++) {
        const v = c.home[row * c.cols + col];
        const col3 = v >= 0.5 ? st.home : st.away;
        const k = (tr * c.cols + col) * 4;
        st.data[k] = col3.srgb[0]; st.data[k + 1] = col3.srgb[1]; st.data[k + 2] = col3.srgb[2];
        st.data[k + 3] = (0.1 + 0.55 * Math.min(1, Math.abs(v - 0.5) * 2)) * 255;
      }
    }
    st.tex.needsUpdate = true;
  });
  return (
    <mesh ref={mesh} rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.025, 0]} visible={false}>
      <planeGeometry args={[L, W]} />
      <meshBasicMaterial map={st.tex} transparent depthWrite={false} toneMapped={false} />
    </mesh>
  );
}

export function Telestrator({ rig, teams, onHulls }: { rig: MutableRefObject<Rig>; teams: TeamSets; onHulls: (h: (HullLabel | undefined)[]) => void }) {
  // Offside: a wall and a line on the grass, coloured by the defending team, and rings for attackers beyond it.
  const off = useMemo(() => ({
    wallHome: fadeMaterial(teams.homeColor, 0.7, 2.6, 1.1), wallAway: fadeMaterial(teams.awayColor, 0.7, 2.6, 1.1),
    rings: Array.from({ length: 6 }, () => new THREE.Mesh(new THREE.RingGeometry(0.75, 0.95, 28), new THREE.MeshBasicMaterial({ color: '#ff4d4d', transparent: true, opacity: 0.95, depthWrite: false, side: THREE.DoubleSide }))),
  }), [teams]);
  const wall = useRef<THREE.Mesh>(null);
  const line = useRef<THREE.Mesh>(null);
  const lineMat = useRef<THREE.MeshBasicMaterial>(null);
  const ringsGroup = useRef<THREE.Group>(null);

  // Hulls: a filled fan and fading walls along the edges, one per team.
  const hulls = useMemo(() => [teams.homeColor, teams.awayColor].map((c) => {
    const fill = new THREE.BufferGeometry();
    fill.setAttribute('position', new THREE.BufferAttribute(new Float32Array((MAX_HULL + 1) * 3), 3));
    const fi: number[] = [];
    for (let i = 0; i < MAX_HULL; i++) fi.push(0, i + 1, ((i + 1) % MAX_HULL) + 1);
    fill.setIndex(fi);
    const edge = new THREE.BufferGeometry();
    edge.setAttribute('position', new THREE.BufferAttribute(new Float32Array(MAX_HULL * 4 * 3), 3));
    const ei: number[] = [];
    for (let i = 0; i < MAX_HULL; i++) { const a = i * 4; ei.push(a, a + 1, a + 2, a + 1, a + 3, a + 2); }
    edge.setIndex(ei);
    return {
      fill, edge,
      fillMat: new THREE.MeshBasicMaterial({ color: c, transparent: true, opacity: 0.16, depthWrite: false, side: THREE.DoubleSide }),
      edgeMat: fadeMaterial(c, 0.75, 0.9, 1.2),
    };
  }), [teams]);
  const hullGroups = useRef<(THREE.Group | null)[]>([]);

  // Run arrows: a pool, flat on the grass.
  const arrows = useMemo(() => Array.from({ length: 22 }, () => {
    const g = new THREE.Group();
    const mat = new THREE.MeshBasicMaterial({ color: '#ffffff', transparent: true, opacity: 0.85, depthWrite: false, side: THREE.DoubleSide });
    const shaft = new THREE.Mesh(new THREE.PlaneGeometry(0.32, 1), mat);
    shaft.rotation.x = -Math.PI / 2;
    const headShape = new THREE.Shape();
    headShape.moveTo(-0.6, 0); headShape.lineTo(0.6, 0); headShape.lineTo(0, 1.1); headShape.closePath();
    const head = new THREE.Mesh(new THREE.ShapeGeometry(headShape), mat);
    head.rotation.x = -Math.PI / 2;
    g.add(shaft, head);
    g.visible = false;
    return { g, shaft, head, mat };
  }), []);
  const arrowGroup = useRef<THREE.Group>(null);

  // Lock: ring, light column and a spotlight from above.
  const lock = useMemo(() => ({
    ring: new THREE.Mesh(new THREE.RingGeometry(0.95, 1.2, 40), new THREE.MeshBasicMaterial({ color: '#f5c518', transparent: true, opacity: 0.95, depthWrite: false, side: THREE.DoubleSide, toneMapped: false })),
    ring2: new THREE.Mesh(new THREE.RingGeometry(1.45, 1.55, 40, 1, 0, Math.PI * 1.4), new THREE.MeshBasicMaterial({ color: '#f5c518', transparent: true, opacity: 0.7, depthWrite: false, side: THREE.DoubleSide })),
    column: new THREE.Mesh(new THREE.CylinderGeometry(0.95, 0.95, 6, 32, 1, true), fadeMaterial('#f5c518', 0.35, 6, 1.6)),
    spot: new THREE.Mesh(new THREE.CylinderGeometry(0.25, 2.6, 24, 32, 1, true), fadeMaterial('#fff4d6', 0.22, 24, 0.6)),
  }), []);
  const lockGroup = useRef<THREE.Group>(null);
  const lastLabels = useRef<string>('');

  useFrame(() => {
    const r = rig.current;
    const f = r.shown;
    const live = r.mode === 'live' && !!f;
    const now = performance.now();

    // --- Offside line
    const showOff = live && r.layers.offside;
    if (wall.current && line.current && ringsGroup.current) {
      wall.current.visible = line.current.visible = ringsGroup.current.visible = false;
      off.rings.forEach((m) => { m.visible = false; });
      if (showOff && f) {
        const bx = wx(f.ballX);
        const awayDefends = bx > 0;
        const defenders = (awayDefends ? teams.away : teams.home);
        const xs: number[] = [];
        r.at.forEach((p, id) => { if (defenders.has(id)) xs.push(p.x); });
        if (xs.length >= 2) {
          xs.sort((a, b) => (awayDefends ? b - a : a - b));
          const second = xs[1];
          const lx = awayDefends ? Math.max(second, bx, 0) : Math.min(second, bx, 0);
          wall.current.visible = line.current.visible = ringsGroup.current.visible = true;
          wall.current.position.set(lx, 1.3, 0);
          wall.current.material = awayDefends ? off.wallAway : off.wallHome;
          line.current.position.set(lx, 0.035, 0);
          lineMat.current?.color.set('#ffffff');
          // Attackers beyond the line.
          let k = 0;
          const attackers = awayDefends ? teams.home : teams.away;
          r.at.forEach((p, id) => {
            if (k >= off.rings.length || !attackers.has(id)) return;
            if (awayDefends ? p.x > lx + 0.25 : p.x < lx - 0.25) {
              const m = off.rings[k++];
              m.visible = true;
              m.position.set(p.x, 0.04, p.z);
              (m.material as THREE.MeshBasicMaterial).opacity = 0.65 + 0.3 * Math.sin(now / 160);
            }
          });
        }
      }
    }

    // --- Hulls
    const labels: (HullLabel | undefined)[] = [undefined, undefined];
    [teams.home, teams.away].forEach((team, ti) => {
      const g = hullGroups.current[ti];
      if (!g) return;
      g.visible = false;
      if (!live || !r.layers.shape) return;
      const pts: { x: number; z: number }[] = [];
      r.at.forEach((p, id) => { if (team.has(id) && !teams.keepers.has(id)) pts.push(p); });
      const h = hull(pts).slice(0, MAX_HULL);
      if (h.length < 3) return;
      g.visible = true;
      const { fill, edge } = hulls[ti];
      const cx = h.reduce((s, p) => s + p.x, 0) / h.length, cz = h.reduce((s, p) => s + p.z, 0) / h.length;
      const fp = fill.attributes.position.array as Float32Array;
      fp.set([cx, 0.03 + ti * 0.005, cz], 0);
      for (let i = 0; i < MAX_HULL; i++) {
        const p = h[Math.min(i, h.length - 1)];
        fp.set([p.x, 0.03 + ti * 0.005, p.z], (i + 1) * 3);
      }
      fill.attributes.position.needsUpdate = true;
      fill.computeBoundingSphere();
      const ep = edge.attributes.position.array as Float32Array;
      for (let i = 0; i < MAX_HULL; i++) {
        const a = h[Math.min(i, h.length - 1)], b = i < h.length ? h[(i + 1) % h.length] : a;
        // Each edge a wall from the grass up to 0.9 m (the shader fades it out upwards).
        ep.set([a.x, -0.45, a.z, a.x, 0.45, a.z, b.x, -0.45, b.z, b.x, 0.45, b.z], i * 12);
      }
      edge.attributes.position.needsUpdate = true;
      edge.computeBoundingSphere();
      const xs = h.map((p) => p.x), zs = h.map((p) => p.z);
      const depth = Math.max(...xs) - Math.min(...xs), width = Math.max(...zs) - Math.min(...zs);
      labels[ti] = { x: cx, z: cz, text: `${Math.round(width)} × ${Math.round(depth)} m` };
    });
    const key = labels.map((l) => l?.text ?? '').join('|');
    r.hullLabels = labels;
    if (key !== lastLabels.current) { lastLabels.current = key; onHulls(labels); }

    // --- Run arrows
    let n = 0;
    if (live && r.layers.runs) {
      r.at.forEach((p, id) => {
        const kmh = r.speeds.get(id) ?? 0, v = r.vel.get(id);
        if (n >= arrows.length || kmh < RUN_KMH || !v || teams.keepers.has(id)) return;
        const a = arrows[n++];
        const len = THREE.MathUtils.clamp((kmh / 3.6) * 1.5, 3, 13);
        a.g.visible = true;
        a.g.position.set(p.x, 0.05, p.z);
        a.g.rotation.y = Math.atan2(v.vx, v.vz);
        a.shaft.scale.y = len - 1.1;
        a.shaft.position.set(0, 0, 0.9 + (len - 1.1) / 2);
        a.head.position.set(0, 0, 0.9 + len - 1.1);
        a.mat.color.set(teams.home.has(id) ? teams.homeColor : teams.awayColor);
      });
    }
    for (let i = n; i < arrows.length; i++) arrows[i].g.visible = false;

    // --- Lock
    const lg = lockGroup.current;
    if (lg) {
      const p = r.lock ? r.at.get(r.lock) : undefined;
      lg.visible = !!p && r.mode === 'live';
      if (p) {
        lg.position.set(p.x, 0, p.z);
        lock.ring.rotation.z = now / 900;
        lock.ring2.rotation.z = -now / 600;
        const pulse = 1 + 0.06 * Math.sin(now / 220);
        lock.ring.scale.setScalar(pulse);
      }
    }
  });

  return (
    <>
      <ControlCarpet rig={rig} teams={teams} />
      <mesh ref={wall} rotation={[0, Math.PI / 2, 0]} visible={false}>
        <planeGeometry args={[W + 2, 2.6]} />
      </mesh>
      <mesh ref={line} rotation={[-Math.PI / 2, 0, 0]} visible={false}>
        <planeGeometry args={[0.3, W]} />
        <meshBasicMaterial ref={lineMat} color="#ffffff" transparent opacity={1} depthWrite={false} toneMapped={false} />
      </mesh>
      <group ref={ringsGroup}>
        {off.rings.map((m, i) => <primitive key={i} object={m} rotation={[-Math.PI / 2, 0, 0]} />)}
      </group>

      {hulls.map((h, i) => (
        <group key={i} ref={(g) => { hullGroups.current[i] = g; }} visible={false}>
          <mesh geometry={h.fill} material={h.fillMat} frustumCulled={false} />
          <mesh geometry={h.edge} material={h.edgeMat} frustumCulled={false} position={[0, 0.45, 0]} />
        </group>
      ))}

      <group ref={arrowGroup}>
        {arrows.map((a, i) => <primitive key={i} object={a.g} />)}
      </group>

      <group ref={lockGroup} visible={false}>
        <primitive object={lock.ring} rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.05, 0]} />
        <primitive object={lock.ring2} rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.05, 0]} />
        <primitive object={lock.column} position={[0, 3, 0]} />
        <primitive object={lock.spot} position={[0, 12, 0]} />
      </group>
    </>
  );
}

/** Half the pitch's length, for clamping labels. */
export const HALF_L = L / 2;
