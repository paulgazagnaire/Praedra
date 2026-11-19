using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Setup;
using Praedra.Core.Tests.AI;

namespace Praedra.Core.Tests.Setup;

/// <summary>
/// The Collective's base drawn per game (Paul 2026-11-19, "Per game"; GameSettings.CollectiveBaseDraw): its construction yard, starting
/// fleets at the base and base regiments go to one Collective world drawn with the game generator among The Shoal and its farreach worlds
/// with room for the yards, never the HQ. rule: m5-setup-and-victory §Starting forces on the v5 galaxy (The Collective's base is drawn per game).
/// </summary>
public class CollectiveBaseDrawTests
{
    private static string BaseOf(GameHost h) => h.Economy.Objects
        .Where(o => o.Category == ObjectCategory.Facility && o.Class == "Construction Yard"
                    && h.Game.State.SystemOwners.TryGetValue(o.System!, out var owner) && owner == Side.Collective)
        .Select(o => o.System!).Distinct().Single();

    private static Fleet FirstFleet(GameHost h, Side side) => h.Game.State.Fleets.Values.Where(f => f.Side == side).OrderBy(f => f.Id).First();

    // m5-setup-and-victory §Starting forces (Collective base drawn per game): eight games, a different base in most, each a fit world
    [Fact]
    public void TheCollectiveBaseIsDrawnPerGame()
    {
        var forces = NewGameData.Default.Forces;
        var bases = new List<string>();
        foreach (int seed in MultiSeed.OneToEight)
        {
            var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE, Settings = new GameSettings { CollectiveBaseDraw = true } });
            string b = BaseOf(h);
            Assert.Equal(Side.Collective, h.Game.State.SystemOwners[b]);
            Assert.NotEqual(h.HqSystem, b);
            Assert.True(b == forces.Str(Side.Collective, "base_system") || h.Data.Galaxy.Get(b).Group == forces.Str(Side.Collective, "base_draw_group"), b);
            // the base's fleets and regiments stand there with the yard
            Assert.Equal(b, FirstFleet(h, Side.Collective).At); // the table's first fleet (Collective Fleet@base)
            Assert.True(h.Economy.Objects.Count(o => o.Side == Side.Collective && o.Category == ObjectCategory.Regiment && o.System == b)
                        >= forces.Int(Side.Collective, "base_regiments"), b);
            bases.Add(b);
        }
        Assert.True(bases.Distinct().Count() >= 5, string.Join(", ", bases));
    }

    // m5-setup-and-victory §Starting forces (Collective base drawn per game): off, the base is always The Shoal, and the Hand's base never moves
    [Fact]
    public void WithoutTheDrawTheBaseIsTheShoal()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var off = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE, Settings = new GameSettings { CollectiveBaseDraw = false } });
            Assert.Equal("The Shoal", BaseOf(off));
            var on = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE, Settings = new GameSettings { CollectiveBaseDraw = true } });
            Assert.Equal("Charter", FirstFleet(on, Side.Hand).At); // Home Fleet@base
        }
    }
}
