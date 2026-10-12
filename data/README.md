# Data

| Folder | What | Edited by hand? |
|---|---|---|
| `original/` | Tables converted from the 1998 game files by `importer/`. Not created yet: the importer needs the game's data folder. | Never |
| `design/` | Our own numbers: the extended galaxy (360 systems, 521 lanes), pinned slot values, the blockade numbers T and E (`blockade.csv`, m1-forced-stop-and-blockade), the placeholder garrison weights (`garrison-weights.csv`, m2-uprisings-and-garrison), later per-weapon stats and new content. Each file is copied from the spec that owns it. | Only through the owning spec |
| `interim/` | Original values copied from spec CSVs (ships, fighters, costs). Used until `importer/` produces `original/`, then deleted. Tag: unverified vs original. | Never |

`design/starting-facilities.csv` is the data list of m2-construction §Starting yards (seven Orbital Shipyards: four Empire, three neutral), written from that sheet's table.

Sources (project folder `rebellion/specs/`): `planets/data/extended-galaxy-v4-*.csv`, `economy/data/slot-values-draft.csv`, `ships/data/*.csv`, `economy/data/unit-and-facility-costs.csv`. Copied 2026-10-12.

`interim/defense-facilities.csv` (the six `DEFFACSD` classes, m3-planetary-defenses) and `interim/bombardment-defense.csv` (regiment and manufacturing-facility bombardment defense, m3-bombardment B7; the facility values are DEFAULT 5 until the importer reads them) were written from those sheets tables.

`design/repair-rules.csv` is the data table `RepairRules` of m4-retreat-damage-repair section 5 (strategic repair, slot 6.8: yard needed, cadence, hull divisor, hits per call).

`design/weapons.csv` (one row per ship, arc and weapon type; guns, shot damage, reload, exact DAT range) and `design/combat-ship-stats.csv` (one row per ship: cost, hull, shield pool, regen, deflection, size class, sublight, maneuver, hyperdrive, berths, gravity well) are copied from `rebellion/specs/ships/data/` (ships/11 per-weapon stats, Proposal values except the DAT columns; unverified vs original) and read by the New battle (m4-battle-new §1) and by CombatStrength (§12). `design/battle-new-tables.csv` is the weapon table of m4-battle-new §2 (damage multipliers and hit chances by target size). Copied 2026-10-12.

`design/day-one/day-one-<preset>-seed1.csv` (presets easy, med_a, med_e, hard_a, hard_e) are copied unchanged from `rebellion/specs/setup-victory/data/` (setup-victory/09-day-one-state-360, written by `deal_day_one.py`; owners, starting Alliance support, explored flags, the Rebel HQ system; PROPOSAL values, 2026-10-12). Read by `NewGame.Create` (m5). The two rows Ilum and Csilla are not on the 360-system graph and are skipped.

`design/new-game-forces.csv` holds the starting forces of the playable slice (yards, mines and refineries per side, garrison, fleets, stockpile). Every row is DEFAULT (playable slice 2026-10-12): the setup-victory specs give no starting force numbers, so these are choices to tune, not rules from the original.

`design/fixed-owners.csv` is the seven Decided fixed owners of m5-setup-and-victory (planets/07), applied on top of the day-one CSVs, which ignore them (sheet Contradictions 6). Fixed owners are strong holders (Empire support 60 to 90).
