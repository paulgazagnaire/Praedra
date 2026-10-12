using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Repair;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.Repair;

// P4.2: strategic repair, slot 6.8. rule sheet: m4-retreat-damage-repair section 5; tests RT19 to RT23.
public class RepairTests
{
    private const string Yard = "Kuat", Other = "Coruscant";
    private static readonly int[] StartHits = { 0, 2, 0, 1, 3, 0 };

    // a host with the economy kept quiet (no incidents, autoscrap out of reach) so only the repair changes ships
    private static GameHost Host(ulong seed = 1, GameData? data = null)
    {
        var h = GameHost.New(seed, new GameSettings { IncidentsEnabled = false, Economy = new EconomyParams { ScrapPeriod = 1_000_000 } }, data);
        h.Fleets.SetOwner(Yard, Side.Empire);
        h.Fleets.SetOwner(Other, Side.Empire);
        return h;
    }

    // an ISD at 1790 of 2750 with hits (0,2,0,1,3,0)
    private static (int fleet, EconObject isd) Damaged(GameHost h, Side side, string system)
    {
        int f = h.Fleets.AddFleet(side, "F", new[] { "Imperial Star Destroyer" }, system);
        var isd = h.Game.State.Economy.Objects[h.Game.State.Fleets[f].Ships[0]];
        isd.Hull = 1790; isd.Hits = new List<int>(StartHits);
        return (f, isd);
    }

    private static List<ShipRepaired> Repaired(GameHost h) => h.Game.Events.OfType<ShipRepaired>().ToList();

    // m4-retreat-damage-repair RT19
    [Fact]
    public void IsdIsWholeAfterFourDaysAtAnOwnYard()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard);
        Assert.Equal(2750, h.Data.Ships["Imperial Star Destroyer"].Hull);

        var expect = new (int hull, int[] hits)[]
        {
            (2065, new[] { 0, 1, 0, 0, 2, 0 }), (2340, new[] { 0, 0, 0, 0, 1, 0 }), (2615, new[] { 0, 0, 0, 0, 0, 0 }), (2750, new[] { 0, 0, 0, 0, 0, 0 }),
        };
        for (int day = 0; day < 4; day++)
        {
            h.Game.Step();
            Assert.Equal(expect[day].hull, isd.Hull);
            Assert.Equal(expect[day].hits, isd.Hits!.ToArray());
        }
        var ev = Repaired(h);
        Assert.Equal(4, ev.Count);
        Assert.Equal(new[] { 2065, 2340, 2615, 2750 }, ev.Select(e => e.Hull));
        Assert.Equal(new[] { 3, 1, 0, 0 }, ev.Select(e => e.HitsRemaining));
        Assert.All(ev, e => Assert.Equal(isd.Id, e.Ship));
        h.Game.Step(); // whole and no hits: skipped from now on
        Assert.Equal(4, Repaired(h).Count);
    }

    // m4-retreat-damage-repair RT20 (no yard, an enemy yard only, a non-shipyard yard, an uncompleted yard)
    [Fact]
    public void NoOwnCompletedShipyardMeansNoRepair()
    {
        var h = Host();
        h.Fleets.SetOwner("Hoth", Side.Empire);
        h.Fleets.SetOwner("Dantooine", Side.Empire);
        h.Economy.AddFacility("Hoth", FacilityKind.TrainingFacility);          // a yard, but not production family 0x28
        h.Economy.AddFacility("Dantooine", FacilityKind.OrbitalShipyard, completed: false); // not completed
        h.Economy.AddFacility(Yard, FacilityKind.AdvancedShipyard);             // Empire yard; the ship below is Alliance
        var ships = new[]
        {
            Damaged(h, Side.Empire, Other).isd, Damaged(h, Side.Empire, "Hoth").isd, Damaged(h, Side.Empire, "Dantooine").isd,
            Damaged(h, Side.Alliance, Yard).isd, // only an enemy yard here
        };
        h.Game.Step(); h.Game.Step(); // prove nothing happens early too
        for (int i = 0; i < 28; i++) h.Game.Step();
        Assert.Equal(30, h.Game.Day);
        Assert.All(ships, s => { Assert.Equal(1790, s.Hull); Assert.Equal(StartHits, s.Hits!.ToArray()); });
        Assert.Empty(Repaired(h));
    }

    // m4-retreat-damage-repair RT20 (a yard of the Alliance on a system the Empire holds is not "own")
    [Fact]
    public void AYardOnAnEnemyHeldSystemIsNotOwn()
    {
        var h = Host();
        h.Economy.AddFacility(Other, FacilityKind.OrbitalShipyard); // Coruscant is Empire-held
        var (_, isd) = Damaged(h, Side.Alliance, Other);
        h.Game.Step();
        Assert.Equal(1790, isd.Hull);
        Assert.Empty(Repaired(h));
    }

    // m4-retreat-damage-repair RT19 (Advanced Orbital yards count, family 0x28)
    [Fact]
    public void AdvancedShipyardRepairsToo()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.AdvancedShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard);
        h.Game.Step();
        Assert.Equal(2065, isd.Hull);
    }

    // m4-retreat-damage-repair RT21
    [Fact]
    public void ShipThatFoughtThisStepIsSkippedThatDay()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard);
        h.Repair.FoughtThisStep = id => id == isd.Id && h.Game.Day == 0; // fought in the first Step only
        h.Game.Step();
        Assert.Equal(1790, isd.Hull);
        Assert.Equal(StartHits, isd.Hits!.ToArray());
        Assert.Empty(Repaired(h));
        h.Game.Step();
        Assert.Equal(2065, isd.Hull);
        Assert.Single(Repaired(h));
    }

    // m4-retreat-damage-repair section 5 (hook default): nobody fought unless the battle package says so
    [Fact]
    public void HookDefaultsToNobodyFought()
    {
        var h = Host();
        Assert.False(h.Repair.FoughtThisStep(1));
        Assert.False(h.Repair.FoughtThisStep(12345));
    }

    // m4-retreat-damage-repair RT22
    [Fact]
    public void SquadronsAreUntouched()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        int f = h.Fleets.AddFleet(Side.Empire, "F", new[] { "Imperial Star Destroyer" }, Yard, new[] { "TIE Fighter", "TIE Fighter" });
        var squadrons = h.Game.State.Economy.Objects.Values.Where(o => o.Category == ObjectCategory.Fighter).ToList();
        Assert.Equal(2, squadrons.Count);
        for (int i = 0; i < 3; i++) h.Game.Step();
        Assert.Empty(Repaired(h)); // the ISD is whole, the squadrons have no state
        Assert.All(squadrons, s => { Assert.Equal(0, s.Hull); Assert.Null(s.Hits); });
        Assert.NotNull(h.Game.State.Fleets[f]);
    }

    // m4-retreat-damage-repair section 5 (first condition: in a fleet at a system, not in transit)
    [Fact]
    public void ShipInTransitIsNotRepaired()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (f, isd) = Damaged(h, Side.Empire, Yard);
        var fleet = h.Game.State.Fleets[f];
        fleet.At = null;
        fleet.Transit = new FleetTransit { Path = new() { Yard, Other }, HopTicks = new() { 50 }, HopIndex = 0, DueDay = 50 };
        h.Game.Step(); h.Game.Step();
        Assert.Equal(1790, isd.Hull);
        Assert.Empty(Repaired(h));
    }

    // m4-retreat-damage-repair section 5 (order: side Alliance then Empire, ship id ascending)
    [Fact]
    public void AllianceBeforeEmpireThenShipIdAscending()
    {
        var h = Host();
        h.Fleets.SetOwner("Hoth", Side.Alliance);
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        h.Economy.AddFacility("Hoth", FacilityKind.OrbitalShipyard);
        var e1 = Damaged(h, Side.Empire, Yard).isd;
        var e2 = Damaged(h, Side.Empire, Yard).isd;
        var a1 = Damaged(h, Side.Alliance, "Hoth").isd;
        h.Game.Step();
        Assert.Equal(new[] { a1.Id, e1.Id, e2.Id }, Repaired(h).Select(e => e.Ship));
        Assert.True(a1.Id > e1.Id); // the Alliance ship has the higher id: the side order, not the id, comes first
    }

    // m4-retreat-damage-repair section 5: partial hull and a ship with hits but full hull both count as damaged
    [Fact]
    public void HitsAloneKeepAShipInRepairAndHullIsCapped()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard);
        isd.Hull = 2750; isd.Hits = new List<int> { 0, 0, 0, 0, 2, 0 };
        h.Game.Step(); h.Game.Step(); h.Game.Step();
        Assert.Equal(2750, isd.Hull);
        Assert.All(isd.Hits!, x => Assert.Equal(0, x));
        Assert.Equal(2, Repaired(h).Count); // two calls, then whole
    }

    // m4-retreat-damage-repair section 5 (Data values: RepairRules)
    [Fact]
    public void RepairRulesFileHoldsTheSheetDefaults()
    {
        var r = GameHost.New(1).Data.Repair;
        Assert.Equal(new RepairRules(true, 1, 10, 1), r);
        Assert.Equal(r, RepairRules.Load(Path.Combine(TestData.DesignDir, RepairRules.FileName)));
        Assert.Throws<Rebellion.Core.Data.DataLoadException>(() => RepairRules.Parse("key,value,source\nRepairNeedsYard,true,x\n"));
        Assert.Throws<Rebellion.Core.Data.DataLoadException>(() => RepairRules.Parse(
            "key,value,source\nRepairNeedsYard,maybe,x\nRepairCadenceDays,1,x\nRepairHullDivisor,10,x\nRepairHitsPerCall,1,x\n"));
        Assert.Throws<Rebellion.Core.Data.DataLoadException>(() => RepairRules.Parse(
            "key,value,source\nRepairNeedsYard,true,x\nRepairCadenceDays,1,x\nRepairHullDivisor,0,x\nRepairHitsPerCall,1,x\n"));
    }

    // m4-retreat-damage-repair section 5 (the four values are data, not code)
    [Fact]
    public void TheDataValuesChangeTheBehaviour()
    {
        // no yard needed: heals anywhere; divisor 5: 550 per call; two hit levels per call; cadence 2: every second day
        var data = GameData.Default with { Repair = new RepairRules(false, 2, 5, 2) };
        var h = Host(data: data);
        var (_, isd) = Damaged(h, Side.Empire, Other);
        h.Game.Step();
        Assert.Equal(1790, isd.Hull); // hook time 1: not a repair day
        h.Game.Step();                // hook time 2
        Assert.Equal(2340, isd.Hull);
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 0 }, isd.Hits!.ToArray());
    }

    // m4-retreat-damage-repair RT23: same seed, same events; repair draws no random numbers
    [Fact]
    public void RepairIsDeterministicAndDrawsNothing()
    {
        string Run(out long draws, bool damaged = true)
        {
            var h = Host(seed: 7);
            h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
            if (damaged) Damaged(h, Side.Empire, Yard);
            for (int i = 0; i < 6; i++) h.Game.Step();
            draws = h.Game.Rng.Draws;
            return string.Join("|", h.Game.Events.Select(e => $"{e.Kind}@{e.Day}")) + h.StateHash();
        }
        Assert.Equal(Run(out _), Run(out _));
        Run(out var withRepair);
        Run(out var without, damaged: false); // the other slots draw the same with nothing to repair
        Assert.Equal(without, withRepair);
    }

    // m4-retreat-damage-repair section 5 (slot 6.8, after 6.7) and the host wiring
    [Fact]
    public void HostExposesRepairAsTheLastModule()
    {
        var h = GameHost.New(1);
        Assert.NotNull(h.Repair);
        Assert.Same(h.Data.Repair, h.Repair.Rules);
        Assert.Contains("6.8", Game.WorkList);
        Assert.True(Array.IndexOf(Game.WorkList.ToArray(), "6.8") > Array.IndexOf(Game.WorkList.ToArray(), "6.7"));
    }
}
