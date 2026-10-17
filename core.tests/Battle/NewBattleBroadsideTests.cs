using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Ships;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.Battle;

// Tests of space-combat/11 section 4 (closing range, which supersedes m4-battle-new section 5) and of m4-battle-new section 6 (facing), on ships whose
// strongest guns are on the broadside (Dauntless Cruiser). Finding of the "4 Dauntless v 2 Imperial II" bug hunt: facing, arc mapping and data copy were
// correct; the Dauntless lost because the old StandOff rule parked the Imperial II out of their reach. The new rule closes to min(reach_a, reach_b).
public class NewBattleBroadsideTests
{
    private readonly ITestOutputHelper _out;
    public NewBattleBroadsideTests(ITestOutputHelper output) { _out = output; }

    private const string Dauntless = "Dauntless Cruiser", ImperialII = "Imperial II Star Destroyer", Isd = "Imperial Star Destroyer", Strike = "Strike Cruiser", BulkCruiser = "Bulk Cruiser", Carrack = "Carrack Light Cruiser",
        Bulwark = "Bulwark Battlecruiser", Cc9600 = "CC-9600 Frigate", VictoryDestroyer = "Victory Destroyer", Corvette = "Corellian Corvette",
        EscortCarrier = "Imperial Escort Carrier", Nebulon = "Nebulon-B Frigate";

    private static (NewBattle B, ClassicBattleResult R) Run(NewBattleParams np, ulong seed, (string, int)[]? a = null, (string, int)[]? e = null,
        bool canRetreat = true, Action<NewBattle>? hook = null, bool log = true)
    {
        var p = new BattleParams { Ruleset = BattleRuleset.New, New = np };
        var (nb, _) = QuickBattle.PrepareNew(p, NewBattleTests.Ships, a ?? new[] { (Dauntless, 4) }, e ?? new[] { (ImperialII, 2) }, seed, canRetreat, canRetreat, logEvents: log);
        hook?.Invoke(nb);
        return (nb, nb.Run());
    }

    private static int ShotsBy(NewBattle nb, Side side)
    {
        var sideOf = nb.Units.ToDictionary(u => u.ObjectId, u => u.Side);
        return nb.Log!.Count(l => (l.Kind == "hit" || l.Kind == "miss" || l.Kind == "damage") && sideOf[l.Unit] == side);
    }

    private static int FirstShotTick(NewBattle nb, Side side)
    {
        var sideOf = nb.Units.ToDictionary(u => u.ObjectId, u => u.Side);
        var shots = nb.Log!.Where(l => (l.Kind == "hit" || l.Kind == "miss") && sideOf[l.Unit] == side).ToList();
        return shots.Count == 0 ? int.MaxValue : shots.Min(l => l.T);
    }

    private static int Sublight(string cls) => NewBattleData.Default.Class(cls).Sublight;

    // ---------------------------------------------------------------- the pinned bug-report case, revised to the new rule

    // space-combat/11 §4 (3): "4 Dauntless v 2 Imperial II" (the bug report). Under the old m4-battle-new §5 rule (desiredD = R - 16) the Imperial II parked at D = 54
    // and the Dauntless never fired (0 of 200 seeds won). Under the new rule D = min(reach) so both fire. What the rule gives is asserted and printed.
    [Fact]
    public void ClosingRange_FourDauntlessV2ImperialII_NowGetInRangeAndFire()
    {
        // one battle, seed 1: both sides fire, the Dauntless turn broadside, D settles at min(reach) = 26 (both ion ranges: 40 - 12 - 2)
        var (nb, r) = Run(new NewBattleParams(), 1);
        Assert.Equal(80, nb.StartDistance);      // longest range present (70) + 10: unchanged
        Assert.Equal(26, nb.TargetDistance);     // min(26, 26)
        Assert.True(ShotsBy(nb, Side.Alliance) > 0);
        Assert.True(ShotsBy(nb, Side.Empire) > 0);
        var sideOf = nb.Units.ToDictionary(u => u.ObjectId, u => u.Side);
        Assert.Contains(nb.Log!, l => l.Kind == "turn" && l.Detail == "broadside" && sideOf[l.Unit] == Side.Alliance);

        int aw = 0, ew = 0, none = 0, dauntlessSurvivors = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var (b, res) = Run(new NewBattleParams(), seed, log: false);
            if (res.AllianceOutcome == BattleOutcome.Victorious) aw++; else if (res.EmpireOutcome == BattleOutcome.Victorious) ew++; else none++;
            dauntlessSurvivors += b.Units.Count(u => u.Side == Side.Alliance && u.IsCapital && !u.Destroyed && !u.Left);
        }
        _out.WriteLine($"4 Dauntless v 2 Imperial II, 200 seeds: Alliance wins {aw / 2.0:0.0}%, Empire wins {ew / 2.0:0.0}%, none {none / 2.0:0.0}%, mean Dauntless survivors {dauntlessSurvivors / 200.0:0.00}");
        Assert.True(aw >= 120, $"4 Dauntless should mostly win against 2 Imperial II, won {aw}/200");
    }

    // ---------------------------------------------------------------- space-combat/11 section 4, one test per numbered point

    // space-combat/11 §4 (1): weapons fire at their exact DAT range. Bands are labels. With a slow approach (k = 0.5) the Imperial II (range 70) opens fire while the
    // Dauntless (65) are still out of range; no shot is ever fired beyond the shooter's longest DAT range, and the Imperial II fire at pair range 66 to 70 (long band, not a band edge).
    [Fact]
    public void Section4_1_WeaponsFireAtExactDatRange()
    {
        var np = new NewBattleParams { ClosingK = 0.5 };
        var sideOf = new Dictionary<int, Side>();
        int shotsAtDatRange = 0;
        var (nb, _) = Run(np, 1, hook: nb0 =>
        {
            int seen = 0;
            nb0.AfterTick = t =>
            {
                var log = nb0.Log!;
                if (sideOf.Count == 0) foreach (var u in nb0.Units) sideOf[u.ObjectId] = u.Side;
                double pair = nb0.Distance + 12; // large + large offsets (6 + 6)
                int alliance = 0, empire = 0;
                for (; seen < log.Count; seen++)
                {
                    var l = log[seen];
                    if (l.Kind != "hit" && l.Kind != "miss") continue;
                    var cls = nb0.Units.First(u => u.ObjectId == l.Unit).Class.Name;
                    int longest = NewBattleData.Default.Class(cls).LongestRange;
                    Assert.True(pair <= longest, $"tick {t}: {cls} fired at pair range {pair} beyond its range {longest}");
                    if (sideOf[l.Unit] == Side.Alliance) alliance++; else empire++;
                }
                if (empire > 0 && alliance == 0 && pair > 65 && pair <= 70) shotsAtDatRange++;
            };
        });
        Assert.True(shotsAtDatRange > 0, "the Imperial II never fired alone at pair range 66..70");
    }

    // space-combat/11 §4 (2): reach uses the batteries that hold 90% of the in-arc dps. An ISD against a shielded Dauntless: ions (35, x1.5 on shields) carry 60% of the dps and
    // the turbolasers (50) 40%, so 90% needs the ions: reach = 35 - 12 - 2 = 21, not 50 - 14 = 36. Lowering the share to 30% lets the turbolasers set it (36).
    [Fact]
    public void Section4_2_ReachUsesTheBatteriesHolding90PercentOfTheDps()
    {
        double Reach(double share)
        {
            double reach = -1;
            Run(new NewBattleParams { ReachDpsShare = share }, 1, new[] { (Dauntless, 1) }, new[] { (Isd, 1) }, log: false,
                hook: nb0 => nb0.AfterTick = t => { if (t == 3) reach = nb0.LastReach(Side.Empire); });
            return reach;
        }
        Assert.Equal(21, Reach(0.9));
        Assert.Equal(36, Reach(0.3));
        // and the Dauntless (ion 40 and turbolaser 65): ions carry the dps against a shield too, reach 40 - 14 = 26
        double dauntless = -1;
        Run(new NewBattleParams(), 1, new[] { (Dauntless, 1) }, new[] { (Isd, 1) }, log: false, hook: nb0 => nb0.AfterTick = t => { if (t == 3) dauntless = nb0.LastReach(Side.Alliance); });
        Assert.Equal(26, dauntless);
    }

    // space-combat/11 §4 (3): D = min(reach_a, reach_b), so both sides can fire. Dauntless (reach 26) against an ISD (reach 21): D heads for 21 and the Dauntless still fire.
    [Fact]
    public void Section4_3_FleetDistanceIsTheSmallerReachAndBothSidesFire()
    {
        double ra = -1, re = -1, target = -1;
        var (nb, _) = Run(new NewBattleParams(), 1, new[] { (Dauntless, 1) }, new[] { (Isd, 1) }, canRetreat: false, hook: nb0 => nb0.AfterTick = t =>
        {
            if (t != 3) return;
            ra = nb0.LastReach(Side.Alliance); re = nb0.LastReach(Side.Empire); target = nb0.TargetDistance;
        });
        Assert.Equal(26, ra);
        Assert.Equal(21, re);
        Assert.Equal(Math.Min(ra, re), target);
        Assert.True(ShotsBy(nb, Side.Alliance) > 0 && ShotsBy(nb, Side.Empire) > 0);
        // the lock-out of the old rule: the longer-ranged side (ISD turbolaser 50) used to park out of reach of the shorter-ranged one
        Assert.Equal(21, nb.Distance);
    }

    // space-combat/11 §4 (4) first half: kiting needs a speed edge AND the larger reach. Carrack Light Cruiser (sublight 6, reach 50) v Bulk Cruiser (sublight 3, reach 35): kite.
    // Equal speed (Bulk Cruiser 3 v Strike 3) with the larger reach on the Strike: no kite. (Pairs re-picked 2026-10-17 after the lore sublight values; the rule is unchanged.) A speed edge with the smaller reach (CC-9600, 6, v Victory Destroyer, 3): no kite.
    [Fact]
    public void Section4_4_KiteOnlyWithSpeedEdgeAndLargerReach()
    {
        double kre = -1, kra = -1;
        var kite = Run(new NewBattleParams(), 1, new[] { (BulkCruiser, 1) }, new[] { (Carrack, 1) }, canRetreat: false,
            hook: nb0 => nb0.AfterTick = t => { if (t == 1) { kre = nb0.LastReach(Side.Empire); kra = nb0.LastReach(Side.Alliance); } }).B;
        Assert.True(Sublight(Carrack) >= Sublight(BulkCruiser) + 1);
        Assert.True(kite.KiteSeconds > 0, "Carrack v Bulk Cruiser should kite");
        Assert.True(kre > kra, $"reach at t = 1: {kre} v {kra}"); // read at the start: the kiter may be gone by the end

        var equalSpeed = Run(new NewBattleParams(), 1, new[] { (BulkCruiser, 1) }, new[] { (Strike, 1) }, canRetreat: false).B;
        Assert.Equal(Sublight(BulkCruiser), Sublight(Strike));
        Assert.True(equalSpeed.LastReach(Side.Empire) > equalSpeed.LastReach(Side.Alliance));
        Assert.Equal(0, equalSpeed.KiteSeconds);

        var fastShort = Run(new NewBattleParams(), 1, new[] { (Cc9600, 1) }, new[] { (VictoryDestroyer, 1) }, canRetreat: false).B;
        Assert.True(Sublight(Cc9600) >= Sublight(VictoryDestroyer) + 1);
        Assert.True(fastShort.LastReach(Side.Alliance) < fastShort.LastReach(Side.Empire));
        Assert.Equal(0, fastShort.KiteSeconds);

        // the speed edge is a tunable: with an edge of 4 points Carrack (6) v Bulk Cruiser (3) no longer kites
        var noEdge = Run(new NewBattleParams { KiteSpeedEdge = 4 }, 1, new[] { (BulkCruiser, 1) }, new[] { (Carrack, 1) }, canRetreat: false).B;
        Assert.Equal(0, noEdge.KiteSeconds);
    }

    // space-combat/11 §4 (4) second half: the slower chaser closes at HALF speed, so the kite ends. Carrack v Bulk Cruiser: the Bulk Cruiser (sublight 3, 4.5 units/s) closes at 2.25 units/s
    // inside the Carrack's reach, until D = its own reach; the kiter fires alone meanwhile.
    [Fact]
    public void Section4_4_TheChaserClosesAtHalfSpeedAndTheKiteEnds()
    {
        var deltas = new List<(int T, double D, double Dist)>();
        double prev = 0; double reachFast = 0, reachSlow = 0;
        var (nb, _) = Run(new NewBattleParams(), 1, new[] { (BulkCruiser, 1) }, new[] { (Carrack, 1) }, canRetreat: false, hook: nb0 =>
        {
            prev = -1;
            nb0.AfterTick = t =>
            {
                if (t == 1) { reachFast = nb0.LastReach(Side.Empire); reachSlow = nb0.LastReach(Side.Alliance); }
                if (prev >= 0) deltas.Add((t, prev - nb0.Distance, nb0.Distance));
                prev = nb0.Distance;
            };
        });
        double full = 1.5 * Sublight(BulkCruiser), half = full / 2;
        Assert.True(reachFast > reachSlow + 2 * half, $"the kite window should last several seconds ({reachFast} v {reachSlow})");
        var inKite = deltas.Where(x => x.Dist + x.D <= reachFast + 1e-9 && x.Dist > reachSlow + 1e-9 && x.D > 0).ToList();
        Assert.NotEmpty(inKite);
        Assert.All(inKite, x => Assert.Equal(half, x.D, 6));            // half speed inside the kiter's reach
        Assert.Contains(deltas, x => Math.Abs(x.D - full) < 1e-9);       // full speed during the approach
        Assert.Equal(reachSlow, nb.TargetDistance);                      // the kite ends: D reaches min(reach)
        Assert.True(deltas.Any(x => Math.Abs(x.Dist - reachSlow) < 1e-9), "the chaser never closed to its own reach");
    }

    // space-combat/11 §4 (5): closing speed = slowest sublight of the closing fleet x 1.5 per point. The side with the smaller reach closes. A fleet of Dauntless (4)
    // and Nebulon-B (4) against one Imperial II (5): whichever side closes moves D by 1.5 x its slowest sublight each second.
    [Fact]
    public void Section4_5_ClosingSpeedIsTheSlowestSublightOfTheClosingFleetTimes1Point5()
    {
        var steps = new List<double>(); var closer = new List<Side>();
        double prev = -1;
        var (nb, _) = Run(new NewBattleParams(), 1, new[] { (Dauntless, 1), (Nebulon, 1) }, new[] { (ImperialII, 1) }, canRetreat: false, hook: nb0 =>
            nb0.AfterTick = t =>
            {
                if (prev >= 0 && prev - nb0.Distance > 0)
                {
                    steps.Add(prev - nb0.Distance);
                    closer.Add(nb0.LastReach(Side.Alliance) < nb0.LastReach(Side.Empire) ? Side.Alliance : Side.Empire);
                }
                prev = nb0.Distance;
            });
        Assert.NotEmpty(steps);
        double slowestAlliance = Math.Min(Sublight(Dauntless), Sublight(Nebulon)) * 1.5, empire = Sublight(ImperialII) * 1.5;
        Assert.Equal(4 * 1.5, slowestAlliance);
        Assert.Equal(5 * 1.5, empire);
        // every full-size step is exactly the closing side's slowest sublight x 1.5 (the last step is cut at the target)
        for (int i = 0; i < steps.Count - 1; i++) Assert.Equal(closer[i] == Side.Alliance ? slowestAlliance : empire, steps[i], 9);
        // the tunable: k = 3.0 doubles the first step (the side with the smaller reach closes)
        double first = -1; Side firstCloser = Side.Alliance;
        Run(new NewBattleParams { ClosingK = 3.0 }, 1, new[] { (Dauntless, 1), (Nebulon, 1) }, new[] { (ImperialII, 1) }, canRetreat: false, hook: nb0 =>
            nb0.AfterTick = t =>
            {
                if (t != 0) return;
                first = nb0.StartDistance - nb0.Distance;
                firstCloser = nb0.LastReach(Side.Alliance) < nb0.LastReach(Side.Empire) ? Side.Alliance : Side.Empire;
            });
        Assert.Equal(firstCloser == Side.Alliance ? 4 * 3.0 : 5 * 3.0, first, 9);
    }

    // space-combat/11 §4 (6): if nobody has a target in range for 20 s, both close at full speed to D = 15. Corellian Corvette (lasers only) v Imperial Escort Carrier
    // (no weapon that can hurt a shielded ship): nobody can ever fire, so D stays at the start value for 20 s and then goes to 15.
    [Fact]
    public void Section4_6_NoFireSafetyClosesToFifteenAfterTwentySeconds()
    {
        var dist = new List<(double D, bool Safety)>();
        var (nb, _) = Run(new NewBattleParams(), 1, new[] { (Corvette, 1) }, new[] { (EscortCarrier, 1) }, canRetreat: false, log: false, hook: nb0 =>
            nb0.AfterTick = t => { if (t <= 40) dist.Add((nb0.Distance, nb0.SafetyClosing)); });
        double start = nb.StartDistance;
        Assert.True(start > 15);
        for (int t = 0; t < 20; t++) { Assert.Equal(start, dist[t].D); Assert.False(dist[t].Safety); }
        Assert.True(dist[20].Safety);
        // both close at full speed: (6 + 3) x 1.5 = 13.5 per second, so 27 -> 15 in one step
        Assert.Equal(15, dist[20].D);
        for (int t = 21; t <= 40; t++) Assert.Equal(15, dist[t].D);
    }

    // space-combat/11 §4 (7): Surround closes to 15 (the safety distance), not 0.
    [Fact]
    public void Section4_7_SurroundClosesToFifteen()
    {
        var p = new BattleParams { Ruleset = BattleRuleset.New };
        var (su, _) = QuickBattle.PrepareNew(p, NewBattleTests.Ships, new[] { (Isd, 1) }, new[] { (Isd, 1) }, 4);
        su.SetTactic(Side.Alliance, BattleTactic.Surround); su.SetTactic(Side.Empire, BattleTactic.Surround);
        double min = 1e9; su.AfterTick = _ => min = Math.Min(min, su.Distance); su.Run();
        Assert.Equal(15, min);
    }

    // ---------------------------------------------------------------- the lock-out scan

    // space-combat/11 §4: all 1 v 1 capital-ship pairings (seed 1, no retreat refusal tricks): a side that fires 0 shots for more than 10 s while the other has fired at
    // least 20. A silent side that has nothing that can hurt the other (laser-only against a shield: reach -1) is deflection, not range, and is counted apart.
    [Fact]
    public void LockOutScan_AllOneVsOneCapitalPairings_NoRangeLockOutRemains()
    {
        var ships = NewBattleTests.Ships;
        var p = new BattleParams { Ruleset = BattleRuleset.New };
        List<string> Names(ShipSide s) => ships.Values.Where(x => x.IsCapital && x.Side == s).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        int total = 0; var range = new List<string>(); var deflection = new List<string>();
        foreach (var a in Names(ShipSide.Alliance))
            foreach (var e in Names(ShipSide.Empire))
            {
                var (nb, _) = QuickBattle.PrepareNew(p, ships, new[] { (a, 1) }, new[] { (e, 1) }, 1, logEvents: true);
                var cannotHurt = new bool[2];
                nb.AfterTick = t => { if (t == 0) { cannotHurt[(int)Side.Alliance] = nb.Reach(Side.Alliance) < 0; cannotHurt[(int)Side.Empire] = nb.Reach(Side.Empire) < 0; } };
                nb.Run();
                total++;
                var sideOf = nb.Units.ToDictionary(u => u.ObjectId, u => u.Side);
                int len = nb.Log!.Count == 0 ? 1 : nb.Log.Max(l => l.T) + 1;
                var shots = new[] { new int[len], new int[len] };
                foreach (var l in nb.Log) if (l.Kind == "hit" || l.Kind == "miss") shots[(int)sideOf[l.Unit]][l.T]++;
                for (int s = 0; s < 2; s++)
                {
                    int own = 0, other = 0;
                    for (int t = 0; t < len; t++)
                    {
                        own += shots[s][t]; other += shots[1 - s][t];
                        if (own > 0 || other < 20) continue;
                        bool silent = true;
                        for (int u = t; u <= t + 10; u++) if (u < len && shots[s][u] > 0) silent = false;
                        if (silent && t + 10 < len) (cannotHurt[s] ? deflection : range).Add($"{a} v {e}: {(Side)s} silent from {t}");
                        break;
                    }
                }
            }
        _out.WriteLine($"1v1 capital pairings scanned: {total}; range lock-outs {range.Count}; deflection-only silences (laser-only v shield) {deflection.Count}");
        foreach (var x in range) _out.WriteLine("RANGE " + x);
        foreach (var x in deflection) _out.WriteLine("DEFLECTION " + x);
        Assert.Empty(range);
    }
}
