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
    // The Empire's landings by the given day (the switch on or off on both sides; the game measures this one switch, see below).
    private static int Landings(bool fix, int seed, int days)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        var p = new SimpleAiParams { UseFog = true, ShelledLandingNeed = fix, ExplorerScouts = 0, FogGroundForgetDays = 0 };
        var m = new AiMatch(h, p, p);
        m.RunTo(days);
        return m.Sent.GetValueOrDefault("Empire Assault");
    }

    [Fact]
    public void ShelledLandingNeedIsOnByDefault() => Assert.True(new SimpleAiParams().ShelledLandingNeed);

    // The fix lands more, over seeds 1 to 8 (MultiSeed). Empire landings by day 500, fix on / off:
    //   8a378a8 (pre-B2e): 8/8, 13/4, 11/2, 13/3, 8/5, 11/2, 10/2, 16/6   (more on 7 of 8; 90 against 32)
    //   9ec544f (B2e):     7/7, 12/4, 8/3, 8/6, 8/4, 12/5, 8/4, 12/7     (more on 7 of 8; 75 against 40)
    // Floor: more on at least 5 of 8, and at least 1.5 times as many in all. Day 500, because later the games part (a war won or lost
    // ends the count) and the gap says less: by day 700 the fix leads on 7 and 4 seeds, by day 1900 on 5 and 3.
    // Replaces (AI trainer, 2026-10-18, the Dev lead's ask) the one-seed "the fogged Empire wins with the fix and nobody wins without it",
    // re-pinned six times (seeds 3, 5, 8; limits 1100 to 1900) because every new draw in the game generator sends one game another way.
    // The win was never the fix's to give: with the fix the fogged Empire wins by day 1900 on 4 of 8 seeds before B2e (2 without it), and on
    // 2 of 8 on B2e (3 without it), where the Alliance takes Coruscant on 5 of 8 with the fix (0 without it).
    // ExplorerScouts and FogGroundForgetDays are off above so the games measure this one switch.
    [Fact]
    public void FoggedEmpireLandsAfterShelling_OnMostSeeds()
    {
        var on = MultiSeed.Play(MultiSeed.OneToEight, s => Landings(true, s, 500));
        var off = MultiSeed.Play(MultiSeed.OneToEight, s => Landings(false, s, 500));
        string seen = string.Join(", ", on.Zip(off, (a, b) => $"{a}/{b}"));
        Assert.True(on.Zip(off).Count(t => t.First > t.Second) >= 5, $"the fix lands more on fewer than 5 of 8 seeds (on/off: {seen})");
        Assert.True(on.Sum() * 2 >= off.Sum() * 3, $"landings with the fix {on.Sum()}, without {off.Sum()} (on/off: {seen})");
    }
}
