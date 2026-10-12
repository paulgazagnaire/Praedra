using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;

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
        economy runs a fixed demo setup (four systems per side with mines and refineries) and prints each side's pools every 30 days.
        build runs a fixed demo: an Empire yard builds two fighters, a mine and two regiments; the regiments' delivery is held at a Rebel picket
        until the picket moves away (--picket-leaves <day>, default 100; "never" keeps it there). --no-forced-stop sets DeliveryForcedStop off.
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
            if (args[0] != "move") throw new ArgumentException($"Unknown command '{args[0]}'.");
            return Move(Options.Parse(args.Skip(1).ToArray()));
        }
        catch (Exception e) when (e is ArgumentException or Rebellion.Core.Data.DataLoadException or FileNotFoundException)
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
        Console.WriteLine($"{Describe(f)}: {string.Join(", ", f.Ships.Concat(f.Squadrons).GroupBy(x => x).Select(g => $"{g.Count()} x {g.Key}"))}, speed {fleets.SpeedOf(f)}");
        foreach (var e in game.State.Fleets.Values.Where(x => x.Id != mover))
            Console.WriteLine($"{Describe(e)} waits at {e.At}: {string.Join(", ", e.Ships.Concat(e.Squadrons).GroupBy(x => x).Select(g => $"{g.Count()} x {g.Key}"))}");
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

    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Rebellion.sln"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        throw new FileNotFoundException("Rebellion.sln not found above the runner or the current folder; run from inside the repository.");
    }

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
