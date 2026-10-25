using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using static Praedra.Core.Tests.Fog.FogWorld;
using Praedra.Core.Tests.Control;

namespace Praedra.Core.Tests.Fog;

// Fog of war tests FW1 to FW15 of m5-fog-and-hq §Tests, on Map M (FogWorld). Fleet F = one Dodger (speed 80): a hop of 100
// map units on a tier 3 lane takes 16 days.
public class FogTests
{
    // m5-fog-and-hq FW1
    [Fact]
    public void FW1_Day0Pictures()
    {
        // B4 2026-10-18: with SetupRevealsFacilities on (Classic; DEFAULT on since 2026-10-18, explicit here).
        var w = new FogWorld(new GameSettings { Fog = new FogParams { SetupRevealsFacilities = true } });
        Assert.Equal(KnowledgeLevel.Charted, w.Fog.Level(Side.Collective, C0));
        var c0 = w.Pic(Side.Collective, C0)!;
        Assert.Equal(Side.Hand, c0.Owner!.Value.Owner);
        Assert.Equal(0, c0.Owner.Stamp);
        // B4 2026-10-17: the Setup reveal now writes group C (facilities) for start_explored_all systems, never D or E (m5-fog-and-hq §F4 1,
        // B4 §11); before B4 this asserted that C was not written.
        Assert.Equal(0, c0.Facilities!.Stamp);
        Assert.Null(c0.Ground); Assert.Null(c0.Fleets);
        Assert.Null(new FogWorld(new GameSettings { Fog = new FogParams { SetupRevealsFacilities = false } }).Pic(Side.Collective, C0)!.Facilities); // switch off: no C, as before B4
        Assert.Null(w.Know(Side.Collective, C0)!.LastLookDay);
        Assert.Equal(KnowledgeLevel.Unknown, w.Fog.Level(Side.Collective, R1));
        Assert.Null(w.Pic(Side.Collective, R1)); // the picture exposes nothing (owner not available)
        Assert.Equal(KnowledgeLevel.Held, w.Fog.Level(Side.Collective, R2));
    }

    // m5-fog-and-hq FW2
    [Fact]
    public void FW2_StandingEnemyFleetIsNotSeenWithoutAccess()
    {
        var w = new FogWorld();
        w.Step(40);
        Assert.Equal(KnowledgeLevel.Unknown, w.Fog.Level(Side.Collective, R1));
        Assert.Null(w.Pic(Side.Collective, R1));
        Assert.DoesNotContain(w.EventsFor(Side.Collective), e => e is FleetSighted fs && fs.System == R1);
        Assert.Empty(w.Fog.Sightings(Side.Collective));
    }

    private static (FogWorld W, int F) Fw3(GameSettings? s = null)
    {
        var w = new FogWorld(s, r3Neutral: true);
        int f = w.Fleet(Side.Collective, R2, Corvette, 1);
        w.H.Fleets.SubmitMove(Side.Collective, f, R4);
        return (w, f);
    }

    // m5-fog-and-hq FW3
    [Fact]
    public void FW3_PassingFleetExploresAndTheLookAges()
    {
        var (w, _) = Fw3();
        w.StepTo(15);
        Assert.Equal(KnowledgeLevel.Unknown, w.Fog.Level(Side.Collective, R3));
        w.StepTo(16);
        var k = w.Know(Side.Collective, R3)!;
        Assert.True(k.Explored);
        var p = w.Pic(Side.Collective, R3)!;
        Assert.Null(p.Owner!.Value.Owner); // neutral
        Assert.Equal(16, p.Owner.Stamp);
        Assert.Contains(w.EventsFor(Side.Collective), e => e is SystemExplored x && x.System == R3 && x.How == RevealKind.Presence && x.Day == 16);
        Assert.Null(p.Facilities);
        Assert.Null(p.Ground);
        w.StepTo(17);
        Assert.Equal(KnowledgeLevel.Observed, w.Fog.Level(Side.Collective, R3));
        Assert.Equal(1, w.Pic(Side.Collective, R3)!.Owner!.AgeDays);
    }

    // m5-fog-and-hq FW4
    [Fact]
    public void FW4_ArrivalAtAHeldSystem()
    {
        var (w, f) = Fw3();
        Assert.True(w.Know(Side.Collective, R4)!.Explored); // from day 0
        w.StepTo(32);
        Assert.Equal(R4, w.Game.State.Fleets[f].At);
        Assert.Equal(KnowledgeLevel.Held, w.Fog.Level(Side.Collective, R4));
    }

    // m5-fog-and-hq FW5
    [Fact]
    public void FW5_ObservedForThirtyDaysThenCharted()
    {
        var (w, _) = Fw3();
        w.StepTo(46);
        Assert.Equal(KnowledgeLevel.Observed, w.Fog.Level(Side.Collective, R3)); // 46 - 16 = 30 <= 30
        w.StepTo(47);
        Assert.Equal(KnowledgeLevel.Charted, w.Fog.Level(Side.Collective, R3));
        var b = w.Pic(Side.Collective, R3)!.Owner!;
        Assert.Null(b.Value.Owner);
        Assert.Equal(16, b.Stamp);
        Assert.Equal(31, b.AgeDays);
    }

    // m5-fog-and-hq FW6
    [Fact]
    public void FW6_ChangeUnseenWithoutPresence()
    {
        var (w, _) = Fw3();
        w.StepTo(20);
        int from = w.Game.Events.Count;
        w.H.Control.SubmitCapture(Side.Hand, R3) /* (B5a, 2026-10-17) Derived control: troops take the world */;
        w.Step(); // Step 20, day 21
        Assert.Equal(Side.Hand, w.H.Fleets.OwnerOf(R3));
        var b = w.Pic(Side.Collective, R3)!.Owner!;
        Assert.Null(b.Value.Owner);
        Assert.Equal(16, b.Stamp);
        Assert.DoesNotContain(w.EventsFor(Side.Collective), e => e.Index >= from && e is SystemCaptured);
    }

    // m5-fog-and-hq FW7 (owner part; the level part contradicts §N1, see the build notes)
    [Fact]
    public void FW7_CoreReportTellsBothSides()
    {
        var w = new FogWorld();
        w.StepTo(4);
        w.H.Control.SubmitCapture(Side.Collective, C0) /* (B5a, 2026-10-17) Derived control: troops take the world */;
        w.Step(); // Step 4, day 5
        foreach (var side in new[] { Side.Hand, Side.Collective })
        {
            var b = w.Pic(side, C0)!.Owner!;
            Assert.Equal(Side.Collective, b.Value.Owner);
            Assert.Equal(5, b.Stamp);
        }
    }

    // m5-fog-and-hq FW8
    [Fact]
    public void FW8_LossNoticeTellsTheLoser()
    {
        var w = new FogWorld();
        w.StepTo(9);
        w.H.Control.SubmitCapture(Side.Hand, R2) /* (B5a, 2026-10-17) Derived control: troops take the world */;
        w.Step(); // Step 9, day 10
        var b = w.Pic(Side.Collective, R2)!.Owner!;
        Assert.Equal(Side.Hand, b.Value.Owner);
        Assert.Equal(10, b.Stamp);
        Assert.Contains(w.EventsFor(Side.Collective), e => e is SystemCaptured c && c.System == R2);
        Assert.Contains(w.EventsFor(Side.Hand), e => e is SystemCaptured c && c.System == R2);
    }

    private static (FogWorld W, int F, int D) Fw9()
    {
        var w = new FogWorld();
        int f = w.Fleet(Side.Collective, R2, Corvette, 1);
        w.H.Fleets.SubmitMove(Side.Collective, f, R1);
        w.StepTo(16);
        return (w, f, 16);
    }

    // m5-fog-and-hq FW9
    [Fact]
    public void FW9_ArrivalAtAnEnemySystemShowsItsFleets()
    {
        var (w, _, d) = Fw9();
        var p = w.Pic(Side.Collective, R1)!;
        Assert.Equal(Side.Hand, p.Owner!.Value.Owner);
        Assert.Equal(30, p.Owner.Value.CollectiveSupport);
        var e = p.Fleets!;
        Assert.Equal(d, e.Stamp);
        var fp = Assert.Single(e.Value);
        Assert.Equal(Side.Hand, fp.Side);
        Assert.Equal(3, fp.Ships[Isd]);
        Assert.Single(fp.Ships);
        Assert.Null(p.Facilities);
        Assert.Null(p.Ground);
        var seen = w.EventsFor(Side.Collective).OfType<FleetSighted>().Where(x => x.System == R1).ToList();
        Assert.NotEmpty(seen);
        Assert.Equal(Side.Hand, seen[0].FleetSide);
        Assert.Null(typeof(FleetSighted).GetProperty("Fleet")); // no fleet id in the sighting
    }

    private static FogWorld Fw10(GameSettings? s = null)
    {
        var w = new FogWorld(s);
        int g = w.Fleet(Side.Hand, R4, Isd, 2);
        w.H.Fleets.SubmitMove(Side.Hand, g, R2);
        w.StepTo(16); // placed at R3 on day 16, not stopped (no Collective fleet)
        return w;
    }

    // m5-fog-and-hq FW10
    [Fact]
    public void FW10_PassingEnemyFleetIsLogged()
    {
        var w = Fw10();
        var s = Assert.Single(w.Fog.Sightings(Side.Collective), x => x.System == R3);
        Assert.Equal(16, s.Day);
        Assert.Equal(SightingKind.Fleet, s.Kind);
        Assert.Equal(Side.Hand, s.Side);
        Assert.Equal(2, s.Detail!.Ships[Isd]);
        Assert.Contains(w.Events<FleetPassed>(), e => e.System == R3);
        var e = w.Pic(Side.Collective, R3)!.Fleets!;
        Assert.Equal(16, e.Stamp);
        Assert.Empty(e.Value); // nobody stands there at 5.4
    }

    // m5-fog-and-hq FW11
    [Fact]
    public void FW11_ClassicPassingFleetsNeitherLogNorExplore()
    {
        var classic = new GameSettings { Fog = new FogParams { Ruleset = FogRuleset.Classic } };
        var w = Fw10(classic);
        Assert.Empty(w.Fog.Sightings(Side.Collective));
        var (w2, _) = Fw3(classic);
        w2.StepTo(17);
        Assert.False(w2.Fog.IsExplored(Side.Collective, R3));
        Assert.Equal(KnowledgeLevel.Unknown, w2.Fog.Level(Side.Collective, R3));
        // Classic exposes no ages and has no Observed level (§Rule (New), last paragraph)
        Assert.Null(w2.Pic(Side.Collective, C0)!.Owner!.AgeDays);
        Assert.Equal(KnowledgeLevel.Charted, w2.Fog.Level(Side.Collective, C0));
    }

    private static List<string> Run(ulong seed)
    {
        var (w, _) = Fw3(new GameSettings());
        var g = w.Fleet(Side.Hand, R4, Isd, 2);
        w.H.Fleets.SubmitMove(Side.Hand, g, R2);
        w.StepTo(60);
        return w.Game.Events.Select(e => e.ToString()).Append(w.Fog.KnowledgeHash(Side.Hand)).Append(w.Fog.KnowledgeHash(Side.Collective)).Append(w.H.StateHash()).ToList();
    }

    // m5-fog-and-hq FW12
    [Fact]
    public void FW12_SameOrdersSameSeedSameEverything()
    {
        var a = Run(1); var b = Run(1); var c = Run(1);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    // m5-fog-and-hq FW13
    [Fact]
    public void FW13_SaveAndLoadKeepPicturesAndLogs()
    {
        var (whole, _) = Fw3();
        whole.StepTo(50);
        var (first, _) = Fw3();
        first.StepTo(20);
        var loaded = GameHost.Load(first.H.Save(), first.H.Settings, FogWorld.Data);
        Assert.Equal(first.H.StateHash(), loaded.StateHash());
        while (loaded.Game.Day < 50) loaded.Game.Step();
        Assert.Equal(whole.H.StateHash(), loaded.StateHash());
        foreach (var side in new[] { Side.Hand, Side.Collective })
            Assert.Equal(whole.Fog.KnowledgeHash(side), loaded.Fog.KnowledgeHash(side));
    }

    // m5-fog-and-hq FW14
    [Fact]
    public void FW14_OldFleetPictureStaysAndAges()
    {
        var (w, f, d) = Fw9();
        if (w.Game.State.Fleets.TryGetValue(f, out var fl) && fl.At == R1) w.H.Fleets.SubmitMove(Side.Collective, f, R2); // F leaves on day d + 1
        w.StepTo(d + 5);
        w.H.Fleets.SubmitMove(Side.Hand, w.E1, C0); // E1 leaves on day d + 5
        w.StepTo(d + 30);
        var e = w.Pic(Side.Collective, R1)!.Fleets!;
        Assert.Equal(d, e.Stamp);
        Assert.Equal(30, e.AgeDays);
        Assert.Equal(3, Assert.Single(e.Value).Ships[Isd]);
    }

    // m5-fog-and-hq FW15
    [Fact]
    public void FW15_SightingsAreDroppedAfterTheWindow()
    {
        var w = Fw10();
        int d = 16;
        w.StepTo(d + 30);
        Assert.Contains(w.Fog.Sightings(Side.Collective), s => s.System == R3 && s.Day == d);
        w.StepTo(d + 31);
        Assert.DoesNotContain(w.Fog.Sightings(Side.Collective), s => s.System == R3 && s.Day == d);
    }
}
