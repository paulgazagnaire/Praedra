using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// SimpleAiParams.FoundYardsBelow (AI trainer 2026-11-20; Paul: a losing side "needs to adapt, regroup, conquer unexplored system, reproduce a
// military and fight until it's dying breath", smarter only). On seed 1 MedA the Medium Collective lost its last construction yard by day 600
// and never built again (the game stalled at 275 Hand worlds to 8). With the founding rule (m2-construction §Founding a construction yard,
// GameSettings.FoundConstructionYards) a side with none left founds one on its safest world.
public class SimpleAiFoundYardsTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiFoundYardsTests(ITestOutputHelper o) { _out = o; }

    // Seed 1 MedA, Medium mirror; on day 100 every Collective construction yard, finished or still being built, is taken away (test setup), then
    // 30 days pass.
    private (int Yards, int Founding, string Where) AfterLosingTheYards(int below, bool rule = true)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedA, Settings = new GameSettings { FoundConstructionYards = rule } });
        var p = AiLevels.For(AiLevel.Medium);
        var m = new AiMatch(host, p, p with { FoundYardsBelow = below });
        while (host.Game.Day < 100) m.Step();
        var eco = host.Economy;
        bool CollectiveYard(EconObject o) => o.Category == ObjectCategory.Facility && o.Class == "Construction Yard" && o.System != null && eco.OwnerOf(o) == Side.Collective;
        foreach (var id in eco.Objects.Where(CollectiveYard).Select(o => o.Id).OrderBy(i => i).ToList()) eco.RemoveObject(id);
        while (host.Game.Day < 130) m.Step();
        var held = host.Data.Galaxy.Systems.Select(s => s.Name).Where(s => host.Fleets.OwnerOf(s) == Side.Collective).ToList();
        var founding = held.Where(s => host.Construction.ProjectAt(s, YardFamily.Construction) is { Founding: true }).OrderBy(s => s, StringComparer.Ordinal).ToList();
        int yards = eco.Objects.Count(o => CollectiveYard(o) && o.Completed);
        _out.WriteLine($"below {below} rule {rule}: {yards} finished construction yards, founding at {string.Join(", ", founding)}");
        return (yards, founding.Count, string.Join(",", founding));
    }

    [Fact]
    public void ASideWithNoConstructionYardFoundsOne_OneAtATime()
    {
        var on = AfterLosingTheYards(AiLevels.FoundBelow);
        Assert.Equal(1, on.Founding);
        var off = AfterLosingTheYards(0);
        Assert.Equal(0, off.Founding);
        var classic = AfterLosingTheYards(AiLevels.FoundBelow, rule: false); // the rule off (Classic): nothing can be founded
        Assert.Equal(0, classic.Founding);
    }

    [Fact]
    public void FoundingIsOffByDefault() => Assert.Equal(0, new SimpleAiParams().FoundYardsBelow);
}
