using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Construction;

// m2-construction §Build time, m2-mines-refineries-pools §Pools: the screen's money numbers (QA's UI game, 2026-11-27, item 2). RefinedFlow
// reads the state and the day's events and changes nothing.
public class RefinedFlowTests
{
    private static (GameHost Host, string Yard, int Yards) HandShipyard()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        var s = h.Game.State;
        var yards = s.Economy.Objects.Values
            .Where(o => o.Completed && o.System != null && o.Class == "Orbital Shipyard" && s.SystemOwners.TryGetValue(o.System, out var w) && w == Side.Hand)
            .GroupBy(o => o.System!).OrderBy(g => g.Key, StringComparer.Ordinal)
            .First(g => h.Construction.ProjectAt(g.Key, YardFamily.Shipyard) == null);
        return (h, yards.Key, yards.Count());
    }

    [Fact]
    public void RF1_APlayerShipOrderUsesOneRefinedPerCyclePerYard()
    {
        var (h, yard, n) = HandShipyard();
        var before = RefinedFlow.Use(h, Side.Hand);
        h.Construction.SubmitBuild(Side.Hand, yard, YardFamily.Shipyard, "Mandate", 1);
        h.Game.Step();
        var after = RefinedFlow.Use(h, Side.Hand);
        Assert.Equal(before.Player + 30 * n / 4, after.Player); // standard shipyards: 1 refined per 4-day cycle each
        Assert.Equal(before.Advisor, after.Advisor);
    }

    [Fact]
    public void RF2_AnAdvisorOrderCountsAsHers()
    {
        var (h, yard, n) = HandShipyard();
        var before = RefinedFlow.Use(h, Side.Hand);
        h.Construction.SubmitBuild(Side.Hand, yard, YardFamily.Shipyard, "Mandate", 1, byAdvisor: true);
        h.Game.Step();
        var after = RefinedFlow.Use(h, Side.Hand);
        Assert.Equal(before.Player, after.Player);
        Assert.Equal(before.Advisor + 30 * n / 4, after.Advisor);
    }

    [Fact]
    public void RF3_IncomeCountsTheLast30DaysOfRefineryOutput()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        Assert.Equal((0, 0), RefinedFlow.Made(h, Side.Hand)); // nothing played yet
        for (int d = 0; d < 45; d++) h.Game.Step();
        var (made, days) = RefinedFlow.Made(h, Side.Hand);
        Assert.Equal(30, days);
        int expect = h.Game.Events.OfType<ResourceProduced>().Count(e => e.Day > h.Game.Day - 30 && e.PoolSide == Side.Hand && e.Resource == "Refined");
        Assert.Equal(expect, made);
        Assert.True(made > 0);
        string hash = h.StateHash();
        RefinedFlow.Made(h, Side.Hand); RefinedFlow.Use(h, Side.Hand);
        Assert.Equal(hash, h.StateHash()); // reads only
    }

    // m2-mines-refineries-pools §Cycle length RF4: the pace is what the refineries really make. Over 60 quiet days the count of batches is
    // within a tenth of the pace read at the start (QA-131: the trend mark compares it with the yards' pace, both in the present tense).
    [Fact]
    public void RF4_ThePaceMatchesWhatTheRefineriesMake()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        for (int d = 0; d < 30; d++) h.Game.Step(); // past the startup cycles
        foreach (var side in new[] { Side.Hand, Side.Collective })
        {
            int pace = RefinedFlow.MadePace(h, side);
            Assert.True(pace > 0);
        }
        int hand = RefinedFlow.MadePace(h, Side.Hand), coll = RefinedFlow.MadePace(h, Side.Collective), from = h.Game.Day;
        for (int d = 0; d < 60; d++) h.Game.Step();
        int Made(Side s) => h.Game.Events.OfType<ResourceProduced>().Count(e => e.Day > from && e.PoolSide == s && e.Resource == "Refined") / 2;
        Assert.InRange(Made(Side.Hand), hand * 9 / 10, hand * 11 / 10);
        Assert.InRange(Made(Side.Collective), coll * 9 / 10, coll * 11 / 10);
    }

    // m2-mines-refineries-pools §Blockade and uprising RF5: a refinery world in uprising leaves the pace at once, not 30 days later.
    [Fact]
    public void RF5_ASuspendedRefineryLeavesThePaceAtOnce()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        for (int d = 0; d < 10; d++) h.Game.Step();
        var s = h.Game.State;
        string world = s.Economy.Objects.Values.Where(o => o.Completed && o.Class == "Refinery" && o.System != null && s.SystemOwners.TryGetValue(o.System, out var w) && w == Side.Hand)
            .Select(o => o.System!).OrderBy(x => x, StringComparer.Ordinal).First();
        int before = RefinedFlow.MadePace(h, Side.Hand);
        var was = h.Economy.IsInUprising;
        h.Economy.IsInUprising = sys => sys == world || was(sys);
        Assert.True(RefinedFlow.MadePace(h, Side.Hand) < before);
        h.Economy.IsInUprising = was;
        Assert.Equal(before, RefinedFlow.MadePace(h, Side.Hand));
    }

    // m2-mines-refineries-pools §Asking for raw / serving a line RF6: each cycle takes one raw, so with no mines and no raw the side's own
    // refineries make nothing; and the pace reads only.
    [Fact]
    public void RF6_NoRawNoRefined()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        for (int d = 0; d < 10; d++) h.Game.Step();
        var s = h.Game.State;
        string hash = h.StateHash();
        Assert.True(RefinedFlow.MadePace(h, Side.Hand) > 0);
        Assert.Equal(hash, h.StateHash()); // reads only
        foreach (var id in s.Economy.Objects.Values.Where(o => o.Class == "Mine" && h.Economy.OwnerOf(o) == Side.Hand).Select(o => o.Id).OrderBy(i => i).ToList())
            h.Economy.RemoveObject(id);
        h.Economy.SetPools(Side.Hand, 0, h.Economy.Refined(Side.Hand));
        // what is left can only come from the Collective's refineries losing batches to smuggling
        int incoming = RefinedFlow.MadePace(h, Side.Hand);
        Assert.True(incoming < 5, $"no raw: {incoming}");
    }
}
