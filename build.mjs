#!/usr/bin/env node
// Praedra single-file bundler. Zero npm dependencies (Node 22).
//
// The project SOURCE lives in src/ (page shell, sim modules, app). This script
// assembles them into the self-contained, double-click-runnable index.html the
// project ships — including the exact /* ===== SIM BEGIN/END ===== */ markers the
// harness contract (docs/SIM_CONTRACT.md) extracts the sim from. index.html is a
// BUILD ARTIFACT now: never edit it by hand, edit src/ and run `node build.mjs`.
//
// Usage:
//   node build.mjs            # write index.html
//   node build.mjs --check    # exit 1 if index.html is stale vs src/ (CI guard)
//
// Sim modules concatenate INSIDE one IIFE (vm-context top-level globals are
// pathologically slow — see LESSONS.md), in the sorted filename order of
// src/sim/*.js. Prefix files 01_..99_ to control ordering.

import { readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';

const ROOT = dirname(fileURLToPath(import.meta.url));
const OUT = resolve(ROOT, 'index.html');

const read = (p) => readFileSync(resolve(ROOT, p), 'utf8');

function simModules() {
  const dir = resolve(ROOT, 'src/sim');
  return readdirSync(dir)
    .filter((f) => f.endsWith('.js'))
    .sort()
    .map((f) => ({ name: f, code: readFileSync(join(dir, f), 'utf8') }));
}

function assemble() {
  const mods = simModules();
  return (
    read('src/page/head.html') +
    '<style>\n' + read('src/page/style.css') + '</style>\n' +
    '</head>\n<body>\n' +
    read('src/page/body.html') +
    '\n<script>\n' +
    read('src/sim/00_open.txt') +
    mods.map((m) => m.code).join('') +
    'return api;\n})();\n/* ===== SIM END ===== */\n' +
    '</script>\n' +
    '\n<script>\n' +
    read('src/app/app.js') +
    '</script>\n</body>\n</html>\n'
  );
}

const html = assemble();
if (process.argv.includes('--check')) {
  let current = null;
  try { current = readFileSync(OUT, 'utf8'); } catch { /* missing counts as stale */ }
  if (current !== html) {
    console.error('build.mjs --check: index.html is STALE — run `node build.mjs`');
    process.exit(1);
  }
  console.log('build.mjs --check: index.html is up to date');
} else {
  writeFileSync(OUT, html);
  console.log(`build.mjs: wrote index.html (${html.length} bytes, ${simModules().length} sim modules)`);
}
