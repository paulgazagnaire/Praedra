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
  assert(ms < 20000, `full match took ${ms} ms wall-clock (hard budget 20 s at the 360 s tick cap)`);
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

runAll();
