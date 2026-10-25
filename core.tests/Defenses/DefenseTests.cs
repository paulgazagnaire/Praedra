using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Tests.Construction;
using Praedra.Core.Tests.Control;
using Praedra.Core.Tests.Economy;

namespace Praedra.Core.Tests.Defenses;

// m3-planetary-defenses, tests D1 to D15, then extra tests named after the rule they check. Real data files.
public class DefenseTests
{
    private static readonly FacilityKind Kdy = FacilityKind.Kdy150, Lnr1 = FacilityKind.LnrSeriesI, Lnr2 = FacilityKind.LnrSeriesII,
        Gen1 = FacilityKind.GenCoreLevelI, Gen2 = FacilityKind.GenCoreLevelII, Dss = FacilityKind.DeathStarShield;

    // m3-planetary-defenses D1: the six rows exactly as the table; only class 4 is Hand only; cost and maintenance agree with the costs file
    [Fact]
    public void D1_SixRowsAsTheTable()
    {
        var d = DefenseData.Load(TestData.ClassicDir, TestData.DesignDir);
        var rows = d.Classes.ToList();
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, rows.Select(r => r.Id));
        var expect = new (string Name, int Family, int Cost, int Maint, int Ro, int Rd, int Attack, int Shield, int Bd, DefenseJob Job)[]
        {
            ("Mordant Lien-150", 34, 4, 4, 0, 0, 2000, 0, 5, DefenseJob.Battery),
            ("Mordant Covenant I", 35, 7, 7, 0, 0, 800, 0, 4, DefenseJob.Battery),
            ("Lode Escrow I", 36, 12, 12, 0, 0, 0, 40, 2, DefenseJob.ShieldGenerator),
            ("Death Star Shield", 37, 25, 20, 3, 24, 0, 0, 1, DefenseJob.DeathStarShield),
            ("Mordant Covenant II", 35, 10, 4, 4, 40, 5000, 0, 3, DefenseJob.Battery),
            ("Lode Escrow II", 36, 20, 7, 6, 40, 0, 80, 3, DefenseJob.ShieldGenerator),
        };
        for (int i = 0; i < 6; i++)
        {
            var r = rows[i]; var e = expect[i];
            Assert.Equal((e.Name, e.Family, e.Cost, e.Maint, e.Ro, e.Rd, e.Attack, e.Shield, e.Bd, e.Job),
                (r.Name, r.Family, r.Cost, r.Maintenance, r.ResearchOrder, r.ResearchDifficulty, r.Attack, r.Shield, r.BombardDefense, r.Job));
        }
        Assert.Equal(new[] { 4 }, rows.Where(r => r.HandOnly).Select(r => r.Id));
        var costs = EcoWorld.Costs.Value;
        foreach (var r in rows) // "Costs and maintenance agree with unit-and-facility-costs.csv"
        {
            var c = costs.Get(r.Name);
            Assert.Equal((r.Cost, r.Maintenance, r.ResearchOrder), (c.Cost, c.Maintenance, c.ResearchOrder));
            Assert.Equal(r.HandOnly, !c.Collective);
        }
    }

    // m3-planetary-defenses D2
    [Theory]
    [InlineData(new int[0], 0)]
    [InlineData(new[] { 3 }, 1)]
    [InlineData(new[] { 3, 6 }, 2)]
    [InlineData(new[] { 6, 6 }, 2)]
    [InlineData(new[] { 3, 4, 4 }, 1)]
    public void D2_ShieldGenerators(int[] classes, int expected)
    {
        var w = new BombWorld();
        foreach (var c in classes) w.Facility(KindOf(c));
        Assert.Equal(expected, w.Def.ShieldGenerators(BombWorld.Sys));
    }

    // m3-planetary-defenses D2 (last case): two GenCore I, one unfinished
    [Fact]
    public void D2_TwoGenCoreOneUnfinishedCountsOne()
    {
        var w = new BombWorld();
        w.Facility(Gen1); w.Facility(Gen1, completed: false);
        Assert.Equal(1, w.Def.ShieldGenerators(BombWorld.Sys));
    }

    // m3-planetary-defenses D3
    [Theory]
    [InlineData(new int[0], 0)]
    [InlineData(new[] { 3 }, 40)]
    [InlineData(new[] { 3, 6 }, 120)]
    [InlineData(new[] { 6, 6 }, 160)]
    [InlineData(new[] { 3, 4 }, 40)]
    public void D3_ShieldStrength(int[] classes, int expected)
    {
        var w = new BombWorld();
        foreach (var c in classes) w.Facility(KindOf(c));
        Assert.Equal(expected, w.Def.ShieldStrength(BombWorld.Sys));
    }

    // m3-planetary-defenses D4: attack / 5 percent; the first three hit on any rand(0..99), the shield on none
    [Fact]
    public void D4_FacilityChance()
    {
        var w = new BombWorld();
        Assert.Equal(new[] { 400, 160, 1000, 0 }, new[] { 2000, 800, 5000, 0 }.Select(w.Def.ChancePercent));
        foreach (int pct in new[] { 400, 160, 1000 }) Assert.All(Enumerable.Range(0, 100), r => Assert.True(r < pct));
        Assert.All(Enumerable.Range(0, 100), r => Assert.False(r < 0));
    }

    // m3-planetary-defenses D5: the assault's step 1 list (the draws themselves are the assault package's)
    [Fact]
    public void D5_BatteryShotsListsEveryDefenseInIdOrder()
    {
        var w = new BombWorld();
        w.Hold(Side.Collective);
        int g = w.Facility(Gen1), k = w.Facility(Kdy), l = w.Facility(Lnr1);
        var shots = w.Def.BatteryShots(BombWorld.Sys, Side.Hand);
        Assert.Equal(new[] { g, k, l }, shots.Select(s => s.Facility));
        Assert.Equal(new[] { 0, 2000, 800 }, shots.Select(s => s.Attack));
        Assert.Equal(new[] { 0, 400, 160 }, shots.Select(s => s.ChancePercent));
        Assert.Empty(w.Def.BatteryShots(BombWorld.Sys, Side.Collective)); // the holder never shoots at itself
    }

    // m3-planetary-defenses D6
    [Fact]
    public void D6_OnlyTheCompletedKdyShoots()
    {
        var w = new BombWorld();
        w.Hold(Side.Collective);
        int done = w.Facility(Kdy); w.Facility(Kdy, completed: false);
        Assert.Equal(new[] { done }, w.Def.BatteryShots(BombWorld.Sys, Side.Hand).Select(s => s.Facility));
    }

    // m3-planetary-defenses D7
    [Fact]
    public void D7_BatteriesCountKdyAndBothLnr()
    {
        var w = new BombWorld();
        w.Facility(Kdy); w.Facility(Lnr1); w.Facility(Lnr2); w.Facility(Gen1);
        Assert.Equal(3, w.Def.Batteries(BombWorld.Sys));
    }

    // m3-planetary-defenses D8
    [Fact]
    public void D8_ReturnFireSetIsTheBatteriesOnly()
    {
        var w = new BombWorld();
        int k = w.Facility(Kdy); w.Facility(Gen1);
        Assert.Equal(new[] { k }, w.Def.ReturnFireSet(BombWorld.Sys));
    }

    // m3-planetary-defenses D9: the Collective cannot order a Death Star Shield (construction sheet: NotAvailable); the Hand can
    [Fact]
    public void D9_CollectiveBuildOrderOfDeathStarShieldIsNotAvailable()
    {
        var a = new BuildWorld();
        a.Hold("Classis", Side.Collective); a.Yard("Classis", FacilityKind.ConstructionYard);
        Assert.Equal(RejectReason.NotAvailable, a.TryOrder(Side.Collective, "Classis", YardFamily.Construction, "Death Star Shield", 1, "Classis"));
        var e = new BuildWorld();
        e.Build.RespectResearchOrder = false; // research gate off = all available (the gate is on by default since m5-research)
        e.Hold("Classis", Side.Hand); e.Yard("Classis", FacilityKind.ConstructionYard);
        Assert.Null(e.TryOrder(Side.Hand, "Classis", YardFamily.Construction, "Death Star Shield", 1, "Classis")); // research flag off = all available
    }

    // m3-planetary-defenses D10 (control sheet C1): the Death Star Shield is removed on a Collective capture, the other defenses transfer
    [Fact]
    public void D10_CollectiveCaptureRemovesTheDeathStarShieldOnly()
    {
        var w = new CtlWorld();
        w.Hold("Classis", Side.Hand, 80);
        int shield = w.Eco.AddFacility("Classis", Dss), gen = w.Eco.AddFacility("Classis", Gen1), kdy = w.Eco.AddFacility("Classis", Kdy);
        w.Capture(Side.Collective, "Classis");
        w.Run(1);
        Assert.Equal(new[] { shield }, w.Events<FacilityRemoved>().Select(e => e.Facility));
        Assert.Equal(new[] { gen, kdy }, w.Events<FacilityTransferred>().Select(e => e.Facility));
    }

    // m3-planetary-defenses D11 (links B19, A10): bombardment destroys one of two GenCore at Step K; at K + 1 the gate is open
    [Fact]
    public void D11_OneOfTwoShieldsDestroyedOpensTheGate()
    {
        var w = new BombWorld();
        w.Hold(Side.Collective);
        w.Facility(Gen1); w.Facility(Gen1);
        Assert.True(w.Def.AssaultForbidden(BombWorld.Sys));
        w.Fleet(Side.Hand, BombWorld.Sys, "Death Star", 6); // P = 90, S = 80, 10 hits
        w.Stub.Expect(0, 2, 1); // B7-bombardment §5.3: the military collateral x = rand(0..m0 = 2) comes first: 1, no collateral
        w.Dice((0, 10), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1)); // the first shield falls, the second survives nine rolls
        int fleet = w.Game.State.Fleets.Keys.Single();
        Assert.Null(w.TryBombard(Side.Hand, fleet, BombWorld.Sys, BombardCategory.Military));
        Assert.Equal(1, w.Def.ShieldGenerators(BombWorld.Sys));
        Assert.False(w.Def.AssaultForbidden(BombWorld.Sys));
    }

    // m3-planetary-defenses D12: over-capacity (slots sheet): energy falls from 3 to 2 with three defenses, one is destroyed, one pick draw
    [Fact]
    public void D12_OverCapacityDestroysOneDefenseWithOnePick()
    {
        var w = new BombWorld();
        w.Hold(Side.Collective);
        w.Eco.SetSlots(BombWorld.Sys, 3, 0);
        int a = w.Facility(Kdy), b = w.Facility(Lnr1), c = w.Facility(Gen1);
        w.Eco.SetSlots(BombWorld.Sys, 2, 0);
        w.S.InStep(ctx => w.Eco.EnforceCapacity(ctx, BombWorld.Sys));
        w.Stub.Queue(1);
        w.Run(1);
        Assert.Equal(new[] { b }, w.Events<FacilityDestroyed>().Select(e => e.Facility)); // pick 1 of (a, b, c)
        Assert.Equal(1, w.Stub.Count(0, 2));
        Assert.Equal(new[] { a, c }, w.Eco.Objects.Select(o => o.Id));
    }

    // m3-planetary-defenses D13: the queries are pure
    [Fact]
    public void D13_QueriesAreRepeatableAndDrawNothing()
    {
        var w = new BombWorld(stub: false, seed: 5);
        w.Hold(Side.Collective);
        w.Facility(Gen1); w.Facility(Kdy); w.Facility(Lnr2); w.Facility(Dss); w.Facility(Gen2, completed: false);
        string Snapshot() => string.Join("|", w.Def.ShieldGenerators(BombWorld.Sys), w.Def.ShieldStrength(BombWorld.Sys), w.Def.Batteries(BombWorld.Sys),
            string.Join(",", w.Def.BatteryShots(BombWorld.Sys, Side.Hand).Select(s => $"{s.Facility}:{s.Attack}:{s.ChancePercent}")),
            string.Join(",", w.Def.ReturnFireSet(BombWorld.Sys)), w.Def.AssaultForbidden(BombWorld.Sys), w.Def.GateShields(BombWorld.Sys));
        string first = Snapshot();
        long draws = w.Game.Rng.Draws;
        string hash = GameSave.StateHash(w.Game);
        for (int i = 0; i < 1000; i++) Assert.Equal(first, Snapshot());
        Assert.Equal(draws, w.Game.Rng.Draws);
        Assert.Equal(hash, GameSave.StateHash(w.Game));
    }

    // m3-planetary-defenses D14: bombard a world with an LNR and a KDY: BatteryReturnFire lists both, no ship changes in M3
    [Fact]
    public void D14_BombardingABatteryWorldListsBothBatteriesAndChangesNoShip()
    {
        // The m3 hook, kept as the BatteryReturnFire-off path (B7 switches off). With the B7 DEFAULTs the batteries do damage ships:
        // B7-bombardment §4, tested in B7BombardmentTests.
        var w = new BombWorld(bp: BombWorld.M3Rule);
        w.Hold(Side.Collective);
        int l = w.Facility(Lnr1), k = w.Facility(Kdy);
        int fleet = w.Fleet(Side.Hand, BombWorld.Sys, "Decree", 2); // P = 8
        var ships = ShipState(w);
        w.Dice(Enumerable.Repeat((0, 1), 8).ToArray()); // every roll is too low to destroy anything
        Assert.Null(w.TryBombard(Side.Hand, fleet, BombWorld.Sys, BombardCategory.Military));
        var fire = Assert.Single(w.Events<BatteryReturnFire>());
        Assert.Equal(new[] { l, k }, fire.Facilities.Items);
        Assert.Equal(ships, ShipState(w));
    }

    // m3-planetary-defenses D15: a neutral system's defense facilities (side 3) are in BatteryShots for a Hand assault
    [Fact]
    public void D15_NeutralFacilitiesShootAtTheHand()
    {
        var w = new BombWorld(); // Fiducia has no holder here: neutral
        int k = w.Facility(Kdy), g = w.Facility(Gen1);
        Assert.Equal(new[] { k, g }, w.Def.BatteryShots(BombWorld.Sys, Side.Hand).Select(s => s.Facility));
        Assert.Equal(new[] { k, g }, w.Def.BatteryShots(BombWorld.Sys, Side.Collective).Select(s => s.Facility));
    }

    // ---------------------------------------------------------------- extra tests

    // m3-planetary-defenses §Defaults (DEFAULT: the Death Star Shield has no M3 effect): not a generator, subtracts 0, but a switch counts it for the gate
    [Fact]
    public void DeathStarShieldIsNotAGateShieldByDefaultAndTheSwitchCountsIt()
    {
        var w = new BombWorld();
        w.Facility(Dss); w.Facility(Dss);
        Assert.Equal(0, w.Def.ShieldGenerators(BombWorld.Sys));
        Assert.Equal(0, w.Def.ShieldStrength(BombWorld.Sys));
        Assert.False(w.Def.AssaultForbidden(BombWorld.Sys)); // m3-assault A10: two Death Star Shields only: accepted

        var s = new BombWorld(dp: new DefenseParams { DeathStarShieldCountsForGate = true });
        s.Facility(Dss); s.Facility(Dss);
        Assert.True(s.Def.AssaultForbidden(BombWorld.Sys));
        Assert.Equal(0, s.Def.ShieldGenerators(BombWorld.Sys)); // the count itself never includes it
    }

    // m3-planetary-defenses §What each class does in M3 (the gate is two or more; 0 switches it off)
    [Fact]
    public void GateNeedsTwoGeneratorsAndCanBeSwitchedOff()
    {
        var w = new BombWorld();
        w.Facility(Gen1);
        Assert.False(w.Def.AssaultForbidden(BombWorld.Sys));
        w.Facility(Gen2);
        Assert.True(w.Def.AssaultForbidden(BombWorld.Sys));
        var off = new BombWorld(dp: new DefenseParams { AssaultGateShields = 0 });
        off.Facility(Gen1); off.Facility(Gen1); off.Facility(Gen2);
        Assert.False(off.Def.AssaultForbidden(BombWorld.Sys));
    }

    // m3-planetary-defenses §Queries (facilities elsewhere do not count)
    [Fact]
    public void FacilitiesOnOtherSystemsDoNotCount()
    {
        var w = new BombWorld();
        w.Facility(Gen1, system: BombWorld.Other); w.Facility(Kdy, system: BombWorld.Other);
        Assert.Equal((0, 0, 0), (w.Def.ShieldGenerators(BombWorld.Sys), w.Def.ShieldStrength(BombWorld.Sys), w.Def.Batteries(BombWorld.Sys)));
    }

    // m3-planetary-defenses §The six classes (data checks: a class that is not a facility kind is refused)
    [Fact]
    public void BadDefenseDataIsRefused()
    {
        string good = File.ReadAllText(Path.Combine(TestData.ClassicDir, DefenseData.DefensesFile));
        string bomb = TestData.MergedText(DefenseData.BombardmentFile);
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => DefenseData.Parse(good.Replace("Mordant Lien-150", "Mystery Gun"), "d.csv", bomb, "b.csv"));
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => DefenseData.Parse(good.Replace(",battery", ",flak"), "d.csv", bomb, "b.csv"));
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => DefenseData.Parse(good, "d.csv", bomb.Replace("troop,Nhal Lookouts", "troop,Militia"), "b.csv"));
    }

    // m3-bombardment §Resolution 4: every regiment class and every facility class has a bombardment defense
    [Fact]
    public void EveryRegimentAndFacilityClassHasABombardmentDefense()
    {
        var d = BombWorld.RealData.Value; var costs = EcoWorld.Costs.Value;
        foreach (var k in FacilityKinds.All) Assert.InRange(d.FacilityDefense(FacilityKinds.ClassName(k)), 0, 10);
        foreach (var name in new[] { "Deckhands", "Militia", "Nhal Lookouts", "Kiru Sparks", "Ilmar Shade Guard",
                     "Pacifiers", "Ballast", "Tranches", "Proxies", "Lictors" })
        {
            Assert.Equal(ObjectCategory.Regiment, costs.Get(name).Category);
            Assert.InRange(d.RegimentDefense(name), 0, 10);
        }
    }

    private static FacilityKind KindOf(int classId) => classId switch
    {
        1 => Kdy, 2 => Lnr1, 3 => Gen1, 4 => Dss, 5 => Lnr2, 6 => Gen2, _ => throw new ArgumentOutOfRangeException(nameof(classId)),
    };

    private static string ShipState(BombWorld w) => string.Join(";", w.Eco.Objects.Where(o => o.Category == ObjectCategory.CapitalShip)
        .Select(o => $"{o.Id}:{o.Hull}:{string.Join(",", o.Hits!)}"));
}
