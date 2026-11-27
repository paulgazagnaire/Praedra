using Praedra.Core.Clock;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §Spying from orbit (Paul 2026-11-27: special forces ride fleets, at most 9, and a team launched from a fleet over its own system is
// checked by every guard at half his chance). Tests OM1 to OM7.
public class OrbitMissionTests
{
    private static MissionWorld World(MissionParams? p = null)
    {
        var w = new MissionWorld(p);
        w.Hold(T, Side.Hand, 0);
        return w;
    }

    private static int Fleet(MissionWorld w, Side side, string system, params string[] ships) => w.Host.Fleets.AddFleet(side, "F", ships, system);
    private static string? Load(MissionWorld w, Side side, int fleet, params int[] sfs) => w.Try(s => new LoadSpecialForcesOrder(s, side, fleet, sfs.ToList()));
    private static string? Unload(MissionWorld w, Side side, int fleet, params int[] sfs) => w.Try(s => new UnloadSpecialForcesOrder(s, side, fleet, sfs.ToList()));

    // OM1: two Listeners on X board a Collective fleet there (its lowest-id ship, no troop slot), ride it to H, and land at H though nobody holds it
    [Fact]
    public void OM1_SpecialForcesBoardRideAndLandAnywhere()
    {
        var w = World();
        int fleet = Fleet(w, Side.Collective, X, "Barricade", "Barricade");
        int a = w.Unit(Side.Collective, Listeners, X), b = w.Unit(Side.Collective, Listeners, X);
        Assert.Null(Load(w, Side.Collective, fleet, a, b));
        var f = w.Game.State.Fleets[fleet];
        int slots = w.Host.Fleets.FreeTroopSlots(f);
        int low = f.Ships.Min();
        Assert.All(new[] { a, b }, id => { var o = w.Host.Economy.Find(id)!; Assert.Equal(low, o.AboardShip); Assert.Null(o.System); });
        Assert.Equal(new[] { a, b }, w.Events<SpecialForcesLoaded>().Single().SpecialForces.Items);
        Assert.Equal(slots, w.Host.Fleets.FreeTroopSlots(f)); // the leader section: no troop slot taken
        w.Host.Fleets.SubmitMove(Side.Collective, fleet, H);
        for (int i = 0; i < 40 && w.Game.State.Fleets[fleet].At != H; i++) w.Game.Step();
        Assert.Equal(H, w.Game.State.Fleets[fleet].At);
        Assert.Null(Unload(w, Side.Collective, fleet, a));
        Assert.Equal(H, w.Host.Economy.Find(a)!.System);
        Assert.Equal(0, w.Host.Economy.Find(a)!.AboardShip);
        Assert.Equal(low, w.Host.Economy.Find(b)!.AboardShip);
    }

    // OM2: at most FleetSpecialForcesMax (9) aboard; a 10th is refused NoCapacity; one on another system NotAtFleetSystem; switch off: OrbitOff
    [Fact]
    public void OM2_NineAboardAtMost()
    {
        var w = World();
        int fleet = Fleet(w, Side.Collective, X, "Barricade");
        var ten = Enumerable.Range(0, 10).Select(_ => w.Unit(Side.Collective, Listeners, X)).ToArray();
        Assert.Equal(RejectReason.NoCapacity, Load(w, Side.Collective, fleet, ten));
        Assert.Null(Load(w, Side.Collective, fleet, ten[..9]));
        Assert.Equal(RejectReason.NoCapacity, Load(w, Side.Collective, fleet, ten[9]));
        int far = w.Unit(Side.Collective, Listeners, H);
        Assert.Equal(RejectReason.NotAtFleetSystem, Load(w, Side.Collective, Fleet(w, Side.Collective, X, "Barricade"), far));

        var off = World(new MissionParams { OrbitMissions = false });
        int f2 = Fleet(off, Side.Collective, X, "Barricade");
        Assert.Equal(MissionReject.OrbitOff, Load(off, Side.Collective, f2, off.Unit(Side.Collective, Listeners, X)));
    }

    // OM3: Listeners aboard a Collective fleet at T (Hand-held, two Hand Speculators on the planet; no Hand fleet, which would mean a battle) spy on
    // T from orbit: the group is launched from the fleet, every guard still rolls, each at half the chance of the foil table
    [Fact]
    public void OM3_FromOrbitEveryGuardChecksAtHalfChance()
    {
        var w = World();
        w.Unit(Side.Hand, Probe, T); w.Unit(Side.Hand, Probe, T);
        int fleet = Fleet(w, Side.Collective, T, "Barricade");
        int a = w.Unit(Side.Collective, Listeners, T);
        Assert.Null(Load(w, Side.Collective, fleet, a));
        w.Stub!.Expect(0, 99, Enumerable.Repeat(99, 20).ToArray());
        long seq = w.Start(Side.Collective, MissionKind.Espionage, Array.Empty<int>(), T, sfs: new[] { a });
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        var m = w.M.Get(w.LastMission)!;
        Assert.Equal(fleet, m.FromFleet);
        Assert.Equal(new[] { a }, m.AboardAtLaunch!);
        Assert.Equal(0, w.Host.Economy.Find(a)!.AboardShip); // left the ship for the mission
        w.RunUntilWorking(m.Id);
        w.RunIteration(m.Id);
        var rolls = w.Events<MissionDetectionRoll>();
        Assert.Equal(2, rolls.Count); // both Speculators checked (no roll stops the walk at 99)
        Assert.All(rolls, r => Assert.Equal(w.M.FoilChance(r.X) * 50 / 100, r.Chance));
    }

    // OM4: the same team standing on the planet (not launched from a fleet) is checked at the full chance
    [Fact]
    public void OM4_FromThePlanetFullChance()
    {
        var w = World();
        w.Unit(Side.Hand, Probe, T); w.Unit(Side.Hand, Probe, T);
        int a = w.Unit(Side.Collective, Listeners, T);
        w.Stub!.Expect(0, 99, Enumerable.Repeat(99, 20).ToArray());
        long seq = w.Start(Side.Collective, MissionKind.Espionage, Array.Empty<int>(), T, sfs: new[] { a });
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        var m = w.M.Get(w.LastMission)!;
        Assert.Equal(0, m.FromFleet);
        w.RunUntilWorking(m.Id);
        w.RunIteration(m.Id);
        var rolls = w.Events<MissionDetectionRoll>();
        Assert.Equal(4, rolls.Count); // from the planet it also meets both Speculators as it sets out (the leaving planet check); from orbit it does not
        Assert.All(rolls, r => Assert.Equal(w.M.FoilChance(r.X), r.Chance));
        Assert.True(rolls[0].Chance > rolls[0].Chance * 50 / 100); // the halving would show
    }

    // OM5: a probe sent from a fleet to another system travels, is checked at full chance there, and comes back aboard its fleet
    [Fact]
    public void OM5_ProbeFromAFleetComesBackAboard()
    {
        var w = World();
        int fleet = Fleet(w, Side.Collective, X, "Barricade");
        int a = w.Unit(Side.Collective, Listeners, X);
        Assert.Null(Load(w, Side.Collective, fleet, a));
        long seq = w.Start(Side.Collective, MissionKind.Espionage, Array.Empty<int>(), T, sfs: new[] { a });
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        var m = w.M.Get(w.LastMission)!;
        Assert.Equal(fleet, m.FromFleet);
        Assert.Equal(X, m.Home);
        for (int i = 0; i < 80 && w.M.Get(m.Id) != null; i++) w.Game.Step();
        Assert.Null(w.M.Get(m.Id));
        Assert.Contains(w.Game.State.Fleets[fleet].Ships, s => s == w.Host.Economy.Find(a)!.AboardShip);
        Assert.Equal(2, w.Events<SpecialForcesLoaded>().Count); // the order, then the return
    }

    // OM6: a group with a character in it is not launched from the fleet (characters cannot ride a mission from a fleet): full chance, home rules
    [Fact]
    public void OM6_MixedGroupIsNotFromOrbit()
    {
        var w = World();
        int fleet = Fleet(w, Side.Collective, T, "Barricade");
        int a = w.Unit(Side.Collective, Listeners, T);
        Assert.Null(Load(w, Side.Collective, fleet, a));
        w.Agent(Ledger, T, esp: 50);
        long seq = w.Start(Side.Collective, MissionKind.Espionage, new[] { Ledger }, T, sfs: new[] { a });
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        var m = w.M.Get(w.LastMission)!;
        Assert.Equal(0, m.FromFleet);
        Assert.Equal(new[] { a }, m.AboardAtLaunch!); // still counts as aboard at the origin (§3)
    }

    // OM7: save and load keep the special forces aboard and a group's launch fleet (state hash unchanged)
    [Fact]
    public void OM7_SaveLoadKeepsItAll()
    {
        var w = World();
        int fleet = Fleet(w, Side.Collective, T, "Barricade");
        int a = w.Unit(Side.Collective, Listeners, T), b = w.Unit(Side.Collective, Listeners, T);
        Assert.Null(Load(w, Side.Collective, fleet, a, b));
        w.Start(Side.Collective, MissionKind.Espionage, Array.Empty<int>(), T, sfs: new[] { a });
        w.Game.Step();
        int id = w.LastMission;
        string before = w.Host.StateHash();
        w.Reload();
        Assert.Equal(before, w.Host.StateHash());
        Assert.Equal(fleet, w.M.Get(id)!.FromFleet);
        Assert.NotEqual(0, w.Host.Economy.Find(b)!.AboardShip);
    }
}
