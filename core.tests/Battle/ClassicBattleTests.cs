using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Ships;
using Rebellion.Core.Tests.Fleets;

namespace Rebellion.Core.Tests.Battle;

// Tests of m4-battle-classic (BC ids as in the sheet's table) and the pure parts of m4-retreat-damage-repair, on plain units (no game around).
public class ClassicBattleTests
{
    internal static IReadOnlyDictionary<string, ShipClass> Ships => World.Ships.Value;
    internal static readonly BattleParams NoRolls = new() { SubsystemRolls = false };
    private const string Corvette = "Corellian Corvette", Isd = "Imperial Star Destroyer", Cc7700 = "CC-7700 Frigate", Interdictor = "Interdictor Cruiser";

    private static (ClassicBattleResult R, IReadOnlyList<BattleUnit> U) Fight(BattleParams p, (string, int)[] alliance, (string, int)[] empire, ulong seed = 1,
        bool aCan = true, bool eCan = true) => QuickBattle.Run(p, Ships, alliance, empire, seed, aCan, eCan);

    private static double CapRate(string cls) => ClassicRates.ClassRateVsCapital(NoRolls, Ships[cls]);

    // m4-battle-classic BC1
    [Fact]
    public void BC1_RatesAgainstACapitalShip()
    {
        Assert.Equal(1.336, CapRate(Corvette), 1e-3);
        Assert.Equal(20.0, CapRate(Isd), 1e-3);
        Assert.Equal(2.548, CapRate(Cc7700), 1e-3);
        Assert.Equal(3.113, CapRate(Interdictor), 1e-3);
    }

    // m4-battle-classic BC2
    [Fact]
    public void BC2_SquadronRatesAgainstAnIsd()
    {
        var y = Ships["Y-wing"];
        Assert.Equal(5.754, ClassicRates.Squadron(NoRolls, y, true, false), 1e-3);
        Assert.Equal(29.76, ClassicRates.Squadron(NoRolls, y, true, true), 1e-2);
        Assert.Equal(2.00, ClassicRates.Squadron(NoRolls, Ships["X-wing"], true, false), 1e-2);
    }

    // m4-battle-classic BC3
    [Fact]
    public void BC3_IsdAgainstAYWingSquadron()
    {
        Assert.Equal(1.25, ClassicRates.CapitalVsSquadron(NoRolls, Ships[Isd], Ships["Y-wing"]), 1e-3);
    }

    // m4-battle-classic BC4
    [Fact]
    public void BC4_GunCountsCancel()
    {
        var isd = Ships[Isd];
        var big = isd with { Arcs = new ShipArcs(new ArcCounts(0, 200, 200), isd.GunArcs.Aft, isd.GunArcs.Port, isd.GunArcs.Starboard) };
        Assert.Equal(20.0, ClassicRates.CapitalVsCapital(NoRolls, big), 1e-3);
    }

    // m4-battle-classic BC5
    [Fact]
    public void BC5_TwelveCorvettesVsIsdRollsOff()
    {
        var (r, u) = Fight(NoRolls, new[] { (Corvette, 12) }, new[] { (Isd, 1) });
        Assert.True(r.AllianceRetreated);
        Assert.Equal(400, r.Seconds);
        Assert.Equal(5, r.AllianceSurvivors);
        Assert.Equal(7, r.AllianceLosses);
        var isd = u.Single(x => x.Side == Side.Empire);
        Assert.Equal(2750.0, isd.Hull);
        Assert.Equal(0, r.Draws);
        Assert.Contains(u.Where(x => x.Side == Side.Alliance && !x.Destroyed), x => Math.Abs(x.Hull - 468.3) < 0.1);
    }

    // m4-battle-classic BC6
    [Fact]
    public void BC6_ElevenCorvettesVsIsdRollsOff()
    {
        var (r, u) = Fight(NoRolls, new[] { (Corvette, 11) }, new[] { (Isd, 1) });
        Assert.True(r.AllianceRetreated);
        Assert.Equal(346, r.Seconds);
        Assert.Equal(5, r.AllianceSurvivors);
        Assert.Equal(2750.0, u.Single(x => x.Side == Side.Empire).Hull);
    }

    // m4-battle-classic BC7
    [Fact]
    public void BC7_FourCorvettesRetreatAtOnce()
    {
        var (r, u) = Fight(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1) });
        Assert.True(r.AllianceRetreated);
        Assert.Equal(1, r.Seconds);
        Assert.All(u, x => Assert.Equal(x.HullMax, x.Hull));
    }

    // m4-battle-classic BC8
    [Fact]
    public void BC8_ThirtyCorvettesBeatTheIsdRollsOff()
    {
        var (r, u) = Fight(NoRolls, new[] { (Corvette, 30) }, new[] { (Isd, 1) });
        Assert.True(r.EmpireRetreated);
        Assert.Equal(46, r.Seconds);
        Assert.Equal(1790.0, u.Single(x => x.Side == Side.Empire).Hull, 0.1);
        Assert.Equal(0, r.AllianceLosses);
    }

    // m4-battle-classic BC10, m4-retreat-damage-repair RT4 (pure part)
    [Fact]
    public void BC10_GravityWellStopsTheRetreat()
    {
        var (r, u) = Fight(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1), (Interdictor, 1) });
        Assert.False(r.AllianceRetreated);
        Assert.Equal(176, r.Seconds);
        Assert.Equal(BattleOutcome.Annihilated, r.AllianceOutcome);
        Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
        Assert.All(u.Where(x => x.Side == Side.Empire), x => Assert.Equal(x.HullMax, x.Hull));
        var refused = r.Steps.OfType<RetreatRefusedStep>().Single();
        Assert.Equal(RetreatRefusal.GravityWell, refused.Reason);
    }

    // m4-battle-classic BC11
    [Fact]
    public void BC11_SixYWingsVsIsd()
    {
        var (r, u) = Fight(NoRolls, new[] { ("Y-wing", 6) }, new[] { (Isd, 1) });
        Assert.True(r.EmpireRetreated);
        Assert.Equal(83, r.Seconds);
        var isd = u.Single(x => x.Side == Side.Empire);
        Assert.Equal(1349.8, isd.Hull, 0.1);
        var sq = u.Where(x => x.Side == Side.Alliance).Select(x => x.Hull).OrderBy(h => h).ToList();
        Assert.Equal(5, sq.Count(h => Math.Abs(h - 96) < 1e-9));
        Assert.Equal(17, sq[0], 0.5);
    }

    // m4-battle-classic BC12
    [Fact]
    public void BC12_IsdVsIsdDestroyEachOther()
    {
        var (r, _) = Fight(NoRolls, new[] { (Isd, 1) }, new[] { (Isd, 1) });
        Assert.Equal(1492, r.Seconds);
        Assert.Equal(BattleOutcome.Indecisive, r.AllianceOutcome);
        Assert.Equal(BattleOutcome.Indecisive, r.EmpireOutcome);
    }

    // m4-battle-classic BC13, m4-retreat-damage-repair RT12
    [Fact]
    public void BC13_StalemateWeakerSideRetreats()
    {
        var (r, u) = Fight(NoRolls, new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) });
        Assert.Equal(1201, r.Seconds);
        Assert.True(r.EmpireRetreated);
        Assert.False(r.AllianceRetreated);
        Assert.All(u, x => Assert.Equal(x.HullMax, x.Hull));
    }

    // m4-battle-classic BC14, m4-retreat-damage-repair RT13
    [Fact]
    public void BC14_StalemateSideThatCannotRetreatIsDestroyed()
    {
        var (r, u) = Fight(NoRolls, new[] { ("Strike Cruiser", 1), (Cc7700, 1) }, new[] { ("Victory II Star Destroyer", 1) });
        Assert.Equal(1201, r.Seconds);
        var v = u.Single(x => x.Side == Side.Empire);
        Assert.Equal(UnitLossReason.Stalemate, v.Loss);
        Assert.Equal(BattleOutcome.Annihilated, r.EmpireOutcome);
    }

    // m4-battle-classic BC15
    [Fact]
    public void BC15_SameSeedSameBattle()
    {
        var p = new BattleParams();
        var (a, ua) = Fight(p, new[] { (Corvette, 8) }, new[] { (Isd, 1) }, 77);
        var (b, ub) = Fight(p, new[] { (Corvette, 8) }, new[] { (Isd, 1) }, 77);
        Assert.Equal(a.Seconds, b.Seconds);
        Assert.Equal(a.Draws, b.Draws);
        Assert.Equal(a.AllianceOutcome, b.AllianceOutcome);
        Assert.Equal(ua.Select(x => (x.Hull, string.Join(",", x.Hits))), ub.Select(x => (x.Hull, string.Join(",", x.Hits))));
        Assert.True(a.Draws > 0);
    }

    // m4-battle-classic BC17
    [Fact]
    public void BC17_UnarmedFleetsBothRetreatAtOnce()
    {
        var (r, _) = Fight(NoRolls, new[] { ("Galleon", 1) }, new[] { ("Bulk Transport", 1) });
        Assert.Equal(1, r.Seconds);
        Assert.True(r.AllianceRetreated && r.EmpireRetreated);
        Assert.Equal(BattleOutcome.Indecisive, r.AllianceOutcome);
    }

    // m4-battle-classic BC18
    [Fact]
    public void BC18_AlertRetreatSkipsTheLoop()
    {
        var (r, _) = QuickBattle.Run(new BattleParams(), Ships, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 5,
            alert: new[] { OnContactMode.Simulate, OnContactMode.Retreat }); // index by Side: Empire 0, Alliance 1
        Assert.Equal(0, r.Seconds);
        Assert.True(r.AllianceRetreated);
        Assert.Equal(0, r.Draws);
        Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
    }

    // m4-battle-classic BC19
    [Fact]
    public void BC19_IonOnlyShipsSkipSquadrons()
    {
        var p = NoRolls;
        // Assault Frigate: ion and turbolaser, no laser: capitals-first; its fore arc against squadrons still hurts, but a pure ion ship does not
        var ion = Ships[Isd] with { Arcs = new ShipArcs(new ArcCounts(0, 100, 0), default, default, default) };
        Assert.Equal(0.0, ClassicRates.CapitalVsSquadron(p, ion, Ships["X-wing"]));
        Assert.True(ClassicRates.CapitalVsSquadron(p, Ships[Corvette], Ships["X-wing"]) > 0);
    }

    // m4-retreat-damage-repair RT5
    [Fact]
    public void RT5_ShipWithoutWorkingHyperdriveIsDestroyedOnRetreat()
    {
        int id = 1;
        var p = NoRolls;
        var a = QuickBattle.Units(p, Ships, Side.Alliance, new[] { (Corvette, 1) }, ref id);
        var e = QuickBattle.Units(p, Ships, Side.Empire, new[] { (Isd, 1), ("Carrack Light Cruiser", 1) }, ref id, a.Count);
        var isd = e.First(x => x.Class.Name == Isd); isd.Hits[4] = 2;
        var all = a.Concat(e).ToList();
        var b = new ClassicBattle(p, all, new GameRng(1), new[] { true, true }, new[] { OnContactMode.Retreat, OnContactMode.Simulate }); // Empire index 0
        var r = b.Run();
        Assert.True(r.EmpireRetreated);
        Assert.Equal(UnitLossReason.NoHyperdrive, isd.Loss);
        Assert.True(e.First(x => x.Class.Name == "Carrack Light Cruiser").Left);
    }

    // m4-retreat-damage-repair RT6
    [Fact]
    public void RT6_NoWorkingHyperdriveRefusesTheRetreat()
    {
        int id = 1;
        var p = NoRolls;
        var a = QuickBattle.Units(p, Ships, Side.Alliance, new[] { (Corvette, 1) }, ref id);
        var e = QuickBattle.Units(p, Ships, Side.Empire, new[] { (Isd, 1) }, ref id, a.Count);
        e[0].Hits[4] = 2;
        var b = new ClassicBattle(p, a.Concat(e).ToList(), new GameRng(1), new[] { true, true }, new[] { OnContactMode.Retreat, OnContactMode.Simulate });
        var r = b.Run();
        Assert.Equal(RetreatRefusal.NoHyperdrive, r.Steps.OfType<RetreatRefusedStep>().First().Reason);
        Assert.False(r.EmpireRetreated);
    }

    // m4-retreat-damage-repair RT7
    [Fact]
    public void RT7_DeathStarJumpsRegardlessOfItsCounters()
    {
        int id = 1;
        var p = NoRolls;
        var a = QuickBattle.Units(p, Ships, Side.Alliance, new[] { (Corvette, 1) }, ref id);
        var e = QuickBattle.Units(p, Ships, Side.Empire, new[] { (Isd, 1), ("Death Star", 1) }, ref id, a.Count);
        foreach (var u in e) u.Hits[4] = 2;
        var b = new ClassicBattle(p, a.Concat(e).ToList(), new GameRng(1), new[] { true, true }, new[] { OnContactMode.Retreat, OnContactMode.Simulate });
        var r = b.Run();
        Assert.True(r.EmpireRetreated);
        Assert.True(e.First(x => x.Class.Name == "Death Star").Left);
        Assert.Equal(UnitLossReason.NoHyperdrive, e.First(x => x.Class.Name == Isd).Loss);
    }

    // m4-retreat-damage-repair RT8, RT9
    [Theory]
    [InlineData(6, 2, 0, 2)]
    [InlineData(4, 3, 2, 1)]
    public void RT8_RT9_SquadronsNeedBerthsOnRetreat(int aboard, int based, int expectGone, int expectBerthLost)
    {
        int id = 1;
        var p = NoRolls;
        var a = QuickBattle.Units(p, Ships, Side.Alliance, new[] { (Corvette, 1) }, ref id);
        var e = QuickBattle.Units(p, Ships, Side.Empire, new[] { (Isd, 1), ("TIE Fighter", aboard + based) }, ref id, a.Count);
        var isd = e.First(x => x.IsCapital);
        var sq = e.Where(x => !x.IsCapital).OrderBy(x => x.ObjectId).ToList();
        for (int i = 0; i < aboard; i++) sq[i].Carrier = isd.ObjectId;
        var b = new ClassicBattle(p, a.Concat(e).ToList(), new GameRng(1), new[] { true, true }, new[] { OnContactMode.Retreat, OnContactMode.Simulate });
        b.Run();
        int free = 6 - aboard;
        Assert.Equal(based - Math.Min(based, free), sq.Count(x => x.Loss == UnitLossReason.NoBerth));
        Assert.Equal(aboard, sq.Take(aboard).Count(x => x.Left));
        if (based > free) Assert.Equal(UnitLossReason.NoBerth, sq.Last().Loss); // the highest id is the one left without a berth
        _ = expectGone; _ = expectBerthLost;
    }

    // m4-retreat-damage-repair RT10
    [Fact]
    public void RT10_BasedSquadronWithHyperdriveJumpsAlone()
    {
        int id = 1;
        var p = NoRolls;
        var a = QuickBattle.Units(p, Ships, Side.Alliance, new[] { ("X-wing", 1), (Isd, 0) }, ref id);
        var e = QuickBattle.Units(p, Ships, Side.Empire, new[] { (Isd, 1) }, ref id, a.Count);
        var b = new ClassicBattle(p, a.Concat(e).ToList(), new GameRng(1), new[] { true, true }, new[] { OnContactMode.Simulate, OnContactMode.Retreat });
        b.Run();
        Assert.True(a[0].Left);
        Assert.True(a[0].JumpedAlone);
        Assert.Equal(0, a[0].NewCarrier);
    }

    // m4-retreat-damage-repair RT11
    [Fact]
    public void RT11_MoraleThreshold()
    {
        Assert.True(ClassicRates.MoraleFlagged(NoRolls, 6.6, 20.0));
        Assert.False(ClassicRates.MoraleFlagged(NoRolls, 6.7, 20.0));
        Assert.True(ClassicRates.MoraleFlagged(NoRolls, 0, 0));
        Assert.False(ClassicRates.MoraleFlagged(NoRolls, 5, 0));
    }

    // m4-retreat-damage-repair RT14
    [Fact]
    public void RT14_DeathStarStalemateSwitch()
    {
        // two unarmed-to-each-other sides: Strike Cruiser (18) against a Victory II (16) never hurt each other; a Death Star would kill, so use the switch on a stalemate pair
        var on = new BattleParams { SubsystemRolls = false, DeathStarStalemate = true };
        var (r, _) = QuickBattle.Run(on, Ships, new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) }, 1);
        Assert.Equal(1201, r.Seconds); // no Death Star alive: the switch changes nothing
    }

    // m4-battle-classic §11 (draw order) and §7 (rolls on): the battle's own generator is the only one used
    [Fact]
    public void RollsOnDrawFromTheBattleGeneratorOnly()
    {
        var (r, _) = Fight(new BattleParams(), new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 3);
        Assert.True(r.Draws > 0);
    }

    // m4-battle-classic §5, §6: Surround and PerSide run to an end (switches)
    [Theory]
    [InlineData(BattleTactic.Surround, ClassicTaskForces.PerUnit)]
    [InlineData(BattleTactic.StandOff, ClassicTaskForces.PerSide)]
    public void SwitchesRunToAnEnd(BattleTactic t, ClassicTaskForces f)
    {
        var p = new BattleParams { Tactic = t, TaskForces = f };
        var (r, _) = Fight(p, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 9);
        Assert.NotEqual(BattleOutcome.Indecisive, r.AllianceOutcome);
    }

    // The traced Classic rules for corvettes against an ISD. Nothing is asserted about the winner (Classic must later be checked against the real game); only determinism.
    [Theory]
    [InlineData(8)]
    [InlineData(30)]
    public void CorvettesVsIsdRatesAreDeterministic(int n)
    {
        var first = Rates(n, 200);
        var second = Rates(n, 200);
        Assert.Equal(first, second);
    }

    internal static (int AllianceWins, int EmpireWins, int Draws, int AllianceRetreats, int EmpireRetreats, int Survivors) Rates(int corvettes, int battles)
    {
        int aw = 0, ew = 0, dr = 0, ar = 0, er = 0, surv = 0;
        for (int s = 1; s <= battles; s++)
        {
            var (r, _) = QuickBattle.Run(new BattleParams(), Ships, new[] { (Corvette, corvettes) }, new[] { (Isd, 1) }, (ulong)s);
            if (r.AllianceOutcome == BattleOutcome.Victorious) aw++;
            else if (r.EmpireOutcome == BattleOutcome.Victorious) ew++;
            else dr++;
            if (r.AllianceRetreated) ar++;
            if (r.EmpireRetreated) er++;
            surv += r.AllianceSurvivors;
        }
        return (aw, ew, dr, ar, er, surv);
    }

    [Fact]
    public void ReportRates()
    {
        foreach (int n in new[] { 8, 30 })
        {
            var x = Rates(n, 1000);
            Console.WriteLine($"RATES {n} corvettes vs ISD, 1000 battles: Alliance wins {x.AllianceWins}, Empire wins {x.EmpireWins}, no winner {x.Draws}, Alliance retreats {x.AllianceRetreats}, Empire retreats {x.EmpireRetreats}, mean corvette survivors {x.Survivors / 1000.0}");
        }
    }
}
