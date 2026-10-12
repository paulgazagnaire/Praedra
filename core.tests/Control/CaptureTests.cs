using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;
using Rebellion.Core.Tests.Economy;
using Rebellion.Core.Tests.Support;

namespace Rebellion.Core.Tests.Control;

/// <summary>A <see cref="SupWorld"/> with the control module attached and debug orders on.</summary>
internal sealed class CtlWorld
{
    /// <summary>The support world; null for a world wrapped around a loaded game (only Game, Ctl, Eco, Fleets, Run, Capture and Events work then).</summary>
    public SupWorld S { get; }
    public ControlModule Ctl { get; }
    public Game Game { get; }
    public EconomyModule Eco { get; }
    public FleetModule Fleets { get; }
    public ConstructionModule? Build { get; }
    public StubRng Stub => S.Stub;

    public CtlWorld(Game loaded, ControlModule ctl, EconomyModule eco, FleetModule fleets, ConstructionModule? build)
    {
        S = null!; Game = loaded; Ctl = ctl; Eco = eco; Fleets = fleets; Build = build;
    }

    public CtlWorld(bool stub = true, ulong seed = 1, bool construction = false)
    {
        S = new SupWorld(stub: stub, seed: seed);
        if (construction) Build = ConstructionModule.Attach(S.Game, S.W.Fleets, S.W.Eco, support: S.Sup);
        Ctl = ControlModule.Attach(S.Game, TestData.RealGalaxy, S.W.Fleets, S.W.Eco, S.Sup, Build);
        Ctl.DebugOrdersEnabled = true;
        Game = S.Game; Eco = S.W.Eco; Fleets = S.W.Fleets;
    }

    public void Run(int n) { for (int i = 0; i < n; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();
    public void Capture(Side by, string system) => Ctl.SubmitDebugSetHolder(by, system, by);
    public void Hold(string system, Side side, int holderSupport) => S.Hold(system, side, holderSupport);
    public int A(string system) => S.A(system);

    /// <summary>Run <paramref name="a"/> inside slot 5.2 of the next Step: how M3's assault will call the capture.</summary>
    public void InAssaultSlot(Action<StepContext> a)
    {
        bool done = false;
        Game.RegisterHook("5.2", ctx => { if (!done) { done = true; a(ctx); } });
    }

    public static string Names(IEnumerable<GameEvent> es) => string.Join(",", es.Select(e => e.Kind));
}

// m2-control-and-capture, tests C1 to C19. C16 is an M6 test (the AI); here C14 checks that the switch is stored, saved and on by default.
public class CaptureTests
{
    private const string Emp = "Corellia";  // Empire-held in most tests
    private const string Ally = "Bespin";   // Alliance-held in most tests

    private static readonly Type[] ControlKinds = { typeof(SystemCaptured), typeof(FacilityTransferred), typeof(FacilityRemoved), typeof(UnfinishedFacilityCancelled), typeof(UprisingEnded) };

    private static List<GameEvent> CaptureEvents(CtlWorld w) =>
        w.Game.Events.Where(e => ControlKinds.Contains(e.GetType())).ToList();

    // m2-control-and-capture C1: Alliance captures an Empire system with the full set; only the Death Star Shield is removed
    [Fact]
    public void C1_AllianceCapture_EverythingTransfers_ShieldRemoved()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        var mines = new[] { w.S.W.Mine(Emp), w.S.W.Mine(Emp), w.S.W.Mine(Emp) };
        var refs = new[] { w.S.W.Refinery(Emp), w.S.W.Refinery(Emp) };
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        int kdy = w.Eco.AddFacility(Emp, FacilityKind.Kdy150);
        int shield = w.Eco.AddFacility(Emp, FacilityKind.DeathStarShield);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);

        Assert.Equal(Side.Alliance, w.Ctl.HolderOf(Emp));
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal((Emp, (Side?)Side.Empire, Side.Alliance, CaptureCause.Debug), (cap.System, cap.From, cap.To, cap.Cause));
        var moved = w.Events<FacilityTransferred>().Select(e => e.Facility).ToList();
        Assert.Equal(mines.Concat(refs).Concat(new[] { yard, kdy }).OrderBy(i => i).ToList(), moved);
        foreach (int id in moved) Assert.Equal(Side.Alliance, w.Eco.OwnerOf(w.Eco.Find(id)!));
        var removed = Assert.Single(w.Events<FacilityRemoved>());
        Assert.Equal(shield, removed.Facility);
        Assert.Null(w.Eco.Find(shield));
    }

    // C2: the Empire captures an Alliance system with the same set minus the shield; nothing is removed
    [Fact]
    public void C2_EmpireCapture_EverythingTransfers_NothingRemoved()
    {
        var w = new CtlWorld();
        w.Hold(Ally, Side.Alliance, 80);
        var ids = new List<int> { w.S.W.Mine(Ally), w.S.W.Mine(Ally), w.S.W.Mine(Ally), w.S.W.Refinery(Ally), w.S.W.Refinery(Ally),
            w.Eco.AddFacility(Ally, FacilityKind.OrbitalShipyard), w.Eco.AddFacility(Ally, FacilityKind.Kdy150) };
        w.Capture(Side.Empire, Ally);
        w.Run(1);
        Assert.Equal(ids.OrderBy(i => i).ToList(), w.Events<FacilityTransferred>().Select(e => e.Facility).ToList());
        Assert.Empty(w.Events<FacilityRemoved>());
        Assert.All(ids, id => Assert.Equal(Side.Empire, w.Eco.OwnerOf(w.Eco.Find(id)!)));
    }

    // The shield only serves the Empire: an Empire capture of a neutral system keeps one, an Alliance capture removes it (Lead, DEFAULT).
    [Fact]
    public void ShieldServesOnlyTheEmpire()
    {
        Assert.True(ControlModule.ServesSide("Death Star Shield", Side.Empire));
        Assert.False(ControlModule.ServesSide("Death Star Shield", Side.Alliance));
        Assert.True(ControlModule.ServesSide("Mine", Side.Alliance));
        var w = new CtlWorld();
        w.Fleets.SetOwner(Emp, null);
        int shield = w.Eco.AddFacility(Emp, FacilityKind.DeathStarShield);
        w.Capture(Side.Empire, Emp);
        w.Run(1);
        Assert.Equal(Side.Empire, w.Eco.OwnerOf(w.Eco.Find(shield)!));
    }

    // C3: capture of a neutral system with a shipyard (side 3): the shipyard becomes the capturer's
    [Fact]
    public void C3_NeutralSystemCapture_ShipyardBecomesCapturers()
    {
        var w = new CtlWorld();
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        Assert.Null(w.Ctl.HolderOf(Emp));
        Assert.Null(w.Eco.OwnerOf(w.Eco.Find(yard)!)); // serves nobody until then
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Null(cap.From);
        Assert.Equal(Side.Alliance, w.Eco.OwnerOf(w.Eco.Find(yard)!));
        var t = Assert.Single(w.Events<FacilityTransferred>());
        Assert.Equal((yard, (Side?)null, Side.Alliance), (t.Facility, t.From, t.To));
    }

    // C4: capture while a mine is under construction: ProjectCancelled, no refund, maintenance freed
    [Fact]
    public void C4_UnfinishedFacility_Cancelled_NoRefund_MaintenanceFreed()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        int mine = w.S.W.Mine(Emp, completed: false);
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard, completed: false); // maintenance 13 in the costs file
        w.Eco.SetPools(Side.Empire, 5, 7); w.Eco.SetPools(Side.Alliance, 1, 2);
        int loadBefore = w.Eco.Load(Side.Empire);
        Assert.Equal(13, loadBefore);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        var cancelled = w.Events<UnfinishedFacilityCancelled>().Select(e => e.Object).ToList();
        Assert.Equal(new[] { mine, yard }, cancelled);
        Assert.Null(w.Eco.Find(mine)); Assert.Null(w.Eco.Find(yard));
        Assert.Equal(0, w.Eco.Load(Side.Empire));
        Assert.Equal(0, w.Eco.Load(Side.Alliance)); // not carried over to the new holder
        Assert.Equal((5, 7), (w.Eco.Raw(Side.Empire), w.Eco.Refined(Side.Empire)));   // no refund
        Assert.Equal((1, 2), (w.Eco.Raw(Side.Alliance), w.Eco.Refined(Side.Alliance)));
        Assert.Empty(w.Events<FacilityTransferred>());
    }

    // C5: capture while an ISD is queued at the old holder's yard: the order is cancelled (by the construction module), no refund; the
    // yard is the new holder's, idle
    [Fact]
    public void C5_QueuedOrderOfOldHolder_Cancelled_YardGoesIdleToNewHolder()
    {
        var w = new CtlWorld(construction: true);
        w.Hold(Emp, Side.Empire, 80);
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        int army = w.Eco.AddObject(Side.Empire, "Imperial Army Regiment", Emp);                          // completed regiment: untouched (4)
        w.Eco.SetPools(Side.Empire, 0, 1000);
        w.Build!.GrantResearch(Side.Empire, "Imperial Star Destroyer");
        w.Build.SubmitBuild(Side.Empire, Emp, YardFamily.Shipyard, "Imperial Star Destroyer", 1);
        w.Run(1);
        var isd = w.Eco.Objects.Single(o => o.Class == "Imperial Star Destroyer").Id;
        Assert.NotNull(w.Build.ProjectAt(Emp, YardFamily.Shipyard));
        int refinedBefore = w.Eco.Refined(Side.Empire);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Null(w.Eco.Find(isd));
        Assert.Null(w.Build.ProjectAt(Emp, YardFamily.Shipyard));
        Assert.NotNull(w.Eco.Find(army));
        Assert.True(w.Eco.Refined(Side.Empire) <= refinedBefore); // no refund
        Assert.Equal(Side.Alliance, w.Eco.OwnerOf(w.Eco.Find(yard)!));
        Assert.True(w.Eco.Find(yard)!.Completed);
        Assert.False(w.Eco.Find(yard)!.InLine);
        Assert.Equal(0, w.Eco.Find(yard)!.Due);
        var c = Assert.Single(w.Events<Rebellion.Core.Construction.ProjectCancelled>()); // one cancellation: 6.6 found nothing left
        Assert.Equal(("SystemLost", Emp), (c.Reason, c.System));
        Assert.Contains(isd, c.Units.Items);
    }

    // C6: Empire-held, A = 70 (Empire support 30), no uprising, Alliance captures: A unchanged, N = 0, no uprising
    [Fact]
    public void C6_SupportUnchanged_NewHolderSupport70_NoUprising()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 30);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal(Side.Alliance, w.Ctl.HolderOf(Emp));
        Assert.Equal(70, w.A(Emp));
        Assert.Equal(70, w.Eco.SupportOf(Emp, Side.Alliance));
        Assert.Equal(0, w.S.Sup.RequiredGarrison(Emp));
        Assert.False(w.S.Sup.IsUprising(Emp));
        Assert.Empty(w.Events<UprisingStarted>());
        Assert.Empty(w.Events<SupportChanged>());
    }

    // C7: Empire-held, A = 20; Alliance captures with 0 regiments: N = 4, UprisingStarted at slot 6.1 of the same Step, arm draw rand(0..70).
    // Captured in slot 5.2, the way M3's assault will call it.
    [Fact]
    public void C7_FreshlyCapturedHostileWorld_UprisingAtSlot61SameStep()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80); // A = 20
        w.Stub.Expect(0, 70, 5);
        int drawsInCapture = -1;
        w.InAssaultSlot(ctx =>
        {
            int before = w.Stub.Requests.Count;
            Assert.True(w.Ctl.SetHolder(ctx, Emp, Side.Alliance, CaptureCause.Assault));
            drawsInCapture = w.Stub.Requests.Count - before;
        });
        w.Run(1);
        Assert.Equal(0, drawsInCapture);
        Assert.Equal(20, w.A(Emp));
        Assert.Equal(4, SupportModule.RequiredGarrison(w.S.Sup.Params, 20, false, false)); // N before the uprising doubles it
        Assert.Equal(8, w.S.Sup.RequiredGarrison(Emp));
        var up = Assert.Single(w.Events<UprisingStarted>());
        Assert.Equal((Emp, Side.Alliance, 1), (up.System, up.Holder, up.Day));
        Assert.Equal(1, w.Stub.Count(0, 70)); // the arm draw of timer 0x38d
        Assert.Equal(1 + 30 + 5, w.Game.State.Support.Systems[Emp].IncidentDue);
        // order in the Step: the capture event comes before the uprising
        Assert.True(w.Game.Events.ToList().FindIndex(e => e is SystemCaptured) < w.Game.Events.ToList().FindIndex(e => e is UprisingStarted));
        Assert.Equal(CaptureCause.Assault, Assert.Single(w.Events<SystemCaptured>()).Cause);
    }

    // C8: capture of an Empire system that was in uprising: flag, rebel side and timers cleared; UprisingEnded before the new holder is evaluated
    [Fact]
    public void C8_UprisingCleared_UprisingEndedBeforeNewHolderEvaluated()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80); // A = 20: the new holder will need a garrison of 4
        w.S.Uprising(Emp, Side.Empire, erosionDue: 12, incidentDue: 15);
        w.Stub.Expect(0, 70, 9);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        var ended = Assert.Single(w.Events<UprisingEnded>());
        Assert.Equal("Captured", ended.Reason);
        var evs = w.Game.Events.ToList();
        int iCap = evs.FindIndex(e => e is SystemCaptured), iEnd = evs.FindIndex(e => e is UprisingEnded), iNew = evs.FindIndex(e => e is UprisingStarted);
        Assert.True(iCap < iEnd && iEnd < iNew);
        var st = w.Game.State.Support.Systems[Emp]; // a fresh uprising of the new holder: new side, new timers
        Assert.True(st.Uprising);
        Assert.Equal(Side.Empire, st.Rebels);
        Assert.Equal(31, st.ErosionDue); // not the old 12
        Assert.Equal(1 + 30 + 9, st.IncidentDue); // not the old 15
    }

    [Fact]
    public void C8b_UprisingCleared_NewHolderContent_LeavesNoFlagAndNoTimers()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 30); // A = 70: the Alliance needs no garrison
        w.S.Uprising(Emp, Side.Empire, erosionDue: 12, incidentDue: 15);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal("Captured", Assert.Single(w.Events<UprisingEnded>()).Reason);
        Assert.False(w.S.Sup.IsUprising(Emp));
        Assert.Null(w.S.Sup.StateOf(Emp)); // idle entry pruned: no timer left
        Assert.Empty(w.Events<UprisingStarted>());
    }

    // C9: support reaches 100 for the Alliance on an Empire-held system: the holder stays Empire
    [Fact]
    public void C9_SupportAt100_NeverFlipsControl()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 0); // A = 100
        w.Run(50);
        Assert.Equal(Side.Empire, w.Ctl.HolderOf(Emp));
        w.Eco.SetAllianceSupport(Emp, 0);
        w.Run(50);
        Assert.Equal(Side.Empire, w.Ctl.HolderOf(Emp));
        Assert.Empty(w.Events<SystemCaptured>());
    }

    // C10: neutral system, A = 90, 1000 Steps: still neutral
    [Fact]
    public void C10_NeutralSystem_StaysNeutral_ThousandSteps()
    {
        var w = new CtlWorld();
        w.Fleets.SetOwner(Emp, null);
        w.Eco.SetAllianceSupport(Emp, 90);
        w.Run(1000);
        Assert.Null(w.Ctl.HolderOf(Emp));
        Assert.Equal(90, w.A(Emp));
        Assert.Empty(w.Events<SystemCaptured>());
        Assert.Empty(w.Events<UprisingStarted>()); // no uprising on a neutral system
    }

    // C11: capture of the capital: no support effect (default); the aftermath is a recorded hook
    [Fact]
    public void C11_CapitalCapture_NoSupportEffect_AftermathHookRaised()
    {
        var w = new CtlWorld();
        w.Hold(ControlModule.CapitalSystem, Side.Empire, 80);
        var seen = new List<(string, bool)>();
        w.Ctl.ControlChangeAftermath += (c, capital) => seen.Add((c.System, capital));
        w.Capture(Side.Alliance, ControlModule.CapitalSystem);
        w.Hold(Emp, Side.Empire, 80);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal(new[] { (ControlModule.CapitalSystem, true), (Emp, false) }, seen);
        Assert.Equal(20, w.A(ControlModule.CapitalSystem));
        Assert.Empty(w.Events<SupportChanged>());
    }

    // C12: blockade flag recomputed: an Alliance fleet at the system is no longer a blockader after the Alliance captures it
    [Fact]
    public void C12_BlockadeRecomputed_AllianceFleetNoLongerBlockader()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        w.Fleets.AddFleet(Side.Alliance, "Picket", new[] { "Corellian Corvette" }, Emp);
        w.Run(1);
        Assert.Equal(Side.Alliance, w.Game.State.Blockades[Emp]);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.False(w.Game.State.Blockades.ContainsKey(Emp));
        var lifted = Assert.Single(w.Events<BlockadeLifted>());
        Assert.Equal((Emp, Side.Alliance), (lifted.System, lifted.Blockader));
        // and it was lifted by the capture itself, before step 5 of that Step: right after the capture event
        var evs = w.Game.Events.ToList();
        Assert.True(evs.FindIndex(e => e is SystemCaptured) < evs.FindIndex(e => e is BlockadeLifted));
    }

    [Fact]
    public void C12b_BlockadeRecomputed_OldHoldersFleetBecomesBlockader()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        w.Fleets.AddFleet(Side.Empire, "Home", new[] { "Imperial Star Destroyer" }, Emp);
        w.Run(1);
        Assert.Empty(w.Game.State.Blockades);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal(Side.Empire, w.Game.State.Blockades[Emp]);
        Assert.Single(w.Events<BlockadeStarted>());
    }

    // C13: pool check: Alliance capture of 3 mines, 2 refineries: capacity rises by the five, Empire's falls by the same, same Step
    [Fact]
    public void C13_PoolsFollowTheFacilities_SameStep()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        for (int i = 0; i < 3; i++) w.S.W.Mine(Emp);
        for (int i = 0; i < 2; i++) w.S.W.Refinery(Emp);
        int shipyard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard); // maintenance 13 moves with it
        Assert.Equal((100, 0, 13, 0), (w.Eco.Capacity(Side.Empire), w.Eco.Capacity(Side.Alliance), w.Eco.Load(Side.Empire), w.Eco.Load(Side.Alliance)));
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal((0, 100, 0, 13), (w.Eco.Capacity(Side.Empire), w.Eco.Capacity(Side.Alliance), w.Eco.Load(Side.Empire), w.Eco.Load(Side.Alliance)));
        Assert.Equal(Side.Alliance, w.Eco.OwnerOf(w.Eco.Find(shipyard)!));
    }

    // a refinery waiting in the old holder's raw line moves to the new holder's line in the same Step (slot 6.5, economy sheet)
    [Fact]
    public void RefineryInRawLine_MovesToNewHoldersLine()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        w.S.W.Mine(Emp);
        int r = w.S.W.Refinery(Emp);
        w.Run(1); // the refinery asks for raw and waits
        Assert.Contains(r, w.Eco.RawLine(Side.Empire));
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.DoesNotContain(r, w.Eco.RawLine(Side.Empire));
        Assert.Contains(r, w.Eco.RawLine(Side.Alliance));
    }

    // C14: scorched_earth setting saved, loaded; default on for a new game
    [Fact]
    public void C14_ScorchedEarth_DefaultOn_SavedAndLoaded()
    {
        var w = new CtlWorld();
        Assert.True(w.Ctl.ScorchedEarth(Side.Empire));
        Assert.True(w.Ctl.ScorchedEarth(Side.Alliance));
        string onHash = GameSave.StateHash(w.Game);
        w.Ctl.SetScorchedEarth(Side.Empire, false);
        Assert.NotEqual(onHash, GameSave.StateHash(w.Game)); // part of the hashed state

        var loaded = GameSave.Load(GameSave.Save(w.Game));
        Assert.False(loaded.State.Control.ScorchedEarth(Side.Empire));
        Assert.True(loaded.State.Control.ScorchedEarth(Side.Alliance));
        Assert.Equal(GameSave.Save(w.Game), GameSave.Save(loaded));
        Assert.True(new GameState().Control.ScorchedEarth(Side.Empire));
    }

    // C15: human Scrap of a mine on its own system, switch off: accepted (economy sheet's refund)
    [Fact]
    public void C15_HumanScrap_SwitchOff_Accepted()
    {
        var w = new CtlWorld();
        w.Ctl.SetScorchedEarth(Side.Alliance, false);
        w.Hold(Ally, Side.Alliance, 80);
        int mine = w.S.W.Mine(Ally);
        w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Alliance, mine));
        w.Run(1);
        Assert.Null(w.Eco.Find(mine));
        Assert.Equal(10, w.Eco.Refined(Side.Alliance));
        Assert.Empty(w.Game.Events.OfType<OrderRejected>());
    }

    // C17: 6 mines/refineries (20 each) and a shipyard (20): refund = 6 x 10 + 10 = 70 refined
    [Fact]
    public void C17_ScrapExample_RefundIs70()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        var ids = new List<int>();
        for (int i = 0; i < 3; i++) { ids.Add(w.S.W.Mine(Emp)); ids.Add(w.S.W.Refinery(Emp)); }
        ids.Add(w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard));
        foreach (int id in ids) w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Empire, id));
        w.Run(1);
        Assert.Equal(70, w.Eco.Refined(Side.Empire));
        Assert.Equal(7, w.Events<ObjectScrapped>().Count);
    }

    // After a scrap, the enemy gains nothing from a capture (the scorched-earth example)
    [Fact]
    public void ScorchedWorld_GivesTheCapturerNothing()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        var ids = new List<int>();
        for (int i = 0; i < 3; i++) { ids.Add(w.S.W.Mine(Emp)); ids.Add(w.S.W.Refinery(Emp)); }
        foreach (int id in ids) w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Empire, id));
        w.Run(1);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal(0, w.Eco.Capacity(Side.Alliance));
        Assert.Empty(w.Events<FacilityTransferred>());
    }

    // C18: capture twice in two Steps (A to B, B to A): each re-applies the rules; facility ids unchanged; events in order
    [Fact]
    public void C18_CaptureTwice_IdsUnchanged_EventsInOrder()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        int mine = w.S.W.Mine(Emp), yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        w.Capture(Side.Empire, Emp);
        w.Run(1);
        var caps = w.Events<SystemCaptured>();
        Assert.Equal(new[] { (Side.Empire, Side.Alliance), (Side.Alliance, Side.Empire) }, caps.Select(c => (c.From!.Value, c.To)).ToArray());
        Assert.Equal(new[] { 0, 1 }, caps.Select(c => c.Day - 1).ToArray());
        var moved = w.Events<FacilityTransferred>();
        Assert.Equal(new[] { mine, yard, mine, yard }, moved.Select(m => m.Facility).ToArray());
        Assert.Equal(new[] { Side.Alliance, Side.Alliance, Side.Empire, Side.Empire }, moved.Select(m => m.To).ToArray());
        Assert.Equal(Side.Empire, w.Eco.OwnerOf(w.Eco.Find(mine)!));
        // per capture: SystemCaptured first, then its facility events; the Alliance held a hostile world with no garrison (A = 20), so its
        // uprising (slot 6.1) is ended by the second capture, last
        Assert.Equal("SystemCaptured,FacilityTransferred,FacilityTransferred,SystemCaptured,FacilityTransferred,FacilityTransferred,UprisingEnded",
            CtlWorld.Names(CaptureEvents(w)));
    }

    // C19: captures with the same inputs run twice: identical event lists; no random draw from the capture
    [Fact]
    public void C19_SameInputsTwice_IdenticalEvents_NoDraws()
    {
        List<GameEvent> Play(out int draws)
        {
            var w = new CtlWorld(stub: false, seed: 7);
            w.Hold(Emp, Side.Empire, 80);
            w.Hold(Ally, Side.Alliance, 80);
            w.S.W.Mine(Emp); w.S.W.Refinery(Emp); w.Eco.AddFacility(Emp, FacilityKind.DeathStarShield);
            w.Eco.AddFacility(Emp, FacilityKind.Mine, completed: false);
            w.S.Uprising(Emp, Side.Empire, erosionDue: 10, incidentDue: 12);
            w.Fleets.AddFleet(Side.Alliance, "Picket", new[] { "Corellian Corvette" }, Emp);
            int d = 0;
            w.InAssaultSlot(ctx =>
            {
                var before = w.Game.Rng.Export();
                w.Ctl.SetHolder(ctx, Emp, Side.Alliance, CaptureCause.Assault);
                w.Ctl.SetHolder(ctx, Ally, Side.Empire, CaptureCause.Assault);
                d = w.Game.Rng.Export() == before ? 0 : 1;
            });
            w.Run(3);
            draws = d;
            return w.Game.Events.ToList();
        }
        var a = Play(out int da); var b = Play(out int db);
        Assert.Equal(0, da); Assert.Equal(0, db);
        Assert.Equal(a, b);
        Assert.Contains(a, e => e is FacilityRemoved);
    }

    // SetHolder to the side that already holds the system changes nothing and raises nothing
    [Fact]
    public void SetHolder_SameHolder_IsANoOp()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        w.S.W.Mine(Emp);
        w.InAssaultSlot(ctx => Assert.False(w.Ctl.SetHolder(ctx, Emp, Side.Empire, CaptureCause.Assault)));
        w.Run(1);
        Assert.Empty(w.Events<SystemCaptured>());
        Assert.Empty(w.Events<FacilityTransferred>());
    }

    // DebugSetHolder: rejected unless enabled, for a system already held, and for an unknown system
    [Fact]
    public void DebugOrder_Rejections()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        w.Ctl.DebugOrdersEnabled = false;
        w.Capture(Side.Alliance, Emp);
        w.Run(1);
        Assert.Equal(RejectReason.DebugDisabled, Assert.Single(w.Events<OrderRejected>()).Reason);
        Assert.Equal(Side.Empire, w.Ctl.HolderOf(Emp));

        w.Ctl.DebugOrdersEnabled = true;
        w.Capture(Side.Empire, Emp);
        w.Ctl.SubmitDebugSetHolder(Side.Empire, "Nowhere", Side.Empire);
        w.Run(1);
        Assert.Equal(new[] { RejectReason.DebugDisabled, RejectReason.AlreadyHolder, RejectReason.UnknownSystem },
            w.Events<OrderRejected>().Select(r => r.Reason).ToArray());
    }

    [Fact]
    public void DebugOrder_SurvivesSaveAsAQueuedOrder()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Empire, 80);
        long seq = w.Ctl.SubmitDebugSetHolder(Side.Alliance, Emp, Side.Alliance);
        var json = GameSave.Save(w.Game);
        var loaded = GameSave.Load(json);
        var order = Assert.Single(loaded.Queue);
        Assert.Equal(new DebugSetHolderOrder(seq, Side.Alliance, Emp, Side.Alliance), order);
    }

    // ---------------------------------------------------------------- integration: owner change mid-run keeps economy, support and fleets consistent

    private static CtlWorld Scenario()
    {
        var w = new CtlWorld(stub: false, seed: 11);
        w.Hold(Emp, Side.Empire, 40);   // A = 60
        w.Hold(Ally, Side.Alliance, 70);
        for (int i = 0; i < 2; i++) { w.S.W.Mine(Emp); w.S.W.Refinery(Emp); }
        w.S.W.Mine(Ally); w.S.W.Refinery(Ally);
        w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        w.S.Regiments(Emp, Side.Empire, SupWorld.ArmyOf(Side.Empire), 2);
        w.Fleets.AddFleet(Side.Alliance, "Raiders", new[] { "Corellian Corvette" }, Emp);
        w.Fleets.AddFleet(Side.Empire, "Hammer", new[] { "Imperial Star Destroyer" }, Ally);
        return w;
    }

    private static CtlWorld Reattach(Game loaded, bool construction = false)
    {
        // attach every module to the loaded game, the way a real load does
        var p = new EconomyParams { ScrapPeriod = 1_000_000 };
        var fleets = FleetModule.Attach(loaded, TestData.RealGalaxy, Rebellion.Core.Tests.Fleets.World.Ships.Value, Rebellion.Core.Tests.Fleets.World.Blockade);
        var eco = EconomyModule.Attach(loaded, TestData.RealGalaxy, EcoWorld.Slots.Value, EcoWorld.Costs.Value, fleets, p);
        eco.IncidentsEnabled = false;
        var sup = SupportModule.Attach(loaded, TestData.RealGalaxy, eco, fleets, SupWorld.Weights.Value);
        var build = construction ? ConstructionModule.Attach(loaded, fleets, eco, support: sup) : null;
        var ctl = ControlModule.Attach(loaded, TestData.RealGalaxy, fleets, eco, sup, build);
        ctl.DebugOrdersEnabled = true;
        return new CtlWorld(loaded, ctl, eco, fleets, build);
    }

    [Fact]
    public void Integration_OwnerChangeMidRun_EconomySupportAndFleetsStayConsistent()
    {
        var w = Scenario();
        w.Run(40);
        int capBefore = w.Eco.Capacity(Side.Empire), allyCapBefore = w.Eco.Capacity(Side.Alliance);
        Assert.Equal(100, capBefore);
        Assert.Equal(50, allyCapBefore);
        Assert.Equal(Side.Alliance, w.Game.State.Blockades[Emp]); // economy: blockaded mines idle
        int aAtCapture = w.A(Emp);
        w.Capture(Side.Alliance, Emp);
        w.Run(1);

        // fleets: the owner changed, the blockade flag follows, the old holder's fleet at Corellia is not there
        Assert.Equal(Side.Alliance, w.Fleets.OwnerOf(Emp));
        Assert.False(w.Game.State.Blockades.ContainsKey(Emp));
        // economy: the facilities are the Alliance's, capacity moved, no negative pool
        Assert.Equal(0, w.Eco.Capacity(Side.Empire));
        Assert.Equal(150, w.Eco.Capacity(Side.Alliance));
        foreach (var f in w.Eco.Objects.Where(o => o.System == Emp && o.Category == ObjectCategory.Facility))
            Assert.Equal(Side.Alliance, w.Eco.OwnerOf(f));
        Assert.True(w.Eco.Raw(Side.Empire) >= 0 && w.Eco.Refined(Side.Empire) >= 0);
        // support: stored value untouched (60 Alliance); the Empire's 2 regiments are not the holder's, so N = 0 here and nothing erupts
        Assert.Equal(aAtCapture, w.A(Emp)); // the 40 blockaded days may have moved it; the capture itself does not
        Assert.DoesNotContain(w.Events<SupportChanged>(), e => e.System == Emp && e.Day == 41);
        Assert.False(w.S.Sup.IsUprising(Emp));
        // the mines now produce for the Alliance: run on and see Alliance-side output from Corellia
        w.Run(400);
        Assert.Contains(w.Events<ResourceProduced>(), e => e.System == Emp && e.Day > 41 && e.Controller == Side.Alliance);
        Assert.DoesNotContain(w.Events<ResourceProduced>(), e => e.System == Emp && e.Day > 41 && e.Controller == Side.Empire);
        // every refinery stands in at most one line, on the line of its holder
        foreach (var side in new[] { Side.Empire, Side.Alliance })
            foreach (int id in w.Eco.RawLine(side)) Assert.Equal(side, w.Eco.OwnerOf(w.Eco.Find(id)!));
    }

    [Fact]
    public void Integration_SaveLoadMidRun_CaptureAfterLoadMatchesAnUninterruptedGame()
    {
        var whole = Scenario(); whole.Run(40); whole.Capture(Side.Alliance, Emp); whole.Run(260);

        var first = Scenario(); first.Run(40);
        var loaded = Reattach(GameSave.Load(GameSave.Save(first.Game)));
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded.Game));
        loaded.Capture(Side.Alliance, Emp);
        loaded.Run(260);
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded.Game));
    }

    [Fact]
    public void Integration_SaveLoadAfterCapture_FutureIsTheSame()
    {
        var whole = Scenario(); whole.Run(40); whole.Capture(Side.Alliance, Emp); whole.Run(10); whole.Capture(Side.Empire, Emp); whole.Run(250);

        var first = Scenario(); first.Run(40); first.Capture(Side.Alliance, Emp); first.Run(10);
        Assert.Equal(Side.Alliance, first.Ctl.HolderOf(Emp));
        var loaded = Reattach(GameSave.Load(GameSave.Save(first.Game)));
        Assert.Equal(Side.Alliance, loaded.Ctl.HolderOf(Emp));
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded.Game));
        loaded.Capture(Side.Empire, Emp);
        loaded.Run(250);
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded.Game));
    }

    // ---------------------------------------------------------------- integration with construction: a running yard project and an inbound delivery

    private const string Home = "Coruscant", Far = "Corellia";

    private static CtlWorld BuildScenario()
    {
        var w = new CtlWorld(stub: false, seed: 21, construction: true);
        w.Hold(Home, Side.Empire, 100); w.Hold(Far, Side.Empire, 100); // A = 0 at Corellia: a hostile world for an Alliance holder
        w.Eco.AddFacility(Home, FacilityKind.ConstructionYard);
        w.Eco.AddFacility(Far, FacilityKind.OrbitalShipyard);
        w.Eco.SetPools(Side.Empire, 0, 2000);
        w.Build!.GrantResearch(Side.Empire, "Imperial Star Destroyer");
        w.Build.SubmitBuild(Side.Empire, Home, YardFamily.Construction, "Mine", 1, Far);                // delivery will fly Coruscant to Corellia
        w.Build.SubmitBuild(Side.Empire, Far, YardFamily.Shipyard, "Imperial Star Destroyer", 1);        // running yard project at Corellia
        w.Fleets.AddFleet(Side.Alliance, "Picket", new[] { "Corellian Corvette" }, Far);                  // blockades Corellia
        return w;
    }

    [Fact]
    public void Integration_CaptureWithRunningProjectAndInboundDelivery()
    {
        var w = BuildScenario();
        while (!w.Game.Events.OfType<DeliveryDeparted>().Any()) w.Run(1);
        var dep = w.Events<DeliveryDeparted>().Single();
        Assert.Equal(Far, dep.To);
        Assert.Equal(Side.Alliance, w.Game.State.Blockades[Far]);
        var isdProject = w.Build!.ProjectAt(Far, YardFamily.Shipyard);
        Assert.NotNull(isdProject);
        var isdUnits = isdProject!.Units.ToList();
        int mineId = dep.Object;
        Assert.True(w.Game.Day < dep.DueDay, "the delivery is still flying");

        w.Capture(Side.Alliance, Far);
        w.Run(1);

        // construction: the project is gone with its units, once, no refund; the delivery is still alive
        var cancelled = Assert.Single(w.Events<Rebellion.Core.Construction.ProjectCancelled>());
        Assert.Equal(("SystemLost", Far, YardFamily.Shipyard), (cancelled.Reason, cancelled.System, cancelled.Family));
        Assert.Equal(isdUnits, cancelled.Units.Items.ToList());
        Assert.Null(w.Build.ProjectAt(Far, YardFamily.Shipyard));
        Assert.All(isdUnits, id => Assert.Null(w.Eco.Find(id)));
        Assert.Single(w.Build.Deliveries);
        Assert.Empty(w.Events<UnfinishedFacilityCancelled>()); // the flying mine is construction's, not a leftover
        Assert.NotNull(w.Eco.Find(mineId));
        // economy: the shipyard is the Alliance's and idle
        var yard = w.Eco.Objects.Single(o => o.System == Far && o.Class == "Orbital Shipyard");
        Assert.Equal(Side.Alliance, w.Eco.OwnerOf(yard));
        Assert.Equal(0, yard.Due); Assert.False(yard.InLine);
        Assert.DoesNotContain(yard.Id, w.Build.RefinedLine(Side.Empire));
        Assert.DoesNotContain(yard.Id, w.Build.RefinedLine(Side.Alliance));
        // fleets: owner and blockade flag follow
        Assert.Equal(Side.Alliance, w.Fleets.OwnerOf(Far));
        Assert.False(w.Game.State.Blockades.ContainsKey(Far));
        // support: stored value untouched; the Alliance, holding a world with A = 0 and no garrison, rises at 6.1 of the same Step
        Assert.Equal(0, w.S.A(Far));
        Assert.Single(w.Events<UprisingStarted>());

        // the inbound delivery is held when it reaches the lost destination (LostDestination = Pause)
        w.Run(dep.DueDay - w.Game.Day + 3);
        Assert.Equal("DestinationLost", w.Events<DeliveryHeld>().Single().Reason);
        Assert.Empty(w.Events<DeliveryArrived>());
        Assert.False(w.Eco.Find(mineId)!.Completed);
        Assert.Single(w.Events<Rebellion.Core.Construction.ProjectCancelled>()); // never cancelled twice
    }

    [Fact]
    public void Integration_ConstructionCaptureSaveLoadMidRun_FutureIsTheSame()
    {
        CtlWorld RunToCapture(CtlWorld w)
        {
            while (!w.Game.Events.OfType<DeliveryDeparted>().Any()) w.Run(1);
            w.Capture(Side.Alliance, Far);
            w.Run(2);
            return w;
        }
        var whole = RunToCapture(BuildScenario()); whole.Run(170);

        var first = RunToCapture(BuildScenario());
        var loaded = Reattach(GameSave.Load(GameSave.Save(first.Game)), construction: true);
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded.Game));
        loaded.Run(170);
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded.Game));
        Assert.Equal(whole.Build!.Deliveries.Count, loaded.Build!.Deliveries.Count);
    }
}
