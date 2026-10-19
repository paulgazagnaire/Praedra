using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;

namespace Praedra.Core.Tests.Construction;

// 2000-day headless runs of the build demo (core/Construction/BuildDemo): no crash, the bookkeeping stays consistent after every Step,
// identical StateHash on two runs, and no random number drawn by construction itself.
public class ConstructionSoakTests
{
    private static void CheckInvariants(BuildDemo d)
    {
        var c = d.Game.State.Construction;
        var eco = d.Economy;
        Assert.True(eco.Refined(Side.Empire) >= 0 && eco.Refined(Side.Alliance) >= 0);
        var seen = new HashSet<int>();
        foreach (var p in c.AllProjects())
        {
            Assert.NotEmpty(p.Units);
            foreach (var id in p.Units)
            {
                var o = eco.Find(id);
                Assert.NotNull(o);
                Assert.False(o!.Completed); // queued units are not completed
                Assert.True(seen.Add(id), "an object is in two places");
            }
            Assert.InRange(p.Progress, 0, eco.Costs.Get(eco.Find(p.Units[0])!.Class).Cost);
        }
        foreach (var dl in c.Deliveries.Values)
        {
            var o = eco.Find(dl.Object);
            Assert.NotNull(o);
            Assert.False(o!.Completed); // in delivery: not completed
            Assert.True(seen.Add(dl.Object), "an object is in two places");
            Assert.InRange(dl.Index, 0, dl.Path.Count - 1);
            if (dl.InFlight) Assert.False(dl.Held);
        }
        // every waiting yard is in exactly one line, and the line holds only waiting yards
        var lines = c.Empire.RefinedLine.Concat(c.Alliance.RefinedLine).ToList();
        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.Equal(lines.Count, eco.Objects.Count(o => o.InLine && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)));
    }

    private static string Run(ulong seed, int days, out BuildDemo d)
    {
        d = BuildDemo.Create(TestData.RepoRoot, seed);
        for (int i = 0; i < days; i++)
        {
            if (d.Game.Day == 100) d.Fleets.SubmitMove(Side.Alliance, d.Picket, d.PicketMovesTo);
            d.Game.Step();
            CheckInvariants(d);
        }
        return GameSave.StateHash(d.Game);
    }

    [Fact]
    public void TwoThousandDaysSameSeedSameHash()
    {
        string a = Run(1, 2000, out var da), b = Run(1, 2000, out _), c = Run(2, 2000, out _);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c); // the home's mines and refineries draw their first cycles
        Assert.Equal(5, da.Game.Events.OfType<UnitCompleted>().Count());
        Assert.Equal(2, da.Game.Events.OfType<DeliveryHeld>().Count(e => e.Reason == "FleetPresent"));
        Assert.Empty(da.Game.State.Construction.Deliveries);
    }

    // m2-construction §Where it runs: construction itself makes no random draw. The demo's only draws are the four first cycles of mines
    // and refineries plus the first cycle of the delivered mine (the economy's), five in all.
    [Fact]
    public void ConstructionDrawsNothing()
    {
        Run(3, 1000, out var d);
        Assert.Equal(5, d.Game.Rng.Draws);
    }
}
