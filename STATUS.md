# STATUS

**Phase:** Combat-sandbox build complete (user-directed pivot from the flip gate), now with **BIG asteroids + gravity**. The game is playable: RTS controls, fog of war, universal friendly fire, always-destructible terrain, detection, flanking, cover demolition, and gravity wells that bend everything.

## What's in the build (all verified)

**TITAN + BIG asteroids + gravity (new):**
- **Every procedural seed carries exactly one TITAN** (r 1150–1600, ~5× a BIG — up to 3200px across) **plus 1–3 BIG asteroids** (r 220–380, vs 26–90 for ordinary rocks), placed in that order so clusters/lanes flow around them; arena grown 4400×3200 → 8000×5600 and match timer 300s → 360s to give titan-scale maps transit room (rock counts rescaled).
- Asteroid outlines: 12–56 vertices scaling with radius (two low-frequency harmonic lobes + fine grit) instead of the old 9–13 uniform-noise polygons — monsters no longer render as decagons.
- Wells are **bounded**: pull fades to zero at `wellReach`×radius (the titan's raw r³ well would otherwise act map-wide), and settled rocks can only be woken within a `rockWakeShell` (650px) of a source's surface — accretion is a local shell, not hemispheric terrain collapse.
- **Gravity**: rocks with r ≥ 100 (the BIGs + their first-gen fragments) are wells with mass r³, accel `G·r³/(d²+(0.5r)²)`, clamped/cutoff. Ships drift and must burn against it (heavies near a monster rock genuinely struggle); debris curls into the wells and **accretes** (settled rocks wake above a pull threshold unless supported down-well — far-field terrain never drifts, cover stays dependable); torpedoes/bombs bend course but keep design speed. Cinematic constant, deterministic (no RNG).
- **Tuning**: gravity slider ×0–3 in the fleet-setup screen AND a live slider in the top bar (mutates `match.config.gravity.G` mid-match; restart keeps the tuned value). `__praedra.setGravity(mult)` for automation.
- Renderer: massive rocks get a cratered look + faint field-contour rings (strong-pull and well-edge).
- Perf: spatial grid now holds settled rocks only (movers scanned as a short list); grid rebuilds only on settle/wake/split flips. Small-fleet match ~700µs/tick at titan scale (harness budget is per-tick now — titan-scale hunts are legitimately long in sim-seconds).
- Anti-entombment set (found by adversarial batteries): ships shove settled pebbles (r < `collision.pushableRockRadius`) aside instead of being walled in by accretion shells; pull on a ship is capped at `gravity.shipEscapeCap` × its own thrust accel (wells threaten, never imprison — keeps the ×3 slider playable); settled rocks are true terrain in rock-rock collisions (accreting pebbles no longer walk the titan across the map — anchors verified at 0.0px drift over 90s); the autopilot stall-breaker commits to one sidestep side per stall episode (picked away from the pinning rock) instead of dithering.
- Titan-scale navigation (found by tracing full-match stalls): the autopilot has **gravity feed-forward** (the burn buys the velocity change AND the hover — station-keeping in a well now costs visible throttle, which feeds the detection plume rules); hunting fleets **route around the titan's flank** instead of pathing through its well (`routeAround`, tangent waypoints at r+900); titan-scale rocks are rim-followed (tangent-bug) when a path must cross them; the `bigRockAvoidPad` is a transit margin, not a wall — it relaxes for a rock when the ship's goal legitimately sits at that rock's rim (ordered points and cover spots near bigs are reachable again).

**Sim (DOM-free, deterministic, force-resolving):**
- **Friendly fire universal**: railgun/gatling hit the first hull on the firing ray regardless of team; torpedoes contact-detonate on any non-owner hull; all AOE (bombs, chains, torpedo interceptions) hits everyone.
- **Asteroids always destructible**: rock HP 80×(r/60)²; railgun ×2.2 and torpedo ×2.5 demolition coupling; destroyed rocks split into 4 polygonal, momentum-carrying, spinning fragments (bounded cascade, cap 500); debris drifts (drag 0.35), collides momentum-conservingly, and shreds hulls on impact.
- **Detection**: per-class signature (destroyer 2800 / frigate 1700 / bomber 900 / interceptor 750) × thrust-plume modulation (0.6 coasting → 1.2 full burn), LOS required, team-shared picture (state.detA/detB), last-seen memory. ALL targeting, threats, PD ship-tracking, and commit waves are detection-gated; blind ships hunt toward last contact.
- **Cover demolition AI**: destroyers (railgun) and frigates (torpedoes) fire on rocks that hide a recently-seen ghost or block a shot at a detected target — no hesitation (1.2 s).
- **Class pathing**: avoidance margins scale with hull (destroyer 2.2×); capitals sample sideways offsets and drift toward open ground with a clean firing lane.
- **Flanking**: 50% of commit waves hook wide left/right before turning in.
- **Player orders**: `Praedra.issueOrder` — move / attackmove / attack / attackrock / hold / auto, per-class execution, opportunistic weapons-free fire, completed orders drop to Hold (PRD §9 semantics).

**App (browser, single self-contained file, console-clean):**
- Play-vs-AI (team A) or spectate. Fog of war: undetected enemies hidden, recent ghosts faded.
- Selection: click / drag-box / shift-toggle / double-click-by-class / Esc. Right-click smart orders (move / attack detected ship / attack rock), shift+right = attack-move, S = hold, Space = ACTIVE PAUSE (orders work while paused). Selection rings, drag rectangle, move vs attack markers, hold indicators, adaptive cursor + reticle.
- Lumpy polygonal asteroids and tumbling debris; shatter/explosion effects; HUD with selection summary and control cheat-sheet.

## Verification
- `harness/tests.mjs`: **15/15 pass** (original 12 + big-asteroids-every-seed, gravity-attracts-debris, gravity-pulls-ships).
- Headless Chromium: zero console errors; 13/13 mandatory + 9/9 supplementary UI assertions (selection, orders, active-pause ordering, fog subset checks, spectate).
- Determinism from seed: verified (incl. with gravity overrides; deep state compare at tick 600). Zero draws/errors across 135+ battery matches; no NaN at any gravity multiplier 0–3×.
- Resolution at titan scale: asymmetric matchups resolve decisively (RAILGUN vs SWARM: 100% railgun across a 30-match sweep, 60-77% by elimination, the rest scored blowouts at the cap with SWARM ground to fleet value ≈ 0). The symmetric BALANCED mirror is an attrition war whose kill rate decays — most mirrors resolve on points (HP-lost) at the 360s cap after continuous combat (fleet values 42 → ~8-15); giving them 480s just inflated durations without changing outcomes (verified). Combat is continuous either way — the old "fleets never find each other" stalls are fixed (hunt routing around the titan + gravity feed-forward, below).

## Known items / notes
- Balance: the flip-gate question is parked (see LESSONS.md and git history for the full record); current AI-vs-AI win rates lean RAILGUN at most densities. The sandbox direction (detection, FF, demolition) reshuffled balance — retune when the flip work resumes. The bigger arena + gravity reshuffle it further; ~10% of armada matches resolve on the timer (both fleets camped in the mutual LOS shadow of a BIG asteroid, or the endgame interceptor-vs-capital slog — scored correctly either way). Cutting that further is hunt-AI tuning; revisit with the flip retune.
- A fully-static scenario where a rock permanently blocks the only sightline is a stalemate by design (never-detected targets can't be engaged); irrelevant in real matches (documented in SIM_CONTRACT).
- `destructibleAsteroids` config key is ignored (kept for compat).
