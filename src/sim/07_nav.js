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

/* Battleship-only gravity-well skirt (see ai.bbWellSkirtFrac). The uniquely slow BB (turnMax 0.14,
   ~22s flip) cannot out-manoeuvre a gravity well: a source's pull (bounded to shipEscapeCap*accel, but
   with a ~22s reorient it drags the hull in faster than the guns can correct) curls the ponderous ship
   PAST its own nav goal and into the rock, where it grinds to death — the design's flagged entombment,
   and the dominant real-match killer (traced: the titan's 3750px well swallows a BB whose goal sits a
   mere ~1000px away, before routeAround's path-geometry ever sees a blocker). So for the BB, and only
   in its AUTO role AI (ordered move/attackmove/hold untouched -> lane-clear tests unaffected): whenever
   the hull is inside a well AND its goal/path would keep it in the killing core, hand back a rim
   waypoint at the well's skirt radius on the goal side. The BB then rounds the rim; its 3200px guns
   still reach deep into the well from there, so 'skirt the well and shell across it' loses no coverage
   and fits the long-range-artillery identity. Applies to EVERY source (BIGs and the titan) — routeAround
   only reroutes when the straight segment clips a huge rock, which gravity-drag defeats. */
function bbSkirtWell(state, ship, pt) {
  var srcs = state.gravSources;
  if (!srcs || !srcs.length) return pt;
  var AI = state.config.ai, T = state.config.terrain, W = state.config.arena.w, H = state.config.arena.h;
  // Among the wells the BB is INSIDE, pick the one whose surface it is nearest — the immediate grind
  // threat. A slug-slow hull can't be trusted to hold even a wide standoff (traced: dragged in from
  // 1020px of clearance), so the safe radius is out at the well edge where the pull has faded, not a
  // fixed rim — the skirt makes the hull CLIMB outward each tick, leaning toward the goal side.
  var best = null, bestSurf = Infinity, bestReach = 0, bestRing = 0;
  for (var i = 0; i < srcs.length; i++) {
    var o = srcs[i].o;
    var reach = srcs[i].reach;
    var dShip = dist(ship.x, ship.y, o.x, o.y);
    if (dShip > reach) continue;                                      // outside this well: no drag
    var ring = Math.max(o.r * AI.bbWellSkirtFrac, o.r + ship.def.radius + 180); // inner floor (BIG wells)
    // fine only if the goal AND path already clear the core (goal beyond the well on a clean lane)
    if (dist(pt.x, pt.y, o.x, o.y) >= ring &&
        !segCircleHit(ship.x, ship.y, pt.x, pt.y, o.x, o.y, ring)) continue;
    var surf = dShip - o.r;
    if (surf < bestSurf) { bestSurf = surf; best = o; bestReach = reach; bestRing = ring; }
  }
  if (!best) return pt;
  var dShip2 = dist(ship.x, ship.y, best.x, best.y);
  var targetR = clamp(dShip2 + 250, bestRing, bestReach);            // climb OUTWARD toward the well edge
  var angS = Math.atan2(ship.y - best.y, ship.x - best.x);
  var angG = Math.atan2(pt.y - best.y, pt.x - best.x);
  // sticky-side rim-follow (mirrors routeAround's titan handling): COMMIT to one way around for a
  // few seconds and take a real tangential step, so a goal across the well makes the hull
  // circumnavigate the rim toward it instead of dithering radially out and grinding to a halt
  // between the climb-out vector and the goal pull (the observed stuck-grind death).
  var side;
  if (ship.ai.skirtRock === best.id && state.time < (ship.ai.skirtUntil || -1)) side = ship.ai.skirtSide;
  else { side = normAngle(angG - angS) > 0 ? 1 : -1; ship.ai.skirtRock = best.id; ship.ai.skirtSide = side; }
  ship.ai.skirtUntil = state.time + 2.5;
  var na = angS + side * clamp(560 / targetR, 0.3, 0.9);             // orbit toward the goal side
  return { x: clamp(best.x + Math.cos(na) * targetR, T.edgeMargin, W - T.edgeMargin),
           y: clamp(best.y + Math.sin(na) * targetR, T.edgeMargin, H - T.edgeMargin) };
}

