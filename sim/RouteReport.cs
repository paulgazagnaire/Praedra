using System.Collections.Concurrent;
using Praedra.Core.AI;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Setup;
using Praedra.Core.Travel;

// Route report: how AI games use the hyperspace routes (galaxy-shape test, Paul 2026-10-26). Plays AI-vs-AI games and reads each fleet move
// the moment it is accepted (its planned path), the deliveries' paths, the battles and the holdings. Reads state and events only; draws no
// random numbers itself.
internal static class RouteReport
{
    public const string Usage = """
          dotnet run --project sim -- routes [--hand-level hard] [--collective-level medium] [--seeds 1-8] [--days 3000] [--jobs 4] [--settings K=V,...] [--dump replay.json]
        routes plays AI-vs-AI games (fog on, MedA start) and reports how fleets and deliveries use the hyperspace routes: the share of flight time
        and distance on great routes (tier 1), minor routes (tier 2) and open space, the time saved against flying straight, the busiest links and
        routes, where battles and captures happen, and who holds each region every 250 days. --dump writes a replay (owners and fleets every 25
        days, battles, legs flown per link) as JSON for a map viewer.
        """;

    private const int Window = 250; // days between holding snapshots
    private const int FrameDays = 25; // days between replay frames (--dump)
    private const int LongMove = 150; // map units (the map is 1000 across): a "long" move, start to destination

    private sealed class Tally
    {
        public long Moves, LongMoves, LongOnRoute, MovesOnGreat, MovesOnMinor, TicksGreat, TicksMinor, TicksOff, TicksStraight, Hops;
        public long DistGreat, DistMinor, DistOff, Deliveries, DeliveriesOnRoute;
        public readonly Dictionary<string, long> Links = new(StringComparer.Ordinal), Routes = new(StringComparer.Ordinal);
        public readonly long[] Battles = new long[3], Captures = new long[3];
        public readonly Dictionary<(int Day, Side Side, string Group), long> Held = new();
        public readonly List<(int Seed, Side? Winner, int Day, int? CharterLost, int Hand, int Collective)> Games = new();

        public void Add(Tally o)
        {
            Moves += o.Moves; LongMoves += o.LongMoves; LongOnRoute += o.LongOnRoute; MovesOnGreat += o.MovesOnGreat; MovesOnMinor += o.MovesOnMinor; TicksGreat += o.TicksGreat; TicksMinor += o.TicksMinor;
            TicksOff += o.TicksOff; TicksStraight += o.TicksStraight; Hops += o.Hops; DistGreat += o.DistGreat; DistMinor += o.DistMinor; DistOff += o.DistOff;
            Deliveries += o.Deliveries; DeliveriesOnRoute += o.DeliveriesOnRoute;
            foreach (var kv in o.Links) Links[kv.Key] = Links.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (var kv in o.Routes) Routes[kv.Key] = Routes.GetValueOrDefault(kv.Key) + kv.Value;
            for (int i = 0; i < 3; i++) { Battles[i] += o.Battles[i]; Captures[i] += o.Captures[i]; }
            foreach (var kv in o.Held) Held[kv.Key] = Held.GetValueOrDefault(kv.Key) + kv.Value;
            Games.AddRange(o.Games);
        }
    }

    public static int Run(string[] a)
    {
        AiLevel hand = AiLevel.Hard, collective = AiLevel.Medium; int days = 3000, jobs = Environment.ProcessorCount; string? ps = null, dump = null;
        var seeds = Enumerable.Range(1, 8).ToList();
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--hand-level": hand = AiLevels.Parse(Next()) ?? throw new ArgumentException("--hand-level must be easy, medium or hard."); break;
                case "--collective-level": collective = AiLevels.Parse(Next()) ?? throw new ArgumentException("--collective-level must be easy, medium or hard."); break;
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--jobs": jobs = int.TryParse(Next(), out var j) && j > 0 ? j : throw new ArgumentException("--jobs must be a positive integer."); break;
                case "--seeds": seeds = Tourney.ParseSeeds(Next()); break;
                case "--settings": ps = Next(); break;
                case "--dump": dump = Next(); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var settings = Tourney.SettingsWith(ps);
        Console.WriteLine($"Route report: Hand {hand} vs Collective {collective}, seeds {string.Join(",", seeds)}, up to {days} days, fog on{(ps != null ? ", settings " + ps : "")}.");
        var probe = NewGame.Create(new NewGameOptions { Seed = seeds[0], Preset = StartPreset.MedA, Settings = settings });
        var galaxy = probe.Fleets.Galaxy;
        // system kind: 0 = on a great route, 1 = on minor routes only, 2 = open space (no route link)
        var kind = galaxy.Systems.Select(s => galaxy.NeighboursOf(s.Index) is var n && n.Any(x => x.Lane.Tier == 1) ? 0 : n.Count > 0 ? 1 : 2).ToArray();
        var groups = galaxy.Systems.Where(s => !s.IsOutsideSectors).Select(s => s.Group).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();

        var tallies = new ConcurrentBag<Tally>();
        var replays = new ConcurrentDictionary<int, object>();
        Parallel.ForEach(seeds, new ParallelOptions { MaxDegreeOfParallelism = jobs }, seed =>
        {
            var t = new Tally();
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = settings });
            var match = new AiMatch(host, AiLevels.For(hand), AiLevels.For(collective));
            var straight = new Pathfinder(galaxy, TravelMode.Straight); // one per game: Find writes to an unsynchronised cache
            int scanned = host.Game.Events.Count; int? charterLost = null;
            var frames = new List<object>(); var battles = new List<int[]>(); var lanePasses = new Dictionary<string, int>(StringComparer.Ordinal);
            void Frame()
            {
                if (dump == null) return;
                var owners = new char[galaxy.Systems.Count];
                for (int k = 0; k < owners.Length; k++)
                    owners[k] = host.Game.State.SystemOwners.TryGetValue(galaxy.Systems[k].Name, out var o) && o is Side os ? (os == Side.Hand ? 'H' : 'C') : '.';
                var fl = new List<int[]>();
                foreach (var f in host.Game.State.Fleets.Values)
                {
                    int ships = f.Ships.Count + f.Squadrons.Count, side = f.Side == Side.Hand ? 0 : 1;
                    if (ships == 0) continue;
                    if (f.Transit is { } tr && tr.HopIndex + 1 < tr.Path.Count) fl.Add(new[] { galaxy.Get(tr.Path[tr.HopIndex]).Index, galaxy.Get(tr.Path[tr.HopIndex + 1]).Index, side, ships });
                    else if (f.At != null) fl.Add(new[] { galaxy.Get(f.At).Index, -1, side, ships });
                }
                int hq = host.HqSystem is { } hs && galaxy.TryFindSystem(hs, out var hi) ? hi : -1;
                frames.Add(new { day = host.Game.Day, owners = new string(owners), fleets = fl, hq });
            }
            Frame();
            void Snapshot()
            {
                foreach (var kv in host.Game.State.SystemOwners)
                    if (kv.Value is Side s && galaxy.TryFindSystem(kv.Key, out var ix))
                        t.Held[(host.Game.Day / Window * Window, s, galaxy.Systems[ix].Group)] = t.Held.GetValueOrDefault((host.Game.Day / Window * Window, s, galaxy.Systems[ix].Group)) + 1;
            }
            Snapshot();
            while (host.Game.Day < days && host.Victory.Winner == null)
            {
                match.Step();
                for (var evs = host.Game.Events; scanned < evs.Count; scanned++)
                    switch (evs[scanned])
                    {
                        case FleetDeparted fd when host.Game.State.Fleets.TryGetValue(fd.Fleet, out var f) && f.Transit is { } tr && tr.Path.Count >= 2:
                            CountMove(t, galaxy, straight, tr.Path, tr.HopTicks, host.Fleets.SpeedOf(f));
                            if (dump != null)
                                for (int h = 0; h + 1 < tr.Path.Count; h++)
                                {
                                    var (x, y) = (galaxy.Get(tr.Path[h]).Index, galaxy.Get(tr.Path[h + 1]).Index);
                                    string key = Math.Min(x, y) + "-" + Math.Max(x, y);
                                    lanePasses[key] = lanePasses.GetValueOrDefault(key) + 1;
                                }
                            break;
                        case DeliveryDeparted dd when dd.Path.Items is { Count: >= 2 } dp:
                            t.Deliveries++;
                            bool any = false;
                            for (int h = 0; h + 1 < dp.Count; h++) any |= galaxy.FindLane(dp[h], dp[h + 1]) != null;
                            if (any) t.DeliveriesOnRoute++;
                            break;
                        case BattleEnded be when galaxy.TryFindSystem(be.Result.System, out var bi):
                            t.Battles[kind[bi]]++;
                            battles.Add(new[] { be.Day, bi });
                            break;
                        case SystemCaptured sc when galaxy.TryFindSystem(sc.System, out var ci):
                            t.Captures[kind[ci]]++;
                            if (sc.System == ControlModule.CapitalSystem && sc.From == Side.Hand && charterLost == null) charterLost = sc.Day;
                            break;
                    }
                if (host.Game.Day % Window == 0) Snapshot();
                if (host.Game.Day % FrameDays == 0) Frame();
            }
            if (host.Game.Day % FrameDays != 0) Frame();
            int Held(Side s) => host.Game.State.SystemOwners.Count(kv => kv.Value == s);
            t.Games.Add((seed, host.Victory.Winner, host.Game.Day, charterLost, Held(Side.Hand), Held(Side.Collective)));
            if (dump != null) replays[seed] = new { seed, winner = host.Victory.Winner?.ToString(), day = host.Game.Day, charterLost, frames, battles, legs = lanePasses };
            tallies.Add(t);
        });
        var all = new Tally();
        foreach (var t in tallies) all.Add(t);
        Print(all, galaxy, kind, groups, days);
        if (dump != null)
        {
            var doc = new
            {
                hand = hand.ToString(), collective = collective.ToString(), settings = ps,
                systems = galaxy.Systems.Select(s => new { name = s.Name, x = s.MapX, y = s.MapY, group = s.Group, sector = s.Sector, kind = kind[s.Index] }),
                lanes = galaxy.Lanes.Select(l => new { a = l.A, b = l.B, tier = l.Tier, routes = l.Routes }),
                games = replays.OrderBy(kv => kv.Key).Select(kv => kv.Value),
            };
            File.WriteAllText(dump, System.Text.Json.JsonSerializer.Serialize(doc));
            Console.WriteLine($"Replay written to {dump}.");
        }
        return 0;
    }

    private static void CountMove(Tally t, Galaxy galaxy, Pathfinder straight, IReadOnlyList<string> path, IReadOnlyList<int> hopTicks, int speed)
    {
        t.Moves++;
        bool great = false, minor = false;
        for (int h = 0; h + 1 < path.Count; h++)
        {
            var a = galaxy.Get(path[h]); var b = galaxy.Get(path[h + 1]);
            int dist = TravelFormula.MapDistance(a.MapX, a.MapY, b.MapX, b.MapY);
            int ticks = h < hopTicks.Count ? hopTicks[h] : 0;
            t.Hops++;
            switch (galaxy.FindLane(a.Index, b.Index))
            {
                case { Tier: 1 } lane:
                    great = true; t.TicksGreat += ticks; t.DistGreat += dist;
                    Bump(t, lane);
                    break;
                case { } lane:
                    minor = true; t.TicksMinor += ticks; t.DistMinor += dist;
                    Bump(t, lane);
                    break;
                default:
                    t.TicksOff += ticks; t.DistOff += dist;
                    break;
            }
        }
        var (s0, s1) = (galaxy.Get(path[0]), galaxy.Get(path[^1]));
        if (TravelFormula.MapDistance(s0.MapX, s0.MapY, s1.MapX, s1.MapY) >= LongMove) { t.LongMoves++; if (great || minor) t.LongOnRoute++; }
        if (great) t.MovesOnGreat++;
        else if (minor) t.MovesOnMinor++;
        var s = straight.Find(path[0], path[^1], speed);
        t.TicksStraight += s.Ok ? s.TotalTicks : hopTicks.Sum();
    }

    private static void Bump(Tally t, Lane lane)
    {
        string link = LinkKey(lane);
        t.Links[link] = t.Links.GetValueOrDefault(link) + 1;
        foreach (var r in (string.IsNullOrEmpty(lane.Routes) ? $"unnamed tier {lane.Tier}" : lane.Routes).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            t.Routes[r] = t.Routes.GetValueOrDefault(r) + 1;
    }

    private static string LinkKey(Lane l) => $"{l.NameA} - {l.NameB} ({(string.IsNullOrEmpty(l.Routes) ? "unnamed" : l.Routes)}, tier {l.Tier})";

    private static string Pct(long part, long whole) => whole == 0 ? "-" : $"{100.0 * part / whole:F0}%";

    private static void Print(Tally t, Galaxy galaxy, int[] kind, List<string> groups, int days)
    {
        string[] kinds = { "on a great route", "on minor routes only", "open space" };
        int[] systems = { kind.Count(k => k == 0), kind.Count(k => k == 1), kind.Count(k => k == 2) };
        Console.WriteLine();
        Console.WriteLine("Games:");
        foreach (var g in t.Games.OrderBy(g => g.Seed))
            Console.WriteLine($"  seed {g.Seed,3}: {(g.Winner is Side w ? $"{w} wins day {g.Day}" : $"no winner by day {g.Day}"),-28} systems Hand {g.Hand} / Collective {g.Collective}" +
                              $"{(g.CharterLost is int c ? $", Charter fell day {c}" : "")}");
        Console.WriteLine();
        Console.WriteLine($"Map: {galaxy.Systems.Count} systems; {systems[0]} on a great route, {systems[1]} on minor routes only, {systems[2]} in open space. " +
                          $"Links: {galaxy.Lanes.Count(l => l.Tier == 1)} great, {galaxy.Lanes.Count(l => l.Tier != 1)} minor.");
        long ticks = t.TicksGreat + t.TicksMinor + t.TicksOff, dist = t.DistGreat + t.DistMinor + t.DistOff;
        Console.WriteLine($"Fleet moves: {t.Moves} ({t.Hops} legs). Used a great route: {Pct(t.MovesOnGreat, t.Moves)}; minor routes only: {Pct(t.MovesOnMinor, t.Moves)}; " +
                          $"open space only: {Pct(t.Moves - t.MovesOnGreat - t.MovesOnMinor, t.Moves)}.");
        Console.WriteLine($"  Long moves ({LongMove}+ map units start to end): {t.LongMoves}; used a route link: {Pct(t.LongOnRoute, t.LongMoves)}.");
        Console.WriteLine($"  Flight time: great routes {Pct(t.TicksGreat, ticks)}, minor routes {Pct(t.TicksMinor, ticks)}, open space {Pct(t.TicksOff, ticks)}.");
        Console.WriteLine($"  Distance:    great routes {Pct(t.DistGreat, dist)}, minor routes {Pct(t.DistMinor, dist)}, open space {Pct(t.DistOff, dist)}.");
        Console.WriteLine($"  Time against flying straight: {ticks} ticks instead of {t.TicksStraight} ({Pct(t.TicksStraight - ticks, t.TicksStraight)} saved).");
        Console.WriteLine($"Deliveries: {t.Deliveries}; used a route link: {Pct(t.DeliveriesOnRoute, t.Deliveries)}.");
        long battles = t.Battles.Sum(), caps = t.Captures.Sum();
        Console.WriteLine($"Where the fighting is ({battles} battles, {caps} captures; share against the share of systems):");
        for (int k = 0; k < 3; k++)
            Console.WriteLine($"  {kinds[k],-22} battles {Pct(t.Battles[k], battles),4}, captures {Pct(t.Captures[k], caps),4}, systems {Pct(systems[k], galaxy.Systems.Count),4}");
        Console.WriteLine("Busiest routes (legs flown):");
        foreach (var kv in t.Routes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(12)) Console.WriteLine($"  {kv.Value,7}  {kv.Key}");
        Console.WriteLine("Busiest links (legs flown):");
        foreach (var kv in t.Links.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(15)) Console.WriteLine($"  {kv.Value,7}  {kv.Key}");
        int unused = galaxy.Lanes.Count(l => !t.Links.ContainsKey(LinkKey(l)));
        Console.WriteLine($"Links never flown: {unused} of {galaxy.Lanes.Count}.");
        Console.WriteLine($"Holdings by region, mean over the games still running (every {Window} days; Hand / Collective):");
        Console.WriteLine("  day    " + string.Join("  ", groups.Select(g => $"{g,-16}")) + "  games");
        for (int day = 0; day <= days; day += Window)
        {
            int running = t.Games.Count(g => g.Day >= day);
            if (running == 0) break;
            Console.WriteLine($"  {day,5}  " + string.Join("  ", groups.Select(g =>
                $"{$"{t.Held.GetValueOrDefault((day, Side.Hand, g)) / (double)running:F0} / {t.Held.GetValueOrDefault((day, Side.Collective, g)) / (double)running:F0}",-16}")) + $"  {running}");
        }
    }
}
