# Data

| Folder | What | Edited by hand? |
|---|---|---|
| `original/` | Tables converted from the 1998 game files by `importer/` (B1). `original/raw/` holds one CSV per `.DAT` file plus `TEXTSTRA-strings.csv` (every word, file order, header row); the other files are built from `raw/` only. See "Original data" below. | Never: re-run the importer |
| `design/` | Our own numbers: the extended galaxy (v5: 555 systems, 553 of them in 64 sectors, plus Ilum and Csilla; 377 hyperspace route links), pinned slot values, the blockade numbers T and E (`blockade.csv`, m1-forced-stop-and-blockade), the placeholder garrison weights (`garrison-weights.csv`, m2-uprisings-and-garrison), later per-weapon stats and new content. Each file is copied from the spec that owns it. | Only through the owning spec |

`design/starting-facilities.csv` is the data list of m2-construction §Starting yards (seven Orbital Shipyards: four Empire, three neutral), written from that sheet's table.

Sources (project folder `rebellion/specs/`): `economy/data/slot-values-draft.csv`. Copied 2026-10-12.

`design/extended-galaxy-v5-planets.csv` and `design/extended-galaxy-v5-lanes.csv` are copied from `planets/data/` (planets/15, route network rebuild, 2026-10-16; they replace the v4 files). 555 systems; the lanes file holds the 377 official hyperspace route links (117 major, factor 300; 260 minor, factor 150; no generated lanes); 332 systems lie on a route. Ships fly anywhere; a link only makes travel along it faster (m1-galaxy-data, m1-travel). `design/lane-tiers.csv` maps each link kind to its tier and factor (tier 2 now also covers `official-inferred` and `bridge`), and `design/expected-galaxy-counts.csv` holds the v5 counts the loader checks. `design/slot-values-draft.csv` and the five `design/day-one/` files were replaced with their 555-system versions at the same time.

`original/defense-facilities.csv` (the six `DEFFACSD` classes, m3-planetary-defenses) and `original/bombardment-defense.csv` (regiment bombardment defense from `TROOPSD`, m3-bombardment B7) come from the importer. The facility bombardment defenses stay DEFAULT 5 in `design/bombardment-defense.csv` (MANFACSD and PROFACSD words 12 and 13 are unnamed until B2).

`design/repair-rules.csv` is the data table `RepairRules` of m4-retreat-damage-repair section 5 (strategic repair, slot 6.8: yard needed, cadence, hull divisor, hits per call).

`design/weapons.csv` (one row per ship, arc and weapon type; guns, shot damage, reload, exact DAT range) and `design/combat-ship-stats.csv` (one row per ship: cost, hull, shield pool, regen, deflection, size class, sublight, maneuver, hyperdrive, berths, gravity well) are copied from `rebellion/specs/ships/data/` (ships/11 per-weapon stats, Proposal values except the DAT columns; unverified vs original) and read by the New battle (m4-battle-new §1) and by CombatStrength (§12). `design/battle-new-tables.csv` is the weapon table of m4-battle-new §2 (damage multipliers and hit chances by target size). Copied 2026-10-12.

`design/combat-ship-stats.csv` column `sublight` (New battle only: closing and kiting speed, m4-battle-new §5) was changed on 2026-10-17 for 17 classes to a lore fit (docs/ships/sublight-suggestions.csv: Wookieepedia MGLT fitted onto the game's 1-11 scale, higher = faster; Decided Paul 2026-10-17 "correct that"). Not original-game numbers. The original values stay in `original/capital-ships.csv` `sub_light_engine` and the Classic battle never reads sublight. Fighter rows changed too but the New battle reads only capital-ship sublight, so they have no effect yet.

`design/day-one/day-one-<preset>-seed1.csv` (presets easy, med_a, med_e, hard_a, hard_e) are copied unchanged from `rebellion/specs/setup-victory/data/` (setup-victory/09-day-one-state-360, written by `deal_day_one.py`; owners, starting Alliance support, explored flags, the Rebel HQ system; PROPOSAL values, 2026-10-12). Read by `NewGame.Create` (m5). Replaced with the v5 versions (555 rows) on 2026-10-16. The two rows Ilum and Csilla are outside sectors and are skipped.

`design/new-game-forces.csv` holds the starting forces of the playable slice (yards, mines and refineries per side, garrison, fleets, stockpile). Every row is DEFAULT (playable slice 2026-10-12): the setup-victory specs give no starting force numbers, so these are choices to tune, not rules from the original.

`design/fixed-owners.csv` is the seven Decided fixed owners of m5-setup-and-victory (planets/07), applied on top of the day-one CSVs, which ignore them (sheet Contradictions 6). Fixed owners are strong holders (Empire support 60 to 90).

`design/day-one-anchors.csv` (system, owner, alliance_support, source) is the anchor list of `DealMode = Anchored` (m5-setup-and-victory §Mode Anchored; setup-victory/10): 23 Empire anchors, Hoth, Dantooine and Mon Calamari for the Alliance, and 11 locked-neutral systems that are never dealt. `alliance_support` is blank except Chandrila 57 and Sullust 58 (Empire-held, Rebel sympathy). In Anchored mode it replaces `fixed-owners.csv` (which only `DealMode = File` still applies). Decided by Paul 2026-10-16.

`design/blocs-totals.csv` (preset, alliance_total, empire_total, alliance_strong, alliance_weak, empire_strong, empire_weak, source) holds the five rows of the Anchored totals table (anchors, Coruscant, Yavin and the HQ included) and the strong/weak shares. Read by `AnchoredDealer`. Decided by Paul 2026-10-16; the numbers are setup-victory/10 section 5 proposals to tune with the sim.

`original/research-unlocks.csv` (the research ladders of m5-research: track, research order and difficulty per class) comes from the importer; its row order (capital ships, fighters, regiments, special forces, MANFACSD, PROFACSD, DEFFACSD, then the rows of `design/research-unlocks.csv`) is the catalogue order of the level check. The loader checks that every class of `unit-and-facility-costs.csv` has one row with the same research order.

`design/research-rules.csv` is the data table `ResearchRules` of m5-research (facility tick delay and random span, points per yard, mission point roll, Sensor Station tier 2 rung and cost); each row carries its source tag.

Characters (m5-characters-and-force, m5-setup-and-victory P9), copied 2026-10-12:
- `original/characters.csv` holds the 60 original records of `MJCHARSD` and `MNCHARSD` (ids 576-579, 640-641, 832-857, 896-923), read by the importer. Since B1 Shenir Rix (918) is Force 10 +- 40 and Noval Garaint (919) 30 +- 20, as in the file (the old spec copy had them swapped).
- `design/added-heroes.csv` holds the 16 heroes Paul added (heroes/added-heroes: 8 Alliance, 4 Empire, 4 Imperial bounty hunters), Proposal stats, ids after each side's minor block (858-865, 924-931). Skills the source leaves blank are 0+0 and loyalty is 100+0 (DEFAULT, as the 60 originals); each row says so.
- `design/roster.csv` lists every record with `in_game` (Decided, heroes/refined-roster: the game starts right after Yavin with a film-era cast; Thrawn, Pellaeon, Bel Iblis, Daala, Zuggs, Ferrier, Grammel, Griff, Klev, Ma'w'shiye and Kaiya Andrimetrum are 0). 65 characters are in the game.
- `design/navigator_speed.csv` is the Navigator table of m5-characters-and-force (Han 50 Traced, the rest Proposal); unlisted characters are 100.
- `design/start-cast.csv` is the fixed start cast of m5-setup-and-victory P9 in placement order (Traced, setup-victory/03), with the two extras rows.
- `design/victory-rules.csv` is the victory rule table of m5-setup-and-victory (subjects by character id); only the `CharacterCaptured` rows are read so far (standard victory is a switch, off by default).

Missions (m5-missions):
- `original/mission-records.csv` holds the 25 `MISSNSD` records (kind and success table names are ours, missions/01).
- `original/mission-tables.csv` holds every row of `FOILTB`, the ten `*MSTB` success tables and the decoy table (written as `FDECOYTB`; the importer checks that `TDECOYTB` is identical). Decoys are accepted. The lookup mode stays `Step` (DEFAULT) until B2.
- `mission-numbers.csv`: the rows that are one GNPRTB value are in `original/` (detection constant, officer percentages, incite support, leadership divisor, research skill gain, group speed, Han's mission speed); the rest (die size, diplomacy and subdue ranges, Han's record id; FOILTB below its first row uses the common lookup since B2a) are in `design/mission-numbers.csv`. Each row keeps its source tag.
- `design/mission-foilable.csv` says which kinds a detection can foil (DEFAULT per the sheet).
- `design/assassination-key-targets.csv` lists the key characters (Luke 578, Mon Mothma 576, Vader 641, Palpatine 640) and their success cap in percent for Assassination and Abduction (m5-missions section 6a; Decided Paul 2026-10-16, numbers Proposal). Tune the caps here, no code change.

Sensor Station (m5-fog-and-hq N4, economy/15 §2; designed, not in the 1998 game; Paul 2026-10-16): its rows are in `design/unit-and-facility-costs.csv` (cost 10, maintenance 5, research order 0), `design/research-unlocks.csv` (level 0, start yes/yes) and `design/bombardment-defense.csv` (DEFAULT 5, as the other facilities), each with its tag.

## Original data (B1, our own importer)

`dotnet run --project importer -- <game folder> [<out folder>]` reads `GData/*.DAT` and `TEXTSTRA.DLL` of a copy of the 1998 game (never committed) into `original/raw/` (stage 1), then builds the drop-in files of `original/` from `raw/` alone (stage 2). `--from-raw <raw folder>` runs stage 2 only. Formats: `specs/reverse/02-gdata-formats.md`; raw headers use its section 5 names, `fN` for unnamed words and `w0..wN` for static-data files it does not list. Nothing rests on Open Rebellion.

The committed `original/raw/` was made in a cloud session without the game files: it is the reference reader's output for Paul's copy (`specs/reverse/golden/`) plus our header rows (`--from-golden`). The golden test (`REBELLION_GAME_DIR=<game folder> dotnet test`) runs the importer on the real files and requires output identical to the committed `original/`.

Loaders read `original/<file>` and, for `unit-and-facility-costs.csv`, `research-unlocks.csv`, `bombardment-defense.csv` and `mission-numbers.csv`, merge the rows of `design/<file>` (rows not in the game files, each with its own `source` or `tag` column). `gnprtb.csv` (key, v0..v7) and `sdprtb.csv` (key, v0..v15) hold the two parameter tables; a test pins every named GNPRTB/SDPRTB constant in core to them.
