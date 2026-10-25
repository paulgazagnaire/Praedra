using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Data;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Ships;
using Praedra.Core.Tests.Construction;
using Praedra.Core.Tests.Economy;

namespace Praedra.Core.Tests.Fleets;

// Tests of m3-ships-and-fighters (ids S1 to S17 as in the sheet's table, then extra tests named after the rule they check).
// The real CSV files are used. Hop Charter to Classis is tier 1, base 20.
public class ShipTests
{
    // v6 re-pin 2026-10-24: Far was Classis (one 100-unit great-route leg); on the spiral map it is Cambium, two legs from
    // Charter (a free jump to Remedium, then a minor-route leg) that also take 5 days at speed 80 and 7 at speed 100.
    private const string Home = "Charter", Far = "Cambium";
    private const string Mandate = "Mandate";

    private static IReadOnlyList<ShipClass> Capitals() => ShipData.LoadCapitalShips(Path.Combine(TestData.ClassicDir, ShipData.CapitalShipsFile));
    private static IReadOnlyList<ShipClass> Fighters() => ShipData.LoadFighters(Path.Combine(TestData.ClassicDir, ShipData.FightersFile));
    private static CsvTable Csv(string file) => CsvTable.Parse(File.ReadAllText(Path.Combine(TestData.ClassicDir, file)), file);

    // ---------------------------------------------------------------- the class tables (S1 to S5)

    // m3-ships-and-fighters S1
    [Fact]
    public void S1_CapitalShipTableHas30ClassesFifteenPerSide()
    {
        var c = Capitals();
        Assert.Equal(30, c.Count);
        Assert.Equal(15, c.Count(x => x.Side == ShipSide.Collective));
        Assert.Equal(15, c.Count(x => x.Side == ShipSide.Hand));
        Assert.DoesNotContain(c, x => x.Side == (ShipSide.Collective | ShipSide.Hand) || x.Side == ShipSide.None); // exactly one side flag
        Assert.Equal(Enumerable.Range(64, 15), c.Where(x => x.Side == ShipSide.Collective).Select(x => x.Id).OrderBy(i => i));
        Assert.Equal(Enumerable.Range(128, 15), c.Where(x => x.Side == ShipSide.Hand).Select(x => x.Id).OrderBy(i => i));
        Assert.All(c, x => Assert.True(x.IsCapital));
    }

    // m3-ships-and-fighters S2
    [Fact]
    public void S2_FighterTableHasEightClassesOfTwelve()
    {
        var f = Fighters();
        Assert.Equal(8, f.Count);
        var t = Csv(ShipData.FightersFile);
        for (int r = 0; r < t.RowCount; r++) Assert.Equal(12, t.GetInt(r, "squadron_size"));
        Assert.Equal(new[] { 1, 2, 3, 4 }, f.Where(x => x.Side == ShipSide.Collective).Select(x => x.Id).OrderBy(i => i));
        Assert.Equal(new[] { 5, 6, 7, 8 }, f.Where(x => x.Side == ShipSide.Hand).Select(x => x.Id).OrderBy(i => i));
        Assert.All(f, x => Assert.False(x.IsCapital));
        Assert.All(f, x => Assert.Equal((0, 0, 0), (x.Hull, x.FighterCapacity, x.TroopCapacity))); // fighters have no hull and carry nothing
    }

    // m3-ships-and-fighters S3
    [Fact]
    public void S3_TheMandateRow()
    {
        var mandate = Capitals().Single(x => x.Id == 133);
        Assert.Equal(Mandate, mandate.Name);
        Assert.Equal(112, mandate.RefinedMaterialCost);
        Assert.Equal(71, EcoWorld.Costs.Value.Get(Mandate).Maintenance);
        Assert.Equal((2750, 300, 80, 100), (mandate.Hull, mandate.ShieldStrength, mandate.Hyperdrive, mandate.HyperdriveIfDamaged));
        Assert.Equal((6, 3, 480), (mandate.FighterCapacity, mandate.TroopCapacity, mandate.OverallAttackStrength));
    }

    // m3-ships-and-fighters S4
    [Fact]
    public void S4_OverallAttackIsTheSumOfTheThreeAttackStrengthsInAll38Rows()
    {
        int rows = 0;
        foreach (var file in new[] { ShipData.CapitalShipsFile, ShipData.FightersFile })
        {
            var t = Csv(file);
            for (int r = 0; r < t.RowCount; r++, rows++)
                Assert.Equal(t.GetInt(r, "heavy_cannon_attack_strength") + t.GetInt(r, "ion_cannon_attack_strength") + t.GetInt(r, "laser_cannon_attack_strength"),
                    t.GetInt(r, "overall_attack_strength"));
        }
        Assert.Equal(38, rows);
    }

    // m3-ships-and-fighters S5
    [Fact]
    public void S5_TheSameIdInTwoKindsIsTwoClasses()
    {
        Assert.Equal("Swift", Fighters().Single(x => x.Id == 3).Name);
        var t = Csv(CostData.FileName);
        string troop3 = "";
        for (int r = 0; r < t.RowCount; r++)
            if (t.Get(r, "kind") == "troop" && t.GetInt(r, "id") == 3) troop3 = t.Get(r, "name");
        Assert.Equal("Nhal Lookouts", troop3);
    }

    // every ship and fighter class has a cost row of the same name and category, and the other way round (the registry joins them by name)
    [Fact]
    public void Ship_classes_and_cost_rows_agree_by_name()
    {
        var ships = ShipData.LoadAll(TestData.ClassicDir);
        foreach (var c in ships.Values)
        {
            Assert.True(EcoWorld.Costs.Value.TryGet(c.Name, out var row), $"no cost row for {c.Name}");
            Assert.Equal(c.IsCapital ? ObjectCategory.CapitalShip : ObjectCategory.Fighter, row.Category);
            Assert.Equal(c.RefinedMaterialCost, row.Cost);
        }
        var t = Csv(CostData.FileName);
        for (int r = 0; r < t.RowCount; r++)
            if (t.Get(r, "kind") is "capital ship" or "fighter") Assert.True(ships.ContainsKey(t.Get(r, "name")), t.Get(r, "name"));
    }

    // ---------------------------------------------------------------- availability (S6 to S8, S14)

    private static BuildWorld Yard(Side side)
    {
        var w = new BuildWorld();
        w.Hold(Home, side);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(side, 2000);
        return w;
    }

    // m3-ships-and-fighters S6
    [Fact]
    public void S6_TheHandCannotOrderAnCollectiveClass()
        => Assert.Equal(RejectReason.NotAvailable, Yard(Side.Hand).TryOrder(Side.Hand, Home, YardFamily.Shipyard, "Ark", 1));

    // m3-ships-and-fighters S7
    [Fact]
    public void S7_AllAvailableLetsTheCollectiveOrderABarricade()
    {
        var w = Yard(Side.Collective);
        w.Build.RespectResearchOrder = false; // AllAvailable (the M3 stub; the default is on since m5-research §Defaults)
        Assert.Null(w.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Barricade", 1)); // research order 11
        Assert.DoesNotContain(w.Game.Events, e => e is OrderRejected);
        // the same order under RespectResearchOrder
        var w2 = Yard(Side.Collective);
        w2.Build.RespectResearchOrder = true;
        Assert.Equal(RejectReason.NotResearched, w2.TryOrder(Side.Collective, Home, YardFamily.Shipyard, "Barricade", 1));
    }

    // m3-ships-and-fighters S8
    [Fact]
    public void S8_RespectResearchOrderListsTheStartingClasses()
    {
        var w = new BuildWorld();
        w.Build.RespectResearchOrder = true;
        var ships = ShipData.LoadAll(TestData.ClassicDir).Values;
        List<string> At(ShipSide side, bool capital) =>
            ships.Where(c => c.IsCapital == capital && c.Side == side && w.Build.IsResearched(side == ShipSide.Collective ? Side.Collective : Side.Hand, c.Name))
                 .Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(6, At(ShipSide.Collective, true).Count);
        Assert.Equal(7, At(ShipSide.Hand, true).Count);
        Assert.Equal(new[] { "Barge", "Dodger", "Grudge", "Hauler", "Mule", "Nest" }, At(ShipSide.Collective, true));
        Assert.Equal(new[] { "Plough", "Swift" }, At(ShipSide.Collective, false));
        Assert.Equal(new[] { "Gleaner" }, At(ShipSide.Hand, false));
        // a grant opens one more class
        w.Build.GrantResearch(Side.Collective, "Ark");
        Assert.Equal(7, At(ShipSide.Collective, true).Count);
    }

    // m3-ships-and-fighters S14
    [Fact]
    public void S14_TheHarvesterIsAnOrdinaryRowUnlessTheSwitchIsOff()
    {
        var on = Yard(Side.Hand);
        Assert.True(on.Build.HarvesterClassBuildable);
        Assert.Null(on.TryOrder(Side.Hand, Home, YardFamily.Shipyard, "harvester", 1));
        Assert.Equal(584, on.Eco.Costs.Get("harvester").Cost);
        Assert.Equal(600, on.Eco.Costs.Get("harvester").Maintenance);
        var ds = on.Eco.Objects.Single(o => o.Class == "harvester");
        Assert.False(ds.Completed);
        Assert.Equal(25000, ds.Hull);

        var off = Yard(Side.Hand);
        off.Build.HarvesterClassBuildable = false;
        Assert.Equal(RejectReason.NotAvailable, off.TryOrder(Side.Hand, Home, YardFamily.Shipyard, "harvester", 1));
        Assert.DoesNotContain(off.Eco.Objects, o => o.Class == "harvester");
    }

    // ---------------------------------------------------------------- damage and speed (S9 to S13, S15, S16)

    private static (BuildWorld World, int Fleet, int Ship) Fleet(params string[] classes)
    {
        var w = new BuildWorld();
        w.Fleets.DebugOrdersEnabled = true;
        int f = w.Fleets.AddFleet(Side.Hand, "F", classes, Home);
        return (w, f, w.State.Fleets[f].Ships[0]);
    }

    private static int DaysTo(BuildWorld w, int fleet, string to)
    {
        long seq = w.Fleets.SubmitMove(Side.Hand, fleet, to);
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
        return w.Events<OrderAccepted>().Last().TotalTicks;
    }

    private static void Damage(BuildWorld w, int ship, int hull, params int[] hits)
    {
        long seq = w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, hull, hits);
        w.Run(1);
        Assert.Null(w.RejectionOf(seq));
    }

    // m3-ships-and-fighters S9
    [Fact]
    public void S9_AnUndamagedMandateTakesFiveDays()
    {
        var (w, f, _) = Fleet(Mandate);
        Assert.Equal(5, DaysTo(w, f, Far)); // v6: two legs, 5 days in all (was one route leg: 100 * 80 / (5 * 300) = 5.33, rounded to 5)
    }

    // m3-ships-and-fighters S10
    [Fact]
    public void S10_AHyperdriveHitMakesItTakeSevenDays()
    {
        var (w, f, ship) = Fleet(Mandate);
        Damage(w, ship, 2750, 0, 0, 0, 0, 1, 0);
        Assert.Equal(100, w.Fleets.SpeedOf(w.State.Fleets[f]));
        Assert.Equal(7, DaysTo(w, f, Far)); // v6: two legs, 7 days in all (was one route leg: 100 * 100 / (5 * 300) = 6.67, rounded to 7)
    }

    // m3-ships-and-fighters S11
    [Fact]
    public void S11_HullLossAloneDoesNotSlowIt()
    {
        var (w, f, ship) = Fleet(Mandate);
        Damage(w, ship, 1, 0, 0, 0, 0, 0, 0);
        Assert.Equal(1, w.Obj(ship).Hull);
        Assert.Equal(5, DaysTo(w, f, Far));
    }

    // m3-ships-and-fighters S12
    [Fact]
    public void S12_ADamagedMediumTransportKeepsItsRating()
    {
        var (w, f, ship) = Fleet("Mule");
        Assert.Equal(7, DaysTo(w, f, Far)); // speed 100, undamaged: 7 days on the v6 two-leg trip
        var (w2, f2, ship2) = Fleet("Mule");
        Damage(w2, ship2, 360, 0, 0, 0, 0, 5, 0); // hyperdrive_if_damaged is 0: use the normal rating
        Assert.Equal(7, DaysTo(w2, f2, Far));
    }

    // m3-ships-and-fighters S13
    [Fact]
    public void S13_AHullAboveTheClassMaximumIsABadValue()
    {
        var (w, _, ship) = Fleet(Mandate);
        string before = System.Text.Json.JsonSerializer.Serialize(w.State.Economy.Objects);
        long seq = w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, 3000, new[] { 0, 0, 0, 0, 0, 0 });
        w.Run(1);
        Assert.Equal(RejectReason.BadValue, w.RejectionOf(seq));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(w.State.Economy.Objects));
        // the other bad values
        var seqs = new[]
        {
            w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, -1, new[] { 0, 0, 0, 0, 0, 0 }),
            w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, 10, new[] { 0, 0, 0, 0, 0 }),
            w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, 10, new[] { 0, 0, 0, 0, 16, 0 }),
            w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, 10, new[] { 0, 0, 0, 0, -1, 0 }),
            w.Fleets.SubmitDebugSetDamage(Side.Hand, 999, 10, new[] { 0, 0, 0, 0, 0, 0 }),
            w.Fleets.SubmitDebugSetDamage(Side.Collective, ship, 10, new[] { 0, 0, 0, 0, 0, 0 }),
        };
        w.Run(1);
        Assert.Equal(new[] { RejectReason.BadValue, RejectReason.BadValue, RejectReason.BadValue, RejectReason.BadValue, RejectReason.UnknownShip, RejectReason.NotYours },
            seqs.Select(w.RejectionOf));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(w.State.Economy.Objects));
    }

    // m3-ships-and-fighters §Damaged state: the debug order is off unless debug orders are enabled
    [Fact]
    public void DebugSetDamage_is_refused_unless_debug_orders_are_enabled()
    {
        var (w, _, ship) = Fleet(Mandate);
        w.Fleets.DebugOrdersEnabled = false;
        long seq = w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, 10, new[] { 0, 0, 0, 0, 0, 0 });
        w.Run(1);
        Assert.Equal(RejectReason.DebugDisabled, w.RejectionOf(seq));
    }

    // m3-ships-and-fighters S15
    [Fact]
    public void S15_OnlyBulkTransportsOrGalleonsAreUnarmedAndOneCorvetteArmsThem()
    {
        var w = new BuildWorld();
        int bulk = w.Fleets.AddFleet(Side.Collective, "Bulk", new[] { "Barge" }, Home);
        int galleon = w.Fleets.AddFleet(Side.Hand, "Bond", new[] { "Bond" }, Home);
        int mixed = w.Fleets.AddFleet(Side.Collective, "Mixed", new[] { "Barge", "Dodger" }, Home);
        Assert.False(CombatStrength.IsArmed(w.Fleets.Classes(w.State.Fleets[bulk])));
        Assert.False(CombatStrength.IsArmed(w.Fleets.Classes(w.State.Fleets[galleon])));
        Assert.True(CombatStrength.IsArmed(w.Fleets.Classes(w.State.Fleets[mixed])));
    }

    // m3-ships-and-fighters S16
    [Fact]
    public void S16_HullAndCountersSurviveSaveAndLoad()
    {
        var (w, _, ship) = Fleet(Mandate);
        Damage(w, ship, 1200, 0, 2, 0, 1, 3, 0);
        var loaded = GameSave.Load(GameSave.Save(w.Game));
        var o = loaded.State.Economy.Objects[ship];
        Assert.Equal(1200, o.Hull);
        Assert.Equal(new[] { 0, 2, 0, 1, 3, 0 }, o.Hits!);
        Assert.Equal(GameSave.StateHash(w.Game), GameSave.StateHash(loaded));
        // a different counter is a different state
        Damage(w, ship, 1200, 0, 2, 0, 1, 4, 0);
        Assert.NotEqual(GameSave.StateHash(w.Game), GameSave.StateHash(loaded));
    }

    // m3-ships-and-fighters S17
    [Fact]
    public void S17_LoadingTablesAndRunningTheTestsDrawsNothing()
    {
        var w = new BuildWorld(7); // a real generator: any draw would be counted
        Capitals(); Fighters(); ShipData.LoadAll(TestData.ClassicDir);
        w.Fleets.DebugOrdersEnabled = true;
        int f = w.Fleets.AddFleet(Side.Hand, "F", new[] { Mandate, "Mule" }, Home, new[] { "Gleaner" });
        int ship = w.State.Fleets[f].Ships[0];
        w.Fleets.SubmitDebugSetDamage(Side.Hand, ship, 1200, new[] { 0, 2, 0, 1, 3, 0 });
        w.Fleets.SubmitMove(Side.Hand, f, Far);
        w.Run(30);
        Assert.Equal(0, w.Game.Rng.Draws);
    }

    // ---------------------------------------------------------------- the ship object

    // m3-ships-and-fighters §The ship object: hull starts at the class maximum, six counters at 0; other objects have none
    [Fact]
    public void A_new_ship_starts_at_full_hull_with_six_zero_counters()
    {
        var w = new BuildWorld();
        int a = w.Eco.AddObject(Side.Hand, Mandate, null, true);
        int b = w.Eco.AddObject(Side.Hand, "harvester", null, false);
        int c = w.Eco.AddObject(Side.Hand, "Gleaner", Home, true);
        int d = w.Eco.AddObject(Side.Hand, "Pacifiers", Home, true);
        int e = w.Fleets.AddFleet(Side.Collective, "F", new[] { "Mender" }, Home);
        Assert.Equal(2750, w.Obj(a).Hull);
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, w.Obj(a).Hits!);
        Assert.Equal(25000, w.Obj(b).Hull);
        Assert.All(new[] { c, d }, id => { Assert.Equal(0, w.Obj(id).Hull); Assert.Null(w.Obj(id).Hits); });
        Assert.Equal(600, w.Obj(w.State.Fleets[e].Ships[0]).Hull);
        Assert.False(ObjectRegistry.IsDamagedForTravel(w.Obj(a)));
    }

    // m3-ships-and-fighters §The ship object: a ship built by a yard is created at the class hull
    [Fact]
    public void A_built_ship_has_its_class_hull()
    {
        var w = new BuildWorld(wireFleets: true);
        w.Hold(Home, Side.Hand);
        w.Yard(Home, FacilityKind.OrbitalShipyard);
        w.Refined(Side.Hand, 200);
        Assert.Null(w.TryOrder(Side.Hand, Home, YardFamily.Shipyard, "Decree", 1));
        var ship = w.Eco.Objects.Single(o => o.Class == "Decree");
        Assert.Equal(1800, ship.Hull);
        Assert.Equal(6, ship.Hits!.Count);
    }
}
