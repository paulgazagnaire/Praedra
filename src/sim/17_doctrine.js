/* ---------------- Doctrine layer (v2 'veteran' team AI) ----------------
   Team-level battle picture recomputed every ai2.focusEvery ticks per team, consumed by
   the v2 per-class AI (aiDestroyerV2 etc.). All of it is deterministic scoring over
   existing state — no RNG, no new events, no API change. config.doctrine.A/B selects
   'v1' (legacy greedy AI, kept verbatim for A/B batteries) or 'v2' per team.

   Doctrine sources (docs/TACTICS.md): Lanchester-law concentration -> focus fire with
   wounded-first finishing and no-overkill spillover; defeat in detail -> isolation-scored
   targeting; U-boat wolfpack + destroyer torpedo doctrine -> synchronized volleys and
   multi-bearing dead-zone dives; Hutier infiltration / terrain masking -> covered
   approach hops; EMCON / burn-and-coast -> plume discipline inside the enemy's likely
   detection band. */

function doctrineOf(state, team) {
  var d = state.config.doctrine;
  return (d && d[team]) === 'v1' ? 'v1' : 'v2';
}

/* Live-and-detected resolver for a stored focus id. */
function doctrineShip(state, team, id) {
  if (id < 0) return null;
  var s = state.shipById[id];
  if (!s || !s.alive) return null;
  return isDetectedBy(state, team, s) ? s : null;
}

/* Enemy capitals within the mutual-support radius of e (excluding e itself). */
function alliesNear(state, e, radius) {
  var own = e.team === 'A' ? state.aliveA : state.aliveB;
  var n = 0;
  for (var i = 0; i < own.length; i++) {
    var s = own[i];
    if (s.id !== e.id && isCapital(s) && dist(s.x, s.y, e.x, e.y) < radius) n++;
  }
  return n;
}

/* Sum of damage already committed (live friendly torpedoes locked) on a target —
   the no-overkill ledger: once a victim's hp is spoken for, spill to the next one. */
function torpsCommitted(state, team, targetId) {
  var T = state.config.torpedo, sum = 0;
  for (var i = 0; i < state.torps.length; i++) {
    var tp = state.torps[i];
    if (tp.alive && !tp.spent && tp.team === team && tp.targetId === targetId) sum += T.damage;
  }
  return sum;
}

/* Per-team doctrine pass. Builds:
   - focusStrike: the fleet's strike focus (torpedoes/bombs/waves). Frigate-first (strip
     the PD/torpedo platform, LESSONS), wounded-first, isolated-first.
   - focusGun: the gunline focus (railgun/heavy-rail preference). Big hulls first,
     wounded-first.
   - pack: wolfpack dive order — set when the strike focus is a thinly-escorted capital
     and enough frigates live to anvil it inside the battleship's dead zone.
   - volleyGo: synchronized-torpedo release flag (>=2 boats loaded => volley NOW). */
function updateDoctrine(state) {
  var teams = ['A', 'B'];
  for (var t = 0; t < teams.length; t++) {
    var team = teams[t];
    if (doctrineOf(state, team) !== 'v2') continue;
    var doc = state.doctrine[team];
    if (state.tick % state.config.ai2.focusEvery !== (t * 7) % state.config.ai2.focusEvery) continue;
    var A2 = state.config.ai2;
    var own = team === 'A' ? state.aliveA : state.aliveB;
    var enemies = livingEnemies(state, team);
    doc.focusStrike = -1; doc.focusGun = -1;
    doc.packTargetId = -1; doc.packIds = null; doc.volleyGo = false;
    if (!enemies.length || !own.length) continue;

    // fleet centroid (reachability anchor for the scoring)
    var cx = 0, cy = 0;
    for (var i = 0; i < own.length; i++) { cx += own[i].x; cy += own[i].y; }
    cx /= own.length; cy /= own.length;

    var bestS = Infinity, bestSId = -1, bestG = Infinity, bestGId = -1;
    for (var j = 0; j < enemies.length; j++) {
      var e = enemies[j];
      var d = dist(cx, cy, e.x, e.y);
      var wounded = A2.focusHpWeight * (1 - e.hp / e.maxHp);
      var support = alliesNear(state, e, A2.isolationRadius) * A2.focusIsolationWeight;
      // strike focus: frigates die first (strip PD + torpedoes), then destroyers, then
      // the battleship's 620-hp hull; lights are never a fleet focus (chaff)
      var clsS = e.cls === 'frigate' ? 0 : (e.cls === 'destroyer' ? 250 : (e.cls === 'battleship' ? 500 : 4000));
      var sScore = d + clsS + support - wounded;
      if (sScore < bestS) { bestS = sScore; bestSId = e.id; }
      // gunline focus: what railguns/heavy rail can actually track — biggest first
      if (isCapital(e)) {
        var clsG = e.cls === 'battleship' ? 0 : (e.cls === 'destroyer' ? 300 : 600);
        var gScore = d + clsG + support * 0.5 - wounded;
        if (gScore < bestG) { bestG = gScore; bestGId = e.id; }
      }
    }
    doc.focusStrike = bestSId;
    doc.focusGun = bestGId >= 0 ? bestGId : bestSId;

    // wolfpack: dive order on a thinly-escorted BATTLESHIP focus. Battleships ONLY —
    // the dead zone (heavyRail.minRange) is what makes point-blank safe; diving a
    // destroyer parks the boats inside its railgun/torpedo envelope (measured: fatal)
    var victim = doctrineShip(state, team, doc.focusStrike);
    if (victim && victim.cls === 'battleship' &&
        alliesNear(state, victim, A2.isolationRadius) <= A2.packEscortMax) {
      var frigs = [];
      for (var k = 0; k < own.length; k++)
        if (own[k].cls === 'frigate' && dist(own[k].x, own[k].y, victim.x, victim.y) < A2.packLeash)
          frigs.push(own[k].id); // leash: never yank a cross-map escort into the dive
      if (frigs.length >= A2.packMinFrigates) {
        frigs.sort(function (a, b) { return a - b; });
        doc.packTargetId = victim.id;
        doc.packIds = frigs;
      }
    }

    // synchronized torpedo volley: two or more loaded boats => release together
    var ready = 0;
    for (var m = 0; m < own.length; m++) {
      var s2 = own[m];
      if (s2.cls === 'frigate' && s2.cool.torp <= 0 && s2.torpAmmo !== 0) ready++;
    }
    doc.volleyGo = ready >= 2;
  }
}

/* EMCON burn-and-coast: return a speedCap that idles the plume while we sit inside the
   band where a burn would light us up but a coast keeps us dim. null = burn freely.
   Uses only own-side knowledge (our signature, distance to enemies WE have detected). */
function emconCap(state, ship) {
  var A2 = state.config.ai2, D = state.config.detection;
  var enemies = livingEnemies(state, ship.team);
  if (!enemies.length) return null;
  var near = nearestWhere(state, ship, enemies, null);
  var visCoast = ship.def.signature * D.thrustMultMin;
  var visBurn = ship.def.signature * D.thrustMultMax;
  if (near.d < visCoast * A2.emconNear) return null;  // already inside their picture
  if (near.d > visBurn * A2.emconFar) return null;    // too far for anyone to see the plume
  // cut the BURN, never the SPEED: a slow coaster is torpedo-predictable (<70) and
  // heavy-rail trackable (<85) — measured as v2 frigates' top killer. Floor at 85% cruise.
  return Math.max(ship.speed, ship.def.maxCruiseSpeed * 0.85);
}

/* Masked approach (terrain infiltration): one covered hop TOWARD tgt — the LOS shadow of
   a corridor rock, on the far side from tgt, between standRange and our current distance.
   Cached like coverPoint; null when the map offers no cover (open approach is the tax). */
function coverHop(state, ship, tgt, standRange) {
  if (state.tick - (ship.ai.hopAt || -999) < state.config.ai.coverHoldTicks * 1.5 &&
      ship.ai.hop && Math.abs((ship.ai.hopTX || 0) - tgt.x) < 250 &&
      Math.abs((ship.ai.hopTY || 0) - tgt.y) < 250) return ship.ai.hop;
  ship.ai.hopAt = state.tick; ship.ai.hopTX = tgt.x; ship.ai.hopTY = tgt.y;
  var d = dist(ship.x, ship.y, tgt.x, tgt.y);
  var best = null, bestScore = Infinity;
  rocksNearSeg(state, ship.x, ship.y, tgt.x, tgt.y, 92 + 620, function (o) {
    if (o.r < 45) return false;
    var dT = dist(o.x, o.y, tgt.x, tgt.y);
    if (dT < standRange * 0.85 || dT > d - 80) return false; // hop must gain ground
    var ux = (o.x - tgt.x) / (dT || 1), uy = (o.y - tgt.y) / (dT || 1);
    var hx = o.x + ux * (o.r + 62), hy = o.y + uy * (o.r + 62);
    var score = dist(ship.x, ship.y, hx, hy) * 0.6 + dT; // near us, deep as allowed
    if (score < bestScore) { bestScore = score; best = { x: hx, y: hy }; }
    return false;
  });
  // reject a hop embedded in a neighbouring rock (stall trap), like coverPoint does
  if (best) {
    var embedded = false;
    rocksNearSeg(state, best.x, best.y, best.x, best.y, 120, function (o2) {
      if (dist(best.x, best.y, o2.x, o2.y) < o2.r + 26) { embedded = true; return true; }
      return false;
    });
    if (embedded) best = null;
  }
  ship.ai.hop = best;
  return best;
}

/* Resolve the doctrine focus for this ship's weapon family, with a sanity leash: never
   cross the map for a focus when a same-family target sits at a fraction of the range. */
function focusFor(state, ship, kind) {
  var doc = state.doctrine[ship.team];
  var f = doctrineShip(state, ship.team, kind === 'gun' ? doc.focusGun : doc.focusStrike);
  if (!f) return null;
  var near = nearestWhere(state, ship, livingEnemies(state, ship.team), isCapital);
  if (near && dist(ship.x, ship.y, f.x, f.y) > near.d * 2.5 + 400) return null;
  return f;
}

/* v2 torpedo fire control: focus-preferring, overkill-aware, volley-synchronized.
   Mirrors tryTorpedo's cadence/salvo bookkeeping exactly — only the RELEASE DECISION
   and target choice differ. */
function torpedoPickV2(state, ship, enemies, TC) {
  var T = state.config.torpedo;
  var focus = focusFor(state, ship, 'strike');
  var valid = function (e) {
    var de = dist(ship.x, ship.y, e.x, e.y);
    if (de < T.lobMinRange || de > TC.range || !losShips(state, ship, e)) return false;
    // no-overkill ledger: hp already spoken for by torps in the water -> spill over
    return torpsCommitted(state, ship.team, e.id) < e.hp + T.damage * 0.5;
  };
  // scored pick: near + PD-THIN + fleet-focus. A mutually-escorting cluster eats
  // torpedoes (measured: focus-firing escorted frigate clusters raised our interception
  // losses by a third) — shoot the straggler PD can't cover. Capitals only for the
  // interceptor's one shot; among lights only a steady bomber is worth a torpedo (v1 rule).
  var best = null, bestScore = Infinity;
  for (var i = 0; i < enemies.length; i++) {
    var e = enemies[i];
    var light = isLight(e);
    if (light && (ship.cls === 'interceptor' || e.cls !== 'bomber' ||
        !(e.speed < T.predictSpeed || e.jinkEMA < T.jinkAccelThreshold))) continue;
    if (!valid(e)) continue;
    var sc = dist(ship.x, ship.y, e.x, e.y)
           + pdShadow(state, e) * 25            // PD-thinness is a TIE-BREAK: at 80 it routinely
                                                  //   out-weighed 100-200px of extra flight, and the
                                                  //   longer flight cost MORE interceptions than the
                                                  //   thin PD saved (measured: median launch 615 vs 478,
                                                  //   interception 21% vs 15%). Closer beats thinner.
           + (light ? 2600 : 0)                 // payload > chaff
           + (focus && e.id === focus.id ? -400 : 0);
    if (sc < bestScore) { bestScore = sc; best = e; }
  }
  return best;
}

/* Enemy PD guns whose bubbles shadow a torpedo's terminal approach on e: e's own slots
   plus any PD ship parked within overlap range (escort umbrellas overlap the target). */
function pdShadow(state, e) {
  var foes = e.team === 'A' ? state.aliveA : state.aliveB; // e's own team
  var slots = e.def.pdSlots || 0;
  for (var i = 0; i < foes.length; i++) {
    var s = foes[i];
    if (s.id === e.id || !s.def.pdSlots) continue;
    if (dist(s.x, s.y, e.x, e.y) < 300) slots += s.def.pdSlots;
  }
  return slots;
}

function tryTorpedoV2(state, ship, dt) {
  var TC = state.config.torpedo[ship.cls];
  if (!TC) return;
  if (ship.torpAmmo === 0) return;
  var A2 = state.config.ai2;
  var enemies = livingEnemies(state, ship.team);
  if (ship.cool.salvoLeft > 0) { // continue a salvo in progress (same as v1)
    ship.cool.salvoGap -= dt;
    if (ship.cool.salvoGap <= 0) {
      var t2 = torpedoPickV2(state, ship, enemies, TC);
      if (t2) { launchTorpedo(state, ship, t2); }
      ship.cool.salvoLeft--; ship.cool.salvoGap = TC.salvoGap || 0;
    }
    return;
  }
  if (ship.cool.torp > 0) { ship.ai.torpHold = 0; return; }
  var target = torpedoPickV2(state, ship, enemies, TC);
  if (!target) { ship.ai.torpHold = 0; return; }
  // volley discipline (frigates only — the boats with sisters to synchronize with):
  // hold a loaded tube until the pack is loaded too, so the spread saturates PD's slots
  // in one wave instead of feeding it torpedoes singly. Knife-fight range fires at will.
  if (ship.cls === 'frigate') {
    var doc = state.doctrine[ship.team];
    var dT = dist(ship.x, ship.y, target.x, target.y);
    var pointBlank = dT <= A2.packDiveRange * 1.4;
    ship.ai.torpHold = (ship.ai.torpHold || 0) + dt;
    if (!pointBlank && !doc.volleyGo && ship.ai.torpHold < A2.volleyWaitMax) return;
  }
  ship.ai.torpHold = 0;
  launchTorpedo(state, ship, target);
  if (ship.torpAmmo > 0) ship.torpAmmo--;
  ship.cool.torp = TC.cooldown || 9999;
  ship.cool.salvoLeft = (TC.salvo || 1) - 1;
  ship.cool.salvoGap = TC.salvoGap || 0;
}

/* Inbound-bomb threat: any live enemy bomb close enough and CLOSING on us. Bombs are
   unguided contact-fuzed — lateral displacement (a jink) is a complete defense, but
   only if the light actually reacts. v1 never does; v2 lights weave the moment one is
   inbound (flak-evasion doctrine: react to the shot, not just the shooter). */
function bombThreat(state, ship) {
  var bombs = state.bombs;
  for (var i = 0; i < bombs.length; i++) {
    var bm = bombs[i];
    if (!bm.alive || bm.team === ship.team) continue;
    var dx = ship.x - bm.x, dy = ship.y - bm.y;
    var d2 = dx * dx + dy * dy;
    if (d2 > 330 * 330) continue;
    if (bm.vx * dx + bm.vy * dy > 0) return true; // closing
  }
  return false;
}
function threatenedV2(state, ship) {
  return threatened(state, ship) || bombThreat(state, ship);
}

/* Splash-spread discipline: keep friendly light hulls out of each other's bomb-AOE
   chain radius (RTS micro: spread against splash). Nudges the nav point directly away
   from the nearest too-close friendly light; deterministic, no state. The furball's
   dominant killer is a 3-bomb salvo catching a clump — spacing beats dodging. */
function spreadNav(state, ship, nav) {
  if (!nav) return nav;
  var own = ship.team === 'A' ? state.aliveA : state.aliveB;
  var nearest = null, nd = Infinity;
  for (var i = 0; i < own.length; i++) {
    var s = own[i];
    if (s.id === ship.id || !isLight(s)) continue;
    var d = dist(ship.x, ship.y, s.x, s.y);
    if (d < nd) { nd = d; nearest = s; }
  }
  if (!nearest || nd > 110) return nav;
  var ux = (ship.x - nearest.x) / (nd || 1), uy = (ship.y - nearest.y) / (nd || 1);
  var push = (110 - nd) * 2.2;
  nav.x += ux * push; nav.y += uy * push;
  return nav;
}


/* Jink-safety: weaving is for open sky. A light that jinks through a debris field or
   along a rock rim kills itself on terrain (rock impacts are every class's #1 killer)
   — suppress the weave when the next second of flight has stone in it. */
function jinkOK(state, ship) {
  var px = ship.x + ship.vx * 0.9, py = ship.y + ship.vy * 0.9;
  var safe = true;
  rocksNearSeg(state, ship.x, ship.y, px, py, 92 + 180, function (o) {
    if (o.r < 26) return false;
    if (segCircleHit(ship.x, ship.y, px, py, o.x, o.y, o.r + 150)) { safe = false; return true; }
    return false;
  });
  return safe;
}
