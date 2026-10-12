using Rebellion.Core.Map;
using Rebellion.Core.Ships;
using Rebellion.Core.Travel;

namespace Rebellion.Core.Tests.Travel;

// rules: m1-travel tests T1-T18, T20, T21 (T19 needs the clock and fleets; not in this package).
public class TravelTests
{
    private static Galaxy G => TestData.RealGalaxy;
    private static readonly Lazy<IReadOnlyDictionary<string, ShipClass>> Ships = new(() => ShipData.LoadAll(TestData.InterimDir));

    private static int Hop(string a, string b, int speed) => TravelFormula.HopTicks(G.FindLane(a, b)!, speed);

    private static int SpeedOf(params string[] classes) =>
        TravelFormula.FleetSpeed(
            classes.Select(c => Ships.Value[c]).Where(s => s.IsCapital).Select(s => s.Speed(false)),
            classes.Select(c => Ships.Value[c]).Where(s => !s.IsCapital).Select(s => s.Speed(false)));

    [Fact]
    public void T1_CoruscantCorelliaTier1() // m1-travel T1
    {
        Assert.Equal(20, G.FindLane("Coruscant", "Corellia")!.BaseDistance);
        Assert.Equal(new[] { 6, 5, 4, 3 }, new[] { 100, 80, 60, 50 }.Select(s => Hop("Coruscant", "Corellia", s)));
    }

    [Fact]
    public void T2_CoruscantSkakoTier3() // m1-travel T2
    {
        Assert.Equal(2, G.FindLane("Coruscant", "Skako")!.BaseDistance);
        Assert.Equal(new[] { 2, 1, 1 }, new[] { 100, 80, 50 }.Select(s => Hop("Coruscant", "Skako", s)));
    }

    [Fact]
    public void T3_CorelliaHosnianPrimeTier1() // m1-travel T3
    {
        Assert.Equal(7, G.FindLane("Corellia", "Hosnian Prime")!.BaseDistance);
        Assert.Equal(1, G.FindLane("Corellia", "Hosnian Prime")!.Tier);
        Assert.Equal(new[] { 2, 1, 1 }, new[] { 100, 80, 50 }.Select(s => Hop("Corellia", "Hosnian Prime", s)));
    }

    [Fact]
    public void T4_CorelliaNubiaTier2() // m1-travel T4
    {
        Assert.Equal(6, G.FindLane("Corellia", "Nubia")!.BaseDistance);
        Assert.Equal(new[] { 4, 3 }, new[] { 100, 80 }.Select(s => Hop("Corellia", "Nubia", s)));
    }

    [Theory]
    [InlineData(100)] [InlineData(80)] [InlineData(50)] [InlineData(1000)]
    public void T5_ShortHopCostsOneDay(int speed) // m1-travel T5
    {
        Assert.Equal(0, TravelFormula.BaseDistance(10, 10, 14, 10)); // 4 units apart
        Assert.Equal(1, TravelFormula.HopTicks(0, speed, 300));
    }

    [Fact]
    public void T6_FleetSpeedIsLargestRating() // m1-travel T6
        => Assert.Equal(100, SpeedOf("Imperial Star Destroyer", "Medium Transport"));

    [Fact]
    public void T7_MonCalamariAndIsd() // m1-travel T7
        => Assert.Equal(80, SpeedOf("Mon Calamari Cruiser", "Imperial Star Destroyer"));

    [Fact]
    public void T8_TieFighterAloneIsImmobile() // m1-travel T8
    {
        var speed = SpeedOf("TIE Fighter");
        Assert.Equal(0, speed);
        var r = new Pathfinder(G).Find("Coruscant", "Corellia", speed);
        Assert.Equal(PathStatus.Immobile, r.Status);
    }

    [Fact]
    public void CapitalShipRatingZeroMakesFleetImmobileAndSquadronsAreIgnored() // m1-travel §Rule (Classic)
    {
        Assert.Equal(0, TravelFormula.FleetSpeed(new[] { 80, 0 }, Array.Empty<int>()));
        Assert.Equal(80, TravelFormula.FleetSpeed(new[] { 80 }, new[] { 0, 60 })); // carried squadrons never count
        Assert.Equal(60, SpeedOf("X-wing")); // squadron-only fleet uses squadron ratings
        Assert.Equal(100, TravelFormula.FleetSpeed(Array.Empty<int>(), Array.Empty<int>())); // unrated: default 100
    }

    private static void AssertPath(PathResult r, string[] names, int[] hops, int total, int? hopCount = null)
    {
        Assert.True(r.Ok, r.Status.ToString());
        if (names.Length > 0) Assert.Equal(names, r.Names(G));
        if (hops.Length > 0) Assert.Equal(hops, r.HopTicks);
        Assert.Equal(total, r.TotalTicks);
        Assert.Equal(r.HopTicks.Sum(), r.TotalTicks);
        if (hopCount is int h) Assert.Equal(h, r.Hops);
    }

    private static readonly string[] CoruscantHoth =
        { "Coruscant", "Corellia", "Hosnian Prime", "Kinyen", "Anoat", "Bespin", "Hoth" };

    [Fact]
    public void T9_CoruscantToHothAtSpeed80() // m1-travel T9
        => AssertPath(new Pathfinder(G).Find("Coruscant", "Hoth", 80), CoruscantHoth, new[] { 5, 1, 6, 7, 1, 1 }, 21);

    [Fact]
    public void T10_CoruscantToHothAtSpeed100And50() // m1-travel T10
    {
        var pf = new Pathfinder(G);
        AssertPath(pf.Find("Coruscant", "Hoth", 100), CoruscantHoth, new[] { 6, 2, 7, 9, 2, 2 }, 28);
        AssertPath(pf.Find("Coruscant", "Hoth", 50), CoruscantHoth, new[] { 3, 1, 3, 4, 1, 1 }, 13);
    }

    [Fact]
    public void T11_CorelliaToBespinAtSpeed80() // m1-travel T11
        => AssertPath(new Pathfinder(G).Find("Corellia", "Bespin", 80),
            new[] { "Corellia", "Hosnian Prime", "Kinyen", "Anoat", "Bespin" }, Array.Empty<int>(), 15, 4);

    [Fact]
    public void T12_GleeAnselmToFlaxPathDependsOnSpeed() // m1-travel T12
    {
        var pf = new Pathfinder(G);
        var p50 = pf.Find("Glee Anselm", "Flax", 50);
        var p80 = pf.Find("Glee Anselm", "Flax", 80);
        var p100 = pf.Find("Glee Anselm", "Flax", 100);
        AssertPath(p50, Array.Empty<string>(), Array.Empty<int>(), 20, 13);
        AssertPath(p80, Array.Empty<string>(), Array.Empty<int>(), 30, 13);
        AssertPath(p100, Array.Empty<string>(), Array.Empty<int>(), 45, 13);
        Assert.Contains("Dorin", p50.Names(G));
        Assert.DoesNotContain("Fef", p50.Names(G));
        Assert.Contains("Fef", p80.Names(G));
        Assert.DoesNotContain("Dorin", p80.Names(G));
        Assert.Contains("Fef", p100.Names(G));
        Assert.DoesNotContain("Dorin", p100.Names(G));
    }

    [Fact]
    public void T13_SerennoToFelucia() // m1-travel T13
    {
        var pf = new Pathfinder(G);
        var p100 = pf.Find("Serenno", "Felucia", 100);
        AssertPath(p100, Array.Empty<string>(), Array.Empty<int>(), 35, 10);
        foreach (var n in new[] { "Ubduria", "Obroa-skai", "Montross" }) Assert.Contains(n, p100.Names(G));
        var p80 = pf.Find("Serenno", "Felucia", 80);
        AssertPath(p80, Array.Empty<string>(), Array.Empty<int>(), 27, 8);
        foreach (var n in new[] { "Chazwa", "Taanab", "Lantillies" }) Assert.Contains(n, p80.Names(G));
    }

    [Fact]
    public void T14_TieOnTicksFewestHopsWins() // m1-travel T14
    {
        var g = TestData.Synthetic(new[] { ("A", 0, 0), ("B", 5, 0), ("D", 10, 0) },
            new[] { ("A", "B"), ("B", "D"), ("A", "D") });
        var r = new Pathfinder(g).Find("A", "D", 100);
        Assert.Equal(1, g.FindLane("A", "B")!.BaseDistance);
        Assert.Equal(new[] { 1, 1 }, new[] { Hop(g, "A", "B"), Hop(g, "B", "D") });
        Assert.Equal(2, Hop(g, "A", "D"));
        Assert.Equal(new[] { "A", "D" }, r.Names(g));
        Assert.Equal(2, r.TotalTicks);
    }

    private static int Hop(Galaxy g, string a, string b) => TravelFormula.HopTicks(g.FindLane(a, b)!, 100);

    [Fact]
    public void T15_TieOnTicksAndHopsTakesFirstNameHopByHop() // m1-travel T15
    {
        // The sheet's coordinates have C at y=-50, which the loader refuses (0..1023); every y is shifted by +50.
        var g = TestData.Synthetic(new[] { ("A", 0, 50), ("B", 50, 100), ("C", 50, 0), ("D", 100, 50) },
            new[] { ("A", "B"), ("A", "C"), ("B", "D"), ("C", "D") });
        Assert.Equal(new[] { 14, 14, 14, 14 }, g.Lanes.Select(l => TravelFormula.HopTicks(l, 100)));
        var r = new Pathfinder(g).Find("A", "D", 100);
        Assert.Equal(new[] { "A", "B", "D" }, r.Names(g));
        Assert.Equal(28, r.TotalTicks);
    }

    [Fact]
    public void T16_NavigatorOnIsdFleet() // m1-travel T16
    {
        var fleet = SpeedOf("Imperial Star Destroyer");
        Assert.Equal(80, fleet);
        var nav = TravelFormula.NavigatorSpeed(fleet, 80);
        Assert.Equal(64, nav);
        Assert.Equal(4, Hop("Coruscant", "Corellia", nav)); // 20 * 64 / 300
    }

    [Fact]
    public void T17_NavigatorFloor() // m1-travel T17
    {
        Assert.Equal(25, TravelFormula.NavigatorSpeed(20, 80)); // 16, raised to the floor
        Assert.Equal(0, TravelFormula.NavigatorSpeed(0, 80));  // immobile stays immobile
    }

    [Fact]
    public void T18_HanMissionGroupSpeed50() // m1-travel T18
        => Assert.Equal(3, Hop("Coruscant", "Corellia", 50));

    [Fact]
    public void T20_OrderToIlumIsUnreachable() // m1-travel T20
    {
        var pf = new Pathfinder(G);
        Assert.Equal(PathStatus.Unreachable, pf.Find("Coruscant", "Ilum", 80).Status);
        Assert.Equal(PathStatus.Unreachable, pf.Find("Csilla", "Coruscant", 80).Status);
    }

    [Fact]
    public void RejectionReasons() // m1-travel §Defaults (AlreadyThere, Immobile), §Path rule 6
    {
        var pf = new Pathfinder(G);
        Assert.Equal(PathStatus.AlreadyThere, pf.Find("Coruscant", "Coruscant", 80).Status);
        Assert.Equal(PathStatus.UnknownSystem, pf.Find("Coruscant", "Nowhere", 80).Status);
        Assert.Equal(PathStatus.UnknownSystem, pf.Find("Nowhere", "Coruscant", 80).Status);
        Assert.Equal(PathStatus.Immobile, pf.Find("Coruscant", "Corellia", 0).Status);
        Assert.Empty(pf.Find("Coruscant", "Ilum", 80).Systems);
    }

    [Fact]
    public void T21_SameInputsGiveIdenticalPaths() // m1-travel T21
    {
        var names = new[] { "Coruscant", "Hoth", "Flax", "Felucia", "Bespin", "Kessel", "Tatooine" };
        List<string> Run()
        {
            var pf = new Pathfinder(TestData.RealGalaxy);
            var lines = new List<string>();
            foreach (var a in names) foreach (var b in names) foreach (var sp in new[] { 50, 80, 100 })
            {
                var r = pf.Find(a, b, sp);
                lines.Add($"{a}>{b}@{sp}:{r.Status}:{string.Join("/", r.Names(G))}:{r.TotalTicks}");
            }
            return lines;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void PathsAreCachedPerStartEndAndSpeed() // m1-travel §Path rule 4
    {
        var pf = new Pathfinder(G);
        var a = pf.Find("Glee Anselm", "Flax", 50);
        Assert.Same(a, pf.Find("Glee Anselm", "Flax", 50));
        var b = pf.Find("Glee Anselm", "Flax", 80);
        Assert.NotSame(a, b);
        Assert.Equal(2, pf.CachedCount);
    }

    [Fact]
    public void EveryHopIsAtLeastOneDay() // m1-travel §Per-hop formula
    {
        var r = new Pathfinder(G).Find("Coruscant", "Hoth", 50);
        Assert.All(r.HopTicks, t => Assert.True(t >= 1));
        Assert.True(r.TotalTicks >= r.Hops);
    }
}
