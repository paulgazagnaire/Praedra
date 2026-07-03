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
// keepRailgun preserves railgun damage wherever it lives (railgun.damage,
// railgunDamage, weapons.railgun.slugDamage, ...).
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

// ---------------------------------------------------------------------- tests

test('determinism', () => {
  const opts = () => ({ seed: 7, overrides: { terrainDensity: 0.5 }, teamA: 'RAILGUN', teamB: 'SWARM' });
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
      const r = Praedra.runMatch({ seed, overrides: { terrainDensity }, teamA: 'RAILGUN', teamB: 'SWARM' });
      assert(r && Number.isFinite(r.ticks), `${tag}: no numeric ticks in result`);
      assert(r.ticks <= cap + 2, `${tag}: ticks ${r.ticks} exceeds tick cap ${cap} (+2 slack)`);
      assert(['A', 'B', 'draw'].includes(r.winner), `${tag}: winner is ${JSON.stringify(r.winner)}`);
    }
  }
});

// Destroyer at origin aiming down +x at a frigate 400 away; rock (r=80) at
// (200,0) sits dead-centre on the firing line. destructibleAsteroids off so
// the rock cannot be shot open mid-test. With LOS blocked NOTHING may hit the
// frigate — torpedoes respect LOS too (PRD §4, load-bearing).
function losScenario(withAsteroid) {
  return Praedra.createScenario({
    seed: 1,
    overrides: { destructibleAsteroids: false },
    ships: [pinned('destroyer', 'A', 0, 0, 0), pinned('frigate', 'B', 400, 0, 0)],
    asteroids: withAsteroid ? [{ x: 200, y: 0, r: 80 }] : [],
  });
}

test('los-blocked', () => {
  const m = losScenario(true);
  const frig = findShip(m, 'B', 'frigate');
  const hp0 = frig.hp;
  stepSeconds(m, 15);
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

// All non-railgun damage zeroed; pin a target 400 away for 120 sim-seconds
// and measure hp lost. Evasion must gate the railgun: the interceptor (0.80)
// loses far less than a frigate (0.15) in the same seat.
function railgunLossAgainst(cls) {
  const m = Praedra.createScenario({
    seed: 3,
    overrides: zeroDamageOverrides(Praedra.defaultConfig(), true),
    ships: [pinned('destroyer', 'A', 0, 0, 0), pinned(cls, 'B', 400, 0, 0)],
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

test('wallclock', () => {
  const t0 = Date.now(); // harness may use Date; only the sim may not
  const r = Praedra.runMatch({ seed: 1, overrides: { terrainDensity: 0.5 }, teamA: 'RAILGUN', teamB: 'SWARM' });
  const ms = Date.now() - t0;
  assert(r && r.winner !== undefined, 'runMatch returned no result object');
  assert(ms < 2000, `full match took ${ms} ms wall-clock (budget 2000 ms)`);
});

runAll();
