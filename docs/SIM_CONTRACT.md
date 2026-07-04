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
    `{ id, team ('A'|'B'), cls, x, y, vx, vy, heading, speed, throttle, hp, maxHp,
    alive, pinned, order }` (`order` is `null` unless `issueOrder` was called, see below);
    `state.asteroids` is an array of `{ id, x, y, r, hp, maxHp, vx, vy, rot, rotVel,
    shape, alive, moving }` (see Asteroids below). `state.detA`/`state.detB` and
    `state.lastSeenShip` hold detection state (see Detection below).
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
    ship does not move, does not run AI navigation, and NEVER accepts `issueOrder`
    (pinned always runs the dedicated `aiPinned` routine, unconditionally, before
    orders are even checked) — but it DOES rotate to face the nearest DETECTED enemy
    and fires every weapon it legally can (railgun/gatling/bombs/torpedoes), gated by
    detection exactly like a normal ship (see Detection below).
  - `asteroids`: array of `{ x, y, r }` — exact terrain; when `ships`/`asteroids` given,
    no procedural terrain is generated.
- HARD GUARANTEES the sim provides:
  1. Every match force-resolves at tick cap = `matchTimerSeconds × 60`. `runMatch`
     can never loop unbounded.
  2. Deterministic from `seed` + config + teams.
  3. A full match runs headless in well under 2 s wall-clock on this machine.

## Detection (a ship must be SEEN to be targetable)

LOS (no rock on the segment) is necessary but not sufficient — you also have to be
close enough, thrust-adjusted, to be seen. This gates EVERY weapon's targeting,
including a `pinned` ship's own AI: a ship never fires on an enemy that isn't
currently in its team's detected set, and (see `blastCoverNearGhosts`) cover-busting
of a hidden enemy requires having detected that enemy at least once already — a
target that is NEVER detected (e.g. a rock sits permanently on the only sightline
between two static ships) is never engaged and never has ordnance spent on cover
hiding it, since neither the enemy nor a "ghost" of it ever entered memory.

- `visibleRange(ship) = ship.signature × lerp(detection.thrustMultMin, detection.thrustMultMax, ship.throttle)`.
  A `pinned` ship's throttle is always 0 (coasting), so its visible range is always
  `signature × detection.thrustMultMin` (defaults: 0.6). Signatures (CONFIG_DEFAULTS.ships):
  destroyer 2800, frigate 1700, bomber 900, interceptor 750 → coasting visible ranges
  1680 / 1020 / 540 / 450 px respectively.
- Team-shared sensor picture, recomputed every `detection.checkEvery` ticks (default 6):
  an enemy is detected if ANY living friendly has LOS to it within the enemy's own
  (thrust-modulated) visible range. Results live in `state.detA` / `state.detB` —
  arrays of the OTHER team's live ship objects currently detected by team A / team B.
  `isDetectedBy(state, team, ship)` checks membership.
- `state.lastSeenShip[shipId] = { x, y, t }` — last known position/time for every ship
  ever detected, kept regardless of current visibility, used to chase memory (player
  `attack` orders) and to demolish cover hiding a recently-seen enemy
  (`ai.rockShootSeconds`-gated; memory itself never expires but is only "recent"
  within `detection.memorySeconds`, default 5s).
- `state.lastContact[team] = { x, y, t }` — most recent sighting per team, used for
  `huntPoint` when a team has no live detected contacts.

## Player orders

`Praedra.issueOrder(match, shipIds, order)` where
`order = { type: 'move'|'attackmove'|'attack'|'attackrock'|'hold'|'auto', x?, y?, targetId? }`:
- `'auto'` clears the order (`ship.order = null`), returning the ship to its normal
  role AI (`aiDestroyer`/`aiFrigate`/`aiBomber`/`aiInterceptor`).
- `'move'` / `'attackmove'` navigate to `{x,y}`, firing opportunistically en route
  (`attackmove` breaks off to engage anything detected within 950px, then resumes).
  On arrival (within 42px, speed < 16) the order collapses to `{type:'hold', x, y}` —
  completed orders become Hold, they never silently revert to Auto.
- `'attack'` (needs `targetId`) closes to weapon range on a specific ship and fires
  when able; if the target isn't currently detected, the ship navigates toward its
  last-seen position (or straight at its live position if truly never seen) without
  attacking blind.
- `'attackrock'` (needs `targetId` = an asteroid id) closes to weapon range and fires
  on that specific rock every tick it's legal, independent of any ship detection.
- `'hold'` parks at `{x,y}` (defaults to current position) and fires opportunistically.
- Orders apply to non-pinned ships only (see `createScenario` above) — a `pinned`
  ship's `order` field is set but never read, since `aiPinned` runs unconditionally.
- Issuing any order pushes an `{ kind: 'order', x, y, order: type }` event (see Events).

## Asteroids: always-destructible, always-splitting

`destructibleAsteroids` is IGNORED — destruction is unconditionally on regardless of
config value (the field is kept only for override-compatibility with older harness
code). Every asteroid is `{ id, x, y, r, hp, maxHp, vx, vy, rot, rotVel, shape,
alive, moving }`: `hp = asteroidHP × (r / asteroidHPRefRadius)²`; `shape` is a
deterministic array of per-vertex radius jitter (visual only, physics stays
circular on `r`); `rot`/`rotVel` are the visual spin; `moving` is true while the
rock has residual velocity (settled/static rocks are immovable to ship impacts).
Any hit that brings `hp` to ≤0 splits it into `debris.fragmentCount` (default 4)
children at `r × debris.childRadiusScale`, each with outward burst velocity +
random spin, `moving: true`. Children below `debris.minChildRadius` don't spawn
(cascade terminates); total live rocks are capped at `debris.maxAsteroids`. Dead
rocks are periodically filtered out of `state.asteroids` (every 30 ticks), so a
destroyed rock's id eventually disappears from the array rather than lingering
with `alive: false`.

Rock damage sources and multipliers: railgun `damage × railgun.rockDamageMult`
(2.2), torpedo `damage × torpedo.rockDamageMult` (2.5), bomb/interceptor-gatling
deal their base damage directly (no multiplier) via `attackrock` orders only.

## Friendly fire (universal, always on)

- **Railgun / gatling**: hit the FIRST ship hull intersecting the firing ray,
  regardless of team — a teammate standing in the line of fire eats the shot
  instead of the intended (possibly enemy) target. If nothing living is in the
  way, a railgun MISS on the intended target continues down the ray and can still
  crack a rock further out (`fireRailgun`'s "missed slug flies on downrange" path).
- **Torpedoes**: contact-detonate against ANY non-owner hull they touch in flight
  (friend or foe), not just their locked target; the locked target itself keeps
  its own evasion-gated terminal-approach roll instead of a flat contact check.
- **AOE** (torpedo/bomb detonation): `applyAoe` always damages every ship in radius,
  friendly or enemy, with linear falloff to 40% at the radius edge; also always
  damages asteroids in radius and sympathetically detonates any bomb caught in the blast.
- `applyDamage`'s `attacker`/team bookkeeping (`state.damage`, `damageDealt`) only
  credits damage dealt to an actual enemy — friendly-fire hits still reduce the
  victim's hp but are not counted in `result.damageA`/`damageB`.

## Key CONFIG fields harness code may rely on

- `terrainDensity` (0..1) — the flip variable.
- `destructibleAsteroids` (bool, present but IGNORED — see above).
- `matchTimerSeconds` (number).
- `presets` — fleet presets (same object as `Praedra.PRESETS`).
- `detection.thrustMultMin` / `thrustMultMax` / `checkEvery` / `memorySeconds`.
- `ships.<cls>.signature` — per-class detection signature.
- `railgun.rockDamageMult`, `torpedo.rockDamageMult`, `asteroidHP`, `asteroidHPRefRadius`.
- `debris.fragmentCount`, `debris.childRadiusScale`, `debris.minChildRadius`, `debris.maxAsteroids`.

Everything else in CONFIG is sim-internal; sweep it via `overrides` generically.

## Events

`match.state.events` is a capped ring buffer (trimmed to the most recent 250 once it
exceeds 500) of `{ t: tick, kind, ... }` records for renderers/inspectors. Kinds seen
in the sim: `rail`, `gat`, `pd`, `launch`, `boom` (torpedo/bomb detonation), `shatter`
(asteroid destroyed), `shipboom` (ship destroyed), and `order` (an `issueOrder` call —
`{ kind: 'order', x, y, order: type }`). Harness code should treat unknown kinds as
forward-compatible no-ops rather than asserting an exhaustive kind list.
