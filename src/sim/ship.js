/* Praedra sim module: ship — construction + Newtonian autopilot — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- Ship construction ---------------- */
function makeShip(state, cls, team, x, y, heading) {
  var def = state.config.ships[cls];
  if (!def) throw new Error('unknown ship class: ' + cls);
  var tcfg = state.config.torpedo[cls];
  var s = {
    id: state.nextId++, team: team, cls: cls, def: def,
    x: x, y: y, vx: 0, vy: 0, heading: heading || 0, angVel: 0,
    hp: def.hp, maxHp: def.hp, alive: true, pinned: false,
    jinkEMA: 0, speed: 0,
    cool: { rail: 0, torp: 0, bomb: 0, gat: 0, pd: 0, salvoLeft: 0, salvoGap: 0 },
    torpAmmo: (tcfg && tcfg.ammo !== undefined) ? tcfg.ammo : -1, // -1 = cooldown-limited
    damageDealt: 0,
    ai: { targetId: -1, retargetAt: 0, mode: 'seek', modeAt: 0, noLosSince: 0, jinkPhase: 0, wp: null },
    ctl: { torque: 0, thrust: 0, face: null, desiredVX: 0, desiredVY: 0 },
    turrets: null, // battleship heavy-rail turrets: [{ ang, cool, load, lost }], index 0 = bow (see below)
    nav: null, // {x, y, arrive, speedCap, face, jink}
  };
  // Heavy-rail turrets, mirroring the lazy pdTrack pattern. Deterministic cooldown stagger:
  // turret i starts cool = i*(cooldown/turrets) so the guns fire at 0.00 / 1.67 / 3.33 s phases.
  // `ang` is the world-space turret facing (renderer draws it) and spawns at the turret's own
  // arc centre (bow pair forward, aft turret astern); `cool` is seconds until the breech is
  // ready; `load` is the rail-charge progress toward loadTime; `lost` times a broken solution.
  if (cls === 'battleship') {
    var HR = state.config.heavyRail;
    s.turrets = [];
    for (var ti = 0; ti < HR.turrets; ti++)
      s.turrets.push({ ang: normAngle((heading || 0) + ((HR.arcCenters && HR.arcCenters[ti]) || 0)),
                       cool: ti * (HR.cooldown / HR.turrets), load: 0, lost: 0 });
  }
  return s;
}

/* ---------------- Autopilot (Newtonian: rotate-to-burn, flip-and-burn arrival) ---------------- */
/* Extra clearance when rounding a gravity source: the well drags the ship into the rim. */
function massivePad(cfg, o) {
  return o.r >= cfg.gravity.sourceMinRadius ? cfg.ai.bigRockAvoidPad : 0;
}
function autopilot(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, def = ship.def;
  var goal = ship.nav;
  var accel = def.thrust / def.mass;
  var aBrake = accel * AI.navBrakeMargin;
  var flipTime = Math.PI / def.turnMax;
  ship.ctl.torque = 0; ship.ctl.thrust = 0;

  if (!goal) { faceToward(ship, ship.ctl.face, dt); return; }

  var dx = goal.x - ship.x, dy = goal.y - ship.y;
  var d = Math.sqrt(dx * dx + dy * dy);
  var cruise = Math.min(def.maxCruiseSpeed, goal.speedCap || def.maxCruiseSpeed);

  // Target speed: full cruise in transit; flip-and-burn arrival solves
  // v^2/(2a) + v*tFlip <= d  =>  v = a*(sqrt(tFlip^2 + 2d/a) - tFlip)
  var vGoal;
  var dr = Math.max(0, d - AI.navArriveSlack);
  if (goal.arrive) vGoal = Math.min(cruise, aBrake * (Math.sqrt(flipTime * flipTime + 2 * dr / aBrake) - flipTime));
  else vGoal = cruise;

  // --- waypoint detour: if the straight path is blocked, swing around the rock's rim
  //     (a goal just past an obstacle's edge is otherwise a local trap) ---
  var gx = goal.x, gy = goal.y;
  if (ship.wpCacheT !== undefined && state.tick - ship.wpCacheT < 6 &&
      Math.abs(goal.x - (ship.wpGoalX || 0)) < 40 && Math.abs(goal.y - (ship.wpGoalY || 0)) < 40) {
    gx = ship.wpX; gy = ship.wpY;
  } else if (d > AI.navArriveSlack * 2) {
    var blk = null, blkT = Infinity;
    rocksNearSeg(state, ship.x, ship.y, goal.x, goal.y, 92 + def.radius + AI.avoidMargin + AI.bigRockAvoidPad, function (ob) {
      var rInf = ob.r + def.radius + AI.avoidMargin * (def.avoidMult || 1) + massivePad(cfg, ob);
      var huge = ob.r >= AI.hugeRockRadius;
      // skip small rocks we're already inside the margin of (collision/probe handles
      // those) — but a HUGE rock must stay in play precisely when we're at its rim
      var dRock = dist(ship.x, ship.y, ob.x, ob.y);
      if (dRock < rInf - 5 && !huge) return false;
      if (dist(goal.x, goal.y, ob.x, ob.y) < rInf - 12 && !huge) return false; // goal snug: go direct
      if (segCircleHit(ship.x, ship.y, goal.x, goal.y, ob.x, ob.y, rInf - 8)) {
        if (dRock < blkT) { blkT = dRock; blk = ob; }
      }
      return false;
    });
    if (blk) {
      var rI = blk.r + def.radius + AI.avoidMargin + massivePad(cfg, blk);
      if (blk.r >= AI.hugeRockRadius) {
        // tangent-bug rim-following: walk the inflated rim toward the goal side,
        // with a sticky side choice so the ship commits to one way around. The
        // ride radius must clear the velocity-probe margin (which scales with
        // avoidMult) or avoidance braking throttles the whole transit to a crawl.
        var rRide = blk.r + def.radius + AI.avoidMargin * (def.avoidMult || 1) + massivePad(cfg, blk) + 50;
        var side2;
        if (ship.rimRock === blk.id && state.time < ship.rimUntil) side2 = ship.rimSide;
        else {
          var angS = Math.atan2(ship.y - blk.y, ship.x - blk.x);
          var angG = Math.atan2(goal.y - blk.y, goal.x - blk.x);
          side2 = normAngle(angG - angS) > 0 ? 1 : -1;
          ship.rimRock = blk.id; ship.rimSide = side2;
        }
        ship.rimUntil = state.time + 2.5; // stays committed while continuously engaged
        var step2 = clamp(380 / rRide, 0.2, 0.9);
        var na = Math.atan2(ship.y - blk.y, ship.x - blk.x) + side2 * step2;
        gx = blk.x + Math.cos(na) * rRide;
        gy = blk.y + Math.sin(na) * rRide;
      } else {
        var t = ((blk.x - ship.x) * dx + (blk.y - ship.y) * dy) / (d * d);
        t = clamp(t, 0, 1);
        var prx = ship.x + dx * t - blk.x, pry = ship.y + dy * t - blk.y;
        var pl = len(prx, pry);
        if (pl < 1) { prx = -dy / d; pry = dx / d; pl = 1; }
        gx = blk.x + (prx / pl) * (rI + 22);
        gy = blk.y + (pry / pl) * (rI + 22);
      }
    }
    ship.wpCacheT = state.tick; ship.wpGoalX = goal.x; ship.wpGoalY = goal.y;
    ship.wpX = gx; ship.wpY = gy;
  }
  var ddx = gx - ship.x, ddy = gy - ship.y;
  var dd = Math.sqrt(ddx * ddx + ddy * ddy);
  var dirx = dd > 1e-6 ? ddx / dd : Math.cos(ship.heading), diry = dd > 1e-6 ? ddy / dd : Math.sin(ship.heading);

  // --- stall-breaker: parked against a rock far from the goal -> sidestep hard.
  //     COMMIT to one side for the whole stall episode: alternating sides every
  //     couple of seconds cancels itself out, and against a gravity well (thrust
  //     barely above the pull) that dither pinned capitals to the rim for minutes.
  //     Suppressed while rim-following a huge rock — the rim path IS the escape,
  //     and a 77-degree sidestep off it just dives back into the well ---
  var rimming = state.time < (ship.rimUntil || -1);
  if (ship.speed < 18 && d > AI.navArriveSlack * 3) ship.stallT = (ship.stallT || 0) + dt;
  else { ship.stallT = 0; ship.stallSide = 0; }
  if (ship.stallT > 1.5 && !rimming) {
    var stallEp = Math.floor((ship.stallT - 1.5) / 9); // long episodes: a destroyer needs ~5s just to turn around
    if (!ship.stallSide) {
      // sidestep AWAY from whatever we're pinned against; id parity in open space
      var pin = null, pinD = Infinity;
      rocksNearSeg(state, ship.x, ship.y, ship.x, ship.y, def.radius + 260, function (ob) {
        var dd = dist(ship.x, ship.y, ob.x, ob.y) - ob.r;
        if (dd < pinD) { pinD = dd; pin = ob; }
        return false;
      });
      if (pin && pinD < 200)
        ship.stallSide = (dirx * (pin.y - ship.y) - diry * (pin.x - ship.x)) > 0 ? -1 : 1;
      else ship.stallSide = ((ship.id + Math.floor(state.time / 2)) % 2 === 0) ? 1 : -1;
      ship.stallEp = stallEp;
    } else if (stallEp !== ship.stallEp) {
      ship.stallEp = stallEp;
      ship.stallSide = -ship.stallSide;  // 6s of futility: try the other way around
    }
    var sside = ship.stallSide;
    var rx = dirx * Math.cos(sside * 1.35) - diry * Math.sin(sside * 1.35);
    var ry = dirx * Math.sin(sside * 1.35) + diry * Math.cos(sside * 1.35);
    dirx = rx; diry = ry;
    vGoal = Math.max(vGoal, cruise * 0.5);
  }

  // --- solid-rock avoidance: probe along current velocity, steer around + shed speed ---
  var sp = ship.speed;
  var avoiding = false;
  var probeT = AI.avoidLookahead;
  if (sp > 15) {
    var pex = ship.x + ship.vx * probeT, pey = ship.y + ship.vy * probeT;
    var threat = null, threatD = Infinity;
    rocksNearSeg(state, ship.x, ship.y, pex, pey, 92 + def.radius + AI.avoidMargin * (def.avoidMult || 1) + AI.bigRockAvoidPad, function (o) {
      // don't weave around a harmless pushable pebble: the hull shoves it aside for a small bonk,
      // and steering around a shattered-debris field (which lane-clearing deliberately spawns) pins
      // a slow capital in its own wreckage. Fast fragments (>= impactMinSpeed, a real shrapnel
      // threat) are still avoided; a settled or slow-drifting pebble is not.
      if (o.r < cfg.collision.pushableRockRadius && len(o.vx, o.vy) < cfg.debris.impactMinSpeed) return false;
      var rr = o.r + def.radius + AI.avoidMargin * (def.avoidMult || 1) + massivePad(cfg, o);
      // the massive pad is a transit-comfort margin, not a wall: when the GOAL itself
      // legitimately sits inside it (ordered point, cover spot at the rim), drop the
      // pad for this rock or the probe deflects the ship around it forever
      if (rr > o.r + def.radius + AI.avoidMargin && dist(goal.x, goal.y, o.x, o.y) < rr)
        rr = o.r + def.radius + AI.avoidMargin;
      // LEAD moving debris for LIGHTS, and only shards CLOSING on us: a fragment closing
      // head-on used to be invisible to the probe until contact (top fighter killer in the
      // round-1 diagnostics). Lights only — a capital's inflated avoidMult margin already
      // covers drift, and leading its own outward-bursting lane-clearing fragments stalled
      // wall transits. Short horizon (0.5s): drag 0.35 bleeds fragment speed fast, so a
      // linear long-lead lands far past where the shard will really be.
      var ox = o.x, oy = o.y;
      if (o.moving && isLight(ship) && (o.vx || o.vy) &&
          (ship.x - o.x) * o.vx + (ship.y - o.y) * o.vy > 0) {
        var leadT = Math.min(probeT * 0.5, 0.5);
        ox += o.vx * leadT; oy += o.vy * leadT;
      }
      var ddx = ox - ship.x, ddy = oy - ship.y;
      var dd = Math.sqrt(ddx * ddx + ddy * ddy);
      if (dd - rr > sp * probeT) return false;
      if (segCircleHit(ship.x, ship.y, pex, pey, ox, oy, rr)) {
        if (dd < threatD) { threatD = dd; threat = o; }
      }
      return false;
    });
    if (threat) {
      var tx = threat.x - ship.x, ty = threat.y - ship.y;
      var td = Math.sqrt(tx * tx + ty * ty) || 1;
      var vdx = ship.vx / sp, vdy = ship.vy / sp;
      // steer to the side of the velocity vector the rock is NOT on — and COMMIT to
      // that side for a while (per-rock hysteresis kills the dither that stalled ships)
      var side;
      if (ship.avoidRock === threat.id && state.time < ship.avoidUntil) side = ship.avoidSide;
      else {
        side = (vdx * ty - vdy * tx) > 0 ? -1 : 1;
        ship.avoidRock = threat.id; ship.avoidSide = side; ship.avoidUntil = state.time + 0.8;
      }
      var surf = Math.max(6, td - (threat.r + def.radius + AI.avoidMargin + massivePad(cfg, threat)));
      var urg = clamp01(1 - surf / (sp * probeT + 1));
      var avx = vdx * Math.cos(side * (0.5 + urg)) - vdy * Math.sin(side * (0.5 + urg));
      var avy = vdx * Math.sin(side * (0.5 + urg)) + vdy * Math.cos(side * (0.5 + urg));
      dirx = dirx * (1 - urg) + avx * urg;
      diry = diry * (1 - urg) + avy * urg;
      var dl = len(dirx, diry) || 1; dirx /= dl; diry /= dl;
      // never carry more speed toward a wall of rock than we can shed
      vGoal = Math.min(vGoal, Math.sqrt(2 * aBrake * surf) + 25);
      avoiding = true;
    }
  }

  var dvxWant = dirx * vGoal, dvyWant = diry * vGoal;

  // --- jink: square-wave lateral weave for lights under threat (suppressed on steady
  //     runs, half-amplitude while threading rocks so nav still works but the vector
  //     stays torpedo-unpredictable) ---
  if (goal.jink) {
    var ph = state.time / AI.jinkPeriod + ship.ai.jinkPhase;
    var sq = (Math.floor(ph * 2) % 2 === 0) ? 1 : -1;
    var jamp = cruise * 0.55 * (avoiding ? 0.45 : 1);
    dvxWant += -diry * jamp * sq;
    dvyWant += dirx * jamp * sq;
  }

  var dvx = dvxWant - ship.vx, dvy = dvyWant - ship.vy;
  var dvm = Math.sqrt(dvx * dvx + dvy * dvy);

  // --- gravity feed-forward: in a well the burn must buy BOTH the velocity change
  //     and the hover. A gravity-blind burn aims all thrust tangentially, sinks the
  //     ship into the rock it's rounding, and full-throttle "progress" nets zero ---
  var gfx = 0, gfy = 0;
  if (state.gravSources && state.gravSources.length) {
    gravityAt(cfg.gravity, state.gravSources, ship.x, ship.y, -1, 0);
    var gm2 = len(GVEC.ax, GVEC.ay);
    if (gm2 > 0) {
      var gcap2 = accel * cfg.gravity.shipEscapeCap;
      var gs = gm2 > gcap2 ? gcap2 / gm2 : 1; // mirror the cap applyGravity enforces
      gfx = -GVEC.ax * cfg.gravity.shipMult * gs;
      gfy = -GVEC.ay * cfg.gravity.shipMult * gs;
    }
  }
  var awx = dvx * 4 + gfx, awy = dvy * 4 + gfy; // desired accel: dv over ~0.25s + hover
  var awm = len(awx, awy);

  if (dvm < 8 && awm < accel * 0.5) {
    // coast: hold position/velocity, point where the AI wants the nose (aiming)
    if (d < AI.navArriveSlack * 2 && ship.speed > 3 && goal.arrive) {
      // kill residual drift
      burnToward(ship, Math.atan2(-ship.vy, -ship.vx), Math.min(1, ship.speed / (accel * 0.3)), dt);
    } else {
      faceToward(ship, goal.face != null ? goal.face : Math.atan2(diry, dirx), dt);
    }
    return;
  }
  var burnAng = Math.atan2(awy, awx);
  var thrWant = clamp(awm / accel, 0, 1);
  if (goal.faceLock && goal.face != null) {
    // weapon-locked: never rotate the hull away from the firing bearing — thrust the
    // PROJECTION of the desired burn (which already contains the gravity hover) onto the
    // locked heading. A hard angle gate here zeroed the hover term and sank faceLocked
    // destroyers into well rims; the projection gives everything the physics allows.
    // Aim beats maneuver until the lock lifts (role AI only locks while a shot is
    // imminent, so braking happens in gun dead-time).
    faceToward(ship, goal.face, dt);
    var alongH = (awx * Math.cos(ship.heading) + awy * Math.sin(ship.heading)) / accel;
    ship.ctl.thrust = clamp(alongH, 0, 1);
    return;
  }
  burnToward(ship, burnAng, thrWant, dt);
}

/* Rotate toward angle `want` (deadbeat controller on angular velocity); no thrust. */
function faceToward(ship, want, dt) {
  if (want == null) { ship.ctl.torque = clamp(-ship.angVel / ((ship.def.rcs / ship.def.mass) * dt || 1), -1, 1); return; }
  var err = normAngle(want - ship.heading);
  var desiredW = clamp(err * 3.5, -ship.def.turnMax, ship.def.turnMax);
  var maxA = ship.def.rcs / ship.def.mass;
  ship.ctl.torque = clamp((desiredW - ship.angVel) / (maxA * dt), -1, 1);
}
/* Rotate toward burn angle and thrust when roughly aligned. */
function burnToward(ship, burnAng, throttle, dt) {
  faceToward(ship, burnAng, dt);
  var err = Math.abs(normAngle(burnAng - ship.heading));
  ship.ctl.thrust = err < 0.35 ? throttle : (err < 0.85 ? throttle * 0.35 : 0);
}

