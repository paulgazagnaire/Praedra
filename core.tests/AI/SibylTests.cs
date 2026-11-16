using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// m6-advisor-sibyl S1 to S8: Sibyl, the Hand player's advisor, on a Medium start (seed 3). Nothing else plays: the Collective gives no orders.
public class SibylTests
{
    private static GameHost NewHost() =>
        NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });

    private static IEnumerable<EconObject> HandFacilities(GameHost h, FacilityKind k) =>
        h.Economy.Objects.Where(o => o.Completed && o.Class == FacilityKinds.ClassName(k) && o.System != null
            && h.Game.State.SystemOwners.TryGetValue(o.System, out var s) && s == Side.Hand).OrderBy(o => o.Id);

    /// <summary>A held, populated Hand world with no yard of any kind, ordinal first.</summary>
    private static string QuietWorld(GameHost h) =>
        h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand).Select(kv => kv.Key)
            .First(s => h.Economy.SlotsOf(s).Populated && !h.Economy.Objects.Any(o => o.System == s && o.Category == ObjectCategory.Facility
                && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)));

    /// <summary>Puts the world in an uprising: one Tranches standing, Hand support 45 (need 2, doubled to 4 once the uprising starts).</summary>
    private static void Riot(GameHost h, string world)
    {
        h.Control.DebugOrdersEnabled = true;
        h.Control.SubmitDebugSetTroops(Side.Hand, world, Side.Hand, "Tranches", 1);
        h.Control.SubmitDebugSetSupport(Side.Hand, world, 55);
        h.Game.Step();
        Assert.True(h.Support.IsUprising(world));
        Assert.Equal(4, h.Support.RequiredGarrison(world));
    }

    // S1
    [Fact]
    public void S1_BothSwitchesStartOff_AndSibylOrdersNothing()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        Assert.False(s.IsOn(SibylJob.ManageGarrisons));
        Assert.False(s.IsOn(SibylJob.ManageProduction));
        for (int d = 0; d < 5; d++) { Assert.Empty(s.PlanDay()); h.Game.Step(); }
        Assert.DoesNotContain(h.Game.State.Markers.Keys, k => k.StartsWith("sibyl.", StringComparison.Ordinal));
    }

    // S8, S2: the switch counts from the next day's plan; then a world short of its garrison gets regiments from a training facility
    [Fact]
    public void S2_S8_GarrisonsOn_FromTheNextDay_OrdersTheRegimentsAWorldIsShort()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        Assert.Empty(s.PlanDay()); // S8: not on yet today
        Riot(h, world);
        Assert.True(s.IsOn(SibylJob.ManageGarrisons));
        var orders = s.PlanDay();
        var o = Assert.Single(orders, x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
        Assert.StartsWith("3 x Tranches at ", o.Detail); // need 4 in the uprising, 1 standing
        Assert.All(orders, x => Assert.Contains("Tranches", x.Detail));
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == o.Sequence);
    }

    // S4: regiments already on order count
    [Fact]
    public void S4_RegimentsOnOrderCount_NoSecondOrder()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        string world = QuietWorld(h);
        s.Submit(SibylJob.ManageGarrisons, true);
        Riot(h, world);
        Assert.Contains(s.PlanDay(), x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
        h.Game.Step();
        Assert.DoesNotContain(s.PlanDay(), x => x.Detail.EndsWith(" for " + world, StringComparison.Ordinal));
    }

    // S5: production orders only mines and refineries, and balances them
    [Fact]
    public void S5_ProductionOn_IdleConstructionYardsGetMinesOrRefineriesOnly()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        var orders = s.PlanDay();
        Assert.NotEmpty(orders);
        Assert.All(orders, x => Assert.True(x.Detail.StartsWith("Mine at ", StringComparison.Ordinal) || x.Detail.StartsWith("Refinery at ", StringComparison.Ordinal), x.Detail));
        int mines = h.Economy.Objects.Count(o => o.Class == "Mine" && o.Side == Side.Hand), refs = h.Economy.Objects.Count(o => o.Class == "Refinery" && o.Side == Side.Hand);
        Assert.StartsWith(mines <= refs ? "Mine" : "Refinery", orders[0].Detail);
        // one order per idle construction yard system at most
        Assert.Equal(orders.Count, orders.Select(x => x.Detail[(x.Detail.LastIndexOf(" from ", StringComparison.Ordinal) + 6)..]).Distinct().Count());
        h.Game.Step();
        Assert.All(orders, x => Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == x.Sequence));
    }

    // S6: a yard with the player's own project is left alone
    [Fact]
    public void S6_AYardWithThePlayersProjectIsNotUsed()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        var yard = HandFacilities(h, FacilityKind.ConstructionYard).First().System!;
        h.Construction.SubmitBuild(Side.Hand, yard, YardFamily.Construction, "Sensor Station", 1, yard);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        Assert.NotNull(h.Construction.ProjectAt(yard, YardFamily.Construction));
        Assert.DoesNotContain(s.PlanDay(), x => x.Detail.EndsWith(" from " + yard, StringComparison.Ordinal));
    }

    // S7: the switches are saved with the game, and a loaded game plans the same orders
    [Fact]
    public void S7_SwitchesSurviveSaveAndLoad_AndPlanTheSameOrders()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageGarrisons, true);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        var loaded = GameHost.Load(h.SaveWithSettings());
        var s2 = new Sibyl(loaded, Side.Hand);
        Assert.True(s2.IsOn(SibylJob.ManageGarrisons) && s2.IsOn(SibylJob.ManageProduction));
        Assert.Equal(s.PlanDay().Select(x => x.Detail), s2.PlanDay().Select(x => x.Detail));
    }

    // Switching off stops her
    [Fact]
    public void SwitchingOff_StopsTheOrders()
    {
        var h = NewHost();
        var s = new Sibyl(h, Side.Hand);
        s.Submit(SibylJob.ManageProduction, true);
        h.Game.Step();
        Assert.NotEmpty(s.PlanDay());
        s.Submit(SibylJob.ManageProduction, false);
        h.Game.Step();
        Assert.Empty(s.PlanDay());
    }
}
