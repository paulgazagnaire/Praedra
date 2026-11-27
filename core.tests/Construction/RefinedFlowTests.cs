using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Construction;

// m2-construction §Build time, m2-mines-refineries-pools §Pools: the screen's money numbers (QA's UI game, 2026-11-27, item 2). RefinedFlow
// reads the state and changes nothing. (RF3, the count of the last 30 days, is gone with it: QA-131.)
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

    // m2-construction §Build time RF8 (UI review 38 P2 2): each yard set is listed, idle ones with the use they would have; the busy ones
    // sum to Use, the list is in world order, and a world in uprising leaves it.
    [Fact]
    public void RF8_EachYardSetIsListedBusyOrIdle()
    {
        var (h, yard, n) = HandShipyard();
        var idle = h.Construction.RefinedUseBySet(Side.Hand).Single(x => x.System == yard && x.Family == YardFamily.Shipyard);
        Assert.False(idle.Busy);
        Assert.Equal(30 * n / 4, idle.Use);
        h.Construction.SubmitBuild(Side.Hand, yard, YardFamily.Shipyard, "Mandate", 1, byAdvisor: true);
        h.Game.Step();
        var sets = RefinedFlow.UseBySet(h, Side.Hand);
        var busy = sets.Single(x => x.System == yard && x.Family == YardFamily.Shipyard);
        Assert.True(busy.Busy && busy.ByAdvisor);
        Assert.Equal(idle.Use, busy.Use);
        var use = RefinedFlow.Use(h, Side.Hand);
        Assert.Equal(use.Player, sets.Where(x => x.Busy && !x.ByAdvisor).Sum(x => x.Use));
        Assert.Equal(use.Advisor, sets.Where(x => x.Busy && x.ByAdvisor).Sum(x => x.Use));
        Assert.Equal(sets.OrderBy(x => x.System, StringComparer.Ordinal).ThenBy(x => x.Family).ToList(), sets);
        var was = h.Construction.IsInUprising;
        h.Construction.IsInUprising = sys => sys == yard || was(sys);
        Assert.DoesNotContain(RefinedFlow.UseBySet(h, Side.Hand), x => x.System == yard);
        h.Construction.IsInUprising = was;
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
        // the side's own refineries' batches that it kept (smuggled ones, either way, are not in the pace)
        int Made(Side s) => h.Game.Events.OfType<ResourceProduced>().Count(e => e.Day > from && e.Controller == s && e.PoolSide == s && e.Resource == "Refined") / 2;
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
        RefinedFlow.Use(h, Side.Hand);
        Assert.Equal(hash, h.StateHash()); // both read only
        foreach (var id in s.Economy.Objects.Values.Where(o => o.Class == "Mine" && h.Economy.OwnerOf(o) == Side.Hand).Select(o => o.Id).OrderBy(i => i).ToList())
            h.Economy.RemoveObject(id);
        h.Economy.SetPools(Side.Hand, 0, h.Economy.Refined(Side.Hand));
        Assert.Equal(0, RefinedFlow.MadePace(h, Side.Hand)); // batches smuggled in from the Collective are not the Hand's refineries (Ponytail on #422)
    }

    // m2-mines-refineries-pools §Asking for raw / serving a line RF7 (UI review 37): with the mines at about two thirds of the refineries and no raw
    // in stock, the raw holds the refineries back, the pace says so, and it stays within a tenth of the batches made over 60 days.
    [Fact]
    public void RF7_PartlyRawLimited()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        for (int d = 0; d < 30; d++) h.Game.Step();
        var s = h.Game.State;
        int refineries = s.Economy.Objects.Values.Count(o => o.Completed && o.Class == "Refinery" && h.Economy.OwnerOf(o) == Side.Hand);
        var mines = s.Economy.Objects.Values.Where(o => o.Completed && o.Class == "Mine" && h.Economy.OwnerOf(o) == Side.Hand).Select(o => o.Id).OrderBy(i => i).ToList();
        foreach (var id in mines.Skip(refineries * 2 / 3)) h.Economy.RemoveObject(id);
        h.Economy.SetPools(Side.Hand, 0, h.Economy.Refined(Side.Hand));
        for (int d = 0; d < 10; d++) h.Game.Step(); // the cycles running when the mines went settle
        Assert.True(RefinedFlow.RawShort(h, Side.Hand));
        int pace = RefinedFlow.MadePace(h, Side.Hand), from = h.Game.Day;
        for (int d = 0; d < 60; d++) h.Game.Step();
        int made = h.Game.Events.OfType<ResourceProduced>().Count(e => e.Day > from && e.Controller == Side.Hand && e.PoolSide == Side.Hand && e.Resource == "Refined") / 2;
        Assert.InRange(made, pace * 9 / 10, pace * 11 / 10);
    }
}
