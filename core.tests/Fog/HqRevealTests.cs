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
        var w = new FogWorld(beforeSetup: x => x.Fleet(Side.Hand, R2, Mandate, 1));
        w.Step(100);
        var p = w.Pic(Side.Hand, R2)!;
        Assert.Equal(Side.Collective, p.Owner!.Value.Owner);
        Assert.NotNull(p.Fleets);
        Assert.False(p.HqHere);
        Assert.Null(w.Fog.KnownHq(Side.Hand));
    }

    // m5-fog-and-hq HR2
    [Fact]
    public void HR2_PresenceRevealsTheHqWhenSwitchedOn()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { PresenceRevealsHq = true } });
        w.H.Fleets.SubmitMove(Side.Hand, w.E1, R2);
        w.StepTo(16);
        Assert.True(w.Pic(Side.Hand, R2)!.HqHere);
        Assert.Equal(new KnownHqInfo(R2, 16), w.Fog.KnownHq(Side.Hand));
    }

    private static (FogWorld W, int Seen) Hr3()
    {
        var w = new FogWorld();
        w.StepTo(29);
        int seen = Espionage(w, Side.Hand, R2); // FE1 on day 30
        Assert.Equal(30, seen);
        w.StepTo(31);
        w.Fog.SubmitMoveHq(Side.Collective, R3); // arrives day 51
        return (w, seen);
    }

    // m5-fog-and-hq HR3
    [Fact]
    public void HR3_TheHandIsNotToldWhenTheHqMoves()
    {
        var (w, _) = Hr3();
        w.StepTo(80);
        Assert.Equal(R3, w.H.HqSystem);
        Assert.Equal(new KnownHqInfo(R2, 30), w.Fog.KnownHq(Side.Hand));
        Assert.Equal(50, w.Game.Day - w.Fog.KnownHq(Side.Hand)!.Stamp);
        Assert.False(w.Know(Side.Hand, R3)?.HqHere ?? false);
        Assert.DoesNotContain(w.EventsFor(Side.Hand), e => e is HqDeparted or HqArrived);
        Assert.DoesNotContain(w.EventsFor(Side.Hand), e => e is SupportChanged s && s.Cause == SupportCause.HqMove);
    }

    // m5-fog-and-hq HR4
    [Fact]
    public void HR4_FindingTheHqClearsTheOldSighting()
    {
        var (w, _) = Hr3();
        w.StepTo(79);
        int stamp = Espionage(w, Side.Hand, R3);
        Assert.True(w.Know(Side.Hand, R3)!.HqHere);
        Assert.False(w.Know(Side.Hand, R2)!.HqHere);
        Assert.Equal(new KnownHqInfo(R3, stamp), w.Fog.KnownHq(Side.Hand));
        Assert.Equal(80, stamp);
    }

    // m5-fog-and-hq HR5
    [Fact]
    public void HR5_LookingAgainAtTheOldSystemShowsNoHq()
    {
        var (w, _) = Hr3();
        w.StepTo(59);
        Espionage(w, Side.Hand, R2);
        Assert.False(w.Know(Side.Hand, R2)!.HqHere);
        Assert.Null(w.Fog.KnownHq(Side.Hand));
    }

    // m5-fog-and-hq HR6
    [Fact]
    public void HR6_NoSectorClueByDefault()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 0, HearthTipSectorPermille = 0 } }); // §N6a tip has its own tests
        Espionage(w, Side.Hand, R3);
        Assert.Empty(w.Fog.Clues(Side.Hand));
        Assert.DoesNotContain(w.Game.State.Fog.Hand.Systems.Values, k => k.HqHere);
    }

    // m5-fog-and-hq HR7
    [Fact]
    public void HR7_SectorClueWhenSwitchedOn()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { SectorSuspicion = SectorSuspicion.OnEspionage, HearthTipPermille = 0, HearthTipSectorPermille = 0 } });
        int stamp = Espionage(w, Side.Hand, R3);
        Assert.Equal(new HqSectorClue("SR", stamp), Assert.Single(w.Fog.Clues(Side.Hand)));
        Assert.DoesNotContain(w.Game.State.Fog.Hand.Systems.Values, k => k.HqHere);
        Assert.Empty(w.Fog.Clues(Side.Collective));
        Assert.Contains(w.EventsFor(Side.Hand), e => e is HqSectorClueGiven);
        Assert.DoesNotContain(w.EventsFor(Side.Collective), e => e is HqSectorClueGiven);
    }

    // m5-fog-and-hq HR12
    [Fact]
    public void HR12_HearthTipTellsTheHandWhereTheHearthIs()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 1000 } });
        int stamp = Espionage(w, Side.Hand, R3);
        Assert.Equal(new KnownHqInfo(R2, stamp), w.Fog.KnownHq(Side.Hand));
        var p = w.Pic(Side.Hand, R2)!;
        Assert.Equal(Side.Collective, p.Owner!.Value.Owner);
        Assert.Null(p.Fleets); // a tip shows where it is and who holds it, nothing more
        Assert.Contains(w.EventsFor(Side.Hand), e => e is HearthTipGiven t && t.System == R2);
        Assert.DoesNotContain(w.EventsFor(Side.Collective), e => e is HearthTipGiven);
    }

    // m5-fog-and-hq HR13
    [Fact]
    public void HR13_NoHearthTipAtZeroInTransitOrAtHome()
    {
        var off = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 0, HearthTipSectorPermille = 0 } });
        Espionage(off, Side.Hand, R3);
        Assert.Null(off.Fog.KnownHq(Side.Hand));

        var moving = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 1000 } });
        moving.Fog.SubmitMoveHq(Side.Collective, R3);
        moving.Step();
        Assert.Equal(HqStatus.InTransit, moving.Game.State.Scenario.Hq.Status);
        Espionage(moving, Side.Hand, R4);
        Assert.Null(moving.Fog.KnownHq(Side.Hand));
        Assert.DoesNotContain(moving.EventsFor(Side.Hand), e => e is HearthTipGiven);

        var home = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 1000 } });
        Espionage(home, Side.Hand, R1); // the Hand holds R1: spying at home never tips
        Assert.Null(home.Fog.KnownHq(Side.Hand));
    }

    // m5-fog-and-hq HR15
    [Fact]
    public void HR15_HearthTipSectorBandGivesTheSectorClue()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 0, HearthTipSectorPermille = 1000 } });
        int stamp = Espionage(w, Side.Hand, R3);
        Assert.Equal(new HqSectorClue("SR", stamp), Assert.Single(w.Fog.Clues(Side.Hand)));
        Assert.Null(w.Fog.KnownHq(Side.Hand));
        Assert.DoesNotContain(w.EventsFor(Side.Collective), e => e is HqSectorClueGiven);
    }

    // m5-fog-and-hq HR14
    [Fact]
    public void HR14_TheCollectiveGetsNoHearthTip()
    {
        var w = new FogWorld(new GameSettings { Fog = new FogParams { HearthTipPermille = 1000 } });
        Espionage(w, Side.Collective, R3);
        Assert.DoesNotContain(w.Game.Events, e => e is HearthTipGiven);
    }

    // m5-fog-and-hq HR8
    [Fact]
    public void HR8_TheHandViewDoesNotHoldTheHq()
    {
        var w = new FogWorld();
        w.Step(10);
        var v = w.Fog.ViewFor(Side.Hand);
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
        var w = new FogWorld(beforeSetup: x => x.Fleet(Side.Hand, R4, Mandate, 1));
        w.Fog.SubmitMoveHq(Side.Collective, R3); // arrives day 20
        w.StepTo(5);
        Assert.Equal(70, w.Pic(Side.Hand, R4)!.Owner!.Value.CollectiveSupport);
        w.StepTo(20);
        var b = w.Pic(Side.Hand, R4)!.Owner!;
        Assert.Equal(60, b.Value.CollectiveSupport);
        Assert.Equal(20, b.Stamp);
        Assert.DoesNotContain(w.EventsFor(Side.Hand), e => e is HqArrived);
        Assert.False(w.Pic(Side.Hand, R4)!.HqHere);

        var none = new FogWorld(); // HR10: no Hand fleet at R4
        none.Fog.SubmitMoveHq(Side.Collective, R3);
        none.StepTo(20);
        Assert.Null(none.Pic(Side.Hand, R4));
        Assert.Equal(60, none.H.Economy.SupportOf(R4, Side.Collective));
    }

    // m5-fog-and-hq HR11
    [Fact]
    public void HR11_DetectedMissionGoesToTheDefenderOnly()
    {
        var w = new FogWorld();
        w.InStep(ctx => w.Fog.OnMissionDetected(ctx, Side.Collective, R3));
        w.Step();
        var s = Assert.Single(w.Fog.Sightings(Side.Collective));
        Assert.Equal((SightingKind.AgentDetected, R3, Side.Hand), (s.Kind, s.System, s.Side));
        Assert.Empty(w.Fog.Sightings(Side.Hand));
        Assert.DoesNotContain(w.EventsFor(Side.Hand), e => e is AgentDetected);
    }

    // m5-fog-and-hq FN1
    [Fact]
    public void FN1_HandKnowledgeDoesNotDependOnWhereTheHqIs()
    {
        var a = new FogWorld(hq: R2);
        var b = new FogWorld(hq: R4);
        foreach (var w in new[] { a, b }) w.H.Fleets.SubmitMove(Side.Hand, w.E1, R2);
        for (int day = 0; day < 60; day++)
        {
            Assert.Equal(a.Fog.KnowledgeHash(Side.Hand), b.Fog.KnowledgeHash(Side.Hand));
            if (day == 30) foreach (var w in new[] { a, b }) w.H.Fleets.SubmitMove(Side.Hand, w.E1, R1);
            a.Step(); b.Step();
        }
        string Accepted(FogWorld w) => string.Join(";", w.EventsFor(Side.Hand).OfType<OrderAccepted>().Select(e => e.ToString()));
        Assert.Equal(Accepted(a), Accepted(b));
        Assert.NotEqual(a.H.StateHash(), b.H.StateHash()); // the worlds do differ
    }

    // m5-fog-and-hq FN3 (partial: the M6 AI assembly does not exist; the view itself exposes no truth type, see HR8) and FN4
    [Fact]
    public void FN4_HandFeedHasNoCollectiveOrdersNorHqMoves()
    {
        var w = new FogWorld();
        int f = w.Fleet(Side.Collective, R2, Corvette, 1);
        var collectiveSeqs = new HashSet<long> { w.Fog.SubmitMoveHq(Side.Collective, R3), w.H.Fleets.SubmitMove(Side.Collective, f, R4) };
        w.StepTo(40);
        var feed = w.EventsFor(Side.Hand);
        Assert.DoesNotContain(feed, e => e is OrderAccepted oa && collectiveSeqs.Contains(oa.Sequence));
        Assert.DoesNotContain(feed, e => e is HqDeparted or HqArrived);
        Assert.Contains(w.EventsFor(Side.Collective), e => e is OrderAccepted oa && collectiveSeqs.Contains(oa.Sequence));
        Assert.Contains(w.EventsFor(Side.Collective), e => e is HqArrived);
    }

    // m5-fog-and-hq FN5
    [Fact]
    public void FN5_NoEnemyFleetIdsInTheCollectiveKnowledge()
    {
        var w = new FogWorld();
        int g = w.Fleet(Side.Hand, R4, Mandate, 2);
        w.H.Fleets.SubmitMove(Side.Hand, g, R2);
        int f = w.Fleet(Side.Collective, R2, Corvette, 1);
        w.H.Fleets.SubmitMove(Side.Collective, f, R1);
        w.StepTo(20);
        Assert.NotEmpty(w.Fog.Sightings(Side.Collective));
        var json = JsonSerializer.Serialize(w.Game.State.Fog.Collective);
        Assert.DoesNotContain("\"Id\"", json);
        Assert.DoesNotContain("Fleet\":", json.Replace("\"Fleets\":", "")); // no single fleet reference field
        foreach (var t in new[] { typeof(FleetPicture), typeof(Sighting), typeof(SystemKnowledge), typeof(GroundPicture), typeof(FleetSighted) })
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.EndsWith("Id", StringComparison.Ordinal) || p.Name == "Fleet");
    }
}
