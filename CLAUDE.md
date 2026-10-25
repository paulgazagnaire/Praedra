# Praedra: rules for anyone writing code here

Praedra is our strategy game. It started as a rebuild of a 1990s galactic strategy game; that base is now called **Classic**, and its names and mechanics are placeholders we keep replacing. Godot 4 draws the screen; all game rules live in a plain C# core.

## Where the rules come from
- The rule book is in the project folder: `/mnt/project-files/praedra/specs/engine/rules/` (index `README.md`). Code implements those sheets. Do not invent rules. If a sheet is unclear or wrong, stop and report it; do not guess.
- The plan: `/mnt/project-files/praedra/specs/engine/04-development-plan.md`. Layout and rules: `03-code-layout.md`.
- For Classic mode, the source of truth is Classic's own data files and our own reading of its executable only. New rules come from the rule sheets.
- Say "Classic" for the base game in code, comments, tests and UI.

## Layout
- `core/` Praedra.Core: all game rules. No Godot. (.NET 8)
- `core.tests/` xUnit tests. Each test names the rule sheet and test id it checks, e.g. `// m1-travel T9`.
- `sim/` command-line runner for headless games and battle batches.
- `data/` see `data/README.md`.
- `godot/` (later) the Godot 4 .NET project. Screens only.

## Hard rules (tests enforce several of them)
1. The core never references Godot.
2. One seeded random generator owned by the core (`GameRng`). No `System.Random`, no wall-clock time, no `Guid.NewGuid`, no Stopwatch in the core. Exception: the Classic battle's Classic dice (`ClassicBattleRng.Classic`) are a second deterministic generator seeded inside the core per battle, as Classic did; switch `Game` uses `GameRng`.
3. Never let dictionary or hash-set iteration order affect game state or random draws. Sort by id or ordinal name first.
4. Orders in, events out. Only the core's day `Step()` changes game state.
5. Integer arithmetic for rules unless a sheet says otherwise.
6. Every rule has a comment naming its sheet: `// rule: m2-mines-refineries-pools §Cycle length`. Every number comes from data or a named constant with its source tag.
7. Undecided rules are switches or data values with the sheet's DEFAULT, never hard-coded.

## Working
- Build and test: `dotnet test Praedra.sln`. All tests must pass before a pull request.
- One pull request per work package, on a branch named `m<milestone>/<topic>`. Keep it focused.
- The .NET 8 SDK installs in cloud sessions with `apt-get install -y dotnet-sdk-8.0` (the dot.net download host is blocked).
