using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.DeathStar;
using Rebellion.Core.Economy;
using Rebellion.Core.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// The computer opponent's Death Star (m5-death-star §6, SimpleAiParams.UseDeathStar). The Alliance AI is a passive scripted side in the scenario
// tests so that only the Empire AI acts; the campaign test lets both AIs play with fog.
public class SimpleAiDeathStarTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiDeathStarTests(ITestOutputHelper o) { _out = o; }

    private const string Ds = EconomyModule.DeathStarClass, Isd = "Imperial Star Destroyer";
    private const int Margin = 140; // 10% of the MedA Empire capacity (1400)

    private static (AiMatch M, GameHost H) Match(int seed, SimpleAiParams p, bool knowHq = false, Side? passive = Side.Alliance)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        var m = new AiMatch(h, p, p, passive);
        if (knowHq) m.Empire.KnownEnemyHq = h.HqSystem;
        return (m, h);
    }

    // An army that never launches by itself, so only the Death Star logic can move the Empire's fleets in the scenario tests.
    private static SimpleAiParams Quiet(bool useDs = true, double? escort = null) => new()
    {
        UseDeathStar = useDs, DeathStarEscortStrength = escort, UseCharactersAndMissions = false, LaunchStrength = int.MaxValue, WinStrikeStrength = int.MaxValue,
    };

    private static List<EconObject> DeathStars(GameHost h) =>
        h.Economy.Objects.Where(o => o.Class == Ds && h.Economy.OwnerOf(o) == Side.Empire).ToList();

    private static void Days(AiMatch m, int n) { for (int i = 0; i < n; i++) m.Step(); }

    // m5-death-star §6.1: queued when the refined stock covers the cost and the headroom after adding its upkeep is at least the margin.
    [Fact]
    public void Builds_OneWhenItCanAfford()
    {
        var (m, h) = Match(1, Quiet());
        h.Economy.SetPools(Side.Empire, 0, 2000);
        Assert.True(h.Economy.Free(Side.Empire) - h.Economy.Costs.Get(Ds).Maintenance >= Margin, "the start has no headroom for the test");
        Days(m, 12);
        Assert.Single(DeathStars(h));
        Assert.Contains(m.Sent.Keys, k => k == "Empire Build");
        Assert.Empty(h.Economy.Objects.Where(o => o.Class == Ds && h.Economy.OwnerOf(o) == Side.Alliance));
    }

    // m5-death-star §6.1 (yard choice DEFAULT, lead 2026-10-13): the system with the most completed shipyards.
    [Fact]
    public void Builds_AtTheSystemWithTheMostShipyards()
    {
        var (m, h) = Match(1, Quiet());
        h.Economy.SetPools(Side.Empire, 0, 2000);
        var yards = h.Economy.Objects.Where(o => o.Completed && o.Class == "Orbital Shipyard" && h.Economy.OwnerOf(o) == Side.Empire && o.System != null)
            .GroupBy(o => o.System!).Select(g => (Sys: g.Key, N: g.Count())).ToList();
        int max = yards.Max(y => y.N);
        var best = yards.Where(y => y.N == max).Select(y => y.Sys).ToHashSet();
        Days(m, 12);
        var ev = h.Game.Events.OfType<ProjectStarted>().Where(e => e.Class == Ds).ToList();
        Assert.Single(ev);
        Assert.Contains(ev[0].System, best);
    }

    // m5-death-star §6.1: the headroom after adding 600 must stay at least 10% of the capacity.
    [Fact]
    public void DoesNotBuild_WhenHeadroomIsShort()
    {
        var (m, h) = Match(1, Quiet());
        h.Economy.SetPools(Side.Empire, 0, 2000);
        int cap = h.Economy.Capacity(Side.Empire), upkeep = h.Economy.Costs.Get(Ds).Maintenance;
        while (h.Economy.Free(Side.Empire) - upkeep >= cap / 10 && h.Economy.Free(Side.Empire) > 0)
            h.Fleets.AddFleet(Side.Empire, "Filler", new[] { Isd }, ControlModule.CapitalSystem);
        Assert.True(h.Economy.Free(Side.Empire) >= 0);
        Days(m, 12);
        Assert.Empty(DeathStars(h));
    }

    // m5-death-star §6.1: not enough refined stock, no Death Star.
    [Fact]
    public void DoesNotBuild_WhenRefinedIsShort()
    {
        var (m, h) = Match(1, Quiet());
        h.Economy.SetPools(Side.Empire, 0, 100);
        Days(m, 4);
        Assert.Empty(DeathStars(h));
    }

    // m5-death-star §6.1: one at a time (none alive or queued).
    [Fact]
    public void NeverTwo_NotWhileOneIsQueuedOrAlive()
    {
        var (m, h) = Match(2, Quiet());
        h.Economy.SetPools(Side.Empire, 0, 2000);
        for (int i = 0; i < 40; i++) { Days(m, 1); h.Economy.SetPools(Side.Empire, 0, 2000); Assert.True(DeathStars(h).Count <= 1); }
        Assert.Single(DeathStars(h));
        // and one alive in a fleet blocks it as well
        var (m2, h2) = Match(2, Quiet());
        h2.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds }, ControlModule.CapitalSystem);
        h2.Economy.SetPools(Side.Empire, 0, 2000);
        for (int i = 0; i < 20; i++) { Days(m2, 1); h2.Economy.SetPools(Side.Empire, 0, 2000); }
        Assert.Single(DeathStars(h2));
    }

    // m5-death-star §6.2: an upkeep crisis scraps other things first; the Death Star stays.
    [Fact]
    public void NotScrappedInAnUpkeepCrisis_WhileOtherShipsExist()
    {
        var (m, h) = Match(3, Quiet());
        var eco = h.Economy;
        h.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds }, ControlModule.CapitalSystem);
        Days(m, 3);
        h.Fleets.AddFleet(Side.Empire, "Spare", new[] { Isd, Isd }, ControlModule.CapitalSystem);
        var mines = eco.Objects.Where(o => o.Completed && o.Class == "Mine" && eco.OwnerOf(o) == Side.Empire).OrderBy(o => o.Id).ToList();
        int n = Math.Min(mines.Count, Math.Max(0, eco.Free(Side.Empire)) / 50 + 1);
        for (int i = 0; i < n; i++) { var id = mines[i].Id; h.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Empire, id)); }
        h.Game.Step();
        Assert.True(eco.Free(Side.Empire) < 0, "the test did not create an overdraft");
        int scraps = 0;
        for (int d = 0; d < 8 && eco.Free(Side.Empire) < 0; d++) { m.Step(); scraps += m.Empire.Submitted.Count(o => o.Kind == "Scrap"); }
        Assert.True(scraps >= 1, "the AI scrapped nothing");
        Assert.True(eco.Free(Side.Empire) >= 0);
        Assert.Single(DeathStars(h));
        Assert.DoesNotContain(h.Game.Events.OfType<ObjectScrapped>(), e => e.Class == Ds);
    }

    // m5-death-star §6.3: it waits at home until the escort strength is met.
    [Fact]
    public void WaitsWithoutEscort()
    {
        var (m, h) = Match(1, Quiet(escort: 1e9), knowHq: true);
        int id = h.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd }, ControlModule.CapitalSystem);
        Days(m, 90);
        var f = h.Game.State.Fleets[id];
        Assert.Equal(ControlModule.CapitalSystem, f.At);
        Assert.Null(f.Transit);
        Assert.Empty(h.Game.Events.OfType<SystemDestroyed>());
        // E is the blockade threshold of the data, a positive number
        Assert.True(h.Fleets.Blockade.EconomicStrength(h.Fleets.ShipClasses, h.Fleets.Strength) > 0);
    }

    // m5-death-star §6.3, §6.4: with an escort and a known HQ it goes there and destroys the HQ system; the Empire wins.
    [Fact]
    public void WithEscortAndKnownHq_GoesAndDestroysTheHq()
    {
        var (m, h) = Match(1, Quiet(), knowHq: true);
        string hq = h.HqSystem!;
        int id = h.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd, Isd, Isd, Isd, Isd }, ControlModule.CapitalSystem);
        while (h.Game.Day < 2500 && h.Victory.Winner == null) m.Step();
        var hit = h.Game.Events.OfType<SystemDestroyed>().ToList();
        _out.WriteLine($"day {h.Game.Day} winner {h.Victory.Winner} reason {h.Victory.Reason}; destroyed {string.Join(",", hit.Select(e => e.System + "@" + e.Day))}");
        Assert.Contains(hit, e => e.System == hq && e.AttackerSide == Side.Empire);
        Assert.Equal(Side.Empire, h.Victory.Winner);
    }

    // m5-death-star §6.4: no HQ known (and no peeking at the true one): the Death Star stays home, escorted or not.
    [Fact]
    public void WithNoKnownHq_StaysHome()
    {
        var (m, h) = Match(1, Quiet(), knowHq: false);
        int id = h.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd, Isd, Isd, Isd, Isd }, ControlModule.CapitalSystem);
        Days(m, 120);
        var f = h.Game.State.Fleets[id];
        Assert.Equal(ControlModule.CapitalSystem, f.At);
        Assert.Null(f.Transit);
        Assert.Empty(h.Game.Events.OfType<SystemDestroyed>());
    }

    // The fog switch: with UseFog the AI knows the HQ only through its own picture, so at the start it has none and the Death Star stays home.
    [Fact]
    public void WithFog_TheAiDoesNotUseTheTrueHq()
    {
        var p = Quiet() with { UseFog = true };
        var (m, h) = Match(1, p);
        int id = h.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd, Isd, Isd, Isd, Isd }, ControlModule.CapitalSystem);
        m.Empire.KnownEnemyHq = h.HqSystem; // overwritten from the side's picture on the first planning day
        m.Step();
        Assert.NotEqual(h.HqSystem, m.Empire.KnownEnemyHq);
        Days(m, 30);
        Assert.Empty(h.Game.Events.OfType<SystemDestroyed>());
    }

    // m5-death-star §6.4: on a system that is the known HQ it orders Destroy System when no enemy fleet is there; if enemy fleets stand there it
    // waits; if the HQ is known elsewhere it sets out for the new one.
    [Fact]
    public void AtTheHq_OrdersDestroySystem_OnlyWithoutEnemyFleets_AndRetargets()
    {
        var p = Quiet();
        // (a) a quiet neutral system, the "HQ" believed there: the fleet orders the strike
        (string s, string far) Pick(GameHost h)
        {
            var g = h.Fleets.Galaxy.Systems.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var quiet = g.Where(x => h.Fleets.OwnerOf(x) == null && !h.Fleets.FleetsAt(x).Any()).ToList();
            return (quiet.First(), h.HqSystem!);
        }
        void Align(AiMatch m, GameHost h) { while ((h.Game.Day + (int)Side.Empire) % p.MilitaryEvery != 0) h.Game.Step(); }

        var (m1, h1) = Match(1, p);
        var (n1, hq1) = Pick(h1); Align(m1, h1);
        int f1 = h1.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd }, n1);
        m1.Empire.KnownEnemyHq = n1; m1.Empire.PlanDay();
        Assert.Contains(m1.Empire.Submitted, o => o.Kind == "DestroySystem" && o.Detail.Contains($"fleet {f1}"));

        // (b) the same with an Alliance fleet standing there: no strike
        var (m2, h2) = Match(1, p);
        var (n2, _) = Pick(h2); Align(m2, h2);
        int f2 = h2.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd }, n2);
        h2.Fleets.AddFleet(Side.Alliance, "Guard", new[] { Isd }, n2);
        m2.Empire.KnownEnemyHq = n2; m2.Empire.PlanDay();
        Assert.DoesNotContain(m2.Empire.Submitted, o => o.Kind == "DestroySystem");

        // (c) the HQ moved (known now elsewhere): it sets out for it
        var (m3, h3) = Match(1, p);
        var (n3, hq3) = Pick(h3); Align(m3, h3);
        int f3 = h3.Fleets.AddFleet(Side.Empire, "DS", new[] { Ds, Isd }, n3);
        m3.Empire.KnownEnemyHq = hq3; m3.Empire.PlanDay();
        Assert.DoesNotContain(m3.Empire.Submitted, o => o.Kind == "DestroySystem");
        Assert.Contains(m3.Empire.Submitted, o => o.Kind == "Move" && o.Detail.Contains($"fleet {f3}") && o.Detail.EndsWith("-> " + hq3));
    }

    // m5-death-star §6 switch: off = never builds one.
    [Fact]
    public void SwitchOff_NeverBuilds()
    {
        Assert.True(new SimpleAiParams().UseDeathStar);
        var (m, h) = Match(1, Quiet(useDs: false));
        for (int i = 0; i < 40; i++) { Days(m, 1); h.Economy.SetPools(Side.Empire, 0, 2000); }
        Assert.Empty(DeathStars(h));
        Assert.DoesNotContain(m.Sent.Keys, k => k.Contains("DestroySystem", StringComparison.Ordinal));
    }

    // ---- a campaign: both AIs with fog, up to 1500 days. Outcomes depend on balance, so only "no crash" and determinism are asserted.
    private sealed record Run(string Hash, int Day, Side? Winner, int? BuiltDay, int? StruckDay, string? Struck, string Reason);

    // funded: the plain AI never meets the §6.1 build condition (it spends its stock and its upkeep room as it goes), so this variant keeps the
    // Empire's upkeep margin at 760 (SimpleAiParams.MaintenanceMargin) and tops its refined stock up to the Death Star's cost each day.
    private static Run Campaign(int seed, int days, bool funded = false)
    {
        var p = new SimpleAiParams { UseFog = true };
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        var m = new AiMatch(h, funded ? p with { MaintenanceMargin = 760 } : p, p);
        while (h.Game.Day < days && h.Victory.Winner == null)
        {
            if (funded && h.Economy.Refined(Side.Empire) < 600) h.Economy.SetPools(Side.Empire, h.Economy.Raw(Side.Empire), 600);
            m.Step();
        }
        var ev = h.Game.Events;
        int? built = ev.OfType<DeathStarDeployed>().Where(e => e.Side == Side.Empire).Select(e => (int?)e.Day).FirstOrDefault();
        var strike = ev.OfType<SystemDestroyed>().FirstOrDefault();
        return new Run(h.StateHash(), h.Game.Day, h.Victory.Winner, built, strike?.Day, strike?.System, h.Victory.Reason ?? "");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Campaign_BothAisWithFog_RunsAndReports(int seed)
    {
        var r = Campaign(seed, 1500);
        _out.WriteLine($"seed {seed}: ended day {r.Day}, winner {r.Winner} ({r.Reason}); Death Star deployed day {r.BuiltDay?.ToString() ?? "never"}; first destroyed system {r.Struck ?? "none"} day {r.StruckDay?.ToString() ?? "-"}");
        Assert.NotEmpty(r.Hash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Campaign_DeathStarFunded_RunsAndReports(int seed)
    {
        var r = Campaign(seed, 1500, funded: true);
        _out.WriteLine($"funded seed {seed}: ended day {r.Day}, winner {r.Winner} ({r.Reason}); Death Star deployed day {r.BuiltDay?.ToString() ?? "never"}; first destroyed system {r.Struck ?? "none"} day {r.StruckDay?.ToString() ?? "-"}");
        Assert.NotEmpty(r.Hash);
    }

    [Fact]
    public void Campaign_SameSeedTwice_SameHash()
    {
        Assert.Equal(Campaign(1, 600).Hash, Campaign(1, 600).Hash);
        Assert.Equal(Campaign(1, 600, funded: true).Hash, Campaign(1, 600, funded: true).Hash);
    }
}
