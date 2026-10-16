const fs = require('fs'), path = require('path');
const { Resvg } = require('@resvg/resvg-js');
const pc = require('polygon-clipping');

const OUT = process.argv[2] || path.join(__dirname, 'out');
for (const d of ['icons', 'empty64']) fs.mkdirSync(path.join(OUT, d), { recursive: true });

const R = Math.PI / 180;
const BIG = { Hl: 0.40, Hu: 0.44, e: 30 * R, D: 2.9, ty: 0.42, S: 64 };
const SMALL = { Hl: 0.33, Hu: 0.33, e: 27 * R, D: 40, ty: 0.33, S: 64 };

function projector(c) {
  const ce = Math.cos(c.e), se = Math.sin(c.e);
  return (x, y, z) => {
    const yy = y - c.ty, Y = yy * ce - z * se, Z = yy * se + z * ce, f = c.D / (c.D - Z);
    return [100 + c.S * x * f, 100 - c.S * Y * f];
  };
}
const ring = (P, ox, oz, r, y, n) => { const a = []; for (let k = 0; k < n; k++) { const t = k / n * 2 * Math.PI; a.push(P(ox + r * Math.cos(t), y, oz + r * Math.sin(t))); } return a; };
const ext = (a) => { let l = 0, r = 0; for (let i = 1; i < a.length; i++) { if (a[i][0] < a[l][0]) l = i; if (a[i][0] > a[r][0]) r = i; } return [l, r]; };
const walk = (a, i0, i1) => { const n = a.length, o = []; let i = i0; for (let g = 0; g <= n; g++) { o.push(a[i]); if (i === i1) break; i = (i + 1) % n; } return o; };
const pd = (p, close) => { let s = ''; for (let i = 0; i < p.length; i++) s += (i ? 'L' : 'M') + p[i][0].toFixed(2) + ',' + p[i][1].toFixed(2); return close ? s + 'Z' : s; };

function cyl(P, ox, oz, r, y0, y1, n) {
  const B = ring(P, ox, oz, r, y0, n), T = ring(P, ox, oz, r, y1, n);
  const eb = ext(B), et = ext(T);
  const fb = walk(B, eb[1], eb[0]), ft = walk(T, et[1], et[0]);
  return { band: fb.concat(ft.slice().reverse()), top: T, bottomFront: fb, topFront: ft, L: T[et[0]][0], Rx: T[et[1]][0] };
}

function geom(m, th, o) {
  const P = projector(m), top = m.Hl + m.Hu;
  const low = cyl(P, 0, 0, 1, 0, m.Hl, 144), up = cyl(P, 0, 0, 1, m.Hl, top, 144);
  const px = o.pc * Math.cos(th), pz = o.pc * Math.sin(th);
  const peg = cyl(P, px, pz, o.pr, top, top + o.ph, 36);
  const ha = th + Math.PI, hx = o.hc * Math.cos(ha), hz = o.hc * Math.sin(ha);
  const hole = ring(P, hx, hz, o.hr, top, 48);
  const deep = ring(P, hx, hz, o.hr, top - 0.09, 48);
  const lit = ring(P, hx + 0.17 * o.hr / 0.19, hz, o.hr, top, 48);
  const dR = 0.86;
  const dish = ring(P, 0, 0, dR, top, 120);
  const dShade = ring(P, 0.13, -0.08, dR, top, 120), dLight = ring(P, -0.11, 0.09, dR, top, 120);
  const fc = P(-0.35, top, 0.3);
  const shadow = ring(P, 0.04, 0.05, 1.07, 0, 96);
  const shadeLow = walk(ring(P, 0, 0, 1, m.Hl - 0.075, 144), ...(() => { const r0 = ring(P, 0, 0, 1, m.Hl - 0.075, 144), e0 = ext(r0); return [e0[1], e0[0]]; })());
  const pegShadow = ring(P, px + 0.05 * o.ps, pz + 0.06 * o.ps, o.pr * 1.7, top, 24);
  const poly = (pts) => [pts.map((q) => [q[0], q[1]]).concat([[pts[0][0], pts[0][1]]])];
  const u = pc.union(poly(low.band), poly(up.band), poly(up.top), poly(peg.band), poly(peg.top));
  let sil = '';
  for (const mp of u) for (const rg of mp) sil += pd(rg, true);
  return { low, up, peg, hole, deep, lit, dish, dShade, dLight, fc, shadow, shadeLow, pegShadow, sil };
}

const BAND_TOP = ['#d8d0c2', '#c3baab', '#8a8276', '#56504a'];
const BAND_LOW = ['#c6bdae', '#afa697', '#776f64', '#48433d'];
const BAND_LOW_S = ['#cdc5b7', '#b9b0a2', '#857d71', '#57514a'];
const PEG = ['#e2bd92', '#c99c6b', '#94673f', '#5e3f25'];
const INK = '#1f1c19';
const grad = (id, x1, x2, c, offs = [0, 0.45, 0.8, 1]) =>
  `<linearGradient id="${id}" x1="${x1.toFixed(2)}" y1="0" x2="${x2.toFixed(2)}" y2="0" gradientUnits="userSpaceOnUse">` +
  c.map((col, i) => `<stop offset="${offs[i]}" stop-color="${col}"/>`).join('') + `</linearGradient>`;

function svg(spec, th = 0.1) {
  const m = spec.small ? SMALL : BIG;
  const o = spec.small
    ? { pc: 0.62, pr: 0.15, ph: 0.8, hc: 0.38, hr: 0.22, ps: 1 }
    : { pc: 0.7, pr: 0.065 * spec.ps, ph: 0.36 * Math.sqrt(spec.ps), hc: 0.4, hr: 0.19, ps: spec.ps };
  const g = geom(m, th, o);
  let place, k = 1;
  if (spec.small) {
    const g0 = geom(m, 0.1, o), pts = [];
    for (const rg of pc.union([g0.low.band.concat([g0.low.band[0]])], [g0.up.top.concat([g0.up.top[0]])], [g0.peg.band.concat([g0.peg.band[0]])], [g0.peg.top.concat([g0.peg.top[0]])])) for (const r of rg) pts.push(...r);
    const a = -10 * R; let x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
    for (const p of pts) { const dx = p[0] - 100, dy = p[1] - 100, x = 100 + dx * Math.cos(a) - dy * Math.sin(a), y = 100 + dx * Math.sin(a) + dy * Math.cos(a); x0 = Math.min(x0, x); x1 = Math.max(x1, x); y0 = Math.min(y0, y); y1 = Math.max(y1, y); }
    k = 172 / Math.max(x1 - x0, y1 - y0);
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
    place = `translate(100 100) scale(${k.toFixed(4)}) translate(${(-cx).toFixed(2)} ${(-cy).toFixed(2)}) rotate(-10 100 100)`;
  } else place = 'translate(2.7 -3.2) rotate(-10 100 100)';
  const U = (pxw) => pxw * 200 / spec.px / k;
  const wo = U(spec.wo), wi = U(spec.wi);
  const id = spec.small ? 's' : 'b';
  const defs = [
    grad(id + 'gt', g.up.L, g.up.Rx, BAND_TOP),
    grad(id + 'gl', g.low.L, g.low.Rx, spec.small ? BAND_LOW_S : BAND_LOW),
    grad(id + 'gp', g.peg.L, g.peg.Rx, PEG, [0, 0.3, 0.72, 1]),
    `<radialGradient id="${id}face" cx="${g.fc[0].toFixed(2)}" cy="${g.fc[1].toFixed(2)}" r="${(1.4 * 64).toFixed(1)}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#f0e9de"/><stop offset="1" stop-color="#c5bbac"/></radialGradient>`,
    `<clipPath id="${id}cd"><path d="${pd(g.dish, true)}"/></clipPath>`,
    `<clipPath id="${id}ch"><path d="${pd(g.hole, true)}"/></clipPath>`,
    `<filter id="${id}soft" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="${Math.max(1.2, U(spec.px / 64 * 1.6)).toFixed(2)}"/></filter>`,
  ].join('');
  const L = [];
  if (spec.rim === 'icon') {
    L.push(`<path d="${g.sil}" fill="#cfc8bc" stroke="#cfc8bc" stroke-width="${(wo + 2 * U(spec.rimPx)).toFixed(2)}" stroke-linejoin="round" opacity=".7"/>`);
  } else if (spec.rim === 'glow') {
    const target = [0.50, 0.40, 0.30, 0.20, 0.10], layer = []; let below = 1;
    for (let q = 4; q >= 0; q--) { const a = 1 - (1 - target[q]) / below; layer[q] = a; below *= (1 - a); }
    const step = U(spec.glowDip * spec.px / spec.dip);
    for (let q = 4; q >= 0; q--)
      L.push(`<path d="${g.sil}" fill="#cfc8bc" stroke="#cfc8bc" stroke-width="${(wo + 2 * step * (q + 1)).toFixed(2)}" stroke-linejoin="round" opacity="${layer[q].toFixed(3)}"/>`);
  }
  L.push(`<path d="${pd(g.low.band, true)}" fill="url(#${id}gl)"/>`);
  L.push(`<clipPath id="${id}cl"><path d="${pd(g.low.band, true)}"/></clipPath>`);
  L.push(`<g clip-path="url(#${id}cl)"><path d="${pd(g.up.bottomFront.concat(g.shadeLow.slice().reverse()), true)}" fill="${INK}" opacity="${spec.small ? 0.14 : 0.2}" filter="url(#${id}soft)"/></g>`);
  L.push(`<path d="${pd(g.up.band, true)}" fill="url(#${id}gt)"/>`);
  const ws = Math.min(wi * 1.75, wo * 0.8);
  L.push(`<path d="${pd(g.up.bottomFront, false)}" fill="none" stroke="${INK}" stroke-width="${ws.toFixed(2)}" stroke-linecap="round" opacity="${Math.min(1, spec.innerOp + 0.15)}"/>`);
  L.push(`<path d="${pd(g.up.top, true)}" fill="${spec.small ? '#e6dfd2' : `url(#${id}face)`}"/>`);
  if (!spec.small) {
    const dishD = pd(g.dish, true);
    L.push(`<path d="${pd(g.up.top, true)}${dishD}" fill="#fffdf6" fill-rule="evenodd" opacity=".18"/>`);
    L.push(`<g clip-path="url(#${id}cd)"><path d="${dishD}${pd(g.dShade, true)}" fill="#2a241f" fill-rule="evenodd" opacity=".34" filter="url(#${id}soft)"/>` +
      `<path d="${dishD}${pd(g.dLight, true)}" fill="#fffaf0" fill-rule="evenodd" opacity=".34" filter="url(#${id}soft)"/></g>`);
    if (spec.dish) L.push(`<path d="${dishD}" fill="none" stroke="${INK}" stroke-width="${(wi * 0.7).toFixed(2)}" opacity="${(spec.innerOp * 0.5).toFixed(2)}"/>`);
  }
  L.push(`<path d="${pd(g.up.topFront, false)}" fill="none" stroke="${INK}" stroke-width="${wi.toFixed(2)}" stroke-linecap="round" opacity="${spec.innerOp}"/>`);
  L.push(`<path d="${pd(g.hole, true)}" fill="${spec.small ? '#6a6157' : '#5f574e'}"/>`);
  L.push(`<g clip-path="url(#${id}ch)">` + (spec.small ? '' : `<path d="${pd(g.lit, true)}" fill="#958a7d"/>`) + `<path d="${pd(g.deep, true)}" fill="#0e0c0b"/></g>`);
  L.push(`<path d="${pd(g.hole, true)}" fill="none" stroke="${INK}" stroke-width="${(wi * 0.95).toFixed(2)}" opacity="${spec.innerOp}"/>`);
  if (!spec.small) L.push(`<path d="${pd(g.pegShadow, true)}" fill="${INK}" opacity=".2"/>`);
  const wp = U(spec.wp);
  L.push(`<path d="${pd(g.peg.band, true)}" fill="url(#${id}gp)" stroke="${INK}" stroke-width="${wp.toFixed(2)}" stroke-linejoin="round"/>`);
  L.push(`<path d="${pd(g.peg.top, true)}" fill="#e6c79f" stroke="${INK}" stroke-width="${wp.toFixed(2)}" stroke-linejoin="round"/>`);
  L.push(`<path d="${g.sil}" fill="none" stroke="${INK}" stroke-width="${wo.toFixed(2)}" stroke-linejoin="round"/>`);
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200" width="${spec.px}" height="${spec.px}"><defs>${defs}</defs><g transform="${place}">${L.join('')}</g></svg>`;
}

const png = (s) => new Resvg(s, { background: 'rgba(0,0,0,0)', shapeRendering: 2 }).render().asPng();

const ICONS = {
  512: { px: 512, wo: 7.6, wi: 2.8, wp: 3.4, ps: 1.0, rimPx: 2.0, dish: true, innerOp: 0.75 },
  256: { px: 256, wo: 4.2, wi: 1.6, wp: 2.0, ps: 1.0, rimPx: 1.2, dish: true, innerOp: 0.75 },
  128: { px: 128, wo: 2.8, wi: 1.15, wp: 1.4, ps: 1.3, rimPx: 1.0, dish: true, innerOp: 0.75 },
  96: { px: 96, wo: 2.4, wi: 1.0, wp: 1.2, ps: 1.35, rimPx: 0.9, dish: true, innerOp: 0.75 },
  64: { small: true, px: 64, wo: 3.6, wi: 1.75, wp: 2.4, rimPx: 2.2, innerOp: 0.8 },
  48: { small: true, px: 48, wo: 2.9, wi: 1.4, wp: 1.95, rimPx: 1.8, innerOp: 0.8 },
  40: { small: true, px: 40, wo: 2.45, wi: 1.2, wp: 1.6, rimPx: 1.5, innerOp: 0.8 },
  32: { small: true, px: 32, wo: 1.95, wi: 0.95, wp: 1.3, rimPx: 1.2, innerOp: 0.8 },
  24: { small: true, px: 24, wo: 1.5, wi: 0.75, wp: 1.0, rimPx: 0.9, innerOp: 0.8 },
  20: { small: true, px: 20, wo: 1.25, wi: 0.65, wp: 0.85, rimPx: 0.75, innerOp: 0.8 },
  16: { small: true, px: 16, wo: 1.0, wi: 0.55, wp: 0.75, rimPx: 0.6, innerOp: 0.85 },
};
for (const [n, sp] of Object.entries(ICONS))
  fs.writeFileSync(path.join(OUT, 'icons', `icon-${n}.png`), png(svg(Object.assign({ rim: 'icon' }, sp))));

const DETAIL64 = { px: 64, wo: 1.9, wi: 0.85, wp: 1.0, ps: 1.45, rimPx: 0.8, dish: false, innerOp: 0.7 };
const E = Object.assign({}, DETAIL64, { px: 128, wo: DETAIL64.wo * 2, wi: DETAIL64.wi * 2, wp: DETAIL64.wp * 2, rim: 'glow', dip: 64, glowDip: 1.2 });
for (let i = 0; i < 72; i++)
  fs.writeFileSync(path.join(OUT, 'empty64', `frame_${String(i).padStart(3, '0')}.png`), png(svg(E, 0.1 + 2 * Math.PI * i / 72)));
fs.writeFileSync(path.join(OUT, 'icon-64-glow-svg.svg'), svg(E));
console.log('done', OUT);
