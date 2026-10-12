# Rebellion.Sim: command-line runner

Runs the core headless and prints what happened, day by day. It reads the core's event list and nothing else, and draws no
random numbers. It finds the repository's `data/` folder by walking up to `Rebellion.sln`, so run it from inside the repo.

## `move`: send one fleet and watch the days

```
dotnet run --project sim -- move --fleet <name> --side <Alliance|Empire> --ships "<Class>:<n>[,<Class>:<n>...]" \
    --from <System> --to <System> \
    [--enemy "<Class>:<n>[,...]@<System>"]...  [--owner "<System>=<Alliance|Empire>"]... \
    [--trigger PaulRule|AnyArmedFleet|GravityWellShip] [--days 40] [--seed 1]
```

`--enemy` places a stationary fleet of the other side (named Picket-1, Picket-2, ...). `--owner` sets who owns a system
(default: every system neutral). Ship classes are the names in `data/interim/capital-ships.csv` and `fighters.csv`, matched
ignoring case; `CR90` and `CR90 Corvette` are accepted for the Corellian Corvette and `ISD` for the Imperial Star Destroyer.
The run ends when the fleet is no longer in transit (arrived, stopped or order rejected) or after `--days`.

### Example 1: a clean trip

```
$ dotnet run --project sim -- move --fleet Red --side Alliance --ships "CR90 Corvette:2" --from Coruscant --to Hoth
Rebel fleet Red: 2 x Corellian Corvette, speed 80
Trigger: PaulRule. Seed: 1.

Day 0: Rebel fleet Red is ordered Coruscant -> Hoth via Corellia, Hosnian Prime, Kinyen, Anoat, Bespin; 21 days, due day 21
Day 0: Rebel fleet Red leaves Coruscant (first hop 5 days)
Day 5: Rebel fleet Red passes Corellia
Day 6: Rebel fleet Red passes Hosnian Prime
Day 12: Rebel fleet Red passes Kinyen
Day 19: Rebel fleet Red passes Anoat
Day 20: Rebel fleet Red passes Bespin
Day 21: Rebel fleet Red arrives at Hoth

After 21 days: Rebel fleet Red is at Hoth. Random numbers drawn: 0.
```

### Example 2: an Interdictor Cruiser at Kinyen forces a stop

```
$ dotnet run --project sim -- move --fleet Red --side Alliance --ships "CR90 Corvette:2" --from Coruscant --to Hoth \
      --enemy "Interdictor Cruiser:1@Kinyen" --seed 1
Rebel fleet Red: 2 x Corellian Corvette, speed 80
Imperial fleet Picket-1 waits at Kinyen: 1 x Interdictor Cruiser
Trigger: PaulRule. Seed: 1.

Day 0: Rebel fleet Red is ordered Coruscant -> Hoth via Corellia, Hosnian Prime, Kinyen, Anoat, Bespin; 21 days, due day 21
Day 0: Rebel fleet Red leaves Coruscant (first hop 5 days)
Day 5: Rebel fleet Red passes Corellia
Day 6: Rebel fleet Red passes Hosnian Prime
Day 12: Rebel fleet Red is stopped at Kinyen by Imperial fleet Picket-1
Day 12: contact at Kinyen: Rebel fleet Red meets Imperial fleet Picket-1

After 12 days: Rebel fleet Red is at Kinyen. Random numbers drawn: 0.
```

A contact is raised by the core every day while both sides share a system; the log shows it once until the fleets present change.

## `economy`: pools of a fixed demo setup

```
dotnet run --project sim -- economy [--days 360] [--seed 1] [--no-incidents]
```

Runs the demo setup of `core/Economy/DemoScenario.cs` (four owned systems per side with mines and refineries, a few yards, ships
and troops for load, one blockading Interdictor, varied support so smuggling shows) and prints each side's raw and refined
pools, capacity, load, free maintenance, mine and refinery counts, units produced and units smuggled away, every 30 days.
The last lines give the autoscrap, incident and destruction counts, the number of random numbers drawn and the state hash.
Same seed, same output.

## `support`: popular support, garrisons and uprisings

```
dotnet run --project sim -- support [--days 360] [--seed 1] [--ruleset classic|new] [--reinforce-day 120]
```

Runs the economy demo with the support module on (economy incidents off) and prints every support change, uprising start and end,
uprising incident and destroyed regiment or facility, plus a table of five systems every 30 days. Mon Calamari starts with support 25
and no regiments, so an uprising starts on day 1 and erodes support 1 point per 30 days; on `--reinforce-day` eight Alliance Army
Regiments land and the uprising ends the next day. Bespin is blockaded by an Empire fleet (support drifts one point per 30 days).
Draws no random numbers itself.
