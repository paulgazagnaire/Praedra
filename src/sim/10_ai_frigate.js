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

