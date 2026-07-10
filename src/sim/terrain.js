/* Praedra sim module: terrain — generation, contours, spatial grids, LOS — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- Terrain generation (one mixed map: lanes + clusters) ---------------- */
function generateTerrain(state, rng) {
  var cfg = state.config, T = cfg.terrain, W = cfg.arena.w, H = cfg.arena.h;
  var density = clamp01(cfg.terrainDensity);
  var cx = W / 2, cy = H / 2;

  // Randomized spawn axis per seed so terrainDensity is the isolated variable (§8).
  var th = rng.angle();
  var rx = (W / 2 - T.edgeMargin - T.spawnClearRadius) * 0.92;
  var ry = (H / 2 - T.edgeMargin - T.spawnClearRadius) * 0.92;
  state.spawnA = { x: cx - rx * Math.cos(th), y: cy - ry * Math.sin(th) };
  state.spawnB = { x: cx + rx * Math.cos(th), y: cy + ry * Math.sin(th) };

  var rocks = [];
  function clearOfSpawns(x, y, r) {
    return dist(x, y, state.spawnA.x, state.spawnA.y) > T.spawnClearRadius + r &&
           dist(x, y, state.spawnB.x, state.spawnB.y) > T.spawnClearRadius + r;
  }
  function tryPlace(x, y, r) {
    if (x < T.edgeMargin + r || x > W - T.edgeMargin - r || y < T.edgeMargin + r || y > H - T.edgeMargin - r) return false;
    if (!clearOfSpawns(x, y, r)) return false;
    for (var i = 0; i < rocks.length; i++) {
      var o = rocks[i];
      if (dist(x, y, o.x, o.y) < (r + o.r) * 0.82) return false; // slight overlap ok (clumpy clusters)
    }
    rocks.push({ x: x, y: y, r: r });
    return true;
  }
  function rockRadius() { var t = rng.next(); return lerp(T.asteroidRadius[0], T.asteroidRadius[1], t * t); }

  // --- THE TITAN first: one colossal rock, ~5x a BIG one. Guaranteed every seed.
  //     It roams anywhere — including CUT by the arena edge — as long as at least
  //     65% of its disc stays in the playable zone, so it always shapes the fight. ---
  var bigs = [];
  var tR = lerp(T.titanRadius[0], T.titanRadius[1], rng.next());
  // deterministic 24x24 disc sampler (no rng draws): fraction of the disc in-arena
  function discInsideFrac(x, y, r) {
    var inside = 0, total = 0;
    for (var si = 0; si < 24; si++) for (var sj = 0; sj < 24; sj++) {
      var px = -1 + (si + 0.5) / 12, py = -1 + (sj + 0.5) / 12;
      if (px * px + py * py > 1) continue;
      total++;
      var sx = x + px * r, sy = y + py * r;
      if (sx >= 0 && sx <= W && sy >= 0 && sy <= H) inside++;
    }
    return inside / total;
  }
  var tPlaced = false;
  for (var tAtt = 0; tAtt < 120 && !tPlaced; tAtt++) {
    // a single straight edge cuts <=35% of the disc when the centre is >=0.24r
    // inside it; the sampler is the exact gate (it also catches corner double-cuts)
    var tx = rng.range(tR * 0.24, W - tR * 0.24);
    var ty = rng.range(tR * 0.24, H - tR * 0.24);
    if (discInsideFrac(tx, ty, tR) < 0.66) continue; // gate above 65% for sampler slack
    if (dist(tx, ty, state.spawnA.x, state.spawnA.y) < T.titanSpawnClear + tR) continue;
    if (dist(tx, ty, state.spawnB.x, state.spawnB.y) < T.titanSpawnClear + tR) continue;
    bigs.push({ x: tx, y: ty, r: tR });
    rocks.push({ x: tx, y: ty, r: tR });
    tPlaced = true;
  }
  if (!tPlaced) { // near-unreachable fallback (120 tries on a mostly-open arena): the
    // centre clears both spawn CLEAR zones on any axis, though a worst-case vertical
    // spawn axis can shave the extra titanSpawnClear comfort margin
    bigs.push({ x: cx, y: cy, r: tR });
    rocks.push({ x: cx, y: cy, r: tR });
  }

  // --- BIG asteroids next: they own the mid-scale, everything else flows around them.
  //     Every seed gets 1..bigCountMax — one lone monster or a spread of wells. ---
  var nBig = 1 + rng.int(Math.max(1, T.bigCountMax | 0));
  var nBigPlaced = 0;
  for (var bg = 0; bg < nBig; bg++) {
    var tB = rng.next();
    if (nBig === 1) tB = 0.55 + 0.45 * tB;   // a lone monster leans monstrous
    var bR = lerp(T.bigRadius[0], T.bigRadius[1], tB);
    var placed = false;
    for (var bAtt = 0; bAtt < 60 && !placed; bAtt++) {
      var bx = rng.range(T.edgeMargin + bR, W - T.edgeMargin - bR);
      var by = rng.range(T.edgeMargin + bR, H - T.edgeMargin - bR);
      if (dist(bx, by, state.spawnA.x, state.spawnA.y) < T.bigSpawnClear + bR) continue;
      if (dist(bx, by, state.spawnB.x, state.spawnB.y) < T.bigSpawnClear + bR) continue;
      var clearB = true;
      for (var bj = 0; bj < bigs.length; bj++) {
        // pairs with the titan use an absolute gap instead of 1.35x the radii sum
        // (which would forbid most of the arena): outside the titan's rock-wake
        // shell, so a placed BIG anchors the layout instead of sliding into it
        var need = bigs[bj].r > T.bigRadius[1] * 1.5
          ? bigs[bj].r + bR + state.config.gravity.rockWakeShell + 40
          : (bR + bigs[bj].r) * T.bigSeparation;
        if (dist(bx, by, bigs[bj].x, bigs[bj].y) < need) { clearB = false; break; }
      }
      if (!clearB) continue;
      bigs.push({ x: bx, y: by, r: bR });
      rocks.push({ x: bx, y: by, r: bR });
      placed = true; nBigPlaced++;
    }
    // hard guarantee: at least one BIG per seed besides the titan. Quarter points all
    // clear both spawns; pick the first that also clears the titan and other bigs.
    if (!placed && nBigPlaced === 0) {
      var cand = [[W * 0.28, H * 0.28], [W * 0.72, H * 0.28], [W * 0.28, H * 0.72], [W * 0.72, H * 0.72], [cx, cy]];
      for (var fc = 0; fc < cand.length; fc++) {
        var fx = cand[fc][0], fy = cand[fc][1], okF = true;
        if (dist(fx, fy, state.spawnA.x, state.spawnA.y) < T.bigSpawnClear + bR) okF = false;
        if (dist(fx, fy, state.spawnB.x, state.spawnB.y) < T.bigSpawnClear + bR) okF = false;
        for (var fj = 0; fj < bigs.length && okF; fj++) {
          var needF = bigs[fj].r > T.bigRadius[1] * 1.5
            ? bigs[fj].r + bR + state.config.gravity.rockWakeShell + 40
            : bR + bigs[fj].r + 40;
          if (dist(fx, fy, bigs[fj].x, bigs[fj].y) < needF) okF = false;
        }
        if (okF) {
          bigs.push({ x: fx, y: fy, r: bR });
          rocks.push({ x: fx, y: fy, r: bR });
          nBigPlaced++;
          break;
        }
      }
    }
  }

  // rock COUNTS were tuned for the reference arena area; a viewport-shaped arena (the app
  // matches arena aspect to the window) scales them so terrain DENSITY feels identical
  var areaMult = T.countRefArea ? (W * H) / T.countRefArea : 1;
  var nClusters = Math.round(lerp(T.clusterCountMin, T.clusterCountMax, density) * areaMult);
  for (var c = 0; c < nClusters; c++) {
    var R = rng.range(T.clusterRadius[0], T.clusterRadius[1]);
    var ccx = 0, ccy = 0, ok = false;
    for (var a = 0; a < 40 && !ok; a++) {
      ccx = rng.range(T.edgeMargin + R * 0.3, W - T.edgeMargin - R * 0.3);
      ccy = rng.range(T.edgeMargin + R * 0.3, H - T.edgeMargin - R * 0.3);
      ok = dist(ccx, ccy, state.spawnA.x, state.spawnA.y) > T.spawnClearRadius + R * 0.35 &&
           dist(ccx, ccy, state.spawnB.x, state.spawnB.y) > T.spawnClearRadius + R * 0.35;
    }
    if (!ok) continue;
    var n = Math.round(T.clusterFillPerArea * Math.PI * R * R);
    for (var k = 0; k < n; k++) {
      for (var att = 0; att < 8; att++) {
        var rr = R * Math.sqrt(rng.next()), ang = rng.angle();
        if (tryPlace(ccx + rr * Math.cos(ang), ccy + rr * Math.sin(ang), rockRadius())) break;
      }
    }
  }
  var nSparse = Math.round(lerp(T.sparseRockMin, T.sparseRockMax, density) * areaMult);
  for (var s = 0; s < nSparse; s++) {
    for (var att2 = 0; att2 < 12; att2++) {
      if (tryPlace(rng.range(T.edgeMargin, W - T.edgeMargin), rng.range(T.edgeMargin, H - T.edgeMargin), rockRadius())) break;
    }
  }
  for (var i2 = 0; i2 < rocks.length; i2++) state.asteroids.push(makeAsteroid(state, rocks[i2].x, rocks[i2].y, rocks[i2].r));
}

function makeAsteroid(state, x, y, r) {
  var cfg = state.config;
  var hp = cfg.asteroidHP * (r / cfg.asteroidHPRefRadius) * (r / cfg.asteroidHPRefRadius);
  // lumpy deterministic outline. Three harmonics: continent lobes (k1), ridge-scale
  // relief (k2), crater-scale detail (k3); fine per-vertex grit on top. Vertex count
  // scales with radius — small rocks ~19, BIGs ~50, the titan 130+ — so detail rank
  // follows size rank. The contour is also the CONTACT surface (see contourR): rocks,
  // ships and warheads rest/strike along it, so accretion hugs the lumps, not the
  // bounding circle. LOS, detection and the railgun ray stay circular on r.
  var nv = clamp(Math.round(r * 0.12) + 16, 18, 160), shape = [];
  var k1 = 2 + state.rng.int(3), k2 = 5 + state.rng.int(4), k3 = 11 + state.rng.int(7);
  var p1 = state.rng.angle(), p2 = state.rng.angle(), p3 = state.rng.angle();
  var a1 = state.rng.range(0.06, 0.13), a2 = state.rng.range(0.03, 0.07);
  var a3 = state.rng.range(0.015, 0.035);
  for (var i = 0; i < nv; i++) {
    var th = i * 2 * Math.PI / nv;
    shape.push(clamp(1 + a1 * Math.sin(k1 * th + p1) + a2 * Math.sin(k2 * th + p2)
                       + a3 * Math.sin(k3 * th + p3)
                       + state.rng.range(-0.04, 0.04), 0.7, 1.22));
  }
  // idle tumble: id-derived (Knuth hash -> [-1,1)), NOT an rng draw — adding a draw here
  // would shift every later sample and reshape all existing seeds/scenarios
  var id = state.nextId++;
  // pebbles only: a tumbling contour UNDER a rested pile would excavate it (settled
  // pairs are never collision-resolved), so pile anchors (mid/BIG/titan) hold still
  var spins = r < (cfg.debris.idleSpinMaxRadius || 0);
  var spin = spins ? ((((id * 2654435761) >>> 0) % 2000) / 1000 - 1) * (cfg.debris.idleSpin || 0) : 0;
  return { id: id, x: x, y: y, r: r, hp: hp, maxHp: hp, vx: 0, vy: 0,
           rot: state.rng.angle(), rotVel: spin, shape: shape, alive: true, moving: false };
}
/* Effective surface radius of rock o toward point (x,y): linear interpolation of the
   shape polygon, matching exactly what drawAsteroid renders (including tumble rot).
   This is the CONTACT surface — accretion piles, hull bonks and warhead strikes all
   land on the lumps, not on an invisible bounding circle. */
function contourR(o, x, y) {
  var n = o.shape.length;
  var a = Math.atan2(y - o.y, x - o.x) - o.rot;
  a -= Math.floor(a / (2 * Math.PI)) * 2 * Math.PI; // wrap to [0, 2PI)
  var f = a * n / (2 * Math.PI);
  var i = Math.floor(f);
  if (i >= n) i = n - 1;
  return o.r * lerp(o.shape[i], o.shape[(i + 1) % n], f - i);
}
/* Cached pair contact distance: contourR(p toward q) + contourR(q toward p), memoized
   a few ticks. Gravity-fed accretion fields evaluate thousands of identical near-pairs
   every tick and contourR's atan2 dominated the profile. Within the TTL the relative
   bearing of a creeping pair drifts by ~a pixel, so the cached sum is within noise of
   exact (and it converges to exact once a pair comes to rest). Cache is wiped
   periodically in stepMatch so dead pairs don't accumulate. */
function pairContact(state, p, q) {
  var key = p.id < q.id ? p.id * 10000000 + q.id : q.id * 10000000 + p.id; // 1e7 pack: ids stay
                                                  // ~1e2-1e3 in real matches (probed), 1e7 keeps the
                                                  // pack injective even under pathological overrides
  var e = state.contactCache[key];
  if (e !== undefined && state.tick - e.t < 4) return e.rr;
  var rr = contourR(p, q.x, q.y) + contourR(q, p.x, p.y);
  state.contactCache[key] = { t: state.tick, rr: rr };
  return rr;
}

/* ---------------- Spatial grid for rock queries (performance only, no gameplay) ----------------
   The STATIC grid holds SETTLED rocks only and is rebuilt when a rock's settled/moving
   state flips (state.gridDirty). Moving rocks live in state.movers AND a second, coarse
   MOVER grid (state.moverGrid) rebuilt every tick — cheap, movers are the short list.
   The mover grid matters because now that gravity wakes whole fields (hundreds of rocks
   creeping into the wells at once), the old linear movers scan per query turned
   O(movers^2) in dense regions and blew the per-tick budget. */
var GRID_CELL = 300;
function rebuildGrid(state) {
  var g = state.grid;
  if (!g) {
    g = state.grid = {
      cw: Math.max(1, Math.ceil(state.config.arena.w / GRID_CELL)),
      ch: Math.max(1, Math.ceil(state.config.arena.h / GRID_CELL)),
      cells: [], stamp: 1,
    };
    for (var i = 0; i < g.cw * g.ch; i++) g.cells.push([]);
  }
  for (var c = 0; c < g.cells.length; c++) g.cells[c].length = 0;
  var A = state.asteroids;
  for (var r = 0; r < A.length; r++) {
    var o = A[r];
    if (!o.alive || o.moving) continue;   // movers are scanned via state.movers instead
    var re = o.r * 1.22; // stamp by the MAX CONTOUR radius: contact now reaches past r,
                         // and a titan bulge (~0.22*1440px) can exceed the cell slack
    var x0 = Math.max(0, Math.floor((o.x - re) / GRID_CELL)), x1 = Math.min(g.cw - 1, Math.floor((o.x + re) / GRID_CELL));
    var y0 = Math.max(0, Math.floor((o.y - re) / GRID_CELL)), y1 = Math.min(g.ch - 1, Math.floor((o.y + re) / GRID_CELL));
    for (var gy = y0; gy <= y1; gy++) for (var gx = x0; gx <= x1; gx++) g.cells[gy * g.cw + gx].push(o);
  }
}
/* Movers bucket grid: rebuilt EVERY tick from state.movers (movers change position
   constantly, so per-tick rebuild beats incremental upkeep). Queried by rocksNearSeg
   alongside the static grid. Bboxes get +8px slack so a mover's sub-cell drift within
   the tick can't slip a contact past a query. */
function moverGridInsert(state, o) {
  var mg = state.moverGrid;
  if (!mg) return;
  var re = o.r * 1.22 + 8; // max contour radius + a tick of drift
  var x0 = Math.max(0, Math.floor((o.x - re) / GRID_CELL)), x1 = Math.min(mg.cw - 1, Math.floor((o.x + re) / GRID_CELL));
  var y0 = Math.max(0, Math.floor((o.y - re) / GRID_CELL)), y1 = Math.min(mg.ch - 1, Math.floor((o.y + re) / GRID_CELL));
  for (var gy = y0; gy <= y1; gy++) for (var gx = x0; gx <= x1; gx++) mg.cells[gy * mg.cw + gx].push(o);
}
function rebuildMoverGrid(state) {
  var mg = state.moverGrid;
  if (!mg) {
    mg = state.moverGrid = {
      cw: Math.max(1, Math.ceil(state.config.arena.w / GRID_CELL)),
      ch: Math.max(1, Math.ceil(state.config.arena.h / GRID_CELL)),
      cells: [],
    };
    for (var i = 0; i < mg.cw * mg.ch; i++) mg.cells.push([]);
  }
  var cells = mg.cells, cw = mg.cw, ch = mg.ch;
  for (var c = 0; c < cells.length; c++) cells[c].length = 0;
  // inlined insert (this runs every tick over every mover — call/property overhead adds up)
  var mv = state.movers;
  for (var m = 0; m < mv.length; m++) {
    var o = mv[m];
    var re = o.r * 1.22 + 8;
    var x0 = ((o.x - re) / GRID_CELL) | 0; if (x0 < 0) x0 = 0;
    var x1 = ((o.x + re) / GRID_CELL) | 0; if (x1 > cw - 1) x1 = cw - 1;
    var y0 = ((o.y - re) / GRID_CELL) | 0; if (y0 < 0) y0 = 0;
    var y1 = ((o.y + re) / GRID_CELL) | 0; if (y1 > ch - 1) y1 = ch - 1;
    for (var gy = y0; gy <= y1; gy++) for (var gx = x0; gx <= x1; gx++) cells[gy * cw + gx].push(o);
  }
}
/* A rock that starts (or resumes) moving leaves the static grid: track it and mark
   the grid for rebuild. Call every time code flips o.moving from false to true.
   Also stamped into the mover grid immediately so a fragment spawned or a rock woken
   MID-tick is visible to queries this same tick (load-bearing for splitAsteroid). */
function rockWoke(state, o) {
  o.everMoved = true;   // anchor immunity is for never-moved terrain only (see rockAnchorRadius)
  state.gridDirty = true;
  if (state.movers) state.movers.push(o);
  moverGridInsert(state, o);
}
/* Visit live rocks near the (padded) segment bbox; fn returning true stops the walk. */
function rocksNearSeg(state, ax, ay, bx, by, pad, fn) {
  var g = state.grid;
  if (!g) {
    var A = state.asteroids;
    for (var i = 0; i < A.length; i++) { var o = A[i]; if (o.alive && fn(o)) return; }
    return;
  }
  var xmin = Math.min(ax, bx) - pad, xmax = Math.max(ax, bx) + pad;
  var ymin = Math.min(ay, by) - pad, ymax = Math.max(ay, by) + pad;
  var stamp = ++g.stamp;
  // moving rocks first, via the per-tick mover grid (linear-scan fallback only for
  // states that have never stepped). Whole creeping fields made the flat scan O(n^2).
  var mg = state.moverGrid;
  if (mg) {
    var mx0 = Math.max(0, Math.floor(xmin / GRID_CELL));
    var mx1 = Math.min(mg.cw - 1, Math.floor(xmax / GRID_CELL));
    var my0 = Math.max(0, Math.floor(ymin / GRID_CELL));
    var my1 = Math.min(mg.ch - 1, Math.floor(ymax / GRID_CELL));
    for (var mgy = my0; mgy <= my1; mgy++) for (var mgx = mx0; mgx <= mx1; mgx++) {
      var mcell = mg.cells[mgy * mg.cw + mgx];
      for (var mk = 0; mk < mcell.length; mk++) {
        var mo = mcell[mk];
        if (!mo.alive || mo.gridStamp === stamp) continue;
        mo.gridStamp = stamp;
        if (fn(mo)) return;
      }
    }
  } else {
    var mv = state.movers;
    if (mv) for (var m = 0; m < mv.length; m++) {
      var mo2 = mv[m];
      if (!mo2.alive || mo2.gridStamp === stamp) continue;
      var mre = mo2.r * 1.22; // bbox by max contour radius (same reason as rebuildGrid)
      if (mo2.x + mre < xmin || mo2.x - mre > xmax || mo2.y + mre < ymin || mo2.y - mre > ymax) continue;
      mo2.gridStamp = stamp;
      if (fn(mo2)) return;
    }
  }
  var x0 = Math.max(0, Math.floor(xmin / GRID_CELL));
  var x1 = Math.min(g.cw - 1, Math.floor(xmax / GRID_CELL));
  var y0 = Math.max(0, Math.floor(ymin / GRID_CELL));
  var y1 = Math.min(g.ch - 1, Math.floor(ymax / GRID_CELL));
  for (var gy = y0; gy <= y1; gy++) for (var gx = x0; gx <= x1; gx++) {
    var cell = g.cells[gy * g.cw + gx];
    for (var k = 0; k < cell.length; k++) {
      var o = cell[k];
      // a settled rock killed mid-tick lingers in the cells until the next rebuild —
      // without this check its corpse still blocks shots and shoves its own fragments
      if (!o.alive || o.gridStamp === stamp) continue;
      o.gridStamp = stamp;
      if (fn(o)) return;
    }
  }
}

/* ---------------- Line of sight (solid rocks block shots; ships don't) ---------------- */
function losClear(state, ax, ay, bx, by) {
  var clear = true;
  rocksNearSeg(state, ax, ay, bx, by, 92, function (o) {
    if (segCircleHit(ax, ay, bx, by, o.x, o.y, o.r)) { clear = false; return true; }
    return false;
  });
  return clear;
}
/* Cached per-ship-pair LOS, refreshed every 6 ticks. */
function losShips(state, a, b) {
  var key = a.id * 4096 + b.id;
  var e = state.losCache[key];
  if (e !== undefined && state.tick - e.t < 6) return e.v;
  var v = losClear(state, a.x, a.y, b.x, b.y);
  state.losCache[key] = { t: state.tick, v: v };
  return v;
}
/* Nearest ship (any team, not the shooter) whose hull intersects the ray. Friendly
   fire is ALWAYS on: whatever is in the way eats the shot. */
function firstShipOnRay(state, shooter, ax, ay, bx, by) {
  var best = null, bestD2 = Infinity;
  var ships = state.ships;
  for (var i = 0; i < ships.length; i++) {
    var sh = ships[i];
    if (!sh.alive || sh.id === shooter.id) continue;
    if (segCircleHit(ax, ay, bx, by, sh.x, sh.y, sh.def.radius)) {
      var d2 = (sh.x - ax) * (sh.x - ax) + (sh.y - ay) * (sh.y - ay);
      if (d2 < bestD2) { bestD2 = d2; best = sh; }
    }
  }
  return best;
}

/* First asteroid hit by ray a->b (for railgun blocking/overshoot); returns rock or null. */
function firstRockOnRay(state, ax, ay, bx, by) {
  var best = null, bestD2 = Infinity;
  rocksNearSeg(state, ax, ay, bx, by, 92, function (o) {
    if (segCircleHit(ax, ay, bx, by, o.x, o.y, o.r)) {
      var d2 = (o.x - ax) * (o.x - ax) + (o.y - ay) * (o.y - ay);
      if (d2 < bestD2) { bestD2 = d2; best = o; }
    }
    return false;
  });
  return best;
}

