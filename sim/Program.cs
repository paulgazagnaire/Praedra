using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Setup;
using Rebellion.Core.Ships;
using Rebellion.Core.Support;

// Command-line runner (engine/04, package P1.4). Reads the core's events and prints them; draws no random numbers itself.
// rule: m1-clock-and-orders §Events out ("the command-line runner reads this list and nothing else").
return SimCli.Run(args);

internal static class SimCli
{
    private const string Usage = """
        Usage:
          dotnet run --project sim -- move --fleet <name> --side <Alliance|Empire> --ships "<Class>:<n>[,<Class>:<n>...]"
                                           --from <System> --to <System>
                                           [--enemy "<Class>:<n>[,...]@<System>"]...   stationary fleets of the other side
                                           [--owner "<System>=<Alliance|Empire>"]...   system owners (default: all neutral)
                                           [--trigger PaulRule|AnyArmedFleet|GravityWellShip]   (default PaulRule)
                                           [--days 40] [--seed 1]
          dotnet run --project sim -- economy [--days 360] [--seed 1] [--no-incidents]
          dotnet run --project sim -- build [--days 150] [--seed 1] [--picket-leaves <day>|never] [--no-forced-stop]
          dotnet run --project sim -- support [--days 360] [--seed 1] [--ruleset classic|new] [--reinforce-day 120]
          dotnet run --project sim -- battle --alliance "<Class>:<n>[,...]" --empire "<Class>:<n>[,...]" [--battles 1000] [--seed 1] [--no-rolls] [--ruleset classic|new] [--carry]
          dotnet run --project sim -- campaign [--days 1000] [--seed 1] [--ruleset Classic|New]
          dotnet run --project sim -- aivai [--days 730] [--seed 1] [--start meda|campaign] [--passive empire|alliance] [--trace]
        aivai plays the simple AI (core/AI) on both sides from the new-game start (MedA, DEFAULT) or the test campaign, printing a yearly summary,
        battle, assault and capture counts, the winner (if any) and the AI's order and rejection counts. The AIs see everything (no fog yet).
        battle runs N seeded battles (Classic: m4-battle-classic, New: m4-battle-new; --ruleset, DEFAULT classic) at a synthetic system where every retreat has a destination, and prints how often each
        side won or retreated, and the mean survivors. Battle k uses seed (seed + k - 1). --no-rolls turns the subsystem rolls off. --carry (New) puts squadrons aboard their side's carriers instead of basing them.
        campaign runs the scripted two-sided TEST campaign (core/Setup/TestCampaign.cs, built through GameHost) and prints a yearly summary per side,
        the final state hash, the random draws and how long the run took.
        support runs the same demo with popular support, garrisons and uprisings on and prints the support events and a table every 30 days.
        build runs a fixed demo: an Empire yard builds two fighters, a mine and two regiments; the regiments' delivery is held at a Rebel picket
        until the picket moves away (--picket-leaves <day>, default 100; "never" keeps it there). --no-forced-stop sets DeliveryForcedStop off.
        economy runs a fixed demo setup (four systems per side with mines and refineries) and prints each side's pools every 30 days.
        Ship classes are matched ignoring case; "CR90" and "CR90 Corvette" mean the Corellian Corvette, "ISD" the Imperial Star Destroyer.
        """;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CR90"] = "Corellian Corvette", ["CR90 Corvette"] = "Corellian Corvette", ["Corvette"] = "Corellian Corvette",
        ["ISD"] = "Imperial Star Destroyer", ["Star Destroyer"] = "Imperial Star Destroyer",
        ["VSD"] = "Victory Destroyer", ["Interdictor"] = "Interdictor Cruiser",
    };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
            if (args[0] == "economy") return Economy(args.Skip(1).ToArray());
            if (args[0] == "build") return Build(args.Skip(1).ToArray());
            if (args[0] == "support") return Support(args.Skip(1).ToArray());
            if (args[0] == "battle") return Battle(args.Skip(1).ToArray());
            if (args[0] == "campaign") return Campaign(args.Skip(1).ToArray());
            if (args[0] == "aivai") return AiVsAi(args.Skip(1).ToArray());
            if (args[0] == "newgame") return NewGameCmd(args.Skip(1).ToArray());
            if (args[0] != "move") throw new ArgumentException($"Unknown command '{args[0]}'.");
            return Move(Options.Parse(args.Skip(1).ToArray()));
        }
        catch (Exception e) when (e is ArgumentException or Rebellion.Core.Data.DataLoadException or FileNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    // Economy demo (engine/04, package P2.1). Reads the pools and the event list; draws nothing itself.
    private static int Economy(string[] a)
    {
        int days = 360; ulong seed = 1; bool incidents = true;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--no-incidents": incidents = false; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var demo = DemoScenario.Create(FindRoot(), seed, incidents);
        var game = demo.Game; var eco = demo.Economy;
        Console.WriteLine($"Economy demo: {days} days, seed {seed}, incidents {(incidents ? "on" : "off")}.");
        Console.WriteLine("day   side      raw  refined  capacity  load  free  mines/refineries  produced  smuggled");
        var produced = new Dictionary<Side, int>(); var smuggled = new Dictionary<Side, int>();
        int printed = 0;
        for (int day = 1; day <= days; day++)
        {
            game.Step();
            for (; printed < game.Events.Count; printed++)
                if (game.Events[printed] is ResourceProduced p)
                {
                    produced[p.PoolSide] = produced.GetValueOrDefault(p.PoolSide) + 1;
                    if (p.Diverted) smuggled[p.Controller] = smuggled.GetValueOrDefault(p.Controller) + 1;
                }
            if (day % 30 != 0 && day != days) continue;
            foreach (var side in new[] { Side.Alliance, Side.Empire })
            {
                int mines = eco.Objects.Count(o => o.Completed && o.Class == "Mine" && eco.OwnerOf(o) == side);
                int refs = eco.Objects.Count(o => o.Completed && o.Class == "Refinery" && eco.OwnerOf(o) == side);
                Console.WriteLine($"{game.Day,4}  {side,-8} {eco.Raw(side),5} {eco.Refined(side),8} {eco.Capacity(side),9} {eco.Load(side),5} {eco.Free(side),5}  {mines,7}/{refs,-9} {produced.GetValueOrDefault(side),9} {smuggled.GetValueOrDefault(side),9}");
            }
        }
        int scrapped = game.Events.OfType<ObjectScrapped>().Count();
        int incidentsSeen = game.Events.OfType<ResourceIncident>().Count(), disasters = game.Events.OfType<Disaster>().Count();
        int destroyed = game.Events.OfType<FacilityDestroyed>().Count();
        Console.WriteLine($"Scrapped by autoscrap: {scrapped}. Resource incidents: {incidentsSeen}. Disasters: {disasters}. Facilities destroyed: {destroyed}. Random numbers drawn: {game.Rng.Draws}.");
        Console.WriteLine($"State hash: {GameSave.StateHash(game)}");
        return 0;
    }

    // Campaign (engine/04, package P2.5). Builds the scripted two-sided test campaign through the composition root (GameHost) and runs it.
    // Reads state and events; draws no random numbers itself. The runtime line is the runner's own clock, never the core's.
    private static int Campaign(string[] a)
    {
        int days = 1000; ulong seed = 1; var ruleset = GarrisonRuleset.Classic;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--ruleset": ruleset = Next().ToLowerInvariant() switch { "classic" => GarrisonRuleset.Classic, "new" => GarrisonRuleset.New, _ => throw new ArgumentException("--ruleset must be Classic or New.") }; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var campaign = TestCampaign.New(seed, new GameSettings { SupportRuleset = ruleset });
        var host = campaign.Host;
        Console.WriteLine($"Test campaign (a test scenario, not the real start): {days} days, seed {seed}, support ruleset {ruleset}.");
        void Summary()
        {
            Console.WriteLine($"-- day {host.Game.Day}");
            foreach (var side in new[] { Side.Empire, Side.Alliance }) Console.WriteLine("  " + SideStats.Of(host, side));
        }
        Summary();
        while (host.Game.Day < days)
        {
            campaign.Step();
            if (host.Game.Day % 365 == 0 && host.Game.Day != days) Summary();
        }
        Summary();
        clock.Stop();
        var ev = host.Game.Events;
        Console.WriteLine($"Events: {ev.Count}. Uprisings started: {ev.OfType<UprisingStarted>().Count()}. Items built: {ev.OfType<UnitCompleted>().Count()}, delivered: {ev.OfType<DeliveryArrived>().Count()}. " +
                          $"Captures: {ev.OfType<Rebellion.Core.Control.SystemCaptured>().Count()}. Blockades started: {ev.OfType<BlockadeStarted>().Count()}.");
        Console.WriteLine($"State hash: {host.StateHash()}");
        Console.WriteLine($"Random numbers drawn: {host.Game.Rng.Draws}");
        Console.WriteLine($"Runtime: {days} days in {clock.Elapsed.TotalMilliseconds:F0} ms.");
        return 0;
    }

    // New game (m5): the real day-one position, then a yearly summary per side.
    private static int NewGameCmd(string[] a)
    {
        int days = 365, seed = 1; var preset = StartPreset.MedA;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = int.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be an integer."); break;
                case "--preset": preset = StartPresets.Parse(Next()); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var host = NewGame.Create(GameData.Default, new NewGameOptions { Seed = seed, Preset = preset });
        Console.WriteLine($"New game: preset {StartPresets.Key(preset)} (player {StartPresets.PlayerSide(preset)}), seed {seed}, {days} days. Rebel HQ: {host.HqSystem}.");
        void Summary()
        {
            Console.WriteLine($"-- day {host.Game.Day}");
            foreach (var side in new[] { Side.Empire, Side.Alliance }) Console.WriteLine("  " + SideStats.Of(host, side));
        }
        Summary();
        while (host.Game.Day < days && host.Victory.Winner == null)
        {
            host.Game.Step();
            if (host.Game.Day % 365 == 0 && host.Game.Day != days) Summary();
        }
        Summary();
        Console.WriteLine(host.Victory.Winner is { } w ? $"Winner: {w} on day {host.Game.Day} ({host.Victory.Reason})." : "No winner yet.");
        Console.WriteLine($"State hash: {host.StateHash()}");
        return 0;
    }

    // AI vs AI: the simple AI on both sides. Reads state and events; the AIs draw nothing from the game generator. Runtime is the runner's own clock.
    private static int AiVsAi(string[] a)
    {
        int days = 730; ulong seed = 1; string start = "meda"; Side? passive = null; bool trace = false;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--trace": trace = true; break;
                case "--passive": passive = Next().ToLowerInvariant() switch { "empire" => Side.Empire, "alliance" => Side.Alliance, "none" => null, _ => throw new ArgumentException("--passive must be empire, alliance or none.") }; break;
                case "--start": start = Next().ToLowerInvariant(); if (start is not ("meda" or "campaign")) throw new ArgumentException("--start must be meda or campaign."); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var host = start == "meda" ? NewGame.Create(new NewGameOptions { Seed = (int)seed, Preset = StartPreset.MedA }) : TestCampaign.New(seed).Host;
        var match = new Rebellion.Core.AI.AiMatch(host, passive: passive);
        if (start == "meda" && host.HqSystem != null) match.Empire.KnownEnemyHq = host.HqSystem; // no fog yet: the Empire knows the Rebel HQ (DEFAULT)
        Console.WriteLine($"AI vs AI: {days} days, seed {seed}, start {start}{(passive is Side ps ? $", {ps} passive (gives no orders)" : "")}. The AIs see everything (no fog of war yet).");
        void Summary()
        {
            Console.WriteLine($"-- day {host.Game.Day}");
            foreach (var side in new[] { Side.Empire, Side.Alliance }) Console.WriteLine("  " + SideStats.Of(host, side));
        }
        Summary();
        while (host.Game.Day < days && host.Victory.Winner == null)
        {
            match.Step();
            if (trace && host.Game.Day % 50 == 0) foreach (var ai in new[] { match.Empire, match.Alliance }) if (ai.Side != passive) Console.WriteLine("  d" + host.Game.Day + " " + ai.Status());
            if (trace) foreach (var ai in new[] { match.Empire, match.Alliance }) foreach (var o in ai.Submitted) Console.WriteLine($"  d{host.Game.Day - 1} {ai.Side} {o.Kind}: {o.Detail}");
            if (host.Game.Day % 365 == 0 && host.Game.Day != days) Summary();
        }
        Summary();
        clock.Stop();
        var ev = host.Game.Events;
        Console.WriteLine($"Battles: {ev.OfType<Rebellion.Core.Battle.BattleEnded>().Count()}. Bombardments: {ev.OfType<Rebellion.Core.Defenses.BombardmentResolved>().Count()}. " +
                          $"Assaults: {ev.OfType<Rebellion.Core.Assault.AssaultResolved>().Count()} (won {ev.OfType<Rebellion.Core.Assault.AssaultResolved>().Count(e => e.Result == "Won")}). " +
                          $"Captures: {ev.OfType<Rebellion.Core.Control.SystemCaptured>().Count()} (Empire {ev.OfType<Rebellion.Core.Control.SystemCaptured>().Count(e => e.To == Side.Empire)}, Alliance {ev.OfType<Rebellion.Core.Control.SystemCaptured>().Count(e => e.To == Side.Alliance)}).");
        Console.WriteLine(host.Victory.Winner is Side w ? $"Winner: {w} on day {host.Game.Day}." : $"No winner after {host.Game.Day} days.");
        Console.WriteLine($"AI orders sent: {match.TotalSent}, rejected: {match.TotalRejected}.");
        foreach (var kv in match.Rejected) Console.WriteLine($"  rejected {kv.Key}: {kv.Value}");
        foreach (var kv in match.Sent) Console.WriteLine($"  sent {kv.Key}: {kv.Value}");
        foreach (var g in ev.OfType<Rebellion.Core.Construction.UnitCompleted>().GroupBy(e => (e.Side, e.Class)).OrderBy(g => g.Key.Side).ThenBy(g => g.Key.Class, StringComparer.Ordinal))
            Console.WriteLine($"  built {g.Key.Side} {g.Key.Class}: {g.Count()}");
        Console.WriteLine($"State hash: {host.StateHash()}");
        Console.WriteLine($"Runtime: {host.Game.Day} days in {clock.Elapsed.TotalMilliseconds:F0} ms.");
        return 0;
    }

    // Support demo (engine/04, package P2.3). Reads state and events; draws nothing itself. At --reinforce-day eight Alliance
    // regiments land on Mon Calamari (a setup-time action between Steps, as the construction package will do through orders later).
    private static int Support(string[] a)
    {
        int days = 360, reinforce = 120; ulong seed = 1; var ruleset = GarrisonRuleset.Classic;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--reinforce-day": reinforce = int.TryParse(Next(), out var r) && r >= 0 ? r : throw new ArgumentException("--reinforce-day must be a non-negative integer."); break;
                case "--ruleset": ruleset = Next().ToLowerInvariant() switch { "classic" => GarrisonRuleset.Classic, "new" => GarrisonRuleset.New, _ => throw new ArgumentException("--ruleset must be classic or new.") }; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var demo = DemoScenario.Create(FindRoot(), seed, incidents: false, support: true);
        var game = demo.Game; var eco = demo.Economy; var sup = demo.Support!; var fleets = demo.Fleets;
        sup.Ruleset = ruleset;
        string[] watch = { "Mon Calamari", "Bespin", "Corellia", "Fondor", "Coruscant" };
        Console.WriteLine($"Support demo: {days} days, seed {seed}, ruleset {ruleset}, reinforcements on day {reinforce}. Economy incidents off.");
        Console.WriteLine("Support is the Alliance share (0..100); N = required garrison, G = regiments there (tenths in New).");
        Console.WriteLine();
        int printed = 0;
        void Table()
        {
            Console.WriteLine($"-- day {game.Day}");
            Console.WriteLine("   system        holder    Alliance  holder  N   G     uprising");
            foreach (var sys in watch)
            {
                var holder = fleets.OwnerOf(sys)!.Value;
                int n = sup.RequiredGarrison(sys);
                string g = ruleset == GarrisonRuleset.New ? $"{sup.Garrison10(sys) / 10}.{sup.Garrison10(sys) % 10}" : $"{sup.RegimentCount(sys)}";
                Console.WriteLine($"   {sys,-13} {holder,-9} {eco.SupportOf(sys, Side.Alliance),8} {eco.SupportOf(sys, holder),7} {n,2}  {g,-4}  {(sup.IsUprising(sys) ? "ON" : "-")}");
            }
        }
        Table();
        for (int day = 0; day < days; day++)
        {
            if (day == reinforce)
            {
                for (int k = 0; k < 8; k++) eco.AddObject(Side.Alliance, "Alliance Army Regiment", "Mon Calamari");
                Console.WriteLine($"Day {game.Day}: 8 Alliance Army Regiments land on Mon Calamari.");
            }
            game.Step();
            for (; printed < game.Events.Count; printed++)
                switch (game.Events[printed])
                {
                    case SupportChanged e: Console.WriteLine($"Day {e.Day}: support at {e.System} {e.Old} -> {e.New} ({e.Cause})"); break;
                    case UprisingStarted e: Console.WriteLine($"Day {e.Day}: UPRISING starts at {e.System} (holder {e.Holder})"); break;
                    case UprisingEnded e: Console.WriteLine($"Day {e.Day}: uprising ends at {e.System} ({e.Reason})"); break;
                    case IncidentRolled e: Console.WriteLine($"Day {e.Day}: incident at {e.System}, score {e.Score}, codes {e.Code1}/{e.Code2}"); break;
                    case RegimentDestroyed e: Console.WriteLine($"Day {e.Day}: regiment destroyed at {e.System} ({e.Class})"); break;
                    case FacilityDestroyed { Reason: DestroyReason.UprisingIncident } e: Console.WriteLine($"Day {e.Day}: a facility is destroyed at {e.System} (uprising incident)"); break;
                }
            if (game.Day % 30 == 0) Table();
        }
        var ev = game.Events;
        Console.WriteLine();
        Console.WriteLine($"Uprisings started: {ev.OfType<UprisingStarted>().Count()}, ended: {ev.OfType<UprisingEnded>().Count()}, incidents: {ev.OfType<IncidentRolled>().Count()}, support changes: {ev.OfType<SupportChanged>().Count()}. Random numbers drawn: {game.Rng.Draws}.");
        Console.WriteLine($"State hash: {GameSave.StateHash(game)}");
        return 0;
    }

    // Build demo (engine/04, package P2.2). Reads the event list; draws nothing itself.
    private static int Build(string[] a)
    {
        int days = 150; ulong seed = 1; int? leaves = 100; bool forcedStop = true;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--picket-leaves":
                    {
                        var v = Next();
                        leaves = v == "never" ? null : int.TryParse(v, out var l) && l >= 0 ? l : throw new ArgumentException("--picket-leaves must be a day or 'never'.");
                        break;
                    }
                case "--no-forced-stop": forcedStop = false; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var demo = BuildDemo.Create(FindRoot(), seed);
        demo.Construction.DeliveryForcedStop = forcedStop;
        var game = demo.Game; var eco = demo.Economy;
        Console.WriteLine($"Build demo: {days} days, seed {seed}, DeliveryForcedStop {(forcedStop ? "on" : "off")}.");
        Console.WriteLine($"Empire yards at {BuildDemo.HomeSystem}: Orbital Shipyard, Training Facility, 2 Construction Yards; refined 1000 and two mine and refinery pairs.");
        Console.WriteLine($"Regiments go {string.Join(" -> ", demo.RegimentPath)}; a Rebel picket waits at {demo.PicketAt}{(leaves is int l2 ? $" and moves to {demo.PicketMovesTo} on day {l2}" : " and stays")}.");
        Console.WriteLine();
        int printed = 0;
        for (int day = 0; day < days; day++)
        {
            if (leaves == day) demo.Fleets.SubmitMove(Side.Alliance, demo.Picket, demo.PicketMovesTo);
            game.Step();
            for (; printed < game.Events.Count; printed++)
            {
                var e = game.Events[printed];
                string? line = e switch
                {
                    ProjectStarted p => $"{p.Side} {p.System} {FamilyName(p.Family)} starts {p.Count} x {p.Class} -> {p.Destination} (objects {p.Units})",
                    ProjectCancelled c => $"project at {c.System} cancelled ({c.Reason}), removed objects {c.Units}",
                    OrderRejected r => $"order #{r.Sequence} rejected ({r.Reason})",
                    UnitCompleted u => $"object {u.Object} ({u.Class}) completed at {u.Yard}" + (u.DeliveryId == 0 ? ", appears there" : $", leaves as delivery {u.DeliveryId} to {u.Destination}"),
                    DeliveryDeparted d => $"delivery {d.Delivery} leaves {d.From}: {string.Join(" -> ", d.Path.Items)}, first hop {d.FirstHopTicks} days, due day {d.DueDay}",
                    DeliveryPassed p => $"delivery {p.Delivery} passes {p.System}",
                    DeliveryHeld h => $"delivery {h.Delivery} is HELD at {h.System} ({h.Reason}{(h.Blockers.Items.Count > 0 ? ", enemy fleet " + h.Blockers : "")})",
                    DeliveryResumed r => $"delivery {r.Delivery} goes on from {r.System}",
                    DeliveryArrived x => $"delivery {x.Delivery}: {x.Class} (object {x.Object}) arrives at {x.System}",
                    DeliveryRefused x => $"delivery {x.Delivery}: {x.Class} refused at {x.System}, refund {x.Refund}",
                    ObjectScrapped s => $"object {s.Object} ({s.Class}) scrapped ({s.Reason}), refund {s.Refund}",
                    FleetDeparted f => $"Rebel picket leaves {f.From}",
                    _ => null,
                };
                if (line != null) Console.WriteLine($"Day {e.Day}: {line}");
            }
            if ((game.Day % 30 == 0 && game.Day > 0) || game.Day == days)
                Console.WriteLine($"  -- day {game.Day}: Empire refined {eco.Refined(Side.Empire)}, load {eco.Load(Side.Empire)}, capacity {eco.Capacity(Side.Empire)}, projects {game.State.Construction.AllProjects().Count}, deliveries {game.State.Construction.Deliveries.Count}, refined line {demo.Construction.RefinedLine(Side.Empire).Count}");
        }
        Console.WriteLine();
        int built = game.Events.OfType<UnitCompleted>().Count();
        Console.WriteLine($"Units completed: {built}. Objects: {string.Join(", ", eco.Objects.Where(o => o.Completed).GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} x {g.Key}"))}. Random numbers drawn: {game.Rng.Draws}.");
        Console.WriteLine($"State hash: {GameSave.StateHash(game)}");
        return 0;
    }

    private static string FamilyName(YardFamily f) => f switch
    {
        YardFamily.Construction => "Construction Yard", YardFamily.Shipyard => "Shipyard", _ => "Training Facility",
    };

    private static int Move(Options o)
    {
        string root = FindRoot();
        var galaxy = GalaxyLoader.LoadFolder(Path.Combine(root, "data", "design"));
        var ships = ShipData.LoadAll(Path.Combine(root, "data", "interim"));
        var blockade = BlockadeData.Load(Path.Combine(root, "data", "design", BlockadeData.FileName));

        var game = new Game(o.Seed);
        var fleets = FleetModule.Attach(game, galaxy, ships, blockade, o.Trigger);
        foreach (var (system, side) in o.Owners) fleets.SetOwner(Canonical(galaxy, system), side);

        Side enemySide = o.Side == Side.Empire ? Side.Alliance : Side.Empire;
        string from = Canonical(galaxy, o.From), to = Canonical(galaxy, o.To);
        int mover = AddFleet(fleets, ships, o.Side, o.FleetName, o.Ships, from);
        int n = 0;
        foreach (var (spec, at) in o.Enemies)
            AddFleet(fleets, ships, enemySide, $"Picket-{++n}", spec, Canonical(galaxy, at));

        var f = game.State.Fleets[mover];
        Console.WriteLine($"{Describe(f)}: {string.Join(", ", fleets.ClassNamesOf(f).GroupBy(x => x).Select(g => $"{g.Count()} x {g.Key}"))}, speed {fleets.SpeedOf(f)}");
        foreach (var e in game.State.Fleets.Values.Where(x => x.Id != mover))
            Console.WriteLine($"{Describe(e)} waits at {e.At}: {string.Join(", ", fleets.ClassNamesOf(e).GroupBy(x => x).Select(g => $"{g.Count()} x {g.Key}"))}");
        Console.WriteLine($"Trigger: {o.Trigger}. Seed: {o.Seed}.");
        Console.WriteLine();

        fleets.SubmitMove(o.Side, mover, to);
        int printed = 0;
        string? lastContact = null;
        for (int i = 0; i < o.Days; i++)
        {
            game.Step();
            for (; printed < game.Events.Count; printed++)
                Print(game.Events[printed], game.State, ref lastContact);
            // The order is rejected, finished or cancelled by a stop: nothing more will move.
            if (game.State.Fleets[mover].Transit == null && game.Day > 0) { break; }
        }

        var end = game.State.Fleets[mover];
        Console.WriteLine();
        Console.WriteLine($"After {game.Day} days: {Describe(end)} is {(end.At != null ? "at " + end.At : "in transit")}. Random numbers drawn: {game.Rng.Draws}.");
        return 0;
    }

    private static int AddFleet(FleetModule fleets, IReadOnlyDictionary<string, ShipClass> ships, Side side, string name,
        IReadOnlyList<(string cls, int count)> spec, string system)
    {
        var caps = new List<string>(); var squads = new List<string>();
        foreach (var (cls, count) in spec)
        {
            string canon = ResolveClass(ships, cls);
            for (int i = 0; i < count; i++) (ships[canon].IsCapital ? caps : squads).Add(canon);
        }
        return fleets.AddFleet(side, name, caps, system, squads);
    }

    // Classic battle batch (engine/04, package P4.1). Runs the core's battle on plain units; draws nothing itself.
    private static int Battle(string[] a)
    {
        int battles = 1000; ulong seed = 1; bool rolls = true, carry = false; var ruleset = BattleRuleset.Classic;
        List<(string, int)>? alliance = null, empire = null;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--alliance": alliance = Options.ParseShipList(Next()); break;
                case "--empire": empire = Options.ParseShipList(Next()); break;
                case "--battles": battles = int.TryParse(Next(), out var n) && n > 0 ? n : throw new ArgumentException("--battles must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--no-rolls": rolls = false; break;
                case "--carry": carry = true; break;
                case "--ruleset": ruleset = Next().ToLowerInvariant() switch { "classic" => BattleRuleset.Classic, "new" => BattleRuleset.New, _ => throw new ArgumentException("--ruleset must be classic or new.") }; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        if (alliance == null || empire == null) throw new ArgumentException("--alliance and --empire are required.");
        var ships = GameData.Default.Ships;
        var al = alliance.Select(x => (ResolveClass(ships, x.Item1), x.Item2)).ToList();
        var em = empire.Select(x => (ResolveClass(ships, x.Item1), x.Item2)).ToList();
        var p = new BattleParams { SubsystemRolls = rolls, Ruleset = ruleset };
        int aWin = 0, eWin = 0, none = 0, aRet = 0, eRet = 0; long aSurv = 0, eSurv = 0, secs = 0;
        for (int k = 0; k < battles; k++)
        {
            var (r, _) = QuickBattle.Run(p, ships, al, em, seed + (ulong)k, carry: carry);
            if (r.AllianceOutcome == BattleOutcome.Victorious) aWin++; else if (r.EmpireOutcome == BattleOutcome.Victorious) eWin++; else none++;
            if (r.AllianceRetreated) aRet++;
            if (r.EmpireRetreated) eRet++;
            aSurv += r.AllianceSurvivors; eSurv += r.EmpireSurvivors; secs += r.Seconds;
        }
        string Pct(int n) => (100.0 * n / battles).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        string Mean(long n) => ((double)n / battles).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{battles} {ruleset} battles, seeds {seed}..{seed + (ulong)battles - 1}, subsystem rolls {(rolls ? "on" : "off")}");
        Console.WriteLine($"Alliance ({string.Join(", ", al.Select(x => $"{x.Item2} x {x.Item1}"))})");
        Console.WriteLine($"Empire   ({string.Join(", ", em.Select(x => $"{x.Item2} x {x.Item1}"))})");
        Console.WriteLine($"  Alliance wins {aWin} ({Pct(aWin)}), Empire wins {eWin} ({Pct(eWin)}), no winner {none} ({Pct(none)})");
        Console.WriteLine($"  Alliance retreated {aRet} ({Pct(aRet)}), Empire retreated {eRet} ({Pct(eRet)})");
        Console.WriteLine($"  mean survivors: Alliance {Mean(aSurv)}, Empire {Mean(eSurv)}; mean length {Mean(secs)} s");
        return 0;
    }

    private static string ResolveClass(IReadOnlyDictionary<string, ShipClass> ships, string name)
    {
        name = name.Trim();
        if (Aliases.TryGetValue(name, out var a)) name = a;
        var hit = ships.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ArgumentException($"Unknown ship class '{name}'. Known: {string.Join(", ", ships.Keys)}.");
    }

    private static string Canonical(Galaxy g, string name)
    {
        var hit = g.Systems.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit?.Name ?? throw new ArgumentException($"Unknown system '{name}'.");
    }

    private static string Describe(Fleet f) => $"{(f.Side == Side.Empire ? "Imperial" : "Rebel")} fleet {f.Name}";

    private static string Describe(GameState s, int id) => s.Fleets.TryGetValue(id, out var f) ? Describe(f) : $"fleet {id}";

    private static string Describe(GameState s, IEnumerable<int> ids) => string.Join(" and ", ids.Select(i => Describe(s, i)));

    private static void Print(GameEvent e, GameState s, ref string? lastContact)
    {
        string? line = e switch
        {
            OrderAccepted { Fleet: int f } a =>
                $"{Describe(s, f)} is ordered {a.Path!.Items[0]} -> {a.Path.Items[^1]} via {string.Join(", ", a.Path.Items.Skip(1).Take(a.Path.Items.Count - 2).DefaultIfEmpty("(direct)"))}; {a.TotalTicks} days, due day {a.ArrivalDay}",
            OrderAccepted a => $"order #{a.Sequence} accepted",
            OrderRejected r => $"order #{r.Sequence} rejected ({r.Reason})",
            FleetDeparted d => $"{Describe(s, d.Fleet)} leaves {d.From} (first hop {d.FirstHopTicks} days)",
            FleetPassed p => $"{Describe(s, p.Fleet)} passes {p.System}",
            FleetArrived a => $"{Describe(s, a.Fleet)} arrives at {a.System}",
            ForcedStop x => $"{Describe(s, x.Fleet)} is stopped at {x.System} by {Describe(s, x.Blockers.Items)}",
            Contact c => ContactLine(s, c, ref lastContact),
            BlockadeStarted b => $"{b.Blockader} fleet blockades {b.System} (Classic flag on)",
            BlockadeLifted b => $"blockade of {b.System} by the {b.Blockader} ends",
            _ => $"{e.Kind}",
        };
        if (line != null) Console.WriteLine($"Day {e.Day}: {line}");
    }

    // A Contact is raised every day while both sides are present; the log shows it once until the set of fleets changes.
    private static string? ContactLine(GameState s, Contact c, ref string? last)
    {
        string key = $"{c.System}|{c.EmpireFleets}|{c.AllianceFleets}";
        if (key == last) return null;
        last = key;
        return $"contact at {c.System}: {Describe(s, c.AllianceFleets.Items)} meets {Describe(s, c.EmpireFleets.Items)}";
    }

    private static string FindRoot() => GameData.FindRepoRoot(); // the composition root's data folder lookup

    private sealed class Options
    {
        public string FleetName = "Red";
        public Side Side = Side.Alliance;
        public List<(string, int)> Ships = new();
        public string From = "", To = "";
        public List<(List<(string, int)> spec, string at)> Enemies = new();
        public List<(string system, Side side)> Owners = new();
        public ForcedStopTrigger Trigger = ForcedStopTrigger.PaulRule;
        public int Days = 40;
        public ulong Seed = 1;

        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                string key = a[i];
                string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
                switch (key)
                {
                    case "--fleet": o.FleetName = Next(); break;
                    case "--side": o.Side = ParseSide(Next()); break;
                    case "--ships": o.Ships = ParseShips(Next()); break;
                    case "--from": o.From = Next(); break;
                    case "--to": o.To = Next(); break;
                    case "--enemy":
                        {
                            var v = Next(); int at = v.LastIndexOf('@');
                            if (at < 0) throw new ArgumentException($"--enemy needs \"<Class>:<n>@<System>\", got '{v}'.");
                            o.Enemies.Add((ParseShips(v[..at]), v[(at + 1)..]));
                            break;
                        }
                    case "--owner":
                        {
                            var v = Next(); int eq = v.LastIndexOf('=');
                            if (eq < 0) throw new ArgumentException($"--owner needs \"<System>=<Side>\", got '{v}'.");
                            o.Owners.Add((v[..eq], ParseSide(v[(eq + 1)..])));
                            break;
                        }
                    case "--trigger":
                        o.Trigger = Enum.TryParse<ForcedStopTrigger>(Next(), ignoreCase: true, out var t) && Enum.IsDefined(t)
                            ? t : throw new ArgumentException("--trigger must be PaulRule, AnyArmedFleet or GravityWellShip.");
                        break;
                    case "--days": o.Days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                    case "--seed": o.Seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                    default: throw new ArgumentException($"Unknown option '{key}'.");
                }
            }
            if (o.Ships.Count == 0) throw new ArgumentException("--ships is required.");
            if (o.From.Length == 0 || o.To.Length == 0) throw new ArgumentException("--from and --to are required.");
            return o;
        }

        private static Side ParseSide(string v) =>
            Enum.TryParse<Side>(v, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : throw new ArgumentException($"Side must be Alliance or Empire, got '{v}'.");

        public static List<(string, int)> ParseShipList(string v) => ParseShips(v);

        private static List<(string, int)> ParseShips(string v)
        {
            var list = new List<(string, int)>();
            foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int c = part.LastIndexOf(':');
                string name = c < 0 ? part : part[..c];
                int n = 1;
                if (c >= 0 && !(int.TryParse(part[(c + 1)..], out n) && n > 0)) throw new ArgumentException($"Bad ship count in '{part}'.");
                list.Add((name, n));
            }
            if (list.Count == 0) throw new ArgumentException("A ship list needs at least one entry.");
            return list;
        }
    }
}
