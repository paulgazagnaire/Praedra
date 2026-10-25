using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// QA-20 (QA round 34): task forces read the enemy fleets of the side's picture, and a later look at a system replaces its entry, so a big fleet
// that left a system was forgotten the day the side looked there again (seed 10 Hard Hand vs Medium Collective: known 43 against a true 227 on
// day 2000, and the Hand's 240 fleet split into 83/82/63). The side now keeps the strongest enemy fleet it has seen in its AI memory, fading
// the same way (TaskForceFadePercent), and a save carries it. rule: ai tuning, SimpleAiParams.FleetMemory (DEFAULT false, measured; New AI rule,
// no sheet); m1-clock-and-orders K12 (save, load, continue).
public class SimpleAiFleetMemoryTests
{
    private static SimpleAiParams Fog() => new() { UseFog = true, FleetMemory = true };

    [Fact]
    public void FleetMemoryIsOffByDefault() => Assert.False(new SimpleAiParams().FleetMemory);

    // Fog AIs on both sides, seed 3 to day 400: the remembered fleet outlives the picture's entries (a look that found the fleet gone, or the
    // fade of the picture's own entry), and it only holds what the picture showed (never above the strongest faded sighting seen so far).
    [Fact]
    public void FleetMemory_KeepsTheStrongestSightingAfterThePictureLosesIt()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });
        var m = new AiMatch(host, Fog(), Fog());
        int outlived = 0;
        double seenMax = 0;
        while (host.Game.Day < 400)
        {
            m.Step();
            double picture = m.Hand.PictureStrongestEnemyFleet(), kept = m.Hand.RememberedFleet();
            seenMax = Math.Max(seenMax, picture);
            Assert.True(kept <= seenMax + 1e-9, $"day {host.Game.Day}: remembered {kept} above anything the picture showed ({seenMax})");
            if (kept > picture) outlived++;
        }
        Assert.True(host.AiMemoryFor(Side.Hand, true).FleetSeen > 0, "the Hand remembers no enemy fleet by day 400");
        Assert.True(outlived > 0, "the remembered fleet never outlived the picture's entries");
    }

    // m1-clock-and-orders K12 with the fleet memory set: the save carries it, the loaded copy reads it back exactly, and both copies play on
    // with the same state hash.
    [Fact]
    public void FleetMemory_RidesInTheSaveAndTheLoadedGamePlaysTheSame()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });
        new AiMatch(host, Fog(), Fog()).RunTo(400);
        var mem = host.AiMemoryFor(Side.Hand, true);
        Assert.True(mem.FleetSeen > 0);

        string save = host.SaveWithSettings();
        Assert.Contains("\"FleetSeen\":", save);
        var loaded = GameHost.Load(save);
        Assert.Equal(mem.FleetSeen, loaded.AiMemoryFor(Side.Hand, true).FleetSeen);
        Assert.Equal(mem.FleetSeenDay, loaded.AiMemoryFor(Side.Hand, true).FleetSeenDay);
        Assert.Equal(save, loaded.SaveWithSettings());

        var a = new AiMatch(host, Fog(), Fog());
        var b = new AiMatch(loaded, Fog(), Fog());
        for (int i = 0; i < 60; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }

    // A save made before the fleet memory existed (AI memory without the two fields) loads with no remembered fleet.
    [Fact]
    public void FleetMemory_AnOlderSaveLoadsWithNone()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });
        new AiMatch(host, Fog(), Fog()).RunTo(400);
        string save = host.SaveWithSettings();
        string old = System.Text.RegularExpressions.Regex.Replace(save, ",\"FleetSeen\":[^,}]+,\"FleetSeenDay\":[^,}]+", "");
        Assert.NotEqual(save, old);
        Assert.DoesNotContain("FleetSeen", old);
        var loaded = GameHost.Load(old);
        Assert.Equal(host.StateHash(), loaded.StateHash());
        Assert.Equal(0, loaded.AiMemoryFor(Side.Hand, true).FleetSeen);
        Assert.Equal(0, loaded.AiMemoryFor(Side.Collective, true).FleetSeen);
    }
}
