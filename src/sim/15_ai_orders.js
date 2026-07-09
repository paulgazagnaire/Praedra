/* Doctrine dispatch: the per-class role AI for this ship's team level (config.doctrine).
   Pinned ships and direct player 'attack' fire-control stay doctrine-independent. */
function roleAI(state, ship) {
  var v2 = doctrineOf(state, ship.team) === 'v2';
  if (ship.cls === 'destroyer') return v2 ? aiDestroyerV2 : aiDestroyer;
  if (ship.cls === 'frigate') return v2 ? aiFrigateV2 : aiFrigate;
  if (ship.cls === 'battleship') return v2 ? aiBattleshipV2 : aiBattleship;
  if (ship.cls === 'bomber') return v2 ? aiBomberV2 : aiBomber;
  return v2 ? aiInterceptorV2 : aiInterceptor;
}
function tryTorpedoD(state, ship, dt) {
  (doctrineOf(state, ship.team) === 'v2' ? tryTorpedoV2 : tryTorpedo)(state, ship, dt);
}

function aiPinned(state, ship, dt) {
  // test scenarios: no navigation, but rotate to aim and use every weapon that is legal
  var enemies = livingEnemies(state, ship.team);
  ship.nav = null;
  if (!enemies.length) return;
  var pick = nearestWhere(state, ship, enemies, null);
  var target = pick.ship;
  ship.ctl.face = Math.atan2(target.y - ship.y, target.x - ship.x);
  if (ship.cls === 'destroyer') {
    var st = railgunReady(state, ship, target);
    if (st === 'ok') fireRailgun(state, ship, target);
  }
  if (ship.cls === 'interceptor') updateGatling(state, ship, target, dt);
  if (ship.cls === 'bomber' && ship.cool.bomb <= 0 &&
      pick.d <= state.config.bomb.launchRange && losShips(state, ship, target)) {
    launchBomb(state, ship, target); ship.cool.bomb = state.config.bomb.cooldown;
  }
  if (ship.cls === 'battleship') updateTurrets(state, ship, dt); // turrets pick their own detected primary
  tryTorpedo(state, ship, dt);
}

/* Opportunity fire while under player orders: shoot what you can without chasing it. */
function weaponsFree(state, ship, dt) {
  var enemies = livingEnemies(state, ship.team);
  var near = enemies.length ? nearestWhere(state, ship, enemies, null) : null;
  if (ship.cls === 'destroyer') {
    if (near) {
      if (ship.nav && ship.nav.face == null) ship.nav.face = Math.atan2(near.ship.y - ship.y, near.ship.x - ship.x);
      if (railgunReady(state, ship, near.ship) === 'ok') fireRailgun(state, ship, near.ship);
    }
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);   // clear a blocking rock across ordered move/attackmove/hold
    tryTorpedoD(state, ship, dt);
  } else if (ship.cls === 'frigate') {
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);   // shares ship.cool.rockTorp with blastCoverNearGhosts (no spam)
    tryTorpedoD(state, ship, dt);
  } else if (ship.cls === 'battleship') {
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);   // set ai.rockAim before turrets process it this tick
    updateTurrets(state, ship, dt);
  } else if (ship.cls === 'interceptor') {
    if (near) updateGatling(state, ship, near.ship, dt);
    if (ship.torpAmmo > 0) tryTorpedo(state, ship, dt);
  } else if (ship.cls === 'bomber' && near) {
    var B = state.config.bomb, tgt = near.ship, d = near.d;
    if (ship.cool.bomb <= 0 && d <= B.launchRange && losShips(state, ship, tgt)) {
      launchBomb(state, ship, tgt);
      ship.cool.bomb = B.cooldown;
    }
  }
}

/* Player orders: the command sets the goal, the autopilot flies it (PRD §9).
   Completed orders drop to Hold, never back to Auto. */
function executeOrder(state, ship, dt) {
  var o = ship.order, AI = state.config.ai;
  if (o.type === 'hold') {
    ship.nav = { x: o.x, y: o.y, arrive: true };
    weaponsFree(state, ship, dt);
    return;
  }
  if (o.type === 'move' || o.type === 'attackmove') {
    if (o.type === 'attackmove') {
      var foes = livingEnemies(state, ship.team);
      var hit = foes.length ? nearestWhere(state, ship, foes, null) : null;
      if (hit && hit.d < 950) { // engage what you meet, resume after
        roleAI(state, ship)(state, ship, dt);
        return;
      }
    }
    ship.nav = { x: o.x, y: o.y, arrive: true };
    weaponsFree(state, ship, dt);
    if (dist(ship.x, ship.y, o.x, o.y) < 42 && ship.speed < 16) ship.order = { type: 'hold', x: o.x, y: o.y };
    return;
  }
  if (o.type === 'attack') {
    var tgt = state.shipById[o.targetId];
    if (!tgt || !tgt.alive) { ship.order = { type: 'hold', x: ship.x, y: ship.y }; return; }
    var seen = isDetectedBy(state, ship.team, tgt);
    if (!seen) {
      var ls = state.lastSeenShip[tgt.id];
      var px = ls ? ls.x : tgt.x, py = ls ? ls.y : tgt.y; // chase the memory
      ship.nav = { x: px, y: py, arrive: true };
      weaponsFree(state, ship, dt);
      return;
    }
    var d = dist(ship.x, ship.y, tgt.x, tgt.y);
    var aim = Math.atan2(tgt.y - ship.y, tgt.x - ship.x);
    if (ship.cls === 'destroyer') {
      var ux = (tgt.x - ship.x) / (d || 1), uy = (tgt.y - ship.y) / (d || 1);
      ship.nav = d > AI.destroyerStandoff + 80
        ? { x: tgt.x - ux * AI.destroyerStandoff, y: tgt.y - uy * AI.destroyerStandoff, arrive: true, face: aim }
        : { x: ship.x, y: ship.y, arrive: true, face: aim };
      var st = railgunReady(state, ship, tgt);
      if (st === 'ok') fireRailgun(state, ship, tgt);
      else if (st === 'los' && ship.cool.rail <= 0) {
        var rk = firstRockOnRay(state, ship.x, ship.y, tgt.x, tgt.y);
        if (rk && Math.abs(normAngle(Math.atan2(rk.y - ship.y, rk.x - ship.x) - ship.heading)) < state.config.railgun.arc / 2)
          fireRailgunAtRock(state, ship, rk);
      }
      tryTorpedo(state, ship, dt);
    } else if (ship.cls === 'frigate') {
      var T = state.config.torpedo, TC = T.frigate;
      var ux2 = (ship.x - tgt.x) / (d || 1), uy2 = (ship.y - tgt.y) / (d || 1);
      ship.nav = { x: tgt.x + ux2 * AI.frigateStandoff, y: tgt.y + uy2 * AI.frigateStandoff, arrive: true, face: aim };
      if (ship.cool.torp <= 0 && d >= T.lobMinRange && d <= TC.range && losShips(state, ship, tgt)) {
        launchTorpedo(state, ship, tgt);
        ship.cool.torp = TC.cooldown;
        ship.cool.salvoLeft = (TC.salvo || 1) - 1;
        ship.cool.salvoGap = TC.salvoGap || 0;
      } else tryTorpedo(state, ship, dt); // continues salvos
    } else if (ship.cls === 'bomber') {
      var B = state.config.bomb;
      var m = ship.ai.mode;
      if (m !== 'run' && m !== 'break') m = 'approach';
      if (m === 'approach') {
        ship.nav = { x: tgt.x, y: tgt.y, arrive: false, jink: threatened(state, ship) };
        if (d <= B.launchRange * B.runStartFactor && losShips(state, ship, tgt)) { m = 'run'; ship.ai.runBombs = 0; ship.ai.modeAt = state.time; }
      }
      if (m === 'run') {
        ship.nav = { x: tgt.x, y: tgt.y, arrive: false, jink: false };
        if (ship.cool.bomb <= 0 && d <= B.launchRange && losShips(state, ship, tgt)) {
          launchBomb(state, ship, tgt); ship.cool.bomb = B.cooldown; ship.ai.runBombs = (ship.ai.runBombs || 0) + 1;
        }
        if ((ship.ai.runBombs || 0) >= 2 || d < AI.bomberBreakRange) m = 'break';
      }
      if (m === 'break') {
        var bx = (ship.x - tgt.x) / (d || 1), by = (ship.y - tgt.y) / (d || 1);
        ship.nav = { x: tgt.x + bx * AI.bomberRegroupRange, y: tgt.y + by * AI.bomberRegroupRange, arrive: false, jink: true };
        if (d > AI.bomberRegroupRange * 0.9) m = 'approach';
      }
      ship.ai.mode = m;
    } else if (ship.cls === 'battleship') {
      // stand off at the top of the gun's reach, face the target, let the turrets work
      var HRa = state.config.heavyRail;
      var uxb = (ship.x - tgt.x) / (d || 1), uyb = (ship.y - tgt.y) / (d || 1);
      ship.nav = { x: tgt.x + uxb * HRa.maxRange * 0.85, y: tgt.y + uyb * HRa.maxRange * 0.85,
                   arrive: true, speedCap: ship.def.maxCruiseSpeed, face: aim };
      blastCoverNearGhosts(state, ship, dt);
      updateTurrets(state, ship, dt);
    } else { // interceptor
      if (ship.torpAmmo > 0 && isCapital(tgt) && ship.cool.torp <= 0 &&
          d >= state.config.torpedo.lobMinRange && d <= state.config.torpedo.interceptor.range && losShips(state, ship, tgt)) {
        launchTorpedo(state, ship, tgt); ship.torpAmmo--; ship.cool.torp = 1.5;
      }
      var tl = Math.min(1.2, d / Math.max(1, ship.def.maxCruiseSpeed));
      ship.nav = { x: tgt.x + tgt.vx * tl, y: tgt.y + tgt.vy * tl, arrive: false,
                   jink: threatened(state, ship) && d > 260 };
      updateGatling(state, ship, tgt, dt);
    }
    return;
  }
  if (o.type === 'attackrock') {
    var rock = null;
    for (var ri = 0; ri < state.asteroids.length; ri++)
      if (state.asteroids[ri].id === o.targetId && state.asteroids[ri].alive) { rock = state.asteroids[ri]; break; }
    if (!rock) { ship.order = { type: 'hold', x: ship.x, y: ship.y }; return; }
    var rd = dist(ship.x, ship.y, rock.x, rock.y);
    var rAim = Math.atan2(rock.y - ship.y, rock.x - ship.x);
    var rux = (ship.x - rock.x) / (rd || 1), ruy = (ship.y - rock.y) / (rd || 1);
    if (ship.cls === 'destroyer') {
      var RG = state.config.railgun;
      var want = clamp(rd, RG.minRange + rock.r + 60, RG.maxRange * 0.8);
      ship.nav = { x: rock.x + rux * want, y: rock.y + ruy * want, arrive: true, face: rAim };
      if (ship.cool.rail <= 0 && rd - rock.r >= RG.minRange && rd <= RG.maxRange &&
          Math.abs(normAngle(rAim - ship.heading)) < RG.arc / 2) fireRailgunAtRock(state, ship, rock);
    } else if (ship.cls === 'frigate') {
      var T2 = state.config.torpedo;
      ship.nav = { x: rock.x + rux * 620, y: rock.y + ruy * 620, arrive: true, face: rAim };
      if (ship.cool.torp <= 0 && rd >= T2.armDistance * 1.15 && rd <= T2.frigate.range) {
        launchTorpedoAtRock(state, ship, rock); ship.cool.torp = T2.frigate.cooldown * 0.6;
      }
    } else if (ship.cls === 'interceptor') {
      var G = state.config.gatling;
      ship.nav = { x: rock.x + rux * (rock.r + G.range * 0.7), y: rock.y + ruy * (rock.r + G.range * 0.7), arrive: true, face: rAim };
      ship.cool.gat -= dt;
      if (rd - rock.r <= G.range && Math.abs(normAngle(rAim - ship.heading)) < G.arc / 2) {
        while (ship.cool.gat <= 0) {
          ship.cool.gat += 1 / G.fireRate;
          damageAsteroid(state, rock, G.damagePerShot);
          pushEvent(state, { kind: 'gat', x: ship.x, y: ship.y, x2: rock.x, y2: rock.y, team: ship.team });
        }
        if (ship.cool.gat < 0) ship.cool.gat = 0;
      }
    } else if (ship.cls === 'battleship') {
      // close to a sensible standoff and flag the rock so the turrets slew to and demolish it
      var HRb = state.config.heavyRail;
      var wantR = clamp(rd, HRb.minRange + rock.r + 80, HRb.maxRange * 0.8);
      ship.nav = { x: rock.x + rux * wantR, y: rock.y + ruy * wantR, arrive: true, speedCap: ship.def.maxCruiseSpeed, face: rAim };
      if (rd >= HRb.minRange && rd <= HRb.maxRange)
        ship.ai.rockAim = { x: rock.x, y: rock.y, rockId: rock.id, until: state.time + 0.2 };
      updateTurrets(state, ship, dt);
    } else { // bomber
      var B2 = state.config.bomb;
      ship.nav = { x: rock.x + rux * (B2.launchRange * 0.8), y: rock.y + ruy * (B2.launchRange * 0.8), arrive: true, face: rAim };
      if (ship.cool.bomb <= 0 && rd <= B2.launchRange && losClear(state, ship.x, ship.y, rock.x + (ship.x - rock.x) / (rd || 1) * (rock.r + 2), rock.y + (ship.y - rock.y) / (rd || 1) * (rock.r + 2))) {
        launchBomb(state, ship, { x: rock.x, y: rock.y, vx: 0, vy: 0 });
        ship.cool.bomb = B2.cooldown;
      }
    }
    return;
  }
}

function aiTick(state, ship, dt) {
  ship.cool.rail -= dt; ship.cool.torp -= dt; ship.cool.bomb -= dt;
  if (ship.pinned) { aiPinned(state, ship, dt); return; }
  if (ship.order) { executeOrder(state, ship, dt); return; } // Ordered beats Auto (PRD §9)
  roleAI(state, ship)(state, ship, dt);
}

