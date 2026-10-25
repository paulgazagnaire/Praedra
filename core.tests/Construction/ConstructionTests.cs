using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace Praedra.Core.Tests.Construction;

// Tests of m2-construction (ids C1 to C25 as in the sheet's table, then extra tests named after the rule they check).
// Hook time T = K + 1; events of slot 6.6 and of steps 3 and 4 carry Day = T. Real costs file unless a test says otherwise.
// Autoscrap is out of reach in these worlds (see BuildWorld); the sheet's tests do not involve it.
public class ConstructionTests
{
    private const string Home = "Charter";
    // v6 re-pin 2026-10-24: Far was Classis (one great-route leg, 7 days at speed 100, 5 at 80). On the spiral map no single-hop trip
    // from Charter takes those days; Remedium is the longest one the router flies direct (a 23-unit free jump: 4 days at speed 100, 3 at 80).
    private const string Far = "Remedium";
    private static readonly Side Emp = Side.Hand;

    /// <summary>Hand at Charter with yards of the given kinds, refined stock, and Remedium held too (support 100 both).</summary>
    private static BuildWorld Hand(int refined, params FacilityKind[] yards)
    {
        var w = new BuildWorld();
        w.Hold(Home, Emp); w.Hold(Far, Emp);
        foreach (var y in yards) w.Yard(Home, y);
        w.Refined(Emp, refined);
        return w;
    }

    private static int CompletedDay(BuildWorld w, int unitIndex = 0) => w.Events<UnitCompleted>()[unitIndex].Day;

    // ---------------------------------------------------------------- build time (C1 to C5)

    // m2-construction C1
    [Fact]
    public void C1_OneStandardYardMineIn80Days()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home));
        w.RunTo(80);
        Assert.Empty(w.Events<UnitCompleted>());
        w.RunTo(81);
        Assert.Equal(81, CompletedDay(w)); // Step 80, Day 81 after it
        Assert.Equal(80, w.Eco.Refined(Emp)); // the yard took 20 points
        Assert.True(w.Eco.Objects.Single(o => o.Class == "Mine").Completed);
        Assert.Null(w.Build.ProjectAt(Home, YardFamily.Construction));
        Assert.Equal(0, w.Game.Rng.Draws); // construction draws nothing (the new mine's first cycle is the economy's draw, at hook 82)
    }

    // m2-construction C2
    [Fact]
    public void C2_TwoStandardYardsMineIn40Days()
    {
        var w = Hand(100, FacilityKind.ConstructionYard, FacilityKind.ConstructionYard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home));
        w.RunTo(41);
        Assert.Equal(41, CompletedDay(w));
        Assert.Equal(80, w.Eco.Refined(Emp));
    }

    // m2-construction C3
    [Fact]
    public void C3_ThreeStandardYardsMineIn28Days()
    {
        var w = Hand(100, FacilityKind.ConstructionYard, FacilityKind.ConstructionYard, FacilityKind.ConstructionYard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home));
        w.RunTo(29);
        Assert.Equal(29, CompletedDay(w)); // 7 rounds: 3 x 6 = 18, then 2 in round 7
        Assert.Equal(80, w.Eco.Refined(Emp)); // no point wasted: the third yard stayed idle in round 7
    }

    // m2-construction C4
    [Fact]
    public void C4_OneAdvancedYardMineIn40Days()
    {
        var w = Hand(100, FacilityKind.AdvancedConstructionYard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home));
        w.RunTo(41);
        Assert.Equal(41, CompletedDay(w));
    }

    // m2-construction C5 (the sheet's fixture assumes load 0: with the real maintenance numbers the mine and refinery cycles are
    // longer, see the next test and the report's contradictions)
    [Fact]
    public void C5_XWingFedByOneMineAndOneRefinery()
    {
        var w = new BuildWorld(BuildWorld.ZeroMaintenance());
        w.Hold(Home, Side.Collective, 100);
        w.Yard(Home, FacilityKind.Mine, 1); w.Yard(Home, FacilityKind.Refinery, 2); // P1 fixture S1
        w.Yard(Home, FacilityKind.OrbitalShipyard, 3);
        Assert.Null(w.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Swift", 1));
        w.RunTo(32);
        Assert.Equal(new[] { 5, 13, 18, 23, 28 },
            w.Events<ResourceProduced>().Where(e => e.Resource == "Refined").Select(e => e.Day).ToArray());
        Assert.Equal(32, CompletedDay(w)); // Step 31
        Assert.Equal(0, w.Eco.Refined(Side.Collective)); // each refined was taken at once
        Assert.Contains(w.Events<YardWaiting>(), e => e.Day == 1); // the yard waited in the line from hook 1
    }

    // m2-construction C5, real maintenance numbers: load 17 (shipyard 13 + Swift 4) slows the mine and refinery, so the Swift is later than 32.
    [Fact]
    public void C5_WithRealMaintenanceTheCyclesAreLonger()
    {
        var w = new BuildWorld();
        w.Hold(Home, Side.Collective, 100);
        w.Yard(Home, FacilityKind.Mine, 1); w.Yard(Home, FacilityKind.Refinery, 2);
        w.Yard(Home, FacilityKind.OrbitalShipyard, 3);
        Assert.Null(w.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Swift", 1));
        w.Run(120);
        Assert.True(CompletedDay(w) > 32);
    }

    // m2-construction C6
    [Fact]
    public void C6_NoRefinedMeansWaitingNotLoss()
    {
        var w = new BuildWorld();
        w.Hold(Home, Side.Collective); w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Side.Collective, 3);
        Assert.Null(w.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Swift", 1));
        w.RunTo(2);
        Assert.Equal(2, w.Eco.Refined(Side.Collective)); // took one at hook 1
        w.RunTo(6);
        Assert.Equal(1, w.Eco.Refined(Side.Collective)); // second at hook 5
        w.RunTo(10);
        Assert.Equal(0, w.Eco.Refined(Side.Collective)); // third at hook 9
        w.RunTo(60);
        var p = w.Build.ProjectAt(Home, YardFamily.Shipyard)!;
        Assert.Equal(3, p.Progress);
        Assert.Empty(w.Events<UnitCompleted>());
        Assert.Single(w.Build.RefinedLine(Side.Collective)); // waiting in line, nothing lost
        w.Eco.SetPools(Side.Collective, 0, 2); // two more arrive later
        w.RunTo(80);
        Assert.Single(w.Events<UnitCompleted>());
        Assert.Equal(0, w.Eco.Refined(Side.Collective));
    }

    // ---------------------------------------------------------------- maintenance, cancel (C7 to C9)

    private static BuildWorld ShipyardWithLoad100()
    {
        var w = Hand(1000, FacilityKind.OrbitalShipyard); // 13
        for (int i = 0; i < 14; i++) w.Eco.AddObject(Emp, "Pacifiers", Home); // 14 x 6 = 84
        w.Eco.AddObject(Emp, "Militia", Home); // 3
        Assert.Equal(100, w.Eco.Load(Emp));
        return w;
    }

    // m2-construction C7 (also m2-maintenance M7)
    [Fact]
    public void C7_ThreeMandateTakeMaintenanceAtOrderAndFirstFinishesAfter448Days()
    {
        var w = ShipyardWithLoad100();
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Mandate", 3));
        Assert.Equal(313, w.Eco.Load(Emp));
        var mandates = w.Eco.Objects.Where(o => o.Class == "Mandate").ToList();
        Assert.Equal(3, mandates.Count);
        Assert.All(mandates, o => Assert.False(o.Completed));
        w.RunTo(449 - 1);
        Assert.Empty(w.Events<UnitCompleted>());
        w.RunTo(449);
        Assert.Equal(449, CompletedDay(w)); // 448 days after hook 1
        Assert.Equal(1, w.Eco.Objects.Count(o => o.Class == "Mandate" && o.Completed));
        Assert.Equal(313, w.Eco.Load(Emp)); // load never changed
    }

    // m2-construction C8
    [Fact]
    public void C8_NewOrderCancelsTheProjectWithoutRefund()
    {
        var w = ShipyardWithLoad100();
        // B5b 2026-10-18: the Classic build command never cancels (B5b §7); this checks the NewOrderReplacesProject switch (DEFAULT off).
        w.Build.NewOrderReplacesProject = true;
        w.TryOrder(Emp, Home, YardFamily.Shipyard, "Mandate", 3);
        w.RunTo(100);
        Assert.Equal(975, w.Eco.Refined(Emp)); // 25 cycles taken (hooks 1, 5, ..., 97)
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Gleaner", 1)); // Step 100
        var cancelled = w.Events<ProjectCancelled>().Single();
        Assert.Equal("Replaced", cancelled.Reason);
        Assert.Equal(3, cancelled.Units.Items.Count);
        Assert.DoesNotContain(w.Eco.Objects, o => o.Class == "Mandate");
        Assert.Equal(103, w.Eco.Load(Emp)); // 100 + one Gleaner (3)
        Assert.Equal(974, w.Eco.Refined(Emp)); // spent refined is gone; the new unit's first cycle started at hook 101
        Assert.Equal(0, w.Build.ProjectAt(Home, YardFamily.Shipyard)!.Progress); // starts clean
    }

    // m2-construction C9 (also m2-maintenance M8)
    [Fact]
    public void C9_StopBuildMidUnit()
    {
        var w = ShipyardWithLoad100();
        w.TryOrder(Emp, Home, YardFamily.Shipyard, "Mandate", 3);
        w.RunTo(100);
        w.Build.SubmitStop(Emp, Home, YardFamily.Shipyard);
        w.Run(1);
        Assert.Equal(100, w.Eco.Load(Emp));
        Assert.Equal(975, w.Eco.Refined(Emp));
        Assert.Null(w.Build.ProjectAt(Home, YardFamily.Shipyard));
        Assert.Equal("Stopped", w.Events<ProjectCancelled>().Single().Reason);
        w.Run(20);
        Assert.Equal(975, w.Eco.Refined(Emp)); // the yard stays idle
    }

    // ---------------------------------------------------------------- suspension (C10, C11)

    // m2-construction C10
    // B5b 2026-10-18: B5b §3 ([EXE-own] 0x50bbb0): yards stop like mines; the running cycle is abandoned with no point
    // (was: it finishes). The yard startup draw is not settled (B5b §12), so the restart is a normal cycle.
    [Fact]
    public void C10_BlockadeAbandonsTheRunningCycleAndStopsNewOnes()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Build.YardStopMode = StopMode.Abandon; // Blockade pause, 2026-10-18: this checks the Classic loss (Classic switch); the DEFAULT pauses (C10b)
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home);
        w.RunTo(6); // cycle 2 runs from hook 5 to 9
        int blocker = w.Fleets.AddFleet(Side.Collective, "Blockade", new[] { "Subpoena" }, Home); // gravity well: economic blockade
        Assert.True(w.Fleets.IsEconomicBlockade(Home, Emp));
        w.RunTo(9);
        var yard = w.Eco.Objects.Single(o => o.Class == "Construction Yard");
        Assert.Equal(1, w.Build.ProjectAt(Home, YardFamily.Construction)!.Progress); // the running cycle was abandoned: no point
        Assert.Equal(0, yard.Due);
        w.RunTo(18);
        Assert.Equal(1, w.Build.ProjectAt(Home, YardFamily.Construction)!.Progress);
        Assert.Equal(0, yard.Due); // no new cycle while blockaded
        w.State.Fleets.Remove(blocker); // lifted
        w.Run(1); // hook 19, the first hook after the flag clears
        Assert.Equal(23, yard.Due);
    }

    // m2-construction C10b (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch): YardStopMode Pause
    // (DEFAULT) freezes the running cycle while blockaded; it resumes where it left off (2 days still to run) and pays its point. No refined lost.
    [Fact]
    public void C10b_BlockadePausesTheRunningCycleAndResumesIt()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        Assert.Equal(StopMode.Pause, w.Build.YardStopMode);
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home);
        w.RunTo(6); // cycle 2 runs from hook 5 to 9
        int refined = w.Eco.Refined(Emp);
        int blocker = w.Fleets.AddFleet(Side.Collective, "Blockade", new[] { "Subpoena" }, Home);
        w.RunTo(18); // hooks 7 to 18 suspended: the due day moved on 12 days
        var yard = w.Eco.Objects.Single(o => o.Class == "Construction Yard");
        Assert.Equal(1, w.Build.ProjectAt(Home, YardFamily.Construction)!.Progress);
        Assert.Equal(9 + 12, yard.Due);
        Assert.Equal(refined, w.Eco.Refined(Emp)); // nothing new taken, nothing lost
        w.State.Fleets.Remove(blocker);
        w.RunTo(21);
        Assert.Equal(2, w.Build.ProjectAt(Home, YardFamily.Construction)!.Progress); // the paused cycle paid its point
    }

    // m2-construction C11
    [Fact]
    public void C11_UprisingSuspendsAndOrdersStillAccepted()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        bool uprising = true;
        w.Build.IsInUprising = s => uprising && s == Home;
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home)); // accepted
        w.Run(30);
        Assert.Equal(100, w.Eco.Refined(Emp));
        Assert.Equal(0, w.Build.ProjectAt(Home, YardFamily.Construction)!.Progress);
        uprising = false;
        w.Run(1);
        Assert.Equal(99, w.Eco.Refined(Emp));
    }

    // m2-construction C11 with the real support module: an uprising suspends new cycles, the end of it resumes them
    [Fact]
    public void C11_RealUprisingSuspendsYardsAndResumesWhenItEnds()
    {
        var w = new BuildWorld(withSupport: true);
        w.Hold(Home, Emp, 10); // Hand support 10 and no garrison: an uprising starts
        w.Yard(Home, FacilityKind.ConstructionYard);
        w.Refined(Emp, 100);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home));
        w.Run(5);
        Assert.True(w.Support!.IsUprising(Home));
        Assert.Equal(100, w.Eco.Refined(Emp)); // no cycle started
        Assert.Equal(0, w.Build.ProjectAt(Home, YardFamily.Construction)!.Progress);
        for (int i = 0; i < 40; i++) w.Eco.AddObject(Emp, "Pacifiers", Home); // garrison arrives
        w.Run(3);
        Assert.False(w.Support.IsUprising(Home));
        Assert.Contains(w.Events<Praedra.Core.Support.UprisingEnded>(), _ => true);
        w.Run(2);
        Assert.True(w.Eco.Refined(Emp) < 100); // cycles resume
    }

    // ---------------------------------------------------------------- delivery (C12 to C14)

    // m2-construction C12
    [Fact]
    public void C12_MineDeliveredToRemedium() // v6 re-pin 2026-10-24: was Classis (7 days); Remedium is 4 days, so every day after departure moves 3 earlier
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Far));
        Assert.Equal(1, w.Eco.EnergyUsed(Far)); // slot reserved at order time
        w.RunTo(81);
        var dep = w.Events<DeliveryDeparted>().Single();
        Assert.Equal(81, dep.Day);
        Assert.Equal(new[] { Home, Far }, dep.Path.Items);
        Assert.Equal(4, dep.FirstHopTicks); // free jump: map distance 23, base 4, 4 * 100 / 100 = 4
        Assert.Equal(85, dep.DueDay);
        var mine = w.Eco.Objects.Single(o => o.Class == "Mine");
        Assert.False(mine.Completed);
        w.RunTo(84);
        Assert.False(mine.Completed);
        w.RunTo(85); // placed in Step 84, step 3
        Assert.True(mine.Completed);
        Assert.Equal(85, w.Events<DeliveryArrived>().Single().Day);
        Assert.Equal(88, mine.Due); // joined the idle sweep that Step: first cycle at hook 85 (draw 0: d = 6, 0 + 3)
        Assert.Contains((0, 6), w.Stub!.Requests);
        Assert.Empty(w.Build.Deliveries);
    }

    // m2-construction C13
    [Fact]
    public void C13_MandateDeliveredAtSpeed80()
    {
        var w = Hand(1000, FacilityKind.OrbitalShipyard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Mandate", 1, Far));
        w.RunTo(449);
        var dep = w.Events<DeliveryDeparted>().Single();
        Assert.Equal(3, dep.FirstHopTicks); // v6 re-pin 2026-10-24: free jump, base 4, 4 * 80 / 100 = 3.2, floored to 3 (was 5)
        w.RunTo(452);
        Assert.Equal(452, w.Events<DeliveryArrived>().Single().Day);
        Assert.True(w.Eco.Objects.Single(o => o.Class == "Mandate").Completed);
    }

    // m2-construction C14
    [Fact]
    public void C14_FighterSquadronDeliveredAtSpeed100()
    {
        var w = Hand(100, FacilityKind.OrbitalShipyard);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Shipyard, "Gleaner", 1, Far)); // hyperdrive 0 as a fleet, 100 as a delivery
        w.RunTo(13);
        Assert.Equal(4, w.Events<DeliveryDeparted>().Single().FirstHopTicks); // v6 re-pin 2026-10-24 (was 7)
        w.RunTo(17);
        Assert.Equal(17, w.Events<DeliveryArrived>().Single().Day);
    }

    // ---------------------------------------------------------------- rejections (C15 to C19)

    // m2-construction C15
    [Fact]
    public void C15_MineOrderToFullLanternRejectedNoSlot()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Hold("The Lantern", Emp);
        w.Eco.SetSlots("The Lantern", 6, 6);
        for (int i = 0; i < 6; i++) w.Eco.AddFacility("The Lantern", FacilityKind.Mine);
        Assert.Equal("NoSlot", w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, "The Lantern"));
    }

    // m2-construction C16
    [Fact]
    public void C16_FacilityToEnemyHeldDestinationRejectedBadDestination()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Hold(Far, Side.Collective);
        Assert.Equal("BadDestination", w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Far));
    }

    // m2-construction C17
    [Fact]
    public void C17_CollectiveCannotBuildMandate()
    {
        var w = new BuildWorld();
        w.Hold(Home, Side.Collective); w.Yard(Home, FacilityKind.OrbitalShipyard); w.Refined(Side.Collective, 500);
        Assert.Equal("NotAvailable", w.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Mandate", 1));
    }

    // m2-construction C18
    [Fact]
    public void C18_AdvancedShipyardNeedsResearch()
    {
        var w = Hand(500, FacilityKind.ConstructionYard);
        w.Build.RespectResearchOrder = true; // the M3 default is AllAvailable (m3-ships-and-fighters, Contradictions): this sheet's test sets the gate explicitly
        Assert.Equal("NotResearched", w.TryOrder(Emp, Home, YardFamily.Construction, "Advanced Shipyard", 1, Home));
        w.Build.GrantResearch(Emp, "Advanced Shipyard"); // the setup grants it
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Advanced Shipyard", 1, Home));
    }

    // m2-construction C19, superseded by free travel (movement-redesign-draft §A, Paul 2026-10-16): every system can be reached,
    // so a delivery to Glasswood (off every route) is accepted.
    [Fact]
    public void C19_OffRouteDestinationIsReachableByFreeTravel()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Hold("Glasswood", Emp);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, "Glasswood"));
    }

    // ---------------------------------------------------------------- lost destination, forced stop (C20 to C22)

    // m2-construction C20 (the sheet body: Refuse). Mine refund 10.
    [Fact]
    public void C20_RefuseMode_FacilityInTransitRefusedWhenDestinationCaptured()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Build.LostDestination = LostDestinationRule.Refuse;
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Far);
        w.RunTo(81);
        w.Fleets.SetOwner(Far, Side.Collective); // captured during the flight
        int before = w.Eco.Refined(Emp);
        w.RunTo(85);
        var refused = w.Events<DeliveryRefused>().Single();
        Assert.Equal(85, refused.Day); // v6 re-pin 2026-10-24: the 4-day flight to Remedium (was 88)
        Assert.Equal(10, refused.Refund);
        Assert.Equal(before + 10, w.Eco.Refined(Emp));
        Assert.DoesNotContain(w.Eco.Objects, o => o.Class == "Mine");
        Assert.Empty(w.Build.Deliveries);
    }

    // m2-construction C20, maintenance freed: a facility with upkeep (Mordant Covenant I, 7 / 7)
    [Fact]
    public void C20_RefuseMode_MaintenanceFreed()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Build.LostDestination = LostDestinationRule.Refuse;
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mordant Covenant I", 1, Far);
        Assert.Equal(10 + 7, w.Eco.Load(Emp));
        w.RunTo(30); // 7 points x 4 days: complete at hook 29, flight 4 days (v6)
        w.Fleets.SetOwner(Far, Side.Collective);
        w.RunTo(40);
        Assert.Equal(3, w.Events<DeliveryRefused>().Single().Refund); // floor(7 / 2)
        Assert.Equal(10, w.Eco.Load(Emp));
    }

    // m2-construction §Lead review note: a delivery whose destination is lost pauses (DEFAULT)
    [Fact]
    public void C20_PauseMode_FacilityHeldAtDestinationUntilRegained()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Far);
        w.RunTo(81);
        w.Fleets.SetOwner(Far, Side.Collective); // lost during the flight (one hop)
        w.RunTo(90);
        var mine = w.Eco.Objects.Single(o => o.Class == "Mine");
        Assert.False(mine.Completed);
        Assert.Single(w.Build.Deliveries);
        Assert.Equal("DestinationLost", w.Events<DeliveryHeld>().Single().Reason);
        Assert.Empty(w.Events<DeliveryRefused>());
        Assert.Equal(Emp, w.Eco.OwnerOf(mine)); // still the ordering side's, whoever holds the system
        w.Fleets.SetOwner(Far, Emp); // regained
        w.Run(1);
        Assert.True(mine.Completed);
        Assert.Empty(w.Build.Deliveries);
        Assert.Equal(Emp, w.Eco.OwnerOf(mine));
    }

    [Fact]
    public void C20_PauseMode_LostBeforeDepartureWaitsAtTheYard()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Far);
        w.RunTo(60);
        w.Fleets.SetOwner(Far, Side.Collective);
        w.RunTo(81);
        var held = w.Events<DeliveryHeld>().Single();
        Assert.Equal(Home, held.System); // never leaves
        w.Run(30);
        Assert.Empty(w.Events<DeliveryArrived>());
        w.Fleets.SetOwner(Far, Emp);
        w.Run(1);
        Assert.Single(w.Events<DeliveryResumed>());
        w.Run(8); // 4 days of flight on the v6 map (7 before), and some slack
        Assert.Single(w.Events<DeliveryArrived>());
    }

    // Path with an intermediate system for the stop tests: Charter to The Thaw at speed 100.
    private static (BuildWorld w, List<string> path) RegimentToThaw()
    {
        var w = Hand(500, FacilityKind.TrainingFacility);
        w.Hold("The Thaw", Emp);
        var path = w.Fleets.FindPath(Home, "The Thaw", 100);
        Assert.True(path.Ok);
        var names = path.Names(TestData.RealGalaxy).ToList();
        Assert.True(names.Count >= 3);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, "Pacifiers", 1, "The Thaw"));
        return (w, names);
    }

    // m2-construction C21
    [Fact]
    public void C21_DeliveryHeldAtEnemyFleetAndContinuesWhenItLeaves()
    {
        var (w, path) = RegimentToThaw();
        string mid = path[1];
        int picket = w.Fleets.AddFleet(Side.Collective, "Picket", new[] { "Subpoena" }, mid); // only a gravity well stops traffic in flight (DEFAULT GravityWellShip)
        w.RunTo(37); // C 9 x 4 days: complete at hook 37, the delivery leaves
        var dep = w.Events<DeliveryDeparted>().Single();
        w.RunTo(dep.DueDay); // arrives at mid in step 3, step 4 holds it
        var held = w.Events<DeliveryHeld>().Single();
        Assert.Equal(mid, held.System);
        Assert.Equal("FleetPresent", held.Reason);
        Assert.Equal(dep.DueDay, held.Day);
        w.Run(60);
        Assert.Single(w.Build.Deliveries); // still held 60 days later
        Assert.Empty(w.Events<Contact>()); // deliveries cause no contact
        w.State.Fleets.Remove(picket); // the enemy leaves
        w.Run(1);
        Assert.Single(w.Events<DeliveryResumed>()); // first step 4 without an armed enemy there
        w.Run(100);
        var arrived = w.Events<DeliveryArrived>().Single();
        Assert.Equal("The Thaw", arrived.System);
        var reg = w.Eco.Find(arrived.Object)!;
        Assert.True(reg.Completed);
        Assert.Equal("The Thaw", reg.System);
    }

    // m2-construction C22
    [Fact]
    public void C22_DeliveryForcedStopOffPassesThrough()
    {
        var (w, path) = RegimentToThaw();
        w.Build.DeliveryForcedStop = false;
        w.Fleets.AddFleet(Side.Collective, "Picket", new[] { "Mandate" }, path[1]);
        w.RunTo(37);
        var dep = w.Events<DeliveryDeparted>().Single();
        w.Run(200);
        Assert.Empty(w.Events<DeliveryHeld>());
        Assert.Empty(w.Events<Contact>());
        Assert.Equal(path.Count - 2, w.Events<DeliveryPassed>().Count);
        int total = dep.Day + w.Fleets.FindPath(Home, "The Thaw", 100).TotalTicks;
        Assert.Equal(total, w.Events<DeliveryArrived>().Single().Day);
    }

    // m2-construction §Lead review note, several hops: held at the last system before the destination
    [Fact]
    public void LostDestinationPausesAtTheLastSystemBeforeIt()
    {
        var (w, path) = RegimentToThaw();
        w.RunTo(37);
        w.Fleets.SetOwner("The Thaw", Side.Collective); // lost while the delivery is under way
        w.Run(200);
        var held = w.Events<DeliveryHeld>().Single();
        Assert.Equal(path[^2], held.System);
        Assert.Equal("DestinationLost", held.Reason);
        Assert.Empty(w.Events<DeliveryArrived>());
        w.Fleets.SetOwner("The Thaw", Emp);
        w.Run(1);
        Assert.Single(w.Events<DeliveryResumed>());
        w.Run(100);
        Assert.Equal("The Thaw", w.Events<DeliveryArrived>().Single().System);
    }

    // ---------------------------------------------------------------- scrapping, starting yards, determinism (C23 to C25)

    // m2-construction C23
    [Fact]
    public void C23_ScrapLastYardMidCycleCancelsProject()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        int yard = w.Eco.Objects.Single().Id;
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home);
        w.RunTo(10); // cycles taken at hooks 1, 5, 9; the third runs until 13
        Assert.Equal(97, w.Eco.Refined(Emp));
        w.Scrap(Emp, yard);
        w.Run(1);
        Assert.Equal(97 + 5, w.Eco.Refined(Emp)); // refund 5; the cycle's refined is lost
        Assert.Null(w.Build.ProjectAt(Home, YardFamily.Construction));
        Assert.DoesNotContain(w.Eco.Objects, o => o.Class is "Mine" or "Construction Yard");
        Assert.Equal("YardLost", w.Events<ProjectCancelled>().Single().Reason);
        w.Run(30);
        Assert.Equal(102, w.Eco.Refined(Emp));
    }

    // m2-construction C24
    [Fact]
    public void C24_StartingYards()
    {
        var w = new BuildWorld();
        var list = StartingFacilities.Load(Path.Combine(TestData.DesignDir, StartingFacilities.FileName));
        foreach (var f in list) w.Fleets.SetOwner(f.System, f.Side);
        w.Build.ApplyStartingFacilities(list);
        Assert.Equal(7, w.Eco.Objects.Count(o => o.Class == "Orbital Shipyard" && o.Completed));
        Assert.Equal(4, list.Count(f => f.Side == Emp));
        Assert.Equal(3, list.Count(f => f.Side == null));
        foreach (var f in list) Assert.Equal(1, w.Eco.EnergyUsed(f.System));
        Assert.Equal(52, w.Eco.Load(Emp)); // 4 x 13
        Assert.Equal(0, w.Eco.Load(Side.Collective)); // neutral yards add no load
        w.Refined(Emp, 100); w.Refined(Side.Collective, 100);
        Assert.Equal("NotYours", w.TryOrder(Emp, "Caminus", YardFamily.Shipyard, "Gleaner", 1));
        Assert.Equal("NotYours", w.TryOrder(Side.Collective, "Caminus", YardFamily.Shipyard, "Swift", 1));
        Assert.Null(w.TryOrder(Emp, "Navalia", YardFamily.Shipyard, "Gleaner", 1));
    }

    // m2-construction C24: a captured neutral yard works for its captor (capture sheet owns the capture itself)
    [Fact]
    public void C24_NeutralYardWorksOnceTaken()
    {
        var w = new BuildWorld();
        w.Yard("Caminus", FacilityKind.OrbitalShipyard);
        w.Refined(Side.Collective, 100);
        w.Hold("Caminus", Side.Collective);
        Assert.Equal(13, w.Eco.Load(Side.Collective));
        Assert.Null(w.TryOrder(Side.Collective, "Caminus", YardFamily.Shipyard, "Swift", 1));
    }

    private static BuildWorld Busy(ulong seed)
    {
        var w = new BuildWorld(seed);
        w.Hold(Home, Emp); w.Hold(Far, Emp); w.Hold("The Thaw", Emp);
        w.Yard(Home, FacilityKind.ConstructionYard); w.Yard(Home, FacilityKind.ConstructionYard);
        w.Yard(Home, FacilityKind.OrbitalShipyard); w.Yard(Home, FacilityKind.TrainingFacility);
        w.Refined(Emp, 5000);
        w.Order(Emp, Home, YardFamily.Construction, "Mordant Lien-150", 2, Far);
        w.Order(Emp, Home, YardFamily.Shipyard, "Decree", 2, Far);
        w.Order(Emp, Home, YardFamily.Training, "Pacifiers", 3, "The Thaw");
        w.Run(300);
        w.Order(Emp, Home, YardFamily.Shipyard, "Gleaner", 4, Far); // replaces the Victory project
        w.Fleets.AddFleet(Side.Collective, "Picket", new[] { "Mandate" }, w.Fleets.FindPath(Home, "The Thaw", 100).Names(TestData.RealGalaxy)[1]);
        w.Run(1700);
        return w;
    }

    // m2-construction C25
    [Fact]
    public void C25_SameOrdersSameSeedSameHashAndNoDraws()
    {
        var a = Busy(1); var b = Busy(1); var c = Busy(2);
        Assert.Equal(2000, a.Game.Day);
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        Assert.Equal(a.Game.Events, b.Game.Events);
        Assert.Equal(0, a.Game.Rng.Draws); // construction draws nothing (no mines or refineries here, so the whole world draws nothing)
        Assert.Equal(GameSave.WorldHash(a.Game), GameSave.WorldHash(c.Game)); // the seed does not matter
        Assert.NotEmpty(a.Events<UnitCompleted>());
    }

    // ---------------------------------------------------------------- more rules of the sheet

    // m2-construction §Build time (table of days with discrete cycles; "3 standard" is the engine's number, not the doc's)
    [Theory]
    [InlineData(5, 20, 12, 8, 10)] [InlineData(9, 36, 20, 12, 18)] [InlineData(20, 80, 40, 28, 40)]
    [InlineData(34, 136, 68, 48, 68)] [InlineData(68, 272, 136, 92, 136)] [InlineData(92, 368, 184, 124, 184)]
    [InlineData(112, 448, 224, 152, 224)] [InlineData(282, 1128, 564, 376, 564)] [InlineData(584, 2336, 1168, 780, 1168)]
    public void BuildDaysTable(int cost, int one, int two, int three, int advanced)
    {
        Assert.Equal(one, ConstructionModule.BuildDays(cost, 1, 4));
        Assert.Equal(two, ConstructionModule.BuildDays(cost, 2, 4));
        Assert.Equal(three, ConstructionModule.BuildDays(cost, 3, 4));
        Assert.Equal(advanced, ConstructionModule.BuildDays(cost, 1, 2));
    }

    // m2-construction §Numbers (Mender 34: 136 / 68 / 48 days with 1 / 2 / 3 standard yards; run, not just formula)
    [Theory]
    [InlineData(1, 136)] [InlineData(2, 68)] [InlineData(3, 48)]
    public void MenderBuildDaysByYardCount(int yards, int days)
    {
        var w = new BuildWorld();
        w.Hold(Home, Side.Collective);
        for (int i = 0; i < yards; i++) w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Side.Collective, 1000);
        w.Build.GrantResearch(Side.Collective, "Mender"); // research order 1: the setup grants it
        Assert.Null(w.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Mender", 1));
        w.Run(days + 5);
        Assert.Equal(days + 1, CompletedDay(w));
        Assert.Equal(1000 - 34, w.Eco.Refined(Side.Collective));
    }

    [Fact]
    public void UnitsOfOneOrderAreBuiltOneAfterAnother()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 2, Home);
        Assert.Equal(2, w.Eco.EnergyUsed(Home) - 1); // both reserved at once
        w.RunTo(161);
        Assert.Equal(new[] { 81, 161 }, w.Events<UnitCompleted>().Select(e => e.Day).ToArray());
        Assert.Equal(60, w.Eco.Refined(Emp));
    }

    [Fact]
    public void ReplacingOrderFreesItsOwnReservations()
    {
        var w = Hand(500, FacilityKind.ConstructionYard);
        w.Build.NewOrderReplacesProject = true; // B5b 2026-10-18: replacing is now a switch (DEFAULT off, B5b §7)
        w.Hold("The Lantern", Emp);
        w.Eco.SetSlots("The Lantern", 6, 6);
        for (int i = 0; i < 3; i++) w.Eco.AddFacility("The Lantern", FacilityKind.Mine);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 3, "The Lantern")); // fills The Lantern
        Assert.Equal("NoSlot", w.TryOrder(Emp, "Charter", YardFamily.Construction, "Mine", 4, "The Lantern")); // even replacing, 4 do not fit
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 3, "The Lantern")); // replaces the old 3
        Assert.Equal(6, w.Eco.RawUsed("The Lantern"));
    }

    [Fact]
    public void RejectionsChangeNothing()
    {
        var w = Hand(100, FacilityKind.ConstructionYard, FacilityKind.OrbitalShipyard);
        w.Hold("The Thaw", Side.Collective);
        int objects = w.Eco.Objects.Count;
        Assert.Equal("UnknownSystem", w.TryOrder(Emp, "No Such Place", YardFamily.Construction, "Mine", 1, Home));
        Assert.Equal("UnknownItem", w.TryOrder(Emp, Home, YardFamily.Construction, "Mine Mk II", 1, Home));
        Assert.Equal("NotYours", w.TryOrder(Emp, "The Thaw", YardFamily.Construction, "Mine", 1, Home));
        Assert.Equal("NoYard", w.TryOrder(Emp, Home, YardFamily.Training, "Pacifiers", 1));
        Assert.Equal("WrongFamily", w.TryOrder(Emp, Home, YardFamily.Shipyard, "Mine", 1, Home));
        Assert.Equal("WrongFamily", w.TryOrder(Emp, Home, YardFamily.Construction, "Gleaner", 1));
        Assert.Equal("BadCount", w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 0, Home));
        Assert.Equal("BadDestination", w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1)); // a facility needs a destination
        Assert.Equal("BadDestination", w.TryOrder(Emp, Home, YardFamily.Shipyard, "Gleaner", 1, "The Thaw")); // a ship to an enemy system
        Assert.Equal("NotAvailable", w.TryOrder(Emp, Home, YardFamily.Shipyard, "Swift", 1)); // Collective only
        Assert.Equal(objects, w.Eco.Objects.Count);
        Assert.Empty(w.C.AllProjects());
        long stop = w.Build.SubmitStop(Emp, Home, YardFamily.Training);
        w.Run(1);
        Assert.Equal("NoProject", w.RejectionOf(stop));
    }

    [Fact]
    public void ZeroCostClassIsNotBuildable()
    {
        var w = Hand(100, FacilityKind.TrainingFacility);
        Assert.Equal("NotAvailable", w.TryOrder(Emp, Home, YardFamily.Training, "Repossessors", 1)); // m3-troops: cost 0 is not listed
    }

    [Fact]
    public void ResearchGateIsOnByDefaultAndOffIsAllAvailable() // m5-research §Defaults (RespectResearchOrder on); m3-ships-and-fighters §Sides and availability (the stub, off)
    {
        var w = Hand(500, FacilityKind.ConstructionYard);
        Assert.True(w.Build.RespectResearchOrder);
        Assert.Equal(RejectReason.NotResearched, w.TryOrder(Emp, Home, YardFamily.Construction, "Advanced Shipyard", 1, Home));
        var off = Hand(500, FacilityKind.ConstructionYard);
        off.Build.RespectResearchOrder = false;
        Assert.Null(off.TryOrder(Emp, Home, YardFamily.Construction, "Advanced Shipyard", 1, Home));
    }

    // m2-mines-refineries-pools §Asking for raw / serving a line (the refined line is first in first out)
    [Fact]
    public void RefinedLineIsFirstInFirstOut()
    {
        var w = new BuildWorld();
        w.Hold(Home, Emp); w.Hold("Navalia", Emp);
        w.Yard(Home, FacilityKind.OrbitalShipyard); w.Yard("Navalia", FacilityKind.OrbitalShipyard);
        w.Order(Emp, Home, YardFamily.Shipyard, "Gleaner", 1);
        w.Order(Emp, "Navalia", YardFamily.Shipyard, "Gleaner", 1);
        w.Run(3); // both asked at hook 1 (Charter first: system name order) and wait
        Assert.Equal(2, w.Build.RefinedLine(Emp).Count);
        w.Eco.SetPools(Emp, 0, 1);
        w.Run(1);
        var yards = w.Eco.Objects.Where(o => o.Class == "Orbital Shipyard").OrderBy(o => o.System, StringComparer.Ordinal).ToList();
        Assert.NotEqual(0, yards[0].Due); // Charter took it
        Assert.Equal(0, yards[1].Due);
        w.Eco.SetPools(Emp, 0, 1);
        w.Run(1);
        Assert.NotEqual(0, yards[1].Due);
        Assert.Empty(w.Build.RefinedLine(Emp));
    }

    // m2-construction §Suspension
    // B5b 2026-10-18: B5b §1 and §3: a suspended yard is IDLE, so it leaves the refined line (was: keeps its place and is
    // skipped) and asks again when the flag clears.
    [Fact]
    public void SuspendedYardLeavesTheLineAndAsksAgainOnRestart()
    {
        var w = new BuildWorld();
        w.Hold(Home, Emp); w.Hold("Navalia", Emp);
        w.Yard(Home, FacilityKind.OrbitalShipyard); w.Yard("Navalia", FacilityKind.OrbitalShipyard);
        w.Order(Emp, Home, YardFamily.Shipyard, "Gleaner", 1);
        w.Order(Emp, "Navalia", YardFamily.Shipyard, "Gleaner", 1);
        w.Run(3);
        int blocker = w.Fleets.AddFleet(Side.Collective, "Blockade", new[] { "Subpoena" }, Home);
        w.Eco.SetPools(Emp, 0, 1);
        w.Run(1);
        var yards = w.Eco.Objects.Where(o => o.Class == "Orbital Shipyard").OrderBy(o => o.System, StringComparer.Ordinal).ToList();
        Assert.Equal(0, yards[0].Due); // Charter is blockaded: stopped
        Assert.NotEqual(0, yards[1].Due);
        Assert.Empty(w.Build.RefinedLine(Emp)); // its line place is gone
        w.State.Fleets.Remove(blocker);
        w.Eco.SetPools(Emp, 0, 1);
        w.Run(1);
        Assert.NotEqual(0, yards[0].Due);
    }

    // m2-construction §Scrapping: scrapping one of two yards keeps the project; its cycle is lost
    [Fact]
    public void ScrappingOneOfTwoYardsKeepsTheProject()
    {
        var w = Hand(100, FacilityKind.ConstructionYard, FacilityKind.ConstructionYard);
        var ids = w.Eco.Objects.Select(o => o.Id).ToList();
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 1, Home);
        w.RunTo(10);
        w.Scrap(Emp, ids[0]);
        w.Run(1);
        Assert.NotNull(w.Build.ProjectAt(Home, YardFamily.Construction));
        w.Run(100);
        Assert.Single(w.Events<UnitCompleted>());
    }

    // m2-construction §Projects and queues: a project whose system changes hands ends
    [Fact]
    public void CapturedSystemEndsTheProject()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.TryOrder(Emp, Home, YardFamily.Construction, "Mine", 2, Home);
        w.RunTo(10);
        w.Fleets.SetOwner(Home, Side.Collective);
        w.Run(1);
        Assert.Null(w.Build.ProjectAt(Home, YardFamily.Construction));
        Assert.Equal("SystemLost", w.Events<ProjectCancelled>().Single().Reason);
        Assert.DoesNotContain(w.Eco.Objects, o => o.Class == "Mine");
        Assert.Equal(0, w.Eco.Objects.Count(o => o.Class == "Construction Yard" && o.Due != 0)); // the yard's cycle is gone too
    }

    // m3-fleets: the unit-appeared hook M3 uses for spare fleets
    [Fact]
    public void UnitAppearedFiresForCompletedAndDelivered()
    {
        var w = Hand(100, FacilityKind.OrbitalShipyard);
        var seen = new List<(string cls, UnitAppearance how)>();
        w.Build.UnitAppeared += (_, o, how) => seen.Add((o.Class, how));
        w.Build.NewOrderReplacesProject = true; // B5b 2026-10-18: keeps the replace path this test relies on (switch, DEFAULT off)
        w.TryOrder(Emp, Home, YardFamily.Shipyard, "Gleaner", 1);
        w.Order(Emp, Home, YardFamily.Shipyard, "Gleaner", 1, Far); // replaces: only the second is built
        w.Run(40);
        Assert.Equal(new[] { ("Gleaner", UnitAppearance.Delivered) }, seen.ToArray());
        w.Order(Emp, Home, YardFamily.Shipyard, "Gleaner", 1);
        w.Run(40);
        Assert.Equal(("Gleaner", UnitAppearance.Completed), seen[1]);
    }

    // m1-clock-and-orders §The clock: a save made between Steps carries projects and deliveries; attach again after loading
    [Fact]
    public void SaveAndLoadMidProjectAndMidDelivery()
    {
        BuildWorld Make()
        {
            var w = new BuildWorld(7);
            w.Hold(Home, Emp); w.Hold(Far, Emp);
            w.Yard(Home, FacilityKind.OrbitalShipyard); w.Yard(Home, FacilityKind.TrainingFacility);
            w.Refined(Emp, 2000);
            w.Order(Emp, Home, YardFamily.Shipyard, "Decree", 3, Far);
            w.Order(Emp, Home, YardFamily.Training, "Pacifiers", 6, Far);
            return w;
        }
        var straight = Make(); straight.Run(1476);
        var saved = Make(); saved.Run(274); // the first Decree left at hook 273 and is in flight (v6 re-pin 2026-10-24: a 3-day flight, so save at 274, not 276)
        Assert.NotEmpty(saved.C.Deliveries); // something is in flight or held at the save
        saved.Reload();
        saved.Run(1202);
        Assert.Equal(1476, saved.Game.Day);
        Assert.Equal(GameSave.StateHash(straight.Game), GameSave.StateHash(saved.Game));
        Assert.NotEmpty(saved.Events<UnitCompleted>().Concat<GameEvent>(Array.Empty<GameEvent>()));
    }

    [Fact]
    public void OrdersSurviveTheSaveFormat()
    {
        var w = Hand(100, FacilityKind.ConstructionYard);
        w.Order(Emp, Home, YardFamily.Construction, "Mine", 1, Far);
        w.Build.SubmitStop(Emp, Home, YardFamily.Construction);
        var loaded = GameSave.Load(GameSave.Save(w.Game));
        Assert.Equal(2, loaded.Queue.Count);
        Assert.IsType<BuildOrder>(loaded.Queue[0]);
        Assert.IsType<StopBuildOrder>(loaded.Queue[1]);
        Assert.Equal(GameSave.StateHash(w.Game), GameSave.StateHash(loaded));
    }
}
