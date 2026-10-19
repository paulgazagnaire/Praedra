using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Ships;

namespace Praedra.Core.Tests.Fleets;

// Tests of m4-battle-new §12 (CombatStrength, the engine's own strength measure) and the strategic rules that use it: forced stop and economic
// blockade (m1-forced-stop-and-blockade, 2026-10-12 update). Ids N8 to N12 as in m4-battle-new. All with the real CSVs.
public class CombatStrengthTests
{
    private const string Isd = "Imperial Star Destroyer", Corvette = "Corellian Corvette", Victory = "Victory Destroyer";
    private static readonly IReadOnlyDictionary<string, ShipClass> Ships = World.Ships.Value;
    private static readonly CombatStrength Cs = new(NewBattleData.Default);

    private static List<ShipClass> Of(string cls, int n) => Enumerable.Repeat(Ships[cls], n).ToList();
    private static List<ShipClass> Ref => Of(Isd, 3);

    // m4-battle-new N8
    [Fact]
    public void N8_StrengthValuesAtTheStrategicLayer()
    {
        Assert.Equal(45.0, Cs.Of(Of(Isd, 1), Of(Isd, 1)), 1e-9);
        Assert.Equal(135.0, Cs.Of(Of(Isd, 3), Of(Isd, 3)), 1e-9);
        Assert.Equal(64.8, Cs.Of(Of(Victory, 3), Ref), 1e-9); // = E
        Assert.Equal(45.0, Cs.Of(Of(Isd, 1), Ref), 1e-9);     // < E
        Assert.Equal(90.0, Cs.Of(Of(Isd, 2), Ref), 1e-9);     // >= E
        Assert.Equal(37.8, Cs.Of(Of("Strike Cruiser", 1), Ref), 1e-9);
        Assert.Equal(6.3, Cs.Of(Of("Interdictor Cruiser", 1), Ref), 1e-9);
        Assert.Equal(0.0, Cs.Of(Of("Lancer Frigate", 1), Ref), 1e-9);
        Assert.Equal(113.4, Cs.Of(Of("Strike Cruiser", 3), Ref), 1e-9); // Lead update 6: 3 Strike Cruisers above E, as intended
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
    public void VersusEmptyIsZero() => Assert.Equal(0.0, Cs.Of(Of(Isd, 3), new List<ShipClass>()));

    // m4-battle-new N9
    [Fact]
    public void N9_TwentyCheapestArmedShipsAreWorthNothingAgainstShieldedShips()
    {
        var mix = Of("Corellian Gunship", 12).Concat(Of("Medium Transport", 8)).ToList();
        Assert.Equal(0.0, Cs.Of(mix, Of(Isd, 3)));
        Assert.Equal(0.0, Cs.Of(Of(Corvette, 30), Of(Isd, 3)));
        var blockade = World.Blockade;
        Assert.True(Cs.Of(mix, Ref) < blockade.EconomicStrength(Ships, Cs)); // no economic blockade (0 < E)
    }

    // m4-battle-new N9: no forced stop (0 is not > 0), through the fleet module
    [Fact]
    public void N9_TwentyCheapShipsDoNotStopThreeIsds()
    {
        var w = new World(World.BCMap());
        int isds = w.Add(Side.Empire, "Isds", "A", Isd, Isd, Isd);
        w.Add(Side.Alliance, "Cheap", "B", Enumerable.Repeat("Corellian Gunship", 12).Concat(Enumerable.Repeat("Medium Transport", 8)).ToArray());
        w.Move(Side.Empire, isds, "C");
        w.Run(40);
        Assert.Empty(w.Events<ForcedStop>());
        Assert.Equal("C", w.Fleet(isds).At);
        Assert.False(w.Fleets.IsEconomicBlockade("B", Side.Empire));
    }

    // m4-battle-new N10
    [Theory]
    [InlineData(1, false)] // 45 < 0.5 x 135 = 67.5
    [InlineData(2, true)]  // 90 >= 67.5
    public void N10_ThreeIsdsPassing_OneOrTwoIsdsBlock(int blockers, bool stops)
    {
        var w = new World(World.BCMap());
        int passing = w.Add(Side.Alliance, "Passing", "A", Isd, Isd, Isd);
        w.Add(Side.Empire, "Block", "B", Enumerable.Repeat(Isd, blockers).ToArray());
        w.Move(Side.Alliance, passing, "C");
        w.Run(40);
        Assert.Equal(stops, w.Events<ForcedStop>().Count == 1);
        Assert.Equal(stops ? "B" : "C", w.Fleet(passing).At);
    }

    // m4-battle-new N11
    [Fact]
    public void N11_ThreeStrikeCruisersStopAConvoyOfMediumTransports()
    {
        var convoy = Of("Medium Transport", 3);
        Assert.Equal(65.7, Cs.Of(Of("Strike Cruiser", 3), convoy), 1e-9);
        Assert.Equal(0.0, Cs.Of(convoy, Of("Strike Cruiser", 3)));
        var w = new World(World.BCMap());
        int c = w.Add(Side.Alliance, "Convoy", "A", "Medium Transport", "Medium Transport", "Medium Transport");
        w.Add(Side.Empire, "Strikes", "B", "Strike Cruiser", "Strike Cruiser", "Strike Cruiser");
        w.Move(Side.Alliance, c, "C");
        w.Run(25); // speed rating 100: a hop takes 20 days
        Assert.Equal(c, Assert.Single(w.Events<ForcedStop>()).Fleet);
        Assert.True(w.Fleets.IsEconomicBlockade("B", Side.Alliance)); // 113.4 >= E: the intended consequence of Lead update 6
    }

    // m4-battle-new N12 with the Lead update 3 (switch LaserPenetratesShield, DEFAULT off)
    [Fact]
    public void N12_CorvetteVsMediumTransport_LeadUpdate3GivesZero_TheOldEdgeCaseGivesThreePointFour()
    {
        Assert.Equal(0.0, Cs.Of(Of(Corvette, 1), Of("Medium Transport", 1)));
        var edge = new CombatStrength(NewBattleData.Default, new NewBattleParams { LaserPenetratesShield = true });
        Assert.Equal(3.4, edge.Of(Of(Corvette, 1), Of("Medium Transport", 1)), 0.05); // 4 guns x 6 x 0.167 x 0.85 > deflection 0.5
        Assert.True(edge.Of(Of(Corvette, 1), Of("Bulk Cruiser", 1)) > 0);            // the edge case: 6 x 0.167 = 1.002 > deflection 1.0
        Assert.Equal(0.0, Cs.Of(Of(Corvette, 1), Of("Bulk Cruiser", 1)));            // Lead update 3: never
    }

    // m4-battle-new §12 (hull ratio and the weapons counter come from the strategic ship)
    [Fact]
    public void HullRatioAndWeaponsHitsScaleTheStrength()
    {
        var isd = Cs.Unit(Ships[Isd]);
        var half = Cs.Unit(Ships[Isd], 0.5);
        var weaponsHit2 = Cs.Unit(Ships[Isd], 1.0, 2);
        var target = new[] { Cs.Unit(Ships[Isd]) };
        Assert.Equal(22.5, Cs.Of(new[] { half }, target), 1e-9);
        // two weapons hits: floor(10 x 0.5) = 5 guns of each type: half of 45
        Assert.Equal(22.5, Cs.Of(new[] { weaponsHit2 }, target), 1e-9);
        Assert.Equal(0.0, Cs.Of(new[] { Cs.Unit(Ships[Isd], 1.0, 4) }, target)); // four hits: no guns
        Assert.Equal(45.0, Cs.Of(new[] { isd }, target), 1e-9);
    }

    // m4-battle-new §12 (squadrons aboard count at the strategic layer as if launched; torpedoes enter as damage over 180 s, only against shield-0 capitals by default)
    [Fact]
    public void SquadronsCountAndTorpedoesNeedDroppedShields()
    {
        var bomber = Cs.Unit(Ships["TIE Bomber"]);
        var isd = Cs.Unit(Ships[Isd]);
        double alone = Cs.Of(new[] { isd }, new[] { isd });
        double carrying = Cs.Of(new[] { isd, bomber, bomber, bomber, bomber, bomber, bomber }, new[] { isd });
        Assert.True(carrying > alone);
        // a bomber's ions: 12 guns x 2.25 x 1.5 x 0.90 / 1 s = 36.45 against a shielded ISD; torpedoes add nothing against shields by default
        Assert.Equal(36.45, Cs.Of(new[] { bomber }, new[] { isd }), 1e-9);
        var on = new CombatStrength(NewBattleData.Default, new NewBattleParams { TorpedoesVsShields = true });
        // 8 torpedoes x 35 x 0.5 x 0.90 / 180 = 0.7
        Assert.Equal(36.45 + 0.7, on.Of(new[] { on.Unit(Ships["TIE Bomber"]) }, new[] { on.Unit(Ships[Isd]) }), 1e-9);
    }

    // m4-battle-new §12 notes: cost weighting keeps cheap squadrons from diluting an ISD in the versus list
    [Fact]
    public void CostWeightingKeepsCheapSquadronsFromDilutingAnIsd()
    {
        var versus = new List<ShipClass>(Of(Isd, 1)); versus.AddRange(Of("TIE Fighter", 6));
        // corvettes cannot hurt the ISD; a TIE squadron (cost 3, maneuver 5) takes laser 4 x 6 x ratio 0.6 x 0.70 = 10.08 per corvette
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
        var isd = data.Class(Isd);
        Assert.Equal(8, isd.Batteries.Count);
        Assert.Equal((Arc.Fore, WeaponKind.Turbolaser, 10, 40.0, 20, 50), (isd.Batteries[0].Arc, isd.Batteries[0].Weapon, isd.Batteries[0].Guns, isd.Batteries[0].ShotDamage, isd.Batteries[0].Reload, isd.Batteries[0].Range));
        Assert.Equal((2750, 300, 15.0, 1.5, SizeClass.Large), (isd.Hull, isd.ShieldPool, isd.ShieldRegen, isd.Deflection, isd.Size));
        var bomber = data.Class("TIE Bomber");
        Assert.Equal((8, 35.0, 7), (bomber.Torpedoes, bomber.TorpedoDamage, bomber.TorpedoRange));
        Assert.Equal(2, bomber.Batteries.Count); // laser and ion; torpedoes are not a battery
        Assert.Equal((uint)(0.95 * 4294967296.0), data.Weapon(WeaponKind.Turbolaser).Threshold[(int)SizeClass.Huge]);
    }
}
