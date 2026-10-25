using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Tests.Fleets;

namespace Praedra.Core.Tests.Battle;

// B6a §1 to §3 (reverse batch B6a, 2026-10-18): which side is the AI, its battle threshold computed at the alert, and its firepower number.
// The battles here are Classic (B6a is the Classic AI threshold): Ruleset = Classic is set explicitly since the DEFAULT became New (2026-10-18).
public class AiThresholdTests
{
    private const string Victory = "Decree", Ark = "Ark", Mandate = "Mandate";

    // B6a-1 set-up: Hand AI at its own non-HQ world B; its fleet 1 Victory; enemy 2 Mon Cal carrying 1 completed Swift squadron each
    private static BattleWorld World1(bool owned = true, bool planetSquadron = true, BattleParams? p = null)
    {
        var w = new BattleWorld(p ?? new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, HandIsAi = true });
        if (owned) w.Fleets.SetOwner("B", Side.Hand);
        if (planetSquadron) w.Eco.AddObject(Side.Hand, "Gleaner", "B");
        w.Fleet(Side.Hand, "B", Victory, 1);
        w.Fleets.AddFleet(Side.Collective, "Collective", new[] { Ark, Ark }, "B", new[] { "Swift", "Swift" });
        return w;
    }

    // B6a-7: firepower = 100 x B3's F against capital ships, integer
    [Fact]
    public void B6a_7_Firepower()
    {
        var s = World.Ships.Value;
        Assert.Equal(2000, AiFirepower.Ship(s[Mandate], 0, true));
        Assert.Equal(4000, AiFirepower.Ship(s["Mandate II"], 0, true));
        Assert.Equal(1200, AiFirepower.Ship(s[Victory], 0, true));
        Assert.Equal(1800, AiFirepower.Ship(s[Ark], 0, true));
        Assert.Equal(771, AiFirepower.Ship(s["Mender"], 0, true));
        Assert.Equal(133, AiFirepower.Ship(s["Dodger"], 0, true));
        Assert.Equal(1000, AiFirepower.Ship(s[Mandate], 2, true));       // w = 20 - 10
        Assert.Equal(0, AiFirepower.Ship(s[Mandate], 0, false));         // under construction
        foreach (var f in new[] { "Swift", "Needle", "Gleaner", "Sickle", "Winnower" }) Assert.Equal(199, AiFirepower.FighterValue(s[f], 12));
        foreach (var f in new[] { "Plough", "Thresher" }) Assert.Equal(574, AiFirepower.FighterValue(s[f], 12));
        Assert.Equal(628, AiFirepower.FighterValue(s["Maul"], 12));
        Assert.Equal(287, AiFirepower.Squadron(s["Plough"], 12, 6, true));
        // Mandate at half hull: 2000 (hull is not in it); Mon Cal carrying 6 of 12 Ploughs: 1800 + 287
        var w = new BattleWorld();
        int mandate = w.Fleet(Side.Hand, "B", Mandate, 1);
        w.Obj(w.F(mandate).Ships[0]).Hull = 1375;
        Assert.Equal(2000, w.Battles.FleetFirepower(w.F(mandate)));
        int mc = w.Fleets.AddFleet(Side.Collective, "MC", new[] { Ark }, "B", new[] { "Plough" });
        w.Game.State.Economy.Objects.Values.Single(o => o.Class == "Plough").CraftLost = 6;
        Assert.Equal(2087, w.Battles.FleetFirepower(w.F(mc)));
    }

    // B6a-1: A = 1200, B = 2 x (1800 + 199) = 3998, threshold 1200 / 3998
    [Fact]
    public void B6a_1_OwnWorldWithPlanetSquadron()
    {
        var w = World1();
        Assert.Equal(1200.0 / 3998.0, w.Battles.AiThreshold("B", Side.Hand), 6);
        Assert.Equal(0.30015, w.Battles.AiThreshold("B", Side.Hand), 5);
    }

    // B6a-2 and B6a-3: no planet squadron, or a world the AI does not hold: 0.9; the AI jumps at its first retreat test, after firing once
    [Fact]
    public void B6a_2_3_NoPlanetSquadronOrNotHeld()
    {
        Assert.Equal(0.9, World1(planetSquadron: false).Battles.AiThreshold("B", Side.Hand));
        var w = World1(owned: false);
        Assert.Equal(0.9, w.Battles.AiThreshold("B", Side.Hand));
        w.Run();
        var r = w.Events<BattleEnded>().Single().Result;
        Assert.Equal(1, r.Seconds);
        Assert.Equal(BattleOutcome.Retreated, r.Hand);
    }

    // B6a-4: at Charter (or an HQ world) held with a planet squadron: 0, it fights on (the data value names the system)
    [Fact]
    public void B6a_4_CharterFightsToTheEnd()
    {
        var w = World1(p: new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, HandIsAi = true, CharterSystem = "B" });
        Assert.Equal(0.0, w.Battles.AiThreshold("B", Side.Hand));
        w.Run();
        Assert.NotEqual(1, w.Events<BattleEnded>().Single().Result.Seconds);
    }

    // B6a-5 and B6a-6: r above the cap (2 Mandate vs 1 Mon Cal: 4000 / 1800) or no enemy fleet (B = 0: r = A = 1200): 0.9
    [Fact]
    public void B6a_5_6_CappedAt09()
    {
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, HandIsAi = true });
        w.Fleets.SetOwner("B", Side.Hand);
        w.Eco.AddObject(Side.Hand, "Gleaner", "B");
        w.Fleet(Side.Hand, "B", Mandate, 2);
        Assert.Equal(0.9, w.Battles.AiThreshold("B", Side.Hand)); // B = 0
        w.Fleet(Side.Collective, "B", Ark, 1);
        Assert.Equal(0.9, w.Battles.AiThreshold("B", Side.Hand));
    }

    // B6a-8: a human side keeps B3's Fight / Retreat; AiMatch-style controllers set both sides; Fixed gives the cap
    [Fact]
    public void B6a_8_HumanSideAndControllers()
    {
        var w = World1(owned: false);
        Assert.False(w.Battles.IsAi(Side.Collective));
        Assert.Equal(OnContactMode.Fight, w.Battles.OnContactOf(Side.Collective));
        w.Run();
        Assert.NotEqual(BattleOutcome.Retreated, w.Events<BattleEnded>().Single().Result.Collective); // Fight 0.0: the Collective stay
        var v = World1(p: new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, AiRetreat = AiRetreatRule.Fixed });
        v.Battles.SetControllers(handAi: true, collectiveAi: true);
        Assert.True(v.Battles.IsAi(Side.Collective) && v.Battles.IsAi(Side.Hand));
        Assert.Equal(0.9, v.Battles.AiThreshold("B", Side.Hand));
    }
}
