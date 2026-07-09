/* ---------------- Role AI (deliberately simple; serves the §5 counter-web) ---------------- */
function livingEnemies(state, team) { return team === 'A' ? state.detA : state.detB; } // DETECTED enemies only
function isCapital(s) { return s.cls === 'destroyer' || s.cls === 'frigate' || s.cls === 'battleship'; }
function isLight(s) { return s.cls === 'bomber' || s.cls === 'interceptor'; }

function nearestWhere(state, ship, list, pred) {
  var best = null, bestD = Infinity;
  for (var i = 0; i < list.length; i++) {
    var e = list[i];
    if (pred && !pred(e)) continue;
    var d = dist(ship.x, ship.y, e.x, e.y);
    if (d < bestD) { bestD = d; best = e; }
  }
  return best ? { ship: best, d: bestD } : null;
}
/* Is a live enemy torpedo tracking me nearby? (lights jink in response) */
function torpedoThreat(state, ship) {
  var torps = state.torps;
  for (var i = 0; i < torps.length; i++) {
    var tp = torps[i];
    if (tp.alive && !tp.spent && tp.team !== ship.team && tp.targetId === ship.id &&
        dist(tp.x, tp.y, ship.x, ship.y) < 650) return true;
  }
  return false;
}
function threatened(state, ship) {
  var AI = state.config.ai;
  var near = nearestWhere(state, ship, livingEnemies(state, ship.team), null);
  return (near && near.d < AI.threatJinkRange) || torpedoThreat(state, ship);
}

function tryTorpedo(state, ship, dt) {
  var TC = state.config.torpedo[ship.cls];
  if (!TC) return;
  if (ship.torpAmmo === 0) return;
  var enemies = livingEnemies(state, ship.team);
  // continue a salvo in progress
  if (ship.cool.salvoLeft > 0) {
    ship.cool.salvoGap -= dt;
    if (ship.cool.salvoGap <= 0) {
      var t2 = torpedoPick(state, ship, enemies, TC);
      if (t2) { launchTorpedo(state, ship, t2); }
      ship.cool.salvoLeft--; ship.cool.salvoGap = TC.salvoGap || 0;
    }
    return;
  }
  if (ship.cool.torp > 0) return;
  var target = torpedoPick(state, ship, enemies, TC);
  if (!target) return;
  launchTorpedo(state, ship, target);
  if (ship.torpAmmo > 0) ship.torpAmmo--;
  ship.cool.torp = TC.cooldown || 9999;
  ship.cool.salvoLeft = (TC.salvo || 1) - 1;
  ship.cool.salvoGap = TC.salvoGap || 0;
}
/* torpedoes go after capitals, else predictable (slow/steady) targets — not jinking lights */
function torpedoPick(state, ship, enemies, TC) {
  var T = state.config.torpedo;
  var self = this;
  var cap = nearestWhere(state, ship, enemies, function (e) {
    var de = dist(ship.x, ship.y, e.x, e.y);
    return isCapital(e) && de >= T.lobMinRange && de <= TC.range && losShips(state, ship, e);
  });
  if (cap) return cap.ship;
  if (ship.cls === 'interceptor') return null; // save the one shot for a capital
  // among lights, only bombers are worth a torpedo (steady/slow = predictable = bait);
  // an interceptor jinks the moment it's threatened and the torpedo is always wasted
  var steady = nearestWhere(state, ship, enemies, function (e) {
    var de = dist(ship.x, ship.y, e.x, e.y);
    return e.cls === 'bomber' &&
           (e.speed < T.predictSpeed || e.jinkEMA < T.jinkAccelThreshold) &&
           de >= T.lobMinRange && de <= TC.range && losShips(state, ship, e);
  });
  return steady ? steady.ship : null;
}

/* Capitals sample sideways offsets and drift toward the one with the most clearance
   and a clean sightline to the target — keep the lane open, keep the gun useful. */
function openLaneBias(state, ship, target) {
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
    var score = lane - clutter * 0.55;
    if (score > bestScore) { bestScore = score; best = { x: offs[i][0], y: offs[i][1] }; }
  }
  return best;
}

/* A contact was seen seconds ago and vanished behind cover: capitals put ordnance on
   the rock along the last-seen bearing. Railgun for the destroyer, torpedoes for the
   frigate. This shatters hiding spots, spawns debris, and makes a glorious mess. */
function blastCoverNearGhosts(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, D = cfg.detection;
  if (ship.cls !== 'destroyer' && ship.cls !== 'frigate' && ship.cls !== 'battleship') return;
  ship.cool.rockTorp = (ship.cool.rockTorp || 0) - dt;
  var best = null, bestT = -1;
  for (var id in state.lastSeenShip) {
    var ls = state.lastSeenShip[id];
    var ghost = state.shipById[id];
    if (!ghost || !ghost.alive || ghost.team === ship.team) continue;
    if (state.time - ls.t > D.memorySeconds || state.time - ls.t < 0.5) continue;
    if (isDetectedBy(state, ship.team, ghost)) continue; // still visible: not a ghost
    if (ls.t > bestT) { bestT = ls.t; best = ls; }
  }
  if (!best) return;
  var rock = firstRockOnRay(state, ship.x, ship.y, best.x, best.y);
  if (!rock) return;
  var rd = dist(ship.x, ship.y, rock.x, rock.y);
  if (ship.cls === 'destroyer') {
    var RG = cfg.railgun;
    if (ship.cool.rail <= 0 && rd >= RG.minRange && rd <= RG.maxRange &&
        Math.abs(normAngle(Math.atan2(rock.y - ship.y, rock.x - ship.x) - ship.heading)) < RG.arc / 2)
      fireRailgunAtRock(state, ship, rock);
  } else if (ship.cls === 'battleship') {
    // turrets must slew before they can fire, so leave a rock-aim hint for updateTurrets to
    // demolish the cover (a later lane-clearing stage sets the same ai.rockAim field)
    var HR = cfg.heavyRail;
    if (rd >= HR.minRange && rd <= HR.maxRange)
      ship.ai.rockAim = { x: rock.x, y: rock.y, rockId: rock.id, until: state.time + AI.rockShootSeconds };
  } else if (ship.cool.rockTorp <= 0 && rd >= cfg.torpedo.armDistance * 1.1 &&
             rd <= cfg.torpedo.frigate.range) {
    launchTorpedoAtRock(state, ship, rock);
    ship.cool.rockTorp = AI.rockTorpCooldown;
  }
}

/* Per-class rock-fire primitive for lane-clearing: reach, cooldown accessor, and a fire()
   that aims+fires that class's rock-capable weapon. Any capital with such a weapon path-clears. */
function rockClearPrimitive(state, ship) {
  var cfg = state.config, AI = cfg.ai;
  if (ship.cls === 'destroyer') {
    var RG = cfg.railgun;
    return { maxRange: RG.maxRange, engageRange: RG.maxRange,
      cooldownLeft: function (s) { return s.cool.rail; },
      fire: function (st, sh, rock, ta) {
        var b = Math.atan2(rock.y - sh.y, rock.x - sh.x);
        // yaw to aim (like blastCoverNearGhosts), but only for a near-forward rock: overriding the
        // face toward a wide-bearing rock fights the transit vector into a weave (deliverable 5).
        if (sh.nav && Math.abs(normAngle(b - ta)) < 0.5) sh.nav.face = b;
        var rd = dist(sh.x, sh.y, rock.x, rock.y);
        if (sh.cool.rail <= 0 && rd >= RG.minRange && rd <= RG.maxRange &&
            Math.abs(normAngle(b - sh.heading)) < RG.arc / 2) fireRailgunAtRock(st, sh, rock);
      } };
  }
  if (ship.cls === 'frigate') {
    var T = cfg.torpedo;
    return { maxRange: T.frigate.range, engageRange: T.frigate.range,
      cooldownLeft: function (s) { return s.cool.rockTorp || 0; },       // shares cover-torp cadence (no spam)
      fire: function (st, sh, rock) {
        var rd = dist(sh.x, sh.y, rock.x, rock.y);
        if (rd >= T.armDistance * 1.1 && rd <= T.frigate.range) {
          launchTorpedoAtRock(st, sh, rock); sh.cool.rockTorp = AI.rockTorpCooldown;
        }
      } };
  }
  if (ship.cls === 'battleship') {
    var HR = cfg.heavyRail;
    return { maxRange: HR.maxRange, engageRange: HR.maxRange,
      // combat only owns the turrets when they have a target they can LEGALLY engage: the
      // heavy rail is hard class-gated out of lights, so a loitering bomber must NOT
      // suppress lane-clearing forever (the generic nearest-any gate below would strand
      // an ordered battleship behind a rock while a bomber it can never shoot circles it)
      combatHold: function (st, sh) { return !!pickHeavyRailPrimary(st, sh); },
      cooldownLeft: function (s) {                                       // ready when ANY turret is
        var m = Infinity;                                               //   off cooldown (loadTime
        if (s.turrets) for (var i = 0; i < s.turrets.length; i++) if (s.turrets[i].cool < m) m = s.turrets[i].cool;
        return m === Infinity ? 0 : m;                                  //   makes this slightly
      },                                                                //   optimistic — acceptable)
      fire: function (st, sh, rock) {
        // turrets must slew before firing: flag the rock, updateTurrets demolishes it (same field
        // blastCoverNearGhosts uses). Deadline gives the ponderous slew time to line up.
        sh.ai.rockAim = { x: rock.x, y: rock.y, rockId: rock.id, until: st.time + 1.5 };
      } };
  }
  return null;
}

/* Lane-clearing fire: a transiting capital blasts a blocking rock out of its corridor. Combat
   owns the gun first (a DETECTED enemy in weapon range suppresses rock fire); the debris-safety
   pad keeps fragments settled before they can reach the shooter. Runs on the auto-hunt path AND
   under player move/attackmove/hold orders (via weaponsFree) — every ordered transit. */
function clearTransitLane(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, def = ship.def;
  var prim = rockClearPrimitive(state, ship);            if (!prim) return;
  var goal = ship.nav;                                   if (!goal) return;
  var dgoal = dist(ship.x, ship.y, goal.x, goal.y);      if (dgoal < AI.rockClearMinGoalDist) return;
  var enemies = livingEnemies(state, ship.team);
  // COMBAT PRIORITY: the gun has a real target. Class-aware where the weapon is (the
  // battleship's combatHold only respects targets its turrets can legally track).
  if (prim.combatHold) { if (prim.combatHold(state, ship)) return; }
  else {
    var ne = enemies.length ? nearestWhere(state, ship, enemies, null) : null;
    if (ne && ne.d <= prim.engageRange) return;
  }
  if (prim.cooldownLeft(ship) > 0) return;               // respect cooldown economy
  var ux, uy;
  if (ship.speed > 15) { ux = ship.vx / ship.speed; uy = ship.vy / ship.speed; }
  else { ux = (goal.x - ship.x) / dgoal; uy = (goal.y - ship.y) / dgoal; }
  var reach = Math.min(dgoal, AI.rockClearLookahead, prim.maxRange);
  var ex = ship.x + ux * reach, ey = ship.y + uy * reach;
  var ta = Math.atan2(uy, ux);
  // Pick the MOST-ALIGNED qualifying blocker (smallest bearing offset from the transit axis),
  // distance as tiebreak. A fixed-arc railgun must yaw onto its target; targeting the nearest
  // (often off-axis) rock makes the ship yaw wide and fight the transit into a stall/weave, so
  // clear the rock most dead-ahead first — it is the one truly plugging the lane. (Turrets and
  // torpedoes track off-axis fine, but the same choice serves them: clear the centre of the lane.)
  var best = null, bestScore = Infinity, C = cfg.collision;
  rocksNearSeg(state, ship.x, ship.y, ex, ey, 92 + def.radius + 60, function (o) {
    if (o.r < C.pushableRockRadius || o.r > AI.rockClearMaxRadius) return false;   // pebble / futile-huge
    var rd = dist(ship.x, ship.y, o.x, o.y);
    if (rd < def.radius + o.r + AI.rockClearDebrisPad) return false;               // DEBRIS SAFETY
    if (!segCircleHit(ship.x, ship.y, ex, ey, o.x, o.y, o.r + def.radius + 30)) return false; // blocks lane?
    var boff = Math.abs(normAngle(Math.atan2(o.y - ship.y, o.x - ship.x) - ta));
    var score = boff * 10000 + rd;                                                // alignment first, then near
    if (score < bestScore) { bestScore = score; best = o; }
    return false;
  });
  if (best) prim.fire(state, ship, best, ta);
}

