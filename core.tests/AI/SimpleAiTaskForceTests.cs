using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.AI;

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
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { TaskForces = taskForces }, AiLevels.For(AiLevel.Medium), passive: Side.Alliance);
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
        const int limit = 1300, by = 1250;
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: Play(s, true, limit), Off: Play(s, false, limit)));
        Assert.All(res, r => Assert.True(r.On.End is > 0 and <= by, $"seed {r.s}: TaskForces on, the Empire won on day {r.On.End} (0 = not by {limit})"));
        Assert.True(res.Sum(r => r.On.Splits) > 0, "TaskForces on: no task-force split on any seed");
        Assert.All(res, r => Assert.Equal(0, r.Off.Splits));
        int going = res.Count(r => r.Off.End == 0 || r.Off.End > by);
        Assert.True(going >= 2, $"TaskForces off: only {going} of 4 seeds still going on day {by} (the test no longer measures the switch)");
        double on = res.Average(r => r.On.End), off = res.Average(r => r.Off.End == 0 ? limit : r.Off.End);
        Assert.True(off - on >= 100, $"TaskForces on ends {off - on:F0} days sooner on average (on {on:F0}, off {off:F0})");
    }
}
