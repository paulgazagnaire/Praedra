/* Praedra sim module: fleet — coordinated layer: focus fire, squadrons, ambush — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- Coordinated fleet layer (focus fire, squadrons, ambush) ----------------
   Everything here is deterministic pure-state math (no RNG) and gated by ai.smartTeams /
   squadron.enabledTeams so self-play sweeps can A/B the smart layer against the base AI. */
function smartTeam(state, team) { return state.config.ai.smartTeams.indexOf(team) >= 0; }
function squadronTeam(state, team) { return state.config.squadron.enabledTeams.indexOf(team) >= 0; }

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
    for (var c = 0; c < 2; c++) {
      var members = [];
      for (var i = 0; i < own.length; i++) if (own[i].cls === classes[c]) members.push(own[i]);
      for (var m = 0; m < members.length; m++) {
        var sh = members[m];
        var sq = Math.floor(m / SQ.size);
        sh.ai.sqSlot = m % SQ.size;
        sh.ai.sqN = Math.min(SQ.size, members.length - sq * SQ.size);
        sh.ai.sqLead = members[sq * SQ.size].id;
      }
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
    var want = (ship.cls === 'bomber' && o.cls === 'bomber') ? SQ.bomberSep : SQ.lightSep;
    var d = dist(ship.x, ship.y, o.x, o.y);
    if (d >= want || d < 1e-6) continue;
    var w = (want - d) / want;
    sepX += ((ship.x - o.x) / d) * w * want;
    sepY += ((ship.y - o.y) / d) * w * want;
  }
  var gain = ship.ai.mode === 'run' ? SQ.runSepGain : SQ.sepGain;
  var sm = len(sepX, sepY), cap = SQ.bomberSep * 1.6;
  if (sm > cap) { sepX *= cap / sm; sepY *= cap / sm; }
  ship.nav.x += sepX * gain;
  ship.nav.y += sepY * gain;
  if (ship.ai.sqLead != null && livingEnemies(state, ship.team).length === 0) {
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
  var lead = state.shipById[ship.ai.sqLead];
  var ref = lead && lead.alive ? lead : ship;
  var base = Math.atan2(ref.y - tgt.y, ref.x - tgt.x);
  var a = base + (ship.ai.sqSlot - (ship.ai.sqN - 1) / 2) * SQ.bearingSpread;
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

