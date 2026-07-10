/* Praedra sim module: physics — integration, collisions, asteroids, gravity — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

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
        var dmg = 0;
        if (o.moving && closing > D.impactMinSpeed) {
          dmg = Math.min(D.impactDamageCap || Infinity,
                         D.impactDamageScale * (o.r * o.r / 1000) * closing); // shrapnel strike
        } else if (closing > C.shipRockMinImpactSpeed) {
          // a real BONK, not field creep: universal gravity keeps whole fields drifting
          // at 3-25 px/s, and sub-threshold contact must push, never sandpaper — heavy
          // hulls were ground to death by their own parked neighbourhood (KE ~ v^2)
          dmg = Math.min(C.shipRockDamageCap, C.shipRockDamageScale * closing * (mShip / 50));
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
  var cfg = state.config, D = cfg.debris, C2 = cfg.collision, W = cfg.arena.w, H = cfg.arena.h;
  var A = state.asteroids;
  var anyMoving = 0;
  for (var i = 0; i < A.length; i++) {
    var o = A[i];
    if (!o.alive) continue;
    o.rot += o.rotVel * dt;   // idle tumble runs for EVERY rock — vacuum: spin never decays
    if (!o.moving) continue;
    anyMoving++;
    o.x += o.vx * dt; o.y += o.vy * dt;
    var damp = Math.max(0, 1 - D.drag * dt);
    o.vx *= damp; o.vy *= damp; // translational damping only: the gameplay brake that lets
                                // debris settle; rotVel is exempt (nothing to slow it in space)
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
        } else if ((q.r < C2.rockAnchorRadius || q.everMoved) && len(q.vx - jimp * nx / mq, q.vy - jimp * ny / mq) > 2) {
          // the hit is hard enough to genuinely dislodge it — but ANCHOR-class rocks
          // (titan) never move for a collision: universal gravity feeds fast heavy
          // infall that legally cleared the old velocity bar, and a dislodged titan
          // then ratcheted across the map against its own accretion shell
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
        // vs settled terrain the mover takes the separation — but a HUGE mover overlapping
        // a pebble field must GRIND through (capped step), not take the full overlap from
        // every pebble each tick (that shoved a woken titan 500px in 5 ticks)
        p.x += nx * (p.r > q.r * 3 ? Math.min(push, 6) : push);
        p.y += ny * (p.r > q.r * 3 ? Math.min(push, 6) : push);
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
    // UNIVERSAL tail: the physics cutoff is the farMinAccel horizon (a per-source bound
    // that scales with mass — the titan's exceeds any arena, a BIG's self-limits at ~5k px).
    // `reach` survives as the AI/render/wake "strong well" boundary, NOT an accel bound.
    var reach = o.r * GR.wellReach;
    var maxD2 = (GR.G * m) / (GR.farMinAccel || GR.minAccel);
    out.push({ o: o, m: m, soft2: soft * soft, maxD2: maxD2,
               reach: reach, reach2: reach * reach });
  }
  return out;
}
/* Sum field accel at (x,y) into GVEC; excludeId skips a source pulling on itself.
   GVEC.wakeOK: some source AT LEAST as big as bodyR contributes from INSIDE its own
   reach — the settled-rock wake gate. >= (not >) makes equal masses fall toward each
   other (mutual attraction); the titan, strictly bigger than everything, is never
   stirred and stays the map's anchor. The far 1/d^2 tail (past reach) pulls on every
   FREE body but never wakes parked terrain: far-tail accels sit in the sub-rockWake
   band where creep stalls on drag and re-settles (the documented thrash zone). */
function gravityAt(GR, srcs, x, y, excludeId, bodyR) {
  var ax = 0, ay = 0, wakeOK = false;
  for (var i = 0; i < srcs.length; i++) {
    var s = srcs[i];
    if (s.o.id === excludeId) continue;
    var dx = s.o.x - x, dy = s.o.y - y;
    var d2 = dx * dx + dy * dy;
    if (d2 > s.maxD2 || d2 < 1e-6) continue;
    var a = GR.G * s.m / (d2 + s.soft2);   // smooth 1/d^2 all the way out — no edge, no taper
    if (a > GR.maxAccel) a = GR.maxAccel;
    var d = Math.sqrt(d2);
    if (s.o.r >= bodyR && d2 <= s.reach2) wakeOK = true;
    ax += (dx / d) * a; ay += (dy / d) * a;
  }
  GVEC.ax = ax; GVEC.ay = ay; GVEC.wakeOK = wakeOK;
}
function applyGravity(state, dt) {
  var GR = state.config.gravity;
  var srcs = gravitySources(state);
  state.gravSources = srcs;                 // renderers/inspectors read this; sim-internal otherwise
  var i, wake2 = GR.rockWake * GR.rockWake;
  var ships = state.ships;
  if (srcs.length) for (i = 0; i < ships.length; i++) {
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
  if (srcs.length) for (i = 0; i < A.length; i++) {
    var o = A[i];
    if (!o.alive) continue;
    // settled rocks: run the wake evaluation every 3rd tick, staggered by id — with the
    // shell gone, WHOLE fields are wake-eligible and a per-tick field+support check for
    // every parked rock is pure waste. A <=2-tick wake latency is invisible at creep speed.
    if (!o.moving && ((state.tick + o.id) % 3) !== 0) continue;
    // slow creepers integrate the (slowly-varying) field every 3rd tick at 3x dt — the
    // universal tail keeps far debris drifting for minutes, and per-tick field math for
    // a whole creeping map is the wallclock hot spot. Fast debris integrates every tick.
    var slow = o.moving && (o.vx * o.vx + o.vy * o.vy) < 900;
    if (slow && ((state.tick + o.id) % 3) !== 0) continue;
    var gdt = (o.moving && ((state.tick + o.id) % 3) === 0 && slow) ? dt * 3 : dt;
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
        // ROTATION-STABLE support bound (review finding): the exact contour rotates with
        // idle tumble and flipped supported on/off, thrashing settled piles in wells.
        // Generous static bound: anything at most touching-distance down-well holds you.
        var reach = (o.r + q.r) * 1.25 + 4;
        if (dd2 > reach * reach) return false;
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
    o.vx += GVEC.ax * GR.rockMult * gdt;
    o.vy += GVEC.ay * GR.rockMult * gdt;
  }
  // Saturn-ring accretion: local MUTUAL gravity among sub-source debris. Movers drive
  // the pass — a drifting rock tugs its neighborhood toward itself (and is tugged back),
  // and can DISLODGE a parked pebble when its pull alone beats the rockWake floor (same
  // thrash-safe threshold as the wells: dislodged creep survives drag). Settled-settled
  // pairs never interact, so parked fields stay parked until something moves nearby —
  // "when an object moves, it influences everything around it." Grid-bounded, K-capped.
  var AR = GR.debrisAccretionRadius || 0;
  var dmG = (GR.debrisMult || 0) * GR.G;
  if (AR > 0 && dmG > 0) {
    var K = GR.debrisNeighborCap || 6;
    var ddt = dt * 2; // pass runs every 2nd tick per mover (staggered) — see below
    for (i = 0; i < A.length; i++) {
      var p = A[i];
      if (!p.alive || !p.moving || p.r >= GR.sourceMinRadius) continue;
      if ((state.tick + p.id) & 1) continue;   // amortized: local field varies slowly
      var pm3 = p.r * p.r * p.r, taken = 0;
      rocksNearSeg(state, p.x, p.y, p.x, p.y, AR + 40, function (q) {
        if (taken >= K) return true;
        if (q.id === p.id || q.r >= GR.sourceMinRadius) return false;
        var dx = q.x - p.x, dy = q.y - p.y;
        var d2 = dx * dx + dy * dy;
        if (d2 > AR * AR || d2 < 1) return false;
        taken++;
        var d = Math.sqrt(d2), ux = dx / d, uy = dy / d;
        var softL = 0.5 * (p.r + q.r);             // shared softening: no point-blank slingshots
        var s2 = d2 + softL * softL;
        // mover-mover pairs are visited from BOTH sides at HALF strength (review finding:
        // the old lower-id-owns-the-pair rule silently dropped pairs whose owner had
        // exhausted its neighbor budget) — two half-passes sum to one full application
        var mm = q.moving ? 0.5 : 1;
        var aOnP = dmG * (q.r * q.r * q.r) / s2 * mm, aOnQ = dmG * pm3 / s2 * mm;
        p.vx += ux * aOnP * ddt; p.vy += uy * aOnP * ddt;
        if (q.moving) {
          q.vx -= ux * aOnQ * ddt; q.vy -= uy * aOnQ * ddt;
        } else if (aOnQ >= GR.rockWake) {
          q.moving = true;
          q.vx -= ux * 4; q.vy -= uy * 4;          // starter kick (see the wake above)
          rockWoke(state, q);
        }
        return false;
      });
    }
  }
  if (!srcs.length) return;
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

