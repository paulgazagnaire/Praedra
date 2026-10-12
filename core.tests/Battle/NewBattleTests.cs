using System.Diagnostics;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Ships;
using Rebellion.Core.Tests.Fleets;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.Battle;

// Tests of m4-battle-new (N ids as in the sheet's table) and of its Lead update 2026-10-12, on plain units (no game around), with the real CSVs.
// The sheet's "model" counts were made with a Mersenne generator, so counts here are our own seeded results, asserted as rates or invariants.
public class NewBattleTests
{
    private readonly ITestOutputHelper _out;
    public NewBattleTests(ITestOutputHelper output) { _out = output; }

    internal static IReadOnlyDictionary<string, ShipClass> Ships => World.Ships.Value;
    internal static readonly BattleParams New = new() { Ruleset = BattleRuleset.New };
    internal static readonly BattleParams NewNoRolls = new() { Ruleset = BattleRuleset.New, SubsystemRolls = false };
    private const string Corvette = "Corellian Corvette", Isd = "Imperial Star Destroyer", MonCal = "Mon Calamari Cruiser", Lancer = "Lancer Frigate",
        Interdictor = "Interdictor Cruiser", Bomber = "TIE Bomber";

    internal static (ClassicBattleResult R, IReadOnlyList<BattleUnit> U) Fight(BattleParams p, (string, int)[] alliance, (string, int)[] empire, ulong seed = 1,
        bool aCan = true, bool eCan = true, bool carry = false, Action<IReadOnlyList<BattleUnit>>? prepare = null, OnContactMode[]? alert = null) =>
        QuickBattle.Run(p, Ships, alliance, empire, seed, aCan, eCan, alert, null, carry, prepare);

    private static BattleUnit First(IReadOnlyList<BattleUnit> u, Side side, string cls) => u.First(x => x.Side == side && x.Class.Name == cls);

    // ---------------------------------------------------------------- N1 determinism

    // m4-battle-new N1
    [Fact]
    public void N1_SameFleetsSameSeedTwiceGiveTheSameResultAndDrawCount()
    {
        var (r1, u1) = Fight(New, new[] { (MonCal, 3) }, new[] { (Isd, 2) }, seed: 7);
        var (r2, u2) = Fight(New, new[] { (MonCal, 3) }, new[] { (Isd, 2) }, seed: 7);
        Assert.True(r1.Draws > 0);
        Assert.Equal(r1.Draws, r2.Draws);
        Assert.Equal((r1.Seconds, r1.AllianceOutcome, r1.EmpireOutcome, r1.AllianceLosses, r1.EmpireLosses), (r2.Seconds, r2.AllianceOutcome, r2.EmpireOutcome, r2.AllianceLosses, r2.EmpireLosses));
        Assert.Equal(u1.Select(x => (x.Hull, x.Alive, x.Left, string.Join(",", x.Hits))), u2.Select(x => (x.Hull, x.Alive, x.Left, string.Join(",", x.Hits))));
        var (r3, _) = Fight(New, new[] { (MonCal, 3) }, new[] { (Isd, 2) }, seed: 8);
        Assert.NotEqual(r1.Draws, r3.Draws); // another seed, another battle
    }

    // ---------------------------------------------------------------- the headline: laser boats against an ISD

    // m4-battle-new N2 (and the plan's M4 proof for New): 30 Corvettes v 1 ISD, corvettes can retreat: they leave in five seconds, the ISD is untouched
    [Fact]
    public void N2_ThirtyCorvettesVsOneIsd_CorvettesRetreatAndTheIsdIsUntouched()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (r, u) = Fight(New, new[] { (Corvette, 30) }, new[] { (Isd, 1) }, seed);
            Assert.True(r.AllianceRetreated, $"seed {seed}");
            Assert.Equal(BattleOutcome.Retreated, r.AllianceOutcome);
            Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
            Assert.InRange(r.Seconds, 5, 6); // flagged in the first second (strength 0 against 26.5 or more), five seconds in the jump
            Assert.Equal(2750.0, First(u, Side.Empire, Isd).Hull);
        }
    }

    // m4-battle-new N2 and the plan's M4 proof: over a seeded batch the ISD wins at least 95% (here every battle)
    [Theory]
    [InlineData(30)]
    [InlineData(8)]
    public void HeadlineCorvettesVsIsd_TheIsdWinsAlmostAlways(int corvettes)
    {
        int isdWins = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var (r, _) = Fight(New, new[] { (Corvette, corvettes) }, new[] { (Isd, 1) }, seed);
            if (r.EmpireOutcome == BattleOutcome.Victorious) isdWins++;
        }
        _out.WriteLine($"New: {corvettes} CR90 v 1 ISD, ISD wins {isdWins}/200");
        Assert.True(isdWins >= 190, $"ISD won {isdWins}/200");
    }

    // m4-battle-new N3: retreat not allowed: the corvettes never scratch the ISD (lasers never penetrate a capital's shield); the ISD kills them or the stalemate does
    [Theory]
    [InlineData(30)]
    [InlineData(8)]
    public void N3_CorvettesThatCannotRetreatNeverScratchTheIsd(int corvettes)
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (r, u) = Fight(New, new[] { (Corvette, corvettes) }, new[] { (Isd, 1) }, seed, aCan: false);
            Assert.False(r.AllianceRetreated);
            Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
            Assert.Equal(BattleOutcome.Annihilated, r.AllianceOutcome);
            Assert.Equal(corvettes, r.AllianceLosses);
            Assert.Equal(2750.0, First(u, Side.Empire, Isd).Hull);
            Assert.Contains(r.Steps.OfType<RetreatRefusedStep>(), s => s.Reason == RetreatRefusal.NoDestination);
        }
    }

    // m4-battle-new N4: 20 Lancers v 1 ISD, no retreat: the ISD hull stays whole, the ISD wins
    [Fact]
    public void N4_TwentyLancersVsOneIsdNoRetreat()
    {
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var (r, u) = Fight(New, new[] { (Lancer, 20) }, new[] { (Isd, 1) }, seed, aCan: false);
            Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
            Assert.Equal(2750.0, First(u, Side.Empire, Isd).Hull);
        }
    }

    // ---------------------------------------------------------------- N5 gravity wells

    // m4-battle-new N5 (changed setup, see the report): the corvettes cannot hurt a hull-1 Interdictor (its shield is up), so a weakened Mon Calamari
    // Cruiser (hull 30%, flagged all the same) shoots it. While the Interdictor lives the Alliance cannot retreat; once it is dead the retreat works.
    [Fact]
    public void N5_AGravityWellShipBlocksRetreatWhileItLivesAndKillingItFreesTheRetreat()
    {
        void Weaken(IReadOnlyList<BattleUnit> u, int interdictorHull)
        {
            var mc = First(u, Side.Alliance, MonCal); mc.Hull = 0.3 * mc.HullMax;
            First(u, Side.Empire, Interdictor).Hull = interdictorHull;
        }
        var fleetA = new[] { (Corvette, 30), (MonCal, 1) };
        var fleetE = new[] { (Isd, 1), (Interdictor, 1) };
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var (r, u) = Fight(New, fleetA, fleetE, seed, prepare: x => Weaken(x, 1200));
            Assert.False(r.AllianceRetreated, $"seed {seed}");
            Assert.Contains(r.Steps.OfType<RetreatRefusedStep>(), s => s.Reason == RetreatRefusal.GravityWell);
            Assert.False(First(u, Side.Empire, Interdictor).Destroyed);

            var (r2, u2) = Fight(New, fleetA, fleetE, seed, prepare: x => Weaken(x, 1));
            Assert.True(First(u2, Side.Empire, Interdictor).Destroyed, $"seed {seed}");
            Assert.True(r2.AllianceRetreated, $"seed {seed}");
            Assert.Equal(BattleOutcome.Retreated, r2.AllianceOutcome);
        }
    }

    // ---------------------------------------------------------------- N6, N17 mirror and Mon Calamari

    // m4-battle-new N6 (no side-order bias): 2 ISD v 2 ISD, each side wins 45 to 55% (here 1000 seeds, so the band is tight enough to mean something)
    [Fact]
    public void N6_MirrorTwoIsdVsTwoIsdIsEvenAndSwappingSidesDoesNotChangeTheRates()
    {
        int a = 0, e = 0;
        for (ulong seed = 1; seed <= 1000; seed++)
        {
            var (r, _) = Fight(New, new[] { (Isd, 2) }, new[] { (Isd, 2) }, seed);
            if (r.AllianceOutcome == BattleOutcome.Victorious) a++; else if (r.EmpireOutcome == BattleOutcome.Victorious) e++;
        }
        _out.WriteLine($"2 ISD v 2 ISD: Alliance {a}, Empire {e} of 1000");
        Assert.InRange(a, 450, 550);
        Assert.InRange(e, 450, 550);
    }

    // m4-battle-new N6 (second half) and N17: ISD v Mon Calamari, 100 seeds each side; rates recorded (tuneable), the two assignments of the sides agree
    [Fact]
    public void N17_IsdVsMonCalamari_RatesAreTheSameWhicheverSideIsWhich()
    {
        int monCalAsAlliance = 0, monCalAsEmpire = 0;
        // Mon Calamari is an Alliance class and the ISD an Empire class, but the engine does not care which side a class is on: swap by listing it on the other side
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var (r, _) = Fight(New, new[] { (MonCal, 1) }, new[] { (Isd, 1) }, seed);
            if (r.AllianceOutcome == BattleOutcome.Victorious) monCalAsAlliance++;
            var (r2, _) = Fight(New, new[] { (Isd, 1) }, new[] { (MonCal, 1) }, seed);
            if (r2.EmpireOutcome == BattleOutcome.Victorious) monCalAsEmpire++;
        }
        _out.WriteLine($"Mon Calamari v ISD, 100 seeds: wins as Alliance {monCalAsAlliance}, as Empire {monCalAsEmpire} (sheet model about 2 in 3)");
        // N17: recorded, tuneable, not a hard bound. space-combat/11 §4: both close to D = 21 and fight at the ISD's ion range, so the ISD now wins most (Mon Calamari 18% of 100 seeds
        // against 73% under the old m4-battle-new §5 rule). Balance flag for the Space combat owner.
        Assert.InRange(monCalAsAlliance, 5, 90);
        Assert.InRange(monCalAsEmpire, 5, 90);
        Assert.InRange(Math.Abs(monCalAsAlliance - monCalAsEmpire), 0, 20); // same rates within sampling noise
    }

    // ---------------------------------------------------------------- N7 shield regeneration pause

    // m4-battle-new N7: a hit at t, then no regen before t + 5: the shield value is unchanged t+1 .. t+4 and grows at t+5 by regen x hullRatio
    [Fact]
    public void N7_ShieldDoesNotRegenerateForFiveSecondsAfterAHit()
    {
        // space-combat/11 §4 changed the engagement: both ships now fire from the first seconds at D = 21, so the Mon Calamari is hit again within 5 s on most seeds;
        // look for the first seed with a hit followed by a quiet spell (a regen at +5), instead of fixing seed 3.
        bool found = false;
        for (ulong seed = 1; seed <= 40 && !found; seed++)
        {
            var (nb, units) = QuickBattle.PrepareNew(NewNoRolls, Ships, new[] { (MonCal, 1) }, new[] { (Isd, 1) }, seed);
            var tracks = new List<(List<double>, List<double>, double)>();
            foreach (var (cls, hullMax) in new[] { (MonCal, 2400.0), (Isd, 2750.0) })
            {
                var mc = units.First(x => x.Class.Name == cls);
                var shield = new List<double>(); var hull = new List<double>();
                var prevHook = nb.AfterTick;
                nb.AfterTick = t => { prevHook?.Invoke(t); shield.Add(nb.ShieldOf(mc)); hull.Add(nb.HullOf(mc)); };
                tracks.Add((shield, hull, hullMax));
            }
            nb.Run();
            foreach (var (shield, hull, hullMax) in tracks)
                for (int hit = 1; hit + 5 < shield.Count && !found; hit++)
                {
                    if (!(shield[hit] < shield[hit - 1])) continue; // a hit
                    bool quiet = true;
                    for (int k = 1; k <= 4; k++) if (shield[hit + k] != shield[hit]) quiet = false;
                    if (!quiet || shield[hit + 5] <= shield[hit]) continue;
                    double hr = hull[hit + 5] / hullMax;
                    Assert.Equal(shield[hit] + 15.0 * hr, shield[hit + 5], 1e-9); // regen 15 x hullRatio, no further hit in between
                    found = true;
                }
        }
        Assert.True(found, "no hit followed by a 5 s pause in 40 seeds");
    }

    // ---------------------------------------------------------------- N13, N14 fighters

    // m4-battle-new N13: an ISD carrying 6 TIE Bomber squadrons beats an ISD; without the hangar it is even
    [Fact]
    public void N13_IsdCarryingSixBomberSquadronsBeatsAnIsd()
    {
        int carrierWins = 0, evenWins = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (r, _) = Fight(New, new[] { (Isd, 1), (Bomber, 6) }, new[] { (Isd, 1) }, seed, carry: true);
            if (r.AllianceOutcome == BattleOutcome.Victorious) carrierWins++;
            var (r0, _) = Fight(New, new[] { (Isd, 1) }, new[] { (Isd, 1) }, seed);
            if (r0.AllianceOutcome == BattleOutcome.Victorious) evenWins++;
        }
        _out.WriteLine($"ISD+6 Bombers v ISD: carrier side wins {carrierWins}/40; ISD v ISD: Alliance wins {evenWins}/40");
        Assert.True(carrierWins >= 36);
        Assert.InRange(evenWins, 10, 30);
    }

    // m4-battle-new N14: a squadron whose carrier dies at t = 3 (before the launch at 5) is lost and never fires
    [Fact]
    public void N14_ASquadronWhoseCarrierDiesBeforeLaunchIsLostAndNeverFires()
    {
        var (nb, units) = QuickBattle.PrepareNew(New, Ships, new[] { (Isd, 1), (Bomber, 1) }, new[] { (Isd, 1) }, 1, carry: true,
            prepare: u => First(u, Side.Alliance, Isd).Hull = 1, logEvents: true);
        var r = nb.Run();
        var carrier = First(units, Side.Alliance, Isd); var sq = First(units, Side.Alliance, Bomber);
        Assert.True(carrier.Destroyed);
        Assert.Equal(3, carrier.LostAt); // the enemy ISD's first volley (D = 60 closing 6 per second against a range of 50)
        Assert.True(sq.Destroyed);
        Assert.Equal(UnitLossReason.NoBerth, sq.Loss);
        Assert.Equal(3, sq.LostAt);
        Assert.DoesNotContain(nb.Log!, e => e.Unit == sq.ObjectId && (e.Kind == "hit" || e.Kind == "miss" || e.Kind == "launch"));
        Assert.Equal(BattleOutcome.Annihilated, r.AllianceOutcome);
    }

    // m4-battle-new §10: at the end a squadron of fewer than 6 craft is lost, a whole one is kept
    [Fact]
    public void SquadronsUnderSixCraftAreLostAtTheEndAndWholeOnesStay()
    {
        var (r, u) = Fight(New, new[] { (Corvette, 4) }, new[] { (Isd, 1), ("TIE Fighter", 2) }, 1, carry: true,
            prepare: x => x.Where(q => q.Class.Name == "TIE Fighter").Last().Hull = 40); // ceil(40 / 8) = 5 craft
        var sq = u.Where(x => x.Class.Name == "TIE Fighter").OrderBy(x => x.ObjectId).ToList();
        Assert.False(sq[0].Destroyed);
        Assert.True(sq[1].Destroyed);
        Assert.Equal(UnitLossReason.Combat, sq[1].Loss);
    }

    // m4-battle-new §10, §11: a retreating carrier takes its squadrons; squadrons of a side that retreats with no berth are lost
    [Fact]
    public void RetreatingCarrierTakesItsSquadronsAndOthersWithoutBerthOrHyperdriveAreLost()
    {
        // Alliance: an Escort Carrier with 6 berths and 8 TIE-less... use Empire squadrons: ISD (6 berths) carries 6, two more are based
        var (r, u) = Fight(New, new[] { (Isd, 1), ("TIE Fighter", 8) }, new[] { (MonCal, 1), (Isd, 3) }, 1, carry: true,
            prepare: x => { }, alert: new[] { OnContactMode.Simulate, OnContactMode.Retreat }); // indexed by (int)Side: Empire, Alliance
        Assert.True(r.AllianceRetreated);
        var sq = u.Where(x => x.Side == Side.Alliance && x.Class.Name == "TIE Fighter").OrderBy(x => x.ObjectId).ToList();
        Assert.Equal(6, sq.Count(x => x.Left)); // the six riding the ISD
        Assert.Equal(2, sq.Count(x => x.Destroyed && x.Loss == UnitLossReason.NoBerth));
    }

    // m4-battle-new Lead update 4: torpedoes fire only at capitals whose shield is 0, unless TorpedoesVsShields is on (then at the first second the squadron may fire)
    [Fact]
    public void TorpedoesFireOnlyAtDroppedShieldsUnlessTheSwitchIsOn()
    {
        int FirstTorpedoTick(NewBattleParams np)
        {
            var (nb, units) = QuickBattle.PrepareNew(new BattleParams { Ruleset = BattleRuleset.New, New = np }, Ships,
                new[] { ("Alliance Escort Carrier", 1), ("Y-wing", 6) }, new[] { (Isd, 1) }, 5, empireCanLand: false, carry: true, logEvents: true); // the ISD cannot run, as it otherwise would (six Y-wing ion batteries outweigh it)
            nb.Run();
            var ids = units.Where(x => x.Class.Name == "Y-wing").Select(x => x.ObjectId).ToHashSet();
            var torp = nb.Log!.Where(e => ids.Contains(e.Unit) && e.Detail == "Torpedo").Select(e => e.T).ToList();
            return torp.Count == 0 ? -1 : torp.Min();
        }
        int off = FirstTorpedoTick(new NewBattleParams());
        int on = FirstTorpedoTick(new NewBattleParams { TorpedoesVsShields = true });
        Assert.Equal(11, on); // launch at 5 + 6 s of flight
        Assert.True(off > 11 || off == -1, $"first torpedo at {off}");
    }

    // ---------------------------------------------------------------- N15 retreat checks

    // m4-battle-new N15: both strengths 0 means no morale retreat (the ratio check needs enemy > 0); only the weaker side is flagged when the ratios differ
    [Fact]
    public void N15_BothZeroStrengthIsNoMoraleRetreatAndOnlyTheWeakerSideRetreats()
    {
        var (r, u) = Fight(New, new[] { (Corvette, 3) }, new[] { (Corvette, 3) }, 1);
        Assert.InRange(r.Seconds, 1200, 1206); // nobody retreated at t = 0; the stalemate at 1200 s ended it (both flagged, equal)
        var (r2, _) = Fight(New, new[] { (Corvette, 3) }, new[] { (Isd, 1) }, 1);
        Assert.True(r2.AllianceRetreated);
        Assert.False(r2.EmpireRetreated);
    }

    // m4-battle-new §11.4: the stalemate flags the side with the lower strength; a side that cannot retreat is destroyed
    [Fact]
    public void StalemateDestroysTheSideThatCannotRetreat()
    {
        var (r, _) = Fight(New, new[] { (Corvette, 3) }, new[] { (Corvette, 3) }, 1, aCan: false, eCan: false);
        Assert.InRange(r.Seconds, 1200, 1206);
        Assert.Equal(BattleOutcome.Indecisive, r.AllianceOutcome); // equal strength: both flagged, both destroyed
        Assert.Equal(BattleOutcome.Indecisive, r.EmpireOutcome);
        Assert.All(new[] { r.AllianceLosses, r.EmpireLosses }, n => Assert.Equal(3, n));
    }

    // ---------------------------------------------------------------- Lead update 2026-10-12

    // Lead update 2: D = the longest weapon range present in the battle + 10
    [Theory]
    [InlineData(Isd, Isd, 60)]       // turbolaser 50
    [InlineData(Corvette, Corvette, 27)] // laser 17
    [InlineData(Corvette, "Imperial II Star Destroyer", 80)] // turbolaser 70
    public void LeadUpdate2_StartDistanceIsTheLongestRangePlusTen(string a, string e, int d)
    {
        var (nb, _) = QuickBattle.PrepareNew(New, Ships, new[] { (a, 1) }, new[] { (e, 1) }, 1);
        nb.Run();
        Assert.Equal(d, nb.StartDistance);
    }

    // Lead update 3: a laser shot at a shielded capital is always deflected; the engine never lets lasers through
    [Fact]
    public void LeadUpdate3_LasersNeverPenetrateAShieldedCapital()
    {
        var m = new CombatModel(NewBattleData.Default);
        var data = NewBattleData.Default;
        foreach (var name in new[] { "Bulk Cruiser", "Corellian Corvette", "Medium Transport", "Corellian Gunship" })
        {
            var target = Combatant.Strategic(data.Class(name));
            Assert.Equal(0.0, m.ShotValue(WeaponKind.LaserCannon, 6.0, data.Class(Corvette), target));
        }
        // with the shield down a laser hurts the hull at 0.167
        var down = new Combatant { Class = data.Class(Isd), HullRatio = 1, ShieldFactor = 0.5, ShieldUp = false };
        Assert.Equal(6.0 * 0.167, m.ShotValue(WeaponKind.LaserCannon, 6.0, data.Class(Corvette), down), 1e-12);
        // Deflection is the class column (ISD 1.5), not shield / 200: ion 20 x 1.5 = 30 passes, a Y-wing ion 2.25 x 1.5 = 3.375 passes the ISD (1.5) but not the Bulwark (5.5)
        Assert.Equal(30.0, m.ShotValue(WeaponKind.IonCannon, 20, data.Class(Isd), Combatant.Strategic(data.Class(MonCal))), 1e-12);
        Assert.True(m.ShotValue(WeaponKind.IonCannon, 2.25, data.Class("Y-wing"), Combatant.Strategic(data.Class(Isd))) > 0);
        Assert.Equal(0.0, m.ShotValue(WeaponKind.IonCannon, 2.25, data.Class("Y-wing"), Combatant.Strategic(data.Class("Bulwark Battlecruiser"))));
        // torpedoes only at shield-0 capitals by default
        Assert.Equal(0.0, m.ShotValue(WeaponKind.Torpedo, 35, data.Class(Bomber), Combatant.Strategic(data.Class(Isd))));
        Assert.Equal(35.0, m.ShotValue(WeaponKind.Torpedo, 35, data.Class(Bomber), down), 1e-12);
        var withShields = new CombatModel(data, new NewBattleParams { TorpedoesVsShields = true });
        Assert.Equal(17.5, withShields.ShotValue(WeaponKind.Torpedo, 35, data.Class(Bomber), Combatant.Strategic(data.Class(Isd))), 1e-12);
    }

    // ---------------------------------------------------------------- the sheet's rules in the small

    // m4-battle-new §1: guns_active = floor(guns x (1 - 0.25 x hits[weapons])), four hits = none
    [Fact]
    public void GunsActiveFollowTheWeaponsCounter()
    {
        var m = new CombatModel(NewBattleData.Default);
        var b = NewBattleData.Default.Class(Isd).Batteries[0]; // 10 guns
        int G(int hits) => m.GunsActive(b, new Combatant { Class = NewBattleData.Default.Class(Isd), WeaponHits = hits });
        Assert.Equal(new[] { 10, 7, 5, 2, 0, 0 }, new[] { G(0), G(1), G(2), G(3), G(4), G(9) });
    }

    // m4-battle-new §2: maneuver ratio against a squadron (turbolaser 0.5 x ratio, laser 1.0 x ratio, ion 0)
    [Fact]
    public void FighterMultipliersUseTheManeuverRatio()
    {
        var d = NewBattleData.Default; var m = new CombatModel(d);
        var tie = Combatant.Strategic(d.Class("TIE Fighter")); // maneuver 5, shield pool 0
        Assert.Equal(40 * 0.5 * (1.0 / 5), m.ShotValue(WeaponKind.Turbolaser, 40, d.Class(Isd), tie), 1e-12); // ISD maneuver 1
        Assert.Equal(6.0 * 1.0 * (3.0 / 5), m.ShotValue(WeaponKind.LaserCannon, 6, d.Class(Corvette), tie), 1e-12); // Corvette maneuver 3
        Assert.Equal(0.0, m.ShotValue(WeaponKind.IonCannon, 20, d.Class(Isd), tie));
        Assert.Equal(3.75, m.ShotValue(WeaponKind.LaserCannon, 3.75, d.Class("A-wing"), tie), 1e-12); // ratio capped at 1
    }

    // space-combat/11 §4 (3), (7) (supersedes m4-battle-new §5): StandOff closes to min(reach), Surround closes to 15; both finish
    [Fact]
    public void TacticSurroundClosesTheDistance()
    {
        var (so, _) = QuickBattle.PrepareNew(New, Ships, new[] { (Isd, 1) }, new[] { (Isd, 1) }, 4);
        double minStand = 1e9; so.AfterTick = _ => minStand = Math.Min(minStand, so.Distance); so.Run();
        var (su, _) = QuickBattle.PrepareNew(New, Ships, new[] { (Isd, 1) }, new[] { (Isd, 1) }, 4);
        su.SetTactic(Side.Alliance, BattleTactic.Surround); su.SetTactic(Side.Empire, BattleTactic.Surround);
        double minSurround = 1e9; su.AfterTick = _ => minSurround = Math.Min(minSurround, su.Distance); su.Run();
        Assert.Equal(21, minStand);      // space-combat/11 §4 (3): min(reach) = 35 - 12 - 2 (was 50 - 16 = 34 under the old m4-battle-new §5)
        Assert.Equal(15, minSurround);   // space-combat/11 §4 (7): Surround closes to 15 (was 0)
    }

    // m4-battle-new §11.5, §13: the battle ends in a result with end hulls and counters; shields are not carried back by the module (BattleModuleTests)
    [Fact]
    public void ABattleEndsWithAWinnerOrIndecisiveAndEveryUnitAccountedFor()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var (r, u) = Fight(New, new[] { (MonCal, 2), (Corvette, 4) }, new[] { (Isd, 2), (Lancer, 3) }, seed);
            Assert.True(r.Seconds <= 1800 + 10);
            Assert.All(u, x => Assert.True(x.Destroyed || x.Left || x.Alive));
            Assert.Equal(u.Count(x => x.Side == Side.Alliance), r.AllianceLosses + r.AllianceSurvivors);
            Assert.All(u.Where(x => x.IsCapital && !x.Destroyed), x => Assert.True(x.Hull > 0 && x.Hull <= x.HullMax));
            Assert.All(u.Where(x => x.IsCapital), x => Assert.All(x.Hits, h => Assert.InRange(h, 0, 4)));
        }
    }

    // m4-battle-new N16: speed. The sheet wants 0.5 s on the Steam Deck for 40 v 40 mixed ships; not checkable here, so this records the time on the
    // build machine and only guards against an order-of-magnitude regression.
    [Fact]
    public void N16_FortyVsFortyMixedShipsFinishQuickly()
    {
        var a = new[] { (MonCal, 4), ("Nebulon-B Frigate", 8), (Corvette, 12), ("Dauntless Cruiser", 2), ("Liberator Cruiser", 4), ("Assault Frigate", 4), ("Corellian Gunship", 6) };
        var e = new[] { (Isd, 8), ("Strike Cruiser", 6), (Lancer, 8), ("Victory II Star Destroyer", 4), ("Carrack Light Cruiser", 6), ("Imperial II Star Destroyer", 2), (Interdictor, 6) };
        Fight(New, a, e, 99); // warm-up (JIT, data load)
        var sw = Stopwatch.StartNew();
        int battles = 3; long secs = 0;
        for (ulong seed = 1; seed <= (ulong)battles; seed++) secs += Fight(New, a, e, seed).R.Seconds;
        sw.Stop();
        _out.WriteLine($"N16: 40 v 40 mixed, {battles} battles, mean {sw.Elapsed.TotalMilliseconds / battles:0} ms, mean length {secs / battles} s");
        Assert.True(sw.Elapsed.TotalSeconds / battles < 5.0);
    }

    // ---------------------------------------------------------------- the comparison table (not an assertion of rates: a record)

    [Fact]
    public void ClassicVersusNewTable_IsPrintedAndTheNewEngineKeepsTheCorvettesOutOfTheIsd()
    {
        var cases = new (string Name, (string, int)[] A, (string, int)[] E)[]
        {
            ("30 CR90 v 1 ISD", new[] { (Corvette, 30) }, new[] { (Isd, 1) }),
            ("8 CR90 v 1 ISD", new[] { (Corvette, 8) }, new[] { (Isd, 1) }),
            ("1 Mon Cal v 1 ISD", new[] { (MonCal, 1) }, new[] { (Isd, 1) }),
            ("2 ISD v 3 Mon Cal", new[] { (Isd, 2) }, new[] { (MonCal, 3) }),
            ("Strike Cruiser v Victory II", new[] { ("Strike Cruiser", 1) }, new[] { ("Victory II Star Destroyer", 1) }),
            ("4 Dauntless v 2 Imp II", new[] { ("Dauntless Cruiser", 4) }, new[] { ("Imperial II Star Destroyer", 2) }),
        };
        foreach (var (name, a, e) in cases)
        {
            string Row(BattleParams p)
            {
                int aw = 0, ew = 0, none = 0;
                for (ulong seed = 1; seed <= 200; seed++)
                {
                    var (r, _) = Fight(p, a, e, seed);
                    if (r.AllianceOutcome == BattleOutcome.Victorious) aw++; else if (r.EmpireOutcome == BattleOutcome.Victorious) ew++; else none++;
                }
                return $"A {aw / 2.0:0.0}% E {ew / 2.0:0.0}% none {none / 2.0:0.0}%";
            }
            _out.WriteLine($"{name,-30} Classic: {Row(new BattleParams())}   New: {Row(New)}");
        }
    }
}
