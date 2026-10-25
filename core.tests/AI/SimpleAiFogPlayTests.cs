using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// The fogged AI as an opponent (SimpleAiParams.UseFog = true): it scouts, attacks from its last-seen picture, the Empire hunts the Rebel HQ
// with a belief map and the Alliance moves its HQ when it is at risk. Rules checked: m5-fog-and-hq §N8 (the AI reads only its side's
// KnowledgeView: FN tests), §N5 (KnownHq), §F6 (MoveHq), §F7 (capturing the HQ system destroys it, DEFAULT AnyCause). The AI behaviour
// itself follows ai/07 §7 and ai/12 D1 (search and hide, Decided YES); its thresholds are SimpleAiParams DEFAULTs.
public class SimpleAiFogPlayTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiFogPlayTests(ITestOutputHelper o) { _out = o; }

    private static readonly SimpleAiParams Fog = new() { UseFog = true };

    private static (AiMatch M, GameHost H) Match(int seed, Side? passive = null, Action<GameHost>? setup = null)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Praedra.Core.Tests.Setup.DealSettings.FileOldVictory });
        setup?.Invoke(h);
        return (new AiMatch(h, Fog, Fog, passive), h);
    }

    private static int Explored(GameHost h, Side s) => h.Data.Galaxy.Systems.Count(x => h.Fog.IsExplored(s, x.Name));

    // m5-fog-and-hq §N8, FN3 in spirit: the fog-only AI code never names a truth getter of the other side. The behavioural FN tests below
    // and in SimpleAiFogTests are the real proof; this catches a careless edit early.
    [Fact]
    public void FN3_FogAiCodeNamesNoTruthGetter()
    {
        string src = File.ReadAllText(Path.Combine(TestData.RepoRoot, "core", "AI", "SimpleAiFog.cs"));
        foreach (var bad in new[] { "HqSystem", "Game.Events", "SystemOwners", "TruthView", "Truth(", "RegimentsOnSystem", "Fleets.FleetsAt(", ".Hq.", "GameRng", "Rng." })
            Assert.False(src.Contains(bad, StringComparison.Ordinal), $"SimpleAiFog.cs mentions {bad}");
    }

    // FN1 for the Alliance AI: two games that differ only in Imperial forces the Alliance has never seen (an extra fleet on an Imperial
    // system it has not explored, extra regiments on Coruscant whose ground it has never seen) give the Alliance the same knowledge, and
    // while the knowledge is the same the Alliance AI gives the same orders.
    [Fact]
    public void FN1_AllianceAiOrdersDoNotDependOnUnseenImperialForces()
    {
        string? hidden = null;
        // both games take the same ids from the shared object counter; game A gives the extra units back at once (ids stay aligned)
        void Extra(GameHost h, bool keep)
        {
            // v6 re-pin 2026-10-24: on the spiral map the first such system by name was seen by the Alliance on day 11 (the test then proves nothing),
            // so the hidden fleet goes to the unexplored Imperial system farthest from every Alliance system (ties by name).
            var g = h.Data.Galaxy;
            var rebels = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance).Select(kv => g.Get(kv.Key)).ToList();
            int Nearest(string s) { var x = g.Get(s); return rebels.Min(r => (r.MapX - x.MapX) * (r.MapX - x.MapX) + (r.MapY - x.MapY) * (r.MapY - x.MapY)); }
            hidden = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Empire && kv.Key != "Coruscant" && !h.Fog.IsExplored(Side.Alliance, kv.Key))
                .Select(kv => kv.Key).OrderByDescending(Nearest).ThenBy(s => s, StringComparer.Ordinal).First();
            int fleet = h.Fleets.AddFleet(Side.Empire, "Hidden", new[] { "Imperial Star Destroyer" }, hidden);
            var ids = h.Game.State.Fleets[fleet].Ships.ToList();
            for (int i = 0; i < 3; i++) ids.Add(h.Economy.AddObject(Side.Empire, "Imperial Army Regiment", "Coruscant"));
            if (!keep) foreach (var id in ids) h.Economy.RemoveObject(id);
        }
        var (a, ha) = Match(4, passive: Side.Empire, setup: h => Extra(h, keep: false));
        var (b, hb) = Match(4, passive: Side.Empire, setup: h => Extra(h, keep: true));
        Assert.NotNull(hidden);
        Assert.NotEqual(ha.StateHash(), hb.StateHash());
        int same = 0;
        for (int day = 0; day < 300; day++)
        {
            a.Step(); b.Step();
            if (ha.Fog.KnowledgeHash(Side.Alliance) != hb.Fog.KnowledgeHash(Side.Alliance)) break; // the Alliance found out: the games may part now
            Assert.Equal(a.Alliance.Submitted.Select(o => o.Kind + " " + o.Detail), b.Alliance.Submitted.Select(o => o.Kind + " " + o.Detail));
            same++;
            if (ha.Victory.Winner != null) break;
        }
        _out.WriteLine($"same knowledge for {same} days; Alliance orders {a.TotalSent}");
        Assert.True(same >= 100, $"the knowledge parted on day {same}");
        Assert.True(a.TotalSent > 20);
    }

    // ai/07 §7 (Empire: belief-map search), ai/12 D1. The fogged Empire against an Alliance that gives no orders explores the rim with its
    // scouts, builds its belief map from what it sees, and takes the Rebel HQ (§F7: capturing the HQ system destroys it). Measured on the
    // MedA start: day 376 (seeds 1 to 5 alike: the passive game draws little); limit 500 for margin.
    [Fact]
    public void FoggedEmpire_AgainstPassiveAlliance_FindsAndTakesTheHq()
    {
        // // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): measured day 1282 on v5 (the HQ, Cyclor, starts beside Naboo, where the Alliance would not hide it, so the belief map
        // looks there late), limit 500 -> 1400
        var (m, h) = Match(1, passive: Side.Alliance);
        int explored0 = Explored(h, Side.Empire), explored200 = -1;
        while (h.Game.Day < 1400 && h.Victory.Winner == null)
        {
            m.Step();
            if (h.Game.Day == 200) explored200 = Explored(h, Side.Empire);
        }
        _out.WriteLine($"winner {h.Victory.Winner} day {h.Game.Day} ({h.Victory.Reason}); explored {explored0} -> {explored200} at day 200; " +
                       $"orders {m.TotalSent} rejected {m.TotalRejected}; scouts built/split {m.Sent.Where(kv => kv.Key is "Empire Split").Sum(kv => kv.Value)}");
        Assert.Equal(Side.Empire, h.Victory.Winner);
        Assert.Contains(h.Game.Events, e => e is HqDestroyed);
        Assert.True(h.Game.Day <= 1400, $"won only on day {h.Game.Day}");
        // scouting: by day 200 the Empire has looked at many more systems than it knew at the start (measured 63 -> 123)
        Assert.True(explored200 - explored0 >= 45, $"explored {explored0} -> {explored200}");
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }

    // The fogged Alliance against an Empire that gives no orders takes Coruscant (known to all: a Core system), estimating its garrison
    // from what it has seen. Measured: day 702 (seeds 1 to 5: 702 to 705); limit 850.
    [Fact]
    public void FoggedAlliance_AgainstPassiveEmpire_KeepsCapturing()
    {
        var (m, h) = Match(1, passive: Side.Empire);
        m.RunTo(850);
        int captures = h.Game.Events.OfType<SystemCaptured>().Count(e => e.To == Side.Alliance);
        _out.WriteLine($"winner {h.Victory.Winner} day {h.Game.Day} ({h.Victory.Reason}); Alliance captures {captures}; orders {m.TotalSent} rejected {m.TotalRejected}");
        // // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): on v5 the fogged Alliance does not take Coruscant (measured: 8 captures by day 850, 20 by day 3000, no winner). The test
        // now pins that it keeps capturing; the old target (wins by day 850) is the trainer's.
        Assert.True(captures >= 6, $"only {captures} Alliance captures by day 850");
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }

    // ai/07 §7 (Alliance: relocate on risk, not on a timer; no dead ends). An Imperial fleet standing on the HQ system is seen (the Alliance
    // holds it); the Alliance AI moves its HQ (m5-fog-and-hq §F6 MoveHq) to a held system with at least HqMinLanes lanes, and not again
    // within HqMoveCooldownDays.
    [Fact]
    public void FoggedAlliance_MovesItsHq_WhenAnImperialFleetStandsOnIt()
    {
        string hq = "";
        var (m, h) = Match(1, passive: Side.Empire, setup: x =>
        {
            hq = x.HqSystem!;
            x.Fleets.AddFleet(Side.Empire, "Probe", new[] { "Imperial Star Destroyer", "Imperial Star Destroyer", "Imperial Star Destroyer" }, hq);
        });
        m.RunTo(10);
        var dep = h.Game.Events.OfType<HqDeparted>().ToList();
        Assert.Single(dep);
        Assert.Equal(hq, dep[0].From);
        string dest = dep[0].Path.Items[^1];
        Assert.Equal(Side.Alliance, h.Fleets.OwnerOf(dest));
        Assert.True(h.Data.Galaxy.NearOf(h.Data.Galaxy.Get(dest).Index).Count >= Fog.HqMinLanes);
        Assert.NotEqual(Side.Empire, h.Victory.Winner);
        m.RunTo(Fog.HqMoveCooldownDays - 5);
        Assert.Single(h.Game.Events.OfType<HqDeparted>()); // no second move inside the cool-down
        _out.WriteLine($"HQ {hq} -> {dest} on day {dep[0].Day}; now {h.HqSystem}");
    }

    // Estimates from the last-seen picture: an enemy garrison never seen is a guess (larger on the system that wins the game); a seen one is
    // the seen count, growing by FogStaleMarginPercent as the look ages to StaleDays. rule: m5-fog-and-hq §N2 (ages exposed; using them is M6).
    [Fact]
    public void DefenderEstimate_UsesTheLastSightingWithAStalenessMargin()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA, Settings = Praedra.Core.Tests.Setup.DealSettings.FileOldVictory });
        var ai = new SimpleAi(h, Side.Alliance, Fog);
        // an Imperial system the Alliance's picture shows Imperial, ground never seen (a Core system: owner reported, nothing else)
        string other = h.Data.Galaxy.Systems.Select(x => x.Name).First(n => n != "Coruscant" &&
            h.Fog.Picture(Side.Alliance, n) is { Ground: null } p && p.Owner?.Value.Owner == Side.Empire);
        Assert.Null(h.Fog.Picture(Side.Alliance, "Coruscant")!.Ground);
        Assert.Equal(Fog.FogUnknownWinDefenders, ai.DefendersEstimate("Coruscant"));
        Assert.Equal(Fog.FogUnknownDefenders, ai.DefendersEstimate(other));
        bool done = false;
        h.Game.RegisterHook("6.8", ctx => { if (!done) { done = true; h.Fog.EspionageSuccess(ctx, Side.Alliance, "Coruscant"); } });
        h.Game.Step();
        int seen = h.Fog.Picture(Side.Alliance, "Coruscant")!.Ground!.Value.Where(g => g.Side == Side.Empire).Sum(g => g.Total);
        Assert.True(seen > 0);
        Assert.Equal(seen, ai.DefendersEstimate("Coruscant"));
        while (h.Game.Day < 1 + Fog.StaleDays + 10) h.Game.Step(); // nobody orders anything; the picture ages
        Assert.Equal((seen * (100 + Fog.FogStaleMarginPercent) + 99) / 100, ai.DefendersEstimate("Coruscant"));
    }

    // QA-5 (SimpleAiParams.FogGroundForgetDays): a garrison seen longer ago than FogGroundForgetDays is forgotten and the never-seen guess comes
    // back, so an endgame strike is not refused forever on a years-old count. Here the switch is 30 days so the test stays short.
    [Fact]
    public void DefenderEstimate_ForgetsAGarrisonSeenTooLongAgo()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA, Settings = Praedra.Core.Tests.Setup.DealSettings.FileOldVictory });
        var p = Fog with { FogGroundForgetDays = 30 };
        var ai = new SimpleAi(h, Side.Alliance, p);
        bool done = false;
        h.Game.RegisterHook("6.8", ctx => { if (!done) { done = true; h.Fog.EspionageSuccess(ctx, Side.Alliance, "Coruscant"); } });
        h.Game.Step();
        int seen = h.Fog.Picture(Side.Alliance, "Coruscant")!.Ground!.Value.Where(g => g.Side == Side.Empire).Sum(g => g.Total);
        Assert.NotEqual(p.FogUnknownWinDefenders, seen);
        while (h.Game.Day < 1 + 30) h.Game.Step();
        Assert.Equal((seen * StalePercent(p, 30) + 99) / 100, ai.DefendersEstimate("Coruscant")); // 30 days old: still believed
        h.Game.Step();
        Assert.Equal(p.FogUnknownWinDefenders, ai.DefendersEstimate("Coruscant"));               // 31 days old: forgotten
    }

    private static int StalePercent(SimpleAiParams p, int age) => 100 + p.FogStaleMarginPercent * Math.Clamp(age, 0, p.StaleDays) / p.StaleDays;

    // With fog both AIs fight each other: by day 600 each side has attacked the other (battles fought, and each side has taken at least one
    // system from the other). Measured on seed 1: see the report; the asserts are the floor.
    [Fact]
    public void FogVsFog_BothSidesAttack()
    {
        var (m, h) = Match(1);
        m.RunTo(600);
        var ev = h.Game.Events;
        int battles = ev.OfType<Praedra.Core.Battle.BattleEnded>().Count();
        int eTook = ev.OfType<SystemCaptured>().Count(e => e.From == Side.Alliance && e.To == Side.Empire);
        int aTook = ev.OfType<SystemCaptured>().Count(e => e.From == Side.Empire && e.To == Side.Alliance);
        _out.WriteLine($"day {h.Game.Day} winner {h.Victory.Winner}; battles {battles}; Empire took {eTook} Alliance systems, Alliance took {aTook} Imperial; HQ moves {ev.OfType<HqDeparted>().Count()}");
        Assert.True(battles >= 5, $"only {battles} battles");
        Assert.True(eTook >= 1, "the Empire never took an Alliance system");
        Assert.True(aTook >= 1, "the Alliance never took an Imperial system");
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }
}
