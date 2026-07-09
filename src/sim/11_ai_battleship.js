/* ---------------- Battleship: heavy-rail turrets + role AI ---------------- */
/* Focused fire, deletion-ordered: one primary per tick, ranked battleship > destroyer > frigate,
   nearest within [minRange, maxRange], detected (livingEnemies is the detected set) and LOS-clear.
   Every turret that can BEAR (WWII arcs) slews to it, loads for loadTime, and fires a real slug
   every `cooldown` seconds — a bearing pair puts ~1 slug / 2.5 s on the primary. */
function heavyRailClassRank(cls) { return cls === 'battleship' ? 0 : (cls === 'destroyer' ? 1 : 2); }
function pickHeavyRailPrimary(state, ship, ignoreLos) {
  var HR = state.config.heavyRail;
  var enemies = livingEnemies(state, ship.team); // DETECTED enemies only
  var best = null, bestKey = Infinity;
  for (var i = 0; i < enemies.length; i++) {
    var e = enemies[i];
    if (!heavyRailTrackable(HR, e)) continue;                       // HARD class gate
    var d = dist(ship.x, ship.y, e.x, e.y);
    if (d < HR.minRange || d > HR.maxRange) continue;               // dead zone / range (from centre)
    if (!ignoreLos && !losShips(state, ship, e)) continue;          // LOS gate (skipped for the APPROACH pick)
    var key = heavyRailClassRank(e.cls) * 1e7 + d;                  // big ships first, then nearest
    if (doctrineOf(state, ship.team) === 'v2')                      // v2: finish wounded hulls first
      key -= (1 - e.hp / e.maxHp) * state.config.ai2.focusHpWeight;
    if (key < bestKey) { bestKey = key; best = e; }
  }
  return best;
}
/* Per-turret update, pure/deterministic (slew/arc/load are geometry/index/time only — NO RNG here;
   heavy-rail RNG lives in updateHeavySlugs: the in-flight hit roll, plus the shared asteroid-split
   draws when a slug's rock stop destroys the rock). Called EVERY tick for a
   battleship from every path (aiBattleship / aiPinned / weaponsFree / executeOrder). Priority per
   design decision 4: (a) the ship-level primary; (b) else a fresh ai.rockAim -> demolish that rock;
   (c) else recentre toward the turret's own arc centre.
   WWII arcs: each mount may only train within arcCenters[i] +- arcHalfWidth of the hull heading —
   an out-of-arc target sends the mount to its limit stop where it waits, holding fire.
   Loading: the rails charge (t.load += dt) whenever the turret HOLDS a valid solution, including
   during the breech cooldown, and the slug releases once cool <= 0 AND load >= loadTime. A broken
   solution only dumps the charge after loadGrace seconds (t.lost) — a maneuvering hull points its
   nose along the burn every thrust tick, and a single-tick hard reset would starve the guns. */
function updateTurrets(state, ship, dt) {
  if (!ship.turrets) return;
  var HR = state.config.heavyRail;
  var primary = pickHeavyRailPrimary(state, ship);
  var ra = ship.ai.rockAim;
  var rockFresh = !primary && ra && state.time < ra.until;
  var rock = null;
  if (rockFresh) {
    for (var ri = 0; ri < state.asteroids.length; ri++) {
      var o = state.asteroids[ri];
      if (o.alive && o.id === ra.rockId) { rock = o; break; }
    }
    if (!rock) rockFresh = false;
  }
  for (var i = 0; i < ship.turrets.length; i++) {
    var t = ship.turrets[i];
    t.cool -= dt;
    var arcC = normAngle(ship.heading + ((HR.arcCenters && HR.arcCenters[i]) || 0));
    var mp = turretMountPoint(state, ship, i);
    var want, mode;
    // aim from the MOUNT (the slug's true origin): aiming from the ship centre would build a
    // constant parallax miss of up to the mount offset (~70 px) on abeam targets
    if (primary) { want = Math.atan2(primary.y - mp.y, primary.x - mp.x); mode = 'ship'; }
    else if (rockFresh) { want = Math.atan2(ra.y - mp.y, ra.x - mp.x); mode = 'rock'; }
    else { want = arcC; mode = 'idle'; }
    // arc clamp: slew to the wanted bearing when it bears, else train to the nearest limit stop
    var rel = normAngle(want - arcC);
    var inArc = Math.abs(rel) <= HR.arcHalfWidth;
    var slewTo = inArc ? want : normAngle(arcC + (rel > 0 ? HR.arcHalfWidth : -HR.arcHalfWidth));
    // slew the gun at slewRate; a fast crosser outruns it and never aligns
    t.ang = normAngle(t.ang + clamp(normAngle(slewTo - t.ang), -HR.slewRate * dt, HR.slewRate * dt));
    // hard clamp in hull-relative space: hull yaw must not drag a stationary barrel past its mount limits
    var relAng = normAngle(t.ang - arcC);
    if (relAng > HR.arcHalfWidth) t.ang = normAngle(arcC + HR.arcHalfWidth);
    else if (relAng < -HR.arcHalfWidth) t.ang = normAngle(arcC - HR.arcHalfWidth);
    // full firing-solution gate: target mode, in arc (TRUE bearing), aligned, in range, LOS (ship mode)
    var solution = mode !== 'idle' && inArc && Math.abs(normAngle(want - t.ang)) <= HR.aimTolerance;
    if (solution) {
      var d = mode === 'ship' ? dist(ship.x, ship.y, primary.x, primary.y)
                              : dist(ship.x, ship.y, rock.x, rock.y);
      if (d < HR.minRange || d > HR.maxRange) solution = false;
      else if (mode === 'ship' && !losShips(state, ship, primary)) solution = false;
    }
    if (solution) {
      t.lost = 0;
      if (t.load < HR.loadTime) t.load += dt; // rails charge even while the breech cools
      if (t.cool <= 0 && t.load >= HR.loadTime) {
        launchHeavySlug(state, ship, i);
        t.cool = HR.cooldown;
        t.load = 0;
      }
    } else {
      t.lost += dt;
      if (t.lost > HR.loadGrace) t.load = 0; // solution genuinely lost: the charge dumps
    }
  }
}

function aiBattleship(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, HR = cfg.heavyRail, def = ship.def;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    // no contacts: advance on the hunt point at cruise, skirting BIG wells + clearing rocks as it goes
    var hpn = bbSkirtWell(state, ship, routeAround(state, ship, huntPoint(state, ship)));
    ship.nav = { x: hpn.x, y: hpn.y, arrive: true, speedCap: def.maxCruiseSpeed * 0.8 };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);   // set ai.rockAim before turrets process it this tick
    updateTurrets(state, ship, dt);
    return;
  }
  // navigation anchor: the heavy-rail primary if any, else the nearest capital, else nearest
  var primary = pickHeavyRailPrimary(state, ship);
  var pick = primary ? { ship: primary, d: dist(ship.x, ship.y, primary.x, primary.y) }
                     : (nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null));
  var target = pick.ship, d = pick.d;
  var aim = Math.atan2(target.y - ship.y, target.x - ship.x);

  // light-swarm centroid within ~450px (the only counter to divers besides PD is creeping away)
  var lcx = 0, lcy = 0, ln = 0;
  for (var i = 0; i < enemies.length; i++) {
    var e = enemies[i];
    if (isLight(e) && dist(ship.x, ship.y, e.x, e.y) < 450) { lcx += e.x; lcy += e.y; ln++; }
  }

  if (state.tick % 30 === (ship.id % 30)) ship.ai.anchorBias = openLaneBias(state, ship, target);
  var bias = ship.ai.anchorBias || { x: 0, y: 0 };
  var maxStand = HR.maxRange * HR.standoffFrac; // top of the gun's reach (the huge-range hold)
  var coverRock = null;                          // a rock to blast out of the firing lane (below)

  // each branch sets goalPt + navFace; the final goal is routed through the BIG-well skirt and the
  // titan/field routeAround before it becomes ship.nav (the slow hull dies in wells otherwise).
  var goalPt, navFace = aim;
  if (ship.hp < 0.3 * ship.maxHp && ln > 0) {
    // low and swarmed: crawl toward the friendly fleet centroid (cruise 28 barely helps — intended)
    var own = ship.team === 'A' ? state.aliveA : state.aliveB;
    var fx = 0, fy = 0, fn = 0;
    for (var k = 0; k < own.length; k++) if (own[k].id !== ship.id) { fx += own[k].x; fy += own[k].y; fn++; }
    goalPt = fn ? { x: fx / fn, y: fy / fn } : huntPoint(state, ship);
    ship.ai.noLos = 0;
  } else if (ln > 0) {
    // creep directly away from the diver centroid to buy PD reaction time; keep them on the nose
    var cx = lcx / ln, cy = lcy / ln, dc = dist(ship.x, ship.y, cx, cy) || 1;
    var ux = (ship.x - cx) / dc, uy = (ship.y - cy) / dc;
    goalPt = { x: ship.x + ux * 300 + bias.x, y: ship.y + uy * 300 + bias.y };
    ship.ai.noLos = 0;
  } else {
    // ENGAGEMENT-AWARE STANDOFF. The real-match failure was parking at maxRange*0.85 (~2720) where
    // the slow BB fell to the rear of its own formation and stood off past terrain LOS, gun idle. So:
    //  - HAS a firing primary  -> hold at its range (never retreat out of a shot), capped at reach;
    //  - detected heavy target in reach but LANE BLOCKED -> close to standoffMin AND, after
    //    rockShootSeconds, flag the plugging rock for the turrets to demolish (mirrors aiDestroyer's
    //    noLos cover-clear — the turrets already out-gun any cover, so advancing + shooting through
    //    is on-theme). blastCoverNearGhosts/clearTransitLane both skip this case, so it's handled here;
    //  - detected capital out of reach -> close to acquire; only distant lights -> keep artillery spacing.
    var navT = target, navD = d, stand = maxStand;
    if (primary) {
      stand = clamp(d, HR.standoffMin, maxStand);
      ship.ai.noLos = 0;
    } else {
      var approach = pickHeavyRailPrimary(state, ship, true); // nearest detected trackable in reach, LOS ignored
      if (approach) {
        navT = approach; navD = dist(ship.x, ship.y, approach.x, approach.y);
        stand = HR.standoffMin;
        ship.ai.noLos = (ship.ai.noLos || 0) + dt;
        if (ship.ai.noLos > AI.rockShootSeconds)
          coverRock = firstRockOnRay(state, ship.x, ship.y, approach.x, approach.y);
      } else if (isCapital(target)) {
        stand = HR.standoffMin; ship.ai.noLos = 0;
      } else {
        ship.ai.noLos = 0;
      }
    }
    navFace = Math.atan2(navT.y - ship.y, navT.x - ship.x);
    var uxn = (navT.x - ship.x) / (navD || 1), uyn = (navT.y - ship.y) / (navD || 1);
    goalPt = { x: navT.x - uxn * stand + bias.x, y: navT.y - uyn * stand + bias.y };
  }
  var g = bbSkirtWell(state, ship, routeAround(state, ship, goalPt));
  ship.nav = { x: g.x, y: g.y, arrive: true, speedCap: def.maxCruiseSpeed, face: navFace };
  blastCoverNearGhosts(state, ship, dt); // sets ai.rockAim on a rock hiding a ghost (turrets slew to it)
  if (coverRock) // the approach-lane blocker takes precedence: turrets shoot through to the heavy target
    ship.ai.rockAim = { x: coverRock.x, y: coverRock.y, rockId: coverRock.id, until: state.time + AI.rockShootSeconds };
  updateTurrets(state, ship, dt);        // turrets fire every tick regardless of nav state
}


/* ================= Battleship v2 (veteran doctrine) ================= */
/* Broadside discipline: when the guns hold a solution, turn the BEAM to the target —
   arcCenters [0,0,pi] +- 2.36 means a beam-on hull bears ALL THREE turrets where a
   bow-on hull bears two (WWII battle-line doctrine). Hysteresis keeps the ponderous
   hull (turnMax 0.14) from flip-flopping between beams mid-slew. */
function bbBroadsideFace(state, ship, aim) {
  var s1 = normAngle(aim + Math.PI / 2), s2 = normAngle(aim - Math.PI / 2);
  var e1 = Math.abs(normAngle(s1 - ship.heading)), e2 = Math.abs(normAngle(s2 - ship.heading));
  var side = ship.ai.bsSide;
  if (side !== 1 && side !== -1) side = e1 <= e2 ? 1 : -1;
  else if (side === 1 && e2 + 0.6 < e1) side = -1;
  else if (side === -1 && e1 + 0.6 < e2) side = 1;
  ship.ai.bsSide = side;
  return side === 1 ? s1 : s2;
}

/* Min-gap field gate: the battleship NEVER threads an opening narrower than ai2.bbMinGap
   (surface-to-surface). Scans the corridor ahead for a pinch — a pair of rocks flanking
   the path whose gap is too tight for the big hull. Verdict:
     null                                    — lane is fine
     { type:'demolish', rock, x, y, face }   — hold at debris-safe standoff, guns MAKE room
     { type:'detour', x, y }                 — both pinch rocks too big to shoot: go around
   "A WWII battleship once removed an entire hill" — tight gaps get widened, not threaded. */
function bbGapGate(state, ship, goalPt) {
  var cfg = state.config, AI = cfg.ai, A2 = cfg.ai2, def = ship.def;
  // demolition TARGET LATCH: finish the jamb we started on. Rescanning every refresh
  // flickered the aim between pinch pairs, dumping the turrets' slew+load investment
  // each time (traced: fire uptime collapsed to ~10%). One rock, until it dies.
  if (ship.ai.demolishRockId != null) {
    var lr = null;
    for (var li = 0; li < state.asteroids.length; li++) {
      var lo = state.asteroids[li];
      if (lo.alive && lo.id === ship.ai.demolishRockId) { lr = lo; break; }
    }
    if (lr && dist(ship.x, ship.y, lr.x, lr.y) < A2.bbGapLookahead * 1.4) {
      ship.ai.gapGate = { type: 'demolish', rockId: lr.id, rx: lr.x, ry: lr.y,
                          x: ship.ai.gapAnchorX, y: ship.ai.gapAnchorY,
                          face: Math.atan2(lr.y - ship.ai.gapAnchorY, lr.x - ship.ai.gapAnchorX) };
      return ship.ai.gapGate;
    }
    ship.ai.demolishRockId = null; // jamb dead or left behind: full re-scan
  }
  // stagger + cache (same cadence as the clutter detour)
  if (state.tick % AI.clutterRefreshTicks !== (ship.id + 7) % AI.clutterRefreshTicks &&
      ship.ai.gapGateT !== undefined && state.tick - ship.ai.gapGateT < AI.clutterRefreshTicks &&
      Math.abs((ship.ai.gapGoalX || 0) - goalPt.x) < 80 && Math.abs((ship.ai.gapGoalY || 0) - goalPt.y) < 80)
    return ship.ai.gapGate;
  ship.ai.gapGateT = state.tick; ship.ai.gapGoalX = goalPt.x; ship.ai.gapGoalY = goalPt.y;
  ship.ai.gapGate = null;
  var dGoal = dist(ship.x, ship.y, goalPt.x, goalPt.y);
  if (dGoal < 240) return null;
  var reach = Math.min(dGoal, A2.bbGapLookahead);
  var ux = (goalPt.x - ship.x) / dGoal, uy = (goalPt.y - ship.y) / dGoal;
  var ex = ship.x + ux * reach, ey = ship.y + uy * reach;
  // corridor rocks big enough to define a pinch. Pebbles are shoved aside, and fast
  // debris is DISPERSING — counting fragments as jambs re-pinches a gap the guns just
  // opened and grinds the ship into an endless demolition loop.
  var rocks = [];
  var C = cfg.collision;
  rocksNearSeg(state, ship.x, ship.y, ex, ey, 92 + def.radius + A2.bbMinGap + 260, function (o) {
    if (o.r < C.pushableRockRadius) return false;
    if (o.moving && len(o.vx, o.vy) > 18) return false;
    if (rocks.length < 28) rocks.push(o);
    return false;
  });
  var bestPinch = null, bestD = Infinity;
  for (var i = 0; i < rocks.length; i++) {
    for (var j = i + 1; j < rocks.length; j++) {
      var a = rocks[i], b = rocks[j];
      var cd = dist(a.x, a.y, b.x, b.y);
      var gap = cd - a.r - b.r;
      if (gap >= A2.bbMinGap || gap < -20) continue;      // wide enough / overlapping = one wall
      // the passage midpoint (between the two surfaces)
      var gx = a.x + (b.x - a.x) * ((a.r + gap / 2) / (cd || 1));
      var gy = a.y + (b.y - a.y) * ((a.r + gap / 2) / (cd || 1));
      // path must actually thread this passage: midpoint near the corridor axis AND the
      // two rocks on opposite sides of the path
      var t = clamp(((gx - ship.x) * ux + (gy - ship.y) * uy) / (reach || 1), 0, 1) * reach;
      var px = ship.x + ux * t, py = ship.y + uy * t;
      if (dist(px, py, gx, gy) > A2.bbMinGap * 0.5 + def.radius) continue;
      var sa = ux * (a.y - ship.y) - uy * (a.x - ship.x);
      var sb = ux * (b.y - ship.y) - uy * (b.x - ship.x);
      if (sa * sb > 0) continue;                          // both rocks on one side: not a pinch
      var dPinch = dist(ship.x, ship.y, gx, gy);
      if (dPinch < bestD) { bestD = dPinch; bestPinch = { a: a, b: b, x: gx, y: gy }; }
    }
  }
  if (!bestPinch) return null;
  var small = bestPinch.a.r <= bestPinch.b.r ? bestPinch.a : bestPinch.b;
  var big = small === bestPinch.a ? bestPinch.b : bestPinch.a;
  // Manoeuvre before munitions: score a flank around the pinch on both sides. When a
  // reasonably clean detour exists, TAKE IT — demolition is for genuine walls (both
  // flanks cluttered) or when the detour would leave the arena. "Blow everything to
  // make room" is the fallback of RAW POWER, not the opening move.
  var pxp = -uy, pyp = ux;
  var off = big.r + A2.bbMinGap + 520;
  var T = cfg.terrain, W = cfg.arena.w, H = cfg.arena.h;
  var c1x = clamp(bestPinch.x + pxp * off, T.edgeMargin, W - T.edgeMargin);
  var c1y = clamp(bestPinch.y + pyp * off, T.edgeMargin, H - T.edgeMargin);
  var c2x = clamp(bestPinch.x - pxp * off, T.edgeMargin, W - T.edgeMargin);
  var c2y = clamp(bestPinch.y - pyp * off, T.edgeMargin, H - T.edgeMargin);
  var w1 = corridorClutter(state, ship.x, ship.y, c1x, c1y, ship) +
           corridorClutter(state, c1x, c1y, goalPt.x, goalPt.y, ship);
  var w2 = corridorClutter(state, ship.x, ship.y, c2x, c2y, ship) +
           corridorClutter(state, c2x, c2y, goalPt.x, goalPt.y, ship);
  var wBest = Math.min(w1, w2);
  var canDemolish = small.r <= A2.bbDemolishMaxR;
  if (wBest <= A2.bbDetourMaxClutter || !canDemolish) {
    ship.ai.gapGate = { type: 'detour', x: w1 <= w2 ? c1x : c2x, y: w1 <= w2 ? c1y : c2y };
  } else {
    // DEMOLITION TRANSIT: it's a wall. Hold at a standoff that is BOTH debris-safe and
    // outside the guns' own dead zone (a hull that creeps inside heavyRail.minRange of
    // the jamb cannot depress its turrets onto it — the observed breach deadlock), and
    // blast the smaller jamb until the opening exceeds bbMinGap.
    var HRg = cfg.heavyRail;
    var want = Math.max(def.radius + small.r + AI.rockClearDebrisPad + small.r * A2.bbDebrisRPad,
                        HRg.minRange + 60);
    var dR = dist(ship.x, ship.y, small.x, small.y) || 1;
    // FIXED firing anchor for the whole demolition episode: holding "current position"
    // re-evaluated per tick follows the hull's own drift and walks it off-station
    var bx = ship.x, by = ship.y;
    if (dR < want) { bx = small.x + ((ship.x - small.x) / dR) * (want + 30); by = small.y + ((ship.y - small.y) / dR) * (want + 30); }
    ship.ai.demolishRockId = small.id;
    ship.ai.gapAnchorX = bx; ship.ai.gapAnchorY = by;
    ship.ai.gapGate = { type: 'demolish', rockId: small.id, rx: small.x, ry: small.y,
                        x: bx, y: by, face: Math.atan2(small.y - by, small.x - bx) };
  }
  return ship.ai.gapGate;
}

/* Apply a gap-gate verdict to the nav/turret state (highest transit priority; a live
   firing solution still owns the guns — updateTurrets prefers its ship primary). */
function bbApplyGate(state, ship, gate, speedCap) {
  if (gate.type === 'demolish') {
    ship.nav = { x: gate.x, y: gate.y, arrive: true, face: gate.face, speedCap: speedCap };
    // demolition LATCH: while the breach is being blown, fragments fly (and are rightly
    // ignored by the pinch scan) — without a hold, the hull surges into the half-open
    // throat between salvos, ends up inside its own dead zone, and the breach deadlocks.
    // Keep station until the rubble has settled and the re-scan stays clean for a while.
    ship.ai.gapHoldX = gate.x; ship.ai.gapHoldY = gate.y; ship.ai.gapHoldFace = gate.face;
    ship.ai.gapCalmUntil = state.time + 2.5;
    var rock = null;
    for (var ri = 0; ri < state.asteroids.length; ri++) {
      var o = state.asteroids[ri];
      if (o.alive && o.id === gate.rockId) { rock = o; break; }
    }
    if (rock) ship.ai.rockAim = { x: rock.x, y: rock.y, rockId: rock.id, until: state.time + 1.0 };
    else ship.ai.gapGate = null; // jamb destroyed: force re-scan next tick
  } else {
    ship.nav = { x: gate.x, y: gate.y, arrive: true, speedCap: speedCap };
  }
}

/* The demolition-latch hold: true while rubble from an active breach is still settling —
   the caller should keep station instead of advancing into the throat. */
function bbGateCalmHold(state, ship) {
  if (state.time >= (ship.ai.gapCalmUntil || -1)) return false;
  ship.nav = { x: ship.ai.gapHoldX, y: ship.ai.gapHoldY, arrive: true, face: ship.ai.gapHoldFace };
  return true;
}

function aiBattleshipV2(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, A2 = cfg.ai2, HR = cfg.heavyRail, def = ship.def;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    // no contacts: hunt like v1 (skirt wells, clear rocks) + NEVER thread a tight gap
    var hpn = bbSkirtWell(state, ship, routeAround(state, ship, huntPoint(state, ship)));
    ship.nav = { x: hpn.x, y: hpn.y, arrive: true, speedCap: def.maxCruiseSpeed * 0.8 };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    var gate0 = bbGapGate(state, ship, hpn);
    if (gate0) bbApplyGate(state, ship, gate0, def.maxCruiseSpeed * 0.8);
    else bbGateCalmHold(state, ship);
    updateTurrets(state, ship, dt);
    return;
  }
  var primary = pickHeavyRailPrimary(state, ship);
  var pick = primary ? { ship: primary, d: dist(ship.x, ship.y, primary.x, primary.y) }
                     : (nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null));
  var target = pick.ship, d = pick.d;
  var aim = Math.atan2(target.y - ship.y, target.x - ship.x);

  // threat centroid: diving lights within 450 AND any trackable hull knife-fighting
  // inside the dead zone — creep away from both to reopen the gun window
  var lcx = 0, lcy = 0, ln = 0;
  for (var i = 0; i < enemies.length; i++) {
    var e = enemies[i];
    var de = dist(ship.x, ship.y, e.x, e.y);
    if ((isLight(e) && de < 450) || (heavyRailTrackable(HR, e) && de < HR.minRange * 1.05)) {
      lcx += e.x; lcy += e.y; ln++;
    }
  }

  if (state.tick % 30 === (ship.id % 30)) ship.ai.anchorBias = openLaneBias(state, ship, target);
  var bias = ship.ai.anchorBias || { x: 0, y: 0 };
  var maxStand = HR.maxRange * HR.standoffFrac;
  var coverRock = null;

  var goalPt, navFace = aim;
  if (ship.hp < 0.3 * ship.maxHp && ln > 0) {
    // low and swarmed: crawl toward the fleet centroid (v1 behavior)
    var own = ship.team === 'A' ? state.aliveA : state.aliveB;
    var fx = 0, fy = 0, fn = 0;
    for (var k = 0; k < own.length; k++) if (own[k].id !== ship.id) { fx += own[k].x; fy += own[k].y; fn++; }
    goalPt = fn ? { x: fx / fn, y: fy / fn } : huntPoint(state, ship);
    ship.ai.noLos = 0;
  } else if (ln > 0) {
    // creep directly away from the close-threat centroid; keep the beam on the primary
    // (the turrets stay ready for the moment a knife-fighter drifts back out past minRange)
    var cx = lcx / ln, cy = lcy / ln, dc = dist(ship.x, ship.y, cx, cy) || 1;
    var ux = (ship.x - cx) / dc, uy = (ship.y - cy) / dc;
    goalPt = { x: ship.x + ux * 320 + bias.x, y: ship.y + uy * 320 + bias.y };
    if (primary && A2.bbBroadside) navFace = bbBroadsideFace(state, ship, aim);
    ship.ai.noLos = 0;
  } else {
    // engagement-aware standoff (v1) + broadside discipline when the solution holds
    var navT = target, navD = d, stand = maxStand;
    if (primary) {
      stand = clamp(d, HR.standoffMin, maxStand);
      ship.ai.noLos = 0;
      if (A2.bbBroadside) navFace = bbBroadsideFace(state, ship, aim);
    } else {
      var approach = pickHeavyRailPrimary(state, ship, true);
      if (approach) {
        navT = approach; navD = dist(ship.x, ship.y, approach.x, approach.y);
        stand = HR.standoffMin;
        ship.ai.noLos = (ship.ai.noLos || 0) + dt;
        if (ship.ai.noLos > AI.rockShootSeconds)
          coverRock = firstRockOnRay(state, ship.x, ship.y, approach.x, approach.y);
        navFace = Math.atan2(navT.y - ship.y, navT.x - ship.x);
      } else if (isCapital(target)) {
        stand = HR.standoffMin; ship.ai.noLos = 0;
      } else {
        ship.ai.noLos = 0;
      }
      if (navFace === aim) navFace = Math.atan2(navT.y - ship.y, navT.x - ship.x);
    }
    var uxn = (navT.x - ship.x) / (navD || 1), uyn = (navT.y - ship.y) / (navD || 1);
    goalPt = { x: navT.x - uxn * stand + bias.x, y: navT.y - uyn * stand + bias.y };
  }
  var g = bbSkirtWell(state, ship, routeAround(state, ship, goalPt));
  ship.nav = { x: g.x, y: g.y, arrive: true, speedCap: def.maxCruiseSpeed, face: navFace };
  blastCoverNearGhosts(state, ship, dt);
  if (coverRock)
    ship.ai.rockAim = { x: coverRock.x, y: coverRock.y, rockId: coverRock.id, until: state.time + AI.rockShootSeconds };
  // transit gap gate — only while there is no live firing solution (combat owns the guns)
  if (!primary) {
    var gate = bbGapGate(state, ship, g);
    if (gate) bbApplyGate(state, ship, gate, def.maxCruiseSpeed);
    else bbGateCalmHold(state, ship);
  }
  updateTurrets(state, ship, dt);
}
