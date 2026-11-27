using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using Praedra.Core.Setup;

// Crash and softlock hunt (Paul 2026-11-27: "hundreds of long games across seeds and difficulties, plus old saves, with every crash fixed", and
// tracked from now on). Plays many AI games to the end or to the day limit and reports, deduplicated: crashes (any exception out of a day),
// hangs (a day that does not finish within --hang-seconds), slow days, broken state found by the checks below, and save/load round trips that
// change the game. Reads state only; draws no random numbers itself. The wall clock is used for the hang watch only, never by a game.
internal static class Hunt
{
    public const string Usage = """
          dotnet run --project sim -c Release -- hunt [--seeds 1-24] [--days 3000] [--levels ee,mm,hh,mh,hm,eh] [--presets all] [--idle-seeds 1-8]
                                                      [--variant-seeds 1-8] [--saves DIR[,DIR]] [--save-days 600] [--jobs N] [--hang-seconds 120]
                                                      [--report hunt.md]
        hunt plays seeds x level pairs (Hand level then Collective level: e easy, m medium, h hard; fog on), each seed from one start preset in turn
        (--presets all: Easy, MedA, MedE, HardA, HardE; or a list such as meda,mede), to --days or a win. --idle-seeds adds, per seed, a game where
        the Hand gives no orders but Sibyl runs both her jobs, against a Medium and a Hard Collective (a human who leaves the game to Sibyl).
        --variant-seeds adds Medium mirror games under other rules (Classic battles, New support, Classic control, no incidents). --saves plays on
        every save file in those folders (*.txt, *.json) with Medium AIs for --days more days. Every game saves and loads itself at each of
        --save-days (comma list, 0 = never) and plays on from the loaded copy; a round trip that changes the state hash is reported. The state
        checks run every 25 days: a fleet that is nowhere, a fleet or delivery or mission overdue by more than 2 days, and ids that point at no
        object. Exit code 0 when nothing was found, 1 when anything was, 2 on a hang (the hung game is abandoned).
        """;

    private sealed record Job(string Label, Func<GameHost> Make, SimpleAiParams Hand, SimpleAiParams Collective, bool IdleHand, int Days);

    private sealed record Problem(string Kind, string Key, string Detail, string Job, int Day);

    public static int Run(string[] a)
    {
        var seeds = Enumerable.Range(1, 24).ToList();
        int days = 3000, jobs = Environment.ProcessorCount, hangSeconds = 120;
        string levels = "ee,mm,hh,mh,hm,eh", presets = "all";
        List<int> idleSeeds = Enumerable.Range(1, 8).ToList(), variantSeeds = Enumerable.Range(1, 8).ToList(), saveDays = new() { 600 };
        var saveDirs = new List<string>();
        string? report = null;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--seeds": seeds = Tourney.ParseSeeds(Next()); break;
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--levels": levels = Next(); break;
                case "--presets": presets = Next(); break;
                case "--idle-seeds": idleSeeds = SeedsOrNone(Next()); break;
                case "--variant-seeds": variantSeeds = SeedsOrNone(Next()); break;
                case "--saves": saveDirs.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--save-days": saveDays = Next() == "0" ? new() : Next0(a[i]); break;
                case "--jobs": jobs = int.TryParse(Next(), out var j) && j > 0 ? j : throw new ArgumentException("--jobs must be a positive integer."); break;
                case "--hang-seconds": hangSeconds = int.TryParse(Next(), out var h) && h > 0 ? h : throw new ArgumentException("--hang-seconds must be a positive integer."); break;
                case "--report": report = Next(); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        static List<int> Next0(string s) => s.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();

        var work = Plan(seeds, days, levels, presets, idleSeeds, variantSeeds, saveDirs);
        Console.WriteLine($"Hunt: {work.Count} games, up to {days} days each, {jobs} at once, save/load at day {(saveDays.Count == 0 ? "never" : string.Join(",", saveDays))}, hang after {hangSeconds} s.");
        NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA }); // loads the shared data once before the parallel games

        var problems = new ConcurrentBag<Problem>();
        var progress = new ConcurrentDictionary<string, (int Day, long Ticks)>();
        var finished = new ConcurrentBag<(string Label, Side? Winner, int Day, long SlowestMs, double Seconds)>();
        var clock = Stopwatch.StartNew();
        var hung = new ConcurrentDictionary<string, int>();
        int count = 0;
        var returned = new ConcurrentDictionary<string, bool>(); // every game that came back, crashed ones too
        using var done = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                try { await Task.Delay(5000, done.Token); } catch (TaskCanceledException) { break; }
                long now = clock.ElapsedTicks;
                foreach (var (label, p) in progress)
                    if ((now - p.Ticks) / (double)Stopwatch.Frequency > hangSeconds && hung.TryAdd(label, p.Day))
                        lock (problems) Console.WriteLine($"  HANG  {label}: day {p.Day} has not finished after {hangSeconds} s");
            }
        });
        var runner = Task.Run(() => Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = jobs }, job =>
        {
            var r = PlayOne(job, saveDays, problems, progress, clock);
            progress.TryRemove(job.Label, out _);
            returned[job.Label] = true;
            if (r != null) finished.Add(r.Value);
            int n = Interlocked.Increment(ref count);
            if (r != null) lock (problems) Console.WriteLine($"  {n}/{work.Count} {job.Label}: {(r.Value.Winner?.ToString() ?? "no winner")} by day {r.Value.Day}, {r.Value.Seconds:0} s");
        }));
        // A hung game never returns: wait for every other game, then give up on the hung ones.
        while (!runner.IsCompleted)
        {
            runner.Wait(2000);
            if (!runner.IsCompleted && !hung.IsEmpty && returned.Keys.Count(k => !hung.ContainsKey(k)) + hung.Count >= work.Count) break; // a slow game that came back late counts once
        }
        done.Cancel();
        foreach (var (label, day) in hung) problems.Add(new Problem("hang", $"hang at day {day}", $"day {day} did not finish within {hangSeconds} s", label, day));

        var text = Report(work.Count, days, finished.ToList(), problems.ToList(), clock.Elapsed);
        Console.WriteLine(text);
        if (report != null) File.WriteAllText(report, text);
        return !hung.IsEmpty ? 2 : problems.IsEmpty ? 0 : 1;
    }

    private static List<int> SeedsOrNone(string s) => s is "0" or "none" ? new() : Tourney.ParseSeeds(s);

    private static List<Job> Plan(List<int> seeds, int days, string levels, string presets, List<int> idleSeeds, List<int> variantSeeds, List<string> saveDirs)
    {
        var all = new[] { StartPreset.Easy, StartPreset.MedA, StartPreset.MedE, StartPreset.HardA, StartPreset.HardE };
        var starts = presets == "all" ? all.ToList() : presets.Split(',').Select(StartPresets.Parse).ToList();
        var pairs = levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(p => p.Length == 2 ? (Hand: Level(p[0]), Collective: Level(p[1])) : throw new ArgumentException($"--levels: '{p}' is not two letters (e, m, h)."))
                          .ToList();
        var work = new List<Job>();
        foreach (int seed in seeds)
        {
            var preset = starts[(seed - 1) % starts.Count];
            foreach (var (h, c) in pairs)
                work.Add(new Job($"seed {seed} {StartPresets.Key(preset)} {h}-{c}", () => NewGame.Create(new NewGameOptions { Seed = seed, Preset = preset }),
                                 AiLevels.For(h), AiLevels.For(c), false, days));
        }
        foreach (int seed in idleSeeds)
            foreach (var c in new[] { AiLevel.Medium, AiLevel.Hard })
                work.Add(new Job($"seed {seed} med_a idle Hand + Sibyl vs {c}", () => NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA }),
                                 AiLevels.For(AiLevel.Medium), AiLevels.For(c), true, days));
        var variants = new (string Name, string Spec)[]
        {
            ("Classic battles", "Battle.Ruleset=Classic"), ("New support", "SupportRuleset=New"), ("assault-only control", "ControlRule=AssaultOnly"), ("no incidents", "IncidentsEnabled=false"),
        };
        foreach (int seed in variantSeeds)
            foreach (var (name, spec) in variants)
            {
                var settings = Tourney.SettingsWith(spec);
                work.Add(new Job($"seed {seed} med_a m-m {name}", () => NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = settings }),
                                 AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium), false, days));
            }
        foreach (var dir in saveDirs)
            foreach (var file in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".txt") || f.EndsWith(".json")).OrderBy(f => f, StringComparer.Ordinal))
            {
                string path = file;
                work.Add(new Job($"save {Path.GetFileName(path)}", () => GameHost.Load(PlayerSaveHeader.CoreSave(File.ReadAllText(path))), AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium), false, -days));
            }
        return work;
    }

    private static AiLevel Level(char c) => c switch
    {
        'e' => AiLevel.Easy, 'm' => AiLevel.Medium, 'h' => AiLevel.Hard,
        _ => throw new ArgumentException($"--levels: '{c}' is not e, m or h."),
    };

    private static (string Label, Side? Winner, int Day, long SlowestMs, double Seconds)? PlayOne(Job job, List<int> saveDays, ConcurrentBag<Problem> problems,
                                                                ConcurrentDictionary<string, (int, long)> progress, Stopwatch clock)
    {
        long started = clock.ElapsedTicks;
        GameHost host;
        progress[job.Label] = (0, clock.ElapsedTicks); // a load that hangs is caught too (day 0 = while making or loading)
        try { host = job.Make(); }
        catch (Exception e) { progress.TryRemove(job.Label, out _); Report(problems, "crash", e, job.Label, 0, "while making or loading the game"); return null; }
        // A save job plays --days more days from the save's own day (Days < 0 marks it).
        int endDay = job.Days < 0 ? host.Game.Day - job.Days : job.Days;
        var pending = new SortedSet<int>(saveDays.Where(d => d > host.Game.Day && d < endDay));
        AiMatch match; Sibyl? sibyl;
        (match, sibyl) = Attach(host, job);
        long slowest = 0;
        int lastCheck = -1;
        try
        {
            progress[job.Label] = (host.Game.Day, clock.ElapsedTicks);
            while (host.Game.Day < endDay && host.Victory.Winner == null)
            {
                int before = host.Game.Day;
                long t0 = clock.ElapsedTicks;
                sibyl?.PlanDay();
                match.Step();
                long ms = (clock.ElapsedTicks - t0) * 1000 / Stopwatch.Frequency;
                if (ms > slowest) slowest = ms;
                progress[job.Label] = (host.Game.Day, clock.ElapsedTicks);
                if (host.Game.Day != before + 1)
                    problems.Add(new Problem("clock", "a step did not advance the day by one", $"day {before} -> {host.Game.Day}", job.Label, before));
                if (host.Game.Day - lastCheck >= 25) { lastCheck = host.Game.Day; Check(host, job.Label, problems); }
                if (pending.Count > 0 && host.Game.Day >= pending.Min)
                {
                    pending.Remove(pending.Min);
                    string save = host.SaveWithSettings();
                    var loaded = GameHost.Load(save);
                    if (loaded.StateHash() != host.StateHash())
                        problems.Add(new Problem("save/load", "a save and load changes the state hash", $"day {host.Game.Day}", job.Label, host.Game.Day));
                    host = loaded;
                    (match, sibyl) = Attach(host, job);
                }
            }
        }
        catch (Exception e)
        {
            Report(problems, "crash", e, job.Label, host.Game.Day, null);
            return null;
        }
        if (slowest > 5000) problems.Add(new Problem("slow", "a day took over 5 s", $"{slowest} ms", job.Label, host.Game.Day));
        return (job.Label, host.Victory.Winner, host.Game.Day, slowest, (clock.ElapsedTicks - started) / (double)Stopwatch.Frequency);
    }

    private static (AiMatch, Sibyl?) Attach(GameHost host, Job job)
    {
        var match = new AiMatch(host, job.Hand, job.Collective, job.IdleHand ? Side.Hand : null);
        if (!job.IdleHand) return (match, null);
        var sibyl = new Sibyl(host, Side.Hand);
        foreach (var j in new[] { SibylJob.ManageGarrisons, SibylJob.ManageProduction })
            if (!sibyl.IsOn(j)) sibyl.Submit(j, true);
        return (match, sibyl);
    }

    private static void Report(ConcurrentBag<Problem> problems, string kind, Exception e, string label, int day, string? where)
    {
        var inner = e is AggregateException ag && ag.InnerExceptions.Count == 1 ? ag.InnerExceptions[0] : e;
        var frames = (inner.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at Praedra.", StringComparison.Ordinal)).Take(3).ToList();
        string key = $"{inner.GetType().Name} at {(frames.Count > 0 ? frames[0] : "?")}";
        string detail = $"{inner.GetType().Name}: {inner.Message}{(where != null ? " (" + where + ")" : "")}\n" + string.Join("\n", frames.Select(f => "    " + f));
        problems.Add(new Problem(kind, key, detail, label, day));
        lock (problems) Console.WriteLine($"  CRASH {label}: day {day}: {inner.GetType().Name}: {inner.Message}");
    }

    /// <summary>State checks: things a running game must never hold. Each is a softlock (something that waits forever) or a dangling id.</summary>
    private static void Check(GameHost host, string label, ConcurrentBag<Problem> problems)
    {
        var s = host.Game.State; int day = host.Game.Day;
        void Bad(string key, string detail) => problems.Add(new Problem("state", key, detail, label, day));
        var objects = s.Economy.Objects;
        foreach (var f in s.Fleets.Values)
        {
            if (f.At == null && f.Transit == null) Bad("a fleet that is nowhere", $"fleet {f.Id} {f.Name} ({f.Side})");
            if (f.Transit != null && f.Transit.DueDay < day - 2) Bad("a fleet overdue in transit", $"fleet {f.Id} {f.Name} ({f.Side}) due day {f.Transit.DueDay}");
            foreach (int id in f.Ships.Concat(f.Squadrons))
                if (!objects.ContainsKey(id)) { Bad("a fleet holding an object that does not exist", $"fleet {f.Id} {f.Name}: object {id}"); break; }
        }
        foreach (var d in s.Construction.Deliveries.Values)
        {
            if (d.InFlight && d.DueDay < day - 2) Bad("a delivery overdue", $"delivery {d.Id} ({d.Side}) {d.From} -> {d.To} due day {d.DueDay}");
            if (!objects.ContainsKey(d.Object)) Bad("a delivery of an object that does not exist", $"delivery {d.Id}: object {d.Object}");
        }
        foreach (var byYard in s.Construction.Projects.Values)
            foreach (var p in byYard.Values)
                foreach (int id in p.Units)
                    if (!objects.ContainsKey(id)) { Bad("a construction project holding an object that does not exist", $"{p.Side} {p.Family} at {p.System}: object {id}"); break; }
        foreach (var o in objects.Values)
            if (o.AboardShip != 0 && !objects.ContainsKey(o.AboardShip)) Bad("an object aboard a ship that does not exist", $"object {o.Id} {o.Class}: ship {o.AboardShip}");
        foreach (var m in s.Missions.Missions.Values)
        {
            bool flying = m.Phase is MissionPhase.Outbound or MissionPhase.Returning;
            if (flying && m.Departed && m.ArrivalDay < day - 2) Bad("a mission overdue on its trip", $"mission {m.Id} {m.Kind} ({m.Side}) {m.Phase}, arrival day {m.ArrivalDay}");
            if (m.Phase == MissionPhase.Working && m.Due < day - 2) Bad("a mission overdue at work", $"mission {m.Id} {m.Kind} ({m.Side}), due day {m.Due}");
        }
    }

    private static string Report(int games, int days, List<(string Label, Side? Winner, int Day, long SlowestMs, double Seconds)> finished, List<Problem> problems, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Crash and softlock hunt");
        sb.AppendLine();
        sb.AppendLine($"{games} games, up to {days} days; {finished.Count} played to the end, {games - finished.Count} stopped by a crash or a hang. Run time {elapsed.TotalMinutes:F0} min.");
        sb.AppendLine($"Wins: Hand {finished.Count(f => f.Winner == Side.Hand)}, Collective {finished.Count(f => f.Winner == Side.Collective)}, none {finished.Count(f => f.Winner == null)}." +
                      (finished.Count > 0 ? $" Slowest day {finished.Max(f => f.SlowestMs)} ms ({finished.MaxBy(f => f.SlowestMs).Label}); longest game {finished.Max(f => f.Seconds):F0} s." : ""));
        sb.AppendLine();
        if (problems.Count == 0) { sb.AppendLine("Nothing found."); return sb.ToString(); }
        foreach (var g in problems.GroupBy(p => (p.Kind, p.Key)).OrderBy(g => g.Key.Kind, StringComparer.Ordinal).ThenByDescending(g => g.Count()))
        {
            var first = g.OrderBy(p => p.Day).First();
            var games2 = g.Select(p => p.Job).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            sb.AppendLine($"## {g.Key.Kind}: {g.Key.Key}");
            sb.AppendLine($"{g.Count()} times in {games2.Count} games. First: {first.Job}, day {first.Day}.");
            sb.AppendLine("```");
            sb.AppendLine(first.Detail);
            sb.AppendLine("```");
            sb.AppendLine("Games: " + string.Join("; ", games2.Take(12)) + (games2.Count > 12 ? $"; and {games2.Count - 12} more" : ""));
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
