using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// QA-2 (QA round 31, Dev lead 2026-10-19): the games left without a winner by day 3000 were mostly slow wins, not stalemates. Every strike
// fleet idle on a held system merged into one pile that took one target at a time: Medium mirror seed 3 ended with the Empire at 70 ships
// against 3 and 223 systems against 33, taking 12-22 systems per 250 days, nearly all of them empty. rule: ai tuning,
// SimpleAiParams.TaskForces (DEFAULT true; New AI rule, no sheet).
public class SimpleAiTaskForceTests
{
    [Fact]
    public void TaskForcesAreOnByDefault() => Assert.True(new SimpleAiParams().TaskForces);

    // A Medium Empire against an Alliance that gives no orders (its fleets stay home, so a task force must outweigh them before the Empire
    // splits): the day it wins, or 0 by the day limit, and the task-force splits it ordered.
    private static (int End, int Splits) Play(int seed, bool taskForces, int days)
    {
        // B7 bombardment pinned off (Dev lead 2026-10-19): with the Classic bombardment (B7-bombardment) even the TaskForces-off Empire beats the
        // passive Alliance by day 1250 on all four seeds (on 1078, 1081, 1231, 1006; off 1174, 1111, 1231, 1210: 82 days sooner on average), so
        // the floors below no longer separate the switch. They were measured on the pre-B7 bombardment, kept here; the AI tourneys measure B7.
        var preB7 = new Praedra.Core.Defenses.BombardParams { BombardWastedHits = false, BatteryReturnFire = false, BombardFleetsTogether = false,
            BombardDamageScales = false, MilitaryCollateral = false };
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = new GameSettings { Bombard = preB7 } });
        // GarrisonsFirst pinned off (Dev lead 2026-10-19): the passive Alliance makes the Empire "far ahead" from the start, and this test
        // measures task forces; SimpleAiGarrisonsFirstTests measures that switch.
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { TaskForces = taskForces, GarrisonsFirst = false },
            AiLevels.For(AiLevel.Medium) with { GarrisonsFirst = false }, passive: Side.Alliance);
        int splits = 0;
        while (h.Game.Day < days && h.Victory.Winner == null)
        {
            m.Step();
            splits += m.Empire.Submitted.Count(o => o.Kind == "Split" && o.Detail.EndsWith("(task force)", StringComparison.Ordinal));
        }
        return (h.Victory.Winner == Side.Empire ? h.Game.Day : 0, splits);
    }

    // Measured (seeds 1-4, limit 1300): on, the Empire wins on day 1222, 1006, 1087, 934 (task-force splits 3, 0, 7, 2); off, on day 1297,
    // 1006, 1255, 1285. Floors: on, every seed by day 1250 and some split; off, at least 2 seeds still going on day 1250 and none split; on
    // ends at least 100 days sooner on average.
    [Fact]
    public void TaskForces_AStrongSideTakesSystemsInParallelAndWinsSooner()
    {
        // v6 re-pin 2026-10-25 (Dev lead; Paul chose "Ship now" on the spiral-map card, re-measure the AI checks on the new map; ai-notes/2026-10-24-spiral-map-v6.md): on, the Empire wins on day 1219, 1123, 1222, 1267 (splits 0, 6, 4, 0); off, on day -, 1300, 1258, - (0 splits). Floors: on, every
        // seed by day 1275 (was 1250); off, at least 2 seeds still going on day 1250 (unchanged); on ends at least 75 days sooner on average
        // (was 100; measured 82).
        // Praedra names re-pin 2026-10-25 (Dev lead; Paul's order to remove the Star Wars names; systems sort by name, so every game moved):
        // on, the Empire wins on day 1333, 1249, 1141, 1108 (splits 6, 4, 3, 3); off, on day 1369, 1387, 1312, 1087 (0 splits). Floors: on,
        // every seed by day 1350 (was 1275); limit 1400 (was 1300); off, at least 2 seeds still going on day 1250 (unchanged); on ends at
        // least 75 days sooner on average (unchanged; measured 81). Measured with the AI no longer sending load orders that can only bounce.
        const int limit = 1400, by = 1250, onBy = 1350;
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: Play(s, true, limit), Off: Play(s, false, limit)));
        Assert.All(res, r => Assert.True(r.On.End is > 0 and <= onBy, $"seed {r.s}: TaskForces on, the Empire won on day {r.On.End} (0 = not by {limit})"));
        Assert.True(res.Sum(r => r.On.Splits) > 0, "TaskForces on: no task-force split on any seed");
        Assert.All(res, r => Assert.Equal(0, r.Off.Splits));
        int going = res.Count(r => r.Off.End == 0 || r.Off.End > by);
        Assert.True(going >= 2, $"TaskForces off: only {going} of 4 seeds still going on day {by} (the test no longer measures the switch)");
        double on = res.Average(r => r.On.End), off = res.Average(r => r.Off.End == 0 ? limit : r.Off.End);
        Assert.True(off - on >= 75, $"TaskForces on ends {off - on:F0} days sooner on average (on {on:F0}, off {off:F0})");
    }

    // QA-20 (QA round 33): sized on the fleets seen within FogFleetMemoryDays only, the task force fell to LaunchStrength once the big enemy fleet
    // was unseen for 120 days, and the side split into piles that each lost to it (seed 12 Medium mirror: known 32 against a true 350 on day
    // 2000; 15 Empire fleets wiped out against 6 without task forces). A sighting now counts whole for FogFleetMemoryDays and then fades.
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
