using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// QA-2 (QA round 31, Dev lead 2026-10-19): the games left without a winner by day 3000 were mostly slow wins, not stalemates. Every strike
// fleet idle on a held system merged into one pile that took one target at a time: Medium mirror seed 3 ended with the Hand at 70 ships
// against 3 and 223 systems against 33, taking 12-22 systems per 250 days, nearly all of them empty. rule: ai tuning,
// SimpleAiParams.TaskForces (DEFAULT true; New AI rule, no sheet).
public class SimpleAiTaskForceTests
{
    [Fact]
    public void TaskForcesAreOnByDefault() => Assert.True(new SimpleAiParams().TaskForces);

    // A Medium Hand against a Collective that gives no orders (its fleets stay home, so a task force must outweigh them before the Hand
    // splits): the day it wins, or 0 by the day limit, and the task-force splits it ordered.
    private static (int End, int Splits) Play(int seed, bool taskForces, int days)
    {
        // B7 bombardment pinned off (Dev lead 2026-10-19): with the Classic bombardment (B7-bombardment) even the TaskForces-off Hand beats the
        // passive Collective by day 1250 on all four seeds (on 1078, 1081, 1231, 1006; off 1174, 1111, 1231, 1210: 82 days sooner on average), so
        // the floors below no longer separate the switch. They were measured on the pre-B7 bombardment, kept here; the AI tourneys measure B7.
        var preB7 = new Praedra.Core.Defenses.BombardParams { BombardWastedHits = false, BatteryReturnFire = false, BombardFleetsTogether = false,
            BombardDamageScales = false, MilitaryCollateral = false };
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = new GameSettings { Bombard = preB7,
            // capital and control pinned off (Dev lead 2026-10-26): it ends the TaskForces-off games 180 days after the Hearth falls, so the floors
            // no longer separate the switch (0 of 4 off games still going on day 1250); this test measures task forces
            Victory = new Praedra.Core.Victory.VictoryRules { DominanceSharePercent = 0 },
            // Hearth tip pinned off (Dev lead 2026-10-26, m5-fog-and-hq §N6a): its draws move the seeds the floors were measured on
            Fog = new Praedra.Core.Fog.FogParams { HearthTipPermille = 0, HearthTipSectorPermille = 0 },
            // harvester upkeep pinned to Classic 600 (Dev lead 2026-10-27, m5-harvester §1 Upkeep): at 450 this default-params Hand builds a
            // harvester, which moves seed 1 (no win by day 1400 with task forces on); this test measures task forces
            Harvester = new Praedra.Core.Harvester.HarvesterParams { Upkeep = null } } });
        // GarrisonsFirst pinned off (Dev lead 2026-10-19): the passive Collective makes the Hand "far ahead" from the start, and this test
        // measures task forces; SimpleAiGarrisonsFirstTests measures that switch.
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { TaskForces = taskForces, GarrisonsFirst = false },
            AiLevels.For(AiLevel.Medium) with { GarrisonsFirst = false }, passive: Side.Collective);
        int splits = 0;
        while (h.Game.Day < days && h.Victory.Winner == null)
        {
            m.Step();
            splits += m.Hand.Submitted.Count(o => o.Kind == "Split" && o.Detail.EndsWith("(task force)", StringComparison.Ordinal));
        }
        return (h.Victory.Winner == Side.Hand ? h.Game.Day : 0, splits);
    }

    // Measured (seeds 1-4, limit 1300): on, the Hand wins on day 1222, 1006, 1087, 934 (task-force splits 3, 0, 7, 2); off, on day 1297,
    // 1006, 1255, 1285. Floors: on, every seed by day 1250 and some split; off, at least 2 seeds still going on day 1250 and none split; on
    // ends at least 100 days sooner on average.
    [Trait("Category", "Play")]
    [Fact]
    public void TaskForces_AStrongSideTakesSystemsInParallelAndWinsSooner()
    {
        // v6 re-pin 2026-10-25 (Dev lead; Paul chose "Ship now" on the spiral-map card, re-measure the AI checks on the new map; ai-notes/2026-10-24-spiral-map-v6.md): on, the Hand wins on day 1219, 1123, 1222, 1267 (splits 0, 6, 4, 0); off, on day -, 1300, 1258, - (0 splits). Floors: on, every
        // seed by day 1275 (was 1250); off, at least 2 seeds still going on day 1250 (unchanged); on ends at least 75 days sooner on average
        // (was 100; measured 82).
        // Praedra names re-pin 2026-10-25 (Dev lead; Paul's order to remove the old placeholder names; systems sort by name, so every game moved):
        // on, the Hand wins on day 1333, 1249, 1141, 1108 (splits 6, 4, 3, 3); off, on day 1369, 1387, 1312, 1087 (0 splits). Floors: on,
        // every seed by day 1350 (was 1275); limit 1400 (was 1300); off, at least 2 seeds still going on day 1250 (unchanged); on ends at
        // least 75 days sooner on average (unchanged; measured 81). Measured with the AI no longer sending load orders that can only bounce.
        // QA-40 re-pin 2026-10-25 (Dev lead; Planets' fixed v6 links, 394 links): on, the Hand wins on day 1354, 1030, 1144, 1288 (splits 3,
        // 2, 4, 2); off, on day 1372, 1285, 1234, 1195 (0 splits). Floors: on, every seed by day 1375 (was 1350); off, at least 2 seeds still
        // going on day 1250 (unchanged); on ends at least 60 days sooner on average (was 75; measured 68; seed 4 is slower on, the other three
        // are 18 to 255 days sooner).
        // Route thinning re-pin 2026-11-11 (Dev lead; Paul: "clean this mess of a route layout, reduce the number", 394 links down to 280):
        // seeds 1-4 alone no longer show the switch (on 1282, 1405, 1249, 1105; off 1249, 1195, 1306, 1159), so seeds 5 and 6 join: on 1084,
        // 1198 (splits 5, 2, 4, 2, 1, 3); off 1555, 1300 (0 splits). On ends sooner on 4 of 6 seeds and 73 days sooner on average. Floors: on,
        // every seed by day 1425 (was 1375); limit 1600 (was 1400); off, at least 2 seeds still going on day 1250 (unchanged; measured 3);
        // on ends at least 50 days sooner on average (was 60). Seed 2 is 210 days slower on: reported to the AI trainer.
        // Whole-batch floors 2026-11-11 (AI trainer, for the Dev lead: the floors broke on every map change while the switch did not move). On
        // seeds 1-24, same AI code, both maps: on ends sooner on 18 of 24 (394 links: 21), 73 days sooner on average (80); on any 4-6 seeds the
        // mean swings from -33 to +155 and one seed per 24 never wins with it on, so per-seed and small-set floors measured the seeds, not the
        // switch. Seeds 1-12 on the 280 links: on 1282, 1405, 1249, 1105, 1084, 1198, -, 1432, 1234, 1138, 1303, 1159 (39 splits); off 1249,
        // 1195, 1306, 1159, 1555, 1300, 1249, 1456, 1408, 1180, 1276, 1165. Sooner on 8 of 12 (394 links: 11), 26 days sooner on average
        // (54), on wins on 11 (12), off still going on day 1250 on 6 (8). Floors: on sooner on a majority (7 of 12), never slower on average,
        // wins on at least 10; off still going on day 1250 on at least 4; some split on, none off.
        const int limit = 1600, by = 1250;
        var res = MultiSeed.Play(Enumerable.Range(1, 12).ToArray(), s => (s, On: Play(s, true, limit), Off: Play(s, false, limit)));
        string all = string.Join(", ", res.Select(r => $"seed {r.s} {r.On.End}/{r.Off.End}"));
        Assert.True(res.Count(r => r.On.End > 0) >= 10, $"TaskForces on: the Hand won by day {limit} on fewer than 10 of 12 seeds: {all}");
        Assert.True(res.Sum(r => r.On.Splits) > 0, "TaskForces on: no task-force split on any seed");
        Assert.All(res, r => Assert.Equal(0, r.Off.Splits));
        int going = res.Count(r => r.Off.End == 0 || r.Off.End > by);
        Assert.True(going >= 4, $"TaskForces off: only {going} of {res.Length} seeds still going on day {by} (the test no longer measures the switch)");
        int End(int e) => e == 0 ? limit : e;
        int sooner = res.Count(r => End(r.On.End) < End(r.Off.End));
        Assert.True(sooner >= 7, $"TaskForces on ends sooner on only {sooner} of 12 seeds: {all}");
        double on = res.Average(r => End(r.On.End)), off = res.Average(r => End(r.Off.End));
        Assert.True(off - on >= 0, $"TaskForces on ends {on - off:F0} days later on average (on {on:F0}, off {off:F0})");
    }

    // QA-20 (QA round 33): sized on the fleets seen within FogFleetMemoryDays only, the task force fell to LaunchStrength once the big enemy fleet
    // was unseen for 120 days, and the side split into piles that each lost to it (seed 12 Medium mirror: known 32 against a true 350 on day
    // 2000; 15 Hand fleets wiped out against 6 without task forces). A sighting now counts whole for FogFleetMemoryDays and then fades.
    [Theory]
    [InlineData(0, 120, 5, 100)]
    [InlineData(120, 120, 5, 100)]
    [InlineData(220, 120, 5, 95)]
    [InlineData(1120, 120, 5, 50)]
    [InlineData(2120, 120, 5, 0)]
    [InlineData(5000, 120, 5, 0)]
    [InlineData(320, 120, 25, 50)]
    [InlineData(520, 120, 25, 0)]
    [InlineData(5000, 120, 0, 100)]
    public void TaskForceSizing_AnOldSightingFadesSlowly(int age, int memory, int fade, int expected) =>
        Assert.Equal(expected, SimpleAi.FadedPercent(age, memory, fade));

    [Fact]
    public void TaskForceSizing_ASightingFadesByAQuarterPer100DaysByDefault() => Assert.Equal(25, new SimpleAiParams().TaskForceFadePercent);
}
