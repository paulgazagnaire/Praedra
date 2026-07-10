/* Praedra sim module: weapons — damage/AOE, railguns, torpedoes, bombs, gatling, PD — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- Damage, AOE, asteroid destruction ---------------- */
function pushEvent(state, ev) {
  ev.t = state.tick;
  state.events.push(ev);
  if (state.events.length > 500) state.events.splice(0, 250);
}

function applyDamage(state, ship, dmg, attacker, src) {
  if (!ship.alive || dmg <= 0) return;
  ship.hp -= dmg;
  var by = src || 'other';
  var dt = state.stats.dmgTo[ship.team];
  dt[by] = (dt[by] || 0) + dmg;
  if (attacker && attacker.team !== ship.team) {
    state.damage[attacker.team] += dmg;
    attacker.damageDealt += dmg;
  }
  if (ship.hp <= 0) {
    ship.hp = 0; ship.alive = false;
    state.stats.deaths.push({ team: ship.team, cls: ship.cls, t: Math.round(state.time), by: by, mode: ship.ai.mode });
    pushEvent(state, { kind: 'shipboom', x: ship.x, y: ship.y, r: ship.def.radius * 2.5 });
  }
}

function damageAsteroid(state, rock, dmg) {
  if (!rock.alive) return;
  rock.hp -= dmg; // destruction is always on — war is hell, especially in space
  if (rock.hp <= 0) splitAsteroid(state, rock);
}

function splitAsteroid(state, rock) {
  var D = state.config.debris;
  rock.alive = false;
  state.gridDirty = true;
  state.stats.splits++;
  pushEvent(state, { kind: 'shatter', x: rock.x, y: rock.y, r: rock.r });
  var childR = rock.r * D.childRadiusScale;
  if (childR < D.minChildRadius) return; // fragments vanish -> cascade terminates
  var live = 0;
  for (var a2 = 0; a2 < state.asteroids.length; a2++) if (state.asteroids[a2].alive) live++;
  var base = state.rng.angle();
  for (var i = 0; i < D.fragmentCount; i++) {
    if (live + i >= D.maxAsteroids) break; // cascade safety cap
    var ang = base + i * (2 * Math.PI / D.fragmentCount) + state.rng.range(-0.3, 0.3);
    var c = makeAsteroid(state, rock.x + Math.cos(ang) * rock.r * 0.45, rock.y + Math.sin(ang) * rock.r * 0.45,
                         childR * state.rng.range(0.85, 1.1));
    var sp = D.burstSpeed * D.speedScale * state.rng.range(0.7, 1.3);
    c.vx = rock.vx + Math.cos(ang) * sp;      // momentum: children inherit the parent's motion
    c.vy = rock.vy + Math.sin(ang) * sp;      // plus the burst impulse
    c.rotVel = state.rng.range(-D.spinMax, D.spinMax);
    c.moving = true;
    state.asteroids.push(c);
    rockWoke(state, c); // visible to queries this tick, never enters the static grid
  }
}

function applyAoe(state, x, y, radius, dmg, attacker, src) {
  var ships = state.ships;
  for (var i = 0; i < ships.length; i++) {
    var sh = ships[i];
    if (!sh.alive) continue;
    var d = dist(x, y, sh.x, sh.y) - sh.def.radius;
    if (d <= radius) applyDamage(state, sh, dmg * (1 - 0.6 * clamp01(d / radius)), attacker, src); // friendlies included
  }
  var A = state.asteroids;
  for (var j = 0; j < A.length; j++) {
    var o = A[j];
    if (!o.alive) continue;
    var dr = dist(x, y, o.x, o.y) - o.r;
    if (dr <= radius) damageAsteroid(state, o, dmg * (1 - 0.6 * clamp01(dr / radius)));
  }
  // sympathetic detonation: any blast sets off live bombs caught in it (a PD hit on a
  // clustered salvo chains the whole cluster — and splashes whoever is nearby)
  var bombs = state.bombs;
  for (var b2 = 0; b2 < bombs.length; b2++) {
    var bm2 = bombs[b2];
    if (bm2.alive && dist(x, y, bm2.x, bm2.y) <= radius + 4) detonateBomb(state, bm2);
  }
}

/* ---------------- Railgun (fixed-forward, min range, instant ray, uninterceptable) ---------------- */
function railgunReady(state, ship, target) {
  var RG = state.config.railgun;
  if (ship.cool.rail > 0) return 'cooldown';
  var d = dist(ship.x, ship.y, target.x, target.y);
  if (d < RG.minRange) return 'deadzone';       // the dead zone lights exploit — cannot fire at all
  if (d > RG.maxRange) return 'range';
  if (Math.abs(normAngle(Math.atan2(target.y - ship.y, target.x - ship.x) - ship.heading)) > RG.arc / 2) return 'arc';
  if (!losShips(state, ship, target)) return 'los';
  return 'ok';
}
function fireRailgun(state, ship, target) {
  var RG = state.config.railgun;
  ship.cool.rail = RG.cooldown;
  state.stats.railShots++;
  var ang = Math.atan2(target.y - ship.y, target.x - ship.x);
  var d = dist(ship.x, ship.y, target.x, target.y);
  var endX = ship.x + Math.cos(ang) * RG.maxRange, endY = ship.y + Math.sin(ang) * RG.maxRange;
  var rock = firstRockOnRay(state, ship.x, ship.y, endX, endY);
  var rockD = rock ? dist(ship.x, ship.y, rock.x, rock.y) - rock.r : Infinity;
  // friendly fire: anyone drifting through the firing line catches the slug first
  var blocker = firstShipOnRay(state, ship, ship.x, ship.y, endX, endY);
  if (blocker && blocker.id !== target.id) {
    var bd = dist(ship.x, ship.y, blocker.x, blocker.y) - blocker.def.radius;
    if (bd < Math.min(rockD, d - target.def.radius)) {
      if (state.rng.chance(1 - clamp01(blocker.def.evasion * RG.evasionMult))) {
        applyDamage(state, blocker, RG.damage, ship, 'rail');
        if (blocker.team !== ship.team) state.stats.railHitsShip++;
      }
      pushEvent(state, { kind: 'rail', x: ship.x, y: ship.y, x2: blocker.x, y2: blocker.y, team: ship.team, hit: 'ship' });
      return;
    }
  }
  if (rock && rockD < d - target.def.radius) {
    damageAsteroid(state, rock, RG.damage * RG.rockDamageMult); // slug slams into the cover instead
    pushEvent(state, { kind: 'rail', x: ship.x, y: ship.y, x2: rock.x, y2: rock.y, team: ship.team, hit: 'rock' });
    return;
  }
  var pHit = 1 - clamp01(target.def.evasion * RG.evasionMult);
  if (state.rng.chance(pHit)) {
    state.stats.railHitsShip++;
    applyDamage(state, target, RG.damage, ship, 'rail');
    pushEvent(state, { kind: 'rail', x: ship.x, y: ship.y, x2: target.x, y2: target.y, team: ship.team, hit: 'ship' });
  } else {
    if (rock) damageAsteroid(state, rock, RG.damage * RG.rockDamageMult); // missed slug flies on downrange
    pushEvent(state, { kind: 'rail', x: ship.x, y: ship.y, x2: endX, y2: endY, team: ship.team, hit: 'miss' });
  }
}
function fireRailgunAtRock(state, ship, rock) {
  var RG = state.config.railgun;
  ship.cool.rail = RG.cooldown;
  var hit = firstRockOnRay(state, ship.x, ship.y,
    ship.x + Math.cos(ship.heading) * RG.maxRange, ship.y + Math.sin(ship.heading) * RG.maxRange) || rock;
  damageAsteroid(state, hit, RG.damage * RG.rockDamageMult);
  pushEvent(state, { kind: 'rail', x: ship.x, y: ship.y, x2: hit.x, y2: hit.y, team: ship.team, hit: 'rock' });
}

/* ---------------- Heavy railgun (Battleship turrets: staggered, ponderous, class/speed-gated) ----
   PROJECTILE model: launchHeavySlug releases a real flying slug (state.slugs) from the turret
   mount along the barrel angle; updateHeavySlugs flies it at slugSpeed and resolves hits by
   swept-segment overlap. Every trackable-class hull crossing the flight path (ANY team — friendly
   fire preserved) gets ONE uniform hit roll (evasion x speed factor) and the slug PIERCES on
   either way; bombers/interceptors are overpenetrated without fuzing (the hard class gate, now
   physical); an asteroid stops the slug dead (damage*rockDamageMult). Slugs get no gravity (at
   2400 px/s the real deflection is ~1 px) and are not PD-interceptable. RNG draws happen only in
   updateHeavySlugs, in slug-array then along-segment order (the per-hull hit roll, plus the shared
   asteroid-split RNG when a rock stop destroys its rock) — fully derived from state.
   Range/dead-zone stay measured from the ship centre in the fire gating, consistent with the
   railgun. */
function heavyRailTrackable(HR, e) { return HR.trackClasses.indexOf(e.cls) >= 0; }
function heavyRailSpeedFactor(HR, target) {
  // v<=speedFullTrack -> 1 (no penalty); v>=speedNoTrack -> 0 (untrackable). Frigate cruise 85 -> 0.
  return clamp01((HR.speedNoTrack - target.speed) / (HR.speedNoTrack - HR.speedFullTrack));
}
function turretMountPoint(state, ship, ti) {
  var HR = state.config.heavyRail;
  var frac = (HR.turretMounts && HR.turretMounts[ti] != null) ? HR.turretMounts[ti] : 0;
  var off = frac * ship.def.radius;
  return { x: ship.x + Math.cos(ship.heading) * off, y: ship.y + Math.sin(ship.heading) * off };
}
function launchHeavySlug(state, ship, ti) {
  // Fire gate already validated (updateTurrets); the slug leaves the barrel along the turret's
  // ACTUAL angle t.ang (within aimTolerance of the solution) and hits whatever is physically
  // in the way downrange. No RNG here — rolls happen in flight (updateHeavySlugs).
  var HR = state.config.heavyRail;
  var t = ship.turrets[ti];
  var mp = turretMountPoint(state, ship, ti);
  var muzz = ship.def.radius * 0.56; // barrel length: the slug appears at the rail tips
  var mx = mp.x + Math.cos(t.ang) * muzz, my = mp.y + Math.sin(t.ang) * muzz;
  state.slugs.push({
    id: state.nextId++, team: ship.team, ownerId: ship.id, turret: ti,
    x: mx, y: my, vx: Math.cos(t.ang) * HR.slugSpeed, vy: Math.sin(t.ang) * HR.slugSpeed,
    traveled: 0, alive: true, rolled: [], hitShip: false,
  });
  pushEvent(state, { kind: 'hrailMuzzle', x: mx, y: my, ang: t.ang, team: ship.team, turret: ti });
}
/* Along-segment parameter of a circle centre's projection onto a->b (0..1); used to order
   pierce hits and the stopping rock deterministically along one tick's swept segment. */
function segHitT(ax, ay, bx, by, cx, cy) {
  var dx = bx - ax, dy = by - ay;
  var L2 = dx * dx + dy * dy || 1;
  return clamp01(((cx - ax) * dx + (cy - ay) * dy) / L2);
}
function updateHeavySlugs(state, dt) {
  var HR = state.config.heavyRail, slugs = state.slugs;
  for (var i = 0; i < slugs.length; i++) {
    var sg = slugs[i];
    if (!sg.alive) continue;
    // this tick's swept segment (~40 px at 60 Hz)
    var nx = sg.x + sg.vx * dt, ny = sg.y + sg.vy * dt;
    // rock stop (NO pierce on asteroids): nearest rock disc on the segment ends the flight
    var rock = firstRockOnRay(state, sg.x, sg.y, nx, ny);
    var rockT = rock ? segHitT(sg.x, sg.y, nx, ny, rock.x, rock.y) : Infinity;
    // ship overlaps: every living trackable-class hull on the segment (ANY team — friendly
    // fire), except the owner and hulls this slug has already rolled against (pierce = one
    // roll per hull, ever). Lights are overpenetrated without fuzing: never damaged.
    var hits = [];
    for (var k = 0; k < state.ships.length; k++) {
      var sh = state.ships[k];
      if (!sh.alive || sh.id === sg.ownerId) continue;
      if (!heavyRailTrackable(HR, sh)) continue;
      if (sg.rolled.indexOf(sh.id) >= 0) continue;
      if (!segCircleHit(sg.x, sg.y, nx, ny, sh.x, sh.y, sh.def.radius)) continue;
      hits.push({ sh: sh, t: segHitT(sg.x, sg.y, nx, ny, sh.x, sh.y) });
    }
    // along-segment order, ship id tiebreak: the RNG draw order is fully state-derived
    hits.sort(function (a, b) { return (a.t - b.t) || (a.sh.id - b.sh.id); });
    var owner = state.shipById[sg.ownerId];
    var attacker = (owner && owner.team === sg.team) ? owner : null; // mirrors torpedo credit
    for (var h = 0; h < hits.length; h++) {
      if (hits[h].t >= rockT) break; // the rock stops the slug before this hull
      var tgt = hits[h].sh;
      sg.rolled.push(tgt.id);
      var pHit = (1 - clamp01(tgt.def.evasion * HR.evasionMult)) * heavyRailSpeedFactor(HR, tgt);
      if (state.rng.chance(pHit)) {
        applyDamage(state, tgt, HR.damage, attacker, 'hrail');
        if (tgt.team !== sg.team) state.stats.railHitsShip++;
        sg.hitShip = true;
        var hx = sg.x + (nx - sg.x) * hits[h].t, hy = sg.y + (ny - sg.y) * hits[h].t;
        pushEvent(state, { kind: 'hrail', x: hx, y: hy, x2: hx, y2: hy, team: sg.team, turret: sg.turret, hit: 'ship' });
      }
      // on a miss: already in rolled — the slug pierces on either way
    }
    if (rock) {
      damageAsteroid(state, rock, HR.damage * HR.rockDamageMult); // slams into the cover, stops dead
      var rx = sg.x + (nx - sg.x) * rockT, ry = sg.y + (ny - sg.y) * rockT;
      pushEvent(state, { kind: 'hrail', x: rx, y: ry, x2: rx, y2: ry, team: sg.team, turret: sg.turret, hit: 'rock' });
      sg.x = rx; sg.y = ry; sg.alive = false;
      continue;
    }
    sg.x = nx; sg.y = ny;
    sg.traveled += HR.slugSpeed * dt;
    if (sg.traveled >= HR.maxRange ||
        sg.x < -50 || sg.y < -50 || sg.x > state.config.arena.w + 50 || sg.y > state.config.arena.h + 50) {
      sg.alive = false; // spent: fell out of range or off the arena (same margin as torpedoes)
      if (!sg.hitShip)
        pushEvent(state, { kind: 'hrail', x: sg.x, y: sg.y, x2: sg.x, y2: sg.y, team: sg.team, turret: sg.turret, hit: 'miss' });
    }
  }
}

/* ---------------- Torpedoes (long range, loose tracking, LOS lock, PD-interceptable) ---------------- */
function launchTorpedoAtRock(state, owner, rock) {
  var T = state.config.torpedo;
  var ang = Math.atan2(rock.y - owner.y, rock.x - owner.x);
  state.torps.push({
    id: state.nextId++, team: owner.team, ownerId: owner.id, targetId: -1, rockId: rock.id,
    x: owner.x + Math.cos(ang) * (owner.def.radius + 6), y: owner.y + Math.sin(ang) * (owner.def.radius + 6),
    heading: ang, vx: Math.cos(ang) * T.speed, vy: Math.sin(ang) * T.speed,
    life: T.lifetime, lockLost: 0, spent: false, alive: true, traveled: 0,
  });
  state.stats.torpsFired[owner.team]++;
  pushEvent(state, { kind: 'launch', x: owner.x, y: owner.y });
}
function launchTorpedo(state, owner, target) {
  var T = state.config.torpedo;
  var ang = Math.atan2(target.y - owner.y, target.x - owner.x);
  state.torps.push({
    id: state.nextId++, team: owner.team, ownerId: owner.id, targetId: target.id, rockId: -1,
    x: owner.x + Math.cos(ang) * (owner.def.radius + 6), y: owner.y + Math.sin(ang) * (owner.def.radius + 6),
    heading: ang, vx: Math.cos(ang) * T.speed, vy: Math.sin(ang) * T.speed,
    life: T.lifetime, lockLost: 0, spent: false, alive: true, traveled: 0,
  });
  state.stats.torpsFired[owner.team]++;
  state.stats.torpLaunch[owner.team].push(Math.round(dist(owner.x, owner.y, target.x, target.y)));
  pushEvent(state, { kind: 'launch', x: owner.x, y: owner.y });
}
function findShip(state, id) {
  var s = state.shipById[id];
  return (s && s.alive) ? s : null;
}
function updateTorpedoes(state, dt) {
  var T = state.config.torpedo, torps = state.torps;
  for (var i = 0; i < torps.length; i++) {
    var tp = torps[i];
    if (!tp.alive) continue;
    tp.life -= dt;
    if (tp.life <= 0) { tp.alive = false; continue; }
    var target = findShip(state, tp.targetId);
    if (tp.rockId > 0 && !tp.spent) {
      // cover-buster: home on the rock (rocks don't dodge; no lock to lose)
      var rk = null;
      for (var ri = 0; ri < state.asteroids.length; ri++)
        if (state.asteroids[ri].id === tp.rockId && state.asteroids[ri].alive) { rk = state.asteroids[ri]; break; }
      if (rk) {
        var wantR = Math.atan2(rk.y - tp.y, rk.x - tp.x);
        var errR = normAngle(wantR - tp.heading);
        tp.heading += clamp(errR, -T.turnRate * dt, T.turnRate * dt);
        tp.vx = Math.cos(tp.heading) * T.speed; tp.vy = Math.sin(tp.heading) * T.speed;
      } else tp.spent = true;
    } else if (!tp.spent && target) {
      // LOS-gated lock (load-bearing for the flip: cover breaks tracking)
      if (state.tick % 6 === 0) tp.hasLos = losClear(state, tp.x, tp.y, target.x, target.y);
      if (tp.hasLos === false) {
        tp.lockLost += dt;
        if (tp.lockLost > T.lockLossSeconds) tp.spent = true; // flies dumb forever
      } else {
        tp.lockLost = 0;
        var d = dist(tp.x, tp.y, target.x, target.y);
        var tLead = Math.min(1.5, d / T.speed);
        var want = Math.atan2(target.y + target.vy * tLead - tp.y, target.x + target.vx * tLead - tp.x);
        var err = normAngle(want - tp.heading);
        tp.heading += clamp(err, -T.turnRate * dt, T.turnRate * dt); // LOOSE tracking
        tp.vx = Math.cos(tp.heading) * T.speed; tp.vy = Math.sin(tp.heading) * T.speed;
      }
    } else if (!tp.spent && !target) {
      tp.spent = true;
    }
    tp.x += tp.vx * dt; tp.y += tp.vy * dt;
    tp.traveled += Math.sqrt(tp.vx * tp.vx + tp.vy * tp.vy) * dt;
    if (tp.x < -50 || tp.y < -50 || tp.x > state.config.arena.w + 50 || tp.y > state.config.arena.h + 50) { tp.alive = false; continue; }
    var armed = tp.traveled >= T.armDistance;
    // rock collision -> detonate on the rock (a dud just crumples)
    var boom = false;
    rocksNearSeg(state, tp.x, tp.y, tp.x, tp.y, 96, function (o) {
      if (dist(tp.x, tp.y, o.x, o.y) < contourR(o, tp.x, tp.y) + 4) { // strike the lump itself
        if (armed) damageAsteroid(state, o, T.damage * T.rockDamageMult);
        detonateTorpedo(state, tp);
        tp.alive = false;
        boom = true; return true;
      }
      return false;
    });
    if (boom) continue;
    // physical contact with ANY hull that is not the shooter — friendly fire is on
    var struck = null;
    for (var sc = 0; sc < state.ships.length; sc++) {
      var shc = state.ships[sc];
      if (!shc.alive || shc.id === tp.ownerId) continue;
      if (target && shc.id === target.id) continue; // the tracked target keeps its evasion model
      if (dist(tp.x, tp.y, shc.x, shc.y) < shc.def.radius + 6) { struck = shc; break; }
    }
    if (struck) {
      if (tp.traveled >= T.armDistance) {
        var ownr = state.shipById[tp.ownerId];
        applyDamage(state, struck, T.damage, ownr && ownr.team === tp.team ? ownr : null, 'torp');
        detonateTorpedo(state, tp);
      }
      tp.alive = false;
      continue;
    }
    // terminal approach on the tracked target — only once armed
    if (armed && !tp.spent && target && dist(tp.x, tp.y, target.x, target.y) < T.hitRadius + target.def.radius) {
      // predictability gate: slow OR steady-vector targets are reliably hit; fast jinkers are missed
      var predictable = target.speed < T.predictSpeed || target.jinkEMA < T.jinkAccelThreshold;
      var pHit = predictable ? 1 - clamp01(target.def.evasion * T.steadyEvasionMult)
                             : 1 - clamp01(target.def.evasion * T.jinkEvasionMult);
      if (state.rng.chance(pHit)) {
        var owner = state.shipById[tp.ownerId];
        state.stats.torpsHit[tp.team]++;
        applyDamage(state, target, T.damage, owner && owner.team === tp.team ? owner : null, 'torp');
        tp.alive = false;
        pushEvent(state, { kind: 'boom', x: tp.x, y: tp.y, r: T.aoeRadius * 0.6 });
      } else {
        tp.spent = true; // sails past, no re-attack
      }
    }
  }
}
function detonateTorpedo(state, tp) {
  var T = state.config.torpedo;
  tp.alive = false;
  if (tp.traveled < T.armDistance) return; // unarmed: a dud — no blast at all
  var owner = state.shipById[tp.ownerId];
  applyAoe(state, tp.x, tp.y, T.aoeRadius, T.aoeDamage, owner && owner.team === tp.team ? owner : null, 'torpAoe');
  pushEvent(state, { kind: 'boom', x: tp.x, y: tp.y, r: T.aoeRadius });
}

/* ---------------- Bombs (straight-line AOE; lead-aimed; jink skews own aim; self-risk) ---------------- */
function launchBomb(state, owner, target) {
  var B = state.config.bomb;
  // lead solve: t^2(|vT|^2 - vB^2) + 2t(rel.vT) + rel.rel = 0
  var relx = target.x - owner.x, rely = target.y - owner.y;
  var a = target.vx * target.vx + target.vy * target.vy - B.speed * B.speed;
  var b = 2 * (relx * target.vx + rely * target.vy);
  var c = relx * relx + rely * rely;
  var t;
  if (Math.abs(a) < 1e-6) t = -c / b;
  else {
    var disc = b * b - 4 * a * c;
    if (disc < 0) t = -1;
    else {
      var sq = Math.sqrt(disc);
      var t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a);
      t = Math.min(t1 > 0 ? t1 : Infinity, t2 > 0 ? t2 : Infinity);
    }
  }
  if (!(t > 0) || t === Infinity) t = Math.sqrt(c) / B.speed; // fallback: aim direct
  var aimX = target.x + target.vx * t, aimY = target.y + target.vy * t;
  var ang = Math.atan2(aimY - owner.y, aimX - owner.x);
  // own-velocity skew: a jinking bomber throws its bombs wide (steady run = accurate)
  var accel = owner.def.thrust / owner.def.mass;
  var spread = B.jinkAimSpread * clamp01(owner.jinkEMA / (accel * 0.7));
  ang += state.rng.range(-spread, spread);
  // salvo release: a fanned cluster. Breaks the PD slot cap by volume — but the bombs
  // fly close enough that one interception can sympathetically detonate the rest.
  var n = Math.max(1, B.salvo | 0);
  var reach = Math.min(B.speed * B.maxFlight, dist(owner.x, owner.y, aimX, aimY) + 40);
  for (var i = 0; i < n; i++) {
    var a2 = ang + (i - (n - 1) / 2) * B.salvoSpread;
    // each fan lane checks its own path — an outer bomb clipping a rock edge would
    // chain-detonate the whole cluster right next to the bomber
    if (!losClear(state, owner.x, owner.y, owner.x + Math.cos(a2) * reach, owner.y + Math.sin(a2) * reach)) continue;
    state.bombs.push({
      id: state.nextId++, team: owner.team, ownerId: owner.id,
      x: owner.x + Math.cos(a2) * (owner.def.radius + 5), y: owner.y + Math.sin(a2) * (owner.def.radius + 5),
      vx: Math.cos(a2) * B.speed, vy: Math.sin(a2) * B.speed,
      age: 0, alive: true,
    });
    state.stats.bombsFired[owner.team]++;
  }
}
function detonateBomb(state, bomb) {
  var B = state.config.bomb;
  bomb.alive = false;
  var owner = state.shipById[bomb.ownerId];
  applyAoe(state, bomb.x, bomb.y, B.aoeRadius, B.damage, owner && owner.team === bomb.team ? owner : null, 'bomb');
  pushEvent(state, { kind: 'boom', x: bomb.x, y: bomb.y, r: B.aoeRadius });
}
function updateBombs(state, dt) {
  var B = state.config.bomb, bombs = state.bombs;
  for (var i = 0; i < bombs.length; i++) {
    var bm = bombs[i];
    if (!bm.alive) continue;
    bm.age += dt;
    if (bm.age > B.maxFlight) { bm.alive = false; continue; } // fizzle
    bm.x += bm.vx * dt; bm.y += bm.vy * dt;
    var hit = false;
    rocksNearSeg(state, bm.x, bm.y, bm.x, bm.y, 96, function (o) {
      if (dist(bm.x, bm.y, o.x, o.y) < contourR(o, bm.x, bm.y) + 4) { // strike the lump itself
        damageAsteroid(state, o, B.damage); detonateBomb(state, bm); hit = true; return true;
      }
      return false;
    });
    if (hit) continue;
    var ships = state.ships;
    for (var k = 0; k < ships.length; k++) {
      var sh = ships[k];
      if (!sh.alive) continue;
      if (sh.id === bm.ownerId && bm.age < B.armTime) continue; // not armed at own muzzle
      if (dist(bm.x, bm.y, sh.x, sh.y) < sh.def.radius + 4) {
        if (sh.team !== bm.team) state.stats.bombsHitShip[bm.team]++;
        detonateBomb(state, bm); break; // contact only — lights dodge
      }
    }
  }
}

/* ---------------- Gatling (interceptor forward gun; kills lights, chips capitals) ---------------- */
function updateGatling(state, ship, target, dt) {
  var G = state.config.gatling;
  ship.cool.gat -= dt;
  if (!target || !target.alive) return;
  var d = dist(ship.x, ship.y, target.x, target.y);
  if (d > G.range) return;
  if (Math.abs(normAngle(Math.atan2(target.y - ship.y, target.x - ship.x) - ship.heading)) > G.arc / 2) return;
  if (!losShips(state, ship, target)) return;
  var inWay = firstShipOnRay(state, ship, ship.x, ship.y, target.x, target.y);
  var recv = (inWay && inWay.id !== target.id &&
              dist(ship.x, ship.y, inWay.x, inWay.y) < d) ? inWay : target; // friendly fire
  while (ship.cool.gat <= 0) {
    ship.cool.gat += 1 / G.fireRate;
    var pHit = 1 - clamp01(recv.def.evasion * G.evasionMult);
    if (state.rng.chance(pHit)) {
      var capital = recv.cls === 'destroyer' || recv.cls === 'frigate';
      applyDamage(state, recv, G.damagePerShot * (capital ? G.capitalMult : 1), ship, 'gat');
    }
    pushEvent(state, { kind: 'gat', x: ship.x, y: ship.y, x2: recv.x, y2: recv.y, team: ship.team });
  }
  if (ship.cool.gat < 0) ship.cool.gat = 0;
}

/* ---------------- Point defence (short range, slot-capped throughput; saturation leaks) ---------------- */
function updatePD(state, ship, dt) {
  var P = state.config.pd, slots = ship.def.pdSlots;
  if (slots <= 0) return;

  // --- tracking pass (every tick): a threat must be held in continuous LOS-track for
  //     reactionSeconds before the guns may engage it. Track breaks when LOS breaks. ---
  if (!ship.pdTrack) ship.pdTrack = {};
  var tr = ship.pdTrack, seen = {};
  var torps = state.torps, bombs = state.bombs, ships = state.ships;
  function trackOne(id, x, y) {
    if (dist(ship.x, ship.y, x, y) > P.trackRange) return;
    if (!losClear(state, ship.x, ship.y, x, y)) return;
    seen[id] = true;
    if (tr[id] === undefined) tr[id] = state.time;
  }
  for (var ti = 0; ti < torps.length; ti++) {
    var tt = torps[ti];
    if (tt.alive && tt.team !== ship.team) trackOne(tt.id, tt.x, tt.y);
  }
  for (var bi = 0; bi < bombs.length; bi++) {
    var bb = bombs[bi];
    if (bb.alive && bb.team !== ship.team) trackOne(bb.id, bb.x, bb.y);
  }
  for (var si = 0; si < ships.length; si++) {
    var ss = ships[si];
    if (ss.alive && ss.team !== ship.team && isDetectedBy(state, ship.team, ss)) trackOne(ss.id, ss.x, ss.y);
  }
  for (var key in tr) if (!seen[key]) delete tr[key]; // LOS/range broken -> re-acquire from zero
  function tracked(id) { return tr[id] !== undefined && state.time - tr[id] >= P.reactionSeconds; }

  ship.cool.pd += dt;
  var interval = 1 / P.shotsPerSecond;
  if (ship.cool.pd < interval) return;
  ship.cool.pd -= interval;

  // gather TRACKED threats in the bubble: incoming projectiles first, then enemy ships
  var threats = [];
  for (var i = 0; i < torps.length; i++) {
    var tp = torps[i];
    if (tp.alive && tp.team !== ship.team && tracked(tp.id)) {
      var d = dist(ship.x, ship.y, tp.x, tp.y);
      if (d <= P.range) threats.push({ kind: 'torp', obj: tp, d: d });
    }
  }
  for (var j = 0; j < bombs.length; j++) {
    var bm = bombs[j];
    if (bm.alive && bm.team !== ship.team && tracked(bm.id)) {
      var d2 = dist(ship.x, ship.y, bm.x, bm.y);
      if (d2 <= P.range) threats.push({ kind: 'bomb', obj: bm, d: d2 });
    }
  }
  threats.sort(function (a, b) { return a.d - b.d; });
  var shipThreats = [];
  for (var k = 0; k < ships.length; k++) {
    var sh = ships[k];
    if (sh.alive && sh.team !== ship.team && tracked(sh.id)) {
      var d3 = dist(ship.x, ship.y, sh.x, sh.y);
      if (d3 <= P.range + sh.def.radius) shipThreats.push({ kind: 'ship', obj: sh, d: d3 });
    }
  }
  shipThreats.sort(function (a, b) { return a.d - b.d; });
  threats = threats.concat(shipThreats);

  // throughput cap: at most `slots` engaged this volley; the (N+1)th leaks through
  var n = Math.min(slots, threats.length);
  for (var s = 0; s < n; s++) {
    var th = threats[s];
    if (!losClear(state, ship.x, ship.y, th.obj.x, th.obj.y)) continue;
    if (th.kind === 'ship') {
      var pHit = 1 - clamp01(th.obj.def.evasion * P.shipEvasionMult);
      if (state.rng.chance(pHit)) applyDamage(state, th.obj, P.shipDamagePerShot, ship, 'pd');
      pushEvent(state, { kind: 'pd', x: ship.x, y: ship.y, x2: th.obj.x, y2: th.obj.y, team: ship.team });
    } else {
      pushEvent(state, { kind: 'pd', x: ship.x, y: ship.y, x2: th.obj.x, y2: th.obj.y, team: ship.team });
      if (state.rng.chance(P.projectileKillChance)) {
        if (th.kind === 'torp') { state.stats.torpsPD[th.obj.team]++; detonateTorpedo(state, th.obj); }
        else { state.stats.bombsPD[th.obj.team]++; detonateBomb(state, th.obj); } // bomb blows anyway — self-risk
      }
    }
  }
}
