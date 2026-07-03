# STATUS

**Phase:** 1 complete through the flip gate (§6.6). **GATE RESULT: FLIP NOT ACHIEVED — stopped per §8, awaiting human design decision.** Phase 2 (player controls, polish) deliberately NOT started.

## Gate numbers (definitive, RAILGUN=A vs SWARM=B, preset fleets, seeded)

Static terrain (`destructibleAsteroids: off`), 60 seeds per density:

| terrainDensity | A-win% | B-win% | non-resolutions | avg match (s) |
|---|---|---|---|---|
| 0.10 | 86.7 | 13.3 | 0 | 60 |
| 0.25 | 61.7 | 38.3 | 5 | 83 |
| 0.40 | 71.7 | 28.3 | 3 | 90 |
| 0.55 | 70.0 | 30.0 | 7 | 121 |
| 0.70 | 68.3 | 31.7 | 17 | 135 |
| 0.90 | 61.7 | 38.3 | 20 | 142 |

- **Flip metric: +25.0 pts** (target ≈ +60). Clear downward trend from open to dense (endpoints 86.7 → 61.7, with mid-sweep seed noise), **but no crossover** — RAILGUN holds a majority at every density. Destructible-on (40 seeds): 85.0 / 72.5 / 70.0 → flip +15, also no crossover.
- Every match resolves (timer force-resolve verified; non-resolutions are a scored finding, not hangs). Zero draws, zero errors across all batches.
- ~50 configurations evaluated across 7 exploration rounds, including every explicitly sanctioned lever (railgunMinRange, pdSlots, evasion, cruise speeds, terrain scaling, PD strength, bomb damage/cadence, torpedo speed/standoff, comp variants 2B5I/3B3I/4B1I, commit-wave shape). Dense-end SWARM ceiling ≈ 38–43%; every further push collapses the open end first. See LESSONS.md for the three structural walls and the mechanic-change candidates that need a human decision.

## Built & verified
- `index.html` — self-contained, runs in browser with **no console errors** (verified via headless Chromium): DOM-free deterministic sim core (60 Hz fixed timestep, seeded PRNG, hard tick cap force-resolve), Newtonian movement + autopilot (flip-and-burn, waypoint routing around solid rocks, stall-breaker, per-rock avoidance hysteresis), procedural mixed map with `terrainDensity` knob, segment-circle LOS, full roster + weapons per PRD §4 (fixed-forward railgun with dead zone, LOS-locked loose-tracking torpedoes, straight-line bombs with own-jink skew and self-risk AOE, interceptor gatling, slot-capped PD), destructible asteroids splitting into momentum-carrying debris (bounded cascade), role AI (commit waves, cover staging in LOS shadows, strafe passes, pop-out bomb runs, frigate-first focus), fleet-value scoring with damage tiebreak. Spectator renderer with firing lines/blocked shots, HUD, seed/density/team/speed controls.
- `harness/run.mjs` — batch flip runner (density sweep, workers, `--set` overrides, JSON dumps, flip metric + crossover detection).
- `harness/tests.mjs` — **8/8 acceptance tests pass**: determinism, always-resolves, LOS blocked/clear, min-range dead zone, railgun evasion gate, tick-cap force-resolve at exactly the cap, wallclock < 2 s/match.
- Performance: ~0.5 s/match open, ~2 s dense (spatial grid + IIFE scoping; ~5× gain).

## Known issues / open items
- The flip crossover itself (above) — blocked on a design/mechanic decision.
- `railgun.evasionMult` 1.0 gives the railgun a 20% hit rate vs interceptors (PRD flavor says "almost always misses"); it protects the open end. Flag for review.
- `torpedo.lobMinRange` is an AI-discipline constant standing in for a real torpedo arming-distance mechanic (candidate change).
