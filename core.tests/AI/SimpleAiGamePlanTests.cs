using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// The AI's game plan (AI trainer 2026-11-19; Paul: "Everytime I start a new game playing the Hand, the Collective ALWAYS capture Excubia ...
// I don't want the enemy to be predictable"). Rule checked: ai tuning, SimpleAiParams.PlanSectorSpreadPercent / PlanSystemSpreadPercent
// (AiLevels sets them at every level): each game draws its own plan number, saved with the AI memory, and the AI's likings and dice follow it.
public class SimpleAiGamePlanTests
{
    private static SimpleAiParams Medium() => AiLevels.For(AiLevel.Medium);

    // Each game draws its own plan, once, and only reads the game generator: the game's random stream is the same with the plan on or off.
    [Fact]
    public void EachGameDrawsItsOwnPlanWithoutTouchingTheGameDice()
    {
        var plans = new List<ulong>();
        foreach (int seed in MultiSeed.OneToEight)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            long draws = host.Game.Rng.Draws;
            var ai = new SimpleAi(host, Side.Collective, Medium());
            ai.PlanDay();
            Assert.Equal(draws, host.Game.Rng.Draws); // planning draws nothing from the game generator
            ulong plan = host.AiMemoryFor(Side.Collective, true).Plan;
            Assert.NotEqual(0UL, plan);
            host.Game.Step();
            ai.PlanDay();
            Assert.Equal(plan, host.AiMemoryFor(Side.Collective, true).Plan); // drawn once per game
            plans.Add(plan);
        }
        Assert.Equal(plans.Count, plans.Distinct().Count());

        // the plan is the Collective's only: the Hand AI stays a straightforward machine (Paul 2026-11-19)
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        new SimpleAi(h, Side.Hand, Medium()).PlanDay();
        Assert.Equal(0UL, h.AiMemoryFor(Side.Hand, true).Plan);
    }

    // With the plan off (the SimpleAiParams DEFAULT) nothing is drawn or saved: the AI of before.
    [Fact]
    public void PlanOffDrawsNoPlan()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedE });
        new SimpleAi(host, Side.Collective, new SimpleAiParams { UseFog = true }).PlanDay();
        Assert.Equal(0UL, host.AiMemoryFor(Side.Collective, true).Plan);
    }

    // m1-clock-and-orders K12 with the plan on: the save carries the plan, so a loaded game plays on exactly as the uninterrupted one.
    [Trait("Category", "Play")]
    [Fact]
    public void K12_ALoadedGameKeepsItsPlan()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 5, Preset = StartPreset.MedE });
        new AiMatch(host, Medium(), Medium()).RunTo(250);
        ulong plan = host.AiMemoryFor(Side.Collective, true).Plan;
        Assert.NotEqual(0UL, plan);

        var loaded = GameHost.Load(host.SaveWithSettings());
        Assert.Equal(plan, loaded.AiMemoryFor(Side.Collective, true).Plan);
        var a = new AiMatch(host, Medium(), Medium());
        var b = new AiMatch(loaded, Medium(), Medium());
        for (int i = 0; i < 60; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }

    // The point of the plan: one and the same game, with only the Collective's plan number changed, plays out differently. Medium against
    // Medium from the Hand player's start (MedE), seed 1, the first two worlds the Collective takes by assault by day 400, for plan numbers
    // 1-8. Measured (2026-11-19, spreads 30/15): 8 different campaigns (first assault Clinker, Wyburn or Navalia: only the 3 soft worlds
    // nearest its start are in reach that early, at any spread up to 100/40; the second assault differs in 7 of 8). With the plan off the
    // eight games are one game.
    [Trait("Category", "Play")]
    [Fact]
    public void ThePlanAloneChangesTheCampaign()
    {
        static string[] FirstAssaults(SimpleAiParams p) => MultiSeed.Play(Enumerable.Range(1, 8).ToArray(), plan =>
        {
            // #442's leader rules pinned to before (MultiSeed.LeaderRulesBefore442): with them seed 1 gave 4 campaigns of 8, floor 5
            var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE, Settings = MultiSeed.LeaderRulesBefore442 });
            host.AiMemoryFor(Side.Collective, true).Plan = (ulong)plan;
            var m = new AiMatch(host, Medium(), p);
            int seen = host.Game.Events.Count;
            var taken = new List<string>();
            while (host.Game.Day < 400 && host.Victory.Winner == null && taken.Count < 2)
            {
                m.Step();
                for (var ev = host.Game.Events; seen < ev.Count; seen++)
                    if (ev[seen] is SystemCaptured { To: Side.Collective, Cause: CaptureCause.Assault } c) taken.Add(c.System);
            }
            return string.Join(" then ", taken.Take(2));
        });
        var on = FirstAssaults(Medium());
        var off = FirstAssaults(Medium() with { PlanSectorSpreadPercent = 0, PlanSystemSpreadPercent = 0 });
        Assert.Single(off.Distinct()); // no plan: the same game eight times
        int distinct = on.Distinct().Count();
        Assert.True(distinct >= 5, $"plan on: {distinct} different campaigns of 8 (floor 5; {string.Join(", ", on)}); off: {off[0]}");
    }
}
