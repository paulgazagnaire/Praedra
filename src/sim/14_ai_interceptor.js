function aiInterceptor(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, G = cfg.gatling, TC = cfg.torpedo.interceptor;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: false, jink: false, speedCap: ship.def.maxCruiseSpeed * 0.85 };
    return;
  }
  var committing = teamCommitting(state, ship.team);
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
      } else {
        var ux = (tgt.x - ship.x) / (d || 1), uy = (tgt.y - ship.y) / (d || 1);
        ship.nav = { x: tgt.x + ux * AI.strafeDivePoint, y: tgt.y + uy * AI.strafeDivePoint,
                     arrive: false, jink: thr && d > cfg.pd.range * 1.3 };
      }
      if (d < G.range * 0.8) m = 'strafe_out';
    } else {
      var ux2 = (ship.x - tgt.x) / (d || 1), uy2 = (ship.y - tgt.y) / (d || 1);
      var side = (ship.id % 2 === 0) ? 1 : -1;
      ship.nav = { x: tgt.x + (ux2 * 0.75 + -uy2 * side * 0.66) * AI.strafeExitRange * 1.6,
                   y: tgt.y + (uy2 * 0.75 + ux2 * side * 0.66) * AI.strafeExitRange * 1.6,
                   arrive: false, jink: true };
      if (d > AI.strafeExitRange) m = 'strafe_in'; // do not loiter: out, around, in again
    }
    ship.ai.mode = m;
  }
  updateGatling(state, ship, gatTarget, dt);
}

