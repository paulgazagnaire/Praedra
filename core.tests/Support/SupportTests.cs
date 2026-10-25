using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;

namespace Praedra.Core.Tests.Support;

// m2-popular-support, tests S1 to S17 (A = Alliance support). Test ids are named in the comments.
public class SupportTests
{
    private const string Ally = "Drift Harbour";   // Alliance-held in most tests
    private const string Emp = "Classis";  // Empire-held in most tests

    private static void Blockade(SupWorld w, string system, Side by) =>
        w.W.Fleets.AddFleet(by, "Picket", new[] { by == Side.Empire ? "Mandate" : "Dodger" }, system);

    // ---------------------------------------------------------------- uprising erosion (timer 0x384)

    // m2-popular-support S1: the timer fires at A = 40. Empire support 60 gives N = 0, so the same Step's slot 6.1 would end this
    // uprising first (see S1b and ErosionNeverFiresOnAnEndedUprising); the firing itself is tested directly.
    [Fact]
    public void S1_EmpireHolds_A40_ErosionFires_A41()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 60);
        w.InStep(ctx => { w.Uprising(Emp, Side.Empire); w.Sup.FireErosion(ctx, 1, Emp); }); // after 6.1, which would end it
        w.Run(1);
        Assert.Equal(41, w.A(Emp));
        Assert.Equal(59, w.W.Eco.SupportOf(Emp, Side.Empire));
    }

    // S1 through a whole Step: the Empire needs a garrison need (s = 59, N = 1, doubled 2) for the uprising to still stand when 6.2 runs.
    [Fact]
    public void S1b_EmpireHolds_UprisingStanding_ErosionRunsInTheStep()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 59); // A = 41
        w.Uprising(Emp, Side.Empire, erosionDue: 1);
        w.Run(1);
        Assert.Equal(42, w.A(Emp));
        var e = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal((Emp, 41, 42, SupportCause.UprisingErosion, 1), (e.System, e.Old, e.New, e.Cause, e.Day));
        Assert.Equal(31, w.Game.State.Support.Systems[Emp].ErosionDue); // re-armed for one more period
    }

    // S2: Alliance holds, A = 0: it would go to -1, clamp, no event
    [Fact]
    public void S2_ClampAtZero_NoEvent()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 0);
        w.Uprising(Ally, Side.Alliance, erosionDue: 1);
        w.Run(1);
        Assert.Equal(0, w.A(Ally));
        Assert.Empty(w.Events<SupportChanged>());
    }

    // S3: Empire holds, A = 100 (Empire support 0): would become 101, clamp
    [Fact]
    public void S3_ClampAtHundred()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 0);
        w.Uprising(Emp, Side.Empire, erosionDue: 1);
        w.Run(1);
        Assert.Equal(100, w.A(Emp));
        Assert.Empty(w.Events<SupportChanged>());
    }

    // S4: Empire holds in a Death Star sector, A = 40: d = +1, damped to 0
    [Fact]
    public void S4_DeathStarDampsTheEmpireErosion()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 60);
        w.Sup.SetDeathStarSystem(Emp, true);
        w.InStep(ctx => { w.Uprising(Emp, Side.Empire); w.Sup.FireErosion(ctx, 1, Emp); }); // after 6.1, which would end it
        w.Run(1);
        Assert.Equal(40, w.A(Emp));
        Assert.Empty(w.Events<SupportChanged>());
    }

    // S5: Alliance holds in a Death Star sector, A = 40: d = -1, not damped
    [Fact]
    public void S5_DeathStarDoesNotDampTheAllianceErosion()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 40);
        w.Sup.SetDeathStarSystem(Ally, true);
        w.Uprising(Ally, Side.Alliance, erosionDue: 1);
        w.Run(1);
        Assert.Equal(39, w.A(Ally));
    }

    // ---------------------------------------------------------------- presence (timer 0x386)

    // S6: Empire fleet blockades an Alliance-held system, A = 70 (Empire support 30, minority), 30 days pass: Empire -1, A = 71
    [Fact]
    public void S6_BlockaderInMinorityLoses()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 70);
        Blockade(w, Ally, Side.Empire);
        w.Run(30); // Steps 0..29: armed in Step 0 (due day 31), not yet fired
        Assert.Equal(70, w.A(Ally));
        w.Run(1);  // Step 30
        Assert.Equal(71, w.A(Ally));
        var e = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal(SupportCause.Presence, e.Cause);
        Assert.Equal(31, e.Day);
        w.Run(30); // re-armed: fires again 30 Steps later
        Assert.Equal(72, w.A(Ally));
    }

    // S7: Alliance fleet blockades an Empire-held system, A = 60 (Alliance majority): Alliance +1, A = 61
    [Fact]
    public void S7_BlockaderInMajorityGains()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 40); // A = 60; N = 2
        w.Regiments(Emp, Side.Empire, "Tranches", 2); // no uprising to disturb the test
        Blockade(w, Emp, Side.Alliance);
        w.Run(31);
        Assert.Equal(61, w.A(Emp));
    }

    // S8: same with A = 50: not strictly more than 50: Alliance -1, A = 49
    [Fact]
    public void S8_TieIsNotAMajority()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 50); // A = 50; N = 1
        w.Regiments(Emp, Side.Empire, "Tranches", 1);
        Blockade(w, Emp, Side.Alliance);
        w.Run(31);
        Assert.Equal(49, w.A(Emp));
    }

    // S9: blockade starts on Step 10 and ends on Step 25 (period 30): support never changes, timer disarmed
    [Fact]
    public void S9_ShortBlockadeNeverTouchesSupport()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 70);
        w.Run(10);
        Blockade(w, Ally, Side.Empire); // present from Step 10
        w.Run(15);                      // Steps 10..24
        Assert.NotEqual(0, w.Game.State.Support.Systems[Ally].PresenceDue);
        w.Game.State.Fleets.Clear();    // gone before Step 25
        w.Run(100);
        Assert.Equal(70, w.A(Ally));
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Empty(w.Game.State.Support.Systems); // disarmed and pruned
    }

    // blockade_support_effect off: the presence timer never runs
    [Fact]
    public void BlockadeSupportEffectSwitchOff()
    {
        var w = new SupWorld();
        w.Sup.BlockadeSupportEffect = false;
        w.Hold(Ally, Side.Alliance, 70);
        Blockade(w, Ally, Side.Empire);
        w.Run(100);
        Assert.Equal(70, w.A(Ally));
    }

    // ---------------------------------------------------------------- arming rule

    // S10: uprising starts (flag first seen) in Step 10: first erosion in Step 40 (event day 41), second in Step 70
    [Fact]
    public void S10_ErosionArmedOnStartFiresOnePeriodLater()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 60); // N = 0
        w.Stub.Expect(0, 70, 70);        // incident timer far away (day 111)
        w.Run(10);
        w.W.Eco.SetAllianceSupport(Ally, 40); // Step 10 sees N = 2 and no regiments
        w.Run(62);                       // Steps 10..71
        var ev = w.Events<SupportChanged>();
        Assert.Equal(new[] { 41, 71 }, ev.Select(e => e.Day).ToArray());
        Assert.Equal(new[] { 39, 38 }, ev.Select(e => e.New).ToArray());
        Assert.Equal(11, w.Events<UprisingStarted>().Single().Day);
    }

    // a timer never fires on an uprising that 6.1 ended (the timer is disarmed first)
    [Fact]
    public void ErosionNeverFiresOnAnEndedUprising()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 60); // N = 0: the uprising ends at 6.1
        w.Uprising(Emp, Side.Empire, erosionDue: 1);
        w.Run(1);
        Assert.Equal(40, w.A(Emp));
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Equal("NoRequirement", w.Events<UprisingEnded>().Single().Reason);
    }

    // erosion and presence due on one Step: 0x384 first, then 0x386
    [Fact]
    public void ErosionBeforePresenceInOneStep()
    {
        var w = new SupWorld();
        w.Hold(Ally, Side.Alliance, 40);
        Blockade(w, Ally, Side.Empire);
        var st = w.Uprising(Ally, Side.Alliance, erosionDue: 1);
        st.PresenceDue = 1; st.PresenceSide = Side.Empire;
        w.Run(1);
        var ev = w.Events<SupportChanged>();
        Assert.Equal(new[] { SupportCause.UprisingErosion, SupportCause.Presence }, ev.Select(e => e.Cause).ToArray());
        Assert.Equal(38, w.A(Ally)); // erosion 40 -> 39, Empire support 61 is a majority: +1 for the Empire, A 38
    }

    // ---------------------------------------------------------------- ChangeSupport (hook for later milestones)

    // S11: ChangeSupport(system, Empire, -20, Bombardment) on A = 45: Empire 55 to 35, A = 65
    [Fact]
    public void S11_EmpireLoses20()
    {
        var w = new SupWorld();
        w.W.Eco.SetAllianceSupport(Emp, 45);
        w.InStep(ctx => w.Sup.ChangeSupport(ctx, Emp, Side.Empire, -20, SupportCause.Bombardment));
        w.Run(1);
        Assert.Equal(65, w.A(Emp));
        var e = Assert.Single(w.Events<SupportChanged>());
        Assert.Equal((45, 65, SupportCause.Bombardment), (e.Old, e.New, e.Cause));
    }

    // S12: same with A = 95: clamp, A = 100
    [Fact]
    public void S12_Clamp()
    {
        var w = new SupWorld();
        w.W.Eco.SetAllianceSupport(Emp, 95);
        w.InStep(ctx => w.Sup.ChangeSupport(ctx, Emp, Side.Empire, -20, SupportCause.Bombardment));
        w.Run(1);
        Assert.Equal(100, w.A(Emp));
    }

    // S13: same as S11 in a Death Star sector: d = +20 damped to +10: A = 55
    [Fact]
    public void S13_DeathStarHalvesTheBombardment()
    {
        var w = new SupWorld();
        w.Sup.SetDeathStarSystem(Emp, true);
        w.W.Eco.SetAllianceSupport(Emp, 45);
        w.InStep(ctx => w.Sup.ChangeSupport(ctx, Emp, Side.Empire, -20, SupportCause.Bombardment));
        w.Run(1);
        Assert.Equal(55, w.A(Emp));
    }

    // S14: ChangeSupport(system, Alliance, +15, Diplomacy) in a Death Star sector, A = 30: damped to 7 (truncating): A = 37
    [Fact]
    public void S14_DampingTruncates()
    {
        var w = new SupWorld();
        w.Sup.SetDeathStarSystem(Ally, true);
        w.W.Eco.SetAllianceSupport(Ally, 30);
        w.InStep(ctx => w.Sup.ChangeSupport(ctx, Ally, Side.Alliance, 15, SupportCause.Diplomacy));
        w.Run(1);
        Assert.Equal(37, w.A(Ally));
    }

    // rule: m2-popular-support §One entry point step 2: only rises on the Alliance axis are damped
    [Fact]
    public void DampingOnlyWhenAllianceSupportRises()
    {
        var w = new SupWorld();
        w.Sup.SetDeathStarSystem(Ally, true);
        w.W.Eco.SetAllianceSupport(Ally, 50);
        w.InStep(ctx =>
        {
            w.Sup.ChangeSupport(ctx, Ally, Side.Alliance, -15, SupportCause.Other); // d = -15, not damped
            w.Sup.ChangeSupport(ctx, Ally, Side.Empire, +15, SupportCause.Other);   // d = -15, not damped
            w.Sup.ChangeSupport(ctx, Ally, Side.Empire, -1, SupportCause.Other);    // d = +1 -> 0, no event
        });
        w.Run(1);
        Assert.Equal(20, w.A(Ally));
        Assert.Equal(2, w.Events<SupportChanged>().Count);
    }

    // ---------------------------------------------------------------- nothing else moves it

    // S15: neutral system, any number of Steps: support never changes (not even with fleets there)
    [Fact]
    public void S15_NeutralSystemNeverMoves()
    {
        var w = new SupWorld();
        w.W.Eco.SetAllianceSupport("Navalia", 33);
        Blockade(w, "Navalia", Side.Empire);
        Blockade(w, "Navalia", Side.Alliance);
        w.Run(200);
        Assert.Equal(33, w.A("Navalia"));
        Assert.Empty(w.Game.State.Support.Systems);
        Assert.Empty(w.Events<SupportChanged>());
    }

    // S16: 1000-day soak, no uprising, no blockade: support of every system identical to day 0
    [Fact]
    public void S16_NothingDrifts()
    {
        var w = new SupWorld(stub: false, seed: 7);
        foreach (var s in new[] { "Charter", "Navalia", "Incus" }) w.Hold(s, Side.Empire, 80);
        foreach (var s in new[] { "The Shoal", "The Thaw", "Caminus" }) w.Hold(s, Side.Alliance, 100);
        var before = TestData.RealGalaxy.Systems.Select(s => w.A(s.Name)).ToList();
        w.Run(1000);
        Assert.Equal(before, TestData.RealGalaxy.Systems.Select(s => w.A(s.Name)).ToList());
        Assert.Empty(w.Events<SupportChanged>());
        Assert.Equal(0, w.Game.Rng.Draws);
    }

    // S17: S1 (standing uprising) twice with the same seed: identical events, no random draw for this sheet
    [Fact]
    public void S17_SameSeedSameEvents_NoDraws()
    {
        GameRng RunOnce(out List<GameEvent> events, out string hash)
        {
            var w = new SupWorld(stub: false, seed: 11);
            w.Hold(Emp, Side.Empire, 59);
            w.Uprising(Emp, Side.Empire, erosionDue: 1);
            w.Run(1);
            events = w.Game.Events.ToList();
            hash = GameSave.StateHash(w.Game);
            return w.Game.Rng;
        }
        var r1 = RunOnce(out var e1, out var h1);
        RunOnce(out var e2, out var h2);
        Assert.Equal(e1, e2);
        Assert.Equal(h1, h2);
        Assert.Equal(0, r1.Draws);
    }

    // ---------------------------------------------------------------- Death Star flag and state

    // m2-popular-support §Death Star sector flag: a sector flag (every system of the sector), wired into the economy hook
    [Fact]
    public void DeathStarSectorFlagsEverySystemOfTheSector_AndFeedsTheEconomyHook()
    {
        var w = new SupWorld();
        var g = TestData.RealGalaxy;
        string sector = g.Get("Charter").Sector;
        Assert.False(w.W.Eco.IsDeathStarSector("Charter"));
        w.Sup.SetDeathStarSector(sector, true);
        var members = g.Systems.Where(s => s.Sector == sector).Select(s => s.Name).ToList();
        Assert.True(members.Count > 1);
        Assert.All(members, m => { Assert.True(w.Sup.IsDeathStarSector(m)); Assert.True(w.W.Eco.IsDeathStarSector(m)); });
        Assert.False(w.W.Eco.IsDeathStarSector(g.Systems.First(s => s.Sector != sector).Name));
        w.Sup.SetDeathStarSector(sector, false);
        Assert.All(members, m => Assert.False(w.W.Eco.IsDeathStarSector(m)));
        Assert.Throws<ArgumentException>(() => w.Sup.SetDeathStarSector("No Such Sector", true));
    }

    // the Death Star counts Empire regiments double in smuggling (economy hook): flag on lowers the chance more
    [Fact]
    public void DeathStarFlagReachesSmuggling()
    {
        var w = new SupWorld();
        w.Hold(Emp, Side.Empire, 20);                 // tier 75
        w.Regiments(Emp, Side.Empire, "Tranches", 5);
        Assert.Equal(75 - 2 * 5, w.W.Eco.DiversionPercent(Emp, Side.Empire));
        w.Sup.SetDeathStarSystem(Emp, true);
        Assert.Equal(75 - 2 * 2 * 5, w.W.Eco.DiversionPercent(Emp, Side.Empire));
    }

    // save and load: uprising state, timers and the flag are in the state; re-attach rewires the hook; the future is the same
    [Fact]
    public void SaveLoadKeepsSupportStateAndFuture()
    {
        SupWorld Make()
        {
            var w = new SupWorld(stub: false, seed: 5);
            w.Hold(Ally, Side.Alliance, 30);
            w.Hold(Emp, Side.Empire, 60);
            w.Sup.SetDeathStarSystem(Emp, true);
            Blockade(w, Ally, Side.Empire);
            return w;
        }
        var whole = Make(); whole.Run(300);
        var first = Make(); first.Run(100);
        Assert.NotEmpty(first.Game.State.Support.Systems);
        var loaded = GameSave.Load(GameSave.Save(first.Game));
        var fleets = Praedra.Core.Fleets.FleetModule.Attach(loaded, TestData.RealGalaxy, Praedra.Core.Tests.Fleets.World.Ships.Value, Praedra.Core.Tests.Fleets.World.Blockade);
        var eco = EconomyModule.Attach(loaded, TestData.RealGalaxy, EcoWorld.Slots.Value, EcoWorld.Costs.Value, fleets);
        eco.IncidentsEnabled = false;
        var sup = SupportModule.Attach(loaded, TestData.RealGalaxy, eco, fleets, SupWorld.Weights.Value);
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded));
        Assert.True(eco.IsDeathStarSector(Emp));
        loaded.Step(); Assert.Equal(101, loaded.Day);
        for (int i = 1; i < 200; i++) loaded.Step();
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded));
        Assert.Equal(whole.Sup.IsUprising(Ally), sup.IsUprising(Ally));
    }
}
