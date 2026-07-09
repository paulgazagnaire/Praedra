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
    spawnDistFrac: 0.40,           // spawn separation as fraction of arena diagonal-ish
    // --- THE TITAN: every seed gets exactly one colossal asteroid, ~5x a BIG one.
    //     It is the map's landmark, its widest LOS shadow, and its deepest well.
    titanRadius: [1035, 1440],     // ~5x a BIG, trimmed 10%
    titanSpawnClear: 760,          // extra clearance from fleet spawns (well reaches far)
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
    impactMinSpeed: 25,            // below this a fragment just rests against the hull
    maxAsteroids: 500,             // hard cap on live rocks (cascade safety)
    spinMax: 1.4,                  // rad/s visual tumble for flung fragments
  },

  // --- GRAVITY (massive rocks bend everything; cinematic constant, not Kepler) ---
  // accel toward a source rock = G * r^3 / (d^2 + (softening*r)^2), clamped to maxAccel.
  // Mass goes with r^3 so surface pull scales with radius: only the BIG asteroids (and
  // their first-generation fragments) matter — a pebble's well is beneath minAccel.
  // Ships, drifting rocks, torpedoes and bombs all feel it; EVERYTHING inside a well
  // responds — a settled rock anywhere in a well wakes once the pull beats rockWake
  // (no distance shell). Only the sub-rockWake fringe (where creep would stall against
  // drag and re-settle) and down-well-supported piles hold still.
  gravity: {
    G: 0.045,                      // the one true knob (UI slider scales it live)
    softening: 0.5,                // core softening length as a fraction of source radius
    sourceMinRadius: 100,          // rocks smaller than this pull too weakly to compute
    minAccel: 0.25,                // px/s^2 cutoff: beyond this the well ends (perf + sanity)
    maxAccel: 45,                  // clamp: no singularity slingshots at point-blank
    wellReach: 2.7,                // absolute well radius = source r * this; the pull fades
                                   // to zero across the outer 15%. Without this bound the
                                   // titan's r^3 mass would drag the ENTIRE map into itself
    rockWakeShell: 650,            // TERRAIN PLACEMENT ONLY: generateTerrain keeps BIGs this
                                   // far outside the titan's surface so they anchor the layout
                                   // instead of spawning mid-fall. (Its old wake-gating role
                                   // is gone: rocks wake ANYWHERE in a well — see rockWake)
    rockWake: 1.2,                 // settled rock starts sliding above this pull, anywhere in
                                   // the well. The floor is drag-derived: below ~0.7 px/s^2
                                   // terminal creep (a/drag) sits under the 2 px/s settle
                                   // threshold and a woken rock just re-settles (thrash).
                                   // 1.2 = the rendered "well's edge" ring: what you see
                                   // creeping is what creeps
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
    cooldown: 5.0,                 // PER TURRET reload; with loadTime + stagger, ~1 slug / 2.1 s ship-wide
    minRange: 420,                 // DEAD ZONE: big guns can't depress point-blank. Deliberately wide —
                                   //   PD ship-fire only reaches ~pd.range+target radius (~156 from centre),
                                   //   so 156..420 is a genuine knife-fight ring where a small ship that
                                   //   survives the approach gets a REAL shot at killing the battleship
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
    arcHalfWidth: 2.36,            // rad (~135 deg): bow turrets blind astern, aft turret blind over
                                   // the bow, all three bear on either beam (WWII arrangement)
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
    lifetime: 9,
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
    frigate:     { range: 950, cooldown: 6.0, salvo: 2, salvoGap: 0.4 },
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
    pushableRockRadius: 55,        // ships shove settled rocks smaller than this aside —
                                   // gravity accretes pebble shells around parked fleets,
                                   // and an immovable shell entombs a capital alive
  },

  // --- DOCTRINE (per-team AI level: 'v1' = legacy greedy AI, 'v2' = veteran doctrine) ---
  // v2 layers researched combat doctrine on top of v1's mechanics: coordinated focus fire
  // (Lanchester concentration), defeat-in-detail wave targeting, wolfpack dead-zone dives,
  // synchronized torpedo volleys (PD saturation), split-axis anvil strikes, emission-control
  // approaches, masked (cover-hopping) approaches, battleship broadside discipline and the
  // min-gap field gate. Selectable per team so batteries can measure v2 vs v1 head-to-head.
  doctrine: { A: 'v2', B: 'v2' },
  ai2: {
    focusEvery: 15,              // ticks between doctrine (team-picture) passes
    isolationRadius: 900,        // mutual-support radius: enemies with fewer allies inside this
                                 //   are ISOLATED -> preferred wave/focus targets (defeat in detail)
    packMinFrigates: 2,          // wolfpack: frigates needed on one victim before a dead-zone dive
    packDiveRange: 300,          // point-blank orbit radius: outside PD ship-fire (~156 from a BB
                                 //   centre), inside heavyRail.minRange 420 -> main-battery-proof
    packBearingSpread: 1.0,      // rad between packmates' attack bearings (anvil the PD arcs)
    packMaxHalfArc: 0.9,         // hard cap on the fan's half-width: keeps every slot's straight
                                 //   approach chord clear of the victim's hull (95px near-miss
                                 //   at the old uncapped fan, review-confirmed)
    packLeash: 3000,             // frigates farther than this from the victim stay on their
                                 //   current job instead of being yanked across the map
    packEscortMax: 1,            // dive only when <= this many enemy capitals guard the victim
                                 //   within isolationRadius (never knife-fight a full battle line)
    volleyWaitMax: 0.4,          // max s a ready torpedo boat holds for a synchronized volley.
                                 //   Twice measured DOWN (3.0 -> 1.2 -> 0.4): holds cost launch
                                 //   volume and same-bearing sync never beat slot-capped PD
    emconNear: 1.05,             // burn-and-coast band: cut the plume when the nearest enemy sits
    emconFar: 2.2,               //   between visRange*near and visRange*far of us (approach unseen)
    anvilMinBombers: 4,          // commit waves split into two attack axes at this many bombers
    gunlineSupport: 700,         // a v2 destroyer beyond standoff+350 with NO sister destroyer
                                 //   within this range falls back to assemble before pressing
                                 //   (never fight the enemy gunline alone — Lanchester)
    waveAssembleFrac: 0.45,      // fraction of the light wing that must be staged before a v2
                                 //   wave commits (time-on-target pulses, not 3-ship trickles).
                                 //   Only bites vs PD-capital enemies (teamCommitting is always
                                 //   true against pure-light fleets)
    screenBomberRange: 900,      // interceptors guard own bombers from enemy lights inside this
    diveNeedsSaturation: true,   // interceptors enter a PD bubble only alongside live ordnance
    bbBroadside: true,           // battleship turns beam-on when holding a firing solution (3 turrets
                                 //   bear on the beam vs 2 over the bow — WWII battle-line discipline)
    bbMinGap: 300,               // narrowest opening (surface-to-surface) the BB will ever thread;
                                 //   tighter pinches are detoured or DEMOLISHED, never entered
    bbDemolishMaxR: 240,         // BB turrets may blast rocks up to this radius to make room
                                 //   (v1 capitals stop at ai.rockClearMaxRadius 200). Was 320:
                                 //   an r-320 rock splits into r-166 children whose drifting mass
                                 //   shredded the shooter — measured 2x battleship rock-deaths
    bbDebrisRPad: 0.8,           // extra demolition standoff per px of target-rock radius: bigger
                                 //   rocks throw bigger (deadlier at any speed) fragments
    bbGapLookahead: 1100,        // corridor length scanned ahead for pinch points
    bbDetourMaxClutter: 240,     // flank-corridor clutter above which detouring a pinch is
                                 //   hopeless (it's a WALL) and the guns make room instead
    focusHpWeight: 600,          // focus scoring: px-equivalent bonus per missing hp fraction
                                 //   (finish wounded targets first — no overkill, no half-kills)
    focusIsolationWeight: 450,   // px-equivalent bonus per missing supporting ally
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
    commitSeconds: 16,             // wave duration (long enough to cross dense fields)
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

