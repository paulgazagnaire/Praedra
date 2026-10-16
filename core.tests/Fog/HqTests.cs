using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Fog;
using Rebellion.Core.Setup;
using Rebellion.Core.Support;
using static Rebellion.Core.Tests.Fog.FogWorld;

namespace Rebellion.Core.Tests.Fog;

// The Rebel HQ, tests HQ1 to HQ28 of m5-fog-and-hq §Tests, on Map M (HQ at R2). The HQ has speed 100: one hop of 100 map units = 20 days.
public class HqTests
{
    // m5-fog-and-hq HQ1
    [Fact]
    public void HQ1_MoveOneHop()
    {
        var w = new FogWorld();
        int refined = w.Game.State.Economy.Alliance.Refined;
        long seq = w.Fog.SubmitMoveHq(Side.Alliance, R3);
        w.Step();
        var acc = Assert.Single(w.Events<OrderAccepted>(), e => e.Sequence == seq);
        Assert.Equal(new[] { R2, R3 }, acc.Path!.Items);
        Assert.Equal(20, acc.TotalTicks);
        Assert.Equal(20, acc.ArrivalDay);
        var dep = Assert.Single(w.Events<HqDeparted>());
        Assert.Equal(0, dep.Day);
        Assert.Equal(R2, dep.From);
        for (int day = 1; day <= 19; day++)
        {
            w.StepTo(day);
            Assert.Equal(HqStatus.InTransit, w.Hq.Status);
            Assert.Null(w.H.HqSystem);
        }
        w.StepTo(20);
        var arr = Assert.Single(w.Events<HqArrived>());
        Assert.Equal((20, R3, "SR"), (arr.Day, arr.System, arr.Sector));
        Assert.Equal(HqStatus.AtRest, w.Hq.Status);
        Assert.Equal(R3, w.H.HqSystem);
        Assert.Equal(refined, w.Game.State.Economy.Alliance.Refined); // cost 0
    }

    // m5-fog-and-hq HQ2
    [Fact]
    public void HQ2_TwoHopsNoStopAtAnEnemyFleet()
    {
        var w = new FogWorld(beforeSetup: x => x.Fleet(Side.Empire, R3, Isd, 14));
        long seq = w.Fog.SubmitMoveHq(Side.Alliance, R4);
        w.Step();
        var acc = Assert.Single(w.Events<OrderAccepted>(), e => e.Sequence == seq);
        Assert.Equal(new[] { R2, R3, R4 }, acc.Path!.Items);
        Assert.Equal(40, acc.TotalTicks);
        w.StepTo(39);
        Assert.Equal(HqStatus.InTransit, w.Hq.Status);
        w.StepTo(40);
        Assert.Equal(R4, w.H.HqSystem);
        Assert.Equal(40, Assert.Single(w.Events<HqArrived>()).Day);
    }

    // m5-fog-and-hq HQ3 to HQ8 and HQ17
    [Fact]
    public void HQ3to8_Rejections()
    {
        Assert.Equal(RejectReason.AlreadyThere, new FogWorld().TryMoveHq(Side.Alliance, R2));                    // HQ3
        Assert.Equal(HqRejectReason.NotHeld, new FogWorld().TryMoveHq(Side.Alliance, R1));                       // HQ4
        Assert.Equal(HqRejectReason.NotHeld, new FogWorld(r4Neutral: true).TryMoveHq(Side.Alliance, R4));        // HQ5
        var w = new FogWorld();
        Assert.Null(w.TryMoveHq(Side.Alliance, R3));
        Assert.Equal(RejectReason.InTransit, w.TryMoveHq(Side.Alliance, R4));                                     // HQ6
        Assert.Equal(R3, w.Hq.Path[^1]);
        Assert.Equal(HqRejectReason.NotAlliance, new FogWorld().TryMoveHq(Side.Empire, R3));                     // HQ7
        var ilum = new FogWorld(beforeSetup: x => x.H.Fleets.SetOwner(Ilum, Side.Alliance));
        Assert.Equal(RejectReason.Unreachable, ilum.TryMoveHq(Side.Alliance, Ilum));                             // HQ8
        Assert.Equal(RejectReason.UnknownSystem, new FogWorld().TryMoveHq(Side.Alliance, "Nowhere"));
        Assert.Null(new FogWorld().TryMoveHq(Side.Alliance, R3));                                                 // HQ17 (no Empire fleet at R2)
    }

    /// <summary>A Map M world with an Empire fleet of <paramref name="isd"/> ISDs (and squadrons aboard) at R2 for one Step (flag computed), then MoveHq(R3).</summary>
    private static string? Pin(int isd, int squadrons = 0, bool allianceFleet = false, GameSettings? s = null)
    {
        var w = new FogWorld(s);
        w.Fleet(Side.Empire, R2, Isd, isd, squadrons);
        if (allianceFleet) w.Fleet(Side.Alliance, R2, Corvette, 1);
        w.Game.State.Blockades.Clear();
        if (!allianceFleet) w.Game.State.Blockades[R2] = Side.Empire; // the flag as step 5 leaves it
        return w.TryMoveHq(Side.Alliance, R3);
    }

    // m5-fog-and-hq HQ9 to HQ12, HQ14 to HQ16
    [Fact]
    public void HQ9to16_DepartureRefusal()
    {
        Assert.Equal(HqRejectReason.Pinned, Pin(14));     // HQ9: 100 - 70 = 30 < 31
        Assert.Null(Pin(13));                              // HQ10: 100 - 65 = 35
        Assert.Equal(HqRejectReason.Pinned, Pin(13, 3));   // HQ11: 65 + 6 = 71
        Assert.Null(Pin(13, 2));                           // HQ12: 65 + 4 = 69
        Assert.Null(Pin(14, allianceFleet: true));         // HQ14: battle bit on, flag off
        Assert.Null(Pin(14, s: new GameSettings { Hq = new HqParams { DepartureOperands = HqDepartureOperands.Alliance } })); // HQ15
        Assert.Null(Pin(14, s: new GameSettings { Hq = new HqParams { DepartureOperands = HqDepartureOperands.Off } }));      // HQ16
    }

    // m5-fog-and-hq HQ13 (squadron-only fleets, put in place directly: the refusal reads the flag and the fleets at step 1)
    [Fact]
    public void HQ13_SquadronsOnly()
    {
        string? Try(int squadrons)
        {
            var w = new FogWorld();
            var ids = Enumerable.Range(0, squadrons).Select(_ => w.H.Economy.AddObject(Side.Empire, Tie, null)).ToList();
            int fid = w.H.Economy.AddObject(Side.Empire, Tie, null); w.H.Economy.RemoveObject(fid); // a fresh id for the fleet
            w.Game.State.Fleets[fid] = new Fleet { Id = fid, Side = Side.Empire, Squadrons = ids, At = R2 };
            w.Game.State.Blockades[R2] = Side.Empire;
            return w.TryMoveHq(Side.Alliance, R3);
        }
        Assert.Equal(HqRejectReason.Pinned, Try(35)); // 70
        Assert.Null(Try(34));                          // 68
    }

    // m5-fog-and-hq HQ18
    [Fact]
    public void HQ18_CaptureDestroysTheHq()
    {
        var w = new FogWorld();
        w.H.Control.SubmitDebugSetHolder(Side.Empire, R2, Side.Empire);
        w.Step();
        var d = Assert.Single(w.Events<HqDestroyed>());
        Assert.Equal((FogModule.CauseCaptured, R2, 1), (d.Cause, d.System, d.Day));
        Assert.Equal(HqStatus.Destroyed, w.Hq.Status);
        Assert.Contains(d, w.EventsFor(Side.Empire));
        Assert.Contains(d, w.EventsFor(Side.Alliance));
        Assert.Equal(HqRejectReason.NoHq, w.TryMoveHq(Side.Alliance, R3));
    }

    // m5-fog-and-hq HQ19
    [Fact]
    public void HQ19_HqInTransitSurvivesTheLossOfItsOrigin()
    {
        var w = new FogWorld();
        w.Fog.SubmitMoveHq(Side.Alliance, R3);
        w.StepTo(10);
        w.H.Control.SubmitDebugSetHolder(Side.Empire, R2, Side.Empire);
        w.StepTo(20);
        Assert.Empty(w.Events<HqDestroyed>());
        Assert.Equal(R3, w.H.HqSystem);
        Assert.Equal(20, Assert.Single(w.Events<HqArrived>()).Day);
    }

    // m5-fog-and-hq HQ20
    [Fact]
    public void HQ20_LostDestinationDestroysTheHqOnArrival()
    {
        var w = new FogWorld();
        w.Fog.SubmitMoveHq(Side.Alliance, R3);
        w.StepTo(10);
        w.H.Control.SubmitDebugSetHolder(Side.Empire, R3, Side.Empire);
        w.StepTo(20);
        var d = Assert.Single(w.Events<HqDestroyed>());
        Assert.Equal((FogModule.CauseDestinationLost, R3, 20), (d.Cause, d.System, d.Day));
        Assert.Empty(w.Events<HqArrived>());
        Assert.DoesNotContain(w.Events<SupportChanged>(), e => e.Cause == SupportCause.HqMove);
    }

    // m5-fog-and-hq HQ21
    [Fact]
    public void HQ21_DeathStarHook()
    {
        var w = new FogWorld();
        w.InStep(ctx => w.Fog.OnSystemDestroyed(ctx, R2));
        w.Step();
        Assert.Equal(FogModule.CauseSystemDestroyed, Assert.Single(w.Events<HqDestroyed>()).Cause);
        Assert.Equal(HqStatus.Destroyed, w.Hq.Status);

        var moving = new FogWorld();
        moving.Fog.SubmitMoveHq(Side.Alliance, R3);
        moving.Step(5);
        moving.InStep(ctx => moving.Fog.OnSystemDestroyed(ctx, R2));
        moving.Step();
        Assert.Empty(moving.Events<HqDestroyed>());
        Assert.Equal(HqStatus.InTransit, moving.Hq.Status);
    }

    // m5-fog-and-hq HQ22
    [Fact]
    public void HQ22_NeverLeavesTheHqOnAnEmpireWorld()
    {
        var w = new FogWorld(new GameSettings { Hq = new HqParams { LostOnHolderChange = HqLostOnHolderChange.Never } });
        w.H.Control.SubmitDebugSetHolder(Side.Empire, R2, Side.Empire);
        w.Step();
        Assert.Empty(w.Events<HqDestroyed>());
        Assert.Equal(HqStatus.AtRest, w.Hq.Status);
        Assert.Equal(R2, w.H.HqSystem); // inconsistent state, as the sheet says
    }

    // m5-fog-and-hq HQ23
    [Fact]
    public void HQ23_ArrivalLowersAllianceSupportInTheSector()
    {
        var w = new FogWorld();
        w.Fog.SubmitMoveHq(Side.Alliance, R3);
        w.StepTo(20);
        Assert.Equal((R3, "SR"), (w.Events<HqArrived>()[0].System, w.Events<HqArrived>()[0].Sector));
        foreach (var s in new[] { R2, R3, R4 }) Assert.Equal(60, w.H.Economy.SupportOf(s, Side.Alliance));
        Assert.Equal(20, w.H.Economy.SupportOf(R1, Side.Alliance)); // its own value, 30, minus 10
        Assert.Equal(30, w.H.Economy.SupportOf(C0, Side.Alliance)); // another sector
        Assert.Equal(4, w.Events<SupportChanged>().Count(e => e.Cause == SupportCause.HqMove));
    }

    // m5-fog-and-hq HQ24 (HQ25: no characters exist in the core yet; HQ26: the HQ is not in the facility registry, so collateral cannot pick it)
    [Fact]
    public void HQ24_FacilitiesStayBehind()
    {
        var w = new FogWorld(beforeSetup: x =>
        {
            x.H.Economy.AddFacility(R2, FacilityKind.Mine);
            x.H.Economy.AddFacility(R2, FacilityKind.Refinery);
            x.H.Economy.AddFacility(R2, FacilityKind.ConstructionYard);
        });
        string Facilities() => string.Join(",", w.H.Economy.Objects.Where(o => o.System == R2 && o.Category == ObjectCategory.Facility).Select(o => $"{o.Id}:{o.Class}"));
        string before = Facilities();
        w.Fog.SubmitMoveHq(Side.Alliance, R3);
        w.StepTo(21);
        Assert.Equal(before, Facilities());
        Assert.DoesNotContain(w.H.Economy.Objects, o => o.Class.Contains("Headquarters", StringComparison.OrdinalIgnoreCase)); // HQ26: not a registry object
    }

    // m5-fog-and-hq HQ27
    [Fact]
    public void HQ27_SaveMidTransit()
    {
        var whole = new FogWorld();
        whole.Fog.SubmitMoveHq(Side.Alliance, R4);
        whole.StepTo(45);
        var first = new FogWorld();
        first.Fog.SubmitMoveHq(Side.Alliance, R4);
        first.StepTo(7);
        var loaded = GameHost.Load(first.H.Save(), first.H.Settings, FogWorld.Data);
        Assert.Equal(HqStatus.InTransit, loaded.Game.State.Scenario.Hq.Status);
        while (loaded.Game.Day < 45) loaded.Game.Step();
        Assert.Equal(R4, loaded.HqSystem);
        Assert.Equal(40, loaded.Game.Events.OfType<HqArrived>().Single().Day);
        Assert.Equal(whole.H.StateHash(), loaded.StateHash());
    }

    // m5-fog-and-hq HQ28
    [Fact]
    public void HQ28_DeterministicAndDrawsNothing()
    {
        (List<string>, string, long) Run()
        {
            var w = new FogWorld();
            // draws made in slots 1 to 3 (orders, departures, arrivals: the HQ's slots; nothing else there draws on Map M)
            long before = 0, hqDraws = 0;
            w.Game.RegisterHook("3", ctx => hqDraws += ctx.Rng.Draws - before);
            void Step() { before = w.Game.Rng.Draws; w.Step(); }
            w.Fog.SubmitMoveHq(Side.Alliance, R3);
            while (w.Game.Day < 25) Step();
            w.Fog.SubmitMoveHq(Side.Alliance, R4);
            while (w.Game.Day < 50) Step();
            return (w.Game.Events.Select(e => e.ToString()).ToList(), w.H.StateHash(), hqDraws);
        }
        var a = Run(); var b = Run();
        Assert.Equal(a.Item1, b.Item1);
        Assert.Equal(a.Item2, b.Item2);
        Assert.Equal(0, a.Item3);
    }

    // Victory reads the destroyed HQ (setup-victory/04 §3 with m5-fog-and-hq §F7): a capture of the HQ system ends a real game for the Empire.
    [Fact]
    public void CaptureOfTheHqSystemWinsTheGameForTheEmpire()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Settings = new GameSettings { DebugOrdersEnabled = true, Victory = OldVictory.Rules } });
        string hq = h.HqSystem!;
        h.Control.SubmitDebugSetHolder(Side.Empire, hq, Side.Empire);
        h.Game.Step();
        Assert.Equal(HqStatus.Destroyed, h.Game.State.Scenario.Hq.Status);
        Assert.Equal(Side.Empire, h.Victory.Winner);
        Assert.Equal($"Empire captured the Rebel HQ at {hq}", h.Victory.Reason);
    }

    [Fact]
    public void MoveHqOrderSavesInTheQueue()
    {
        var w = new FogWorld();
        w.Fog.SubmitMoveHq(Side.Alliance, R3);
        var loaded = GameHost.Load(w.H.Save(), w.H.Settings, FogWorld.Data);
        Assert.IsType<MoveHqOrder>(Assert.Single(loaded.Game.Queue));
        loaded.Game.Step();
        Assert.Equal(HqStatus.InTransit, loaded.Game.State.Scenario.Hq.Status);
    }
}
