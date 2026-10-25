using Praedra.Core.Data;
using Praedra.Core.Map;
using Praedra.Core.Ships;

namespace Praedra.Core.Tests.Map;

// rules: m1-galaxy-data (tests G1-G13 and validation checks 1-10), on the real CSV files.
public class GalaxyDataTests
{
    private static Galaxy G => TestData.RealGalaxy;

    [Fact]
    public void G1_RealFilesLoadWithExpectedCounts() // m1-galaxy-data G1 (v6 spiral data, planets/16)
    {
        // v6 re-pin 2026-10-24: the spiral map (planets/16) has 390 links, all great-route links of kind route, no route-inferred.
        Assert.Equal(555, G.Systems.Count);
        Assert.Equal(390, G.Lanes.Count);
        Assert.Equal(64, G.Sectors.Count);
        Assert.Equal(102, G.Lanes.Count(l => l.Tier == 1));
        Assert.Equal(288, G.Lanes.Count(l => l.Tier == 2));
        Assert.Equal(0, G.Lanes.Count(l => l.Tier == 3));
        Assert.Equal(102, G.Lanes.Count(l => l.Kind == "route"));
        Assert.Equal(0, G.Lanes.Count(l => l.Kind == "route-inferred"));
        Assert.Equal(19, G.Lanes.Count(l => l.Kind == "bridge"));
        Assert.Empty(G.Warnings); // check 10: stored lane_count and chokepoint agree with the recomputed values
    }

    [Fact]
    public void G2_SectorSizes() // m1-galaxy-data G2
    {
        // v6 re-pin 2026-10-24: the spiral re-cut makes sectors of 8 or 9 (planets/16); the cap stays 10 (Paul 2026-10-16).
        Assert.True(G.Sectors.Max(s => s.Members.Count) <= GalaxyRules.Default.MaxSectorSize);
        var hist = G.Sectors.GroupBy(s => s.Members.Count).ToDictionary(x => x.Key, x => x.Count());
        Assert.Equal(new Dictionary<int, int> { [9] = 41, [8] = 23 }, hist);
        Assert.Equal(553, G.Sectors.Sum(s => s.Members.Count));
    }

    [Fact]
    public void G3_RouteSystems() // m1-galaxy-data G3: 358 systems on a route, the rest reached by free travel only
    {
        // v6 re-pin 2026-10-24: 358 on a route (was 332); Yavin is on a route now, Hoth is the off-route example.
        Assert.Equal(358, G.Systems.Count(s => G.OnRoute(s.Index)));
        Assert.Equal(358, G.Systems.Count(s => G.NeighboursOf(s.Index).Count > 0));
        Assert.Empty(G.NeighboursOf(G.Get("Ilum").Index));
        Assert.Empty(G.NeighboursOf(G.Get("Csilla").Index));
        Assert.False(G.OnRoute(G.Get("Hoth").Index));
        Assert.True(G.OnRoute(G.Get("Coruscant").Index));
    }

    [Fact]
    public void G4_ChokepointsAndLaneCounts() // m1-galaxy-data G4 (chokepoints per route network)
    {
        var chokes = GalaxyLoader.Chokepoints(G);
        Assert.Equal(179, chokes.Count); // v6 re-pin 2026-10-24 (was 98)
        Assert.Equal(64, G.Systems.Count(s => G.NeighboursOf(s.Index).Count == 1)); // v6 re-pin 2026-10-24 (was 45)
        foreach (var s in G.Systems)
        {
            Assert.Equal(int.Parse(s.Carried["lane_count"]), G.NeighboursOf(s.Index).Count);
            Assert.Equal(s.Carried["chokepoint"] == "1", chokes.Contains(s.Index));
        }
    }

    [Fact]
    public void G5_RouteInferredLaneIsTier1() // m1-galaxy-data G5
    {
        // v6 re-pin 2026-10-24: v6 has no route-inferred links; the kind stays tier 1 in the tier table, and a real great-route
        // link (Coruscant-Tralus, Trunk Line) is tier 1 with factor 300.
        Assert.Equal(1, TestData.Tiers.TierOfKind("route-inferred"));
        var l = G.FindLane("Coruscant", "Tralus")!;
        Assert.Equal("route", l.Kind);
        Assert.Equal("Trunk Line", l.Routes);
        Assert.Equal(1, l.Tier);
        Assert.Equal(300, l.FactorPercent);
    }

    [Fact]
    public void G6_OfficialLaneIsTier2() // m1-galaxy-data G6
    {
        var l = G.FindLane("Coruscant", "Uveron")!; // v6 re-pin 2026-10-24: Coruscant's minor link (was Corulag)
        Assert.Equal("official", l.Kind);
        Assert.Equal(2, l.Tier);
        Assert.Equal(150, l.FactorPercent);
        Assert.Equal("bridge", G.FindLane("Gizer", "Kinyen")!.Kind); // v6 re-pin 2026-10-24 (was Mirial-Jelucan)
        Assert.Equal(2, G.FindLane("Gizer", "Kinyen")!.Tier); // bridge: a minor link
    }

    [Fact]
    public void G7_NoGeneratedLanes() // m1-galaxy-data G7: the v6 data has no generated placeholder lanes
    {
        Assert.DoesNotContain(G.Lanes, l => l.Kind == "generated");
        Assert.Null(G.FindLane("Coruscant", "Yavin"));
    }

    [Fact]
    public void G8_BaseDistanceCoruscantCorellia() // m1-galaxy-data G8
    {
        // v6 re-pin 2026-10-24: Coruscant sits at the centre (500,500); Corellia is no longer linked to it, so the stored
        // lane base distance is checked on Coruscant-Tralus (503,471): 3*3 + 29*29 = 850, isqrt 29, /5 = 5.
        var c = G.Get("Coruscant"); var k = G.Get("Corellia"); var t = G.Get("Tralus");
        Assert.Equal((500, 500), (c.MapX, c.MapY));
        Assert.Equal((454, 424), (k.MapX, k.MapY));
        Assert.Equal(7892, 46 * 46 + 76 * 76);
        Assert.Equal(88, (int)GalaxyLoader.Isqrt(7892));
        Assert.Equal((503, 471), (t.MapX, t.MapY));
        Assert.Equal(5, G.FindLane("Coruscant", "Tralus")!.BaseDistance);
    }

    [Fact]
    public void G9_NearbyIsSymmetricSortedAndConnected() // m1-galaxy-data §Derived fields (Nearby)
    {
        foreach (var s in G.Systems)
        {
            var near = G.NearOf(s.Index);
            Assert.True(near.Count >= 2);
            Assert.Equal(near.OrderBy(x => x).ToList(), near.ToList());
            Assert.DoesNotContain(s.Index, near);
            foreach (int j in near) Assert.Contains(s.Index, G.NearOf(j));
            foreach (var nb in G.NeighboursOf(s.Index)) Assert.Contains(nb.System, near); // route links are always nearby
        }
        var seen = new HashSet<int> { G.Get("Coruscant").Index };
        var stack = new Stack<int>(seen);
        while (stack.Count > 0) foreach (int n in G.NearOf(stack.Pop())) if (seen.Add(n)) stack.Push(n);
        Assert.Equal(553, seen.Count(i => !G.Systems[i].IsOutsideSectors));
    }

    [Fact]
    public void G10_LaneToMissingSystemFails() // m1-galaxy-data G10 (check 2)
    {
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(TestData.PlanetsText, TestData.LanesText + "Coruscant,Nowhere,generated,,,\n", TestData.Tiers));
        Assert.Contains("Nowhere", ex.Message);
        Assert.Contains("Coruscant", ex.Message);
    }

    [Fact]
    public void G11_EleventhSystemInASectorFails() // m1-galaxy-data G11 (check 6): a 9-system sector plus two
    {
        var full = G.Sectors.First(s => s.Members.Count == 9);
        var rows = $"Zzz Extra,{full.Name},{full.Group},,,,10,10,,,,0,0,0,,\nZzz Extra2,{full.Name},{full.Group},,,,12,12,,,,0,0,0,,\n";
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(TestData.PlanetsText + rows, TestData.LanesText, TestData.Tiers));
        Assert.Contains(full.Name, ex.Message);
    }

    [Fact]
    public void G12_SamePairTwiceFails() // m1-galaxy-data G12 (check 3)
    {
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(TestData.PlanetsText, TestData.LanesText + "Tralus,Coruscant,route,Trunk Line,,\n", TestData.Tiers));
        Assert.Contains("already has a lane", ex.Message);
    }

    [Fact]
    public void G13_NeighboursBothWaysAndSorted() // m1-galaxy-data G13
    {
        var a = G.Get("Coruscant"); var b = G.Get("Tralus"); // v6 re-pin 2026-10-24 (Corellia-Duros is no longer a link)
        Assert.Contains(G.NeighboursOf(a.Index), n => n.System == b.Index);
        Assert.Contains(G.NeighboursOf(b.Index), n => n.System == a.Index);
        foreach (var s in G.Systems)
        {
            var names = G.NeighboursOf(s.Index).Select(n => G.Systems[n.System].Name).ToList();
            var sorted = names.OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(sorted, names);
        }
        // systems themselves are in ordinal name order
        var all = G.Systems.Select(s => s.Name).ToList();
        Assert.Equal(all.OrderBy(x => x, StringComparer.Ordinal).ToList(), all);
    }

    // ---- validation checks beyond G10-G12 ----

    private const string Tiny = "name,sector,group,map_x,map_y\nA,S,core,1,1\nB,S,core,2,2\n";
    private const string TinyLane = "from,to,kind,routes\nA,B,generated,\n";

    [Fact]
    public void Check1_DuplicateAndEmptyNamesFail()
    {
        var ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(Tiny + "A,S,core,3,3\n", TinyLane, TestData.Tiers));
        Assert.Contains("duplicate system name 'A'", ex.Message);
        Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(Tiny + ",S,core,3,3\n", TinyLane, TestData.Tiers));
    }

    [Fact]
    public void Check3_SelfLaneFails()
    {
        var ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(Tiny, TinyLane + "A,A,generated,\n", TestData.Tiers));
        Assert.Contains("itself", ex.Message);
    }

    [Theory]
    [InlineData("A,B,fancy,", "unknown kind")]
    [InlineData("A,B,route,", "routes")]          // tier 1 kind needs a route name
    [InlineData("A,B,generated,Hydian Way", "routes")] // other kinds must have none
    public void Check4_KindAndRoutesFail(string lane, string fragment)
    {
        var ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(Tiny, "from,to,kind,routes\n" + lane + "\n", TestData.Tiers));
        Assert.Contains(fragment, ex.Message);
    }

    [Theory]
    [InlineData("A,S,core,1024,1")]
    [InlineData("A,S,core,-1,1")]
    [InlineData("A,S,core,x,1")]
    public void Check5_MapPositionMustBeIntegerInRange(string row)
    {
        Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load("name,sector,group,map_x,map_y\n" + row + "\nB,S,core,2,2\n", TinyLane, TestData.Tiers));
    }

    [Fact]
    public void Check6_MixedGroupInSectorFails()
    {
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load("name,sector,group,map_x,map_y\nA,S,core,1,1\nB,S,outer_rim,2,2\n", TinyLane, TestData.Tiers));
        Assert.Contains("mixes groups", ex.Message);
    }

    [Fact]
    public void Check7_OutsidersHaveNoLanesOthersMayHaveNone() // since 2026-10-16 a system may lie off every route (free travel)
    {
        var g = GalaxyLoader.Load(Tiny + "C,S,core,3,3\n", TinyLane, TestData.Tiers);
        Assert.False(g.OnRoute(g.Get("C").Index));
        // Ilum with a lane
        var p = Tiny + $"Ilum,{Galaxy.OutsideSectorsLabel},,9,9\n";
        var ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(p, TinyLane + "A,Ilum,generated,\n", TestData.Tiers));
        Assert.Contains("no route links", ex.Message);
        // someone else outside sectors
        ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(Tiny + $"Hermit,{Galaxy.OutsideSectorsLabel},,9,9\n", TinyLane, TestData.Tiers));
        Assert.Contains("Hermit", ex.Message);
    }

    [Fact]
    public void Check8_SeparateRouteNetworksLoad() // the old "one connected lane graph" check is gone (m1-galaxy-data check 8, retired 2026-10-16)
    {
        var p = Tiny + "C,S,core,3,3\nD,S,core,4,4\n";
        var g = GalaxyLoader.Load(p, TinyLane + "C,D,generated,\n", TestData.Tiers);
        Assert.Equal(2, g.Lanes.Count);
    }

    [Fact]
    public void Check9_CountsMismatchIsReported()
    {
        var expected = ExpectedGalaxyCounts.Load(Path.Combine(TestData.DesignDir, GalaxyLoader.CountsFile));
        expected.Check(G); // the real data matches
        var ex = Assert.Throws<DataLoadException>(() => (expected with { Lanes = 520 }).Check(G));
        Assert.Contains("lanes: expected 520, found 390", ex.Message);
    }

    [Fact]
    public void Check10_StoredColumnMismatchIsOnlyAWarning()
    {
        var p = "name,sector,group,map_x,map_y,chokepoint,lane_count\nA,S,core,1,1,1,5\nB,S,core,2,2,0,1\n";
        var g = GalaxyLoader.Load(p, TinyLane, TestData.Tiers);
        Assert.Equal(2, g.Warnings.Count); // A: lane_count 5 vs 1, chokepoint 1 vs 0
        Assert.Contains(g.Warnings, w => w.Contains("lane_count"));
        Assert.Contains(g.Warnings, w => w.Contains("chokepoint"));
    }

    [Fact]
    public void TierFactorBelow100IsRefused() // m1-galaxy-data §Defaults
    {
        Assert.Throws<DataLoadException>(() => LaneTiers.Parse("tier,factor_percent,kinds\n1,99,route\n"));
        // above 300 is allowed
        var t = LaneTiers.Parse("tier,factor_percent,kinds\n1,1000,route|route-inferred\n2,150,official\n3,100,generated\n");
        Assert.Equal(1000, t.FactorOfTier(1));
    }

    [Fact]
    public void TierTableComesFromData()
    {
        var t = TestData.Tiers;
        Assert.Equal(300, t.FactorOfTier(1));
        Assert.Equal(150, t.FactorOfTier(2));
        Assert.Equal(100, t.FactorOfTier(3));
        Assert.Equal(1, t.TierOfKind("route-inferred"));
    }

    [Fact]
    public void CarriedColumnsAreKept()
    {
        var c = G.Get("Coruscant");
        Assert.Equal("Core", c.Carried["canon_region"]);
        Assert.Contains("origin", c.Carried.Keys);
        Assert.DoesNotContain("map_x", c.Carried.Keys);
    }

    [Fact]
    public void CsvHandlesQuotesBomAndCrlf()
    {
        var t = CsvTable.Parse("﻿a,b\r\n\"x,1\",\"he said \"\"hi\"\"\"\r\n\r\n3,\"two\nlines\"\r\n", "t.csv");
        Assert.Equal(2, t.RowCount);
        Assert.Equal("x,1", t.Get(0, "a"));
        Assert.Equal("he said \"hi\"", t.Get(0, "b"));
        Assert.Equal("two\nlines", t.Get(1, "b"));
        Assert.Throws<DataLoadException>(() => CsvTable.Parse("a,b\n1\n", "t.csv"));
        Assert.Throws<DataLoadException>(() => CsvTable.Parse("a\n\"open\n", "t.csv"));
    }

    // ---- slot values ----

    [Fact]
    public void SlotValuesLoadForEverySystem()
    {
        var sv = SlotValues.Load(Path.Combine(TestData.DesignDir, SlotValues.File), G);
        Assert.Equal(555, sv.Count);
        var a = sv.Of(G.Get("Abregado").Index);
        Assert.Equal((12, 9, true), (a.Energy, a.Raw, a.Populated));
    }

    [Fact]
    public void SlotValuesRefuseUnknownDuplicateAndMissingSystems()
    {
        var text = File.ReadAllText(Path.Combine(TestData.DesignDir, SlotValues.File));
        var ex = Assert.Throws<DataLoadException>(() => SlotValues.Parse(text + "Nowhere,X,core,Core,1,1,1,formula,\n", G));
        Assert.Contains("Nowhere", ex.Message);
        ex = Assert.Throws<DataLoadException>(() => SlotValues.Parse(text + "Abregado,Fondor Sector,inner_rim,Core,1,1,1,formula,\n", G));
        Assert.Contains("twice", ex.Message);
        var lines = text.Split('\n').ToList();
        lines.RemoveAt(2); // drop one system's row
        ex = Assert.Throws<DataLoadException>(() => SlotValues.Parse(string.Join("\n", lines), G));
        Assert.Contains("has no row", ex.Message);
    }

    // ---- ships ----

    [Fact]
    public void ShipDataLoads()
    {
        var all = ShipData.LoadAll(TestData.ClassicDir);
        Assert.Equal(30 + 8, all.Count);
        var isd = all["Imperial Star Destroyer"];
        Assert.True(isd.IsCapital);
        Assert.Equal((80, 100, 0, 480), (isd.Hyperdrive, isd.HyperdriveIfDamaged, isd.GravityWellProjector, isd.OverallAttackStrength));
        Assert.Equal(ShipSide.Empire, isd.Side);
        Assert.Equal(4, all["CC-7700 Frigate"].GravityWellProjector);
        Assert.Equal(4, all["Interdictor Cruiser"].GravityWellProjector);
        Assert.Equal(0, all["TIE Fighter"].Hyperdrive);
        Assert.False(all["TIE Fighter"].IsCapital);
        Assert.Equal(60, all["Mon Calamari Cruiser"].Speed(false));
        Assert.Equal(100, all["Mon Calamari Cruiser"].Speed(true));
        Assert.Equal(80, all["Corellian Corvette"].Speed(true)); // damaged rating 0 in the data: normal rating
    }
}
