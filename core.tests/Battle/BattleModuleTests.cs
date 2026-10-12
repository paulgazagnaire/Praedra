using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Tests.Battle;

// Tests of m4-battle-classic §1 and §10 and m4-retreat-damage-repair §1 to §4 inside the day Step (synthetic map A-B-C, B-D).
public class BattleModuleTests
{
    private const string Corvette = "Corellian Corvette", Isd = "Imperial Star Destroyer", Interdictor = "Interdictor Cruiser";

    // m4-retreat-damage-repair RT1
    [Fact]
    public void RT1_CorvettesRetreatToTheSystemTheyCameFrom()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "A", Corvette, 4);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Fleets.SubmitMove(Side.Alliance, rebel, "B");
        w.Run(40);
        var re = w.Events<FleetRetreated>().Single();
        Assert.Equal(("B", "A"), (re.From, re.To));
        var f = w.F(rebel);
        Assert.Equal("A", f.At);
        Assert.Equal("B", f.PreviousSystem);
        Assert.Null(f.Transit);
        var end = w.Events<BattleEnded>().Single().Result;
        Assert.Equal(1, end.Seconds);
        Assert.Equal(BattleOutcome.Retreated, end.Alliance);
        Assert.Equal(2750, w.Obj(w.F(imp).Ships[0]).Hull);
    }

    // m4-retreat-damage-repair RT2
    [Fact]
    public void RT2_EnemyAtThePreviousSystemSendsThemToTheNearestNeutral()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        w.F(rebel).PreviousSystem = "A";
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Fleet(Side.Empire, "A", Isd, 1);
        w.Run();
        Assert.Equal("D", w.F(rebel).At);
    }

    // m4-retreat-damage-repair RT3
    [Fact]
    public void RT3_NoDestinationFightsOnAndDies()
    {
        var w = new BattleWorld();
        foreach (var s in new[] { "A", "C", "D" }) w.Fleets.SetOwner(s, Side.Empire);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run();
        Assert.Equal(RetreatRefusal.NoDestination, w.Events<RetreatRefused>().Single().Reason);
        Assert.False(w.Game.State.Fleets.ContainsKey(rebel));
        Assert.Equal(216, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.Equal(2750, w.Obj(w.F(imp).Ships[0]).Hull);
    }

    // m4-retreat-damage-repair RT4
    [Fact]
    public void RT4_GravityWellRefusesTheRetreat()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        w.Fleets.AddFleet(Side.Empire, "imp", new[] { Isd, Interdictor }, "B");
        w.Run();
        Assert.Equal(RetreatRefusal.GravityWell, w.Events<RetreatRefused>().Single().Reason);
        Assert.Equal(176, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.False(w.Game.State.Fleets.ContainsKey(rebel));
        Assert.Equal(4, w.Events<UnitDestroyed>().Count);
    }

    // m4-retreat-damage-repair RT15
    [Fact]
    public void RT15_NoRetreatModeActsLikeRT3()
    {
        var w = new BattleWorld(new BattleParams { SubsystemRolls = false, RetreatMode = RetreatMode.NoRetreat });
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run();
        Assert.Equal(RetreatRefusal.NoDestination, w.Events<RetreatRefused>().Single().Reason);
        Assert.False(w.Game.State.Fleets.ContainsKey(rebel));
    }

    // m4-retreat-damage-repair RT16
    [Fact]
    public void RT16_NearestLandsTwoLanesAway()
    {
        var g = TestData.Synthetic(new[] { ("A", 0, 0), ("B", 100, 0), ("C", 200, 0), ("D", 100, 60), ("E", 300, 0) },
            new[] { ("A", "B"), ("B", "C"), ("B", "D"), ("C", "E") });
        var w = new BattleWorld(new BattleParams { SubsystemRolls = false, RetreatMode = RetreatMode.Nearest }, galaxy: g);
        w.Fleets.SetOwner("A", Side.Empire); w.Fleets.SetOwner("C", Side.Empire); w.Fleets.SetOwner("D", Side.Empire);
        w.Fleets.SetOwner("E", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run();
        Assert.Equal("E", w.F(rebel).At);
    }

    // m4-retreat-damage-repair RT17
    [Fact]
    public void RT17_BothRetreatToTheSameNeutralAndMeetNextStep()
    {
        var w = new BattleWorld(new BattleParams { SubsystemRolls = false });
        w.Fleets.SetOwner("A", Side.Alliance);
        w.Fleets.SetOwner("C", Side.Empire);
        // unarmed fleets: both flagged at t = 1, both can leave; D (shortest lane, neutral) is the choice of both
        int a = w.Fleets.AddFleet(Side.Alliance, "a", new[] { "Galleon" }, "B");
        int e = w.Fleets.AddFleet(Side.Empire, "e", new[] { "Bulk Transport" }, "B");
        w.Run();
        Assert.Equal("D", w.F(a).At);
        Assert.Equal("D", w.F(e).At);
        Assert.Single(w.Events<BattleEnded>());
        w.Run();
        Assert.Single(w.Events<Contact>(), c => c.System == "D");
        Assert.Equal(2, w.Events<BattleEnded>().Count);
    }

    // m4-battle-classic BC16
    [Fact]
    public void BC16_OneSeedDrawPerBattleInNameOrder()
    {
        var w = new BattleWorld();
        w.Fleet(Side.Alliance, "C", Corvette, 12); w.Fleet(Side.Empire, "C", Isd, 1);
        w.Fleet(Side.Alliance, "B", Corvette, 12); w.Fleet(Side.Empire, "B", Isd, 1);
        long before = w.Game.Rng.Draws;
        w.Run();
        Assert.Equal(before + 2, w.Game.Rng.Draws);
        Assert.Equal(new[] { "B", "C" }, w.Events<BattleStarted>().Select(b => b.System).ToArray());
    }

    // m4-battle-classic BC18 and §1.3
    [Fact]
    public void BC18_SetOnContactRetreatLeavesAtSecondZero()
    {
        var w = new BattleWorld(new BattleParams { SubsystemRolls = false, EmpireIsAi = true });
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 12);
        w.Fleet(Side.Empire, "B", Isd, 1);
        long s1 = w.Battles.SubmitSetOnContact(Side.Alliance, OnContactMode.Retreat);
        long s2 = w.Battles.SubmitSetOnContact(Side.Empire, OnContactMode.Retreat);
        long draws = w.Game.Rng.Draws;
        w.Run();
        Assert.Equal("AiSide", w.Events<OrderRejected>().Single(r => r.Sequence == s2).Reason);
        Assert.Contains(w.Events<OrderAccepted>(), a => a.Sequence == s1);
        Assert.Equal(0, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.Equal("D", w.F(rebel).At);
        Assert.Equal(draws + 1, w.Game.Rng.Draws); // only the seed
        var choices = w.Events<BattleChoice>();
        Assert.Equal(OnContactMode.Retreat, choices.Single(c => c.Side == Side.Alliance).Mode);
        Assert.Equal(OnContactMode.Simulate, choices.Single(c => c.Side == Side.Empire).Mode);
    }

    // m4-battle-classic §1.3: OnContact is saved and hashed
    [Fact]
    public void OnContactIsSavedAndHashed()
    {
        var w = new BattleWorld();
        string h0 = GameSave.StateHash(w.Game);
        w.Battles.SubmitSetOnContact(Side.Alliance, OnContactMode.Retreat);
        w.Run();
        Assert.NotEqual(h0, GameSave.StateHash(w.Game));
        var g = GameSave.Load(GameSave.Save(w.Game));
        Assert.Equal((int)OnContactMode.Retreat, g.State.Battle.AllianceOnContact);
        Assert.Equal(GameSave.StateHash(w.Game), GameSave.StateHash(g));
    }

    // m4-battle-classic BC20 / m4-retreat-damage-repair RT18: write-back of hull and hits; shield is not stored
    [Fact]
    public void BC20_WriteBackKeepsHullAndHitsNotShield()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Empire);
        int alli = w.Fleet(Side.Alliance, "B", Corvette, 30);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        int isdId = w.F(imp).Ships[0];
        w.Run();
        Assert.Equal(1790, w.Obj(isdId).Hull); // 1789.95 rounds half up
        Assert.All(w.Obj(isdId).Hits!, h => Assert.Equal(0, h));
        Assert.Equal("D", w.F(imp).At); // the ISD retreated
        Assert.Equal("B", w.F(alli).At);
    }

    // m4-retreat-damage-repair §5 hook: objects that fought this Step
    [Fact]
    public void FoughtThisStepIsTrueOnlyInsideTheBattleStep()
    {
        var w = new BattleWorld();
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Fleet(Side.Alliance, "B", Corvette, 12);
        int isdId = w.F(imp).Ships[0];
        bool inside = false;
        w.Game.RegisterHook("6.8", _ => inside = w.Battles.FoughtThisStep(isdId));
        Assert.False(w.Battles.FoughtThisStep(isdId));
        w.Run();
        Assert.True(inside);
        Assert.False(w.Battles.FoughtThisStep(isdId)); // between Steps: false
        Assert.Equal(GameSave.StateHash(w.Game), GameSave.StateHash(GameSave.Load(GameSave.Save(w.Game)))); // saved state consistent
    }

    // m4-battle-classic §1.1: based squadrons make a side present
    [Fact]
    public void BasedSquadronsMakeASidePresent()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("B", Side.Empire);
        w.Eco.AddObject(Side.Empire, "TIE Fighter", "B");
        w.Fleet(Side.Alliance, "B", "Nebulon-B Frigate", 1);
        w.Run();
        Assert.Single(w.Events<BattleStarted>());
        Assert.Single(w.Events<BattleEnded>());
    }

    // m4-battle-classic §1.1: no battle with only one side present
    [Fact]
    public void NoBattleWithOneSide()
    {
        var w = new BattleWorld();
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run(3);
        Assert.Empty(w.Events<BattleStarted>());
    }

    // m4-battle-classic §10: a destroyed ship takes its regiments; a fleet left empty dissolves
    [Fact]
    public void DestroyedShipTakesRegimentsAndFleetDissolves()
    {
        var w = new BattleWorld(new BattleParams { SubsystemRolls = false, RetreatMode = RetreatMode.NoRetreat });
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 2);
        int reg = w.Eco.AddObject(Side.Alliance, "Alliance Army Regiment", null);
        w.Obj(reg).AboardShip = w.F(rebel).Ships[0];
        w.Fleets.AddFleet(Side.Empire, "imp", new[] { Isd }, "B", new[] { "TIE Fighter" });
        w.Run();
        Assert.False(w.Game.State.Fleets.ContainsKey(rebel));
        Assert.False(w.Game.State.Economy.Objects.ContainsKey(reg));
        Assert.Contains(w.Events<FleetDissolved>(), d => d.Fleet == rebel);
        Assert.Contains(w.Events<UnitDestroyed>(), d => d.Unit == reg && d.Reason == UnitLossReason.ShipLost);
    }

    // m4-battle-classic §1.2: the blockade flag is recomputed after the battle
    [Fact]
    public void BlockadeFlagRecomputedAfterTheBattle()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("B", Side.Alliance);
        w.Fleets.SetOwner("A", Side.Alliance);
        w.Fleet(Side.Alliance, "B", Corvette, 4);
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run();
        Assert.Equal(Side.Empire, w.Game.State.Blockades["B"]); // the Empire alone at an Alliance system
    }

    // m4-retreat-damage-repair RT23 / m4-battle-classic BC15 at game level: same seed, same state hash
    [Fact]
    public void SameSeedSameStateHash()
    {
        string Play()
        {
            var w = new BattleWorld(new BattleParams(), seed: 11);
            w.Fleets.SetOwner("A", Side.Alliance);
            w.Fleet(Side.Alliance, "B", Corvette, 8); w.Fleet(Side.Empire, "B", Isd, 1);
            w.Run(3);
            return GameSave.StateHash(w.Game);
        }
        Assert.Equal(Play(), Play());
    }
}
