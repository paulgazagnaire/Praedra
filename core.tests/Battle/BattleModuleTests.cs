using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace Praedra.Core.Tests.Battle;

// Tests of m4-battle-classic §1 and §10 and m4-retreat-damage-repair §1 to §4 inside the day Step (synthetic map A-B-C, B-D).
// Every BattleParams here sets Ruleset = Classic explicitly: these are Classic-battle tests and the DEFAULT became New (Paul, 2026-10-18).
public class BattleModuleTests
{
    private const string Corvette = "Dodger", Isd = "Mandate", Interdictor = "Subpoena";

    // m4-retreat-damage-repair RT1 (moved by B3: the old 0.33 morale rule is gone; the Corvettes answer Retreat at the alert, take the ISD's volley and leave in
    // second 1. With Fight they would leave at second 163, after three of them are destroyed, see RT1b)
    [Fact]
    public void RT1_CorvettesRetreatToTheSystemTheyCameFrom()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "A", Corvette, 4);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Battles.SubmitSetOnContact(Side.Alliance, OnContactMode.Retreat);
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

    // m4-retreat-damage-repair RT1 with Fight (B3 §10): the fleet stays until every Corvette is out-shot one by one; the last one leaves [Model]
    [Fact]
    public void RT1b_FightLeavesWhenItsStrengthIsZero()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "A", Corvette, 4);
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Fleets.SubmitMove(Side.Alliance, rebel, "B");
        w.Run(40);
        var end = w.Events<BattleEnded>().Single().Result;
        Assert.Equal(163, end.Seconds);
        Assert.Equal(BattleOutcome.Retreated, end.Alliance);
        Assert.Equal(3, end.AllianceLosses);
        Assert.Equal("A", w.F(rebel).At);
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
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, RetreatMode = RetreatMode.NoRetreat });
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
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, RetreatMode = RetreatMode.Nearest }, galaxy: g);
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
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false });
        w.Fleets.SetOwner("A", Side.Alliance);
        w.Fleets.SetOwner("C", Side.Empire);
        // unarmed fleets: both flagged at t = 1, both can leave; D (shortest lane, neutral) is the choice of both
        int a = w.Fleets.AddFleet(Side.Alliance, "a", new[] { "Bond" }, "B");
        int e = w.Fleets.AddFleet(Side.Empire, "e", new[] { "Barge" }, "B");
        w.Run();
        Assert.Equal("D", w.F(a).At);
        Assert.Equal("D", w.F(e).At);
        Assert.Single(w.Events<BattleEnded>());
        w.Run();
        Assert.Single(w.Events<Contact>(), c => c.System == "D");
        Assert.Equal(2, w.Events<BattleEnded>().Count);
    }

    // m4-battle-classic BC16 (moved by B3: with ClassicBattleRng = Classic the battles draw nothing from the game generator; with Game, one seed each, in name order)
    [Theory]
    [InlineData(ClassicBattleRng.Classic, 0)]
    [InlineData(ClassicBattleRng.Game, 2)]
    public void BC16_SeedDrawsPerBattleInNameOrder(ClassicBattleRng rng, int draws)
    {
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, BattleRng = rng });
        w.Fleet(Side.Alliance, "C", Corvette, 12); w.Fleet(Side.Empire, "C", Isd, 1);
        w.Fleet(Side.Alliance, "B", Corvette, 12); w.Fleet(Side.Empire, "B", Isd, 1);
        long before = w.Game.Rng.Draws;
        w.Run();
        Assert.Equal(before + draws, w.Game.Rng.Draws);
        Assert.Equal(new[] { "B", "C" }, w.Events<BattleStarted>().Select(b => b.System).ToArray());
    }

    // B3-27: the same battle twice gives the same result and draw count, and no game draw
    [Fact]
    public void B3_27_SameBattleTwiceNoGameDraw()
    {
        (string, long) Play()
        {
            var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic });
            w.Fleet(Side.Alliance, "B", Corvette, 12); w.Fleet(Side.Empire, "B", Isd, 1);
            long before = w.Game.Rng.Draws;
            w.Run();
            Assert.Equal(before, w.Game.Rng.Draws);
            var r = w.Events<BattleEnded>().Single().Result;
            return ($"{r.Seconds} {r.Alliance} {r.Empire} {r.AllianceLosses} {r.EmpireLosses}", w.Game.Rng.Draws);
        }
        Assert.Equal(Play(), Play());
    }

    // m4-battle-classic BC18 and §1.3 (moved by B3: Retreat is a threshold of 10000 inside the loop; the Corvettes take the AI Empire's volley and leave in
    // second 1; no game draw at all with the Classic dice)
    [Fact]
    public void BC18_SetOnContactRetreatLeavesInSecondOne()
    {
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, EmpireIsAi = true });
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 12);
        w.Fleet(Side.Empire, "B", Isd, 1);
        long s1 = w.Battles.SubmitSetOnContact(Side.Alliance, OnContactMode.Retreat);
        long s2 = w.Battles.SubmitSetOnContact(Side.Empire, OnContactMode.Retreat);
        long draws = w.Game.Rng.Draws;
        w.Run();
        Assert.Equal("AiSide", w.Events<OrderRejected>().Single(r => r.Sequence == s2).Reason);
        Assert.Contains(w.Events<OrderAccepted>(), a => a.Sequence == s1);
        Assert.Equal(1, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.Equal("D", w.F(rebel).At);
        Assert.Equal(draws, w.Game.Rng.Draws); // the Classic dice are the battle's own
        var choices = w.Events<BattleChoice>();
        Assert.Equal(OnContactMode.Retreat, choices.Single(c => c.Side == Side.Alliance).Mode);
        Assert.Equal(OnContactMode.Fight, choices.Single(c => c.Side == Side.Empire).Mode);
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

    // m4-battle-classic BC20 / m4-retreat-damage-repair RT18 (moved by B3: 30 Corvettes in 5 squads; the ISD leaves at second 25 with hull 2423.09, stored
    // truncated as 2423; was 1790 rounded half up from 1789.95) [Model]
    [Fact]
    public void BC20_WriteBackKeepsHullAndHitsNotShield()
    {
        var w = new BattleWorld();
        w.Fleets.SetOwner("A", Side.Empire);
        int alli = w.Fleet(Side.Alliance, "B", Corvette, 30);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        int isdId = w.F(imp).Ships[0];
        w.Run();
        Assert.Equal(25, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.Equal(2423, w.Obj(isdId).Hull); // 2423.09 truncated
        Assert.All(w.Obj(isdId).Hits!, h => Assert.Equal(0, h));
        Assert.Equal("D", w.F(imp).At); // the ISD retreated
        Assert.Equal("B", w.F(alli).At);
    }

    // m4-retreat-damage-repair RT18 (moved by B3: shields start full, not at the hull-scaled cap: the ISD stored at 1350 hull starts its next battle at 300)
    [Fact]
    public void RT18_NextBattleStartsWithAFullShield()
    {
        var w = new BattleWorld();
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Obj(w.F(imp).Ships[0]).Hull = 1350;
        var units = new List<BattleUnit>();
        int id = 1;
        units.AddRange(QuickBattle.Units(new BattleParams { Ruleset = BattleRuleset.Classic }, Fleets.World.Ships.Value, Side.Empire, new[] { (Isd, 1) }, ref id));
        units[0].Hull = 1350;
        _ = new ClassicBattle(new BattleParams { Ruleset = BattleRuleset.Classic }, units, null, new[] { true, true });
        Assert.Equal(300.0, units[0].Shield);
        Assert.Equal(300.0, units[0].ShieldMax);
    }

    // B3-23: write-back numbers (hull truncated, at least 1; squadron craft; the hyperdrive split; counters capped at 4)
    [Fact]
    public void B3_23_WriteBack()
    {
        var p = new BattleParams { Ruleset = BattleRuleset.Classic };
        var ships = Fleets.World.Ships.Value;
        var w = new BattleWorld(p);
        w.Fleets.SetOwner("A", Side.Alliance); w.Fleets.SetOwner("C", Side.Empire); w.Fleets.SetOwner("D", Side.Empire);
        // stored hull: trunc(max(1, hull))
        Assert.Equal(1349, (int)Math.Truncate(Math.Max(1.0, 1349.8)));
        Assert.Equal(new List<int> { 0, 0, 0, 0, 1, 1 }, BattleModule.StoredHits(p, ships[Isd], new[] { 0, 0, 0, 0, 2, 0 }));
        Assert.Equal(new List<int> { 4, 4, 0, 0, 1, 0 }, BattleModule.StoredHits(p, ships[Corvette], new[] { 5, 4, 0, 0, 1, 0 }));
        // a game battle: the Plough squadrons of a carrier-less Alliance raid; a squadron at hull 17 comes back with 2 craft (10 lost)
        Assert.Equal(2, Math.Max(1, (int)Math.Truncate(17 / p.SquadronCraftHull)));
        Assert.Equal(1, Math.Max(1, (int)Math.Truncate(0.4)));
    }

    // m4-retreat-damage-repair §4 (B3 §14): squadron losses are partial and persist as craft lost; the next battle starts the squadron short
    [Fact]
    public void SquadronCraftLostPersistsAndShortSquadronsFightShort()
    {
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic });
        w.Fleets.SetOwner("A", Side.Alliance);
        int sq = w.Eco.AddObject(Side.Alliance, "Swift", "B");
        w.Obj(sq).CraftLost = 4;
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run();
        var o = w.Game.State.Economy.Objects.GetValueOrDefault(sq);
        Assert.True(o == null || o.CraftLost >= 4); // it fought with 8 craft and lost none back
        Assert.Equal(GameSave.StateHash(w.Game), GameSave.StateHash(GameSave.Load(GameSave.Save(w.Game))));
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
        w.Eco.AddObject(Side.Empire, "Gleaner", "B");
        w.Fleet(Side.Alliance, "B", "Mender", 1);
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
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic, SubsystemRolls = false, RetreatMode = RetreatMode.NoRetreat });
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 2);
        int reg = w.Eco.AddObject(Side.Alliance, "Militia", null);
        w.Obj(reg).AboardShip = w.F(rebel).Ships[0];
        w.Fleets.AddFleet(Side.Empire, "imp", new[] { Isd }, "B", new[] { "Gleaner" });
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
            var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.Classic }, seed: 11);
            w.Fleets.SetOwner("A", Side.Alliance);
            w.Fleet(Side.Alliance, "B", Corvette, 8); w.Fleet(Side.Empire, "B", Isd, 1);
            w.Run(3);
            return GameSave.StateHash(w.Game);
        }
        Assert.Equal(Play(), Play());
    }
}
