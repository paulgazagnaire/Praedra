# STATUS

**Phase:** Combat-sandbox build complete (user-directed pivot from the flip gate). The game is playable: RTS controls, fog of war, universal friendly fire, always-destructible terrain, detection, flanking, cover demolition.

## What's in the build (all verified)

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
- `harness/tests.mjs`: **12/12 pass** (original 8 hardened for new semantics + detection-gate, friendly-fire-railgun, asteroid-splits, order-move-hold).
- Headless Chromium: zero console errors; 13/13 mandatory + 9/9 supplementary UI assertions (selection, orders, active-pause ordering, fog subset checks, spectate).
- Determinism from seed: verified. Matches resolve 47–172 s typical (timer cap 300 s per PRD band); zero draws/errors in latest batteries; ~4–6 asteroid shatters per match.

## Known items / notes
- Balance: the flip-gate question is parked (see LESSONS.md and git history for the full record); current AI-vs-AI win rates lean RAILGUN at most densities. The sandbox direction (detection, FF, demolition) reshuffled balance — retune when the flip work resumes.
- A fully-static scenario where a rock permanently blocks the only sightline is a stalemate by design (never-detected targets can't be engaged); irrelevant in real matches (documented in SIM_CONTRACT).
- `destructibleAsteroids` config key is ignored (kept for compat).
