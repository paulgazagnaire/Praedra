// Shared sim loader for the headless harness (tests.mjs / run.mjs / diagnose.mjs).
//
// The sim's source of truth is index.html's <script src="src/sim/*.js"> manifest —
// the browser loads those files as plain scripts sharing global scope, while this
// loader concatenates the same files IN TAG ORDER and wraps them in a single
// closure before evaluating in a vm context (vm-context global variable access is
// pathologically slow, so the harness must not run the sim as sandbox globals).
//
// Also accepts the legacy single-file format (inline /* ===== SIM BEGIN/END ===== */
// markers) so old builds remain loadable via --file.

import { readFileSync } from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';

const MARK_BEGIN = '/* ===== SIM BEGIN ===== */';
const MARK_END = '/* ===== SIM END ===== */';

// Returns the sim as one closed-over script string exporting `Praedra`.
export function simSource(file) {
  let html;
  try {
    html = readFileSync(file, 'utf8');
  } catch (err) {
    throw new Error(`cannot read sim file ${file}: ${err.message}`);
  }
  const b = html.indexOf(MARK_BEGIN);
  if (b >= 0) {
    const e = html.indexOf(MARK_END, b + MARK_BEGIN.length);
    if (e < 0) throw new Error(`marker "${MARK_END}" not found after BEGIN in ${file}`);
    return html.slice(b + MARK_BEGIN.length, e);
  }
  const dir = path.dirname(path.resolve(file));
  const srcs = [...html.matchAll(/<script\s+src="(src\/sim\/[^"]+)"><\/script>/g)].map((m) => m[1]);
  if (!srcs.length) {
    throw new Error(`${file} has neither "${MARK_BEGIN}" markers nor <script src="src/sim/..."> tags`);
  }
  const parts = srcs.map((s) => {
    const p = path.join(dir, s);
    try {
      return `/* ---- ${s} ---- */\n${readFileSync(p, 'utf8')}`;
    } catch (err) {
      throw new Error(`cannot read sim module ${p} (referenced by ${file}): ${err.message}`);
    }
  });
  // The modules' own `var Praedra = api;` (match.js) lands inside this closure;
  // re-export it as the context global the harness consumers expect.
  return `var Praedra = (function () {\n'use strict';\n${parts.join('\n;\n')}\n;return Praedra;\n})();`;
}

export function loadSim(file, { requireFn = 'createMatch', timeout } = {}) {
  const code = simSource(file);
  const ctx = { console };
  vm.runInNewContext(code, ctx, { filename: `praedra-sim(${file})`, ...(timeout ? { timeout } : {}) });
  if (!ctx.Praedra || typeof ctx.Praedra[requireFn] !== 'function') {
    throw new Error(`sim evaluated but no Praedra.${requireFn} global found`);
  }
  return ctx.Praedra;
}
