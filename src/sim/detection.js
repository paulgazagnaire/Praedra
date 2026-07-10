/* Praedra sim module: detection — LOS + signature sensing, team picture — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- Detection (LOS + signature; the plume gives you away) ---------------- */
function visibleRange(state, ship) {
  var D = state.config.detection;
  return ship.def.signature * lerp(D.thrustMultMin, D.thrustMultMax, ship.throttle || 0);
}
/* Team-shared sensor picture: an enemy is detected if ANY friendly has LOS to it
   within its (thrust-modulated) signature range. Sweeps every few ticks. */
function computeDetection(state) {
  var D = state.config.detection;
  if (state.tick % D.checkEvery !== 1 && state.detReady) return;
  state.detReady = true;
  var teams = [['A', state.aliveA, state.aliveB], ['B', state.aliveB, state.aliveA]];
  for (var t = 0; t < 2; t++) {
    var team = teams[t][0], own = teams[t][1], foes = teams[t][2];
    var det = [];
    for (var e = 0; e < foes.length; e++) {
      var foe = foes[e];
      var vr = visibleRange(state, foe);
      for (var o = 0; o < own.length; o++) {
        var me = own[o];
        if (dist(me.x, me.y, foe.x, foe.y) <= vr && losShips(state, me, foe)) {
          det.push(foe);
          state.lastSeenShip[foe.id] = { x: foe.x, y: foe.y, t: state.time };
          state.lastContact[team] = { x: foe.x, y: foe.y, t: state.time };
          break;
        }
      }
    }
    if (team === 'A') state.detA = det; else state.detB = det;
  }
}
function isDetectedBy(state, team, ship) {
  var det = team === 'A' ? state.detA : state.detB;
  for (var i = 0; i < det.length; i++) if (det[i].id === ship.id) return true;
  return false;
}
/* Where a ship with no live contacts should look: recent memory, else the enemy
   fleet's rough centroid (strategic picture, not targeting — it gates no weapon).
   The old enemy-SPAWN fallback was equally omniscient but pointed at where the
   enemy USED to be; on titan-scale maps that turned endgames into six-minute
   hide-and-seek around the monster rock's LOS shadow and ran out the clock. */
function huntPoint(state, ship) {
  var D = state.config.detection;
  var lc = state.lastContact[ship.team];
  if (lc && state.time - lc.t < D.memorySeconds * 2.5) return lc;
  var foes = ship.team === 'A' ? state.aliveB : state.aliveA;
  if (foes.length) {
    var cx = 0, cy = 0;
    for (var i = 0; i < foes.length; i++) { cx += foes[i].x; cy += foes[i].y; }
    return { x: cx / foes.length, y: cy / foes.length };
  }
  return ship.team === 'A' ? state.spawnB : state.spawnA;
}
/* Route a hunt goal AROUND titan-scale rocks: give the autopilot a flank point far
   outside the well and its accretion shell instead of a goal in the rock's shadow.
   Threading the rim — deep in gravity, through accreted pebbles, in an escort
   scrum — parked whole fleets against the titan for entire matches. */
/* Sum of radii of transit-relevant rocks (pushableRockRadius..rockClearMaxRadius) whose
   inflated disc intersects the corridor a->b — the field-density metric the capital detour
   scores. Pebbles (hull-shoved) and titans (huge-rock flank handles them) are excluded. */
function corridorClutter(state, ax, ay, bx, by, ship) {
  var AI = state.config.ai, C = state.config.collision;
  var infl = ship.def.radius + AI.avoidMargin * (ship.def.avoidMult || 1);
  var sum = 0;
  rocksNearSeg(state, ax, ay, bx, by, 92 + 900, function (o) {
    if (o.r < C.pushableRockRadius || o.r > AI.rockClearMaxRadius) return false;
    if (segCircleHit(ax, ay, bx, by, o.x, o.y, o.r + infl)) sum += o.r;
    return false;
  });
  return sum;
}

function routeAround(state, ship, pt) {
  var AI = state.config.ai, T = state.config.terrain, C = state.config.collision;
  var W = state.config.arena.w, H = state.config.arena.h;
  var infl = ship.def.radius + AI.avoidMargin * (ship.def.avoidMult || 1);
  // one segment scan: find the first titan-scale blocker AND accumulate straight-lane clutter
  // (the clutter sum only matters when there is NO huge blocker, and the scan then completes)
  var huge = null, straightC = 0;
  rocksNearSeg(state, ship.x, ship.y, pt.x, pt.y, 92 + 900, function (o) {
    if (!huge && o.r >= C.pushableRockRadius && o.r <= AI.rockClearMaxRadius &&
        segCircleHit(ship.x, ship.y, pt.x, pt.y, o.x, o.y, o.r + infl)) straightC += o.r;
    if (o.r < AI.hugeRockRadius) return false;
    if (segCircleHit(ship.x, ship.y, pt.x, pt.y, o.x, o.y, o.r + 700)) { huge = o; return true; }
    return false;
  });
  if (huge) {
    // existing titan/huge-rock behavior: rim-flank far outside the well (kept fully intact)
    var ring = huge.r + 900;
    var angS = Math.atan2(ship.y - huge.y, ship.x - huge.x);
    var angG = Math.atan2(pt.y - huge.y, pt.x - huge.x);
    var side = normAngle(angG - angS) > 0 ? 1 : -1;
    var na = angS + side * clamp(700 / ring, 0.3, 0.9);
    return { x: clamp(huge.x + Math.cos(na) * ring, T.edgeMargin, W - T.edgeMargin),
             y: clamp(huge.y + Math.sin(na) * ring, T.edgeMargin, H - T.edgeMargin) };
  }
  // --- field-density detour: capitals round a compact field, transit an un-detourable wall ---
  if (!isCapital(ship)) return pt; // lights keep the titan-only routing above
  // stagger + cache (budget + determinism): recompute on this ship's tick phase, reuse between
  var fresh = ship.ai.detourPt && (state.tick - (ship.ai.detourT || -999)) < AI.clutterRefreshTicks &&
              Math.abs((ship.ai.detourForX || 0) - pt.x) < 60 && Math.abs((ship.ai.detourForY || 0) - pt.y) < 60;
  if (state.tick % AI.clutterRefreshTicks !== ship.id % AI.clutterRefreshTicks && fresh)
    return ship.ai.detourPt;
  ship.ai.detourT = state.tick; ship.ai.detourForX = pt.x; ship.ai.detourForY = pt.y;
  if (straightC < AI.clutterDetourThreshold) { ship.ai.detourPt = null; return pt; } // clean lane: direct
  // goal-snug guard: the goal legitimately sits inside the field -> transit, lane-clearing takes over
  var snug = false;
  rocksNearSeg(state, pt.x, pt.y, pt.x, pt.y, AI.rockClearMaxRadius + infl + 40, function (o) {
    if (o.r < C.pushableRockRadius || o.r > AI.rockClearMaxRadius) return false;
    if (dist(pt.x, pt.y, o.x, o.y) < ship.def.radius + o.r + AI.avoidMargin) { snug = true; return true; }
    return false;
  });
  if (snug) { ship.ai.detourPt = null; return pt; }
  // two-leg flank scoring: midpoint + perpendicular offset, both sides, penalised per px of detour
  var mx = (ship.x + pt.x) / 2, my = (ship.y + pt.y) / 2;
  var dx = pt.x - ship.x, dy = pt.y - ship.y, dl = len(dx, dy) || 1;
  var perpx = -dy / dl, perpy = dx / dl;
  var best = null, bestScore = straightC, bestLeg = Infinity, offs = AI.clutterDetourOffsets;
  for (var s = 0; s < 2; s++) {
    var sgn = s === 0 ? 1 : -1;
    for (var k = 0; k < offs.length; k++) {
      var off = offs[k];
      var fx = clamp(mx + perpx * sgn * off, T.edgeMargin, W - T.edgeMargin);
      var fy = clamp(my + perpy * sgn * off, T.edgeMargin, H - T.edgeMargin);
      var leg = corridorClutter(state, ship.x, ship.y, fx, fy, ship) +
                corridorClutter(state, fx, fy, pt.x, pt.y, ship);
      var score = leg + off * AI.clutterDetourOffsetCost;
      if (score < bestScore) { bestScore = score; best = { x: fx, y: fy }; bestLeg = leg; }
    }
  }
  if (best && bestLeg <= AI.clutterDetourGain * straightC) { ship.ai.detourPt = best; return best; }
  ship.ai.detourPt = null; return pt; // wall too costly to round: transit and clear it
}

