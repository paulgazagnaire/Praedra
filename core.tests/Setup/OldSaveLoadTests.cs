using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Setup;

// m1-clock-and-orders K12, K16 (a loaded game continues as before): QA-24. A save written before the Praedra rename (made at 492d052: seed 3,
// MedA, fog AIs to day 60) has the REBELLION-CORE header and names the Classic settings "Original". It loads, keeps its state and its
// settings, and saves back in today's format.
public class OldSaveLoadTests
{
    private static string OldSave() => File.ReadAllText(Path.Combine(TestData.RepoRoot, "core.tests", "Setup", "saves", "before-praedra-rename.txt"));

    [Fact]
    public void QA24_SaveFromBeforeTheRenameLoads()
    {
        var loaded = GameHost.Load(OldSave());
        Assert.Equal(60, loaded.Game.Day);
        Assert.Equal("7861e1018def4b62aa1b684d98682329deb80859f01650ee85fd0dff071369d2", loaded.StateHash()); // loaded with the Praedra names (before them: 620900ae..., the hash 492d052 printed for this game)
        Assert.Equal(Praedra.Core.Battle.ClassicBattleRng.Classic, loaded.Settings.Battle!.BattleRng);
        Assert.Equal(Praedra.Core.Battle.ClassicTaskForces.Classic, loaded.Settings.Battle!.TaskForces);
        Assert.Equal(Praedra.Core.Characters.StartCastOfficers.Classic, loaded.Settings.Characters!.StartCastOfficers);
        Assert.Equal(12345678, loaded.Settings.Battle!.ClassicRngSeed);
        string again = loaded.SaveWithSettings();
        Assert.StartsWith("PRAEDRA-CORE 1 battle=", again);
        Assert.DoesNotContain("\"Original\"", again);
        Assert.Equal(loaded.StateHash(), GameHost.Load(again).StateHash());
    }
}
