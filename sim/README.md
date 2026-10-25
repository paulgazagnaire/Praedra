# Praedra.Sim: command-line runner

Runs the core headless and prints what happened, day by day. It reads the core's event list and nothing else, and draws no
random numbers. It finds the repository's `data/` folder by walking up to `Praedra.sln`, so run it from inside the repo.

## `mission`: N seeded missions against a defended system

```
dotnet run --project sim -- mission --kind <MissionKind> --agent "<Name>" [--agent ...] [--side Collective|Hand] [--skills "esp=60,com=50"] \
    [--sf "<Class>:<n>"] [--ships "<Class>:<n>"] [--troops "<Class>:<n>"] [--officer "<Name>" [--officer-esp <n>]] \
    [--target-agent "<Name>"] [--facility Mine] [--at Charter] [--owner Collective|Hand|neutral] [--support 50] [--missions 1000] [--seed 1]
```

Mission k runs in a fresh game with seed (seed + k - 1), incidents off. The team stands at the target, so it lands at once. Agents keep
their record's base skills unless `--skills` sets them. `--ships` is one enemy fleet at the target (`--officer` its Admiral), `--troops`
are landed enemy regiments, fighters or special forces. It prints how often the first iteration succeeded, failed or was foiled, and the
mean detection and success chances the core rolled. The success tables are only the sheet's anchor rows until the importer reads them, so
the success chances are coarse.

```
$ dotnet run --project sim -- mission --kind Espionage --agent "Midwife" --troops "Pacifiers:2" --missions 300
300 Espionage missions by the Collective at Charter (owner Hand, Collective support 50), seeds 1..300
  team: Midwife
  defenders: 2 x Pacifiers
  first iteration: succeeded 294 (98.0%), failed 6 (2.0%), foiled 0 (0.0%)
  detection rolls 0, detections 0, mean chance per detection roll -; success rolls 300, mean success chance 99.0%
  mission 1: no detection rolls; mean end day 12.1
```

## `campaign`: the scripted two-sided test campaign, 1000 days

```
dotnet run --project sim -- campaign [--days 1000] [--seed 1] [--ruleset Classic|New]
```

Builds `core/Setup/TestCampaign.cs` through the composition root (`GameHost`, which loads the data and attaches fleets, economy,
support, construction and control in order) and runs it. This is a TEST scenario, not the real game start: about 17 systems per side in two
regions with support values, mines, refineries, yards (the starting-facilities rows that fall inside the regions), garrisons, a standing
one reserve fleet of real ships per side (they pay upkeep) and a few fleets; then fixed scripted orders by day (periodic build orders, fleet moves, a Subpoena fleet
parked on Tierra Larga from day 60 for a blockade) and one scripted capture (Hand takes Fiducia on day 400, a stand-in for the M3 assault).
It prints a summary per side on day 0 and every 365 days (systems held, raw, refined, free maintenance with capacity and load, facilities,
ships, fighters, regiments, uprisings active, items built, objects scrapped), then the event totals, the final state hash, the random
draws and a runtime line (the runner's own clock). Same seed and ruleset, same output except the runtime. `--ruleset` is the support
garrison ruleset (default Classic).
