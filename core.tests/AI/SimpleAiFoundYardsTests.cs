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
    // 30 days pass; Built: by day 190 (a founding takes FoundingDays, 40 at the yard's cost) a finished Collective construction yard stands where it
    // was founded.
    private (int Yards, int Founding, string Where, bool Built) AfterLosingTheYards(int below, bool rule = true)
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
        while (host.Game.Day < 190) m.Step();
        bool built = founding.Count == 1 && eco.Objects.Any(o => CollectiveYard(o) && o.Completed && o.System == founding[0]);
        _out.WriteLine($"below {below} rule {rule}: {yards} finished construction yards, founding at {string.Join(", ", founding)}; built by day 190 {built}");
        return (yards, founding.Count, string.Join(",", founding), built);
    }

    [Fact]
    public void ASideWithNoConstructionYardFoundsOne_OneAtATime()
    {
        var on = AfterLosingTheYards(AiLevels.FoundBelow);
        Assert.Equal(1, on.Founding);
        Assert.True(on.Built, $"the yard founded at {on.Where} was not finished by day 190");
        var off = AfterLosingTheYards(0);
        Assert.Equal(0, off.Founding);
        var classic = AfterLosingTheYards(AiLevels.FoundBelow, rule: false); // the rule off (Classic): nothing can be founded
        Assert.Equal(0, classic.Founding);
    }

    // QA-117 (QA round 199, AI trainer 2026-11-20; Paul 2026-11-19: the enemy must not be predictable): the Collective re-founded at The Moorings
    // in 7 of 8 wipe games (most worlds tie on hops from the enemy, capped at FoundSafeHops). Medium mirror, MedE, every Collective construction
    // yard taken away on day 150 (test setup), the founding site on day 160.
    private static string? FoundingSite(int seed)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
        var p = AiLevels.For(AiLevel.Medium);
        var m = new AiMatch(host, p, p);
        while (host.Game.Day < 150) m.Step();
        var eco = host.Economy;
        foreach (var id in eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Class == "Construction Yard" && o.System != null
                     && eco.OwnerOf(o) == Side.Collective).Select(o => o.Id).OrderBy(i => i).ToList())
            eco.RemoveObject(id);
        while (host.Game.Day < 160) m.Step();
        return host.Data.Galaxy.Systems.Select(s => s.Name).Where(s => host.Fleets.OwnerOf(s) == Side.Collective)
            .Where(s => host.Construction.ProjectAt(s, YardFamily.Construction) is { Founding: true }).OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault();
    }

    [Trait("Category", "Play")]
    [Fact]
    public void TheRefoundingSiteChangesFromGameToGame()
    {
        var sites = MultiSeed.Play(MultiSeed.OneToEight, seed => (Seed: seed, Site: FoundingSite(seed)));
        _out.WriteLine(string.Join(", ", sites.Select(x => $"seed {x.Seed} {x.Site ?? "-"}")));
        Assert.All(sites, x => Assert.NotNull(x.Site));
        int distinct = sites.Select(x => x.Site).Distinct().Count(), most = sites.GroupBy(x => x.Site).Max(g => g.Count());
        Assert.True(distinct >= 4 && most <= 3, $"{distinct} distinct sites, one site {most} of 8 times");
        Assert.Equal(sites.First().Site, FoundingSite(sites.First().Seed)); // the same seed, the same site
    }

    [Fact]
    public void FoundingIsOffByDefault() => Assert.Equal(0, new SimpleAiParams().FoundYardsBelow);

    // m6-advisor-sibyl §Manage Production 0a (QA-116): Sibyl founds only once the side has none left
    [Fact]
    public void SibylFoundsOnlyWithNoneLeftByDefault() => Assert.Equal(1, new SimpleAiParams().AdvisorFoundYardsBelow);
}
