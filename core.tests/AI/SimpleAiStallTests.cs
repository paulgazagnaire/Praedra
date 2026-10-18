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
        var p = new SimpleAiParams { UseFog = true, ShelledLandingNeed = fix, ExplorerScouts = 0, FogGroundForgetDays = 0 };
        var m = new AiMatch(h, p, p);
        m.RunTo(days);
        return (h.Game.Day, h.Victory.Winner, m.Sent.GetValueOrDefault("Empire Assault"));
    }

    [Fact]
    public void ShelledLandingNeedIsOnByDefault() => Assert.True(new SimpleAiParams().ShelledLandingNeed);

    // seed 3: with the fix the Empire takes the Rebel HQ (day 913 measured on the 360-system map; day 1747 on v5); without it nobody wins
    // by day 3000. // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): limit 1100 -> 1900.
    // Re-pin 2026-10-17 (merge of main #57 into m0/importer): ExplorerScouts off above, so the game measures the one switch as before. With an
    // explorer on each side the games are identical to day 161; on day 162 the Alliance's Medium Transport (fleet 471, the explorer) goes to
    // never-explored Rekelos instead of Cymoon and the war takes another course: on day 729 the Alliance strike fleet 427 (13 ships, strength
    // 82) beats the Coruscant guard (fleet 277, strength 112) in a simulated battle, the guard retreats to Delaya and moves on to Anaxes, the
    // fleet shells the 8 regiments on day 732 and lands on day 735: "Alliance captured Coruscant" (old victory rules). Without explorers on
    // this branch the Empire takes the Rebel HQ on day 1393. A legal outcome of the rules on one seed, unrelated to the landing fix.
    // B2b re-pin 2026-10-17 (needs Paul's or the AI trainer's sign-off): the betrayal draws (one per member per mission check) shift the game
    // generator, and on seed 3 the Alliance now wins on day 816. With the fix the fogged Empire wins on seeds 1 (day 1744) and 5 (day 1705);
    // seeds 2, 4 and 6 have no winner by day 1900. Seed 1 lands less with the fix than without (63 against 70), so seed 5.
    // QA-5 2026-10-17 (AI trainer): FogGroundForgetDays off above too, for the same reason as ExplorerScouts (the game measures this one switch).
    // With old ground counts forgotten, seed 5 takes another course and has no winner by day 1900 (fix on).
    // B3 re-seed 2026-10-18 (Dev lead's rule: same pass rate as main, dated, with the table): the Classic battle follows the traced original and AI sides
    // now answer the alert with the AI threshold 0.9, so every game takes another course. Seeds 1 to 8 (fix on: winner day landings / fix off: winner landings):
    // main 9d313cd: A 600 12/- 11, A 504 8/- 9, E 1807 75/E 74, E 1396 47/- 37, E 1672 74/- 68, - 1900 72/- 61, A 669 14/- 10, - 1900 110/- 73 (passes: 4, 5);
    // B3: A 960 32/- 33, E 1600 72/- 61, E 847 32/- 19, A 636 13/- 17, A 732 9/- 14, E 1627 97/E 29, - 1900 70/- 67, - 1900 92/- 82 (passes: 2, 3).
    // The Empire wins 3 of 8 with the fix on both. Seed 3.
    [Fact]
    public void FoggedEmpireLandsAfterShellingAndWins()
    {
        var on = Play(true, 3, 1900);
        Assert.Equal(Side.Empire, on.Winner);
        var off = Play(false, 3, on.Day);
        Assert.Null(off.Winner);
        Assert.True(on.Assaults > off.Assaults, $"landings with the fix {on.Assaults}, without {off.Assaults}");
    }
}
