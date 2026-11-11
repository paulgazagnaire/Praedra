using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// Medium mirror seed 10 (Dev lead 2026-10-19): far ahead (about 255 systems against 10-25), the Hand AI spent its upkeep on warships (4239 of
// about 5700 on capital ships on day 2500) and trained regiments at 4 training facilities only (about 45 per 250 days), while uprisings killed
// 45-80 of its regiments per 250 days and the worlds left with none went to the Collective by support (20-39 per 250 days): no winner by day
// 3000. rule: ai tuning, SimpleAiParams.GarrisonsFirst (DEFAULT true; New AI rule, no sheet); m2-uprisings-and-garrison §Required garrison N.
public class SimpleAiGarrisonsFirstTests
{
    [Fact]
    public void GarrisonsFirstIsOnByDefault()
    {
        var p = new SimpleAiParams();
        Assert.True(p.GarrisonsFirst);
        Assert.Equal(20, p.GarrisonShortPerTrainingFacility);
        Assert.Equal(12, p.MaxTrainingFacilities);
    }

    // Day 1: one Hand regiment lands on every Collective system but the HQ, so the Hand is far ahead (FinishWhenAheadRatio) and holds about 37
    // worlds whose people support the Collective, short of their garrisons. The Collective gives no orders. Regiments the Hand orders by day 500,
    // and training facilities it owns then.
    private static (int Ordered, int Training) Play(int seed, bool on)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { GarrisonsFirst = on }, AiLevels.For(AiLevel.Medium), passive: Side.Collective);
        var collective = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Collective).Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
        bool taken = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (taken) return;
            taken = true;
            foreach (var s in collective) if (s != h.HqSystem) h.Control.DebugApplyTroops(ctx, s, Side.Hand, "Tranches", 1);
        });
        int ordered = 0;
        while (h.Game.Day < 500 && h.Victory.Winner == null)
        {
            m.Step();
            foreach (var o in m.Hand.Submitted)
                if (o.Kind == "Build" && o.Detail.Contains("Tranches", StringComparison.Ordinal)) ordered += int.Parse(o.Detail.Split(' ')[0]);
        }
        return (ordered, h.Economy.Objects.Count(o => o.Class == "Training Facility" && h.Economy.OwnerOf(o) == Side.Hand));
    }

    // Measured (seeds 1-4): on, 95, 71, 87, 87 regiments ordered and 6, 4, 6, 5 training facilities; off, 75, 75, 69, 68 and 4, 5, 4, 5 (seeds 5
    // and 6: on 91 and 89, off 73 and 71). Floors: on orders more on at least 3 seeds and 10% more in all; on owns more training facilities on
    // at least 2 seeds.
    // Route thinning re-pin 2026-11-11 (Dev lead; Paul: "clean this mess of a route layout, reduce the number", 394 links down to 280): seeds
    // 1-6, on 88, 68, 91, 62, 87, 83 regiments and 6, 4, 6, 5, 5, 5 training facilities; off 69, 74, 80, 66, 74, 69 and 4, 5, 5, 5, 4, 4. On
    // orders more on 4 of 6 seeds (seeds 2 and 4 no longer do) and 111% in all. Floors: seeds 1-6 (was 1-4); on orders more on at least 4
    // (was 3 of 4) and 10% more in all (unchanged); on owns more training facilities on at least 2 (unchanged; measured 4).
    [Trait("Category", "Play")]
    [Fact]
    public void GarrisonsFirst_AFarAheadSideTrainsMoreGarrisonTroops()
    {
        // Whole-batch floors 2026-11-11 (AI trainer, for the Dev lead: map changes kept breaking the per-seed floors while the switch held). Seeds
        // 1-24, same AI code: on orders more than off on 22 of 24 (394 links: 21), 122% in all on both maps; seeds 2 and 4 here, 15 and 16 on
        // the old map, are the misses. Seeds 1-12 on the 280 links: on more on 10 (394 links: 12), 118% in all (128%), more training
        // facilities on 9 (9). Floors: on orders more on at least 8 of 12, 110% in all, more training facilities on at least 6.
        var res = MultiSeed.Play(Enumerable.Range(1, 12).ToArray(), s => (s, On: Play(s, true), Off: Play(s, false)));
        string all = string.Join(", ", res.Select(r => $"seed {r.s} {r.On.Ordered}/{r.Off.Ordered}"));
        Assert.True(res.Count(r => r.On.Ordered > r.Off.Ordered) >= 8, "on ordered more regiments than off on fewer than 8 of 12 seeds: " + all);
        int on = res.Sum(r => r.On.Ordered), off = res.Sum(r => r.Off.Ordered);
        Assert.True(on * 100 >= off * 110, $"on ordered {on} regiments in all, off {off} (floor 110%)");
        Assert.True(res.Count(r => r.On.Training > r.Off.Training) >= 6, "on owns more training facilities than off on fewer than 6 of 12 seeds");
    }
}
