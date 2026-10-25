using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Construction;

namespace Praedra.Core.Tests.Economy;

// Tests of reverse-engineering batch B5b (mines, refineries, upkeep and build orders the Classic way), ids B5b-1 to B5b-11.
// Sheets: m2-mines-refineries-pools, m2-maintenance, m2-construction (B5b corrections, 2026-10-18). Hook time T = K + 1.
public class B5bEconomyTests
{
    private const string Home = "Charter";
    private const string Kuat = "Navalia";
    private const string Sullust = "Caminus";

    private static int Interdictor(EcoWorld w, Side side, string system) =>
        w.Fleets.AddFleet(side, "Blockade", new[] { "Subpoena" }, system);

    // B5b-1: capacity 350, load 300: 1 ISD (71) refused whole, nothing created; 2 Corellian Corvettes (46) accepted; 3 (69) refused whole.
    // rule: m2-maintenance §Overdraft (B5b §7, [EXE-own] 0x52e970)
    [Fact]
    public void B5b_1_AnOrderAboveTheFreeCapacityIsRefusedWhole()
    {
        var w = new BuildWorld();
        w.Build.RefuseOverdrawOrders = true; // the DEFAULT (Dev lead 2026-10-18); BuildWorld turns it off for the construction sheet
        w.Hold(Home, Side.Empire); w.Hold(Sullust, Side.Alliance);
        foreach (var (sys, side) in new[] { (Home, Side.Empire), (Sullust, Side.Alliance) })
        {
            for (int i = 0; i < 7; i++) { w.Eco.AddFacility(sys, FacilityKind.Mine); w.Eco.AddFacility(sys, FacilityKind.Refinery); }
            w.Yard(sys, FacilityKind.OrbitalShipyard); // 13
            w.Refined(side, 1000);
        }
        for (int i = 0; i < 4; i++) w.Eco.AddObject(Side.Empire, "Mandate", Home); // 284
        w.Eco.AddObject(Side.Empire, "Gleaner", Home); // 3
        for (int i = 0; i < 8; i++) w.Eco.AddObject(Side.Alliance, "Mender", Sullust); // 280
        for (int i = 0; i < 2; i++) w.Eco.AddObject(Side.Alliance, "Militia", Sullust); // 6
        w.Eco.AddObject(Side.Alliance, "Strikers", Sullust); // 1
        foreach (var side in new[] { Side.Empire, Side.Alliance })
        {
            Assert.Equal(350, w.Eco.Capacity(side));
            Assert.Equal(300, w.Eco.Load(side));
        }
        int objects = w.Eco.Objects.Count();

        Assert.Equal(RejectReason.NoUpkeep, w.TryOrder(Side.Empire, Home, YardFamily.Shipyard, "Mandate", 1));
        Assert.Equal(RejectReason.NoUpkeep, w.TryOrder(Side.Alliance, Sullust, YardFamily.Shipyard, "Dodger", 3));
        Assert.Equal(objects, w.Eco.Objects.Count()); // nothing created, no partial order
        Assert.Empty(w.Events<ProjectStarted>());
        Assert.Equal((300, 300), (w.Eco.Load(Side.Empire), w.Eco.Load(Side.Alliance)));

        Assert.Null(w.TryOrder(Side.Alliance, Sullust, YardFamily.Shipyard, "Dodger", 2));
        Assert.Equal(346, w.Eco.Load(Side.Alliance));
        Assert.Equal(2, Assert.Single(w.Events<ProjectStarted>()).Count);
    }

    private static EcoWorld TwoMiningWorlds()
    {
        var w = new EcoWorld(freeShips: true); // the blockading Interdictor pays no upkeep, so it cannot be autoscrapped
        w.Hold(Home, Side.Empire, 100); w.Hold(Kuat, Side.Empire, 100);
        for (int i = 0; i < 2; i++) w.Mine(Home);
        for (int i = 0; i < 2; i++) w.Refinery(Home);
        for (int i = 0; i < 5; i++) w.Mine(Kuat);
        for (int i = 0; i < 5; i++) w.Refinery(Kuat);
        return w;
    }

    private static int[] AtHome(EcoWorld w, string cls) =>
        w.Eco.Objects.Where(o => o.System == Home && o.Class == cls).Select(o => o.Id).ToArray();

    /// <summary>Runs a world where Home's mines are mid-cycle, applies <paramref name="stop"/>, runs one more day and checks B5b-2's outcome.</summary>
    private static void AssertHomeStopsMidCycle(EcoWorld w, string reason)
    {
        var mines = AtHome(w, "Mine");
        var all = mines.Concat(AtHome(w, "Refinery")).ToArray();
        Assert.All(mines, id => Assert.Equal(1 + 2, w.E.Objects[id].Due)); // startup cycles running, due at hook 3
        w.Run(1); // hook 2
        Assert.Equal(250, w.Eco.Capacity(Side.Empire)); // 2 mines and 2 refineries leave the sums at once
        Assert.All(all, id => Assert.Equal(0, w.E.Objects[id].Due));
        Assert.Equal(all.OrderBy(i => i).ToArray(),
            w.Events<FacilityStopped>().Where(e => e.Day == 2 && e.Reason == reason).Select(e => e.Facility).OrderBy(i => i).ToArray());
        w.Run(10);
        Assert.DoesNotContain(w.Events<ResourceProduced>(), e => all.Contains(e.Facility)); // the abandoned cycles paid nothing
        Assert.Contains(w.Events<ResourceProduced>(), e => e.System == Kuat);
        Assert.Empty(w.Eco.RawLine(Side.Empire).Intersect(all));
    }

    // B5b-2: a blockade mid-cycle on a world with 2 mines and 2 refineries: the cycles end with no point; capacity drops by 100 the same day.
    // rule: m2-mines-refineries-pools §Blockade and uprising (B5b §3, [EXE-own] 0x50bbb0); m2-maintenance §Capacity
    [Fact]
    public void B5b_2_BlockadeMidCycleAbandonsTheCyclesAndDropsCapacityAtOnce()
    {
        var w = TwoMiningWorlds();
        // Blockade pause, 2026-10-18: this checks the Classic rule, so it sets the Classic switches (SuspendedLeaveCapacity on, MineStopMode Abandon).
        w.Eco.SuspendedLeaveCapacity = true; w.Eco.MineStopMode = StopMode.Abandon;
        w.Run(1);
        Assert.Equal(350, w.Eco.Capacity(Side.Empire));
        Interdictor(w, Side.Alliance, Home);
        Assert.True(w.Fleets.IsEconomicBlockade(Home, Side.Empire));
        Assert.Equal(250, w.Eco.Capacity(Side.Empire)); // the same day the blockade stands
        AssertHomeStopsMidCycle(w, "Suspended");
    }

    // B5b-2b (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch): with the DEFAULTs
    // (SuspendedLeaveCapacity off, MineStopMode Pause) the same blockade leaves capacity at 350, and Home's mines and refineries keep their
    // running cycles frozen; lifted, the mines pay the points they were working on. rule: m2-maintenance §Capacity; m2-mines-refineries-pools §Blockade and uprising
    [Fact]
    public void B5b_2b_BlockadeKeepsCapacityAndPausesTheCycles()
    {
        var w = TwoMiningWorlds();
        Assert.False(w.Eco.SuspendedLeaveCapacity);
        w.Run(1);
        var mines = AtHome(w, "Mine");
        Assert.All(mines, id => Assert.Equal(1 + 2, w.E.Objects[id].Due));
        int blocker = Interdictor(w, Side.Alliance, Home);
        Assert.Equal(350, w.Eco.Capacity(Side.Empire)); // still counted
        w.Run(10); // hooks 2 to 11 suspended
        Assert.Equal(350, w.Eco.Capacity(Side.Empire));
        Assert.All(mines, id => Assert.Equal(3 + 10, w.E.Objects[id].Due));
        Assert.All(mines, id => Assert.Equal(Side.Empire, w.E.Objects[id].PausedFor));
        Assert.DoesNotContain(w.Events<ResourceProduced>(), e => mines.Contains(e.Facility));
        w.State.Fleets.Remove(blocker);
        w.Run(2); // hook 12 resumes (one day left), hook 13 pays
        Assert.Equal(mines.OrderBy(i => i).ToArray(),
            w.Events<ResourceProduced>().Where(e => e.Day == 13 && mines.Contains(e.Facility)).Select(e => e.Facility).OrderBy(i => i).ToArray());
        Assert.Empty(w.Events<StartupCycle>().Where(e => e.Day >= 12 && mines.Contains(e.Facility))); // resumed, not restarted
    }

    // B5b-2c (Blockade pause, 2026-10-18): a refinery whose cycle is running keeps the raw it took; frozen while blockaded, it pays its
    // refined unit after the blockade. Abandon (Classic): the raw is lost and nothing is paid. rule: m2-mines-refineries-pools §Blockade and uprising
    [Theory]
    [InlineData(StopMode.Pause, 1)]
    [InlineData(StopMode.Abandon, 0)]
    public void B5b_2c_ARefineryCycleIsPausedOrLost(StopMode mode, int refined)
    {
        var w = new EcoWorld(freeShips: true);
        w.Eco.MineStopMode = mode;
        w.Hold(Home, Side.Empire, 100);
        int r = w.Refinery(Home);
        w.Eco.SetPools(Side.Empire, 1, 0);
        w.Run(1); // the refinery takes the raw and starts
        Assert.Equal(0, w.Eco.Raw(Side.Empire));
        Assert.NotEqual(0, w.E.Objects[r].Due);
        int blocker = Interdictor(w, Side.Alliance, Home);
        w.Run(20);
        Assert.Equal(0, w.Eco.Refined(Side.Empire));
        w.State.Fleets.Remove(blocker);
        w.Run(20);
        Assert.Equal(refined, w.Eco.Refined(Side.Empire));
        Assert.Equal(0, w.Eco.Raw(Side.Empire));
    }

    // Blockade pause, 2026-10-18: SuspendedLeaveCapacity on is the Classic rule (Classic switch): the same blockade takes Home's pair out at once.
    // rule: m2-maintenance §Capacity
    [Theory]
    [InlineData(false, 350)]
    [InlineData(true, 250)]
    public void B5b_2d_SuspendedLeaveCapacityBothSettings(bool leave, int capacity)
    {
        var w = TwoMiningWorlds();
        w.Eco.SuspendedLeaveCapacity = leave;
        w.Run(1);
        Interdictor(w, Side.Alliance, Home);
        Assert.Equal(capacity, w.Eco.Capacity(Side.Empire));
        w.Run(5);
        Assert.Equal(capacity, w.Eco.Capacity(Side.Empire));
    }

    // B5b-3: an uprising does the same with UprisingStopsMining on (DEFAULT, Dev lead 2026-10-18); off, nothing stops.
    // rule: m2-mines-refineries-pools §Blockade and uprising (B5b §3)
    [Fact]
    public void B5b_3_UprisingStopsMiningUnlessSwitchedOff()
    {
        var w = TwoMiningWorlds();
        Assert.True(w.Eco.UprisingStopsMining);
        // Blockade pause, 2026-10-18: this checks the Classic rule, so it sets the Classic switches (SuspendedLeaveCapacity on, MineStopMode Abandon).
        w.Eco.SuspendedLeaveCapacity = true; w.Eco.MineStopMode = StopMode.Abandon;
        bool uprising = false;
        w.Eco.IsInUprising = s => uprising && s == Home;
        w.Run(1);
        uprising = true;
        Assert.Equal(250, w.Eco.Capacity(Side.Empire));
        AssertHomeStopsMidCycle(w, "Suspended");

        var off = TwoMiningWorlds();
        off.Eco.UprisingStopsMining = false;
        off.Eco.IsInUprising = s => s == Home;
        off.Run(12);
        Assert.Equal(350, off.Eco.Capacity(Side.Empire));
        Assert.Empty(off.Events<FacilityStopped>());
        Assert.Contains(off.Events<ResourceProduced>(), e => e.System == Home);
    }

    // B5b-4: blockade lifted: each stopped mine restarts on a startup cycle of length in [d/2, d + d/2], one draw each.
    // rule: m2-mines-refineries-pools §First cycle (B5b §2, [EXE-own] 0x53b1d0; one draw when the startup cycle starts, DEFAULT)
    [Fact]
    public void B5b_4_EveryRestartIsAStartupCycleWithOneDraw()
    {
        var w = new EcoWorld(freeShips: true);
        w.Hold(Home, Side.Empire, 50); // d = 5 x 200 / 100 = 10 at share 0
        int a = w.Mine(Home), b = w.Mine(Home);
        Assert.Equal(10, EconomyModule.CycleDays(new EconomyParams(), 50, 0));
        w.Eco.MineStopMode = StopMode.Abandon; // Blockade pause, 2026-10-18: the Classic restart after an abandoned cycle (Classic switch); Pause resumes instead
        w.Run(1);
        int blocker = Interdictor(w, Side.Alliance, Home);
        w.Run(5); // stopped at hook 2, still blockaded at hook 6
        Assert.Equal((0, 0), (w.E.Objects[a].Due, w.E.Objects[b].Due));
        w.State.Fleets.Remove(blocker);
        int before = w.Stub!.Requests.Count;
        w.Stub.Expect(0, 10, 0, 10); // the two extremes of rand(0..d)
        w.Run(1); // hook 7
        Assert.Equal(new[] { (0, 10), (0, 10) }, w.Stub.Requests.Skip(before).ToArray()); // one draw each, nothing else
        Assert.Equal(7 + 10 / 2, w.E.Objects[a].Due);          // d/2 = 5
        Assert.Equal(7 + 10 + 10 / 2, w.E.Objects[b].Due);     // d + d/2 = 15
        Assert.Equal(new[] { (a, 5), (b, 15) }, w.Events<StartupCycle>().Where(e => e.Day == 7).Select(e => (e.Facility, e.Days)).ToArray());
    }

    // B5b-5: cycle length table. rule: m2-mines-refineries-pools §Cycle length (B5b §2, [EXE-own] 0x55e2b0: the share division rounds up)
    [Theory]
    [InlineData(50, 100, 10)]
    [InlineData(50, 50, 20)]
    [InlineData(0, 100, 5)]
    [InlineData(41, 100, 10)] // ceil(41 / 10) = 5
    [InlineData(50, 30, 33)]  // modifier 10000 / 30 = 333; 10 x 333 / 100 = 33
    public void B5b_5_CycleLengthTable(int share, int support, int days) =>
        Assert.Equal(days, EconomyModule.CycleDays(new EconomyParams(), support, share));

    // B5b-6: incremental share: 3 mines, load 0 -> 10: 4, 3, 3; a fourth mine comes online, load unchanged: share 0; load 12: 4, 3, 3, 2.
    // rule: m2-mines-refineries-pools §Load share (B5b §3, [EXE-own] 0x52f6b0)
    [Fact]
    public void B5b_6_SharesAreDealtIncrementally()
    {
        var w = new EcoWorld();
        w.Hold(Kuat, Side.Alliance, 100);
        for (int i = 0; i < 3; i++) w.Mine(Kuat);
        int[] Shares() => w.Eco.Objects.Where(o => o.Class == "Mine").Select(o => w.Eco.ShareOf(o.Id)).ToArray();
        w.Run(1);
        Assert.Equal(new[] { 0, 0, 0 }, Shares());
        for (int i = 0; i < 10; i++) w.Eco.AddObject(Side.Alliance, "Strikers"); // maintenance 1 each
        w.Run(1);
        Assert.Equal(new[] { 4, 3, 3 }, Shares());
        w.Mine(Kuat);
        w.Run(1);
        Assert.Equal(new[] { 4, 3, 3, 0 }, Shares()); // existing shares are kept; the newcomer has none
        for (int i = 0; i < 2; i++) w.Eco.AddObject(Side.Alliance, "Strikers");
        w.Run(1);
        Assert.Equal(new[] { 4, 3, 3, 2 }, Shares());
    }

    /// <summary>Capacity 100 (2 mines, 2 refineries), load 90 (15 Stormtrooper Regiments): under. Mine id 1 is the one taken away.</summary>
    private static EcoWorld UpkeepWorld()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire, 100);
        w.Mine(Home, id: 1); w.Mine(Home, id: 2); w.Refinery(Home, id: 3); w.Refinery(Home, id: 4);
        for (int i = 0; i < 15; i++) w.Eco.AddObject(Side.Empire, "Pacifiers", Home);
        return w;
    }

    private static void RunToDay(EcoWorld w, int day) { while (w.Game.Day < day) w.Game.Step(); }

    private static int[] AutoscrapDays(EcoWorld w) =>
        w.Events<ObjectScrapped>().Where(e => e.Reason == "Autoscrap").Select(e => e.Day).ToArray();

    // B5b-7: the overdraft starts on day 100 (capacity loss): scraps on 110 and 120; if it ends on 115 and starts again on 117: next scrap on 127.
    // rule: m2-maintenance §Overdraft and autoscrap (B5b §6, [EXE-own] 0x5329f0), AutoscrapTimer = Armed (DEFAULT)
    [Fact]
    public void B5b_7_TheAutoscrapTimerIsArmedWhenTheOverdraftStarts()
    {
        var w = UpkeepWorld();
        RunToDay(w, 99);
        Assert.Empty(AutoscrapDays(w));
        w.Eco.RemoveObject(1); // capacity 50 < load 90, seen at hook 100
        RunToDay(w, 125);
        Assert.Equal(new[] { 110, 120 }, AutoscrapDays(w));

        var v = UpkeepWorld();
        RunToDay(v, 99);
        v.Eco.RemoveObject(1); // over from hook 100
        RunToDay(v, 110);
        Assert.Equal(new[] { 110 }, AutoscrapDays(v));
        RunToDay(v, 114);
        int mine = v.Mine(Home); // back under at hook 115: the pending firing is dropped
        RunToDay(v, 116);
        v.Eco.RemoveObject(mine); // over again at hook 117
        RunToDay(v, 130);
        Assert.Equal(new[] { 110, 127 }, AutoscrapDays(v));
    }

    // B5b-8: autoscrap picks a ship in transit: it is destroyed on arrival, not before, with half its cost refunded then.
    // rule: m2-maintenance §Overdraft step 3 (B5b §6, [EXE-own] 0x4fb310); §Scrapping and refunds (B5b §8)
    [Fact]
    public void B5b_8_AShipPickedInTransitIsScrappedOnArrival()
    {
        var w = new EcoWorld(); // capacity 0, so the Empire is over from the first day: armed at hook 1, first firing at hook 11
        w.Hold(Home, Side.Empire, 100);
        int fleet = w.Fleets.AddFleet(Side.Empire, "F", new[] { "Mandate" }, Home);
        int ship = w.State.Fleets[fleet].Ships.Single();
        w.Fleets.SubmitMove(Side.Empire, fleet, "The Thaw");
        RunToDay(w, 11); // through hook 11
        Assert.Null(w.State.Fleets[fleet].At); // still on its way
        Assert.Empty(w.Events<FleetArrived>());
        Assert.Empty(w.Events<ObjectScrapped>()); // picked, but not destroyed in transit
        Assert.True(w.E.Objects[ship].ScrapPending);
        Assert.Equal(1, w.Stub!.Count(0, 0)); // the pick: a list of one
        int refined = w.Eco.Refined(Side.Empire);
        while (!w.Events<FleetArrived>().Any() && w.Game.Day < 400)
        {
            Assert.Empty(w.Events<ObjectScrapped>());
            w.Game.Step();
        }
        var arrived = Assert.Single(w.Events<FleetArrived>());
        var s = Assert.Single(w.Events<ObjectScrapped>()); // in the very Step it arrived
        Assert.Equal((ship, "Autoscrap", 112 / 2), (s.Object, s.Reason, s.Refund));
        Assert.Equal(refined + 56, w.Eco.Refined(Side.Empire));
        Assert.Null(w.Eco.Find(ship));
        Assert.True(arrived.Index < s.Index);
    }

    // B5b-9: capture of a world with a mine mid-cycle: no point for anyone; the mine restarts for the new holder on a startup cycle.
    // rule: m2-mines-refineries-pools §Side change (B5b §3, [EXE-own] 0x52d0e0 -> 0x55b010)
    [Fact]
    public void B5b_9_ACapturedMineRestartsFromZeroForTheNewHolder()
    {
        var w = new EcoWorld();
        w.Hold(Home, Side.Empire, 100);
        int mine = w.Mine(Home);
        w.Stub!.Expect(0, 5, 5); // the Empire's startup cycle: 5 + 2 = 7 days, due at hook 8
        w.Run(1);
        Assert.Equal(8, w.E.Objects[mine].Due);
        w.Run(3);
        w.Fleets.SetOwner(Home, Side.Alliance); // capture before hook 5
        w.Eco.SetAllianceSupport(Home, 100);
        w.Run(1); // hook 5
        var stop = Assert.Single(w.Events<FacilityStopped>());
        Assert.Equal((mine, Side.Empire, "Released", 5), (stop.Facility, stop.Side, stop.Reason, stop.Day));
        var start = w.Events<StartupCycle>().Last();
        Assert.Equal((mine, Side.Alliance, 5), (start.Facility, start.Side, start.Day));
        Assert.Equal(5 + 2, w.E.Objects[mine].Due); // startup rand(0..5) + 2 with the stub at 0
        w.Run(5);
        var paid = Assert.Single(w.Events<ResourceProduced>()); // only the new holder's cycle, at hook 7
        Assert.Equal((Side.Alliance, 7), (paid.PoolSide, paid.Day));
        Assert.Equal(0, w.Eco.Raw(Side.Empire));
    }

    // B5b-10: a second build order at a busy shipyard queues behind the first, which is not cancelled. Cancelling the queue gives nothing back for
    // unfinished units; the finished unit is not touched. rule: m2-construction §Projects and queues (B5b §7, inferred); m2-maintenance §Scrapping
    // and refunds (B5b §8). In the rebuild a finished unit leaves the project at once, so the "finished unit in a cancelled queue" refund cannot arise.
    [Fact]
    public void B5b_10_ASecondOrderQueuesBehindAndCancelRefundsNothingUnfinished()
    {
        var w = new BuildWorld();
        Assert.False(w.Build.NewOrderReplacesProject);
        w.Hold(Home, Side.Empire);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Side.Empire, 1000);
        Assert.Null(w.TryOrder(Side.Empire, Home, YardFamily.Shipyard, "Gleaner", 1));
        Assert.Null(w.TryOrder(Side.Empire, Home, YardFamily.Shipyard, "Mandate", 1));
        Assert.Empty(w.Events<ProjectCancelled>());
        var started = w.Events<ProjectStarted>();
        int tie = started[0].Units.Items[0], isd = started[1].Units.Items[0];
        var p = w.Build.ProjectAt(Home, YardFamily.Shipyard)!;
        Assert.Equal(new[] { tie, isd }, p.AllUnits().ToArray()); // the ISD waits behind the Gleaner
        while (!w.Events<UnitCompleted>().Any() && w.Game.Day < 200) w.Run(1);
        Assert.Equal(tie, Assert.Single(w.Events<UnitCompleted>()).Object); // the first order finished first
        Assert.Equal(new[] { isd }, w.Build.ProjectAt(Home, YardFamily.Shipyard)!.AllUnits().ToArray());
        w.Run(3);
        int refined = w.Eco.Refined(Side.Empire);
        w.Game.Submit(seq => new StopBuildOrder(seq, Side.Empire, Home, YardFamily.Shipyard));
        w.Run(1);
        var c = Assert.Single(w.Events<ProjectCancelled>());
        Assert.Equal(new[] { isd }, c.Units.Items.ToArray());
        Assert.Equal(refined, w.Eco.Refined(Side.Empire)); // nothing back for the unfinished ISD
        Assert.Null(w.Eco.Find(isd));
        Assert.NotNull(w.Eco.Find(tie)); // the finished unit stays
    }

    // B5b-10 (queue detail): each queued order keeps its own destination: a unit for Navalia departs for Navalia even when the order behind it is
    // for the yard's own system. rule: m2-construction §Projects and queues (B5b §7)
    [Fact]
    public void B5b_10_EachQueuedOrderKeepsItsOwnDestination()
    {
        var w = new BuildWorld();
        w.Hold(Home, Side.Empire); w.Hold(Kuat, Side.Empire);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Side.Empire, 1000);
        Assert.Null(w.TryOrder(Side.Empire, Home, YardFamily.Shipyard, "Gleaner", 1, Kuat));
        Assert.Null(w.TryOrder(Side.Empire, Home, YardFamily.Shipyard, "Gleaner", 1));
        while (w.Events<UnitCompleted>().Count < 2 && w.Game.Day < 200) w.Run(1);
        var done = w.Events<UnitCompleted>();
        Assert.Equal(("Gleaner", Kuat), (done[0].Class, done[0].Destination));
        Assert.NotEqual(0, done[0].DeliveryId);
        Assert.Equal(Kuat, Assert.Single(w.Events<DeliveryDeparted>()).To);
        Assert.Equal(("Gleaner", Home, 0), (done[1].Class, done[1].Destination, done[1].DeliveryId));
    }

    // B5b-11: a new game in the Classic economy (Settings.Economy set) starts with raw 0 and refined 0.
    // rule: m2-mines-refineries-pools §Starting stockpile (B5b §5, [EXE-own] 0x52d7c5 -> 0x533300)
    [Fact]
    public void B5b_11_ClassicEconomyStartsAtZeroZero()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Settings = new GameSettings { Economy = new EconomyParams() } });
        foreach (var side in new[] { Side.Empire, Side.Alliance })
            Assert.Equal((0, 0), (h.Economy.Raw(side), h.Economy.Refined(side)));
    }
}
