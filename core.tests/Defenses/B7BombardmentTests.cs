using Praedra.Core.Clock;
using Praedra.Core.Data;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Support;

namespace Praedra.Core.Tests.Defenses;

// B7-bombardment (the Classic bombardment): QA checks Q74 to Q83, then the switches C1 to C9 and the rules of sections 3 to 8 by name.
// Stub dice are given by range (Expect) where draws of several kinds interleave; statistical tests use the seeded game generator. The return-fire
// tests order Civilian on a world with no civilian facility, so no hit draws anything and only the return fire is seen.
public class B7BombardmentTests
{
    private const string Sys = BombWorld.Sys;
    private static readonly Side Emp = Side.Empire, All = Side.Alliance;
    private const BombardCategory Mil = BombardCategory.Military, Civ = BombardCategory.Civilian, Gen = BombardCategory.General;
    private const string Neb = "Mender", ImpArmy = "Tranches";

    private static EconObject Ship(BombWorld w, int fleet, int index = 0) => w.Eco.Find(w.Game.State.Fleets[fleet].Ships[index])!;

    // B7-bombardment Q74: one LNR I, no General, against one Nebulon-B (shield 300, hull 600): 100 hull left; two LNR I destroy it and, as it was
    // the only ship, nothing is bombed
    [Fact]
    public void Q74_LnrShotDamagesHullAndTwoDestroyTheOnlyShip()
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.LnrSeriesI);
        int reg = w.Regiment(Emp, ImpArmy);
        int f = w.Fleet(All, Sys, Neb);
        w.Stub.Expect(0, 50, 0, 0);                // the critical roll (0 + pct(500, 600) = 83: a hit) and the CSCRHTTB roll 0 (shield recharge)
        w.Stub.Expect(0, 2, 1);                    // collateral x over m0 = 2 (the regiment and the LNR): none
        w.Stub.Expect(0, 1, 0);                    // the pick: the regiment
        w.Stub.Expect(1, 10, 1);                   // the hit's die: the regiment survives
        Assert.Null(w.TryBombard(All, f, Sys, Mil));
        var ship = Ship(w, f);
        Assert.Equal(100, ship.Hull);
        var d = Assert.Single(w.Events<BombardShipDamaged>());
        Assert.Equal((800, 300, 500, false, 0), (d.Turbolaser, d.Shield, d.HullLost, d.Destroyed, d.Critical));
        Assert.Equal(1, ship.Hits![0]);
        Assert.Equal(1, w.Events<BombardmentStarted>().Single().Hits); // C4: 1 - 1 x 500 / 600 = 1 - 0 (truncating)
        Assert.NotNull(w.Eco.Find(reg));

        var w2 = new BombWorld();
        w2.Hold(Emp);
        w2.Facility(FacilityKind.LnrSeriesI); w2.Facility(FacilityKind.LnrSeriesI);
        int reg2 = w2.Regiment(Emp, ImpArmy);
        int f2 = w2.Fleet(All, Sys, Neb);
        int shipId = w2.Game.State.Fleets[f2].Ships[0];
        Assert.Null(w2.TryBombard(All, f2, Sys, Mil));
        Assert.Null(w2.Eco.Find(shipId));
        Assert.True(Assert.Single(w2.Events<BombardShipDamaged>()).Destroyed);
        var s = Assert.Single(w2.Events<BombardmentStarted>());
        Assert.Equal((0, 0), (s.P, s.Hits));
        Assert.Empty(w2.Events<TargetHit>());
        Assert.NotNull(w2.Eco.Find(reg2));
        Assert.Empty(w2.Stub.Requests);           // one ship: no target draw; destroyed: no critical roll; no hits: no collateral
    }

    // B7-bombardment Q75: one Mordant Lien-150 against a Nebulon-B carrying 2 Maul squadrons: the frigate's 1 does not count; P = 4 (the B-wings) x admiral
    [Theory]
    [InlineData(0, 4)]
    [InlineData(40, 8)]
    public void Q75_KdyGroundsTheShipButNotItsFighters(int leadership, int power)
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.Kdy150);
        int f = w.Fleets.AddFleet(All, "Neb", new[] { Neb }, Sys, new[] { "Maul", "Maul" });
        w.Bomb.AdmiralLeadership = _ => leadership;
        Assert.Equal(1 * (leadership / 40 + 1) + power, w.Bomb.Power(w.Game.State.Fleets[f])); // outside a strike the frigate counts
        Assert.Null(w.TryBombard(All, f, Sys, Civ)); // Civilian on a world with no civilian facility: no hit draws anything
        var d = Assert.Single(w.Events<BombardShipDamaged>());
        Assert.Equal((2000, 300, 0, true), (d.Ion, d.Shield, d.HullLost, d.Grounded));
        Assert.Equal(power, w.Events<BombardmentStarted>().Single().P);
        Assert.Equal(600, Ship(w, f).Hull);       // a KDY shot only drains shields
    }

    // B7-bombardment Q76: two regiments of defence 0, 2 hits, Military: mean kills 1.50 (the second hit is wasted half the time); the m3 rule 2.00
    [Theory]
    [InlineData(true, 1.50)]
    [InlineData(false, 2.00)]
    public void Q76_TwoHitsOnTwoSureKills(bool wasted, double mean)
    {
        var w = new BombWorld(stub: false, seed: 76, data: BombWorld.ArmyDefense(0), bp: new BombardParams { BombardWastedHits = wasted });
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "Warrant", 2); // P = 2
        long killed = 0;
        const int trials = 20_000;
        for (int i = 0; i < trials; i++)
        {
            w.Regiment(All, BombWorld.Army); w.Regiment(All, BombWorld.Army);
            w.Bombard(Emp, f, Sys, Mil);
            w.Run(1);
            var left = w.Eco.Objects.Where(o => o.Category == ObjectCategory.Regiment).ToList();
            killed += 2 - left.Count;
            foreach (var o in left) w.Eco.RemoveObject(o.Id);
        }
        Assert.True(Math.Abs((double)killed / trials - mean) <= 0.02, $"mean {(double)killed / trials:F3}, expected {mean}");
    }

    // B7-bombardment Q76 (exact): first hit kills target 0; the second pick t = 1 points past the one regiment left: wasted, no die
    [Fact]
    public void Q76_ASecondPickPastTheStandingTargetsIsWasted()
    {
        var w = new BombWorld(data: BombWorld.ArmyDefense(0));
        w.Hold(All);
        int a = w.Regiment(All, BombWorld.Army), b = w.Regiment(All, BombWorld.Army);
        int f = w.Fleet(Emp, Sys, "Warrant", 2);
        w.Stub.Expect(0, 2, 1);                   // collateral x = 1: none
        w.Stub.Expect(0, 1, 0, 1);                // t = 0 (a dies), then t = 1 with one standing: wasted
        w.Stub.Expect(1, 10, 1);
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Null(w.Eco.Find(a)); Assert.NotNull(w.Eco.Find(b));
        var wasted = Assert.Single(w.Events<HitWasted>());
        Assert.Equal((1, 1), (wasted.Pick, wasted.Standing));
        Assert.Equal(new[] { (0, 2), (0, 1), (1, 10), (0, 1) }, w.Stub.Requests);
    }

    // B7-bombardment Q77: two Alliance fleets of base 30, both ordered, one GenCore I: one strike, 20 hits; the m3 rule (C3 off) 0 and 0
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Q77_FleetsStrikeTogetherAndShieldsSubtractOnce(bool together)
    {
        var w = new BombWorld(stub: false, seed: 77, bp: new BombardParams { BombardFleetsTogether = together });
        w.Hold(Emp);
        w.Facility(FacilityKind.GenCoreLevelI);
        var ships = Enumerable.Repeat("Barricade", 7).Concat(Enumerable.Repeat("Dodger", 2)).ToArray(); // 7 x 4 + 2 x 1 = 30
        int a = w.Fleet(All, Sys, ships), b = w.Fleet(All, Sys, ships);
        w.Bombard(All, a, Sys, Mil); w.Bombard(All, b, Sys, Mil);
        w.Run(1);
        var started = w.Events<BombardmentStarted>();
        if (together)
        {
            var s = Assert.Single(started);
            Assert.Equal((60, 40, 20, a), (s.P, s.S, s.Hits, s.Fleet));
            Assert.Equal(new[] { a, b }, s.Fleets.Items);
        }
        else
        {
            Assert.Equal(2, started.Count);
            Assert.All(started, s => Assert.Equal((30, 40, 0), (s.P, s.S, s.Hits)));
        }
    }

    // B7-bombardment §5.1 (C3): each fleet of the strike is multiplied by its own Admiral
    [Fact]
    public void FleetsTogetherEachKeepsItsOwnAdmiral()
    {
        var w = new BombWorld(stub: false, seed: 5);
        w.Hold(All);
        int a = w.Fleet(Emp, Sys, "Decree"), b = w.Fleet(Emp, Sys, "Decree"); // 4 each
        w.Bomb.AdmiralLeadership = fl => fl.Id == b ? 80 : 0;                                      // b: x3
        w.Bombard(Emp, b, Sys, Gen); w.Bombard(Emp, a, Sys, Mil);
        w.Run(1);
        var s = Assert.Single(w.Events<BombardmentStarted>());
        Assert.Equal((16, a, Mil), (s.P, s.Fleet, s.Category)); // 4 + 12; the lowest fleet id starts the strike and gives its category
        Assert.Equal(new[] { a, b }, s.Fleets.Items);
    }

    // B7-bombardment Q78: Civilian on a world with mines and energy: mines can die, energy and raw never change (C5)
    [Fact]
    public void Q78_CivilianHitsBuildingsOnly()
    {
        var w = new BombWorld(stub: false, seed: 78);
        w.Hold(All, 45);
        w.Eco.SetSlots(Sys, 6, 4);
        for (int i = 0; i < 3; i++) w.Eco.AddFacility(Sys, FacilityKind.Mine);
        int f = w.Fleet(Emp, Sys, "Death Star", 2); // 30 hits
        Assert.Null(w.TryBombard(Emp, f, Sys, Civ));
        Assert.Equal((6, 4), (w.Eco.SlotsOf(Sys).Energy, w.Eco.SlotsOf(Sys).Raw));
        Assert.Empty(w.Events<ResourceReduced>());
        Assert.All(w.Events<TargetHit>(), h => Assert.Equal("Facility", h.TargetKind));
        Assert.Equal(3, w.Events<FacilityDestroyed>().Count(e => e.Reason == DestroyReason.Bombardment)); // 30 hits at 50 percent clear three mines
    }

    // B7-bombardment Q79: General, only an energy entry destroyed: -20 to the bomber (C7)
    [Fact]
    public void Q79_EnergyLossCostsTheBomber20()
    {
        var w = new BombWorld();
        w.Hold(All, 45);
        w.Eco.SetSlots(Sys, 4, 0);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(1, 10, 10);                 // pool [Energy]: m0 = 1, no pick draw; the die 10 beats 9
        Assert.Null(w.TryBombard(Emp, f, Sys, Gen));
        Assert.Equal(3, w.Eco.SlotsOf(Sys).Energy);
        var sc = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal((45, 65, SupportCause.Bombardment), (sc.Old, sc.New, sc.Cause));
    }

    // B7-bombardment Q80: Military, 1 regiment and 1 mine (defence 5), 1 hit: the mine is destroyed in about 25 percent of strikes, with -20 when it is
    [Fact]
    public void Q80_MilitaryCollateralOnTheMine()
    {
        var w = new BombWorld(stub: false, seed: 80);
        w.Hold(All, 70);                          // no garrison needed (60 or more), and the bomber's 30 can lose 20
        w.Eco.SetSlots(Sys, 4, 4);
        int f = w.Fleet(Emp, Sys, "Warrant"); // 1 hit
        int lost = 0, penalties = 0;
        const int trials = 10_000;
        for (int i = 0; i < trials; i++)
        {
            w.Eco.SetAllianceSupport(Sys, 70);
            int reg = w.Regiment(All, BombWorld.Army);
            int mine = w.Eco.AddFacility(Sys, FacilityKind.Mine);
            int before = w.Events<SupportChanged>().Count(e => e.Cause == SupportCause.Bombardment);
            w.Bombard(Emp, f, Sys, Mil);
            w.Run(1);
            bool gone = w.Eco.Find(mine) == null;
            if (gone) lost++;
            if (w.Events<SupportChanged>().Count(e => e.Cause == SupportCause.Bombardment) > before) penalties++;
            if (!gone) w.Eco.RemoveObject(mine);
            if (w.Eco.Find(reg) != null) w.Eco.RemoveObject(reg);
        }
        double rate = 100.0 * lost / trials;
        Assert.True(Math.Abs(rate - 25.0) <= 1.5, $"mine lost in {rate:F2} percent");
        Assert.Equal(lost, penalties);            // -20 exactly when the mine fell
    }

    // B7-bombardment Q80 (exact) and §5.3: x = 0, the pick over two civilian facilities is drawn and ignored (the Classic bug: the first one is rolled),
    // the roll uses up no hit, and the -20 applies
    [Fact]
    public void Q80_CollateralAlwaysRollsTheFirstCivilianFacility()
    {
        var w = new BombWorld();
        w.Hold(All, 70);
        w.Eco.SetSlots(Sys, 4, 4);
        int reg = w.Regiment(All, BombWorld.Army);
        int mine = w.Eco.AddFacility(Sys, FacilityKind.Mine), refinery = w.Eco.AddFacility(Sys, FacilityKind.Refinery);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(0, 1, 0, 1);                // x = 0 (collateral); then the pick over [mine, refinery] = 1 (the refinery), ignored
        w.Stub.Expect(1, 10, 6, 1);               // collateral die 6 > 5: the mine (not the refinery) falls; the hit's die 1
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil));
        Assert.Null(w.Eco.Find(mine)); Assert.NotNull(w.Eco.Find(refinery)); Assert.NotNull(w.Eco.Find(reg));
        var c = Assert.Single(w.Events<CollateralHit>());
        Assert.Equal((mine, 5, 6, true), (c.Facility, c.Defense, c.Roll, c.Destroyed));
        Assert.Single(w.Events<TargetHit>());     // the one hit still falls on the regiment
        Assert.Equal((70, 90), (w.Events<SupportChanged>().Single().Old, w.Events<SupportChanged>().Single().New));
        Assert.Equal(new[] { (0, 1), (0, 1), (1, 10), (1, 10) }, w.Stub.Requests); // m0 = 1: the hit draws no pick
    }

    // B7-bombardment §5.3: with no civilian facility the collateral die is still drawn and does nothing; no hits, no collateral draw at all
    [Fact]
    public void CollateralDieIsDrawnWithoutAFacilityAndNotWithoutHits()
    {
        var w = new BombWorld();
        w.Hold(All);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(1, 10, 10);
        Assert.Null(w.TryBombard(Emp, f, Sys, Mil)); // m0 = 0: rand(0..0) draws nothing and gives 0
        var c = Assert.Single(w.Events<CollateralHit>());
        Assert.Equal((0, false), (c.Facility, c.Destroyed));
        Assert.Equal(new[] { (1, 10) }, w.Stub.Requests);
        Assert.Empty(w.Events<SupportChanged>());

        var g = new BombWorld();
        g.Hold(All);
        g.Facility(FacilityKind.GenCoreLevelI);
        int f2 = g.Fleet(Emp, Sys, "Warrant");
        Assert.Null(g.TryBombard(Emp, f2, Sys, Mil)); // P 1 < S 40: no hits
        Assert.Empty(g.Events<CollateralHit>());
        Assert.Empty(g.Stub.Requests);
    }

    // B7-bombardment Q81: an ISD with 1500 of 2750 hull lost, no admiral: P = 1 (C4); full value with the switch off
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void Q81_DamagedShipBombsLess(bool scales, int power)
    {
        var w = new BombWorld(bp: new BombardParams { BombardDamageScales = scales });
        int f = w.Fleet(Emp, Sys, BombWorld.Isd);
        Ship(w, f).Hull = 2750 - 1500;
        Assert.Equal(power, w.Bomb.Power(w.Game.State.Fleets[f]));
    }

    // B7-bombardment §5.1 (C4): a squadron counts v - v x lost craft / squadron size, truncated (Maul 2, size 12)
    [Theory]
    [InlineData(0, 2)]
    [InlineData(5, 2)]   // 2 - 10 / 12 = 2 - 0
    [InlineData(6, 1)]   // 2 - 12 / 12
    [InlineData(11, 1)]  // 2 - 22 / 12
    [InlineData(12, 0)]
    public void DepletedSquadronBombsLess(int lost, int value)
    {
        var w = new BombWorld();
        int f = w.Fleets.AddFleet(All, "Carrier", new[] { "Nest" }, Sys, new[] { "Maul" }); // the carrier counts 0
        var sq = w.Eco.Objects.Single(o => o.Category == ObjectCategory.Fighter);
        sq.CraftLost = lost;
        Assert.Equal(value, w.Bomb.Power(w.Game.State.Fleets[f]));
    }

    // B7-bombardment Q82: 9 Imperial Army regiments, 26 hits, Military, 10,000 runs: mean kills about 7.0 (reference model 6.96), all 9 in about 7
    // percent (model 7.2)
    [Fact]
    public void Q82_NineRegimentsMostlySurvive26Hits()
    {
        var w = new BombWorld(stub: false, seed: 82);
        w.Hold(Emp);
        int f = w.Fleet(All, Sys, Enumerable.Repeat("Jubilee", 13).ToArray()); // 13 x 2 = 26
        long killed = 0; int wiped = 0;
        const int trials = 10_000;
        for (int i = 0; i < trials; i++)
        {
            for (int k = 0; k < 9; k++) w.Regiment(Emp, ImpArmy);
            w.Bombard(All, f, Sys, Mil);
            w.Run(1);
            var left = w.Eco.Objects.Where(o => o.Category == ObjectCategory.Regiment).ToList();
            killed += 9 - left.Count;
            if (left.Count == 0) wiped++;
            foreach (var o in left) w.Eco.RemoveObject(o.Id);
        }
        double mean = (double)killed / trials, all = 100.0 * wiped / trials;
        Assert.True(Math.Abs(mean - 6.96) <= 0.08, $"mean {mean:F3}");
        Assert.True(Math.Abs(all - 7.2) <= 1.2, $"all 9 in {all:F2} percent");
    }

    // B7-bombardment Q83: a Shipyard, a Training Facility and a Refinery each hit once: destroyed with chance 80, 70 and 80 percent (C8 data)
    [Theory]
    [InlineData(FacilityKind.OrbitalShipyard, 2, 8)]
    [InlineData(FacilityKind.TrainingFacility, 3, 7)]
    [InlineData(FacilityKind.Refinery, 2, 8)]
    public void Q83_BuildingDefencesAreTheOriginalValues(FacilityKind kind, int defense, int destroyedOfTen)
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Eco.SetSlots(Sys, 4, 4);
        int f = w.Fleet(Emp, Sys, "Warrant");
        int destroyed = 0;
        for (int r = 1; r <= 10; r++)
        {
            int id = w.Eco.AddFacility(Sys, kind);
            w.Stub.Expect(1, 10, r);              // Civilian, one target: no pick draw
            Assert.Null(w.TryBombard(Emp, f, Sys, Civ));
            Assert.Equal(defense, w.Events<TargetHit>().Last().Defense);
            if (w.Eco.Find(id) == null) destroyed++; else w.Eco.RemoveObject(id);
        }
        Assert.Equal(destroyedOfTen, destroyed);
    }

    // B7-bombardment §7 (C8): the building defences come from the Classic data files (MANFACSD and PROFACSD word 12), the Rebel HQ is 9 (ALLFACSD)
    [Fact]
    public void BuildingDefencesAreData()
    {
        var d = BombWorld.RealData.Value;
        Assert.Equal(new[] { 2, 3, 3, 2, 4, 4, 5, 2 }, new[] { "Orbital Shipyard", "Training Facility", "Construction Yard", "Advanced Shipyard",
            "Advanced Training Facility", "Advanced Construction Yard", "Mine", "Refinery" }.Select(d.FacilityDefense));
        Assert.Equal(new[] { 5, 4, 2, 1, 3, 3 }, new[] { "Mordant Lien-150", "Mordant Covenant I", "Lode Escrow I", "Death Star Shield", "Mordant Covenant II", "Lode Escrow II" }
            .Select(d.FacilityDefense));
        Assert.Equal(9, d.HqDefense);
        var original = CsvTable.Load(Path.Combine(TestData.ClassicDir, DefenseData.BombardmentFile));
        Assert.Equal(8, Enumerable.Range(0, original.RowCount).Count(r => original.Get(r, "kind") == "facility"));
    }

    // B7-bombardment §Collisions: the switches' DEFAULTs (Dev lead 2026-10-19): C1, C2, C3, C4, C6 on, C9 off, the same in Classic and New
    [Fact]
    public void SwitchDefaults()
    {
        var p = new BombardParams();
        Assert.Equal((true, true, true, true, true, false),
            (p.BombardWastedHits, p.BatteryReturnFire, p.BombardFleetsTogether, p.BombardDamageScales, p.MilitaryCollateral, p.HqBombardable));
        Assert.Equal(p, Praedra.Core.Setup.GameHost.New(1).Bombardment.Params);
    }

    // B7-bombardment §4.4.5: CSCRHTTB (the critical-hit table) has the thresholds the module uses: 0, 16, 31, 41, 46 over rand(0..50)
    [Fact]
    public void CriticalTableIsTheOriginalFile()
    {
        var t = CsvTable.Load(Path.Combine(TestData.ClassicDir, "raw", "CSCRHTTB.csv"));
        var p = new BombardParams();
        Assert.Equal(new[] { 0, p.CriticalShieldUpTo + 1, p.CriticalWeaponsUpTo + 1, p.CriticalTractorUpTo + 1, p.CriticalSpeedUpTo + 1 },
            Enumerable.Range(0, t.RowCount).Select(r => t.GetInt(r, "threshold")));
    }

    // B7-bombardment §4.4.5: the CSCRHTTB roll picks the counter; the hyperdrive goes to the backup once the primary is at the cap 4
    [Theory]
    [InlineData(15, 0, 0)] [InlineData(16, 0, 1)] [InlineData(30, 0, 1)] [InlineData(31, 0, 2)] [InlineData(40, 0, 2)] [InlineData(41, 0, 3)]
    [InlineData(45, 0, 3)] [InlineData(46, 0, 4)] [InlineData(50, 3, 4)] [InlineData(50, 4, 5)]
    public void CriticalHitTable(int roll, int primaryBefore, int counter)
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.LnrSeriesI);
        int f = w.Fleet(All, Sys, Neb);
        Ship(w, f).Hits![4] = primaryBefore;
        w.Stub.Expect(0, 50, 0, roll);           // score 0 + pct(500, 600) = 83 >= 50, then the table roll
        Assert.Null(w.TryBombard(All, f, Sys, Civ));
        Assert.Equal(counter, w.Events<BombardShipDamaged>().Single().Critical);
        var hits = Ship(w, f).Hits!;
        if (counter == 5) Assert.Equal((4, 1), (hits[4], hits[5]));
        else if (counter == 4) Assert.Equal(primaryBefore + 1, hits[4]);
        else Assert.Equal(1, hits[counter]);
    }

    // B7-bombardment §4.4.5: a critical score below 50 hits nothing (one draw only); a level already at the cap stays at 4
    [Fact]
    public void CriticalRollBelowFiftyAndTheCap()
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.LnrSeriesI);
        int f = w.Fleet(All, Sys, "Barricade");         // shield 1100, hull 3000: 800 is absorbed, no hull lost, no roll
        Assert.Null(w.TryBombard(All, f, Sys, Civ));
        Assert.Equal(0, w.Stub.Count(0, 50));
        Assert.Equal(3000, Ship(w, f).Hull);

        var g = new BombWorld();
        g.Hold(Emp);
        g.Facility(FacilityKind.LnrSeriesII);                         // fires first (both LNR: id order)
        g.Facility(FacilityKind.LnrSeriesI);
        int f2 = g.Fleet(All, Sys, "Ark", "Ark"); // shield 300, hull 2400
        g.Stub.Expect(0, 1, 0, 1);                                    // LNR II at ship 0 (destroyed: 300 + 2400 < 5000), LNR I at ship 1
        g.Stub.Expect(0, 50, 0);                                      // ship 1: 800 lost, 1600 left: 0 + pct(800, 2400) = 33 < 50: no hit
        var ship1 = Ship(g, f2, 1);
        ship1.Hits![0] = 4;                                           // shield recharge at the cap: S = 300 x 0 / 4 = 0
        Assert.Null(g.TryBombard(All, f2, Sys, Civ));
        var dmg = g.Events<BombardShipDamaged>();
        Assert.Equal(2, dmg.Count);
        Assert.True(dmg[0].Destroyed);
        Assert.Equal((0, 800, -1), (dmg[1].Shield, dmg[1].HullLost, dmg[1].Critical));
        Assert.Equal(1600, ship1.Hull);
        Assert.Equal(1, g.Stub.Count(0, 50));
    }

    // B7-bombardment §4.4.1: the current shield S = shield x (4 - c) / 4 x hull left / hull, two truncating steps
    [Fact]
    public void CurrentShieldFollowsRechargeLevelAndHull()
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.Kdy150);
        int f = w.Fleet(All, Sys, "Crowbar");                // shield 600, hull 1400
        var ship = Ship(w, f);
        ship.Hull = 700; ship.Hits![0] = 1;                           // 600 x 3 / 4 = 450, x 700 / 1400 = 225
        Assert.Null(w.TryBombard(All, f, Sys, Civ));
        var d = Assert.Single(w.Events<BombardShipDamaged>());
        Assert.Equal((225, 2000, 0, true), (d.Shield, d.Ion, d.HullLost, d.Grounded));
    }

    // B7-bombardment §4.4.2, §4.4.3: ion eats the shield first, turbolaser eats what is left of it, then the hull; ion left grounds the ship
    [Fact]
    public void IonFirstThenTurbolaserThenHull()
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.Kdy150); w.Facility(FacilityKind.LnrSeriesI);
        int f = w.Fleet(All, Sys, "Barricade");         // shield 1100, hull 3000: KDY 2000 takes all 1100, LNR 800 all to hull
        w.Stub.Expect(0, 50, 50, 0);
        Assert.Null(w.TryBombard(All, f, Sys, Civ));
        var d = Assert.Single(w.Events<BombardShipDamaged>());
        Assert.Equal((1100, 800, true, 0), (d.Shield, d.HullLost, d.Grounded, d.Critical)); // 50 + pct(800, 3000) = 76: a hit on shield recharge
        Assert.Equal(2200, Ship(w, f).Hull);
        Assert.Equal(0, w.Events<BombardmentStarted>().Single().P); // grounded: the Bulwark's 4 does not count
        var shots = w.Events<BatteryShot>();
        Assert.Equal(new[] { true, false }, shots.Select(s => s.Ion)); // KDY first, then LNR
    }

    // B7-bombardment §4.2, §4.3, §8: each battery draws rand(0..n - 1) (KDY first, then LNR); a General multiplies the damage by L / 40 + 1
    [Fact]
    public void ShotsPickRandomShipsAndTheGeneralMultiplies()
    {
        var w = new BombWorld();
        w.Hold(Emp);
        int lnr = w.Facility(FacilityKind.LnrSeriesI);
        int kdy = w.Facility(FacilityKind.Kdy150);
        int f = w.Fleet(All, Sys, Neb, Neb, Neb);
        var ids = w.Game.State.Fleets[f].Ships.ToArray();
        w.Bomb.BatteryGeneralLeadership = s => s == Sys ? 45 : 0;    // x2
        w.Stub.Expect(0, 2, 2, 1);                                    // the KDY (higher id, but KDY fire first) at ship 2, the LNR at ship 1
        Assert.Null(w.TryBombard(All, f, Sys, Civ));
        var shots = w.Events<BatteryShot>();
        Assert.Equal(new[] { (kdy, ids[2], 4000), (lnr, ids[1], 1600) },
            shots.Select(s => (s.Facility, s.Ship, s.Damage)));       // ship 1 is destroyed: 1600 > 300 + 600
        Assert.Equal(new[] { kdy, lnr }, w.Events<BatteryReturnFire>().Single().Facilities.Items);
        Assert.Equal(2, w.Game.State.Fleets[f].Ships.Count);
        Assert.Equal(1, w.Events<BombardmentStarted>().Single().P); // ship 0 counts 1; ship 2 is grounded by 4000 ion
    }

    // B7-bombardment §4.4.4 and §10: a ship destroyed by return fire takes its regiments with it; its squadrons land (the generic ship-loss path)
    [Fact]
    public void DestroyedShipLosesItsRegimentsAndLandsItsSquadrons()
    {
        var w = new BombWorld();
        w.Hold(Emp);
        w.Facility(FacilityKind.LnrSeriesII);
        int f = w.Fleets.AddFleet(All, "Neb", new[] { Neb }, Sys, new[] { "Maul" });
        int ship = w.Game.State.Fleets[f].Ships[0];
        int reg = w.Regiment(All, BombWorld.Army); var r = w.Eco.Find(reg)!; r.System = null; r.AboardShip = ship;
        Assert.Null(w.TryBombard(All, f, Sys, Mil));
        Assert.Null(w.Eco.Find(ship)); Assert.Null(w.Eco.Find(reg));
        Assert.Equal("ShipLost", w.Events<RegimentDestroyed>().Single().Reason);
        var sq = w.Eco.Objects.Single(o => o.Category == ObjectCategory.Fighter);
        Assert.Equal(0, sq.AboardShip);
        Assert.Equal(0, w.Events<BombardmentStarted>().Single().Hits); // §4.7: no bombarding ship left (the landed Maul does not bomb)
    }

    // B7-bombardment §4 (C2 off): the m3 hook only: the batteries listed after the hits, no ship touched, no draw
    [Fact]
    public void ReturnFireOffIsTheHookOnly()
    {
        var w = new BombWorld(bp: new BombardParams { BatteryReturnFire = false });
        w.Hold(Emp);
        int k = w.Facility(FacilityKind.LnrSeriesII);
        w.Facility(FacilityKind.GenCoreLevelII);
        int f = w.Fleet(All, Sys, Neb);
        Assert.Null(w.TryBombard(All, f, Sys, Mil));
        Assert.Equal(600, Ship(w, f).Hull);
        Assert.Empty(w.Events<BatteryShot>()); Assert.Empty(w.Events<BombardShipDamaged>());
        Assert.Equal(new[] { k }, w.Events<BatteryReturnFire>().Single().Facilities.Items);
        var kinds = w.Game.Events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf(nameof(BombardmentStarted)) < kinds.IndexOf(nameof(BatteryReturnFire)));
    }

    // B7-bombardment §1 table (C5): Energy and Raw are General targets only; General walks Military, Civilian, Energy, Raw
    [Fact]
    public void EnergyAndRawAreGeneralTargetsOnly()
    {
        var w = new BombWorld();
        w.Hold(All);
        w.Eco.SetSlots(Sys, 4, 1);
        w.Regiment(All, BombWorld.Army); w.Facility(FacilityKind.ConstructionYard);
        int f = w.Fleet(Emp, Sys, "Warrant");
        w.Stub.Expect(1, 10, 1);
        w.TryBombard(Emp, f, Sys, Civ);
        Assert.Empty(w.Stub.Requests.Where(r => r.Min == 0)); // Civilian = [yard]: one target, no pick draw
        var g = new BombWorld();
        g.Hold(All, 70);
        g.Eco.SetSlots(Sys, 4, 1);
        g.Regiment(All, BombWorld.Army); g.Facility(FacilityKind.GenCoreLevelI); g.Facility(FacilityKind.ConstructionYard);
        int f2 = g.Fleet(Emp, Sys, "Death Star", 3);           // 45 - 40 = 5 hits
        g.Stub.Expect(0, 4, 4, 0, 0, 0, 0);
        g.Stub.Expect(1, 10, 10, 1, 1, 1, 1);
        g.TryBombard(Emp, f2, Sys, Gen);
        Assert.Equal((0, 4), g.Stub.Requests[0]);              // regiment, GenCore, yard, Energy, Raw
        Assert.Equal("Raw", g.Events<TargetHit>().First().TargetKind);
        Assert.Single(g.Events<SupportChanged>());             // C7: a raw loss costs -20 too
    }

    // B7-bombardment §3 and C9: HqBombardable off (DEFAULT): the Rebel HQ is no target; on: a Military target of defence 9 after the defences, and its
    // destruction reaches the untraced hook, which throws
    [Fact]
    public void RebelHqIsATargetOnlyWithTheSwitchAndItsEffectIsNotGuessed()
    {
        var off = new BombWorld();
        off.Hold(All);
        off.Bomb.IsRebelHq = s => s == Sys;
        off.Regiment(All, BombWorld.Army);
        int f = off.Fleet(Emp, Sys, "Warrant");
        off.Stub.Expect(0, 1, 1);
        off.Stub.Expect(1, 10, 1);
        Assert.Null(off.TryBombard(Emp, f, Sys, Mil));
        Assert.Equal(new[] { (0, 1), (1, 10) }, off.Stub.Requests); // m0 = 1 (the regiment): collateral x only, no pick

        var on = new BombWorld(bp: new BombardParams { HqBombardable = true });
        on.Hold(All);
        on.Bomb.IsRebelHq = s => s == Sys;
        on.Regiment(All, BombWorld.Army);
        int f2 = on.Fleet(Emp, Sys, "Warrant");
        on.Stub.Expect(0, 2, 1);                                    // m0 = 2: collateral x = 1
        on.Stub.Expect(0, 1, 1);                                    // t = 1: the HQ
        on.Stub.Expect(1, 10, 9);                                   // 9 does not beat 9
        Assert.Null(on.TryBombard(Emp, f2, Sys, Mil));
        var hit = on.Events<TargetHit>().Single();
        Assert.Equal(("Hq", 9, false), (hit.TargetKind, hit.Defense, hit.Destroyed));
        on.Bombard(Emp, f2, Sys, Mil);
        on.Stub.Expect(0, 2, 1); on.Stub.Expect(0, 1, 1); on.Stub.Expect(1, 10, 10);
        Assert.Throws<NotImplementedException>(() => on.Run(1));
    }

    // B7-bombardment §8: same seed and orders twice with every B7 rule in play (return fire, criticals, collateral, wasted hits): identical draws,
    // events and state hash (m3-bombardment B21)
    [Fact]
    public void SameSeedSameStrike()
    {
        BombWorld Run()
        {
            var w = new BombWorld(stub: false, seed: 707);
            w.Hold(Emp, 45);
            w.Eco.SetSlots(Sys, 6, 2);
            w.Eco.AddFacility(Sys, FacilityKind.Mine); w.Facility(FacilityKind.Kdy150); w.Facility(FacilityKind.LnrSeriesI); w.Facility(FacilityKind.LnrSeriesI);
            for (int i = 0; i < 6; i++) w.Regiment(Emp, ImpArmy);
            int a = w.Fleet(All, Sys, Neb, Neb, "Jubilee"), b = w.Fleets.AddFleet(All, "B", new[] { Neb }, Sys, new[] { "Maul", "Plough" });
            w.Bombard(All, a, Sys, Mil); w.Bombard(All, b, Sys, Mil);
            w.Run(1);
            return w;
        }
        var x = Run(); var y = Run();
        Assert.Equal(x.Game.Rng.Export(), y.Game.Rng.Export());
        Assert.True(x.Game.Events.SequenceEqual(y.Game.Events));
        Assert.Equal(GameSave.StateHash(x.Game), GameSave.StateHash(y.Game));
        Assert.Equal(3, x.Events<BatteryShot>().Count);
    }
}
