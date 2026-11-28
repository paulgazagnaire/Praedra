using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
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
        Assert.Equal(0, p.ShellHoles);
        Assert.Equal(0, p.RoomFloor);
    }

    // AI trainer 2026-11-27 (night log 2026-11-27-commander-night-log.md): Medium and Hard wall the capital, shell shields they can beat,
    // and pick front plans with memory; Easy does none of it; the cut plans stay off at every level.
    [Theory]
    [InlineData(AiLevel.Easy, false)]
    [InlineData(AiLevel.Medium, true)]
    [InlineData(AiLevel.Hard, true)]
    public void TheCommanderPlansAreOnAtMediumAndHard(AiLevel level, bool on)
    {
        var p = AiLevels.For(level);
        Assert.Equal(on ? 1 : 0, p.FortifyWorlds);
        Assert.Equal(on, p.SiegeShields);
        Assert.Equal(on, p.FrontPlans);
        Assert.Equal(on, p.FrontMemory);
        Assert.False(p.HandFronts);
        Assert.Equal(0, p.RoomFloor);
        Assert.Equal(0, p.ShellHoles);
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
        var mOff = new AiMatch(off, AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 0 }, AiLevels.For(AiLevel.Medium));
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

    // The Shell game (the Collective's plan 2): with ShellHoles 2 the Collective walls bolt-holes besides the Hearth's world, so in most games
    // three worlds stand with 2 complete shields (m3-assault check 4) by day 700 in most games (seeds 1-4, Medium mirror). Without it only the
    // Hearth's worlds are walled (more than one only when the Hearth has moved).
    [Fact]
    public void TheCollectiveWallsBoltHoles()
    {
        int Walled(GameHost host)
        {
            var bySystem = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in host.Economy.Objects)
                if (o.Completed && o.Category == ObjectCategory.Facility && host.Economy.OwnerOf(o) == Side.Collective
                    && host.Data.Defenses.TryGetClass(o.Class, out var c) && c.Job == DefenseJob.ShieldGenerator)
                    bySystem[o.System!] = bySystem.GetValueOrDefault(o.System!) + 1;
            return bySystem.Values.Count(n => n >= 2);
        }
        var walled = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var on = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var off = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            new AiMatch(on, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 1, ShellHoles = 2 }).RunTo(700);
            new AiMatch(off, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 1 }).RunTo(700);
            return (on: Walled(on), off: Walled(off));
        });
        foreach (var w in walled) _out.WriteLine($"walled worlds: with bolt-holes {w.on}, without {w.off}");
        Assert.True(walled.Count(w => w.on >= 3) >= 3, "the Collective walled its Hearth's world and 2 bolt-holes in fewer than 3 of 4 games");
        Assert.True(walled.Sum(w => w.on) > walled.Sum(w => w.off), "bolt-holes added no walled world");
    }

    // The economy guard (AI trainer 2026-11-27, Paul: "a management layer under each of them"): with GuardRaidedDays the Medium Hand walls
    // its shelled mine worlds (2 shields each) by day 700: in seeds 1-4 more such worlds than without it in at least half the games, and none
    // fewer (a mine world with no free energy slot cannot take a shield). Off by default.
    [Fact]
    public void TheHandShieldsItsShelledMineWorlds()
    {
        Assert.Equal(0, new SimpleAiParams().GuardRaidedDays);
        int Guarded(GameHost host)
        {
            var eco = host.Economy;
            var bySystem = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in eco.Objects)
                if (o.Completed && o.Category == ObjectCategory.Facility && eco.OwnerOf(o) == Side.Hand && o.System != ControlModule.CapitalSystem
                    && host.Data.Defenses.TryGetClass(o.Class, out var c) && c.Job == DefenseJob.ShieldGenerator)
                    bySystem[o.System!] = bySystem.GetValueOrDefault(o.System!) + 1;
            return bySystem.Count(kv => kv.Value >= 2 && eco.Objects.Any(o => o.System == kv.Key && o.Class == "Mine" && eco.OwnerOf(o) == Side.Hand));
        }
        // m5-fog-and-hq §N6e pinned off (MultiSeed.GuardsSeeInboundOff): with the guards' draw the guard walled more mine worlds in 1 game of 4
        // (seed 3: 2 vs 0; seeds 1, 2, 4: 0 vs 0), and alone 4 worlds in seeds 1 and 3.
        var guarded = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var on = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE, Settings = MultiSeed.GuardsSeeInboundOff });
            var off = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE, Settings = MultiSeed.GuardsSeeInboundOff });
            new AiMatch(on, AiLevels.For(AiLevel.Medium) with { GuardRaidedDays = 360 }, AiLevels.For(AiLevel.Medium)).RunTo(700);
            new AiMatch(off, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium)).RunTo(700);
            // the guard runs on its own too, without walled key worlds (FortifyWorlds 0; Ponytail's #431 review)
            var alone = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE, Settings = MultiSeed.GuardsSeeInboundOff });
            new AiMatch(alone, AiLevels.For(AiLevel.Medium) with { GuardRaidedDays = 360, FortifyWorlds = 0 }, AiLevels.For(AiLevel.Medium)).RunTo(700);
            return (on: Guarded(on), off: Guarded(off), alone: Guarded(alone));
        });
        foreach (var g in guarded) _out.WriteLine($"walled mine worlds: with the guard {g.on}, without {g.off}, the guard without Fortify {g.alone}");
        Assert.True(guarded.Sum(g => g.alone) > 0, "the guard walled no mine world without FortifyWorlds");
        Assert.All(guarded, g => Assert.True(g.on >= g.off, $"the guard walled {g.on} mine worlds, {g.off} without it"));
        Assert.True(guarded.Count(g => g.on > g.off) >= 2, "the guard walled more mine worlds in fewer than 2 of 4 games");
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
