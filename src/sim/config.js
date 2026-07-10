/* Praedra sim module: config — every gameplay number lives here — DOM-free, deterministic. Part of the sim manifest
   (script tags in index.html; harness wraps all modules in one closure).
   Contract: docs/SIM_CONTRACT.md. Units: px, seconds, radians. heading 0 = +x, CCW+. */
"use strict";

/* ---------------- CONFIG — every gameplay number lives here ---------------- */
var CONFIG_DEFAULTS = {
  tickRate: 60,
  matchTimerSeconds: 360,          // hard cap; titan-scale maps need transit room (was PRD
                                   // 150-300). Symmetric armada mirrors are attrition wars
                                   // whose kill rate decays — they resolve on points at the
                                   // cap regardless of how much more time they get (verified
                                   // at 480s: durations inflated, nonres barely moved)
  movementModel: 'newtonian',      // 'newtonian' | 'arcade' (arcade = debug fallback only)
  fleetMode: 'preset',

  arena: { w: 8000, h: 5600 },     // titan-scale: one asteroid alone is up to 3200px across

  // --- DETECTION (LOS is necessary but not sufficient: you also have to SEE it) ---
  detection: {
    thrustMultMin: 0.6,            // coasting ships are dim...
    thrustMultMax: 1.2,            // ...a full burn lights the plume
    checkEvery: 6,                 // ticks between sensor sweeps
    memorySeconds: 5,              // last-seen positions stay actionable this long
  },

  // --- TERRAIN (one mixed map; terrainDensity is THE flip variable) ---
  terrainDensity: 0.5,             // 0..1: fraction of map given to dense clusters vs open lanes
  destructibleAsteroids: true,     // ALWAYS ON (kept for override compat; the sim ignores false)
  terrain: {
    clusterCountMin: 1,            // clusters at density 0
    clusterCountMax: 24,           // clusters at density 1 (scaled with the larger arena)
    clusterRadius: [300, 600],
    clusterFillPerArea: 0.000026,  // rocks per px^2 of cluster area
    asteroidRadius: [26, 90],
    sparseRockMin: 4,              // lone rocks scattered outside clusters (low..high density).
    sparseRockMax: 90,             // High density must be dense EVERYWHERE — capitals park in
                                   // whatever open pocket exists, so pockets must not exist.
    spawnClearRadius: 320,         // no rocks near either spawn
    edgeMargin: 130,
    countRefArea: 44800000,        // 8000x5600: the area the rock COUNT knobs above were tuned
                                   // for. generateTerrain scales cluster/sparse counts by
                                   // (arena.w*arena.h)/this, so viewport-shaped arenas (the app
                                   // sizes arena aspect to the window) keep the same density feel
    spawnDistFrac: 0.40,           // spawn separation as fraction of arena diagonal-ish
    // --- THE TITAN: every seed gets exactly one colossal asteroid, ~5x a BIG one.
    //     It is the map's landmark, its widest LOS shadow, and its deepest well.
    titanRadius: [1035, 1440],     // ~5x a BIG, trimmed 10%
    titanSpawnClear: 1400,         // extra clearance from fleet spawns (was 760: fleets
                                   // spawned inside the titan's accretion infall stream and
                                   // lost half their lights to debris before first contact)
    // --- BIG asteroids: every generated seed also gets 1..bigCountMax of them. They
    //     are placed after the titan but before everything else (clusters and sparse
    //     rocks flow around them) and anchor the mid-scale map layout.
    bigRadius: [220, 380],
    bigCountMax: 3,                // roll is uniform 1..bigCountMax — never zero
    bigSeparation: 1.35,           // min centre distance between bigs = (r1+r2)*this
                                   // (pairs with the titan relax to 1.1 — see generateTerrain)
    bigSpawnClear: 520,            // extra clearance from fleet spawns (their wells reach far)
  },
  asteroidHP: 80,                  // rocks crack fast — demolition is a tactic
  asteroidHPRefRadius: 60,         // HP scales with (r/ref)^2

  // --- DEBRIS PHYSICS ---
  debris: {
    fragmentCount: 4,
    minChildRadius: 15,            // children below this vanish -> cascade terminates
    childRadiusScale: 0.52,        // child r = parent r * scale (area ~ 1/4 per generation)
    burstSpeed: 85,                // launch impulse magnitude
    speedScale: 1.0,
    drag: 0.35,                    // per-second velocity damping -> debris drifts, then lingers
    restitution: 0.35,
    impactDamageScale: 0.05,       // ship damage = scale * (r^2/1000) * closingSpeed
    impactDamageCap: 12,           // per-strike ceiling: gravity infall streams pelt hulls
                                   // with r80+ rocks at terminal speed — a strike hurts any
                                   // light badly (hp 14-26) but never one-shots from full
    impactMinSpeed: 25,            // below this a fragment just rests against the hull
    maxAsteroids: 500,             // hard cap on live rocks (cascade safety)
    spinMax: 1.4,                  // rad/s visual tumble for flung fragments
    idleSpin: 0.08,                // rad/s cap on the id-derived tumble small rocks are born
                                   // with (space: rotation never decays to a standstill —
                                   // the field is visibly alive even far outside the wells).
                                   // Deterministic from o.id, no RNG draw (seed streams and
                                   // scenario layouts are untouched)
    idleSpinMaxRadius: 60,         // only rocks under this tumble idly: bigger rocks anchor
                                   // accretion piles, and a rotating contour under a settled
                                   // pile would excavate it (settled pairs are never
                                   // collision-resolved)
  },

  // --- GRAVITY (massive rocks bend everything; cinematic constant, not Kepler) ---
  // accel toward a source rock = G * r^3 / (d^2 + (softening*r)^2), clamped to maxAccel.
  // UNIVERSAL: the 1/d^2 tail is never zeroed inside the arena — every FREE body (ships,
  // drifting rocks, torpedoes, bombs) feels every source everywhere, however subtly; the
  // old wellReach hard fade is gone from the physics and survives only as the AI/render
  // "strong well" boundary. SETTLED rocks are the exception (anti-thrash): they can only
  // WAKE inside a source's reach where the pull beats rockWake; the far tail never stirs
  // parked terrain. Equal-mass sources wake each other (mutual attraction — two drifting
  // monsters fall together); only a strictly-larger body is immune to its lessers, so the
  // titan stays the map's anchor. Small debris additionally attracts NEARBY debris
  // (movers only — the Saturn-ring accretion pass; see debris* knobs).
  gravity: {
    G: 0.045,                      // the one true knob (UI slider scales it live)
    softening: 0.5,                // core softening length as a fraction of source radius
    sourceMinRadius: 100,          // rocks smaller than this pull too weakly for the GLOBAL
                                   // field (they still join the local debris-accretion pass)
    minAccel: 0.25,                // legacy near-well floor (kept for compat; the physics
                                   // cutoff is farMinAccel below)
    farMinAccel: 0.02,             // px/s^2 perf cutoff for the universal tail: per-source
                                   // horizon d = sqrt(G*m/this). The titan's horizon (~62k px)
                                   // exceeds any arena — effectively no cutoff; a BIG's
                                   // (~5k px) self-limits. One branch cheaper than the old taper
    maxAccel: 45,                  // clamp: no singularity slingshots at point-blank
    wellReach: 2.7,                // "strong well" boundary = source r * this. NO LONGER a
                                   // physics bound: AI routing (bbSkirtWell), the rendered
                                   // well-edge ring, and the settled-rock WAKE gate use it
    rockWakeShell: 650,            // TERRAIN PLACEMENT ONLY: generateTerrain keeps BIGs this
                                   // far outside the titan's surface so they anchor the layout
                                   // instead of spawning mid-fall. (Its old wake-gating role
                                   // is gone: rocks wake ANYWHERE in a well — see rockWake)
    rockWake: 1.2,                 // settled rock starts sliding above this pull, INSIDE a
                                   // source's reach (far-tail pull never wakes terrain).
                                   // The floor is drag-derived: below ~0.7 px/s^2
                                   // terminal creep (a/drag) sits under the 2 px/s settle
                                   // threshold and a woken rock just re-settles (thrash).
                                   // 1.2 = the rendered "well's edge" ring: what you see
                                   // creeping is what creeps
    debrisAccretionRadius: 240,    // local mutual-gravity range between a MOVING sub-source
                                   // rock and its neighbors (grid-bounded; Saturn-ring pass)
    debrisNeighborCap: 6,          // pairwise partners per mover per tick (perf bound)
    debrisMult: 1.0,               // feel multiplier for the debris-accretion pass; a settled
                                   // neighbor is dislodged only when the mover's pull beats
                                   // rockWake (same thrash-safe floor as the wells)
    shipEscapeCap: 0.65,           // pull on a ship never exceeds this fraction of its own
                                   // max thrust accel — wells threaten, they never imprison
                                   // (also what keeps the x3 slider playable)
    shipMult: 1.0,                 // per-family feel multipliers
    rockMult: 1.0,
    projectileMult: 1.0,           // torpedoes/bombs bend course but keep their speed.
                                   // Deflection is naturally ~ g/v^2: a 420px/s torpedo
                                   // curls visibly, a 2400px/s rail slug barely at all
                                   // (heavy-rail slugs skip gravity entirely — see updateHeavySlugs)
    slugBendVisual: 40,            // RENDER-ONLY: DESTROYER rail traces draw with their
                                   // (physically ~1px) gravitational sagitta boosted this
                                   // many times, so the speed hierarchy reads on screen. Hit
                                   // resolution is untouched — that slug is still hitscan
  },

  // --- SHIPS (accel = thrust/mass; angular accel = rcs/mass, capped at turnMax rad/s) ---
  ships: {
    destroyer:   { cost: 6, hp: 140, mass: 100, thrust: 1600, rcs:  90, turnMax: 0.6,
                   maxCruiseSpeed: 55,  evasion: 0.05, radius: 26, pdSlots: 2,
                   signature: 2800, avoidMult: 2.2 },   // that engine plume lights up the sky
    frigate:     { cost: 3, hp:  90, mass:  55, thrust: 2300, rcs: 260, turnMax: 1.6,
                   maxCruiseSpeed: 85,  evasion: 0.15, radius: 16, pdSlots: 3,
                   signature: 1700, avoidMult: 1.6 },
    bomber:      { cost: 2, hp:  26, mass:  20, thrust: 2300, rcs: 380, turnMax: 3.2,
                   maxCruiseSpeed: 125, evasion: 0.65, radius: 10, pdSlots: 0,
                   signature: 900,  avoidMult: 1.0 },
    interceptor: { cost: 1, hp:  14, mass:  12, thrust: 2000, rcs: 520, turnMax: 4.2,
                   maxCruiseSpeed: 150, evasion: 0.80, radius: 8,  pdSlots: 0,
                   signature: 750,  avoidMult: 1.0 },
    // BATTLESHIP: ponderous turreted gun platform. Two fit the 42-pt budget. Deliberately
    // fragile to bombs (radius-78 hull is fully in AOE, see applyAoe) but a 5+ bomber wave
    // immolates itself on the durable hull — a pair fails, a squad gets it. Turns/moves like
    // a barn; holds the line, never chases. Huge signature 4200 lights up the sky (largest in
    // the game) so its 3200-range guns get the detection-lit targets they need. See design §1/§5.
    battleship:  { cost: 15, hp: 620, mass: 900, thrust: 6000, rcs: 700, turnMax: 0.14,
                   maxCruiseSpeed: 28,  evasion: 0.02, radius: 78, pdSlots: 4,
                   signature: 4200, avoidMult: 3.0 },
  },

  // --- RAILGUN (Destroyer; fixed-forward, min range, near-hitscan, anti-capital only) ---
  railgun: {
    arc: 0.10,                     // rad, total width of firing cone about heading
    minRange: 150,                 // DEAD ZONE: target closer than this cannot be fired on
    maxRange: 700,
    damage: 30,
    cooldown: 4.0,
    slugSpeed: 2400,               // near-hitscan; resolved as instant ray (cannot be intercepted)
    rockDamageMult: 2.2,           // a kinetic slug SHATTERS rock — cover demolition weapon
    evasionMult: 1.0,              // hit chance = 1 - evasion: int .20, bmb .35, frig .85, dest .95
  },

  // --- HEAVY RAILGUN (Battleship; 3 independent turrets, staggered, ponderous slew) ---
  // Detection-bounded artillery: maxRange 3200 is far past railgun 700, but ALL targeting is
  // detection-gated, so at long range only LIT-UP (burning/spotted) ships can be hit. Hard
  // class gate never lets a turret touch a bomber/interceptor; a speed gate makes a frigate at
  // cruise (85) untouchable and a slowed one very hittable. See design §2/§3.
  // PROJECTILE weapon: each shot is a REAL flying slug (state.slugs, launched along the barrel
  // after a visible load cycle). Slugs PIERCE trackable-class hulls (one hit roll per hull, the
  // slug flies on either way) and are STOPPED DEAD by asteroids. WWII turret arcs: the bow pair
  // is blind astern, the aft turret blind over the bow; full broadside only on the beam.
  heavyRail: {
    turrets: 3,                    // three big-gun turrets, independent state (renderer draws them)
    turretMounts: [0.90, 0.30, -0.55], // turret centre as a fraction of def.radius along the spine
                                   // (bow->aft; index 0 = bow). Slugs launch from here.
    damage: 30,                    // identical to the destroyer railgun (requirement)
    cooldown: 6.25,                // PER TURRET reload (was 5.0; -20% rate of fire per player tuning)
    minRange: 220,                 // DEAD ZONE (radius 78 + margin; big guns can't depress point-blank)
    maxRange: 3200,                // HUGE; detection-bounded (coasting dest visible 1680, burning 3360)
    slugSpeed: 2400,               // px/s of the REAL flying slug (crosses 1400 px in ~0.6 s)
    slewRate: 0.35,                // rad/s per-turret rotation (~20 deg/s; WWII-ponderous, renderer animates t.ang)
    aimTolerance: 0.06,            // rad; a turret may fire only within this of the target bearing
    loadTime: 1.2,                 // s a turret must HOLD a valid solution while the rails charge
                                   // before the slug releases (the visible loading phase; t.load)
    loadGrace: 0.25,               // solution lost longer than this dumps the charge (t.load -> 0);
                                   // a one-tick nav yaw on a burning hull must not dump a full charge
    arcCenters: [0, 0, Math.PI],   // per-turret firing-arc centre, HULL-RELATIVE: bow pair forward,
                                   // aft turret astern (matches turretMounts order)
    arcHalfWidth: 1.62,            // rad (~93 deg): bow turrets cover bow-to-just-past-the-beam, the
                                   // aft turret covers the stern half — every bearing is coverable
                                   // (union is gapless for >= pi/2) but a BOW gun can no longer take
                                   // solutions deep into the rear quarter, which read on screen as
                                   // "the battleship fires backwards" (was 2.36 rad = 135 deg)
    rockDamageMult: 2.2,           // == railgun; blasts asteroids out of transit lanes
    evasionMult: 1.0,              // base hit = 1 - evasion*this (dest .98, frig .85 baseline)
    trackClasses: ['destroyer', 'frigate', 'battleship'], // HARD gate: never bomber/interceptor
    speedFullTrack: 55,            // target speed <= this: no penalty (factor 1)
    speedNoTrack: 85,              // target speed >= this: untrackable (frigate cruise 85 -> factor 0)
    standoffFrac: 0.85,            // aiBattleship holds at maxRange*this (~2720) when it HAS a firing
                                   //   solution far out — the huge-range delete. Engagement-aware
                                   //   below it: never retreat out of a shot it already has.
    standoffMin: 1500,             // when it CAN'T fire (lane blocked / target out of reach), close to
                                   //   ~this range to regain LOS+detection and blast the plugging rock,
                                   //   instead of parking at extreme range with an idle gun. Stays well
                                   //   outside destroyer railgun 700 / frigate torpedo 950.
    event: 'hrail',                // event kind for the renderer (unknown kinds are contract-safe)
  },

  // --- TORPEDOES (Frigate specialist; Destroyer weak afterthought; Interceptor single-use) ---
  torpedo: {
    speed: 420,                    // far slower than slug; PD gets a shot or two in transit
    turnRate: 1.1,                 // rad/s — LOOSE tracking
    damage: 24,
    aoeRadius: 45,                 // detonation on PD interception: small AOE
    aoeDamage: 8,
    rockDamageMult: 2.5,           // warheads crack asteroids open — the other demolition weapon
    lifetime: 9,                   // GUIDANCE FUEL seconds: burnout -> spent (ballistic coast),
                                   // NOT despawn. A torpedo only dies on impact or arena exit.
    hitRadius: 14,                 // terminal proximity (added to target radius)
    lockLossSeconds: 0.5,          // LOS blocked longer than this -> lock lost, flies dumb.
                                   // Short: ducking behind a rock actually sheds the torpedo (dense-map counterplay)
    predictSpeed: 70,              // target slower than this is 'predictable'
    armDistance: 260,              // REAL minimum range: a torpedo that has not flown this far
                                   // is a dud — no terminal roll, no AOE, no rock damage
    lobMinRange: 300,              // AI discipline on top: don't waste shots barely past arming
    jinkAccelThreshold: 55,        // lateral-accel EMA above this (and fast) = 'jinking'
    steadyEvasionMult: 0.15,       // predictable target: hit = 1 - evasion*this  (steady bomber ~.90)
    jinkEvasionMult: 1.25,         // jinking target:     hit = 1 - evasion*this  (interceptor ~0)
    frigate:     { range: 950, cooldown: 12.0, salvo: 2, salvoGap: 0.4 },  // was 6.0: halved fire rate — 14 frigates at 42pts deleted everything
    destroyer:   { range: 650, cooldown: 11.0, salvo: 1, salvoGap: 0 },
    interceptor: { range: 750, ammo: 1 },
  },

  // --- BOMBS (Bomber; straight-line AOE, no tracking, lead-aimed, self-risk) ---
  bomb: {
    speed: 430,                    // fast: sluggish capital cannot drift off-aim during flight
    maxFlight: 1.6,                // seconds; then fizzles
    launchRange: 380,              // release distance — inside the PD reaction envelope
                                   // (bombs fired closer than ~350 arrive before tracking completes)
    runStartFactor: 1.6,           // go steady once LOS is clear within launchRange*this.
                                   // Open maps: sighted early -> LONG steady run -> torpedo bait.
                                   // Dense maps: sighted late -> short pop-out run. The flip lives here.
    standoff: 300,                 // O1 default: medium standoff, artillery-leaning
    damage: 28,
    aoeRadius: 60,
    salvo: 3,                      // bombs per trigger pull — a slot-cap-breaking spike
    salvoSpread: 0.13,             // rad of fan — wide enough that only tight/point-blank
                                   // clusters chain when PD picks one off
    cooldown: 2.5,                 // per salvo
    armTime: 0.20,                 // won't detonate on its own bomber at the muzzle
    jinkAimSpread: 0.9,            // rad of aim error at full jink — jinking bombers throw wide
  },

  // --- INTERCEPTOR GATLING (forward, short; kills lights, small capital chip) ---
  gatling: {
    range: 95,                     // MUST stay << pd.range (deep exposed strafe — load-bearing, §5)
    arc: 0.6,
    fireRate: 8,                   // shots/s
    damagePerShot: 2,
    capitalMult: 0.4,              // chip vs capitals — MODEST by design (§5 flip-flatten watch):
                                   // worth the dive, but the bomber is the capital-killer
    evasionMult: 0.85,             // hit = 1 - evasion*this
  },

  // --- POINT DEFENCE (Destroyer 2 slots / Frigate 3; throughput-capped; MUST stay weak) ---
  pd: {
    range: 140,
    trackRange: 700,               // PD starts tracking (LOS required) out here
    reactionSeconds: 0.5,          // continuous track needed before it may engage: pop-outs
                                   // from cover beat the reaction; open-field targets are
                                   // tracked long before they arrive (the terrain gate)
    shotsPerSecond: 3.0,           // per slot
    projectileKillChance: 0.7,     // per shot vs a TRACKED torpedo/bomb — reaction gating
                                   // is the weakness now: untracked pop-outs leak entirely (§5)
    shipDamagePerShot: 2.5,        // lethal to TRACKED lingerers; untracked divers get grace
    shipEvasionMult: 0.65,         // hit vs ships = 1 - evasion*this; punishes lingerers
                                   // when slots are free without shutting the strafe game down
  },

  // --- COLLISIONS (solid rocks; ships bonk, don't pass) ---
  collision: {
    shipRockRestitution: 0.35,
    shipRockDamageScale: 0.02,     // dmg = scale * closingSpeed * (mass/50), capped
    shipRockDamageCap: 10,
    shipShipRestitution: 0.3,
    rockAnchorRadius: 500,         // rocks this big are NEVER dislodged by a collision —
                                   // they are the map's gravity anchors (the titan); only
                                   // demolition removes them. BIGs stay dislodgeable.
    shipRockMinImpactSpeed: 30,    // closing speed below this = contact push only, no damage
                                   // (universal gravity keeps fields creeping at 3-25 px/s;
                                   // creep nudges hulls aside — it must not sandpaper them)
    pushableRockRadius: 55,        // ships shove settled rocks smaller than this aside —
                                   // gravity accretes pebble shells around parked fleets,
                                   // and an immovable shell entombs a capital alive
  },

  // --- ROLE AI ---
  ai: {
    retargetSeconds: 0.25,
    destroyerStandoff: 600,        // hold near (under) railgun max range
    destroyerRetreatRange: 260,    // back off from lights closer than this
    frigateStandoff: 780,          // outside enemy railgun reach, inside own torpedo range
    escortRange: 220,              // frigate keeps PD umbrella near its destroyer
    bomberBreakRange: 310,         // end run / turn away inside this (turnaround stays out of PD reach)
    bomberRegroupRange: 620,       // retreat out to here after a run
    strafeDivePoint: 60,           // dive aim = this far past the target
    strafeExitRange: 330,          // strafe exit distance (out of PD bubble)
    jinkAccel: 0.9,                // fraction of max accel devoted to jinking
    jinkPeriod: 1.1,               // seconds per jink flip
    threatJinkRange: 520,          // lights jink when enemies/torpedoes within this
    rockShootSeconds: 1.2,         // capitals blast a blocking rock after this long with no clean shot
    rockTorpCooldown: 7,           // frigate cadence for torpedoing cover
    flankChance: 0.5,              // odds a wave flanks (split evenly left/right)
    flankOffset: 620,              // how wide the hook swings
    flankDone: 240,                // close enough to the hook point -> turn in
    // commit waves: lights stage, then attack TOGETHER (saturation is strategic — §5)
    stageRange: 640,               // lights hold here between waves
    commitRadius: 780,             // staged = within this of the wave target
    commitMinLights: 3,            // staged lights needed to trigger a wave (capped at living lights)
    commitSeconds: 18,             // wave duration (long enough to cross dense fields AND give
                                   //   the trailing staggered squadron time-on-target; was 16)
    commitStaggerSeconds: 2.0,     // squadron k enters the wave k*this after window-open
    diveSlotStaggerSeconds: 0.35,  // within a squad, slot s dives s*this after its squadron's beat
    torpSenseRange: 650,           // a light jinks at a tracking torpedo within this AND in LOS
                                   //   (was hardcoded 650 with no LOS gate — pre-cognitive jinks)
    commitCooldown: 3,             // regroup time between waves
    coverMinRange: 220,            // never stage inside the enemy PD bubble
    coverHoldTicks: 30,            // how long a chosen cover point is held before rescoring
    navArriveSlack: 14,
    navBrakeMargin: 0.72,          // use this fraction of max accel when planning braking
    avoidLookahead: 2.2,           // seconds of velocity lookahead for rock avoidance
    avoidMargin: 30,               // extra clearance around rocks
    bigRockAvoidPad: 70,           // extra clearance around gravity sources: the well
                                   // drags ships into the rim while they round it
    hugeRockRadius: 500,           // rocks this big get RIM-FOLLOWED (tangent-bug):
                                   // a perpendicular detour nudge never clears a
                                   // titan's shadow and ships parked at its rim
                                   // otherwise dive back into the well forever
    bbWellSkirtFrac: 1.9,          // BATTLESHIP-ONLY well skirt: the uniquely slow BB (turnMax 0.14,
                                   // ~22s flip) is dragged into a BIG asteroid's well (r*wellReach) and
                                   // grinds to death before it can burn clear — the design's flagged
                                   // entombment, and the dominant real-match killer. In its AUTO role AI
                                   // only (ordered transit untouched), aiBattleship routes its nav goal
                                   // around any BIG (sub-hugeRockRadius) well at this fraction of the
                                   // source radius, keeping the ponderous hull out of the killing core.

    // --- field-density detour (routeAround extension; AUTO/hunt path only, capitals) ---
    clutterDetourThreshold: 220,   // straight-corridor clutter (Sum of radii of 55..200px rocks in
                                   //   the inflated lane) above which a detour is considered.
                                   //   measured: lone r90 rock ~90, compact well core ~320, wall ~365
    clutterDetourGain:      0.6,   // a flank corridor must be <= this * straight clutter to qualify
    clutterDetourOffsetCost: 0.3,  // clutter-units charged per px of lateral detour in the score:
                                   //   makes compact fields detour (700px offset wins) but tall walls
                                   //   transit+clear (every offset scores worse than going straight)
    clutterDetourOffsets: [700, 1000, 1300, 1600], // candidate lateral flank offsets, scored each side
    clutterRefreshTicks:    20,    // recompute detour every N ticks, staggered by ship.id (cache between)

    // --- lane-clearing fire (clearTransitLane; auto hunt + ordered transit) ---
    rockClearMinGoalDist:  500,    // only clear when the nav goal is farther than this (real transit)
    rockClearLookahead:    900,    // corridor length searched ahead for a blocker (capped by weapon range)
    rockClearMaxRadius:    200,    // never shatter rocks bigger than this: debris too dangerous / futile,
                                   //   these are routed AROUND. (min radius reuses pushableRockRadius 55)
    rockClearDebrisPad:    280,    // DEBRIS SAFETY: require dist(ship,rock) >= def.radius + rock.r + this,
                                   //   so fragments (peak ~110px/s, drag 0.35, dangerous over ~244px) settle
                                   //   below impactMinSpeed(25) before reaching the shooter. Scaled per
                                   //   hull via def.radius; verified 0.00 self rock-damage in scenario runs.

    // --- coordinated fleet AI (the "smart" layer; sweepable per team for self-play A/B) ---
    smartTeams: 'AB',              // which teams run the coordinated layer: '', 'A', 'B', 'AB'.
                                   //   Gates focus fire, capital pincers, escort rings, ambushes,
                                   //   and the destroyer gun-discipline governor — NOT the squadron
                                   //   spacing (squadron.enabledTeams gates that separately)
    focusRetargetSeconds: 1.0,     // fleet focus-target rescore cadence
    capitalPincerBearing: 0.55,    // rad: sibling capitals approach a shared target on offset
                                   //   bearings instead of single file — crossfire + LOS diversity
    escortRingSpread: 0.85,        // rad between frigate escort slots around their destroyer
                                   //   (they used to all compute the SAME escort point and stack)
    ambushLingerSeconds: 9,        // hunting capital waits this long in a LOS-shadow ambush
    ambushCooldown: 14,            // per-ship pause between ambush episodes (bounds stalemate risk)
    ambushContactMax: 2200,        // only ambush when the stale contact is within this range —
                                   //   the enemy is coming; across the map you march, not lurk
  },

  // --- SQUADRON (fighter swarm coordinator: spacing, spread attack lanes, loose formation) ---
  squadron: {
    enabledTeams: 'AB',            // which teams' lights use the coordinator ('', 'A', 'B', 'AB')
    size: 5,                       // max members per squadron (per team, per class, id-ordered)
    bomberSep: 150,                // 2*bomb.aoeRadius(60)+30 — applies to ANY pair involving a
                                   //   bomber (was bomber-bomber only at 135, which let
                                   //   interceptors sit 82px inside the sympathetic-chain annulus)
    lightSep: 82,                  // baseline spacing between any two friendly lights
    sepGain: 1.1,                  // nav-goal displacement per px of spacing intrusion
    runSepGain: 0.9,               // near-full separation while bombs are live (was 0.5: weakest
                                   //   spacing at the most dangerous moment; the frozen run lanes
                                   //   mean squadmates rarely intrude, so the run vector survives)
    runLaneSep: 150,               // parallel bomb-run lane spacing (frozen frame, per slot)
    sectorSpread: 0.9,             // rad between squadron approach SECTORS on a shared focus
                                   //   target — cross-squadron deconfliction (bombers centre,
                                   //   interceptor squads +-0.9 rad)
    slotChord: 150,                // min CHORD between adjacent spreadPoint lanes at any standDist
                                   //   (binds only close-in, where the old angular fan collapsed)
    duckSlotSpread: 0.35,          // rad of per-slot spread along a shared duck rock's shadow arc
    bombAvoidMargin: 40,           // live-bomb keep-out = aoeRadius + hull radius + this
    bombPushGain: 1.4,             // strength of the live friendly-bomb repulsion term
    bearingSpread: 0.5,            // rad between adjacent members' approach lanes on a shared
                                   //   target — separated lanes = no cross-bomber sympathetic chain
    formGain: 0.3,                 // weak pull toward the transit-formation slot; formation must
                                   //   NEVER fight pathing (autopilot avoidance dominates)
    reformTicks: 45,               // membership refresh cadence (death/merge reshuffle)
  },

  // --- ADMIRAL (fleet command layer: posture, axis, task org, search; A/B-sweepable).
  //     Deterministic pure-state math, zero RNG draws. Pinned ships and ships under player
  //     orders are exempt (contract). Read via admiralTeam(), like smartTeams/squadron. ---
  admiral: {
    enabledTeams: 'AB',            // which teams get an admiral ('', 'A', 'B', 'AB')
    cadenceTicks: 30,              // command pass every 0.5s (tick phase 2, offset from detection's 1)
    postureMinSeconds: 2.0,        // min dwell before search/advance/strike may flip (anti-dither)
    scoutCount: 2,                 // interceptors detached to probe ahead
    scoutSpread: 900,              // lateral separation between scout probe lanes
    scoutHoldRange: 900,           // scouts shadow a detected enemy from here, never press home
    screenDist: 700,               // picket line this far ahead of the main body on the axis
    screenSpread: 340,             // lateral spacing between screen slots
    capitalLineSpacing: 520,       // line-abreast offset between capitals during search/advance
    advanceSpeedFrac: 0.9,         // body speed = slowest own capital cruise * this (0 capitals = off)
    strikeRange: 3400,             // detected enemy within this of the main body -> posture 'strike'
    reserveSquads: 1,              // interceptor squadrons held at the rally during a strike
    reserveMinSquads: 3,           // fewer light squadrons than this -> no reserve at all
    reserveReleaseFrac: 0.5,       // release when lights drop below this frac of strike-entry count
    rallyBehind: 900,              // rally point distance behind the main body along the axis
    rallySpread: 170,              // lateral spacing between rally slots (> bomberSep)
    withdrawOwnFrac: 0.35,         // fleet value fraction that triggers a bounded withdraw
    withdrawSeconds: 20,           // withdraw duration; then forced re-advance (never flees forever)
    withdrawLatestFrac: 0.7,       // no withdrawals after this fraction of the match timer
    ghostMaxAge: 30,               // s a lastContact ghost stays navigable (hunt tier T2)
    searchRingStep: 1100,          // expanding-sweep ring spacing around the last-seen anchor
    searchRingBearings: 6,         // waypoints per ring
    searchRings: 2,                // rings before falling back to the spawn/centre landmark cycle
                                   // (short: after ~12 local waypoints the landmark cycle forces
                                   // both fleets through the centre — re-contact beats coverage)
    searchWptRadius: 650,          // any own ship this close -> next waypoint
    searchWptTimeout: 22,          // s before an unreachable waypoint is abandoned (also the T4 slice)
  },

  // --- FLEETS (42-point budget; costs: destroyer 6 / frigate 3 / bomber 2 / interceptor 1) ---
  fleetPoints: 42,
  presets: {
    RAILGUN:  ['destroyer', 'destroyer', 'destroyer', 'destroyer',
               'frigate', 'frigate', 'frigate', 'frigate', 'frigate', 'frigate'],          // 4D+6F = 42
    SWARM:    ['bomber', 'bomber', 'bomber', 'bomber', 'bomber',
               'bomber', 'bomber', 'bomber', 'bomber', 'bomber',
               'interceptor', 'interceptor', 'interceptor', 'interceptor', 'interceptor',
               'interceptor', 'interceptor', 'interceptor', 'interceptor', 'interceptor',
               'interceptor', 'interceptor', 'interceptor', 'interceptor', 'interceptor',
               'interceptor', 'interceptor', 'interceptor', 'interceptor', 'interceptor',
               'interceptor', 'interceptor'],                                              // 10B+22I = 42
    BALANCED: ['destroyer', 'destroyer', 'frigate', 'frigate', 'frigate', 'frigate',
               'bomber', 'bomber', 'bomber', 'bomber', 'bomber',
               'interceptor', 'interceptor', 'interceptor', 'interceptor',
               'interceptor', 'interceptor', 'interceptor', 'interceptor'],                // 2D+4F+5B+8I = 42
  },
  spawnFormationGap: 70,
  spawnRankSize: 8,                // ships per rank; big fleets deploy in a block, not a wall
};
