function aiFrigate(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var own0 = ship.team === 'A' ? state.aliveA : state.aliveB;
    var d0 = null;
    for (var i0 = 0; i0 < own0.length; i0++) if (own0[i0].cls === 'destroyer') { d0 = own0[i0]; break; }
    var hp = routeAround(state, ship, d0 ? { x: d0.x, y: d0.y } : huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    return;
  }
  var own = (ship.team === 'A' ? state.aliveA : state.aliveB);
  var dest = null;
  for (var i = 0; i < own.length; i++) if (own[i].cls === 'destroyer') { dest = own[i]; break; }
  var near = nearestWhere(state, ship, enemies, null);
  var aim = Math.atan2(near.ship.y - ship.y, near.ship.x - ship.x);
  if (dest) {
    // escort: park the PD umbrella between the destroyer and the threat axis
    var ex = near.ship.x - dest.x, ey = near.ship.y - dest.y;
    var el = len(ex, ey) || 1;
    ship.nav = { x: dest.x + (ex / el) * AI.escortRange, y: dest.y + (ey / el) * AI.escortRange,
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


/* ================= Frigate v2 (veteran doctrine) ================= */
/* Wolfpack: when the doctrine pass has declared a thinly-escorted capital the pack
   victim, the frigates stop lobbing from standoff and DIVE — through the heavy rail's
   speed gate (stay fast, jink) into the dead zone (ai2.packDiveRange sits inside
   heavyRail.minRange and outside PD's ship reach), on SPREAD BEARINGS so PD and any
   escort can't cover every axis, and pound torpedoes point-blank where PD's 0.5 s
   reaction can't track them in time. U-boat wolfpack + PT-boat doctrine. */
function aiFrigateV2(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, A2 = cfg.ai2;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var own0 = ship.team === 'A' ? state.aliveA : state.aliveB;
    var d0 = null;
    for (var i0 = 0; i0 < own0.length; i0++) if (own0[i0].cls === 'destroyer') { d0 = own0[i0]; break; }
    var hp = routeAround(state, ship, d0 ? { x: d0.x, y: d0.y } : huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    return;
  }
  var doc = state.doctrine[ship.team];
  var packVictim = null;
  if (doc.packTargetId >= 0 && doc.packIds && doc.packIds.indexOf(ship.id) >= 0)
    packVictim = doctrineShip(state, ship.team, doc.packTargetId);

  if (packVictim) {
    var n = doc.packIds.length, idx = doc.packIds.indexOf(ship.id);
    var dV = dist(ship.x, ship.y, packVictim.x, packVictim.y);
    var aimV = Math.atan2(packVictim.y - ship.y, packVictim.x - ship.x);
    // spread bearings around the pack's mean approach axis (multi-axis anvil)
    var own = ship.team === 'A' ? state.aliveA : state.aliveB;
    var pcx = 0, pcy = 0, pn = 0;
    for (var p = 0; p < own.length; p++)
      if (doc.packIds.indexOf(own[p].id) >= 0) { pcx += own[p].x; pcy += own[p].y; pn++; }
    var ca = pn ? Math.atan2(pcy / pn - packVictim.y, pcx / pn - packVictim.x) : aimV + Math.PI;
    // cap the fan: with 6 boats an uncapped 1.0 rad/slot spread put the outer slots
    // ~143 deg off the approach axis — the straight chord to such a slot passes THROUGH
    // the victim's hull (review-confirmed: closest approach 95px vs 94px combined radii)
    var spreadPer = Math.min(A2.packBearingSpread, n > 1 ? (2 * A2.packMaxHalfArc) / (n - 1) : 0);
    var slot = ca + (idx - (n - 1) / 2) * spreadPer;
    var ox = packVictim.x + Math.cos(slot) * A2.packDiveRange;
    var oy = packVictim.y + Math.sin(slot) * A2.packDiveRange;
    // approach FAST (the heavy rail's speed gate can't track cruise 85; slowing down
    // outside the dead zone is what gets a boat killed), jinking only under actual
    // threat — jinking through rock fields for no reason is how boats die to terrain.
    // Settle only once inside the main-battery blind ring.
    var inside = dV < cfg.heavyRail.minRange * 0.95;
    ship.nav = { x: ox, y: oy, arrive: inside, jink: !inside && threatenedV2(state, ship), face: aimV };
    blastCoverNearGhosts(state, ship, dt);
    tryTorpedoV2(state, ship, dt); // point-blank release bypasses the volley hold
    return;
  }

  var own2 = ship.team === 'A' ? state.aliveA : state.aliveB;
  // ROUND-ROBIN escort assignment: v1 sends every frigate to the FIRST destroyer in
  // the roster, leaving the rest of the gunline naked — spread the PD umbrellas
  // (screening doctrine: every capital gets an escort before any gets two)
  var dests = [], frigs = [];
  for (var i = 0; i < own2.length; i++) {
    if (own2[i].cls === 'destroyer') dests.push(own2[i]);
    else if (own2[i].cls === 'frigate') frigs.push(own2[i]);
  }
  var dest = null;
  if (dests.length) {
    var myIdx = 0;
    for (var fi = 0; fi < frigs.length; fi++) if (frigs[fi].id < ship.id) myIdx++;
    dest = dests[myIdx % dests.length];
  }
  var near = nearestWhere(state, ship, enemies, null);
  var aim = Math.atan2(near.ship.y - ship.y, near.ship.x - ship.x);
  // the PD umbrella is only worth keeping when the enemy still fields ordnance it
  // can shoot down (lights' bombs, frigate torpedoes); against a pure gunline the
  // escort slot is wasted — take the torpedoes to the fight instead
  var ordnanceThreat = false;
  for (var e2 = 0; e2 < enemies.length; e2++)
    if (isLight(enemies[e2]) || enemies[e2].cls === 'frigate') { ordnanceThreat = true; break; }
  if (dest && ordnanceThreat) {
    var ex = near.ship.x - dest.x, ey = near.ship.y - dest.y;
    var el = len(ex, ey) || 1;
    ship.nav = { x: dest.x + (ex / el) * AI.escortRange, y: dest.y + (ey / el) * AI.escortRange,
                 arrive: true, face: aim };
  } else {
    // standoff torpedo work against the fleet's strike focus (spread + volley handled
    // by tryTorpedoV2); EMCON on the way in — arrive dim, leave lit
    var focus = focusFor(state, ship, 'strike');
    var anchor = focus ? { ship: focus, d: dist(ship.x, ship.y, focus.x, focus.y) }
                       : (nearestWhere(state, ship, enemies, function (e) { return e.cls === 'destroyer'; }) || near);
    var ux = (ship.x - anchor.ship.x) / (anchor.d || 1), uy = (ship.y - anchor.ship.y) / (anchor.d || 1);
    var stand = isCapital(anchor.ship) ? AI.frigateStandoff : 500;
    var cap = anchor.d > AI.frigateStandoff * 1.3 ? emconCap(state, ship) : null;
    ship.nav = { x: anchor.ship.x + ux * stand, y: anchor.ship.y + uy * stand,
                 arrive: true, face: aim, speedCap: cap || undefined };
  }
  blastCoverNearGhosts(state, ship, dt);
  tryTorpedoV2(state, ship, dt);
}
