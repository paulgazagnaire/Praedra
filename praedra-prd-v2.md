# Praedra — PRD v2 (Physics Rebuild)

Browser-based tactical space combat. 2D, top-down, Newtonian physics, "The Expanse" feel. Rebuilt from scratch. Constants are *starting values chosen to produce the relationships in §5* — tune by feel, but don't "fix" them into something that breaks those relationships.

> **What changed from v1:** movement is now **full Newtonian physics by default** (not an optional toggle); the roster is renamed and re-mechaniced (Destroyer / Frigate / Bomber / Interceptor); asteroids are **solid bodies** (ships collide, can't pass through) that **destruct and split into smaller rocks with realistic debris**. Crucially: **in the prior build the core hypothesis failed — the "flip" measured +0 (the railgun composition won on both sparse and dense maps).** Making the new roster + physics actually *produce* the flip, and validating it before building anything else, is this rebuild's first and gating job.

---

## 1. The hypothesis this exists to test (and currently FAILS)

**Does terrain density flip which composition wins?** The game is played on **one big mixed map** — open **lanes** and dense **clusters** that break line of sight (see §4). The hypothesis is tested by varying how much of that map is cluster vs lane (`terrainDensity`, §7):
- **Lane-heavy (open) terrain** → the Destroyer/railgun composition dominates (long-range direct fire; lights die crossing open ground; the fixed gun has room to orient).
- **Cluster-heavy (dense) terrain** → a light + bomber + frigate composition dominates (closes under cover into the Destroyer's dead zone, saturates its limited point-defence, kills the blinded capital).

**Status: unproven. The prior build returned +0 (railgun won both maps).** The likely cause: the old railgun was turreted and hit anything with line-of-sight, so cover was irrelevant. The new mechanics in this PRD — a **fixed-forward railgun with a minimum range, ineffective against small ships**, **solid asteroids**, and **momentum** — exist substantially to give the flip a real mechanism. **The rebuild's success is defined by the flip emerging (§8), not by feature completeness.** If the flip does not emerge once the core is built, that is a stop-and-decide moment for the human, not a cue to keep building.

---

## 2. Goals

1. Resolve a fight between two AI fleet compositions on a big 2D arena, under Newtonian physics, and **always produce a winner** (force-resolve on a timer — see §8).
2. Make **line-of-sight through solid asteroids** the decisive combat variable (no LOS = no shot; asteroids also block movement).
3. On one big mixed map (lanes + clusters), run the same matchup across a range of **terrain density** (lane-heavy → cluster-heavy) and watch the winner flip.
4. Resolve a full fight in **~2–5 minutes**; no match may hang.
5. Keep it **readable and tunable**: every gameplay number in one CONFIG block.
6. **Validate the flip via a headless batch harness before building player controls, UI polish, or any non-core system.**

## 3. Non-Goals

- **PvP / networked multiplayer** (single human vs AI is in scope, §9).
- **Economy / mining / base-building.**
- **Art, sprites, sound.** Shapes and lines. Ugly on purpose (§6).
- **Campaign, missions, progression.**
- **3D.** Strictly 2D — heading only, no pitch/roll, no "above/under" an asteroid.

---

## 4. Tech constraints

- **One self-contained `index.html`.** HTML + CSS + JS inline. No build step, no dependencies, no CDN. Runs by opening the file; hostable as one file on GitHub Pages. (This is the artifact the human plays and pushes to GitHub.)
- **Canvas 2D**, top-down, responsive; the arena auto-fits the viewport.
- **Vanilla ES6**, readable — a human tunes this by hand.
- **Single `CONFIG` object at the top** — one source of truth for every constant.
- **Fixed-timestep simulation (60 Hz) fully decoupled from rendering and the DOM.** No canvas/window/document references inside game logic. The sim must run **headless** at max speed (§8B).
- **Seeded PRNG for all randomness** — every run reproducible from its seed; a batch deterministic given seeds. No bare `Math.random()`.
- **Every match has a hard tick cap** that force-resolves and scores it. The sim can never hang or run unbounded (see §8 resolution rules). This is a load-bearing requirement, not a nicety.
- Target: laptop (mouse + keyboard). Touch deferred (§11); keep rendering resolution-independent.

### Roster — starting constants (§5 explains intent; preserve the relationships)

Movement is Newtonian: each ship has position, velocity, heading, angular velocity, and stats `mass`, `mainThrust` (linear accel = thrust/mass), `rcsTorque` (angular accel), `maxCruiseSpeed` (autopilot transit ceiling). `evasion` is an abstract 0–1 hit-avoidance stat applied to incoming fire (see §5). All values below are starting points.

| Class | Cost | HP | mass | accel | turn (rcs) | cruise | evasion | Primary weapon | PD | Role |
|---|---|---|---|---|---|---|---|---|---|---|
| **Destroyer** | 6 | 140 | 100 | low | low | low | 0.05 | **Fixed-forward railgun** | gatling (short, capped) | Long-range line-breaker; blind & helpless up close |
| **Frigate** | 3 | 90 | 55 | med | med | med | 0.15 | **Torpedoes (specialist)** | gatling (short, capped) | Standoff saturation + screen |
| **Bomber** | 2 | 26 | 20 | high | high | high | 0.65 | **Straight-line bombs (AOE)** | — | Anti-capital AOE by saturation |
| **Interceptor** | 1 | 14 | 12 | v.high | v.high | highest | 0.80 | **Front gatling + 1 torpedo** | — | Anti-light screen + one finite anti-capital strike |

**Weapon specs (the important part):**

- **Railgun (Destroyer):** fires **only forward**, within a narrow arc of the ship's heading — so the Destroyer must *rotate to aim*, and rotation is slow. **Minimum range** (`railgunMinRange`, e.g. 150): a target *closer* than this cannot be fired on — the dead zone lights exploit. Long max range (e.g. 700). High damage (e.g. 30). Slow fire (~4 s). Slug is **near-hitscan** (very fast) and **cannot be intercepted**. **Highly ineffective vs small/evasive ships** — the interceptor's 0.80 and bomber's 0.65 evasion mean the slug almost always misses them; it reliably hits only slow, low-evasion targets (Frigate, Destroyer). This is deliberate: the railgun is an anti-capital / anti-frigate direct-fire weapon, not an anti-swarm one.
- **Point-defence gatling (Destroyer + Frigate):** intercepts **both torpedoes and ships** within a **short range** (`pdRange`, e.g. 140), **throughput-capped** (`pdSlots`, e.g. Destroyer 2 / Frigate 3): engages at most N incoming at once; the (N+1)th and beyond **leak through**. Saturation is the counter. The Destroyer's PD is *weaker* than the Frigate's (fewer slots) — it cannot defend itself alone.
- **Torpedoes (Frigate primary; Destroyer weak secondary):** fire from **very long range**, **loosely tracking** — they *reliably* hit slow targets (Destroyer, Frigate) and **almost never** hit an interceptor (loose tracking + evasion). Interceptable by PD → on interception they detonate for a **small** AOE. Torpedo speed ≈ **4× interceptor**, but **far slower than a railgun slug**. **The Frigate is the torpedo specialist** (longer range / faster reload / larger salvo); the Destroyer's torpedoes are a weak afterthought — this is what stops the Destroyer from being a strictly-better Frigate. **Tracking reliability keys on target *predictability*, not just speed:** a slow **or** steady-vector target is reliably hit; a fast, jinking one is missed — so a bomber flying a **steady bomb-run vector (or sitting still) is torpedo bait**. **Torpedoes respect line-of-sight** — blocked by / losing lock behind asteroids — so in dense terrain a bomber makes a short steady run from behind a cluster and ducks back before the torpedo arrives, while in the open it's exposed the whole run. This is what makes "stationary bombers eat a torpedo" a **terrain-differentiated pressure that reinforces the flip**, not a flat tax. (If torpedoes ignored LOS, this would kill bombers on both maps and take the dense-map win with it — so the LOS coupling is load-bearing.)
- **Bombs (Bomber):** travel in a **straight line** (no tracking), fired from **beyond the Destroyer's PD range**; the autopilot leads the target. **Accuracy emerges from the target's manoeuvrability, and this sidesteps a balance nightmare:** a Destroyer is heavy and sluggish under Newtonian physics — it *cannot* jink meaningfully during a bomb's flight — so a well-led bomb **reliably hits it**, and its only counter is **PD interception + escort**, not dodging. Nimble lights dodge unguided bombs easily, so bombs are naturally **anti-capital only**. **Tune bombs *fast* (short flight time)** so the sluggish Destroyer can't drift off-aim before impact — slow bombs reintroduce the knife's-edge "did the capital wiggle enough to miss" regime; fast bombs make it predictable. Consequence (correct for a "tactics-not-piloting" saturation weapon): bombing has **no aiming skill** — the skill is committing *enough* bombers to overwhelm PD. **The bomber's own motion also matters:** an unguided bomb fired on a **steady approach vector** lands true, but a bomber **jinking** to dodge torpedoes throws its bombs wide (own-velocity skew). So the bomber faces the choice its enemy's torpedoes enforce (see Torpedoes): fly **steady** to bomb accurately — and become torpedo-predictable — or **jink** to survive and waste bombs. In dense terrain it threads short steady runs from cover; in the open, steady = dead. **Zone (AOE) damage** on impact; **interceptable by PD** but **detonates anyway when intercepted**, hitting whatever's in radius **including the bomber itself and friendlies**. `bombRange` (standoff distance — O1) sets standoff-vs-suicidal.
- **Front gatling + single torpedo (Interceptor):** the gatling fires **forward**, short range, low damage, fast — kills lights (bombers, interceptors) and does **a little** to a capital's hull (no longer zero — enough that a Destroyer can't simply ignore a swarm of them over time). Each interceptor also carries **one single-use torpedo** — a finite anti-capital strike, loose-tracking like all torpedoes (reliable vs a slow Destroyer, useless vs other lights; interceptable by PD). So the interceptor's play is: fire its one torpedo from range, then either commit close to chip with the gatling (and get melted — see §5 linger-vs-transit) or fall back. It is an anti-light screen with **one** anti-capital punch — **not** a sustained capital-killer (that's the bomber). Keep the torpedo weak and highly interceptable (see §5 flip-flatten watch).

**Asteroids & debris (reproduce; validated in the prior build):**
- Asteroids are **solid circles**: they **block line-of-sight** *and* **block movement** — ships collide with them and cannot pass over/under/through. They interact physically with everything (ships, debris, each other).
- **Any weapon from any ship damages any asteroid.** When an asteroid's HP is exhausted it **splits into ~3 smaller, lower-HP asteroids** (HP scales with area, ~¼ per generation), down to a minimum radius floor below which fragments vanish — so the cascade is **bounded and terminates**. Children are ordinary asteroids (grey, block LOS, collide, further destructible).
- **Debris/children carry momentum but move slowly** (small launch impulse + per-tick drag → they settle into a lingering field near the burst, they do not streak across the map) and **collide with rocks, ships, and each other** (momentum-conserving, with a restitution constant). A moving fragment deals **impact damage to a ship it strikes** (∝ mass × closing speed); a settled one just rests against the hull. Slugs/torpedoes/bombs destroy fragments they hit. **Bursting a rock is therefore a weapon** — shatter the asteroid a bomber group hides behind and the shrapnel can shred them.
- CONFIG blocks: `asteroidHP` (≈112, i.e. ~4 railgun hits), `fragmentCount`, `asteroidMinChildRadius`, and a labelled **DEBRIS PHYSICS** block (`fragmentSpeedScale`, `fragmentBurstSpeed`, `fragmentDrag`, `fragmentRestitution`).

Other CONFIG: arena size (**big** — a fight crosses it in minutes, not seconds); **`terrainDensity`** (0–1: fraction of the map given to dense clusters vs open lanes — the flip variable, §7); cluster/lane generation params (cluster count, cluster radius, lane width); asteroid radius range; `matchTimerSeconds` (target 150–300) and the derived **hard tick cap**; preset fleet definitions; toggle defaults.

**One mixed map, not a binary.** The map is a single large arena procedurally seeded with open **lanes** and dense asteroid **clusters** that block line-of-sight and movement. `terrainDensity` shifts the lane↔cluster ratio: low → mostly open lanes; high → mostly dense clusters. Play happens at a **balanced** ratio (≈0.5) where positioning matters; the flip test **sweeps** the ratio (§8). There is no separate "sparse map" and "dense map" — there is one map whose density knob is swept for testing and set mid-range for play.

---

## 5. The counter-web (build the AI and tuning to serve this)

The fights must produce these relationships. If tuning breaks one, the hypothesis test is invalid.

- **Destroyer > Frigate / other slow targets** — the railgun reliably kills low-evasion ships at range; ~4 hits kills a Frigate. This is the Destroyer's whole job, and it only works when it has *space and time to orient* (open map).
- **Destroyer ⇄ Interceptor now resolves (by design).** Each interceptor carries one torpedo (fired safely from long range) + minor gatling chip. To land the gatling it must **strafe**: dive fast into the Destroyer's PD bubble (~140) down to short gatling range (~90), dump a burst, and exit before PD kills it (§6.5 AI). Because the gatling range is **much shorter** than PD range, that strafe is a deep, exposed pass — and with 14 HP, enough strafes cost interceptors kills. So the interceptor trades: safe torpedo-poke, or risky strafes for extra chip. Either way the fight resolves on kills, not the clock.
- **Keep the Destroyer's PD weak anyway — do NOT strengthen it to "shred interceptors."** What makes a strafe costly is that interceptors are **fragile and must penetrate deep** (gatling range ≪ PD range), not that the PD is strong. A **transiting** torpedo or bomb only needs to reach the hull and crosses the bubble fast → the weak PD gets a shot or two → saturation leaks and still beats the Destroyer (§5 core). **The reconciliation: weak PD punishes a deep strafe but can't stop a saturating salvo.** Two guards fall out: keep **gatling range ≪ PD range** (else strafes dump from the bubble's edge, take no fire, and interceptors become unkillable → stalemate returns), and keep **PD weak** (else it eats saturation and the flip dies).
- **The gatling rewards diving (decided).** The interceptor's gatling does enough to a capital that strafing is worth the PD risk — so the strafing runs will actually happen. But this adds to the lights' raw anti-capital power (the flip-flatten lever, below), so the strafe must stay **genuinely lethal** to the interceptor (deep, exposed pass) — enough that on OPEN terrain the Destroyer+Frigate PD trades interceptors down and the railgun side still wins. In dense terrain they dive from cover (shorter exposure) and survive to contribute — so, like bombers, the interceptor's effectiveness is **terrain-gated**, which reinforces the flip. Never a free lunch.
- **⚠️ Flip-flatten watch (the biggest balance risk):** every buff to the lights' *raw* anti-capital power — interceptor torpedo, interceptor gatling-vs-capital, bomb damage — helps lights on **both** maps and nudges toward lights-win-everywhere (the +0 you already have, mirrored). **The flip must come from terrain-differentiation — cover shortens light exposure in dense terrain; the open exposes them to PD and torpedoes — NOT from lights simply being good at killing capitals.** Keep the flat anti-capital knobs modest; if lights start winning the OPEN (low-density) end of the sweep, **cut** them rather than tuning them. Watch the whole aggregate in the sweep, not each knob alone.
- **Lights close into the Destroyer's dead zone** — inside `railgunMinRange` the railgun *cannot fire at all*, and the Destroyer rotates too slowly to keep its nose on a jinking target. A Destroyer without escort, caught by lights at close range, is helpless. **This is the mechanism the whole flip depends on** — dense terrain lets lights reach that dead zone alive.
- **Frigate/Bomber saturation > Destroyer** — the Destroyer's PD (2 slots) is trivially overwhelmed: more torpedoes/bombs than it can intercept → the surplus lands. Saturation is a *strategic* decision (bring enough, commit together), not execution skill.
- **Frigate PD + Destroyer escort > lights (in the open)** — combined PD downs bombs/torpedoes and screens; the railgun farms anything slow. This is why the railgun composition *should* win the open map.
- **Interceptor > Bomber, Interceptor ⇄ Interceptor** — the light-vs-light layer; your interceptors keep theirs off your bombers.
- **Bomber self-risk** — a bomb intercepted near its own bomber can kill it; committing bombers into heavy PD is a real cost.

**Net effect (the flip):** open → Destroyer composition farms at range and lights can't punish it → railgun wins. Dense → LOS breaks, solid rocks funnel and shelter, lights reach the dead zone, saturation overwhelms the capped PD → lights win. **The map decides the winner. That is the whole game, and it is exactly what the prior build failed to produce.**

**⚠️ Stalemate rule (mandatory — this is also the anti-hang rule).** Interceptor-vs-Destroyer is now largely designed out (see above), but some matchups can still fail to kill each other (two evasion-heavy fleets trading near-misses). Left alone, those matches never end — which both makes the flip unmeasurable *and* is the "game never finishes" hang. Therefore:
- **Every match force-resolves at `matchTimerSeconds` (and at the hard tick cap in headless).** Scoring at resolution: the side with greater **surviving fleet value** wins; tiebreak by **total damage dealt**; still tied → draw (logged as a finding).
- **Every unit must be killable by *something* that can be on the field.** The interceptor is killable by PD and by other interceptors — acceptable. But watch for compositions that produce true mutual-immunity; a match that reaches the tick cap without meaningful attrition is a **balance finding to report**, not normal.

---

## 6. Systems — build in this order (flip is the gate)

Ugly-on-purpose rendering: **class-distinct shapes, oriented to heading, unambiguous front** — interceptor = small **triangle** (apex = nose), bomber = small **diamond** (mark its nose), frigate = medium **square**, destroyer = large **rectangle** (mark the front). Colour by team, size by class. Asteroids = grey **circles that split into smaller circles**. Draw a short **velocity vector** per ship so momentum/flip-and-burn reads.

1. **Scaffold.** `index.html`, CONFIG, fixed-timestep loop, render (shapes + asteroids + HUD), seeded PRNG, empty STATUS.md/LESSONS.md.
2. **Newtonian movement + autopilot.** Momentum, thrust, rotation, flip-and-burn to arrive at rest; obstacle avoidance around **solid** asteroids; `maxCruiseSpeed` as the only speed ceiling (no artificial global cap — the "fast in the open, slow when navigating" feel emerges from the autopilot never carrying more speed than it can shed). **The autopilot is the riskiest subsystem — if it oscillates, overshoots, or bonks into rocks, the whole game feels broken.**
3. **Solid asteroids + line-of-sight.** Ships collide with rocks and cannot pass through; segment-vs-circle LOS gating (no LOS = no shot). *(Destructible split/debris from §4 can come here or in Phase 2, but the flip test in step 6 runs on **static** terrain for a clean signal.)*
4. **Weapons + evasion + PD + force-resolve.** Railgun (fixed-forward, min-range, near-hitscan, ineffective vs evasive); torpedoes (long, loose-tracking); bombs (straight-line AOE, self-risk); interceptor gatling; PD (short, slot-capped, hits ships + torpedoes + bombs); evasion rolls; win condition + **hard timer/tick-cap force-resolve with scoring**.
5. **Role AI.** Deliberately simple, serving §5: Destroyer holds max range and orients to fire, retreats from close lights; Frigate stands off and lobs torpedoes, screens with PD; Bomber lobs straight-line bombs at capitals from beyond PD range; **Interceptor makes fast strafing runs** — fire its torpedo from range, then dive through the target's PD bubble, dump a gatling burst at closest approach, and immediately exit PD range (do not loiter); a **spent** interceptor (torpedo used) commits or peels off to hunt enemy lights rather than idling. Nearest-valid-target of any class; every weapon can damage any asteroid. *(This AI is no longer "dumb" — strafing, ambushes, terrain-seeking, and flip-and-burn nav are accumulating into a real behaviour layer. Fine, but the more behaviour you add, the more a flip result reflects AI quality, not just mechanics — keep §8B's "overfit to the AI" caveat live.)*
6. **HEADLESS FLIP HARNESS + THE GATE (§8, §8B).** Batch-run the canonical matchup on sparse vs dense, static terrain, seeded, force-resolved. **Measure the flip. If it does not emerge, STOP and report — do not proceed.** (Bounded auto-tuning of §5 numbers is allowed; inventing new mechanics is a human decision.)
7. *(Only after the flip validates)* **Destructible/split/debris** (if not already in step 3), **player controls (§9)**, feedback/polish, then a clean state ready to push to GitHub.

---

## 7. Toggles (defaults in bold)

- `terrainDensity`: **≈0.5 for play** — a 0–1 knob setting the cluster↔lane ratio on the single mixed map. The flip test sweeps it low→high (§8); play uses a balanced mid value.
- `destructibleAsteroids`: **on** | off — asteroids split/shatter (§4). *The flip harness runs it **off** (static terrain) for a clean baseline, then **on** to confirm the flip survives dynamic terrain.*
- `movementModel`: **newtonian** | arcade — Newtonian is the game; a simple `speed`+`turn` **arcade** mode is retained **only** as a fast, deterministic fallback for isolating whether a bug is in the physics/autopilot or elsewhere. Not the shipping model.
- `fleetMode`: **preset** | pointsBuy — preset = the §8 canonical comps; pointsBuy = pre-match 9-point budget (costs 6/3/2/1), build each side, then fight.

---

## 8. Acceptance criteria — the flip is the definition of done for the core

Canonical test — `movementModel: newtonian`, `destructibleAsteroids: off` (static terrain), `fleetMode: preset`, one mixed map with `terrainDensity` **swept** across seeds:
- **Preset RAILGUN:** 1 Destroyer + 1 Frigate. **Preset SWARM:** ~2 Bombers + 3 Interceptors (+ tune to ≈ equal points).

```
Given RAILGUN vs SWARM at LOW terrainDensity (lane-heavy / open), across many seeds
Then RAILGUN wins the clear majority.

Given the same matchup at HIGH terrainDensity (cluster-heavy / dense), across many seeds
Then SWARM wins the clear majority.

Given a sweep of terrainDensity from low to high
Then the winner crosses over (the win-rate curve is not flat) — this crossover IS the flip.

Given a railgun shot with an asteroid between muzzle and target
Then no damage is dealt (LOS blocked).

Given a target inside railgunMinRange
Then the Destroyer cannot fire on it.

Given any matchup
Then the match ALWAYS resolves within matchTimerSeconds / the tick cap (never hangs).
```

**Read the extremes for the clean signal.** At low/high density the terrain dominates regardless of AI smarts (few clusters → nowhere to hide; few lanes → no open ground), so the crossover there is a real test of the mechanic. The **mid-density** balanced map is where the *game* lives (positional decisions), but its outcome depends on the AI actually using terrain — treat mid-density results as game-feel, not as the mechanic's proof. **Randomise spawn positions across seeds** so `terrainDensity` is the isolated variable, not a spawn advantage.

- Then repeat with `destructibleAsteroids: on` — **the flip must survive** dynamic terrain.
- A full fight resolves in ~2–5 min. Firing lines are drawn and fade; a blocked/held shot is visibly blocked. The player can *see why* a shot missed or didn't fire.
- **Failure signal & gate:** if the same composition wins both maps (flip ≈ 0), or matches routinely fail to resolve, **the core has failed — STOP and report to the human.** Do not proceed to polish. Because modifiers are controlled, the fault is attributable to the core, which is the whole point of testing here first.

## 8B. Self-test & headless balance harness

- **DOM-free sim core** run headless; **seeded/deterministic**; **every match force-resolves at a hard tick cap** (no wall-clock waits, no unbounded runs).
- **Batch runner:** N seeded games (optionally sweeping one CONFIG constant), aggregate win-rate per side, avg duration, avg survivors, and **count of non-resolutions** (a non-resolution is a first-class finding).
- **Objectives — the correct ones:**
  1. **Regression:** after any change, re-run and confirm §8 still holds.
  2. **Flip magnitude — the objective that matters:** maximise/verify `(RAILGUN win% at low terrainDensity) − (RAILGUN win% at high terrainDensity)`; target a large gap (≈ +60 pts) with a clean crossover across the sweep. **Do NOT optimise toward 50/50 at any single density** — 50/50 is the crossover *point*, never the target of one density; an optimiser told "make it fair everywhere" will flatten the whole curve and report success. Optimise for a **steep crossover** + absence of a density-independent dominant strategy.
  3. **Dominant-strategy / stalemate detection:** flag any comp winning >~65% regardless of map (design hole), and any matchup that reaches the tick cap without attrition (stalemate hole).
  4. **Sensitivity sweeps** to find the cliffs (where a constant flips the outcome).
- **Two hard limits:** results are **overfit to the simple role AI** (directional, not gospel — that AI is deliberately dumb) and the sim **cannot measure fun** (it certifies necessary conditions — flippy, non-degenerate, resolves in time — not sufficiency). Green suite = "not broken," not "good."

---

## 9. Player control (built only AFTER the flip validates)

Player commands one fleet; AI commands the other. **Orders set a ship's goal; the autopilot executes it** (steering, flip-and-burn, guns, evasion). Three states: **Auto** (role AI — un-commanded ships and the whole enemy fleet), **Ordered** (executing Move / Attack-move / Attack-target; on completion drops to **Hold**, not back to Auto), **Hold** (station-keep at rest, weapons free — the ambush tool). Pursuit is a deliberate order, not automatic.

Desktop scheme: left-click select; drag = box-select friendlies; shift+left-click toggles a ship in selection; **right-click = smart order** (empty → Move; enemy ship → Attack that target; asteroid → Attack the rock, i.e. blow open cover); shift+right-click = Attack-move; **`S`/Stop = Hold**; cursor adapts to the hover target; left-click empty / Esc = deselect. **Active pause** (issue orders while paused) recommended — keeps it about decisions, not fast hands. Feedback: selection rings, drag rectangle, distinct Move vs Attack-move markers, hold indicator, order flash.

The command layer is an **input source feeding goals to the same autopilot** — with no human, the AI is the goal source, so headless AI-vs-AI batches (§8B) are untouched.

---

## 10. Open questions (resolve before/early in the build; defaults chosen if silent)

- **O1 — Bomb range / bomber identity.** Is `bombRange` a long standoff (bombers lob from safety, hard to intercept, low self-risk) or short (bombers dive into PD, high self-risk, near-kamikaze)? This defines whether the bomber is an artillery piece or a glass sacrifice, and it strongly affects the flip. **Default if silent:** medium standoff (~300), leaning artillery, tuned so a bomber usually survives a run against a lone Destroyer but not against Frigate-backed PD.
- **O2 — Frigate vs Destroyer separation.** Confirmed in §4 (Frigate = torpedo specialist; Destroyer's torpedoes weak). If the numbers ever let the Destroyer out-torpedo the Frigate, the Frigate is dominated — guard against it.
- **O3 — Interceptor vs Destroyer stalemate — RESOLVED.** Interceptors now carry one single-use torpedo + minor gatling chip (finite anti-capital), and the Destroyer's PD melts interceptors that linger close to use their short-range gun (§5 linger-vs-transit). Force-resolve remains the backstop. **Role-AI note:** make *spent* interceptors (torpedo used) commit or retreat rather than idle at range, so fights end on kills, not the clock.

---

## 11. Parking lot (designed-later, do not build)

- Control groups, waypoint queuing, double-click-select-by-type.
- Touch / phone input mapping.
- Wreck-carcasses (destroyed ships) as additional cover.
- Torpedo fragmentation / flak interplay.
- Economy: asteroid mining, base-on-asteroid, ship production.
- Real art, camera, effects, audio.
- Campaign / persistent fleet.
