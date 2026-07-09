/* ---------------- PRNG (mulberry32) + helpers ---------------- */
function makeRng(seed) {
  var s = (seed >>> 0) || 1;
  function next() {
    s |= 0; s = (s + 0x6D2B79F5) | 0;
    var t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  }
  return {
    next: next,
    range: function (a, b) { return a + (b - a) * next(); },
    int: function (n) { return Math.floor(next() * n); },
    chance: function (p) { return next() < p; },
    angle: function () { return next() * Math.PI * 2; },
  };
}

function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
function clamp01(v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
function lerp(a, b, t) { return a + (b - a) * t; }
function dist(ax, ay, bx, by) { var dx = bx - ax, dy = by - ay; return Math.sqrt(dx * dx + dy * dy); }
function len(x, y) { return Math.sqrt(x * x + y * y); }
function normAngle(a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

function deepClone(o) {
  if (o === null || typeof o !== 'object') return o;
  if (Array.isArray(o)) { var a = []; for (var i = 0; i < o.length; i++) a.push(deepClone(o[i])); return a; }
  var r = {}; for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) r[k] = deepClone(o[k]);
  return r;
}
function deepMerge(base, over) {
  if (over === null || typeof over !== 'object' || Array.isArray(over)) return deepClone(over);
  var r = (base === null || typeof base !== 'object' || Array.isArray(base)) ? {} : base;
  for (var k in over) if (Object.prototype.hasOwnProperty.call(over, k)) r[k] = deepMerge(r[k], over[k]);
  return r;
}

/* Segment (ax,ay)-(bx,by) vs circle (cx,cy,r): true if intersecting. */
function segCircleHit(ax, ay, bx, by, cx, cy, r) {
  var dx = bx - ax, dy = by - ay;
  var fx = ax - cx, fy = ay - cy;
  var a = dx * dx + dy * dy;
  if (a < 1e-9) return fx * fx + fy * fy <= r * r;
  var t = clamp(-(fx * dx + fy * dy) / a, 0, 1);
  var px = ax + t * dx - cx, py = ay + t * dy - cy;
  return px * px + py * py <= r * r;
}
/* ---------------- Terrain generation (one mixed map: lanes + clusters) ---------------- */
