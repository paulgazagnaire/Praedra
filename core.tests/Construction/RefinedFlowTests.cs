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

    // m2-mines-refineries-pools §Cycle length RF4: the screen counts whole refinery cycles (UI review 36 on #419), so a shorter window counts
    // only its own days, and never more than the 30-day window.
    [Fact]
    public void RF4_AShorterWindowCountsOnlyItsOwnDays()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        for (int d = 0; d < 45; d++) h.Game.Step();
        foreach (int w in new[] { 21, 24, 28 })
        {
            var (made, days) = RefinedFlow.Made(h, Side.Hand, w);
            Assert.Equal(w, days);
            int expect = h.Game.Events.OfType<ResourceProduced>().Count(e => e.Day > h.Game.Day - w && e.PoolSide == Side.Hand && e.Resource == "Refined");
            Assert.Equal(expect, made);
        }
        Assert.Equal(RefinedFlow.Made(h, Side.Hand), RefinedFlow.Made(h, Side.Hand, 99)); // clamped to 30 days
        Assert.Equal(RefinedFlow.Made(h, Side.Hand), RefinedFlow.Made(h, Side.Hand, RefinedFlow.WindowDays));
    }
}
