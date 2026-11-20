using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// SimpleAiParams.SpreadConstructionYards (AI trainer 2026-11-20; Paul: a losing Collective "needs to adapt, regroup, conquer unexplored system,
// reproduce a military and fight until it's dying breath"). On seed 1 MedA the Medium Collective stacked 3 construction yards on Unbought next
// to its base's one, lost both worlds by day 600 and never built again (the game stalled at 275 Hand worlds to 8). With the switch it keeps its
// construction yards one per world. Measured (seed 1 MedA, Medium mirror, day 400): off, 3 construction yards on 2 worlds (Unbought 2); on,
// 3 on 3 worlds (Fallout, Paydirt, The Moorings).
public class SimpleAiSpreadYardsTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiSpreadYardsTests(ITestOutputHelper o) { _out = o; }

    private List<(string System, int Count)> CollectiveConstructionYards(int spread)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA });
        var p = AiLevels.For(AiLevel.Medium);
        var m = new AiMatch(host, p, p with { SpreadConstructionYards = spread });
        while (host.Game.Day < 400) m.Step();
        var eco = host.Economy;
        var yards = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Class == "Construction Yard" && o.System != null && eco.OwnerOf(o) == Side.Collective)
            .GroupBy(o => o.System!).Select(g => (g.Key, g.Count())).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        _out.WriteLine($"spread {spread}: " + string.Join(", ", yards.Select(y => $"{y.Key} {y.Item2}")));
        return yards;
    }

    [Fact]
    public void TheCollectiveKeepsItsConstructionYardsOnePerWorld()
    {
        var on = CollectiveConstructionYards(AiLevels.SpreadYards);
        Assert.True(on.Count >= AiLevels.SpreadYards, $"only {on.Count} worlds with a construction yard");
        Assert.All(on, y => Assert.Equal(1, y.Count));
        var off = CollectiveConstructionYards(0);
        Assert.Contains(off, y => y.Count > 1); // off, they stack (YardsPerSystem), as before
    }

    [Fact]
    public void TheSpreadIsOffByDefault_AndTheHandNeverReadsIt() =>
        Assert.Equal(0, new SimpleAiParams().SpreadConstructionYards);
}
