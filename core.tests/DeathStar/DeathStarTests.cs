using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Harvester;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using Praedra.Core.Victory;
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;
using Praedra.Core.Tests.Missions;
using static Praedra.Core.Tests.Harvester.DsWorld;
using Praedra.Core.Tests.Control;

namespace Praedra.Core.Tests.Harvester;

/// <summary>The tests of m5-death-star §Tests (DS1 to DS14), plus the switch defaults and a few cases the table leaves implicit.</summary>
public class HarvesterTests
{
    // ------------------------------------------------------------------ building it (§1)

    // m5-death-star DS1
    [Fact]
    public void DS1_BuildFromDayOneAndDeployed()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Hand, 30);
        w.Host.Economy.AddFacility(A1, FacilityKind.OrbitalShipyard);
        w.Host.Economy.SetPools(Side.Hand, 0, 1000);
        long seq = w.Host.Construction.SubmitBuild(Side.Hand, A1, YardFamily.Shipyard, HarvesterClass, 1);
        w.Game.Step(); // day 1: queued, research order 0 (available from day one)
        Assert.Null(w.Reason(seq));
        var started = Assert.Single(w.Events<ProjectStarted>());
        Assert.Equal((A1, HarvesterClass, 1), (started.System, started.Class, started.Count));
        // the cost is 584 points of 4-day cycles; the sheet takes the normal cost-derived time, so start the project one cycle short
        var project = w.Host.Construction.ProjectAt(A1, YardFamily.Shipyard)!;
        Assert.Equal(584, w.Host.Economy.Costs.Get(HarvesterClass).Cost);
        project.Progress = 583;
        for (int i = 0; i < 12 && !w.Events<HarvesterDeployed>().Any(); i++) w.Game.Step();
        var ev = Assert.Single(w.Events<HarvesterDeployed>());
        Assert.Equal((Side.Hand, A1), (ev.Side, ev.System));
        Assert.Equal(3, w.Game.AudienceOf(ev.Index)); // both sides: fog does not hide it
        Assert.True(w.Ds.HasHarvester(Side.Hand, A1));
    }

    // m5-death-star DS2
    [Fact]
    public void DS2_OnePerSystem()
    {
        var w = new DsWorld();
        foreach (var s in new[] { A1, A2 }) { w.Hold(s, Side.Hand, 30); w.Host.Economy.AddFacility(s, FacilityKind.OrbitalShipyard); }
        w.Host.Economy.SetPools(Side.Hand, 0, 1000);
        w.Harvester(Side.Hand, A1);
        long atX = w.Host.Construction.SubmitBuild(Side.Hand, A1, YardFamily.Shipyard, HarvesterClass, 1);
        long atY = w.Host.Construction.SubmitBuild(Side.Hand, A2, YardFamily.Shipyard, HarvesterClass, 1);
        w.Game.Step();
        Assert.Equal(HarvesterReject.HarvesterLimit, w.Reason(atX));
        Assert.Null(w.Reason(atY));
        // an order for two at once also exceeds the cap of one
        Assert.Equal(HarvesterReject.HarvesterLimit, new DsWorld().TryBuild(2));
    }

    // ------------------------------------------------------------------ Destroy System (§2)

    // m5-death-star DS3
    [Fact]
    public void DS3_NoHarvesterInTheFleet()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Collective);
        int f = w.Fleet(Side.Hand, A1, Isd);
        Assert.Equal(HarvesterReject.NoHarvester, w.Destroy(Side.Hand, f));
        Assert.False(w.Game.State.Harvester.IsDestroyed(A1));
        // the other legality codes: unknown fleet, not yours, in hyperspace, already destroyed
        int ds = w.Harvester(Side.Hand, A1);
        Assert.Equal(RejectReason.UnknownFleet, w.Destroy(Side.Hand, 9999));
        Assert.Equal(RejectReason.NotYours, w.Destroy(Side.Collective, ds));
    }

    // m5-death-star DS4
    [Fact]
    public void DS4_DestroyANeutralSystemWithAnEnemyFleetAndAFacility()
    {
        var w = new DsWorld();
        w.Hold(A3, null, 50);
        int mine = w.Host.Economy.AddFacility(A3, FacilityKind.Mine);
        int reg = w.Host.Economy.AddObject(Side.Collective, "Militia", A3);
        // A fleet of both sides at one system fights at slot 5, before the strike at 5.1, so an enemy fleet that is still there at 5.1 is one that
        // the battle left alone (a stalemate). The test puts it there after the battle, at the end of slot 5, to reach the rule on its own.
        int enemy = 0, aboard = 0; var enemyShips = new List<int>();
        w.Game.RegisterHook("5", ctx =>
        {
            if (enemy != 0) return;
            enemy = w.Fleet(Side.Collective, A3, Corvette, Corvette);
            aboard = w.Host.Economy.AddObject(Side.Collective, "Militia");
            w.Host.Economy.Find(aboard)!.AboardShip = w.ShipOf(enemy);
            enemyShips.AddRange(w.Game.State.Fleets[enemy].Ships);
        });
        int attacker = w.Harvester(Side.Hand, A3, Isd);
        int dsShip = w.ShipOf(attacker);
        int hull = w.Host.Economy.Find(dsShip)!.Hull;

        Assert.Null(w.Destroy(Side.Hand, attacker)); // enemy fleets present do not block it (DEFAULT)

        Assert.True(w.Game.State.Harvester.IsDestroyed(A3));
        Assert.Null(w.Host.Fleets.OwnerOf(A3));
        Assert.False(w.FleetExists(enemy));
        foreach (var s in enemyShips) Assert.False(w.Exists(s));
        Assert.False(w.Exists(aboard)); // everything aboard goes with the fleet
        Assert.False(w.Exists(mine));   // the facility is gone, though nobody held the system
        Assert.False(w.Exists(reg));    // and so is the regiment on the planet
        Assert.Equal(DestroyReason.Harvester, Assert.Single(w.Events<FacilityDestroyed>()).Reason);
        Assert.True(w.FleetExists(attacker));
        Assert.Equal(2, w.Game.State.Fleets[attacker].Ships.Count); // the attacker's fleet is intact
        Assert.Equal(hull, w.Host.Economy.Find(dsShip)!.Hull);       // the harvester is not damaged or consumed
        var ev = Assert.Single(w.Events<SystemDestroyed>());
        Assert.Equal((A3, Side.Hand, (Side?)null), (ev.System, ev.AttackerSide, ev.PriorHolder));
        Assert.Equal(3, w.Game.AudienceOf(ev.Index));
        var lost = Assert.Single(w.Events<FleetDestroyedByHarvester>());
        Assert.Equal((enemy, Side.Collective), (lost.Fleet, lost.FleetSide));
        Assert.True(w.Host.Fog.IsExplored(Side.Collective, A3) && w.Host.Fog.IsExplored(Side.Hand, A3));
        Assert.True(w.Host.Fog.Picture(Side.Collective, A3)!.Destroyed && w.Host.Fog.Picture(Side.Hand, A3)!.Destroyed); // both views know
        // no slots, no support value, no production there
        Assert.Equal((0, 0), (w.Host.Economy.SlotsOf(A3).Energy, w.Host.Economy.SlotsOf(A3).Raw));
        Assert.Equal((0, 0), (w.Collective(A3), w.Host.Economy.SupportOf(A3, Side.Hand)));
    }

    /// <summary>The first seed whose harvester rolls on minors of these combats give exactly <paramref name="hits"/> hits (so the test sees both outcomes).</summary>
    private static ulong FindSeed(int[] combats, int hits)
    {
        for (ulong seed = 1; ; seed++)
            if (Simulate(new GameRng(seed), combats).Count(k => k) == hits) return seed;
    }

    /// <summary>
    /// The draws of the harvester's roll on free minors, in id order: rand(1..100) &lt;= 50 hits; a hit makes the Hurt roll on the minor's combat
    /// (B2e: rand(0..99) &lt; max(1, 100 - combat), then rand(0..p), rand(0..29) and the death draw rand(0..99)), then the kill. Every hit dies.
    /// </summary>
    private static List<bool> Simulate(GameRng rng, int[] combats)
    {
        var kills = new List<bool>();
        foreach (int com in combats)
        {
            bool hit = rng.NextInt(1, 100) <= 50;
            kills.Add(hit);
            if (!hit) continue;
            int p = Math.Max(1, 100 - com);
            if (rng.NextInt(0, 99) < p) { rng.NextInt(0, p); rng.NextInt(0, 29); rng.NextInt(0, 99); }
        }
        return kills;
    }

    private static int[] MinorCombats(int[] minors) =>
        minors.Select(id => GameData.Default.Characters.Get(id).SkillBase[(int)Skill.Combat]).ToArray();

    // m5-death-star DS5
    [Fact]
    public void DS5_CharactersOnTheDestroyedPlanet()
    {
        var minors = new[] { Drayson, Borsk, Tura, Derlin };
        ulong seed = FindSeed(MinorCombats(minors), hits: 2);
        var w = new DsWorld(seed: seed);
        w.Hold(A1, Side.Collective, 80); w.Hold(B3, Side.Collective, 80); w.Hold(A6, Side.Collective, 80);
        // (B5a, 2026-10-17) the strike's +10 would carry the unheld worlds A2..A5 from 50 to 60 and (derived control) make them Collective, each with
        // its capture loyalty draw; at 45 they stay neutral (55), so the test still sees only the four character rolls
        foreach (var n in new[] { A2, A3, A4, A5 }) w.Hold(n, null, 45);
        foreach (var m in minors) w.Person(m, A1);
        w.Person(Luke, A1);
        int attacker = w.Harvester(Side.Hand, A1);
        var replica = new GameRng(seed);
        var kills = Simulate(replica, MinorCombats(minors)); // id order, p = 50; B2e: a hit adds the Hurt draws

        Assert.Null(w.Destroy(Side.Hand, attacker));

        for (int i = 0; i < minors.Length; i++)
            Assert.Equal(kills[i] ? CharacterStatus.Dead : CharacterStatus.Free, w.C(minors[i]).Status);
        Assert.Equal(replica.Export(), w.Game.Rng.Export()); // four rolls plus the Hurt draws of the hits: the major cost none
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
        var minors = new[] { Drayson, Borsk, Tura, Derlin };
        ulong seed = FindSeed(MinorCombats(minors), hits: 2);
        var w = new DsWorld(seed: seed);
        w.Hold(A1, Side.Collective, 80);
        foreach (var m in minors) w.Person(m, A1);
        w.Person(Luke, A1);
        int attacker = w.Harvester(Side.Hand, A1);
        Assert.Null(w.Destroy(Side.Hand, attacker));
        var luke = w.C(Luke);
        Assert.Equal((CharacterStatus.Captured, Side.Hand, A1), (luke.Status, luke.Captor!.Value, luke.HeldAt));
        Assert.Equal(2, minors.Count(m => w.C(m).Status == CharacterStatus.Captured));
        Assert.Equal(2, minors.Count(m => w.C(m).Status == CharacterStatus.Dead));
        Assert.All(w.Events<CharacterCaptured>(), e => Assert.Equal("Harvester", e.Cause));
    }

    [Fact]
    public void DS5_NearestSystemTiesBreakBySystemIndexAndTheAttackersSideBoardsItsFleet()
    {
        // m5-death-star §2 effect 3: fewest lane steps, ties by system id; one of the attacker's side boards the attacking fleet
        var w = new DsWorld();
        w.Hold(A1, Side.Hand, 20); w.Hold(A2, Side.Collective, 80); w.Hold(B1, Side.Collective, 80);
        w.Person(Luke, A1);   // Collective major: A2 and B1 are both one step away; A2 comes first
        w.Person(Vader, A1);  // Hand major: boards the harvester's fleet
        int attacker = w.Harvester(Side.Hand, A1);
        Assert.Null(w.Destroy(Side.Hand, attacker));
        Assert.Equal(A2, w.C(Luke).Transit!.Path[^1]);
        var vader = w.C(Vader);
        Assert.Equal((attacker, CharacterPost.None, A1), (vader.AttachedFleet, vader.Post, vader.System));
    }

    // m5-death-star DS6
    [Fact]
    public void DS6_DestroyingTheHearthWinsTheGameForTheHand()
    {
        var w = new DsWorld(settings: new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true, Economy = new EconomyParams { ScrapPeriod = 1_000_000 }, Victory = OldVictory.Rules }); // HQ-only set explicitly: the test's subject is the HQ's destruction
        w.Hold(A1, Side.Collective, 80); w.Hold(B1, Side.Collective, 80); w.Hold(A2, Side.Hand, 20);
        w.Host.Fog.PlaceHq(A1);
        w.Game.State.Scenario.Active = true;
        int attacker = w.Harvester(Side.Hand, A1);
        Assert.Null(w.Destroy(Side.Hand, attacker));
        var hq = w.Game.State.Scenario.Hq;
        Assert.Equal((HqStatus.Destroyed, FogModule.CauseSystemDestroyed, A1), (hq.Status, hq.DestroyedCause, hq.System));
        var hqEvent = Assert.Single(w.Events<HqDestroyed>());
        Assert.Equal(3, w.Game.AudienceOf(hqEvent.Index));
        Assert.Equal(Side.Hand, w.Host.Victory.Winner); // the victory step of the same day (slot 6.11)
        Assert.Equal(1, w.Game.Day);
        Assert.Equal(Side.Hand, Assert.Single(w.Events<Praedra.Core.Victory.GameWon>()).Winner);
    }

    // m5-setup-and-victory SV52 (B4 §15): a destroyed Charter meets the Collective's place term; in a standard game the key captures are still needed
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SV52_DestroyedCoruscantMeetsTheCollectivePlaceTerm(bool standard, bool collectiveWins)
    {
        var rules = standard ? new VictoryRules { KeyDeathFallback = KeyDeathFallback.None } : OldVictory.Rules;
        var w = new DsWorld(settings: new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true, Economy = new EconomyParams { ScrapPeriod = 1_000_000 }, Victory = rules });
        w.Hold(A1, Side.Collective, 80); w.Hold(A2, Side.Hand, 20);
        w.Host.Fog.PlaceHq(A1);
        w.Game.State.Scenario.Active = true;
        w.Game.State.Harvester.Destroyed.Add(VictoryModule.CoruscantName);
        w.Game.Step();
        Assert.Equal(collectiveWins ? Side.Collective : null, w.Host.Victory.Winner);
        if (collectiveWins) Assert.Equal("Charter is destroyed", w.Host.Victory.Reason);
    }

    // m5-death-star DS7. (B5a, 2026-10-18) Run under both control rules: AssaultOnly (the DEFAULT until the AI copes) and Derived (Classic).
    [Theory]
    [InlineData(ControlRule.AssaultOnly)]
    [InlineData(ControlRule.Derived)]
    public void DS7_FirstStrikeInASectorOfSix(ControlRule rule)
    {
        var w = new DsWorld(rule: rule);
        w.Hold(A1, Side.Hand, 40);
        w.Hold(A2, Side.Hand, 40); w.Hold(A3, null, 50); w.Hold(A4, Side.Collective, 95); w.Hold(A5, Side.Hand, 0); w.Hold(A6, Side.Collective, 70);
        w.Hold(B1, Side.Hand, 33); w.Hold(B2, Side.Collective, 77); w.Hold(B3, null, 5);
        int attacker = w.Harvester(Side.Hand, A1);
        w.Game.Step(); // slot 5.6 raises the sector flag: its damping must not touch the strike's own change
        Assert.True(w.Host.Support.IsHarvesterSector(A2));
        Assert.Null(w.Destroy(Side.Hand, attacker));
        Assert.Equal(new[] { 50, 60, 100, 10, 80 }, new[] { A2, A3, A4, A5, A6 }.Select(w.Collective).ToArray()); // +10 each, A4 clamped
        Assert.Equal(new[] { 33, 77, 5 }, new[] { B1, B2, B3 }.Select(w.Collective).ToArray()); // nothing outside the sector changes
        var changes = w.Events<SupportChanged>().Where(e => e.Cause == SupportCause.Harvester).ToList();
        Assert.Equal(new[] { A2, A3, A4, A5, A6 }, changes.Select(e => e.System).ToArray());
        // (B5a, 2026-10-17) under derived control a world the +10 carries across a band changes holder: A2 (Hand, unguarded) 40 -> 50 goes
        // neutral, A3 (neutral) 50 -> 60 joins the Collective; their sector steps (+1) are halved to 0 in the harvester sector. AssaultOnly: none.
        if (rule == ControlRule.AssaultOnly) Assert.Empty(w.Events<SystemCaptured>());
        else
        {
            Assert.Equal(new (string, Side?, Side?)[] { (A2, Side.Hand, null), (A3, null, Side.Collective) },
                w.Events<SystemCaptured>().Select(e => (e.System, e.From, e.To)).ToArray());
            Assert.All(w.Events<SystemCaptured>(), e => Assert.Equal(CaptureCause.Support, e.Cause));
        }
        Assert.Equal(1, w.Ds.StrikesIn(Sa));
        Assert.Equal(0, w.Ds.StrikesIn(Sb));
        var sector = Assert.Single(w.Events<SectorStruck>());
        Assert.Equal((Sa, 1, 5), (sector.Sector, sector.Strike, sector.Systems.Items.Count));
    }

    // m5-death-star DS8. (B5a, 2026-10-18) Run under both control rules: AssaultOnly (the DEFAULT until the AI copes) and Derived (Classic).
    [Theory]
    [InlineData(ControlRule.AssaultOnly)]
    [InlineData(ControlRule.Derived)]
    public void DS8_SecondStrikeInTheSameSector(ControlRule rule)
    {
        var w = new DsWorld(rule: rule);
        w.Hold(A1, Side.Hand, 40); w.Hold(A2, Side.Hand, 40);
        w.Hold(A3, null, 50);                 // neutral: becomes Collective-held at 100
        w.Hold(A4, Side.Hand, 30);          // Hand with a garrison of 5 regiments (the strike-2 requirement is 6: 12 in an uprising, halved in a harvester sector): stays Hand, at 100, uprising at once
        for (int i = 0; i < 5; i++) w.Host.Economy.AddObject(Side.Hand, "Tranches", A4);
        w.Hold(A5, Side.Collective, 60);        // Collective: just goes to 100
        w.Hold(A6, null, 50);
        w.Hold(B1, Side.Collective, 12);
        int first = w.Harvester(Side.Hand, A1), second = w.Harvester(Side.Hand, A2);
        w.Game.Step();
        Assert.Null(w.Destroy(Side.Hand, first));
        Assert.Equal(1, w.Ds.StrikesIn(Sa));
        Assert.False(w.Host.Support.IsUprising(A4)); // the garrison covers the first strike's change (Hand support 60: no requirement)
        Assert.Equal(40, w.Collective(A4)); // 30 + 10
        Assert.Null(w.Destroy(Side.Hand, second));
        Assert.Equal(2, w.Ds.StrikesIn(Sa));

        Assert.Equal(Side.Collective, w.Host.Fleets.OwnerOf(A3));
        Assert.Equal(Side.Collective, w.Host.Fleets.OwnerOf(A6));
        // (B5a, 2026-10-17) under derived control the support change to 100 makes the neutral worlds Collective (cause Support); HarvesterRevolt is the
        // AssaultOnly cause (m5-death-star §2, the B5a note)
        Assert.Equal(rule == ControlRule.Derived ? CaptureCause.Support : CaptureCause.HarvesterRevolt, w.Events<SystemCaptured>().First(e => e.System == A3).Cause);
        Assert.Equal(Side.Hand, w.Host.Fleets.OwnerOf(A4));
        Assert.True(w.Host.Support.IsUprising(A4));
        Assert.Equal(A4, Assert.Single(w.Events<UprisingStarted>().Where(e => e.System != B1)).System); // B1 (Collective, support 12, no garrison) rises on day 1 by itself
        Assert.Equal(Side.Collective, w.Host.Fleets.OwnerOf(A5));
        foreach (var s in new[] { A3, A4, A5, A6 }) Assert.Equal(100, w.Collective(s));
        Assert.Equal(12, w.Collective(B1)); // nothing outside the sector
    }

    // m5-death-star DS9
    [Fact]
    public void DS9_ADestroyedSystemIsPassableDebris()
    {
        var w = new DsWorld();
        w.Hold(A2, Side.Hand, 20); w.Hold(A4, Side.Hand, 20);
        w.Hold(A3, null, 50);
        int attacker = w.Harvester(Side.Hand, A3);
        Assert.Null(w.Destroy(Side.Hand, attacker));
        Assert.True(w.Game.State.Harvester.IsDestroyed(A3));

        // a fleet flies through it to A4, another stops there
        int through = w.Fleet(Side.Hand, A2, Corvette), stops = w.Fleet(Side.Hand, A2, Corvette);
        w.Host.Fleets.SubmitMove(Side.Hand, through, A4);
        w.Host.Fleets.SubmitMove(Side.Hand, stops, A3);
        for (int i = 0; i < 400 && (w.Game.State.Fleets[through].At != A4 || w.Game.State.Fleets[stops].At != A3); i++) w.Game.Step();
        Assert.Equal(A4, w.Game.State.Fleets[through].At);
        Assert.Equal(A3, w.Game.State.Fleets[stops].At);
        Assert.Contains(w.Events<FleetArrived>(), e => e.Fleet == stops && e.System == A3);

        // no holder, no support, no build, no assault, no bombardment, no capture, no mission
        Assert.Null(w.Host.Fleets.OwnerOf(A3));
        Assert.Equal((0, 0), (w.Collective(A3), w.Host.Economy.SupportOf(A3, Side.Hand)));
        w.Host.Economy.AddFacility(A3, FacilityKind.OrbitalShipyard); // even a yard put there by hand cannot build: the order is refused first
        long build = w.Host.Construction.SubmitBuild(Side.Hand, A3, YardFamily.Shipyard, Corvette, 1);
        long assault = w.Host.Assault.SubmitAssault(Side.Hand, stops, A3);
        long bombard = w.Host.Bombardment.SubmitBombard(Side.Hand, stops, A3, Praedra.Core.Defenses.BombardCategory.General);
        long capture = w.Host.Control.SubmitCapture(Side.Hand, A3); // (B5a, 2026-10-17) under derived control a capture is troops landing
        var agent = w.Person(Luke, A2);
        agent.Skills[(int)Skill.Espionage] = 60;
        long mission = w.Game.Submit(s => new StartMissionOrder(s, Side.Collective, MissionKind.Espionage, new() { Luke }, new(), new(), A3));
        w.Game.Step();
        foreach (var seq in new[] { build, assault, bombard, capture, mission })
            Assert.Equal(RejectReason.SystemDestroyed, w.Reason(seq));
    }

    // m5-death-star DS10
    [Fact]
    public void DS10_TheSectorFlagFollowsTheHarvester()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Hand, 20);
        int ds = w.Harvester(Side.Hand, A1);
        bool Flag(string s) => w.Host.Support.IsHarvesterSector(s);
        w.Game.Step();
        Assert.All(new[] { A1, A2, A3, A4, A5, A6 }, s => Assert.True(Flag(s)));
        Assert.All(new[] { B1, B2, B3 }, s => Assert.False(Flag(s)));
        w.Host.Fleets.SubmitMove(Side.Hand, ds, B1);
        bool arrivedStep = false;
        for (int i = 0; i < 2000 && !arrivedStep; i++)
        {
            w.Game.Step();
            arrivedStep = w.Game.State.Fleets[ds].At == B1;
            if (!arrivedStep) Assert.All(new[] { A1, B1 }, s => Assert.False(Flag(s))); // in hyperspace the harvester is in no sector
        }
        Assert.True(arrivedStep);
        Assert.All(new[] { B1, B2, B3 }, s => Assert.True(Flag(s)));   // set at slot 5.6 of the arrival day
        Assert.All(new[] { A1, A2, A3, A4, A5, A6 }, s => Assert.False(Flag(s)));
    }

    // m5-death-star DS11
    [Fact]
    public void DS11_SabotageRemovesTheHarvesterAndTheFleetKeepsTheRest()
    {
        var w = new MissionWorld();
        w.Hold(MissionWorld.T, Side.Hand, 0);
        int fleet = w.Host.Fleets.AddFleet(Side.Hand, "F", new[] { HarvesterClass, Isd }, MissionWorld.T);
        int dsShip = w.Game.State.Fleets[fleet].Ships.First(id => w.Host.Economy.Find(id)!.Class == HarvesterClass);
        int isdShip = w.Game.State.Fleets[fleet].Ships.First(id => id != dsShip);
        w.Agent(MissionWorld.Luke, MissionWorld.T, esp: 100, com: 100);
        // B2d: the arriving check (orbit) walks the harvester and the destroyer (99 is not detected); the job check (target in orbit) opens with
        // Luke's betrayal draw (B2b), then walks the same two guards
        w.Stub!.Expect(0, 99, 99, 99).Expect(0, 99, MissionWorld.Bt(1, 99, 99));
        w.Start(Side.Collective, MissionKind.SpikeTheHarvester, new[] { MissionWorld.Luke }, MissionWorld.T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        Assert.Null(w.Host.Economy.Find(dsShip));
        Assert.NotNull(w.Host.Economy.Find(isdShip));
        Assert.Equal(new[] { isdShip }, w.Game.State.Fleets[fleet].Ships);
        var ev = Assert.Single(w.Events<HarvesterDestroyed>());
        Assert.Equal(("Sabotage", Side.Hand, dsShip), (ev.Cause, ev.Side, ev.Ship));
        Assert.Equal(3, w.Game.AudienceOf(ev.Index));
        Assert.False(w.Host.Harvester.HasHarvester(Side.Hand, MissionWorld.T));
    }

    // m5-death-star DS12 (legality 6, the planet-killer shield blocking Destroy System) has nothing to test: the shield is cut
    // (data/design/cut-classes.csv; Decided Paul 2026-10-25).

    // m5-death-star DS13
    [Fact]
    public void DS13_SaveAfterAStrikeAndLoad()
    {
        var w = new DsWorld();
        w.Hold(A3, null, 50); w.Hold(A2, Side.Hand, 20);
        int attacker = w.Harvester(Side.Hand, A3);
        Assert.Null(w.Destroy(Side.Hand, attacker));
        string hash = w.Host.StateHash();
        string save = w.Host.Save();
        Assert.Contains("\"Harvester\":{\"Destroyed\":[\"A3\"],\"StrikesInSector\":{\"SA\":1}}", save);
        w.Reload();
        Assert.Equal(new[] { A3 }, w.Game.State.Harvester.Destroyed.ToArray());
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
        w.Hold(A3, null, 50); w.Hold(A4, Side.Hand, 60); w.Hold(A5, Side.Collective, 60); w.Hold(A6, null, 50);
        w.Hold(A1, Side.Collective, 80); w.Hold(A2, Side.Hand, 30); w.Hold(B1, Side.Collective, 80);
        for (int i = 0; i < 5; i++) w.Host.Economy.AddObject(Side.Hand, "Tranches", A4);
        w.Host.Economy.AddFacility(A3, FacilityKind.Mine);
        w.Fleet(Side.Collective, A3, Corvette, Corvette);
        foreach (var m in new[] { Drayson, Borsk, Tura, Derlin }) w.Person(m, A3);
        w.Person(Luke, A3);
        int first = w.Harvester(Side.Hand, A3, Isd), second = w.Harvester(Side.Hand, A5);
        w.Game.Step();
        w.Destroy(Side.Hand, first);
        w.Run(3);
        w.Destroy(Side.Hand, second);
        w.Run(5);
        return w.Host.StateHash();
    }

    // ------------------------------------------------------------------ the rest of the sheet

    [Fact]
    public void Switches_DefaultsAndRefusedValues()
    {
        var p = new HarvesterParams();
        Assert.Equal((1, false, 10, 2, 100), (p.CapPerSystem, p.TrenchRun, p.SectorPenalty, p.RevoltFromStrike, p.RevoltCollectiveSupport));
        Assert.Equal(50, new CharacterParams().HarvesterHitPercent);
        Assert.Throws<RuleMissingException>(() => new DsWorld(new HarvesterParams { TrenchRun = true })); // no code until a rule exists
        Assert.Contains("5.6", Game.WorkList);
        Assert.True(Game.WorkList.ToList().IndexOf("5.6") > Game.WorkList.ToList().IndexOf("5.4") && Game.WorkList.ToList().IndexOf("5.6") < Game.WorkList.ToList().IndexOf("6.1"));
    }

    [Fact]
    public void TheOrderRunsAfterTheBombardmentAndIsCancelledWhenTheFleetLeft()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Collective, 80);
        int attacker = w.Harvester(Side.Hand, A1);
        long order = w.Ds.SubmitDestroySystem(Side.Hand, attacker);
        long dup = w.Ds.SubmitDestroySystem(Side.Hand, attacker);
        long move = w.Host.Fleets.SubmitMove(Side.Hand, attacker, A2); // accepted after the destroy order: the fleet departs in step 2
        w.Game.Step();
        Assert.Null(w.Reason(order));
        Assert.Equal(HarvesterReject.OrderPending, w.Reason(dup));
        Assert.Null(w.Reason(move));
        var cancelled = Assert.Single(w.Events<DestroySystemCancelled>());
        Assert.Equal((attacker, HarvesterReject.NotAtSystem), (cancelled.Fleet, cancelled.Reason));
        Assert.False(w.Ds.IsDestroyed(A1));
    }

    [Fact]
    public void AnAcceptedOrderIsSavedInTheQueue()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Collective, 80);
        int attacker = w.Harvester(Side.Hand, A1);
        w.Ds.SubmitDestroySystem(Side.Hand, attacker); // not yet validated: it waits in the queue, and the queue is saved
        w.Reload();
        Assert.IsType<DestroySystemOrder>(Assert.Single(w.Game.Queue));
        w.Game.Step();
        Assert.True(w.Ds.IsDestroyed(A1));
    }

    [Fact]
    public void AHarvesterLostInBattleIsReported_AndOneDestroyedWithTheSystemToo()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Collective, 80);
        int mine = w.Harvester(Side.Hand, A1);
        w.Game.RegisterHook("5", ctx => w.Host.Economy.DestroyUnit(ctx, ctx.Day + 1, w.Host.Economy.Find(w.ShipOf(mine))!)); // the loss path of a battle
        w.Game.Step();
        Assert.Equal("Battle", Assert.Single(w.Events<HarvesterDestroyed>()).Cause);

        // an enemy harvester standing at a destroyed system dies with it
        var v = new DsWorld();
        v.Hold(A1, Side.Collective, 80);
        int attacker = v.Harvester(Side.Hand, A1), victim = 0;
        v.Game.RegisterHook("5", ctx => { if (victim == 0) victim = v.Harvester(Side.Collective, A1); }); // after the battle of slot 5 (see DS4)
        Assert.Null(v.Destroy(Side.Hand, attacker));
        Assert.False(v.FleetExists(victim));
        var lost = Assert.Single(v.Events<HarvesterDestroyed>());
        Assert.Equal(("Harvester", Side.Collective), (lost.Cause, lost.Side));
    }

    [Fact]
    public void QueuedWorkAtTheSystemIsCancelledAndNothingRemainsOnIt()
    {
        var w = new DsWorld();
        w.Hold(A1, Side.Hand, 20);
        w.Host.Economy.AddFacility(A1, FacilityKind.OrbitalShipyard);
        w.Host.Economy.SetPools(Side.Hand, 0, 1000);
        w.Host.Construction.SubmitBuild(Side.Hand, A1, YardFamily.Shipyard, Isd, 2);
        w.Game.Step();
        Assert.NotNull(w.Host.Construction.ProjectAt(A1, YardFamily.Shipyard));
        int attacker = w.Harvester(Side.Hand, A1);
        Assert.Null(w.Destroy(Side.Hand, attacker));
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
        w.Hold(MissionWorld.T, Side.Hand, 20);
        w.Agent(MissionWorld.Leia, MissionWorld.T, esp: 50);
        w.Stub!.Expect(0, 20, 20); // a long timer: the mission stays working
        w.Start(Side.Collective, MissionKind.Espionage, new[] { MissionWorld.Leia }, MissionWorld.T);
        w.Game.Step();
        Assert.Equal(MissionPhase.Working, w.M.Missions.Single().Phase);
        int attacker = w.Host.Fleets.AddFleet(Side.Hand, "DS", new[] { HarvesterClass }, MissionWorld.T);
        long seq = w.Host.Harvester.SubmitDestroySystem(Side.Hand, attacker);
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        Assert.Empty(w.M.Missions);
        Assert.Equal(MissionOutcome.TargetDestroyed, Assert.Single(w.Events<MissionEnded>()).Outcome);
    }
}

internal static class DsWorldExtensions
{
    /// <summary>Order <paramref name="count"/> harvesters at a fresh Hand yard with no harvester in sight, and return the rejection reason.</summary>
    public static string? TryBuild(this DsWorld w, int count)
    {
        w.Hold(A1, Side.Hand, 30);
        w.Host.Economy.AddFacility(A1, FacilityKind.OrbitalShipyard);
        w.Host.Economy.SetPools(Side.Hand, 0, 1000);
        long seq = w.Host.Construction.SubmitBuild(Side.Hand, A1, YardFamily.Shipyard, DsWorld.HarvesterClass, count);
        w.Game.Step();
        return w.Reason(seq);
    }
}
