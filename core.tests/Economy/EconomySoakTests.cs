using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;

namespace Praedra.Core.Tests.Economy;

// 1000-day headless runs on the real galaxy with the demo setup (core/Economy/DemoScenario): no crash, nothing negative where
// the sheets forbid it, identical StateHash on two runs, and only the random numbers the sheets require.
public class EconomySoakTests
{
    private static DemoScenario Demo(ulong seed, bool incidents) => DemoScenario.Create(TestData.RepoRoot, seed, incidents);

    private static void CheckInvariants(DemoScenario d)
    {
        var eco = d.Economy;
        foreach (var side in new[] { Side.Hand, Side.Collective })
        {
            Assert.True(eco.Raw(side) >= 0 && eco.Refined(side) >= 0);
            Assert.True(eco.Capacity(side) >= 0 && eco.Load(side) >= 0);
        }
        // rule: m2-slots-and-resources §Over-capacity destruction (runs immediately, so it holds after every Step)
        foreach (var g in eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed).GroupBy(o => o.System!))
        {
            var slots = eco.SlotsOf(g.Key);
            Assert.True(g.Count() <= slots.Energy, $"{g.Key}: {g.Count()} facilities, energy {slots.Energy}");
            Assert.True(g.Count(o => o.Class == "Mine") <= eco.EffectiveRaw(g.Key), $"{g.Key}: too many mines");
        }
        // rule: m2-slots-and-resources §Resource incident (caps)
        foreach (var s in d.Economy.Objects.Where(o => o.System != null).Select(o => o.System!).Distinct())
        {
            var sl = eco.SlotsOf(s);
            Assert.InRange(sl.Energy, 0, 15); Assert.InRange(sl.Raw, 0, 15);
        }
        // every waiting refinery is in exactly one line
        var lines = eco.RawLine(Side.Hand).Concat(eco.RawLine(Side.Collective)).ToList();
        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.Equal(lines.Count, eco.Objects.Count(o => o.InLine));
    }

    private static string Run(ulong seed, bool incidents, int days, out DemoScenario d)
    {
        d = Demo(seed, incidents);
        for (int i = 0; i < days; i++) { d.Game.Step(); CheckInvariants(d); }
        return GameSave.StateHash(d.Game);
    }

    [Fact]
    public void ThousandDaysIncidentsOnSameSeedSameHash()
    {
        string a = Run(1, true, 1000, out var da), b = Run(1, true, 1000, out _), c = Run(2, true, 1000, out _);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(da.Game.Events.OfType<ResourceProduced>().Any());
    }

    // movement + economy: with incidents off the only draws are first cycles, smuggling rolls and one pick per autoscrap firing
    [Fact]
    public void ThousandDaysDrawOnlyWhatTheSheetsRequire()
    {
        Run(3, false, 1000, out var d);
        var ev = d.Game.Events;
        long firstCycles = d.Economy.Objects.Count(o => o.FirstDone);
        // B5b 2026-10-18: every restart from IDLE is a startup cycle with one draw (B5b §2), not only the first one; the StartupCycle
        // events count them (first starts included).
        long startups = ev.OfType<StartupCycle>().Count();
        Assert.True(startups >= firstCycles);
        long smuggleRolls = ev.OfType<ResourceProduced>().Count(e => e.SmugglePercent > 0);
        long picks = ev.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap");
        Assert.Equal(startups + smuggleRolls + picks, d.Game.Rng.Draws);
        Assert.True(smuggleRolls > 0, "the demo has low-support systems, so smuggling rolls happen");
        Assert.True(picks > 0, "the Hand starts over capacity");
        Assert.Contains(ev, e => e is FleetArrived);
    }

    // with incidents on the extra draws are exactly those of the slot sheet
    [Fact]
    public void ThousandDaysWithIncidentsDrawOnlyWhatTheSheetsRequire()
    {
        Run(4, true, 1000, out var d);
        int placed = Demo(4, true).Economy.Objects.Count(o => o.Class is "Mine" or "Refinery");
        var ev = d.Game.Events;
        long smuggleRolls = ev.OfType<ResourceProduced>().Count(e => e.SmugglePercent > 0);
        long picks = ev.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap");
        long overCapacity = ev.OfType<FacilityDestroyed>().Count(e => e.Reason != DestroyReason.Disaster);
        long incidents = 2 /* arming */ + 3 * ev.OfType<ResourceIncident>().Count() + overCapacity
                       + ev.OfType<Disaster>().Sum(x => 2L + x.Rolled);
        long rest = d.Game.Rng.Draws - smuggleRolls - picks - incidents; // first-cycle draws: at most one per mine or refinery ever placed
        Assert.InRange(rest, 0, placed);
    }

    [Fact]
    public void SaveAndLoadMidRunGivesTheSameFutureAsAnUninterruptedRun()
    {
        Run(5, true, 400, out var whole);

        var first = Demo(5, true);
        for (int i = 0; i < 200; i++) first.Game.Step();
        string json = GameSave.Save(first.Game);
        var loaded = GameSave.Load(json);
        var fleets = FleetModule.Attach(loaded, TestData.RealGalaxy, Praedra.Core.Tests.Fleets.World.Ships.Value, Praedra.Core.Tests.Fleets.World.Blockade);
        var eco = EconomyModule.Attach(loaded, TestData.RealGalaxy, EcoWorld.Slots.Value, EcoWorld.Costs.Value, fleets);
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded));
        for (int i = 0; i < 200; i++) loaded.Step();
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded));
        Assert.Equal(whole.Economy.Raw(Side.Hand), eco.Raw(Side.Hand));
    }

    // m1-clock-and-orders §Seeded random numbers rule 3 (dice-free rules): the demo with no draw sources gives the same world on any seed
    [Fact]
    public void WorldWithoutAnyDrawSourceIsTheSameOnEverySeed()
    {
        string Make(ulong seed)
        {
            var w = new EcoWorld(seed);
            w.Hold("Charter", Side.Hand, 100); // no mine or refinery: no first-cycle draw; incidents off; no load
            w.Eco.AddFacility("Charter", FacilityKind.OrbitalShipyard);
            w.Run(300);
            return GameSave.WorldHash(w.Game);
        }
        Assert.Equal(Make(1), Make(2));
    }

    // ---------------------------------------------------------------- support and uprisings on (P2.3)

    private static void CheckSupportInvariants(DemoScenario d)
    {
        var sup = d.Support!; var st = d.Game.State.Support;
        foreach (var sys in d.Galaxy.Systems)
            Assert.InRange(d.Economy.SupportOf(sys.Name, Side.Collective), 0, 100);
        foreach (var (name, s) in st.Systems)
        {
            // rule: m2-uprisings-and-garrison: the flag and timers 0x384 and 0x38d live and die together; only held systems rebel
            Assert.Equal(s.Uprising, s.ErosionDue != 0);
            Assert.Equal(s.Uprising, s.IncidentDue != 0);
            if (s.Uprising) Assert.NotNull(d.Fleets.OwnerOf(name));
            Assert.False(s.IsIdle);
        }
    }

    private static string RunSupport(ulong seed, int days, out DemoScenario d, bool incidents = true, bool reinforce = true)
    {
        d = DemoScenario.Create(TestData.RepoRoot, seed, incidents, support: true);
        for (int i = 0; i < days; i++)
        {
            if (reinforce && i == 150) // eight regiments land on The Moorings: the uprising there can end
                for (int k = 0; k < 8; k++) d.Economy.AddObject(Side.Collective, "Militia", "The Moorings");
            d.Game.Step();
            CheckInvariants(d); CheckSupportInvariants(d);
        }
        return GameSave.StateHash(d.Game);
    }

    [Fact]
    public void ThousandDaysSupportOnSameSeedSameHash_SupportInBounds()
    {
        string a = RunSupport(1, 1000, out var da), b = RunSupport(1, 1000, out _), c = RunSupport(2, 1000, out _);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        var ev = da.Game.Events;
        Assert.Contains(ev, e => e is UprisingStarted);
        Assert.Contains(ev, e => e is UprisingEnded);
        Assert.Contains(ev, e => e is IncidentRolled);
        Assert.Contains(ev, e => e is SupportChanged { Cause: SupportCause.UprisingErosion });
        Assert.Contains(ev, e => e is SupportChanged { Cause: SupportCause.Presence });
    }

    // the only draws of the support sheets: one per uprising start, two dice + one re-arm per incident, one per facility or regiment hit
    [Fact]
    public void ThousandDaysSupportOnDrawOnlyWhatTheSheetsRequire()
    {
        RunSupport(3, 1000, out var d, incidents: false);
        var ev = d.Game.Events;
        long firstCycles = d.Economy.Objects.Count(o => o.FirstDone);
        long smuggleRolls = ev.OfType<ResourceProduced>().Count(e => e.SmugglePercent > 0);
        long picks = ev.OfType<ObjectScrapped>().Count(e => e.Reason == "Autoscrap");
        long support = ev.OfType<UprisingStarted>().Count() + 3L * ev.OfType<IncidentRolled>().Count()
                     + ev.OfType<FacilityDestroyed>().Count(e => e.Reason == DestroyReason.UprisingIncident)
                     + ev.OfType<RegimentDestroyed>().Count();
        // first-cycle draws can be lost with a destroyed facility, so allow up to the number of mines and refineries ever placed
        long rest = d.Game.Rng.Draws - smuggleRolls - picks - support;
        Assert.InRange(rest, 0, d.Economy.Objects.Count(o => o.Class is "Mine" or "Refinery") + ev.OfType<FacilityDestroyed>().Count());
        Assert.True(ev.OfType<IncidentRolled>().Any());
    }

    [Fact]
    public void SupportOnSaveAndLoadMidRunGivesTheSameFuture()
    {
        RunSupport(6, 400, out var whole, reinforce: false);
        var first = DemoScenario.Create(TestData.RepoRoot, 6, true, support: true);
        for (int i = 0; i < 200; i++) first.Game.Step();
        Assert.NotEmpty(first.Game.State.Support.Systems);
        var loaded = GameSave.Load(GameSave.Save(first.Game));
        var fleets = FleetModule.Attach(loaded, TestData.RealGalaxy, Praedra.Core.Tests.Fleets.World.Ships.Value, Praedra.Core.Tests.Fleets.World.Blockade);
        var eco = EconomyModule.Attach(loaded, TestData.RealGalaxy, EcoWorld.Slots.Value, EcoWorld.Costs.Value, fleets);
        SupportModule.Attach(loaded, TestData.RealGalaxy, eco, fleets, GarrisonWeights.Load(Path.Combine(TestData.DesignDir, GarrisonWeights.FileName)));
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded));
        for (int i = 0; i < 200; i++) loaded.Step();
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded));
    }

    // the New ruleset runs the whole demo too (weights from the data file)
    [Fact]
    public void ThousandDaysNewRulesetRuns()
    {
        var d = DemoScenario.Create(TestData.RepoRoot, 8, true, support: true);
        d.Support!.Ruleset = GarrisonRuleset.New;
        for (int i = 0; i < 1000; i++) { d.Game.Step(); CheckSupportInvariants(d); }
        Assert.Contains(d.Game.Events, e => e is UprisingStarted);
    }

    [Fact]
    public void FacilityKindsAllHaveCostRows()
    {
        foreach (var k in FacilityKinds.All)
        {
            var row = EcoWorld.Costs.Value.Get(FacilityKinds.ClassName(k));
            Assert.Equal(ObjectCategory.Facility, row.Category);
        }
        Assert.Equal(0, EcoWorld.Costs.Value.Get("Mine").Maintenance);
        Assert.Equal(20, EcoWorld.Costs.Value.Get("Refinery").Cost);
    }
}
