# Praedra Combat Doctrine

This is the doctrine reference for Praedra's team AI. It translates real-world tactics
(air/sea/land/RTS-AI/space) into behavior specs for a 2D deterministic sim where combat
is decided by turret arcs, sensor emissions, cover, and point-defence throughput rather
than raw DPS math.

**Grounding.** Praedra already ships a `v1` (legacy per-ship greedy AI) and a `v2`
("veteran doctrine") team AI, selected per team via `config.doctrine.{A,B}`. Where a
tactic below is already implemented, its entry says **[v2: `field.name`]** and points at
the real config field / function so an implementer can find and tune it. Where a tactic
is not yet implemented, its entry says **[GAP — proposed]** and gives a concrete spec —
new fields following the existing `ai2.*` naming convention, ready to wire into
`aiXxxV2` / `updateDoctrine`. Everything here targets the real mechanics documented in
`docs/SIM_CONTRACT.md`: 60 Hz fixed tick, `state.rng` (mulberry32, seeded) for **all**
randomness, `state.time`/`state.tick` for **all** timing — no wall clock, no
`Math.random`, ever. Any spec below that needs a pseudo-random choice (a jink offset, a
feint's dice-roll) draws from `state.rng` in a fixed, id-ordered iteration each tick,
exactly like the existing heavy-rail hit-roll and asteroid-split draws — same seed,
same replay, always.

Real numbers referenced throughout (from `CONFIG_DEFAULTS`): battleship turn rate
`turnMax` 0.14 rad/s, turret `slewRate` 0.35 rad/s, `arcHalfWidth` 2.36 rad (~135°,
so bow pair is blind in a ~90° stern cone and the aft turret blind in a ~90° bow cone);
heavy-rail `minRange` 420 / `maxRange` 3200, hard `trackClasses` gate (destroyer,
frigate, battleship only — bombers/interceptors are structurally immune), speed gate
`speedFullTrack` 55 / `speedNoTrack` 85 (frigate cruise 85 sits exactly at the
untrackable threshold); destroyer railgun `maxRange` 700, fixed-forward `arc` 0.10 rad;
frigate torpedo `range` 950, `salvo` 2; bomber `bomb.launchRange` 380; interceptor
`gatling.range` 95; PD `reactionSeconds` 0.5 continuous LOS-track before it may engage,
throughput-capped at `pdSlots` (battleship 4 / frigate 3 / destroyer 2 / bomber &
interceptor 0); detection `visibleRange = signature × lerp(thrustMultMin 0.6,
thrustMultMax 1.2, throttle)`.

---

## 1. Doctrine principles

Ten cross-domain principles, ranked by how directly they exploit a Praedra-specific
mechanic. Each per-class/fleet section below cites back to these.

### 1.1 Concentration of Force / Lanchester's Square Law
*Provenance:* F.W. Lanchester's aimed-fire attrition laws (1916); Nelson's
concentration at Trafalgar (1805); USN fire-distribution studies (1907/1914) showing
two ships piling on one target barely outscore one; Dicta Boelcke Rule 8 ("don't pile
on"); StarCraft-community "focus fire" and the SparCraft NOKAV (no-overkill) baseline.
*Principle:* under aimed-fire attrition, combat power scales with the **square** of
concentrated force — mass superiority against an isolated fraction of the enemy, and
never spend a second/third shooter on a target already-doomed by committed damage.
*Praedra hook:* **[v2: `updateDoctrine`, `ai2.focusHpWeight`, `ai2.focusIsolationWeight`,
`torpsCommitted`]** — implemented as a shared per-team blackboard (`state.doctrine[team]`)
recomputed every `ai2.focusEvery` ticks, ranking enemies by `distance + classPriority +
isolationBonus − woundedBonus` and a torpedo no-overkill ledger that spills fire to the
next target once a victim's HP is already spoken for. See §3.1 for the full formula.

### 1.2 Defeat in Detail
*Provenance:* Napoleonic operational practice; Frederick's oblique order at Leuthen
(1757, refuse one wing, mass the other); Hannibal's double envelopment at Cannae;
Jeune École (cheap craft deny a battle-line freedom of maneuver); "defeat in detail" as
a named military-theory principle.
*Principle:* isolate and destroy the weakest supported fraction of the enemy fleet
before it can be reinforced, rather than engaging the whole line evenly.
*Praedra hook:* **[v2: `alliesNear`, `ai2.isolationRadius`, `ai2.packEscortMax`]** — a
target's "support" is the count of allied capitals within `isolationRadius` (900px);
low-support capitals are preferred doctrine focuses, and the wolfpack dive (§3.3) only
triggers on a victim with `alliesNear(victim) <= packEscortMax` (default 1) so frigates
never knife-fight a screened battle line.

### 1.3 Emission Discipline (EMCON)
*Provenance:* USN/submarine EMCON doctrine; Dicta Boelcke Rules 1-2 (secure the
advantage — altitude/sun — before attacking); Atomic Rockets/Project Rho's "no stealth
in space, but coasting still hides you" analysis.
*Principle:* detection is driven by emissions (a burn), not just distance — throttle
back to stay under the enemy's noticing threshold, and only spend a plume when the
attack commits.
*Praedra hook:* **[v2: `emconCap`, `ai2.emconNear`, `ai2.emconFar`]** — `visibleRange =
signature × lerp(0.6, 1.2, throttle)` is a hard engine mechanic; `emconCap` returns a
speed cap that clips a ship to `maxCruiseSpeed × 0.35` (coast on momentum) whenever the
nearest known enemy sits between `visCoast×emconNear` and `visBurn×emconFar` of us — the
exact band where burning would light us up but coasting keeps us dim. Used today by
destroyer masked-approach and frigate standoff-transit; not yet by bomber/interceptor
staging (see §2 gaps).

### 1.4 Cover / LOS Exploitation
*Provenance:* Savo Island ambush doctrine (cold approach, snap-illuminate, strike before
reaction); hull-down/defilade tank gunnery; reverse-slope defense (Wellington,
Rommel); attack-helicopter pop-up/unmask-fire-remask NOE doctrine.
*Principle:* an asteroid's LOS shadow is smoke, a ridge, and a screen all at once — hold
behind it until the shot is ready, expose for the minimum instant needed to fire, then
duck back.
*Praedra hook:* **[v2: `coverHop`, `coverPoint`, `openLaneBiasV2` (destroyer defilade
anchor)]** — `coverHop` walks a ship from rock-shadow to rock-shadow toward a target
that outranges it; `openLaneBiasV2` biases a destroyer's holding position to mask it
from a *second* enemy capital while keeping its own firing lane on the *first* — a
direct hull-down implementation. Crucially: in this sim PD tracking resets **only on an
LOS break** (`updatePD`: `tr[key]` deleted the instant `losClear` fails), not on bearing
rate or jink — so "pop-out beats the 0.5 s reaction" is real and mechanical, but jinking
in the open while still in LOS does **not** reset a PD lock. Any tactic that assumes
jink-resets-track (see several air-domain sources below) must be corrected to
LOS-break-resets-track for Praedra; §2.4/§2.5 specs reflect the corrected mechanic.

### 1.5 PD Saturation Timing
*Provenance:* USAF Wild Weasel/SEAD (expend the defender's engagement capacity just
ahead of the real strike); Aegis shoot-look-shoot slot allocation debates; WWII
artillery Time-on-Target (stagger release so unequal flight times converge); U-boat
wolfpack convergent strikes.
*Principle:* point defence has a finite slot count and a mandatory acquisition delay —
saturate it with more simultaneous *tracked* threats than it has slots, or force it to
spend its reaction window on cheap ordnance right before the real shot lands.
*Praedra hook:* **[v2: `ai2.packBearingSpread`, `ai2.volleyWaitMax`, `doc.volleyGo`,
`tryTorpedoV2` volley hold]** — frigates hold a loaded tube (up to `volleyWaitMax` 3.0 s)
until `>= 2` boats are ready, then release together; wolfpack divers spread attack
bearings by `packBearingSpread` (1.0 rad) around the victim so no single PD arc/escort
covers every axis. Because PD's threat list is sorted **by distance, not time-to-impact**
(`updatePD` threat sort), the true saturation lever is *count of simultaneously-tracked
threats*, not exact-tick arrival sync — see §3.2 for the corrected formula.

### 1.6 Range Control & Turret-Arc Exploitation
*Provenance:* Crossing the T (Tsushima 1905, Surigao Strait 1944); WWII stand-off
battleship gunnery; Energy-Maneuverability boom-and-zoom (Boyd/Christie); RTS kiting
(Uriarte & Ontañón) and stutter-step micro.
*Principle:* a slow platform with a range/arc advantage should hold the band where it
can hit and not be hit; a fast platform with an agility edge should never loiter inside
a slower, harder-hitting platform's engagement envelope.
*Praedra hook:* **[v2: `heavyRail.standoffFrac`/`standoffMin`, `ai2.bbBroadside`,
`bbBroadsideFace`]** — the battleship already holds `[standoffMin 1500,
maxRange×0.85≈2720]` when it has a firing solution and turns beam-on
(`bbBroadsideFace`) to put 3 turrets instead of 2 on the target; it also creeps directly
away from any close-threat centroid inside its dead zone while keeping the beam locked.
The frigate wolfpack dive (§1.2/§3.3) is the mirror-image exploit: it deliberately
holds **cruise ≥ 85** (the `speedNoTrack` gate) all the way into the dead zone so heavy
rail's speed-gate hit-chance is factor 0 the whole approach.

### 1.7 Multi-Axis Attack
*Provenance:* Bracket/pincer two-ship offense (NATO BFM); double envelopment (Cannae,
Kesselschlacht); PT-boat anvil attacks at Guadalcanal; oblique-order refused flank.
*Principle:* split attackers across bearings wide enough apart that no single
turret/PD arc/escort can cover both, forcing the defender to choose which axis to
answer.
*Praedra hook:* **[v2: `ai2.packBearingSpread`, `ai2.anvilMinBombers`, bomber `c.anvil`
id-parity split]** — wolfpack divers (§3.3) and, at `anvilMinBombers`+ strength, bomber
waves split into two flank axes by ship-id parity, each converging on the WWII blind
arcs (bow pair blind astern / aft turret blind over the bow) simultaneously rather than
sequentially.

### 1.8 Demolition as Maneuver
*Provenance:* SOSRA combined-arms breach doctrine (suppress-obscure-secure-reduce-
assault); WWII UDT ("frogman") beach-obstacle clearance opening a lane for the main
assault at a chosen time and place.
*Principle:* a blocking obstacle is not just terrain to route around — it is a target
whose destruction, correctly timed, opens a firing lane exactly when the main force
needs it.
*Praedra hook:* **[v2: `ai2.bbDemolishMaxR`, `ai2.bbMinGap`, `ai.rockShootSeconds`,
`ai.rockClearMaxRadius`, `blastCoverNearGhosts`, `clearTransitLane`]** — capitals already
demolish blockers after `rockShootSeconds` of no clean shot, and the battleship alone
may blast rocks up to `bbDemolishMaxR` (320, bigger than the general
`rockClearMaxRadius` 200) to keep its ponderous hull from ever threading a gap narrower
than `bbMinGap` (300). §2.1/§3.5 add a proposed sequencing gate so a strike package
doesn't enter a lane before it's actually clear.

### 1.9 Friendly-Fire Deconfliction
*Provenance:* Dicta Boelcke Rule 8 (avoid multiple attackers converging fire); naval
gun-target-line safety / danger-close doctrine (never let a friendly sit on the
firing line of a wide-dispersion or AOE weapon).
*Principle:* friendly fire is real damage — a shooter must check its own gun-target
line and AOE radius for friendly hulls before committing, not just pick the
highest-value enemy target.
*Praedra hook:* **[GAP — proposed]**. Friendly fire is universal and already modeled at
the engine level (`docs/SIM_CONTRACT.md` §Friendly fire: railgun/gatling hit the FIRST
hull on the ray regardless of team; heavy-rail slugs pierce and roll against every
hull, any team; AOE always damages friendlies in radius). No doctrine layer currently
vetoes a shot for this. See §3.7 for a concrete pre-fire check.

### 1.10 Defense in Depth / Screening
*Provenance:* WWII radar-picket destroyers (forward sensor line, absorb the first
strikes); the "Big Blue Blanket" layered CAP at Okinawa; Luftwaffe finger-four mutual
lookout (never solo a unit with a blind arc); Jeune École (a gunline is structurally
naked against cheap fast craft and must never sail without a screen).
*Principle:* cheap, sensor-capable or agile units stationed ahead of / around a
high-value platform absorb the first contact and buy warning time; a platform with a
structural blind spot must never operate without a partner watching it.
*Praedra hook:* **[v2 partial: `ai2.screenBomberRange`, frigate `escortRange`,
`ordnanceNear`]** — interceptors already screen bombers from enemy lights within 900px,
and frigates escort a destroyer's threat-facing quarter within `escortRange` (220) when
the enemy still fields PD-defeatable ordnance. §2.1/§2.2/§3.4 add the still-missing
pieces: a hard "battleship never advances without escort" gate and a forward-picket
waypoint.

---

## 2. Per-class doctrine

### BATTLESHIP — artillery platform, structurally blind to small/fast craft

| Tactic | Provenance | Status |
|---|---|---|
| Crossing the T / Battle-Turn Arc Maintenance | Tsushima 1905, Surigao 1944; Jutland-era battle-turns | **[v2: `bbBroadsideFace`, `ai2.bbBroadside`]** |
| Outrange / Stand-off Gunnery | Iowa-class rangekeeping doctrine | **[v2: `heavyRail.standoffFrac`/`standoffMin`]** |
| Hull-Down / Defilade | WWII armor doctrine | **[v2: partial via destroyer `openLaneBiasV2`; not yet on the battleship itself — GAP]** |
| Jeune École (never sail without escort) | French torpedo-boat doctrine, 1880s | **[GAP — proposed]** |
| Rocking Ladder / Ranging Salvo | USN WWII gunnery doctrine, 1944 | **[GAP — proposed]** |
| Counter-Battery / Shoot-and-Scoot | WWII/Cold War artillery doctrine | **[partial — GAP for the explicit "just fired" trigger]** |

**1. Crossing the T / predictive arc maintenance — [v2 implemented].**
`bbBroadsideFace(state, ship, aim)` turns the hull so the beam (all 3 turrets bear) faces
the primary target whenever `pickHeavyRailPrimary` holds a solution, instead of pointing
the bow/stern (only 2 turrets, or the aft turret's dead cone) at it. Given `turnMax`
0.14 rad/s, this must commit to the turn well before the geometry actually changes —
implemented as continuous re-evaluation every tick rather than a reactive snap, so the
hull is always converging toward beam-on. **Tuning note:** the current trigger is
"holds a solution now"; a straightforward improvement is to begin the turn as soon as
`timeToEnterDeadzone(target_bearing_rate) <= bearingError / turnMax`, i.e. budget the
~22 s full-reverse time before the target's bearing actually crosses into the bow/stern
cone, not after.

**2. Outrange / stand-off — [v2 implemented].** `heavyRail.standoffMin` (1500) /
`standoffFrac × maxRange` (~2720) bound the hold range: never close inside 1500 while
lacking a clean shot (regain LOS/detection instead), never retreat outside ~2720 while
already holding a solution (the "huge-range delete" — don't waste the one asset that
outranges everything). This also keeps the ship outside destroyer railgun (700) and
frigate torpedo (950) reach by a wide margin whenever it's not already committed to a
knife-fight.

**3. Hull-down for the battleship itself — [GAP, proposed extension of
`openLaneBiasV2`].** Currently only the destroyer masks itself from a *second* capital
while holding a lane on the first. Extend the same `openLaneBiasV2` call to
`aiBattleshipV2`'s standoff branch: when two+ enemy capitals are detected, bias the
battleship's holding point toward a position where an intact asteroid sits on the LOS
line to the *non-primary* capital, using the same rock-shadow search already used for
`coverHop`/`openLaneBiasV2`. Config: reuse `ai.coverMinRange`, no new field needed —
just call the existing bias function unconditionally on the battleship path, not only
the destroyer path.

**4. Jeune École escort gate — [GAP, proposed].** The battleship's structural weakness
(hard class-gate: heavy rail literally cannot damage bombers/interceptors; the
speed-gate makes fast frigates untrackable) means it must never advance alone into a
zone with detected small/fast craft. Proposed field `ai2.bbMinEscort` (default 1): in
`aiBattleshipV2`'s hunt/advance branch (the `!enemies.length` early-out and the
no-primary transit branch), check
`escortCount = alliesNear(ship, own-team small/fast craft within 1500px)`; if
`escortCount < bbMinEscort` **and** any enemy bomber/interceptor is currently detected,
clamp `speedCap` to 0 (hold station) instead of continuing to advance, mirroring the
existing `bbWellSkirtFrac`-style defensive override. This is a state check, not a new
state machine — same tick, deterministic, no RNG.

**5. Rocking Ladder / ranging salvo — [GAP, proposed, cosmetic/optional].** Real-world
gunnery opens with a bracketed spread and walks onto the target over several salvos;
Praedra's heavy rail already resolves hit chance as a flat per-hull roll
(`evasion × speedFactor`), so this tactic doesn't change outcomes, only presentation/
pacing. Not recommended for the core hit model (would add RNG-visible "miss streaks"
without changing win rates), but worth keeping in mind if a future aim-error layer is
added: any such layer must derive its correction step from `state.rng` in turret-index
order to stay deterministic, exactly like the existing per-hull roll order.

**6. Counter-battery shoot-and-scoot — [partial, GAP for explicit trigger].** The
existing "creep away from close-threat centroid while keeping the beam locked" behavior
already produces scoot-like repositioning, but it's triggered by *proximity of a
knife-fighter*, not by *having just fired*. A cheap addition: after any turret fires
(`t.cool` resets to `cooldown`), nudge `ship.ai.anchorBias` laterally by a fixed offset
for the next few ticks so a shooter that just revealed itself via muzzle flash isn't
sitting exactly where return fire would expect it — low priority since the battleship's
signature (4200) already makes it detected well before it fires.

---

### DESTROYER — anti-capital sniper, fixed-forward gun, glass everywhere else

| Tactic | Provenance | Status |
|---|---|---|
| Masked approach / EMCON vs outranging guns | Hutier infiltration; EMCON doctrine | **[v2: `coverHop`, `emconCap`]** |
| Hull-down defilade anchor | WWII armor doctrine | **[v2: `openLaneBiasV2`]** |
| Finger-Four / never solo | Luftwaffe finger-four, 1938 | **[v2 partial: frigate escort; GAP for a hard "never solo" gate]** |
| Thach Weave (mutual six-o'clock cover) | Thach, 1941; VF-3 at Midway | **[GAP — proposed]** |
| Scissors reversal vs a circling interceptor | USN/USAF BFM doctrine | **[GAP — proposed]** |
| L-Shaped Ambush base-of-fire role | Small-unit ambush doctrine | **[GAP — proposed]** |
| Suppressive fire for a vulnerable ally | US Army suppression doctrine | **[GAP — proposed]** |

**1. Masked approach + EMCON — [v2 implemented].** `aiDestroyerV2`: whenever the target
is a battleship beyond railgun range (`d > RG.maxRange × 1.05`), the destroyer calls
`coverHop` to walk rock-shadow to rock-shadow instead of a straight burn, and applies
`emconCap` to throttle down inside the detection band. No cover on the map falls back to
a straight approach with `jink: true`. This is the Hutier-infiltration + EMCON-ambush
combination from §1.3/§1.4 already wired end-to-end for the one class whose gun is
badly outranged by the battleship.

**2. Hull-down defilade anchor — [v2 implemented].** `openLaneBiasV2(state, ship,
target, second)` biases the destroyer's standoff point so an asteroid masks it from
whichever enemy capital *isn't* its current gun target, refreshed every 30 ticks
(staggered by `ship.id % 30` to spread the cost). Directly the hull-down/reverse-slope
principle (§1.4): expose only to the threat you're already engaging.

**3. Never-solo escort gate — [GAP, proposed].** Because 100% of the destroyer's hull
outside a 0.10-rad bow cone is pure offense-blind (only its `pdSlots: 2` cover it),
research strongly recommends never dispatching one without a trailing partner. Today
the frigate escorts opportunistically (`ordnanceThreat` check in `aiFrigateV2`), but
nothing on the destroyer's side *requires* it. Proposed: extend the `ai2.bbMinEscort`
pattern (§2 Battleship #4) to destroyers as `ai2.ddMinEscort` — when a destroyer is
undetected-by-doctrine as having a frigate within `escortRange × 2`, and an enemy
interceptor/bomber is detected within `pd.trackRange`, reduce `speedCap` and prefer
falling back toward the nearest friendly frigate over continuing to close on the gun
target. Deterministic, no new RNG.

**4. Thach Weave — [GAP, proposed].** Pair destroyers (or a destroyer+frigate) into a
`weaveElementId` at fleet setup. Each element member tracks a `sixOClockThreat`: the
nearest detected enemy inside a cone centered on the *partner's* stern within
`pd.trackRange`. When populated, the free element member's `focusFor('gun')` result is
overridden for that tick to aim at the threat's predicted crossing point (current
position + `enemy.vx/vy × leadTime`, `leadTime = dist / railgun.slugSpeed`), so a
pursuer chasing the bait ship's tail flies through the free ship's fixed-forward cone.
Config: `ai2.weaveThreatCone` (rad, e.g. 0.9), `ai2.weaveMaxRange` (matches
`pd.trackRange` 700). Trigger and lead computation are pure state math — no RNG needed.

**5. Scissors reversal — [GAP, proposed].** When a fast small craft (interceptor) is
inside `railgun.minRange` and its bearing-rate (`d(bearing)/dt`, computed from two
consecutive ticks) crosses zero — i.e. it's circling — reverse the destroyer's own
turn direction to re-point its bow at the interceptor's predicted position rather than
chasing its current one. This can never win the turn-fight outright (turnMax dwarfs the
destroyer's own agility budget) but repeatedly resets the interceptor's own approach
geometry and, more importantly per §1.4, forces the interceptor to re-enter the
destroyer's `pd.trackRange` LOS cone from a fresh angle each reversal — useful chiefly
as the trigger that invites the Thach-Weave partner in (#4) rather than as a
standalone win condition.

**6. L-shaped ambush base-of-fire — [GAP, proposed].** At a scripted chokepoint
(asteroid-gap corridor from `bbGapLookahead`-style analysis), station a destroyer
directly on the lane centerline behind cover with its bow already aimed down the lane
(`arc` 0.10 is narrow — this only works pre-aimed), while interceptors take flanking
positions perpendicular to the lane. Candidate flank positions are rejected if the
segment from flanker to the lane's midpoint passes within `ship.def.radius +
target.def.radius + safetyMargin` of the base-of-fire ship's position (see §3.7's
friendly-fire check — this is the same test, reused at placement time instead of
fire time).

**7. Suppressive fire — [GAP, proposed].** When a bomber is in its `run` mode
(`ship.ai.mode === 'run'`, exposed and committed to a straight vector) or a
demolition-tasked ship is transiting a breach lane (§3.5), any destroyer/battleship
with a legal shot on a threat that currently has LOS to that vulnerable ally gets a
target-priority bonus in `focusFor`: `score -= ai2.suppressBonus` (proposed, e.g. 400,
same units as `focusHpWeight`) if `hasLOS(candidateThreat, vulnerableAlly)`. This does
not require the shot to land — forcing the threat to dodge/reposition breaks its own
firing solution, which is the point.

---

### FRIGATE — torpedo specialist, fast light capital, the wolfpack's teeth

| Tactic | Provenance | Status |
|---|---|---|
| Wolfpack dead-zone dive + synchronized volley | Kriegsmarine Rudeltaktik | **[v2: `packMinFrigates`, `packDiveRange`, `packBearingSpread`, `volleyWaitMax`]** |
| Long Lance (launch before burn, close under the guns) | IJN Type 93 doctrine | **[v2 partial: point-blank release bypasses volley-hold; explicit launch-before-burn sequencing is a GAP]** |
| Kiting via the speed gate | RTS kiting (Uriarte & Ontañón); Energy-Maneuverability | **[v2: dive holds cruise ≥ speedNoTrack the whole approach]** |
| Torpedo Spread (dual lead bias) | US submarine doctrine, WWII Pacific | **[GAP — proposed]** |
| Smoke-Screen / LOS-cover withdrawal | 2nd Sirte 1942; Samar 1944 | **[GAP — proposed]** |
| Distributed Lethality (cap committed inventory) | CIMSEC "VLS hole" critique | **[GAP — proposed]** |

**1. Wolfpack — [v2 implemented].** `updateDoctrine` designates a pack victim (a
capital with `alliesNear <= packEscortMax`) and assigns all frigates with
`doc.packIds`; `aiFrigateV2` spreads their dive bearings by `packBearingSpread` (1.0
rad) around the pack's mean approach axis, and each dives fast + jinking through the
speed-gate band, settling only inside `heavyRail.minRange × 0.95` (the main-battery-proof
knife-fight ring: past PD ship-range ~156px but short of 420). `tryTorpedoV2` holds a
loaded tube for up to `volleyWaitMax` (3.0 s) so `>= 2` ready boats release together —
saturating a limited PD slot pool with simultaneously-tracked torpedoes (§1.5).

**2. Long Lance sequencing — [GAP, proposed refinement].** Real doctrine fires the
torpedo *before* igniting the burn that would reveal position. Today's dive already
holds cruise speed the whole way (so there's no separate "burn reveal" moment — the
frigate is lit up from the start of the dive by design, trading stealth for the
speed-gate immunity). The genuine remaining gap is standoff-range engagements (not
pack dives): in `aiFrigateV2`'s non-pack branch, when a frigate is at `frigateStandoff`
under EMCON (`speedCap` from `emconCap`) and a torpedo shot is already valid
(`torpedoPickV2` returns a target), fire the salvo *before* any subsequent nav update
raises `speedCap` back up — i.e. sequence "torpedo release" ahead of "resume higher
transit speed" in the same tick's execution order, so the plume spike never precedes
the shot.

**3. Torpedo spread (dual lead bias) — [GAP, proposed].** `launchTorpedo` currently
aims every torpedo in a salvo at the same predicted intercept point. Add a per-tube
guidance bias: tube 0 leads assuming the target holds `(vx, vy)` (current behavior,
`k=1.0`); tube 1 (the `salvoGap`-delayed second shot in a 2-torpedo salvo) biases its
lead point toward the nearest LOS-blocking asteroid within `torpedo.turnRate`-reachable
arc of the target's position (the rock a jinking target is likeliest to duck behind),
using `k=1.4`. Both still respect `torpedo.turnRate` (1.1 rad/s, loose tracking) and
`lockLossSeconds` (0.5 — LOS blocked longer than this sheds the lock). Deterministic:
the "nearest reachable rock" query is a pure geometric search, no RNG.

**4. Kiting via the speed gate — [v2 implemented, documented for clarity].** This is
Praedra's sharpest version of "kiting" (§1.6): a frigate at cruise 85 sits exactly at
`heavyRail.speedNoTrack`, so **for the specific weapon that most threatens it**, holding
speed is a full counter, not a maneuver — `canKite` reduces to "don't slow down."
Destroyer railgun and PD, by contrast, have no speed gate — a frigate deep in a
destroyer's 700-range envelope is not protected by speed alone.

**5. Smoke-screen / cover withdrawal — [GAP, proposed].** During a retreat (a damaged
friendly capital's `hp < 0.3×maxHp` retreat branch, already present on the battleship),
assign a rearguard frigate a `retreatCoverRockId`: the asteroid currently on the
segment between the pursuer and the retreating friendly, found via
`firstRockOnRay(state, pursuer.x, pursuer.y, friendly.x, friendly.y)`. Tag that rock id
in a per-team `protectedCoverIds` set for the retreat's duration; demolition logic
(`blastCoverNearGhosts`, `clearTransitLane`, `bbDemolishMaxR` targeting) must skip any
rock id present in that set, inverting the normal "clear my lane" default specifically
while it's shielding a friendly withdrawal.

**6. Distributed lethality cap — [GAP, proposed].** `doc.volleyGo` currently commits
every ready frigate (`cool.torp <= 0 && torpAmmo !== 0`) once `>= 2` are loaded, with no
ceiling. Add `ai2.maxVolleyFraction` (e.g. 0.6): when building the volley group in
`updateDoctrine`, cap the committed count at
`ceil(totalReadyFrigates × maxVolleyFraction)`, holding the remainder in reserve rather
than emptying the entire torpedo-capable fleet into one strike — so a single AOE
counter-hit can't zero out the team's whole stand-off punch at once. Selection of
*which* frigates go first should be deterministic (e.g. lowest `ship.id`), not random.

---

### BOMBER — unguided AOE, needs a steady run-in, fragile

| Tactic | Provenance | Status |
|---|---|---|
| Stage → approach → run → break (jink-then-steady + egress) | WWII flak-evasion + defensive-break doctrine | **[v2: `ship.ai.mode` FSM in `aiBomberV2`]** |
| Pop-up / unmask-fire-remask | NOE attack-helicopter doctrine | **[v2 partial via `break`'s duck-behind-rock; corrected for LOS-reset mechanic]** |
| EMCON on the approach leg | EMCON doctrine | **[v2: `emconCap` in `approach` mode]** |
| Anvil (two-axis wave split) | PT-boat/Guadalcanal anvil attacks; oblique order | **[v2: `c.anvil`, `ai2.anvilMinBombers`]** |
| Gravity-well rock-hugging approach | Orbital slingshot (adapted) | **[GAP — proposed]** |
| Feint / demonstration element | US Army FM 90-2 deception doctrine | **[GAP — proposed]** |

**1. Stage/approach/run/break FSM — [v2 implemented].** `aiBomberV2`'s `ship.ai.mode`
cycles `stage` (hold behind cover, or at `stageRange` if none) → `approach` (once the
team is `committing`; flank offset if the wave is split) → `run` (steady vector,
`arrive: false, jink: false`, fires when in `bomb.launchRange` and LOS-clear, breaks
after 2 bombs or LOS loss) → `break` (duck behind the nearest rock under 380px if one
exists and a threat is nearby, else a fixed diagonal jink-away). This is exactly the
jink/stage-then-steady-then-break pattern from WWII bomb-run doctrine (§1.4), with the
"jink" replaced by "stay covered" during staging rather than an in-flight weave (see
correction below).

**2. Pop-up / unmask-fire-remask, LOS-corrected — [v2 partial, correction applies to
future tuning].** Because PD tracking in this sim resets **only on LOS break**
(`updatePD`), the bomber's actual counter to PD is minimizing *time between breaking
cover and releasing*, not in-flight jinking during the exposed run (`run` mode already
sets `jink: false` — correct, since jinking without breaking LOS wastes maneuver budget
for zero tracking-reset benefit and only adds aim error against the bomber's own
target). The lever that matters is `bomb.runStartFactor` (1.6× `launchRange`, i.e. ~608
px) — the range at which the bomber commits to the steady run once LOS is clear. On
dense maps this run is short (sighted late → short pop-out run, PD often can't complete
`reactionSeconds` before bombs-away); on open maps it's a long straight exposure — this
is by design the torpedo-bait window §1.5/§3.2 exploits with a synchronized frigate
volley timed to land while the bomber is mid-run.

**3. EMCON on approach — [v2 implemented].** `approach` mode applies `emconCap` while
`!thr` (not currently threatened), same mechanism as frigate/destroyer.

**4. Anvil split — [v2 implemented].** At `anvilMinBombers`+ staged bombers,
`c.anvil = true` and each bomber's flank direction is set by `ship.id % 2` instead of
the shared wave `flank` call, producing two attack axes instead of one.

**5. Gravity-well rock-hugging approach — [GAP, proposed].** Gravity wells
(`gravity.G`, `sourceMinRadius` 100+) already bend bomb/torpedo trajectories and drag
ships; nothing in bomber routing currently *prefers* a well-assisted curve as a
lower-thrust (lower-signature) path. Proposed: in the `stage`→`approach` transition,
when comparing a direct route to one that clips the edge of a BIG asteroid's
`wellReach`, prefer the well-assisted route if its total required thrust-tick count
(a proxy already computable from `routeAround`'s waypoint list) is lower, trading a
slightly longer flight for a materially dimmer plume during the approach leg. Marginal
value given bombers already have very high signature-to-stealth headroom (900 vs.
battleship 4200), but cheap to add opportunistically wherever `routeAround` already
samples candidate waypoints.

**6. Feint / demonstration element — [GAP, proposed].** Add a per-wave
`ai2.feintFraction` (e.g. 0.15— one bomber in a 6-7 strong wave): a designated feint
bomber burns hard (no EMCON cap, deliberately high signature) toward a secondary
asteroid lane different from the real strike axis, baiting enemy interceptors/frigates
to reorient. The real strike package's `approach`→`run` transition gates on
`observedEnemyReorientedTowardFeint(state) OR feintTimeout` (a fixed tick budget, e.g.
`ai2.feintTimeoutTicks` 180 = 3 s) rather than committing immediately — since the sim
is fully deterministic and every ship's heading/throttle is visible in `state`, "did the
enemy reorient" is a plain state read (compare a tracked enemy's heading-delta since the
feint began against a threshold), no RNG needed.

---

### INTERCEPTOR — tiny dogfighter, extreme agility, one-shot torpedo

| Tactic | Provenance | Status |
|---|---|---|
| Screen bombers from enemy lights | Radar picket / finger-four mutual lookout | **[v2: `ai2.screenBomberRange`]** |
| Dive only alongside live ordnance (SEAD/saturation) | Wild Weasel; Aegis slot allocation | **[v2: `ordnanceNear`, `ai2.diveNeedsSaturation`]** |
| Strafe-in / strafe-out (energy fighting, boom-and-zoom) | E-M theory (Boyd/Christie); P-38-vs-Zero doctrine | **[v2: `strafe_in`/`strafe_out` mode]** |
| Infiltration past the battleship's hard class-gate | Hutier infiltration tactics | **[v2 partial — implicit; explicit routing bonus is a GAP]** |
| High/Low Yo-Yo pursuit-curve correction | USN/USAF BFM manuals | **[GAP — proposed]** |
| Lufbery Circle (outnumbered defensive ring) | WWI Lafayette Escadrille | **[GAP — proposed]** |
| PT-Boat swarm minimum bearing spread (own-craft) | Guadalcanal PT-boat doctrine | **[v2 partial — shares `packBearingSpread` mechanism via anvil; not a standalone interceptor gate — GAP for a dedicated minimum]** |
| Drag-and-Bag (paired with a frigate dragger) | Cold War NATO/USN 2-vs-many split | **[GAP — proposed]** |

**1. Screening — [v2 implemented].** `aiInterceptorV2` hunts the nearest detected
"light" (bomber/interceptor) within 700px whenever one is present, keeping enemy strike
craft off the team's own bombers — the finger-four "never leave a blind arc uncovered"
principle applied at the fleet level (§1.10).

**2. Saturation-gated diving — [v2 implemented].** `ordnanceNear(state, team, tgt,
range)` checks for live friendly torpedoes/bombs already inside a target's PD bubble;
`ai2.diveNeedsSaturation` gates the committed strafe run on this, so interceptors enter
a defended bubble only while PD's slots are already busy with projectiles (Wild
Weasel/SEAD, §1.5) — "strike-package timing, not lone heroics," per the existing code
comment.

**3. Strafe-in/strafe-out — [v2 implemented, is Praedra's energy-fighting analogue].**
The interceptor dives to `strafeDivePoint` (60px past the target), fires the gatling,
and once `d < gatling.range × 0.8` snaps to `strafe_out` (a lateral+outbound vector to
`strafeExitRange × 1.6`) rather than orbiting inside the target's PD bubble — the direct
analogue of "one fast slashing pass, extend away, never turn-fight in the enemy's
envelope." **Gap for refinement:** the mode switch is purely range-based
(`d < gatling.range × 0.8`), not dwell-time-based; a target with a firing solution
already open on the interceptor (e.g. `pd.reactionSeconds` already elapsed) should
force an immediate egress regardless of range, since lingering in a *tracked* bubble is
the actual danger, not proximity per se. Proposed: track
`ship.ai.pdTrackedSince` (mirroring the target's own `pdTrack` state, which is already
inspectable via `state`) and force `strafe_out` the instant continuous tracking exceeds
`pd.reactionSeconds × 0.8`, independent of the dive-point range check.

**4. Infiltration past the hard class-gate — [v2 implicit, GAP for an explicit
bonus].** Heavy rail's `trackClasses` gate already makes interceptors (and bombers)
structurally invulnerable to the battleship's main guns; nothing currently routes an
interceptor *through* a battleship's engagement envelope on purpose to reach a softer
target behind it, though nothing stops it either (no active penalty is applied there
today). Proposed: in `routeAround`'s waypoint cost for interceptors/bombers only, apply
a **negative** cost (a bonus) to any candidate segment that passes within a
battleship's heavy-rail range band but *outside* PD's `trackRange`/`range` bubble —
explicitly preferring the free lane past the one platform that can't touch this class,
rather than detouring around it out of habit.

**5. High/Low Yo-Yo pursuit correction — [GAP, proposed].** In `strafe_in`, the current
aim point is always the live target position (pure pursuit). Add a `closureRate`
check (`d(distance)/dt` over the last tick) against `gatling.range`: if closure would
overshoot the gatling window before `strafeDivePoint` is reached, offset the approach
laterally (perpendicular component added to the dive-point vector, scaled by
`closureRate` in excess of a `ai2.yoyoOvershootThreshold`) instead of flying a straight
collision course — cutting inside the target's turn radius rather than repeatedly
blowing through effective range against an evading bomber/frigate.

**6. Lufbery Circle — [GAP, proposed, low priority].** When a group of interceptors is
outnumbered (`livingEnemies` capitals+lights in range exceed friendly count by a
margin) and cannot disengage (e.g. screening a stationary/damaged ally), switch
formation to a shared-radius ring (`ai2.lufberyRadius`) with fixed angular spacing
(`2π / ringSize`), each interceptor's gatling aimed at whichever enemy is closing on the
ring member directly ahead of it. Break the formation once the threat ratio drops below
`ai2.lufberyBreakRatio` or the screened ally is no longer present. Genuinely low value
given interceptors already out-turn essentially everything (`turnMax` 4.2 vs. next-best
3.2) — mostly relevant if a future enemy class out-agilities the interceptor.

**7. Drag-and-Bag — [GAP, proposed, fleet-composition tactic].** Pair a frigate
"dragger" (stays lit, standoff-kiting a capital group at `frigateStandoff` to fix its
gun target) with interceptor "baggers" that go EMCON-cold and use the infiltration
routing bonus (#4) to reach the target's blind stern arc. Bagger commit trigger:
`dragger's target's heavyRail primary === dragger.id` has held true for
`ai2.dragConfirmTicks` (proposed, e.g. 60 = 1 s) — a plain state read on
`pickHeavyRailPrimary`'s result, no new detection mechanic required.

---

## 3. Fleet coordination layer

### 3.1 Focus-fire target selection (no overkill)

**[v2 implemented]** — `updateDoctrine(state)`, run once per team every
`ai2.focusEvery` ticks (staggered `(team_index × 7) % focusEvery` so both teams'
passes don't collide on the same tick):

```
for each detected enemy e:
  d       = dist(fleet_centroid, e)
  wounded = focusHpWeight * (1 - e.hp / e.maxHp)     // finish the wounded first
  support = alliesNear(e, isolationRadius) * focusIsolationWeight   // defeat in detail
  clsS    = frigate:0, destroyer:250, battleship:500, light:4000   // strike focus:
            // strip the torpedo/PD platform first, lights are never a fleet focus
  sScore  = d + clsS + support - wounded
  clsG    = battleship:0, destroyer:300, frigate:600               // gun focus:
            // biggest hull first (what heavy rail can legally track)
  gScore  = d + clsG + support*0.5 - wounded   // only scored for isCapital(e)
focusStrike = argmin(sScore); focusGun = argmin(gScore) or focusStrike
```

`focusFor(state, ship, kind)` resolves this to a live, currently-detected ship (never
a stale ghost — see §1.4's LOS-break note) with a sanity leash: a same-family target at
`< 2.5× + 400px` of the nearest reachable capital is preferred over crossing the whole
map for the doctrine focus. **No-overkill** is enforced separately per weapon family:
`torpsCommitted(state, team, targetId)` sums live, unspent friendly torpedo damage
already assigned to a target; `torpedoPickV2`'s `valid()` check skips any target where
`torpsCommitted >= hp + damage*0.5`, spilling to the next-best target instead of a
second boat redundantly finishing an already-doomed hull. **Gap:** the equivalent
ledger doesn't yet exist for heavy-rail/railgun fire or bomb salvos — `focusGun`
concentrates railgun/heavy-rail on one target by ranking, but two capitals both locked
onto the same `focusGun` id have no shared "is this already dead this tick" check the
way torpedoes do. Proposed: extend `torpsCommitted`'s pattern to a generic
`damageCommitted(state, team, targetId, weaponKind)` covering in-flight heavy-rail slugs
too (their damage-per-hit is fixed and known at launch, so this is a pure sum, no new
RNG).

**Target-lock hysteresis** — *[GAP, proposed]*. Currently `focusFor` is
recomputed fresh every tick from whatever `doc.focusStrike/focusGun` holds (itself
only updated every `focusEvery` ticks), which already provides coarse stickiness.
Finer per-ship stickiness (avoiding flicker between two near-equal `sScore`/`gScore`
enemies within the same doctrine window) is not yet applied. Proposed: cache
`ship.ai.lockedTargetId` and only replace it when the newly-scored best target beats
the currently-locked one by more than `ai2.lockSwitchMargin` (e.g. 150, same units as
the scoring formula) — a simple hysteresis band, no timers needed since the scoring
itself is already tick-quantized.

### 3.2 Strike synchronization (time-on-target)

**[v2 implemented for frigate torpedo volleys; GAP for cross-weapon TOT]** —
`tryTorpedoV2`'s volley hold (`doc.volleyGo`, `ai2.volleyWaitMax`) already synchronizes
*frigate-to-frigate* torpedo releases. Because Praedra's PD sorts its threat list **by
distance, not time-to-impact** (`updatePD`: `threats.sort((a,b) => a.d - b.d)`), the
saturation lever is *simultaneously-tracked count*, not exact-tick flight-time
matching — so the existing "hold until 2+ ready" rule already captures the load-bearing
mechanic. **Gap:** nothing currently coordinates a *cross-weapon* strike (e.g. a
frigate torpedo volley timed to land while a bomber wave is mid-`run`, so PD's slots
are already saturated with torpedoes the instant the bombs also come in range).
Proposed: expose `state.commit[team].until` (already tracked for wave timing) as a
read for `tryTorpedoV2`'s volley-hold condition — release the frigate volley when
`teamCommitting(state, team)` *and* the bomber wave's `state.commit[team].targetId`
matches the frigate's own torpedo target, using each weapon's known flight time
(`dist / torpedo.speed`, `dist / bomb.speed`) to bias which fires first so more total
ordnance is in the target's PD bubble at once than it has slots for.

### 3.3 Wolfpack bearing spread

**[v2 implemented]** — see §1.2/§1.5/§2 Frigate #1. Formula: for `n` pack members with
index `idx` in `doc.packIds` (sorted by ship id for determinism), attack bearing
`slot = centroidApproachAngle + (idx - (n-1)/2) * packBearingSpread`, dive point
`victim.pos + packDiveRange * (cos(slot), sin(slot))`. `packBearingSpread` (1.0 rad
≈ 57°) is tuned so 2-3 divers spread wider than any single PD arc's practical coverage
angle, and the same `flankOffset`/`c.anvil` id-parity mechanism gives bomber waves an
equivalent 2-axis split at `anvilMinBombers`+ strength (§1.7). **Gap:** bearing spread
is currently frigate-wolfpack-specific and bomber-anvil-specific as two separate code
paths with the same idea; a shared `computeBearingSlots(members, victim, spreadRad)`
helper would let interceptor strafe groups and any future multi-class strike packages
reuse the identical, already-validated math instead of re-deriving it.

### 3.4 Screening assignments

**[v2 partial]** — implemented pieces: interceptor bomber-screening
(`ai2.screenBomberRange`), frigate destroyer-escort (`ai.escortRange`, gated by
`ordnanceThreat`). **Gaps, proposed:**
- **Forward picket waypoint** (§1.10, §2 Battleship #4/Destroyer #3): station one
  cheap sensor-adequate unit (interceptor or frigate) at
  `mainBodyCentroid + pickDistance * unit(bearingToLastKnownEnemyContact)` ahead of the
  main body, coasting (EMCON) to stay relatively dim itself, sharing its detections
  fleet-wide the instant they occur — this already happens automatically for detection
  (`state.det{A,B}` is a team-shared picture per `docs/SIM_CONTRACT.md`), so the only
  missing piece is the *positioning* rule, not a new detection mechanic.
- **Layered CAP** (concentric intercept rings around a protected battleship): today
  interceptors screen bombers reactively wherever they are; a genuine outer/inner ring
  split (outer ring engages inbound strike craft beyond `bomb.launchRange`, inner ring
  is the battleship's own `pdSlots`) would need a per-ship `ai2.capRingRadius` and a
  rule that screening interceptors hold station on that ring rather than free-chasing
  every detected light.
- **Hard escort gates** for battleship/destroyer (§2, both classes' "never sail alone"
  entries) are the concrete, ready-to-implement version of Jeune École / finger-four
  for this fleet layer.

### 3.5 Demolition breach ticketing

**[v2 partial]** — `blastCoverNearGhosts`/`clearTransitLane`/`rockShootSeconds` already
demolish blockers reactively (after N seconds with no clean shot). **Gap:** there's no
explicit SOSRA-style sequencing (§1.8) that *times* a demolition to a strike package's
arrival rather than firing on the blocker as soon as it's annoying. Proposed: a
per-team `breachTicket = { rockId, openBy: tick, assignedShooter }` structure —
issued when a doctrine pass (§3.1) identifies a single asteroid as the sole LOS
obstruction between the fleet's committed strike axis and its focus target, with
`openBy` set to the strike package's projected arrival tick (computed from its current
speed and distance, same math as §3.2's flight-time calc). The assigned shooter (any
capital already in range of the rock) prioritizes it over its normal gun focus until
either the rock breaks or `openBy` passes. This turns today's "shoot the annoying rock
after N seconds" into "shoot the rock so the lane is open exactly when the strike needs
it," using only state already computed elsewhere.

### 3.6 Deception & feints

**[GAP — proposed]**, see §2 Bomber #6 for the concrete spec (`ai2.feintFraction`,
deterministic reorientation read instead of an RNG dice-roll). Fleet-level framing:
a designated feint sub-group is excluded from `updateDoctrine`'s normal `focusStrike`
targeting (it has its own scripted secondary-axis goal) and the real strike package's
commit gate reads the feint's effect on enemy headings directly from `state` — fully
deterministic since every ship's heading and throttle are visible sim state, not hidden
information.

### 3.7 Formation control & friendly-fire deconfliction

**[GAP — proposed]**, directly addressing §1.9. Two additions, both pure geometry
checks against already-known state (no RNG):
- **Pre-fire gun-target-line check**: before any doctrine-directed shot commits
  (railgun/heavy-rail — gatling and PD already resolve their own targets locally),
  check `firstShipOnRay(state, shooter, shooter.pos, target.pos)` for a friendly hull
  closer than the target; if found, either hold fire (if a better target exists this
  tick) or accept the risk only when no alternative target scores higher — surfacing a
  friendly-fire risk to the scoring function instead of firing blind. Since
  `firstShipOnRay` already exists and is used for the enemy case (`recv` in
  `updateGatling`), this is a call-site addition, not new machinery.
- **AOE/torpedo spacing under bomb threat**: maintain a per-formation minimum
  separation `ai2.sepMin` that snaps from a tight PD-overlap default to
  `bomb.aoeRadius * 1.5` the instant a bomber's `run` mode is observed
  targeting the formation's centroid, sidestepping ships to reopen spacing before the
  bomb salvo (`bomb.salvo` 3, `salvoSpread` 0.13 rad) can catch more than one hull.
  Reverts to the tight default once the bomber contact is lost or destroyed.

---

## 4. Sources

**Air domain**
- Energy–Maneuverability theory (Boyd/Christie) — https://en.wikipedia.org/wiki/Energy%E2%80%93maneuverability_theory
- Basic Fighter Maneuvers (yo-yo, scissors) — https://en.wikipedia.org/wiki/Basic_fighter_maneuvers ; https://navyflightmanuals.tpub.com/P-1222/High-Yo-Yo-P-12220026-26.htm
- "Fast Transients" (Boyd) — https://theleanthinker.com/2013/08/19/fast-transients/
- Thach Weave — https://en.wikipedia.org/wiki/Thach_Weave ; https://theaviationgeekclub.com/the-dogfight-that-led-to-the-birth-of-the-thach-weave-maneuver-the-defensive-counter-employed-during-wwii-by-all-us-navy-and-usmc-fighter-pilots-when-dealing-with-the-zeros-su/
- Dicta Boelcke — https://en.wikipedia.org/wiki/Dicta_Boelcke ; https://militaryhistorynow.com/2015/04/06/rules-of-engagement-8-air-combat-maxims-the-red-baron-used-to-conquer-the-skies/
- Drag-and-bag / wingman tactics — http://www.combatsim.com/memb123/archive/htm/htm_arc5/wingman2.htm ; https://www.mission4today.com/index.php?name=Knowledge_Base&op=show&kid=297 ; https://avi-8.com/blogs/the-aviation-journal/wingman-tactics-defensive-and-offensive-maneuvers-in-air-combat
- Scissors — https://en.wikipedia.org/wiki/Scissors_(aeronautics)
- Lufbery Circle — https://en.wikipedia.org/wiki/Lufbery_circle
- Finger-four / loose deuce — https://en.wikipedia.org/wiki/Finger-four ; https://theaviationgeekclub.com/loose-deuce-vs-fluid-four-during-the-vietnam-war-the-fighting-tactics-used-by-us-naval-aviators-were-better-than-those-of-the-usaf-pilots-heres-why/
- Escort fighter / strike package doctrine — https://en.wikipedia.org/wiki/Escort_fighter ; https://apps.dtic.mil/sti/tr/pdf/ADA194274.pdf ; https://en.wikipedia.org/wiki/Strike_package
- SEAD / Wild Weasel — https://en.wikipedia.org/wiki/Suppression_of_Enemy_Air_Defenses ; https://sofrep.com/fightersweep/air-force-f-16-sead-mission-know-wild-weasel/
- Jinking / defensive break — https://historyrise.com/article/the-use-of-jinking-to-avoid-enemy-missiles-in-air-combat/

**Sea domain**
- Crossing the T — https://en.wikipedia.org/wiki/Crossing_the_T ; https://www.britannica.com/topic/crossing-the-T ; https://grokipedia.com/page/Crossing_the_T
- USN gunnery / ranging salvo — https://www.ibiblio.org/hyperwar/USN/ref/BGD/index.html ; https://www.usni.org/magazines/naval-history-magazine/2011/july/beep-beep-boom ; https://www.navalgazing.net/Spotting
- Fire distribution — https://www.usni.org/magazines/proceedings/1914/march/concentration-fire-and-numerical-strength-division ; https://www.usni.org/magazines/proceedings/1907/january/gun-distribution-aboard-modern-battleships-and-its-influence
- Wolfpack (Rudeltaktik) — https://en.wikipedia.org/wiki/Wolfpack_(naval_tactic) ; https://uboat.net/ops/wolfpacks/overview.htm
- PT-boat anvil attacks — https://naval-encyclopedia.com/ww2/us/pt-boats.php ; https://warfarehistorynetwork.com/article/fast-boats-in-harms-way/
- Long Lance doctrine — https://warfarehistorynetwork.com/barroom-brawl-off-guadalcanal/ ; http://www.navweaps.com/index_tech/tech-067.php ; https://www.history.navy.mil/about-us/leadership/director/directors-corner/h-grams/h-gram-008/h-008-3.html
- Savo Island — https://www.ibiblio.org/hyperwar/USN/rep/Savo/NWC/NWC-Savo-Fwd.html ; https://warontherocks.com/the-importance-of-the-battle-of-savo-island/
- Radar picket / Okinawa — https://en.wikipedia.org/wiki/Radar_picket ; https://www.ibiblio.org/hyperwar/USN/rep/Kamikaze/BatExp-Okinawa/index.html ; https://www.history.navy.mil/browse-by-topic/wars-conflicts-and-operations/world-war-ii/1945/battle-of-okinawa/dangerous-okinawa.html
- Big Blue Blanket / layered CAP — https://historyrise.com/article/the-impact-of-kamikaze-attacks-on-allied-naval-strategies-and-countermeasures/ ; https://digital-commons.usnwc.edu/cgi/viewcontent.cgi?article=8459&context=nwc-review
- Jeune École — https://www.usni.org/magazines/naval-history/2024/august/jeune-ecole-offers-lessons-new-contested-maritime-environment ; https://www.globalsecurity.org/military/world/europe/jeune-ecole.htm
- Smoke screen / LOS withdrawal — https://www.portandterminal.com/a-brief-history-of-naval-smoke-screens/ ; https://www.history.navy.mil/our-collections/art/exhibits/conflicts-and-operations/wwii/art-of-naval-amphibious-operations-from-wwii/smoke-screen.html
- Zigzag evasion / torpedo defense — https://en.wikipedia.org/wiki/Torpedo_defense
- Torpedo spread — https://thestrategybridge.org/the-bridge/2018/2/8/fire-one-fire-ten-implications-of-the-torpedo-scandal-of-world-war-ii ; http://www.dionysus.biz/torpedoaccuracy.html

**Land domain**
- Bounding overwatch / fire and movement — https://en.wikipedia.org/wiki/Bounding_overwatch ; https://en.wikipedia.org/wiki/Fire_and_movement ; https://www.benning.army.mil/Infantry/DoctrineSupplement/ATP3-21.8/chapter_04/section_09/page_0030/index.html
- Suppressive fire — https://en.wikipedia.org/wiki/Suppressive_fire ; https://www.benning.army.mil/infantry/magazine/issues/2014/Apr-Jun/ConradTinsley.html
- L-shaped ambush / kill zone — https://survivaldispatch.com/small-unit-tactics-the-l-shaped-ambush/ ; https://www.benning.army.mil/Infantry/DoctrineSupplement/ATP3-21.8/chapter_08/CombatPatrols/ActionsontheObjective_Ambush/index.html ; https://en.wikipedia.org/wiki/Kill_zone
- Hull-down / defilade — https://en.wikipedia.org/wiki/Hull_down ; https://en.wikipedia.org/wiki/Enfilade_and_defilade
- Reverse-slope defense — https://en.wikipedia.org/wiki/Reverse_slope_defence ; https://historyrise.com/article/an-in-depth-look-at-the-german-afrika-korps-strategy-at-kasserine-pass/
- Defeat in detail / force concentration — https://en.wikipedia.org/wiki/Defeat_in_detail ; https://en.wikipedia.org/wiki/Force_concentration
- Feint and demonstration — https://en.wikipedia.org/wiki/Feint ; https://simple.wikipedia.org/wiki/Demonstration_(military) ; https://irp.fas.org/doddir/army/fm90-2/90-2ch5.htm
- Oblique order (Leuthen) — https://en.wikipedia.org/wiki/Oblique_order ; https://warfarehistorynetwork.com/article/frederick-the-great-at-leuthen-the-oblique-order/
- Double envelopment / Cannae — https://en.wikipedia.org/wiki/Pincer_movement ; https://en.wikipedia.org/wiki/Envelopment ; https://ancientwarhistory.com/hannibals-masterpiece-the-battle-of-cannae-and-the-art-of-encirclement/
- Infiltration tactics (Hutier) — https://en.wikipedia.org/wiki/Infiltration_tactics ; https://michaeltfassbender.com/nonfiction/the-world-wars/big-picture/storm-troops-and-infiltration-tactics-in-the-german-army-in-world-war-i/
- SOSRA breach doctrine — https://www.benning.army.mil/Infantry/DoctrineSupplement/ATP3-21.8/appendix_h/ObstacleReduction/BreachingFundamentals/index.html ; https://www.globalsecurity.org/military/library/policy/army/fm/3-34-2/chap1.htm
- Time on target — https://en.wikipedia.org/wiki/Time_on_target ; https://balagan.info/artillery-and-mortar-tactics-of-ww2
- Counter-battery fire — https://en.wikipedia.org/wiki/Counter-battery_fire

**RTS-AI domain**
- Focus fire — https://starcraft.fandom.com/wiki/Focus_fire ; https://liquipedia.net/starcraft2/Automatic_Targeting
- SparCraft / NOKAV — https://github.com/davechurchill/ualbertabot/wiki/SparCraft-Artificial-Intelligence ; https://github.com/davechurchill/ualbertabot/wiki/SparCraft-Introduction
- Target-lock hysteresis / combat-system engineering — https://www.slashskill.com/how-to-build-a-combat-system-for-your-rts-game-complete-guide/
- Lanchester-attrition combat prediction — https://www.researchgate.net/publication/285588866_Using_Lanchester_Attrition_Laws_for_Combat_Prediction_in_StarCraft ; https://arxiv.org/pdf/1403.1521
- Lanchester's laws / concentration of force — https://en.wikipedia.org/wiki/Lanchester%27s_laws ; https://www.gamedeveloper.com/design/the-designer-s-notebook-kicking-butt-by-the-numbers-lanchester-s-laws ; http://www.doolanshire.net/2018/12/13/force-concentration-lanchester-and-trafalgar/
- Kiting — https://ojs.aaai.org/index.php/AIIDE/article/view/12544 ; https://ojs.aaai.org/index.php/AIIDE/article/download/12544/12395/16064
- Stutter-step micro — https://tl.net/forum/closed-threads/233397-stalker-stutter-step-micro ; https://terrancraft.com/2017/08/08/es-mechanics-micro-and-trade-off/
- Concave formation — https://terrancraft.com/2017/08/08/es-mechanics-micro-and-trade-off/
- Splash-damage spreading — https://news.blizzard.com/en-us/article/6640646/game-guide-unit-positioning
- Influence maps — https://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter30_Modular_Tactical_Influence_Maps.pdf
- Potential fields — https://www.researchgate.net/publication/30498947_The_Rise_of_Potential_Fields_in_Real_Time_Strategy_Bots
- GOAP / F.E.A.R. AI — https://www.gamedeveloper.com/design/building-the-ai-of-f-e-a-r-with-goal-oriented-action-planning ; https://www.gamedevs.org/uploads/three-states-plan-ai-of-fear.pdf
- Timing-window attacks — https://learntheleague.wordpress.com/2014/10/03/rts-musings-part-3-timing-attacks/

**Space / near-future domain**
- EMCON — https://radartopix.com/en/what-is-emcon-in-the-navy/ ; https://www.projectrho.com/public_html/rocket/spacewardetect.php
- Ballistic-coast prediction — https://www.projectrho.com/public_html/rocket/spacewardefense.php
- Aegis shoot-look-shoot / saturation attack — https://www.usni.org/magazines/proceedings/2019/november/navy-losing-missile-arms-race ; https://en.wikipedia.org/wiki/Saturation_attack
- Distributed lethality — https://cimsec.org/driving-toward-distributed-maritime-operations-getting-the-navy-out-of-its-vls-hole/ ; https://www.usni.org/magazines/proceedings/2026/february/thinking-outside-box-launcher
- Nap-of-the-earth / pop-up attack — https://en.wikipedia.org/wiki/Nap-of-the-earth ; https://wiki.hoggitworld.com/view/Attack_Helicopter_Operations
- UDT obstacle demolition — https://en.wikipedia.org/wiki/Underwater_Demolition_Team ; https://www.smithsonianmag.com/history/the-stealth-swimmers-whose-wwii-scouting-laid-the-groundwork-for-the-navy-seals-180980427/
- Gravitational slingshot — https://www.forbes.com/sites/quora/2017/01/06/space-travel-how-do-gravitational-slingshots-work/ ; https://symbolaris.com/course/fcps16/projects/amoran.pdf
- Nelson / Trafalgar concentration — https://en.wikipedia.org/wiki/Battle_of_Trafalgar ; https://en.wikipedia.org/wiki/The_Nelson_Touch
- Gun-target line safety / danger close — https://www.globalsecurity.org/military/library/policy/army/fm/6-30/f630_9.htm ; https://www.globalsecurity.org/military/library/policy/army/fm/3-21-71/ch8.htm

**Praedra internal**
- `docs/SIM_CONTRACT.md` — sim API, detection/PD/heavy-rail/friendly-fire mechanics ground-truthed throughout this document.
- `index.html` `/* SIM BEGIN */.../* SIM END */` — `CONFIG_DEFAULTS`, `updateDoctrine`, `aiBattleshipV2`/`aiDestroyerV2`/`aiFrigateV2`/`aiBomberV2`/`aiInterceptorV2`, `updatePD`, `emconCap`, `coverHop`/`coverPoint`, `openLaneBiasV2` — the existing `v2` doctrine implementation this document documents and extends.
