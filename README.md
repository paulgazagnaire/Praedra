# Praedra

A deterministic 2D space-RTS combat sandbox: RTS controls, fog of war via
emission-based detection, universal friendly fire, always-destructible terrain,
gravity wells, and five ship classes fighting under a researched combat doctrine
(see `docs/TACTICS.md`).

## Play it

Open **`index.html`** in a browser. That's it — the whole game is one
self-contained file (no server, no network, no dependencies). Pick fleets, seed,
density, gravity and per-team AI doctrine (**veteran** = the full doctrine layer,
**line** = the legacy greedy AI) on the setup screen.

## Project layout

`index.html` is a **build artifact** — never edit it by hand. Source of truth:

```
src/
  page/       head.html, style.css, body.html   (page shell)
  sim/        00_open.txt + 01..17_*.js         (the DOM-free deterministic sim;
              concatenated inside ONE IIFE in sorted filename order — vm-context
              top-level globals are pathologically slow, see LESSONS.md)
  app/        app.js                            (renderer + RTS input, DOM side)
build.mjs     zero-dependency bundler (Node 22)
harness/      run.mjs (batch battery runner), tests.mjs (acceptance gates)
docs/         SIM_CONTRACT.md (binding API), TACTICS.md (combat doctrine research)
```

## Workflow

```sh
node build.mjs             # src/ -> index.html (do this after ANY src edit)
node build.mjs --check     # CI guard: fails if index.html is stale
node harness/tests.mjs     # 38 acceptance gates (loads index.html)

# battery: any matchup, any config override, JSON stats out
node harness/run.mjs --densities 0.3,0.6 --seeds 15 \
  --teamA BALANCED --teamB BALANCED \
  --set doctrine.A=v2 --set doctrine.B=v1 --json out.json
```

The sim between the `/* ===== SIM BEGIN/END ===== */` markers is pure JS —
no DOM, no `Date`, no `Math.random` — so the harness executes the *exact* code
the browser runs. Same seed + config ⇒ identical match, headless or rendered.

## AI doctrine

`config.doctrine.A/B` selects each team's brain: `v1` is the legacy per-ship
greedy AI, `v2` (default) layers fleet-level doctrine on top — focus fire with a
no-overkill ledger, defeat-in-detail targeting, wolfpack dead-zone dives against
battleships, synchronized torpedo volleys, split-axis bomber anvils, EMCON
burn-and-coast approaches, masked (cover-hopping) advances, battleship broadside
discipline and the min-gap field gate (openings under `ai2.bbMinGap` are never
threaded — the battleship goes around, or demolishes the jamb from debris-safe
standoff when it's a wall). Doctrine and tuning rationale: `docs/TACTICS.md`;
binding API: `docs/SIM_CONTRACT.md`.
