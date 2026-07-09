#!/usr/bin/env node
// Praedra scripted acceptance tests — headless, Node 22 ESM, zero npm deps.
//
//   node harness/tests.mjs                 # loads <repo-root>/index.html
//   node harness/tests.mjs --file PATH     # load a different sim file
//
// Binding API: docs/SIM_CONTRACT.md.  Criteria: praedra-prd-v2.md §8/§8B.
// Exit code = number of failed tests (capped at 100). Every loop is bounded
// by a tick cap — nothing here can run unbounded even against a broken sim.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import path from 'node:path';
import vm from 'node:vm';

// ---------------------------------------------------------------- sim loading

const HERE = path.dirname(fileURLToPath(import.meta.url));

function cliFile() {
  const argv = process.argv.slice(2);
  const i = argv.indexOf('--file');
  if (i !== -1 && argv[i + 1]) return argv[i + 1];
  const eq = argv.find((a) => a.startsWith('--file='));
  return eq ? eq.slice('--file='.length) : null;
}

const SIM_PATH = path.resolve(cliFile() ?? path.join(HERE, '..', 'index.html'));

function loadSim(file) {
  let html;
  try {
    html = readFileSync(file, 'utf8');
  } catch (err) {
    console.error(`Cannot read sim file ${file}: ${err.message}`);
    process.exit(100);
  }
  const BEGIN = '/* ===== SIM BEGIN ===== */';
  const END = '/* ===== SIM END ===== */';
  const b = html.indexOf(BEGIN);
  const e = html.indexOf(END);
  if (b === -1 || e === -1 || e <= b) {
    console.error(`Sim markers not found in ${file} (need "${BEGIN}" ... "${END}")`);
    process.exit(100);
  }
  const sandbox = { console };
  vm.runInNewContext(html.slice(b + BEGIN.length, e), sandbox, {
    filename: 'praedra-sim.js',
    timeout: 15000,
  });
  if (!sandbox.Praedra) {
    console.error('Sim evaluated but did not define a global `Praedra`.');
    process.exit(100);
  }
  return sandbox.Praedra;
}

const Praedra = loadSim(SIM_PATH);
console.log(`sim: ${SIM_PATH}`);

// ---------------------------------------------------------- micro test runner

const TESTS = [];
function test(name, fn) { TESTS.push({ name, fn }); }
function assert(cond, msg) { if (!cond) throw new Error(msg); }
const warn = (msg) => console.log(`       warn: ${msg}`);

function runAll() {
  let passed = 0;
  let failed = 0;
  for (const { name, fn } of TESTS) {
    try {
      fn();
      passed += 1;
      console.log(`PASS   ${name}`);
    } catch (err) {
      failed += 1;
      const reason = String(err && err.message ? err.message : err).split('\n')[0];
      console.log(`FAIL   ${name} — ${reason}`);
    }
  }
  console.log(`${passed} passed, ${failed} failed`);
  process.exit(Math.min(failed, 100));
}

// -------------------------------------------------------------------- helpers

const TICK_RATE = 60; // contract: fixed 60 Hz ticks

function stepSeconds(match, seconds) {
  const n = Math.round(seconds * TICK_RATE); // hard bound, never an open loop
  for (let i = 0; i < n && !match.done; i++) match.step();
}

function findShip(match, team, cls) {
  const s = match.state.ships.find((x) => x.team === team && x.cls === cls);
  assert(s, `scenario has no ${team}/${cls} ship in state.ships`);
  return s;
}

function hpOf(match, id) {
  const s = match.state.ships.find((x) => x.id === id);
  return s ? s.hp : 0; // vanished from state ⇒ treat as destroyed
}

function pinned(cls, team, x, y, heading = 0) {
  return { cls, team, x, y, heading, pinned: true };
}

// Walk the live CONFIG generically (contract: read numbers at runtime, don't
// hardcode) and build a nested overrides object zeroing every numeric field
// whose key mentions "damage" (damage, bombDamage, impactDamage, ...).
// keepRailgun preserves the WHOLE railgun subtree wherever "railgun" appears
// in the path — that includes both railgun.damage (ship damage) AND
// railgun.rockDamageMult (rock damage), since the sim derives rock damage as
// railgun.damage * railgun.rockDamageMult (zeroing one zeroes both anyway).
// torpedo.rockDamageMult also matches /damage/i but its path has no "railgun"
// in it, so it's zeroed like any other non-railgun damage field even when
// keepRailgun is set — that's intentional (isolates the railgun for testing).
function zeroDamageOverrides(cfg, keepRailgun) {
  const out = {};
  (function walk(node, ov, pathStr) {
    for (const [k, v] of Object.entries(node)) {
      const p = pathStr ? `${pathStr}.${k}` : k;
      if (v && typeof v === 'object' && !Array.isArray(v)) {
        const child = {};
        walk(v, child, p);
        if (Object.keys(child).length > 0) ov[k] = child;
      } else if (typeof v === 'number' && /damage/i.test(k)) {
        if (keepRailgun && /railgun/i.test(p)) continue;
        ov[k] = 0;
      }
    }
  })(cfg, out, '');
  return out;
}

function railgunMinRange(cfg) {
  let found = null;
  (function walk(node, pathStr) {
    for (const [k, v] of Object.entries(node)) {
      const p = pathStr ? `${pathStr}.${k}` : k;
      if (v && typeof v === 'object' && !Array.isArray(v)) walk(v, p);
      else if (typeof v === 'number' && /railgun/i.test(p) && /minrange/i.test(k)) found = v;
    }
  })(cfg, '');
  return found; // handles railgun.minRange and railgunMinRange spellings
}

// A pinned (coasting) ship's throttle is always 0, so its visible range to enemy
// sensors is signature * detection.thrustMultMin (contract: detection.md in
// SIM_CONTRACT). Tests that pin a target must keep it inside this range or it is
// never DETECTED — and an undetected target can't be engaged at all (aiPinned only
// fires on state.detA/detB members), which would make a test pass for the wrong
// reason (never fired on) instead of the reason under test.
function coastingVisibleRange(cfg, cls) {
  const sig = cfg.ships && cfg.ships[cls] && cfg.ships[cls].signature;
  assert(typeof sig === 'number', `no ships.${cls}.signature in live config — detection helper needs updating`);
  return sig * cfg.detection.thrustMultMin;
}

// Fail loudly (rather than silently exercising the wrong code path) if a chosen
// pinned-target distance doesn't leave `marginPx` of slack inside its coasting
// detection range.
function assertDetectable(cfg, cls, dist, marginPx, label) {
  const vis = coastingVisibleRange(cfg, cls);
  assert(dist + marginPx <= vis,
    `${label}: pinned ${cls} at ${dist}px leaves < ${marginPx}px margin to its coasting detection range ` +
    `${vis}px (signature ${cfg.ships[cls].signature} x thrustMultMin ${cfg.detection.thrustMultMin}) — ` +
    'config changed, scenario needs new numbers');
  return vis;
}

function dist2D(ax, ay, bx, by) { return Math.sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)); }

// Mid-arena-ish anchor for new scenario tests (arena is 8000x5600; (0,0) is a corner).
// The old tests use small coordinates near that corner and get away with it because
// their ships are pinned (never touch the walls) and have no debris/AI ships that
// would; new tests use this offset to stay clear of wall effects on principle.
const MID = { x: 2000, y: 1500 };
// presets are 42-pt armadas now (~10s headless); timing/battery tests use small fleets
const SMALL_A = ['destroyer', 'frigate'];
const SMALL_B = ['bomber', 'bomber', 'bomber', 'interceptor', 'interceptor', 'interceptor'];


// ---------------------------------------------------------------------- tests

test('determinism', () => {
  const opts = () => ({ seed: 7, overrides: { terrainDensity: 0.5 }, teamA: SMALL_A, teamB: SMALL_B });
  const r1 = Praedra.runMatch(opts());
  const r2 = Praedra.runMatch(opts());
  assert(isDeepStrictEqual(r1, r2), 'same seed + config + teams produced different results');
  const o8 = opts();
  o8.seed = 8;
  const r3 = Praedra.runMatch(o8);
  if (r3.ticks === r1.ticks && r3.winner === r1.winner) {
    warn(`seed 8 matched seed 7 in ticks (${r3.ticks}) and winner (${r3.winner}) — check PRNG seeding`);
  }
});

test('always-resolves', () => {
  const cap = Praedra.defaultConfig().matchTimerSeconds * TICK_RATE;
  for (let seed = 1; seed <= 5; seed++) {
    for (const terrainDensity of [0.1, 0.5, 0.9]) {
      const tag = `seed ${seed} density ${terrainDensity}`;
      const r = Praedra.runMatch({ seed, overrides: { terrainDensity }, teamA: SMALL_A, teamB: SMALL_B });
      assert(r && Number.isFinite(r.ticks), `${tag}: no numeric ticks in result`);
      assert(r.ticks <= cap + 2, `${tag}: ticks ${r.ticks} exceeds tick cap ${cap} (+2 slack)`);
      assert(['A', 'B', 'draw'].includes(r.winner), `${tag}: winner is ${JSON.stringify(r.winner)}`);
    }
  }
});

// Destroyer at origin aiming down +x at a frigate 400 away; a rock sits dead-centre
// on the firing line. destructibleAsteroids is now ALWAYS ignored (destruction is
// always on — see docs/SIM_CONTRACT.md), so we can't rely on that flag to keep the
// rock intact; instead the rock is sized so it cannot possibly be shattered inside
// the test window even under sustained railgun fire: r=140 -> HP = 80*(140/60)^2 ≈
// 436, and the window is 10 sim-seconds -> at most 3 railgun slugs (cooldown 4s) =
// 3*66 = 198 rock damage, well short of 436. (In practice a rock dead-centre on the
// line ALSO blocks detection — see the isDetectedBy/losShips coupling in the sim —
// so the frigate here is never even fired at; the oversized rock + short window is
// belt-and-suspenders robustness against that assumption changing.) With LOS
// blocked NOTHING may hit the frigate — torpedoes respect LOS too (PRD §4, load-bearing).
function losScenario(withAsteroid) {
  const cfg = Praedra.defaultConfig();
  const dist = 400;
  assertDetectable(cfg, 'frigate', dist, 100, 'los-blocked/los-clear');
  return Praedra.createScenario({
    seed: 1,
    ships: [pinned('destroyer', 'A', 0, 0, 0), pinned('frigate', 'B', dist, 0, 0)],
    asteroids: withAsteroid ? [{ x: 200, y: 0, r: 140 }] : [],
  });
}

test('los-blocked', () => {
  const m = losScenario(true);
  const frig = findShip(m, 'B', 'frigate');
  const hp0 = frig.hp;
  stepSeconds(m, 10);
  const hp1 = hpOf(m, frig.id);
  assert(hp1 === hp0, `frigate hp ${hp0} -> ${hp1} despite asteroid blocking LOS`);
});

test('los-clear', () => {
  const m = losScenario(false);
  const frig = findShip(m, 'B', 'frigate');
  const hp0 = frig.hp;
  stepSeconds(m, 15);
  const hp1 = hpOf(m, frig.id);
  assert(hp1 < hp0, `frigate hp still ${hp1} after 15 s with clear LOS — railgun never connected`);
});

test('min-range-dead-zone', () => {
  const cfg = Praedra.defaultConfig();
  const minRange = railgunMinRange(cfg);
  let dist = 100;
  if (minRange == null) warn('railgun min range not found in config; assuming 100 is inside the dead zone');
  else if (minRange <= dist) dist = Math.max(10, Math.floor(minRange / 2)); // stay strictly inside
  // The target must be DETECTED for the dead zone (not simple non-detection) to be
  // what's actually gating fire — otherwise this would pass for the wrong reason.
  assertDetectable(cfg, 'frigate', dist, 100, 'min-range-dead-zone');
  const m = Praedra.createScenario({
    seed: 2,
    overrides: zeroDamageOverrides(cfg, true), // only the railgun can hurt anything
    ships: [pinned('destroyer', 'A', 0, 0, 0), pinned('frigate', 'B', dist, 0, 0)],
    asteroids: [],
  });
  const frig = findShip(m, 'B', 'frigate');
  const hp0 = frig.hp;
  stepSeconds(m, 15);
  const hp1 = hpOf(m, frig.id);
  assert(hp1 === hp0, `frigate at ${dist} (inside minRange ${minRange ?? '>=150'}) took damage: hp ${hp0} -> ${hp1}`);
});

// Pick a pinned-target distance that's inside railgun range AND leaves a safety
// margin inside BOTH classes' coasting detection range (the interceptor's is the
// tight one: signature 750 x thrustMultMin 0.6 = 450). Computed against live
// config every call and asserted, rather than a bare hardcoded number, so a future
// config change fails the test loudly instead of quietly detecting nothing.
function evasionGateDistance(cfg) {
  const dist = 350;
  const margin = 100;
  assert(dist > cfg.railgun.minRange,
    `evasion-gate distance ${dist} falls inside railgun minRange ${cfg.railgun.minRange} — config changed, pick new numbers`);
  assert(dist <= cfg.railgun.maxRange,
    `evasion-gate distance ${dist} exceeds railgun maxRange ${cfg.railgun.maxRange} — config changed, pick new numbers`);
  const minVis = Math.min(coastingVisibleRange(cfg, 'frigate'), coastingVisibleRange(cfg, 'interceptor'));
  assert(dist + margin <= minVis,
    `evasion-gate distance ${dist} leaves < ${margin}px margin to the tightest coasting detection range ` +
    `${minVis}px — config changed, pick new numbers`);
  return dist;
}

// All non-railgun damage zeroed; pin a target (inside detection, outside the dead
// zone) for 120 sim-seconds and measure hp lost. Evasion must gate the railgun:
// the interceptor (0.80) loses far less than a frigate (0.15) in the same seat.
function railgunLossAgainst(cls) {
  const cfg = Praedra.defaultConfig();
  const dist = evasionGateDistance(cfg);
  const m = Praedra.createScenario({
    seed: 3,
    overrides: zeroDamageOverrides(cfg, true),
    ships: [pinned('destroyer', 'A', 0, 0, 0), pinned(cls, 'B', dist, 0, 0)],
    asteroids: [],
  });
  const target = findShip(m, 'B', cls);
  const hp0 = target.hp;
  stepSeconds(m, 120);
  return { lost: hp0 - hpOf(m, target.id), maxHp: target.maxHp };
}

test('railgun-evasion-gate', () => {
  const frig = railgunLossAgainst('frigate');
  const icpt = railgunLossAgainst('interceptor');
  assert(frig.lost >= 0.4 * frig.maxHp,
    `pinned frigate lost only ${frig.lost}/${frig.maxHp} hp in 120 s — railgun not substantial vs slow targets`);
  assert(icpt.lost < 0.25 * frig.lost,
    `interceptor lost ${icpt.lost} hp vs frigate's ${frig.lost} — evasion is not gating the railgun`);
});

test('tick-cap-forced', () => {
  const m = Praedra.createScenario({
    seed: 4,
    overrides: zeroDamageOverrides(Praedra.defaultConfig(), false), // NOTHING can deal damage
    ships: [pinned('destroyer', 'A', -600, -600, 0), pinned('destroyer', 'B', 600, 600, 0)],
    asteroids: [],
  });
  const cap = m.config.matchTimerSeconds * TICK_RATE;
  for (let i = 0; i < cap + 10 && !m.done; i++) m.step(); // bounded just past cap
  assert(m.done, `match still not done after ${cap + 10} ticks (cap ${cap}) — force-resolve broken`);
  assert(m.tick <= cap + 2, `resolved at tick ${m.tick}, past cap ${cap}`);
  if (m.tick !== cap) warn(`resolved at tick ${m.tick}, expected exactly the cap ${cap}`);
  assert(m.result && m.result.nonResolution === true,
    `result.nonResolution is ${m.result && m.result.nonResolution}, want true`);
  assert(m.result.reason === 'timer', `result.reason is ${JSON.stringify(m.result.reason)}, want 'timer'`);
});

// Titan-scale maps (8000x5600, 360s timer) made small-fleet matches legitimately LONG
// in sim-seconds (hunt + transit), so the wall-clock budget is per-tick-derived: the
// SIM_CONTRACT bound is per-tick cost, and the worst case is the full tick cap.
test('wallclock', () => {
  const t0 = Date.now(); // harness may use Date; only the sim may not
  const r = Praedra.runMatch({ seed: 1, overrides: { terrainDensity: 0.5 }, teamA: SMALL_A, teamB: SMALL_B });
  const ms = Date.now() - t0;
  assert(r && r.winner !== undefined, 'runMatch returned no result object');
  const usPerTick = (ms * 1000) / r.ticks;
  assert(usPerTick < 2000, `small-fleet match cost ${usPerTick.toFixed(0)} us/tick (budget 2000 us/tick)`);
  // per-tick is THE contract bound (SIM_CONTRACT: budget wall-clock per tick, not per match);
  // the absolute ceiling only guards catastrophic blowups. It used to sit at 20s — a mere ~4%
  // above the measured runtime once whole-well gravity landed — and would flake on any loaded
  // runner without a real defect.
  assert(ms < 40000, `full match took ${ms} ms wall-clock (catastrophic-blowup ceiling 40 s)`);
});

// ------------------------------------------------------------- new: detection

// A ship is only a legal target once the opposing team DETECTS it (state.detA/detB);
// aiPinned (and every AI role) only ever engages state.detA/detB members. Detection
// range for a coasting (pinned, throttle 0) ship is signature * thrustMultMin.
// Beyond that range: never detected, never fired on. Inside it: detected and engaged.
test('detection-gate', () => {
  const cfg = Praedra.defaultConfig();
  const interceptorVis = coastingVisibleRange(cfg, 'interceptor'); // 750 * 0.6 = 450 by default
  const beyondDist = interceptorVis + 150; // clearly outside coasting detection
  const insideDist = interceptorVis - 150; // clearly inside coasting detection
  assert(insideDist > cfg.railgun.minRange,
    `detection-gate inside distance ${insideDist} falls inside railgun minRange ${cfg.railgun.minRange} — config changed, pick new numbers`);
  assert(beyondDist <= cfg.railgun.maxRange,
    `detection-gate beyond distance ${beyondDist} exceeds railgun maxRange ${cfg.railgun.maxRange} — config changed, pick new numbers`);

  function scenario(dist) {
    return Praedra.createScenario({
      seed: 11,
      overrides: zeroDamageOverrides(cfg, true), // only railgun can hurt anything
      ships: [pinned('destroyer', 'A', MID.x, MID.y, 0), pinned('interceptor', 'B', MID.x + dist, MID.y, Math.PI)],
      asteroids: [],
    });
  }

  const beyond = scenario(beyondDist);
  const bIcpt = findShip(beyond, 'B', 'interceptor');
  const bHp0 = bIcpt.hp;
  stepSeconds(beyond, 30);
  const bHp1 = hpOf(beyond, bIcpt.id);
  assert(bHp1 === bHp0,
    `interceptor at ${beyondDist} (coasting vis ${interceptorVis}) took damage: hp ${bHp0} -> ${bHp1} — should never be detected`);

  const inside = scenario(insideDist);
  const iIcpt = findShip(inside, 'B', 'interceptor');
  const iHp0 = iIcpt.hp;
  stepSeconds(inside, 120); // 30 shots @ ~20% hit chance ≈ 6 expected hits — generous window
  const iHp1 = hpOf(inside, iIcpt.id);
  assert(iHp1 < iHp0,
    `interceptor at ${insideDist} (coasting vis ${interceptorVis}) took no damage in 120s of being detected`);
});

// Railgun and gatling hit the FIRST hull on the firing ray regardless of team (PRD
// friendly fire, load-bearing). A friendly frigate parked dead-centre on the
// destroyer's line to a detected enemy must eat the slugs meant for that enemy.
test('friendly-fire-railgun', () => {
  const cfg = Praedra.defaultConfig();
  const m = Praedra.createScenario({
    seed: 12,
    overrides: zeroDamageOverrides(cfg, true), // only railgun can hurt anything
    ships: [
      pinned('destroyer', 'A', MID.x, MID.y, 0),
      pinned('frigate', 'A', MID.x + 250, MID.y, Math.PI), // friendly, dead on the firing line
      pinned('frigate', 'B', MID.x + 500, MID.y, Math.PI),
    ],
    asteroids: [],
  });
  const friendly = findShip(m, 'A', 'frigate');
  const hp0 = friendly.hp;
  stepSeconds(m, 40);
  const hp1 = hpOf(m, friendly.id);
  assert(hp1 < hp0, `friendly frigate on the firing line kept full hp (${hp0}) after 40s — friendly fire not modeled`);
});

// Asteroid destruction is always on; a shattered rock splits into debris.fragmentCount
// children with burst velocity. NOTE: a rock placed directly BETWEEN two static ships
// fully blocks LOS — and detection reuses that same LOS check (isDetectedBy/losShips) —
// so such a target is never even DETECTED and a pinned ship's AI (which only engages
// state.detA/detB) would never fire on it or the rock hiding it at all (verified against
// the sim directly: see los-blocked, which relies on exactly this). To exercise rock
// destruction with pinned ships we instead put the rock BEYOND a detected, evasive
// target, directly on the destroyer's boresight: fireRailgun's "missed slug flies on
// downrange" path (index.html, fireRailgun) then damages it. The target's evasion is
// boosted for this scenario only so most shots miss (and feed the rock) regardless of
// RNG seed — the shipped frigate evasion (0.15) would make this seed-dependent.
test('asteroid-splits', () => {
  const cfg = Praedra.defaultConfig();
  const overrides = zeroDamageOverrides(cfg, true); // only railgun can hurt anything (ship or rock)
  overrides.ships = { frigate: { evasion: 0.92 } }; // force most shots to miss and fly downrange
  const m = Praedra.createScenario({
    seed: 21,
    overrides,
    ships: [
      pinned('destroyer', 'A', MID.x, MID.y, 0),
      pinned('frigate', 'B', MID.x + 250, MID.y, Math.PI),
    ],
    asteroids: [{ x: MID.x + 500, y: MID.y, r: 60 }],
  });
  const rockId = m.state.asteroids[0].id;
  const initialCount = m.state.asteroids.length;
  let sawMovingChild = false;
  for (let i = 0; i < 60 * TICK_RATE && !m.done; i++) {
    m.step();
    for (const a of m.state.asteroids) if (a.id !== rockId && a.moving) sawMovingChild = true;
  }
  const rock = m.state.asteroids.find((a) => a.id === rockId);
  assert(!rock || !rock.alive, `original rock (id ${rockId}) still alive after 60s of railgun fire`);
  const finalAlive = m.state.asteroids.filter((a) => a.alive).length;
  assert(finalAlive >= initialCount + 3,
    `only ${finalAlive} live asteroids after the split (started at ${initialCount}, wanted +3 or more)`);
  assert(sawMovingChild, 'no debris fragment was ever observed with moving === true — a split should launch children with velocity');
});

// issueOrder sets a goal the autopilot flies to; a completed move/attackmove order
// becomes {type:'hold'} (PRD §9, never silently reverts to auto).
test('order-move-hold', () => {
  const m = Praedra.createMatch({ seed: 1, overrides: { terrainDensity: 0.3 }, teamA: SMALL_A, teamB: SMALL_B });
  const ship = findShip(m, 'A', 'frigate'); // nimble enough to close & settle within the window
  const cx = m.config.arena.w / 2, cy = m.config.arena.h / 2;
  const dx = cx - ship.x, dy = cy - ship.y;
  const len = Math.sqrt(dx * dx + dy * dy) || 1;
  const targetX = ship.x + (dx / len) * 600, targetY = ship.y + (dy / len) * 600; // ~600px toward open mid-arena
  Praedra.issueOrder(m, [ship.id], { type: 'move', x: targetX, y: targetY });

  let minDist = Infinity;
  for (let i = 0; i < 30 * TICK_RATE && !m.done; i++) {
    m.step();
    const s = m.state.ships.find((x) => x.id === ship.id);
    if (!s) break;
    const d = dist2D(s.x, s.y, targetX, targetY);
    if (d < minDist) minDist = d;
  }
  assert(minDist < 60, `ship never got within 60px of the ordered point (best ${minDist.toFixed(1)}px)`);

  const final = m.state.ships.find((x) => x.id === ship.id);
  assert(final && final.alive, 'ordered ship did not survive the 30s window');
  const stationKeeping = dist2D(final.x, final.y, targetX, targetY) < 60 && final.speed < 16;
  assert((final.order && final.order.type === 'hold') || stationKeeping,
    `order never completed to hold and ship is not station-keeping near the point ` +
    `(order=${JSON.stringify(final.order)}, speed=${final.speed && final.speed.toFixed(1)})`);
});

// ------------------------------------------------- new: BIG asteroids + gravity

// Every generated map carries exactly 1 TITAN (r >= titanRadius[0]) and
// 1..terrain.bigCountMax BIG asteroids (bigRadius[0] <= r < titanRadius[0]),
// regardless of seed or density (SIM_CONTRACT §Terrain). createScenario terrain is
// explicit and exempt. Thresholds read from live config, not hardcoded. Also checks
// outline detail: every asteroid's shape has at least 12 vertices (visual realism).
test('big-asteroids-every-seed', () => {
  const cfg = Praedra.defaultConfig();
  const bigMin = cfg.terrain && cfg.terrain.bigRadius && cfg.terrain.bigRadius[0];
  const titanMin = cfg.terrain && cfg.terrain.titanRadius && cfg.terrain.titanRadius[0];
  assert(typeof bigMin === 'number', 'no terrain.bigRadius in live config — big-asteroid support missing');
  assert(typeof titanMin === 'number', 'no terrain.titanRadius in live config — titan support missing');
  const bigMax = cfg.terrain.bigCountMax;
  for (let seed = 1; seed <= 12; seed++) {
    for (const terrainDensity of [0, 0.5, 1]) {
      const m = Praedra.createMatch({ seed, overrides: { terrainDensity }, teamA: SMALL_A, teamB: SMALL_B });
      const titans = m.state.asteroids.filter((o) => o.alive && o.r >= titanMin);
      const bigs = m.state.asteroids.filter((o) => o.alive && o.r >= bigMin && o.r < titanMin);
      assert(titans.length === 1,
        `seed ${seed} density ${terrainDensity}: ${titans.length} titans (r >= ${titanMin}), want exactly 1`);
      assert(bigs.length >= 1,
        `seed ${seed} density ${terrainDensity}: no BIG asteroid (${bigMin} <= r < ${titanMin}) on the map`);
      assert(bigs.length <= bigMax,
        `seed ${seed} density ${terrainDensity}: ${bigs.length} BIG asteroids exceeds bigCountMax ${bigMax}`);
      for (const o of titans.concat(bigs)) {
        assert(o.x > 0 && o.x < m.config.arena.w && o.y > 0 && o.y < m.config.arena.h,
          `seed ${seed} density ${terrainDensity}: huge asteroid centre (${o.x | 0},${o.y | 0}) outside the arena`);
      }
      // titan may be cut by the boundary but must keep >= 65% of its disc in play
      {
        const t = titans[0], W = m.config.arena.w, H = m.config.arena.h;
        let inside = 0, total = 0;
        for (let i = 0; i < 24; i++) for (let j = 0; j < 24; j++) {
          const px = -1 + (i + 0.5) / 12, py = -1 + (j + 0.5) / 12;
          if (px * px + py * py > 1) continue;
          total++;
          const sx = t.x + px * t.r, sy = t.y + py * t.r;
          if (sx >= 0 && sx <= W && sy >= 0 && sy <= H) inside++;
        }
        const frac = inside / total;
        assert(frac >= 0.63, // sampler tolerance on the sim's own 65% gate
          `seed ${seed} density ${terrainDensity}: titan only ${(frac * 100).toFixed(0)}% inside the playable zone`);
      }
      // outline detail scales with size: grit floor for pebbles, 100+ for the titan
      for (const o of m.state.asteroids) {
        assert(o.shape.length >= 18,
          `seed ${seed} density ${terrainDensity}: asteroid r=${o.r | 0} has only ${o.shape.length} outline vertices`);
      }
      assert(titans[0].shape.length >= 100,
        `seed ${seed} density ${terrainDensity}: titan has only ${titans[0].shape.length} outline vertices`);
    }
  }
});

// Gravity: a massive rock wakes a settled small rock inside its well and pulls it in;
// with gravity.G zeroed the same rock never moves. Displacement thresholds are loose —
// the point is direction and the on/off differential, not the exact constant.
test('gravity-attracts-debris', () => {
  function scenario(G) {
    const overrides = G == null ? {} : { gravity: { G } };
    return Praedra.createScenario({
      seed: 31,
      overrides,
      ships: [], // rocks only: gravity acts on terrain independent of any fleet
      asteroids: [{ x: MID.x, y: MID.y, r: 300 }, { x: MID.x + 600, y: MID.y, r: 30 }],
    });
  }
  const on = scenario(null); // default (gravity enabled)
  const small0 = on.state.asteroids.find((o) => o.r < 100);
  const startX = small0.x;
  stepSeconds(on, 8);
  const small1 = on.state.asteroids.find((o) => o.r < 100);
  assert(small1 && small1.alive, 'small rock vanished from a rocks-only scenario');
  const pulled = startX - small1.x;
  assert(pulled > 20,
    `small rock drifted only ${pulled.toFixed(1)}px toward the massive rock in 8s — gravity not pulling debris`);

  const off = scenario(0);
  const s0 = off.state.asteroids.find((o) => o.r < 100);
  const offStartX = s0.x;
  stepSeconds(off, 8);
  const s1 = off.state.asteroids.find((o) => o.r < 100);
  assert(Math.abs(s1.x - offStartX) < 1,
    `gravity.G = 0 but the small rock still moved ${(offStartX - s1.x).toFixed(1)}px — gravity not disableable`);
});

// Gravity acts on ships — and the autopilot fights it with a hover burn (gravity
// feed-forward). Contract-visible effect: a destroyer holding station in a well
// keeps its position ONLY by sustained throttle (plume up, per detection rules);
// with G zeroed the same ship holds station dark and motionless. Pinned exempt.
test('gravity-pulls-ships', () => {
  function hover(G) {
    const overrides = G == null ? {} : { gravity: { G } };
    const m = Praedra.createScenario({
      seed: 32,
      overrides,
      ships: [{ cls: 'destroyer', team: 'A', x: MID.x + 500, y: MID.y, heading: 0 }],
      asteroids: [{ x: MID.x, y: MID.y, r: 300 }],
    });
    const ship = findShip(m, 'A', 'destroyer');
    Praedra.issueOrder(m, [ship.id], { type: 'hold' }); // station-keep: no role-AI wandering
    stepSeconds(m, 4); // settle into the hover regime
    let thr = 0, n = 0;
    for (let i = 0; i < 3 * TICK_RATE && !m.done; i++) {
      m.step();
      thr += ship.throttle; n++;
    }
    const drift = dist2D(ship.x, ship.y, MID.x + 500, MID.y);
    return { thr: thr / n, drift };
  }
  const on = hover(null);
  assert(on.thr > 0.08,
    `avg throttle ${on.thr.toFixed(3)} while holding station in a well — autopilot is not hover-burning against gravity`);
  assert(on.drift < 150,
    `ship drifted ${on.drift.toFixed(0)}px off its hold point in a well — gravity is winning against the autopilot`);
  const off = hover(0);
  assert(off.thr < 0.05,
    `gravity.G = 0 but avg hold throttle is ${off.thr.toFixed(3)} — ship should hold station dark`);
  assert(off.drift < 20,
    `gravity.G = 0 but the ship drifted ${off.drift.toFixed(0)}px — hold-at-rest should stay at rest`);
});

// EVERYTHING inside a well responds to gravity: the old 650px surface wake-shell is
// gone and the wake threshold sits just above the drag/settle stall boundary. Two
// regressions that FAILED under the old rules:
// (a) a pebble 2500px from a titan-scale rock's CENTER — far outside the old shell
//     (surface + 650 = 1850) but inside the well (reach 2.7r = 3240) — must wake and fall;
// (b) a pebble 700px from an r=300 source (pull ~2.2 px/s^2, below the OLD rockWake 3.0)
//     must now creep inward.
test('gravity-wakes-whole-well', () => {
  const cfg = Praedra.defaultConfig();
  assert(cfg.gravity.rockWake < 2.0,
    `gravity.rockWake is ${cfg.gravity.rockWake} — whole-well wake expects the low threshold`);

  const far = Praedra.createScenario({
    seed: 33,
    ships: [],
    asteroids: [{ x: 4000, y: 2800, r: 1200 }, { x: 6500, y: 2800, r: 40 }],
  });
  const fp0 = far.state.asteroids.find((o) => o.r < 100);
  const fx0 = fp0.x;
  stepSeconds(far, 12);
  const fp1 = far.state.asteroids.find((o) => o.r < 100);
  assert(fp1 && fp1.alive, 'far-field pebble vanished from a rocks-only scenario');
  assert(fx0 - fp1.x > 100,
    `pebble 2500px out (old shell ended at 1850) fell only ${(fx0 - fp1.x).toFixed(1)}px in 12s — ` +
    'far-field rocks are still not affected by gravity');
  const titan = far.state.asteroids.find((o) => o.r >= 1000);
  assert(titan.x === 4000 && titan.y === 2800, 'the titan itself moved — it must stay the fixed anchor');

  const mid = Praedra.createScenario({
    seed: 34,
    ships: [],
    asteroids: [{ x: MID.x, y: MID.y, r: 300 }, { x: MID.x + 700, y: MID.y, r: 30 }],
  });
  const mp0 = mid.state.asteroids.find((o) => o.r < 100);
  const mx0 = mp0.x;
  stepSeconds(mid, 15);
  const mp1 = mid.state.asteroids.find((o) => o.r < 100);
  assert(mx0 - mp1.x > 40,
    `pebble under ~2.2 px/s^2 pull (old rockWake 3.0 ignored it) crept only ${(mx0 - mp1.x).toFixed(1)}px in 15s`);
});

// Accretion rests on the CONTOUR, not the bounding circle: a pebble that falls onto
// a massive rock must settle at the interpolated shape radius toward its resting
// bearing (SIM_CONTRACT §Terrain). Reimplements the contour interpolation from the
// contract and compares against the actual resting distance.
test('accretion-follows-contour', () => {
  const m = Praedra.createScenario({
    seed: 41,
    ships: [],
    asteroids: [{ x: MID.x, y: MID.y, r: 300 }, { x: MID.x + 520, y: MID.y, r: 24 }],
  });
  const big = m.state.asteroids.find((o) => o.r > 100);
  const peb = m.state.asteroids.find((o) => o.r < 100);
  stepSeconds(m, 60); // fall + settle
  assert(big.x === MID.x && big.y === MID.y, 'massive rock moved while a pebble accreted onto it');
  assert(!peb.moving, `pebble still moving after 60s (at ${peb.x | 0},${peb.y | 0}) — never accreted`);
  function contour(o, x, y) {
    const n = o.shape.length;
    let a = Math.atan2(y - o.y, x - o.x) - o.rot;
    a -= Math.floor(a / (2 * Math.PI)) * 2 * Math.PI;
    const f = a * n / (2 * Math.PI);
    const i = Math.min(n - 1, Math.floor(f));
    const t = f - Math.floor(f);
    return o.r * (o.shape[i] * (1 - t) + o.shape[(i + 1) % n] * t);
  }
  const d = dist2D(peb.x, peb.y, big.x, big.y);
  const expected = contour(big, peb.x, peb.y) + contour(peb, big.x, big.y);
  assert(Math.abs(d - expected) < 15,
    `pebble rests at ${d.toFixed(1)}px from centre but the contour contact is ${expected.toFixed(1)}px ` +
    `(bounding circles would be ${(big.r + peb.r).toFixed(0)}) — accretion not following the contour`);
});

// ------------------------------------------------- new: BATTLESHIP heavy railgun

// Live heavy-rail config (read at runtime, never hardcoded — same discipline as the rest).
function heavyRailCfg(cfg) {
  assert(cfg.heavyRail && cfg.ships.battleship,
    'no heavyRail block / ships.battleship in live config — battleship support missing');
  return cfg.heavyRail;
}
// Collect events matching `pred` seen since the last drain; consumes state.events so the capped
// ring buffer isn't re-scanned (the sim itself never reads state.events, so clearing it is a
// harmless renderer-style consume).
function drainEvents(match, pred) {
  const out = [];
  for (const e of match.state.events) if (pred(e)) out.push(e);
  match.state.events.length = 0;
  return out;
}
// Count 'hrail' impact events of a given hit kind ('ship'|'rock'|'miss'; falsy = all).
function drainHrail(match, hitKind) {
  return drainEvents(match, (e) => e.kind === 'hrail' && (!hitKind || e.hit === hitKind)).length;
}

// TEST 1 — the bow turrets delete a DETECTED capital far beyond railgun range (range 3200 >> 700,
// damage 30, real flying slugs, detection-gated). Destroyer at 1400px: past railgun 700, inside
// its own coasting detection 1680, inside heavyRail [220,3200]. Post-rework cadence: cooldown 5
// + loadTime 1.2 -> the two bearing bow turrets land ~10 slugs in 26s (the aft turret is blind
// over the bow); 5 hits kill the 140hp destroyer. The window includes the last slug's ~0.6s
// flight time (in-flight rounds must resolve before the assertion).
test('heavyRail-hits-capital', () => {
  const cfg = Praedra.defaultConfig();
  const HR = heavyRailCfg(cfg);
  const d = 1400;
  assert(d > cfg.railgun.maxRange, `dist ${d} is not beyond railgun maxRange ${cfg.railgun.maxRange}`);
  assert(d > HR.minRange && d < HR.maxRange, `dist ${d} outside heavyRail [${HR.minRange},${HR.maxRange}]`);
  assertDetectable(cfg, 'destroyer', d, 100, 'heavyRail-hits-capital');
  const m = Praedra.createScenario({
    seed: 1,
    ships: [pinned('battleship', 'A', MID.x, MID.y, 0), pinned('destroyer', 'B', MID.x + d, MID.y, Math.PI)],
    asteroids: [],
  });
  const dst = findShip(m, 'B', 'destroyer');
  stepSeconds(m, 26);
  assert(hpOf(m, dst.id) === 0,
    `destroyer survived 26s of heavy-rail at ${d}px (hp ${hpOf(m, dst.id)}) — turrets not deleting capitals past railgun range`);
});

// TEST 2 — HARD class gate: turrets can never touch a bomber/interceptor even when detected and
// in range. Both lights sit inside their coasting detection range but OUTSIDE PD range (so PD is
// not the reason they're spared), and the bomber bombs the BB (scenario is live, not inert).
test('heavyRail-hard-class-gate', () => {
  const cfg = Praedra.defaultConfig();
  heavyRailCfg(cfg);
  const dB = 360, dI = 380; // bomber inside launchRange 380 so it actually bombs the BB
  assertDetectable(cfg, 'bomber', dB, 120, 'heavyRail-hard-class-gate/bomber');
  assertDetectable(cfg, 'interceptor', dI, 50, 'heavyRail-hard-class-gate/interceptor');
  assert(dB > cfg.pd.range && dI > cfg.pd.range,
    `lights must sit outside PD range ${cfg.pd.range} so PD isn't the reason they're untouched`);
  const m = Praedra.createScenario({
    seed: 2,
    ships: [
      pinned('battleship', 'A', MID.x, MID.y, 0),
      pinned('bomber', 'B', MID.x + dB, MID.y, Math.PI),
      pinned('interceptor', 'B', MID.x, MID.y + dI, -Math.PI / 2),
    ],
    asteroids: [],
  });
  const bmb = findShip(m, 'B', 'bomber'), icp = findShip(m, 'B', 'interceptor'), bb = findShip(m, 'A', 'battleship');
  const bmb0 = bmb.hp, icp0 = icp.hp, bb0 = bb.hp;
  let hrailShip = 0;
  for (let i = 0; i < 40 * TICK_RATE && !m.done; i++) { m.step(); hrailShip += drainHrail(m, 'ship'); }
  assert(hpOf(m, bmb.id) === bmb0, `bomber lost hp (${bmb0}->${hpOf(m, bmb.id)}) — turrets must never hit a bomber`);
  assert(hpOf(m, icp.id) === icp0, `interceptor lost hp (${icp0}->${hpOf(m, icp.id)}) — turrets must never hit an interceptor`);
  assert(hrailShip === 0, `${hrailShip} heavy-rail ship-hits against a lights-only enemy set — class gate leaking`);
  assert(hpOf(m, bb.id) < bb0, 'BB took no bomb damage — the scenario was inert, class-gate result is meaningless');
});

// TEST 3 — frigate speed gate. (a) a pinned (speed 0) frigate is very hittable; (b) a non-pinned
// frigate ordered to cross the BB's front at cruise (== speedNoTrack) is untrackable. The mover
// carries an initial cruise velocity so it's at full speed from tick 0 (no standing-start window
// where a briefly-slow frigate would be shot); it must be DETECTED throughout so the SPEED gate,
// not detection, is what spares it.
test('heavyRail-frigate-speed-gate', () => {
  const cfg = Praedra.defaultConfig();
  const HR = heavyRailCfg(cfg);
  const d = 900; // inside frigate coasting vis 1020, inside heavyRail range, outside PD
  assertDetectable(cfg, 'frigate', d, 100, 'heavyRail-frigate-speed-gate');
  assert(d > HR.minRange && d < HR.maxRange, `standoff ${d} outside heavyRail [${HR.minRange},${HR.maxRange}]`);

  const ma = Praedra.createScenario({
    seed: 3,
    ships: [pinned('battleship', 'A', MID.x, MID.y, 0), pinned('frigate', 'B', MID.x + d, MID.y, Math.PI)],
    asteroids: [],
  });
  const fa = findShip(ma, 'B', 'frigate'), fa0 = fa.hp;
  stepSeconds(ma, 30);
  const lostStationary = fa0 - hpOf(ma, fa.id);
  assert(lostStationary >= 0.4 * fa.maxHp,
    `pinned frigate lost only ${lostStationary}/${fa.maxHp} in 30s — turrets not hitting slow frigates`);

  const cruise = cfg.ships.frigate.maxCruiseSpeed;
  const mb = Praedra.createScenario({
    seed: 3,
    ships: [
      pinned('battleship', 'A', MID.x, MID.y, 0),
      { cls: 'frigate', team: 'B', x: MID.x + d, y: MID.y - d, heading: Math.PI / 2, vx: 0, vy: cruise },
    ],
    asteroids: [],
  });
  const fb = mb.state.ships.find((s) => s.team === 'B' && s.cls === 'frigate');
  const fb0 = fb.hp, fbId = fb.id;
  Praedra.issueOrder(mb, [fbId], { type: 'move', x: MID.x + d, y: MID.y + 3000 }); // perpendicular crossing
  let detTicks = 0;
  for (let i = 0; i < 30 * TICK_RATE && !mb.done; i++) {
    mb.step();
    if (mb.state.detA.some((s) => s.id === fbId)) detTicks++;
  }
  const lostMoving = fb0 - hpOf(mb, fbId);
  assert(detTicks > 0, 'moving frigate was never detected — the speed-gate test would pass for the wrong reason');
  assert(lostMoving < 0.25 * lostStationary,
    `moving frigate at cruise ${cruise} lost ${lostMoving.toFixed(1)} vs stationary ${lostStationary} ` +
    `(ratio ${(lostMoving / (lostStationary || 1)).toFixed(2)}, want <0.25) — speed gate not sparing fast crossers`);
});

// TEST 4 — dead zone: a target closer than minRange cannot be fired on at all. Placed inside the
// dead zone but OUTSIDE the BB's PD ship-engagement range (pd.range + target radius), so ANY hp
// loss or 'hrail' event would indict the turrets rather than PD.
test('heavyRail-dead-zone', () => {
  const cfg = Praedra.defaultConfig();
  const HR = heavyRailCfg(cfg);
  const pdShip = cfg.pd.range + cfg.ships.destroyer.radius;
  const d = Math.round((pdShip + HR.minRange) / 2);
  assert(d > pdShip && d < HR.minRange && d > 0,
    `dead-zone dist ${d} not between PD-ship range ${pdShip} and minRange ${HR.minRange}`);
  assertDetectable(cfg, 'destroyer', d, 100, 'heavyRail-dead-zone');
  const m = Praedra.createScenario({
    seed: 4,
    ships: [pinned('battleship', 'A', MID.x, MID.y, 0), pinned('destroyer', 'B', MID.x + d, MID.y, Math.PI)],
    asteroids: [],
  });
  const dst = findShip(m, 'B', 'destroyer');
  const hp0 = dst.hp;
  let hrail = 0;
  for (let i = 0; i < 15 * TICK_RATE && !m.done; i++) { m.step(); hrail += drainHrail(m, null); }
  assert(hrail === 0, `${hrail} heavy-rail shots fired at a target inside the dead zone (${d} < minRange ${HR.minRange})`);
  assert(hpOf(m, dst.id) === hp0, `destroyer at ${d}px (dead zone) lost hp ${hp0}->${hpOf(m, dst.id)}`);
});

// TEST 5a — PROJECTILE FLIGHT (the "it's a real projectile now" regression gate): on the muzzle
// tick the target is UNHARMED and a living slug exists in state.slugs; the slug advances at
// slugSpeed; the first hp drop arrives no sooner than the flight time to the target.
test('heavyRail-projectile-flight', () => {
  const cfg = Praedra.defaultConfig();
  const HR = heavyRailCfg(cfg);
  const d = 1400;
  assertDetectable(cfg, 'destroyer', d, 100, 'heavyRail-projectile-flight');
  const m = Praedra.createScenario({
    seed: 11,
    ships: [pinned('battleship', 'A', MID.x, MID.y, 0), pinned('destroyer', 'B', MID.x + d, MID.y, Math.PI)],
    asteroids: [],
  });
  const dst = findShip(m, 'B', 'destroyer');
  const hp0 = dst.hp;
  let muzzleTick = -1;
  for (let i = 0; i < 10 * TICK_RATE && muzzleTick < 0 && !m.done; i++) {
    m.step();
    if (drainEvents(m, (e) => e.kind === 'hrailMuzzle').length > 0) muzzleTick = m.tick;
  }
  assert(muzzleTick > 0, 'no hrailMuzzle event within 10s — turret never fired');
  assert(hpOf(m, dst.id) === hp0,
    `target lost hp on the muzzle tick (${hp0}->${hpOf(m, dst.id)}) — the shot resolved instantly (still hitscan)`);
  const slug = m.state.slugs.find((s) => s.alive);
  assert(slug, 'no living slug in state.slugs on the muzzle tick — projectile not spawned');
  const sx = slug.x, sy = slug.y;
  m.step();
  const moved = dist2D(slug.x, slug.y, sx, sy);
  const wantStep = HR.slugSpeed / TICK_RATE;
  assert(Math.abs(moved - wantStep) < 2,
    `slug advanced ${moved.toFixed(1)}px in one tick (want ~${wantStep.toFixed(1)}) — not flying at slugSpeed`);
  // muzzle sits ~1.46 radii ahead of the BB centre (mount 0.90r + barrel 0.56r); the slug must
  // cross the remaining gap to the target's near edge before any hp can drop
  const flightTicks = Math.floor(
    (d - 1.46 * cfg.ships.battleship.radius - cfg.ships.destroyer.radius) / HR.slugSpeed * TICK_RATE);
  let hpDropTick = -1;
  for (let i = 0; i < 15 * TICK_RATE && !m.done; i++) {
    if (hpOf(m, dst.id) < hp0) { hpDropTick = m.tick; break; }
    m.step();
  }
  assert(hpDropTick > 0, 'target never lost hp within 15s of the first muzzle — slugs never connect');
  assert(hpDropTick - muzzleTick >= flightTicks - 1,
    `hp dropped ${hpDropTick - muzzleTick} ticks after the muzzle (flight needs >= ${flightTicks - 1}) — damage is not flight-delayed`);
});

// TEST 5b — WWII TURRET ARCS. (a) target dead ASTERN: only the aft turret (index 2) may bear —
// the bow pair is blind astern. A pinned ship still YAWS to face its nearest detected enemy
// (aiPinned), so a nearer NON-TRACKABLE decoy bomber (class-gated out of the turrets, beyond
// bomb launchRange so it stays inert, outside PD ship range) anchors the hull facing forward
// while the turrets engage the trackable destroyer astern. (b) target dead AHEAD: the aft
// turret is blind over the bow — no turret-2 events at all.
test('heavyRail-turret-arcs', () => {
  const cfg = Praedra.defaultConfig();
  const HR = heavyRailCfg(cfg);
  assert(Array.isArray(HR.arcCenters) && typeof HR.arcHalfWidth === 'number',
    'no arcCenters/arcHalfWidth in live config — turret arcs missing');
  const dDecoy = 450, dTarget = 800;
  assertDetectable(cfg, 'bomber', dDecoy, 50, 'heavyRail-turret-arcs/decoy');
  assertDetectable(cfg, 'destroyer', dTarget, 100, 'heavyRail-turret-arcs/target');
  assert(dDecoy > cfg.bomb.launchRange && dDecoy > cfg.pd.range,
    `decoy bomber at ${dDecoy} must be beyond bomb launchRange ${cfg.bomb.launchRange} and PD ${cfg.pd.range} to stay inert`);
  const ma = Praedra.createScenario({
    seed: 12,
    ships: [
      pinned('battleship', 'A', MID.x, MID.y, 0),
      pinned('bomber', 'B', MID.x + dDecoy, MID.y, Math.PI), // dead ahead: the facing anchor
      pinned('destroyer', 'B', MID.x - dTarget, MID.y, 0),   // dead astern: the gun target
    ],
    asteroids: [],
  });
  const dstA = findShip(ma, 'B', 'destroyer');
  const hpA0 = dstA.hp;
  const seenA = new Set();
  for (let i = 0; i < 20 * TICK_RATE && !ma.done; i++) {
    ma.step();
    for (const e of drainEvents(ma, (x) => x.kind === 'hrailMuzzle' || x.kind === 'hrail')) seenA.add(e.turret);
  }
  assert(seenA.has(2), 'aft turret (2) never fired at a target dead astern — it should cover the stern');
  assert(!seenA.has(0) && !seenA.has(1),
    `bow turret events with a target dead astern (turrets seen: ${[...seenA]}) — bow arcs must be blind astern`);
  assert(hpOf(ma, dstA.id) < hpA0, 'astern destroyer took no damage — the aft turret is not connecting');
  const mb = Praedra.createScenario({
    seed: 12,
    ships: [pinned('battleship', 'A', MID.x, MID.y, 0), pinned('destroyer', 'B', MID.x + dTarget, MID.y, Math.PI)],
    asteroids: [],
  });
  const seenB = new Set();
  for (let i = 0; i < 20 * TICK_RATE && !mb.done; i++) {
    mb.step();
    for (const e of drainEvents(mb, (x) => x.kind === 'hrailMuzzle' || x.kind === 'hrail')) seenB.add(e.turret);
  }
  assert(seenB.has(0) && seenB.has(1), `both bow turrets should fire dead ahead (turrets seen: ${[...seenB]})`);
  assert(!seenB.has(2), 'aft turret fired at a target dead ahead — it must be blind over the bow');
});

// TEST 5c — SLUGS PIERCE SHIPS, ARE STOPPED BY ROCKS. (a) two destroyers in line dead ahead:
// one firing line, BOTH take damage (the slug pierces the near hull and rolls the far one too).
// (b) same geometry with a rock BETWEEN the two destroyers covering the line: the near hull
// still takes hits, the rock takes the slugs, the far hull takes ZERO. Torpedo damage is zeroed
// so the destroyers' return fire can't muddy the hp bookkeeping (their torps also can't chip the
// rock: 0 x rockDamageMult); the near destroyer's railgun only reaches the BB, never the rock.
// Rock r=120 (hp 80x(120/60)^2 = 320) outlasts 10s of slugs (4 x 66 = 264): no mid-test split.
test('heavyRail-pierce-ships-not-rocks', () => {
  const cfg = Praedra.defaultConfig();
  heavyRailCfg(cfg);
  const dNear = 700, dFar = 1000;
  assertDetectable(cfg, 'destroyer', dFar, 100, 'heavyRail-pierce/far');
  const opts = (asteroids) => ({
    seed: 13,
    overrides: { torpedo: { damage: 0, aoeDamage: 0 } },
    ships: [
      pinned('battleship', 'A', MID.x, MID.y, 0),
      pinned('destroyer', 'B', MID.x + dNear, MID.y, Math.PI),
      pinned('destroyer', 'B', MID.x + dFar, MID.y, Math.PI),
    ],
    asteroids,
  });
  const ma = Praedra.createScenario(opts([]));
  const [nearA, farA] = ma.state.ships.filter((s) => s.team === 'B');
  const na0 = nearA.hp, fa0 = farA.hp;
  stepSeconds(ma, 10);
  assert(hpOf(ma, nearA.id) < na0, 'near destroyer took no heavy-rail damage in the clear-line variant');
  assert(hpOf(ma, farA.id) < fa0,
    'far destroyer (directly behind the near hull) took no damage — slugs are not piercing ships');
  const mb = Praedra.createScenario(opts([{ x: MID.x + (dNear + dFar) / 2, y: MID.y, r: 120 }]));
  const [nearB, farB] = mb.state.ships.filter((s) => s.team === 'B');
  const rock = mb.state.asteroids[0];
  const nb0 = nearB.hp, fb0 = farB.hp, rHp0 = rock.hp;
  stepSeconds(mb, 10);
  assert(hpOf(mb, nearB.id) < nb0, 'near destroyer (clear of the rock) took no damage in the rock variant');
  assert(rock.alive && rock.hp < rHp0,
    `covering rock took no slug hits (hp ${rHp0}->${rock.hp}) — slugs not slamming into cover`);
  assert(hpOf(mb, farB.id) === fb0,
    `far destroyer behind the covering rock lost hp (${fb0}->${hpOf(mb, farB.id)}) — asteroids must stop slugs dead`);
});

// TEST 5d — RELOAD CADENCE: consecutive shots from the SAME turret are >= cooldown (5s) apart.
// All damage zeroed (zeroDamageOverrides) so the pinned target survives the whole window and
// the muzzle stream is unbroken; only the two bow turrets bear (target dead ahead).
test('heavyRail-reload-cadence', () => {
  const cfg = Praedra.defaultConfig();
  const HR = heavyRailCfg(cfg);
  const m = Praedra.createScenario({
    seed: 14,
    overrides: zeroDamageOverrides(cfg),
    ships: [pinned('battleship', 'A', MID.x, MID.y, 0), pinned('destroyer', 'B', MID.x + 1400, MID.y, Math.PI)],
    asteroids: [],
  });
  const times = new Map(); // turret index -> [muzzle times]
  for (let i = 0; i < 26 * TICK_RATE && !m.done; i++) {
    m.step();
    for (const e of drainEvents(m, (x) => x.kind === 'hrailMuzzle')) {
      if (!times.has(e.turret)) times.set(e.turret, []);
      times.get(e.turret).push(m.state.time);
    }
  }
  let intervals = 0;
  for (const [turret, ts] of times) {
    assert(ts.length >= 4, `turret ${turret} fired only ${ts.length} slugs in 26s (want >=4 at a 5s reload)`);
    for (let i = 1; i < ts.length; i++) {
      intervals++;
      assert(ts[i] - ts[i - 1] >= HR.cooldown - 2.5 / TICK_RATE, // one-tick slack
        `turret ${turret} refired after ${(ts[i] - ts[i - 1]).toFixed(2)}s (reload must be >= ${HR.cooldown}s)`);
    }
  }
  assert(times.size >= 2 && intervals >= 6,
    `only ${times.size} turrets / ${intervals} intervals observed — cadence sample too thin`);
});

// TEST 6 — battleship spawns with its fleet at full hp, and the widened per-fleet spawn pitch
// (2.4 x the largest hull's radius) keeps a radius-78 hull from spawning interpenetrating.
test('battleship-spawns-with-fleet', () => {
  const cfg = Praedra.defaultConfig();
  assert(cfg.ships.battleship, 'no battleship def in live config');
  const m = Praedra.createMatch({
    seed: 5,
    teamA: ['battleship', 'destroyer', 'frigate'],
    teamB: ['destroyer', 'frigate', 'frigate'],
  });
  const bb = m.state.ships.find((s) => s.team === 'A' && s.cls === 'battleship');
  assert(bb, 'no team-A battleship spawned from the custom fleet');
  assert(bb.maxHp === cfg.ships.battleship.hp, `battleship maxHp ${bb.maxHp} != cfg hp ${cfg.ships.battleship.hp}`);
  const sh = m.state.ships;
  for (let i = 0; i < sh.length; i++) for (let j = i + 1; j < sh.length; j++) {
    const dd = dist2D(sh[i].x, sh[i].y, sh[j].x, sh[j].y);
    const sumR = sh[i].def.radius + sh[j].def.radius;
    assert(dd >= sumR, `${sh[i].cls}/${sh[j].cls} spawned overlapping: centres ${dd.toFixed(0)}px < r+r ${sumR}`);
  }
  let nn = Infinity;
  for (const s of sh) if (s !== bb) nn = Math.min(nn, dist2D(bb.x, bb.y, s.x, s.y));
  const wantPitch = 2.4 * cfg.ships.battleship.radius;
  assert(nn >= wantPitch - 1,
    `battleship nearest neighbour ${nn.toFixed(1)}px < widened pitch ${wantPitch.toFixed(1)} — spawn-gap fix not applied`);
});

// A committed bomber wave: laterally spaced on one flank, 3 depth echelons, ~1500px out — the
// design's calibration placement (scratchpad bbsim*/bbfinal). The BB sits at the ARENA CORNER
// (0,0), exactly as the design measured: the walls funnel divers into convergence so they
// self-splash (design finding #1) — that emergent self-attrition is what makes a *pair* fail
// while a squad succeeds, and it is the whole point of the tune. (Other new tests use MID to
// stay clear of walls; this one deliberately uses the corner to reproduce the measured regime.)
function bomberWave(n, seed, origin, dist, lat, dep) {
  let s = (seed >>> 0) || 1;
  const nx = () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
  const a = nx() * Math.PI * 2, ca = Math.cos(a), sa = Math.sin(a), out = [];
  for (let i = 0; i < n; i++) {
    const L = (i - (n - 1) / 2) * lat, D = (i % 3) * dep, r = dist + D;
    const px = origin.x - ca * r - sa * L, py = origin.y - sa * r + ca * L;
    out.push({ cls: 'bomber', team: 'B', x: px, y: py, heading: Math.atan2(origin.y - py, origin.x - px) });
  }
  return out;
}
function bbMinHpFrac(nBombers, seed, capSeconds) {
  const origin = { x: 0, y: 0 };
  const ships = [pinned('battleship', 'A', origin.x, origin.y, Math.PI)]
    .concat(bomberWave(nBombers, seed, origin, 1500, 150, 240));
  const m = Praedra.createScenario({ seed, ships, asteroids: [] });
  const bb = findShip(m, 'A', 'battleship');
  const mh = bb.maxHp;
  let minFrac = 1;
  const cap = Math.round(capSeconds * TICK_RATE);
  for (let i = 0; i < cap && !m.done; i++) {
    m.step();
    if (bb.alive) minFrac = Math.min(minFrac, bb.hp / mh);
    else return 0;
  }
  return minFrac;
}

// TEST 7 — durability regression from the simulated tune (loose bounds; a guard, not exact-match).
// A PAIR of bombers must fail (BB stays healthy); a SQUAD of 6 must be a real threat (drives it
// below half). 2 seeds each, 240s cap. Uses min-hp (robust) not the noisy binary kill.
test('battleship-durability', () => {
  const cfg = Praedra.defaultConfig();
  assert(cfg.ships.battleship.hp > 0 && cfg.ships.battleship.pdSlots >= 0, 'battleship def missing hp/pdSlots');
  for (const seed of [7, 22]) {
    const frac = bbMinHpFrac(2, seed, 240);
    assert(frac > 0.55, `seed ${seed}: 2 bombers drove the BB to ${(frac * 100).toFixed(0)}% min-hp (want >55%) — a pair should fail`);
  }
  for (const seed of [7, 22]) {
    const frac = bbMinHpFrac(6, seed, 240);
    assert(frac < 0.5, `seed ${seed}: 6 bombers only reached ${(frac * 100).toFixed(0)}% min-hp (want <50%) — a squad should threaten it`);
  }
});

// TEST 8 — determinism with a battleship in the custom fleet (turret slew/stagger/fire and the
// spawn-gap fix are all pure/deterministic). Same seed twice -> identical result.
test('determinism-with-battleship', () => {
  const opts = () => ({
    // seed 1: verified the heavy rail actually CONNECTS within the cap (measured 420 hrail dmg
    // by 150s on the projectile rework; seeds 2/3 deal 0 — the turrets never land a hit there)
    seed: 1,
    // 150s cap: determinism is proven by the comparison, not the match length — running the
    // full 360s twice (~29s wall) bought nothing. The assertion below guarantees the turret
    // slew/stagger/fire RNG path is genuinely exercised before the cap.
    overrides: { terrainDensity: 0.5, matchTimerSeconds: 150 },
    teamA: ['battleship', 'destroyer', 'frigate', 'frigate'],
    teamB: ['destroyer', 'destroyer', 'frigate', 'frigate'],
  });
  const r1 = Praedra.runMatch(opts());
  const r2 = Praedra.runMatch(opts());
  assert((r1.stats.dmgTo.B.hrail || 0) > 0,
    'the heavy rail never dealt damage in the determinism window — the seed/cap no longer ' +
    'exercises the turret RNG path; pick a seed where the turrets connect');
  assert(isDeepStrictEqual(r1, r2),
    'same seed + config + a battleship fleet produced different results — turret slew/stagger/fire not deterministic');
  const m = Praedra.createMatch(opts());
  assert(m.state.ships.some((s) => s.team === 'A' && s.cls === 'battleship'),
    'determinism fleet spawned no battleship — guards a silent spawn regression');
});

// TEST — BATTLESHIP FLEET REAL-MATCH EFFECTIVENESS (smoke guard, NOT a balance lock). The heavy-rail
// turrets are lethal in isolation, but a raw battleship fleet in cluttered createMatch armadas used to
// deal ~0 enemy damage: the slow radius-78 / turnMax-0.14 hull fell to the rear past terrain LOS and,
// above all, was dragged into the titan/BIG gravity wells and ground to death (by:'rock') before it
// ever fired — measured median BB damage 0 vs capital fleets. The aiBattleship fixes (engagement-aware
// standoff + noLos cover-clear + the gravity-well SKIRT, all config-driven) restored it: a 42-pt
// two-battleship fleet now puts real heavy-rail damage on a RAILGUN fleet. Seeds 1 and 5 are picked
// because they EXERCISE the fix — on the pre-fix sim the BB was entombed and dealt 0 + 0 on both.
// The heavy-rail PROJECTILE REWORK is a deliberate nerf (cooldown 2->5s, loadTime 1.2s, slew
// 0.9->0.35, WWII arcs): the hitscan build measured 960 + 270 = 1230 on these seeds; the rework
// measures 870 + 90 = 960. Threshold 400 is the retuned wide-margin smoke bound (catches a
// regression back to the entombed / rock-shooting / never-fires behaviour without pinning the
// balance, which still needs the deep flip retune). BB enemy damage == dmgTo.B.hrail (only team A
// carries battleships).
test('battleship-fleet-effectiveness', () => {
  const bbFleet = ['battleship', 'battleship', 'frigate', 'frigate', 'frigate', 'frigate']; // 30 + 12 = 42 pts
  let total = 0;
  for (const seed of [1, 5]) {
    const r = Praedra.runMatch({ seed, overrides: { terrainDensity: 0.3 }, teamA: bbFleet, teamB: 'RAILGUN' });
    total += (r.stats && r.stats.dmgTo && r.stats.dmgTo.B && r.stats.dmgTo.B.hrail) || 0;
  }
  assert(total > 400,
    `BB fleet dealt only ${Math.round(total)} heavy-rail dmg vs RAILGUN across seeds 1+5 (want >400; ` +
    'measured 960 on the projectile rework — 870+90; ~1230 pre-nerf; 0 when entombed/rear-parked) — effectiveness regressed');
});

// ------------------------------------------ new: capital pathing / fire-discipline
//
// Two composable capital behaviours added in the pathing/fire-discipline upgrade:
//   (1) field-density DETOUR in routeAround (AUTO/hunt path): a capital rounds a compact,
//       clutter-heavy field instead of grinding through it.
//   (2) lane-clearing FIRE in clearTransitLane (auto-hunt + ordered move/attackmove/hold): a
//       transiting capital blasts a blocking rock out of its corridor, combat-priority-gated.
// Layouts are built from a LOCAL LCG so they don't touch the sim RNG and are identical every run
// (createScenario terrain is fully explicit — no titan/bigs injected). Thresholds are grounded in
// measurements of THIS build vs git HEAD (the pre-feature sim): every criterion below fails on HEAD
// (baseline never fires to clear / never rounds the field) and passes here. See the design doc and
// scratchpad measure scripts. All coords in the 8000x5600 arena.
function lcgLayout(seed) {
  let s = (seed >>> 0) || 1;
  return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
}
// A dense single rock-column spanning the arena at x=3560 with a rock dead-ahead at y=2800; the
// intact gaps are too tight for the wide destroyer hull to thread, so it must shatter blockers to
// punch through. r 88..108 -> children r46..56 (mostly pushable pebbles the hull shoves aside).
// `step` sets the vertical pitch (destroyer 308: it can thread the cleared gap; frigate 296: the
// nimble hull would thread 308 without ever firing, so the frigate variant uses a tighter wall
// that forces the torpedo — the two hulls avoid at very different widths, documented deviation
// from the design's single shared wall).
function rockColumn(step) {
  const nx = lcgLayout(4242);
  const rocks = [];
  for (let y = 1500; y <= 4100; y += step) rocks.push({ x: 3560, y, r: 88 + nx() * 20 });
  for (let i = 0; i < 22; i++) rocks.push({ x: 3300 + nx() * 520, y: 2000 + nx() * 1600, r: 28 + nx() * 18 });
  return rocks;
}
// A compact well: a big impassable core (r320, excluded from the clutter metric as > rockClearMaxRadius
// and routed AROUND, not shot) ringed by a dense band of medium rocks (the counted clutter signal) and
// a pebble shell. The straight lane through it clutters hard (~650); a lateral flank clears it.
function wellField() {
  const nx = lcgLayout(555);
  const rocks = [{ x: 3600, y: 2800, r: 320 }];
  for (let i = 0; i < 22; i++) {
    const a = (i / 22) * Math.PI * 2 + nx() * 0.15, rr = 430 + nx() * 130;
    rocks.push({ x: 3600 + Math.cos(a) * rr, y: 2800 + Math.sin(a) * rr, r: 78 + nx() * 55 });
  }
  for (let i = 0; i < 40; i++) {
    const a = nx() * Math.PI * 2, rr = 360 + nx() * 520;
    rocks.push({ x: 3600 + Math.cos(a) * rr, y: 2800 + Math.sin(a) * rr, r: 28 + nx() * 22 });
  }
  return rocks;
}

// Drive one ship on an ordered attackmove straight across a rock column; track how far it punches
// and whether it arrives. Returns the observed extrema plus splits and self rock-damage.
function transitAcross(cls, rocks, seconds) {
  const m = Praedra.createScenario({
    seed: 77,
    ships: [{ cls, team: 'A', x: 2200, y: 2800, heading: 0 }],
    asteroids: rocks,
  });
  const ship = findShip(m, 'A', cls);
  Praedra.issueOrder(m, [ship.id], { type: 'attackmove', x: 5000, y: 2800 });
  let minDgoal = Infinity, maxX = ship.x;
  const cap = Math.round(seconds * TICK_RATE);
  for (let i = 0; i < cap && !m.done; i++) {
    m.step();
    const s = m.state.ships.find((x) => x.id === ship.id);
    if (!s || !s.alive) break;
    const dg = dist2D(s.x, s.y, 5000, 2800);
    if (dg < minDgoal) minDgoal = dg;
    if (s.x > maxX) maxX = s.x;
    if (dg < 60) break; // arrived — stop early
  }
  return { splits: m.state.stats.splits, minDgoal, maxX, rockDmg: m.state.stats.dmgTo.A.rock || 0 };
}

// TEST — LANE-CLEAR ACROSS A WALL (headline; ordered attackmove). A destroyer ordered to cross a
// wall it cannot thread must railgun the blockers out of its lane and punch through. Measured HEAD
// baseline (pre-feature): splits 0, maxX ~3891, never arrives. This build: splits 2, punches past,
// arrives ~140s. rockDmg is an absolute debris-safety bound (the pad keeps self-damage low; measured
// ~13.8 here, ~13.3 on HEAD — both low, the point is it does not blow up when firing amid the wall).
test('lane-clear-wall', () => {
  const r = transitAcross('destroyer', rockColumn(308), 158);
  assert(r.splits >= 2, `destroyer split only ${r.splits} rocks crossing the wall (want >=2; HEAD fires 0)`);
  assert(r.maxX > 4300, `destroyer only reached x=${r.maxX.toFixed(0)} (want >4300, past the wall; HEAD stuck ~3891)`);
  assert(r.minDgoal < 80, `destroyer never arrived (best ${r.minDgoal.toFixed(0)}px from goal; want <80)`);
  assert(r.rockDmg < 16, `destroyer took ${r.rockDmg.toFixed(1)} rock damage clearing the lane (debris-safety bound 16)`);
});

// TEST — LANE-CLEAR ACROSS A WALL, FRIGATE (torpedo variant). Same builder, tighter pitch (the nimble
// frigate threads the destroyer's wall without firing). It must torpedo at least one blocker and punch
// past. HEAD baseline: splits 0, stuck ~3529. This build: splits >=1, arrives ~71s.
test('lane-clear-wall-frigate', () => {
  const r = transitAcross('frigate', rockColumn(296), 130);
  assert(r.splits >= 1, `frigate split only ${r.splits} rocks (want >=1 torpedoed blocker; HEAD fires 0)`);
  assert(r.maxX > 4300, `frigate only reached x=${r.maxX.toFixed(0)} (want >4300, past the wall; HEAD stuck ~3529)`);
  assert(r.rockDmg < 15, `frigate took ${r.rockDmg.toFixed(1)} rock damage (guard; torp shatters far from the hull)`);
});

// TEST — LANE-CLEAR ACROSS A WALL, BATTLESHIP. The turrets demolish blockers in the corridor (many
// splits). Cruise 28 is slow, so a generous but bounded budget; only splits are asserted (arrival is
// not the point — the turret lane-clearing is). HEAD baseline: splits 0.
test('battleship-lane-clear', () => {
  const r = transitAcross('battleship', rockColumn(308), 70);
  assert(r.splits >= 2,
    `battleship turrets split only ${r.splits} rocks crossing the wall (want >=2; HEAD fires 0)`);
});

// TEST — FIELD DETOUR AROUND A WELL (auto hunt; robust damage reduction). An un-ordered destroyer
// hunts toward a pinned foe on the far side of a compact, clutter-heavy field. routeAround must round
// the field rather than grind through it: it reaches the far side having taken almost no rock damage.
// The pinned foe stays hidden behind the core until the hunter has rounded it (LOS blocked), so the
// detour drives the whole approach. HEAD baseline: trapped short (minDgoal ~2204, maxX ~3009, rockDmg
// ~16.6). This build: rounds it (maxX ~4849), reaches the far vicinity (minDgoal ~546), rockDmg ~0.
test('field-detour', () => {
  const m = Praedra.createScenario({
    seed: 88,
    ships: [
      { cls: 'destroyer', team: 'A', x: 2200, y: 2800, heading: 0 },
      { cls: 'destroyer', team: 'B', x: 5200, y: 2800, heading: Math.PI, pinned: true },
    ],
    asteroids: wellField(),
  });
  const a = findShip(m, 'A', 'destroyer');
  let minDgoal = Infinity, maxX = a.x;
  for (let i = 0; i < 140 * TICK_RATE && !m.done; i++) {
    m.step();
    const s = m.state.ships.find((x) => x.id === a.id);
    if (!s || !s.alive) break; // may perish in the far-side firefight AFTER rounding; extrema already recorded
    const dg = dist2D(s.x, s.y, 5200, 2800);
    if (dg < minDgoal) minDgoal = dg;
    if (s.x > maxX) maxX = s.x;
  }
  const rockDmg = m.state.stats.dmgTo.A.rock || 0;
  assert(maxX > 4600, `hunter only reached x=${maxX.toFixed(0)} (want >4600, rounded the well; HEAD trapped ~3009)`);
  assert(minDgoal < 900, `hunter never reached the far vicinity (best ${minDgoal.toFixed(0)}px; want <900; HEAD ~2204)`);
  assert(rockDmg < 10, `hunter took ${rockDmg.toFixed(1)} rock damage (want <10; the detour avoids the field; HEAD ~16.6)`);
});

// TEST — COMBAT-PRIORITY GATE (mutation guard). clearTransitLane must NOT spend the gun on a rock while
// a detected enemy sits inside weapon range. A battleship transiting toward a rock blocker, with a
// pinned bomber detected within heavy-rail range (but beyond bomb range, so inert, and class-gated from
// the turrets so the turrets themselves never engage it): the rock survives ONLY because the gate
// suppresses lane-clearing. Remove the gate and the turrets would demolish the rock — so the control
// (no enemy) MUST clear it. Distinguishes the gate, not just presence/absence of the feature.
test('lane-clear-combat-priority', () => {
  const cfg = Praedra.defaultConfig();
  const bombVis = cfg.ships.bomber.signature * cfg.detection.thrustMultMin; // 900*0.6 = 540
  const dEnemy = 450; // detected (<540) but beyond bomb launchRange (380) so the bomber is inert
  assert(dEnemy < bombVis && dEnemy > cfg.bomb.launchRange && dEnemy < cfg.heavyRail.maxRange,
    `combat-priority enemy distance ${dEnemy} not in the intended band ` +
    `(bomber vis ${bombVis}, bomb range ${cfg.bomb.launchRange}, heavyRail range ${cfg.heavyRail.maxRange})`);
  function run(enemy) { // enemy: null | 'bomber' | 'destroyer'
    const ships = [{ cls: 'battleship', team: 'A', x: 2200, y: 2800, heading: 0 }];
    // the bomber is class-gated (turrets can NEVER engage it); the destroyer sits abeam with a
    // clear lane (trackable: a legal turret target -> combat owns the gun)
    if (enemy === 'bomber') ships.push(pinned('bomber', 'B', 2200, 2800 + dEnemy, -Math.PI / 2));
    if (enemy === 'destroyer') ships.push(pinned('destroyer', 'B', 2200, 2800 + 1000, -Math.PI / 2));
    const m = Praedra.createScenario({ seed: 5, ships, asteroids: [{ x: 3000, y: 2800, r: 100 }] });
    const bb = findShip(m, 'A', 'battleship');
    Praedra.issueOrder(m, [bb.id], { type: 'move', x: 6000, y: 2800 });
    let detected = false;
    for (let i = 0; i < 22 * TICK_RATE && !m.done; i++) { m.step(); if (m.state.detA.length) detected = true; }
    return { splits: m.state.stats.splits, detected };
  }
  const withTrackable = run('destroyer'), withLight = run('bomber'), ctrl = run(null);
  assert(withTrackable.detected && withLight.detected,
    'combat-priority enemy was never detected — the gate test would pass for the wrong reason');
  assert(ctrl.splits >= 1, `control (no enemy) failed to demolish the blocking rock (splits ${ctrl.splits}) — lane-clearing not firing`);
  // a TRACKABLE target in range owns the turrets: no rock fire
  assert(withTrackable.splits === 0,
    `a detected trackable capital in weapon range did NOT suppress rock fire (splits ${withTrackable.splits}) — combat-priority gate leaking`);
  // an UNTRACKABLE light must NOT suppress lane-clearing: the class gate means the turrets can
  // never engage it, and an ordered battleship stranded behind a rock by a loitering bomber it
  // cannot shoot is exactly the failure this guards against
  assert(withLight.splits >= 1,
    `a class-gated bomber the turrets can never hit suppressed lane-clearing (splits ${withLight.splits}) — ` +
    'the battleship combat-priority gate must only respect trackable targets');
});

// ------------------------------------------------ v2 doctrine + battleship gates

test('doctrine-mixed-determinism', () => {
  // v2-vs-v1 must be exactly as deterministic as everything else
  const opts = {
    seed: 11, teamA: 'BALANCED', teamB: 'BALANCED',
    overrides: { doctrine: { A: 'v2', B: 'v1' }, terrainDensity: 0.5, matchTimerSeconds: 90 },
  };
  const a = Praedra.runMatch(opts);
  const b = Praedra.runMatch(opts);
  assert(isDeepStrictEqual(a, b), 'v2-vs-v1 match not deterministic from seed');
});

test('bb-knife-fight-window', () => {
  // The user-facing minimum-engagement-distance mechanic: small ships that manage to
  // approach must have a REAL chance at killing the battleship. Three v2 frigates
  // (9 fleet points) vs one lone unescorted BB (15 points) on an open map: the pack
  // dives the dead zone (heavyRail.minRange 420 vs PD ship-reach ~156) and wins.
  // v1 frigates in the same spot lob from standoff and lose 23/24 hulls (measured).
  const m = Praedra.createScenario({
    seed: 1,
    overrides: { matchTimerSeconds: 150, doctrine: { A: 'v2', B: 'v2' }, gravity: { G: 0 } },
    ships: [
      { cls: 'frigate', team: 'A', x: 1200, y: 2000, heading: 0 },
      { cls: 'frigate', team: 'A', x: 1200, y: 2400, heading: 0 },
      { cls: 'frigate', team: 'A', x: 1200, y: 2800, heading: 0 },
      { cls: 'battleship', team: 'B', x: 4200, y: 2400, heading: Math.PI },
    ],
    asteroids: [],
  });
  while (!m.done) m.step();
  const bb = m.state.ships.find((s) => s.cls === 'battleship');
  const frigsAlive = m.state.ships.filter((s) => s.cls === 'frigate' && s.alive).length;
  assert(!bb.alive, `lone battleship survived the wolfpack (hp ${bb.hp.toFixed(0)}/${bb.maxHp}) — dead-zone window closed`);
  assert(frigsAlive >= 2, `wolfpack lost ${3 - frigsAlive}/3 boats — the dead zone is not protecting the divers`);
});

test('bb-gap-gate-never-threads', () => {
  // A v2 battleship must NEVER thread an opening narrower than ai2.bbMinGap: two rocks
  // with a 160px surface gap sit dead on its hunt path; the hull (r 78) would physically
  // fit but doctrine forbids it. It must round the pinch (open flanks here) instead.
  const m = Praedra.createScenario({
    seed: 3,
    overrides: { matchTimerSeconds: 150, doctrine: { A: 'v2', B: 'v2' }, gravity: { G: 0 } },
    ships: [
      { cls: 'battleship', team: 'A', x: 1400, y: 2430, heading: 0 },
      pinned('destroyer', 'B', 4600, 2430, Math.PI),
    ],
    asteroids: [{ x: 2600, y: 2200, r: 150 }, { x: 2600, y: 2660, r: 150 }],
  });
  const bb = findShip(m, 'A', 'battleship');
  let minThroat = Infinity, engaged = false;
  while (!m.done) {
    m.step();
    if (!bb.alive) break;
    const gd = Math.hypot(bb.x - 2600, bb.y - 2430);
    if (gd < minThroat) minThroat = gd;
    if (m.state.stats.railShots > 0 || m.state.slugs.length > 0 ||
        m.state.events.some((e) => e.kind === 'hrailMuzzle')) engaged = true;
  }
  assert(minThroat > 150, `battleship entered the sub-minimum gap (min throat distance ${minThroat.toFixed(0)}px) — gap gate failed`);
  assert(engaged, 'battleship never opened fire — it must round the pinch AND still prosecute the target');
});

test('bb-wall-demolition', () => {
  // When the pinch is part of a WALL (no clean flank), the battleship must MAKE room:
  // hold at debris-safe standoff and demolish — never thread the tight gap, never stall
  // silently. "A WWII battleship once removed an entire hill."
  const wall = [];
  for (let y = 200; y <= 4800; y += 460) {
    if (Math.abs(y - 2200) < 10 || Math.abs(y - 2660) < 10) continue;
    wall.push({ x: 2600, y, r: 170 });
  }
  wall.push({ x: 2600, y: 2200, r: 150 }, { x: 2600, y: 2660, r: 150 }); // 160px gap in the wall
  const m = Praedra.createScenario({
    seed: 3,
    overrides: { matchTimerSeconds: 90, doctrine: { A: 'v2', B: 'v2' }, gravity: { G: 0 } },
    ships: [
      { cls: 'battleship', team: 'A', x: 1400, y: 2430, heading: 0 },
      pinned('destroyer', 'B', 4600, 2430, Math.PI),
    ],
    asteroids: wall,
  });
  const bb = findShip(m, 'A', 'battleship');
  let slugsFired = 0, minThroat = Infinity;
  while (!m.done) {
    const tickBefore = m.state.tick;
    m.step();
    slugsFired += m.state.events.filter((e) => e.kind === 'hrailMuzzle' && e.t === tickBefore + 1).length;
    if (!bb.alive) break;
    const gd = Math.hypot(bb.x - 2600, bb.y - 2430);
    if (gd < minThroat) minThroat = gd;
  }
  assert(minThroat > 150, `battleship threaded the wall gap (min throat distance ${minThroat.toFixed(0)}px)`);
  assert(slugsFired >= 8, `battleship fired only ${slugsFired} slugs at the wall in 90s — demolition transit not engaging`);
  assert(m.state.stats.splits >= 1, `no rock was split (splits ${m.state.stats.splits}) — the wall is not actually being demolished`);
});

runAll();
