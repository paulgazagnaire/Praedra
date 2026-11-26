using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// rule: ai tuning, the adaptive commander (AI trainer 2026-11-26; Paul 2026-11-26 chose "Commander" and asked for different strategies per
// front on Medium and Hard). Design: specs/engine/ai-notes/2026-11-26-adaptive-commander.md. SimpleAiParams.FortifyWorlds (plan 1 of both
// sides: shields and batteries on the key worlds) and SimpleAiParams.FrontPlans (the Collective's front plans Campaign, Bleed, Starve).
public class SimpleAiCommanderTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiCommanderTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void FortifyAndFrontPlansAreOffByDefault()
    {
        var p = new SimpleAiParams();
        Assert.Equal(0, p.FortifyWorlds);
        Assert.False(p.FrontPlans);
    }

    private static (int Shields, int Batteries) DefencesAt(GameHost host, string system, Side side)
    {
        int sh = 0, bat = 0;
        foreach (var o in host.Economy.Objects)
        {
            if (o.System != system || o.Category != ObjectCategory.Facility || !host.Data.Defenses.TryGetClass(o.Class, out var c)) continue;
            if (host.Economy.OwnerOf(o) != side) continue;
            if (c.Job == DefenseJob.ShieldGenerator) sh++; else bat++;
        }
        return (sh, bat);
    }

    // m3-assault check 4: two complete shields forbid an assault; §1: each battery shoots one landing regiment. Seed 1 MedE, Medium mirror: with
    // FortifyWorlds 1 the Hand orders 2 shields and a battery for Charter (its capital) right after its first yards; without, it orders none.
    [Fact]
    public void TheHandFortifiesCharterFirst()
    {
        var off = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        var on = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        string charter = Praedra.Core.Control.ControlModule.CapitalSystem;
        var start = DefencesAt(on, charter, Side.Hand);
        var mOff = new AiMatch(off, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium));
        var mOn = new AiMatch(on, AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 1 }, AiLevels.For(AiLevel.Medium));
        mOff.RunTo(60); mOn.RunTo(60);
        var a = DefencesAt(off, charter, Side.Hand); var b = DefencesAt(on, charter, Side.Hand);
        _out.WriteLine($"Charter shields/batteries: start {start}, off {a}, on {b}");
        Assert.Equal(start, a);
        Assert.True(b.Shields >= 2 && b.Batteries >= 1, $"Charter has {b.Shields} shields and {b.Batteries} batteries with Fortify");
    }

    // The Collective fortifies its Hearth's world (fog: its own HQ object, m5-fog-and-hq §N5), seeds 1-4, by day 120.
    [Fact]
    public void TheCollectiveFortifiesItsHearthWorld()
    {
        var hit = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 1 });
            m.RunTo(120);
            var hq = host.Fog.ViewFor(Side.Collective).KnownHq?.System;
            var d = hq == null ? (0, 0) : DefencesAt(host, hq, Side.Collective);
            return $"seed {seed} Hearth {hq} {d}";
        });
        foreach (var h in hit) _out.WriteLine(h);
        Assert.True(hit.Count(h => !h.EndsWith("(0, 0)")) >= 3, "the Collective fortified the Hearth's world in fewer than 3 of 4 games");
    }

    // Paul 2026-11-26: different strategies per front. Two fronts at once with FrontPlans run different plans whenever both are up (seeds 1-8,
    // Medium mirror, the Collective with CampaignsAtOnce 2), and over the 8 games every plan is used at least once.
    [Fact]
    public void TwoFrontsRunDifferentPlans()
    {
        var plans = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium) with { CampaignsAtOnce = 2, FrontPlans = true });
            var used = new HashSet<AiFrontPlan>();
            int both = 0, same = 0;
            while (host.Game.Day < 400)
            {
                m.Step();
                var f = m.Collective.FrontsNow;
                foreach (var x in f) used.Add(x.Plan);
                if (f.Count == 2) { both++; if (f[0].Plan == f[1].Plan) same++; }
            }
            return (used, both, same);
        });
        foreach (var p in plans) _out.WriteLine($"plans {string.Join(",", p.used)}; days with 2 fronts {p.both}, same plan {p.same}");
        Assert.All(plans, p => Assert.Equal(0, p.same));
        foreach (var plan in Enum.GetValues<AiFrontPlan>()) Assert.Contains(plans, p => p.used.Contains(plan));
    }

    // A front's plan is saved with the game (AiCampaign.Plan): a loaded game keeps its fronts and plays on exactly as the uninterrupted one.
    [Fact]
    public void FrontPlansAreSavedWithTheGame()
    {
        var c = AiLevels.For(AiLevel.Medium) with { CampaignsAtOnce = 2, FrontPlans = true };
        var host = NewGame.Create(new NewGameOptions { Seed = 2, Preset = StartPreset.MedE });
        var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), c);
        m.RunTo(150);
        var before = m.Collective.FrontsNow;
        Assert.NotEmpty(before);
        var loaded = GameHost.Load(host.SaveWithSettings());
        var a = new AiMatch(host, AiLevels.For(AiLevel.Medium), c);
        var b = new AiMatch(loaded, AiLevels.For(AiLevel.Medium), c);
        var mem = loaded.AiMemoryFor(Side.Collective, true);
        var saved = new[] { mem.Campaign! }.Concat(mem.MoreCampaigns ?? new()).Where(x => x.Sector.Length > 0).Select(x => (x.Sector, x.Plan, x.Strike > 0, x.Number)).ToList();
        Assert.Equal(before, saved);
        for (int i = 0; i < 30; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }
}
