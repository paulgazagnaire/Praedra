using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.AI;

// Medium mirror seed 10 (Dev lead 2026-10-19): far ahead (about 255 systems against 10-25), the Empire AI spent its upkeep on warships (4239 of
// about 5700 on capital ships on day 2500) and trained regiments at 4 training facilities only (about 45 per 250 days), while uprisings killed
// 45-80 of its regiments per 250 days and the worlds left with none went to the Alliance by support (20-39 per 250 days): no winner by day
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

    // Day 1: one Imperial regiment lands on every Rebel system but the HQ, so the Empire is far ahead (FinishWhenAheadRatio) and holds about 37
    // worlds whose people support the Alliance, short of their garrisons. The Alliance gives no orders. Regiments the Empire orders by day 500,
    // and training facilities it owns then.
    private static (int Ordered, int Training) Play(int seed, bool on)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        var m = new AiMatch(h, AiLevels.For(AiLevel.Medium) with { GarrisonsFirst = on }, AiLevels.For(AiLevel.Medium), passive: Side.Alliance);
        var rebel = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance).Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal).ToList();
        bool taken = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (taken) return;
            taken = true;
            foreach (var s in rebel) if (s != h.HqSystem) h.Control.DebugApplyTroops(ctx, s, Side.Empire, "Imperial Army Regiment", 1);
        });
        int ordered = 0;
        while (h.Game.Day < 500 && h.Victory.Winner == null)
        {
            m.Step();
            foreach (var o in m.Empire.Submitted)
                if (o.Kind == "Build" && o.Detail.Contains("Imperial Army Regiment", StringComparison.Ordinal)) ordered += int.Parse(o.Detail.Split(' ')[0]);
        }
        return (ordered, h.Economy.Objects.Count(o => o.Class == "Training Facility" && h.Economy.OwnerOf(o) == Side.Empire));
    }

    // Measured (seeds 1-4): on, 95, 71, 87, 87 regiments ordered and 6, 4, 6, 5 training facilities; off, 75, 75, 69, 68 and 4, 5, 4, 5 (seeds 5
    // and 6: on 91 and 89, off 73 and 71). Floors: on orders more on at least 3 seeds and 10% more in all; on owns more training facilities on
    // at least 2 seeds.
    [Fact]
    public void GarrisonsFirst_AFarAheadSideTrainsMoreGarrisonTroops()
    {
        var res = MultiSeed.Play(new[] { 1, 2, 3, 4 }, s => (s, On: Play(s, true), Off: Play(s, false)));
        Assert.True(res.Count(r => r.On.Ordered > r.Off.Ordered) >= 3,
            "on ordered more regiments than off on fewer than 3 seeds: " + string.Join(", ", res.Select(r => $"seed {r.s} {r.On.Ordered}/{r.Off.Ordered}")));
        int on = res.Sum(r => r.On.Ordered), off = res.Sum(r => r.Off.Ordered);
        Assert.True(on * 100 >= off * 110, $"on ordered {on} regiments in all, off {off} (floor 110%)");
        Assert.True(res.Count(r => r.On.Training > r.Off.Training) >= 2, "on owns more training facilities than off on fewer than 2 seeds");
    }
}
