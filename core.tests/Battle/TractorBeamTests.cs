using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Ships;

namespace Praedra.Core.Tests.Battle;

// m4-retreat-damage-repair §6 (Hold beams hold retreat, New rules only; TB ids as in the sheet's table). Plain units with the real CSVs,
// plus one game-level test of the write-back (the held ship stays at the battle system in a fleet of its own).
public class HoldBeamTests
{
    private static IReadOnlyDictionary<string, ShipClass> Ships => NewBattleTests.Ships;
    private static readonly BattleParams New = NewBattleTests.New;
    private static readonly BattleParams NewNoHoldBeams = NewBattleTests.NewNoHoldBeams;
    private const string Corvette = "Dodger", Isd = "Mandate", MonCal = "Ark", Ssd = "Edict",
        Interdictor = "Subpoena", Bulwark = "Barricade";

    // alert indexed by (int)Side: Hand, Collective. The Collective answers Retreat: its ships try to jump at second 0.
    private static readonly OnContactMode[] CollectiveRetreats = { OnContactMode.Fight, OnContactMode.Retreat };

    private static (ClassicBattleResult R, IReadOnlyList<BattleUnit> U, IReadOnlyList<BattleLogEntry> Log) Fight(BattleParams p, (string, int)[] collective,
        (string, int)[] hand, ulong seed = 1, bool eCan = true, OnContactMode[]? alert = null, Action<IReadOnlyList<BattleUnit>>? prepare = null)
    {
        var (nb, units) = QuickBattle.PrepareNew(p, Ships, collective, hand, seed, true, eCan, alert, null, false, prepare, logEvents: true);
        var r = nb.Run();
        return (r, units, nb.Log!);
    }

    private static List<BattleUnit> Of(IReadOnlyList<BattleUnit> u, Side side, string cls) =>
        u.Where(x => x.Side == side && x.Class.Name == cls).OrderBy(x => x.ObjectId).ToList();

    // data: hold_beam_power is loaded from capital-ships.csv (ISD 3, Mon Cal 2, SSD 6, Interdictor 0, Corvette 0)
    [Fact]
    public void TB0_HoldBeamPowerIsLoadedFromTheShipData()
    {
        Assert.Equal((3, 2, 6, 0, 0), (Ships[Isd].HoldBeamPower, Ships[MonCal].HoldBeamPower, Ships[Ssd].HoldBeamPower,
            Ships[Interdictor].HoldBeamPower, Ships[Corvette].HoldBeamPower));
        Assert.True(new NewBattleParams().HoldBeamsHoldRetreat); // DEFAULT on (New)
    }

    // m4-retreat-damage-repair TB1: one ISD, one corvette that would retreat: the ISD holds it, it never leaves and the ISD kills it
    [Fact]
    public void TB1_IsdHoldsACorvetteThatWouldRetreat()
    {
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var (r, u, log) = Fight(New, new[] { (Corvette, 1) }, new[] { (Isd, 1) }, seed);
            var cv = Of(u, Side.Collective, Corvette)[0]; var isd = Of(u, Side.Hand, Isd)[0];
            var held = Assert.Single(r.Steps.OfType<HoldBeamHeldStep>());
            Assert.Equal((cv, isd), (held.Ship, held.Holder));
            Assert.Contains(log, e => e.Kind == "held" && e.Unit == cv.ObjectId && e.Other == isd.ObjectId);
            Assert.False(cv.Left, $"seed {seed}");
            Assert.True(cv.Destroyed, $"seed {seed}");
            Assert.False(r.CollectiveRetreated);
            Assert.Equal(BattleOutcome.Victorious, r.HandOutcome);
            Assert.Equal(2750.0, isd.Hull); // corvette lasers never get through the ISD's shield
        }
    }

    // m4-retreat-damage-repair TB2: a retreating ship of equal or larger max hull is not held (ISD 2750 v ISD 2750; Bulwark 3000 v ISD)
    [Theory]
    [InlineData(Isd)]
    [InlineData(Bulwark)]
    public void TB2_EqualOrLargerHullIsNotHeld(string retreater)
    {
        var (r, u, _) = Fight(New, new[] { (retreater, 1) }, new[] { (Isd, 1) }, alert: CollectiveRetreats);
        Assert.Empty(r.Steps.OfType<HoldBeamHeldStep>());
        Assert.True(Of(u, Side.Collective, retreater)[0].Left);
        Assert.Equal(BattleOutcome.Retreated, r.CollectiveOutcome);
    }

    // m4-retreat-damage-repair TB3: one ISD, two corvettes retreating together: it holds only the first by unit id; the other leaves
    [Fact]
    public void TB3_OneIsdHoldsOnlyOneOfTwoCorvettes()
    {
        var (r, u, _) = Fight(New, new[] { (Corvette, 2) }, new[] { (Isd, 1) }, alert: CollectiveRetreats);
        var cv = Of(u, Side.Collective, Corvette);
        var held = Assert.Single(r.Steps.OfType<HoldBeamHeldStep>());
        Assert.Equal(cv[0], held.Ship);
        Assert.Equal(0, held.Second);
        Assert.False(cv[0].Left);
        Assert.True(cv[1].Left);
        Assert.True(r.CollectiveRetreated);
    }

    // m4-retreat-damage-repair TB4: the holder dies (an SSD at hull 1, its side cannot retreat): the held Ark tries again and jumps out
    [Fact]
    public void TB4_HolderDestroyedFreesTheHeldShip()
    {
        var (r, u, log) = Fight(New, new[] { (MonCal, 1) }, new[] { (Ssd, 1), (Corvette, 1) }, eCan: false, alert: CollectiveRetreats,
            prepare: x => x.First(q => q.Class.Name == Ssd).Hull = 1);
        var mc = Of(u, Side.Collective, MonCal)[0]; var ssd = Of(u, Side.Hand, Ssd)[0];
        var held = Assert.Single(r.Steps.OfType<HoldBeamHeldStep>());
        Assert.Equal((mc, ssd), (held.Ship, held.Holder));
        Assert.True(ssd.Destroyed);
        var kill = r.Steps.OfType<UnitDestroyedStep>().Single(s => s.Unit == ssd);
        var jump = Assert.Single(log, e => e.Kind == "jump" && e.Unit == mc.ObjectId);
        Assert.Equal("released", jump.Detail);
        Assert.True(jump.T >= kill.Second); // on its next try: step 7 of the second the holder died in (or later)
        Assert.True(mc.Left);
        Assert.Equal(BattleOutcome.Retreated, r.CollectiveOutcome);
    }

    // m4-retreat-damage-repair TB5: Classic battles are unchanged: the switch on or off gives the same Classic battle, and no hold
    [Fact]
    public void TB5_ClassicIsUnaffected()
    {
        var on = new BattleParams { Ruleset = BattleRuleset.Classic, New = new NewBattleParams { HoldBeamsHoldRetreat = true } };
        var off = new BattleParams { Ruleset = BattleRuleset.Classic, New = new NewBattleParams { HoldBeamsHoldRetreat = false } };
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var (r1, u1) = QuickBattle.Run(on, Ships, new[] { (Corvette, 4) }, new[] { (Isd, 1) }, seed);
            var (r2, u2) = QuickBattle.Run(off, Ships, new[] { (Corvette, 4) }, new[] { (Isd, 1) }, seed);
            Assert.Empty(r1.Steps.OfType<HoldBeamHeldStep>());
            Assert.True(r1.CollectiveRetreated);
            Assert.All(u1.Where(x => x.Side == Side.Collective), x => Assert.True(x.Left));
            Assert.Equal((r1.Seconds, r1.Draws, r1.CollectiveOutcome, r1.HandOutcome), (r2.Seconds, r2.Draws, r2.CollectiveOutcome, r2.HandOutcome));
            Assert.Equal(u1.Select(x => (x.Hull, x.Left)), u2.Select(x => (x.Hull, x.Left)));
        }
    }

    // m4-retreat-damage-repair TB6: New with the switch off: no hold, every corvette leaves
    [Fact]
    public void TB6_SwitchOffNoHold()
    {
        var (r, u, _) = Fight(NewNoHoldBeams, new[] { (Corvette, 2) }, new[] { (Isd, 1) }, alert: CollectiveRetreats);
        Assert.Empty(r.Steps.OfType<HoldBeamHeldStep>());
        Assert.All(Of(u, Side.Collective, Corvette), x => Assert.True(x.Left));
        Assert.Equal(BattleOutcome.Retreated, r.CollectiveOutcome);
    }

    // m4-retreat-damage-repair TB7: an Interdictor (Hold beam power 0) holds nothing by Hold beam; its gravity well still refuses the whole retreat
    [Fact]
    public void TB7_InterdictorHoldsNoShipByHoldBeamButItsGravityWellStillBlocks()
    {
        var (r, _, _) = Fight(New, new[] { (Corvette, 2) }, new[] { (Interdictor, 1) }, alert: CollectiveRetreats);
        Assert.Empty(r.Steps.OfType<HoldBeamHeldStep>());
        Assert.Contains(r.Steps.OfType<RetreatRefusedStep>(), s => s.Reason == RetreatRefusal.GravityWell);
        Assert.False(r.CollectiveRetreated);
    }

    // m4-retreat-damage-repair TB8: a holder whose Hold beam counter is not clear holds nothing
    [Fact]
    public void TB8_DamagedHoldBeamHoldsNothing()
    {
        var (r, u, _) = Fight(New, new[] { (Corvette, 1) }, new[] { (Isd, 1) }, alert: CollectiveRetreats,
            prepare: x => x.First(q => q.Class.Name == Isd).Hits[ObjectRegistry.HoldBeamCounter] = 1);
        Assert.Empty(r.Steps.OfType<HoldBeamHeldStep>());
        Assert.True(Of(u, Side.Collective, Corvette)[0].Left);
    }

    // m4-retreat-damage-repair TB9 (game level): 4 corvettes stopped by an ISD: one is held and dies, three retreat; the event names ship and holder
    [Fact]
    public void TB9_InTheGameTheHoldIsAnEventAndTheOthersRetreat()
    {
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.New });
        w.Fleets.SetOwner("A", Side.Collective);
        int rebel = w.Fleet(Side.Collective, "A", Corvette, 4);
        int imp = w.Fleet(Side.Hand, "B", Isd, 1);
        var corvettes = w.F(rebel).Ships.ToList();
        w.Fleets.SubmitMove(Side.Collective, rebel, "B");
        w.Run(40);
        var held = w.Events<RetreatHeldByHoldBeam>().Single();
        Assert.Equal((corvettes[0], w.F(imp).Ships[0], "B"), (held.Ship, held.Holder, held.System));
        Assert.Contains(w.Events<UnitDestroyed>(), d => d.Unit == corvettes[0]);
        var re = w.Events<FleetRetreated>().Single();
        Assert.Equal(corvettes.Skip(1), re.Ships.Items);
        Assert.Equal("A", w.F(rebel).At);
        Assert.Equal(2750, w.Obj(w.F(imp).Ships[0]).Hull);
    }

    // m4-retreat-damage-repair TB10 (game level): a held ship that survives the battle stays at the battle system in a fleet of its own (cause HoldBeamHeld)
    [Fact]
    public void TB10_HeldShipThatSurvivesStaysInAFleetOfItsOwn()
    {
        var w = new BattleWorld(new BattleParams { Ruleset = BattleRuleset.New, SubsystemRolls = false });
        foreach (var s in new[] { "A", "C", "D" }) w.Fleets.SetOwner(s, Side.Collective); // the Hand has nowhere to go
        int rebel = w.Fleet(Side.Collective, "B", Corvette, 2);
        int imp = w.Fleet(Side.Hand, "B", Isd, 1);
        var corvettes = w.F(rebel).Ships.ToList();
        w.Obj(w.F(imp).Ships[0]).Hits![1] = 4; // weapons out: the ISD cannot kill the held corvette
        w.Battles.SubmitSetOnContact(Side.Collective, OnContactMode.Retreat);
        w.Run();
        Assert.Equal(corvettes[0], w.Events<RetreatHeldByHoldBeam>().Single().Ship);
        var created = w.Events<FleetCreated>().Single(c => c.Cause == FleetCreateCause.HoldBeamHeld);
        Assert.Equal("B", created.System);
        Assert.Equal(new[] { corvettes[0] }, created.Members.Items);
        Assert.Equal("B", w.F(created.Fleet).At);
        Assert.Equal(new[] { corvettes[1] }, w.F(rebel).Ships.ToArray());
        Assert.NotEqual("B", w.F(rebel).At);
    }

    // m4-retreat-damage-repair TB11: the same battle twice gives the same holds, log and draws
    [Fact]
    public void TB11_Deterministic()
    {
        var (r1, u1, l1) = Fight(New, new[] { (Corvette, 6), (MonCal, 1) }, new[] { (Isd, 2) }, seed: 3, alert: CollectiveRetreats);
        var (r2, u2, l2) = Fight(New, new[] { (Corvette, 6), (MonCal, 1) }, new[] { (Isd, 2) }, seed: 3, alert: CollectiveRetreats);
        Assert.Equal(2, r1.Steps.OfType<HoldBeamHeldStep>().Count()); // each ISD holds one ship
        Assert.Equal(r1.Draws, r2.Draws);
        Assert.Equal(r1.Steps.OfType<HoldBeamHeldStep>().Select(s => (s.Ship.ObjectId, s.Holder.ObjectId, s.Second)),
            r2.Steps.OfType<HoldBeamHeldStep>().Select(s => (s.Ship.ObjectId, s.Holder.ObjectId, s.Second)));
        Assert.Equal(l1!, l2!);
        Assert.Equal(u1.Select(x => (x.Hull, x.Alive, x.Left)), u2.Select(x => (x.Hull, x.Alive, x.Left)));
    }
}
