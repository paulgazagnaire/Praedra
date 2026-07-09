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
      var ddx = o.x - ship.x, ddy = o.y - ship.y;
      var dd = Math.sqrt(ddx * ddx + ddy * ddy);
      if (dd - rr > sp * probeT) return false;
      if (segCircleHit(ship.x, ship.y, pex, pey, o.x, o.y, rr)) {
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
  burnToward(ship, burnAng, clamp(awm / accel, 0, 1), dt);
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

/* ---------------- Integration + collisions ---------------- */
function integrateShip(state, ship, dt) {
  var def = ship.def, cfg = state.config;
  // rotation (pinned ships may rotate to aim, per contract)
  var maxAngA = def.rcs / def.mass;
  ship.angVel = clamp(ship.angVel + maxAngA * clamp(ship.ctl.torque, -1, 1) * dt, -def.turnMax, def.turnMax);
  ship.heading = normAngle(ship.heading + ship.angVel * dt);

  ship.throttle = ship.pinned ? 0 : clamp(ship.ctl.thrust, 0, 1);
  if (ship.pinned) { ship.vx = 0; ship.vy = 0; ship.speed = 0; return; }

  if (cfg.movementModel === 'arcade') {
    // debug fallback: direct kinematics, no momentum
    var wantX = ship.ctl.desiredVX, wantY = ship.ctl.desiredVY;
    ship.vx = wantX; ship.vy = wantY;
    if (len(wantX, wantY) > 5) ship.heading = Math.atan2(wantY, wantX);
  } else {
    var a = (def.thrust / def.mass) * clamp(ship.ctl.thrust, 0, 1);
    var ax = Math.cos(ship.heading) * a, ay = Math.sin(ship.heading) * a;
    ship.vx += ax * dt; ship.vy += ay * dt;
    // lateral-accel EMA (torpedo predictability + bomb aim skew read this)
    var s = len(ship.vx, ship.vy);
    var alat = s > 20 ? Math.abs(ax * (ship.vy / s) - ay * (ship.vx / s)) : a;
    ship.jinkEMA += (alat - ship.jinkEMA) * Math.min(1, dt / 0.4);
  }
  ship.x += ship.vx * dt; ship.y += ship.vy * dt;
  ship.speed = len(ship.vx, ship.vy);

  // arena walls: clamp + damp
  var W = cfg.arena.w, H = cfg.arena.h, r = def.radius;
  if (ship.x < r) { ship.x = r; ship.vx = Math.abs(ship.vx) * 0.4; }
  if (ship.x > W - r) { ship.x = W - r; ship.vx = -Math.abs(ship.vx) * 0.4; }
  if (ship.y < r) { ship.y = r; ship.vy = Math.abs(ship.vy) * 0.4; }
  if (ship.y > H - r) { ship.y = H - r; ship.vy = -Math.abs(ship.vy) * 0.4; }
}

function collideShipsAndRocks(state, dt) {
  var cfg = state.config, C = cfg.collision, D = cfg.debris;
  var W = cfg.arena.w, H = cfg.arena.h;
  var ships = state.ships, rocks = state.asteroids;
  for (var i = 0; i < ships.length; i++) {
    var sh = ships[i];
    if (!sh.alive || sh.pinned) continue;
    var near = [];
    rocksNearSeg(state, sh.x, sh.y, sh.x, sh.y, sh.def.radius + 96, function (o) { near.push(o); return false; });
    for (var j = 0; j < near.length; j++) {
      var o = near[j];
      var dx = sh.x - o.x, dy = sh.y - o.y;
      var d2 = dx * dx + dy * dy;
      var rrMax = sh.def.radius + o.r * 1.22;          // cheap bound, then exact contour
      if (d2 >= rrMax * rrMax || d2 < 1e-9) continue;
      var rr = sh.def.radius + contourR(o, sh.x, sh.y); // hulls bonk the lumps, not the circle
      if (d2 >= rr * rr) continue;
      var d = Math.sqrt(d2), nx = dx / d, ny = dy / d;
      var mShip = sh.def.mass, mRock = o.r * o.r / 10;
      // settled rocks are immovable walls — except pebbles, which a hull shoves aside
      var invS = 1 / mShip, invR = (o.moving || o.r < C.pushableRockRadius) ? 1 / mRock : 0;
      var rvx = sh.vx - o.vx, rvy = sh.vy - o.vy;
      var vn = rvx * nx + rvy * ny;
      var closing = -vn;
      if (vn < 0) {
        var jimp = -(1 + C.shipRockRestitution) * vn / (invS + invR);
        sh.vx += jimp * invS * nx; sh.vy += jimp * invS * ny;
        o.vx -= jimp * invR * nx; o.vy -= jimp * invR * ny;
        if (invR > 0 && !o.moving && len(o.vx, o.vy) > 2) { o.moving = true; rockWoke(state, o); }
        var dmg;
        if (o.moving && closing > D.impactMinSpeed) {
          dmg = D.impactDamageScale * (o.r * o.r / 1000) * closing; // shrapnel strike
        } else {
          dmg = Math.min(C.shipRockDamageCap, C.shipRockDamageScale * closing * (mShip / 50)); // bonk
        }
        if (dmg > 0.3) applyDamage(state, sh, dmg, null, 'rock');
      }
      var push = (rr - d) + 0.5;
      var wS = invR > 0 ? invS / (invS + invR) : 1;
      var pnx = nx, pny = ny;
      if (invR === 0) {
        // edge-cut titan wedge: if the radial exit points through the arena wall the
        // clamp just feeds the ship back in — slide tangentially along the contour
        var txp = sh.x + nx * push * wS, typ = sh.y + ny * push * wS;
        var rW = sh.def.radius;
        if (txp < rW || txp > W - rW || typ < rW || typ > H - rW) {
          var tside = (sh.vx * -ny + sh.vy * nx) >= 0 ? 1 : -1;
          pnx = -ny * tside; pny = nx * tside;
        }
      }
      sh.x += pnx * push * wS;
      sh.y += pny * push * wS;
      if (invR > 0) { o.x -= nx * push * (invR / (invS + invR)); o.y -= ny * push * (invR / (invS + invR)); }
    }
    // ship-ship separation (no damage)
    for (var k = i + 1; k < ships.length; k++) {
      var ot = ships[k];
      if (!ot.alive) continue;
      var dx2 = sh.x - ot.x, dy2 = sh.y - ot.y;
      var rr2 = sh.def.radius + ot.def.radius;
      var dd2 = dx2 * dx2 + dy2 * dy2;
      if (dd2 >= rr2 * rr2 || dd2 < 1e-9) continue;
      var dd = Math.sqrt(dd2), nx2 = dx2 / dd, ny2 = dy2 / dd;
      var push2 = (rr2 - dd) / 2 + 0.25;
      if (!sh.pinned) { sh.x += nx2 * push2; sh.y += ny2 * push2; }
      if (!ot.pinned) { ot.x -= nx2 * push2; ot.y -= ny2 * push2; }
      var rvn = (sh.vx - ot.vx) * nx2 + (sh.vy - ot.vy) * ny2;
      if (rvn < 0) {
        var jj = -(1 + C.shipShipRestitution) * rvn / (1 / sh.def.mass + 1 / ot.def.mass);
        if (!sh.pinned) { sh.vx += jj * nx2 / sh.def.mass; sh.vy += jj * ny2 / sh.def.mass; }
        if (!ot.pinned) { ot.vx -= jj * nx2 / ot.def.mass; ot.vy -= jj * ny2 / ot.def.mass; }
      }
    }
  }
}

function updateAsteroids(state, dt) {
  var cfg = state.config, D = cfg.debris, W = cfg.arena.w, H = cfg.arena.h;
  var A = state.asteroids;
  var anyMoving = 0;
  for (var i = 0; i < A.length; i++) {
    var o = A[i];
    if (!o.alive || !o.moving) continue;
    anyMoving++;
    o.x += o.vx * dt; o.y += o.vy * dt;
    o.rot += o.rotVel * dt;
    var damp = Math.max(0, 1 - D.drag * dt);
    o.vx *= damp; o.vy *= damp; o.rotVel *= damp;
    if (o.x < o.r) { o.x = o.r; o.vx = Math.abs(o.vx) * D.restitution; }
    if (o.x > W - o.r) { o.x = W - o.r; o.vx = -Math.abs(o.vx) * D.restitution; }
    if (o.y < o.r) { o.y = o.r; o.vy = Math.abs(o.vy) * D.restitution; }
    if (o.y > H - o.r) { o.y = H - o.r; o.vy = -Math.abs(o.vy) * D.restitution; }
    if (len(o.vx, o.vy) < 2) { o.vx = 0; o.vy = 0; o.moving = false; state.gridDirty = true; } // rejoin the static grid
  }
  state.movingRocks = anyMoving;
  if (!anyMoving) return;
  // rock-rock collisions (momentum-conserving; only pairs involving a mover).
  // Grid query per mover, not the all-pairs sweep: gravity keeps a few dozen rocks
  // creeping into the wells for long stretches and O(n^2) over 200+ rocks adds up.
  for (var a2 = 0; a2 < A.length; a2++) {
    var p = A[a2];
    if (!p.alive || !p.moving) continue;
    // creeping rocks collide-check every 2nd tick (staggered by id): gravity keeps whole
    // fields drifting at <0.5px/tick where penetration risk is nil, and this pass is the
    // per-tick hot loop. Fast debris (shatter bursts) keeps the every-tick check.
    if ((p.vx * p.vx + p.vy * p.vy) < 900 && ((state.tick + p.id) & 1)) continue;
    rocksNearSeg(state, p.x, p.y, p.x, p.y, p.r * 1.25 + 6, function (q) {
      if (q.id === p.id) return false;
      if (q.moving && q.id < p.id) return false;  // mover-mover pair: handled once, from the lower id
      var dx = p.x - q.x, dy = p.y - q.y;
      var d2 = dx * dx + dy * dy;
      var rrMax = (p.r + q.r) * 1.22;             // cheap bound, then exact contour:
      if (d2 >= rrMax * rrMax || d2 < 1e-9) return false;
      var rr = pairContact(state, p, q);          // accretion hugs the lumps (cached contour)
      if (d2 >= rr * rr) return false;
      var d = Math.sqrt(d2), nx = dx / d, ny = dy / d;
      var mp = p.r * p.r, mq = q.r * q.r;
      var vn = (p.vx - q.vx) * nx + (p.vy - q.vy) * ny;
      if (vn < 0) {
        var jimp = -(1 + D.restitution) * vn / (1 / mp + 1 / mq);
        p.vx += jimp * nx / mp; p.vy += jimp * ny / mp;
        if (q.moving) {
          q.vx -= jimp * nx / mq; q.vy -= jimp * ny / mq;
        } else if (len(q.vx - jimp * nx / mq, q.vy - jimp * ny / mq) > 2) {
          // the hit is hard enough to genuinely dislodge it
          q.vx -= jimp * nx / mq; q.vy -= jimp * ny / mq;
          q.moving = true; rockWoke(state, q);
        } // else: settled rock is terrain — it shrugs the tap off entirely
      }
      // separation: movers split by mass; a still-settled rock absorbs nothing —
      // sustained accretion jitter was walking the titan ~1px/s across the map
      var push = (rr - d) + 0.5;
      if (q.moving) {
        var wp = (1 / mp) / (1 / mp + 1 / mq);
        p.x += nx * push * wp; p.y += ny * push * wp;
        q.x -= nx * push * (1 - wp); q.y -= ny * push * (1 - wp);
      } else {
        p.x += nx * push; p.y += ny * push;
      }
      return false;
    });
  }
}
/* ---------------- Gravity (BIG rocks bend trajectories; everything with mass feels it) ----------------
   Sources: live rocks with r >= gravity.sourceMinRadius (the BIG asteroids + their
   first-generation fragments). Mass = r^3, accel = G*m/(d^2 + (softening*r)^2), clamped.
   Bodies: ships (autopilot fights the drift — that IS the navigation impact), drifting
   rocks (debris curls into the wells and accretes), settled rocks ANYWHERE in a well
   where the pull beats rockWake (whole fields creep and accrete — nothing in a well is
   exempt; only down-well-supported piles and the sub-wake fringe rest), and projectiles
   (torpedoes/bombs bend course but keep design speed — guidance/lead-aim survive). */
var GVEC = { ax: 0, ay: 0, wakeOK: false };
function gravitySources(state) {
  var GR = state.config.gravity;
  var out = [];
  if (!GR || GR.G <= 0) return out;
  var A = state.asteroids;
  for (var i = 0; i < A.length; i++) {
    var o = A[i];
    if (!o.alive || o.r < GR.sourceMinRadius) continue;
    var m = o.r * o.r * o.r;
    var soft = GR.softening * o.r;
    // well ends at the earlier of the minAccel horizon and the bounded reach —
    // an unbounded titan-mass well would swallow the whole map
    var reach = o.r * GR.wellReach;
    var maxD2 = Math.min((GR.G * m) / GR.minAccel, reach * reach);
    out.push({ o: o, m: m, soft2: soft * soft, maxD2: maxD2,
               reach: reach, fade0: reach * 0.85 });
  }
  return out;
}
/* Sum field accel at (x,y) into GVEC; excludeId skips a source pulling on itself.
   GVEC.wakeOK: some contributing source is strictly BIGGER than bodyR — anything in a
   well falls toward what out-masses it (pebbles onto BIGs, BIGs onto the titan), but a
   monster is never stirred by its lessers, so the titan stays the map's fixed anchor. */
function gravityAt(GR, srcs, x, y, excludeId, bodyR) {
  var ax = 0, ay = 0, wakeOK = false;
  for (var i = 0; i < srcs.length; i++) {
    var s = srcs[i];
    if (s.o.id === excludeId) continue;
    var dx = s.o.x - x, dy = s.o.y - y;
    var d2 = dx * dx + dy * dy;
    if (d2 > s.maxD2 || d2 < 1e-6) continue;
    var a = GR.G * s.m / (d2 + s.soft2);
    if (a > GR.maxAccel) a = GR.maxAccel;
    var d = Math.sqrt(d2);
    if (d > s.fade0) a *= (s.reach - d) / (s.reach - s.fade0); // smooth edge, no cliff
    if (s.o.r > bodyR) wakeOK = true;
    ax += (dx / d) * a; ay += (dy / d) * a;
  }
  GVEC.ax = ax; GVEC.ay = ay; GVEC.wakeOK = wakeOK;
}
function applyGravity(state, dt) {
  var GR = state.config.gravity;
  var srcs = gravitySources(state);
  state.gravSources = srcs;                 // renderers/inspectors read this; sim-internal otherwise
  if (!srcs.length) return;
  var i, wake2 = GR.rockWake * GR.rockWake;
  var ships = state.ships;
  for (i = 0; i < ships.length; i++) {
    var sh = ships[i];
    if (!sh.alive || sh.pinned) continue;   // pinned ships hold station by contract
    gravityAt(GR, srcs, sh.x, sh.y, -1, 0);
    var gax = GVEC.ax * GR.shipMult, gay = GVEC.ay * GR.shipMult;
    // escape guarantee: a ship can always out-burn the well (see shipEscapeCap)
    var gcap = (sh.def.thrust / sh.def.mass) * GR.shipEscapeCap;
    var gm = len(gax, gay);
    if (gm > gcap) { gax *= gcap / gm; gay *= gcap / gm; }
    sh.vx += gax * dt;
    sh.vy += gay * dt;
  }
  var A = state.asteroids;
  for (i = 0; i < A.length; i++) {
    var o = A[i];
    if (!o.alive) continue;
    // settled rocks: run the wake evaluation every 3rd tick, staggered by id — with the
    // shell gone, WHOLE fields are wake-eligible and a per-tick field+support check for
    // every parked rock is pure waste. A <=2-tick wake latency is invisible at creep speed.
    if (!o.moving && ((state.tick + o.id) % 3) !== 0) continue;
    gravityAt(GR, srcs, o.x, o.y, o.id, o.r);
    var g2 = GVEC.ax * GVEC.ax + GVEC.ay * GVEC.ay;
    if (g2 <= 0) continue;
    if (!o.moving) {
      if (g2 < wake2 || !GVEC.wakeOK) continue; // sub-wake fringe: creep would stall on drag
      // supported: resting against something on the down-well side (the source itself,
      // or the accretion pile stacked on it) — normal force holds it, don't stir it
      var gl = Math.sqrt(g2), gux = GVEC.ax / gl, guy = GVEC.ay / gl;
      var supported = false;
      rocksNearSeg(state, o.x, o.y, o.x, o.y, o.r * 1.25 + 6, function (q) {
        if (q.id === o.id) return false;
        var ddx = q.x - o.x, ddy = q.y - o.y;
        var dd2 = ddx * ddx + ddy * ddy;
        var loose = (o.r + q.r) * 1.22 + 4;       // cheap bound before the contour math
        if (dd2 > loose * loose) return false;
        var reach2 = pairContact(state, o, q) + 4; // contact on the contour (cached)
        if (dd2 > reach2 * reach2) return false;
        if (ddx * gux + ddy * guy > 0) { supported = true; return true; }
        return false;
      });
      if (supported) continue;
      o.moving = true;
      // starter kick along the pull: below the settle threshold (2 px/s) the very next
      // updateAsteroids would zero the nascent creep and re-pin the rock every tick
      o.vx += gux * 4; o.vy += guy * 4;
      rockWoke(state, o);
    }
    o.vx += GVEC.ax * GR.rockMult * dt;
    o.vy += GVEC.ay * GR.rockMult * dt;
  }
  var torps = state.torps, T = state.config.torpedo;
  for (i = 0; i < torps.length; i++) {
    var tp = torps[i];
    if (!tp.alive) continue;
    gravityAt(GR, srcs, tp.x, tp.y, -1, 0);
    if (GVEC.ax === 0 && GVEC.ay === 0) continue;
    var tvx = tp.vx + GVEC.ax * GR.projectileMult * dt;
    var tvy = tp.vy + GVEC.ay * GR.projectileMult * dt;
    var tl = len(tvx, tvy) || 1;            // bend, don't accelerate: powered flight
    tp.vx = (tvx / tl) * T.speed; tp.vy = (tvy / tl) * T.speed;
    tp.heading = Math.atan2(tp.vy, tp.vx);  // guided torps re-steer from here next tick
  }
  var bombs = state.bombs, B = state.config.bomb;
  for (i = 0; i < bombs.length; i++) {
    var bm = bombs[i];
    if (!bm.alive) continue;
    gravityAt(GR, srcs, bm.x, bm.y, -1, 0);
    if (GVEC.ax === 0 && GVEC.ay === 0) continue;
    var bvx = bm.vx + GVEC.ax * GR.projectileMult * dt;
    var bvy = bm.vy + GVEC.ay * GR.projectileMult * dt;
    var bl = len(bvx, bvy) || 1;
    bm.vx = (bvx / bl) * B.speed; bm.vy = (bvy / bl) * B.speed;
  }
}

/* ---------------- Damage, AOE, asteroid destruction ---------------- */
