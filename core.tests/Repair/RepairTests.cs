using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Repair;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Repair;

// Strategic repair, slot 6.8. rule sheet: m4-retreat-damage-repair section 5 (Traced, batch B3 section 15); tests B3-24 to B3-26 and the
// sheet's RT tests that still apply.
public class RepairTests
{
    private const string Yard = "Navalia", Other = "Charter";
    // weapons 2, sub-light 1, primary hyperdrive 1 (B3-24)
    private static readonly int[] StartHits = { 0, 2, 0, 1, 1, 0 };

    // a host with the economy kept quiet (no incidents, autoscrap out of reach) so only the repair changes ships
    private static GameHost Host(ulong seed = 1, GameData? data = null)
    {
        var h = GameHost.New(seed, new GameSettings { IncidentsEnabled = false, Economy = new EconomyParams { ScrapPeriod = 1_000_000 } }, data);
        h.Fleets.SetOwner(Yard, Side.Empire);
        h.Fleets.SetOwner(Other, Side.Empire);
        Garrison(h, Yard, "Tranches");
        return h;
    }

    // regiments keep the yard system out of an uprising (an uprising suspends the yard and drops FastRepair)
    private static void Garrison(GameHost h, string system, string regiment)
    {
        for (int i = 0; i < 3; i++) h.Economy.AddObject(h.Fleets.OwnerOf(system)!.Value, regiment, system);
    }

    // an ISD at 1790 of 2750 with hits (0,2,0,1,1,0)
    private static (int fleet, EconObject isd) Damaged(GameHost h, Side side, string system, int[]? hits = null)
    {
        int f = h.Fleets.AddFleet(side, "F", new[] { "Mandate" }, system);
        var isd = h.Game.State.Economy.Objects[h.Game.State.Fleets[f].Ships[0]];
        isd.Hull = 1790; isd.Hits = new List<int>(hits ?? StartHits);
        return (f, isd);
    }

    private static List<ShipRepaired> Repaired(GameHost h) => h.Game.Events.OfType<ShipRepaired>().ToList();

    // B3-24 (replaces RT19: every 2 days at an own shipyard, one hit per turn on a damage-control roll)
    [Fact]
    public void B3_24_IsdAtItsOwnYardRepairsEveryTwoDays()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard);
        var hulls = new List<int>(); var sums = new List<int> { StartHits.Sum() };
        for (int day = 1; day <= 8; day++)
        {
            h.Game.Step();
            if (day % 2 == 0) { hulls.Add(isd.Hull); sums.Add(isd.Hits!.Sum()); }
            else Assert.Equal(day == 1 ? 1790 : hulls[^1], isd.Hull);
        }
        Assert.Equal(new[] { 2065, 2340, 2615, 2750 }, hulls);
        var ev = Repaired(h);
        Assert.Equal(new[] { 2, 4, 6, 8 }, ev.Select(e => e.Day));
        for (int i = 1; i < sums.Count; i++) Assert.InRange(sums[i - 1] - sums[i], 0, 1); // at most one hit per turn
        Assert.Equal(sums.Skip(1), ev.Select(e => e.HitsRemaining));

        // the draws: per turn with hits one roll rand(0..99) < 30, then a pick when it succeeded and more than one hit was left
        int expected = 0;
        for (int i = 1; i < sums.Count; i++)
        {
            if (sums[i - 1] == 0) continue;
            expected++;
            if (sums[i] < sums[i - 1] && sums[i - 1] > 1) expected++;
        }
        long Draws(bool damaged)
        {
            var g = Host();
            g.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
            if (damaged) Damaged(g, Side.Empire, Yard); else g.Fleets.AddFleet(Side.Empire, "F", new[] { "Mandate" }, Yard);
            for (int d = 0; d < 8; d++) g.Game.Step();
            return g.Game.Rng.Draws;
        }
        Assert.Equal(expected, Draws(true) - Draws(false));
    }

    // B3-25 (replaces RT20: no own yard means the 30-day period, not no repair)
    [Fact]
    public void B3_25_AwayFromAYardEveryThirtyDays()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.AdvancedShipyard); // Empire yard; the Alliance ship there is not at its own yard
        var ships = new[] { Damaged(h, Side.Empire, Other).isd, Damaged(h, Side.Alliance, Yard).isd };
        for (int i = 0; i < 29; i++) h.Game.Step();
        Assert.All(ships, s => Assert.Equal(1790, s.Hull));
        h.Game.Step(); // hook time 30
        Assert.All(ships, s => Assert.Equal(2065, s.Hull));
        Assert.All(Repaired(h), e => Assert.Equal(30, e.Day));
        for (int i = 0; i < 30; i++) h.Game.Step();
        Assert.All(ships, s => Assert.Equal(2340, s.Hull));
    }

    // B3-25 (a ship in a moving fleet does not repair)
    [Fact]
    public void B3_25_ShipInTransitIsNotRepaired()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (f, isd) = Damaged(h, Side.Empire, Yard);
        var fleet = h.Game.State.Fleets[f];
        fleet.At = null;
        fleet.Transit = new FleetTransit { Path = new() { Yard, Other }, HopTicks = new() { 50 }, HopIndex = 0, DueDay = 50 };
        for (int i = 0; i < 40; i++) h.Game.Step();
        Assert.Equal(1790, isd.Hull);
        Assert.Empty(Repaired(h));
        Assert.Equal(0, isd.RepairMode);
    }

    // B3-25 (a blockade or an uprising suspends the yard: 30 days); m4-retreat-damage-repair Open question 8
    [Fact]
    public void B3_25_SuspendedYardGivesTheThirtyDayPeriod()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard);
        h.Repair.YardSuspended = s => s == Yard;
        for (int i = 0; i < 29; i++) h.Game.Step();
        Assert.Equal(1790, isd.Hull);
        h.Game.Step();
        Assert.Equal(2065, isd.Hull);
        Assert.Equal(1, isd.RepairMode);
    }

    // m4-retreat-damage-repair section 5: the host wires the construction module's suspension test
    [Fact]
    public void HostWiresTheYardSuspension()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        Assert.False(h.Repair.YardSuspended(Yard));
        Assert.True(h.Repair.YardSuspended("The Thaw")); // no owner: suspended
        Assert.True(h.Repair.FastRepair(Yard, Side.Empire));
        Assert.False(h.Repair.FastRepair(Yard, Side.Alliance));
    }

    // m4-retreat-damage-repair section 5: a change of the period restarts the timer from that day
    [Fact]
    public void ChangeOfPeriodRestartsTheTimer()
    {
        var h = Host();
        var (_, isd) = Damaged(h, Side.Empire, Yard); // no yard yet: the 30-day timer runs
        for (int i = 0; i < 10; i++) h.Game.Step();
        Assert.Equal(1, isd.RepairMode);
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard); // day 10: FastRepair from now
        h.Game.Step();  // hook time 11: restarted, due 12
        Assert.Equal(1790, isd.Hull);
        h.Game.Step();  // 12
        Assert.Equal(2065, isd.Hull);
    }

    // B3-26 (replaces RT22: squadrons keep partial losses and get one craft back per turn)
    [Fact]
    public void B3_26_SquadronGetsOneCraftBackEveryTwoDays()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        int id = h.Economy.AddObject(Side.Empire, "Gleaner", Yard);
        var sq = h.Game.State.Economy.Objects[id];
        sq.CraftLost = 5;
        var seen = new List<int>();
        for (int day = 1; day <= 10; day++) { h.Game.Step(); seen.Add(sq.CraftLost); }
        Assert.Equal(new[] { 5, 4, 4, 3, 3, 2, 2, 1, 1, 0 }, seen);
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, h.Game.Events.OfType<SquadronRepaired>().Select(e => e.Day));
        string hash = GameSave.StateHash(h.Game);
        Assert.Equal(hash, GameSave.StateHash(GameSave.Load(GameSave.Save(h.Game))));
    }

    // m4-retreat-damage-repair section 5: craft lost is saved and hashed
    [Fact]
    public void CraftLostIsSavedAndHashed()
    {
        var h = Host();
        int id = h.Economy.AddObject(Side.Empire, "Gleaner", Yard);
        string before = GameSave.StateHash(h.Game);
        h.Game.State.Economy.Objects[id].CraftLost = 3;
        Assert.NotEqual(before, GameSave.StateHash(h.Game));
        var g = GameSave.Load(GameSave.Save(h.Game));
        Assert.Equal(3, g.State.Economy.Objects[id].CraftLost);
    }

    // m4-retreat-damage-repair section 5 (order: side Alliance then Empire, object id ascending)
    [Fact]
    public void AllianceBeforeEmpireThenShipIdAscending()
    {
        var h = Host();
        h.Fleets.SetOwner("The Thaw", Side.Alliance);
        Garrison(h, "The Thaw", "Militia");
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        h.Economy.AddFacility("The Thaw", FacilityKind.OrbitalShipyard);
        var e1 = Damaged(h, Side.Empire, Yard).isd;
        var e2 = Damaged(h, Side.Empire, Yard).isd;
        var a1 = Damaged(h, Side.Alliance, "The Thaw").isd;
        h.Game.Step(); h.Game.Step();
        Assert.Equal(new[] { a1.Id, e1.Id, e2.Id }, Repaired(h).Select(e => e.Ship));
        Assert.True(a1.Id > e1.Id); // the Alliance ship has the higher id: the side order, not the id, comes first
    }

    // m4-retreat-damage-repair section 5: hits alone keep a ship damaged; the hull is capped; a whole ship stops its timer
    [Fact]
    public void HitsAloneKeepAShipInRepairAndHullIsCapped()
    {
        var h = Host();
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var (_, isd) = Damaged(h, Side.Empire, Yard, new[] { 0, 0, 0, 0, 1, 0 });
        isd.Hull = 2750;
        for (int i = 0; i < 60 && isd.Hits!.Sum() > 0; i++) h.Game.Step();
        Assert.Equal(2750, isd.Hull);
        Assert.All(isd.Hits!, x => Assert.Equal(0, x));
        Assert.Equal(0, isd.RepairMode);
        int n = Repaired(h).Count;
        h.Game.Step(); h.Game.Step();
        Assert.Equal(n, Repaired(h).Count); // whole: no more turns
    }

    // m4-retreat-damage-repair section 5 (Data values: RepairRules)
    [Fact]
    public void RepairRulesFileHoldsTheSheetValues()
    {
        var r = GameHost.New(1).Data.Repair;
        Assert.Equal(new RepairRules(false, 30, 2, 30, 2, 10), r);
        Assert.Equal(r, RepairRules.Load(Path.Combine(TestData.DesignDir, RepairRules.FileName)));
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => RepairRules.Parse("key,value,source\nRepairNeedsYard,true,x\n"));
        const string ok = "RepairPeriodDays,30,x\nFastRepairPeriodDays,2,x\nSquadronRepairPeriodDays,30,x\nSquadronFastRepairPeriodDays,2,x\n";
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => RepairRules.Parse("key,value,source\nRepairNeedsYard,maybe,x\n" + ok + "RepairHullDivisor,10,x\n"));
        Assert.Throws<Praedra.Core.Data.DataLoadException>(() => RepairRules.Parse("key,value,source\nRepairNeedsYard,true,x\n" + ok + "RepairHullDivisor,0,x\n"));
    }

    // m4-retreat-damage-repair section 5 (the values are data, not code): RepairNeedsYard on stops repair away from a yard
    [Fact]
    public void TheDataValuesChangeTheBehaviour()
    {
        var data = GameData.Default with { Repair = new RepairRules(true, 30, 3, 30, 3, 5) };
        var h = Host(data: data);
        h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
        var away = Damaged(h, Side.Empire, Other).isd;
        var home = Damaged(h, Side.Empire, Yard).isd;
        for (int i = 0; i < 3; i++) h.Game.Step();
        Assert.Equal(1790 + 550, home.Hull); // divisor 5, period 3
        for (int i = 0; i < 30; i++) h.Game.Step();
        Assert.Equal(1790, away.Hull);
    }

    // m4-retreat-damage-repair section 5: same seed, same events
    [Fact]
    public void RepairIsDeterministic()
    {
        string Run()
        {
            var h = Host(seed: 7);
            h.Economy.AddFacility(Yard, FacilityKind.OrbitalShipyard);
            Damaged(h, Side.Empire, Yard);
            for (int i = 0; i < 12; i++) h.Game.Step();
            return string.Join("|", h.Game.Events.Select(e => $"{e.Kind}@{e.Day}")) + h.StateHash();
        }
        Assert.Equal(Run(), Run());
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
