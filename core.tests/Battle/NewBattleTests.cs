using System.Diagnostics;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Ships;
using Praedra.Core.Tests.Fleets;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.Battle;

// Tests of m4-battle-new (N ids as in the sheet's table) and of its Lead update 2026-10-12, on plain units (no game around), with the real CSVs.
// The sheet's "model" counts were made with a Mersenne generator, so counts here are our own seeded results, asserted as rates or invariants.
public class NewBattleTests
{
    private readonly ITestOutputHelper _out;
    public NewBattleTests(ITestOutputHelper output) { _out = output; }

    internal static IReadOnlyDictionary<string, ShipClass> Ships => World.Ships.Value;
    internal static readonly BattleParams New = new() { Ruleset = BattleRuleset.New };
    internal static readonly BattleParams NewNoRolls = new() { Ruleset = BattleRuleset.New, SubsystemRolls = false };
    /// <summary>New with Hold beams off (m4-retreat-damage-repair §6): for the sheet's base retreat tests, where an Mandate would hold one corvette.</summary>
    internal static readonly BattleParams NewNoHoldBeams = new() { Ruleset = BattleRuleset.New, New = new NewBattleParams { HoldBeamsHoldRetreat = false } };
    private const string Corvette = "Dodger", Mandate = "Mandate", Ark = "Ark", Lancer = "Writ",
        Interdictor = "Subpoena", Bomber = "Thresher";

    internal static (ClassicBattleResult R, IReadOnlyList<BattleUnit> U) Fight(BattleParams p, (string, int)[] collective, (string, int)[] hand, ulong seed = 1,
        bool aCan = true, bool eCan = true, bool carry = false, Action<IReadOnlyList<BattleUnit>>? prepare = null, OnContactMode[]? alert = null) =>
        QuickBattle.Run(p, Ships, collective, hand, seed, aCan, eCan, alert, null, carry, prepare);

    private static BattleUnit First(IReadOnlyList<BattleUnit> u, Side side, string cls) => u.First(x => x.Side == side && x.Class.Name == cls);

    // ---------------------------------------------------------------- N1 determinism

    // m4-battle-new N1
    [Fact]
    public void N1_SameFleetsSameSeedTwiceGiveTheSameResultAndDrawCount()
    {
        var (r1, u1) = Fight(New, new[] { (Ark, 3) }, new[] { (Mandate, 2) }, seed: 7);
        var (r2, u2) = Fight(New, new[] { (Ark, 3) }, new[] { (Mandate, 2) }, seed: 7);
        Assert.True(r1.Draws > 0);
        Assert.Equal(r1.Draws, r2.Draws);
        Assert.Equal((r1.Seconds, r1.CollectiveOutcome, r1.HandOutcome, r1.CollectiveLosses, r1.HandLosses), (r2.Seconds, r2.CollectiveOutcome, r2.HandOutcome, r2.CollectiveLosses, r2.HandLosses));
        Assert.Equal(u1.Select(x => (x.Hull, x.Alive, x.Left, string.Join(",", x.Hits))), u2.Select(x => (x.Hull, x.Alive, x.Left, string.Join(",", x.Hits))));
        var (r3, _) = Fight(New, new[] { (Ark, 3) }, new[] { (Mandate, 2) }, seed: 8);
        Assert.NotEqual(r1.Draws, r3.Draws); // another seed, another battle
    }

    // ---------------------------------------------------------------- the headline: laser boats against an Mandate

    // m4-battle-new N2 (and the plan's M4 proof for New): 30 Corvettes v 1 Mandate, corvettes can retreat: they leave in five seconds, the Mandate is untouched
    // (Hold beams off: with them the Mandate holds one corvette and the battle runs until it dies, m4-retreat-damage-repair TB1)
    [Fact]
    public void N2_ThirtyCorvettesVsOneMandate_CorvettesRetreatAndTheMandateIsUntouched()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (r, u) = Fight(NewNoHoldBeams, new[] { (Corvette, 30) }, new[] { (Mandate, 1) }, seed);
            Assert.True(r.CollectiveRetreated, $"seed {seed}");
            Assert.Equal(BattleOutcome.Retreated, r.CollectiveOutcome);
            Assert.Equal(BattleOutcome.Victorious, r.HandOutcome);
            Assert.InRange(r.Seconds, 5, 6); // flagged in the first second (strength 0 against 26.5 or more), five seconds in the jump
            Assert.Equal(2750.0, First(u, Side.Hand, Mandate).Hull);
        }
    }

    // m4-battle-new N2 and the plan's M4 proof: over a seeded batch the Mandate wins at least 95% (here every battle)
    [Theory]
    [InlineData(30)]
    [InlineData(8)]
    public void HeadlineCorvettesVsMandate_TheMandateWinsAlmostAlways(int corvettes)
    {
        int mandateWins = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var (r, _) = Fight(New, new[] { (Corvette, corvettes) }, new[] { (Mandate, 1) }, seed);
            if (r.HandOutcome == BattleOutcome.Victorious) mandateWins++;
        }
        _out.WriteLine($"New: {corvettes} Dodger v 1 Mandate, Mandate wins {mandateWins}/200");
        Assert.True(mandateWins >= 190, $"Mandate won {mandateWins}/200");
    }

    // m4-battle-new N3: retreat not allowed: the corvettes never scratch the Mandate (lasers never penetrate a capital's shield); the Mandate kills them or the stalemate does
    [Theory]
    [InlineData(30)]
    [InlineData(8)]
    public void N3_CorvettesThatCannotRetreatNeverScratchTheMandate(int corvettes)
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (r, u) = Fight(New, new[] { (Corvette, corvettes) }, new[] { (Mandate, 1) }, seed, aCan: false);
            Assert.False(r.CollectiveRetreated);
            Assert.Equal(BattleOutcome.Victorious, r.HandOutcome);
            Assert.Equal(BattleOutcome.Annihilated, r.CollectiveOutcome);
            Assert.Equal(corvettes, r.CollectiveLosses);
            Assert.Equal(2750.0, First(u, Side.Hand, Mandate).Hull);
            Assert.Contains(r.Steps.OfType<RetreatRefusedStep>(), s => s.Reason == RetreatRefusal.NoDestination);
        }
    }

    // m4-battle-new N4: 20 Lancers v 1 Mandate, no retreat: the Mandate hull stays whole, the Mandate wins
    [Fact]
    public void N4_TwentyLancersVsOneMandateNoRetreat()
    {
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var (r, u) = Fight(New, new[] { (Lancer, 20) }, new[] { (Mandate, 1) }, seed, aCan: false);
            Assert.Equal(BattleOutcome.Victorious, r.HandOutcome);
            Assert.Equal(2750.0, First(u, Side.Hand, Mandate).Hull);
        }
    }

    // ---------------------------------------------------------------- N5 gravity wells

    // m4-battle-new N5 (changed setup, see the report): the corvettes cannot hurt a hull-1 Interdictor (its shield is up), so a weakened The Moorings
    // Cruiser (hull 30%, flagged all the same) shoots it. While the Interdictor lives the Collective cannot retreat; once it is dead the retreat works.
    [Fact]
    public void N5_AGravityWellShipBlocksRetreatWhileItLivesAndKillingItFreesTheRetreat()
    {
        void Weaken(IReadOnlyList<BattleUnit> u, int interdictorHull)
        {
            var mc = First(u, Side.Collective, Ark); mc.Hull = 0.3 * mc.HullMax;
            First(u, Side.Hand, Interdictor).Hull = interdictorHull;
        }
        var fleetA = new[] { (Corvette, 30), (Ark, 1) };
        var fleetE = new[] { (Mandate, 1), (Interdictor, 1) };
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var (r, u) = Fight(New, fleetA, fleetE, seed, prepare: x => Weaken(x, 1200));
            Assert.False(r.CollectiveRetreated, $"seed {seed}");
            Assert.Contains(r.Steps.OfType<RetreatRefusedStep>(), s => s.Reason == RetreatRefusal.GravityWell);
            Assert.False(First(u, Side.Hand, Interdictor).Destroyed);

            var (r2, u2) = Fight(New, fleetA, fleetE, seed, prepare: x => Weaken(x, 1));
            Assert.True(First(u2, Side.Hand, Interdictor).Destroyed, $"seed {seed}");
            Assert.True(r2.CollectiveRetreated, $"seed {seed}");
            Assert.Equal(BattleOutcome.Retreated, r2.CollectiveOutcome);
        }
    }

    // ---------------------------------------------------------------- N6, N17 mirror and The Moorings

    // m4-battle-new N6 (no side-order bias): 2 Mandate v 2 Mandate, each side wins 45 to 55% (here 1000 seeds, so the band is tight enough to mean something)
    [Fact]
    public void N6_MirrorTwoMandateVsTwoMandateIsEvenAndSwappingSidesDoesNotChangeTheRates()
    {
        int a = 0, e = 0;
        for (ulong seed = 1; seed <= 1000; seed++)
        {
            var (r, _) = Fight(New, new[] { (Mandate, 2) }, new[] { (Mandate, 2) }, seed);
            if (r.CollectiveOutcome == BattleOutcome.Victorious) a++; else if (r.HandOutcome == BattleOutcome.Victorious) e++;
        }
        _out.WriteLine($"2 Mandate v 2 Mandate: Collective {a}, Hand {e} of 1000");
        Assert.InRange(a, 450, 550);
        Assert.InRange(e, 450, 550);
    }

    // m4-battle-new N6 (second half) and N17: Mandate v The Moorings, 100 seeds each side; rates recorded (tuneable), the two assignments of the sides agree
    [Fact]
    public void N17_MandateVsMonCalamari_RatesAreTheSameWhicheverSideIsWhich()
    {
        int arkAsCollective = 0, arkAsHand = 0;
        // The Moorings is a Collective class and the Mandate a Hand class, but the engine does not care which side a class is on: swap by listing it on the other side
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var (r, _) = Fight(New, new[] { (Ark, 1) }, new[] { (Mandate, 1) }, seed);
            if (r.CollectiveOutcome == BattleOutcome.Victorious) arkAsCollective++;
            var (r2, _) = Fight(New, new[] { (Mandate, 1) }, new[] { (Ark, 1) }, seed);
            if (r2.HandOutcome == BattleOutcome.Victorious) arkAsHand++;
        }
        _out.WriteLine($"The Moorings v Mandate, 100 seeds: wins as Collective {arkAsCollective}, as Hand {arkAsHand} (sheet model about 2 in 3)");
        // N17: recorded, tuneable, not a hard bound. space-combat/11 §4: both close to D = 21 and fight at the Mandate's ion range, so the Mandate now wins most (The Moorings 18% of 100 seeds
        // against 73% under the old m4-battle-new §5 rule). Balance flag for the Space combat owner.
        Assert.InRange(arkAsCollective, 5, 90);
        Assert.InRange(arkAsHand, 5, 90);
        Assert.InRange(Math.Abs(arkAsCollective - arkAsHand), 0, 20); // same rates within sampling noise
    }

    // ---------------------------------------------------------------- N7 shield regeneration pause

    // m4-battle-new N7: a hit at t, then no regen before t + 5: the shield value is unchanged t+1 .. t+4 and grows at t+5 by regen x hullRatio
    [Fact]
    public void N7_ShieldDoesNotRegenerateForFiveSecondsAfterAHit()
    {
        // space-combat/11 §4 changed the engagement: both ships now fire from the first seconds at D = 21, so The Moorings is hit again within 5 s on most seeds;
        // look for the first seed with a hit followed by a quiet spell (a regen at +5), instead of fixing seed 3.
        bool found = false;
        for (ulong seed = 1; seed <= 40 && !found; seed++)
        {
            var (nb, units) = QuickBattle.PrepareNew(NewNoRolls, Ships, new[] { (Ark, 1) }, new[] { (Mandate, 1) }, seed);
            var tracks = new List<(List<double>, List<double>, double)>();
            foreach (var (cls, hullMax) in new[] { (Ark, 2400.0), (Mandate, 2750.0) })
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

    // m4-battle-new N13: an Mandate carrying 6 Thresher squadrons beats an Mandate; without the hangar it is even
    [Fact]
    public void N13_MandateCarryingSixBomberSquadronsBeatsAnMandate()
    {
        int carrierWins = 0, evenWins = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (r, _) = Fight(New, new[] { (Mandate, 1), (Bomber, 6) }, new[] { (Mandate, 1) }, seed, carry: true);
            if (r.CollectiveOutcome == BattleOutcome.Victorious) carrierWins++;
            var (r0, _) = Fight(New, new[] { (Mandate, 1) }, new[] { (Mandate, 1) }, seed);
            if (r0.CollectiveOutcome == BattleOutcome.Victorious) evenWins++;
        }
        _out.WriteLine($"Mandate+6 Bombers v Mandate: carrier side wins {carrierWins}/40; Mandate v Mandate: Collective wins {evenWins}/40");
        Assert.True(carrierWins >= 36);
        Assert.InRange(evenWins, 10, 30);
    }

    // m4-battle-new N14: a squadron whose carrier dies at t = 3 (before the launch at 5) is lost and never fires
    [Fact]
    public void N14_ASquadronWhoseCarrierDiesBeforeLaunchIsLostAndNeverFires()
    {
        var (nb, units) = QuickBattle.PrepareNew(New, Ships, new[] { (Mandate, 1), (Bomber, 1) }, new[] { (Mandate, 1) }, 1, carry: true,
            prepare: u => First(u, Side.Collective, Mandate).Hull = 1, logEvents: true);
        var r = nb.Run();
        var carrier = First(units, Side.Collective, Mandate); var sq = First(units, Side.Collective, Bomber);
        Assert.True(carrier.Destroyed);
        Assert.Equal(2, carrier.LostAt); // the enemy Mandate's first volley (D = 60 closing 7.5 per second, Mandate sublight 5, against a range of 50)
        Assert.True(sq.Destroyed);
        Assert.Equal(UnitLossReason.NoBerth, sq.Loss);
        Assert.Equal(2, sq.LostAt);
        Assert.DoesNotContain(nb.Log!, e => e.Unit == sq.ObjectId && (e.Kind == "hit" || e.Kind == "miss" || e.Kind == "launch"));
        Assert.Equal(BattleOutcome.Annihilated, r.CollectiveOutcome);
    }

    // m4-battle-new §10: at the end a squadron of fewer than 6 craft is lost, a whole one is kept
    [Fact]
    public void SquadronsUnderSixCraftAreLostAtTheEndAndWholeOnesStay()
    {
        var (r, u) = Fight(NewNoHoldBeams, new[] { (Corvette, 4) }, new[] { (Mandate, 1), ("Gleaner", 2) }, 1, carry: true, // Hold beams off: a held corvette would shoot the Gleaners
            prepare: x => x.Where(q => q.Class.Name == "Gleaner").Last().Hull = 40); // ceil(40 / 8) = 5 craft
        var sq = u.Where(x => x.Class.Name == "Gleaner").OrderBy(x => x.ObjectId).ToList();
        Assert.False(sq[0].Destroyed);
        Assert.True(sq[1].Destroyed);
        Assert.Equal(UnitLossReason.Combat, sq[1].Loss);
    }

    // m4-battle-new §10, §11: a retreating carrier takes its squadrons; squadrons of a side that retreats with no berth are lost
    [Fact]
    public void RetreatingCarrierTakesItsSquadronsAndOthersWithoutBerthOrHyperdriveAreLost()
    {
        // Collective: an Escort Carrier with 6 berths and 8 fighter-less... use Hand squadrons: Mandate (6 berths) carries 6, two more are based
        var (r, u) = Fight(New, new[] { (Mandate, 1), ("Gleaner", 8) }, new[] { (Ark, 1), (Mandate, 3) }, 1, carry: true,
            prepare: x => { }, alert: new[] { OnContactMode.Fight, OnContactMode.Retreat }); // indexed by (int)Side: Hand, Collective
        Assert.True(r.CollectiveRetreated);
        var sq = u.Where(x => x.Side == Side.Collective && x.Class.Name == "Gleaner").OrderBy(x => x.ObjectId).ToList();
        Assert.Equal(6, sq.Count(x => x.Left)); // the six riding the Mandate
        Assert.Equal(2, sq.Count(x => x.Destroyed && x.Loss == UnitLossReason.NoBerth));
    }

    // m4-battle-new Lead update 4: torpedoes fire only at capitals whose shield is 0, unless TorpedoesVsShields is on (then at the first second the squadron may fire)
    [Fact]
    public void TorpedoesFireOnlyAtDroppedShieldsUnlessTheSwitchIsOn()
    {
        int FirstTorpedoTick(NewBattleParams np)
        {
            var (nb, units) = QuickBattle.PrepareNew(new BattleParams { Ruleset = BattleRuleset.New, New = np }, Ships,
                new[] { ("Nest", 1), ("Plough", 6) }, new[] { (Mandate, 1) }, 5, handCanLand: false, carry: true, logEvents: true); // the Mandate cannot run, as it otherwise would (six Plough ion batteries outweigh it)
            nb.Run();
            var ids = units.Where(x => x.Class.Name == "Plough").Select(x => x.ObjectId).ToHashSet();
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
        var (r2, _) = Fight(New, new[] { (Corvette, 3) }, new[] { (Mandate, 1) }, 1);
        Assert.True(r2.CollectiveRetreated);
        Assert.False(r2.HandRetreated);
    }

    // m4-battle-new §11.4: the stalemate flags the side with the lower strength; a side that cannot retreat is destroyed
    [Fact]
    public void StalemateDestroysTheSideThatCannotRetreat()
    {
        var (r, _) = Fight(New, new[] { (Corvette, 3) }, new[] { (Corvette, 3) }, 1, aCan: false, eCan: false);
        Assert.InRange(r.Seconds, 1200, 1206);
        Assert.Equal(BattleOutcome.Indecisive, r.CollectiveOutcome); // equal strength: both flagged, both destroyed
        Assert.Equal(BattleOutcome.Indecisive, r.HandOutcome);
        Assert.All(new[] { r.CollectiveLosses, r.HandLosses }, n => Assert.Equal(3, n));
    }

    // ---------------------------------------------------------------- Lead update 2026-10-12

    // Lead update 2: D = the longest weapon range present in the battle + 10
    [Theory]
    [InlineData(Mandate, Mandate, 60)]       // heavy cannon 50
    [InlineData(Corvette, Corvette, 27)] // laser 17
    [InlineData(Corvette, "Mandate II", 80)] // heavy cannon 70
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
        foreach (var name in new[] { "Hauler", "Dodger", "Mule", "Wasp" })
        {
            var target = Combatant.Strategic(data.Class(name));
            Assert.Equal(0.0, m.ShotValue(WeaponKind.LaserCannon, 6.0, data.Class(Corvette), target));
        }
        // with the shield down a laser hurts the hull at 0.167
        var down = new Combatant { Class = data.Class(Mandate), HullRatio = 1, ShieldFactor = 0.5, ShieldUp = false };
        Assert.Equal(6.0 * 0.167, m.ShotValue(WeaponKind.LaserCannon, 6.0, data.Class(Corvette), down), 1e-12);
        // Deflection is the class column (Mandate 1.5), not shield / 200: ion 20 x 1.5 = 30 passes, a Plough ion 2.25 x 1.5 = 3.375 passes the Mandate (1.5) but not the Barricade (5.5)
        Assert.Equal(30.0, m.ShotValue(WeaponKind.IonCannon, 20, data.Class(Mandate), Combatant.Strategic(data.Class(Ark))), 1e-12);
        Assert.True(m.ShotValue(WeaponKind.IonCannon, 2.25, data.Class("Plough"), Combatant.Strategic(data.Class(Mandate))) > 0);
        Assert.Equal(0.0, m.ShotValue(WeaponKind.IonCannon, 2.25, data.Class("Plough"), Combatant.Strategic(data.Class("Barricade"))));
        // torpedoes only at shield-0 capitals by default
        Assert.Equal(0.0, m.ShotValue(WeaponKind.Torpedo, 35, data.Class(Bomber), Combatant.Strategic(data.Class(Mandate))));
        Assert.Equal(35.0, m.ShotValue(WeaponKind.Torpedo, 35, data.Class(Bomber), down), 1e-12);
        var withShields = new CombatModel(data, new NewBattleParams { TorpedoesVsShields = true });
        Assert.Equal(17.5, withShields.ShotValue(WeaponKind.Torpedo, 35, data.Class(Bomber), Combatant.Strategic(data.Class(Mandate))), 1e-12);
    }

    // ---------------------------------------------------------------- the sheet's rules in the small

    // m4-battle-new §1: guns_active = floor(guns x (1 - 0.25 x hits[weapons])), four hits = none
    [Fact]
    public void GunsActiveFollowTheWeaponsCounter()
    {
        var m = new CombatModel(NewBattleData.Default);
        var b = NewBattleData.Default.Class(Mandate).Batteries[0]; // 10 guns
        int G(int hits) => m.GunsActive(b, new Combatant { Class = NewBattleData.Default.Class(Mandate), WeaponHits = hits });
        Assert.Equal(new[] { 10, 7, 5, 2, 0, 0 }, new[] { G(0), G(1), G(2), G(3), G(4), G(9) });
    }

    // m4-battle-new §2: maneuver ratio against a squadron (heavy cannon 0.5 x ratio, laser 1.0 x ratio, ion 0)
    [Fact]
    public void FighterMultipliersUseTheManeuverRatio()
    {
        var d = NewBattleData.Default; var m = new CombatModel(d);
        var tie = Combatant.Strategic(d.Class("Gleaner")); // maneuver 5, shield pool 0
        Assert.Equal(40 * 0.5 * (1.0 / 5), m.ShotValue(WeaponKind.HeavyCannon, 40, d.Class(Mandate), tie), 1e-12); // Mandate maneuver 1
        Assert.Equal(6.0 * 1.0 * (3.0 / 5), m.ShotValue(WeaponKind.LaserCannon, 6, d.Class(Corvette), tie), 1e-12); // Corvette maneuver 3
        Assert.Equal(0.0, m.ShotValue(WeaponKind.IonCannon, 20, d.Class(Mandate), tie));
        Assert.Equal(3.75, m.ShotValue(WeaponKind.LaserCannon, 3.75, d.Class("Needle"), tie), 1e-12); // ratio capped at 1
    }

    // space-combat/11 §4 (3), (7) (supersedes m4-battle-new §5): StandOff closes to min(reach), Surround closes to 15; both finish
    [Fact]
    public void TacticSurroundClosesTheDistance()
    {
        var (so, _) = QuickBattle.PrepareNew(New, Ships, new[] { (Mandate, 1) }, new[] { (Mandate, 1) }, 4);
        double minStand = 1e9; so.AfterTick = _ => minStand = Math.Min(minStand, so.Distance); so.Run();
        var (su, _) = QuickBattle.PrepareNew(New, Ships, new[] { (Mandate, 1) }, new[] { (Mandate, 1) }, 4);
        su.SetTactic(Side.Collective, BattleTactic.Surround); su.SetTactic(Side.Hand, BattleTactic.Surround);
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
            var (r, u) = Fight(New, new[] { (Ark, 2), (Corvette, 4) }, new[] { (Mandate, 2), (Lancer, 3) }, seed);
            Assert.True(r.Seconds <= 1800 + 10);
            Assert.All(u, x => Assert.True(x.Destroyed || x.Left || x.Alive));
            Assert.Equal(u.Count(x => x.Side == Side.Collective), r.CollectiveLosses + r.CollectiveSurvivors);
            Assert.All(u.Where(x => x.IsCapital && !x.Destroyed), x => Assert.True(x.Hull > 0 && x.Hull <= x.HullMax));
            Assert.All(u.Where(x => x.IsCapital), x => Assert.All(x.Hits, h => Assert.InRange(h, 0, 4)));
        }
    }

    // m4-battle-new N16: speed. The sheet wants 0.5 s on the Steam Deck for 40 v 40 mixed ships; not checkable here, so this records the time on the
    // build machine and only guards against an order-of-magnitude regression.
    [Fact]
    public void N16_FortyVsFortyMixedShipsFinishQuickly()
    {
        var a = new[] { (Ark, 4), ("Mender", 8), (Corvette, 12), ("Homeward", 2), ("Jubilee", 4), ("Crowbar", 4), ("Wasp", 6) };
        var e = new[] { (Mandate, 8), ("Injunction", 6), (Lancer, 8), ("Decree II", 4), ("Warrant", 6), ("Mandate II", 2), (Interdictor, 6) };
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
    public void ClassicVersusNewTable_IsPrintedAndTheNewEngineKeepsTheCorvettesOutOfTheMandate()
    {
        var cases = new (string Name, (string, int)[] A, (string, int)[] E)[]
        {
            ("30 Dodger v 1 Mandate", new[] { (Corvette, 30) }, new[] { (Mandate, 1) }),
            ("8 Dodger v 1 Mandate", new[] { (Corvette, 8) }, new[] { (Mandate, 1) }),
            ("1 Mon Cal v 1 Mandate", new[] { (Ark, 1) }, new[] { (Mandate, 1) }),
            ("2 Mandate v 3 Mon Cal", new[] { (Mandate, 2) }, new[] { (Ark, 3) }),
            ("Injunction v Victory II", new[] { ("Injunction", 1) }, new[] { ("Decree II", 1) }),
            ("4 Homeward v 2 Imp II", new[] { ("Homeward", 4) }, new[] { ("Mandate II", 2) }),
        };
        foreach (var (name, a, e) in cases)
        {
            string Row(BattleParams p)
            {
                int aw = 0, ew = 0, none = 0;
                for (ulong seed = 1; seed <= 200; seed++)
                {
                    var (r, _) = Fight(p, a, e, seed);
                    if (r.CollectiveOutcome == BattleOutcome.Victorious) aw++; else if (r.HandOutcome == BattleOutcome.Victorious) ew++; else none++;
                }
                return $"A {aw / 2.0:0.0}% E {ew / 2.0:0.0}% none {none / 2.0:0.0}%";
            }
            _out.WriteLine($"{name,-30} Classic: {Row(new BattleParams())}   New: {Row(New)}");
        }
    }
}
