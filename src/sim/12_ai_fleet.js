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
    var v2 = doctrineOf(state, team) === 'v2';
    var A2 = state.config.ai2;
    for (var k = 0; k < enemies.length; k++) {
      var e = enemies[k];
      var pri = isCapital(e) ? 0 : 1; // any capital; the pack naturally hits the escort first
      var d = dist(probe.x, probe.y, e.x, e.y) + pri * 100000;
      if (v2 && isCapital(e)) {
        // defeat in detail: strike the ISOLATED capital (fewest supporting capitals in
        // mutual-support range), finish the wounded one — not merely the nearest
        d += alliesNear(state, e, A2.isolationRadius) * A2.focusIsolationWeight * 2
           - (1 - e.hp / e.maxHp) * A2.focusHpWeight;
      }
      if (d < bestD) { bestD = d; tgt = e; }
    }
    c.targetId = tgt.id;
    if (state.time < c.until || state.time < c.cool) continue;
    // staged = in position AND slowed to hold — the wave assembles before it goes in.
    // In the open that dwell is naked torpedo exposure; in dense it happens behind rocks.
    var staged = 0, stagedBombers = 0, aliveBombers = 0;
    for (var s = 0; s < lights.length; s++) {
      var L = lights[s];
      if (L.cls === 'bomber') aliveBombers++;
      if (dist(L.x, L.y, tgt.x, tgt.y) < AI.commitRadius && L.speed < L.def.maxCruiseSpeed * 0.5) {
        staged++;
        if (L.cls === 'bomber') stagedBombers++;
      }
    }
    // don't blow the wave before the payload is in position
    if (aliveBombers > 0 && stagedBombers === 0) continue;
    // v2: assemble REAL waves — a big light wing commits at ~45% strength staged, not
    // the flat 3-ship trickle (time-on-target doctrine: PD saturates on pulses, and a
    // dribble of threes feeds its slots one course at a time)
    var needLights = Math.min(lights.length, AI.commitMinLights);
    if (v2) needLights = Math.min(lights.length, Math.max(AI.commitMinLights, Math.ceil(lights.length * 0.45)));
    if (staged >= needLights) {
      c.until = state.time + AI.commitSeconds;
      c.cool = c.until + AI.commitCooldown;
      // sometimes the whole wave hooks around a side — war loves a flank
      var r = state.rng.next();
      c.flank = r < AI.flankChance / 2 ? -1 : (r < AI.flankChance ? 1 : 0);
      // v2 anvil: a big enough bomber wave splits into TWO attack axes (hammer-and-anvil,
      // the torpedo-bomber doctrine: whichever way the victim turns, one axis gets its beam)
      c.anvil = v2 && stagedBombers >= state.config.ai2.anvilMinBombers;
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

