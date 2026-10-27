import * as THREE from 'three';

/** Pitch size in metres; the scene's origin is the centre spot, x along the length (home attacks +x), z across. */
export const L = 105, W = 68;
/** Board units (percent of the pitch, home attacking to 100) to scene metres. */
export const wx = (pct: number) => (pct / 100) * L - L / 2;
export const wz = (pct: number) => (pct / 100) * W - W / 2;
export const SKIN = ['#f1c7a5', '#e0ab84', '#c68a5f', '#9a6440', '#6e4428'];

export function hashOf(s: string) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
  return h >>> 0;
}

export function lerpAngle(a: number, b: number, t: number) {
  let d = ((b - a + Math.PI) % (Math.PI * 2)) - Math.PI;
  if (d < -Math.PI) d += Math.PI * 2;
  return a + d * t;
}

/** A world point on screen, in CSS pixels of the canvas, or undefined when it is behind the camera. */
export function project(p: THREE.Vector3, cam: THREE.Camera, size: { width: number; height: number }) {
  const view = p.clone().applyMatrix4(cam.matrixWorldInverse);
  if (view.z > -0.5) return undefined;
  const n = p.clone().project(cam);
  return { x: (n.x + 1) / 2 * size.width, y: (1 - n.y) / 2 * size.height };
}

/**
 * Puts an element on a plane in the scene: its four corners (top-left, top-right, bottom-left, bottom-right) go to
 * the projected world corners, through the projective transform between the two quads.
 */
export function placeQuad(el: HTMLElement, corners: [number, number, number][], cam: THREE.Camera, size: { width: number; height: number }, scratch: THREE.Vector3[]) {
  const pts: { x: number; y: number }[] = [];
  for (let i = 0; i < 4; i++) {
    const p = project(scratch[i].set(...corners[i]), cam, size);
    if (!p) { el.style.visibility = 'hidden'; return; }
    pts.push(p);
  }
  const w = el.offsetWidth, h = el.offsetHeight;
  if (!w || !h) return;
  const t = quadToQuad([0, 0, w, 0, 0, h, w, h], [pts[0].x, pts[0].y, pts[1].x, pts[1].y, pts[2].x, pts[2].y, pts[3].x, pts[3].y]);
  for (let i = 0; i < 9; i++) t[i] /= t[8];
  el.style.visibility = 'visible';
  el.style.transform = `matrix3d(${t[0]},${t[3]},0,${t[6]},${t[1]},${t[4]},0,${t[7]},0,0,1,0,${t[2]},${t[5]},0,${t[8]})`;
}

type M3 = number[];
const adj = (m: M3): M3 => [
  m[4] * m[8] - m[5] * m[7], m[2] * m[7] - m[1] * m[8], m[1] * m[5] - m[2] * m[4],
  m[5] * m[6] - m[3] * m[8], m[0] * m[8] - m[2] * m[6], m[2] * m[3] - m[0] * m[5],
  m[3] * m[7] - m[4] * m[6], m[1] * m[6] - m[0] * m[7], m[0] * m[4] - m[1] * m[3],
];
function mul(a: M3, b: M3): M3 {
  const c = new Array(9).fill(0);
  for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) for (let k = 0; k < 3; k++) c[3 * i + j] += a[3 * i + k] * b[3 * k + j];
  return c;
}
const mulv = (m: M3, v: number[]) => [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];
function basis(p: number[]): M3 {
  const m = [p[0], p[2], p[4], p[1], p[3], p[5], 1, 1, 1];
  const v = mulv(adj(m), [p[6], p[7], 1]);
  return mul(m, [v[0], 0, 0, 0, v[1], 0, 0, 0, v[2]]);
}
const quadToQuad = (src: number[], dst: number[]) => mul(basis(dst), adj(basis(src)));
