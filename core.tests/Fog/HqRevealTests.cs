using System.Reflection;
using System.Text.Json;
using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using Praedra.Core.Support;
using static Praedra.Core.Tests.Fog.FogWorld;

namespace Praedra.Core.Tests.Fog;

// What reveals the HQ (HR1 to HR11) and fairness (FN1, FN3 to FN5) of m5-fog-and-hq §Tests, on Map M (HQ at R2).
public class HqRevealTests
{
    private static int Espionage(FogWorld w, Side side, string target)
    {
        int stamp = 0;
        w.InStep(ctx => { w.Fog.EspionageSuccess(ctx, side, target); stamp = ctx.Day + 1; });
        w.Step();
        return stamp;
    }

    // m5-fog-and-hq HR1
    [Fact]
    public void HR1_PresenceDoesNotRevealTheHq()
    {
        var w = new FogWorld(beforeSetup: x => x.Fleet(Side.Empire, R2, Isd, 1));
        w.Step(100);
        var p = w.Pic(Side.Empire, R2)!;
        Assert.Equal(Side.Alliance, p.Owner!.Value.Owner);
        Assert.NotNull(p.Fleets);
        Assert.False(p.HqHere);
        Assert.Null(w.Fog.KnownHq(Side.Empire));
    }

    // m5-fog-and-hq HR2
    [Fact]
    public void HR2_PresenceRevealsTheHqWhenSwitchedOn()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { PresenceRevealsHq = true } });
        w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        w.StepTo(16);
        Assert.True(w.Pic(Side.Empire, R2)!.HqHere);
        Assert.Equal(new KnownHqInfo(R2, 16), w.Fog.KnownHq(Side.Empire));
    }

    private static (FogWorld W, int Seen) Hr3()
    {
        var w = new FogWorld();
        w.StepTo(29);
        int seen = Espionage(w, Side.Empire, R2); // FE1 on day 30
        Assert.Equal(30, seen);
        w.StepTo(31);
        w.Fog.SubmitMoveHq(Side.Alliance, R3); // arrives day 51
        return (w, seen);
    }

    // m5-fog-and-hq HR3
    [Fact]
    public void HR3_TheEmpireIsNotToldWhenTheHqMoves()
    {
        var (w, _) = Hr3();
        w.StepTo(80);
        Assert.Equal(R3, w.H.HqSystem);
        Assert.Equal(new KnownHqInfo(R2, 30), w.Fog.KnownHq(Side.Empire));
        Assert.Equal(50, w.Game.Day - w.Fog.KnownHq(Side.Empire)!.Stamp);
        Assert.False(w.Know(Side.Empire, R3)?.HqHere ?? false);
        Assert.DoesNotContain(w.EventsFor(Side.Empire), e => e is HqDeparted or HqArrived);
        Assert.DoesNotContain(w.EventsFor(Side.Empire), e => e is SupportChanged s && s.Cause == SupportCause.HqMove);
    }

    // m5-fog-and-hq HR4
    [Fact]
    public void HR4_FindingTheHqClearsTheOldSighting()
    {
        var (w, _) = Hr3();
        w.StepTo(79);
        int stamp = Espionage(w, Side.Empire, R3);
        Assert.True(w.Know(Side.Empire, R3)!.HqHere);
        Assert.False(w.Know(Side.Empire, R2)!.HqHere);
        Assert.Equal(new KnownHqInfo(R3, stamp), w.Fog.KnownHq(Side.Empire));
        Assert.Equal(80, stamp);
    }

    // m5-fog-and-hq HR5
    [Fact]
    public void HR5_LookingAgainAtTheOldSystemShowsNoHq()
    {
        var (w, _) = Hr3();
        w.StepTo(59);
        Espionage(w, Side.Empire, R2);
        Assert.False(w.Know(Side.Empire, R2)!.HqHere);
        Assert.Null(w.Fog.KnownHq(Side.Empire));
    }

    // m5-fog-and-hq HR6
    [Fact]
    public void HR6_NoSectorClueByDefault()
    {
        var w = new FogWorld();
        Espionage(w, Side.Empire, R3);
        Assert.Empty(w.Fog.Clues(Side.Empire));
        Assert.DoesNotContain(w.Game.State.Fog.Empire.Systems.Values, k => k.HqHere);
    }

    // m5-fog-and-hq HR7
    [Fact]
    public void HR7_SectorClueWhenSwitchedOn()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { SectorSuspicion = SectorSuspicion.OnEspionage } });
        int stamp = Espionage(w, Side.Empire, R3);
        Assert.Equal(new HqSectorClue("SR", stamp), Assert.Single(w.Fog.Clues(Side.Empire)));
        Assert.DoesNotContain(w.Game.State.Fog.Empire.Systems.Values, k => k.HqHere);
        Assert.Empty(w.Fog.Clues(Side.Alliance));
        Assert.Contains(w.EventsFor(Side.Empire), e => e is HqSectorClueGiven);
        Assert.DoesNotContain(w.EventsFor(Side.Alliance), e => e is HqSectorClueGiven);
    }

    // m5-fog-and-hq HR8
    [Fact]
    public void HR8_TheEmpireViewDoesNotHoldTheHq()
    {
        var w = new FogWorld();
        w.Step(10);
        var v = w.Fog.ViewFor(Side.Empire);
        Assert.Null(v.KnownHq);
        foreach (var s in w.H.Data.Galaxy.Systems) Assert.False(v.Picture(s.Name)?.HqHere ?? false);
        // no member of the view returns or holds the HQ state, the game or the module
        var forbidden = new[] { typeof(HqState), typeof(Game), typeof(GameState), typeof(GameHost), typeof(FogModule), typeof(FogState), typeof(GameRng) };
        foreach (var m in typeof(KnowledgeView).GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = m switch { PropertyInfo p => p.PropertyType, MethodInfo mi => mi.ReturnType, FieldInfo f => f.FieldType, _ => null };
            if (t != null) Assert.DoesNotContain(t, forbidden);
        }
    }

    // m5-fog-and-hq HR9 and HR10
    [Fact]
    public void HR9_HR10_SupportDropIsSeenOnlyWithLiveAccess()
    {
        var w = new FogWorld(beforeSetup: x => x.Fleet(Side.Empire, R4, Isd, 1));
        w.Fog.SubmitMoveHq(Side.Alliance, R3); // arrives day 20
        w.StepTo(5);
        Assert.Equal(70, w.Pic(Side.Empire, R4)!.Owner!.Value.AllianceSupport);
        w.StepTo(20);
        var b = w.Pic(Side.Empire, R4)!.Owner!;
        Assert.Equal(60, b.Value.AllianceSupport);
        Assert.Equal(20, b.Stamp);
        Assert.DoesNotContain(w.EventsFor(Side.Empire), e => e is HqArrived);
        Assert.False(w.Pic(Side.Empire, R4)!.HqHere);

        var none = new FogWorld(); // HR10: no Empire fleet at R4
        none.Fog.SubmitMoveHq(Side.Alliance, R3);
        none.StepTo(20);
        Assert.Null(none.Pic(Side.Empire, R4));
        Assert.Equal(60, none.H.Economy.SupportOf(R4, Side.Alliance));
    }

    // m5-fog-and-hq HR11
    [Fact]
    public void HR11_DetectedMissionGoesToTheDefenderOnly()
    {
        var w = new FogWorld();
        w.InStep(ctx => w.Fog.OnMissionDetected(ctx, Side.Alliance, R3));
        w.Step();
        var s = Assert.Single(w.Fog.Sightings(Side.Alliance));
        Assert.Equal((SightingKind.AgentDetected, R3, Side.Empire), (s.Kind, s.System, s.Side));
        Assert.Empty(w.Fog.Sightings(Side.Empire));
        Assert.DoesNotContain(w.EventsFor(Side.Empire), e => e is AgentDetected);
    }

    // m5-fog-and-hq FN1
    [Fact]
    public void FN1_EmpireKnowledgeDoesNotDependOnWhereTheHqIs()
    {
        var a = new FogWorld(hq: R2);
        var b = new FogWorld(hq: R4);
        foreach (var w in new[] { a, b }) w.H.Fleets.SubmitMove(Side.Empire, w.E1, R2);
        for (int day = 0; day < 60; day++)
        {
            Assert.Equal(a.Fog.KnowledgeHash(Side.Empire), b.Fog.KnowledgeHash(Side.Empire));
            if (day == 30) foreach (var w in new[] { a, b }) w.H.Fleets.SubmitMove(Side.Empire, w.E1, R1);
            a.Step(); b.Step();
        }
        string Accepted(FogWorld w) => string.Join(";", w.EventsFor(Side.Empire).OfType<OrderAccepted>().Select(e => e.ToString()));
        Assert.Equal(Accepted(a), Accepted(b));
        Assert.NotEqual(a.H.StateHash(), b.H.StateHash()); // the worlds do differ
    }

    // m5-fog-and-hq FN3 (partial: the M6 AI assembly does not exist; the view itself exposes no truth type, see HR8) and FN4
    [Fact]
    public void FN4_EmpireFeedHasNoAllianceOrdersNorHqMoves()
    {
        var w = new FogWorld();
        int f = w.Fleet(Side.Alliance, R2, Corvette, 1);
        var allianceSeqs = new HashSet<long> { w.Fog.SubmitMoveHq(Side.Alliance, R3), w.H.Fleets.SubmitMove(Side.Alliance, f, R4) };
        w.StepTo(40);
        var feed = w.EventsFor(Side.Empire);
        Assert.DoesNotContain(feed, e => e is OrderAccepted oa && allianceSeqs.Contains(oa.Sequence));
        Assert.DoesNotContain(feed, e => e is HqDeparted or HqArrived);
        Assert.Contains(w.EventsFor(Side.Alliance), e => e is OrderAccepted oa && allianceSeqs.Contains(oa.Sequence));
        Assert.Contains(w.EventsFor(Side.Alliance), e => e is HqArrived);
    }

    // m5-fog-and-hq FN5
    [Fact]
    public void FN5_NoEnemyFleetIdsInTheAllianceKnowledge()
    {
        var w = new FogWorld();
        int g = w.Fleet(Side.Empire, R4, Isd, 2);
        w.H.Fleets.SubmitMove(Side.Empire, g, R2);
        int f = w.Fleet(Side.Alliance, R2, Corvette, 1);
        w.H.Fleets.SubmitMove(Side.Alliance, f, R1);
        w.StepTo(20);
        Assert.NotEmpty(w.Fog.Sightings(Side.Alliance));
        var json = JsonSerializer.Serialize(w.Game.State.Fog.Alliance);
        Assert.DoesNotContain("\"Id\"", json);
        Assert.DoesNotContain("Fleet\":", json.Replace("\"Fleets\":", "")); // no single fleet reference field
        foreach (var t in new[] { typeof(FleetPicture), typeof(Sighting), typeof(SystemKnowledge), typeof(GroundPicture), typeof(FleetSighted) })
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.EndsWith("Id", StringComparison.Ordinal) || p.Name == "Fleet");
    }
}
