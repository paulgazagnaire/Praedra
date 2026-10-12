using Rebellion.Core.Clock;
using Rebellion.Core.Economy;

namespace Rebellion.Core.Tests.Economy;

// Tests of m2-maintenance (ids M1 to M22) on the real costs file. Hook time T = K + 1; events carry Day = T.
// Empire fixture E: 7 mines and 7 refineries (capacity 350); Orbital Shipyard id 100; 4 ISD ids 101 to 104;
// 10 Stormtrooper Regiments ids 105 to 114; all completed. Load = 13 + 4 x 71 + 10 x 6 = 357.
public class MaintenanceTests
{
    private const string Home = "Corellia";

    private static EcoWorld FixtureE(EcoWorld? w = null)
    {
        w ??= new EcoWorld();
        w.Hold(Home, Side.Empire, 100);
        for (int i = 1; i <= 7; i++) w.Mine(Home, id: i);
        for (int i = 8; i <= 14; i++) w.Refinery(Home, id: i);
        w.Eco.AddFacility(Home, FacilityKind.OrbitalShipyard, id: 100);
        for (int i = 101; i <= 104; i++) w.Eco.AddObject(Side.Empire, "Imperial Star Destroyer", id: i);
        for (int i = 105; i <= 114; i++) w.Eco.AddObject(Side.Empire, "Stormtrooper Regiment", id: i);
        return w;
    }

    private static EcoWorld Pairs(int mines, int refineries, bool extraMineInDelivery = false)
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire, 100);
        for (int i = 0; i < mines; i++) w.Mine(Home);
        for (int i = 0; i < refineries; i++) w.Refinery(Home);
        if (extraMineInDelivery) w.Mine(Home, completed: false);
        return w;
    }

    private static List<ObjectScrapped> Scraps(EcoWorld w) => w.Events<ObjectScrapped>();

    // m2-maintenance M1, M2, M3, M4
    [Theory]
    [InlineData(7, 7, false, 350)]
    [InlineData(7, 3, false, 150)]
    [InlineData(0, 5, false, 0)]
    [InlineData(7, 7, true, 350)]
    public void M1_to_M4_Capacity(int mines, int refineries, bool inDelivery, int capacity) =>
        Assert.Equal(capacity, Pairs(mines, refineries, inDelivery).Eco.Capacity(Side.Empire));

    // m2-maintenance M5
    [Fact]
    public void M5_FixtureELoadIs357AndFreeIsMinusSeven()
    {
        var w = FixtureE();
        Assert.Equal(357, w.Eco.Load(Side.Empire));
        Assert.Equal(-7, w.Eco.Free(Side.Empire));
    }

    // m2-maintenance M6
    [Fact]
    public void M6_MinesAndRefineriesCostNoMaintenance()
    {
        var w = FixtureE();
        w.Mine(Home); w.Refinery(Home);
        Assert.Equal(357, w.Eco.Load(Side.Empire));
        Assert.Equal(400, w.Eco.Capacity(Side.Empire));
    }

    // m2-maintenance M7 (the order itself is construction's; the three ISD objects it would create are added here)
    [Fact]
    public void M7_QueuedShipsCountAtOnceButAreNotAutoscrappable()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire);
        int a = w.Eco.AddObject(Side.Empire, "Imperial Star Destroyer"); // 71
        int b = w.Eco.AddObject(Side.Empire, "Assault Transport");       // 29
        Assert.Equal(100, w.Eco.Load(Side.Empire));
        for (int i = 0; i < 3; i++) w.Eco.AddObject(Side.Empire, "Imperial Star Destroyer", completed: false);
        Assert.Equal(313, w.Eco.Load(Side.Empire));
        w.Run(10); // capacity 0: autoscrap fires at hook time 10 over the two completed objects only
        var s = Assert.Single(Scraps(w));
        Assert.Contains(s.Object, new[] { a, b });
        Assert.Single(w.Stub!.Requests.Where(r => r.Max == 1));
    }

    // m2-maintenance M8
    [Fact]
    public void M8_CancellingQueuedShipsGivesBackMaintenanceButNoRefined()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire);
        w.Eco.AddObject(Side.Empire, "Imperial Star Destroyer"); w.Eco.AddObject(Side.Empire, "Assault Transport");
        var queued = Enumerable.Range(0, 3).Select(_ => w.Eco.AddObject(Side.Empire, "Imperial Star Destroyer", completed: false)).ToList();
        foreach (var id in queued) w.Eco.RemoveObject(id);
        Assert.Equal(100, w.Eco.Load(Side.Empire));
        Assert.Equal(0, w.Eco.Refined(Side.Empire));
    }

    // m2-maintenance M9 (there is no character object; a zero-maintenance object stands in: Bounty Hunters, cost 0)
    [Fact]
    public void M9_ZeroMaintenanceObjectsDoNotChangeLoad()
    {
        var w = FixtureE();
        w.Eco.AddObject(Side.Empire, "Bounty Hunters");
        Assert.Equal(357, w.Eco.Load(Side.Empire));
    }

    // m2-maintenance M10
    [Fact]
    public void M10_NeutralShipyardIsInNoSidesLoad()
    {
        var w = new EcoWorld();
        w.Eco.AddFacility("Hoth", FacilityKind.OrbitalShipyard);
        Assert.Equal(0, w.Eco.Load(Side.Empire));
        Assert.Equal(0, w.Eco.Load(Side.Alliance));
    }

    // m2-maintenance M11
    [Fact]
    public void M11_TimerFirstDueAtHookTimeTen()
    {
        var w = FixtureE();
        w.Run(9); // Steps 0 to 8
        Assert.Empty(Scraps(w));
        Assert.Equal(0, w.Stub!.Count(0, 14));
        w.Run(1); // Step 9: K + 1 = 10
        Assert.Single(Scraps(w));
        Assert.Equal(10, Scraps(w)[0].Day);
    }

    // m2-maintenance M12
    [Fact]
    public void M12_ClassicDrawZeroScrapsTheShipyard()
    {
        var w = FixtureE();
        w.Stub!.Expect(0, 14, 0);
        w.Run(10);
        var s = Assert.Single(Scraps(w));
        Assert.Equal((100, 10, "Autoscrap"), (s.Object, s.Refund, s.Reason));
        Assert.Equal(344, w.Eco.Load(Side.Empire));
        Assert.Equal(1, w.Stub.Count(0, 14)); // eligible = ids 100 to 114, 15 objects
        Assert.Null(w.Eco.Find(100));
    }

    // m2-maintenance M13
    [Fact]
    public void M13_ClassicDrawFourteenScrapsTheLastRegimentAndTheTimerFiresAgain()
    {
        var w = FixtureE();
        w.Stub!.Expect(0, 14, 14);
        w.Run(10);
        var s = Assert.Single(Scraps(w));
        Assert.Equal((114, 4), (s.Object, s.Refund));
        Assert.Equal(351, w.Eco.Load(Side.Empire)); // still over capacity 350 by 1
        w.Run(10); // Step 19 (K + 1 = 20)
        Assert.Equal(2, Scraps(w).Count);
        Assert.Equal(20, Scraps(w)[1].Day);
    }

    // m2-maintenance M14
    [Fact]
    public void M14_SmartScrapsRegimentsFirstCheapestLowestIdWithNoDraws()
    {
        var w = FixtureE();
        w.Eco.AutoscrapMode = AutoscrapMode.Smart;
        w.Run(10);
        Assert.Equal(105, Assert.Single(Scraps(w)).Object);
        Assert.Equal(4, Scraps(w)[0].Refund);
        Assert.Equal(351, w.Eco.Load(Side.Empire));
        w.Run(10);
        Assert.Equal(new[] { 105, 106 }, Scraps(w).Select(x => x.Object).ToArray());
        Assert.Equal(345, w.Eco.Load(Side.Empire));
        Assert.DoesNotContain(w.Stub!.Requests, r => r.Max is 13 or 14); // autoscrap made no draw
    }

    // m2-maintenance M15
    [Fact]
    public void M15_SmartWithOnlyYardsWarnsThenScrapsTheCheapestYard()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire);
        w.Mine(Home); w.Refinery(Home); // capacity 50
        w.Eco.AddFacility(Home, FacilityKind.OrbitalShipyard, id: 10);
        w.Eco.AddFacility(Home, FacilityKind.OrbitalShipyard, id: 11);
        w.Eco.AddFacility(Home, FacilityKind.AdvancedShipyard, id: 12);
        w.Eco.AddFacility(Home, FacilityKind.ConstructionYard, id: 13); // load 13 + 13 + 20 + 10 = 56
        w.Eco.AutoscrapMode = AutoscrapMode.Smart;
        w.Run(10);
        var kinds = w.Game.Events.Where(e => e is AutoscrapWarning or ObjectScrapped).Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { "AutoscrapWarning", "ObjectScrapped" }, kinds);
        Assert.Equal(13, Scraps(w)[0].Object); // cost 10 beats cost 20
        Assert.Equal(13, w.Events<AutoscrapWarning>()[0].Object);
    }

    // m2-maintenance M16
    [Fact]
    public void M16_LoadEqualToCapacityDoesNothingAndDrawsNothing()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire);
        w.Mine(Home); w.Refinery(Home);
        for (int i = 0; i < 5; i++) w.Eco.AddFacility(Home, FacilityKind.ConstructionYard); // load 50 = capacity 50
        w.Run(10);
        Assert.Empty(Scraps(w));
        Assert.Equal(0, w.Stub!.Count(0, 4));
    }

    private static int Submit(EcoWorld w, int obj, Side side = Side.Empire) =>
        (int)w.Game.Submit(seq => new ScrapObjectOrder(seq, side, obj));

    // m2-maintenance M17
    [Fact]
    public void M17_PlayerScrapIsImmediateWithRefund()
    {
        var w = FixtureE();
        Submit(w, 101);
        w.Run(1);
        var s = Assert.Single(Scraps(w));
        Assert.Equal((101, 56, "Order", Side.Empire), (s.Object, s.Refund, s.Reason, s.Side));
        Assert.Equal(357 - 71, w.Eco.Load(Side.Empire));
        Assert.Equal(56, w.Eco.Refined(Side.Empire));
        Assert.Single(w.Events<OrderAccepted>());
    }

    [Fact]
    public void ScrapOrderRejections()
    {
        var w = FixtureE();
        w.Eco.AddObject(Side.Empire, "Imperial Star Destroyer", id: 200, completed: false);
        Submit(w, 999); Submit(w, 101, Side.Alliance); Submit(w, 200);
        w.Run(1);
        Assert.Equal(new[] { RejectReason.UnknownObject, RejectReason.NotYours, RejectReason.NotCompleted },
            w.Events<OrderRejected>().Select(r => r.Reason).ToArray());
        Assert.Equal(357 + 71, w.Eco.Load(Side.Empire));
    }

    // m2-maintenance M18
    [Fact]
    public void M18_ScrappingAMineRefundsTenAndLowersCapacity()
    {
        var w = FixtureE();
        Submit(w, 1);
        w.Run(1);
        Assert.Equal(10, Scraps(w)[0].Refund);
        Assert.Equal(300, w.Eco.Capacity(Side.Empire));
        Assert.True(w.Eco.Load(Side.Empire) > w.Eco.Capacity(Side.Empire));
    }

    // m2-maintenance M19
    [Fact]
    public void M19_DeathStarShieldRefundsTwelve()
    {
        var w = FixtureE();
        int id = w.Eco.AddFacility(Home, FacilityKind.DeathStarShield);
        Submit(w, id);
        w.Run(1);
        Assert.Equal(12, Scraps(w)[0].Refund);
    }

    // m2-maintenance M20
    [Fact]
    public void M20_AllianceFiresBeforeEmpireOnTheSameDay()
    {
        var w = FixtureE();
        w.Hold("Sullust", Side.Alliance);
        w.Mine("Sullust"); w.Refinery("Sullust");
        for (int i = 0; i < 3; i++) w.Eco.AddObject(Side.Alliance, "Mon Calamari Cruiser"); // 210 over capacity 50
        w.Run(10);
        Assert.Equal(new[] { Side.Alliance, Side.Empire }, Scraps(w).Select(s => s.Side).ToArray());
        int a = w.Stub!.Requests.IndexOf((0, 2)), e = w.Stub.Requests.IndexOf((0, 14));
        Assert.True(a >= 0 && e > a);
    }

    // m2-maintenance M21
    [Fact]
    public void M21_OnlyCompletedObjectsAreEligible()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire);
        int done = w.Eco.AddObject(Side.Empire, "Stormtrooper Regiment");
        w.Eco.AddObject(Side.Empire, "Stormtrooper Regiment", completed: false);
        w.Run(10);
        Assert.Equal(done, Assert.Single(Scraps(w)).Object);
        Assert.Equal(1, w.Stub!.Count(0, 0)); // list of one
    }

    // m2-maintenance M22
    [Fact]
    public void M22_SameSeedSameHashAndDrawsMatchFirings()
    {
        (string hash, long draws, int scraps, int firstCycles) Run()
        {
            var w = FixtureE(new EcoWorld(seed: 5));
            w.Run(2000);
            return (GameSave.StateHash(w.Game), w.Game.Rng.Draws, Scraps(w).Count, w.E.Objects.Values.Count(o => o.FirstDone));
        }
        var a = Run(); var b = Run();
        Assert.Equal(a.hash, b.hash);
        Assert.True(a.scraps > 0);
        Assert.Equal(a.firstCycles + a.scraps, a.draws); // support 100 gives no smuggling draws; one pick per firing over capacity
    }
}
