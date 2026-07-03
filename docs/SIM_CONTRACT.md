# Praedra Sim API Contract (v1)

This contract binds `index.html` (the sim implementation) and everything in `harness/`
(headless runners/tests). Both sides are built against THIS document. Do not deviate
without updating this file.

## Where the sim lives

`index.html` contains a `<script>` block with the ENTIRE DOM-free simulation between
these exact marker lines:

```
/* ===== SIM BEGIN ===== */
...pure JS, zero DOM/window/document/Date/performance/Math.random references...
/* ===== SIM END ===== */
```

Harness scripts extract the text between the markers and evaluate it with
`vm.runInNewContext(code, ctx)` where `ctx = { console }`. After evaluation the
context has a global `Praedra` object (the sim declares `var Praedra = ...` at top
level).

## Praedra API

- `Praedra.defaultConfig()` → fresh deep-copied CONFIG object (safe to mutate).
- `Praedra.PRESETS` → `{ RAILGUN: [...class names...], SWARM: [...] }`.
- `Praedra.createMatch(opts)` → match object. `opts`:
  - `seed` (integer, required) — all randomness derives from it; same opts ⇒ identical run.
  - `overrides` (object, optional) — deep-merged onto default CONFIG. Dotted keys are
    NOT used; pass nested objects, e.g. `{ terrainDensity: 0.2, railgun: { minRange: 180 } }`.
  - `teamA`, `teamB` — preset name string (`'RAILGUN'`/`'SWARM'`) or array of class
    names from `'destroyer' | 'frigate' | 'bomber' | 'interceptor'`.
- Match object:
  - `match.step()` — advance exactly one fixed 60 Hz tick.
  - `match.tick` (int), `match.done` (bool), `match.result` (null until done).
  - `match.state` — inspectable: `state.ships` is an array of
    `{ id, team ('A'|'B'), cls, x, y, vx, vy, heading, hp, maxHp, alive }`;
    `state.asteroids` is an array of `{ x, y, r, hp, alive }`.
  - `match.config` — the merged config in use.
- `Praedra.runMatch(opts)` → runs a created match to completion and returns `result`:
  ```
  {
    winner: 'A' | 'B' | 'draw',
    reason: 'elimination' | 'timer',
    ticks: int, seconds: number,
    nonResolution: bool,          // true iff resolved by timer/tick-cap, not kills
    fleetValueA, fleetValueB,     // surviving fleet value (Σ cost × hp fraction)
    damageA, damageB,             // total damage dealt BY each team to enemy ships
    survivorsA, survivorsB,       // e.g. { destroyer: 1, frigate: 0, ... } counts of living ships
  }
  ```
- `Praedra.createScenario(opts)` → match object for scripted tests. `opts`:
  - `seed`, `overrides` as above.
  - `ships`: array of `{ cls, team, x, y, vx?, vy?, heading?, pinned? }`. A `pinned: true`
    ship does not move, does not run AI navigation, but its weapons DO fire (AI targeting
    still selects targets and fires within arcs/ranges).
  - `asteroids`: array of `{ x, y, r }` — exact terrain; when `ships`/`asteroids` given,
    no procedural terrain is generated.
- HARD GUARANTEES the sim provides:
  1. Every match force-resolves at tick cap = `matchTimerSeconds × 60`. `runMatch`
     can never loop unbounded.
  2. Deterministic from `seed` + config + teams.
  3. A full match runs headless in well under 2 s wall-clock on this machine.

## Key CONFIG fields harness code may rely on

- `terrainDensity` (0..1) — the flip variable.
- `destructibleAsteroids` (bool).
- `matchTimerSeconds` (number).
- `presets` — fleet presets (same object as `Praedra.PRESETS`).

Everything else in CONFIG is sim-internal; sweep it via `overrides` generically.
