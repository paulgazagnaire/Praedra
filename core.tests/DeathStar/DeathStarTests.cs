using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.DeathStar;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Fog;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Missions;
using static Rebellion.Core.Tests.DeathStar.DsWorld;

namespace Rebellion.Core.Tests.DeathStar;

/// <summary>The tests of m5-death-star §Tests (DS1 to DS14), plus the switch defaults and a few cases the table leaves implicit.</summary>
public class DeathStarTests
{
    // ------------------------------------------------------------------ building it (§1)

    // m5-death-star DS1
    [Fact]
    public void DS1_BuildFromDayOneAndDeployed()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Empire, 30);
        w.Host.Economy.AddFacility(A1, FacilityKind.OrbitalShipyard);
        w.Host.Economy.SetPools(Side.Empire, 0, 1000);
        long seq = w.Host.Construction.SubmitBuild(Side.Empire, A1, YardFamily.Shipyard, DeathStarClass, 1);
        w.Game.Step(); // day 1: queued, research order 0 (available from day one)
        Assert.Null(w.Reason(seq));
        var started = Assert.Single(w.Events<ProjectStarted>());
        Assert.Equal((A1, DeathStarClass, 1), (started.System, started.Class, started.Count));
        // the cost is 584 points of 4-day cycles; the sheet takes the normal cost-derived time, so start the project one cycle short
        var project = w.Host.Construction.ProjectAt(A1, YardFamily.Shipyard)!;
        Assert.Equal(584, w.Host.Economy.Costs.Get(DeathStarClass).Cost);
        project.Progress = 583;
        for (int i = 0; i < 12 && !w.Events<DeathStarDeployed>().Any(); i++) w.Game.Step();
        var ev = Assert.Single(w.Events<DeathStarDeployed>());
        Assert.Equal((Side.Empire, A1), (ev.Side, ev.System));
        Assert.Equal(3, w.Game.AudienceOf(ev.Index)); // both sides: fog does not hide it
        Assert.True(w.Ds.HasDeathStar(Side.Empire, A1));
    }

    // m5-death-star DS2
    [Fact]
    public void DS2_OnePerSystem()
    {
        var w = new DsWorld();
        foreach (var s in new[] { A1, A2 }) { w.Hold(s, Side.Empire, 30); w.Host.Economy.AddFacility(s, FacilityKind.OrbitalShipyard); }
        w.Host.Economy.SetPools(Side.Empire, 0, 1000);
        w.DeathStar(Side.Empire, A1);
        long atX = w.Host.Construction.SubmitBuild(Side.Empire, A1, YardFamily.Shipyard, DeathStarClass, 1);
        long atY = w.Host.Construction.SubmitBuild(Side.Empire, A2, YardFamily.Shipyard, DeathStarClass, 1);
        w.Game.Step();
        Assert.Equal(DeathStarReject.DeathStarLimit, w.Reason(atX));
        Assert.Null(w.Reason(atY));
        // an order for two at once also exceeds the cap of one
        Assert.Equal(DeathStarReject.DeathStarLimit, new DsWorld().TryBuild(2));
    }

    // ------------------------------------------------------------------ Destroy System (§2)

    // m5-death-star DS3
    [Fact]
    public void DS3_NoDeathStarInTheFleet()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Alliance);
        int f = w.Fleet(Side.Empire, A1, Isd);
        Assert.Equal(DeathStarReject.NoDeathStar, w.Destroy(Side.Empire, f));
        Assert.False(w.Game.State.DeathStar.IsDestroyed(A1));
        // the other legality codes: unknown fleet, not yours, in hyperspace, already destroyed
        int ds = w.DeathStar(Side.Empire, A1);
        Assert.Equal(RejectReason.UnknownFleet, w.Destroy(Side.Empire, 9999));
        Assert.Equal(RejectReason.NotYours, w.Destroy(Side.Alliance, ds));
    }

    // m5-death-star DS4
    [Fact]
    public void DS4_DestroyANeutralSystemWithAnEnemyFleetAndAFacility()
    {
        var w = new DsWorld();
        w.Hold(A3, null, 50);
        int mine = w.Host.Economy.AddFacility(A3, FacilityKind.Mine);
        int reg = w.Host.Economy.AddObject(Side.Alliance, "Alliance Army Regiment", A3);
        // A fleet of both sides at one system fights at slot 5, before the strike at 5.1, so an enemy fleet that is still there at 5.1 is one that
        // the battle left alone (a stalemate). The test puts it there after the battle, at the end of slot 5, to reach the rule on its own.
        int enemy = 0, aboard = 0; var enemyShips = new List<int>();
        w.Game.RegisterHook("5", ctx =>
        {
            if (enemy != 0) return;
            enemy = w.Fleet(Side.Alliance, A3, Corvette, Corvette);
            aboard = w.Host.Economy.AddObject(Side.Alliance, "Alliance Army Regiment");
            w.Host.Economy.Find(aboard)!.AboardShip = w.ShipOf(enemy);
            enemyShips.AddRange(w.Game.State.Fleets[enemy].Ships);
        });
        int attacker = w.DeathStar(Side.Empire, A3, Isd);
        int dsShip = w.ShipOf(attacker);
        int hull = w.Host.Economy.Find(dsShip)!.Hull;

        Assert.Null(w.Destroy(Side.Empire, attacker)); // enemy fleets present do not block it (DEFAULT)

        Assert.True(w.Game.State.DeathStar.IsDestroyed(A3));
        Assert.Null(w.Host.Fleets.OwnerOf(A3));
        Assert.False(w.FleetExists(enemy));
        foreach (var s in enemyShips) Assert.False(w.Exists(s));
        Assert.False(w.Exists(aboard)); // everything aboard goes with the fleet
        Assert.False(w.Exists(mine));   // the facility is gone, though nobody held the system
        Assert.False(w.Exists(reg));    // and so is the regiment on the planet
        Assert.Equal(DestroyReason.DeathStar, Assert.Single(w.Events<FacilityDestroyed>()).Reason);
        Assert.True(w.FleetExists(attacker));
        Assert.Equal(2, w.Game.State.Fleets[attacker].Ships.Count); // the attacker's fleet is intact
        Assert.Equal(hull, w.Host.Economy.Find(dsShip)!.Hull);       // the Death Star is not damaged or consumed
        var ev = Assert.Single(w.Events<SystemDestroyed>());
        Assert.Equal((A3, Side.Empire, (Side?)null), (ev.System, ev.AttackerSide, ev.PriorHolder));
        Assert.Equal(3, w.Game.AudienceOf(ev.Index));
        var lost = Assert.Single(w.Events<FleetDestroyedByDeathStar>());
        Assert.Equal((enemy, Side.Alliance), (lost.Fleet, lost.FleetSide));
        Assert.True(w.Host.Fog.IsExplored(Side.Alliance, A3) && w.Host.Fog.IsExplored(Side.Empire, A3));
        Assert.True(w.Host.Fog.Picture(Side.Alliance, A3)!.Destroyed && w.Host.Fog.Picture(Side.Empire, A3)!.Destroyed); // both views know
        // no slots, no support value, no production there
        Assert.Equal((0, 0), (w.Host.Economy.SlotsOf(A3).Energy, w.Host.Economy.SlotsOf(A3).Raw));
        Assert.Equal((0, 0), (w.Alliance(A3), w.Host.Economy.SupportOf(A3, Side.Empire)));
    }

    /// <summary>The first seed whose first <paramref name="draws"/> rolls of rand(1..100) give exactly <paramref name="hits"/> at 50 or less (so the test sees both outcomes).</summary>
    private static ulong FindSeed(int draws, int hits)
    {
        for (ulong seed = 1; ; seed++)
        {
            var rng = new GameRng(seed);
            if (Enumerable.Range(0, draws).Count(_ => rng.NextInt(1, 100) <= 50) == hits) return seed;
        }
    }

    // m5-death-star DS5
    [Fact]
    public void DS5_CharactersOnTheDestroyedPlanet()
    {
        ulong seed = FindSeed(draws: 4, hits: 2);
        var w = new DsWorld(seed: seed);
        var minors = new[] { Drayson, Borsk, Tura, Derlin };
        w.Hold(A1, Side.Alliance, 80); w.Hold(B3, Side.Alliance, 80); w.Hold(A6, Side.Alliance, 80);
        foreach (var m in minors) w.Person(m, A1);
        w.Person(Luke, A1);
        int attacker = w.DeathStar(Side.Empire, A1);
        var replica = new GameRng(seed);
        var kills = minors.Select(_ => replica.NextInt(1, 100) <= 50).ToList(); // id order, p = 50

        Assert.Null(w.Destroy(Side.Empire, attacker));

        for (int i = 0; i < minors.Length; i++)
            Assert.Equal(kills[i] ? CharacterStatus.Dead : CharacterStatus.Free, w.C(minors[i]).Status);
        Assert.Equal(replica.Export(), w.Game.Rng.Export()); // exactly four draws: the major cost none
        Assert.Equal(CharacterStatus.Free, w.C(Luke).Status);
        // every survivor, the major included, leaves for the nearest system its side holds (B3 is three steps away, A6 five), arriving the next day
        var survivors = minors.Where((_, i) => !kills[i]).Append(Luke).ToList();
        foreach (var id in survivors)
        {
            var c = w.C(id);
            Assert.Null(c.System);
            Assert.Equal(B3, c.Transit!.Path[^1]);
        }
        w.Game.Step();
        foreach (var id in survivors) { Assert.Equal(B3, w.C(id).System); Assert.Null(w.C(id).Transit); }
        Assert.Equal(survivors.Count, w.Events<CharacterArrived>().Count);
    }

    // m5-death-star DS5, the last sentence: no own system left, so the survivors are captured by the attacker
    [Fact]
    public void DS5_NoHeldSystemLeftMeansCaptured()
    {
        ulong seed = FindSeed(draws: 4, hits: 2);
        var w = new DsWorld(seed: seed);
        var minors = new[] { Drayson, Borsk, Tura, Derlin };
        w.Hold(A1, Side.Alliance, 80);
        foreach (var m in minors) w.Person(m, A1);
        w.Person(Luke, A1);
        int attacker = w.DeathStar(Side.Empire, A1);
        Assert.Null(w.Destroy(Side.Empire, attacker));
        var luke = w.C(Luke);
        Assert.Equal((CharacterStatus.Captured, Side.Empire, A1), (luke.Status, luke.Captor!.Value, luke.HeldAt));
        Assert.Equal(2, minors.Count(m => w.C(m).Status == CharacterStatus.Captured));
        Assert.Equal(2, minors.Count(m => w.C(m).Status == CharacterStatus.Dead));
        Assert.All(w.Events<CharacterCaptured>(), e => Assert.Equal("DeathStar", e.Cause));
    }

    [Fact]
    public void DS5_NearestSystemTiesBreakBySystemIndexAndTheAttackersSideBoardsItsFleet()
    {
        // m5-death-star §2 effect 3: fewest lane steps, ties by system id; one of the attacker's side boards the attacking fleet
        var w = new DsWorld();
        w.Hold(A1, Side.Empire, 20); w.Hold(A2, Side.Alliance, 80); w.Hold(B1, Side.Alliance, 80);
        w.Person(Luke, A1);   // Alliance major: A2 and B1 are both one step away; A2 comes first
        w.Person(Vader, A1);  // Empire major: boards the Death Star's fleet
        int attacker = w.DeathStar(Side.Empire, A1);
        Assert.Null(w.Destroy(Side.Empire, attacker));
        Assert.Equal(A2, w.C(Luke).Transit!.Path[^1]);
        var vader = w.C(Vader);
        Assert.Equal((attacker, CharacterPost.None, A1), (vader.AttachedFleet, vader.Post, vader.System));
    }

    // m5-death-star DS6
    [Fact]
    public void DS6_DestroyingTheRebelHqWinsTheGameForTheEmpire()
    {
        var w = new DsWorld(settings: new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true, Economy = new EconomyParams { ScrapPeriod = 1_000_000 }, Victory = OldVictory.Rules }); // HQ-only set explicitly: the test's subject is the HQ's destruction
        w.Hold(A1, Side.Alliance, 80); w.Hold(B1, Side.Alliance, 80); w.Hold(A2, Side.Empire, 20);
        w.Host.Fog.PlaceHq(A1);
        w.Game.State.Scenario.Active = true;
        int attacker = w.DeathStar(Side.Empire, A1);
        Assert.Null(w.Destroy(Side.Empire, attacker));
        var hq = w.Game.State.Scenario.Hq;
        Assert.Equal((HqStatus.Destroyed, FogModule.CauseSystemDestroyed, A1), (hq.Status, hq.DestroyedCause, hq.System));
        var hqEvent = Assert.Single(w.Events<HqDestroyed>());
        Assert.Equal(3, w.Game.AudienceOf(hqEvent.Index));
        Assert.Equal(Side.Empire, w.Host.Victory.Winner); // the victory step of the same day (slot 6.11)
        Assert.Equal(1, w.Game.Day);
        Assert.Equal(Side.Empire, Assert.Single(w.Events<Rebellion.Core.Victory.GameWon>()).Winner);
    }

    // m5-death-star DS7
    [Fact]
    public void DS7_FirstStrikeInASectorOfSix()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Empire, 40);
        w.Hold(A2, Side.Empire, 40); w.Hold(A3, null, 50); w.Hold(A4, Side.Alliance, 95); w.Hold(A5, Side.Empire, 0); w.Hold(A6, Side.Alliance, 70);
        w.Hold(B1, Side.Empire, 33); w.Hold(B2, Side.Alliance, 77); w.Hold(B3, null, 5);
        int attacker = w.DeathStar(Side.Empire, A1);
        w.Game.Step(); // slot 5.6 raises the sector flag: its damping must not touch the strike's own change
        Assert.True(w.Host.Support.IsDeathStarSector(A2));
        Assert.Null(w.Destroy(Side.Empire, attacker));
        Assert.Equal(new[] { 50, 60, 100, 10, 80 }, new[] { A2, A3, A4, A5, A6 }.Select(w.Alliance).ToArray()); // +10 each, A4 clamped
        Assert.Equal(new[] { 33, 77, 5 }, new[] { B1, B2, B3 }.Select(w.Alliance).ToArray()); // nothing outside the sector changes
        var changes = w.Events<SupportChanged>().Where(e => e.Cause == SupportCause.DeathStar).ToList();
        Assert.Equal(new[] { A2, A3, A4, A5, A6 }, changes.Select(e => e.System).ToArray());
        Assert.Empty(w.Events<SystemCaptured>());
        Assert.Equal(1, w.Ds.StrikesIn(Sa));
        Assert.Equal(0, w.Ds.StrikesIn(Sb));
        var sector = Assert.Single(w.Events<SectorStruck>());
        Assert.Equal((Sa, 1, 5), (sector.Sector, sector.Strike, sector.Systems.Items.Count));
    }

    // m5-death-star DS8
    [Fact]
    public void DS8_SecondStrikeInTheSameSector()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Empire, 40); w.Hold(A2, Side.Empire, 40);
        w.Hold(A3, null, 50);                 // neutral: becomes Alliance-held at 100
        w.Hold(A4, Side.Empire, 30);          // Imperial with a garrison of 5 regiments (the strike-2 requirement is 6: 12 in an uprising, halved in a Death Star sector): stays Imperial, at 100, uprising at once
        for (int i = 0; i < 5; i++) w.Host.Economy.AddObject(Side.Empire, "Imperial Army Regiment", A4);
        w.Hold(A5, Side.Alliance, 60);        // Alliance: just goes to 100
        w.Hold(A6, null, 50);
        w.Hold(B1, Side.Alliance, 12);
        int first = w.DeathStar(Side.Empire, A1), second = w.DeathStar(Side.Empire, A2);
        w.Game.Step();
        Assert.Null(w.Destroy(Side.Empire, first));
        Assert.Equal(1, w.Ds.StrikesIn(Sa));
        Assert.False(w.Host.Support.IsUprising(A4)); // the garrison covers the first strike's change (Empire support 60: no requirement)
        Assert.Equal(40, w.Alliance(A4)); // 30 + 10
        Assert.Null(w.Destroy(Side.Empire, second));
        Assert.Equal(2, w.Ds.StrikesIn(Sa));

        Assert.Equal(Side.Alliance, w.Host.Fleets.OwnerOf(A3));
        Assert.Equal(Side.Alliance, w.Host.Fleets.OwnerOf(A6));
        Assert.Equal(CaptureCause.DeathStarRevolt, w.Events<SystemCaptured>().First(e => e.System == A3).Cause);
        Assert.Equal(Side.Empire, w.Host.Fleets.OwnerOf(A4));
        Assert.True(w.Host.Support.IsUprising(A4));
        Assert.Equal(A4, Assert.Single(w.Events<UprisingStarted>().Where(e => e.System != B1)).System); // B1 (Alliance, support 12, no garrison) rises on day 1 by itself
        Assert.Equal(Side.Alliance, w.Host.Fleets.OwnerOf(A5));
        foreach (var s in new[] { A3, A4, A5, A6 }) Assert.Equal(100, w.Alliance(s));
        Assert.Equal(12, w.Alliance(B1)); // nothing outside the sector
    }

    // m5-death-star DS9
    [Fact]
    public void DS9_ADestroyedSystemIsPassableDebris()
    {
        var w = new DsWorld();
        w.Hold(A2, Side.Empire, 20); w.Hold(A4, Side.Empire, 20);
        w.Hold(A3, null, 50);
        int attacker = w.DeathStar(Side.Empire, A3);
        Assert.Null(w.Destroy(Side.Empire, attacker));
        Assert.True(w.Game.State.DeathStar.IsDestroyed(A3));

        // a fleet flies through it to A4, another stops there
        int through = w.Fleet(Side.Empire, A2, Corvette), stops = w.Fleet(Side.Empire, A2, Corvette);
        w.Host.Fleets.SubmitMove(Side.Empire, through, A4);
        w.Host.Fleets.SubmitMove(Side.Empire, stops, A3);
        for (int i = 0; i < 400 && (w.Game.State.Fleets[through].At != A4 || w.Game.State.Fleets[stops].At != A3); i++) w.Game.Step();
        Assert.Equal(A4, w.Game.State.Fleets[through].At);
        Assert.Equal(A3, w.Game.State.Fleets[stops].At);
        Assert.Contains(w.Events<FleetArrived>(), e => e.Fleet == stops && e.System == A3);

        // no holder, no support, no build, no assault, no bombardment, no capture, no mission
        Assert.Null(w.Host.Fleets.OwnerOf(A3));
        Assert.Equal((0, 0), (w.Alliance(A3), w.Host.Economy.SupportOf(A3, Side.Empire)));
        w.Host.Economy.AddFacility(A3, FacilityKind.OrbitalShipyard); // even a yard put there by hand cannot build: the order is refused first
        long build = w.Host.Construction.SubmitBuild(Side.Empire, A3, YardFamily.Shipyard, Corvette, 1);
        long assault = w.Host.Assault.SubmitAssault(Side.Empire, stops, A3);
        long bombard = w.Host.Bombardment.SubmitBombard(Side.Empire, stops, A3, Rebellion.Core.Defenses.BombardCategory.General);
        long capture = w.Host.Control.SubmitDebugSetHolder(Side.Empire, A3, Side.Empire);
        var agent = w.Person(Luke, A2);
        agent.Skills[(int)Skill.Espionage] = 60;
        long mission = w.Game.Submit(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Luke }, new(), new(), A3));
        w.Game.Step();
        foreach (var seq in new[] { build, assault, bombard, capture, mission })
            Assert.Equal(RejectReason.SystemDestroyed, w.Reason(seq));
    }

    // m5-death-star DS10
    [Fact]
    public void DS10_TheSectorFlagFollowsTheDeathStar()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Empire, 20);
        int ds = w.DeathStar(Side.Empire, A1);
        bool Flag(string s) => w.Host.Support.IsDeathStarSector(s);
        w.Game.Step();
        Assert.All(new[] { A1, A2, A3, A4, A5, A6 }, s => Assert.True(Flag(s)));
        Assert.All(new[] { B1, B2, B3 }, s => Assert.False(Flag(s)));
        w.Host.Fleets.SubmitMove(Side.Empire, ds, B1);
        bool arrivedStep = false;
        for (int i = 0; i < 2000 && !arrivedStep; i++)
        {
            w.Game.Step();
            arrivedStep = w.Game.State.Fleets[ds].At == B1;
            if (!arrivedStep) Assert.All(new[] { A1, B1 }, s => Assert.False(Flag(s))); // in hyperspace the Death Star is in no sector
        }
        Assert.True(arrivedStep);
        Assert.All(new[] { B1, B2, B3 }, s => Assert.True(Flag(s)));   // set at slot 5.6 of the arrival day
        Assert.All(new[] { A1, A2, A3, A4, A5, A6 }, s => Assert.False(Flag(s)));
    }

    // m5-death-star DS11
    [Fact]
    public void DS11_SabotageRemovesTheDeathStarAndTheFleetKeepsTheRest()
    {
        var w = new MissionWorld();
        w.Hold(MissionWorld.T, Side.Empire, 0);
        int fleet = w.Host.Fleets.AddFleet(Side.Empire, "F", new[] { DeathStarClass, Isd }, MissionWorld.T);
        int dsShip = w.Game.State.Fleets[fleet].Ships.First(id => w.Host.Economy.Find(id)!.Class == DeathStarClass);
        int isdShip = w.Game.State.Fleets[fleet].Ships.First(id => id != dsShip);
        w.Agent(MissionWorld.Luke, MissionWorld.T, esp: 100, com: 100);
        w.Stub!.Expect(0, 99, 99, 99); // the Death Star and the destroyer are defenders with a detection roll each: 99 is not detected
        w.Start(Side.Alliance, MissionKind.DeathStarSabotage, new[] { MissionWorld.Luke }, MissionWorld.T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Null(w.Host.Economy.Find(dsShip));
        Assert.NotNull(w.Host.Economy.Find(isdShip));
        Assert.Equal(new[] { isdShip }, w.Game.State.Fleets[fleet].Ships);
        var ev = Assert.Single(w.Events<DeathStarDestroyed>());
        Assert.Equal(("Sabotage", Side.Empire, dsShip), (ev.Cause, ev.Side, ev.Ship));
        Assert.Equal(3, w.Game.AudienceOf(ev.Index));
        Assert.False(w.Host.DeathStar.HasDeathStar(Side.Empire, MissionWorld.T));
    }

    // m5-death-star DS12
    [Fact]
    public void DS12_ShieldBlocksDestroyIsASwitch()
    {
        foreach (bool blocks in new[] { true, false })
        {
            var w = new DsWorld(new DeathStarParams { ShieldBlocksDestroy = blocks });
            w.Hold(A1, Side.Alliance, 80);
            w.Host.Economy.AddFacility(A1, FacilityKind.DeathStarShield);
            int attacker = w.DeathStar(Side.Empire, A1);
            string? reason = w.Destroy(Side.Empire, attacker);
            if (blocks) { Assert.Equal(DeathStarReject.ShieldProtected, reason); Assert.False(w.Game.State.DeathStar.IsDestroyed(A1)); }
            else { Assert.Null(reason); Assert.True(w.Game.State.DeathStar.IsDestroyed(A1)); } // DEFAULT: nothing in the destroy path reads the shield
        }
        // the shield only protects against an enemy: an Empire attacker at an Empire system with its own shield is not stopped
        var own = new DsWorld(new DeathStarParams { ShieldBlocksDestroy = true });
        own.Hold(A1, Side.Empire, 20);
        own.Host.Economy.AddFacility(A1, FacilityKind.DeathStarShield);
        Assert.Null(own.Destroy(Side.Empire, own.DeathStar(Side.Empire, A1)));
    }

    // m5-death-star DS13
    [Fact]
    public void DS13_SaveAfterAStrikeAndLoad()
    {
        var w = new DsWorld();
        w.Hold(A3, null, 50); w.Hold(A2, Side.Empire, 20);
        int attacker = w.DeathStar(Side.Empire, A3);
        Assert.Null(w.Destroy(Side.Empire, attacker));
        string hash = w.Host.StateHash();
        string save = w.Host.Save();
        Assert.Contains("\"DeathStar\":{\"Destroyed\":[\"A3\"],\"StrikesInSector\":{\"SA\":1}}", save);
        w.Reload();
        Assert.Equal(new[] { A3 }, w.Game.State.DeathStar.Destroyed.ToArray());
        Assert.Equal(1, w.Ds.StrikesIn(Sa));
        Assert.Equal(hash, w.Host.StateHash());
        w.Game.Step(); // a loaded game goes on: A3 stays destroyed and cannot be struck twice
        Assert.True(w.Ds.IsDestroyed(A3));
    }

    // m5-death-star DS14
    [Fact]
    public void DS14_SameSeedSameStateHash()
    {
        string a = Campaign(7), b = Campaign(7), c = Campaign(8);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // DS4 to DS8 in one game: a neutral planet with fleets, a facility and characters, then a second strike in the sector.
    private static string Campaign(ulong seed)
    {
        var w = new DsWorld(seed: seed);
        w.Hold(A3, null, 50); w.Hold(A4, Side.Empire, 60); w.Hold(A5, Side.Alliance, 60); w.Hold(A6, null, 50);
        w.Hold(A1, Side.Alliance, 80); w.Hold(A2, Side.Empire, 30); w.Hold(B1, Side.Alliance, 80);
        for (int i = 0; i < 5; i++) w.Host.Economy.AddObject(Side.Empire, "Imperial Army Regiment", A4);
        w.Host.Economy.AddFacility(A3, FacilityKind.Mine);
        w.Fleet(Side.Alliance, A3, Corvette, Corvette);
        foreach (var m in new[] { Drayson, Borsk, Tura, Derlin }) w.Person(m, A3);
        w.Person(Luke, A3);
        int first = w.DeathStar(Side.Empire, A3, Isd), second = w.DeathStar(Side.Empire, A5);
        w.Game.Step();
        w.Destroy(Side.Empire, first);
        w.Run(3);
        w.Destroy(Side.Empire, second);
        w.Run(5);
        return w.Host.StateHash();
    }

    // ------------------------------------------------------------------ the rest of the sheet

    [Fact]
    public void Switches_DefaultsAndRefusedValues()
    {
        var p = new DeathStarParams();
        Assert.Equal((1, false, false, 10, 2, 100), (p.CapPerSystem, p.ShieldBlocksDestroy, p.TrenchRun, p.SectorPenalty, p.RevoltFromStrike, p.RevoltAllianceSupport));
        Assert.Equal(50, new CharacterParams().DeathStarHitPercent);
        Assert.Throws<RuleMissingException>(() => new DsWorld(new DeathStarParams { TrenchRun = true })); // no code until a rule exists
        Assert.Contains("5.6", Game.WorkList);
        Assert.True(Game.WorkList.ToList().IndexOf("5.6") > Game.WorkList.ToList().IndexOf("5.4") && Game.WorkList.ToList().IndexOf("5.6") < Game.WorkList.ToList().IndexOf("6.1"));
    }

    [Fact]
    public void TheOrderRunsAfterTheBombardmentAndIsCancelledWhenTheFleetLeft()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Alliance, 80);
        int attacker = w.DeathStar(Side.Empire, A1);
        long order = w.Ds.SubmitDestroySystem(Side.Empire, attacker);
        long dup = w.Ds.SubmitDestroySystem(Side.Empire, attacker);
        long move = w.Host.Fleets.SubmitMove(Side.Empire, attacker, A2); // accepted after the destroy order: the fleet departs in step 2
        w.Game.Step();
        Assert.Null(w.Reason(order));
        Assert.Equal(DeathStarReject.OrderPending, w.Reason(dup));
        Assert.Null(w.Reason(move));
        var cancelled = Assert.Single(w.Events<DestroySystemCancelled>());
        Assert.Equal((attacker, DeathStarReject.NotAtSystem), (cancelled.Fleet, cancelled.Reason));
        Assert.False(w.Ds.IsDestroyed(A1));
    }

    [Fact]
    public void AnAcceptedOrderIsSavedInTheQueue()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Alliance, 80);
        int attacker = w.DeathStar(Side.Empire, A1);
        w.Ds.SubmitDestroySystem(Side.Empire, attacker); // not yet validated: it waits in the queue, and the queue is saved
        w.Reload();
        Assert.IsType<DestroySystemOrder>(Assert.Single(w.Game.Queue));
        w.Game.Step();
        Assert.True(w.Ds.IsDestroyed(A1));
    }

    [Fact]
    public void ADeathStarLostInBattleIsReported_AndOneDestroyedWithTheSystemToo()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Alliance, 80);
        int mine = w.DeathStar(Side.Empire, A1);
        w.Game.RegisterHook("5", ctx => w.Host.Economy.DestroyUnit(ctx, ctx.Day + 1, w.Host.Economy.Find(w.ShipOf(mine))!)); // the loss path of a battle
        w.Game.Step();
        Assert.Equal("Battle", Assert.Single(w.Events<DeathStarDestroyed>()).Cause);

        // an enemy Death Star standing at a destroyed system dies with it
        var v = new DsWorld();
        v.Hold(A1, Side.Alliance, 80);
        int attacker = v.DeathStar(Side.Empire, A1), victim = 0;
        v.Game.RegisterHook("5", ctx => { if (victim == 0) victim = v.DeathStar(Side.Alliance, A1); }); // after the battle of slot 5 (see DS4)
        Assert.Null(v.Destroy(Side.Empire, attacker));
        Assert.False(v.FleetExists(victim));
        var lost = Assert.Single(v.Events<DeathStarDestroyed>());
        Assert.Equal(("DeathStar", Side.Alliance), (lost.Cause, lost.Side));
    }

    [Fact]
    public void QueuedWorkAtTheSystemIsCancelledAndNothingRemainsOnIt()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Empire, 20);
        w.Host.Economy.AddFacility(A1, FacilityKind.OrbitalShipyard);
        w.Host.Economy.SetPools(Side.Empire, 0, 1000);
        w.Host.Construction.SubmitBuild(Side.Empire, A1, YardFamily.Shipyard, Isd, 2);
        w.Game.Step();
        Assert.NotNull(w.Host.Construction.ProjectAt(A1, YardFamily.Shipyard));
        int attacker = w.DeathStar(Side.Empire, A1);
        Assert.Null(w.Destroy(Side.Empire, attacker));
        Assert.Null(w.Host.Construction.ProjectAt(A1, YardFamily.Shipyard));
        Assert.DoesNotContain(w.Host.Economy.Objects, o => o.System == A1 && o.Category == ObjectCategory.Facility);
        Assert.Contains(w.Events<ProjectCancelled>(), e => e.Reason == "SystemDestroyed");
        // the yard's energy slots are gone with the system
        Assert.Equal(0, w.Host.Economy.SlotsOf(A1).Energy);
        Assert.Equal("NoSlot", w.Host.Economy.CheckPlace(A1, FacilityKind.Mine));
    }

    [Fact]
    public void ARunningMissionAtTheSystemEndsAsATargetDestroyedFailure()
    {
        var w = new MissionWorld();
        w.Hold(MissionWorld.T, Side.Empire, 20);
        w.Agent(MissionWorld.Leia, MissionWorld.T, esp: 50);
        w.Stub!.Expect(0, 20, 20); // a long timer: the mission stays working
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { MissionWorld.Leia }, MissionWorld.T);
        w.Game.Step();
        Assert.Equal(MissionPhase.Working, w.M.Missions.Single().Phase);
        int attacker = w.Host.Fleets.AddFleet(Side.Empire, "DS", new[] { DeathStarClass }, MissionWorld.T);
        long seq = w.Host.DeathStar.SubmitDestroySystem(Side.Empire, attacker);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        Assert.Empty(w.M.Missions);
        Assert.Equal(MissionOutcome.TargetDestroyed, Assert.Single(w.Events<MissionEnded>()).Outcome);
    }
}

internal static class DsWorldExtensions
{
    /// <summary>Order <paramref name="count"/> Death Stars at a fresh Empire yard with no Death Star in sight, and return the rejection reason.</summary>
    public static string? TryBuild(this DsWorld w, int count)
    {
        w.Hold(A1, Side.Empire, 30);
        w.Host.Economy.AddFacility(A1, FacilityKind.OrbitalShipyard);
        w.Host.Economy.SetPools(Side.Empire, 0, 1000);
        long seq = w.Host.Construction.SubmitBuild(Side.Empire, A1, YardFamily.Shipyard, DsWorld.DeathStarClass, count);
        w.Game.Step();
        return w.Reason(seq);
    }
}
