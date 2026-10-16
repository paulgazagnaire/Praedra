using Rebellion.Core.Clock;
using Rebellion.Core.Economy;

namespace Rebellion.Core.Tests.Economy;

// Tests of m2-slots-and-resources (ids S1 to S15). Draws are stubbed; "draw v" means the stub returns v.
// Step K runs hook time T = K + 1; events carry Day = T.
public class SlotTests
{
    private static string[] Sys(int n) => TestData.RealGalaxy.Systems.Skip(5).Take(n).Select(s => s.Name).ToArray();

    // m2-slots-and-resources S1
    [Fact]
    public void S1_SlotFileLoads551RowsWithPinnedTotals()
    {
        var slots = EcoWorld.Slots.Value;
        Assert.Equal(551, slots.Count);
        int energy = 0, raw = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots.Of(i);
            energy += s.Energy; raw += s.Raw;
            Assert.True(s.Raw <= s.Energy, s.Name);
            Assert.True(s.Populated, s.Name);
        }
        Assert.Equal(4293, energy);
        Assert.Equal(2895, raw);
    }

    // m2-slots-and-resources S2
    [Theory]
    [InlineData("Kuat", 15, 6)]
    [InlineData("Coruscant", 15, 4)]
    [InlineData("Corellia", 14, 7)]
    [InlineData("Kessel", 6, 6)]
    [InlineData("Hoth", 2, 1)]
    public void S2_NamedSystemsHaveTheirSlots(string system, int energy, int raw)
    {
        var w = new EcoWorld();
        var s = w.Eco.SlotsOf(system);
        Assert.Equal((energy, raw), (s.Energy, s.Raw));
    }

    // m2-slots-and-resources S3
    [Fact]
    public void S3_FullSystemRejectsA16thFacility()
    {
        var w = new EcoWorld();
        for (int i = 0; i < 15; i++) w.Eco.AddFacility("Kuat", i % 2 == 0 ? FacilityKind.Refinery : FacilityKind.Kdy150);
        Assert.Equal("NoSlot", w.Eco.CheckPlace("Kuat", FacilityKind.OrbitalShipyard));
        Assert.Equal("NoSlot", w.Eco.CheckPlace("Kuat", FacilityKind.Mine));
    }

    // m2-slots-and-resources S4 (a reserved delivery counts as used)
    [Fact]
    public void S4_KesselMineNeedsFreeRawAndEnergy()
    {
        var w = new EcoWorld();
        for (int i = 0; i < 5; i++) w.Mine("Kessel");
        Assert.Null(w.Eco.CheckPlace("Kessel", FacilityKind.Mine));
        w.Mine("Kessel", completed: false); // accepted order: slot reserved at once
        Assert.Equal("NoSlot", w.Eco.CheckPlace("Kessel", FacilityKind.Mine));
        Assert.Equal(0, w.Eco.FreeEnergy("Kessel"));
        Assert.Equal(0, w.Eco.FreeRaw("Kessel"));
    }

    // m2-slots-and-resources S5
    [Fact]
    public void S5_OverEnergyDestroysTheDrawnFacilityWithoutRefund()
    {
        var w = new EcoWorld();
        w.Stub!.Expect(0, 3, 2); // the mine's and refinery's first-cycle draws have other ranges
        w.Hold("Kessel", Side.Empire);
        w.Eco.SetSlots("Kessel", 3, 3);
        w.Mine("Kessel", id: 5); w.Refinery("Kessel", id: 6);
        w.Eco.AddFacility("Kessel", FacilityKind.OrbitalShipyard, id: 7);
        w.Eco.AddFacility("Kessel", FacilityKind.LnrSeriesI, id: 8);
        w.InStep(ctx => w.Eco.EnforceCapacity(ctx, "Kessel"));
        w.Run(1);
        Assert.Equal(new[] { (0, 3) }, w.Stub.Requests.Where(r => r.Max == 3).ToArray());
        var e = Assert.Single(w.Events<FacilityDestroyed>());
        Assert.Equal((7, "Kessel", DestroyReason.OverEnergy), (e.Facility, e.System, e.Reason));
        Assert.Null(w.Eco.Find(7));
        Assert.Equal(0, w.Eco.Refined(Side.Empire)); // no refund
    }

    // m2-slots-and-resources S6
    [Fact]
    public void S6_OverRawDestroysAMine()
    {
        var w = new EcoWorld();
        w.Stub!.Expect(0, 1, 1);
        w.Eco.SetSlots("Kessel", 4, 1);
        w.Mine("Kessel", id: 1); w.Mine("Kessel", id: 2);
        w.InStep(ctx => w.Eco.EnforceCapacity(ctx, "Kessel"));
        w.Run(1);
        Assert.Single(w.Stub.Requests, (0, 1));
        var e = Assert.Single(w.Events<FacilityDestroyed>());
        Assert.Equal((2, DestroyReason.OverRaw), (e.Facility, e.Reason));
        Assert.NotNull(w.Eco.Find(1));
    }

    private static EcoWorld IncidentWorld(string system, int band, int nextInterval, out int due)
    {
        var w = new EcoWorld(incidents: true);
        w.Stub!.Expect(0, 550, EcoWorld.IndexOf(system)).Expect(0, 99, band).Expect(0, 499, nextInterval);
        w.E.ResourceDue = 1; // due at hook time 1: fires in Step 0
        due = 1;
        return w;
    }

    // m2-slots-and-resources S7 (the sheet's "next interval 100" read as the interval: draw 99, interval 1 + 99)
    [Fact]
    public void S7_RawPlusIncidentAppliesAndRearmsTimer()
    {
        var w = IncidentWorld("Coruscant", 62, 99, out _);
        w.Run(1);
        Assert.Equal(5, w.Eco.SlotsOf("Coruscant").Raw);
        var e = Assert.Single(w.Events<ResourceIncident>());
        Assert.Equal((SlotChange.RawPlus, 4, 5), (e.Change, e.Before, e.After));
        Assert.Equal(1 + 100, w.E.ResourceDue); // T + 100
        Assert.Equal(new[] { (0, 550), (0, 99), (0, 499) }, w.Stub!.Requests.ToArray());
    }

    // m2-slots-and-resources S8
    [Fact]
    public void S8_EnergyPlusAtTheCapDoesNothingButStillRearms()
    {
        var w = IncidentWorld("Coruscant", 80, 99, out _);
        w.Run(1);
        Assert.Equal(15, w.Eco.SlotsOf("Coruscant").Energy);
        Assert.Empty(w.E.Slots);
        var e = Assert.Single(w.Events<ResourceIncident>());
        Assert.Equal(SlotChange.None, e.Change);
        Assert.Equal(101, w.E.ResourceDue);
    }

    // m2-slots-and-resources S9
    [Fact]
    public void S9_RawMinusWithoutMineDoesNothing()
    {
        var w = IncidentWorld("Coruscant", 10, 0, out _);
        w.Run(1);
        Assert.Equal(4, w.Eco.SlotsOf("Coruscant").Raw);
        Assert.Equal(SlotChange.None, Assert.Single(w.Events<ResourceIncident>()).Change);
    }

    [Fact]
    public void S9b_RawMinusWithAMineLowersRaw()
    {
        var w = IncidentWorld("Coruscant", 10, 0, out _);
        w.Mine("Coruscant");
        w.Run(1);
        Assert.Equal(3, w.Eco.SlotsOf("Coruscant").Raw);
        Assert.Empty(w.Events<FacilityDestroyed>()); // 1 mine <= raw 3
    }

    // m2-slots-and-resources S10
    [Fact]
    public void S10_EnergyMinusThenOverCapacityDestroysTwoWithTwoDraws()
    {
        var w = IncidentWorld("Kessel", 30, 0, out _);
        w.Eco.SetSlots("Kessel", 2, 1);
        w.Refinery("Kessel", id: 1); w.Eco.AddFacility("Kessel", FacilityKind.Kdy150, id: 2); w.Eco.AddFacility("Kessel", FacilityKind.LnrSeriesI, id: 3);
        w.Stub!.Expect(0, 2, 0).Expect(0, 1, 0);
        w.Run(1);
        Assert.Equal(1, w.Eco.SlotsOf("Kessel").Energy);
        var destroyed = w.Events<FacilityDestroyed>();
        Assert.Equal(2, destroyed.Count);
        Assert.All(destroyed, d => Assert.Equal(DestroyReason.OverEnergy, d.Reason));
        Assert.Equal(1, w.Stub.Count(0, 2));
        Assert.Equal(1, w.Stub.Count(0, 1));
        Assert.Single(w.Eco.Objects); // one facility left
    }

    // m2-slots-and-resources S11
    [Fact]
    public void S11_DisasterDestroysFacilitiesBelowTenPercentAndKeepsSlots()
    {
        var w = new EcoWorld(incidents: true);
        w.Stub!.Expect(0, 550, EcoWorld.IndexOf("Kessel")).Queue();
        w.Mine("Kessel", id: 3); w.Refinery("Kessel", id: 4); w.Eco.AddFacility("Kessel", FacilityKind.Kdy150, id: 9);
        // per-facility rolls are (0,99): 5, 50, 9
        w.Stub.Expect(0, 99, 5, 50, 9);
        w.E.DisasterDue = 1;
        w.Run(1);
        var d = Assert.Single(w.Events<Disaster>());
        Assert.Equal(new[] { 3, 9 }, d.Destroyed.Items);
        Assert.Equal(3, d.Rolled);
        Assert.Equal(new[] { 3, 9 }, w.Events<FacilityDestroyed>().Select(e => e.Facility).ToArray());
        Assert.All(w.Events<FacilityDestroyed>(), e => Assert.Equal(DestroyReason.Disaster, e.Reason));
        Assert.NotNull(w.Eco.Find(4));
        var s = w.Eco.SlotsOf("Kessel");
        Assert.Equal((6, 6), (s.Energy, s.Raw));
        Assert.Empty(w.E.Slots);
        Assert.True(w.E.DisasterDue > 1);
    }

    // m2-slots-and-resources S12
    [Fact]
    public void S12_ResourceIncidentIsFullyResolvedBeforeTheDisaster()
    {
        var w = new EcoWorld(incidents: true);
        w.Eco.AddFacility("Hoth", FacilityKind.Kdy150, id: 1);
        w.Stub!.Queue(EcoWorld.IndexOf("Coruscant"), 62, 10, EcoWorld.IndexOf("Hoth"), 50, 10);
        w.E.ResourceDue = 1; w.E.DisasterDue = 1;
        w.Run(1);
        Assert.Equal(new[] { (0, 550), (0, 99), (0, 499), (0, 550), (0, 99), (0, 399) }, w.Stub.Requests.ToArray());
        var order = w.Game.Events.Where(e => e is ResourceIncident or Disaster).Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { "ResourceIncident", "Disaster" }, order);
    }

    // m2-slots-and-resources S13
    [Fact]
    public void S13_IncidentsOffMakeNoDrawsAndLeaveTheFileValues()
    {
        var w = new EcoWorld(seed: 7, incidents: false);
        w.Eco.SetupNewGame(); // arms nothing when incidents are off
        long before = w.Game.Rng.Draws;
        w.Run(5000);
        Assert.Equal(0, before);
        Assert.Equal(0, w.Game.Rng.Draws);
        Assert.Empty(w.E.Slots);
        Assert.Equal(0, w.E.ResourceDue + w.E.DisasterDue);
    }

    // m2-slots-and-resources S14
    [Fact]
    public void S14_ThousandStepsWithIncidentsGiveTheSameHashTwice()
    {
        string Run(ulong seed, out int incidents)
        {
            var w = new EcoWorld(seed, incidents: true);
            w.Eco.SetupNewGame();
            w.Run(1000);
            incidents = w.Events<ResourceIncident>().Count;
            return GameSave.StateHash(w.Game);
        }
        string a = Run(11, out int n1), b = Run(11, out int n2), c = Run(12, out _);
        Assert.Equal(a, b);
        Assert.Equal(n1, n2);
        Assert.NotEqual(a, c);
    }

    // m2-slots-and-resources S15
    [Fact]
    public void S15_RolledCoreSystemsMatchTheMonteCarloMeans()
    {
        var rng = new GameRng(2026);
        var p = new EconomyParams();
        double energy = 0, raw = 0; int populated = 0; const int n = 20000;
        for (int i = 0; i < n; i++)
        {
            var s = SlotRoller.Roll(rng, core: true, p);
            Assert.True(s.Raw <= s.Energy && s.Energy <= 15);
            energy += s.Energy; raw += s.Raw; if (s.Populated) populated++;
        }
        Assert.InRange(energy / n, 11.95, 12.05);
        Assert.InRange(raw / n, 9.0, 9.2);
        Assert.Equal(n, populated);
    }

    // m2-slots-and-resources §Rule (New) SlotSource: rolled in ordinal name order, rim stays within the caps
    [Fact]
    public void RolledSlotSourceFillsEverySystemAndIsDeterministic()
    {
        EcoWorld Make() { var w = new EcoWorld(seed: 3) { }; w.Eco.SlotSource = SlotSource.Rolled; w.Eco.SetupNewGame(); return w; }
        var a = Make(); var b = Make();
        Assert.Equal(551, a.E.Slots.Count);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        Assert.All(a.E.Slots.Values, s => Assert.True(s.Raw <= s.Energy && s.Energy is >= 1 and <= 15));
    }

    // m2-slots-and-resources §Where it runs (New game setup row): resource first, then disaster
    [Fact]
    public void SetupArmsResourceTimerBeforeDisasterTimer()
    {
        var w = new EcoWorld(incidents: true);
        w.Stub!.Expect(0, 499, 100).Expect(0, 399, 50);
        w.Eco.SetupNewGame();
        Assert.Equal(new[] { (0, 499), (0, 399) }, w.Stub.Requests.ToArray());
        Assert.Equal(1 + 1 + 100, w.E.ResourceDue);
        Assert.Equal(1 + 1 + 50, w.E.DisasterDue);
    }
}
