using Praedra.Core.Assault;
using Praedra.Core.Clock;
using Praedra.Core.Economy;

namespace Praedra.Core.Tests.Assault;

// m3-assault A4, A13 to A16: statistical tests with the seeded game generator through the real module (one Step per assault).
public class AssaultStatTests
{
    private static readonly Side Emp = Side.Empire, All = Side.Alliance;
    private const string Sys = AssaultWorld.Sys;

    private sealed record Rates(double Win, double MeanLeft, double AttackerDies, double DefenderDies, double Nobody);

    /// <summary>
    /// <paramref name="trials"/> assaults of <paramref name="n"/> Alliance regiments of one class against <paramref name="m"/> Imperial ones
    /// (plus the given facilities); the world is reset after each. MeanLeft = attackers alive at the end, averaged over all assaults.
    /// </summary>
    private static Rates Run(ulong seed, int trials, string attackers, int n, string defenders, int m, int attackerLeadership = 0, params FacilityKind[] facilities)
    {
        var w = new AssaultWorld(stub: false, seed: seed);
        w.Hold(Emp);
        foreach (var k in facilities) w.B.Facility(k);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        w.Assault.FleetGeneralLeadership = _ => attackerLeadership;
        int wins = 0, left = 0, ad = 0, dd = 0, none = 0;
        for (int i = 0; i < trials; i++)
        {
            w.Aboard(fleet, attackers, n);
            w.Defenders(Emp, defenders, m);
            w.Submit(All, fleet);
            w.Run(1);
            var r = w.LastResolved();
            if (r.Result == "Won") wins++;
            left += r.A;
            if (n == 1 && m == 1) { if (r.A == 0) ad++; else if (r.D == 0) dd++; else none++; }
            w.ResetRegiments(Emp);
        }
        return new Rates(100.0 * wins / trials, (double)left / trials, 100.0 * ad / trials, 100.0 * dd / trials, 100.0 * none / trials);
    }

    // m3-assault A4: 10,000 duels with L_att 90, Army attacker against Army defender: the +1 row, 55/36/9, within 2.0 points
    [Fact]
    public void A4_LeadershipNinetyDuels()
    {
        var r = Run(4, 10_000, AssaultWorld.Reb, 1, AssaultWorld.Imp, 1, attackerLeadership: 90);
        Assert.True(Math.Abs(r.DefenderDies - 55.0) <= 2.0, $"defender dies {r.DefenderDies:F2}");
        Assert.True(Math.Abs(r.AttackerDies - 36.0) <= 2.0, $"attacker dies {r.AttackerDies:F2}");
        Assert.True(Math.Abs(r.Nobody - 9.0) <= 2.0, $"nobody {r.Nobody:F2}");
    }

    // m3-assault A13: 4 Alliance Fleet vs 2 Imperial Army (20,000 seeded assaults): win 48.0 percent (2.0 points), mean attackers left 3.03 (0.1)
    [Fact]
    public void A13_FourFleetAgainstTwoArmy()
    {
        var r = Run(13, 20_000, "Alliance Fleet Regiment", 4, AssaultWorld.Imp, 2);
        Assert.True(Math.Abs(r.Win - 48.0) <= 2.0, $"win {r.Win:F2} percent");
        Assert.True(Math.Abs(r.MeanLeft - 3.03) <= 0.1, $"mean left {r.MeanLeft:F3}");
    }

    // m3-assault A14: the same plus one KDY-150: win 32.2 percent
    [Fact]
    public void A14_WithAKdy()
    {
        var r = Run(14, 20_000, "Alliance Fleet Regiment", 4, AssaultWorld.Imp, 2, 0, FacilityKind.Kdy150);
        Assert.True(Math.Abs(r.Win - 32.2) <= 2.0, $"win {r.Win:F2} percent");
    }

    // m3-assault A15: 3 Army vs 3 Army: win 0.5 percent (a single pass: even numbers almost never win)
    [Fact]
    public void A15_EvenNumbersAlmostNeverWin()
    {
        var r = Run(15, 20_000, AssaultWorld.Reb, 3, AssaultWorld.Imp, 3);
        Assert.True(Math.Abs(r.Win - 0.5) <= 2.0, $"win {r.Win:F2} percent");
    }

    // m3-assault A16: 8 Wookiee vs 6 Dark Trooper: win 2.7 percent with L_att 90, 0.2 percent without
    [Fact]
    public void A16_WookieeAgainstDarkTrooper()
    {
        var withGeneral = Run(16, 20_000, "Wookiee Regiment", 8, "Dark Trooper Regiment", 6, attackerLeadership: 90);
        var without = Run(17, 20_000, "Wookiee Regiment", 8, "Dark Trooper Regiment", 6);
        Assert.True(Math.Abs(withGeneral.Win - 2.7) <= 2.0, $"with L 90: win {withGeneral.Win:F2} percent");
        Assert.True(Math.Abs(without.Win - 0.2) <= 2.0, $"without: win {without.Win:F2} percent");
        Assert.True(withGeneral.Win > without.Win);
    }
}
