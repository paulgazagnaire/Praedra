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

