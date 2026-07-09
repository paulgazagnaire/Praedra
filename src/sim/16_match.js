/* ---------------- Match orchestration ---------------- */
function resolveMatch(state, reason) {
  function fleetValue(team) {
    var v = 0;
    for (var i = 0; i < state.ships.length; i++) {
      var s = state.ships[i];
      if (s.alive && s.team === team) v += s.def.cost * (s.hp / s.maxHp);
    }
    return v;
  }
  function survivors(team) {
    var out = {};
    for (var i = 0; i < state.ships.length; i++) {
      var s = state.ships[i];
      if (s.alive && s.team === team) out[s.cls] = (out[s.cls] || 0) + 1;
    }
    return out;
  }
  var va = fleetValue('A'), vb = fleetValue('B');
  var winner;
  if (reason === 'timer') {
    // timer resolution: the side that DESTROYED more of the enemy fleet wins —
    // hiding a fat surviving hull behind PD is not winning
    function hpFracLost(team) {
      var cur = 0;
      for (var i = 0; i < state.ships.length; i++) {
        var sh = state.ships[i];
        if (sh.team === team && sh.alive) cur += sh.hp;
      }
      return 1 - cur / (state.initialHp[team] || 1);
    }
    var lostA = hpFracLost('A'), lostB = hpFracLost('B');
    if (Math.abs(lostA - lostB) > 1e-9) winner = lostA < lostB ? 'A' : 'B';
    else if (Math.abs(state.damage.A - state.damage.B) > 1e-9) winner = state.damage.A > state.damage.B ? 'A' : 'B';
    else winner = 'draw';
  } else if (Math.abs(va - vb) > 1e-9) winner = va > vb ? 'A' : 'B';
  else if (Math.abs(state.damage.A - state.damage.B) > 1e-9) winner = state.damage.A > state.damage.B ? 'A' : 'B';
  else winner = 'draw';
  return {
    winner: winner, reason: reason,
    ticks: state.tick, seconds: state.tick / state.config.tickRate,
    nonResolution: reason === 'timer',
    fleetValueA: va, fleetValueB: vb,
    damageA: state.damage.A, damageB: state.damage.B,
    survivorsA: survivors('A'), survivorsB: survivors('B'),
    stats: deepClone(state.stats),
  };
}

function stepMatch(match) {
  var state = match.state;
  if (match.done) return;
  var dt = 1 / state.config.tickRate;
  state.tick++; state.time += dt;
  // movers list first (queries scan it alongside the static grid), then the grid —
  // rebuilt only when a rock's settled/moving state flipped or a rock split/died
  var mv = state.movers;
  mv.length = 0;
  for (var m0 = 0; m0 < state.asteroids.length; m0++) {
    var mr = state.asteroids[m0];
    if (mr.alive && mr.moving) mv.push(mr);
  }
  if (!state.grid || state.gridDirty) {
    rebuildGrid(state);
    state.gridDirty = false;
  }
  rebuildMoverGrid(state); // every tick: movers relocate constantly, the list is short
  if (state.tick % 6 === 0) state.losCache = {};
  if (state.tick % 90 === 0) state.contactCache = {}; // entries carry their own TTL; the
                                                      // wipe just stops dead pairs piling up

  // alive rosters (stable order)
  state.aliveA = []; state.aliveB = [];
  for (var i = 0; i < state.ships.length; i++) {
    var s = state.ships[i];
    if (s.alive) (s.team === 'A' ? state.aliveA : state.aliveB).push(s);
  }

  computeDetection(state);
  updateDoctrine(state); // v2 team battle picture (focus/pack/volley) — before commits consume it
  updateCommits(state);
  for (var a = 0; a < state.ships.length; a++) {
    var sh = state.ships[a];
    if (!sh.alive) continue;
    aiTick(state, sh, dt);
    updatePD(state, sh, dt);
  }
  applyGravity(state, dt); // before the pilots plan: they see (and fight) the drift
  for (var b = 0; b < state.ships.length; b++) {
    var sb = state.ships[b];
    if (!sb.alive) continue;
    autopilot(state, sb, dt);
    integrateShip(state, sb, dt);
  }
  collideShipsAndRocks(state, dt);
  updateTorpedoes(state, dt);
  updateBombs(state, dt);
  updateHeavySlugs(state, dt); // fixed slot: after bombs, before asteroids (deterministic RNG order)
  updateAsteroids(state, dt);
  if (state.tick % 30 === 0) {
    state.torps = state.torps.filter(function (t) { return t.alive; });
    state.bombs = state.bombs.filter(function (bm) { return bm.alive; });
    state.slugs = state.slugs.filter(function (sg) { return sg.alive; });
    state.asteroids = state.asteroids.filter(function (o) { return o.alive; });
  }

  var na = 0, nb = 0;
  for (var c = 0; c < state.ships.length; c++) {
    var sc = state.ships[c];
    if (sc.alive) { if (sc.team === 'A') na++; else nb++; }
  }
  if ((na === 0 || nb === 0) && state.hadBothTeams) {
    match.result = resolveMatch(state, 'elimination');
    match.done = true;
  } else if (state.tick >= state.tickCap) {
    match.result = resolveMatch(state, 'timer'); // force-resolve: the sim can NEVER hang
    match.done = true;
  }
  match.tick = state.tick;
}

function baseState(seed, overrides) {
  var config = deepMerge(deepClone(CONFIG_DEFAULTS), overrides || {});
  var rng = makeRng((seed >>> 0) || 1);
  rng.next(); rng.next(); rng.next(); // warm up small seeds
  return {
    config: config, rng: rng, seed: seed,
    tick: 0, time: 0, nextId: 1,
    ships: [], shipById: {}, asteroids: [], torps: [], bombs: [], slugs: [], movers: [],
    events: [], losCache: {}, contactCache: {}, damage: { A: 0, B: 0 },
    stats: { railShots: 0, railHitsShip: 0, railHitsRock: 0,
             torpsFired: { A: 0, B: 0 }, torpsPD: { A: 0, B: 0 }, torpsHit: { A: 0, B: 0 },
             bombsFired: { A: 0, B: 0 }, bombsPD: { A: 0, B: 0 }, bombsHitShip: { A: 0, B: 0 },
             dmgTo: { A: {}, B: {} }, deaths: [], waves: { A: 0, B: 0 },
             torpLaunch: { A: [], B: [] },
             runEntries: 0, fireBlocked: { cooldown: 0, range: 0, los: 0, lane: 0 }, salvosFired: 0,
             splits: 0 },
    aliveA: [], aliveB: [], spawnA: null, spawnB: null, gravSources: [],
    detA: [], detB: [],                       // enemy ships DETECTED by team A / team B
    lastSeenShip: {},                         // shipId -> {x, y, t} (team-shared memory)
    lastContact: { A: null, B: null },        // most recent enemy sighting per team
    commit: { A: { until: -1, cool: 0, targetId: -1, flank: 0, anvil: false }, B: { until: -1, cool: 0, targetId: -1, flank: 0, anvil: false } },
    doctrine: { A: { focusStrike: -1, focusGun: -1, packTargetId: -1, packIds: null, volleyGo: false },
                B: { focusStrike: -1, focusGun: -1, packTargetId: -1, packIds: null, volleyGo: false } },
    tickCap: 0, hadBothTeams: false,
  };
}
function finishSetup(state) {
  state.tickCap = Math.round(state.config.matchTimerSeconds * state.config.tickRate);
  var a = 0, b = 0;
  state.initialHp = { A: 0, B: 0 };
  for (var i = 0; i < state.ships.length; i++) {
    var sh = state.ships[i];
    state.shipById[sh.id] = sh;
    if (sh.team === 'A') a++; else b++;
    state.initialHp[sh.team] += sh.maxHp;
    sh.ai.jinkPhase = (sh.id * 0.37) % 1;
  }
  state.hadBothTeams = a > 0 && b > 0;
}
function resolveFleet(config, spec) {
  if (typeof spec === 'string') {
    var p = config.presets[spec];
    if (!p) throw new Error('unknown preset: ' + spec);
    return p;
  }
  return spec || [];
}
function makeMatchObject(state) {
  var match = { state: state, config: state.config, tick: 0, done: false, result: null };
  match.step = function () { stepMatch(match); };
  return match;
}

var api = {
  defaultConfig: function () { return deepClone(CONFIG_DEFAULTS); },
  PRESETS: deepClone(CONFIG_DEFAULTS.presets),

  createMatch: function (opts) {
    var state = baseState(opts.seed, opts.overrides);
    generateTerrain(state, state.rng);
    var fa = resolveFleet(state.config, opts.teamA || 'RAILGUN');
    var fb = resolveFleet(state.config, opts.teamB || 'SWARM');
    function spawnFleet(fleet, at, facing, team) {
      var px = -Math.sin(facing), py = Math.cos(facing); // rank axis
      var bx = -Math.cos(facing), by = -Math.sin(facing); // ranks stack rearward
      var perRank = state.config.spawnRankSize;
      // per-fleet pitch: a radius-78 battleship at the flat 70-px gap would spawn interpenetrating
      // and blow the formation apart, so open the rank/ranks to 2.4x the fleet's largest hull.
      var maxR = 0;
      for (var q = 0; q < fleet.length; q++) maxR = Math.max(maxR, state.config.ships[fleet[q]].radius);
      var gap = Math.max(state.config.spawnFormationGap, 2.4 * maxR); // 2.4*78 = 187 for a BB fleet
      for (var i = 0; i < fleet.length; i++) {
        var rank = Math.floor(i / perRank), col = i % perRank;
        var inRank = Math.min(perRank, fleet.length - rank * perRank);
        var off = (col - (inRank - 1) / 2) * gap;
        var back = rank * gap * 1.25;
        state.ships.push(makeShip(state, fleet[i], team, at.x + px * off + bx * back, at.y + py * off + by * back, facing));
      }
    }
    var faceA = Math.atan2(state.spawnB.y - state.spawnA.y, state.spawnB.x - state.spawnA.x);
    spawnFleet(fa, state.spawnA, faceA, 'A');
    spawnFleet(fb, state.spawnB, normAngle(faceA + Math.PI), 'B');
    finishSetup(state);
    return makeMatchObject(state);
  },

  createScenario: function (opts) {
    var state = baseState(opts.seed, opts.overrides);
    state.spawnA = { x: 0, y: 0 }; state.spawnB = { x: 0, y: 0 };
    var rocks = opts.asteroids || [];
    for (var i = 0; i < rocks.length; i++) state.asteroids.push(makeAsteroid(state, rocks[i].x, rocks[i].y, rocks[i].r));
    var ships = opts.ships || [];
    for (var j = 0; j < ships.length; j++) {
      var sp = ships[j];
      var sh = makeShip(state, sp.cls, sp.team, sp.x, sp.y, sp.heading || 0);
      sh.vx = sp.vx || 0; sh.vy = sp.vy || 0;
      sh.pinned = !!sp.pinned;
      state.ships.push(sh);
    }
    finishSetup(state);
    return makeMatchObject(state);
  },

  /* RTS command layer: sets goals; the autopilot executes (PRD §9).
     order: { type: 'move'|'attackmove'|'attack'|'attackrock'|'hold'|'auto', x?, y?, targetId? } */
  issueOrder: function (match, shipIds, order) {
    var st = match.state;
    for (var i = 0; i < shipIds.length; i++) {
      var sh = st.shipById[shipIds[i]];
      if (!sh || !sh.alive) continue;
      if (order.type === 'auto') { sh.order = null; continue; }
      var o = { type: order.type, x: order.x, y: order.y, targetId: order.targetId };
      if (o.type === 'hold' && (o.x == null || o.y == null)) { o.x = sh.x; o.y = sh.y; }
      sh.order = o;
      sh.ai.mode = 'seek'; // reset any role state machine
      pushEvent(st, { kind: 'order', x: o.x != null ? o.x : sh.x, y: o.y != null ? o.y : sh.y, order: o.type });
    }
  },

  runMatch: function (opts) {
    var m = api.createMatch(opts);
    var guard = m.state.tickCap + 5;
    while (!m.done && guard-- > 0) m.step();
    return m.result;
  },
};
