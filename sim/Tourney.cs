using System.Collections.Concurrent;
using System.Globalization;
using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Setup;

// AI tournament (m6 AI trainer, ai/12 P11 "headless AI-vs-AI harness"). Plays every seed twice, A as the Empire against B as the Alliance and
// then the other way round, so the side a level plays cancels out. Reads state and events only; draws no random numbers itself.
internal static class Tourney
{
    public const string Usage = """
          dotnet run --project sim -- tourney [--a hard] [--b medium] [--seeds 1-16] [--days 3000] [--jobs 4] [--csv out.csv] [--verbose] [--a-params K=V,...] [--b-params K=V,...] [--settings K=V,...]
        tourney plays the simple AI at level A against level B (easy, medium, hard; core/AI/AiLevel.cs), fog on for both, from the MedA start:
        every seed once with A as the Empire and once with A as the Alliance. It prints, per level and side, the wins, the median win day, the day
        the Empire's own picture first showed the true Rebel HQ, and each level's mean share of the systems the two sides hold at the end (a win
        counts 100%, a loss 0%). --a-params / --b-params set SimpleAiParams values on top of a level (e.g. "MilitaryEvery=1,ScoutCount=8"; ints and
        bools), to try a knob before it becomes part of a level. --seeds takes a range (1-16), a list (1,4,9) or a count (16 = 1-16). --jobs plays that many games at once.
        """;

    private sealed record GameResult(int Seed, bool AIsEmpire, AiLevel EmpireLevel, AiLevel AllianceLevel, Side? Winner, int Day, int EmpireSystems,
                                     int AllianceSystems, int? HqFoundDay, int EmpireCaptures, int AllianceCaptures)
    {
        /// <summary>Share (percent) of the systems the two sides hold at the end for <paramref name="side"/>; a win is 100, a loss 0.</summary>
        public double Share(Side side) => Winner is Side w ? (w == side ? 100 : 0)
            : 100.0 * (side == Side.Empire ? EmpireSystems : AllianceSystems) / Math.Max(1, EmpireSystems + AllianceSystems);
    }

    public static int Run(string[] a)
    {
        AiLevel la = AiLevel.Hard, lb = AiLevel.Medium; string? pa = null, pb = null, ps = null; int days = 3000, jobs = Environment.ProcessorCount; string? csv = null; bool verbose = false;
        var seeds = Enumerable.Range(1, 16).ToList();
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--a": la = AiLevels.Parse(Next()) ?? throw new ArgumentException("--a must be easy, medium or hard."); break;
                case "--b": lb = AiLevels.Parse(Next()) ?? throw new ArgumentException("--b must be easy, medium or hard."); break;
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--jobs": jobs = int.TryParse(Next(), out var j) && j > 0 ? j : throw new ArgumentException("--jobs must be a positive integer."); break;
                case "--seeds": seeds = ParseSeeds(Next()); break;
                case "--csv": csv = Next(); break;
                case "--a-params": pa = Next(); break;
                case "--b-params": pb = Next(); break;
                case "--settings": ps = Next(); break;
                case "--verbose": verbose = true; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var paramsA = WithOverrides(AiLevels.For(la), pa); var paramsB = WithOverrides(AiLevels.For(lb), pb);
        var settings = SettingsWith(ps);
        Console.WriteLine($"Tournament: {la}{(pa != null ? " [" + pa + "]" : "")} (A) against {lb}{(pb != null ? " [" + pb + "]" : "")} (B), seeds {string.Join(",", seeds)}, up to {days} days, fog on, {jobs} at once{(ps != null ? ", settings " + ps : "")}.");
        NewGame.Create(new NewGameOptions { Seed = seeds[0], Preset = StartPreset.MedA }); // loads the shared data once before the parallel games
        var games = seeds.SelectMany(s => new[] { (Seed: s, AEmp: true), (Seed: s, AEmp: false) }).ToList();
        var results = new ConcurrentBag<GameResult>();
        Parallel.ForEach(games, new ParallelOptions { MaxDegreeOfParallelism = jobs }, g =>
        {
            var r = Play(g.Seed, g.AEmp, g.AEmp ? la : lb, g.AEmp ? lb : la, g.AEmp ? paramsA : paramsB, g.AEmp ? paramsB : paramsA, days, settings);
            results.Add(r);
            if (verbose) lock (results) Console.WriteLine("  " + Line(r));
        });
        var all = results.OrderBy(r => r.Seed).ThenBy(r => !r.AIsEmpire).ToList();
        if (!verbose) foreach (var r in all) Console.WriteLine("  " + Line(r));
        Console.WriteLine();
        foreach (var (isA, lvl) in new[] { (true, la), (false, lb) })
        {
            var asE = all.Where(r => r.AIsEmpire == isA).ToList();
            var asA = all.Where(r => r.AIsEmpire != isA).ToList();
            Console.WriteLine($"{(isA ? "A" : "B")} = {lvl}:");
            Console.WriteLine($"  as the Empire:   wins {asE.Count(r => r.Winner == Side.Empire)}/{asE.Count}, losses {asE.Count(r => r.Winner == Side.Alliance)}, " +
                              $"median win day {Median(asE.Where(r => r.Winner == Side.Empire).Select(r => r.Day))}, HQ found in {asE.Count(r => r.HqFoundDay != null)}/{asE.Count} " +
                              $"(median day {Median(asE.Where(r => r.HqFoundDay != null).Select(r => r.HqFoundDay!.Value))}), mean share {Mean(asE.Select(r => r.Share(Side.Empire)))}%");
            Console.WriteLine($"  as the Alliance: wins {asA.Count(r => r.Winner == Side.Alliance)}/{asA.Count}, losses {asA.Count(r => r.Winner == Side.Empire)}, " +
                              $"median win day {Median(asA.Where(r => r.Winner == Side.Alliance).Select(r => r.Day))}, mean share {Mean(asA.Select(r => r.Share(Side.Alliance)))}%");
            Console.WriteLine($"  overall mean share {Mean(asE.Select(r => r.Share(Side.Empire)).Concat(asA.Select(r => r.Share(Side.Alliance))))}%");
        }
        if (csv != null)
        {
            using var w = new StreamWriter(csv);
            w.WriteLine("seed,a_is_empire,empire_level,alliance_level,winner,day,empire_systems,alliance_systems,hq_found_day,empire_captures,alliance_captures");
            foreach (var r in all)
                w.WriteLine(string.Join(",", r.Seed, r.AIsEmpire, r.EmpireLevel, r.AllianceLevel, r.Winner?.ToString() ?? "", r.Day, r.EmpireSystems, r.AllianceSystems,
                                        r.HqFoundDay?.ToString(CultureInfo.InvariantCulture) ?? "", r.EmpireCaptures, r.AllianceCaptures));
        }
        Console.WriteLine($"Runtime: {games.Count} games in {clock.Elapsed.TotalSeconds:F0} s.");
        return 0;
    }

    private static GameResult Play(int seed, bool aIsEmpire, AiLevel empire, AiLevel alliance, SimpleAiParams pe, SimpleAiParams pa, int days, GameSettings? settings)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = settings });
        var match = new AiMatch(host, pe, pa);
        int? found = null;
        while (host.Game.Day < days && host.Victory.Winner == null)
        {
            match.Step();
            if (found == null && host.HqSystem != null && host.Fog.KnownHq(Side.Empire)?.System == host.HqSystem) found = host.Game.Day;
        }
        var caps = host.Game.Events.OfType<SystemCaptured>().ToList();
        int Held(Side s) => host.Game.State.SystemOwners.Count(kv => kv.Value == s);
        return new GameResult(seed, aIsEmpire, empire, alliance, host.Victory.Winner, host.Game.Day, Held(Side.Empire), Held(Side.Alliance), found,
                              caps.Count(c => c.To == Side.Empire), caps.Count(c => c.To == Side.Alliance));
    }

    /// <summary>Game settings for every game (--settings K=V,...: int, bool or enum properties of GameSettings, nullable ones too, "null" for
    /// the default; dotted keys reach the nested tables, e.g. Characters.TalentedSkipStrong=false or Battle.Ruleset=Classic); null = the defaults.</summary>
    internal static GameSettings? SettingsWith(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var copy = new GameSettings();
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) throw new ArgumentException($"{pair} is not K=V.");
            var path = kv[0].Split('.');
            object target = copy;
            for (int i = 0; i < path.Length - 1; i++)
            {
                // A nested table: a fresh copy (the defaults when null), so a shared default instance is never changed.
                var nested = Prop(target, path[i]);
                var current = nested.GetValue(target);
                var fresh = current == null ? Activator.CreateInstance(nested.PropertyType)
                          : current.GetType().GetMethod("<Clone>$")?.Invoke(current, null) ?? throw new ArgumentException($"{path[i]} cannot be copied.");
                nested.SetValue(target, fresh);
                target = fresh!;
            }
            var prop = Prop(target, path[^1]);
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            bool nullable = !prop.PropertyType.IsValueType || prop.PropertyType != t;
            object? v = nullable && kv[1].Equals("null", StringComparison.OrdinalIgnoreCase) ? null
                      : t == typeof(int) ? int.Parse(kv[1], CultureInfo.InvariantCulture)
                      : t == typeof(bool) ? bool.Parse(kv[1])
                      : t.IsEnum ? Enum.Parse(t, kv[1], ignoreCase: true)
                      : throw new ArgumentException($"{kv[0]} is not an int, a bool or an enum.");
            prop.SetValue(target, v);
        }
        return copy;

        static System.Reflection.PropertyInfo Prop(object o, string name) =>
            o.GetType().GetProperty(name) ?? throw new ArgumentException($"{o.GetType().Name} has no {name}.");
    }

    /// <summary>A copy of the parameters with "Name=value" pairs set (int and bool properties of SimpleAiParams).</summary>
    internal static SimpleAiParams WithOverrides(SimpleAiParams p, string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return p;
        var copy = p with { };
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = pair.Split('=', 2);
            var prop = typeof(SimpleAiParams).GetProperty(kv[0]) ?? throw new ArgumentException($"SimpleAiParams has no {kv[0]}.");
            object v = prop.PropertyType == typeof(int) ? int.Parse(kv[1], CultureInfo.InvariantCulture)
                     : prop.PropertyType == typeof(bool) ? bool.Parse(kv[1])
                     : throw new ArgumentException($"{kv[0]} is not an int or a bool.");
            prop.SetValue(copy, v);
        }
        return copy;
    }

    private static string Line(GameResult r) =>
        $"seed {r.Seed,3}: Empire {r.EmpireLevel,-6} vs Alliance {r.AllianceLevel,-6} -> {(r.Winner is Side w ? $"{w} wins day {r.Day}" : $"no winner by day {r.Day}"),-26} " +
        $"systems {r.EmpireSystems}/{r.AllianceSystems}, HQ found {(r.HqFoundDay is int h ? "day " + h : "never")}, captures {r.EmpireCaptures}/{r.AllianceCaptures}";

    private static List<int> ParseSeeds(string s)
    {
        if (s.Contains('-')) { var p = s.Split('-'); int lo = int.Parse(p[0], CultureInfo.InvariantCulture), hi = int.Parse(p[1], CultureInfo.InvariantCulture); return Enumerable.Range(lo, hi - lo + 1).ToList(); }
        if (s.Contains(',')) return s.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
        int n = int.Parse(s, CultureInfo.InvariantCulture); return Enumerable.Range(1, n).ToList();
    }

    private static string Median(IEnumerable<int> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? "-" : l[l.Count / 2].ToString(CultureInfo.InvariantCulture);
    }

    private static string Mean(IEnumerable<double> xs)
    {
        var l = xs.ToList();
        return l.Count == 0 ? "-" : l.Average().ToString("F1", CultureInfo.InvariantCulture);
    }
}
