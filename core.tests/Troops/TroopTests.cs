using System.Text.Json;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;
using Praedra.Core.Tests.Construction;
using Praedra.Core.Troops;

namespace Praedra.Core.Tests.Troops;

// Tests of m3-troops-and-special-forces (ids T1 to T23 as in the sheet's table, then extra tests named after the rule they check).
// Fixture T: Hand holds Charter with one standard Training Facility (id 500) and refined 1000; ISD 201 (3 slots), fleet 200 at
// Charter; Stormtrooper regiments 401 to 403 on Charter. Hook time T = K + 1; events of orders carry Day = K.
// The real galaxy, ship data, costs and troop files are used.
public class TroopTests
{
    // v6 re-pin 2026-10-24: Far was Classis (one great-route leg, 7 days at speed 100, 5 at 80). On the spiral map no single-hop trip
    // from Charter takes those days; Remedium is the longest one the router flies direct (a 23-unit free jump: 4 days at speed 100, 3 at 80).
    private const string Home = "Charter", Far = "Remedium";
    private const string Isd = "Mandate", Storm = "Pacifiers";
    private static readonly Side Emp = Side.Hand, Reb = Side.Collective;

    private static TroopData Data => BuildWorld.RealTroops.Value;

    /// <summary>Fixture T. <paramref name="regiments"/> = ids 401 to 403 on Charter; <paramref name="support"/> only for tests of the garrison.</summary>
    private static BuildWorld T(bool regiments = true, bool withSupport = false, int holderSupport = 100)
    {
        var w = new BuildWorld(withSupport: withSupport, wireFleets: true);
        w.Hold(Home, Emp, holderSupport);
        w.Hold(Far, Emp);
        w.Yard(Home, FacilityKind.TrainingFacility, 500);
        w.Refined(Emp, 1000);
        w.Eco.AddObject(Emp, Isd, null, true, 201);
        w.Fleets.AddFleetOf(Emp, "Fleet", Home, new[] { 201 }, 200);
        if (regiments) for (int id = 401; id <= 403; id++) w.Eco.AddObject(Emp, Storm, Home, true, id);
        w.E.NextObjectId = 600;
        return w;
    }

    private static string? Step(BuildWorld w, long seq) { w.Run(1); return w.RejectionOf(seq); }
    private static string? Load(BuildWorld w, int fleet, params int[] regs) => Step(w, w.Troops.SubmitLoad(Emp, fleet, regs));
    private static string? Unload(BuildWorld w, int fleet, params int[] regs) => Step(w, w.Troops.SubmitUnload(Emp, fleet, regs));
    private static string Snap(BuildWorld w) => JsonSerializer.Serialize(new { w.State.Fleets, w.State.Economy.Objects, w.State.Economy.NextObjectId });

    // ---------------------------------------------------------------- the class tables (T1, T2, T23)

    // m3-troops T1
    [Fact]
    public void T1_TenRegimentClasses()
    {
        Assert.Equal(10, Data.Regiments.Count);
        Assert.Equal(5, Data.Regiments.Count(r => r.Side == Reb));
        Assert.Equal(5, Data.Regiments.Count(r => r.Side == Emp));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Data.Regiments.Where(r => r.Side == Reb).Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, Data.Regiments.Where(r => r.Side == Emp).Select(r => r.Id).ToArray());
        var st = Data.Regiment(Storm)!;
        Assert.Equal((9, 6, 6, 6, 0), (st.Cost, st.Maintenance, st.Attack, st.Defense, st.ResearchOrder));
        var dt = Data.Regiment("Lictors")!;
        Assert.Equal((12, 8, 2), (dt.Cost, dt.Maintenance, dt.ResearchOrder));
        Assert.All(Data.Regiments, r => Assert.Equal(TroopData.TrainingFamily, r.ProductionFamily));
    }

    // m3-troops §Numbers (attack and defense, research orders, cost and maintenance): every class, and the same cost numbers as the costs file
    [Fact]
    public void RegimentNumbersMatchTheSheetAndTheCostsFile()
    {
        var expected = new (string, int, int, int, int, int, int)[] // name, cost, maint, attack, defense, research order, bombardment defense
        {
            ("Deckhands", 8, 6, 6, 3, 0, 5), ("Militia", 6, 3, 3, 5, 0, 5), ("Nhal Lookouts", 3, 1, 1, 4, 1, 2),
            ("Kiru Sparks", 10, 8, 8, 4, 3, 4), ("Ilmar Shade Guard", 6, 4, 2, 8, 2, 9),
            ("Pacifiers", 9, 6, 6, 6, 0, 6), ("Ballast", 7, 5, 5, 3, 0, 2), ("Tranches", 6, 3, 3, 5, 0, 5),
            ("Proxies", 9, 8, 8, 2, 1, 2), ("Lictors", 12, 8, 8, 8, 2, 6),
        };
        foreach (var (n, cost, maint, atk, def, ro, bd) in expected)
        {
            var c = Data.Regiment(n)!;
            Assert.Equal((cost, maint, atk, def, ro, bd), (c.Cost, c.Maintenance, c.Attack, c.Defense, c.ResearchOrder, c.BombardmentDefense));
            var k = BuildWorld.RealCosts.Value.Get(n);
            Assert.Equal((c.Cost, c.Maintenance, c.ResearchOrder), (k.Cost, k.Maintenance, k.ResearchOrder));
        }
    }

    // m3-troops T2
    [Fact]
    public void T2_NineSpecialForceClasses()
    {
        Assert.Equal(9, Data.SpecialForces.Count);
        Assert.Equal(4, Data.SpecialForces.Count(s => s.Side == Reb));
        Assert.Equal(5, Data.SpecialForces.Count(s => s.Side == Emp));
        var bh = Data.SpecialForce("Repossessors")!;
        Assert.Equal((0, 0), (bh.Cost, bh.Maintenance));
        Assert.All(Data.SpecialForces, s => Assert.Equal(0, s.ResearchOrder));
        Assert.All(Data.SpecialForces, s => Assert.Equal((0, 0, 0, 100), (s.EspionageVariance, s.CombatVariance, s.LeadershipVariance, s.LoyaltyBase)));
        var costs = BuildWorld.RealCosts.Value;
        foreach (var s in Data.SpecialForces)
            Assert.Equal((s.Cost, s.Maintenance), (costs.Get(s.Name).Cost, costs.Get(s.Name).Maintenance));
        Assert.Equal(new[] { 1, 2, 2, 1, 1, 1, 2, 2, 0 }, Data.SpecialForces.Select(s => s.Cost).ToArray());
    }

    // m3-troops T23
    [Fact]
    public void T23_NamesTableRenamesWithoutChangingStats()
    {
        var eh = Data.SpecialForce("Notaries")!;
        var isb = Data.SpecialForce("Auditors")!;
        // Praedra names 2026-10-25: the classes carry their Praedra names (Notaries, Auditors), so unit-names.csv no longer renames them
        Assert.Equal("Notaries", Data.SpecialForceDisplayName(eh));
        Assert.Equal("Auditors", Data.SpecialForceDisplayName(isb));
        Assert.Equal((8, 0x800, 55, 70, 2, 1), (eh.Id, eh.MissionBit, eh.Espionage, eh.Combat, eh.Cost, eh.Maintenance));
        Assert.Equal((6, 0x200, 60, 5, 1, 1), (isb.Id, isb.MissionBit, isb.Espionage, isb.Combat, isb.Cost, isb.Maintenance));
        Assert.Equal("Strikers", Data.SpecialForceDisplayName(Data.SpecialForce("Strikers")!)); // not renamed
        Assert.Equal(new[] { 0x001, 0x002, 0x004, 0x008, 0x100, 0x200, 0x400, 0x800, 0x1000 }, Data.SpecialForces.Select(s => s.MissionBit).ToArray());
    }

    // the names table must name classes that exist
    [Fact]
    public void NamesTableRejectsAnUnknownClass()
    {
        string regs = File.ReadAllText(Path.Combine(TestData.ClassicDir, TroopData.RegimentsFile));
        string sfs = File.ReadAllText(Path.Combine(TestData.ClassicDir, TroopData.SpecialForcesFile));
        Assert.ThrowsAny<Exception>(() => TroopData.Parse(regs, sfs, "kind,id,data_name,display_name\nspecial_force,8,Wrong Name,X\n"));
    }

    // ---------------------------------------------------------------- Training Facility production (T3 to T10b)

    // m3-troops T3
    [Fact]
    public void T3_ThreeStormtroopersCompleteAt37_73_109AndAppearOnTheSystem()
    {
        var w = T(regiments: false);
        int load0 = w.Eco.Load(Emp);
        long seq = w.Order(Emp, Home, YardFamily.Training, Storm, 3);
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
        Assert.Equal(load0 + 18, w.Eco.Load(Emp)); // 3 x 6 at once
        w.RunTo(120);
        Assert.Equal(new[] { 37, 73, 109 }, w.Events<UnitCompleted>().Select(e => e.Day).ToArray());
        var made = w.Events<RegimentCreated>();
        Assert.Equal(new[] { 37, 73, 109 }, made.Select(e => e.Day).ToArray());
        Assert.All(made, e => { Assert.Equal(Home, e.System); Assert.Equal(Storm, e.Class); });
        foreach (var e in made)
        {
            var o = w.Obj(e.Id);
            Assert.True(o.Completed);
            Assert.Equal(Home, o.System);
            Assert.Equal(0, o.AboardShip);
        }
        Assert.Single(w.State.Fleets); // not in a fleet
    }

    // m3-troops T4
    [Fact]
    public void T4_TwoFacilitiesFirstCompletionAtHook21()
    {
        var w = T(regiments: false);
        w.Yard(Home, FacilityKind.TrainingFacility, 501);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, Storm, 3));
        w.RunTo(25);
        Assert.Equal(21, w.Events<RegimentCreated>().First().Day);
    }

    // m3-troops T5
    [Fact]
    public void T5_CollectiveCannotOrderAStormtrooper()
    {
        var w = new BuildWorld();
        w.Hold(Home, Reb); w.Yard(Home, FacilityKind.TrainingFacility); w.Refined(Reb, 100);
        Assert.Equal("NotAvailable", w.TryOrder(Reb, Home, YardFamily.Training, Storm, 1));
    }

    // m3-troops T6
    [Fact]
    public void T6_BountyHuntersAreNotBuildable()
    {
        var w = T(regiments: false);
        Assert.Equal("NotAvailable", w.TryOrder(Emp, Home, YardFamily.Training, "Repossessors", 1));
    }

    // m3-troops T7
    [Fact]
    public void T7_ARegimentThroughAShipyardIsWrongFamily()
    {
        var w = T(regiments: false);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Yard(Home, FacilityKind.ConstructionYard);
        Assert.Equal("WrongFamily", w.TryOrder(Emp, Home, YardFamily.Shipyard, Storm, 1));
        Assert.Equal("WrongFamily", w.TryOrder(Emp, Home, YardFamily.Construction, Storm, 1));
    }

    // m3-troops T7b
    [Fact]
    public void T7b_NoCompletedTrainingFacilityIsNoYard()
    {
        var w = new BuildWorld();
        w.Hold(Home, Emp); w.Refined(Emp, 100);
        Assert.Equal("NoYard", w.TryOrder(Emp, Home, YardFamily.Training, Storm, 1));
        w.Eco.AddFacility(Home, FacilityKind.TrainingFacility, completed: false); // queued is not completed
        Assert.Equal("NoYard", w.TryOrder(Emp, Home, YardFamily.Training, Storm, 1));
    }

    // m3-troops T8
    [Fact]
    public void T8_DarkTrooperNeedsResearchOnlyUnderRespectResearchOrder()
    {
        var w = T(regiments: false);
        w.Build.RespectResearchOrder = true;
        Assert.Equal("NotResearched", w.TryOrder(Emp, Home, YardFamily.Training, "Lictors", 1));
        w.Build.RespectResearchOrder = false; // AllAvailable
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, "Lictors", 1));
    }

    // m3-troops T9
    [Fact]
    public void T9_DeliveryToRhinnalTakesFourDaysAndAppearsInStep3() // v6 re-pin 2026-10-24: was Classis, 7 days
    {
        var w = T(regiments: false);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, Storm, 1, Far));
        w.RunTo(60);
        var dep = w.Events<DeliveryDeparted>().Single();
        var arr = w.Events<DeliveryArrived>().Single();
        Assert.Equal(4, arr.Day - dep.Day); // free jump: map distance 23, base 4, 4 * 100 / 100 = 4
        var made = w.Events<RegimentCreated>().Single(); // once, on arrival (not at completion)
        Assert.Equal((Far, arr.Day), (made.System, made.Day));
        var o = w.Obj(made.Id);
        Assert.Equal((Far, 0), (o.System, o.AboardShip));
    }

    // m3-troops T10
    [Fact]
    public void T10_DestinationHeldByTheCollectiveIsBadDestination()
    {
        var w = T(regiments: false);
        w.Hold("The Thaw", Reb);
        Assert.Equal("BadDestination", w.TryOrder(Emp, Home, YardFamily.Training, Storm, 1, "The Thaw"));
    }

    // m3-troops T10b, superseded by free travel (movement-redesign-draft §A, Paul 2026-10-16): Glasswood, off every route, is reachable.
    [Fact]
    public void T10b_OffRouteDestinationIsReachableByFreeTravel()
    {
        var w = T(regiments: false);
        w.Hold("Glasswood", Emp);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, Storm, 1, "Glasswood"));
    }

    // ---------------------------------------------------------------- loading (T11 to T15, T18)

    // m3-troops T11
    [Fact]
    public void T11_ThreeRegimentsLoadOntoTheIsd()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401, 402, 403));
        Assert.All(new[] { 401, 402, 403 }, id => { Assert.Equal(201, w.Obj(id).AboardShip); Assert.Null(w.Obj(id).System); });
        var e = w.Events<RegimentsLoaded>().Single();
        Assert.Equal(new[] { 401, 402, 403 }, e.Regiments.Items.ToArray());
        Assert.Equal(new[] { 201, 201, 201 }, e.Ships.Items.ToArray());
        Assert.Equal((200, Home, 0), (e.Fleet, e.System, e.Day)); // an order event carries Day K
        Assert.Equal(0, w.Fleets.FreeTroopSlots(w.State.Fleets[200]));
    }

    // m3-troops T12
    [Fact]
    public void T12_AFourthRegimentHasNoSlot()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401, 402, 403));
        w.Eco.AddObject(Emp, Storm, Home, true, 404);
        var before = Snap(w);
        Assert.Equal("NoCapacity", Load(w, 200, 404));
        Assert.Equal(before, Snap(w));
    }

    // m3-troops T13
    [Fact]
    public void T13_LoadingIsAllOrNothing()
    {
        var w = new BuildWorld(wireFleets: true);
        w.Hold(Home, Reb);
        w.Eco.AddObject(Reb, "Mule", null, true, 201);
        w.Fleets.AddFleetOf(Reb, "Transport", Home, new[] { 201 }, 200);
        for (int id = 401; id <= 403; id++) w.Eco.AddObject(Reb, "Militia", Home, true, id);
        var before = Snap(w);
        Assert.Equal("NoCapacity", Step(w, w.Troops.SubmitLoad(Reb, 200, new[] { 401, 402, 403 })));
        Assert.Equal(before, Snap(w)); // none loaded
        Assert.Null(Step(w, w.Troops.SubmitLoad(Reb, 200, new[] { 401, 402 })));
        Assert.Equal(0, w.Fleets.FreeTroopSlots(w.State.Fleets[200]));
    }

    // m3-troops T14
    [Fact]
    public void T14_ARegimentOnAnotherSystemIsNotAtTheFleetSystem()
    {
        var w = T(regiments: false);
        w.Eco.AddObject(Emp, Storm, Far, true, 405);
        var before = Snap(w);
        Assert.Equal("NotAtFleetSystem", Load(w, 200, 405));
        Assert.Equal(before, Snap(w));
    }

    // m3-troops T15
    [Fact]
    public void T15_AFleetInTransitCannotLoad()
    {
        var w = T();
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        w.Run(1); // on the lane now
        Assert.Equal("InTransit", Load(w, 200, 401));
    }

    // m3-troops §Orders: a fleet ordered to move earlier in the same Step is in transit too
    [Fact]
    public void FleetOrderedToMoveEarlierInTheSameStepIsInTransit()
    {
        var w = T();
        w.Fleets.SubmitMove(Emp, 200, Far);
        long seq = w.Troops.SubmitLoad(Emp, 200, new[] { 401 });
        w.Run(1);
        Assert.Equal("InTransit", w.RejectionOf(seq));
    }

    // m3-troops T18
    [Fact]
    public void T18_ASpecialForceIsAnUnknownRegiment()
    {
        var w = T();
        w.Eco.AddObject(Emp, "Speculators", Home, true, 410);
        Assert.Equal("UnknownRegiment", Load(w, 200, 410));
        Assert.Equal("UnknownRegiment", Load(w, 200, 999)); // no such object either
        Assert.Equal("UnknownRegiment", Load(w, 200, 201)); // a ship is not a regiment
        Assert.Equal("UnknownRegiment", Unload(w, 200, 410));
        Assert.Equal(0, w.Obj(410).AboardShip); // special forces never board (DEFAULT)
    }

    // m3-troops §Orders: common rejections UnknownFleet, NotYours, DuplicateRegiment, NotCompleted, AlreadyAboard
    [Fact]
    public void CommonRejections()
    {
        var w = T();
        w.Eco.AddObject(Reb, "Militia", Home, true, 420);
        w.Eco.AddObject(Emp, Storm, Home, false, 421); // queued, not completed
        w.Eco.AddObject(Reb, "Dodger", null, true, 430);
        w.Fleets.AddFleetOf(Reb, "Collective", Home, new[] { 430 }, 431);
        var before = Snap(w);
        Assert.Equal("UnknownFleet", Load(w, 999, 401));
        Assert.Equal("NotYours", Step(w, w.Troops.SubmitLoad(Reb, 200, new[] { 401 }))); // a fleet of the other side
        Assert.Equal("NotYours", Load(w, 200, 420)); // a regiment of the other side
        Assert.Equal("DuplicateRegiment", Load(w, 200, 401, 401));
        Assert.Equal("NotCompleted", Load(w, 200, 421));
        Assert.Equal("EmptySelection", Load(w, 200));
        Assert.Equal(before, Snap(w));
        Assert.Null(Load(w, 200, 401));
        Assert.Equal("AlreadyAboard", Load(w, 200, 401));
        Assert.Equal("UnknownFleet", Unload(w, 999, 401));
        Assert.Equal("DuplicateRegiment", Unload(w, 200, 401, 401));
    }

    // m3-troops §Orders (LoadRegiments): listed order to ships by ascending id, first free slot
    [Fact]
    public void RegimentsGoToShipsByAscendingIdInListedOrder()
    {
        var w = T();
        w.Eco.AddObject(Emp, Isd, null, true, 202);
        w.Fleets.AddFleetOf(Emp, "Two", Home, new[] { 202 }, 210);
        w.Eco.AddObject(Emp, Storm, Home, true, 404);
        Assert.Null(Step(w, w.Fleets.SubmitMergeFleets(Emp, 210, 200)));
        Assert.Null(Load(w, 200, 404, 403, 402, 401)); // 4 regiments, 2 ISD of 3 slots
        Assert.Equal(new[] { 201, 201, 201, 202 }, new[] { 404, 403, 402, 401 }.Select(id => w.Obj(id).AboardShip).ToArray());
        Assert.Equal(new[] { 201, 201, 201, 202 }, w.Events<RegimentsLoaded>().Single().Ships.Items.ToArray());
        Assert.Equal(2, w.Fleets.FreeTroopSlots(w.State.Fleets[200]));
    }

    // ---------------------------------------------------------------- unloading and the garrison (T16, T17)

    // m3-troops T16 (the uprising ends when G reaches 2N: m2 DEFAULT, see the contradiction in the report), so the end half uses 6 regiments
    [Fact]
    public void T16_AboardRegimentsAreNoGarrisonUntilUnloaded()
    {
        var w = T(regiments: false, withSupport: true, holderSupport: 30); // N = 3
        w.Eco.AddObject(Emp, "Mandate", null, true, 202);
        w.Fleets.AddFleetOf(Emp, "Two", Home, new[] { 202 }, 210);
        for (int id = 401; id <= 406; id++) w.Eco.AddObject(Emp, Storm, Home, true, id);
        Assert.Equal(3, w.Support!.RequiredGarrison(Home));
        Assert.Null(Step(w, w.Fleets.SubmitMergeFleets(Emp, 210, 200)));
        Assert.False(w.Support.IsUprising(Home)); // 6 landed: G = 6 >= N
        Assert.Null(Load(w, 200, 401, 402, 403, 404, 405, 406));
        Assert.Equal(0, w.Support.Garrison10(Home)); // aboard: G = 0, the same Step's slot 6.1 sees it
        Assert.True(w.Support.IsUprising(Home));
        Assert.Contains(w.Events<UprisingStarted>(), e => e.System == Home);
        Assert.Null(Unload(w, 200, 401, 402, 403, 404, 405, 406));
        Assert.Equal(60, w.Support.Garrison10(Home)); // G = 6 at the next 6.1 (here: the same Step)
        Assert.False(w.Support.IsUprising(Home)); // 2N = 6 reached
        Assert.Contains(w.Events<UprisingEnded>(), e => e.System == Home);
        var ev = w.Events<RegimentsUnloaded>().Single();
        Assert.Equal((200, Home), (ev.Fleet, ev.System));
        Assert.All(new[] { 401, 402, 403 }, id => { Assert.Equal(Home, w.Obj(id).System); Assert.Equal(0, w.Obj(id).AboardShip); });
    }

    // m3-troops T16 as printed: 3 regiments, N = 3. G = 3 after the unload, but the m2 end test needs 2N = 6, so the uprising stays (m2 DEFAULT).
    [Fact]
    public void T16_AsPrintedThreeRegimentsStartAndStopTheUprisingStartButNotItsEnd()
    {
        var w = T(regiments: true, withSupport: true, holderSupport: 30);
        Assert.Null(Load(w, 200, 401, 402, 403));
        Assert.True(w.Support!.IsUprising(Home)); // G = 0 < N = 3
        Assert.Null(Unload(w, 200, 401, 402, 403));
        Assert.Equal(30, w.Support.Garrison10(Home)); // G = 3
        Assert.True(w.Support.IsUprising(Home)); // 3 < 2N: the sheet's "uprising ends" needs the m2 end test to use N, not 2N
    }

    // m3-troops T17
    [Fact]
    public void T17_UnloadAtAnCollectiveHeldSystemIsNotYourSystem()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401));
        w.Hold(Home, Reb); // the system is captured while the regiment rides
        var before = Snap(w);
        Assert.Equal("NotYourSystem", Unload(w, 200, 401));
        Assert.Equal(before, Snap(w));
        // a regiment left on a system that was captured can still board and leave (no holder condition on loading)
        w.Eco.AddObject(Emp, Storm, Home, true, 404);
        Assert.Null(Load(w, 200, 404));
    }

    // m3-troops §Orders: neutral systems refuse too; the switch UnloadOnlyAtHeldSystem is the DEFAULT-on switch
    [Fact]
    public void UnloadAtANeutralSystemIsRefusedUnlessTheSwitchIsOff()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401));
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, "The Thaw"))); // neutral
        w.Run(80);
        Assert.Equal("The Thaw", w.State.Fleets[200].At);
        Assert.True(w.Troops.UnloadOnlyAtHeldSystem);
        Assert.Equal("NotYourSystem", Unload(w, 200, 401));
        w.Troops.UnloadOnlyAtHeldSystem = false;
        Assert.Null(Unload(w, 200, 401));
        Assert.Equal("The Thaw", w.Obj(401).System);
    }

    // m3-troops §Orders (UnloadRegiments): NotAboard for a landed regiment or one on another fleet; InTransit while flying
    [Fact]
    public void UnloadNeedsRegimentsAboardThisFleetAndNoTransit()
    {
        var w = T();
        Assert.Equal("NotAboard", Unload(w, 200, 401)); // landed
        w.Eco.AddObject(Emp, Isd, null, true, 202);
        w.Fleets.AddFleetOf(Emp, "Other", Home, new[] { 202 }, 210);
        Assert.Null(Step(w, w.Troops.SubmitLoad(Emp, 210, new[] { 402 })));
        Assert.Equal("NotAboard", Unload(w, 200, 402)); // aboard fleet 210's ship
        Assert.Null(Load(w, 200, 401));
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        w.Run(1);
        Assert.Equal("InTransit", Unload(w, 200, 401));
    }

    // m3-troops §Where regiments live: flying with regiments aboard, unload at the destination
    [Fact]
    public void RegimentsRideToAnotherSystemAndUnloadThere()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401, 402));
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        w.Run(60);
        Assert.Equal(Far, w.State.Fleets[200].At);
        Assert.Equal(201, w.Obj(401).AboardShip);
        Assert.Null(Unload(w, 200, 401, 402));
        Assert.Equal(Far, w.Obj(401).System);
        Assert.Equal(Far, w.Obj(402).System);
    }

    // m3-fleets §Fleet speed: regiments aboard never slow a fleet
    [Fact]
    public void RegimentsAboardDoNotChangeFleetSpeed()
    {
        var w = T();
        int speed = w.Fleets.SpeedOf(w.State.Fleets[200]);
        Assert.Null(Load(w, 200, 401, 402, 403));
        Assert.Equal(speed, w.Fleets.SpeedOf(w.State.Fleets[200]));
    }

    // m3-troops §Orders (rejection changes nothing; accepted orders change only places)
    [Fact]
    public void ARejectedOrderChangesNothingAnAcceptedOneOnlyMovesRegiments()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401));
        var hashed = JsonSerializer.Serialize(w.State.Economy.Objects.Values.Where(o => o.Id != 401));
        Assert.Null(Unload(w, 200, 401));
        Assert.Equal(hashed, JsonSerializer.Serialize(w.State.Economy.Objects.Values.Where(o => o.Id != 401)));
    }

    // ---------------------------------------------------------------- what happens to troops when the ship goes (m3-fleets F19, DEFAULT)

    // m3-fleets F19 (the troops half): scrapping a loaded ship puts its regiments on the system, held or not
    [Fact]
    public void ScrappingALoadedShipLandsItsRegimentsOnTheSystem()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401, 402, 403));
        w.Scrap(Emp, 201);
        w.Run(1);
        Assert.False(w.E.Objects.ContainsKey(201));
        Assert.All(new[] { 401, 402, 403 }, id => { Assert.Equal((Home, 0), (w.Obj(id).System, w.Obj(id).AboardShip)); });
        var e = w.Events<RegimentsLanded>().Single();
        Assert.Equal(new[] { 401, 402, 403 }, e.Regiments.Items.ToArray());
        Assert.Equal((200, Home), (e.Fleet, e.System));
        Assert.Empty(w.State.Fleets); // the fleet's last ship is gone
    }

    // DEFAULT (m3-fleets Open question 6; the lane case is a DEFAULT the sheet lacks): a ship removed on the lane lands its regiments at the system left
    [Fact]
    public void AShipRemovedOnTheLaneLandsItsRegimentsAtTheLastLeftSystem()
    {
        var w = T();
        Assert.Null(Load(w, 200, 401, 402));
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        w.Run(1); // v6 re-pin 2026-10-24: the flight to Remedium takes 3 days at speed 80 (was 5), so check one day in, not two
        Assert.Null(w.State.Fleets[200].At);
        w.Scrap(Emp, 201); // ScrapObject on a flying ship
        w.Run(1);
        Assert.Equal(Home, w.Obj(401).System);
        Assert.Equal(Home, w.Obj(402).System);
        Assert.Equal(Home, w.Events<RegimentsLanded>().Single().System);
    }

    // ---------------------------------------------------------------- special forces (T19 to T21)

    // m3-troops T19
    [Fact]
    public void T19_ProbeDroidCompletesAtHook5AndIsNeverInAFleet()
    {
        var w = T(regiments: false);
        int load0 = w.Eco.Load(Emp);
        Assert.Null(w.TryOrder(Emp, Home, YardFamily.Training, "Speculators", 1));
        Assert.Equal(load0 + 1, w.Eco.Load(Emp));
        w.RunTo(8);
        var e = w.Events<SpecialForceCreated>().Single();
        Assert.Equal((5, Home, "Speculators"), (e.Day, e.System, e.Class));
        var o = w.Obj(e.Id);
        Assert.Equal((Home, 0, true), (o.System, o.AboardShip, o.Completed));
        Assert.Single(w.State.Fleets);
        Assert.Empty(w.Events<RegimentCreated>());
    }

    // m3-troops T20
    [Fact]
    public void T20_SpecialForcesNeverCountForTheGarrison()
    {
        var w = new BuildWorld(withSupport: true);
        w.Hold("Drift Harbour", Reb, 40); // N = 2
        w.Eco.AddObject(Reb, "Wreckers", "Drift Harbour", true);
        w.Eco.AddObject(Reb, "Wreckers", "Drift Harbour", true);
        Assert.Equal(2, w.Support!.RequiredGarrison("Drift Harbour"));
        w.Run(2);
        Assert.Equal(0, w.Support.Garrison10("Drift Harbour"));
        Assert.Equal(0, w.Support.RegimentCount("Drift Harbour"));
        Assert.True(w.Support.IsUprising("Drift Harbour")); // as for 0 regiments
    }

    // m3-troops T21 (B5a, 2026-10-17: the sheet's expectation changed): special forces cannot be scrapped (Classic code 23, B5a section 7);
    // both orders are refused with CannotScrap and nothing changes. The test keeps its name so the id stays findable.
    [Fact]
    public void T21_ScrapRefundsHalfOfCostRoundedDown()
    {
        var w = new BuildWorld();
        w.Hold(Home, Reb);
        int inf = w.Eco.AddObject(Reb, "Wreckers", Home, true);
        int gue = w.Eco.AddObject(Reb, "Strikers", Home, true);
        int load = w.Eco.Load(Reb), refined = w.Eco.Refined(Reb);
        w.Scrap(Reb, inf);
        w.Run(1);
        w.Scrap(Reb, gue);
        w.Run(1);
        Assert.Equal(new[] { RejectReason.CannotScrap, RejectReason.CannotScrap }, w.Events<OrderRejected>().Select(r => r.Reason).ToArray());
        Assert.Equal((refined, load), (w.Eco.Refined(Reb), w.Eco.Load(Reb)));
        Assert.Equal(new[] { inf, gue }, w.Eco.Objects.Select(o => o.Id).OrderBy(i => i).ToArray());
    }

    // m3-troops §What special forces do in M3: a bare owner change (no capture: this world has no control module) leaves a special force alone.
    // (B5a, 2026-10-17) A real capture destroys an old-side special force not on a mission: B5aControlTests B5a-11.
    [Fact]
    public void ASpecialForceOnACapturedSystemIsUntouched()
    {
        var w = T();
        int sf = w.Eco.AddObject(Emp, "Closers", Home, true);
        w.Hold(Home, Reb);
        w.Run(3);
        Assert.Equal((Home, 0, Emp), (w.Obj(sf).System, w.Obj(sf).AboardShip, w.Obj(sf).Side));
    }

    // ---------------------------------------------------------------- determinism (T22)

    private static (string State, string Events, long Draws) Scenario(ulong seed)
    {
        var w = new BuildWorld(seed);
        w.Hold(Home, Emp); w.Hold(Far, Emp);
        w.Yard(Home, FacilityKind.TrainingFacility, 500);
        w.Refined(Emp, 1000);
        w.Eco.AddObject(Emp, Isd, null, true, 201);
        w.Fleets.AddFleetOf(Emp, "Fleet", Home, new[] { 201 }, 200);
        for (int id = 401; id <= 403; id++) w.Eco.AddObject(Emp, Storm, Home, true, id);
        w.Order(Emp, Home, YardFamily.Training, Storm, 2, Far);
        w.Order(Emp, Home, YardFamily.Training, "Speculators", 1);
        w.Run(1);
        w.Troops.SubmitLoad(Emp, 200, new[] { 401, 402, 403 });
        w.Troops.SubmitLoad(Emp, 200, new[] { 401 }); // rejected
        w.Run(1);
        w.Fleets.SubmitMove(Emp, 200, Far);
        w.Run(30);
        w.Troops.SubmitUnload(Emp, 200, new[] { 401, 402 });
        w.Scrap(Emp, 201);
        w.Run(100);
        return (w.Game.State.Economy.Objects.Count + ":" + w.State.Fleets.Count + JsonSerializer.Serialize(w.State.Economy.Objects),
            JsonSerializer.Serialize(w.Game.Events.Select(e => e.GetType().Name + e.Day).ToList()), w.Game.Rng.Draws);
    }

    // m3-troops T22: the orders and events here draw nothing; two seeds give identical states and events
    [Fact]
    public void T22_NoRandomDrawsAndSeedsDoNotMatter()
    {
        var a = Scenario(11); var b = Scenario(22); var a2 = Scenario(11);
        Assert.Equal(0, a.Draws); Assert.Equal(0, b.Draws);
        Assert.Equal(a.State, b.State);
        Assert.Equal(a.Events, b.Events);
        Assert.Equal(a, a2);
    }

    // ---------------------------------------------------------------- the interface for the assault package

    [Fact]
    public void RegimentsAboardAtListsAttackersBySideAndSystemById()
    {
        var w = T();
        w.Eco.AddObject(Emp, Isd, null, true, 202);
        w.Fleets.AddFleetOf(Emp, "Other", Home, new[] { 202 }, 210);
        w.Eco.AddObject(Reb, "Militia", Home, true, 420);
        Assert.Null(Step(w, w.Troops.SubmitLoad(Emp, 210, new[] { 403 })));
        Assert.Null(Load(w, 200, 402, 401));
        var list = w.Troops.RegimentsAboardAt(Home, Emp);
        Assert.Equal(new[] { 401, 402, 403 }, list.Select(a => a.Regiment.Id).ToArray()); // by regiment id
        Assert.Equal(new[] { 200, 200, 210 }, list.Select(a => a.Fleet.Id).ToArray());
        Assert.Equal(new[] { 201, 201, 202 }, list.Select(a => a.Ship).ToArray());
        Assert.Empty(w.Troops.RegimentsAboardAt(Home, Reb));
        Assert.Empty(w.Troops.RegimentsAboardAt(Far, Emp));
        Assert.Equal(new[] { 420 }, w.Troops.RegimentsOnSystem(Home, Emp).Select(o => o.Id).ToArray()); // defenders of a Hand attack
        Assert.Empty(w.Troops.RegimentsOnSystem(Home, Reb)); // aboard ones never defend
        // a fleet in transit is not "at the system"
        Assert.Null(Step(w, w.Fleets.SubmitMove(Emp, 200, Far)));
        Assert.Equal(new[] { 403 }, w.Troops.RegimentsAboardAt(Home, Emp).Select(a => a.Regiment.Id).ToArray());
        // Land moves a regiment off its ship onto the system
        TroopModule.Land(w.Obj(403), Home);
        Assert.Equal((Home, 0), (w.Obj(403).System, w.Obj(403).AboardShip));
    }
}
