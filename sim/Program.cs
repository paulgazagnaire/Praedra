using Rebellion.Core.Clock;
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
