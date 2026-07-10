/* Praedra sim module: roles — per-class role AI, orders, weapons-free, aiTick — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

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
/* Is a live enemy torpedo tracking me nearby — that I can SEE? (lights jink in
   response). LOS-gated: the old check sensed warheads through solid rock, so ships
   jinked pre-cognitively at threats no sensor could report. */
function torpedoThreat(state, ship) {
  var AI = state.config.ai, torps = state.torps;
  for (var i = 0; i < torps.length; i++) {
    var tp = torps[i];
    if (tp.alive && !tp.spent && tp.team !== ship.team && tp.targetId === ship.id &&
        dist(tp.x, tp.y, ship.x, ship.y) < AI.torpSenseRange &&
        losClear(state, ship.x, ship.y, tp.x, tp.y)) return true;
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
  // fleet focus fire: put the warhead on the team's deletion target when it's in the basket
  var f = focusTarget(state, ship);
  if (f && isCapital(f)) {
    var df = dist(ship.x, ship.y, f.x, f.y);
    if (df >= T.lobMinRange && df <= TC.range && losShips(state, ship, f)) return f;
  }
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

function aiDestroyer(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, RG = cfg.railgun;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    // fresh blind contact nearby: set an LOS-shadow ambush on the expected approach
    // instead of marching into the open (smart teams; strictly time-bounded)
    var amb = ambushNav(state, ship, null);
    if (amb) { ship.nav = amb; blastCoverNearGhosts(state, ship, dt); return; }
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
  // fleet focus fire: swing onto the team's deletion target when it's a capital within reach
  var focus = focusTarget(state, ship);
  if (focus && isCapital(focus)) {
    var df = dist(ship.x, ship.y, focus.x, focus.y);
    if (df <= Math.max(RG.maxRange * 2.2, d * 1.3)) { target = focus; d = df; }
  }
  var aim = Math.atan2(target.y - ship.y, target.x - ship.x);
  var closeLight = nearestWhere(state, ship, enemies, function (e) { return isLight(e); });

  // a big hull wants open ground and a clean lane, not a rock maze
  if (state.tick % 30 === (ship.id % 30)) ship.ai.anchorBias = openLaneBias(state, ship, target);
  var bias = ship.ai.anchorBias || { x: 0, y: 0 };
  var smart = smartTeam(state, ship.team);

  if (closeLight && closeLight.d < AI.destroyerRetreatRange) {
    // lights in the dead zone: burn away (nose turns away — the railgun is now useless, by design)
    var ux = (ship.x - closeLight.ship.x) / (closeLight.d || 1), uy = (ship.y - closeLight.ship.y) / (closeLight.d || 1);
    ship.nav = { x: ship.x + ux * 500, y: ship.y + uy * 500, arrive: false, jink: false };
  } else if (d > AI.destroyerStandoff + 100) {
    var appAng = Math.atan2(ship.y - target.y, ship.x - target.x);
    var scap = null;
    if (smart) {
      // pincer: sibling destroyers swing onto offset bearings — a crossfire, not a queue
      var sibs = 0, idx = 0;
      var own2 = ship.team === 'A' ? state.aliveA : state.aliveB;
      for (var s2 = 0; s2 < own2.length; s2++)
        if (own2[s2].cls === 'destroyer') { if (own2[s2].id < ship.id) idx++; sibs++; }
      if (sibs > 1) appAng += (idx % 2 === 0 ? 1 : -1) * AI.capitalPincerBearing * (1 + (idx >> 1) * 0.5);
      // approach governor: bleed speed on the way in so arrival needs no flip-and-burn —
      // the flip is what used to swing the gun off target at exactly the wrong moment
      var aB = (ship.def.thrust / ship.def.mass) * AI.navBrakeMargin;
      scap = clamp(Math.sqrt(2 * aB * Math.max(0, d - AI.destroyerStandoff)) + 18, 34, ship.def.maxCruiseSpeed);
    }
    ship.nav = { x: target.x + Math.cos(appAng) * AI.destroyerStandoff + bias.x,
                 y: target.y + Math.sin(appAng) * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
    if (scap != null) ship.nav.speedCap = scap;
  } else if (d < AI.destroyerStandoff - 140 && isCapital(target)) {
    var ux3 = (ship.x - target.x) / d, uy3 = (ship.y - target.y) / d;
    ship.nav = { x: target.x + ux3 * AI.destroyerStandoff + bias.x, y: target.y + uy3 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else {
    ship.nav = { x: ship.x + bias.x, y: ship.y + bias.y, arrive: true, face: aim }; // hold in the open, gun on target
  }

  if (smart && ship.nav && !(closeLight && closeLight.d < AI.destroyerRetreatRange)) {
    // gun discipline: while a shot is imminent the nose stays ON TARGET (no flip-and-burn
    // wander); during cooldown dead-time the pilot may brake/maneuver freely and realign
    // before the gun comes back up — slower repositioning, but the weapon stays live
    var gunHot = ship.cool.rail <= 1.4;
    if (gunHot && d <= RG.maxRange * 1.15) { ship.nav.face = aim; ship.nav.faceLock = true; }
    else if (!gunHot) ship.nav.face = null;
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

function aiFrigate(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var own0 = ship.team === 'A' ? state.aliveA : state.aliveB;
    var d0 = null;
    for (var i0 = 0; i0 < own0.length; i0++) if (own0[i0].cls === 'destroyer') { d0 = own0[i0]; break; }
    if (!d0) {
      // no destroyer to escort: a frigate with a fresh blind contact lurks in a LOS shadow
      // (torpedoes from ambush) instead of strolling into the open
      var ambF = ambushNav(state, ship, null);
      if (ambF) { ship.nav = ambF; blastCoverNearGhosts(state, ship, dt); return; }
    }
    var hp = routeAround(state, ship, d0 ? { x: d0.x, y: d0.y } : huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    return;
  }
  var own = (ship.team === 'A' ? state.aliveA : state.aliveB);
  var dest = null, gIdx = 0, gSibs = 1;
  if (smartTeam(state, ship.team)) {
    // distribute escorts: frigate k guards destroyer k % nDest — one PD umbrella per hull,
    // not every frigate stacked on the lead destroyer while the rest of the line dies alone
    var dests = [], fidx = 0, fsibs = 0;
    for (var i = 0; i < own.length; i++) {
      if (own[i].cls === 'destroyer') dests.push(own[i]);
      else if (own[i].cls === 'frigate') { if (own[i].id < ship.id) fidx++; fsibs++; }
    }
    if (dests.length) {
      dest = dests[fidx % dests.length];
      gIdx = Math.floor(fidx / dests.length);                       // slot within my destroyer's guard group
      gSibs = Math.max(1, Math.ceil((fsibs - (fidx % dests.length)) / dests.length));
    }
  } else {
    for (var i2 = 0; i2 < own.length; i2++) if (own[i2].cls === 'destroyer') { dest = own[i2]; break; }
  }
  var near = nearestWhere(state, ship, enemies, null);
  var aim = Math.atan2(near.ship.y - ship.y, near.ship.x - ship.x);
  if (dest) {
    // escort: park the PD umbrella between the destroyer and the threat axis
    var ex = near.ship.x - dest.x, ey = near.ship.y - dest.y;
    var ea = Math.atan2(ey, ex);
    // escort RING: each guard takes its own slot around the threat axis — stacked escorts
    // were one blast-sized clump (and one torpedo AOE) waiting to happen
    if (gSibs > 1) ea += (gIdx - (gSibs - 1) / 2) * AI.escortRingSpread;
    ship.nav = { x: dest.x + Math.cos(ea) * AI.escortRange, y: dest.y + Math.sin(ea) * AI.escortRange,
                 arrive: true, face: aim };
  } else {
    var eCap = nearestWhere(state, ship, enemies, function (e) { return e.cls === 'destroyer'; });
    var anchor = eCap || near;
    var ux = (ship.x - anchor.ship.x) / (anchor.d || 1), uy = (ship.y - anchor.ship.y) / (anchor.d || 1);
    var stand = eCap ? AI.frigateStandoff : 500;
    ship.nav = { x: anchor.ship.x + ux * stand, y: anchor.ship.y + uy * stand, arrive: true, face: aim };
  }
  blastCoverNearGhosts(state, ship, dt);
  tryTorpedo(state, ship, dt); // the torpedo specialist
}

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

function teamCommitting(state, team) {
  if (state.time < state.commit[team].until) return true;
  // no PD capitals on the other side: nothing to saturate, no reason to hold back
  var enemies = livingEnemies(state, team);
  for (var i = 0; i < enemies.length; i++) if (isCapital(enemies[i])) return false;
  return true;
}
function updateCommits(state) {
  var AI = state.config.ai;
  var teams = ['A', 'B'];
  for (var t = 0; t < teams.length; t++) {
    var team = teams[t];
    var own = team === 'A' ? state.aliveA : state.aliveB;
    var lights = [];
    for (var i = 0; i < own.length; i++) if (isLight(own[i])) lights.push(own[i]);
    if (!lights.length) continue;
    var enemies = livingEnemies(state, team);
    if (!enemies.length) continue;
    var c = state.commit[team];
    // wave target: the enemy destroyer, else any capital, else nearest to the pack
    var cx = 0, cy = 0;
    for (var j = 0; j < lights.length; j++) { cx += lights[j].x; cy += lights[j].y; }
    var probe = { x: cx / lights.length, y: cy / lights.length };
    var tgt = null, bestD = Infinity;
    for (var k = 0; k < enemies.length; k++) {
      var e = enemies[k];
      var pri = isCapital(e) ? 0 : 1; // any capital; the pack naturally hits the escort first
      var d = dist(probe.x, probe.y, e.x, e.y) + pri * 100000;
      if (d < bestD) { bestD = d; tgt = e; }
    }
    if (smartTeam(state, team)) {
      // the whole wave saturates the fleet's ONE focus victim (a capital) instead of whatever
      // happens to be nearest — coordinated deletion beats spread damage
      var f = state.shipById[state.plan[team].focusId];
      if (f && f.alive && isCapital(f) && isDetectedBy(state, team, f)) tgt = f;
    }
    c.targetId = tgt.id;
    if (state.time < c.until || state.time < c.cool) continue;
    // admiral posture gate: waves in flight run to completion, NEW waves only open in strike
    if (admiralTeam(state, team) && state.admiral[team].posture !== 'strike') continue;
    // staged = in position AND slowed to hold — the wave assembles before it goes in.
    // In the open that dwell is naked torpedo exposure; in dense it happens behind rocks.
    var staged = 0, stagedBombers = 0, aliveBombers = 0, reserveCount = 0;
    var admOn = admiralTeam(state, team);
    for (var s = 0; s < lights.length; s++) {
      var L = lights[s];
      if (admOn && L.ai.fleetRole === 'reserve') { reserveCount++; continue; } // held back
      if (L.cls === 'bomber') aliveBombers++;
      if (dist(L.x, L.y, tgt.x, tgt.y) < AI.commitRadius && L.speed < L.def.maxCruiseSpeed * 0.5) {
        staged++;
        if (L.cls === 'bomber') stagedBombers++;
      }
    }
    // don't blow the wave before the payload is in position
    if (aliveBombers > 0 && stagedBombers === 0) continue;
    if (staged >= Math.min(lights.length - reserveCount, AI.commitMinLights)) {
      c.until = state.time + AI.commitSeconds;
      c.cool = c.until + AI.commitCooldown;
      // sometimes the whole wave hooks around a side — war loves a flank
      var r = state.rng.next();
      c.flank = r < AI.flankChance / 2 ? -1 : (r < AI.flankChance ? 1 : 0);
      state.stats.waves[team]++;
    }
  }
}

/* Find a staging point in the LOS shadow of a rock near the wave ring: behind cover
   from the enemy's torpedo source. Returns null on open maps — exposure is the point. */
function coverPoint(state, ship, tgt, minD, maxD) {
  var key = Math.round(minD) + ':' + Math.round(maxD);
  if (state.tick - (ship.ai.coverAt || -999) < state.config.ai.coverHoldTicks && ship.ai.cover && ship.ai.coverKey === key) return ship.ai.cover; // hold: a stable point you can arrive at beats a perfect one that keeps moving
  ship.ai.coverAt = state.tick; ship.ai.coverKey = key;
  // hide from the enemy's TORPEDO platform — the frigate is the specialist; the
  // destroyer's shadow is worthless if the frigate has a sightline around the rock
  var enemies = livingEnemies(state, ship.team);
  var threat = nearestWhere(state, ship, enemies, function (e) { return e.cls === 'frigate'; }) ||
               nearestWhere(state, ship, enemies, isCapital);
  var thx = threat ? threat.ship.x : tgt.x, thy = threat ? threat.ship.y : tgt.y;
  var cands = [];
  rocksNearSeg(state, tgt.x, tgt.y, tgt.x, tgt.y, maxD + 92, function (o) {
    if (o.r < 32) return false;
    var dTgt = dist(o.x, o.y, tgt.x, tgt.y);
    if (dTgt < minD || dTgt > maxD) return false;
    var dth = dist(o.x, o.y, thx, thy) || 1;
    var sx = (o.x - thx) / dth, sy = (o.y - thy) / dth;
    var hx = o.x + sx * (o.r + 55), hy = o.y + sy * (o.r + 55);
    // creep to the INNERMOST shadow behind the BIGGEST rock: a wide umbra tolerates
    // both our drift and the emitter's movement; a small rock's shadow is a knife-edge
    var score = dTgt + 0.3 * dist(ship.x, ship.y, hx, hy) + (90 - Math.min(90, o.r)) * 3;
    cands.push({ score: score, x: hx, y: hy, sx: sx, sy: sy });
    return false;
  });
  cands.sort(function (a, b) { return a.score - b.score; });
  // validate lazily: reject hide points embedded in a neighbouring rock (stall traps)
  var best = null;
  for (var c = 0; c < cands.length && c < 6; c++) {
    var cd = cands[c], embedded = false;
    rocksNearSeg(state, cd.x, cd.y, cd.x, cd.y, 120, function (o2) {
      if (dist(cd.x, cd.y, o2.x, o2.y) < o2.r + 28) { embedded = true; return true; }
      return false;
    });
    if (!embedded) { best = { x: cd.x, y: cd.y, sx: cd.sx, sy: cd.sy }; break; }
  }
  ship.ai.cover = best;
  return best;
}
/* Approach a cover point through its shadow cone so the braking phase stays hidden. */
function navToCover(ship, cover, jink) {
  var dx = cover.x - ship.x, dy = cover.y - ship.y;
  if (dx * dx + dy * dy > 260 * 260)
    return { x: cover.x + cover.sx * 220, y: cover.y + cover.sy * 220, arrive: false, jink: jink };
  return { x: cover.x, y: cover.y, arrive: true, jink: jink };
}

function aiBomber(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, B = cfg.bomb;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true, jink: false };
    return;
  }
  // nearest capital first: strip the escort (torpedoes + 3 PD slots), THEN the blinded
  // destroyer is §5's helpless victim. Only achievable where the approach is covered.
  var pick = nearestWhere(state, ship, enemies, isCapital) ||
             nearestWhere(state, ship, enemies, null);
  var target = pick.ship, d = pick.d;
  var focus = focusTarget(state, ship);
  if (focus && isCapital(focus)) { target = focus; d = dist(ship.x, ship.y, focus.x, focus.y); }
  var thr = threatened(state, ship);
  var committing = squadCommitting(state, ship);   // squadron-staggered wave entry; reserve holds
  var m = ship.ai.mode;
  if (m !== 'run' && m !== 'break' && m !== 'approach') m = 'stage';
  if (m === 'stage' && committing) { m = 'approach'; }
  if ((m === 'approach' || m === 'run') && !committing) m = 'break';

  if (m === 'stage') {
    // hold the ring between waves — in the LOS shadow of a rock when the map offers one.
    // Coasting exposed is torpedo bait (open maps); cover blocks the lock (dense maps).
    var cover = coverPoint(state, ship, target, AI.coverMinRange, AI.stageRange * 1.4);
    if (cover) ship.nav = navToCover(ship, cover, thr);
    else {
      var sx = (ship.x - target.x) / (d || 1), sy = (ship.y - target.y) / (d || 1);
      ship.nav = { x: target.x + sx * AI.stageRange, y: target.y + sy * AI.stageRange, arrive: true, jink: thr };
    }
  }
  if (m === 'approach') {
    // committed push at the target — hooking wide first when the wave called a flank
    var flk = state.commit[ship.team].flank;
    if (flk !== 0 && d > AI.flankDone * 2) {
      var fx = (ship.x - target.x) / (d || 1), fy = (ship.y - target.y) / (d || 1);
      var ca = Math.cos(flk * 1.1), sa = Math.sin(flk * 1.1);
      var wx = target.x + (fx * ca - fy * sa) * AI.flankOffset;
      var wy = target.y + (fx * sa + fy * ca) * AI.flankOffset;
      if (dist(ship.x, ship.y, wx, wy) > AI.flankDone) {
        ship.nav = { x: wx, y: wy, arrive: false, jink: thr };
      } else {
        var ap = d > B.launchRange * 1.25 ? spreadPoint(state, ship, target, B.launchRange * 1.1) : target;
        ship.nav = { x: ap.x, y: ap.y, arrive: false, jink: thr };
      }
    } else {
      // slot-separated approach lane: squadmates fan around the lead's bearing so their
      // release lanes never overlap (one interception cannot chain the wave)
      var ap2 = d > B.launchRange * 1.25 ? spreadPoint(state, ship, target, B.launchRange * 1.1) : target;
      ship.nav = { x: ap2.x, y: ap2.y, arrive: false, jink: thr };
    }
    // a bomb needs a steady vector BEFORE release: go steady as soon as the target is
    // sighted inside run-start range — early in the open (long, exposed), late in dense
    if (d <= B.launchRange * B.runStartFactor && losShips(state, ship, target)) {
      m = 'run'; ship.ai.modeAt = state.time; ship.ai.runBombs = 0;
      var ba = Math.atan2(target.y - ship.y, target.x - ship.x);
      ship.ai.lane = { px: -Math.sin(ba), py: Math.cos(ba) }; // frozen run-lane perpendicular
      ship.ai.laneTgt = target.id;
      state.stats.runEntries++;
    }
  }
  if (m === 'run') {
    // steady bomb-run vector: accurate bombs, torpedo-predictable — the enforced tradeoff.
    // Pop out, stream a pair, duck back before the answering torpedo lands.
    // PARALLEL LANES: each squadmate flies through a per-slot offset of the target in the
    // frame frozen at run entry — the old converge-on-target here was THE clump-wipe cause
    // (spread lanes collapsed to one point exactly where the bombs go live). launchBomb
    // still lead-solves the hull itself, so bombs converge while bombers stay 150 apart.
    var SQn = state.config.squadron;
    if (ship.ai.laneTgt !== target.id) {   // focus switched mid-run: re-freeze for the NEW target
      var ba2 = Math.atan2(target.y - ship.y, target.x - ship.x);
      ship.ai.lane = { px: -Math.sin(ba2), py: Math.cos(ba2) };
      ship.ai.laneTgt = target.id;
    }
    var laneOff = (ship.ai.sqSlot != null && ship.ai.sqN > 1)
      ? (ship.ai.sqSlot - (ship.ai.sqN - 1) / 2) * SQn.runLaneSep : 0;
    var ln = ship.ai.lane || { px: 0, py: 0 };
    ship.nav = { x: target.x + ln.px * laneOff, y: target.y + ln.py * laneOff, arrive: false, jink: false };
    var clear = losShips(state, ship, target);
    if (ship.cool.bomb > 0) state.stats.fireBlocked.cooldown++;
    else if (d > B.launchRange) state.stats.fireBlocked.range++;
    else if (!clear) state.stats.fireBlocked.los++;
    else {
      // don't waste the bomb on a rock: the LEAD path (not just the sightline) must be clear
      var tl = Math.min(B.maxFlight, d / B.speed);
      var aimX = target.x + target.vx * tl, aimY = target.y + target.vy * tl;
      if (!losClear(state, ship.x, ship.y, aimX, aimY)) state.stats.fireBlocked.lane++;
      else {
        launchBomb(state, ship, target);
        state.stats.salvosFired++;
        ship.cool.bomb = B.cooldown;
        ship.ai.runBombs = (ship.ai.runBombs || 0) + 1;
      }
    }
    if ((ship.ai.runBombs || 0) >= 2 || d < AI.bomberBreakRange ||
        (!clear && state.time - ship.ai.modeAt > 1.0)) m = 'break';
  }
  if (m === 'break') {
    // duck behind the NEAREST solid rock — the pursuing torpedo must lose its lock NOW,
    // not after a cross-map transit. No rock nearby (open map) = no duck. That's the flip.
    // per-slot rock choice + shadow-arc spread + staggered regroup: the whole squadron
    // used to compute ONE duck point and ONE regroup instant — it re-clumped every wave
    var ducks = [];
    var AR = state.asteroids;
    var thrShip = nearestWhere(state, ship, enemies, function (e) { return e.cls === 'frigate'; }) ||
                  nearestWhere(state, ship, enemies, isCapital);
    for (var ri = 0; ri < AR.length; ri++) {
      var ro = AR[ri];
      if (!ro.alive || ro.r < 40) continue;
      var rd2 = dist(ship.x, ship.y, ro.x, ro.y);
      if (rd2 < 380) ducks.push({ o: ro, d: rd2 });
    }
    ducks.sort(function (a, b) { return (a.d - b.d) || (a.o.id - b.o.id); });
    var duck = ducks.length ? ducks[(ship.ai.sqSlot || 0) % ducks.length].o : null;
    if (duck && thrShip) {
      var tdx = duck.x - thrShip.ship.x, tdy = duck.y - thrShip.ship.y;
      var tdl = len(tdx, tdy) || 1;
      var SQd = state.config.squadron;
      var rot = ((ship.ai.sqSlot || 0) - ((ship.ai.sqN || 1) - 1) / 2) * SQd.duckSlotSpread;
      var ca3 = Math.cos(rot), sa3 = Math.sin(rot);
      var uxr = (tdx / tdl) * ca3 - (tdy / tdl) * sa3, uyr = (tdx / tdl) * sa3 + (tdy / tdl) * ca3;
      ship.nav = { x: duck.x + uxr * (duck.r + 50), y: duck.y + uyr * (duck.r + 50),
                   arrive: true, jink: true };
    } else {
      var ux = (ship.x - target.x) / (d || 1), uy = (ship.y - target.y) / (d || 1);
      var side = (ship.id % 2 === 0) ? 1 : -1;
      ship.nav = { x: ship.x + (ux * 0.8 + -uy * side * 0.6) * 400, y: ship.y + (uy * 0.8 + ux * side * 0.6) * 400,
                   arrive: false, jink: true };
    }
    if (d > AI.bomberRegroupRange * (1 + 0.06 * (ship.ai.sqSlot || 0))) m = committing ? 'approach' : 'stage';
  }
  if (m !== ship.ai.mode) ship.ai.modeAt = state.time;
  ship.ai.mode = m;
}

/* Any LIVE friendly bomb arriving near pt within `secs`? Divers hold out of the
   impact zone instead of strafing through their own bombers' blast wave. */
function bombsInboundTo(state, team, pt, secs) {
  var B = state.config.bomb, R = B.aoeRadius + 60;
  for (var i = 0; i < state.bombs.length; i++) {
    var bm = state.bombs[i];
    if (!bm.alive || bm.team !== team) continue;
    var d = dist(bm.x, bm.y, pt.x, pt.y);
    if (d < R || d / B.speed < secs) return true;
  }
  return false;
}
function aiInterceptor(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, G = cfg.gatling, TC = cfg.torpedo.interceptor;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: false, jink: false, speedCap: ship.def.maxCruiseSpeed * 0.85 };
    return;
  }
  var committing = squadCommitting(state, ship);   // squadron-staggered wave entry; reserve holds
  // the one anti-capital punch: held for the wave, then volleyed together (saturation)
  if (ship.torpAmmo > 0 && committing) tryTorpedo(state, ship, dt);

  var thr = threatened(state, ship);
  var light = nearestWhere(state, ship, enemies, function (e) { return isLight(e); });
  var gatTarget = null;

  if (light && light.d < 700) {
    // screen: hunt enemy lights (keep theirs off our bombers)
    var lt = light.ship;
    var tLead = Math.min(1.2, light.d / Math.max(1, ship.def.maxCruiseSpeed));
    ship.nav = { x: lt.x + lt.vx * tLead, y: lt.y + lt.vy * tLead, arrive: false,
                 jink: thr && light.d > 260 };
    gatTarget = lt;
    ship.ai.mode = 'screen';
  } else {
    var cap = nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null);
    var tgt = cap.ship, d = cap.d;
    var focusI = focusTarget(state, ship);
    if (focusI && isCapital(focusI)) { tgt = focusI; d = dist(ship.x, ship.y, focusI.x, focusI.y); }
    gatTarget = tgt;
    var m = ship.ai.mode;
    if (m !== 'strafe_in' && m !== 'strafe_out') m = 'strafe_in';
    if (!committing) {
      // hold the ring until the wave goes in — behind cover when the map offers it
      var cov = coverPoint(state, ship, tgt, AI.coverMinRange, AI.stageRange * 1.4);
      if (cov) ship.nav = navToCover(ship, cov, thr);
      else {
        var sx = (ship.x - tgt.x) / (d || 1), sy = (ship.y - tgt.y) / (d || 1);
        ship.nav = { x: tgt.x + sx * AI.stageRange, y: tgt.y + sy * AI.stageRange, arrive: true, jink: thr };
      }
      ship.ai.mode = 'strafe_in';
      updateGatling(state, ship, gatTarget, dt);
      return;
    }
    // committed strafe runs: dive through the PD bubble to gatling range, dump, exit.
    // Deep and exposed by design (§5) — PD makes lingering lethal, saturation makes
    // the pass survivable. So: dive WITH the bomb stream (PD slots prioritise projectiles),
    // hold outside the bubble when no bombs fly. With no bombers left, spent interceptors
    // commit regardless (O3: fights end on kills, not the clock).
    var m = ship.ai.mode;
    if (m !== 'strafe_in' && m !== 'strafe_out') m = 'strafe_in';
    if (m === 'strafe_in') {
      var flk2 = state.commit[ship.team].flank;
      if (flk2 !== 0 && d > AI.flankDone * 2.2) {
        var gx2 = (ship.x - tgt.x) / (d || 1), gy2 = (ship.y - tgt.y) / (d || 1);
        var ca2 = Math.cos(flk2 * 1.1), sa2 = Math.sin(flk2 * 1.1);
        ship.nav = { x: tgt.x + (gx2 * ca2 - gy2 * sa2) * AI.flankOffset,
                     y: tgt.y + (gx2 * sa2 + gy2 * ca2) * AI.flankOffset,
                     arrive: false, jink: thr };
      } else if (d > AI.strafeExitRange * 1.5) {
        // fan the inbound lanes around the squadron lead's bearing — spaced divers split the
        // PD picture and never eat one shared bomb-chain blast on the way in
        var apI = spreadPoint(state, ship, tgt, Math.max(AI.strafeExitRange, d * 0.4));
        ship.nav = { x: apI.x, y: apI.y, arrive: false, jink: thr && d > cfg.pd.range * 1.3 };
      } else {
        // per-slot dive TIME stagger: squadmates arrive as a stream, not a stack. The dive
        // geometry itself is untouched (a lateral offset would starve the 95px gatling).
        var cw = state.commit[ship.team];
        var goTime = (cw.until - AI.commitSeconds) + (ship.ai.sqOrd || 0) * AI.commitStaggerSeconds
                                                   + (ship.ai.sqSlot || 0) * AI.diveSlotStaggerSeconds;
        var bombsIn = bombsInboundTo(state, ship.team, tgt, 1.2);
        if ((state.time < cw.until && state.time < goTime) || bombsIn) {
          // not my beat yet — or friendly bombs are seconds from the target: hold just
          // outside the PD bubble on my squadron's sector bearing, never strafe through
          // the blast wave (the traced 3-interceptors-die-to-own-bombs egress chain)
          var holdP = spreadPoint(state, ship, tgt, AI.strafeExitRange * 1.35);
          ship.nav = { x: holdP.x, y: holdP.y, arrive: true, jink: thr };
        } else {
          var ux = (tgt.x - ship.x) / (d || 1), uy = (tgt.y - ship.y) / (d || 1);
          ship.nav = { x: tgt.x + ux * AI.strafeDivePoint, y: tgt.y + uy * AI.strafeDivePoint,
                       arrive: false, jink: thr && d > cfg.pd.range * 1.3 };
        }
      }
      if (d < G.range * 0.8) m = 'strafe_out';
    } else {
      var ux2 = (ship.x - tgt.x) / (d || 1), uy2 = (ship.y - tgt.y) / (d || 1);
      var side = (ship.id % 2 === 0) ? 1 : -1;
      // per-slot egress fan: the two id-parity lanes stacked 4 ships on one exit vector
      // straight through the live bomb corridor (3 died to one friendly salvo, traced)
      var eRot = ((ship.ai.sqSlot || 0) - ((ship.ai.sqN || 1) - 1) / 2) * 0.4;
      var ce = Math.cos(eRot), se = Math.sin(eRot);
      var ex2 = (ux2 * 0.75 + -uy2 * side * 0.66), ey2 = (uy2 * 0.75 + ux2 * side * 0.66);
      ship.nav = { x: tgt.x + (ex2 * ce - ey2 * se) * AI.strafeExitRange * 1.6,
                   y: tgt.y + (ex2 * se + ey2 * ce) * AI.strafeExitRange * 1.6,
                   arrive: false, jink: true };
      if (d > AI.strafeExitRange) m = 'strafe_in'; // do not loiter: out, around, in again
    }
    ship.ai.mode = m;
  }
  updateGatling(state, ship, gatTarget, dt);
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
    tryTorpedo(state, ship, dt);
  } else if (ship.cls === 'frigate') {
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);   // shares ship.cool.rockTorp with blastCoverNearGhosts (no spam)
    tryTorpedo(state, ship, dt);
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
        if (ship.cls === 'destroyer') aiDestroyer(state, ship, dt);
        else if (ship.cls === 'frigate') aiFrigate(state, ship, dt);
        else if (ship.cls === 'battleship') aiBattleship(state, ship, dt);
        else if (ship.cls === 'bomber') aiBomber(state, ship, dt);
        else aiInterceptor(state, ship, dt);
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
  if (ship.cls === 'destroyer') aiDestroyer(state, ship, dt);
  else if (ship.cls === 'frigate') aiFrigate(state, ship, dt);
  else if (ship.cls === 'battleship') aiBattleship(state, ship, dt);
  else if (ship.cls === 'bomber') aiBomber(state, ship, dt);
  else aiInterceptor(state, ship, dt);
}

