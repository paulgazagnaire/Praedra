# STATUS

**Phase:** 1 + approved mechanics A-D integrated. **Flip gate: still short of a stable crossover — reported to human with data and options.** Phase 2 not started.

## Approved mechanics (all implemented & tested)
- **A. PD reaction gating:** PD tracks threats from `pd.trackRange` (700, LOS required) and may only engage after `pd.reactionSeconds` (0.5) of continuous track; track resets when LOS breaks. Bombs launched inside ~350 px arrive before tracking completes and leak entirely.
- **B. Bomb salvos + sympathetic detonation:** bombers release a fanned salvo (`bomb.salvo` 3, spread 0.13 rad, per-lane launch checks); ANY blast (incl. a PD interception) detonates live bombs inside its radius — tight clusters chain, splashing whoever is near, including the bomber.
- **C. Timer scoring by destruction:** timer-resolved matches are won by the side that destroyed the larger FRACTION of the enemy fleet's HP (tiebreak damage, then draw). Elimination scoring unchanged.
- **D. Torpedo arming distance:** `torpedo.armDistance` (260) — an unarmed torpedo is a dud (no terminal roll, no AOE, no rock damage); AI additionally lobs only beyond `torpedo.lobMinRange` (300).

## Gate numbers (final config, 60 seeds/density static; 40 destructible)

| terrainDensity | static A-win% | static B-win% | destructible-on A-win% |
|---|---|---|---|
| 0.10 | 81.7 | 18.3 | 77.5 |
| 0.50 | 65.0 | 35.0 | 67.5 |
| 0.90 | 56.7 | 43.3 | 75.0 |

- Static flip **+25.0 pts**, no win-rate crossover (SWARM ceiling ~43-50% at 0.9 across the tuned front). **At density 0.9 SWARM leads eliminations 24-18** — the timer channel (A 12-6) nets it back; `matchTimerSeconds` 330 (out of PRD band) gave SWARM 53.3%, 300 (in-band, baked) gives ~50/50 on the deepest variant.
- **Destructible-on washes the gradient out (+2.5)**: the railgun side profits from shattering cover (opens its own torpedo/railgun lanes; debris punishes the swarm's close-quarters game).
- Every match force-resolves; zero draws/errors across all batches; 8/8 acceptance tests pass; browser build console-clean.
- ~80 configurations evaluated post-mechanics + pre-mechanics rounds. Pareto front: (open A 82%, dense B 45%) / (open A 72%, dense B 50%) — the target corner (open ≥ 80, dense B ≥ 60) was never reached.

## Key structural findings for the next design decision
1. The reaction envelope (A) is distance-gated, not terrain-gated: sub-350 launches leak in the open too, once a bomber survives to get there. What terrain actually gates is *surviving the approach* — that asymmetry is real but only worth ~15-25 pts.
2. PD is now correctly "strong when tracking" (`projectileKillChance` 0.7 vs tracked salvos in the open) — that alone held the open end at ~82%.
3. Cover-point stability trades ends: rescoring every 0.5 s (baked) preserves the open-end torpedo tax; holding 2 s helps dense arrivals but shelters open bombers (flip -> 0). `ai.coverHoldTicks` exposes this.
4. Destructible terrain is a RAILGUN buff as implemented — if the flip must survive destructibles (§8), cover destruction economics likely need a design pass (e.g., slower rock HP scaling, debris that threatens the shooter side too).

## Built & verified
(unchanged from previous entry — sim core, autopilot, terrain, LOS, all weapons + new mechanics, role AI, harness, 8/8 tests, browser clean, deterministic, ~0.5-2 s/match headless)
