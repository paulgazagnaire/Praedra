#!/usr/bin/env node
// Praedra headless diagnostics runner. See docs/SIM_CONTRACT.md (binding) and harness/run.mjs
// (batch win-rate sweeper, same conventions). Node 22, ESM, zero npm dependencies.
//
// Purpose: run N matches for a single (density, teamA, teamB) config and print DIAGNOSTIC
// stats that identify AI mistakes — ordnance economy, damage/death breakdowns, fighter
// clumping/chain-risk exposure, fireBlocked reasons — not just win rates (see run.mjs for that).
//
// Usage:
//   node harness/diagnose.mjs [--file index.html] [--seeds 10] [--seedStart 1]
//     [--density 0.55] [--teamA RAILGUN] [--teamB SWARM] [--set path.to.key=value ...]
//     [--json out.json] [--sample 15]

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import vm from 'node:vm';

const SELF = fileURLToPath(import.meta.url);
const REPO_ROOT = resolve(dirname(SELF), '..');
const MARK_BEGIN = '/* ===== SIM BEGIN ===== */';
const MARK_END = '/* ===== SIM END ===== */';

// Ship-class groupings for clumping/chain-risk metrics. The sim has no single "capital"
// class — we treat the three non-fighter hulls as "capitals" and bomber+interceptor as
// "light", which is the natural complement and matches how the AI actually splits roles.
const LIGHT_CLASSES = ['bomber', 'interceptor'];
const CAPITAL_CLASSES = ['battleship', 'destroyer', 'frigate'];

function fail(msg) {
  console.error(`diagnose.mjs: ${msg}`);
  process.exit(1);
}

// ---------------------------------------------------------------- CLI / config helpers
// (parseValue/setPath/deepMerge mirror harness/run.mjs so --set behaves identically.)

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

function parseTeam(v) {
  return v.includes(',') ? v.split(',').map((s) => s.trim()).filter(Boolean) : v;
}

function parseArgs(argv) {
  const opts = {
    file: resolve(REPO_ROOT, 'index.html'),
    seeds: 10,
    seedStart: 1,
    density: 0.55,
    teamA: 'RAILGUN',
    teamB: 'SWARM',
    sets: {},
    json: null,
    sample: 15,
  };
  const next = (i, flag) => {
    if (i + 1 >= argv.length) fail(`missing value for ${flag}`);
    return argv[i + 1];
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    switch (a) {
      case '--file': opts.file = resolve(process.cwd(), next(i, a)); i++; break;
      case '--seeds':
        opts.seeds = Number(next(i, a));
        if (!Number.isInteger(opts.seeds) || opts.seeds < 1) fail('--seeds must be a positive integer');
        i++; break;
      case '--seedStart':
        opts.seedStart = Number(next(i, a));
        if (!Number.isInteger(opts.seedStart)) fail('--seedStart must be an integer');
        i++; break;
      case '--density':
        opts.density = Number(next(i, a));
        if (!Number.isFinite(opts.density)) fail('--density must be a number');
        i++; break;
      case '--teamA': opts.teamA = parseTeam(next(i, a)); i++; break;
      case '--teamB': opts.teamB = parseTeam(next(i, a)); i++; break;
      case '--set': {
        const kv = next(i, a);
        const eq = kv.indexOf('=');
        if (eq <= 0) fail(`--set expects path.to.key=value, got "${kv}"`);
        setPath(opts.sets, kv.slice(0, eq), parseValue(kv.slice(eq + 1)));
        i++; break;
      }
      case '--json': opts.json = resolve(process.cwd(), next(i, a)); i++; break;
      case '--sample':
        opts.sample = Number(next(i, a));
        if (!Number.isInteger(opts.sample) || opts.sample < 1) fail('--sample must be a positive integer');
        i++; break;
      default: fail(`unknown flag "${a}"`);
    }
  }
  return opts;
}

// ---------------------------------------------------------------- sim loading

function loadSim(file) {
  let html;
  try {
    html = readFileSync(file, 'utf8');
  } catch (err) {
    throw new Error(`cannot read sim file ${file}: ${err.message}`);
  }
  const b = html.indexOf(MARK_BEGIN);
  if (b < 0) throw new Error(`marker "${MARK_BEGIN}" not found in ${file}`);
  const e = html.indexOf(MARK_END, b + MARK_BEGIN.length);
  if (e < 0) throw new Error(`marker "${MARK_END}" not found after BEGIN in ${file}`);
  const code = html.slice(b + MARK_BEGIN.length, e);
  const ctx = { console };
  vm.runInNewContext(code, ctx, { filename: file });
  if (!ctx.Praedra || typeof ctx.Praedra.createMatch !== 'function')
    throw new Error('sim evaluated but no Praedra.createMatch global found');
  return ctx.Praedra;
}

// ---------------------------------------------------------------- small math helpers

function mean(arr) {
  return arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : null;
}

function percentile(arr, p) {
  if (arr.length === 0) return null;
  const a = [...arr].sort((x, y) => x - y);
  if (a.length === 1) return a[0];
  const idx = (p / 100) * (a.length - 1);
  const lo = Math.floor(idx), hi = Math.ceil(idx);
  if (lo === hi) return a[lo];
  return a[lo] + (a[hi] - a[lo]) * (idx - lo);
}

function pct(x, n) {
  return n > 0 ? (100 * x) / n : null;
}

function fmt(x, d = 1) {
  return x === null || x === undefined || Number.isNaN(x) ? 'n/a' : Number(x).toFixed(d);
}

function fmtPct(x, d = 1) {
  return x === null || x === undefined || Number.isNaN(x) ? 'n/a' : `${Number(x).toFixed(d)}%`;
}

// ---------------------------------------------------------------- clumping metrics

// Nearest same-team same-group neighbor distance for every live ship in the group.
// Ships with no live groupmate are excluded (nearest-neighbor is undefined for them).
function nearestNeighborDists(ships, team, classSet) {
  const group = ships.filter((s) => s && s.alive && s.team === team && classSet.includes(s.cls));
  const out = [];
  for (let i = 0; i < group.length; i++) {
    let best = Infinity;
    for (let j = 0; j < group.length; j++) {
      if (i === j) continue;
      const d = Math.hypot(group[i].x - group[j].x, group[i].y - group[j].y);
      if (d < best) best = d;
    }
    if (Number.isFinite(best)) out.push(best);
  }
  return out;
}

// Fraction of live group members whose nearest same-team groupmate is within `threshold`.
// A lone survivor (no groupmate) contributes 0 (no chain risk possible). Returns null if
// the group is empty (no live members at all — nothing to measure this sample).
function chainExposureFraction(ships, team, classSet, threshold) {
  const group = ships.filter((s) => s && s.alive && s.team === team && classSet.includes(s.cls));
  if (group.length === 0) return null;
  let close = 0;
  for (let i = 0; i < group.length; i++) {
    let best = Infinity;
    for (let j = 0; j < group.length; j++) {
      if (i === j) continue;
      const d = Math.hypot(group[i].x - group[j].x, group[i].y - group[j].y);
      if (d < best) best = d;
    }
    if (best < threshold) close++;
  }
  return close / group.length;
}

// ---------------------------------------------------------------- one match

function runOneMatch(Praedra, opts, seed) {
  const overrides = deepMerge({ terrainDensity: opts.density }, opts.sets);
  const match = Praedra.createMatch({ seed, overrides, teamA: opts.teamA, teamB: opts.teamB });
  const state = match.state;

  const initialRoster = { A: {}, B: {} };
  for (const sh of state.ships) {
    if (!sh) continue;
    const r = initialRoster[sh.team];
    if (r) r[sh.cls] = (r[sh.cls] || 0) + 1;
  }

  const clump = {
    lightP10: { A: [], B: [] }, lightMedian: { A: [], B: [] },
    capP10: { A: [], B: [] }, capMedian: { A: [], B: [] },
    bomberChainFrac: { A: [], B: [] }, capChainFrac: { A: [], B: [] },
  };

  function sampleNow() {
    const ships = state.ships;
    for (const team of ['A', 'B']) {
      const lightDists = nearestNeighborDists(ships, team, LIGHT_CLASSES);
      if (lightDists.length) {
        clump.lightP10[team].push(percentile(lightDists, 10));
        clump.lightMedian[team].push(percentile(lightDists, 50));
      }
      const capDists = nearestNeighborDists(ships, team, CAPITAL_CLASSES);
      if (capDists.length) {
        clump.capP10[team].push(percentile(capDists, 10));
        clump.capMedian[team].push(percentile(capDists, 50));
      }
      const bFrac = chainExposureFraction(ships, team, ['bomber'], 90);
      if (bFrac !== null) clump.bomberChainFrac[team].push(bFrac);
      const cFrac = chainExposureFraction(ships, team, CAPITAL_CLASSES, 200);
      if (cFrac !== null) clump.capChainFrac[team].push(cFrac);
    }
  }

  sampleNow(); // tick-0 baseline
  while (!match.done) {
    match.step();
    if (state.tick % opts.sample === 0) sampleNow();
  }

  const result = match.result;
  const stats = state.stats || {};

  // Deaths breakdown — defensive against unexpected/missing element shapes.
  const deathsByClass = { A: {}, B: {} };
  const deathsByCause = { A: {}, B: {} };
  const deathsArr = Array.isArray(stats.deaths) ? stats.deaths : [];
  for (const d of deathsArr) {
    if (!d || (d.team !== 'A' && d.team !== 'B')) continue;
    const cls = typeof d.cls === 'string' ? d.cls : 'unknown';
    const cause = typeof d.by === 'string' ? d.by : 'unknown';
    deathsByClass[d.team][cls] = (deathsByClass[d.team][cls] || 0) + 1;
    deathsByCause[d.team][cause] = (deathsByCause[d.team][cause] || 0) + 1;
  }
  // The death record carries no attacker-team field, so friendly-fire attribution
  // ("killed by own side's rail/torp/bomb") is NOT reliably derivable from `by` alone
  // (by is a weapon name, not a source team) — deliberately not reported to avoid
  // fabricating a stat the data can't support.

  function lifetimeStats(team, cls) {
    const total = (initialRoster[team] && initialRoster[team][cls]) || 0;
    if (total === 0) return { count: 0, deadCount: 0, meanLifetimeS: null };
    const deathTimes = deathsArr
      .filter((d) => d && d.team === team && d.cls === cls && typeof d.t === 'number')
      .map((d) => d.t);
    const deadCount = Math.min(deathTimes.length, total);
    const survivorCount = Math.max(0, total - deadCount);
    const sumDead = deathTimes.reduce((a, b) => a + b, 0);
    const sumSurvivors = survivorCount * (result.seconds || 0);
    return { count: total, deadCount, meanLifetimeS: (sumDead + sumSurvivors) / total };
  }

  const bomberLife = { A: lifetimeStats('A', 'bomber'), B: lifetimeStats('B', 'bomber') };
  const interceptorLife = { A: lifetimeStats('A', 'interceptor'), B: lifetimeStats('B', 'interceptor') };

  function bombsPerBomberLife(team) {
    const n = (initialRoster[team] && initialRoster[team].bomber) || 0;
    const fired = (stats.bombsFired && stats.bombsFired[team]) || 0;
    return n > 0 ? fired / n : null;
  }

  const clumpSummary = {
    lightP10: { A: mean(clump.lightP10.A), B: mean(clump.lightP10.B) },
    lightMedian: { A: mean(clump.lightMedian.A), B: mean(clump.lightMedian.B) },
    capP10: { A: mean(clump.capP10.A), B: mean(clump.capP10.B) },
    capMedian: { A: mean(clump.capMedian.A), B: mean(clump.capMedian.B) },
    bomberChainExposure: { A: mean(clump.bomberChainFrac.A), B: mean(clump.bomberChainFrac.B) },
    capChainExposure: { A: mean(clump.capChainFrac.A), B: mean(clump.capChainFrac.B) },
  };

  return {
    seed,
    winner: result.winner,
    reason: result.reason,
    ticks: result.ticks,
    seconds: result.seconds,
    nonResolution: result.nonResolution,
    fleetValueA: result.fleetValueA,
    fleetValueB: result.fleetValueB,
    damageA: result.damageA,
    damageB: result.damageB,
    survivorsA: result.survivorsA,
    survivorsB: result.survivorsB,
    initialRoster,
    stats: {
      torpsFired: stats.torpsFired, torpsPD: stats.torpsPD, torpsHit: stats.torpsHit,
      bombsFired: stats.bombsFired, bombsPD: stats.bombsPD, bombsHitShip: stats.bombsHitShip,
      dmgTo: stats.dmgTo,
      waves: stats.waves,
      runEntries: stats.runEntries,
      fireBlocked: stats.fireBlocked,
      salvosFired: stats.salvosFired,
    },
    deathsByClass, deathsByCause,
    bomberLife, interceptorLife,
    bombsPerBomberLifeA: bombsPerBomberLife('A'),
    bombsPerBomberLifeB: bombsPerBomberLife('B'),
    clump: clumpSummary,
  };
}

// ---------------------------------------------------------------- aggregation

function sumKeyed(records, pick, team) {
  const out = {};
  for (const r of records) {
    const obj = pick(r);
    const sub = obj && obj[team];
    if (!sub) continue;
    for (const [k, v] of Object.entries(sub)) out[k] = (out[k] || 0) + (Number(v) || 0);
  }
  return out;
}

function aggregate(opts, records) {
  const n = records.length;
  const winsA = records.filter((r) => r.winner === 'A').length;
  const winsB = records.filter((r) => r.winner === 'B').length;
  const draws = n - winsA - winsB;
  const elim = records.filter((r) => r.reason === 'elimination').length;
  const timer = records.filter((r) => r.reason === 'timer').length;

  const teamStat = (team, path) => records.map((r) => {
    let v = r.stats;
    for (const k of path) v = v && v[k];
    return v ? v[team] : undefined;
  }).filter((v) => typeof v === 'number');

  function ordnance(team) {
    const torpsFired = teamStat(team, ['torpsFired']).reduce((a, b) => a + b, 0);
    const torpsPD = teamStat(team, ['torpsPD']).reduce((a, b) => a + b, 0);
    const torpsHit = teamStat(team, ['torpsHit']).reduce((a, b) => a + b, 0);
    const bombsFired = teamStat(team, ['bombsFired']).reduce((a, b) => a + b, 0);
    const bombsPD = teamStat(team, ['bombsPD']).reduce((a, b) => a + b, 0);
    const bombsHitShip = teamStat(team, ['bombsHitShip']).reduce((a, b) => a + b, 0);
    return {
      torpsFired, torpsPD, torpsHit,
      torpsPDRate: pct(torpsPD, torpsFired), torpsHitRate: pct(torpsHit, torpsFired),
      bombsFired, bombsPD, bombsHitShip,
      bombsPDRate: pct(bombsPD, bombsFired), bombsHitRate: pct(bombsHitShip, bombsFired),
    };
  }

  function damage(team) {
    const total = records.reduce((a, r) => a + (Number(team === 'A' ? r.damageA : r.damageB) || 0), 0);
    const dmgToBreakdown = sumKeyed(records, (r) => r.stats.dmgTo, team); // damage RECEIVED by `team`, by weapon
    return { totalDealt: total, meanDealt: total / n, dmgToBreakdown };
  }

  function deaths(team) {
    const byClass = sumKeyed(records, (r) => r.deathsByClass, team);
    const byCause = sumKeyed(records, (r) => r.deathsByCause, team);
    const total = Object.values(byClass).reduce((a, b) => a + b, 0);
    return { total, byClass, byCause };
  }

  function clumping(team) {
    const pick = (key) => mean(records.map((r) => r.clump[key][team]).filter((v) => v !== null && v !== undefined));
    return {
      lightP10: pick('lightP10'), lightMedian: pick('lightMedian'),
      capP10: pick('capP10'), capMedian: pick('capMedian'),
      bomberChainExposure: pick('bomberChainExposure'), capChainExposure: pick('capChainExposure'),
    };
  }

  function fighter(team) {
    const bomberN = records.reduce((a, r) => a + ((r.initialRoster[team] && r.initialRoster[team].bomber) || 0), 0);
    const bomberLifeMean = mean(records.map((r) => r.bomberLife[team].meanLifetimeS).filter((v) => v !== null));
    const bombsPerLife = mean(records.map((r) => (team === 'A' ? r.bombsPerBomberLifeA : r.bombsPerBomberLifeB)).filter((v) => v !== null));
    const waves = records.reduce((a, r) => a + ((r.stats.waves && r.stats.waves[team]) || 0), 0);
    return { bomberCount: bomberN, bomberLifeMeanS: bomberLifeMean, bombsPerBomberLife: bombsPerLife, waves };
  }

  // fireBlocked/salvosFired/runEntries are NOT split by team in the sim (they track the
  // shared aiBomber run-logic path for whichever ship is executing it) — report globally.
  const fireBlockedTotal = records.reduce((acc, r) => {
    const fb = r.stats.fireBlocked || {};
    for (const k of ['cooldown', 'range', 'los', 'lane']) acc[k] = (acc[k] || 0) + (Number(fb[k]) || 0);
    return acc;
  }, {});
  const salvosFired = records.reduce((a, r) => a + (Number(r.stats.salvosFired) || 0), 0);
  const runEntries = records.reduce((a, r) => a + (Number(r.stats.runEntries) || 0), 0);

  return {
    n, winsA, winsB, draws,
    aWinPct: pct(winsA, n), bWinPct: pct(winsB, n), drawPct: pct(draws, n),
    elimination: elim, timer, nonResolution: records.filter((r) => r.nonResolution).length,
    avgSeconds: mean(records.map((r) => r.seconds)),
    avgFleetValueA: mean(records.map((r) => r.fleetValueA)),
    avgFleetValueB: mean(records.map((r) => r.fleetValueB)),
    ordnanceA: ordnance('A'), ordnanceB: ordnance('B'),
    damageA: damage('A'), damageB: damage('B'),
    deathsA: deaths('A'), deathsB: deaths('B'),
    clumpingA: clumping('A'), clumpingB: clumping('B'),
    fighterA: fighter('A'), fighterB: fighter('B'),
    fireBlocked: fireBlockedTotal, salvosFired, runEntries,
  };
}

// ---------------------------------------------------------------- reporting

function line(cols, widths) {
  return cols.map((c, i) => String(c).padStart(widths[i])).join('  ');
}

function printSeedTable(records) {
  const header = ['seed', 'winner', 'reason', 'secs', 'fvA', 'fvB', 'bombHit/Fired-A', 'bombHit/Fired-B', 'bChainExp-A%', 'bChainExp-B%'];
  const widths = [5, 7, 11, 7, 7, 7, 16, 16, 12, 12];
  console.log('');
  console.log('Per-seed table:');
  console.log(line(header, widths));
  console.log(line(widths.map((w) => '-'.repeat(w)), widths));
  for (const r of records) {
    const bfA = (r.stats.bombsHitShip && r.stats.bombsHitShip.A) || 0;
    const ffA = (r.stats.bombsFired && r.stats.bombsFired.A) || 0;
    const bfB = (r.stats.bombsHitShip && r.stats.bombsHitShip.B) || 0;
    const ffB = (r.stats.bombsFired && r.stats.bombsFired.B) || 0;
    const ceA = r.clump.bomberChainExposure.A, ceB = r.clump.bomberChainExposure.B;
    console.log(line([
      r.seed, r.winner, r.reason, fmt(r.seconds), fmt(r.fleetValueA), fmt(r.fleetValueB),
      `${bfA}/${ffA}`, `${bfB}/${ffB}`,
      ceA === null ? 'n/a' : fmt(ceA * 100, 0), ceB === null ? 'n/a' : fmt(ceB * 100, 0),
    ], widths));
  }
}

function printBreakdown(title, obj) {
  const keys = Object.keys(obj).sort((a, b) => obj[b] - obj[a]);
  if (keys.length === 0) { console.log(`    ${title}: (none)`); return; }
  console.log(`    ${title}: ${keys.map((k) => `${k}=${fmt(obj[k], Number.isInteger(obj[k]) ? 0 : 1)}`).join('  ')}`);
}

function printReport(opts, records, agg) {
  console.log('');
  console.log(`Praedra diagnostics — ${opts.teamA} vs ${opts.teamB}, density ${opts.density}, ${agg.n} seed(s) [${opts.seedStart}..${opts.seedStart + agg.n - 1}]`);
  console.log(`file: ${opts.file}`);

  console.log('');
  console.log('1. Outcomes');
  console.log(`  A wins ${agg.winsA} (${fmtPct(agg.aWinPct)})  B wins ${agg.winsB} (${fmtPct(agg.bWinPct)})  draws ${agg.draws} (${fmtPct(agg.drawPct)})`);
  console.log(`  elimination ${agg.elimination}  timer/non-resolution ${agg.timer}  mean seconds ${fmt(agg.avgSeconds)}`);
  console.log(`  mean surviving fleet value  A=${fmt(agg.avgFleetValueA)}  B=${fmt(agg.avgFleetValueB)}`);

  console.log('');
  console.log('2. Ordnance economy');
  for (const team of ['A', 'B']) {
    const o = agg[`ordnance${team}`];
    console.log(`  Team ${team}: torps fired=${o.torpsFired} PD=${o.torpsPD} (${fmtPct(o.torpsPDRate)}) hit=${o.torpsHit} (${fmtPct(o.torpsHitRate)})`);
    console.log(`           bombs fired=${o.bombsFired} PD=${o.bombsPD} (${fmtPct(o.bombsPDRate)}) hitShip=${o.bombsHitShip} (${fmtPct(o.bombsHitRate)})`);
  }

  console.log('');
  console.log('3. Damage');
  for (const team of ['A', 'B']) {
    const d = agg[`damage${team}`];
    console.log(`  Team ${team}: total dealt=${fmt(d.totalDealt, 0)}  mean/match=${fmt(d.meanDealt)}`);
    printBreakdown(`damage RECEIVED by ${team}, by weapon`, d.dmgToBreakdown);
  }

  console.log('');
  console.log('4. Deaths');
  for (const team of ['A', 'B']) {
    const d = agg[`deaths${team}`];
    console.log(`  Team ${team}: total deaths=${d.total}`);
    printBreakdown('by class', d.byClass);
    printBreakdown('by cause (weapon; NOT attacker-team-attributed — see note below)', d.byCause);
  }
  console.log('  note: stats.deaths carries {team, cls, t, by, mode} — `by` is a weapon name');
  console.log('  (rail/hrail/torp/torpAoe/bomb/gat/pd/rock/other), not an attacker team, so');
  console.log('  friendly-fire/chain-kill attribution cannot be derived from this field alone.');

  console.log('');
  console.log('5. Clumping / chain-risk (sampled every ' + opts.sample + ' ticks)');
  for (const team of ['A', 'B']) {
    const c = agg[`clumping${team}`];
    console.log(`  Team ${team} LIGHT (bomber+interceptor): nearest-neighbor p10=${fmt(c.lightP10)}px  median=${fmt(c.lightMedian)}px`);
    console.log(`           bomber chain-risk exposure (nearest same-team bomber < 90px): ${c.bomberChainExposure === null ? 'n/a' : fmt(c.bomberChainExposure * 100) + '%'}`);
    console.log(`  Team ${team} CAPITAL (battleship+destroyer+frigate): nearest-neighbor p10=${fmt(c.capP10)}px  median=${fmt(c.capMedian)}px`);
    console.log(`           capital chain-risk exposure (nearest same-team capital < 200px): ${c.capChainExposure === null ? 'n/a' : fmt(c.capChainExposure * 100) + '%'}`);
  }

  console.log('');
  console.log('6. Fighter effectiveness');
  for (const team of ['A', 'B']) {
    const f = agg[`fighter${team}`];
    console.log(`  Team ${team}: bombers deployed(total across seeds)=${f.bomberCount}  mean bomber lifetime=${fmt(f.bomberLifeMeanS)}s`);
    console.log(`           bombs fired per bomber-life=${fmt(f.bombsPerBomberLife, 2)}  wave count(total)=${f.waves}`);
  }
  console.log(`  fireBlocked (global, both teams' bombers combined): cooldown=${agg.fireBlocked.cooldown || 0}  range=${agg.fireBlocked.range || 0}  los=${agg.fireBlocked.los || 0}  lane=${agg.fireBlocked.lane || 0}`);
  console.log(`  salvosFired(total)=${agg.salvosFired}  runEntries(total)=${agg.runEntries}`);

  printSeedTable(records);

  console.log('');
  console.log('=== AGGREGATE SUMMARY ===');
  console.log(`${opts.teamA} vs ${opts.teamB} @ density ${opts.density}, n=${agg.n}`);
  console.log(`Win rate: A ${fmtPct(agg.aWinPct)}  B ${fmtPct(agg.bWinPct)}  draw ${fmtPct(agg.drawPct)}  (elim ${agg.elimination}/timer ${agg.timer})`);
  console.log(`Torp hit%: A ${fmt(agg.ordnanceA.torpsHitRate)}  B ${fmt(agg.ordnanceB.torpsHitRate)}   Bomb hit%: A ${fmt(agg.ordnanceA.bombsHitRate)}  B ${fmt(agg.ordnanceB.bombsHitRate)}`);
  console.log(`Bomber chain-risk exposure: A ${agg.clumpingA.bomberChainExposure === null ? 'n/a' : fmt(agg.clumpingA.bomberChainExposure * 100) + '%'}  B ${agg.clumpingB.bomberChainExposure === null ? 'n/a' : fmt(agg.clumpingB.bomberChainExposure * 100) + '%'}`);
  console.log('');
}

// ---------------------------------------------------------------- main

function main() {
  const opts = parseArgs(process.argv.slice(2));
  let Praedra;
  try { Praedra = loadSim(opts.file); } catch (err) { fail(err.message); }

  const records = [];
  const errors = [];
  for (let s = 0; s < opts.seeds; s++) {
    const seed = opts.seedStart + s;
    try {
      records.push(runOneMatch(Praedra, opts, seed));
    } catch (err) {
      errors.push({ seed, error: String((err && err.stack) || err) });
    }
  }

  if (records.length === 0) {
    for (const e of errors) console.error(`seed ${e.seed} failed: ${e.error}`);
    fail('every match errored — nothing to report');
  }
  if (errors.length) {
    console.error(`WARNING: ${errors.length}/${opts.seeds} seed(s) errored and were excluded:`);
    for (const e of errors) console.error(`  seed ${e.seed}: ${e.error.split('\n')[0]}`);
  }

  const agg = aggregate(opts, records);
  agg.n = records.length;
  printReport(opts, records, agg);

  if (opts.json) {
    writeFileSync(opts.json, JSON.stringify({
      config: {
        file: opts.file, seeds: opts.seeds, seedStart: opts.seedStart, density: opts.density,
        teamA: opts.teamA, teamB: opts.teamB, setOverrides: opts.sets, sample: opts.sample,
      },
      records, aggregate: agg, errors,
    }, null, 2) + '\n');
    console.log(`JSON written: ${opts.json}`);
  }
}

main();
