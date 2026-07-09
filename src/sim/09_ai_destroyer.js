function aiDestroyer(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, RG = cfg.railgun;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true, speedCap: ship.def.maxCruiseSpeed * 0.8 };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    return;
  }
  // railgun food first (slow, low evasion), then whatever is nearest
  var pick = nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null);
  var target = pick.ship;
  var d = pick.d;
  var aim = Math.atan2(target.y - ship.y, target.x - ship.x);
  var closeLight = nearestWhere(state, ship, enemies, function (e) { return isLight(e); });

  // a big hull wants open ground and a clean lane, not a rock maze
  if (state.tick % 30 === (ship.id % 30)) ship.ai.anchorBias = openLaneBias(state, ship, target);
  var bias = ship.ai.anchorBias || { x: 0, y: 0 };

  if (closeLight && closeLight.d < AI.destroyerRetreatRange) {
    // lights in the dead zone: burn away (nose turns away — the railgun is now useless, by design)
    var ux = (ship.x - closeLight.ship.x) / (closeLight.d || 1), uy = (ship.y - closeLight.ship.y) / (closeLight.d || 1);
    ship.nav = { x: ship.x + ux * 500, y: ship.y + uy * 500, arrive: false, jink: false };
  } else if (d > AI.destroyerStandoff + 100) {
    var ux2 = (target.x - ship.x) / d, uy2 = (target.y - ship.y) / d;
    ship.nav = { x: target.x - ux2 * AI.destroyerStandoff + bias.x, y: target.y - uy2 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else if (d < AI.destroyerStandoff - 140 && isCapital(target)) {
    var ux3 = (ship.x - target.x) / d, uy3 = (ship.y - target.y) / d;
    ship.nav = { x: target.x + ux3 * AI.destroyerStandoff + bias.x, y: target.y + uy3 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else {
    ship.nav = { x: ship.x + bias.x, y: ship.y + bias.y, arrive: true, face: aim }; // hold in the open, gun on target
  }

  var st = railgunReady(state, ship, target);
  if (st === 'ok') { fireRailgun(state, ship, target); ship.ai.noLos = 0; }
  else if (st === 'los') {
    ship.ai.noLos = (ship.ai.noLos || 0) + dt;
    // blow open the cover: no hesitation — the rock hiding a contact IS a target
    if (ship.ai.noLos > AI.rockShootSeconds && d < RG.maxRange && ship.cool.rail <= 0) {
      var rock = firstRockOnRay(state, ship.x, ship.y, target.x, target.y);
      if (rock) {
        if (ship.nav) ship.nav.face = Math.atan2(rock.y - ship.y, rock.x - ship.x);
        if (Math.abs(normAngle(Math.atan2(rock.y - ship.y, rock.x - ship.x) - ship.heading)) < RG.arc / 2)
          fireRailgunAtRock(state, ship, rock);
      }
    }
  } else ship.ai.noLos = 0;
  blastCoverNearGhosts(state, ship, dt);
  tryTorpedo(state, ship, dt); // weak secondary — only ever finds capital/steady targets
}


/* ================= Destroyer v2 (veteran doctrine) ================= */
/* v2 lane bias: openLaneBias + DEFILADE — prefer an offset where a rock blocks the
   sightline from the SECOND threat while the firing lane to the primary stays open
   (hull-down gunnery: engage one axis, masked from the other). */
function openLaneBiasV2(state, ship, target, second) {
  var best = { x: 0, y: 0 }, bestScore = -Infinity;
  var offs = [[0, 0], [220, 0], [-220, 0], [0, 220], [0, -220]];
  for (var i = 0; i < offs.length; i++) {
    var px = ship.x + offs[i][0], py = ship.y + offs[i][1];
    var clutter = 0;
    rocksNearSeg(state, px, py, px, py, 400, function (o) {
      var dd = dist(px, py, o.x, o.y) - o.r;
      if (dd < 380) clutter += (380 - Math.max(0, dd)) / 380;
      return false;
    });
    var lane = target && losClear(state, px, py, target.x, target.y) ? 1.6 : 0;
    var defil = second && !losClear(state, px, py, second.x, second.y) ? 0.8 : 0;
    var score = lane + defil - clutter * 0.55;
    if (score > bestScore) { bestScore = score; best = { x: offs[i][0], y: offs[i][1] }; }
  }
  return best;
}

function aiDestroyerV2(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, RG = cfg.railgun;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true, speedCap: ship.def.maxCruiseSpeed * 0.8 };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    return;
  }
  // FOCUS FIRE (Lanchester): the fleet's gunline focus when it's sanely reachable,
  // else the v1 pick (nearest capital, then nearest anything)
  var focus = focusFor(state, ship, 'gun');
  var pick = focus ? { ship: focus, d: dist(ship.x, ship.y, focus.x, focus.y) }
                   : (nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null));
  var target = pick.ship;
  var d = pick.d;
  var aim = Math.atan2(target.y - ship.y, target.x - ship.x);
  var closeLight = nearestWhere(state, ship, enemies, function (e) { return isLight(e); });

  // defilade anchor: mask from the second capital while keeping the lane to the first
  var second = nearestWhere(state, ship, enemies, function (e) { return isCapital(e) && e.id !== target.id; });
  if (state.tick % 30 === (ship.id % 30))
    ship.ai.anchorBias = openLaneBiasV2(state, ship, target, second && second.ship);
  var bias = ship.ai.anchorBias || { x: 0, y: 0 };

  if (closeLight && closeLight.d < AI.destroyerRetreatRange) {
    // lights in the dead zone: burn away (v1)
    var ux = (ship.x - closeLight.ship.x) / (closeLight.d || 1), uy = (ship.y - closeLight.ship.y) / (closeLight.d || 1);
    ship.nav = { x: ship.x + ux * 500, y: ship.y + uy * 500, arrive: false, jink: false };
  } else if (target.cls === 'battleship' && d > RG.maxRange * 1.05) {
    // MASKED APPROACH (infiltration): the battleship's 3200px guns outrange our 700 —
    // never stroll the open. Hop rock shadows toward it, engines cold inside the band
    // where a burn would light us up (EMCON). No cover on the map = jink the approach.
    var hop = coverHop(state, ship, target, AI.destroyerStandoff);
    var cap = emconCap(state, ship);
    if (hop) ship.nav = { x: hop.x, y: hop.y, arrive: true, speedCap: cap || undefined, face: aim };
    else {
      var uxm = (target.x - ship.x) / d, uym = (target.y - ship.y) / d;
      ship.nav = { x: target.x - uxm * AI.destroyerStandoff, y: target.y - uym * AI.destroyerStandoff,
                   arrive: true, speedCap: cap || undefined, jink: !cap };
    }
  } else if (d > AI.destroyerStandoff + 100) {
    var ux2 = (target.x - ship.x) / d, uy2 = (target.y - ship.y) / d;
    ship.nav = { x: target.x - ux2 * AI.destroyerStandoff + bias.x, y: target.y - uy2 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else if (d < AI.destroyerStandoff - 140 && isCapital(target)) {
    var ux3 = (ship.x - target.x) / d, uy3 = (ship.y - target.y) / d;
    ship.nav = { x: target.x + ux3 * AI.destroyerStandoff + bias.x, y: target.y + uy3 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else {
    ship.nav = { x: ship.x + bias.x, y: ship.y + bias.y, arrive: true, face: aim };
  }

  var st = railgunReady(state, ship, target);
  if (st === 'ok') { fireRailgun(state, ship, target); ship.ai.noLos = 0; }
  else if (st === 'los') {
    ship.ai.noLos = (ship.ai.noLos || 0) + dt;
    if (ship.ai.noLos > AI.rockShootSeconds && d < RG.maxRange && ship.cool.rail <= 0) {
      var rock = firstRockOnRay(state, ship.x, ship.y, target.x, target.y);
      if (rock) {
        if (ship.nav) ship.nav.face = Math.atan2(rock.y - ship.y, rock.x - ship.x);
        if (Math.abs(normAngle(Math.atan2(rock.y - ship.y, rock.x - ship.x) - ship.heading)) < RG.arc / 2)
          fireRailgunAtRock(state, ship, rock);
      }
    }
  } else ship.ai.noLos = 0;
  blastCoverNearGhosts(state, ship, dt);
  tryTorpedoV2(state, ship, dt);
}
