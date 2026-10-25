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
        Assert.Equal("50bd1fea6dd1bcfcad8bca17e4eea326cbec55897cb3679f79fb735957cf4055", loaded.StateHash()); // loaded with the Praedra names and words (side words only: 9105e202...) (the state JSON names its fields, so the hash moved; with the names only: 7861e101..., before them: 620900ae..., the hash 492d052 printed for this game)
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
