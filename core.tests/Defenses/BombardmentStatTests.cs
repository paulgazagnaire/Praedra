using Rebellion.Core.Clock;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;

namespace Rebellion.Core.Tests.Defenses;

// m3-bombardment B8, B9, B10: statistical tests with the seeded game generator and the real module (one Step per bombardment).
public class BombardmentStatTests
{
    private const string Sys = BombWorld.Sys;
    private static readonly Side Emp = Side.Empire, All = Side.Alliance;

    // m3-bombardment B8: 10,000 single hits on one target of each bombardment defense 0..10: each observed rate within 2.0 points of exact
    [Theory]
    [MemberData(nameof(DefenseValues))]
    public void B8_SingleHitRateMatchesTenMinusDOverTen(int d)
    {
        var w = new BombWorld(stub: false, seed: (ulong)(800 + d), data: BombWorld.ArmyDefense(d));
        w.Hold(All);
        int fleet = w.Fleet(Emp, Sys, "Carrack Light Cruiser"); // P = 1: one hit per bombardment
        int destroyed = 0;
        const int trials = 10_000;
        for (int i = 0; i < trials; i++)
        {
            int reg = w.Regiment(All, BombWorld.Army);
            w.Bombard(Emp, fleet, Sys, BombardCategory.Military);
            w.Run(1);
            if (w.Eco.Find(reg) == null) destroyed++; else w.Eco.RemoveObject(reg);
        }
        double observed = 100.0 * destroyed / trials, exact = (10 - d) * 10.0;
        Assert.True(Math.Abs(observed - exact) <= 2.0, $"defense {d}: observed {observed:F2} percent, exact {exact}");
    }

    public static IEnumerable<object[]> DefenseValues() => Enumerable.Range(0, 11).Select(d => new object[] { d });

    // m3-bombardment B9: 4 hits on one Mon Calamari regiment (defense 9): destroyed with probability 1 - 0.9^4 = 34.39 percent (20,000 runs, within 1.0 point)
    [Fact]
    public void B9_FourHitsOnAMonCalamariRegiment()
    {
        var w = new BombWorld(stub: false, seed: 9);
        w.Hold(All);
        int fleet = w.Fleet(Emp, Sys, "Victory Destroyer"); // P = 4
        int destroyed = 0;
        const int trials = 20_000;
        for (int i = 0; i < trials; i++)
        {
            int reg = w.Regiment(All, "Mon Calamari Regiment");
            w.Bombard(Emp, fleet, Sys, BombardCategory.Military);
            w.Run(1);
            if (w.Eco.Find(reg) == null) destroyed++; else w.Eco.RemoveObject(reg);
        }
        double observed = 100.0 * destroyed / trials;
        Assert.True(Math.Abs(observed - 34.39) <= 1.0, $"observed {observed:F2} percent, exact 34.39");
    }

    // m3-bombardment B10: reference means over 20,000 runs (tolerance 0.05); depends on "targets removed on destruction" and "hits discarded when empty"
    [Theory]
    [InlineData(2, false, 3.93)]   // 8 hits on 4 Army regiments plus an LNR I (pool Military): 3.93 of 5
    [InlineData(6, false, 5.00)]   // 24 hits: 5.00
    [InlineData(15, true, 5.99)]   // 20 hits (P = 60, GenCore I absorbs 40) on 4 Army + LNR I + GenCore I: 5.99 of 6
    public void B10_ReferenceMeans(int victoryDestroyers, bool withShield, double mean)
    {
        var w = new BombWorld(stub: false, seed: (ulong)(1000 + victoryDestroyers));
        w.Hold(All);
        int fleet = w.Fleet(Emp, Sys, "Victory Destroyer", victoryDestroyers); // 4 each: P = 8, 24, 60
        long total = 0;
        const int trials = 20_000;
        for (int i = 0; i < trials; i++)
        {
            for (int k = 0; k < 4; k++) w.Regiment(All, BombWorld.Army);
            w.Facility(FacilityKind.LnrSeriesI);
            if (withShield) w.Facility(FacilityKind.GenCoreLevelI);
            int before = w.Eco.Objects.Count(o => o.System == Sys);
            w.Bombard(Emp, fleet, Sys, BombardCategory.Military);
            w.Run(1);
            var left = w.Eco.Objects.Where(o => o.System == Sys).ToList();
            total += before - left.Count;
            foreach (var o in left) w.Eco.RemoveObject(o.Id);
        }
        double observed = (double)total / trials;
        Assert.True(Math.Abs(observed - mean) <= 0.05, $"mean destroyed {observed:F3}, reference {mean}");
    }
}
