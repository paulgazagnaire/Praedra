using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Ships;
using Rebellion.Core.Tests.Fleets;

namespace Rebellion.Core.Tests.Battle;

// Tests of m4-battle-classic (BC ids as in the sheet's table, B3 ids from batch B3) and the pure parts of m4-retreat-damage-repair, on plain units
// (no game around). [Model] values come from specs/reverse/tools/b3_simulate.py.
// Every BattleParams here sets Ruleset = Classic explicitly: these are Classic-battle tests and the DEFAULT became New (Paul, 2026-10-18).
public class ClassicBattleTests
{
    internal static IReadOnlyDictionary<string, ShipClass> Ships => World.Ships.Value;
    internal static readonly BattleParams NoRolls = new() { Ruleset = BattleRuleset.Classic, SubsystemRolls = false }; // Classic set explicitly (battle DEFAULT New since 2026-10-18)
    private static readonly BattleParams P = new() { Ruleset = BattleRuleset.Classic };
    private const string Corvette = "Corellian Corvette", Isd = "Imperial Star Destroyer", Cc7700 = "CC-7700 Frigate", Interdictor = "Interdictor Cruiser";
    private const OnContactMode Fight = OnContactMode.Fight, Retreat = OnContactMode.Retreat;

    private static (ClassicBattleResult R, IReadOnlyList<BattleUnit> U) Run(BattleParams p, (string, int)[] alliance, (string, int)[] empire, ulong seed = 1,
        bool aCan = true, bool eCan = true, OnContactMode[]? alert = null) => QuickBattle.Run(p, Ships, alliance, empire, seed, aCan, eCan, alert);

    /// <summary>The alert answers, indexed by (int)Side (Empire 0, Alliance 1).</summary>
    internal static OnContactMode[] Alert(OnContactMode alliance, OnContactMode empire)
    {
        var a = new OnContactMode[2];
        a[(int)Side.Alliance] = alliance; a[(int)Side.Empire] = empire;
        return a;
    }

    /// <summary>Units of both sides (Alliance first), optionally changed, and the battle built on them (not run).</summary>
    private static (ClassicBattle B, List<BattleUnit> A, List<BattleUnit> E) Make(BattleParams p, (string, int)[] alliance, (string, int)[] empire,
        OnContactMode[]? alert = null, Action<List<BattleUnit>, List<BattleUnit>>? prepare = null, bool[]? ai = null)
    {
        int id = 1;
        var a = QuickBattle.Units(p, Ships, Side.Alliance, alliance, ref id);
        var e = QuickBattle.Units(p, Ships, Side.Empire, empire, ref id, a.Count);
        prepare?.Invoke(a, e);
        return (new ClassicBattle(p, a.Concat(e).ToList(), new GameRng(1), new[] { true, true }, alert, ai), a, e);
    }

    private static BattleUnit Unit(string cls, Side side = Side.Alliance)
    {
        int id = 1;
        var u = QuickBattle.Units(P, Ships, side, new[] { (cls, 1) }, ref id)[0];
        u.ShieldMax = u.Shield = ClassicRates.StartShield(P, u.Class, u.IsCapital);
        return u;
    }

    private static double CapRate(string cls) => ClassicRates.F(P, Unit(cls));

    // ================================================================== rates (m4-battle-classic §4)

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
        var isd = Unit(Isd, Side.Empire);
        Assert.Equal(5.754, ClassicRates.Rate(P, Unit("Y-wing"), isd, true), 1e-3);
        isd.Shield = 0;
        Assert.Equal(29.76, ClassicRates.Rate(P, Unit("Y-wing"), isd, true), 1e-2);
        isd.Shield = 300;
        Assert.Equal(2.00, ClassicRates.Rate(P, Unit("X-wing"), isd, true), 1e-2);
    }

    // m4-battle-classic BC3 (moved by B3: maneuverability is class + 1, so the ISD's ratio against a Y-wing is 2 / 5, not 1 / 4: 1.25 -> 2.0)
    [Fact]
    public void BC3_IsdAgainstAYWingSquadron()
    {
        Assert.Equal(2.0, ClassicRates.Rate(P, Unit(Isd), Unit("Y-wing", Side.Empire), false), 1e-3);
    }

    // m4-battle-classic BC4
    [Fact]
    public void BC4_GunCountsCancel()
    {
        var isd = Ships[Isd];
        var u = Unit(Isd);
        u.Class = isd with { Arcs = new ShipArcs(new ArcCounts(0, 200, 200), isd.GunArcs.Aft, isd.GunArcs.Port, isd.GunArcs.Starboard) };
        Assert.Equal(20.0, ClassicRates.F(P, u), 1e-3);
    }

    // B3-2
    [Fact]
    public void B3_2_RatesAgainstACapitalShip()
    {
        Assert.Equal(1.336, CapRate(Corvette), 1e-3);
        Assert.Equal(7.716, CapRate("Nebulon-B Frigate"), 1e-3);
        Assert.Equal(18.0, CapRate("Mon Calamari Cruiser"), 1e-3);
        Assert.Equal(3.113, CapRate(Interdictor), 1e-3);
        Assert.Equal(20.0, CapRate(Isd), 1e-3);
        Assert.Equal(40.0, CapRate("Imperial II Star Destroyer"), 1e-3);
    }

    // B3-3
    [Fact]
    public void B3_3_RatesAgainstSquadrons()
    {
        double R(string u, string v) => ClassicRates.Rate(P, Unit(u), Unit(v, Side.Empire), false);
        Assert.Equal(2.0, R(Isd, "Y-wing"), 1e-3);
        Assert.Equal(1.667, R(Isd, "TIE Fighter"), 1e-3);
        Assert.Equal(5.333, R(Corvette, "TIE Fighter"), 1e-3);
        Assert.Equal(12.003, R("X-wing", "TIE Fighter"), 1e-3);
        Assert.Equal(18.005, R("A-wing", "TIE Fighter"), 1e-3);
    }

    // B3-4
    [Fact]
    public void B3_4_YWingAgainstAnIsd()
    {
        var isd = Unit(Isd, Side.Empire);
        var y = Unit("Y-wing");
        Assert.Equal(5.754, ClassicRates.Rate(P, y, isd, true), 1e-3);
        isd.Shield = 0;
        Assert.Equal(29.76, ClassicRates.Rate(P, y, isd, true), 1e-2);
        isd.Shield = 300;
        y.Hull = 6 * 8; // 6 craft lost
        Assert.Equal(2.877, ClassicRates.Rate(P, y, isd, true), 1e-3);
    }

    // B3-5
    [Fact]
    public void B3_5_WeaponHitsComeOffTheHullScaledRate()
    {
        var isd = Unit(Isd);
        isd.Hull = 1375; isd.Hits[1] = 2;
        Assert.Equal(0.0, ClassicRates.F(P, isd));
        isd.Hits[1] = 1;
        Assert.Equal(5.0, ClassicRates.F(P, isd), 1e-3); // 75 x 0.25 x K x f / N
    }

    // ================================================================== task forces (§2)

    private static int[] Sizes(ClassicBattle b, Side s) => b.TaskForcesOf(s).Select(t => t.Members.Count).ToArray();

    // B3-6
    [Fact]
    public void B3_6_TaskForces()
    {
        Assert.Equal(new[] { 4, 4, 4 }, Sizes(Make(P, new[] { (Corvette, 12) }, new[] { (Isd, 1) }).B, Side.Alliance));
        Assert.Equal(new[] { 5, 5, 6 }, Sizes(Make(P, new[] { (Corvette, 16) }, new[] { (Isd, 1) }).B, Side.Alliance));
        Assert.Equal(new[] { 3, 3, 4 }, Sizes(Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 10) }).B, Side.Empire));
        var mixed = Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 5), (Corvette, 9), ("Galleon", 2) }).B.TaskForcesOf(Side.Empire);
        Assert.Equal(new[] { 2, 3, 3, 3, 3, 2 }, mixed.Select(t => t.Members.Count));
        Assert.Equal(new[] { Isd, Isd, Corvette, Corvette, Corvette, "Galleon" }, mixed.Select(t => t.Members[0].Class.Name));
        var big = Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 20), (Corvette, 30), ("Galleon", 20) }).B;
        Assert.Equal(new[] { 6, 6, 8, 7, 7, 7, 9, 20 }, Sizes(big, Side.Empire));
        Assert.Equal(new[] { 5, 5, 5, 5 }, Sizes(Make(P, new[] { (Corvette, 1) }, new[] { ("TIE Fighter", 20) }).B, Side.Empire));
        var fighters = Make(P, new[] { ("X-wing", 9), ("A-wing", 2) }, new[] { (Isd, 1) }).B.TaskForcesOf(Side.Alliance);
        Assert.Equal(new[] { 2, 5, 4 }, fighters.Select(t => t.Members.Count));
        Assert.Equal(new[] { "A-wing", "X-wing", "X-wing" }, fighters.Select(t => t.Members[0].Class.Name));
    }

    // ================================================================== hits (§6, §7)

    private static (ClassicBattle B, BattleUnit C) CorvetteTarget(Action<BattleUnit>? set = null)
    {
        var (b, a, _) = Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 1) });
        set?.Invoke(a[0]);
        return (b, a[0]);
    }

    // B3-7
    [Fact]
    public void B3_7_SmallHitOnAFullShield()
    {
        var (b, c) = CorvetteTarget();
        b.ApplyHit(c, 20); // draw 79 > 10 percent
        Assert.Equal(1, b.Draws);
        Assert.Equal(180.0, c.Shield, 1e-9);
        Assert.Equal(0, c.Hits[0]);
    }

    // B3-8
    [Fact]
    public void B3_8_HitThatBreaksTheShield()
    {
        var (b, c) = CorvetteTarget();
        b.ApplyHit(c, 250); // draw 79 <= 125: generator hit; hull 450; draw 70: score 60: a second generator hit
        Assert.Equal(2, b.Draws);
        Assert.Equal(450.0, c.Hull, 1e-9);
        Assert.Equal(0.0, c.Shield);
        Assert.Equal(2, c.Hits[0]);
    }

    // B3-9
    [Fact]
    public void B3_9_MinimumOneAtShieldZero()
    {
        var (b, c) = CorvetteTarget(u => u.Shield = 0);
        c.Shield = 0;
        b.ApplyHit(c, 0.5); // no generator draw; hull roll 79 - 0.2 = 78.8: weapons
        Assert.Equal(1, b.Draws);
        Assert.Equal(499.0, c.Hull, 1e-9);
        Assert.Equal(1, c.Hits[1]);
        Assert.Equal(0, c.Hits[0]);
    }

    // B3-10
    [Fact]
    public void B3_10_KillingHitStillRollsThenNothingDraws()
    {
        var (b, c) = CorvetteTarget();
        c.Hull = 10; c.Shield = 0;
        b.ApplyHit(c, 20); // hull 0, score 79 - 100 = -21: nothing
        Assert.Equal(0.0, c.Hull);
        Assert.Equal(1, b.Draws);
        Assert.All(c.Hits, h => Assert.Equal(0, h));
        b.ApplyHit(c, 5);
        Assert.Equal(1, b.Draws);
    }

    // B3-11
    [Fact]
    public void B3_11_CountersStopAtTheirCaps()
    {
        var (b, c) = CorvetteTarget();
        c.Hits[1] = 4;
        b.Bump(c, 1);
        Assert.Equal(4, c.Hits[1]);
        b.Bump(c, 4); b.Bump(c, 4); // the Corvette has one hyperdrive component
        Assert.Equal(1, c.Hits[4]);
    }

    // B3-12
    [Fact]
    public void B3_12_Recharge()
    {
        var (b, a, e) = Make(P, new[] { ("Y-wing", 1) }, new[] { (Isd, 1) });
        var isd = e[0]; isd.Hull = 1375; isd.Hits[0] = 1; isd.Shield = 200;
        b.RechargeSide(Side.Empire);
        Assert.Equal(203.75, isd.Shield, 1e-9);
        isd.Shield = 298;
        b.RechargeSide(Side.Empire);
        Assert.Equal(300.0, isd.Shield, 1e-9); // the class shield is the cap
        var y = a[0]; y.Hull = 48; y.Shield = 10;
        Assert.Equal(60.0, y.ShieldMax);
        b.RechargeSide(Side.Alliance);
        Assert.Equal(30.0, y.ShieldMax, 1e-9);
        Assert.Equal(10.15, y.Shield, 1e-9);
        b.RechargeSide(Side.Alliance);
        Assert.Equal(15.0, y.ShieldMax, 1e-9);
        Assert.Equal(10.30, y.Shield, 1e-9);
    }

    // ================================================================== targeting (§5, §6)

    // B3-13
    [Fact]
    public void B3_13_ValuesAndLists()
    {
        var (b, a, _) = Make(P, new[] { (Isd, 1), (Corvette, 1), ("Galleon", 1), ("Y-wing", 1) }, new[] { (Isd, 1) });
        var isd = a.Single(u => u.Class.Name == Isd); var cor = a.Single(u => u.Class.Name == Corvette);
        var gal = a.Single(u => u.Class.Name == "Galleon"); var y = a.Single(u => u.Class.Name == "Y-wing");
        Assert.Equal(299.98, b.Value(isd), 1e-2);
        Assert.Equal(20.02, b.Value(cor), 1e-2);
        Assert.Equal(86.29, b.Value(y), 1e-2);
        Assert.Equal(new[] { isd, y, cor, gal }, b.Ranked(Side.Empire));
        var (l, t) = b.Lists(Side.Empire);
        Assert.Equal(new[] { y, isd, cor, gal }, l);
        Assert.Equal(new[] { isd, cor, gal, y, isd, cor, gal }, t);
    }

    private static (ClassicBattle B, List<BattleUnit> A, List<ClassicTaskForce> Tfs) TwoGunners(int corvettes, params int[] rates)
    {
        var p = new BattleParams { Ruleset = BattleRuleset.Classic, TaskForces = ClassicTaskForces.PerUnit };
        var (b, a, _) = Make(p, new[] { (Corvette, corvettes), ("Galleon", 1) }, rates.Select(_ => (Isd, 1)).ToArray(),
            prepare: (_, e) => { for (int i = 0; i < e.Count; i++) e[i].Class = Ships[Isd] with { WeaponRechargeRate = rates[i] }; });
        return (b, a, b.TaskForcesOf(Side.Empire).ToList());
    }

    // B3-14
    [Fact]
    public void B3_14_StandOffNeverCommitsTheBest()
    {
        var (b, a, tfs) = TwoGunners(2, 30, 10);
        b.StandOff(tfs, new List<BattleUnit> { a[0], a[1] });
        Assert.Same(a[0], tfs[1].Target); // the 10 alone is committed to the first Corvette (30 + 10 kills it in 17.5 s)
        Assert.Same(a[1], tfs[0].Target); // the 30 stays free; alone, it goes to the next target
    }

    // B3-15
    [Fact]
    public void B3_15_SingleFreeTaskForceEndsThePass()
    {
        var (b, a, tfs) = TwoGunners(2, 10);
        var gal = a.Single(u => u.Class.Name == "Galleon");
        b.StandOff(tfs, new List<BattleUnit> { gal, a[0], a[1] });
        Assert.Same(a[0], tfs[0].Target); // the Galleon is not eligible
    }

    // ================================================================== strength and retreat (§8)

    // B3-16
    [Fact]
    public void B3_16_StrengthLeavesOutOutShotShips()
    {
        var (b, a, _) = Make(new BattleParams { Ruleset = BattleRuleset.Classic, TaskForces = ClassicTaskForces.PerUnit }, new[] { (Corvette, 2), ("X-wing", 1) }, new[] { (Isd, 1) });
        b.TaskForcesOf(Side.Empire)[0].Target = a[0]; // rate 20 >= its recharge 10
        Assert.Equal(3.336, b.Strength(Side.Alliance), 1e-2); // 1.336 + 2.0045
    }

    // B3-17
    [Fact]
    public void B3_17_RetreatTest()
    {
        Assert.False(ClassicRates.RetreatWanted(3.336, 20, 0.0));
        Assert.True(ClassicRates.RetreatWanted(0, 20, 0.0));
        Assert.True(ClassicRates.RetreatWanted(3.336, 20, 10000.0));
        Assert.True(ClassicRates.RetreatWanted(17, 20, 0.9));
        Assert.False(ClassicRates.RetreatWanted(18, 20, 0.9));
        Assert.False(ClassicRates.RetreatWanted(5, 0, 10000.0)); // the enemy's strength is 0: stay
        var (b, _, _) = Make(P, new[] { (Corvette, 4) }, new[] { (Isd, 1), (Interdictor, 1) });
        Assert.Equal(RetreatRefusal.GravityWell, b.CanRetreat(Side.Alliance));
        var (b2, _, _) = Make(P, new[] { (Corvette, 4) }, new[] { (Isd, 1) });
        Assert.Null(b2.CanRetreat(Side.Alliance));
    }

    // m4-battle-classic §1.3: the per-side thresholds
    [Fact]
    public void ThresholdsComeFromTheController()
    {
        var (b, _, _) = Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 1) }, Alert(Retreat, Fight));
        Assert.Equal(10000.0, b.ThresholdOf(Side.Alliance));
        Assert.Equal(0.0, b.ThresholdOf(Side.Empire));
        var ai = new bool[2]; ai[(int)Side.Empire] = true;
        var (c, _, _) = Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 1) }, Alert(Fight, Fight), ai: ai);
        Assert.Equal(0.9, c.ThresholdOf(Side.Empire));
        var (d, _, _) = Make(P, new[] { (Corvette, 1) }, new[] { (Isd, 1) });
        Assert.Equal(0.3300000131, d.ThresholdOf(Side.Alliance));
    }

    // ================================================================== whole battles (Original dice)

    // B3-18
    [Fact]
    public void B3_18_CorvettesRetreatAfterTheEmpiresVolley()
    {
        var (r, u) = Run(P, new[] { (Corvette, 4) }, new[] { (Isd, 1) }, alert: Alert(Retreat, Fight));
        Assert.Equal(1, r.Seconds);
        Assert.Equal(3, r.Draws);
        var c = u.Where(x => x.Side == Side.Alliance).ToList();
        Assert.Equal(180.0, c[0].Shield, 1e-3);
        Assert.Equal(0, c[0].Hits[0]);
        Assert.Equal(294.66, u.Single(x => x.Side == Side.Empire).Shield, 1e-2);
        Assert.All(c, x => Assert.True(x.Left));
        Assert.True(r.AllianceRetreated);
    }

    // B3-19
    [Fact]
    public void B3_19_IsdVsIsdTheAllianceLeaves()
    {
        var (r, u) = Run(P, new[] { (Isd, 1) }, new[] { (Isd, 1) }, alert: Alert(Fight, Fight));
        Assert.Equal(1, r.Seconds);
        Assert.Equal(2, r.Draws);
        var a = u.Single(x => x.Side == Side.Alliance);
        Assert.True(a.Left);
        Assert.Equal(280.0, a.Shield, 1e-3);
        Assert.True(u.Single(x => x.Side == Side.Empire).Alive);
    }

    // B3-20
    [Fact]
    public void B3_20_BothRetreat()
    {
        var (r1, _) = Run(P, new[] { (Corvette, 4) }, new[] { (Isd, 1) }, alert: Alert(Retreat, Retreat));
        Assert.True(r1.EmpireRetreated); Assert.False(r1.AllianceRetreated);
        Assert.Equal(1, r1.Draws);
        Assert.Equal(BattleOutcome.Victorious, r1.AllianceOutcome);
        var (r2, _) = Run(P, new[] { (Isd, 1) }, new[] { (Isd, 1) }, alert: Alert(Retreat, Retreat));
        Assert.True(r2.AllianceRetreated); Assert.False(r2.EmpireRetreated);
        Assert.Equal(2, r2.Draws);
        var (r3, _) = Run(P, new[] { ("Galleon", 1) }, new[] { ("Bulk Transport", 1) }, alert: Alert(Fight, Fight));
        Assert.Equal(1, r3.Seconds);
        Assert.True(r3.AllianceRetreated && r3.EmpireRetreated);
        Assert.Equal(1, r3.Draws); // the Bulk Transport's (Empire, fires first) zero hit on the Galleon still rolls the generator (1 percent minimum); B3-20 as corrected in m4-battle-classic
    }

    // B3-21
    [Fact]
    public void B3_21_Stalemate()
    {
        var (r, u) = Run(NoRolls, new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) }, alert: Alert(Fight, Fight));
        Assert.Equal(1201, r.Seconds);
        Assert.True(r.EmpireRetreated); Assert.False(r.AllianceRetreated);
        Assert.All(u, x => Assert.Equal(x.HullMax, x.Hull));
        var (r2, u2) = Run(NoRolls, new[] { ("Strike Cruiser", 1), (Cc7700, 1) }, new[] { ("Victory II Star Destroyer", 1) }, alert: Alert(Fight, Fight));
        var v = u2.Single(x => x.Side == Side.Empire);
        Assert.Equal(UnitLossReason.Stalemate, v.Loss);
        Assert.Equal(1200, v.LostAt);
        Assert.Equal(1200, r2.Seconds);
        Assert.Equal(BattleOutcome.Annihilated, r2.EmpireOutcome);
    }

    // B3-22
    [Fact]
    public void B3_22_SquadronsDockBackInTheirOwnCarrier()
    {
        var (b, _, e) = Make(P, new[] { (Corvette, 4) }, new[] { (Isd, 2), ("TIE Fighter", 4) }, Alert(Fight, Retreat), prepare: (_, e) =>
        {
            e[0].Hits[4] = 2;
            foreach (var q in e.Where(x => !x.IsCapital)) q.Carrier = e[0].ObjectId;
        });
        var r = b.Run();
        Assert.True(r.EmpireRetreated);
        Assert.Equal(UnitLossReason.NoHyperdrive, e[0].Loss);
        Assert.All(e.Where(x => !x.IsCapital), q => Assert.Equal(UnitLossReason.ShipLost, q.Loss));
        Assert.True(e[1].Left);
    }

    // B3-22b
    [Fact]
    public void B3_22b_LaunchedSquadronsComeAfterTheBasedOnes()
    {
        var (b, _, e) = Make(P, new[] { (Corvette, 4) }, new[] { (Isd, 1), ("TIE Fighter", 7) }, Alert(Fight, Retreat), prepare: (_, e) =>
        {
            foreach (var q in e.Where(x => !x.IsCapital).Take(4)) q.Carrier = e[0].ObjectId; // 4 aboard, 3 based
        });
        b.Run();
        var sq = e.Where(x => !x.IsCapital).ToList();
        Assert.Equal(6, sq.Count(q => q.Left && q.NewCarrier == e[0].ObjectId));
        Assert.Equal(UnitLossReason.NoBerth, sq[3].Loss); // the last launched one
    }

    // B3-27
    [Fact]
    public void B3_27_SameBattleTwiceOriginalDice()
    {
        var (a, ua) = Run(P, new[] { (Corvette, 8) }, new[] { (Isd, 1) }, 77, alert: Alert(Fight, Fight));
        var (b, ub) = Run(P, new[] { (Corvette, 8) }, new[] { (Isd, 1) }, 99, alert: Alert(Fight, Fight)); // the seed is not used
        Assert.Equal(a.Seconds, b.Seconds);
        Assert.Equal(a.Draws, b.Draws);
        Assert.Equal(ua.Select(x => (x.Hull, string.Join(",", x.Hits))), ub.Select(x => (x.Hull, string.Join(",", x.Hits))));
        Assert.True(a.Draws > 0);
    }

    // B3-28 [Model]
    [Fact]
    public void B3_28_TwelveCorvettesVsIsdBothFight()
    {
        var (r, u) = Run(P, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, alert: Alert(Fight, Fight));
        Assert.Equal(35, r.Seconds);
        Assert.True(r.EmpireRetreated);
        Assert.Equal(3, u.Single(x => x.Side == Side.Empire).Hits[0]);
        Assert.Equal(232.5, u.First(x => x.Side == Side.Alliance).Hull, 0.1);
        Assert.Equal(0, r.AllianceLosses);
        Assert.Equal(138, r.Draws);
    }

    // B3-29 [Model]
    [Fact]
    public void B3_29_SixYWingsVsIsdBothFight()
    {
        var (r, _) = Run(P, new[] { ("Y-wing", 6) }, new[] { (Isd, 1) }, alert: Alert(Fight, Fight));
        Assert.Equal(2, r.Seconds);
        Assert.True(r.EmpireRetreated);
        Assert.Equal(3, r.Draws);
    }

    // [Model] the other scenarios of b3_simulate.py
    [Fact]
    public void ModelScenarios()
    {
        var ai = new bool[2]; ai[(int)Side.Empire] = true; // the model's 0.9 Empire
        var (r, u) = QuickBattle.Run(P, Ships, new[] { (Corvette, 4) }, new[] { (Isd, 1) }, 1, alert: Alert(Fight, Fight), ai: ai);
        Assert.Equal((31, 94L), (r.Seconds, r.Draws));
        Assert.True(r.EmpireRetreated);
        Assert.Equal(304.6, u[0].Hull, 0.1);
        (r, u) = QuickBattle.Run(P, Ships, new[] { ("Mon Calamari Cruiser", 2) }, new[] { (Isd, 2) }, 1, alert: Alert(Fight, Fight), ai: ai);
        Assert.Equal((35, 72L), (r.Seconds, r.Draws));
        Assert.True(r.AllianceRetreated);
        Assert.Equal(1682.8, u[0].Hull, 0.1);
        Assert.Equal(2053.7, u[2].Hull, 0.1);
        (r, u) = QuickBattle.Run(P, Ships, new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) }, 1, alert: Alert(Fight, Fight));
        Assert.Equal((31, 62L), (r.Seconds, r.Draws));
        Assert.True(r.AllianceRetreated);
        (r, _) = QuickBattle.Run(P, Ships, new[] { ("Y-wing", 6) }, new[] { (Isd, 1) }, 1, alert: Alert(Fight, Fight), ai: ai);
        Assert.Equal((1, 1L), (r.Seconds, r.Draws));
    }

    // ================================================================== the old BC battles under the B3 rules (no controller: threshold 0.33)

    // m4-battle-classic BC5 (moved by B3: squads of 4, Empire first; the ISD out-shoots one Corvette at a time (20 >= recharge 10), so it leaves the
    // strength; the Corvettes' three squads of 5.34 never pass the ISD's recharge 15. One Corvette dies every 54 s; at 5 left (5 x 1.336 / 20 < 0.33,
    // the out-shot one not counted) they leave. Was: t = 400, one survivor at hull 468.3. [Model]
    [Fact]
    public void BC5_TwelveCorvettesVsIsdRollsOff()
    {
        var (r, u) = Run(NoRolls, new[] { (Corvette, 12) }, new[] { (Isd, 1) });
        Assert.True(r.AllianceRetreated);
        Assert.Equal(379, r.Seconds);
        Assert.Equal(5, r.AllianceSurvivors);
        Assert.Equal(7, r.AllianceLosses);
        Assert.Equal(2750.0, u.Single(x => x.Side == Side.Empire).Hull);
        Assert.Equal(0, r.Draws);
        Assert.All(u.Where(x => x.Side == Side.Alliance && !x.Destroyed), x => Assert.Equal(500.0, x.Hull));
        Assert.Equal(new[] { 54, 108, 162, 216, 270, 324, 378 }, u.Where(x => x.Destroyed).Select(x => x.LostAt));
    }

    // m4-battle-classic BC6 (moved by B3 as BC5: was t = 346) [Model]
    [Fact]
    public void BC6_ElevenCorvettesVsIsdRollsOff()
    {
        var (r, u) = Run(NoRolls, new[] { (Corvette, 11) }, new[] { (Isd, 1) });
        Assert.True(r.AllianceRetreated);
        Assert.Equal(325, r.Seconds);
        Assert.Equal(5, r.AllianceSurvivors);
        Assert.Equal(2750.0, u.Single(x => x.Side == Side.Empire).Hull);
    }

    // m4-battle-classic BC7 (moved by B3: 4 Corvettes at 0.267 of the ISD still leave in second 1, but only after the Empire's volley)
    [Fact]
    public void BC7_FourCorvettesRetreatAtOnce()
    {
        var (r, u) = Run(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1) });
        Assert.True(r.AllianceRetreated);
        Assert.Equal(1, r.Seconds);
        Assert.All(u, x => Assert.Equal(x.HullMax, x.Hull));
        Assert.Equal(180.0, u[0].Shield, 1e-3); // the ISD's 20 hit Corvette 1 first
    }

    // m4-battle-classic BC8 (moved by B3: 30 Corvettes form 5 squads of 6 (8.0 each, under the ISD's recharge 15), but the five together still drain it;
    // once a squad out-shoots the unshielded ISD its strength is 0 and it leaves. Was: t = 46 at hull 1790.0) [Model]
    [Fact]
    public void BC8_ThirtyCorvettesBeatTheIsdRollsOff()
    {
        var (r, u) = Run(NoRolls, new[] { (Corvette, 30) }, new[] { (Isd, 1) });
        Assert.True(r.EmpireRetreated);
        Assert.Equal(25, r.Seconds);
        Assert.Equal(2423.1, u.Single(x => x.Side == Side.Empire).Hull, 0.1);
        Assert.Equal(0, r.AllianceLosses);
    }

    // m4-battle-classic BC10, m4-retreat-damage-repair RT4 (pure part)
    [Fact]
    public void BC10_GravityWellStopsTheRetreat()
    {
        var (r, u) = Run(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1), (Interdictor, 1) });
        Assert.False(r.AllianceRetreated);
        Assert.Equal(BattleOutcome.Annihilated, r.AllianceOutcome);
        Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
        Assert.All(u.Where(x => x.Side == Side.Empire), x => Assert.Equal(x.HullMax, x.Hull));
        Assert.Equal(RetreatRefusal.GravityWell, r.Steps.OfType<RetreatRefusedStep>().Single().Reason);
    }

    // m4-battle-classic BC11 (moved by B3: the six Y-wings form one group of 34.5 a second against the ISD's recharge 15, so the ISD is out-shot and leaves
    // in second 2, before torpedoes ever matter. Was: t = 83 at hull 1349.8, one squadron at hull 17) [Model]
    [Fact]
    public void BC11_SixYWingsVsIsd()
    {
        var (r, u) = Run(NoRolls, new[] { ("Y-wing", 6) }, new[] { (Isd, 1) });
        Assert.True(r.EmpireRetreated);
        Assert.Equal(2, r.Seconds);
        Assert.Equal(2750.0, u.Single(x => x.Side == Side.Empire).Hull);
        Assert.All(u.Where(x => x.Side == Side.Alliance), x => Assert.Equal(96.0, x.Hull));
    }

    // m4-battle-classic BC12 (moved by B3: the Empire ISD fires first and out-shoots the Alliance ISD (20 >= 15), whose strength is 0: it leaves in second 1.
    // Was: both destroyed at t = 1492) [Model]
    [Fact]
    public void BC12_IsdVsIsd()
    {
        var (r, u) = Run(NoRolls, new[] { (Isd, 1) }, new[] { (Isd, 1) });
        Assert.Equal(1, r.Seconds);
        Assert.True(r.AllianceRetreated);
        Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
        Assert.All(u, x => Assert.Equal(2750.0, x.Hull));
    }

    // m4-battle-classic BC13, m4-retreat-damage-repair RT12 (unchanged: the timers pass 1200 at the end of second 1200, the Victory II jumps in 1201)
    [Fact]
    public void BC13_StalemateWeakerSideRetreats()
    {
        var (r, u) = Run(NoRolls, new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) });
        Assert.Equal(1201, r.Seconds);
        Assert.True(r.EmpireRetreated);
        Assert.False(r.AllianceRetreated);
        Assert.All(u, x => Assert.Equal(x.HullMax, x.Hull));
    }

    // m4-battle-classic BC14, m4-retreat-damage-repair RT13 (moved by B3: destroyed at the end of second 1200, not 1201)
    [Fact]
    public void BC14_StalemateSideThatCannotRetreatIsDestroyed()
    {
        var (r, u) = Run(NoRolls, new[] { ("Strike Cruiser", 1), (Cc7700, 1) }, new[] { ("Victory II Star Destroyer", 1) });
        Assert.Equal(1200, r.Seconds);
        var v = u.Single(x => x.Side == Side.Empire);
        Assert.Equal(UnitLossReason.Stalemate, v.Loss);
        Assert.Equal(BattleOutcome.Annihilated, r.EmpireOutcome);
    }

    // m4-battle-classic BC15 (B3: with the Original dice the seed is not used at all)
    [Fact]
    public void BC15_SameBattleSameResult()
    {
        var (a, ua) = Run(P, new[] { (Corvette, 8) }, new[] { (Isd, 1) }, 77);
        var (b, ub) = Run(P, new[] { (Corvette, 8) }, new[] { (Isd, 1) }, 77);
        Assert.Equal((a.Seconds, a.Draws, a.AllianceOutcome), (b.Seconds, b.Draws, b.AllianceOutcome));
        Assert.Equal(ua.Select(x => (x.Hull, string.Join(",", x.Hits))), ub.Select(x => (x.Hull, string.Join(",", x.Hits))));
        Assert.True(a.Draws > 0);
    }

    // m4-battle-classic BC9 (moved by B3: informational; with the Original dice every battle of the same fleets is the same, so the 400 seeds give one result)
    [Fact]
    public void BC9_TwelveCorvettesVsIsdRollsOn()
    {
        var (r, u) = Run(P, new[] { (Corvette, 12) }, new[] { (Isd, 1) });
        var (r2, _) = Run(P, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 400);
        Assert.Equal((r.Seconds, r.Draws), (r2.Seconds, r2.Draws));
        Console.WriteLine($"BC9 12 CR90 vs ISD, rolls on, Original dice: t = {r.Seconds}, draws {r.Draws}, Alliance {r.AllianceOutcome} (lost {r.AllianceLosses}), Empire {r.EmpireOutcome}, ISD hull {u.Single(x => x.Side == Side.Empire).Hull:0.0}");
    }

    // m4-battle-classic BC17 (moved by B3: the Empire's step comes first; the Bulk Transport's zero hit draws nothing with rolls off; both leave in second 1)
    [Fact]
    public void BC17_UnarmedFleetsBothRetreatAtOnce()
    {
        var (r, _) = Run(NoRolls, new[] { ("Galleon", 1) }, new[] { ("Bulk Transport", 1) });
        Assert.Equal(1, r.Seconds);
        Assert.True(r.AllianceRetreated && r.EmpireRetreated);
        Assert.Equal(BattleOutcome.Indecisive, r.AllianceOutcome);
    }

    // m4-battle-classic BC18 (moved by B3: an alert Retreat is a threshold of 10000 inside the loop, not a skip at second 0: the Corvettes take the Empire's
    // volley, fire once and leave in second 1; the battle draws from its own dice only)
    [Fact]
    public void BC18_AlertRetreatLeavesInSecondOne()
    {
        var (r, _) = Run(P, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 5, alert: Alert(Retreat, Fight));
        Assert.Equal(1, r.Seconds);
        Assert.True(r.AllianceRetreated);
        Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
        Assert.True(r.Draws > 0);
    }

    // m4-battle-classic BC19
    [Fact]
    public void BC19_IonOnlyShipsSkipSquadrons()
    {
        var ion = Unit(Isd);
        ion.Class = Ships[Isd] with { Arcs = new ShipArcs(new ArcCounts(0, 100, 0), default, default, default) };
        Assert.Equal(0.0, ClassicRates.Rate(P, ion, Unit("X-wing", Side.Empire), false));
        Assert.True(ClassicRates.Rate(P, Unit(Corvette), Unit("X-wing", Side.Empire), false) > 0);
    }

    // ================================================================== retreat execution (m4-retreat-damage-repair §1, §2)

    // m4-retreat-damage-repair RT5
    [Fact]
    public void RT5_ShipWithoutWorkingHyperdriveIsDestroyedOnRetreat()
    {
        var (b, _, e) = Make(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1), ("Carrack Light Cruiser", 1) }, Alert(Fight, Retreat),
            prepare: (_, e) => e[0].Hits[4] = 2);
        var r = b.Run();
        Assert.True(r.EmpireRetreated);
        Assert.Equal(UnitLossReason.NoHyperdrive, e[0].Loss);
        Assert.True(e[1].Left);
    }

    // m4-retreat-damage-repair RT6
    [Fact]
    public void RT6_NoWorkingHyperdriveRefusesTheRetreat()
    {
        var (b, _, _) = Make(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1) }, Alert(Fight, Retreat), prepare: (_, e) => e[0].Hits[4] = 2);
        var r = b.Run();
        Assert.Equal(RetreatRefusal.NoHyperdrive, r.Steps.OfType<RetreatRefusedStep>().First().Reason);
        Assert.False(r.EmpireRetreated);
    }

    // m4-retreat-damage-repair §1.3 (B3): four sub-light hits stop a jump too
    [Fact]
    public void SubLightHitsStopAJump()
    {
        var (b, _, _) = Make(NoRolls, new[] { (Corvette, 1) }, new[] { (Isd, 1) }, Alert(Fight, Retreat), prepare: (_, e) => e[0].Hits[3] = 4);
        Assert.Equal(RetreatRefusal.NoHyperdrive, b.CanRetreat(Side.Empire));
    }

    // m4-retreat-damage-repair RT7
    [Fact]
    public void RT7_DeathStarJumpsRegardlessOfItsCounters()
    {
        var (b, _, e) = Make(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1), ("Death Star", 1) }, Alert(Fight, Retreat),
            prepare: (_, e) => { foreach (var u in e) u.Hits[4] = 2; });
        var r = b.Run();
        Assert.True(r.EmpireRetreated);
        Assert.True(e[1].Left);
        Assert.Equal(UnitLossReason.NoHyperdrive, e[0].Loss);
    }

    // m4-retreat-damage-repair RT8, RT9 (B3 §11: squadrons dock in their own carrier, then any own ship with room; launched ones lost their place)
    [Theory]
    [InlineData(6, 2)]
    [InlineData(4, 3)]
    public void RT8_RT9_SquadronsNeedRoomOnRetreat(int aboard, int based)
    {
        var (b, _, e) = Make(NoRolls, new[] { (Corvette, 4) }, new[] { (Isd, 1), ("TIE Fighter", aboard + based) }, Alert(Fight, Retreat), prepare: (_, e) =>
        {
            foreach (var q in e.Where(x => !x.IsCapital).Take(aboard)) q.Carrier = e[0].ObjectId;
        });
        b.Run();
        var sq = e.Where(x => !x.IsCapital).ToList();
        int room = 6;
        Assert.Equal(Math.Max(0, aboard + based - room), sq.Count(x => x.Loss == UnitLossReason.NoBerth));
        Assert.Equal(Math.Min(room, aboard + based), sq.Count(x => x.Left));
    }

    // m4-retreat-damage-repair RT10
    [Fact]
    public void RT10_BasedSquadronWithHyperdriveJumpsAlone()
    {
        var (b, a, _) = Make(NoRolls, new[] { ("X-wing", 1) }, new[] { (Isd, 1) }, Alert(Retreat, Fight));
        b.Run();
        Assert.True(a[0].Left);
        Assert.True(a[0].JumpedAlone);
        Assert.Equal(0, a[0].NewCarrier);
    }

    // m4-retreat-damage-repair RT11 (B3: the 0.33 is only the no-controller value; the test itself is RetreatWanted)
    [Fact]
    public void RT11_MoraleThreshold()
    {
        Assert.True(ClassicRates.RetreatWanted(6.6, 20.0, NoRolls.RetreatThreshold));
        Assert.False(ClassicRates.RetreatWanted(6.7, 20.0, NoRolls.RetreatThreshold));
        Assert.True(ClassicRates.RetreatWanted(0, 0, NoRolls.RetreatThreshold));
        Assert.False(ClassicRates.RetreatWanted(5, 0, NoRolls.RetreatThreshold));
    }

    // m4-retreat-damage-repair RT14 (B3 §12: DeathStarStalemate DEFAULT on)
    [Fact]
    public void RT14_DeathStarStalemateSwitch()
    {
        Assert.True(new BattleParams { Ruleset = BattleRuleset.Classic }.DeathStarStalemate);
        // no Death Star in the battle: the switch changes nothing
        var (r, _) = Run(NoRolls, new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) }, alert: Alert(Fight, Fight));
        Assert.Equal(1201, r.Seconds);
        Assert.True(r.EmpireRetreated);
    }

    // ================================================================== the battle dice (§1.4, §11)

    // B3-1
    [Fact]
    public void B3_1_TheOriginalBattleGenerator()
    {
        var g = new LehmerBattleRng(12345678, 16);
        Assert.Equal(1_894_809_716, g.State);
        Assert.Equal(new[] { 79, 70, 35, 61, 1, 73, 52, 8, 71, 25, 68, 36 }, Enumerable.Range(0, 12).Select(_ => g.Rand(100)));
        long s = g.State, d = g.Draws;
        Assert.Equal(0, g.Rand(1));
        Assert.Equal(s, g.State);
        Assert.Equal(d, g.Draws);
    }

    // m4-battle-classic §1.4: ClassicBattleRng = Game draws from the battle's GameRng (seeded per battle)
    [Fact]
    public void GameDiceDependOnTheSeed()
    {
        var p = new BattleParams { Ruleset = BattleRuleset.Classic, BattleRng = ClassicBattleRng.Game };
        var (a, _) = Run(p, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 3, alert: Alert(Fight, Fight));
        var (b, _) = Run(p, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 3, alert: Alert(Fight, Fight));
        Assert.Equal((a.Seconds, a.Draws), (b.Seconds, b.Draws));
        Assert.True(a.Draws > 0);
    }

    // m4-battle-classic §2, §3, §6: the switches run to an end
    [Theory]
    [InlineData(BattleTactic.Surround, ClassicTaskForces.Original, ClassicFireOrder.EmpireFirst)]
    [InlineData(BattleTactic.StandOff, ClassicTaskForces.PerSide, ClassicFireOrder.EmpireFirst)]
    [InlineData(BattleTactic.StandOff, ClassicTaskForces.PerUnit, ClassicFireOrder.Simultaneous)]
    public void SwitchesRunToAnEnd(BattleTactic t, ClassicTaskForces f, ClassicFireOrder o)
    {
        var p = new BattleParams { Ruleset = BattleRuleset.Classic, Tactic = t, TaskForces = f, FireOrder = o };
        var (r, _) = Run(p, new[] { (Corvette, 12) }, new[] { (Isd, 1) }, 9, alert: Alert(Fight, Fight));
        Assert.True(r.Seconds > 0);
        Assert.True(r.AllianceRetreated || r.EmpireRetreated || r.AllianceLosses + r.EmpireLosses > 0);
    }

    // Corvettes against an ISD with the Game dice: a spread over seeds, only determinism asserted.
    [Theory]
    [InlineData(8)]
    [InlineData(30)]
    public void CorvettesVsIsdRatesAreDeterministic(int n)
    {
        Assert.Equal(Rates(n, 100), Rates(n, 100));
    }

    internal static (int AllianceWins, int EmpireWins, int Draws, int AllianceRetreats, int EmpireRetreats, int Survivors) Rates(int corvettes, int battles)
    {
        int aw = 0, ew = 0, dr = 0, ar = 0, er = 0, surv = 0;
        var p = new BattleParams { Ruleset = BattleRuleset.Classic, BattleRng = ClassicBattleRng.Game };
        for (int s = 1; s <= battles; s++)
        {
            var (r, _) = QuickBattle.Run(p, Ships, new[] { (Corvette, corvettes) }, new[] { (Isd, 1) }, (ulong)s, alert: Alert(Fight, Fight));
            if (r.AllianceOutcome == BattleOutcome.Victorious) aw++;
            else if (r.EmpireOutcome == BattleOutcome.Victorious) ew++;
            else dr++;
            if (r.AllianceRetreated) ar++;
            if (r.EmpireRetreated) er++;
            surv += r.AllianceSurvivors;
        }
        return (aw, ew, dr, ar, er, surv);
    }
}
