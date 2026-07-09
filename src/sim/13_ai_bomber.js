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
  var thr = threatened(state, ship);
  var committing = teamCommitting(state, ship.team);
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
      } else ship.nav = { x: target.x, y: target.y, arrive: false, jink: thr };
    } else ship.nav = { x: target.x, y: target.y, arrive: false, jink: thr };
    // a bomb needs a steady vector BEFORE release: go steady as soon as the target is
    // sighted inside run-start range — early in the open (long, exposed), late in dense
    if (d <= B.launchRange * B.runStartFactor && losShips(state, ship, target)) {
      m = 'run'; ship.ai.modeAt = state.time; ship.ai.runBombs = 0;
      state.stats.runEntries++;
    }
  }
  if (m === 'run') {
    // steady bomb-run vector: accurate bombs, torpedo-predictable — the enforced tradeoff.
    // Pop out, stream a pair, duck back before the answering torpedo lands.
    ship.nav = { x: target.x, y: target.y, arrive: false, jink: false };
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
    var duck = null, duckD = Infinity;
    var AR = state.asteroids;
    var thrShip = nearestWhere(state, ship, enemies, function (e) { return e.cls === 'frigate'; }) ||
                  nearestWhere(state, ship, enemies, isCapital);
    for (var ri = 0; ri < AR.length; ri++) {
      var ro = AR[ri];
      if (!ro.alive || ro.r < 40) continue;
      var rd2 = dist(ship.x, ship.y, ro.x, ro.y);
      if (rd2 < 380 && rd2 < duckD) { duckD = rd2; duck = ro; }
    }
    if (duck && thrShip) {
      var tdx = duck.x - thrShip.ship.x, tdy = duck.y - thrShip.ship.y;
      var tdl = len(tdx, tdy) || 1;
      ship.nav = { x: duck.x + (tdx / tdl) * (duck.r + 50), y: duck.y + (tdy / tdl) * (duck.r + 50),
                   arrive: true, jink: true };
    } else {
      var ux = (ship.x - target.x) / (d || 1), uy = (ship.y - target.y) / (d || 1);
      var side = (ship.id % 2 === 0) ? 1 : -1;
      ship.nav = { x: ship.x + (ux * 0.8 + -uy * side * 0.6) * 400, y: ship.y + (uy * 0.8 + ux * side * 0.6) * 400,
                   arrive: false, jink: true };
    }
    if (d > AI.bomberRegroupRange) m = committing ? 'approach' : 'stage';
  }
  if (m !== ship.ai.mode) ship.ai.modeAt = state.time;
  ship.ai.mode = m;
}


/* ================= Bomber v2 (veteran doctrine) ================= */
/* Torpedo-bomber anvil: a big committed wave splits into two attack axes (id parity)
   so whichever way the victim creeps, one group keeps a clean run — and PD's slots
   face saturation from two bearings at once. Approach flies EMCON (burn-and-coast:
   full burn far out, engines cold through the enemy's detection band) so the run
   starts from a pop-out, not a tracked crawl. Focus target from the doctrine pass. */
function aiBomberV2(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, B = cfg.bomb, A2 = cfg.ai2;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true, jink: false };
    return;
  }
  var focus = focusFor(state, ship, 'strike');
  var pick = (focus && isCapital(focus)) ? { ship: focus, d: dist(ship.x, ship.y, focus.x, focus.y) }
           : (nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null));
  var target = pick.ship, d = pick.d;
  var thr = threatenedV2(state, ship);
  var committing = teamCommitting(state, ship.team);
  var m = ship.ai.mode;
  if (m !== 'run' && m !== 'break' && m !== 'approach') m = 'stage';
  if (m === 'stage' && committing) { m = 'approach'; }
  if ((m === 'approach' || m === 'run') && !committing) m = 'break';

  if (m === 'stage') {
    var cover = coverPoint(state, ship, target, AI.coverMinRange, AI.stageRange * 1.4);
    if (cover) ship.nav = navToCover(ship, cover, thr);
    else {
      var sx = (ship.x - target.x) / (d || 1), sy = (ship.y - target.y) / (d || 1);
      ship.nav = { x: target.x + sx * AI.stageRange, y: target.y + sy * AI.stageRange, arrive: true, jink: thr };
    }
  }
  if (m === 'approach') {
    var c = state.commit[ship.team];
    // ANVIL: two axes by id parity when the wave is big enough; else the wave's flank call
    var flk = c.anvil ? ((ship.id % 2 === 0) ? 1 : -1) : c.flank;
    if (flk !== 0 && d > AI.flankDone * 2) {
      var fx = (ship.x - target.x) / (d || 1), fy = (ship.y - target.y) / (d || 1);
      var ca = Math.cos(flk * 1.1), sa = Math.sin(flk * 1.1);
      var wx = target.x + (fx * ca - fy * sa) * AI.flankOffset;
      var wy = target.y + (fx * sa + fy * ca) * AI.flankOffset;
      if (dist(ship.x, ship.y, wx, wy) > AI.flankDone) {
        ship.nav = { x: wx, y: wy, arrive: false, jink: thr };
      } else ship.nav = { x: target.x, y: target.y, arrive: false, jink: thr };
    } else ship.nav = { x: target.x, y: target.y, arrive: false, jink: thr };
    // EMCON: cold-coast the detection band when nothing is actively hunting us —
    // the run then starts from an untracked pop-out and beats PD's reaction clock
    if (!thr) {
      var cap = emconCap(state, ship);
      if (cap) ship.nav.speedCap = cap;
    }
    if (d <= B.launchRange * B.runStartFactor && losShips(state, ship, target)) {
      m = 'run'; ship.ai.modeAt = state.time; ship.ai.runBombs = 0;
      state.stats.runEntries++;
    }
  }
  if (m === 'run') {
    ship.nav = { x: target.x, y: target.y, arrive: false, jink: false };
    var clear = losShips(state, ship, target);
    if (ship.cool.bomb > 0) state.stats.fireBlocked.cooldown++;
    else if (d > B.launchRange) state.stats.fireBlocked.range++;
    else if (!clear) state.stats.fireBlocked.los++;
    else {
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
    var duck = null, duckD = Infinity;
    var AR = state.asteroids;
    var thrShip = nearestWhere(state, ship, enemies, function (e) { return e.cls === 'frigate'; }) ||
                  nearestWhere(state, ship, enemies, isCapital);
    for (var ri = 0; ri < AR.length; ri++) {
      var ro = AR[ri];
      if (!ro.alive || ro.r < 40) continue;
      var rd2 = dist(ship.x, ship.y, ro.x, ro.y);
      if (rd2 < 380 && rd2 < duckD) { duckD = rd2; duck = ro; }
    }
    if (duck && thrShip) {
      var tdx = duck.x - thrShip.ship.x, tdy = duck.y - thrShip.ship.y;
      var tdl = len(tdx, tdy) || 1;
      ship.nav = { x: duck.x + (tdx / tdl) * (duck.r + 50), y: duck.y + (tdy / tdl) * (duck.r + 50),
                   arrive: true, jink: true };
    } else {
      var ux = (ship.x - target.x) / (d || 1), uy = (ship.y - target.y) / (d || 1);
      var side = (ship.id % 2 === 0) ? 1 : -1;
      ship.nav = { x: ship.x + (ux * 0.8 + -uy * side * 0.6) * 400, y: ship.y + (uy * 0.8 + ux * side * 0.6) * 400,
                   arrive: false, jink: true };
    }
    if (d > AI.bomberRegroupRange) m = committing ? 'approach' : 'stage';
  }
  if (m !== ship.ai.mode) ship.ai.modeAt = state.time;
  ship.ai.mode = m;
}
