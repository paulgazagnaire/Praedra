using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

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
        string[] lines = save.Split('\n', 4);
        Assert.StartsWith("PRAEDRA-CORE 1 battle=", lines[0]);
        Assert.StartsWith("SETTINGS ", lines[1]);
        Assert.StartsWith("AI-MEMORY ", lines[2]);
        Assert.Equal(host.Save(), lines[3]);

        var old = GameHost.Load(lines[0] + "\n" + lines[3]); // an older save: header and core only
        Assert.Equal(host.StateHash(), old.StateHash());
        Assert.Empty(old.AiMemoryFor(Side.Empire, true).Notes);
    }

    // m1-clock-and-orders K16: QA-17 repro. The save kept only the battle ruleset; every other setting fell back to the code DEFAULT on load.
    // Seed 12 under ControlRule Derived (AssaultOnly is the code DEFAULT today), fog AIs to day 400: the loaded copy came back AssaultOnly and
    // split on day 448 (Begamor changed hands only in Classic).
    [Fact]
    public void K16_SaveKeepsEveryGameSetting()
    {
        var settings = new GameSettings
        {
            ControlRule = Praedra.Core.Control.ControlRule.Derived,
            Characters = new Praedra.Core.Characters.CharacterParams { TalentedSkipStrong = false },
        };
        var host = NewGame.Create(new NewGameOptions { Seed = 12, Preset = StartPreset.MedA, Settings = settings });
        new AiMatch(host, Fog(), Fog()).RunTo(400);

        string save = host.SaveWithSettings();
        var loaded = GameHost.Load(save);
        Assert.Equal(Praedra.Core.Control.ControlRule.Derived, loaded.Settings.ControlRule);
        Assert.False(loaded.Settings.Characters!.TalentedSkipStrong);
        Assert.Equal(save, loaded.SaveWithSettings()); // every setting comes back, the number tables included

        var a = new AiMatch(host, Fog(), Fog());
        var b = new AiMatch(loaded, Fog(), Fog());
        for (int i = 0; i < 60; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }

    // m1-clock-and-orders K16: settings passed to Load still replace the saved ones (tests and tools load a save under other rules).
    [Fact]
    public void K16_PassedSettingsReplaceTheSavedOnes()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA,
            Settings = new GameSettings { ControlRule = Praedra.Core.Control.ControlRule.Derived } });
        var loaded = GameHost.Load(host.SaveWithSettings(), new GameSettings { ControlRule = Praedra.Core.Control.ControlRule.AssaultOnly });
        Assert.Equal(Praedra.Core.Control.ControlRule.AssaultOnly, loaded.Settings.ControlRule);
    }
}
