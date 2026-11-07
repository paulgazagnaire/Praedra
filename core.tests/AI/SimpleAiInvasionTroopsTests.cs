using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// AI trainer 2026-11-07 (Paul: fix the Collective training too few troops): the Collective counted every spare regiment on any of its worlds as
// ready to invade (InvasionRegimentTarget), so about 45 spares scattered over its worlds stopped the training on day 500 while its upkeep room
// stood about 900 unused. rule: ai tuning, SimpleAiParams.InvasionReadyAtRally (DEFAULT off, on at Medium and Hard; New AI rule, no sheet).
public class SimpleAiInvasionTroopsTests
{
    [Fact]
    public void InvasionReadyAtRally_OnAtMediumAndHardOnly()
    {
        Assert.False(new SimpleAiParams().InvasionReadyAtRally);
        Assert.False(AiLevels.For(AiLevel.Easy).InvasionReadyAtRally);
        Assert.True(AiLevels.For(AiLevel.Medium).InvasionReadyAtRally);
        Assert.True(AiLevels.For(AiLevel.Hard).InvasionReadyAtRally);
    }

    // Medium mirror; the switch only for the Collective's AI. Regiments the Collective orders by day 500.
    private static int Ordered(int seed, bool on)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium) with { InvasionReadyAtRally = on });
        int ordered = 0;
        while (h.Game.Day < 500 && h.Victory.Winner == null)
        {
            m.Step();
            foreach (var o in m.Collective.Submitted)
            {
                if (o.Kind != "Build") continue;
                var w = o.Detail.Split(" x ", 2); // "{count} x {class} at {yard} for {destination}" (OrderRegiments); other builds read differently
                int at = w.Length == 2 ? w[1].IndexOf(" at ", StringComparison.Ordinal) : -1;
                if (at > 0 && int.TryParse(w[0], out int n) && h.Economy.Costs.Get(w[1][..at]).Category == ObjectCategory.Regiment) ordered += n;
            }
        }
        return ordered;
    }

    // Measured (seeds 1-4, main aeb5f12): on 42, 51, 50, 53 regiments ordered; off 29, 25, 23, 27 (196 against 104). Floors: on orders more on
    // at least 3 seeds and 30% more in all.
    [Fact]
    public void InvasionReadyAtRally_TheCollectiveTrainsMoreRegiments()
    {
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: Ordered(s, true), Off: Ordered(s, false)));
        string all = string.Join(", ", res.Select(r => $"seed {r.s} {r.On}/{r.Off}"));
        Assert.True(res.Count(r => r.On > r.Off) >= 3, "on ordered more regiments than off on fewer than 3 seeds: " + all);
        int on = res.Sum(r => r.On), off = res.Sum(r => r.Off);
        Assert.True(on * 100 >= off * 130, $"on ordered {on} regiments in all, off {off} (floor 130%): {all}");
    }
}
