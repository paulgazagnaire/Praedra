using Rebellion.Core.Assault;
using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Defenses;
using Rebellion.Core.Troops;

namespace Rebellion.Core.Tests.Assault;

// m3-assault, tests A1 to A21 (A13 to A16 are in AssaultStatTests), then extra tests named after the rule they check.
// Real data files. Stub generator unless a test says seeded. Events of slot 5.2 carry day = K + 1.
public class AssaultTests
{
    private static readonly Side Emp = Side.Empire, All = Side.Alliance;
    private const string Sys = AssaultWorld.Sys, Reb = AssaultWorld.Reb, Imp = AssaultWorld.Imp;

    // ------------------------------------------------------------------ the pure rules

    private static AssaultModule Pure() => new AssaultWorld().Assault;

    // percent rounded half up as units/07 does
    private static int Pct(int k) => (int)Math.Floor(k * 100.0 / 11 + 0.5);

    /// <summary>The exact (defender dies, attacker dies, nobody) counts out of 11 for x = r + diff over r = 0..10.</summary>
    private static (int D, int A, int N) Exact(AssaultModule m, int diff, int la = 0, int ld = 0)
    {
        int d = 0, a = 0, n = 0;
        for (int r = 0; r <= 10; r++)
            switch (m.DuelResult(m.DuelValue(r, diff, 0, la, ld)))
            {
                case DuelOutcome.DefenderDestroyed: d++; break;
                case DuelOutcome.AttackerDestroyed: a++; break;
                default: n++; break;
            }
        return (d, a, n);
    }

    // m3-assault A1: the exact duel table for attack - defense -6..+6
    [Fact]
    public void A1_ExactDuelTable()
    {
        var m = Pure();
        var table = new Dictionary<int, (int, int, int)>
        {
            [-6] = (0, 100, 0), [-5] = (0, 91, 9), [-4] = (9, 82, 9), [-3] = (18, 73, 9), [-2] = (27, 64, 9), [-1] = (36, 55, 9), [0] = (45, 45, 9),
            [1] = (55, 36, 9), [2] = (64, 27, 9), [3] = (73, 18, 9), [4] = (82, 9, 9), [5] = (91, 0, 9), [6] = (100, 0, 0),
        };
        foreach (var (diff, want) in table)
        {
            var (d, a, n) = Exact(m, diff);
            Assert.Equal(want, (Pct(d), Pct(a), Pct(n)));
        }
        Assert.Equal((5, 5, 1), Exact(m, 0)); // exact fractions k / 11
        Assert.Equal((0, 10, 1), Exact(m, -5));
    }

    // m3-assault A2: the 10 x 10 matrix of units/07 from the class table's attack and defense
    [Fact]
    public void A2_MatrixOfUnits07()
    {
        string[] names = { "Alliance Fleet Regiment", "Alliance Army Regiment", "Sullustan Regiment", "Wookiee Regiment", "Mon Calamari Regiment",
            "Stormtrooper Regiment", "Imperial Fleet Regiment", "Imperial Army Regiment", "War Droid Regiment", "Dark Trooper Regiment" };
        string[] rows =
        {
            "73/18 55/36 64/27 64/27 27/64 45/45 73/18 55/36 82/9 27/64",
            "45/45 27/64 36/55 36/55 0/91 18/73 45/45 27/64 55/36 0/91",
            "27/64 9/82 18/73 18/73 0/100 0/91 27/64 9/82 36/55 0/100",
            "91/0 73/18 82/9 82/9 45/45 64/27 91/0 73/18 100/0 45/45",
            "36/55 18/73 27/64 27/64 0/100 9/82 36/55 18/73 45/45 0/100",
            "73/18 55/36 64/27 64/27 27/64 45/45 73/18 55/36 82/9 27/64",
            "64/27 45/45 55/36 55/36 18/73 36/55 64/27 45/45 73/18 18/73",
            "45/45 27/64 36/55 36/55 0/91 18/73 45/45 27/64 55/36 0/91",
            "91/0 73/18 82/9 82/9 45/45 64/27 91/0 73/18 100/0 45/45",
            "91/0 73/18 82/9 82/9 45/45 64/27 91/0 73/18 100/0 45/45",
        };
        var m = Pure(); var data = AssaultWorld.Troops.Value;
        int cells = 0;
        for (int a = 0; a < 10; a++)
        {
            var cols = rows[a].Split(' ');
            for (int d = 0; d < 10; d++)
            {
                var (kd, ka, _) = Exact(m, data.Regiment(names[a])!.Attack - data.Regiment(names[d])!.Defense);
                Assert.Equal(cols[d], $"{Pct(kd)}/{Pct(ka)}");
                cells++;
            }
        }
        Assert.Equal(100, cells);
    }

    // m3-assault A3: 10,000 duels per attack - defense -6..+6 with the seeded game generator, one rand(0..10) each; within 2.0 points of k/11
    [Theory]
    [MemberData(nameof(Diffs))]
    public void A3_SeededDuelRates(int diff)
    {
        var m = Pure();
        var rng = new GameRng((ulong)(300 + diff + 6));
        int d = 0, a = 0, n = 0;
        const int trials = 10_000;
        for (int i = 0; i < trials; i++)
            switch (m.DuelResult(m.DuelValue(rng.NextInt(0, m.Params.DieMax), diff, 0)))
            {
                case DuelOutcome.DefenderDestroyed: d++; break;
                case DuelOutcome.AttackerDestroyed: a++; break;
                default: n++; break;
            }
        var (ed, ea, en) = Exact(m, diff);
        Assert.True(Math.Abs(100.0 * d / trials - 100.0 * ed / 11) <= 2.0, $"diff {diff}: defender dies {100.0 * d / trials:F2}");
        Assert.True(Math.Abs(100.0 * a / trials - 100.0 * ea / 11) <= 2.0, $"diff {diff}: attacker dies {100.0 * a / trials:F2}");
        Assert.True(Math.Abs(100.0 * n / trials - 100.0 * en / 11) <= 2.0, $"diff {diff}: nobody {100.0 * n / trials:F2}");
    }

    public static IEnumerable<object[]> Diffs() => Enumerable.Range(-6, 13).Select(d => new object[] { d });

    // m3-assault A5: leadership steps of 30
    [Theory]
    [InlineData(0, 0), InlineData(29, 0), InlineData(30, 1), InlineData(59, 1), InlineData(60, 2), InlineData(89, 2), InlineData(90, 3), InlineData(119, 3), InlineData(120, 4)]
    public void A5_LeadershipSteps(int leadership, int steps) => Assert.Equal(steps, Pure().LeadershipSteps(leadership));

    // m3-assault A5: both generals 90 cancel
    [Fact]
    public void A5_BothGeneralsNinetyCancel() => Assert.Equal(Pure().DuelValue(7, 3, 5), Pure().DuelValue(7, 3, 5, 90, 90));

    // m3-assault A4 (exact part): L_att 90 for an Army attacker (atk 3) against an Army defender (def 5) is the +1 row
    [Fact]
    public void A4_LeadershipNinetyIsThePlusOneRow()
    {
        var m = Pure();
        var (d, a, n) = Exact(m, 3 - 5, la: 90);
        Assert.Equal((55, 36, 9), (Pct(d), Pct(a), Pct(n)));
        Assert.Equal(Exact(m, 1), (d, a, n));
    }

    // m3-assault A6: battery chance attack / 5; the first three hit on every draw, the shield never
    [Fact]
    public void A6_BatteryChance()
    {
        var def = new AssaultWorld().B.Def;
        var data = def.Data;
        var got = new[] { "KDY-150", "LNR Series I", "LNR Series II", "GenCore Level I" }.Select(n => def.ChancePercent(data.Get(n).Attack)).ToArray();
        Assert.Equal(new[] { 400, 160, 1000, 0 }, got);
        Assert.All(got.Take(3), c => Assert.True(c > 99));
    }

    // ------------------------------------------------------------------ small assaults with stub draws

    // m3-assault A7: 4 attackers, one KDY, D0 = 0: one attacker destroyed, 3 land, win, duels 0, collateral 0, draws r and v only
    [Fact]
    public void A7_BatteryThenLanding()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int kdy = w.B.Facility(FacilityKind.Kdy150);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        var regs = w.Aboard(fleet, Reb, 4);
        w.Stub.Queue(0, 1); // r = 0 hits (chance 400), v = 1 picks the second attacker
        Assert.Null(w.Try(All, fleet));
        var hit = Assert.Single(w.Events<BatteryHit>());
        Assert.Equal((kdy, regs[1]), (hit.Facility, hit.Regiment));
        var res = w.LastResolved();
        Assert.Equal(("Won", 4, 3, 0, 0, 0, 0), (res.Result, res.A0, res.A, res.D0, res.D, res.Duels, res.Rounds));
        Assert.Equal(new[] { regs[0], regs[2], regs[3] }, Assert.Single(w.Events<AssaultLanded>()).Regiments.Items);
        Assert.Null(w.Eco.Find(regs[1]));
        // (B5a, 2026-10-17) the win flips the world inside slot 5.2; at Alliance support 0 the landed regiments are short of N = 6, so the capture starts the uprising at once (its (0,70) draw): B5a section 7, m2-control-and-capture C7
        Assert.Equal(new[] { (0, 99), (0, 3), (0, 70) }, w.AssaultDraws);
        Assert.Equal(Alliance(), w.Control.HolderOf(Sys));
        Assert.Contains(w.Events<RegimentDestroyed>(), e => e.Reason == "AssaultBattery" && e.Object == regs[1]);
    }

    private static Side? Alliance() => All;

    // m3-assault A8: 4 Alliance Fleet regiments against nobody and no facility: win, 4 land, SetHolder, no draw at all
    [Fact]
    public void A8_EmptyWorldNoDraws()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        var regs = w.Aboard(fleet, "Alliance Fleet Regiment", 4);
        Assert.Null(w.Try(All, fleet));
        // (B5a, 2026-10-17) the win flips the world inside slot 5.2; at Alliance support 0 the landed regiments are short of N = 6, so the capture starts the uprising at once (its (0,70) draw): B5a section 7, m2-control-and-capture C7
        Assert.Equal(new[] { (0, 70) }, w.AssaultDraws); // the assault itself draws nothing
        var res = w.LastResolved();
        Assert.Equal(("Won", 4, 4), (res.Result, res.A0, res.A));
        Assert.Equal(regs, Assert.Single(w.Events<AssaultLanded>()).Regiments.Items);
        Assert.All(regs, id => Assert.Equal((0, Sys), (w.Eco.Find(id)!.AboardShip, w.Eco.Find(id)!.System)));
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal((Sys, Emp, All, CaptureCause.Assault), (cap.System, cap.From, cap.To, cap.Cause));
        Assert.Equal(All, w.Control.HolderOf(Sys));
        Assert.False(w.Assault.HasPending(fleet));
    }

    // m3-assault A9: 9 attackers against nobody: 6 land (ids 1..6 of the nine, lowest first), 3 stay aboard
    [Fact]
    public void A9_LandingCapSix()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        var regs = w.Aboard(fleet, Reb, 9);
        Assert.Null(w.Try(All, fleet));
        Assert.Equal(regs.Take(6), Assert.Single(w.Events<AssaultLanded>()).Regiments.Items);
        Assert.All(regs.Take(6), id => Assert.Equal(Sys, w.Eco.Find(id)!.System));
        Assert.All(regs.Skip(6), id => Assert.NotEqual(0, w.Eco.Find(id)!.AboardShip));
        Assert.Equal(("Won", 9, 9), (w.LastResolved().Result, w.LastResolved().A0, w.LastResolved().A));
    }

    // m3-assault landing cap is data (DEFAULT 6, Open question 1)
    [Fact]
    public void LandingCapIsAParameter()
    {
        var w = new AssaultWorld(p: new AssaultParams { LandingCap = 2 });
        w.Hold(Emp);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        var regs = w.Aboard(fleet, Reb, 4);
        Assert.Null(w.Try(All, fleet));
        Assert.Equal(regs.Take(2), Assert.Single(w.Events<AssaultLanded>()).Regiments.Items);
    }

    // ------------------------------------------------------------------ the order (A10, A11, A12)

    private static void Gen(AssaultWorld w, params FacilityKind[] kinds) { foreach (var k in kinds) w.B.Facility(k); }

    // m3-assault A10: two GenCore I, or one I and one II: FullyShielded; one GenCore: accepted; two Death Star Shields: accepted
    [Theory]
    [InlineData(new[] { FacilityKind.GenCoreLevelI, FacilityKind.GenCoreLevelI }, AssaultReject.FullyShielded)]
    [InlineData(new[] { FacilityKind.GenCoreLevelI, FacilityKind.GenCoreLevelII }, AssaultReject.FullyShielded)]
    [InlineData(new[] { FacilityKind.GenCoreLevelI }, null)]
    [InlineData(new[] { FacilityKind.DeathStarShield, FacilityKind.DeathStarShield }, null)]
    public void A10_ShieldGate(FacilityKind[] kinds, string? expected)
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        Gen(w, kinds);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(fleet, Reb, 2);
        Assert.Equal(expected, w.Try(All, fleet));
        if (expected != null) Assert.Empty(w.Events<AssaultStarted>());
    }

    // m3-assault A10 (both sides): the gate also forbids the holder's own counter-landing on a system it lost, and a neutral system
    [Fact]
    public void A10_GateAppliesToNeutralSystemsToo()
    {
        var w = new AssaultWorld();
        Gen(w, FacilityKind.GenCoreLevelI, FacilityKind.GenCoreLevelII);
        int fleet = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(fleet, Reb, 2);
        Assert.Equal(AssaultReject.FullyShielded, w.Try(All, fleet));
    }

    // m3-assault A11: own system, no regiment aboard, fleet in transit, enemy fleet at the system (and the unknown/not-yours/not-at-system codes)
    [Fact]
    public void A11_Rejections()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        Assert.Equal(AssaultReject.NoTroops, w.Try(All, f));
        w.Aboard(f, Reb, 1);
        Assert.Equal(RejectReason.UnknownFleet, w.Try(All, 99999));
        Assert.Equal(RejectReason.NotYours, w.Try(Emp, f));
        Assert.Equal(AssaultReject.NotAtSystem, w.Try(All, f, "Bespin"));
        // own system
        int g = w.Fleet(Emp, system: "Bespin");
        w.Aboard(g, Imp, 1);
        w.B.Fleets.SetOwner("Bespin", Emp);
        Assert.Equal(AssaultReject.OwnSystem, w.Try(Emp, g, "Bespin"));
        // transit
        w.B.Fleets.SubmitMove(All, f, "Bespin");
        w.Run(1);
        Assert.Equal(RejectReason.InTransit, w.Try(All, f));
        // an enemy fleet at the system (any fleet, armed or not)
        var w2 = new AssaultWorld();
        w2.Hold(Emp);
        int a = w2.Fleet(All, AssaultWorld.Neb);
        w2.Aboard(a, Reb, 1);
        w2.Fleet(Emp, "Imperial Star Destroyer");
        Assert.Equal(AssaultReject.EnemyFleetPresent, w2.Try(All, a));
    }

    // m3-assault A12: pending assault, then an enemy fleet arrives before slot 5.2: cancelled, no draw
    [Fact]
    public void A12_CancelledWhenAnEnemyFleetArrives()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        var regs = w.Aboard(f, Reb, 2);
        w.Defenders(Emp, Imp, 1);
        bool done = false;
        w.Game.RegisterHook("3", _ => { if (!done) { done = true; w.Fleet(Emp, "Victory Destroyer"); } }); // the arrival of step 3
        Assert.Null(w.Try(All, f));
        var c = Assert.Single(w.Events<AssaultCancelled>());
        Assert.Equal((f, Sys, AssaultReject.EnemyFleetPresent, w.Game.Day), (c.Fleet, c.System, c.Reason, c.Day));
        Assert.Empty(w.Events<AssaultStarted>());
        Assert.Empty(w.AssaultDraws);
        Assert.False(w.Assault.HasPending(f));
        Assert.All(regs, id => Assert.NotNull(w.Eco.Find(id)));
    }

    // m3-assault check 6: no other order pending (an assault or a bombardment of the same fleet), both ways round
    [Fact]
    public void Check6_OrderPending()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, "Victory Destroyer"); // has bombardment power and room for regiments (no capacity check here)
        w.Aboard(f, Reb, 2);
        w.Submit(All, f); w.Submit(All, f);
        long second = w.Submit(All, f);
        w.Run(1);
        Assert.Equal(AssaultReject.OrderPending, w.B.RejectionOf(second)); // the first one was accepted in the same step 1
        Assert.Equal(2, w.Events<OrderRejected>().Count(e => e.Reason == AssaultReject.OrderPending));

        var v = new AssaultWorld();
        v.Hold(Emp);
        int g = v.Fleet(All, "Victory Destroyer");
        v.Aboard(g, Reb, 2);
        long bomb = v.B.Bombard(All, g, Sys, BombardCategory.Military);
        long ass = v.Submit(All, g);
        v.Run(1);
        Assert.Null(v.B.RejectionOf(bomb));
        Assert.Equal(AssaultReject.OrderPending, v.B.RejectionOf(ass));
        // the other way round
        var u = new AssaultWorld();
        u.Hold(Emp);
        int h = u.Fleet(All, "Victory Destroyer");
        u.Aboard(h, Reb, 2);
        long ass2 = u.Submit(All, h);
        long bomb2 = u.B.Bombard(All, h, Sys, BombardCategory.Military);
        u.Run(1);
        Assert.Null(u.B.RejectionOf(ass2));
        Assert.Equal(AssaultReject.OrderPending, u.B.RejectionOf(bomb2));
    }

    // ------------------------------------------------------------------ duels, generals and the draw order

    // Army (atk 3) attacks Army (def 5): x = r - 2; r = 7 gives 5 (nobody dies), r = 10 gives 8 (defender dies), r = 0 gives -2 (attacker dies)
    [Fact]
    public void DrawOrder_EngagementThenDefenderThenRoll()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        var att = w.Aboard(f, Reb, 3);
        var def = w.Defenders(Emp, Imp, 3);
        // attacker 1: e = 0 (< D = 3), j = 2, r = 10 -> defender 3 dies (D = 2); attacker 2: e = 2 (>= D = 2) skips; attacker 3: e = 1, j = 0, r = 0 -> attacker dies
        w.Stub.Queue(0, 2, 10, 2, 1, 0, 0);
        Assert.Null(w.Try(All, f));
        var duels = w.Events<DuelResolved>();
        Assert.Equal(new[] { (att[0], def[2], 8, DuelOutcome.DefenderDestroyed), (att[2], def[0], -2, DuelOutcome.AttackerDestroyed) },
            duels.Select(d => (d.Attacker, d.Defender, d.X, d.Outcome)).ToArray());
        Assert.Equal(new[] { (0, 2), (0, 2), (0, 10) }, w.AssaultDraws.Take(3).ToArray()); // e, j, r
        var res = w.LastResolved();
        Assert.Equal(("Failed", 3, 2, 3, 2, 2, 0), (res.Result, res.A0, res.A, res.D0, res.D, res.Duels, res.Rounds));
        Assert.Equal(Emp, w.Control.HolderOf(Sys)); // a failed assault changes nothing but the dead
        Assert.NotNull(w.Eco.Find(att[1]));
        Assert.NotEqual(0, w.Eco.Find(att[1])!.AboardShip);
        Assert.Null(w.Eco.Find(def[2])); Assert.Null(w.Eco.Find(att[2]));
        Assert.Empty(w.Events<AssaultLanded>());
        // the event order of the sheet
        var kinds = w.Game.Events.Select(e => e.Kind).SkipWhile(k => k != nameof(AssaultStarted)).ToList();
        Assert.Equal(new[] { "AssaultStarted", "DuelResolved", "RegimentDestroyed", "DuelResolved", "RegimentDestroyed", "AssaultResolved" }, kinds.Take(6));
    }

    // m3-assault §Resolution 2: a draw (x = 5) leaves both; the attacker that skipped is not counted as a duel
    [Fact]
    public void DrawLeavesBoth()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        var att = w.Aboard(f, Reb, 1);
        var def = w.Defenders(Emp, Imp, 1);
        w.Stub.Queue(0, 0, 7);
        w.Try(All, f);
        var d = Assert.Single(w.Events<DuelResolved>());
        Assert.Equal((5, DuelOutcome.NobodyDies), (d.X, d.Outcome));
        Assert.NotNull(w.Eco.Find(att[0])); Assert.NotNull(w.Eco.Find(def[0]));
        var res = w.LastResolved();
        Assert.Equal(("Failed", 1, 1, 1), (res.Result, res.A, res.D, res.Duels));
    }

    // m3-assault §Resolution 3: leadership of the attacker's own fleet general and the system's general enter x (steps of 30)
    [Fact]
    public void GeneralsEnterTheDuel()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 1);
        w.Defenders(Emp, Imp, 1);
        w.Assault.FleetGeneralLeadership = fl => fl.Id == f ? 95 : 0;  // +3
        w.Assault.SystemGeneralLeadership = s => s == Sys ? 60 : 0;   // +2
        w.Stub.Queue(0, 0, 7);
        w.Try(All, f);
        Assert.Equal(6, Assert.Single(w.Events<DuelResolved>()).X); // x = 7 + 3 + 3 - 5 - 2
    }

    // m3-assault §Resolution 2: with no defender (D0 = 0) there are no duels and no engagement draw
    [Fact]
    public void NoDefendersNoDuels()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 3);
        w.Try(All, f);
        Assert.Empty(w.Events<DuelResolved>());
        // (B5a, 2026-10-17) the win flips the world inside slot 5.2; at Alliance support 0 the landed regiments are short of N = 6, so the capture starts the uprising at once (its (0,70) draw): B5a section 7, m2-control-and-capture C7
        Assert.Equal(new[] { (0, 70) }, w.AssaultDraws); // no engagement draw
    }

    // m3-assault §Resolution 0: regiments aboard ships at the system do not defend; special forces and the attacker's own side take no part
    [Fact]
    public void AboardRegimentsDoNotDefend()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 2);
        int guard = w.Fleet(Emp, "Victory Destroyer", 1, "Bespin"); // an Empire fleet elsewhere carries a regiment: not at the system
        w.Aboard(guard, Imp, 1);
        int landed = w.Defenders(All, Reb, 1).Single(); // an Alliance regiment already on the system (own side): not a defender
        w.Try(All, f);
        var started = Assert.Single(w.Events<AssaultStarted>());
        Assert.Equal((2, 0), (started.A0, started.D0));
        Assert.NotNull(w.Eco.Find(landed));
    }

    // m3-assault §Resolution 0: every side other than the attacker defends a neutral system (the holder's regiments, side 3)
    [Fact]
    public void NeutralSystemAssault()
    {
        var w = new AssaultWorld();
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 2);
        Assert.Null(w.Try(All, f));
        var res = w.LastResolved();
        Assert.Equal(("Won", 2, 2, 0), (res.Result, res.A0, res.A, res.D0));
        Assert.Null(Assert.Single(w.Events<SystemCaptured>()).From);
    }

    // ------------------------------------------------------------------ collateral

    /// <summary>A world with <paramref name="duels"/> duels that nobody wins (x = 5), ready for collateral.</summary>
    private static (AssaultWorld W, int Fleet) Collateral(int duels, AssaultParams? p = null)
    {
        var w = new AssaultWorld(p: p);
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, duels);
        w.Defenders(Emp, Imp, 20);
        for (int i = 0; i < duels; i++) w.Stub.Queue(0, 0, 7); // e, j, r: Army 3 vs Army 5 gives x = 5
        return (w, f);
    }

    // m3-assault A17: 13 duels / 12 duels / 25 duels give 1 / 0 / 2 rounds
    [Theory]
    [InlineData(13, 1), InlineData(12, 0), InlineData(25, 2)]
    public void A17_CollateralRounds(int duels, int rounds)
    {
        var (w, f) = Collateral(duels);
        w.Stub.Queue(0, 0); // k = 0 (Energy, no facilities on the system)
        Assert.Null(w.Try(All, f));
        var res = w.LastResolved();
        Assert.Equal((duels, rounds), (res.Duels, res.Rounds));
        Assert.Equal(rounds, w.Events<CollateralDamage>().Count);
    }

    // m3-assault A17 (PerDuelChance): one rand(0..99) after each duel, so the draw count of that range equals the duels
    [Fact]
    public void A17_PerDuelChanceDrawsOncePerDuel()
    {
        var w = new AssaultWorld(p: new AssaultParams { CollateralMode = CollateralMode.PerDuelChance });
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 5);
        w.Defenders(Emp, Imp, 20);
        for (int i = 0; i < 5; i++) { w.Stub.Queue(0, 0, 7); w.Stub.Expect(0, 99, i == 2 ? 3 : 90); } // the third duel's chance draw (3 < 8) adds a round
        w.Stub.Queue(0); // k
        w.Try(All, f);
        Assert.Equal(5, w.AssaultDraws.Count(r => r == (0, 99)));
        var res = w.LastResolved();
        Assert.Equal((5, 1), (res.Duels, res.Rounds));
    }

    private static (AssaultWorld W, int Fleet, int Mine, int Yard) FacilityWorld()
    {
        var (w, f) = Collateral(13);
        w.Eco.SetSlots(Sys, 3, 1);
        int mine = w.B.Facility(FacilityKind.Mine), yard = w.B.Facility(FacilityKind.ConstructionYard);
        return (w, f, mine, yard);
    }

    // m3-assault A18: facilities {mine, yard}, energy 3, raw 1: k = 0 mine destroyed, 1 yard destroyed, 2 energy 2, 3 raw 0
    [Fact]
    public void A18_CollateralPicksTheMineFirst()
    {
        var (w, f, mine, yard) = FacilityWorld();
        w.Stub.Queue(0);
        w.Try(All, f);
        Assert.Null(w.Eco.Find(mine)); Assert.NotNull(w.Eco.Find(yard));
        var fd = Assert.Single(w.Events<FacilityDestroyed>());
        Assert.Equal((mine, DestroyReason.Assault), (fd.Facility, fd.Reason));
        Assert.Equal(("Facility", mine), Assert.Single(w.Events<CollateralDamage>()).Let(c => (c.Target, c.Id)));
    }

    [Fact]
    public void A18_CollateralPicksTheYardSecond()
    {
        var (w, f, mine, yard) = FacilityWorld();
        w.Stub.Queue(1);
        w.Try(All, f);
        Assert.NotNull(w.Eco.Find(mine)); Assert.Null(w.Eco.Find(yard));
    }

    [Fact]
    public void A18_CollateralEnergyMinusOneThenOverCapacityCheck()
    {
        var (w, f, mine, yard) = FacilityWorld();
        w.Stub.Queue(2);
        w.Try(All, f);
        Assert.Equal((2, 1), (w.Eco.SlotsOf(Sys).Energy, w.Eco.SlotsOf(Sys).Raw));
        Assert.NotNull(w.Eco.Find(mine)); Assert.NotNull(w.Eco.Find(yard)); // 2 installed fit 2 energy slots
        // with the energy at 2 and three facilities installed the check destroys one, drawing rand(0..n-1)
        var (v, g, m2, y2) = FacilityWorld();
        int extra = v.B.Facility(FacilityKind.OrbitalShipyard);
        v.Eco.SetSlots(Sys, 3, 1);
        v.Stub.Queue(3, 2); // k = 3 (Energy: facilities mine, yard, shipyard then Energy), then the pick among 3 installed = the shipyard
        v.Try(All, g);
        Assert.Equal(2, v.Eco.SlotsOf(Sys).Energy);
        var fd = Assert.Single(v.Events<FacilityDestroyed>());
        Assert.Equal((extra, DestroyReason.OverEnergy), (fd.Facility, fd.Reason));
    }

    [Fact]
    public void A18_CollateralRawMinusOne()
    {
        var (w, f, mine, yard) = FacilityWorld();
        w.Stub.Queue(3);
        w.Try(All, f);
        Assert.Equal((3, 0), (w.Eco.SlotsOf(Sys).Energy, w.Eco.SlotsOf(Sys).Raw));
        Assert.Contains(w.Events<FacilityDestroyed>(), e => e.Facility == mine && e.Reason == DestroyReason.OverRaw); // the mine no longer has a raw slot
    }

    // m3-assault §Resolution 4: a round on a zero value does nothing and is not re-drawn
    [Fact]
    public void CollateralOnZeroDoesNothing()
    {
        var (w, f) = Collateral(13);
        w.Eco.SetSlots(Sys, 0, 0);
        w.Stub.Queue(0);
        w.Try(All, f);
        var c = Assert.Single(w.Events<CollateralDamage>());
        Assert.False(c.Changed);
        Assert.Equal((0, 0), (w.Eco.SlotsOf(Sys).Energy, w.Eco.SlotsOf(Sys).Raw));
    }

    // m3-assault §Resolution 1: unfinished facilities do not fire; a shield draws r too (fixed draw count); neutral facilities shoot too
    [Fact]
    public void ShieldDrawsButNeverHitsAndUnfinishedDoNotFire()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        w.B.Facility(FacilityKind.GenCoreLevelI);
        w.B.Facility(FacilityKind.Kdy150, completed: false);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 2);
        w.Stub.Queue(0); // the shield's r = 0: 0 < 0 is false, no hit
        w.Try(All, f);
        // (B5a, 2026-10-17) the win flips the world inside slot 5.2; at Alliance support 0 the landed regiments are short of N = 6, so the capture starts the uprising at once (its (0,70) draw): B5a section 7, m2-control-and-capture C7
        Assert.Equal(new[] { (0, 99), (0, 70) }, w.AssaultDraws);
        Assert.Empty(w.Events<BatteryHit>());
    }

    // m3-assault §Resolution 1: while A > 0 only; a battery with no attacker left does not draw
    [Fact]
    public void BatteriesStopWhenNoAttackerIsLeft()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        w.B.Facility(FacilityKind.Kdy150); w.B.Facility(FacilityKind.LnrSeriesI);
        int f = w.Fleet(All, AssaultWorld.Neb);
        var regs = w.Aboard(f, Reb, 1);
        w.Stub.Queue(0, 0); // first battery kills the only attacker; the second draws nothing
        w.Try(All, f);
        Assert.Equal(2, w.AssaultDraws.Count);
        var res = w.LastResolved();
        Assert.Equal(("Failed", 1, 0), (res.Result, res.A0, res.A));
        Assert.Null(w.Eco.Find(regs[0]));
    }

    // ------------------------------------------------------------------ what a win does (A19) and sides (A21)

    // m3-assault A19: a win on a system with support 20 for the new holder and 6 landed Army: capture events in the control sheet's order, slot 6.1 sees N = 4 and G = 6
    [Fact]
    public void A19_WinLandsSixAndTheGarrisonCoversN()
    {
        var w = new AssaultWorld();
        w.Hold(All, support: 80); // Empire support 20
        int f = w.Fleet(Emp);
        w.Aboard(f, Imp, 9);
        Assert.Null(w.Try(Emp, f));
        Assert.Equal(4, SupportModule.RequiredGarrison(new SupportParams(), 20, false, false));
        var kinds = w.Game.Events.Select(e => e.Kind).SkipWhile(k => k != nameof(AssaultStarted)).ToList();
        Assert.Equal(new[] { "AssaultStarted", "AssaultLanded", "AssaultResolved", "SystemCaptured" }, kinds.Take(4));
        Assert.Empty(w.Events<UprisingStarted>());
        Assert.Equal(6, w.Eco.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.System == Sys));
        Assert.Equal(Emp, w.Control.HolderOf(Sys));
    }

    // m3-assault §What a win does: the old holder's facilities change side with the system; support is unchanged by the assault
    [Fact]
    public void WinDoesNotChangeSupport()
    {
        var w = new AssaultWorld();
        w.Hold(Emp, support: 90);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, Reb, 2);
        int before = w.Eco.SupportOf(Sys, Side.Alliance);
        w.Try(All, f);
        Assert.Equal(All, w.Control.HolderOf(Sys));
        Assert.Equal(before, w.Eco.SupportOf(Sys, Side.Alliance));
        Assert.Empty(w.Events<SupportChanged>());
    }

    // m3-assault A21: Alliance and Empire both assault one neutral system in one Step: the Alliance (side 1) first; the Empire is re-checked against the new holder
    [Fact]
    public void A21_TwoSidesOneNeutralSystem()
    {
        var w = new AssaultWorld(stub: false, seed: 21, p: new AssaultParams { EnemyFleetBlocks = false }); // both fleets stand there: check 5 off
        int a = w.Fleet(All, AssaultWorld.Neb);
        int e = w.Fleet(Emp);
        var ar = w.Aboard(a, Reb, 3); var er = w.Aboard(e, Imp, 3);
        long so = w.Submit(Emp, e), sa = w.Submit(All, a); // the Empire's order is queued first on purpose
        w.Run(1);
        Assert.Null(w.B.RejectionOf(so)); Assert.Null(w.B.RejectionOf(sa));
        var started = w.Events<AssaultStarted>();
        Assert.Equal(new[] { All, Emp }, started.Select(s => s.Side).ToArray());
        Assert.Equal((3, 0), (started[0].A0, started[0].D0));        // the Alliance lands on the empty neutral system
        Assert.Equal((3, 3), (started[1].A0, started[1].D0));        // the Empire now faces the three landed Alliance regiments
        Assert.Equal(new Side?[] { Side.Alliance }, w.Events<SystemCaptured>().Take(1).Select(c => c.To).ToArray());
    }

    // m3-assault §Where it runs: systems in name order, each with its own events; Bespin before Denon
    [Fact]
    public void SystemsResolveInNameOrder()
    {
        var w = new AssaultWorld();
        w.Hold(Emp); w.Hold(Emp, system: "Bespin");
        int a = w.Fleet(All, AssaultWorld.Neb, 1, Sys);
        int b = w.Fleet(All, AssaultWorld.Neb, 1, "Bespin");
        w.Aboard(a, Reb, 1); w.Aboard(b, Reb, 1);
        w.Submit(All, a, Sys); w.Submit(All, b, "Bespin");
        w.Run(1);
        Assert.Equal(new[] { "Bespin", Sys }, w.Events<AssaultStarted>().Select(s => s.System).ToArray());
        Assert.All(w.Events<AssaultStarted>(), s => Assert.Equal(w.Game.Day, s.Day)); // day = K + 1
    }

    // m3-assault: two fleets of one side with pending assaults on one system are one assault (regiments pooled, id order)
    [Fact]
    public void FleetsOfOneSidePoolTheirRegiments()
    {
        var w = new AssaultWorld();
        w.Hold(Emp);
        int a = w.Fleet(All, AssaultWorld.Neb), b = w.Fleet(All, AssaultWorld.Neb);
        var ra = w.Aboard(a, Reb, 2); var rb = w.Aboard(b, Reb, 2);
        w.Submit(All, b); w.Submit(All, a);
        w.Run(1);
        var s = Assert.Single(w.Events<AssaultStarted>());
        Assert.Equal(new[] { a, b }.OrderBy(x => x), s.Fleets.Items);
        Assert.Equal((4, 0), (s.A0, s.D0));
        Assert.Equal(ra.Concat(rb).OrderBy(x => x), Assert.Single(w.Events<AssaultLanded>()).Regiments.Items);
    }

    // ------------------------------------------------------------------ switches

    // m3-assault §Defaults: AssaultPasses = 0 repeats passes until one side is empty
    [Fact]
    public void PassesZeroRepeatsUntilOneSideIsEmpty()
    {
        var w = new AssaultWorld(stub: false, seed: 5, p: new AssaultParams { Passes = 0 });
        w.Hold(Emp);
        int f = w.Fleet(All, AssaultWorld.Neb);
        w.Aboard(f, "Wookiee Regiment", 6);
        w.Defenders(Emp, Imp, 2);
        w.Try(All, f);
        var res = w.LastResolved();
        Assert.True(res.A == 0 || res.D == 0);
    }

    // CLAUDE.md hard rule 7: the settings' default is the module's own default
    [Fact]
    public void ParamsDefaultsAreTheSheetsDefaults()
    {
        var p = new AssaultParams();
        Assert.Equal((10, 30, 6, 4, 8, 6, 1, CollateralMode.Truncate, true),
            (p.DieMax, p.LeadershipStep, p.DefenderDiesAtLeast, p.AttackerDiesAtMost, p.CollateralPercent, p.LandingCap, p.Passes, p.CollateralMode, p.EnemyFleetBlocks));
    }
}

internal static class AssaultTestExtensions
{
    public static R Let<T, R>(this T x, Func<T, R> f) => f(x);
}
