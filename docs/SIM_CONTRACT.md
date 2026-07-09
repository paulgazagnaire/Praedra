# Praedra Sim API Contract (v1)

This contract binds `index.html` (the sim implementation) and everything in `harness/`
(headless runners/tests). Both sides are built against THIS document. Do not deviate
without updating this file.

## Where the sim lives

**`index.html` is a BUILD ARTIFACT.** The source of truth is `src/` (sim modules in
`src/sim/*.js`, app in `src/app/`, page shell in `src/page/`); `node build.mjs`
assembles the self-contained single file, and `node build.mjs --check` fails if the
artifact is stale. Everything below about `index.html`'s layout is preserved verbatim
by the build — harness code needs no knowledge of `src/`.

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
  - `teamA`, `teamB` — preset name string (`'RAILGUN'`/`'SWARM'`/`'BALANCED'`, each a
    42-point fleet; budget constant in `config.fleetPoints`) or an arbitrary array of
    class names from `'battleship' | 'destroyer' | 'frigate' | 'bomber' | 'interceptor'`
    (custom fleets; ships spawn in ranks of `spawnRankSize`, array order front-to-back).
    The three presets contain no battleships; build a BB fleet with a custom array.
- Match object:
  - `match.step()` — advance exactly one fixed 60 Hz tick.
  - `match.tick` (int), `match.done` (bool), `match.result` (null until done).
  - `match.state` — inspectable: `state.ships` is an array of
    `{ id, team ('A'|'B'), cls, x, y, vx, vy, heading, speed, throttle, hp, maxHp,
    alive, pinned, order }` (`order` is `null` unless `issueOrder` was called, see below);
    `state.asteroids` is an array of `{ id, x, y, r, hp, maxHp, vx, vy, rot, rotVel,
    shape, alive, moving }` (see Asteroids below); `state.slugs` holds in-flight
    heavy-rail slugs (see Heavy railgun below). `state.detA`/`state.detB` and
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
  3. Headless speed: per-tick cost stays well under the 16 ms real-time budget
     (small fleets ~0.7 ms/tick, full armadas a few ms). Wall-clock per match is
     bounded by the tick cap; titan-scale maps (8000x5600, 360 s timer) make long
     hunts legitimately long in SIM seconds, so budget wall-clock per tick, not
     per match.

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
  battleship 4200, destroyer 2800, frigate 1700, bomber 900, interceptor 750 → coasting
  visible ranges 2520 / 1680 / 1020 / 540 / 450 px respectively (burning, ×thrustMultMax
  1.2: battleship 5040 / destroyer 3360 / …). The battleship has the largest signature in
  the game — it lights up the sky and is detected early, which its long guns rely on.
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
  `huntPoint` when a team has no live detected contacts. When it goes stale
  (> `memorySeconds*2.5`), hunting falls back to the enemy fleet's rough CENTROID
  (strategic picture only — it gates no weapon; the old enemy-spawn fallback was
  equally omniscient but stale, and on titan-scale maps it ran matches into the timer).

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

## Terrain: the TITAN + BIG asteroids (guaranteed) + gravity

Every `createMatch` map (procedural terrain) contains **exactly one TITAN** with `r`
in `terrain.titanRadius` (default `[1035, 1440]` — ~5x a BIG) **and
1..`terrain.bigCountMax` BIG asteroids** with `r` in `terrain.bigRadius` (default
`[220, 380]`) — never zero, regardless of seed or `terrainDensity`. The titan is
placed first and roams anywhere — it MAY be cut by the arena boundary, but always
keeps **at least 65% of its disc inside the playable zone** (so it always shapes
the fight). The bigs are placed next, fully inside the margins (clusters and sparse
rocks flow around them all); they anchor the map layout. `createScenario` terrain
stays fully explicit — no titan or bigs are injected there.

Asteroid outlines (`shape`) have 18..160 vertices scaling with radius (three
harmonics + fine grit — detail rank follows size rank; the titan carries 130+).
The contour is also the **contact surface**: rock-rock resting/collision, ship
hull bonks, gravity-accretion support, and torpedo/bomb rock strikes all resolve
against the interpolated shape polygon (`contourR`), so accretion hugs the lumps
rather than an invisible bounding circle. LOS, detection, the railgun ray, AOE
radii, avoidance margins and `attackrock` ranges stay circular on `r`.

**Gravity** (CONFIG `gravity`): every live rock with `r >= gravity.sourceMinRadius`
(default 100 — the titan, the BIGs, and their large fragments) is a gravity source
with mass `r^3`. Acceleration toward a source at distance `d` is
`G * r^3 / (d^2 + (softening*r)^2)`, clamped to `maxAccel`, ignored below `minAccel`,
and bounded to a well radius of `r * wellReach` (fading smoothly to zero over the
outer 15% — without this bound the titan's mass would act map-wide). It acts every
tick on:

- **ships** (× `gravity.shipMult`, additionally capped at `gravity.shipEscapeCap` ×
  the ship's own max thrust accel so a well can threaten but never imprison) — the
  autopilot fights the drift; `pinned` ships are exempt (they hold station by contract);
- **rocks** (× `gravity.rockMult`) — moving debris curls into the wells; a settled
  rock ANYWHERE in a well wakes when the pull exceeds `gravity.rockWake` (default
  1.2 px/s², just above the drag/settle stall boundary — there is NO distance shell;
  everything a well reaches, it moves) provided some contributing source is STRICTLY
  BIGGER than the rock AND nothing supports it on the down-well side. Whole fields
  creep and accrete; BIG asteroids themselves fall into the titan's well over
  minutes. Piles are stable once in contact (support), rocks outside every well or
  in the sub-`rockWake` fringe rest, and the titan — never out-massed — never moves:
  the landmark stays dependable even though the cover around it migrates.
  (`gravity.rockWakeShell` still exists but is TERRAIN-PLACEMENT ONLY: generateTerrain
  keeps BIGs that far off the titan's surface so they anchor the layout at spawn);
- **torpedoes and bombs** (× `gravity.projectileMult`) — their course bends but
  their speed is renormalised to the design speed (guidance and lead-aim semantics
  survive; a well only curves the path).

Everything responds to gravity per the equivalence principle (acceleration is
mass-independent), and trajectory deflection scales as `~ g·L/v²`: a 420 px/s
torpedo visibly curls through a well, while the 2400 px/s DESTROYER railgun slug's
real sagitta is ~1 px — it stays hitscan mechanically (the battleship's heavy-rail
slug is a real projectile, see §Heavy railgun, and gets no gravity at all), and the
app renders the destroyer trace
with that sagitta boosted `gravity.slugBendVisual`× (render-only) so the speed
hierarchy reads on screen. Inertial mass appears wherever momentum is exchanged
(ships `def.mass`, rocks `r²` in collisions, `r³` as gravitational source mass;
warheads detonate on contact rather than exchanging momentum).

`gravity.G: 0` (or `sourceMinRadius: Infinity`) disables the whole system; the app's
UI sliders scale `G` live via `match.config.gravity.G` (deterministic per run only
if left untouched, which headless code always is). Gravity is pure state math — no
RNG — so determinism from seed is unaffected.

## Asteroids: always-destructible, always-splitting

`destructibleAsteroids` is IGNORED — destruction is unconditionally on regardless of
config value (the field is kept only for override-compatibility with older harness
code). Every asteroid is `{ id, x, y, r, hp, maxHp, vx, vy, rot, rotVel, shape,
alive, moving }`: `hp = asteroidHP × (r / asteroidHPRefRadius)²`; `shape` is a
deterministic array of per-vertex radius factors — the render outline AND the
contact surface (see §Terrain: contacts resolve on the interpolated contour;
LOS/rays/ranges stay circular on `r`); `rot`/`rotVel` are the spin (the contact
contour tumbles with `rot`); `moving` is true while the rock has residual velocity
(settled rocks are immovable to ship impacts, except pebbles below
`collision.pushableRockRadius`, which hulls shove aside).
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

- **Railgun / gatling**: hit the FIRST ship hull intersecting the
  firing ray, regardless of team — a teammate standing in the line of fire eats the
  shot instead of the intended (possibly enemy) target. If nothing living is in the
  way, a railgun MISS on the intended target continues down the ray and can still
  crack a rock further out (`fireRailgun`'s "missed slug flies on downrange" path).
- **Heavy railgun (battleship)**: no longer first-hull-stops-shot — slugs are real
  flying projectiles that PIERCE. Every trackable-class hull crossing the flight
  path, ANY team, rolls the same uniform hit chance (evasion × speed factor) exactly
  once per slug, and the slug flies on either way; a teammate in the line can still
  eat a hit, it just no longer shields whatever is behind it. An asteroid stops the
  slug dead (`damage × rockDamageMult`). Slugs launch from the firing turret's MOUNT
  point (a small offset along the hull spine) plus a barrel length, along the
  turret's actual barrel angle. See §Heavy railgun.
- **Torpedoes**: contact-detonate against ANY non-owner hull they touch in flight
  (friend or foe), not just their locked target; the locked target itself keeps
  its own evasion-gated terminal-approach roll instead of a flat contact check.
- **AOE** (torpedo/bomb detonation): `applyAoe` always damages every ship in radius,
  friendly or enemy, with linear falloff to 40% at the radius edge; also always
  damages asteroids in radius and sympathetically detonates any bomb caught in the blast.
- `applyDamage`'s `attacker`/team bookkeeping (`state.damage`, `damageDealt`) only
  credits damage dealt to an actual enemy — friendly-fire hits still reduce the
  victim's hp but are not counted in `result.damageA`/`damageB`.

## Heavy railgun (Battleship turrets)

The battleship (`ships.battleship`) mounts `heavyRail.turrets` (3) independent turrets
instead of a fixed-forward railgun. It is a PROJECTILE weapon: each shot is a real
flying slug in `state.slugs`, released after a visible load cycle. Each ship carries
`ship.turrets = [{ ang, cool, load, lost }]` (index 0 = bow); `ang` is the turret's
absolute world facing (the renderer draws it), `cool` is seconds until the breech is
ready, `load` is the rail-charge progress toward `loadTime`, `lost` times how long the
firing solution has been broken. Deterministic firing model:

- **Stagger**: at spawn, turret `i` starts `cool = i × (cooldown / turrets)` so the guns
  fire out of phase (0.00 / 1.67 / 3.33 s with `cooldown` 5); with `loadTime` the
  ship-wide rate is ~1 slug / 2.1 s when all three bear.
- **Turret arcs (WWII arrangement)**: each turret may only train/fire within
  `arcCenters[i] ± arcHalfWidth` of the hull heading (hull-relative; bow pair centre 0,
  aft turret centre π; half-width 2.36 ≈ 135°). The bow turrets are blind astern, the
  aft turret is blind over the bow; all three bear on either beam. An out-of-arc target
  sends the mount to its limit stop, where it waits holding fire; hull yaw can never
  drag a barrel past its mount limits (hard hull-relative clamp). Idle turrets recentre
  on their own arc centre, and spawn `ang` at it.
- **Slew**: each turret rotates toward its slew target at `slewRate` (0.35) rad/s and may
  fire only when within `aimTolerance` of the true bearing — a target whose bearing
  changes faster than `slewRate` outruns the turret and is never engaged.
- **Loading**: the rails charge (`load += dt`, capped at `loadTime` 1.2 s) whenever the
  turret HOLDS a full firing solution (target mode, in arc, aligned, in range window,
  LOS for ship targets) — including while the breech is still cooling. The slug releases
  once `cool <= 0` AND `load >= loadTime`, then `cool = cooldown` (5 s per turret),
  `load = 0`. A broken solution only dumps the charge after `loadGrace` (0.25 s) — brief
  nav yaws on a burning hull don't reset a nearly-complete load. Pure accumulation, no
  RNG anywhere in the turret update.
- **Slug flight** (`updateHeavySlugs`, a fixed `stepMatch` slot after `updateBombs`):
  slugs fly at `slugSpeed` (2400 px/s) along the barrel angle from the mount point.
  Slug record: `{ id, team, ownerId, turret, x, y, vx, vy, traveled, alive, rolled,
  hitShip }`. Slugs receive NO gravity (real deflection at 2400 px/s is ~1 px), are not
  PD-interceptable, and die at `maxRange` traveled or off-arena (±50 px, like torpedoes).
  Dead slugs are filtered from `state.slugs` every 30 ticks like torps/bombs.
- **Pierce (ships) / stop (rocks)**: per tick the slug sweeps a segment; every living
  trackable-class hull on it — ANY team, friendly fire preserved — that the slug has not
  already rolled against (ids in `rolled`) gets ONE hit roll, and the slug pierces on
  whether it hits or misses. The first asteroid on the segment stops the slug dead and
  absorbs `damage × rockDamageMult`.
- **Class gate (hard)**: only classes in `heavyRail.trackClasses` (`destroyer`,
  `frigate`, `battleship`) can be damaged — a slug overpenetrates a bomber or
  interceptor without fuzing, NEVER damaging it, regardless of range or detection.
- **Speed gate**: per-hull hit chance is `(1 − evasion×evasionMult) × speedFactor`, where
  `speedFactor = clamp01((speedNoTrack − v) / (speedNoTrack − speedFullTrack))`. A target
  at/above `speedNoTrack` (85, the frigate cruise) is untrackable (factor 0); at/below
  `speedFullTrack` (55, the destroyer cruise) there is no penalty (factor 1).
- **Dead zone / range**: no fire inside `minRange` (420 — deliberately wider than PD's
  ship reach so close-in smalls get a real fighting window, see §Doctrine) or beyond
  `maxRange` (3200);
  range is measured from the ship centre. `maxRange` is far past the railgun's 700 but
  detection-bounded — at long range only lit-up (burning/scouted) ships are engageable.
- **Determinism**: the only RNG decision unique to heavy rail is the per-hull hit roll
  (`state.rng.chance`) inside `updateHeavySlugs`, drawn in slug-array order then
  along-segment order (ties broken by ship id). A rock stop additionally invokes the
  shared asteroid-split RNG via `damageAsteroid` → `splitAsteroid` (fragment angles/
  sizes/spins), interleaved at the same deterministic point in that order — everything
  is fully derived from state.
- **Events**: firing pushes `{ kind: 'hrailMuzzle', x, y, ang, team, turret }` at the
  muzzle; each impact pushes `{ kind: 'hrail', x, y, x2, y2, team, turret, hit }` with
  `x == x2, y == y2` (a point record at the impact) and `hit ∈ 'ship'|'rock'|'miss'`
  ('miss' fires once, where a slug that damaged no ship and hit no rock expires).

## Doctrine (per-team AI level)

`config.doctrine = { A: 'v2', B: 'v2' }` selects each team's AI doctrine:

- `'v2'` (**veteran**, the default): the researched doctrine layer (docs/TACTICS.md) —
  team battle picture recomputed every `ai2.focusEvery` ticks (focus-fire target
  selection with wounded-first finishing and isolation scoring, a no-overkill torpedo
  ledger, wolfpack dive orders against thinly-escorted battleships, synchronized
  torpedo volleys), per-class behaviors (battleship broadside discipline + min-gap
  field gate + demolition transit, destroyer masked approach + defilade, frigate
  dead-zone dives, bomber anvil axis-splitting + EMCON burn-and-coast, interceptor
  bomber-guarding screens + saturation-timed dives, lights dodging inbound bombs).
- `'v1'` (**line**): the legacy greedy per-ship AI, kept verbatim so batteries can
  measure doctrine head-to-head (`--set doctrine.A=v2 --set doctrine.B=v1`).

Doctrine changes AI DECISIONS only — no weapon stats, no new state fields the harness
must know, no API change, no new RNG draws. Determinism from seed holds for any
doctrine mix. Pinned ships (`aiPinned`) and the player `attack`-order fire control are
doctrine-independent. `ai2.*` holds every v2 knob (see CONFIG for the commented list);
sweep them via `overrides` like anything else.

The battleship's `heavyRail.minRange` is now **420** (was 220): PD ship-fire tops out
at `pd.range + target radius` (~156 from a battleship's centre), so 156..420 is a real
knife-fight ring — a small ship that survives the approach is safe from the main
battery and gets a genuine window to kill the big hull. The v2 wolfpack exploits
exactly this ring; an unescorted battleship against 2+ frigates is expected to die.

## Key CONFIG fields harness code may rely on

- `terrainDensity` (0..1) — the flip variable.
- `terrain.titanRadius` — TITAN size band (count is always exactly 1 on procedural maps).
- `terrain.bigRadius` / `terrain.bigCountMax` — BIG-asteroid size band and max count
  (min count is always 1 on procedural maps).
- `gravity.G` / `sourceMinRadius` / `softening` / `minAccel` / `maxAccel` / `wellReach`
  / `rockWake` / `shipEscapeCap` / `shipMult` / `rockMult` /
  `projectileMult` — see Terrain above. (`gravity.rockWakeShell` remains as a
  terrain-placement separation constant only; it no longer gates rock wake-up.)
- `destructibleAsteroids` (bool, present but IGNORED — see above).
- `matchTimerSeconds` (number).
- `presets` — fleet presets (same object as `Praedra.PRESETS`).
- `detection.thrustMultMin` / `thrustMultMax` / `checkEvery` / `memorySeconds`.
- `ships.<cls>.signature` — per-class detection signature.
- `ships.battleship` — the battleship def (`hp`, `radius` 78, `pdSlots`, `signature` 4200,
  `avoidMult`, etc.); largest hull in the game.
- `heavyRail.*` — the battleship's turret weapon: `turrets`, `turretMounts`, `damage`,
  `cooldown` (5, per turret), `loadTime` (1.2), `loadGrace`, `arcCenters` ([0, 0, π]),
  `arcHalfWidth` (2.36), `minRange` (420 — the knife-fight dead zone, see Doctrine
  above), `maxRange`, `slugSpeed`, `slewRate` (0.35),
  `aimTolerance`, `rockDamageMult`, `evasionMult`, `trackClasses`, `speedFullTrack`,
  `speedNoTrack` (see Heavy railgun above).
- `doctrine.A` / `doctrine.B` — per-team AI level, `'v1' | 'v2'` (see Doctrine above).
- `ai2.*` — v2-doctrine knobs (focus scoring, wolfpack, volley sync, EMCON band,
  anvil size, battleship `bbMinGap`/`bbDemolishMaxR`/`bbDetourMaxClutter`/
  `bbBroadside`). AI-internal; present for override sweeps only.
- `railgun.rockDamageMult`, `torpedo.rockDamageMult`, `asteroidHP`, `asteroidHPRefRadius`.
- `debris.fragmentCount`, `debris.childRadiusScale`, `debris.minChildRadius`, `debris.maxAsteroids`.
- `ai.clutterDetour*` (`Threshold`/`Gain`/`OffsetCost`/`Offsets`/`RefreshTicks`) and `ai.rockClear*`
  (`MinGoalDist`/`Lookahead`/`MaxRadius`/`DebrisPad`) tune the capital field-detour (`routeAround`)
  and lane-clearing fire (`clearTransitLane`). These are **AI-internal** — they change how a
  transiting capital routes around a cluttered field and shoots blockers out of its own corridor;
  they add no new API, event, or state field. Present for override sweeps only.

Everything else in CONFIG is sim-internal; sweep it via `overrides` generically.

## Events

`match.state.events` is a capped ring buffer (trimmed to the most recent 250 once it
exceeds 500) of `{ t: tick, kind, ... }` records for renderers/inspectors. Kinds seen
in the sim: `rail`, `hrail` (battleship heavy-rail slug IMPACT — see Heavy railgun above;
`{ kind: 'hrail', x, y, x2, y2, team, turret, hit }` with `x == x2, y == y2`),
`hrailMuzzle` (a turret firing — `{ kind: 'hrailMuzzle', x, y, ang, team, turret }`),
`gat`, `pd`, `launch`, `boom`
(torpedo/bomb detonation), `shatter` (asteroid destroyed), `shipboom` (ship destroyed),
and `order` (an `issueOrder` call — `{ kind: 'order', x, y, order: type }`). Harness code
should treat unknown kinds as forward-compatible no-ops rather than asserting an exhaustive
kind list.
