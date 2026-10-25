using Praedra.Core.Clock;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;

namespace Praedra.Core.Tests.Defenses;

// m3-bombardment, tests B1 to B22 (B23 is an M6 test, recorded in the sheet), then extra tests named after the rule they check.
// Dice are injected with the stub generator: per hit t, then r (Dice(t, r)); statistical tests are in BombardmentStatTests.
// Since B7 (B7-bombardment, Dev lead 2026-10-19) the module runs the Classic rules by default: a Military strike with hits first draws the collateral
// x = rand(0..m0) (Expect(0, m0, ...)), and a pick over one target draws nothing (rand(0..0), B7 §Rules). Tests of the m3 mechanics that B7 replaced
// run with the switches off (BombWorld.M3Rule or one switch); the B7 rules themselves are tested in B7BombardmentTests.
public class BombardmentTests
{
    private const string Sys = BombWorld.Sys;
    private static readonly Side Emp = Side.Hand, All = Side.Collective;
    private const BombardCategory Mil = BombardCategory.Military, Civ = BombardCategory.Civilian, Gen = BombardCategory.General;

    private static BombardmentStarted Started(BombWorld w) => Assert.Single(w.Events<BombardmentStarted>());

    // m3-bombardment B1
    [Theory]
    [InlineData(0, 1)] [InlineData(39, 1)] [InlineData(40, 2)] [InlineData(79, 2)] [InlineData(80, 3)] [InlineData(119, 3)] [InlineData(120, 4)]
    public void B1_AdmiralMultiplier(int leadership, int multiplier)
    {
        var w = new BombWorld();
        Assert.Equal(multiplier, w.Bomb.AdmiralMultiplier(leadership));
        int f = w.Fleet(Emp, Sys, "Dodger"); // base value 1
        w.Bomb.AdmiralLeadership = _ => leadership;
        Assert.Equal(multiplier, w.Bomb.Power(w.Game.State.Fleets[f]));
    }

    // m3-bombardment B2
    [Theory]
    [InlineData(0, 8)]
    [InlineData(80, 24)]
    public void B2_FourMandateAtLeadership(int leadership, int power)
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, BombWorld.Mandate, 4);
        w.Bomb.AdmiralLeadership = fl => fl.Id == f ? leadership : 0;
        Assert.Equal(power, w.Bomb.Power(w.Game.State.Fleets[f]));
        w.Stub.Expect(1, 10, 1); // B7-bombardment §5.3: no target (m0 = 0): the collateral die only
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(power, Started(w).P);
    }

    // m3-bombardment B3
    [Fact]
    public void B3_OneHarvesterIs15HitsAgainstNoShields()
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "harvester");
        w.Stub.Expect(1, 10, 1); // B7-bombardment §5.3: no target (m0 = 0): the collateral die only
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        var s = Started(w);
        Assert.Equal((15, 0, 15), (s.P, s.S, s.Hits));
    }

    // m3-bombardment B4: carrier and squadrons; the squadron counts once at the column's value
    [Theory]
    [InlineData("Plough", 14)]
    [InlineData("Needle", 2)]
    public void B4_LiberatorCarryingSixSquadrons(string squadron, int power)
    {
        var w = new BombWorld();
        int f = w.Fleets.AddFleet(All, "Lib", new[] { "Jubilee" }, Sys, Enumerable.Repeat(squadron, 6));
        Assert.Equal(power, w.Bomb.Power(w.Game.State.Fleets[f]));
    }

    // m3-bombardment B5 (hits = max(0, P - S)): the powers are built from ships and an injected admiral
    [Theory]
    [InlineData("3", "VD6", 0, 24, 40, 0)]                     // 6 Decrees (4 each) against Escrow I
    [InlineData("3,6", "HV4+V2", 40, 130, 120, 10)]            // (4 harvesters + a Victory II = 65) x 2 against Escrow I + II
    [InlineData("3,6", "HV3+V2", 40, 100, 120, 0)]             // (3 harvesters + a Victory II = 50) x 2
    public void B5_ShieldsAbsorbPower(string gens, string ships, int leadership, int power, int shield, int hits)
    {
        var w = new BombWorld();
        w.Hold(All);
        foreach (var c in gens.Split(',')) w.Facility(c == "3" ? FacilityKind.EscrowI : FacilityKind.EscrowII);
        var list = ships switch
        {
            "VD6" => Enumerable.Repeat("Decree", 6),
            "HV4+V2" => Enumerable.Repeat("harvester", 4).Append("Decree II"),
            _ => Enumerable.Repeat("harvester", 3).Append("Decree II"),
        };
        int fleet = w.Fleet(Emp, Sys, list.ToArray());
        w.Bomb.AdmiralLeadership = _ => leadership;
        w.Stub.Expect(0, 2, 1);                          // B7-bombardment §5.3: the collateral x over m0 = 2 generators: none
        w.Dice(Enumerable.Repeat((0, 1), 10).ToArray()); // every roll too low to destroy a generator (defense 2 and 3)
        Assert.Null(w.TryBombard(Emp, fleet, Sys, Mil));
        var s = Started(w);
        Assert.Equal((power, shield, hits), (s.P, s.S, s.Hits));
        Assert.Equal(hits, w.Events<TargetHit>().Count);
    }

    // m3-bombardment B6: for each bombardment defense d the destroy chance is exactly (10 - d) / 10: r in 1..10, destroyed when r > d
    [Fact]
    public void B6_DefenseValueTableDestroysExactlyTenMinusDOfTenRolls()
    {
        for (int d = 0; d <= 10; d++)
        {
            var w = new BombWorld(data: BombWorld.ArmyDefense(d));
            w.Hold(All);
            int f = w.Fleet(Emp, Sys, "Dodger"); // P = 1
            int destroyed = 0;
            for (int r = 1; r <= 10; r++)
            {
                int reg = w.Regiment(All, BombWorld.Army);
                w.Stub.Expect(0, 1, 1).Expect(1, 10, r); // collateral x = 1 (none); one target: no pick, then the die r
                Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
                if (w.Eco.Find(reg) == null) destroyed++; else w.Eco.RemoveObject(reg);
            }
            Assert.Equal(10 - d, destroyed);
        }
    }

    // m3-bombardment B7: the ten regiments, percent of the ten rolls
    [Fact]
    public void B7_TheTenRegimentsChances()
    {
        var expect = new (Side Side, string Class, int Percent)[]
        {
            (All, "Deckhands", 50), (All, "Militia", 50), (All, "Nhal Lookouts", 80), (All, "Kiru Sparks", 60),
            (All, "Ilmar Shade Guard", 10), (Emp, "Pacifiers", 40), (Emp, "Ballast", 80),
            (Emp, "Tranches", 50), (Emp, "Proxies", 80), (Emp, "Lictors", 40),
        };
        var w = new BombWorld();
        w.Hold(All);                                  // Fiducia: Collective regiments, bombed by the Hand
        w.Hold(Emp, 100, BombWorld.Imp);              // Charter: Hand regiments, bombed by the Collective
        int empFleet = w.Fleet(Emp, Sys, "Warrant");
        int allFleet = w.Fleet(All, BombWorld.Imp, "Dodger");
        foreach (var (side, cls, percent) in expect)
        {
            string system = side == All ? Sys : BombWorld.Imp;
            int destroyed = 0;
            for (int r = 1; r <= 10; r++)
            {
                int reg = w.Regiment(side, cls, system);
                w.Stub.Expect(0, 1, 1).Expect(1, 10, r); // collateral x = 1 (none); one target: no pick, then the die r
                Assert.Null(w.TryBombard(side == All ? Emp : All, side == All ? empFleet : allFleet, system, Mil));
                if (w.Eco.Find(reg) == null) destroyed++; else w.Eco.RemoveObject(reg);
            }
            Assert.Equal(percent, destroyed * 10);
        }
    }

    // m3-bombardment B11: P = 24 against one Escrow I (40): zero hits, zero draws, the events still come
    [Fact]
    public void B11_NoHitsNoDrawsButStillResolved()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Facility(FacilityKind.EscrowI); w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "Decree", 6);
        Assert.Null(w.TryBombard(Emp, f, Sys, Gen));
        Assert.Empty(w.Stub.Requests);
        var s = Started(w);
        Assert.Equal((24, 40, 0), (s.P, s.S, s.Hits));
        var r = Assert.Single(w.Events<BombardmentResolved>());
        Assert.Equal((0, 0, 0, 0, 0), (r.Hits, r.Regiments, r.Facilities, r.Energy, r.Raw));
        Assert.Empty(w.Events<TargetHit>());
    }

    // m3-bombardment B12: Military on a world with mines and Army regiments, 30 hits: no mine or energy touched, no support change
    [Fact]
    public void B12_MilitaryNeverTouchesMinesEnergyOrSupport()
    {
        var w = new BombWorld(stub: false, seed: 12);
        w.Hold(All, 45);
        w.Eco.SetSlots(Sys, 4, 2);
        int m1 = w.Eco.AddFacility(Sys, FacilityKind.Mine), m2 = w.Eco.AddFacility(Sys, FacilityKind.Mine);
        for (int i = 0; i < 4; i++) w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "harvester", 2); // 30 hits
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(30, Started(w).Hits);
        Assert.NotNull(w.Eco.Find(m1)); Assert.NotNull(w.Eco.Find(m2));
        Assert.Equal((4, 2), (w.Eco.SlotsOf(Sys).Energy, w.Eco.SlotsOf(Sys).Raw));
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Empty(w.Events<ResourceReduced>());
        Assert.Equal(0, w.Eco.Objects.Count(o => o.Category == ObjectCategory.Regiment)); // 30 hits at 50 percent clear four regiments
    }

    // m3-bombardment B13 (the S11 case of the support sheet): one civilian facility destroyed, one -20 to the bomber
    [Fact]
    public void B13_CivilianHitMovesSupportByMinus20()
    {
        var w = new BombWorld();
        w.Hold(All, 45);                                   // Collective 45, Hand 55
        w.Eco.SetSlots(Sys, 2, 1);
        int mine = w.Eco.AddFacility(Sys, FacilityKind.Mine);
        int f = w.Fleet(Emp, Sys, "Warrant"); // P = 1
        w.Stub.Expect(1, 10, 10);                          // Civilian pool [mine] (B7 C5: no Energy or Raw): no pick, a roll of 10
        Assert.Null(w.TryBombard(Emp, f, Sys, Civ));
        Assert.Null(w.Eco.Find(mine));
        var sc = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal((45, 65, SupportCause.Bombardment), (sc.Old, sc.New, sc.Cause)); // Hand 55 to 35
        Assert.Equal(35, w.Eco.SupportOf(Sys, Emp));
        var r = Assert.Single(w.Events<BombardmentResolved>());
        Assert.Equal((1, 0, 1, 0, 0), (r.Hits, r.Regiments, r.Facilities, r.Energy, r.Raw));
        Assert.Equal(3, w.Game.Events.Count(e => e is BombardmentStarted or TargetHit or FacilityDestroyed));
    }

    // m3-bombardment B14: three mines destroyed by a General bombardment: still one -20
    [Fact]
    public void B14_ThreeMinesStillOneSupportChange()
    {
        var w = new BombWorld();
        w.Hold(All, 45);
        w.Eco.SetSlots(Sys, 5, 3);
        for (int i = 0; i < 3; i++) w.Eco.AddFacility(Sys, FacilityKind.Mine);
        int f = w.Fleet(Emp, Sys, "Warrant", 3);
        w.Dice((0, 10), (0, 10), (0, 10));
        Assert.Null(w.TryBombard(Emp, f, Sys, Gen));
        Assert.Equal(3, w.Events<FacilityDestroyed>().Count(e => e.Reason == DestroyReason.Bombardment));
        var sc = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal((45, 65), (sc.Old, sc.New));
    }

    // m3-bombardment B15, corrected by B7-bombardment §6 and §9 (C5, C7): Energy hit only, now under General (Energy is no Civilian target):
    // energy lowered, the over-capacity check runs, and the -20 now applies (before B7: no support change)
    [Fact]
    public void B15_EnergyHitLowersEnergyAndCostsTheBomber20()
    {
        var w = new BombWorld();
        w.Hold(All, 45);
        w.Eco.SetSlots(Sys, 4, 1);
        int mine = w.Eco.AddFacility(Sys, FacilityKind.Mine);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Dice((1, 10));                                   // General pool [mine, Energy, Raw]: Energy
        Assert.Null(w.TryBombard(Emp, f, Sys, Gen));
        Assert.Equal(3, w.Eco.SlotsOf(Sys).Energy);
        Assert.NotNull(w.Eco.Find(mine));
        var sc = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal((45, 65, SupportCause.Bombardment), (sc.Old, sc.New, sc.Cause));
        Assert.Equal(new[] { "Energy" }, w.Events<ResourceReduced>().Select(e => e.Resource));
    }

    // m3-bombardment B15 (second half): energy falls below the installed facilities, so the slots sheet's pick draw runs; that collateral is not a
    // civilian hit of its own: one -20, from the energy loss (B7 C7), under General (B7 C5)
    [Fact]
    public void B15_EnergyHitRunsTheOverCapacityCheck()
    {
        var w = new BombWorld();
        w.Hold(All, 45);
        w.Eco.SetSlots(Sys, 2, 1);
        int mine = w.Eco.AddFacility(Sys, FacilityKind.Mine), yard = w.Eco.AddFacility(Sys, FacilityKind.ConstructionYard);
        int f = w.Fleet(Emp, Sys, "Warrant");
        // pool [mine, yard, Energy, Raw]: t = 2 (Energy), r = 10; then the over-capacity pick rand(0..1) = 0 destroys the mine
        var stub = w.Stub;
        stub.Expect(0, 3, 2).Expect(1, 10, 10).Expect(0, 1, 0);
        Assert.Null(w.TryBombard(Emp, f, Sys, Gen));
        Assert.Equal(1, w.Eco.SlotsOf(Sys).Energy);
        var d = Assert.Single(w.Events<FacilityDestroyed>());
        Assert.Equal((mine, DestroyReason.OverEnergy), (d.Facility, d.Reason));
        Assert.NotNull(w.Eco.Find(yard));
        Assert.Equal(1, stub.Count(0, 1));
        Assert.Single(w.Events<SupportChanged>());
    }

    // m3-bombardment B16: a neutral system with a yard: accepted, the yard falls, support moves, no capture
    [Fact]
    public void B16_NeutralSystemCanBeBombarded()
    {
        var w = new BombWorld();                           // Fiducia is neutral here
        w.Eco.SetSlots(Sys, 2, 0);
        int yard = w.Facility(FacilityKind.ConstructionYard);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(1, 10, 10);                          // Civilian pool [yard] (B7 C5): no pick, a roll of 10
        Assert.Null(w.TryBombard(Emp, f, Sys, Civ));
        Assert.Null(w.Eco.Find(yard));
        var sc = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal(sc.Old + 20, sc.New);                 // the Hand loses 20: the Collective share rises
        Assert.Null(w.Fleets.OwnerOf(Sys));
    }

    // m3-bombardment B17: the rejections
    [Fact]
    public void B17_Rejections()
    {
        var w = new BombWorld();
        w.Hold(All);
        int own = w.Fleet(All, Sys, "Dodger");                       // the holder's own fleet
        Assert.Equal(BombardReject.OwnSystem, w.TryBombard(All, own, Sys, Mil));

        w.Hold(Emp, 100, BombWorld.Imp);
        int fl = w.Fleet(Emp, Sys, "Decree");
        w.Fleets.SubmitMove(Emp, fl, BombWorld.Imp);                               // departs this Step
        w.Run(1);
        Assert.Equal(RejectReason.InTransit, w.TryBombard(Emp, fl, Sys, Mil));

        int a = w.Fleet(Emp, Sys, "Decree");                           // the Collective fleet is at Fiducia too
        Assert.Equal(BombardReject.EnemyFleetPresent, w.TryBombard(Emp, a, Sys, Mil));

        var w2 = new BombWorld();
        w2.Hold(All);
        int zero = w2.Fleet(Emp, Sys, "Mule");                        // bombardment modifier 0
        Assert.Equal(BombardReject.NoBombardPower, w2.TryBombard(Emp, zero, Sys, Mil));
        int dsFleet = w2.Fleet(Emp, Sys, "harvester");                           // power 15, but Destroy System is not available in M3
        Assert.Equal(RejectReason.NotAvailable, w2.TryBombard(Emp, dsFleet, Sys, BombardCategory.DestroySystem));
    }

    // m3-bombardment §Order and preconditions check 1 (the other codes) and 6 (OrderPending)
    [Fact]
    public void FleetCodesAndOrderPending()
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "Decree");
        Assert.Equal(RejectReason.UnknownFleet, w.TryBombard(Emp, 9999, Sys, Mil));
        Assert.Equal(RejectReason.NotYours, w.TryBombard(All, f, Sys, Mil));
        Assert.Equal(BombardReject.NotAtSystem, w.TryBombard(Emp, f, BombWorld.Other, Mil));
        w.Stub.Expect(1, 10, 1); // B7-bombardment §5.3: no target: the collateral die only
        long first = w.Bombard(Emp, f, Sys, Mil), second = w.Bombard(Emp, f, Sys, Gen);
        w.Run(1);
        Assert.Null(w.RejectionOf(first));
        Assert.Equal(BombardReject.OrderPending, w.RejectionOf(second));
        Assert.Single(w.Events<BombardmentStarted>());
        Assert.Equal(f, w.Events<OrderAccepted>().Last().Fleet);
    }

    // m3-bombardment B18: a garrison of three loses two to a bombardment: slot 6.1 of the same Step starts the uprising with one arm draw
    [Fact]
    public void B18_DeadRegimentsStartAnUprisingTheSameStep()
    {
        var w = new BombWorld();
        w.Hold(All, 35);                                   // N = ceil(25 / 10) = 3
        Assert.Equal(3, w.S.Sup.RequiredGarrison(Sys));
        for (int i = 0; i < 3; i++) w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "Warrant", 2);
        w.Stub.Expect(0, 3, 1);                            // B7-bombardment §5.3: the collateral x over m0 = 3: none
        w.Dice((0, 10), (0, 10));
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(1, w.Eco.Objects.Count(o => o.Category == ObjectCategory.Regiment));
        Assert.Equal(2, w.Events<RegimentDestroyed>().Count(e => e.Reason == "Bombardment"));
        Assert.Single(w.Events<UprisingStarted>());
        Assert.Equal(1, w.Stub.Count(0, w.S.Sup.Params.IncidentIntervalSpread)); // the one arm draw of 6.1, after every bombardment draw
        var kinds = w.Game.Events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf(nameof(BombardmentResolved)) < kinds.IndexOf(nameof(UprisingStarted)));
    }

    // m3-bombardment B19: a shield destroyed at Step K lowers S at K + 1, and with one Escrow left the gate is open
    [Fact]
    public void B19_ShieldDestroyedLowersSNextStep()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Facility(FacilityKind.EscrowI); w.Facility(FacilityKind.EscrowI);
        int f = w.Fleet(Emp, Sys, new[] { "harvester", "harvester", "harvester", "harvester", "harvester", "Decree II", "Warrant", "Warrant" }); // P = 82
        w.Stub.Expect(0, 2, 1);  // B7-bombardment §5.3: the collateral x over m0 = 2: none
        w.Dice((0, 10), (0, 1)); // the first shield falls (10 > 2), the second survives its roll (1)
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        var first = Assert.Single(w.Events<BombardmentStarted>());
        Assert.Equal((82, 80, 2), (first.P, first.S, first.Hits));
        Assert.False(w.Def.AssaultForbidden(Sys));
        w.Stub.Expect(0, 1, 1).Expect(1, 10, Enumerable.Repeat(1, 42).ToArray()); // m0 = 1: collateral x = 1 (none), no picks, 42 dice
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(40, w.Events<BombardmentStarted>().Last().S);
    }

    // m3-bombardment B20: an enemy fleet arrives before 5.1: the order is cancelled, no draw
    [Fact]
    public void B20_EnemyFleetArrivingCancelsTheOrder()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "Decree");
        bool arrived = false;
        w.Game.RegisterHook("5", _ => { if (!arrived) { arrived = true; w.Fleet(All, Sys, "Dodger"); } }); // before 5.1
        long seq = w.Bombard(Emp, f, Sys, Mil);
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
        var c = Assert.Single(w.Events<BombardCancelled>());
        Assert.Equal((f, Sys, BombardReject.EnemyFleetPresent), (c.Fleet, c.System, c.Reason));
        Assert.Empty(w.Events<BombardmentStarted>());
        Assert.Empty(w.Stub.Requests);
    }

    // m3-bombardment B21: same seed and orders twice: identical draw log, events and state hash
    [Fact]
    public void B21_SameSeedSameResult()
    {
        BombWorld Run()
        {
            var w = new BombWorld(stub: false, seed: 77);
            w.Hold(All, 45);
            w.Eco.SetSlots(Sys, 4, 2);
            w.Eco.AddFacility(Sys, FacilityKind.Mine); w.Eco.AddFacility(Sys, FacilityKind.Refinery); w.Facility(FacilityKind.Lien150);
            for (int i = 0; i < 5; i++) w.Regiment(All, BombWorld.Army);
            int f = w.Fleet(Emp, Sys, "Decree", 5);
            w.TryBombard(Emp, f, Sys, Gen);
            return w;
        }
        var a = Run(); var b = Run();
        Assert.Equal(a.Game.Rng.Export(), b.Game.Rng.Export());
        Assert.True(a.Game.Rng.Draws >= 2);
        Assert.Equal(a.Game.Events.Count, b.Game.Events.Count);
        Assert.True(a.Game.Events.SequenceEqual(b.Game.Events));
        Assert.Equal(GameSave.StateHash(a.Game), GameSave.StateHash(b.Game));
        Assert.Equal(a.Events<TargetHit>().Select(h => (h.TargetKind, h.Id, h.Roll)), b.Events<TargetHit>().Select(h => (h.TargetKind, h.Id, h.Roll)));
    }

    // m3-bombardment B22: a facility list with a battery: BatteryReturnFire is emitted, ships unchanged. Since B7 this is the BatteryReturnFire-off
    // path (B7 switches off); with the DEFAULT the batteries damage ships (B7-bombardment §4, B7BombardmentTests)
    [Fact]
    public void B22_BatteryReturnFireIsOnlyAHook()
    {
        var w = new BombWorld(bp: BombWorld.M3Rule);
        w.Hold(All);
        int k = w.Facility(FacilityKind.Lien150); w.Facility(FacilityKind.EscrowI);
        int f = w.Fleet(Emp, Sys, "Decree", 11);                 // P = 44, S = 40, 4 hits
        var before = string.Join(";", w.Eco.Objects.Where(o => o.Category == ObjectCategory.CapitalShip).Select(o => $"{o.Hull}:{string.Join(",", o.Hits!)}"));
        w.Dice(Enumerable.Repeat((0, 1), 4).ToArray());
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(new[] { k }, Assert.Single(w.Events<BatteryReturnFire>()).Facilities.Items);
        Assert.Equal(before, string.Join(";", w.Eco.Objects.Where(o => o.Category == ObjectCategory.CapitalShip).Select(o => $"{o.Hull}:{string.Join(",", o.Hits!)}")));
    }

    // ---------------------------------------------------------------- extra tests

    // m3-bombardment §Draw order, B7-bombardment §8: the collateral x = rand(0..m), then per hit t over the pool of size m (0..m-1), then r (1..10)
    [Fact]
    public void DrawOrderIsTThenRAndTheRangesAreThePoolAndTheDie()
    {
        var w = new BombWorld();
        w.Hold(All);
        for (int i = 0; i < 3; i++) w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "Warrant", 2); // 2 hits
        w.Stub.Expect(0, 3, 1);
        w.Dice((2, 1), (0, 1));
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(new[] { (0, 3), (0, 2), (1, 10), (0, 2), (1, 10) }, w.Stub.Requests.Take(5)); // survivors stay: the pool does not shrink
    }

    // m3-bombardment §Resolution 3, B7-bombardment §5.4.4: the targets are recounted after every destroyed target; none left stops the strike without draws
    [Fact]
    public void DestroyedTargetsLeaveThePoolAndAnEmptyPoolStopsTheDraws()
    {
        var w = new BombWorld();
        w.Hold(All);
        int reg = w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "Decree", 3); // 12 hits
        w.Stub.Expect(0, 1, 1).Expect(1, 10, 10);
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Null(w.Eco.Find(reg));
        Assert.Equal(new[] { (0, 1), (1, 10) }, w.Stub.Requests); // the collateral x, then one r (m0 = 1: no pick): the other eleven hits were discarded
        Assert.Equal(12, w.Events<BombardmentResolved>().Single().Hits);
        Assert.Equal(1, w.Events<BombardmentResolved>().Single().Regiments);
    }

    // m3-bombardment §Resolution 3: Military pool = ground regiments of the holder, then completed defenses; not aboard units, not unfinished
    [Fact]
    public void MilitaryPoolContents()
    {
        var w = new BombWorld(bp: new BombardParams { BatteryReturnFire = false }); // the Covenant II would destroy the Warrant (B7 §4): pools only here
        w.Hold(All);
        w.Regiment(All, BombWorld.Army);
        w.Facility(FacilityKind.Lien150, completed: false);                          // unfinished: not a target
        w.Facility(FacilityKind.ConstructionYard);                               // civilian: not in Military
        int aboard = w.Regiment(All, BombWorld.Army); w.Eco.Find(aboard)!.AboardShip = 1;                                      // aboard a ship: not a target
        int k = w.Facility(FacilityKind.CovenantII);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(0, 2, 1);                                                  // B7-bombardment §5.3: collateral x over m0 = 2: none
        w.Dice((1, 1));
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        var hit = Assert.Single(w.Events<TargetHit>());
        Assert.Equal(("Facility", k, 3), (hit.TargetKind, hit.Id, hit.Defense)); // pool = [regiment, Covenant II]: index 1
        Assert.Equal(new[] { (0, 2), (0, 1) }, w.Stub.Requests.Take(2));
    }

    // m3-bombardment §Resolution 3 as corrected by B7-bombardment §1 and §3 (C5): Civilian pool = non-defense completed facilities only; General =
    // Military, then Civilian, then Energy and Raw while above 0. The Lien's return fire is switched off: pools only here
    [Fact]
    public void CivilianAndGeneralPools()
    {
        var off = new BombardParams { BatteryReturnFire = false };
        var w = new BombWorld(bp: off);
        w.Hold(All);
        w.Eco.SetSlots(Sys, 4, 0);                                               // Raw is 0: not in the pool
        w.Regiment(All, BombWorld.Army); w.Facility(FacilityKind.Lien150); w.Facility(FacilityKind.ConstructionYard); w.Facility(FacilityKind.Refinery);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Dice((0, 1));
        w.TryBombard(Emp, f, Sys, Civ);
        Assert.Equal((0, 1), w.Stub.Requests[0]);                                // [yard, refinery]: 2 entries (before B7 also Energy: 3)
        var g = new BombWorld(bp: off);
        g.Hold(All);
        g.Eco.SetSlots(Sys, 4, 1);
        g.Regiment(All, BombWorld.Army); g.Facility(FacilityKind.Lien150); g.Facility(FacilityKind.ConstructionYard);
        int f2 = g.Fleet(Emp, Sys, "Warrant");
        g.Dice((0, 1));
        g.TryBombard(Emp, f2, Sys, Gen);
        Assert.Equal((0, 4), g.Stub.Requests[0]);                                // regiment, Lien, yard, Energy, Raw
    }

    // m3-bombardment §Resolution 3: with no Energy and no Raw and no facility the Civilian pool is empty
    [Fact]
    public void EmptyCivilianPoolDrawsNothing()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Eco.SetSlots(Sys, 0, 0);
        w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "harvester");
        Assert.Null(w.TryBombard(Emp, f, Sys, Civ));
        Assert.Empty(w.Stub.Requests);
        Assert.Equal(15, w.Events<BombardmentResolved>().Single().Hits);
    }

    // m3-bombardment §Resolution 4: a defense facility (a Mordant Covenant I, defense 4) is a Military target; a destroyed defense frees its slot
    [Fact]
    public void DefenseFacilitiesAreMilitaryTargets()
    {
        var w = new BombWorld(bp: new BombardParams { BatteryReturnFire = false }); // a battery would fire back first (B7 C3); this test checks the target only
        w.Hold(Emp, 100);
        int dss = w.Facility(FacilityKind.CovenantI);
        int used = w.Eco.EnergyUsed(Sys);
        int f = w.Fleet(All, Sys, "Dodger");
        w.Stub.Expect(0, 1, 1).Expect(1, 10, 5);                                 // collateral x = 1 (none); one target: no pick, then the die 5
        Assert.Null(w.TryBombard(All, f, Sys, Mil));
        Assert.Null(w.Eco.Find(dss));
        Assert.Equal(used - 1, w.Eco.EnergyUsed(Sys));
        Assert.Equal(4, w.Events<TargetHit>().Single().Defense);
        Assert.Empty(w.Events<SupportChanged>());                                // a defense facility is not a civilian one
    }

    // m3-bombardment §Resolution 3 (DEFAULT: unfinished facilities are not targets)
    [Fact]
    public void UnfinishedFacilitiesAreNeverTargets()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Eco.SetSlots(Sys, 0, 0);
        w.Facility(FacilityKind.ConstructionYard, completed: false);
        int f = w.Fleet(Emp, Sys, "Warrant");
        Assert.Null(w.TryBombard(Emp, f, Sys, Civ));
        Assert.Empty(w.Stub.Requests);
    }

    // m3-bombardment §Resolution 4: Energy and Raw have defense 9 (only r = 10 destroys), the destroyed raw never goes below 0
    [Fact]
    public void RawEntryHasDefenseNineAndLowersRaw()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Eco.SetSlots(Sys, 2, 1);
        int f = w.Fleet(Emp, Sys, "Warrant", 2);                   // General pool [Energy, Raw] (B7 C5: no longer Civilian targets)
        w.Dice((1, 9), (1, 10));
        Assert.Null(w.TryBombard(Emp, f, Sys, Gen));
        Assert.Equal(new[] { 9, 9 }, w.Events<TargetHit>().Select(h => h.Defense));
        Assert.Equal((false, true), (w.Events<TargetHit>()[0].Destroyed, w.Events<TargetHit>()[1].Destroyed));
        Assert.Equal((2, 0), (w.Eco.SlotsOf(Sys).Energy, w.Eco.SlotsOf(Sys).Raw));
        Assert.Equal(new[] { "Raw" }, w.Events<ResourceReduced>().Select(e => e.Resource));
    }

    // m3-bombardment §Where it runs: resolved in system name order, then fleet id; events carry day K + 1 (BombardFleetsTogether off: one strike per
    // fleet, the m3 rule)
    [Fact]
    public void ResolutionOrderIsSystemNameThenFleetId()
    {
        var w = new BombWorld(bp: new BombardParams { BombardFleetsTogether = false, MilitaryCollateral = false });
        w.Hold(All); w.Hold(All, 100, BombWorld.Other);
        int a = w.Fleet(Emp, Sys, "Warrant");
        int b = w.Fleet(Emp, BombWorld.Other, "Warrant");
        int c = w.Fleet(Emp, Sys, "Warrant"); // a second fleet at Fiducia, higher id than a
        // submitted in the "wrong" order: c and a at Fiducia, then b at Drift Harbour; resolved Drift Harbour (b), Fiducia (a), Fiducia (c)
        w.Bombard(Emp, c, Sys, Mil); w.Bombard(Emp, a, Sys, Mil); w.Bombard(Emp, b, BombWorld.Other, Mil);
        w.Run(1);
        var started = w.Events<BombardmentStarted>();
        Assert.Equal(new[] { b, a, c }, started.Select(e => e.Fleet));
        Assert.Equal(new[] { BombWorld.Other, Sys, Sys }, started.Select(e => e.System));
        Assert.All(started, e => Assert.Equal(1, e.Day));
    }

    // m3-bombardment §Where it runs with B7-bombardment §1 (C3, DEFAULT): system name order; the two fleets at Fiducia strike once together, started by
    // the lower fleet id
    [Fact]
    public void ResolutionOrderWithFleetsTogether()
    {
        var w = new BombWorld(bp: new BombardParams { MilitaryCollateral = false });
        w.Hold(All); w.Hold(All, 100, BombWorld.Other);
        int a = w.Fleet(Emp, Sys, "Warrant");
        int b = w.Fleet(Emp, BombWorld.Other, "Warrant");
        int c = w.Fleet(Emp, Sys, "Warrant");
        w.Bombard(Emp, c, Sys, Mil); w.Bombard(Emp, a, Sys, Mil); w.Bombard(Emp, b, BombWorld.Other, Mil);
        w.Run(1);
        var started = w.Events<BombardmentStarted>();
        Assert.Equal(new[] { b, a }, started.Select(e => e.Fleet));
        Assert.Equal(new[] { BombWorld.Other, Sys }, started.Select(e => e.System));
        Assert.Equal(new[] { a, c }, started[1].Fleets.Items);
        Assert.Equal(2, started[1].P);
        Assert.All(started, e => Assert.Equal(1, e.Day));
    }

    // m3-bombardment §Order and preconditions check 6 / step 7: one-shot; the same fleet can be ordered again next Step
    [Fact]
    public void OrderIsOneShot()
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(1, 10, 1, 1); // B7-bombardment §5.3: no target: one collateral die per strike
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        w.Run(2);
        Assert.Single(w.Events<BombardmentStarted>());
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(2, w.Events<BombardmentStarted>().Count);
    }

    // m3-bombardment §Order and preconditions: every check runs again at 5.1: a fleet ordered away is cancelled InTransit
    [Fact]
    public void FleetThatLeavesBeforeResolutionIsCancelled()
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "Decree");
        w.Bombard(Emp, f, Sys, Mil);
        w.Fleets.SubmitMove(Emp, f, BombWorld.Imp);
        w.Run(1);
        Assert.Equal(RejectReason.InTransit, Assert.Single(w.Events<BombardCancelled>()).Reason);
        Assert.Empty(w.Events<BombardmentStarted>());
    }

    // m3-bombardment §Resolution 1: the power of a fleet counts squadrons aboard only; the multiplier uses the injected admiral
    [Fact]
    public void ShipAndFighterValuesComeFromTheData()
    {
        var d = Praedra.Core.Setup.GameData.Default.Ships;
        Assert.Equal(new[] { 15, 4, 5, 2, 1, 0, 0 }, new[] { "harvester", "Barricade", "Decree II", "Mandate",
            "Dodger", "Mule", "Tender" }.Select(n => d[n].Bombardment));
        Assert.Equal(new[] { 2, 2, 2, 1, 1, 0 }, new[] { "Plough", "Maul", "Thresher", "Swift", "Winnower", "Needle" }.Select(n => d[n].Bombardment));
    }

    // m3-bombardment §Numbers: the order is a polymorphic order that saves and loads
    [Fact]
    public void BombardOrderSurvivesASaveBetweenSteps()
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Bombard(Emp, f, Sys, Civ);
        var loaded = GameSave.Load(GameSave.Save(w.Game));
        var order = Assert.IsType<BombardOrder>(Assert.Single(loaded.Queue));
        Assert.Equal((f, Sys, Civ), (order.Fleet, order.System, order.Category));
    }
}
