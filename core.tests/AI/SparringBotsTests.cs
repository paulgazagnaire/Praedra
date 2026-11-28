using Praedra.Core.AI;

namespace Praedra.Core.Tests.AI;

// The sparring bots (AI trainer 2026-11-27, specs/engine/ai-notes/2026-11-27-sparring-bots.md): Hand presets on top of a level, no cheats.
public class SparringBotsTests
{
    [Fact]
    public void EveryBotIsItsLevelWithAStyleOnTop()
    {
        foreach (var name in SparringBots.Names)
            foreach (var level in new[] { AiLevel.Medium, AiLevel.Hard })
            {
                var bot = SparringBots.For(name, level);
                Assert.NotNull(bot);
                var lv = AiLevels.For(level);
                // no cheats (Paul 2026-10-12): the same fog, the same review pace, the same upkeep margin as the level
                Assert.Equal(lv.UseFog, bot!.UseFog);
                Assert.Equal(lv.MilitaryEvery, bot.MilitaryEvery);
                Assert.Equal(lv.EconomyEvery, bot.EconomyEvery);
                Assert.Equal(lv.MaintenanceMargin, bot.MaintenanceMargin);
                Assert.NotEqual(lv, bot);
            }
        Assert.Null(SparringBots.For("Nobody"));
    }
}
