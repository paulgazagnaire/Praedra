# Data

| Folder | What | Edited by hand? |
|---|---|---|
| `original/` | Tables converted from the 1998 game files by `importer/`. Not created yet: the importer needs the game's data folder. | Never |
| `design/` | Our own numbers: the extended galaxy (360 systems, 521 lanes), pinned slot values, the blockade numbers T and E (`blockade.csv`, m1-forced-stop-and-blockade), later per-weapon stats and new content. Each file is copied from the spec that owns it. | Only through the owning spec |
| `interim/` | Original values copied from spec CSVs (ships, fighters, costs). Used until `importer/` produces `original/`, then deleted. Tag: unverified vs original. | Never |

Sources (project folder `rebellion/specs/`): `planets/data/extended-galaxy-v4-*.csv`, `economy/data/slot-values-draft.csv`, `ships/data/*.csv`, `economy/data/unit-and-facility-costs.csv`. Copied 2026-10-12.
