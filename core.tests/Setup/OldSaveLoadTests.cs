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
        Assert.Equal("a1927576e37002cf10b4b5fe5b562a3d6172e6979b2f587821e95bc8d2146fe4", loaded.StateHash()); // per-fleet OnContact 2026-11-21: the two side fields left the state JSON (with them back the hash is exactly 8496893b...326f, the game is the same); loaded with the Praedra names and words (old-tongue names since 2026-11-16: Saleucami now loads as Kelmeer, was 9ca0a402...; accent-free since 2026-11-15, was d8fd8a1a...), ships named "<old class> <n>" renamed too (QA-44; before: bd7426f2..., up to the harvester words: 50bd1fea..., side words only: 9105e202...) (the state JSON names its fields, so the hash moved; with the names only: 7861e101..., before them: 620900ae..., the hash 492d052 printed for this game)
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
