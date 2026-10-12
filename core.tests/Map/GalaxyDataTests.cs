using Rebellion.Core.Data;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Tests.Map;

// rules: m1-galaxy-data (tests G1-G13 and validation checks 1-10), on the real CSV files.
public class GalaxyDataTests
{
    private static Galaxy G => TestData.RealGalaxy;

    [Fact]
    public void G1_RealFilesLoadWithExpectedCounts() // m1-galaxy-data G1
    {
        Assert.Equal(362, G.Systems.Count);
        Assert.Equal(521, G.Lanes.Count);
        Assert.Equal(37, G.Sectors.Count);
        Assert.Equal(59, G.Lanes.Count(l => l.Tier == 1));
        Assert.Equal(84, G.Lanes.Count(l => l.Tier == 2));
        Assert.Equal(378, G.Lanes.Count(l => l.Tier == 3));
        Assert.Equal(40, G.Lanes.Count(l => l.Kind == "route"));
        Assert.Equal(19, G.Lanes.Count(l => l.Kind == "route-inferred"));
        Assert.Empty(G.Warnings); // check 10: stored lane_count and chokepoint agree with the recomputed values
    }

    [Fact]
    public void G2_SectorSizes() // m1-galaxy-data G2
    {
        Assert.Equal(10, G.Sectors.Max(s => s.Members.Count));
        var hist = G.Sectors.GroupBy(s => s.Members.Count).ToDictionary(x => x.Key, x => x.Count());
        Assert.Equal(new Dictionary<int, int> { [10] = 31, [9] = 4, [8] = 1, [6] = 1 }, hist);
        Assert.Equal(360, G.Sectors.Sum(s => s.Members.Count));
    }

    [Fact]
    public void G3_GraphReachability() // m1-galaxy-data G3
    {
        Assert.Equal(360, G.Systems.Count(s => G.NeighboursOf(s.Index).Count > 0));
        Assert.Empty(G.NeighboursOf(G.Get("Ilum").Index));
        Assert.Empty(G.NeighboursOf(G.Get("Csilla").Index));
        var seen = new HashSet<int> { G.Get("Coruscant").Index };
        var stack = new Stack<int>(seen);
        while (stack.Count > 0)
            foreach (var n in G.NeighboursOf(stack.Pop()))
                if (seen.Add(n.System)) stack.Push(n.System);
        Assert.Equal(360, seen.Count);
    }

    [Fact]
    public void G4_ChokepointsAndLaneCounts() // m1-galaxy-data G4
    {
        var chokes = GalaxyLoader.Chokepoints(G);
        Assert.Equal(83, chokes.Count);
        Assert.Equal(49, G.Systems.Count(s => G.NeighboursOf(s.Index).Count == 1));
        foreach (var s in G.Systems)
        {
            Assert.Equal(int.Parse(s.Carried["lane_count"]), G.NeighboursOf(s.Index).Count);
            Assert.Equal(s.Carried["chokepoint"] == "1", chokes.Contains(s.Index));
        }
    }

    [Fact]
    public void G5_RouteInferredLaneIsTier1() // m1-galaxy-data G5
    {
        var l = G.FindLane("Coruscant", "Corellia")!;
        Assert.Equal("route-inferred", l.Kind);
        Assert.Equal("Corellian Run", l.Routes);
        Assert.Equal(1, l.Tier);
        Assert.Equal(300, l.FactorPercent);
    }

    [Fact]
    public void G6_OfficialLaneIsTier2() // m1-galaxy-data G6
    {
        var l = G.FindLane("Corellia", "Nubia")!;
        Assert.Equal("official", l.Kind);
        Assert.Equal(2, l.Tier);
        Assert.Equal(150, l.FactorPercent);
    }

    [Fact]
    public void G7_GeneratedLaneIsTier3() // m1-galaxy-data G7
    {
        var l = G.FindLane("Coruscant", "Skako")!;
        Assert.Equal("generated", l.Kind);
        Assert.Equal(3, l.Tier);
        Assert.Equal(100, l.FactorPercent);
    }

    [Fact]
    public void G8_BaseDistanceCoruscantCorellia() // m1-galaxy-data G8
    {
        var c = G.Get("Coruscant"); var k = G.Get("Corellia");
        Assert.Equal((366, 390), (c.MapX, c.MapY));
        Assert.Equal((381, 489), (k.MapX, k.MapY));
        Assert.Equal(10026, 15 * 15 + 99 * 99);
        Assert.Equal(100, (int)GalaxyLoader.Isqrt(10026));
        Assert.Equal(20, G.FindLane("Coruscant", "Corellia")!.BaseDistance);
    }

    [Fact]
    public void G9_BaseDistanceCoruscantSkako() // m1-galaxy-data G9
    {
        Assert.Equal((355, 385), (G.Get("Skako").MapX, G.Get("Skako").MapY));
        Assert.Equal(12, (int)GalaxyLoader.Isqrt(146));
        Assert.Equal(2, G.FindLane("Coruscant", "Skako")!.BaseDistance);
    }

    [Fact]
    public void G10_LaneToMissingSystemFails() // m1-galaxy-data G10 (check 2)
    {
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(TestData.PlanetsText, TestData.LanesText + "Coruscant,Nowhere,generated,\n", TestData.Tiers));
        Assert.Contains("Nowhere", ex.Message);
        Assert.Contains("Coruscant", ex.Message);
    }

    [Fact]
    public void G11_EleventhSystemInASectorFails() // m1-galaxy-data G11 (check 6)
    {
        var full = G.Sectors.First(s => s.Members.Count == 10);
        var row = $"Zzz Extra,{full.Name},{full.Group},,,,10,10,,,,0,0\n";
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(TestData.PlanetsText + row, TestData.LanesText, TestData.Tiers));
        Assert.Contains(full.Name, ex.Message);
    }

    [Fact]
    public void G12_SamePairTwiceFails() // m1-galaxy-data G12 (check 3)
    {
        var ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(TestData.PlanetsText, TestData.LanesText + "Arkanis,Ryloth,route,Corellian Run\n", TestData.Tiers));
        Assert.Contains("already has a lane", ex.Message);
    }

    [Fact]
    public void G13_NeighboursBothWaysAndSorted() // m1-galaxy-data G13
    {
        var ryloth = G.Get("Ryloth"); var arkanis = G.Get("Arkanis");
        Assert.Contains(G.NeighboursOf(ryloth.Index), n => n.System == arkanis.Index);
        Assert.Contains(G.NeighboursOf(arkanis.Index), n => n.System == ryloth.Index);
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
    public void Check7_OutsidersHaveNoLanesAndOthersHaveOne()
    {
        // a system inside a sector with no lane
        var ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(Tiny + "C,S,core,3,3\n", TinyLane, TestData.Tiers));
        Assert.Contains("at least one lane", ex.Message);
        // Ilum with a lane
        var p = Tiny + $"Ilum,{Galaxy.OutsideSectorsLabel},,9,9\n";
        ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(p, TinyLane + "A,Ilum,generated,\n", TestData.Tiers));
        Assert.Contains("no lanes", ex.Message);
        // someone else outside sectors
        ex = Assert.Throws<DataLoadException>(() =>
            GalaxyLoader.Load(Tiny + $"Hermit,{Galaxy.OutsideSectorsLabel},,9,9\n", TinyLane, TestData.Tiers));
        Assert.Contains("Hermit", ex.Message);
    }

    [Fact]
    public void Check8_DisconnectedSectorsFail()
    {
        var p = Tiny + "C,S,core,3,3\nD,S,core,4,4\n";
        var ex = Assert.Throws<DataLoadException>(() => GalaxyLoader.Load(p, TinyLane + "C,D,generated,\n", TestData.Tiers));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public void Check9_CountsMismatchIsReported()
    {
        var expected = ExpectedGalaxyCounts.Load(Path.Combine(TestData.DesignDir, GalaxyLoader.CountsFile));
        expected.Check(G); // the real data matches
        var ex = Assert.Throws<DataLoadException>(() => (expected with { Lanes = 520 }).Check(G));
        Assert.Contains("lanes: expected 520, found 521", ex.Message);
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
        Assert.Equal(362, sv.Count);
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
        var all = ShipData.LoadAll(TestData.InterimDir);
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
