using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Fog;

public class GuardsSeeTeamTests
{
    private static Praedra.Core.Tests.Missions.MissionWorld World(FogParams? fog = null)
    {
        var w = new Praedra.Core.Tests.Missions.MissionWorld(fog: fog);
        w.Hold(H, Side.Collective, 100); w.Hold(T, Side.Hand, 0);
        w.Agent(Midwife, H, esp: 100);
        return w;
    }

    private static void General(Praedra.Core.Tests.Missions.MissionWorld w, int id)
    {
        Assert.Equal(Side.Hand, w.Ch.Data.Get(id).Side);
        w.Agent(id, T, lead: 50);
        Assert.Null(w.Try(s => new AssignSystemGeneralOrder(s, Side.Hand, id, T)));
    }

    // m5-fog-and-hq FE12 (§N6e; Paul 2026-11-28 "Low probability when there's only one regiment ... increases even more when a general is
    // designed on the system"): the chance from the Hand's garrison on T, 3 per regiment capped at 15, plus 10 with a free General posted.
    [Fact]
    public void FE12_TheGuardsChanceGrowsWithRegimentsAndAGeneral()
    {
        var w = World();
        Assert.Equal(0, w.M.GuardsSeeChance(Side.Hand, T));
        w.Unit(Side.Hand, ImpArmy, T);
        Assert.Equal(3, w.M.GuardsSeeChance(Side.Hand, T));
        for (int i = 0; i < 6; i++) w.Unit(Side.Hand, ImpArmy, T);
        Assert.Equal(15, w.M.GuardsSeeChance(Side.Hand, T)); // 7 regiments: capped
        w.Unit(Side.Collective, RebArmy, T); // the other side's regiments do not count
        Assert.Equal(15, w.M.GuardsSeeChance(Side.Hand, T));
        General(w, Ravilla);
        Assert.Equal(25, w.M.GuardsSeeChance(Side.Hand, T));

        var one = World();
        one.Unit(Side.Hand, ImpArmy, T);
        General(one, Ravilla);
        Assert.Equal(13, one.M.GuardsSeeChance(Side.Hand, T));
    }

    // FE12: at 100 (by data) the team that leaves for T is seen at once: an inbound sighting of each member with the group's arrival day, one
    // TeamInboundSpotted for the Hand; a Move group to T is not watched; with the switch off nothing is seen.
    [Fact]
    public void FE12_AWatchedTeamIsSeenWhenItLeaves()
    {
        var sure = new FogParams { GuardRegimentPercent = 100, GuardRegimentCapPercent = 100 };
        var w = World(sure);
        w.Unit(Side.Hand, ImpArmy, T);
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Midwife }, T);
        w.Game.Step();
        var left = Assert.Single(w.Events<MissionDeparted>());
        var seen = Assert.Single(w.Events<TeamInboundSpotted>());
        Assert.Equal(Side.Hand, seen.Viewer);
        Assert.Equal(T, seen.System);
        Assert.Equal(left.ArrivalDay, seen.ReachDay);
        Assert.Equal(new[] { Midwife }, seen.Characters.Items);
        var known = Assert.Single(w.Host.Fog.LeadersInbound(Side.Hand));
        Assert.Equal((Midwife, T, left.ArrivalDay, true), (known.Character, known.System, known.ReachDay, known.InGroup));
        Assert.Empty(w.Host.Fog.LeadersInbound(Side.Collective));

        var move = World(sure);
        move.Unit(Side.Hand, ImpArmy, T);
        Assert.Null(move.Try(s => new MoveGroupOrder(s, Side.Collective, new List<int> { Midwife }, new List<int>(), T)));
        Assert.NotEmpty(move.Events<MissionDeparted>());
        Assert.Empty(move.Events<TeamInboundSpotted>());

        var off = World(sure with { GuardsSeeInbound = false });
        off.Unit(Side.Hand, ImpArmy, T);
        off.Start(Side.Collective, MissionKind.Espionage, new[] { Midwife }, T);
        off.Game.Step();
        Assert.NotEmpty(off.Events<MissionDeparted>());
        Assert.Empty(off.Events<TeamInboundSpotted>());
        Assert.Empty(off.Host.Fog.LeadersInbound(Side.Hand));
    }
}
