function aiDestroyer(state, ship, dt) {
  var cfg = state.config, AI = cfg.ai, RG = cfg.railgun;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) {
    var hp = routeAround(state, ship, huntPoint(state, ship));
    ship.nav = { x: hp.x, y: hp.y, arrive: true, speedCap: ship.def.maxCruiseSpeed * 0.8 };
    blastCoverNearGhosts(state, ship, dt);
    clearTransitLane(state, ship, dt);
    return;
  }
  // railgun food first (slow, low evasion), then whatever is nearest
  var pick = nearestWhere(state, ship, enemies, isCapital) || nearestWhere(state, ship, enemies, null);
  var target = pick.ship;
  var d = pick.d;
  var aim = Math.atan2(target.y - ship.y, target.x - ship.x);
  var closeLight = nearestWhere(state, ship, enemies, function (e) { return isLight(e); });

  // a big hull wants open ground and a clean lane, not a rock maze
  if (state.tick % 30 === (ship.id % 30)) ship.ai.anchorBias = openLaneBias(state, ship, target);
  var bias = ship.ai.anchorBias || { x: 0, y: 0 };

  if (closeLight && closeLight.d < AI.destroyerRetreatRange) {
    // lights in the dead zone: burn away (nose turns away — the railgun is now useless, by design)
    var ux = (ship.x - closeLight.ship.x) / (closeLight.d || 1), uy = (ship.y - closeLight.ship.y) / (closeLight.d || 1);
    ship.nav = { x: ship.x + ux * 500, y: ship.y + uy * 500, arrive: false, jink: false };
  } else if (d > AI.destroyerStandoff + 100) {
    var ux2 = (target.x - ship.x) / d, uy2 = (target.y - ship.y) / d;
    ship.nav = { x: target.x - ux2 * AI.destroyerStandoff + bias.x, y: target.y - uy2 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else if (d < AI.destroyerStandoff - 140 && isCapital(target)) {
    var ux3 = (ship.x - target.x) / d, uy3 = (ship.y - target.y) / d;
    ship.nav = { x: target.x + ux3 * AI.destroyerStandoff + bias.x, y: target.y + uy3 * AI.destroyerStandoff + bias.y,
                 arrive: true, face: aim };
  } else {
    ship.nav = { x: ship.x + bias.x, y: ship.y + bias.y, arrive: true, face: aim }; // hold in the open, gun on target
  }

  var st = railgunReady(state, ship, target);
  if (st === 'ok') { fireRailgun(state, ship, target); ship.ai.noLos = 0; }
  else if (st === 'los') {
    ship.ai.noLos = (ship.ai.noLos || 0) + dt;
    // blow open the cover: no hesitation — the rock hiding a contact IS a target
    if (ship.ai.noLos > AI.rockShootSeconds && d < RG.maxRange && ship.cool.rail <= 0) {
      var rock = firstRockOnRay(state, ship.x, ship.y, target.x, target.y);
      if (rock) {
        if (ship.nav) ship.nav.face = Math.atan2(rock.y - ship.y, rock.x - ship.x);
        if (Math.abs(normAngle(Math.atan2(rock.y - ship.y, rock.x - ship.x) - ship.heading)) < RG.arc / 2)
          fireRailgunAtRock(state, ship, rock);
      }
    }
  } else ship.ai.noLos = 0;
  blastCoverNearGhosts(state, ship, dt);
  tryTorpedo(state, ship, dt); // weak secondary — only ever finds capital/steady targets
}

