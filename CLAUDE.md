# Rebellion: rules for anyone writing code here

A rebuild of Star Wars: Rebellion (1998). Godot 4 draws the screen; all game rules live in a plain C# core.

## Where the rules come from
- The rule book is in the project folder: `/mnt/project-files/rebellion/specs/engine/rules/` (index `README.md`). Code implements those sheets. Do not invent rules. If a sheet is unclear or wrong, stop and report it; do not guess.
- The plan: `/mnt/project-files/rebellion/specs/engine/04-development-plan.md`. Layout and rules: `03-code-layout.md`.
- Source of truth for game data and mechanics is the original 1998 game only. Open Rebellion is a lead, never a source.

## Layout
- `core/` Rebellion.Core: all game rules. No Godot. (.NET 8)
- `core.tests/` xUnit tests. Each test names the rule sheet and test id it checks, e.g. `// m1-travel T9`.
- `sim/` command-line runner for headless games and battle batches.
- `importer/` (later) converts the original DAT files into `data/original/`.
- `data/` see `data/README.md`.
- `godot/` (later) the Godot 4 .NET project. Screens only.

## Hard rules (tests enforce several of them)
1. The core never references Godot.
2. One seeded random generator owned by the core (`GameRng`). No `System.Random`, no wall-clock time, no `Guid.NewGuid`, no Stopwatch in the core.
3. Never let dictionary or hash-set iteration order affect game state or random draws. Sort by id or ordinal name first.
4. Orders in, events out. Only the core's day `Step()` changes game state.
5. Integer arithmetic for rules unless a sheet says otherwise.
6. Every rule has a comment naming its sheet: `// rule: m2-mines-refineries-pools §Cycle length`. Every number comes from data or a named constant with its source tag.
7. Undecided rules are switches or data values with the sheet's DEFAULT, never hard-coded.

## Working
- Build and test: `dotnet test Rebellion.sln`. All tests must pass before a pull request.
- One pull request per work package, on a branch named `m<milestone>/<topic>`. Keep it focused.
- The .NET 8 SDK installs in cloud sessions with `apt-get install -y dotnet-sdk-8.0` (the dot.net download host is blocked).
