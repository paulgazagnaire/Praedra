using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Missions;
using Praedra.Core.Characters;
using Praedra.Core.Setup;
using Praedra.Core.Ships;
using Praedra.Core.Support;

// Command-line runner (engine/04, package P1.4). Reads the core's events and prints them; draws no random numbers itself.
// rule: m1-clock-and-orders §Events out ("the command-line runner reads this list and nothing else").
return SimCli.Run(args);

internal static class SimCli
{
    private const string Usage = """
        Usage:
          dotnet run --project sim -- battle --collective "<Class>:<n>[,...]" --hand "<Class>:<n>[,...]" [--battles 1000] [--seed 1] [--no-rolls] [--ruleset classic|new] [--carry]
          dotnet run --project sim -- mission --kind <MissionKind> --agent "<Name>" [--agent ...] [--side Collective|Hand] [--skills "esp=60,com=50,..."]
                                              [--sf "<Class>:<n>[,...]"] [--ships "<Class>:<n>[,...]"] [--troops "<Class>:<n>[,...]"]
                                              [--officer "<Name>" [--officer-esp <n>]] [--target-agent "<Name>"] [--facility <Kind>[,<Kind>...]]
                                              [--at Charter] [--owner Collective|Hand|neutral] [--support 50] [--missions 1000] [--seed 1]
          dotnet run --project sim -- campaign [--days 1000] [--seed 1] [--ruleset Classic|New]
          dotnet run --project sim -- aivai [--days 730] [--seed 1] [--start meda|campaign] [--passive hand|collective] [--trace] [--hq-unknown] [--fog] [--no-agents] [--hand-level L] [--collective-level L] [--hand-params K=V,..] [--collective-params K=V,..] [--hunt-log]
        aivai plays the simple AI (core/AI) on both sides from the new-game start (MedA, DEFAULT) or the test campaign, printing a yearly summary,
        battle, assault and capture counts, the winner (if any) and the AI's order and rejection counts. Without --fog the AIs see everything and
        the Hand is told the Hearth (unless --hq-unknown). --fog turns SimpleAiParams.UseFog on for both AIs: each reads only its side's
        KnowledgeView (m5-fog-and-hq), and the Hand knows the HQ only once its own picture shows it. With fog the AIs scout, attack from
        their last-seen pictures, the Hand hunts the HQ with a belief map and the Collective moves it when it is at risk. --passive works with
        --fog (e.g. --fog --passive collective: the fogged Hand hunts a passive Collective). --trace with --fog also prints the Hand's HQ belief.
        The AIs use characters (fleet and system posts) and missions (SimpleAiParams.UseCharactersAndMissions, DEFAULT on); --no-agents turns
        that off (the AI of before, byte for byte). --hand-level / --collective-level (easy, medium, hard; core/AI/AiLevel.cs) play that side at a
        difficulty level, fog on (AiLevels.For); --hand-params / --collective-params set SimpleAiParams values on top (as in tourney; they turn --fog on). --hunt-log prints every Espionage and Reconnaissance the Hand starts and how it ends, next to the
        true Hearth of that day (a trainer's log; the AI never reads the truth). The report adds missions started and succeeded by side and kind, posts given, and the days
        the Hand's own picture first showed (and later re-found) the Hearth.
        battle runs N seeded battles (Classic: m4-battle-classic, New: m4-battle-new; --ruleset, DEFAULT new, Paul 2026-10-18) at a synthetic system where every retreat has a destination, and prints how often each
        side won or retreated, and the mean survivors. Battle k uses seed (seed + k - 1). --no-rolls turns the subsystem rolls off. --carry (New) puts squadrons aboard their side's carriers instead of basing them.
        mission runs N seeded missions (m5-missions) of one kind against a defended system and prints how often the first iteration succeeded,
        failed or was foiled, with the detection and success chances the core rolled. Mission k uses seed (seed + k - 1) on the full galaxy, with
        incidents off. The team (--agent, --sf) stands at the target (--at), so it lands at once (no travel). Agents use their record's base skills
        (no variance) unless --skills sets them (keys dip, esp, ship, troop, fac, com, lead). Defenders belong to the other side: --ships form one
        fleet at the target (--officer is its Admiral, espionage --officer-esp), --troops are landed regiments, fighters or special forces.
        --owner holds the target (DEFAULT the other side); --support is the Collective popular support there. --target-agent places that enemy
        character at the target (Rescue, Abduction, Assassination); --facility adds facilities (Sabotage).
        campaign runs the scripted two-sided TEST campaign (core/Setup/TestCampaign.cs, built through GameHost) and prints a yearly summary per side,
        the final state hash, the random draws and how long the run took.
        Ship classes are matched ignoring case; "Corvette" means the Dodger, "Interdictor" the Subpoena.
        """;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Corvette"] = "Dodger", ["Interdictor"] = "Subpoena",
    };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage + Tourney.Usage); return args.Length == 0 ? 1 : 0; }
            if (args[0] == "battle") return Battle(args.Skip(1).ToArray());
            if (args[0] == "mission") return MissionCmd(args.Skip(1).ToArray());
            if (args[0] == "campaign") return Campaign(args.Skip(1).ToArray());
            if (args[0] == "aivai") return AiVsAi(args.Skip(1).ToArray());
            if (args[0] == "tourney") return Tourney.Run(args.Skip(1).ToArray());
            if (args[0] == "newgame") return NewGameCmd(args.Skip(1).ToArray());
            throw new ArgumentException($"Unknown command '{args[0]}'.");
        }
        catch (Exception e) when (e is ArgumentException or Praedra.Core.Data.DataLoadException or FileNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage + Tourney.Usage);
            return 2;
        }
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
            foreach (var side in new[] { Side.Hand, Side.Collective }) Console.WriteLine("  " + SideStats.Of(host, side));
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
                          $"Captures: {ev.OfType<Praedra.Core.Control.SystemCaptured>().Count()}. Blockades started: {ev.OfType<BlockadeStarted>().Count()}.");
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
        Console.WriteLine($"New game: preset {StartPresets.Key(preset)} (player {StartPresets.PlayerSide(preset)}), seed {seed}, {days} days. Hearth: {host.HqSystem}.");
        PrintCast(host);
        void Summary()
        {
            Console.WriteLine($"-- day {host.Game.Day}");
            foreach (var side in new[] { Side.Hand, Side.Collective }) Console.WriteLine("  " + SideStats.Of(host, side));
        }
        Summary();
        while (host.Game.Day < days && host.Victory.Winner == null)
        {
            host.Game.Step();
            if (host.Game.Day % 365 == 0 && host.Game.Day != days) Summary();
        }
        Summary();
        Console.WriteLine(host.Victory.Winner is { } w ? $"Winner: {w} on day {host.Game.Day} ({host.Victory.Reason})." : "No winner yet.");
        Console.WriteLine(ResearchLine(host));
        Console.WriteLine($"State hash: {host.StateHash()}");
        return 0;
    }

    // The characters placed at setup (m5-setup-and-victory P9) with where they stand and their Talent, per side.
    private static void PrintCast(GameHost host)
    {
        var ch = host.Characters;
        foreach (var side in new[] { Side.Hand, Side.Collective })
        {
            var placed = ch.InPlay.Where(c => c.Side == side).Select(c =>
            {
                string where = c.AttachedFleet != 0 ? $"fleet {c.AttachedFleet} at {ch.LocationOf(c)}" : ch.LocationOf(c) ?? "in transit";
                string force = c.TalentHolder ? $", Talent {c.EffectiveTalent}{(c.TalentDiscovered ? "" : " hidden")}" : "";
                return $"{ch.ClassOf(c).Name} ({where}{force})";
            });
            Console.WriteLine($"Cast {side}: {string.Join("; ", placed)}. Pool: {ch.PoolOf(side).Count}.");
        }
    }

    // Research levels and points per side and track (m5-research §Rule (New) 4: read-only queries).
    private static string ResearchLine(GameHost host)
    {
        var r = host.Research;
        string Of(Side s) => $"{s} " + string.Join(", ", Praedra.Core.Research.ResearchModule.Kinds.Select(k =>
            $"{k} {r.Level(s, k)}{(r.Exhausted(s, k) ? " (done)" : "")} +{r.Points(s, k)}"));
        return $"Research (level +points): {Of(Side.Collective)}; {Of(Side.Hand)}. Unlocks: {host.Game.Events.OfType<Praedra.Core.Research.ResearchLevelReached>().Count()}.";
    }

    // AI vs AI: the simple AI on both sides. Reads state and events; the AIs draw nothing from the game generator. Runtime is the runner's own clock.
    private static int AiVsAi(string[] a)
    {
        int days = 730; ulong seed = 1; string start = "meda"; Side? passive = null; bool trace = false; bool hqKnown = true; bool fog = false; bool agents = true;
        Praedra.Core.AI.AiLevel? empLevel = null, allLevel = null; bool huntLog = false; int hunted = 0; string? empParams = null, allParams = null;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--trace": trace = true; break;
                case "--hunt-log": huntLog = true; break;
                case "--hand-params": empParams = Next(); fog = true; break;
                case "--collective-params": allParams = Next(); fog = true; break;
                case "--hq-unknown": hqKnown = false; break;
                case "--fog": fog = true; break;
                case "--no-agents": agents = false; break;
                case "--hand-level": empLevel = Praedra.Core.AI.AiLevels.Parse(Next()) ?? throw new ArgumentException("--hand-level must be easy, medium or hard."); fog = true; break;
                case "--collective-level": allLevel = Praedra.Core.AI.AiLevels.Parse(Next()) ?? throw new ArgumentException("--collective-level must be easy, medium or hard."); fog = true; break;
                case "--passive": passive = Next().ToLowerInvariant() switch { "hand" => Side.Hand, "collective" => Side.Collective, "none" => null, _ => throw new ArgumentException("--passive must be hand, collective or none.") }; break;
                case "--start": start = Next().ToLowerInvariant(); if (start is not ("meda" or "campaign")) throw new ArgumentException("--start must be meda or campaign."); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var host = start == "meda" ? NewGame.Create(new NewGameOptions { Seed = (int)seed, Preset = StartPreset.MedA }) : TestCampaign.New(seed).Host;
        var aiParams = fog || !agents ? new Praedra.Core.AI.SimpleAiParams { UseFog = fog, UseCharactersAndMissions = agents } : null;
        Praedra.Core.AI.SimpleAiParams? Level(Praedra.Core.AI.AiLevel? l) => l is { } v ? Praedra.Core.AI.AiLevels.For(v, aiParams) : aiParams;
        var match = new Praedra.Core.AI.AiMatch(host, Tourney.WithOverrides(Level(empLevel) ?? new() { UseFog = fog, UseCharactersAndMissions = agents }, empParams),
                                                  Tourney.WithOverrides(Level(allLevel) ?? new() { UseFog = fog, UseCharactersAndMissions = agents }, allParams), passive: passive);
        if (!fog && hqKnown && start == "meda" && host.HqSystem != null) match.Hand.KnownEnemyHq = host.HqSystem; // without fog the Hand is told the Hearth (DEFAULT)
        Console.WriteLine($"AI vs AI: {days} days, seed {seed}, start {start}{(passive is Side ps ? $", {ps} passive (gives no orders)" : "")}. " +
                          (fog ? "Fog of war: each AI reads only its side's knowledge." : "The AIs see everything (fog off)."));
        void Summary()
        {
            Console.WriteLine($"-- day {host.Game.Day}");
            foreach (var side in new[] { Side.Hand, Side.Collective }) Console.WriteLine("  " + SideStats.Of(host, side));
        }
        Summary();
        // battles by the holder of the system right after the Step that fought them (the attacker is the other side; neutral = open space)
        int battlesAtHand = 0, battlesAtCollective = 0, battlesAtNeutral = 0, scanned = host.Game.Events.Count;
        // the Hand's own picture of the HQ, day by day: the first day it showed the HQ, and every later day it showed another (moved) HQ system
        int traced = host.Game.Events.Count;
        var hqFinds = new List<string>(); string? lastKnown = host.Fog.KnownHq(Side.Hand)?.System;
        while (host.Game.Day < days && host.Victory.Winner == null)
        {
            match.Step();
            if (host.Fog.KnownHq(Side.Hand)?.System is { } kh && kh != lastKnown) hqFinds.Add($"day {host.Game.Day} {kh}{(kh == host.HqSystem ? "" : " (stale)")}");
            lastKnown = host.Fog.KnownHq(Side.Hand)?.System;
            for (var evs = host.Game.Events; scanned < evs.Count; scanned++)
                if (evs[scanned] is Praedra.Core.Battle.BattleEnded be)
                    switch (host.Fleets.OwnerOf(be.Result.System)) { case Side.Hand: battlesAtHand++; break; case Side.Collective: battlesAtCollective++; break; default: battlesAtNeutral++; break; }
            if (trace && host.Game.Day % 50 == 0) foreach (var ai in new[] { match.Hand, match.Collective }) if (ai.Side != passive) Console.WriteLine("  d" + host.Game.Day + " " + ai.Status());
            if (trace && fog && host.Game.Day % 50 == 0 && passive != Side.Hand)
            {
                var b = match.Hand.HqBelief();
                Console.WriteLine($"  d{host.Game.Day} Hand HQ belief ({b.Count} candidates): " + string.Join(", ", b.Take(8).Select(t => $"{t.System} {t.Weight}")) +
                                  (host.HqSystem is { } hq ? $"; true HQ {hq} rank {b.ToList().FindIndex(t => t.System == hq) + 1}, search rank {match.Hand.HqSearchRank(hq)}" : ""));
            }
            if (trace) for (int k = traced; k < host.Game.Events.Count; k++)
                if (host.Game.Events[k] is Praedra.Core.Battle.BattleEnded tb) Console.WriteLine($"  d{tb.Day} battle at {tb.Result.System}: Collective {tb.Result.Collective} (lost {tb.Result.CollectiveLosses}), Hand {tb.Result.Hand} (lost {tb.Result.HandLosses})");
                else if (host.Game.Events[k] is Praedra.Core.Battle.FleetRetreated tr) Console.WriteLine($"  d{tr.Day} fleet {tr.Fleet} retreats {tr.From} -> {tr.To}");
            traced = host.Game.Events.Count;
            if (trace) foreach (var ai in new[] { match.Hand, match.Collective }) foreach (var o in ai.Submitted) Console.WriteLine($"  d{host.Game.Day - 1} {ai.Side} {o.Kind}: {o.Detail}");
            if (huntLog)
            {
                // the Hand's HQ looks: every Espionage or Reconnaissance it starts and how it ends, against the true HQ of that day (truth: a log only)
                for (var evs = host.Game.Events; hunted < evs.Count; hunted++)
                {
                    string hq = host.HqSystem ?? "(moving)";
                    if (evs[hunted] is Praedra.Core.Missions.MissionStarted ms && ms.Side == Side.Hand && ms.MissionKind is Praedra.Core.Missions.MissionKind.Espionage or Praedra.Core.Missions.MissionKind.Reconnaissance)
                        Console.WriteLine($"  hunt d{ms.Day} start {ms.MissionKind} {ms.Home} -> {ms.Target} (arrives d{ms.ArrivalDay}; {ms.Characters.Items.Count} characters, {ms.SpecialForces.Items.Count} special forces); true HQ {hq}{(ms.Target == hq ? " <== HQ" : "")}");
                    else if (evs[hunted] is Praedra.Core.Missions.MissionEnded me && me.Side == Side.Hand && me.MissionKind is Praedra.Core.Missions.MissionKind.Espionage or Praedra.Core.Missions.MissionKind.Reconnaissance)
                        Console.WriteLine($"  hunt d{me.Day} end {me.MissionKind} at {me.Target}: {me.Outcome}; true HQ {hq}{(me.Target == hq ? " <== HQ" : "")}; regiments there {host.Troops.RegimentsOnSystem(me.Target).Count}");
                    else if (evs[hunted] is Praedra.Core.Fog.HqDeparted hd) Console.WriteLine($"  hunt d{hd.Day} HQ leaves {hd.From} for {hd.Path.Items[^1]}");
                }
            }
            if (host.Game.Day % 365 == 0 && host.Game.Day != days) Summary();
        }
        Summary();
        clock.Stop();
        var ev = host.Game.Events;
        Console.WriteLine($"Battles: {ev.OfType<Praedra.Core.Battle.BattleEnded>().Count()}. Bombardments: {ev.OfType<Praedra.Core.Defenses.BombardmentResolved>().Count()}. " +
                          $"Assaults: {ev.OfType<Praedra.Core.Assault.AssaultResolved>().Count()} (won {ev.OfType<Praedra.Core.Assault.AssaultResolved>().Count(e => e.Result == "Won")}). " +
                          $"Captures: {ev.OfType<Praedra.Core.Control.SystemCaptured>().Count()} (Hand {ev.OfType<Praedra.Core.Control.SystemCaptured>().Count(e => e.To == Side.Hand)}, Collective {ev.OfType<Praedra.Core.Control.SystemCaptured>().Count(e => e.To == Side.Collective)}).");
        {
            var be = ev.OfType<Praedra.Core.Battle.BattleEnded>().ToList();
            Console.WriteLine($"Battles by place: at Hand-held systems (Collective attacking) {battlesAtHand}, at Collective-held (Hand attacking) {battlesAtCollective}, " +
                              $"at neutral {battlesAtNeutral}. Ships lost in battle: Hand {be.Sum(b => b.Result.HandLosses)}, Collective {be.Sum(b => b.Result.CollectiveLosses)}. " +
                              $"Collective systems taken by the Hand {ev.OfType<Praedra.Core.Control.SystemCaptured>().Count(e => e.From == Side.Collective && e.To == Side.Hand)}, " +
                              $"Hand systems taken by the Collective {ev.OfType<Praedra.Core.Control.SystemCaptured>().Count(e => e.From == Side.Hand && e.To == Side.Collective)}. " +
                              $"HQ moves: {ev.OfType<Praedra.Core.Fog.HqDeparted>().Count()}.");
        }
        {
            // retreat-chases: a fleet that retreated from a battle in which neither side lost anything (the morale retreat of m4-battle-classic),
            // again and again: a run is such retreats of the SAME fleet at most 10 days apart; chase days = first to last retreat of each run of two
            // or more. Counted by the side whose fleet retreated (the other side did the chasing, or kept bouncing it off a defended system).
            var lossless = new List<(int Day, int Fleet, Side Side)>(); var pending = new List<Praedra.Core.Battle.FleetRetreated>();
            foreach (var e in ev)
                if (e is Praedra.Core.Battle.FleetRetreated fr) pending.Add(fr);
                else if (e is Praedra.Core.Battle.BattleEnded b2)
                {
                    bool noLoss = b2.Result.CollectiveLosses == 0 && b2.Result.HandLosses == 0;
                    foreach (var fr2 in pending)
                    {
                        var rs = b2.Result.Collective == Praedra.Core.Battle.BattleOutcome.Retreated ? Side.Collective : Side.Hand;
                        if (noLoss) lossless.Add((b2.Day, fr2.Fleet, rs));
                    }
                    pending.Clear();
                }
            foreach (var side in new[] { Side.Hand, Side.Collective })
            {
                var mine = lossless.Where(t => t.Side == side).ToList();
                int chaseDays = 0, longest = 0;
                foreach (var g in mine.GroupBy(t => t.Fleet).OrderBy(g => g.Key))
                {
                    int runStart = -1, prev = -1000;
                    foreach (var d in g.Select(t => t.Day).Distinct().OrderBy(d => d).Append(int.MaxValue))
                    {
                        if (d - prev > 10) { if (runStart >= 0 && prev > runStart) { chaseDays += prev - runStart; longest = Math.Max(longest, prev - runStart); } runStart = d; }
                        prev = d;
                    }
                }
                Console.WriteLine($"Retreat-chases of {side} fleets: {mine.Count} retreats without a loss on either side; {chaseDays} chase days (longest run {longest}).");
            }
        }
        Console.WriteLine(host.Victory.Winner is Side w ? $"Winner: {w} on day {host.Game.Day} ({host.Victory.Reason})." : $"No winner after {host.Game.Day} days.");
        var known = host.Fog.KnownHq(Side.Hand);
        Console.WriteLine($"Hearth: {host.HqSystem ?? "none"}; Hand's picture of it: {(known == null ? "unknown" : $"{known.System} (seen day {known.Stamp})")}. " +
                          $"Explored systems: Hand {host.Data.Galaxy.Systems.Count(s => host.Fog.IsExplored(Side.Hand, s.Name))}, Collective {host.Data.Galaxy.Systems.Count(s => host.Fog.IsExplored(Side.Collective, s.Name))}.");
        Console.WriteLine($"Hand learned the HQ: {(hqFinds.Count == 0 ? "never" : string.Join("; ", hqFinds))}.");
        foreach (var side in new[] { Side.Hand, Side.Collective })
        {
            var parts = ev.OfType<MissionStarted>().Where(e => e.Side == side && e.MissionKind != MissionKind.Move).GroupBy(e => e.MissionKind).OrderBy(g => g.Key).Select(g =>
            {
                var ids = g.Select(e => e.Mission).ToHashSet();
                int succ = ev.OfType<MissionSucceeded>().Count(e => ids.Contains(e.Mission));
                int foiled = ev.OfType<MissionEnded>().Count(e => ids.Contains(e.Mission) && e.Outcome == MissionOutcome.Foiled);
                int failed = ev.OfType<MissionEnded>().Count(e => ids.Contains(e.Mission) && e.Outcome == MissionOutcome.Failed);
                return $"{g.Key} {g.Count()} (successes {succ}, foiled {foiled}, failed {failed})";
            }).ToList();
            Console.WriteLine($"Missions {side}: {(parts.Count == 0 ? "none" : string.Join("; ", parts))}.");
            Console.WriteLine($"Posts {side}: fleet posts given {ev.OfType<CharacterAttached>().Count(e => e.Side == side && e.Post != CharacterPost.None && e.Cause == CharacterCause.Order)}, " +
                              $"system Generals given {ev.OfType<SystemGeneralAssigned>().Count(e => e.Side == side)}; own characters captured {ev.OfType<CharacterCaptured>().Count(e => e.Side == side)}; " +
                              $"at the end on fleet posts {host.Characters.InPlay.Count(c => c.Side == side && c.Post != CharacterPost.None)}, " +
                              $"system Generals {host.Game.State.Characters.SystemGenerals.Values.Count(id => host.Characters.Get(id)?.Side == side)}, " +
                              $"on missions {host.Characters.InPlay.Count(c => c.Side == side && host.Missions.IsOnMission(c.Id))}.");
        }
        Console.WriteLine(ResearchLine(host));
        Console.WriteLine($"AI orders sent: {match.TotalSent}, rejected: {match.TotalRejected}.");
        foreach (var kv in match.Rejected) Console.WriteLine($"  rejected {kv.Key}: {kv.Value}");
        foreach (var kv in match.Sent) Console.WriteLine($"  sent {kv.Key}: {kv.Value}");
        foreach (var g in ev.OfType<Praedra.Core.Construction.UnitCompleted>().GroupBy(e => (e.Side, e.Class)).OrderBy(g => g.Key.Side).ThenBy(g => g.Key.Class, StringComparer.Ordinal))
            Console.WriteLine($"  built {g.Key.Side} {g.Key.Class}: {g.Count()}");
        Console.WriteLine($"State hash: {host.StateHash()}");
        Console.WriteLine($"Runtime: {host.Game.Day} days in {clock.Elapsed.TotalMilliseconds:F0} ms.");
        return 0;
    }

    // Classic battle batch (engine/04, package P4.1). Runs the core's battle on plain units; draws nothing itself.
    private static int Battle(string[] a)
    {
        int battles = 1000; ulong seed = 1; bool rolls = true, carry = false; var ruleset = new BattleParams().Ruleset; // DEFAULT New (m4-battle-new; Paul 2026-10-18)
        List<(string, int)>? collective = null, hand = null;
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--collective": collective = ParseShipList(Next()); break;
                case "--hand": hand = ParseShipList(Next()); break;
                case "--battles": battles = int.TryParse(Next(), out var n) && n > 0 ? n : throw new ArgumentException("--battles must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                case "--no-rolls": rolls = false; break;
                case "--carry": carry = true; break;
                case "--ruleset": ruleset = Next().ToLowerInvariant() switch { "classic" => BattleRuleset.Classic, "new" => BattleRuleset.New, _ => throw new ArgumentException("--ruleset must be classic or new.") }; break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        if (collective == null || hand == null) throw new ArgumentException("--collective and --hand are required.");
        var ships = GameData.Default.Ships;
        var al = collective.Select(x => (ResolveClass(ships, x.Item1), x.Item2)).ToList();
        var em = hand.Select(x => (ResolveClass(ships, x.Item1), x.Item2)).ToList();
        var p = new BattleParams { SubsystemRolls = rolls, Ruleset = ruleset };
        int aWin = 0, eWin = 0, none = 0, aRet = 0, eRet = 0; long aSurv = 0, eSurv = 0, secs = 0;
        for (int k = 0; k < battles; k++)
        {
            var (r, _) = QuickBattle.Run(p, ships, al, em, seed + (ulong)k, carry: carry);
            if (r.CollectiveOutcome == BattleOutcome.Victorious) aWin++; else if (r.HandOutcome == BattleOutcome.Victorious) eWin++; else none++;
            if (r.CollectiveRetreated) aRet++;
            if (r.HandRetreated) eRet++;
            aSurv += r.CollectiveSurvivors; eSurv += r.HandSurvivors; secs += r.Seconds;
        }
        string Pct(int n) => (100.0 * n / battles).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        string Mean(long n) => ((double)n / battles).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{battles} {ruleset} battles, seeds {seed}..{seed + (ulong)battles - 1}, subsystem rolls {(rolls ? "on" : "off")}");
        Console.WriteLine($"Collective ({string.Join(", ", al.Select(x => $"{x.Item2} x {x.Item1}"))})");
        Console.WriteLine($"Hand   ({string.Join(", ", em.Select(x => $"{x.Item2} x {x.Item1}"))})");
        Console.WriteLine($"  Collective wins {aWin} ({Pct(aWin)}), Hand wins {eWin} ({Pct(eWin)}), no winner {none} ({Pct(none)})");
        Console.WriteLine($"  Collective retreated {aRet} ({Pct(aRet)}), Hand retreated {eRet} ({Pct(eRet)})");
        Console.WriteLine($"  mean survivors: Collective {Mean(aSurv)}, Hand {Mean(eSurv)}; mean length {Mean(secs)} s");
        return 0;
    }

    // Mission batch (m5-missions). Builds each game through GameHost and reads the mission events; draws nothing itself (the character records
    // are created with their base skills, so the throwaway generator handed to Create changes nothing the mission reads).
    private static int MissionCmd(string[] a)
    {
        int missions = 1000, support = 50; ulong seed = 1; int? officerEsp = null;
        MissionKind? kind = null; Side side = Side.Collective; string at = "Charter", ownerArg = "";
        var agents = new List<string>(); var skills = new SortedDictionary<Skill, int>();
        List<(string, int)> sfs = new(), ships = new(), troops = new(); string? officer = null, targetAgent = null; var facilities = new List<FacilityKind>();
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--kind": kind = Enum.TryParse<MissionKind>(Next(), true, out var k) && Enum.IsDefined(k) ? k : throw new ArgumentException($"--kind must be one of {string.Join(", ", Enum.GetNames<MissionKind>())}."); break;
                case "--side": side = Enum.TryParse<Side>(Next(), true, out var sd) && Enum.IsDefined(sd) ? sd : throw new ArgumentException("--side must be Collective or Hand."); break;
                case "--agent": agents.Add(Next()); break;
                case "--skills":
                    foreach (var part in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var kv = part.Split('=');
                        Skill sk = kv[0].ToLowerInvariant() switch
                        {
                            "dip" => Skill.Diplomacy, "esp" => Skill.Espionage, "ship" => Skill.ShipDesign, "troop" => Skill.TroopTraining,
                            "fac" => Skill.FacilityDesign, "com" => Skill.Combat, "lead" => Skill.Leadership,
                            _ => throw new ArgumentException($"Unknown skill '{kv[0]}' (dip, esp, ship, troop, fac, com, lead)."),
                        };
                        skills[sk] = kv.Length == 2 && int.TryParse(kv[1], out var v) && v >= 0 ? v : throw new ArgumentException($"Bad skill value in '{part}'.");
                    }
                    break;
                case "--sf": sfs = ParseShipList(Next()); break;
                case "--ships": ships = ParseShipList(Next()); break;
                case "--troops": troops = ParseShipList(Next()); break;
                case "--officer": officer = Next(); break;
                case "--officer-esp": officerEsp = int.TryParse(Next(), out var oe) && oe >= 0 ? oe : throw new ArgumentException("--officer-esp must be a non-negative integer."); break;
                case "--target-agent": targetAgent = Next(); break;
                case "--facility":
                    foreach (var f in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        facilities.Add(Enum.TryParse<FacilityKind>(f, true, out var fk) && Enum.IsDefined(fk) ? fk : throw new ArgumentException($"Unknown facility '{f}'."));
                    break;
                case "--at": at = Next(); break;
                case "--owner": ownerArg = Next(); break;
                case "--support": support = int.TryParse(Next(), out var su) && su is >= 0 and <= 100 ? su : throw new ArgumentException("--support must be 0..100."); break;
                case "--missions": missions = int.TryParse(Next(), out var n) && n > 0 ? n : throw new ArgumentException("--missions must be a positive integer."); break;
                case "--seed": seed = ulong.TryParse(Next(), out var s) ? s : throw new ArgumentException("--seed must be a non-negative integer."); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        if (kind == null) throw new ArgumentException("--kind is required.");
        if (agents.Count == 0 && sfs.Count == 0) throw new ArgumentException("--agent or --sf is required.");
        if (officer != null && ships.Count == 0) throw new ArgumentException("--officer needs --ships (the officer is the defending fleet's Admiral).");
        var data = GameData.Default;
        at = Canonical(data.Galaxy, at);
        Side enemy = side == Side.Collective ? Side.Hand : Side.Collective;
        Side? owner = ownerArg.ToLowerInvariant() switch
        {
            "" => enemy, "collective" => Side.Collective, "hand" => Side.Hand, "neutral" or "none" => null,
            _ => throw new ArgumentException("--owner must be Collective, Hand or neutral."),
        };
        CharacterClass Char(string name, Side want)
        {
            var c = data.Characters.All.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? (int.TryParse(name, out var id) && data.Characters.TryGet(id, out var byId) ? byId : null)
                ?? throw new ArgumentException($"Unknown character '{name}'.");
            if (c.Side != want) throw new ArgumentException($"{c.Name} is not on the {want} side.");
            return c;
        }
        var team = agents.Select(x => Char(x, side)).ToList();
        var officerClass = officer != null ? Char(officer, enemy) : null;
        var targetClass = targetAgent != null ? Char(targetAgent, enemy) : null;
        var settings = new GameSettings { IncidentsEnabled = false };
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        int ok = 0, failed = 0, foiled = 0, ended = 0, detectRolls = 0, detections = 0, memberRolls = 0; long detChance = 0, succChance = 0, days = 0;
        string? sample = null, rejected = null;
        for (int r = 0; r < missions; r++)
        {
            var host = GameHost.New(seed + (ulong)r, settings, data);
            var ch = host.Characters;
            host.Fleets.SetOwner(at, owner);
            host.Economy.SetCollectiveSupport(at, support);
            CharacterRecord Make(CharacterClass cls)
            {
                var c = ch.Get(cls.Id) ?? ch.Create(cls, new GameRng(0));
                for (int i = 0; i < CharacterClass.SkillCount; i++) c.Skills[i] = cls.SkillBase[i];
                ch.Place(cls.Id, at);
                return c;
            }
            foreach (var c in team) { var rec = Make(c); foreach (var (sk, v) in skills) rec.Skills[(int)sk] = v; }
            var sfIds = new List<int>();
            foreach (var (cls, count) in sfs) for (int i = 0; i < count; i++) sfIds.Add(host.Economy.AddObject(side, cls, at));
            if (ships.Count > 0)
            {
                var names = ships.SelectMany(x => Enumerable.Repeat(ResolveClass(data.Ships, x.Item1), x.Item2)).ToList();
                int fleet = host.Fleets.AddFleet(enemy, "Defenders", names, at);
                if (officerClass != null)
                {
                    var o = Make(officerClass);
                    if (officerEsp is int e) o.Skills[(int)Skill.Espionage] = e;
                    ch.SetPost(officerClass.Id, fleet, CharacterPost.Admiral);
                }
            }
            foreach (var (cls, count) in troops) for (int i = 0; i < count; i++) host.Economy.AddObject(enemy, cls, at);
            foreach (var f in facilities) host.Economy.AddFacility(at, f);
            int? tc = targetClass != null ? Make(targetClass).Id : null;
            long seq = host.Game.Submit(q => new StartMissionOrder(q, side, kind.Value, team.Select(c => c.Id).ToList(), sfIds, new(), tc == null ? at : null, tc));
            host.Game.Step();
            if (host.Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq) is { } rej) { rejected = rej.Reason; break; }
            int mission = host.Game.Events.OfType<MissionStarted>().Last().Mission;
            bool done = false;
            for (int step = 0; step < 1000 && !done; step++)
            {
                int from = host.Game.Events.Count;
                host.Game.Step();
                var evs = host.Game.Events.Skip(from).ToList(); // this Step's events
                foreach (var d in evs.OfType<MissionDetectionRoll>().Where(d => d.Mission == mission)) { detectRolls++; detChance += d.Chance; if (d.Detected) detections++; }
                foreach (var m in evs.OfType<MissionMemberRoll>().Where(m => m.Mission == mission)) { memberRolls++; succChance += m.Chance; }
                if (evs.OfType<MissionFoiled>().Any(e => e.Mission == mission)) { foiled++; done = true; }
                else if (evs.OfType<MissionSucceeded>().Any(e => e.Mission == mission)) { ok++; done = true; }
                else if (evs.OfType<MissionFailed>().Any(e => e.Mission == mission)) { failed++; done = true; }
                else if (evs.OfType<MissionIterationDone>().FirstOrDefault(e => e.Mission == mission) is { } it) { if (it.Success) ok++; else failed++; done = true; } // a repeating kind goes on
                else if (evs.OfType<MissionEnded>().FirstOrDefault(e => e.Mission == mission) is { } end) { ended++; done = true; }
                if (done && r == 0)
                {
                    var rolls = evs.OfType<MissionDetectionRoll>().Where(d => d.Mission == mission).Select(d => $"defender {d.Defender} x {d.X} chance {d.Chance}%").ToList();
                    sample = rolls.Count == 0 ? "no detection rolls" : string.Join("; ", rolls);
                }
            }
            days += host.Game.Day;
        }
        if (rejected != null) { Console.WriteLine($"StartMission was refused: {rejected}"); return 3; }
        string Pct(int x) => (100.0 * x / missions).ToString("0.0", inv) + "%";
        string Avg(long sum, int count) => count == 0 ? "-" : ((double)sum / count).ToString("0.0", inv) + "%";
        Console.WriteLine($"{missions} {kind} missions by the {side} at {at} (owner {(owner?.ToString() ?? "neutral")}, Collective support {support}), seeds {seed}..{seed + (ulong)missions - 1}");
        Console.WriteLine($"  team: {string.Join(", ", team.Select(c => c.Name).Concat(sfs.Select(x => $"{x.Item2} x {x.Item1}")))}");
        Console.WriteLine($"  defenders: {(ships.Count + troops.Count == 0 ? "none" : string.Join(", ", ships.Concat(troops).Select(x => $"{x.Item2} x {x.Item1}")))}{(officerClass != null ? $"; Admiral {officerClass.Name}" : "")}");
        Console.WriteLine($"  first iteration: succeeded {ok} ({Pct(ok)}), failed {failed} ({Pct(failed)}), foiled {foiled} ({Pct(foiled)}){(ended > 0 ? $", ended without a roll {ended} ({Pct(ended)})" : "")}");
        Console.WriteLine($"  detection rolls {detectRolls}, detections {detections}, mean chance per detection roll {Avg(detChance, detectRolls)}; success rolls {memberRolls}, mean success chance {Avg(succChance, memberRolls)}");
        Console.WriteLine($"  mission 1: {sample}; mean end day {((double)days / missions).ToString("0.0", inv)}");
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

    private static List<(string, int)> ParseShipList(string v)
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
