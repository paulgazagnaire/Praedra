/* Praedra sim module: fleet — coordinated layer: focus fire, squadrons, ambush — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- Coordinated fleet layer (focus fire, squadrons, ambush) ----------------
   Everything here is deterministic pure-state math (no RNG) and gated by ai.smartTeams /
   squadron.enabledTeams so self-play sweeps can A/B the smart layer against the base AI. */
function smartTeam(state, team) { return state.config.ai.smartTeams.indexOf(team) >= 0; }
function squadronTeam(state, team) { return state.config.squadron.enabledTeams.indexOf(team) >= 0; }
function admiralTeam(state, team) { return state.config.admiral.enabledTeams.indexOf(team) >= 0; }

/* Fleet focus fire: ONE deletion-ordered primary per team. LESSONS: frigate-first strips the
   torpedo platform + PD escort, and only then is the blinded destroyer helpless. Among equal
   classes, finish the most-wounded; distance from the fleet centroid breaks remaining ties.
   Sticky between rescores — target stability beats micro-optimal switching. */
function focusRank(cls) {
  return cls === 'frigate' ? 0 : (cls === 'destroyer' ? 1 : (cls === 'battleship' ? 2 : (cls === 'bomber' ? 3 : 4)));
}
function updateFleetPlan(state) {
  var AI = state.config.ai;
  var teams = ['A', 'B'];
  for (var t = 0; t < 2; t++) {
    var team = teams[t];
    if (!smartTeam(state, team)) continue;
    var p = state.plan[team];
    var cur = state.shipById[p.focusId];
    if (cur && cur.alive && isDetectedBy(state, team, cur) &&
        state.time - p.focusAt < AI.focusRetargetSeconds) continue;
    var enemies = livingEnemies(state, team);
    if (!enemies.length) { p.focusId = -1; continue; }
    var own = team === 'A' ? state.aliveA : state.aliveB;
    var cx = 0, cy = 0, n = own.length || 1;
    for (var i = 0; i < own.length; i++) { cx += own[i].x; cy += own[i].y; }
    cx /= n; cy /= n;
    var best = null, bestKey = Infinity;
    for (var j = 0; j < enemies.length; j++) {
      var e = enemies[j];
      var key = focusRank(e.cls) * 1e8 + (e.hp / e.maxHp) * 1e6 + dist(cx, cy, e.x, e.y);
      if (key < bestKey) { bestKey = key; best = e; }
    }
    p.focusId = best ? best.id : -1;
    p.focusAt = state.time;
  }
}
function focusTarget(state, ship) {
  if (!smartTeam(state, ship.team)) return null;
  var f = state.shipById[state.plan[ship.team].focusId];
  return f && f.alive && isDetectedBy(state, ship.team, f) ? f : null;
}

/* Squadron membership: per team, per light class, id-ordered chunks of squadron.size.
   Deterministic (roster order == id order); refreshed on a cadence so deaths compact slots. */
function updateSquadrons(state) {
  var SQ = state.config.squadron;
  if (state.squads.at >= 0 && state.tick - state.squads.at < SQ.reformTicks) return;
  state.squads.at = state.tick;
  var teams = ['A', 'B'];
  for (var t = 0; t < 2; t++) {
    var own = teams[t] === 'A' ? state.aliveA : state.aliveB;
    var classes = ['bomber', 'interceptor'];
    var squadsSoFar = 0;                       // team-global squadron ordinal: bombers' squads
    for (var c = 0; c < 2; c++) {              // first (payload leads), then interceptors'
      var members = [];
      for (var i = 0; i < own.length; i++) if (own[i].cls === classes[c]) members.push(own[i]);
      for (var m = 0; m < members.length; m++) {
        var sh = members[m];
        var sq = Math.floor(m / SQ.size);
        sh.ai.sqSlot = m % SQ.size;
        sh.ai.sqN = Math.min(SQ.size, members.length - sq * SQ.size);
        sh.ai.sqLead = members[sq * SQ.size].id;
        sh.ai.sqOrd = squadsSoFar + sq;        // consumed by sectors + staggered commitment
      }
      squadsSoFar += Math.ceil(members.length / SQ.size);
    }
  }
}

/* Swarm-coordinator nav pass (auto-AI lights only; player orders are never touched):
   (1) SEPARATION — displace the nav goal away from crowding friendlies, so bombers never fly
       inside each other's blast/sympathetic-chain radius and one PD interception costs the
       squadron ONE ship's salvo, not the squadron;
   (2) loose transit FORMATION — a weak pull toward an echelon slot off the squadron lead while
       nothing is detected. Both gains are small, additive nav-goal nudges: the autopilot's
       avoidance/pathing always dominates, so navigation never degrades. */
function squadronNav(state, ship) {
  var SQ = state.config.squadron;
  if (!ship.nav || !isLight(ship) || !squadronTeam(state, ship.team)) return;
  var own = ship.team === 'A' ? state.aliveA : state.aliveB;
  var sepX = 0, sepY = 0;
  for (var i = 0; i < own.length; i++) {
    var o = own[i];
    if (o.id === ship.id || !isLight(o)) continue;
    // ANY pair involving a bomber keeps blast-safe distance — an interceptor 82px from a
    // live bomb cloud died in the same sympathetic chain the bombers did
    var want = (ship.cls === 'bomber' || o.cls === 'bomber') ? SQ.bomberSep : SQ.lightSep;
    var d = dist(ship.x, ship.y, o.x, o.y);
    if (d >= want || d < 1e-6) continue;
    var w = (want - d) / want;
    sepX += ((ship.x - o.x) / d) * w * want;
    sepY += ((ship.y - o.y) / d) * w * want;
  }
  // repel from LIVE friendly bombs (not one's own salvo — a bomber must not flee its own
  // release). The term the old code lacked entirely: a diver headed into the bomb stream
  // is shoved out before the chain catches it.
  var danger = state.config.bomb.aoeRadius + ship.def.radius + SQ.bombAvoidMargin;
  for (var b = 0; b < state.bombs.length; b++) {
    var bm = state.bombs[b];
    if (!bm.alive || bm.team !== ship.team || bm.ownerId === ship.id) continue;
    var db = dist(ship.x, ship.y, bm.x, bm.y);
    if (db >= danger || db < 1e-6) continue;
    sepX += ((ship.x - bm.x) / db) * (danger - db) * SQ.bombPushGain;
    sepY += ((ship.y - bm.y) / db) * (danger - db) * SQ.bombPushGain;
  }
  var gain = ship.ai.mode === 'run' ? SQ.runSepGain : SQ.sepGain;
  var sm = len(sepX, sepY), cap = SQ.bomberSep * 1.6;
  if (sm > cap) { sepX *= cap / sm; sepY *= cap / sm; }
  ship.nav.x += sepX * gain;
  ship.nav.y += sepY * gain;
  // echelon formation: while blind, AND while the admiral has the fleet searching/advancing
  // (the visible 'moving as a body under command' read) — never during strike/withdraw
  var formOK = livingEnemies(state, ship.team).length === 0 ||
    (admiralTeam(state, ship.team) && state.admiral[ship.team].posture !== 'strike' &&
     state.admiral[ship.team].posture !== 'withdraw');
  if (ship.ai.sqLead != null && formOK) {
    var lead = state.shipById[ship.ai.sqLead];
    if (lead && lead.alive && lead.id !== ship.id) {
      var ux = lead.speed > 12 ? lead.vx / lead.speed : Math.cos(lead.heading);
      var uy = lead.speed > 12 ? lead.vy / lead.speed : Math.sin(lead.heading);
      var side = (ship.ai.sqSlot % 2 === 1) ? 1 : -1;
      var k = Math.ceil(ship.ai.sqSlot / 2);
      var sep2 = ship.cls === 'bomber' ? SQ.bomberSep : SQ.lightSep;
      var sx = lead.x - ux * k * sep2 * 0.8 - uy * side * k * sep2;
      var sy = lead.y - uy * k * sep2 * 0.8 + ux * side * k * sep2;
      ship.nav.x = ship.nav.x * (1 - SQ.formGain) + sx * SQ.formGain;
      ship.nav.y = ship.nav.y * (1 - SQ.formGain) + sy * SQ.formGain;
    }
  }
}

/* Slot-separated attack lane: squadmates converge on a shared target along bearings fanned
   around the SQUADRON LEAD's line of approach. Adjacent release lanes end up ~2x the bomb
   AOE apart, so a PD interception can never sympathetically chain across lanes. */
function spreadPoint(state, ship, tgt, standDist) {
  var SQ = state.config.squadron;
  if (!squadronTeam(state, ship.team) || ship.ai.sqN == null || ship.ai.sqN < 2)
    return { x: tgt.x, y: tgt.y };
  var adm = admiralTeam(state, ship.team) ? state.admiral[ship.team] : null;
  var lead = state.shipById[ship.ai.sqLead];
  var ref = lead && lead.alive ? lead : ship;
  // fleet-stable base bearing when the admiral is on (frozen between cadences — the
  // bearing stability the fan needs), else the squadron lead's line of approach
  var rx = (adm && adm.at >= 0) ? adm.mainX : ref.x;
  var ry = (adm && adm.at >= 0) ? adm.mainY : ref.y;
  var base = Math.atan2(ry - tgt.y, rx - tgt.x);
  // per-squadron SECTOR (0, +1, -1, +2, -2, ... x sectorSpread): squads own distinct
  // world-space corridors onto the one focus victim — no cross-squadron pile-up
  var k = ship.ai.sqOrd || 0;
  var sector = adm ? (((k + 1) >> 1) * ((k % 2 === 1) ? 1 : -1)) * SQ.sectorSpread : 0;
  // chord floor: adjacent lanes stay >= slotChord px apart at ANY standDist (the old
  // fixed-angle fan collapsed to a point as standDist shrank toward the target)
  var stepA = Math.max(SQ.bearingSpread,
                       2 * Math.asin(Math.min(0.9, SQ.slotChord / (2 * Math.max(standDist, 1)))));
  var a = base + sector + (ship.ai.sqSlot - (ship.ai.sqN - 1) / 2) * stepA;
  return { x: tgt.x + Math.cos(a) * standDist, y: tgt.y + Math.sin(a) * standDist };
}

/* LOS-shadow ambush: a hunting capital with a FRESH but blind contact nearby parks in the
   shadow of a rock on the expected approach, gun trained on the corridor, instead of marching
   into the open. Strictly bounded (linger cap + cooldown + contact-freshness) so two ambushing
   fleets cannot camp the clock out — when nothing shows, the hunt resumes. */
function ambushNav(state, ship, huntPt) {
  var cfg = state.config, AI = cfg.ai, D = cfg.detection;
  if (!smartTeam(state, ship.team)) return null;
  var lc = state.lastContact[ship.team];
  if (!lc || state.time - lc.t > D.memorySeconds * 2.5) {
    ship.ai.ambush = null; ship.ai.ambushUntil = 0;  // stale contact: drop the lurk point too
    return null;
  }
  var dc = dist(ship.x, ship.y, lc.x, lc.y);
  if (dc > AI.ambushContactMax || dc < 420) return null; // too far to lurk / already on top of it
  if (state.time < (ship.ai.ambushCoolT || 0)) return null;
  if (ship.ai.ambush && state.time < ship.ai.ambushUntil) {
    var a = ship.ai.ambush;
    return { x: a.x, y: a.y, arrive: true, face: Math.atan2(lc.y - a.y, lc.x - a.x) };
  }
  if (ship.ai.ambushUntil && state.time >= ship.ai.ambushUntil) {
    ship.ai.ambush = null; ship.ai.ambushUntil = 0;          // nothing showed: march again,
    ship.ai.ambushCoolT = state.time + AI.ambushCooldown;    // cooldown before the next lurk
    return null;
  }
  var best = null, bestScore = Infinity;
  rocksNearSeg(state, ship.x, ship.y, lc.x, lc.y, 92 + 520, function (o) {
    if (o.r < 46 || o.r > 420) return false;                 // big enough to hide a hull, not a titan
    var dTgt = dist(o.x, o.y, lc.x, lc.y);
    if (dTgt < 480 || dTgt > AI.ambushContactMax) return false;
    var hx = o.x + ((o.x - lc.x) / (dTgt || 1)) * (o.r + 60);
    var hy = o.y + ((o.y - lc.y) / (dTgt || 1)) * (o.r + 60);
    // prefer a shadow near us whose rock sits about weapon range off the expected path
    var score = dist(ship.x, ship.y, hx, hy) + Math.abs(dTgt - 700) * 0.5;
    if (score < bestScore) { bestScore = score; best = { x: hx, y: hy }; }
    return false;
  });
  if (!best) return null;
  ship.ai.ambush = best;
  ship.ai.ambushUntil = state.time + AI.ambushLingerSeconds;
  return { x: best.x, y: best.y, arrive: true, face: Math.atan2(lc.y - best.y, lc.x - best.x) };
}

/* Battleship-only gravity-well skirt (see ai.bbWellSkirtFrac). The uniquely slow BB (turnMax 0.14,
   ~22s flip) cannot out-manoeuvre a gravity well: a source's pull (bounded to shipEscapeCap*accel, but
   with a ~22s reorient it drags the hull in faster than the guns can correct) curls the ponderous ship
   PAST its own nav goal and into the rock, where it grinds to death — the design's flagged entombment,
   and the dominant real-match killer (traced: the titan's 3750px well swallows a BB whose goal sits a
   mere ~1000px away, before routeAround's path-geometry ever sees a blocker). So for the BB, and only
   in its AUTO role AI (ordered move/attackmove/hold untouched -> lane-clear tests unaffected): whenever
   the hull is inside a well AND its goal/path would keep it in the killing core, hand back a rim
   waypoint at the well's skirt radius on the goal side. The BB then rounds the rim; its 3200px guns
   still reach deep into the well from there, so 'skirt the well and shell across it' loses no coverage
   and fits the long-range-artillery identity. Applies to EVERY source (BIGs and the titan) — routeAround
   only reroutes when the straight segment clips a huge rock, which gravity-drag defeats. */
function bbSkirtWell(state, ship, pt) {
  var srcs = state.gravSources;
  if (!srcs || !srcs.length) return pt;
  var AI = state.config.ai, T = state.config.terrain, W = state.config.arena.w, H = state.config.arena.h;
  // Among the wells the BB is INSIDE, pick the one whose surface it is nearest — the immediate grind
  // threat. A slug-slow hull can't be trusted to hold even a wide standoff (traced: dragged in from
  // 1020px of clearance), so the safe radius is out at the well edge where the pull has faded, not a
  // fixed rim — the skirt makes the hull CLIMB outward each tick, leaning toward the goal side.
  var best = null, bestSurf = Infinity, bestReach = 0, bestRing = 0;
  for (var i = 0; i < srcs.length; i++) {
    var o = srcs[i].o;
    var reach = srcs[i].reach;
    var dShip = dist(ship.x, ship.y, o.x, o.y);
    if (dShip > reach) continue;                                      // outside this well: no drag
    var ring = Math.max(o.r * AI.bbWellSkirtFrac, o.r + ship.def.radius + 180); // inner floor (BIG wells)
    // fine only if the goal AND path already clear the core (goal beyond the well on a clean lane)
    if (dist(pt.x, pt.y, o.x, o.y) >= ring &&
        !segCircleHit(ship.x, ship.y, pt.x, pt.y, o.x, o.y, ring)) continue;
    var surf = dShip - o.r;
    if (surf < bestSurf) { bestSurf = surf; best = o; bestReach = reach; bestRing = ring; }
  }
  if (!best) return pt;
  var dShip2 = dist(ship.x, ship.y, best.x, best.y);
  var targetR = clamp(dShip2 + 250, bestRing, bestReach);            // climb OUTWARD toward the well edge
  var angS = Math.atan2(ship.y - best.y, ship.x - best.x);
  var angG = Math.atan2(pt.y - best.y, pt.x - best.x);
  // sticky-side rim-follow (mirrors routeAround's titan handling): COMMIT to one way around for a
  // few seconds and take a real tangential step, so a goal across the well makes the hull
  // circumnavigate the rim toward it instead of dithering radially out and grinding to a halt
  // between the climb-out vector and the goal pull (the observed stuck-grind death).
  var side;
  if (ship.ai.skirtRock === best.id && state.time < (ship.ai.skirtUntil || -1)) side = ship.ai.skirtSide;
  else { side = normAngle(angG - angS) > 0 ? 1 : -1; ship.ai.skirtRock = best.id; ship.ai.skirtSide = side; }
  ship.ai.skirtUntil = state.time + 2.5;
  var na = angS + side * clamp(560 / targetR, 0.3, 0.9);             // orbit toward the goal side
  return { x: clamp(best.x + Math.cos(na) * targetR, T.edgeMargin, W - T.edgeMargin),
           y: clamp(best.y + Math.sin(na) * targetR, T.edgeMargin, H - T.edgeMargin) };
}

/* ---------------- Admiral layer (fleet command: posture, axis, task org, search) ----------------
   One commander per team (admiral.enabledTeams). Deterministic pure-state math — zero RNG
   draws, fixed tick cadence, id-ordered rosters. Reads only the team's own ships, the
   DETECTED enemy set (data-link legal) and ghost memory. Pinned ships and ships under
   player orders never hold fleet roles and are never steered (contract). */
function countLights(own) {
  var n = 0;
  for (var i = 0; i < own.length; i++) if (isLight(own[i])) n++;
  return n;
}
function enemyCapitalKnown(state, team) {
  var foes = team === 'A' ? state.aliveB : state.aliveA;
  for (var i = 0; i < foes.length; i++)
    if (isCapital(foes[i]) && (isDetectedBy(state, team, foes[i]) || state.lastSeenShip[foes[i].id])) return true;
  return false;
}
function enemyHasPdCapital(foes) {
  for (var i = 0; i < foes.length; i++) if (foes[i].def.pdSlots > 0) return true;
  return false;
}
/* Freshest ghost of a LIVING enemy: the search anchor. Iterates the alive roster (stable
   id order), never for..in over lastSeenShip. Returns a ghost POSITION, never live coords. */
function freshestGhost(state, team) {
  var foes = team === 'A' ? state.aliveB : state.aliveA;
  var best = null;
  for (var i = 0; i < foes.length; i++) {
    var ls = state.lastSeenShip[foes[i].id];
    if (ls && (!best || ls.t > best.t)) best = ls;
  }
  return best;
}
/* Deterministic expanding-ring sweep around the freshest ghost (else the enemy spawn):
   waypoint = pure function of (anchor, n, team). Timeout kills unreachable waypoints
   (e.g. inside the titan); after searchRings rings the cursor falls back to the
   spawn/centre landmark cycle — the mutual-convergence resolution backstop. */
function searchWpt(state, team, sr, n) {
  var ADM = state.config.admiral, T = state.config.terrain;
  var W = state.config.arena.w, H = state.config.arena.h, B = ADM.searchRingBearings;
  if (n === 0) return { x: sr.ax, y: sr.ay };
  if (n > B * ADM.searchRings) {
    var eSpawn = team === 'A' ? state.spawnB : state.spawnA;
    return ((n - B * ADM.searchRings) % 2 === 1) ? eSpawn : { x: W / 2, y: H / 2 };
  }
  var ring = 1 + Math.floor((n - 1) / B), b = (n - 1) % B;
  var phase = team === 'A' ? 0 : Math.PI;             // mirrored teams sweep opposite lobes first
  var ang = phase + b * (2 * Math.PI / B) + ring * (Math.PI / B); // ring-to-ring twist
  var r = ring * ADM.searchRingStep;
  return { x: clamp(sr.ax + Math.cos(ang) * r, T.edgeMargin, W - T.edgeMargin),
           y: clamp(sr.ay + Math.sin(ang) * r, T.edgeMargin, H - T.edgeMargin) };
}
function advanceSearch(state, team, adm) {
  var ADM = state.config.admiral;
  var sr = adm.search;
  var anchor = freshestGhost(state, team) || (team === 'A' ? state.spawnB : state.spawnA);
  if (dist(anchor.x, anchor.y, sr.ax, sr.ay) > ADM.searchRingStep) { // new intel resets the pattern
    sr.n = 0; sr.ax = anchor.x; sr.ay = anchor.y; sr.sinceT = state.time; sr.wpt = null;
  }
  if (!sr.wpt) sr.wpt = searchWpt(state, team, sr, sr.n);
  // advance on proximity of any own ship (scouts arrive first in practice) or timeout
  var own = team === 'A' ? state.aliveA : state.aliveB, arrived = false;
  for (var i = 0; i < own.length; i++)
    if (dist(own[i].x, own[i].y, sr.wpt.x, sr.wpt.y) < ADM.searchWptRadius) { arrived = true; break; }
  if (arrived || state.time - sr.sinceT > ADM.searchWptTimeout) {
    sr.n++; sr.sinceT = state.time; sr.wpt = searchWpt(state, team, sr, sr.n);
  }
}
/* Task organization: id-ordered, AUTO ships only (pinned/ordered never hold roles).
   scout: first scoutCount eligible interceptors. reserve: the reserveSquads highest-sqOrd
   interceptor squads (whole squads — a held squadron reads on screen; bombers are never
   reserve: the payload leads, the reserve is the exploitation force). screen: remaining
   eligible interceptors. main: capitals + bombers + leftovers. */
function assignFleetRoles(state, team, adm, own) {
  var ADM = state.config.admiral, SQ = state.config.squadron;
  var eligible = [];
  for (var i = 0; i < own.length; i++) {
    var sh = own[i];
    if (sh.pinned || sh.order) { sh.ai.fleetRole = null; continue; }
    eligible.push(sh);
  }
  var ints = [], caps = 0, sqOrds = {};
  for (i = 0; i < eligible.length; i++) {
    if (eligible[i].cls === 'interceptor') { ints.push(eligible[i]); sqOrds[eligible[i].ai.sqOrd || 0] = true; }
    if (isCapital(eligible[i])) caps++;
  }
  var nLightSquads = 0;
  for (i = 0; i < eligible.length; i++) if (isLight(eligible[i]) && eligible[i].ai.sqSlot === 0) nLightSquads++;
  var reserveOrds = {};
  if (nLightSquads >= ADM.reserveMinSquads && adm.posture !== 'withdraw' && !adm.reserveReleased) {
    var ords = [];
    for (var k in sqOrds) ords.push(+k);
    ords.sort(function (a, b) { return b - a; });               // highest sqOrd squads held back
    for (i = 0; i < Math.min(ADM.reserveSquads, ords.length - 1); i++) reserveOrds[ords[i]] = true;
  }
  var scoutLeft = ADM.scoutCount, scoutIdx = 0, screenIdx = 0, capIdx = 0;
  for (i = 0; i < eligible.length; i++) {
    var e = eligible[i];
    if (e.cls === 'interceptor' && reserveOrds[e.ai.sqOrd || 0]) {
      e.ai.fleetRole = 'reserve'; e.ai.roleIdx = 0;
    } else if (e.cls === 'interceptor' && scoutLeft > 0) {
      e.ai.fleetRole = 'scout'; e.ai.roleIdx = scoutIdx++; scoutLeft--;
    } else if (e.cls === 'interceptor') {
      e.ai.fleetRole = 'screen'; e.ai.roleIdx = screenIdx++;
    } else {
      e.ai.fleetRole = 'main';
      e.ai.roleIdx = isCapital(e) ? capIdx++ : 0;
    }
    e.ai.rallyIdx = i;
  }
  adm.nScreen = screenIdx; adm.nCap = capIdx; adm.nEligible = eligible.length;
}
function updateAdmiral(state) {
  var ADM = state.config.admiral, T = state.config.terrain;
  var W = state.config.arena.w, H = state.config.arena.h;
  var teams = ['A', 'B'];
  for (var t = 0; t < 2; t++) {
    var team = teams[t];
    if (!admiralTeam(state, team)) continue;
    var adm = state.admiral[team];
    if (state.tick % ADM.cadenceTicks !== 2 && adm.at >= 0) continue; // fixed phase; first pass forced
    adm.at = state.tick;
    var own = team === 'A' ? state.aliveA : state.aliveB;
    var foes = livingEnemies(state, team);                 // DETECTED set — data-link legal
    if (!own.length) continue;

    // 1. main-body centroid (capitals; else all own) + fleet speed governor
    var cx = 0, cy = 0, nc = 0, minCruise = Infinity, i;
    for (i = 0; i < own.length; i++) if (isCapital(own[i])) {
      cx += own[i].x; cy += own[i].y; nc++;
      if (own[i].def.maxCruiseSpeed < minCruise) minCruise = own[i].def.maxCruiseSpeed;
    }
    if (!nc) { for (i = 0; i < own.length; i++) { cx += own[i].x; cy += own[i].y; } nc = own.length; }
    adm.mainX = cx / nc; adm.mainY = cy / nc;
    adm.fleetSpeed = (minCruise < Infinity) ? minCruise * ADM.advanceSpeedFrac : 0;

    // 2. posture. withdraw: own value collapsed while a known enemy capital remains and
    // the clock isn't nearly out — fall back to the rally, guns covering, STRICTLY
    // time-boxed (a fleet that flees forever never resolves). search/advance/strike flips
    // carry a hysteresis dwell so the posture never dithers at a boundary.
    var nearestFoeD = Infinity;
    for (i = 0; i < foes.length; i++) {
      var fd = dist(adm.mainX, adm.mainY, foes[i].x, foes[i].y);
      if (fd < nearestFoeD) nearestFoeD = fd;
    }
    var engaged = foes.length > 0 && nearestFoeD < ADM.strikeRange;
    var ownVal = 0;
    for (i = 0; i < own.length; i++) ownVal += own[i].def.cost * (own[i].hp / own[i].maxHp);
    var ownFrac = ownVal / (state.initialValue[team] || 1);
    var lc = state.lastContact[team];
    if (adm.posture !== 'withdraw' && ownFrac < ADM.withdrawOwnFrac &&
        enemyCapitalKnown(state, team) &&
        state.time < ADM.withdrawLatestFrac * state.config.matchTimerSeconds) {
      adm.posture = 'withdraw'; adm.postureAt = state.time;
      adm.withdrawUntil = state.time + ADM.withdrawSeconds;
    } else if (adm.posture === 'withdraw') {
      if (state.time >= adm.withdrawUntil) {                 // bounded: ALWAYS re-attacks
        adm.posture = engaged ? 'strike' : 'advance'; adm.postureAt = state.time;
      }
    } else {
      var desired = engaged ? 'strike'
                  : (foes.length || (lc && state.time - lc.t < ADM.ghostMaxAge)) ? 'advance' : 'search';
      if (desired !== adm.posture && state.time - adm.postureAt >= ADM.postureMinSeconds) {
        if (desired === 'strike') { adm.strikeLights0 = countLights(own); adm.reserveReleased = false; }
        adm.posture = desired; adm.postureAt = state.time;
      }
    }
    if (adm.posture === 'search') advanceSearch(state, team, adm);

    // 3. objective + axis + rally
    if (foes.length) {                                       // centroid of DETECTED enemies — legal
      var ex = 0, ey = 0;
      for (i = 0; i < foes.length; i++) { ex += foes[i].x; ey += foes[i].y; }
      adm.objective = { x: ex / foes.length, y: ey / foes.length };
    } else if (lc && state.time - lc.t < ADM.ghostMaxAge) adm.objective = { x: lc.x, y: lc.y };
    else adm.objective = adm.search.wpt || landmarkCycle(state, team);
    adm.axis = Math.atan2(adm.objective.y - adm.mainY, adm.objective.x - adm.mainX);
    adm.rally = { x: clamp(adm.mainX - Math.cos(adm.axis) * ADM.rallyBehind, T.edgeMargin, W - T.edgeMargin),
                  y: clamp(adm.mainY - Math.sin(adm.axis) * ADM.rallyBehind, T.edgeMargin, H - T.edgeMargin) };

    // 4. reserve release (sticky until posture leaves strike): losses mounting or no PD
    // umbrella left to saturate -> throw everything in
    if (adm.posture === 'strike' && !adm.reserveReleased &&
        (countLights(own) < ADM.reserveReleaseFrac * adm.strikeLights0 || !enemyHasPdCapital(foes)))
      adm.reserveReleased = true;

    // 5. task organization
    assignFleetRoles(state, team, adm, own);
  }
}
/* Per-ship nav post-pass (AUTO ships only — called behind the same !pinned && !order gate
   as squadronNav, which runs AFTER this so separation is always the last word on spacing).
   search/advance: scouts probe the objective on spread lanes, the screen pickets ahead of
   the main body, capitals bias line-abreast at a governed common speed, the reserve trails
   at the rally. strike: combat AI owns everyone except the held reserve. withdraw:
   everyone falls back to rally slots — role AI already fired this tick, so the retreat
   is fought, not fled. */
function admiralNav(state, ship) {
  if (!admiralTeam(state, ship.team) || !ship.nav) return;
  var adm = state.admiral[ship.team];
  if (adm.at < 0) return;
  var ADM = state.config.admiral, role = ship.ai.fleetRole || 'main';
  function centred(i, n) { return i - (Math.max(n, 1) - 1) / 2; }
  var px = -Math.sin(adm.axis), py = Math.cos(adm.axis);
  function rallySlot() {
    var off = centred(ship.ai.rallyIdx || 0, adm.nEligible || 1) * ADM.rallySpread;
    return { x: adm.rally.x + px * off, y: adm.rally.y + py * off };
  }
  if (adm.posture === 'withdraw') {
    var s = rallySlot();
    if (isCapital(ship)) {                 // fragile hulls route the retreat like any transit
      s = routeAround(state, ship, s);
      if (ship.cls === 'battleship') s = bbSkirtWell(state, ship, s);
    }
    ship.nav.x = s.x; ship.nav.y = s.y; ship.nav.arrive = true;
    return;
  }
  if (adm.posture === 'strike') {
    if (role === 'reserve') {
      var rs = rallySlot();
      ship.nav = { x: rs.x, y: rs.y, arrive: true, jink: isLight(ship) && threatened(state, ship) };
    }
    return;                                                  // everyone else: combat AI untouched
  }
  // search / advance:
  if (role === 'scout') {
    var off = centred(ship.ai.roleIdx || 0, ADM.scoutCount) * ADM.scoutSpread;
    ship.nav = { x: adm.objective.x + px * off, y: adm.objective.y + py * off,
                 arrive: false, jink: threatened(state, ship) };
    // shadow a detected foe from scoutHoldRange — the scout's job is contact, not combat
    var near = nearestWhere(state, ship, livingEnemies(state, ship.team), null);
    if (near && near.d < ADM.scoutHoldRange) {
      ship.nav.x = ship.x + (ship.x - near.ship.x) / (near.d || 1) * 200;
      ship.nav.y = ship.y + (ship.y - near.ship.y) / (near.d || 1) * 200;
    }
  } else if (role === 'screen') {
    var st = { x: adm.mainX + Math.cos(adm.axis) * ADM.screenDist,
               y: adm.mainY + Math.sin(adm.axis) * ADM.screenDist };
    var off2 = centred(ship.ai.roleIdx || 0, adm.nScreen || 1) * ADM.screenSpread;
    ship.nav = { x: st.x + px * off2, y: st.y + py * off2, arrive: true, jink: threatened(state, ship) };
  } else if (role === 'reserve') {
    var rs2 = rallySlot();
    ship.nav = { x: rs2.x, y: rs2.y, arrive: true, jink: false };
  } else { // main: role-AI goal kept; capitals get the body speed governor. The line-abreast
    // offset lives in huntPoint (admiralLineOffset) so it displaces the goal BEFORE
    // routeAround/bbSkirtWell — a post-routing nudge shoved the battleship into the very
    // rocks its skirt had just routed around (traced: 810 rock dmg, ground to death blind)
    if (isCapital(ship) && adm.fleetSpeed > 0)
      ship.nav.speedCap = Math.min(ship.nav.speedCap || 1e9, adm.fleetSpeed);
  }
}
/* Line-abreast displacement for a MAIN capital's hunt goal during search/advance: a
   perpendicular slot offset applied BEFORE routeAround/bbSkirtWell, so the fleet spreads
   into a line while every hull still routes its own safe path. Returns pt unchanged
   whenever the admiral is off/irrelevant. */
function admiralLineOffset(state, ship, pt) {
  if (!admiralTeam(state, ship.team) || !isCapital(ship)) return pt;
  if (ship.ai.fleetRole !== 'main') return pt;
  var adm = state.admiral[ship.team];
  if (adm.at < 0 || (adm.posture !== 'search' && adm.posture !== 'advance')) return pt;
  var ADM = state.config.admiral, T = state.config.terrain;
  var W = state.config.arena.w, H = state.config.arena.h;
  var off = ((ship.ai.roleIdx || 0) - (Math.max(adm.nCap, 1) - 1) / 2) * ADM.capitalLineSpacing;
  return { x: clamp(pt.x - Math.sin(adm.axis) * off, T.edgeMargin, W - T.edgeMargin),
           y: clamp(pt.y + Math.cos(adm.axis) * off, T.edgeMargin, H - T.edgeMargin) };
}
/* Squadron-staggered wave entry: squadron k crosses the line k*commitStaggerSeconds after
   window-open (bombers are sqOrd 0 — the payload leads); the held reserve never commits.
   The free-fire path (past the window, or no PD capital) skips the stagger. */
function squadCommitting(state, ship) {
  if (!teamCommitting(state, ship.team)) return false;
  if (admiralTeam(state, ship.team) && ship.ai.fleetRole === 'reserve') return false;
  var c = state.commit[ship.team], AI = state.config.ai;
  if (state.time >= c.until) return true;                    // no-PD-capital free-fire path
  var start = c.until - AI.commitSeconds;
  return state.time >= start + (ship.ai.sqOrd || 0) * AI.commitStaggerSeconds;
}
