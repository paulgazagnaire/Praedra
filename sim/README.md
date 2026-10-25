# Praedra.Sim: command-line runner

Runs the core headless and prints what happened, day by day. It reads the core's event list and nothing else, and draws no
random numbers. It finds the repository's `data/` folder by walking up to `Praedra.sln`, so run it from inside the repo.

## `move`: send one fleet and watch the days

```
dotnet run --project sim -- move --fleet <name> --side <Alliance|Empire> --ships "<Class>:<n>[,<Class>:<n>...]" \
    --from <System> --to <System> \
    [--enemy "<Class>:<n>[,...]@<System>"]...  [--owner "<System>=<Alliance|Empire>"]... \
    [--trigger PaulRule|AnyArmedFleet|GravityWellShip] [--days 40] [--seed 1]
```

`--enemy` places a stationary fleet of the other side (named Picket-1, Picket-2, ...). `--owner` sets who owns a system
(default: every system neutral). Ship classes are the names in `data/classic/capital-ships.csv` and `fighters.csv`, matched
ignoring case; `CR90` and `CR90 Corvette` are accepted for the Dodger and `ISD` for the Mandate.
The run ends when the fleet is no longer in transit (arrived, stopped or order rejected) or after `--days`.

### Example 1: a clean trip

```
$ dotnet run --project sim -- move --fleet Red --side Alliance --ships "CR90 Corvette:2" --from Charter --to Lowmarket
Rebel fleet Red: 2 x Dodger, speed 80
Trigger: PaulRule. Seed: 1.

Day 0: Rebel fleet Red is ordered Charter -> Lowmarket via Pyrite, Vigilia, Nutrix, Nordhaven; 7 days, due day 7
Day 0: Rebel fleet Red leaves Charter (first hop 1 days)
Day 1: Rebel fleet Red passes Pyrite
Day 3: Rebel fleet Red passes Vigilia
Day 5: Rebel fleet Red passes Nutrix
Day 6: Rebel fleet Red passes Nordhaven
Day 7: Rebel fleet Red arrives at Lowmarket

After 7 days: Rebel fleet Red is at Lowmarket. Random numbers drawn: 0.
```

### Example 2: a Subpoena at Nutrix forces a stop

```
$ dotnet run --project sim -- move --fleet Red --side Alliance --ships "CR90 Corvette:2" --from Charter --to Lowmarket \
      --enemy "Subpoena:1@Nutrix" --seed 1
Rebel fleet Red: 2 x Dodger, speed 80
Imperial fleet Picket-1 waits at Nutrix: 1 x Subpoena
Trigger: PaulRule. Seed: 1.

Day 0: Rebel fleet Red is ordered Charter -> Lowmarket via Pyrite, Vigilia, Nutrix, Nordhaven; 7 days, due day 7
Day 0: Rebel fleet Red leaves Charter (first hop 1 days)
Day 1: Rebel fleet Red passes Pyrite
Day 3: Rebel fleet Red passes Vigilia
Day 5: Rebel fleet Red is stopped at Nutrix by Imperial fleet Picket-1
Day 5: contact at Nutrix: Rebel fleet Red meets Imperial fleet Picket-1

After 5 days: Rebel fleet Red is at Nutrix. Random numbers drawn: 0.
```

A contact is raised by the core every day while both sides share a system; the log shows it once until the fleets present change.

## `economy`: pools of a fixed demo setup

```
dotnet run --project sim -- economy [--days 360] [--seed 1] [--no-incidents]
```

Runs the demo setup of `core/Economy/DemoScenario.cs` (four owned systems per side with mines and refineries, a few yards, ships
and troops for load, one blockading Subpoena, varied support so smuggling shows) and prints each side's raw and refined
pools, capacity, load, free maintenance, mine and refinery counts, units produced and units smuggled away, every 30 days.
The last lines give the autoscrap, incident and destruction counts, the number of random numbers drawn and the state hash.
Same seed, same output.

## `build`: a yard building, a delivery held by an enemy fleet

```
dotnet run --project sim -- build [--days 150] [--seed 1] [--picket-leaves <day>|never] [--no-forced-stop]
```

Runs `core/Construction/BuildDemo.cs`: an Empire home (Charter) with a shipyard, a training facility, two construction yards and
two mine and refinery pairs builds two Gleaners, a mine for Classis and two Pacifiers regiments for The Thaw. A Rebel picket sits at
Bina Major, the first stop on the regiments' path, so their deliveries are held there (`DeliveryForcedStop`, Paul D2) until the picket moves
away on `--picket-leaves` (default day 100; `never` keeps it there; `--no-forced-stop` lets deliveries fly through). It prints orders,
completions and deliveries leaving, passing, held, resumed and arriving, and a status line every 30 days. Excerpt (`--days 160`):

```
Day 37: delivery 1 leaves Charter: Charter -> Bina Major -> Bina Minor -> Magnetite III -> Nutrix -> Nordhaven -> Lowmarket -> Tierra Larga -> Seldwyn -> Overgrowth -> Longtide -> Galeholm -> Stopgap -> Groenveld -> Brokengate -> Towline -> Ashgarden -> Kelpfast -> The Thaw, first hop 2 days, due day 39
Day 39: delivery 1 is HELD at Bina Major (FleetPresent, enemy fleet 9)
Day 100: Rebel picket leaves Bina Major
Day 101: delivery 1 goes on from Bina Major
Day 155: delivery 1: Pacifiers (object 13) arrives at The Thaw
```
## `support`: popular support, garrisons and uprisings

```
dotnet run --project sim -- support [--days 360] [--seed 1] [--ruleset classic|new] [--reinforce-day 120]
```

Runs the economy demo with the support module on (economy incidents off) and prints every support change, uprising start and end,
uprising incident and destroyed regiment or facility, plus a table of five systems every 30 days. The Moorings starts with support 25
and no regiments, so an uprising starts on day 1 and erodes support 1 point per 30 days; on `--reinforce-day` eight Alliance Army
Regiments land and the uprising ends the next day. Drift Harbour is blockaded by an Empire fleet (support drifts one point per 30 days).
Draws no random numbers itself.

## `mission`: N seeded missions against a defended system

```
dotnet run --project sim -- mission --kind <MissionKind> --agent "<Name>" [--agent ...] [--side Alliance|Empire] [--skills "esp=60,com=50"] \
    [--sf "<Class>:<n>"] [--ships "<Class>:<n>"] [--troops "<Class>:<n>"] [--officer "<Name>" [--officer-esp <n>]] \
    [--target-agent "<Name>"] [--facility Mine] [--at Charter] [--owner Alliance|Empire|neutral] [--support 50] [--missions 1000] [--seed 1]
```

Mission k runs in a fresh game with seed (seed + k - 1), incidents off. The team stands at the target, so it lands at once. Agents keep
their record's base skills unless `--skills` sets them. `--ships` is one enemy fleet at the target (`--officer` its Admiral), `--troops`
are landed enemy regiments, fighters or special forces. It prints how often the first iteration succeeded, failed or was foiled, and the
mean detection and success chances the core rolled. The success tables are only the sheet's anchor rows until the importer reads them, so
the success chances are coarse.

```
$ dotnet run --project sim -- mission --kind Espionage --agent "Midwife" --troops "Pacifiers:2" --missions 300
300 Espionage missions by the Alliance at Charter (owner Empire, Alliance support 50), seeds 1..300
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
parked on Tierra Larga from day 60 for a blockade) and one scripted capture (Empire takes Fiducia on day 400, a stand-in for the M3 assault).
It prints a summary per side on day 0 and every 365 days (systems held, raw, refined, free maintenance with capacity and load, facilities,
ships, fighters, regiments, uprisings active, items built, objects scrapped), then the event totals, the final state hash, the random
draws and a runtime line (the runner's own clock). Same seed and ruleset, same output except the runtime. `--ruleset` is the support
garrison ruleset (default Classic).
