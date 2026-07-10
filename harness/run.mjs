#!/usr/bin/env node
// Praedra headless batch runner. See docs/SIM_CONTRACT.md (binding) and PRD §8/§8B.
// Node 22, ESM, zero npm dependencies.
//
// Usage:
//   node harness/run.mjs [--file index.html] [--densities 0.1,0.4,0.7]
//     [--seeds 30] [--seedStart 1] [--teamA RAILGUN] [--teamB SWARM]
//     [--destructible on|off] [--set path.to.key=value ...]
//     [--json out.json] [--workers N]

import { readFileSync, writeFileSync } from 'node:fs';
import { fork } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { cpus } from 'node:os';
import { loadSim as sharedLoadSimRM } from './simloader.mjs';

const SELF = fileURLToPath(import.meta.url);
const REPO_ROOT = resolve(dirname(SELF), '..');

function fail(msg) {
  console.error(`run.mjs: ${msg}`);
  process.exit(1);
}

// ---------------------------------------------------------------- helpers

function parseValue(raw) {
  if (raw === 'true') return true;
  if (raw === 'false') return false;
  if (raw !== '' && !Number.isNaN(Number(raw))) return Number(raw);
  return raw;
}

function setPath(obj, dotted, value) {
  const keys = dotted.split('.');
  let node = obj;
  for (const k of keys.slice(0, -1)) {
    if (typeof node[k] !== 'object' || node[k] === null) node[k] = {};
    node = node[k];
  }
  node[keys[keys.length - 1]] = value;
}

function deepMerge(base, over) {
  for (const [k, v] of Object.entries(over)) {
    if (v && typeof v === 'object' && !Array.isArray(v)) {
      if (typeof base[k] !== 'object' || base[k] === null || Array.isArray(base[k])) base[k] = {};
      deepMerge(base[k], v);
    } else {
      base[k] = v;
    }
  }
  return base;
}

function parseArgs(argv) {
  const opts = {
    file: resolve(REPO_ROOT, 'index.html'),
    densities: [0.1, 0.25, 0.4, 0.55, 0.7, 0.85],
    seeds: 30,
    seedStart: 1,
    teamA: 'RAILGUN',
    teamB: 'SWARM',
    destructible: false,
    sets: {},
    json: null,
    workers: Math.min(4, cpus().length),
  };
  const next = (i, flag) => {
    if (i + 1 >= argv.length) fail(`missing value for ${flag}`);
    return argv[i + 1];
  };
  const parseTeam = (v) => (v.includes(',') ? v.split(',').map((s) => s.trim()).filter(Boolean) : v);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    switch (a) {
      case '--file': opts.file = resolve(process.cwd(), next(i, a)); i++; break;
      case '--densities':
        opts.densities = next(i, a).split(',').map((s) => Number(s.trim()));
        if (opts.densities.length === 0 || opts.densities.some((d) => !Number.isFinite(d)))
          fail(`--densities must be a comma list of numbers, got "${argv[i + 1]}"`);
        i++; break;
      case '--seeds':
        opts.seeds = Number(next(i, a));
        if (!Number.isInteger(opts.seeds) || opts.seeds < 1) fail('--seeds must be a positive integer');
        i++; break;
      case '--seedStart':
        opts.seedStart = Number(next(i, a));
        if (!Number.isInteger(opts.seedStart)) fail('--seedStart must be an integer');
        i++; break;
      case '--teamA': opts.teamA = parseTeam(next(i, a)); i++; break;
      case '--teamB': opts.teamB = parseTeam(next(i, a)); i++; break;
      case '--destructible': {
        const v = next(i, a);
        if (v !== 'on' && v !== 'off') fail('--destructible must be "on" or "off"');
        opts.destructible = v === 'on'; i++; break;
      }
      case '--set': {
        const kv = next(i, a);
        const eq = kv.indexOf('=');
        if (eq <= 0) fail(`--set expects path.to.key=value, got "${kv}"`);
        setPath(opts.sets, kv.slice(0, eq), parseValue(kv.slice(eq + 1)));
        i++; break;
      }
      case '--json': opts.json = resolve(process.cwd(), next(i, a)); i++; break;
      case '--workers':
        opts.workers = Number(next(i, a));
        if (!Number.isInteger(opts.workers) || opts.workers < 1) fail('--workers must be a positive integer');
        i++; break;
      default: fail(`unknown flag "${a}"`);
    }
  }
  opts.densities = [...opts.densities].sort((x, y) => x - y);
  return opts;
}

// ---------------------------------------------------------------- sim loading

// Base overrides (everything except per-match terrainDensity).
function baseOverrides(opts) {
  return deepMerge({ destructibleAsteroids: opts.destructible }, opts.sets);
}

function runOne(Praedra, cfg, task) {
  const overrides = deepMerge(deepMerge({}, cfg.baseOverrides), { terrainDensity: task.density });
  try {
    const result = Praedra.runMatch({ seed: task.seed, overrides, teamA: cfg.teamA, teamB: cfg.teamB });
    return { density: task.density, seed: task.seed, result };
  } catch (err) {
    return { density: task.density, seed: task.seed, error: String((err && err.message) || err) };
  }
}

// ---------------------------------------------------------------- worker child

function workerMain() {
  process.on('message', (msg) => {
    if (!msg || msg.type !== 'init') return;
    try {
      const Praedra = sharedLoadSimRM(msg.cfg.file, { requireFn: 'runMatch' });
      for (const task of msg.tasks) process.send({ type: 'result', rec: runOne(Praedra, msg.cfg, task) });
      process.send({ type: 'done' }, () => process.disconnect());
    } catch (err) {
      process.send({ type: 'fatal', error: String((err && err.stack) || err) }, () => process.disconnect());
    }
  });
}

// ---------------------------------------------------------------- collection

function makeCollector(opts, onAllDone) {
  const t0 = Date.now();
  const byKey = new Map(); // "densityIndex:seed" -> rec (dedupe + scheduling-independent)
  const perDensity = opts.densities.map((d) => ({ density: d, remaining: opts.seeds, printed: false }));
  const densityIndex = new Map(opts.densities.map((d, i) => [d, i]));
  let total = 0;
  const expect = opts.densities.length * opts.seeds;

  function flushLines() {
    for (const pd of perDensity) {
      if (pd.printed) continue;
      if (pd.remaining > 0) break; // keep output in sweep order
      pd.printed = true;
      console.log(`density ${pd.density.toFixed(3)} complete: ${opts.seeds} matches  (t+${((Date.now() - t0) / 1000).toFixed(2)}s wall)`);
    }
  }

  return {
    add(rec) {
      const di = densityIndex.get(rec.density);
      const key = `${di}:${rec.seed}`;
      if (byKey.has(key)) return;
      byKey.set(key, rec);
      perDensity[di].remaining--;
      total++;
      flushLines();
      if (total === expect) onAllDone(byKey);
    },
  };
}

function aggregate(opts, byKey) {
  return opts.densities.map((density, di) => {
    const recs = [];
    for (let s = 0; s < opts.seeds; s++) recs.push(byKey.get(`${di}:${opts.seedStart + s}`));
    const errors = recs.filter((r) => r.error !== undefined).length;
    const ok = recs.filter((r) => r.result);
    const n = recs.length;
    const winsA = ok.filter((r) => r.result.winner === 'A').length;
    const winsB = ok.filter((r) => r.result.winner === 'B').length;
    const draws = ok.length - winsA - winsB;
    const mean = (f) => (ok.length ? ok.reduce((acc, r) => acc + (Number(f(r.result)) || 0), 0) / ok.length : 0);
    const pct = (x) => (ok.length ? (100 * x) / ok.length : 0);
    return {
      density, n, errors,
      winsA, winsB, draws,
      aWinPct: pct(winsA), bWinPct: pct(winsB), drawPct: pct(draws),
      nonResolution: ok.filter((r) => r.result.nonResolution).length,
      avgSeconds: mean((r) => r.seconds),
      avgFleetValueA: mean((r) => r.fleetValueA),
      avgFleetValueB: mean((r) => r.fleetValueB),
    };
  });
}

function majority(agg) {
  if (agg.winsA > agg.winsB) return 'A';
  if (agg.winsB > agg.winsA) return 'B';
  return 'tie';
}

function report(opts, byKey) {
  const aggs = aggregate(opts, byKey);
  const header = ['density', 'N', 'A-win%', 'B-win%', 'draw%', 'err', 'nonres', 'avg s', 'avgFV-A', 'avgFV-B'];
  const widths = [7, 5, 7, 7, 6, 4, 7, 8, 8, 8];
  const line = (cols) => cols.map((c, i) => String(c).padStart(widths[i])).join('  ');
  console.log('');
  console.log(line(header));
  console.log(line(widths.map((w) => '-'.repeat(w))));
  for (const a of aggs) {
    console.log(line([
      a.density.toFixed(3), a.n, a.aWinPct.toFixed(1), a.bWinPct.toFixed(1), a.drawPct.toFixed(1),
      a.errors, a.nonResolution, a.avgSeconds.toFixed(1), a.avgFleetValueA.toFixed(1), a.avgFleetValueB.toFixed(1),
    ]));
  }
  console.log('');

  const lo = aggs[0], hi = aggs[aggs.length - 1];
  const flip = lo.aWinPct - hi.aWinPct;
  console.log(`FLIP METRIC: A-win%(lowest density ${lo.density.toFixed(3)}) − A-win%(highest density ${hi.density.toFixed(3)}) = ${flip.toFixed(1)} pts`);

  let crossover = null;
  for (let i = 1; i < aggs.length; i++) {
    const prev = majority(aggs[i - 1]), cur = majority(aggs[i]);
    if (prev !== cur) { crossover = { fromDensity: aggs[i - 1].density, toDensity: aggs[i].density, from: prev, to: cur }; break; }
  }
  console.log(crossover
    ? `CROSSOVER: between density ${crossover.fromDensity.toFixed(3)} and ${crossover.toDensity.toFixed(3)} (${crossover.from} -> ${crossover.to})`
    : 'CROSSOVER: no crossover (majority winner never flips across the sweep)');

  if (opts.json) {
    const matches = [...byKey.values()].sort((a, b) => (a.density - b.density) || (a.seed - b.seed));
    writeFileSync(opts.json, JSON.stringify({
      config: {
        file: opts.file, densities: opts.densities, seeds: opts.seeds, seedStart: opts.seedStart,
        teamA: opts.teamA, teamB: opts.teamB, destructibleAsteroids: opts.destructible,
        setOverrides: opts.sets, workers: opts.workers,
      },
      matches,
      aggregates: aggs,
      flipMetricPts: flip,
      crossover,
    }, null, 2) + '\n');
    console.log(`JSON written: ${opts.json}`);
  }
}

// ---------------------------------------------------------------- parent main

function main() {
  const opts = parseArgs(process.argv.slice(2));
  const cfg = { file: opts.file, teamA: opts.teamA, teamB: opts.teamB, baseOverrides: baseOverrides(opts) };
  const tasks = [];
  for (const density of opts.densities)
    for (let s = 0; s < opts.seeds; s++) tasks.push({ density, seed: opts.seedStart + s });

  const collector = makeCollector(opts, (byKey) => report(opts, byKey));

  const nWorkers = Math.min(opts.workers, tasks.length);
  if (nWorkers <= 1) {
    let Praedra;
    try { Praedra = sharedLoadSimRM(opts.file, { requireFn: 'runMatch' }); } catch (err) { fail(err.message); }
    for (const task of tasks) collector.add(runOne(Praedra, cfg, task));
    return;
  }

  // Preflight the sim in the parent so bad files/markers fail fast with exit 1.
  try { sharedLoadSimRM(opts.file, { requireFn: 'runMatch' }); } catch (err) { fail(err.message); }

  for (let w = 0; w < nWorkers; w++) {
    const child = fork(SELF, ['--worker'], { stdio: ['ignore', 'inherit', 'inherit', 'ipc'] });
    child.on('message', (msg) => {
      if (msg.type === 'result') collector.add(msg.rec);
      else if (msg.type === 'fatal') fail(`worker ${w} failed: ${msg.error}`);
    });
    child.on('exit', (code, signal) => {
      if (code !== 0 && code !== null) fail(`worker ${w} exited with code ${code}`);
      if (signal) fail(`worker ${w} killed by ${signal}`);
    });
    child.send({ type: 'init', cfg, tasks: tasks.filter((_, i) => i % nWorkers === w) });
  }
}

if (process.argv.includes('--worker')) workerMain();
else main();
