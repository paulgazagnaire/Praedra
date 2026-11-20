using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// AI trainer 2026-11-20 (#343, Ponytail): every other AI behaviour test pins the old start (DealSettings.FileOldVictory, YardSpikes), so this
// floor watches the start a player gets: the big Hand start and the drawn Collective base (GameSettings.BigHandStart and CollectiveBaseDraw,
// DEFAULT on), Medium against Medium on MedE (the Hand player's Medium start), fog on both sides, as `sim aivai` and the AI notes measure.
// The Collective must stay hard to kill there (Paul plays the Hand). Over 192 seeds the Collective won 132, the Hand 34, 26 open
// (ai-notes/2026-11-20-big-hand-start.md); the first version of the big start had dropped it to 99.
// Seeds 1 to 8, measured on #343: the Collective wins all 8, on day 1368 to 2720. Floor: the Collective wins at least 5 of 8 by day 3000.
public class DefaultStartPlayTests
{
    private readonly ITestOutputHelper _out;
    public DefaultStartPlayTests(ITestOutputHelper o) { _out = o; }

    [Trait("Category", "Play")]
    [Fact]
    public void DefaultStart_MediumCollectiveStaysHardToKill()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var p = AiLevels.For(AiLevel.Medium);
            var m = new AiMatch(host, p, p);
            while (host.Game.Day < 3000 && host.Victory.Winner == null) m.Step();
            return (Seed: seed, host.Victory.Winner, host.Game.Day);
        });
        foreach (var g in games) _out.WriteLine($"seed {g.Seed}: winner {g.Winner?.ToString() ?? "none"} day {g.Day}");
        Assert.True(new GameSettings().BigHandStart && new GameSettings().CollectiveBaseDraw, "this floor is for the default start");
        int collective = games.Count(g => g.Winner == Side.Collective);
        Assert.True(collective >= 5, $"the Collective won {collective} of 8 Medium games on the default start by day 3000");
    }
}
