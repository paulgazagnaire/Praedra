using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;

namespace Praedra.Core.Tests.Battle;

/// <summary>
/// Defensive / Elusive per fleet (Paul 2026-11-21): each fleet has its own answer to the Battle Alert; in a battle with both, the Elusive fleet leaves at
/// once and the Defensive one stays and fights. rule: m4-battle-classic §1.3 (per fleet); m3-fleets §What a fleet is. Synthetic map A-B-C, B-D.
/// </summary>
public class PerFleetOnContactTests
{
    private const string Corvette = "Dodger", Mandate = "Mandate", Interdictor = "Subpoena";

    // Hold beams off: the plain retreat (a Mandate's Hold beam would hold the leaving ships, PF2b)
    private static BattleParams Rules(BattleRuleset r, bool holdBeams = false) =>
        new() { Ruleset = r, SubsystemRolls = false, HandIsAi = true, New = new NewBattleParams { HoldBeamsHoldRetreat = holdBeams } };

    private static (BattleWorld W, int Elusive, int Defensive) Mixed(BattleRuleset r, string hand = Mandate, bool holdBeams = false, string defensive = Corvette)
    {
        var w = new BattleWorld(Rules(r, holdBeams));
        w.Fleets.SetOwner("A", Side.Collective);
        int el = w.Fleet(Side.Collective, "A", Corvette, 4), de = w.Fleet(Side.Collective, "A", defensive, defensive == Corvette ? 4 : 1);
        w.Fleet(Side.Hand, "B", Mandate, 1);
        if (hand != Mandate) w.Fleet(Side.Hand, "B", hand, 1);
        w.Battles.SubmitSetFleetOnContact(Side.Collective, el, OnContactMode.Retreat);
        w.Fleets.SubmitMove(Side.Collective, el, "B");
        w.Fleets.SubmitMove(Side.Collective, de, "B");
        w.Run(40);
        return (w, el, de);
    }

    // m4-battle-classic PF1: Classic, one battle with an Elusive and a Defensive fleet: the Elusive one leaves in the side's first step, the other fights on
    [Fact]
    public void PF1_ClassicElusiveLeavesAndDefensiveFights()
    {
        var (w, el, de) = Mixed(BattleRuleset.Classic);
        Assert.Equal(OnContactMode.Fight, w.Events<BattleChoice>().Single(c => c.Side == Side.Collective).Mode);
        var first = w.Events<FleetRetreated>().First();
        Assert.Equal(el, first.Fleet);
        Assert.Equal("A", w.F(el).At);
        var end = w.Events<BattleEnded>().Single().Result;
        Assert.True(end.Seconds > 1, $"the battle went on after the Elusive fleet left ({end.Seconds} s)");
        Assert.Equal(4, w.F(el).Ships.Count); // it left before the Mandate's first kill
        Assert.True(end.CollectiveLosses > 0, "the Defensive fleet fought and lost ships");
    }

    // m4-battle-classic PF2: the same under the New battle: the Elusive fleet jumps at second 0 (gone at 5), the Defensive one fights on. Its own
    // Mandate, not 4 Corvettes: under New the Corvettes alone would leave at once by the morale rule (0.33, m4-battle-new §11.2), as any Fight side does
    [Fact]
    public void PF2_NewElusiveLeavesAndDefensiveFights()
    {
        var (w, el, _) = Mixed(BattleRuleset.New, defensive: Mandate);
        Assert.Equal("A", w.F(el).At);
        Assert.Equal(4, w.F(el).Ships.Count);
        var end = w.Events<BattleEnded>().Single().Result;
        Assert.True(end.Seconds > 5, $"the Defensive fleet fought on ({end.Seconds} s)");
    }

    // m4-battle-classic PF2b: New with Hold beams (DEFAULT on): the Mandate's Hold beam holds a leaving Elusive ship as it holds a retreating side's
    [Fact]
    public void PF2b_AHoldBeamHoldsAnElusiveShip()
    {
        var (w, _, _) = Mixed(BattleRuleset.New, holdBeams: true);
        var held = w.Events<RetreatHeldByHoldBeam>();
        Assert.NotEmpty(held);
        // QA-119: a held Elusive ship stays held when its side's own retreat runs the same second; it is reported once, not twice
        Assert.All(held.GroupBy(h => (h.Ship, h.Second)), g => Assert.Single(g));
    }

    // m4-battle-classic PF2c (QA round 201 note 2): under New, Elusive squadrons with a hyperdrive leave at second 0 even with no Elusive ship; the Ark fights on
    [Fact]
    public void PF2c_NewElusiveSquadronsLeaveAlone()
    {
        var (r, u) = NewBattleTests.Fight(NewBattleTests.NewNoHoldBeams, new[] { ("Ark", 1), ("Swift", 3) }, new[] { (Mandate, 1) },
            prepare: us => { foreach (var x in us.Where(x => x.Side == Side.Collective && !x.IsCapital)) x.Elusive = true; });
        Assert.All(u.Where(x => x.Side == Side.Collective && !x.IsCapital), x => Assert.True(x.Left && x.JumpedAlone));
        Assert.False(u.Single(x => x.Side == Side.Collective && x.IsCapital).Left && r.Seconds <= 5, "the Ark did not leave with them");
    }

    // m4-battle-classic PF3: an enemy gravity well holds the Elusive fleet too: the refusal is reported and it fights on
    [Theory]
    [InlineData(BattleRuleset.Classic)]
    [InlineData(BattleRuleset.New)]
    public void PF3_GravityWellKeepsTheElusiveFleet(BattleRuleset r)
    {
        var (w, el, _) = Mixed(r, Interdictor);
        Assert.Equal(RetreatRefusal.GravityWell, w.Events<RetreatRefused>().First().Reason);
        Assert.DoesNotContain(w.Events<FleetRetreated>(), e => e.Fleet == el);
    }

    // m4-battle-classic PF4: every fleet of the side Elusive is the side's Retreat (BattleChoice Retreat, the whole side leaves in second 1)
    [Fact]
    public void PF4_AllElusiveIsTheSideRetreat()
    {
        var w = new BattleWorld(Rules(BattleRuleset.Classic));
        w.Fleets.SetOwner("A", Side.Collective);
        int a = w.Fleet(Side.Collective, "A", Corvette, 2), b = w.Fleet(Side.Collective, "A", Corvette, 2);
        w.Fleet(Side.Hand, "B", Mandate, 1);
        w.Battles.SubmitSetFleetOnContact(Side.Collective, a, OnContactMode.Retreat);
        w.Battles.SubmitSetFleetOnContact(Side.Collective, b, OnContactMode.Retreat);
        w.Fleets.SubmitMove(Side.Collective, a, "B");
        w.Fleets.SubmitMove(Side.Collective, b, "B");
        w.Run(40);
        Assert.Equal(OnContactMode.Retreat, w.Events<BattleChoice>().Single(c => c.Side == Side.Collective).Mode);
        Assert.Equal(1, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.Equal(2, w.Events<FleetRetreated>().Count);
    }

    // m4-battle-classic PF5: SetFleetOnContact is accepted for an own fleet and refused UnknownFleet, NotYours, AiSide; the setting is saved
    [Fact]
    public void PF5_TheOrderAndItsRefusals()
    {
        var w = new BattleWorld(Rules(BattleRuleset.Classic));
        int mine = w.Fleet(Side.Collective, "A", Corvette, 1), theirs = w.Fleet(Side.Hand, "C", Mandate, 1);
        long ok = w.Battles.SubmitSetFleetOnContact(Side.Collective, mine, OnContactMode.Retreat);
        long unknown = w.Battles.SubmitSetFleetOnContact(Side.Collective, 99999, OnContactMode.Retreat);
        long notYours = w.Battles.SubmitSetFleetOnContact(Side.Collective, theirs, OnContactMode.Retreat);
        long ai = w.Battles.SubmitSetFleetOnContact(Side.Hand, theirs, OnContactMode.Retreat);
        w.Run();
        string? Why(long s) => w.Events<OrderRejected>().SingleOrDefault(r => r.Sequence == s)?.Reason;
        Assert.Null(Why(ok));
        Assert.Equal(RejectReason.UnknownFleet, Why(unknown));
        Assert.Equal(RejectReason.NotYours, Why(notYours));
        Assert.Equal(BattleReject.AiSide, Why(ai));
        Assert.Equal(OnContactMode.Retreat, w.Battles.OnContactOfFleet(mine));
        Assert.Equal(OnContactMode.Fight, w.Battles.OnContactOfFleet(theirs));
        Assert.Equal(OnContactMode.Retreat, GameSave.Load(GameSave.Save(w.Game)).State.Fleets[mine].OnContact);
    }

    // m3-fleets PF6: a new fleet starts Defensive; a split copies its parent's answer; a merge keeps the receiving fleet's
    [Fact]
    public void PF6_NewSplitAndMerge()
    {
        var w = new BattleWorld(Rules(BattleRuleset.Classic));
        int el = w.Fleet(Side.Collective, "A", Corvette, 3), de = w.Fleet(Side.Collective, "A", Corvette, 1);
        Assert.Equal(OnContactMode.Fight, w.F(el).OnContact);
        w.Battles.SubmitSetFleetOnContact(Side.Collective, el, OnContactMode.Retreat);
        w.Run();
        w.Fleets.SubmitSplitFleet(Side.Collective, el, new[] { w.F(el).Ships[0] });
        w.Run();
        int split = w.Events<FleetCreated>().Single(c => c.Cause == FleetCreateCause.SplitFleet).Fleet;
        Assert.Equal(OnContactMode.Retreat, w.F(split).OnContact);
        w.Fleets.SubmitMergeFleets(Side.Collective, split, de);
        w.Run();
        Assert.False(w.Game.State.Fleets.ContainsKey(split));
        Assert.Equal(OnContactMode.Fight, w.F(de).OnContact);
    }
}
