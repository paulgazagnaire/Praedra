using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// rule: ai tuning, the Collective commander's later plans (AI trainer "Rooster", 2026-11-27; design
// specs/engine/ai-notes/2026-11-26-adaptive-commander.md §3). Switches in core/AI/SimpleAiParams.Collective.cs.
public class SimpleAiCollectivePlansTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiCollectivePlansTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void TheLaterCollectivePlansAreOffByDefault()
    {
        var p = new SimpleAiParams();
        Assert.False(p.Neutralise);
        Assert.Equal(0, p.NeutraliseElsewherePercent);
    }

    // Plan 11, Neutralise. m2-control-and-capture §Control: a world with no regiments is held by the side with 60 support, neutral at 41 to 59.
    // With Neutralise on, Collective fleets shell thin Hand garrisons on half-loyal worlds bare (Military, no landing) in most games, and the Hand loses
    // worlds to "troops left" (seeds 1-4, Medium mirror, MedE, day 800; the counts are printed, not asserted: one game swings too much).
    [Fact]
    public void NeutraliseShellsThinGarrisonsBare()
    {
        var r = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            int Run(bool on, out int shells)
            {
                var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
                var c = AiLevels.For(AiLevel.Medium) with { Neutralise = on, NeutraliseElsewherePercent = on ? 100 : 0 };
                var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), c);
                shells = 0;
                while (host.Game.Day < 800 && host.Victory.Winner == null)
                {
                    m.Step();
                    shells += m.Collective.Submitted.Count(o => o.Detail.Contains("(Neutralise)", StringComparison.Ordinal));
                }
                return host.Game.Events.OfType<SystemCaptured>().Count(x => x.From == Side.Hand && x.Cause == CaptureCause.TroopsLeft);
            }
            int off = Run(false, out int s0), on = Run(true, out int s1);
            return (off, on, s0, s1);
        });
        foreach (var x in r) _out.WriteLine($"Hand worlds lost to troops left: off {x.off}, on {x.on}; Neutralise shellings off {x.s0}, on {x.s1}");
        Assert.All(r, x => Assert.Equal(0, x.s0));
        Assert.True(r.Count(x => x.s1 > 0) >= 3, "the Collective shelled no thin garrison bare in 2 or more of 4 games");
    }
}
