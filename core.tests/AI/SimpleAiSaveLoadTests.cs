using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.AI;

// A loaded game continues the same game, the AI included (QA-1). Rule checked: m1-clock-and-orders K12 (save, load, continue: same state
// hash as the uninterrupted run), with the fogged simple AI giving the orders on both sides. The AI reads its side's event feed (m5-fog-and-hq
// §N8), which a save does not carry (m1-clock-and-orders DEFAULT: events are not saved), so the AI keeps a small memory of what it read and
// the save carries that memory.
public class SimpleAiSaveLoadTests
{
    private static SimpleAiParams Fog() => new() { UseFog = true };

    // m1-clock-and-orders K12 (with AI): QA-1 repro. Seed 3, fog AIs to day 400, SaveWithSettings, Load, fresh AIs on both copies: they
    // diverged on day 411 before the fix. Seed 1 saved at day 300 diverged on day 313.
    [Theory]
    [InlineData(3, 400, 60)]
    [InlineData(1, 300, 60)]
    public void K12_AiGameContinuesTheSameAfterSaveAndLoad(int seed, int saveDay, int moreDays)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        new AiMatch(host, Fog(), Fog()).RunTo(saveDay);
        Assert.Equal(saveDay, host.Game.Day);

        string save = host.SaveWithSettings();
        var loaded = GameHost.Load(save);
        Assert.Equal(host.StateHash(), loaded.StateHash());
        Assert.Equal(save, loaded.SaveWithSettings()); // the loaded game saves back to the same text

        var a = new AiMatch(host, Fog(), Fog());
        var b = new AiMatch(loaded, Fog(), Fog());
        for (int i = 0; i < moreDays; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }

    // m1-clock-and-orders §DEFAULT events are not saved: the AI memory rides beside the core save, not in it. A save made before the AI memory
    // existed (no AI-MEMORY line) still loads, with an empty memory, and the core save text is unchanged by the AI line.
    [Fact]
    public void AiMemoryLineIsOptionalAndOutsideTheCoreSave()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });
        new AiMatch(host, Fog(), Fog()).RunTo(120);
        string save = host.SaveWithSettings();
        string[] lines = save.Split('\n', 3);
        Assert.StartsWith("REBELLION-CORE 1 battle=", lines[0]);
        Assert.StartsWith("AI-MEMORY ", lines[1]);
        Assert.Equal(host.Save(), lines[2]);

        var old = GameHost.Load(lines[0] + "\n" + lines[2]); // an older save: header and core only
        Assert.Equal(host.StateHash(), old.StateHash());
        Assert.Empty(old.AiMemoryFor(Side.Empire, true).Notes);
    }
}
