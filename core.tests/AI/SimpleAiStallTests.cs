using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.AI;

// m6 stall fix: a fogged strike fleet shelled a target four times and left without landing, over and over (PickTarget asked Need(d / 2) for
// the pick, the landing asked Need(d), and with fog the believed garrison stays at the unseen guess of FogUnknownDefenders while the real
// garrison was empty). Measured on seed 3 (fog, characters and missions on): Empire fleets left such a target 77 times in 700 days, landed
// 4 times per 100 days, and the game ran to day 1500 with no winner (Empire 71 systems against 21, fleet strength 1475 against 52).
// rule: ai tuning, SimpleAiParams.ShelledLandingNeed (DEFAULT true).
public class SimpleAiStallTests
{
    private static (int Day, Side? Winner, int Assaults) Play(bool fix, int seed, int days)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        var p = new SimpleAiParams { UseFog = true, ShelledLandingNeed = fix };
        var m = new AiMatch(h, p, p);
        m.RunTo(days);
        return (h.Game.Day, h.Victory.Winner, m.Sent.GetValueOrDefault("Empire Assault"));
    }

    [Fact]
    public void ShelledLandingNeedIsOnByDefault() => Assert.True(new SimpleAiParams().ShelledLandingNeed);

    // seed 3: with the fix the Empire takes the Rebel HQ (day 913 measured on the 360-system map; day 1747 on v5); without it nobody wins
    // by day 3000. // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): limit 1100 -> 1900.
    [Fact]
    public void FoggedEmpireLandsAfterShellingAndWinsSeed3()
    {
        var on = Play(true, 3, 1900);
        Assert.Equal(Side.Empire, on.Winner);
        var off = Play(false, 3, on.Day);
        Assert.Null(off.Winner);
        Assert.True(on.Assaults > off.Assaults, $"landings with the fix {on.Assaults}, without {off.Assaults}");
    }
}
