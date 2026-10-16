using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Tests.Battle;

// The New battle inside the day Step (m4-battle-new §Rule (New); the shared plumbing is m4-battle-classic §1, §10 and m4-retreat-damage-repair §1 to §4):
// contact detection, OnContact, retreat destinations, write-back, FoughtThisStep and events work as for Classic, with BattleParams.Ruleset = New.
public class NewBattleModuleTests
{
    private const string Corvette = "Corellian Corvette", Isd = "Imperial Star Destroyer", Interdictor = "Interdictor Cruiser", MonCal = "Mon Calamari Cruiser";

    // tractors: m4-retreat-damage-repair §6 (DEFAULT on). Tests of the base retreat rules that an ISD's tractor would change pass false.
    private static BattleWorld World(bool rolls = true, ulong seed = 1, RetreatMode mode = RetreatMode.LaneAdjacent, bool tractors = true) =>
        new(new BattleParams { Ruleset = BattleRuleset.New, SubsystemRolls = rolls, RetreatMode = mode, New = new NewBattleParams { TractorBeamsHoldRetreat = tractors } }, seed);

    // m4-retreat-damage-repair RT1 under New: the corvettes are flagged at once (strength 0), jump for five seconds and land at the system they came from
    // (tractors off: with them the ISD holds one corvette, see TB9)
    [Fact]
    public void NM1_CorvettesRetreatToTheSystemTheyCameFrom()
    {
        var w = World(tractors: false);
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "A", Corvette, 4);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Fleets.SubmitMove(Side.Alliance, rebel, "B");
        w.Run(40);
        var re = w.Events<FleetRetreated>().Single();
        Assert.Equal(("B", "A"), (re.From, re.To));
        Assert.Equal("A", w.F(rebel).At);
        Assert.Equal("B", w.F(rebel).PreviousSystem);
        var end = w.Events<BattleEnded>().Single().Result;
        Assert.InRange(end.Seconds, 5, 6);
        Assert.Equal(BattleOutcome.Retreated, end.Alliance);
        Assert.Equal(BattleOutcome.Victorious, end.Empire);
        Assert.Equal(2750, w.Obj(w.F(imp).Ships[0]).Hull);
    }

    // m4-retreat-damage-repair RT3 under New: no destination, the corvettes fight on and are destroyed (by the ISD or the stalemate), the ISD is untouched
    [Fact]
    public void NM2_NoDestinationFightsOnAndDies()
    {
        var w = World();
        foreach (var s in new[] { "A", "C", "D" }) w.Fleets.SetOwner(s, Side.Empire);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        int imp = w.Fleet(Side.Empire, "B", Isd, 1);
        w.Run();
        Assert.Equal(RetreatRefusal.NoDestination, w.Events<RetreatRefused>().Single().Reason);
        Assert.False(w.Game.State.Fleets.ContainsKey(rebel));
        Assert.Equal(BattleOutcome.Annihilated, w.Events<BattleEnded>().Single().Result.Alliance);
        Assert.Equal(4, w.Events<UnitDestroyed>().Count);
        Assert.Equal(2750, w.Obj(w.F(imp).Ships[0]).Hull);
    }

    // m4-retreat-damage-repair RT4 under New: a live Interdictor's gravity well refuses the retreat
    [Fact]
    public void NM3_GravityWellRefusesTheRetreat()
    {
        var w = World();
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 4);
        w.Fleets.AddFleet(Side.Empire, "imp", new[] { Isd, Interdictor }, "B");
        w.Run();
        Assert.Equal(RetreatRefusal.GravityWell, w.Events<RetreatRefused>().Single().Reason);
        Assert.False(w.Game.State.Fleets.ContainsKey(rebel));
        Assert.Equal(4, w.Events<UnitDestroyed>().Count);
    }

    // m4-battle-new §3.6 and m4-battle-classic BC16: one draw of the game generator per battle (the seed), systems in name order
    [Fact]
    public void NM4_OneSeedDrawPerBattleInNameOrder()
    {
        var w = World();
        w.Fleet(Side.Alliance, "C", Corvette, 12); w.Fleet(Side.Empire, "C", Isd, 1);
        w.Fleet(Side.Alliance, "B", Corvette, 12); w.Fleet(Side.Empire, "B", Isd, 1);
        long before = w.Game.Rng.Draws;
        w.Run();
        Assert.Equal(before + 2, w.Game.Rng.Draws);
        Assert.Equal(new[] { "B", "C" }, w.Events<BattleStarted>().Select(b => b.System).ToArray());
    }

    // m4-battle-new §13 and m4-retreat-damage-repair §4: end hull and counters are written back (hull max(1, round), counters as they stand), dead ships leave the game, shields are not kept
    [Fact]
    public void NM5_WriteBack()
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var w = World(seed: seed);
            int mc = w.Fleet(Side.Alliance, "B", MonCal, 1);
            int imp = w.Fleet(Side.Empire, "B", Isd, 1);
            int mcId = w.F(mc).Ships[0], impId = w.F(imp).Ships[0];
            w.Run();
            var end = w.Events<BattleEnded>().Single().Result;
            foreach (var (id, max, side) in new[] { (mcId, 2400, mc), (impId, 2750, imp) })
            {
                bool alive = w.Game.State.Economy.Objects.ContainsKey(id);
                bool destroyed = w.Events<UnitDestroyed>().Any(d => d.Unit == id);
                Assert.True(alive != destroyed || (alive && !destroyed));
                if (!alive) continue;
                var o = w.Obj(id);
                Assert.InRange(o.Hull, 1, max);
                Assert.All(o.Hits!, h => Assert.InRange(h, 0, 15));
            }
            Assert.True(end.Alliance != BattleOutcome.Indecisive || end.Empire != BattleOutcome.Indecisive || end.Seconds > 0);
        }
    }

    // m4-retreat-damage-repair §5 (no repair on a battle day) and m4-battle-classic §10: FoughtThisStep is true only inside the battle Step
    [Fact]
    public void NM6_FoughtThisStepCoversEveryUnitOfTheBattle()
    {
        var w = World();
        int a = w.Fleet(Side.Alliance, "B", MonCal, 1), e = w.Fleet(Side.Empire, "B", Isd, 1);
        int aId = w.F(a).Ships[0], eId = w.F(e).Ships[0];
        bool inside = false;
        w.Game.RegisterHook("6.8", _ => inside = w.Battles.FoughtThisStep(aId) && w.Battles.FoughtThisStep(eId));
        w.Run();
        Assert.True(inside);
        Assert.False(w.Battles.FoughtThisStep(aId));
    }

    // m4-battle-new §10: carried squadrons stay aboard their carrier after the fight
    [Fact]
    public void NM7_CarriedSquadronsStayAboardTheirCarrier()
    {
        var w = World(tractors: false); // tractors off: a held corvette would fight on and could shoot the TIEs after launch
        w.Fleets.SetOwner("A", Side.Alliance);
        w.Fleet(Side.Alliance, "B", Corvette, 4);
        int imp = w.Fleets.AddFleet(Side.Empire, "imp", new[] { Isd }, "B", new[] { "TIE Fighter", "TIE Bomber" });
        int isdId = w.F(imp).Ships[0];
        var squads = w.Game.State.Economy.Objects.Values.Where(o => o.Category == ObjectCategory.Fighter && o.AboardShip == isdId).Select(o => o.Id).ToList();
        Assert.Equal(2, squads.Count);
        w.Run();
        Assert.All(squads, id => Assert.Equal(isdId, w.Obj(id).AboardShip));
    }

    // m4-battle-new §3.6 / m1-clock rule 9: same game, same seed, same state hash after a New battle day
    [Fact]
    public void NM8_SameGameSameHash()
    {
        string Run()
        {
            var w = World(seed: 5);
            w.Fleet(Side.Alliance, "B", MonCal, 2); w.Fleet(Side.Empire, "B", Isd, 2);
            w.Run(3);
            return GameSave.StateHash(w.Game);
        }
        Assert.Equal(Run(), Run());
    }

    // m4-battle-classic §1.3 under New: OnContact Retreat leaves at second 0 (hittable for the five seconds of the jump)
    [Fact]
    public void NM9_OnContactRetreat()
    {
        var w = World(rolls: false, tractors: false); // tractors off: the base alert retreat (5 s)
        w.Fleets.SetOwner("A", Side.Alliance);
        int rebel = w.Fleet(Side.Alliance, "B", Corvette, 12);
        w.Fleet(Side.Empire, "B", Isd, 1);
        w.Battles.SubmitSetOnContact(Side.Alliance, OnContactMode.Retreat);
        w.Run();
        Assert.Equal("D", w.F(rebel).At); // D is nearer than A
        Assert.Equal(5, w.Events<BattleEnded>().Single().Result.Seconds);
        Assert.Equal(BattleOutcome.Retreated, w.Events<BattleEnded>().Single().Result.Alliance);
    }

    // the Classic ruleset is still the DEFAULT
    [Fact]
    public void ClassicIsTheDefaultRuleset() => Assert.Equal(BattleRuleset.Classic, new BattleParams().Ruleset);
}
