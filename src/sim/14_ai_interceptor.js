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


/* ================= Interceptor v2 (veteran doctrine) ================= */
/* Is any live friendly ordnance (bombs/torps) inside the victim's PD tracking bubble?
   The dive doctrine: enter a defended bubble ONLY while PD's slots are busy with
   projectiles — strike-package timing, not lone heroics. */
function ordnanceNear(state, team, tgt, range) {
  var torps = state.torps, bombs = state.bombs;
  for (var i = 0; i < torps.length; i++) {
    var tp = torps[i];
    if (tp.alive && tp.team === team && dist(tp.x, tp.y, tgt.x, tgt.y) < range) return true;
  }
  for (var j = 0; j < bombs.length; j++) {
    var bm = bombs[j];
    if (bm.alive && bm.team === team && dist(bm.x, bm.y, tgt.x, tgt.y) < range) return true;
  }
  return false;
}

function aiInterceptorV2(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, A2 = cfg.ai2, G = cfg.gatling;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: false, jink: false, speedCap: ship.def.maxCruiseSpeed * 0.85 };
    return;
  }
  var committing = teamCommitting(state, ship.team);
  if (ship.torpAmmo > 0 && committing) tryTorpedoV2(state, ship, dt);

  var thr = threatenedV2(state, ship);
  var own = ship.team === 'A' ? state.aliveA : state.aliveB;

  // SCREEN DOCTRINE: guard the strike package. Prefer enemy lights that are near OUR
  // bombers (they're hunting the payload), and among lights prefer enemy BOMBERS
  // (kill their payload — steady bomb-runners are the easy gun kill; jinking
  // interceptors are the time sink). Boelcke: protect the striking force first.
  // SELF-DEFENSE overrides the screen: an enemy interceptor already at guns range owns
  // this fight — flying past it after a bomber is how screens get shredded (measured:
  // 2x gatling deaths). Otherwise: guard bombers first, kill payloads first.
  var light = null, lightScore = Infinity, lightD = 0;
  var pressing = nearestWhere(state, ship, enemies, function (e2) { return e2.cls === 'interceptor'; });
  if (pressing && pressing.d < 280) { light = pressing.ship; lightD = pressing.d; }
  else for (var e = 0; e < enemies.length; e++) {
    var en = enemies[e];
    if (!isLight(en)) continue;
    var de = dist(ship.x, ship.y, en.x, en.y);
    if (de > 950) continue;
    var guard = 0;
    for (var b = 0; b < own.length; b++) {
      if (own[b].cls === 'bomber' && dist(en.x, en.y, own[b].x, own[b].y) < A2.screenBomberRange) { guard = 1; break; }
    }
    if (de > 700 && !guard) continue;              // far and harmless: not our fight yet
    var sc = de - guard * 600 - (en.cls === 'bomber' ? 350 : 0);
    if (sc < lightScore) { lightScore = sc; light = en; lightD = de; }
  }
  var gatTarget = null;

  if (light) {
    var tLead = Math.min(1.2, lightD / Math.max(1, ship.def.maxCruiseSpeed));
    ship.nav = { x: light.x + light.vx * tLead, y: light.y + light.vy * tLead, arrive: false,
                 jink: thr && lightD > 260 };
    gatTarget = light;
    ship.ai.mode = 'screen';
  } else {
    var focus = focusFor(state, ship, 'strike');
    var cap = (focus && isCapital(focus)) ? { ship: focus, d: dist(ship.x, ship.y, focus.x, focus.y) }
            : (nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null));
    var tgt = cap.ship, d = cap.d;
    gatTarget = tgt;
    var m = ship.ai.mode;
    if (m !== 'strafe_in' && m !== 'strafe_out') m = 'strafe_in';
    if (!committing) {
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
    // SATURATION-TIMED DIVES: a defended bubble is entered alongside live ordnance
    // (PD slots busy) or not at all — unless the strike wing is spent (then it's the
    // v1 endgame rule: fights end on kills, not the clock).
    var bombersLeft = 0;
    for (var ob = 0; ob < own.length; ob++) if (own[ob].cls === 'bomber') bombersLeft++;
    var pdDefended = tgt.def.pdSlots > 0;
    var mayDive = !A2.diveNeedsSaturation || !pdDefended || bombersLeft === 0 ||
                  ordnanceNear(state, ship.team, tgt, cfg.pd.trackRange);
    if (m === 'strafe_in' && !mayDive && d < AI.stageRange * 1.1) {
      // hold the rim of the bubble, jinking, until the ordnance wave arrives
      var hx = (ship.x - tgt.x) / (d || 1), hy = (ship.y - tgt.y) / (d || 1);
      ship.nav = { x: tgt.x + hx * AI.stageRange * 0.9, y: tgt.y + hy * AI.stageRange * 0.9,
                   arrive: true, jink: true };
      updateGatling(state, ship, gatTarget, dt);
      return;
    }
    if (m === 'strafe_in') {
      var c = state.commit[ship.team];
      var flk2 = c.anvil ? ((ship.id % 2 === 0) ? 1 : -1) : c.flank;
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
      if (d > AI.strafeExitRange) m = 'strafe_in';
    }
    ship.ai.mode = m;
  }
  updateGatling(state, ship, gatTarget, dt);
}
