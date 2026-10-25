using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Ships;

namespace Praedra.Core.Tests.Fleets;

// Tests of m4-battle-new §12 (CombatStrength, the engine's own strength measure) and the strategic rules that use it: forced stop and economic
// blockade (m1-forced-stop-and-blockade, 2026-10-12 update). Ids N8 to N12 as in m4-battle-new. All with the real CSVs.
public class CombatStrengthTests
{
    private const string Mandate = "Mandate", Corvette = "Dodger", Victory = "Decree";
    private static readonly IReadOnlyDictionary<string, ShipClass> Ships = World.Ships.Value;
    private static readonly CombatStrength Cs = new(NewBattleData.Default);

    private static List<ShipClass> Of(string cls, int n) => Enumerable.Repeat(Ships[cls], n).ToList();
    private static List<ShipClass> Ref => Of(Mandate, 3);

    // m4-battle-new N8
    [Fact]
    public void N8_StrengthValuesAtTheStrategicLayer()
    {
        Assert.Equal(45.0, Cs.Of(Of(Mandate, 1), Of(Mandate, 1)), 1e-9);
        Assert.Equal(135.0, Cs.Of(Of(Mandate, 3), Of(Mandate, 3)), 1e-9);
        Assert.Equal(64.8, Cs.Of(Of(Victory, 3), Ref), 1e-9); // = E
        Assert.Equal(45.0, Cs.Of(Of(Mandate, 1), Ref), 1e-9);     // < E
        Assert.Equal(90.0, Cs.Of(Of(Mandate, 2), Ref), 1e-9);     // >= E
        Assert.Equal(37.8, Cs.Of(Of("Injunction", 1), Ref), 1e-9);
        Assert.Equal(6.3, Cs.Of(Of("Subpoena", 1), Ref), 1e-9);
        Assert.Equal(0.0, Cs.Of(Of("Writ", 1), Ref), 1e-9);
        Assert.Equal(113.4, Cs.Of(Of("Injunction", 3), Ref), 1e-9); // Lead update 6: 3 Strike Cruisers above E, as intended
    }

    // m4-battle-new N8 and §Numbers: E is computed from data, never typed
    [Fact]
    public void E_isComputedFromTheDataAndEquals648()
    {
        var blockade = World.Blockade;
        double e = blockade.EconomicStrength(Ships, Cs);
        Assert.Equal(64.8, e, 1e-9);
        Assert.Equal(e, Cs.Of(Of(Victory, blockade.EconomicShipCount), blockade.Reference(Ships)), 0.0);
    }

    // m4-battle-new §12: versus empty gives 0
    [Fact]
    public void VersusEmptyIsZero() => Assert.Equal(0.0, Cs.Of(Of(Mandate, 3), new List<ShipClass>()));

    // m4-battle-new N9
    [Fact]
    public void N9_TwentyCheapestArmedShipsAreWorthNothingAgainstShieldedShips()
    {
        var mix = Of("Wasp", 12).Concat(Of("Mule", 8)).ToList();
        Assert.Equal(0.0, Cs.Of(mix, Of(Mandate, 3)));
        Assert.Equal(0.0, Cs.Of(Of(Corvette, 30), Of(Mandate, 3)));
        var blockade = World.Blockade;
        Assert.True(Cs.Of(mix, Ref) < blockade.EconomicStrength(Ships, Cs)); // no economic blockade (0 < E)
    }

    // m4-battle-new N9: no forced stop (0 is not > 0), through the fleet module
    [Fact]
    public void N9_TwentyCheapShipsDoNotStopThreeMandates()
    {
        var w = new World(World.BCMap());
        int mandates = w.Add(Side.Hand, "Mandates", "A", Mandate, Mandate, Mandate);
        w.Add(Side.Collective, "Cheap", "B", Enumerable.Repeat("Wasp", 12).Concat(Enumerable.Repeat("Mule", 8)).ToArray());
        w.Move(Side.Hand, mandates, "C");
        w.Run(40);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Equal("C", w.Fleet(mandates).At);
        Assert.False(w.Fleets.IsEconomicBlockade("B", Side.Hand));
    }

    // m4-battle-new N10
    [Theory]
    [InlineData(1, false)] // 45 < 0.5 x 135 = 67.5
    [InlineData(2, true)]  // 90 >= 67.5
    public void N10_ThreeMandatesPassing_OneOrTwoMandatesBlock(int blockers, bool stops)
    {
        var w = new World(World.BCMap());
        int passing = w.Add(Side.Collective, "Passing", "A", Mandate, Mandate, Mandate);
        w.Add(Side.Hand, "Block", "B", Enumerable.Repeat(Mandate, blockers).ToArray());
        w.Move(Side.Collective, passing, "C");
        w.Run(40);
        Assert.Equal(stops, w.Events<ForcedStop>().Count == 1);
        Assert.Equal(stops ? "B" : "C", w.Fleet(passing).At);
    }

    // m4-battle-new N11
    [Fact]
    public void N11_ThreeStrikeCruisersStopAConvoyOfMediumTransports()
    {
        var convoy = Of("Mule", 3);
        Assert.Equal(65.7, Cs.Of(Of("Injunction", 3), convoy), 1e-9);
        Assert.Equal(0.0, Cs.Of(convoy, Of("Injunction", 3)));
        var w = new World(World.BCMap());
        int c = w.Add(Side.Collective, "Convoy", "A", "Mule", "Mule", "Mule");
        w.Add(Side.Hand, "Strikes", "B", "Injunction", "Injunction", "Injunction");
        w.Move(Side.Collective, c, "C");
        w.Run(25); // speed rating 100: a hop takes 20 days
        Assert.Equal(c, Assert.Single(w.Events<ForcedStop>()).Fleet);
        Assert.True(w.Fleets.IsEconomicBlockade("B", Side.Collective)); // 113.4 >= E: the intended consequence of Lead update 6
    }

    // m4-battle-new N12 with the Lead update 3 (switch LaserPenetratesShield, DEFAULT off)
    [Fact]
    public void N12_CorvetteVsMediumTransport_LeadUpdate3GivesZero_TheOldEdgeCaseGivesThreePointFour()
    {
        Assert.Equal(0.0, Cs.Of(Of(Corvette, 1), Of("Mule", 1)));
        var edge = new CombatStrength(NewBattleData.Default, new NewBattleParams { LaserPenetratesShield = true });
        Assert.Equal(3.4, edge.Of(Of(Corvette, 1), Of("Mule", 1)), 0.05); // 4 guns x 6 x 0.167 x 0.85 > deflection 0.5
        Assert.True(edge.Of(Of(Corvette, 1), Of("Hauler", 1)) > 0);            // the edge case: 6 x 0.167 = 1.002 > deflection 1.0
        Assert.Equal(0.0, Cs.Of(Of(Corvette, 1), Of("Hauler", 1)));            // Lead update 3: never
    }

    // m4-battle-new §12 (hull ratio and the weapons counter come from the strategic ship)
    [Fact]
    public void HullRatioAndWeaponsHitsScaleTheStrength()
    {
        var mandate = Cs.Unit(Ships[Mandate]);
        var half = Cs.Unit(Ships[Mandate], 0.5);
        var weaponsHit2 = Cs.Unit(Ships[Mandate], 1.0, 2);
        var target = new[] { Cs.Unit(Ships[Mandate]) };
        Assert.Equal(22.5, Cs.Of(new[] { half }, target), 1e-9);
        // two weapons hits: floor(10 x 0.5) = 5 guns of each type: half of 45
        Assert.Equal(22.5, Cs.Of(new[] { weaponsHit2 }, target), 1e-9);
        Assert.Equal(0.0, Cs.Of(new[] { Cs.Unit(Ships[Mandate], 1.0, 4) }, target)); // four hits: no guns
        Assert.Equal(45.0, Cs.Of(new[] { mandate }, target), 1e-9);
    }

    // m4-battle-new §12 (squadrons aboard count at the strategic layer as if launched; torpedoes enter as damage over 180 s, only against shield-0 capitals by default)
    [Fact]
    public void SquadronsCountAndTorpedoesNeedDroppedShields()
    {
        var bomber = Cs.Unit(Ships["Thresher"]);
        var mandate = Cs.Unit(Ships[Mandate]);
        double alone = Cs.Of(new[] { mandate }, new[] { mandate });
        double carrying = Cs.Of(new[] { mandate, bomber, bomber, bomber, bomber, bomber, bomber }, new[] { mandate });
        Assert.True(carrying > alone);
        // a bomber's ions: 12 guns x 2.25 x 1.5 x 0.90 / 1 s = 36.45 against a shielded Mandate; torpedoes add nothing against shields by default
        Assert.Equal(36.45, Cs.Of(new[] { bomber }, new[] { mandate }), 1e-9);
        var on = new CombatStrength(NewBattleData.Default, new NewBattleParams { TorpedoesVsShields = true });
        // 8 torpedoes x 35 x 0.5 x 0.90 / 180 = 0.7
        Assert.Equal(36.45 + 0.7, on.Of(new[] { on.Unit(Ships["Thresher"]) }, new[] { on.Unit(Ships[Mandate]) }), 1e-9);
    }

    // m4-battle-new §12 notes: cost weighting keeps cheap squadrons from diluting an Mandate in the versus list
    [Fact]
    public void CostWeightingKeepsCheapSquadronsFromDilutingAnMandate()
    {
        var versus = new List<ShipClass>(Of(Mandate, 1)); versus.AddRange(Of("Gleaner", 6));
        // corvettes cannot hurt the Mandate; a Gleaner squadron (cost 3, maneuver 5) takes laser 4 x 6 x ratio 0.6 x 0.70 = 10.08 per corvette
        double v = Cs.Of(Of(Corvette, 1), versus);
        Assert.Equal(18.0 * 10.08 / 130.0, v, 1e-9);
        Assert.True(v < 10.08 * 6 / 7); // a plain mean over seven units would say 8.64
    }

    // m4-battle-new §1: data loaded by column name; every class of the ship files has combat data, and the numbers are the CSV's
    [Fact]
    public void CombatDataCoversEveryShipClassAndSquadron()
    {
        var data = NewBattleData.Default;
        foreach (var cls in Ships.Values)
        {
            var c = data.Class(cls.Name);
            Assert.Equal(cls.Id, c.Id);
            Assert.Equal(cls.Hull > 0 ? cls.Hull : 96, c.Hull);
            Assert.Equal(cls.RefinedMaterialCost, c.Cost);
            Assert.Equal(cls.GravityWellProjector > 0, c.GravityWell > 0);
        }
        var mandate = data.Class(Mandate);
        Assert.Equal(8, mandate.Batteries.Count);
        Assert.Equal((Arc.Fore, WeaponKind.HeavyCannon, 10, 40.0, 20, 50), (mandate.Batteries[0].Arc, mandate.Batteries[0].Weapon, mandate.Batteries[0].Guns, mandate.Batteries[0].ShotDamage, mandate.Batteries[0].Reload, mandate.Batteries[0].Range));
        Assert.Equal((2750, 300, 15.0, 1.5, SizeClass.Large), (mandate.Hull, mandate.ShieldPool, mandate.ShieldRegen, mandate.Deflection, mandate.Size));
        var bomber = data.Class("Thresher");
        Assert.Equal((8, 35.0, 7), (bomber.Torpedoes, bomber.TorpedoDamage, bomber.TorpedoRange));
        Assert.Equal(2, bomber.Batteries.Count); // laser and ion; torpedoes are not a battery
        Assert.Equal((uint)(0.95 * 4294967296.0), data.Weapon(WeaponKind.HeavyCannon).Threshold[(int)SizeClass.Huge]);
    }
}
