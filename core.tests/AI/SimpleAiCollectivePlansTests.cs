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
        Assert.False(p.RefitAway);
        Assert.Equal(0, p.NeutraliseElsewherePercent);
        Assert.False(p.RaidShieldsSeen);
        Assert.False(p.WreckShields);
        Assert.Equal(0, p.ShieldsFeltDays);
    }

    // Rooster 2026-11-27 (specs/engine/ai-notes/rooster-log.md): Neutralise anywhere at Medium and Hard, not at Easy.
    [Theory]
    [InlineData(AiLevel.Easy, false)]
    [InlineData(AiLevel.Medium, true)]
    [InlineData(AiLevel.Hard, true)]
    public void NeutraliseIsOnAtMediumAndHard(AiLevel level, bool on)
    {
        var p = AiLevels.For(level);
        Assert.Equal(on, p.Neutralise);
        Assert.Equal(on ? 100 : 0, p.NeutraliseElsewherePercent);
        Assert.Equal(on, p.RefitAway); // keeps the navy renewing while the warships shell (Dev lead after #416)
        Assert.Equal(on ? 120 : 0, p.ShieldsFeltDays); // Rooster 2026-11-27 evening: shields felt and seen, Wreckers aboard
        Assert.Equal(on, p.RaidShieldsSeen);
        Assert.Equal(on, p.WreckShields);
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
                var c = AiLevels.For(AiLevel.Medium) with { Neutralise = on, NeutraliseElsewherePercent = on ? 100 : 0, WreckShields = false, RaidShieldsSeen = false, ShieldsFeltDays = 0 };
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

    private static SimpleAiParams WalledHand() => AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 4, FortifyBatteries = 2 };
    private static SimpleAiParams ShieldWise(bool wreck) => AiLevels.For(AiLevel.Medium) with { ShieldsFeltDays = 120, RaidShieldsSeen = true, WreckShields = wreck };

    // m3-bombardment §2 (hits = max(0, P - S)) and m5-fog-and-hq §F2 (a fleet in orbit sees no facilities): with fog the Collective shelled
    // shields it did not know of for no hits again and again. ShieldsFeltDays (it reads the shields its last shelling met) and RaidShieldsSeen
    // (it shells only where its power beats them) cut those shellings (seed 1, walled Medium Hand, day 1200: 8 with the switches off, 1 on).
    [Fact]
    public void ShieldsFeltStopsShellingForNoHits()
    {
        int NoHits(SimpleAiParams c)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
            new AiMatch(host, WalledHand(), c).RunTo(1200);
            return host.Game.Events.OfType<Praedra.Core.Defenses.BombardmentStarted>().Count(b => b.Side == Side.Collective && b.S > 0 && b.Hits == 0);
        }
        int off = NoHits(ShieldWise(false) with { ShieldsFeltDays = 0, RaidShieldsSeen = false }), on = NoHits(ShieldWise(false));
        _out.WriteLine($"Collective shellings for no hits: off {off}, on {on}");
        Assert.True(off > 0, "the walled Hand's shields absorbed no shelling: the test no longer checks anything");
        Assert.True(on * 2 <= off, $"the shields felt did not halve the shellings for no hits ({off} -> {on})");
    }

    // m5-missions §Spying from orbit (#412): with WreckShields, Wreckers board armed Collective fleets and are launched from orbit on
    // Sabotage over shielded Hand worlds (seeds 1-4, walled Medium Hand, day 1200). One seed was too thin: since the AI's leader missions aim at
    // the last sighting, seed 1 swung between 0 and 3 orbit launches, so Wreckers must board in at least 2 games and launch in at least 1 (Rooster 2026-11-28).
    [Fact]
    public void WreckersRideFleetsAndSabotageShieldsFromOrbit()
    {
        var r = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var m = new AiMatch(host, WalledHand(), ShieldWise(true));
            int loads = 0, launches = 0;
            while (host.Game.Day < 1200 && host.Victory.Winner == null)
            {
                m.Step();
                loads += m.Collective.Submitted.Count(o => o.Kind == "LoadSpecialForces" && o.Detail.Contains("(wreck the shields)", StringComparison.Ordinal));
                launches += m.Collective.Submitted.Count(o => o.Kind == "MissionSabotage" && o.Detail.Contains("(wreck the shields)", StringComparison.Ordinal));
            }
            var orbit = host.Game.Events.OfType<Praedra.Core.Missions.MissionStarted>()
                .Count(x => x.Side == Side.Collective && x.MissionKind == Praedra.Core.Missions.MissionKind.Sabotage && x.Home == x.Target);
            return (seed, loads, launches, orbit);
        });
        foreach (var g in r) _out.WriteLine($"seed {g.seed}: Wrecker loads {g.loads}, orbit sabotage orders {g.launches}, started {g.orbit}");
        Assert.True(r.Count(g => g.loads > 0) >= 2, "Wreckers boarded a fleet in fewer than 2 of 4 games");
        Assert.True(r.Any(g => g.launches > 0 && g.orbit > 0), "no Sabotage was launched from orbit in 4 games");
    }

    // m1-clock-and-orders K12 with the shields felt: the notes ride in the AI memory of the save, so a loaded game decides as the uninterrupted one.
    [Trait("Category", "Play")]
    [Fact]
    public void ShieldsFeltGameContinuesTheSameAfterSaveAndLoad()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        new AiMatch(host, WalledHand(), ShieldWise(true)).RunTo(700);
        var loaded = GameHost.Load(host.SaveWithSettings());
        var a = new AiMatch(host, WalledHand(), ShieldWise(true));
        var b = new AiMatch(loaded, WalledHand(), ShieldWise(true));
        for (int i = 0; i < 60; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }

    // AiMemory.Ingest, ShieldsFelt (m3-bombardment §2 and §Resolution 4, 5.1; Ponytail on #426): a shelling with hits notes the shields S; one
    // with none notes what the shelling side knows, that fleets of their power do nothing there: their power now, at least the strike's P (the
    // batteries ground ships before P is counted). Nothing is noted while the note is not kept (ShieldsFeltDays 0), nor for the enemy's shellings.
    [Fact]
    public void AiMemory_NotesTheShieldsAShellingFelt()
    {
        var fleets = new ValueList<int>(new[] { 7 });
        GameEvent[] feed =
        {
            new Praedra.Core.Defenses.BombardmentStarted(0, 5, "Navalia", 7, Side.Collective, Praedra.Core.Defenses.BombardCategory.Civilian, 50, 40, 10, fleets),
            new Praedra.Core.Defenses.BombardmentStarted(1, 6, "Midway", 7, Side.Collective, Praedra.Core.Defenses.BombardCategory.Civilian, 30, 80, 0, fleets),
            new Praedra.Core.Defenses.BombardmentStarted(2, 6, "Charter", 9, Side.Hand, Praedra.Core.Defenses.BombardCategory.Civilian, 30, 80, 0, fleets),
        };
        var off = new AiMemory { Side = Side.Collective, Fog = true };
        off.Ingest(feed, _ => false, _ => 55);
        Assert.DoesNotContain(off.Notes, n => n.Kind == AiNoteKind.ShieldsFelt);
        var on = new AiMemory { Side = Side.Collective, Fog = true };
        on.KeepAtLeast(0, 0, 0, 0, 0, 0, shieldsFelt: 120);
        on.Ingest(feed, _ => false, _ => 55);
        Assert.Equal(new[] { ("Navalia", 40), ("Midway", 55) }, on.Notes.Where(n => n.Kind == AiNoteKind.ShieldsFelt).Select(n => (n.System!, n.Value)).ToArray());
    }
}
