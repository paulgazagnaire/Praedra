using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Missions;

// m5-missions §Tests MI44 to MI55, MI58, MI59: orders, travel, timers, legality, cancel, return; plus the switches, the RuleMissing refusals and
// the hooks into the character, fog and support modules.
public class MissionOrderTests
{
    // m5-missions MI44: Sabotage ordered in Step 0, 3 ticks, timer draw rand(0..2) = 2: lands in Step 2 step 3 (A = 3), T = 3, due 6, iteration in Step 5 slot 5.3
    [Fact]
    public void MI44_TravelAndTimer()
    {
        var w = new MissionWorld();
        w.Agent(Leia, H, esp: 50, com: 50);
        w.Stub!.Expect(0, 2, 2);
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Leia }, T);
        w.Game.Step();
        var started = w.Events<MissionStarted>().Single();
        Assert.Equal((3, 3), (started.TotalTicks, started.ArrivalDay));
        Assert.Null(w.Ch.Get(Leia)!.System); // in transit with the group
        Assert.Equal(0, w.Events<MissionDeparted>().Single().Day);
        w.Run(1); // Step 1
        Assert.Empty(w.Events<MissionLanded>());
        w.Game.Step(); // Step 2
        var landed = w.Events<MissionLanded>().Single();
        Assert.Equal((3, 6), (landed.Day, landed.Due));
        Assert.Equal(T, w.Ch.Get(Leia)!.System);
        w.Run(2); // Steps 3, 4
        Assert.Empty(w.Events<MissionMemberRoll>());
        w.Game.Step(); // Step 5
        Assert.Equal(6, w.Events<MissionMemberRoll>().Single().Day);
        Assert.Equal(6, w.Game.Day);
    }

    // m5-missions MI45: Diplomacy lands A = 10, timer draw 4 (T = 9): due 19, resolves in Step 18; on success next draw 0 (T' = 5): due 24
    [Fact]
    public void MI45_RepeatingTimer()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Leia, T, dip: 120);
        w.Run(9);
        w.Stub!.Expect(0, 10, 4, 0);
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step(); // Step 9
        Assert.Equal((10, 19), (w.Events<MissionLanded>().Single().Day, w.Events<MissionLanded>().Single().Due));
        w.Run(8); // Steps 10 to 17
        Assert.Empty(w.Events<MissionMemberRoll>());
        w.Game.Step(); // Step 18
        Assert.Equal(19, w.Events<MissionMemberRoll>().Single().Day);
        var done = w.Events<MissionIterationDone>().Single();
        Assert.Equal((19, true, 24), (done.Day, done.Success, done.Due));
    }

    // m5-missions MI46: a team already at the target, ordered in Step 7, lands in Step 7 step 3 (A = 8)
    [Fact]
    public void MI46_AlreadyThere()
    {
        var w = new MissionWorld();
        w.Agent(Leia, T, esp: 10);
        w.Run(7);
        w.Start(Side.Alliance, MissionKind.Reconnaissance, new[] { Leia }, T);
        w.Game.Step();
        Assert.Equal(8, w.Events<MissionLanded>().Single().Day);
        Assert.Equal(0, w.Events<MissionStarted>().Single().TotalTicks);
    }

    // m5-missions MI47: Han alone over a 7-tick hop takes 7 * 50 / 100 = 3 ticks; with a decoy (a special force) the normal 7
    [Fact]
    public void MI47_HanSpeed()
    {
        var w = new MissionWorld(decoys: true);
        w.Agent(Han, T, esp: 100);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Han }, X);
        w.Game.Step();
        Assert.Equal(3, w.Events<MissionStarted>().Single().TotalTicks);

        var d = new MissionWorld(decoys: true);
        d.Agent(Han, T, esp: 100);
        int bothan = d.Unit(Side.Alliance, Bothan, T);
        d.Start(Side.Alliance, MissionKind.Espionage, new[] { Han }, X, decoys: new[] { bothan });
        d.Game.Step();
        Assert.Equal(7, d.Events<MissionStarted>().Single().TotalTicks);
    }

    // m5-missions MI48: Assassination as the Alliance; Death Star Sabotage as the Empire: WrongSide
    [Fact]
    public void MI48_WrongSide()
    {
        var w = new MissionWorld();
        w.Agent(Han, T, com: 100);
        w.Agent(Vader, T, com: 100, esp: 50);
        Assert.Equal(MissionReject.WrongSide, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Assassination, new() { Han }, new(), new(), null, Vader)));
        Assert.Equal(MissionReject.WrongSide, w.Try(s => new StartMissionOrder(s, Side.Empire, MissionKind.DeathStarSabotage, new() { Vader }, new(), new(), T)));
    }

    // m5-missions MI49: special forces as team members, checked against the record's mask
    [Theory]
    [InlineData(Guerrillas, MissionKind.Sabotage, Side.Alliance, false)]
    [InlineData(Infiltrators, MissionKind.Sabotage, Side.Alliance, true)]
    [InlineData(Bothan, MissionKind.Espionage, Side.Alliance, true)]
    [InlineData(Infiltrators, MissionKind.Rescue, Side.Alliance, true)]
    [InlineData(Probe, MissionKind.Reconnaissance, Side.Empire, true)]
    [InlineData(Commandos, MissionKind.SubdueUprising, Side.Empire, true)]
    [InlineData(Bothan, MissionKind.Diplomacy, Side.Alliance, false)]
    public void MI49_SpecialForceEligibility(string cls, MissionKind kind, Side side, bool ok)
    {
        var w = new MissionWorld();
        w.Hold(T, null, 50);
        int sf = w.Unit(side, cls, T);
        var reason = w.Try(s => new StartMissionOrder(s, side, kind, new(), new() { sf }, new(), T));
        if (ok) Assert.NotEqual(MissionReject.NotEligibleSpecialForce, reason);
        else Assert.Equal(MissionReject.NotEligibleSpecialForce, reason);
    }

    // m5-missions MI49 accepted cases where the target is legal
    [Fact]
    public void MI49_EligibleTeamsAreAccepted()
    {
        var w = new MissionWorld();
        w.Hold(T, null, 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, T), bothan = w.Unit(Side.Alliance, Bothan, T), probe = w.Unit(Side.Empire, Probe, T);
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Sabotage, new(), new() { inf }, new(), T)));
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new(), new() { bothan }, new(), T)));
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Empire, MissionKind.Reconnaissance, new(), new() { probe }, new(), T)));
    }

    // m5-missions MI50: members on two systems; a decoy-only team; a character already on a mission; a special force not completed
    [Fact]
    public void MI50_TeamChecks()
    {
        var w = new MissionWorld(decoys: true);
        w.Agent(Leia, T, esp: 50);
        w.Agent(Han, H, esp: 50);
        int bothan = w.Unit(Side.Alliance, Bothan, T);
        int unfinished = w.Unit(Side.Alliance, Bothan, T, completed: false);
        Assert.Equal(MissionReject.NotTogether, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Leia, Han }, new(), new(), X)));
        Assert.Equal(MissionReject.NoTeam, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new(), new(), new() { bothan }, X)));
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Leia }, new(), new(), X)));
        Assert.Equal(MissionReject.NotFree, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Reconnaissance, new() { Leia }, new(), new(), X)));
        Assert.Equal(RejectReason.NotCompleted, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new(), new() { unfinished }, new(), X)));
        // the other checks of the list, in order
        Assert.Equal(MissionReject.UnknownKind, w.Try(s => new StartMissionOrder(s, Side.Alliance, (MissionKind)0x99, new() { Han }, new(), new(), X)));
        Assert.Equal(MissionReject.UnknownMember, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { 12345 }, new(), new(), X)));
        w.Agent(Vader, H, esp: 50);
        Assert.Equal(RejectReason.NotYours, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Vader }, new(), new(), X)));
        Assert.Equal(MissionReject.DuplicateMember, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Han, Han }, new(), new(), X)));
        w.Agent(Lando, H); // espionage 0
        Assert.Equal(MissionReject.NotAbleToDo, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Lando }, new(), new(), X)));
        int bothanH = w.Unit(Side.Alliance, Bothan, H);
        Assert.Equal(MissionReject.BadDecoy, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Han }, new(), new(), X, null, null, new() { Lando })));
        Assert.Equal(MissionReject.DuplicateMember, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Han }, new() { bothanH }, new() { bothanH }, X)));
    }

    // m5-missions MI51: Diplomacy on an enemy system, a neutral one, an own one in uprising; Incite on an own system; Subdue with no uprising
    [Fact]
    public void MI51_Targets()
    {
        var w = new MissionWorld();
        w.Agent(Leia, T, dip: 100, lead: 100);
        w.Hold(X, Side.Empire, 0); w.Hold(Alderaan, null, 50); w.Hold(Bespin, Side.Alliance, 0); w.Hold(H, Side.Alliance, 100);
        w.Game.Step(); // 6.1: Bespin (support 0, no garrison) rises
        Assert.True(w.Host.Support.IsUprising(Bespin));
        Assert.Equal(MissionReject.BadTarget, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Diplomacy, new() { Leia }, new(), new(), X)));
        Assert.Equal(MissionReject.BadTarget, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Diplomacy, new() { Leia }, new(), new(), Bespin)));
        Assert.Equal(MissionReject.BadTarget, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.InciteUprising, new() { Leia }, new(), new(), H)));
        Assert.Equal(MissionReject.BadTarget, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.SubdueUprising, new() { Leia }, new(), new(), H)));
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Diplomacy, new() { Leia }, new(), new(), Alderaan)));
    }

    // m5-missions MI52: a Diplomacy mission whose target falls into uprising before its next iteration ends TargetInvalid with no draw and goes home
    [Fact]
    public void MI52_TargetInvalidAtIteration()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 50);
        int reg = w.Unit(Side.Alliance, RebArmy, T); // N = 1: covered
        w.Agent(Leia, T, dip: 100);
        w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, T);
        w.Game.Step();
        Assert.False(w.Host.Support.IsUprising(T));
        w.Host.Economy.RemoveObject(reg);
        w.Game.Step(); // 6.1: the uprising starts
        Assert.True(w.Host.Support.IsUprising(T));
        int rolls = w.Stub!.Count(0, 99);
        w.RunIteration(w.LastMission);
        Assert.Equal(MissionOutcome.TargetInvalid, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(rolls, w.Stub.Count(0, 99));
        w.Run(2);
        Assert.Equal(T, w.Events<MissionReturned>().Single().At);
        Assert.False(w.M.IsOnMission(Leia));
        Assert.Empty(w.M.Missions);
    }

    // m5-missions MI53: Death Star Sabotage with no Death Star; a story kind (Dagobah)
    [Fact]
    public void MI53_NoDeathStarAndHiddenKind()
    {
        var w = new MissionWorld();
        w.Agent(Luke, T, esp: 50, com: 50);
        Assert.Equal(MissionReject.NoDeathStar, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.DeathStarSabotage, new() { Luke }, new(), new(), T)));
        Assert.Equal(MissionReject.HiddenKind, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Dagobah, new() { Luke }, new(), new(), T)));
        Assert.Equal(MissionReject.HiddenKind, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Move, new() { Luke }, new(), new(), T)));
    }

    // m5-missions MI54: CancelMission on a working mission: accepted in step 1, no iteration, the group departs home in step 2; while outbound: InTransit
    [Fact]
    public void MI54_Cancel()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Alliance, 100);
        w.Agent(Leia, H, esp: 50, com: 50);
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Leia }, T);
        w.Game.Step();
        int id = w.LastMission;
        Assert.Equal(RejectReason.InTransit, w.Try(s => new CancelMissionOrder(s, Side.Alliance, id))); // Step 1
        long unknown = w.Submit(s => new CancelMissionOrder(s, Side.Alliance, 9999));
        long notYours = w.Submit(s => new CancelMissionOrder(s, Side.Empire, id));
        w.Game.Step(); // Step 2
        Assert.Equal(MissionReject.UnknownMission, w.Reason(unknown));
        Assert.Equal(RejectReason.NotYours, w.Reason(notYours));
        Assert.Equal(MissionPhase.Working, w.M.Get(id)!.Phase); // landed in Step 2 (A = 3); due 3 + 1 + rand = 4: iteration in Step 3, after the orders
        int day = w.Game.Day;
        Assert.Null(w.Try(s => new CancelMissionOrder(s, Side.Alliance, id)));
        Assert.Empty(w.Events<MissionMemberRoll>());
        Assert.Equal(MissionOutcome.Cancelled, w.Events<MissionEnded>().Single().Outcome);
        var dep = w.Events<MissionDeparted>().Last();
        Assert.Equal((true, day, H), (dep.Returning, dep.Day, dep.Destination));
        w.Run(3);
        Assert.Equal(H, w.Events<MissionReturned>().Single().At);
        Assert.Equal(H, w.Ch.Get(Leia)!.System);
    }

    // m5-missions MI55: the mission ends after HomeSystem was captured; Alderaan and Bespin are both own systems 4 ticks away: the tie goes to Alderaan
    [Fact]
    public void MI55_ReturnToNearestOwnSystem()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Alliance, 100); w.Hold(Alderaan, Side.Alliance, 100); w.Hold(Bespin, Side.Alliance, 100);
        w.Agent(Leia, H, esp: 50, com: 50);
        w.Stub!.Expect(0, 99, Bt(1, 99)); // B2b: Leia's betrayal draw first
        w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Leia }, T);
        w.Game.Step();
        w.Host.Fleets.SetOwner(H, Side.Empire);
        w.RunIteration(w.LastMission);
        Assert.Equal(MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome);
        w.Game.Step();
        var dep = w.Events<MissionDeparted>().Last();
        Assert.Equal((true, Alderaan, 4), (dep.Returning, dep.Destination, dep.TotalTicks));
        w.Run(4);
        Assert.Equal(Alderaan, w.Events<MissionReturned>().Single().At);
    }

    // m5-missions MI58: same seed and orders twice (a mixed game of five missions): identical events, draws and state hash; a save mid-game plays on identically
    [Fact]
    public void MI58_Determinism()
    {
        static (MissionWorld W, string Hash, List<string> Events) Play(ulong seed, int saveAt = -1)
        {
            var w = new MissionWorld(seed: seed);
            w.Hold(H, Side.Alliance, 100); w.Hold(T, Side.Empire, 20); w.Hold(X, null, 50); w.Hold(Alderaan, Side.Alliance, 30);
            w.Host.Economy.AddFacility(T, FacilityKind.Mine);
            w.Host.Economy.AddFacility(H, FacilityKind.OrbitalShipyard);
            w.Unit(Side.Empire, ImpArmy, T);
            w.Agent(Leia, H, esp: 60, com: 50, dip: 110);
            w.Agent(Han, H, esp: 90, com: 90);
            w.Agent(Luke, H, ship: 50);
            w.Agent(MonMothma, Alderaan, dip: 100);
            w.Agent(Vader, T, esp: 40, com: 100);
            int bothan = w.Unit(Side.Alliance, Bothan, H);
            w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { Leia }, X);
            w.Start(Side.Alliance, MissionKind.Sabotage, new[] { Han }, T);
            w.Start(Side.Alliance, MissionKind.ShipResearch, new[] { Luke }, H);
            w.Start(Side.Alliance, MissionKind.Diplomacy, new[] { MonMothma }, Alderaan);
            w.Start(Side.Empire, MissionKind.Espionage, new[] { Vader }, H);
            w.Start(Side.Alliance, MissionKind.Espionage, Array.Empty<int>(), T, new[] { bothan });
            for (int d = 0; d < 120; d++)
            {
                if (d == saveAt) w.Reload();
                w.Game.Step();
            }
            return (w, w.Host.StateHash(), w.Game.Events.Select(e => e.ToString()).ToList());
        }
        var a = Play(7); var b = Play(7);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Events, b.Events);
        Assert.True(a.W.Events<MissionMemberRoll>().Count >= 5);
        Assert.Equal(6, a.W.Events<MissionStarted>().Count);
        var c = Play(7, saveAt: 30); // save and load mid-mission: the rest of the game is the same
        Assert.Equal(a.Hash, c.Hash);
        Assert.NotEqual(a.Hash, Play(8).Hash);
    }

    // Save and load in the middle of a mission keeps every mission field (the state hash is equal after the round trip).
    [Fact]
    public void SaveLoadRoundTrip()
    {
        var w = new MissionWorld(seed: 3);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 50, com: 50);
        int bothan = w.Unit(Side.Alliance, Bothan, H);
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, new[] { bothan });
        w.Run(2);
        string hash = w.Host.StateHash();
        var m = w.M.Missions.Single();
        w.Reload();
        Assert.Equal(hash, w.Host.StateHash());
        var n = w.M.Missions.Single();
        Assert.Equal((m.Id, m.Kind, m.Phase, m.ArrivalDay, m.Target, m.Home), (n.Id, n.Kind, n.Phase, n.ArrivalDay, n.Target, n.Home));
        Assert.Equal(m.SpecialForces, n.SpecialForces);
        Assert.True(w.M.IsOnMission(Leia));
        // a queued mission order survives a save too
        w.Submit(s => new CancelMissionOrder(s, Side.Alliance, n.Id));
        w.Reload();
        Assert.IsType<CancelMissionOrder>(w.Game.Queue.Single());
    }

    // m5-missions MI59: mission groups pass an armed enemy fleet without a stop and without a draw (MissionsIgnoreForcedStop)
    [Fact]
    public void MI59_NoForcedStop()
    {
        var w = new MissionWorld();
        w.Host.Fleets.AddFleet(Side.Empire, "Picket", new[] { "Imperial Star Destroyer" }, T);
        w.Agent(Leia, H);
        int draws = w.Stub!.Requests.Count;
        w.Submit(s => new MoveGroupOrder(s, Side.Alliance, new() { Leia }, new(), X));
        w.Run(10);
        var arrived = w.Events<MissionGroupArrived>().Single();
        Assert.Equal((10, X), (arrived.Day, arrived.At)); // 3 + 7 ticks
        Assert.Equal(X, w.Ch.Get(Leia)!.System);
        Assert.False(w.M.IsOnMission(Leia));
        Assert.Equal(draws, w.Stub.Requests.Count);
    }

    // MoveGroup checks: Bounty Hunters cannot Move (mask all but 0x1000); AlreadyThere; UnknownSystem; members become free on arrival.
    [Fact]
    public void MoveGroupChecks()
    {
        var w = new MissionWorld();
        w.Agent(Vader, T);
        int hunters = w.Unit(Side.Empire, Hunters, T), probe = w.Unit(Side.Empire, Probe, T);
        Assert.Equal(MissionReject.NotEligibleSpecialForce, w.Try(s => new MoveGroupOrder(s, Side.Empire, new() { Vader }, new() { hunters }, H)));
        Assert.Equal(RejectReason.UnknownSystem, w.Try(s => new MoveGroupOrder(s, Side.Empire, new() { Vader }, new(), "Nowhere")));
        Assert.Equal(RejectReason.AlreadyThere, w.Try(s => new MoveGroupOrder(s, Side.Empire, new() { Vader }, new(), T)));
        Assert.Null(w.Try(s => new MoveGroupOrder(s, Side.Empire, new() { Vader }, new() { probe }, H)));
        Assert.Null(w.Host.Economy.Find(probe)!.System);
        w.Run(3);
        Assert.Equal(H, w.Host.Economy.Find(probe)!.System);
        Assert.Equal(H, w.Ch.Get(Vader)!.System);
    }

    // Rules with no data or no owner are refused, never invented: decoys while the decoy table is Missing, Recruitment while the recruit pick is
    // Missing, Jedi Training (each sheet names the other as owner).
    [Fact]
    public void RuleMissingRefusals()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Alliance, 100);
        w.Agent(Leia, T, esp: 50, lead: 80);
        w.Agent(Luke, T, esp: 50);
        int bothan = w.Unit(Side.Alliance, Bothan, T);
        // B1: the shipped data has the decoy table (FDECOYTB), so a decoy is accepted; RuleMissing only for data without one
        var w0 = new MissionWorld();
        w0.Hold(T, Side.Alliance, 100);
        w0.Agent(Leia, T, esp: 50);
        int bothan0 = w0.Unit(Side.Alliance, Bothan, T);
        Assert.NotNull(w0.M.Data.DecoyTable);
        Assert.Null(w0.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Leia }, new(), new() { bothan0 }, X)));
        _ = bothan;
        Assert.Equal(MissionReject.RuleMissing, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Recruitment, new() { Leia }, new(), new(), T)));
        Assert.Equal(MissionReject.RuleMissing, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.JediTraining, new() { Luke }, new(), new(), T)));
        // with a recruit hook the mission runs (MI21: leadership 80 gives RCRTMSTB(80) = 60 on the full table, step lookup)
        CharacterRecord? recruited = null;
        w.M.Recruit = (ctx, side, sys) => { var c = w.Ch.Get(Wedge) ?? w.Ch.Create(w.Ch.Data.Get(Wedge), new Rebellion.Core.Tests.Economy.StubRng()); w.Ch.Place(c.Id, sys, ctx); recruited = c; return c; };
        w.Stub!.Expect(0, 99, 0);
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Recruitment, new() { Leia }, new(), new(), T)));
        w.RunIteration(w.LastMission);
        Assert.NotNull(recruited);
        Assert.Equal(recruited!.Id, w.Events<CharacterRecruited>().Single().Character);
    }

    [Theory]
    [InlineData(nameof(MissionParams.MissionsIgnoreForcedStop))]
    [InlineData(nameof(MissionParams.UprisingFailsMissions))]
    [InlineData(nameof(MissionParams.InformantIncident))]
    [InlineData(nameof(MissionParams.StoryMissions))]
    [InlineData(nameof(MissionParams.NewMissions))]
    public void SwitchesWithNoRuleAreRefused(string name)
    {
        var p = name switch
        {
            nameof(MissionParams.MissionsIgnoreForcedStop) => new MissionParams { MissionsIgnoreForcedStop = false },
            nameof(MissionParams.UprisingFailsMissions) => new MissionParams { UprisingFailsMissions = true },
            nameof(MissionParams.InformantIncident) => new MissionParams { InformantIncident = true },
            nameof(MissionParams.StoryMissions) => new MissionParams { StoryMissions = true },
            _ => new MissionParams { NewMissions = true },
        };
        Assert.Throws<RuleMissingException>(() => GameHost.New(1, new GameSettings { Missions = p }, MissionWorld.Data));
    }

    // GameSettings.Missions null = the sheet's DEFAULTs; the data loads from the repository.
    [Fact]
    public void DefaultsAndData()
    {
        var h = GameHost.New(1);
        Assert.Null(new GameSettings().Missions);
        Assert.Equal(new MissionParams(), h.Missions.Params);
        var p = new MissionParams();
        Assert.Equal((true, true), (p.MissionGuardChecks, p.GuardsAnySide)); // B2d
        Assert.Equal((MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue, SuccessRule.AnyMember, true, true, 0, 0, true, true, 0),
            (p.MstbLookup, p.MstbBelowFirstRow, p.SuccessRule, p.MissionGuardChecks, p.DecoysFromSpecialForcesOnly, p.MaxTeamMembers, p.MaxDecoys, p.CountAboardUnits, p.DiplomacyNeedsCalm, p.RecruitSystemTerm));
        Assert.Equal((InciteInputMode.LeadershipMinusControllerSupport, InciteSupportTiming.PerIteration, false, SabotageSkill.Average, SabotageTargetMode.RandomFacility, true),
            (p.InciteInputMode, p.InciteSupportTiming, p.SubdueEndsUprising, p.SabotageSkill, p.SabotageTargetMode, p.ExposureHooks));
        var d = GameData.Default.Missions;
        Assert.Equal(25, d.Records.Count);
        Assert.Equal(14, d.DecoyTable!.Count); // B1: FDECOYTB read from the 1998 files (was null: decoys refused)
        Assert.Equal(14, d.Foil.Count);
        Assert.Equal((5, 10, true, "DIPLMSTB"), (d.Get(MissionKind.Diplomacy).TimerMin, d.Get(MissionKind.Diplomacy).TimerSpread, d.Get(MissionKind.Diplomacy).Repeats, d.Get(MissionKind.Diplomacy).Table));
        Assert.Equal(0x802, d.Get(MissionKind.Rescue).SpecialForceMask);
        Assert.True(d.Get(MissionKind.Dagobah).Hidden);
        Assert.False(d.Get(MissionKind.Assassination).Alliance);
        Assert.Equal((35, 35, -1, 50, 579), (d.Numbers.DecoyOfficerPercent, d.Numbers.DetectionOfficerPercent, d.Numbers.DetectionConstant, d.Numbers.HanMissionSpeed, d.Numbers.HanCharacterId));
        Assert.Equal(10, GameData.Default.Ships["Imperial Star Destroyer"].Detection > 0 ? 10 : 0); // ships carry their detection column
        Assert.Contains("5.3", Game.WorkList);
        Assert.True(Game.WorkList.ToList().IndexOf("5.3") > Game.WorkList.ToList().IndexOf("5.2") && Game.WorkList.ToList().IndexOf("5.3") < Game.WorkList.ToList().IndexOf("5.4"));
    }

    // Defenders (B2d §3): what a planet-target mission meets: the capital ships of the fleets there (officer = the fleet's Admiral) and the
    // regiments aboard (the fleet's General; only with CountAboardUnits) at arrival, the special forces on the enemy-held planet at each job (no
    // officer, detection 0); never landed regiments, squadrons, facilities or the mission side's units when only the enemy is there.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DefenderList(bool countAboard)
    {
        var w = new MissionWorld(new MissionParams { CountAboardUnits = countAboard });
        w.Hold(T, Side.Empire, 0);
        int fleet = w.Host.Fleets.AddFleet(Side.Empire, "E", new[] { "Imperial Star Destroyer" }, T);
        int ship = w.Game.State.Fleets[fleet].Ships[0];
        var aboard = w.Host.Economy.Find(w.Unit(Side.Empire, ImpArmy, T))!;
        aboard.System = null; aboard.AboardShip = ship;
        int landed = w.Unit(Side.Empire, Storm, T);
        int based = w.Unit(Side.Empire, "TIE Fighter", T);
        w.Unit(Side.Empire, Probe, T); w.Unit(Side.Alliance, RebArmy, T); w.Host.Economy.AddFacility(T, FacilityKind.Mine);
        w.Agent(Piett, T, esp: 60); w.Ch.SetPost(Piett, fleet, CharacterPost.Admiral);
        w.Agent(Veers, T, esp: 40); w.Ch.SetPost(Veers, fleet, CharacterPost.General);
        var list = w.M.Defenders(Side.Alliance, T);
        int probe = w.Host.Economy.Objects.Single(o => o.Class == Probe).Id;
        var expected = new List<(int, int)> { (ship, 60) };
        if (countAboard) expected.Add((aboard.Id, 40));
        expected.Add((probe, 0));
        Assert.Equal(expected, list.Select(d => (d.Unit.Id, d.OfficerEspionage)));
        Assert.DoesNotContain(list, d => d.Unit.Id == landed || d.Unit.Id == based);
        Assert.Equal(0, list.Single(d => d.Unit.Id == probe).Detection);
        Assert.Equal(w.Host.Fleets.ClassOf(w.Host.Economy.Find(ship)!)!.Detection, list[0].Detection);
    }

    // The character module sees mission membership (IsOnMission: MoveCharacter and AttachCharacter refuse); a capture ends it; the fog sees agents.
    [Fact]
    public void HooksIntoCharactersAndFog()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        w.Agent(Han, T, esp: 50);
        w.Stub!.Expect(0, 20, 20); // a long timer: the mission stays working
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia, Han }, T);
        w.Game.Step();
        Assert.True(w.Ch.Get(Leia) is { } l && !w.Ch.IsAvailable(l));
        Assert.Equal(CharacterReject.OnMission, w.Try(s => new MoveCharacterOrder(s, Side.Alliance, Leia, H)));
        Assert.True(w.M.AgentsAt(Side.Alliance, T));
        Assert.True(w.Host.Fog.HasLiveAccess(Side.Alliance, T)); // AgentPresenceCounts DEFAULT on
        // Leia is captured by some other rule: her membership ends; Han carries on
        bool once = false;
        w.Game.RegisterHook("5.2", ctx => { if (!once) { once = true; w.Ch.Capture(ctx, w.Ch.Get(Leia)!, Side.Empire, null, CharacterCause.Debug); } });
        w.Game.Step();
        Assert.Equal(new[] { Han }, w.M.Missions.Single().Characters);
        Assert.False(w.M.IsOnMission(Leia));
        Assert.Equal(T, w.Ch.Get(Leia)!.HeldAt);
    }

    // The mission side's own events go to that side only (fog §N7); MissionFoiled is seen by the defender with live access.
    [Fact]
    public void EventAudience()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, H, esp: 15);
        w.Unit(Side.Empire, Probe, T); // B2d: a special force guards the enemy-held planet
        w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T);
        w.Game.Step();
        w.RunIteration(w.LastMission);
        var empire = w.Host.Fog.EventsFor(Side.Empire);
        Assert.DoesNotContain(empire, e => e is MissionLanded or MissionStarted or MissionDetectionRoll or MissionEnded);
        Assert.Contains(empire, e => e is MissionFoiled);
        Assert.Contains(w.Host.Fog.EventsFor(Side.Alliance), e => e is MissionLanded);
    }

    // TooManyMembers only when a cap is set.
    [Fact]
    public void Caps()
    {
        var w = new MissionWorld(new MissionParams { MaxTeamMembers = 1 });
        w.Agent(Leia, T, esp: 50); w.Agent(Han, T, esp: 50);
        Assert.Equal(MissionReject.TooManyMembers, w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Leia, Han }, new(), new(), X)));
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Alliance, MissionKind.Espionage, new() { Leia }, new(), new(), X)));
    }

    // m5-missions §Orders (StartMission): the read-only dry run the screens use gives the handler's verdict (same reason, or the route and the
    // arrival day of MI44 for an accepted order) and changes nothing: no state, no draw, no event.
    [Fact]
    public void CheckStartMatchesTheHandlerAndChangesNothing()
    {
        var w = new MissionWorld();
        w.Agent(Leia, H, esp: 50, com: 50);
        w.Agent(Luke, T, esp: 50);
        StartMissionOrder Order(MissionKind k, List<int> team, string? target) => new(0, Side.Alliance, k, team, new(), new(), target);
        string hash = w.Host.StateHash(); int events = w.Game.Events.Count, draws = w.Stub!.Requests.Count;

        var ok = w.M.CheckStart(Order(MissionKind.Sabotage, new() { Leia }, T));
        Assert.Null(ok.Reason);
        Assert.Equal((H, T, 3, 3), (ok.Home, ok.Target, ok.Ticks, ok.ArrivalDay));
        Assert.Equal(H, ok.Path![0]); Assert.Equal(T, ok.Path[^1]);
        Assert.Equal(MissionReject.NotTogether, w.M.CheckStart(Order(MissionKind.Espionage, new() { Leia, Luke }, X)).Reason);
        Assert.Equal(MissionReject.RuleMissing, w.M.CheckStart(Order(MissionKind.JediTraining, new() { Luke }, T)).Reason);
        Assert.Equal(MissionReject.BadTarget, w.M.CheckStart(Order(MissionKind.Espionage, new() { Leia }, null)).Reason);
        Assert.Equal((hash, events, draws), (w.Host.StateHash(), w.Game.Events.Count, w.Stub.Requests.Count));

        // the handler agrees: the same refusal, then the same trip
        Assert.Equal(MissionReject.NotTogether, w.Try(s => Order(MissionKind.Espionage, new() { Leia, Luke }, X) with { Sequence = s }));
        var check = w.M.CheckStart(Order(MissionKind.Sabotage, new() { Leia }, T));
        w.Stub.Expect(0, 2, 2);
        Assert.Null(w.Try(s => Order(MissionKind.Sabotage, new() { Leia }, T) with { Sequence = s }));
        var started = w.Events<MissionStarted>().Single();
        Assert.Equal((check.Ticks, check.ArrivalDay), (started.TotalTicks, started.ArrivalDay));
        Assert.Equal(MissionReject.NotFree, w.M.CheckStart(Order(MissionKind.Sabotage, new() { Leia }, T)).Reason);
    }
}
