using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;
using Praedra.Core.Tests.Support;

namespace Praedra.Core.Tests.Control;

/// <summary>
/// A <see cref="SupWorld"/> with the control module attached and debug orders on. The C tests run under <see cref="ControlRule.AssaultOnly"/> (the rule
/// before B5a, kept behind the switch), where the capture is the DebugSetHolder order; the B5a tests (B5aControlTests) pass Derived, the DEFAULT.
/// </summary>
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

    public CtlWorld(bool stub = true, ulong seed = 1, bool construction = false, ControlRule rule = ControlRule.AssaultOnly)
    {
        S = new SupWorld(stub: stub, seed: seed);
        if (construction) Build = ConstructionModule.Attach(S.Game, S.W.Fleets, S.W.Eco, support: S.Sup);
        if (Build != null) Build.RefuseOverdrawOrders = false; // B5b 2026-10-18: these worlds have no mines (capacity 0); capture, not upkeep, is the subject
        Ctl = ControlModule.Attach(S.Game, TestData.RealGalaxy, S.W.Fleets, S.W.Eco, S.Sup, Build);
        Ctl.Rule = rule;
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
    private const string Emp = "Classis";  // Hand-held in most tests
    private const string Ally = "Drift Harbour";   // Collective-held in most tests

    private static readonly Type[] ControlKinds = { typeof(SystemCaptured), typeof(FacilityTransferred), typeof(FacilityRemoved), typeof(UnfinishedFacilityCancelled), typeof(UprisingEnded) };

    private static List<GameEvent> CaptureEvents(CtlWorld w) =>
        w.Game.Events.Where(e => ControlKinds.Contains(e.GetType())).ToList();

    // m2-control-and-capture C1: Collective captures a Hand system with the full set; everything transfers (the sheet's planet-killer shield,
    // the one facility it removed, is cut: data/design/cut-classes.csv)
    [Fact]
    public void C1_CollectiveCapture_EverythingTransfers()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        var mines = new[] { w.S.W.Mine(Emp), w.S.W.Mine(Emp), w.S.W.Mine(Emp) };
        var refs = new[] { w.S.W.Refinery(Emp), w.S.W.Refinery(Emp) };
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        int lien = w.Eco.AddFacility(Emp, FacilityKind.Lien150);
        w.Capture(Side.Collective, Emp);
        w.Run(1);

        Assert.Equal(Side.Collective, w.Ctl.HolderOf(Emp));
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal((Emp, (Side?)Side.Hand, Side.Collective, CaptureCause.Debug), (cap.System, cap.From, cap.To, cap.Cause));
        var moved = w.Events<FacilityTransferred>().Select(e => e.Facility).ToList();
        Assert.Equal(mines.Concat(refs).Concat(new[] { yard, lien }).OrderBy(i => i).ToList(), moved);
        foreach (int id in moved) Assert.Equal(Side.Collective, w.Eco.OwnerOf(w.Eco.Find(id)!));
        Assert.Empty(w.Events<FacilityRemoved>());
    }

    // C2: the Hand captures a Collective system with the same set; nothing is removed
    [Fact]
    public void C2_HandCapture_EverythingTransfers_NothingRemoved()
    {
        var w = new CtlWorld();
        w.Hold(Ally, Side.Collective, 80);
        var ids = new List<int> { w.S.W.Mine(Ally), w.S.W.Mine(Ally), w.S.W.Mine(Ally), w.S.W.Refinery(Ally), w.S.W.Refinery(Ally),
            w.Eco.AddFacility(Ally, FacilityKind.OrbitalShipyard), w.Eco.AddFacility(Ally, FacilityKind.Lien150) };
        w.Capture(Side.Hand, Ally);
        w.Run(1);
        Assert.Equal(ids.OrderBy(i => i).ToList(), w.Events<FacilityTransferred>().Select(e => e.Facility).ToList());
        Assert.Empty(w.Events<FacilityRemoved>());
        Assert.All(ids, id => Assert.Equal(Side.Hand, w.Eco.OwnerOf(w.Eco.Find(id)!)));
    }

    // C3: capture of a neutral system with a shipyard (side 3): the shipyard becomes the capturer's
    [Fact]
    public void C3_NeutralSystemCapture_ShipyardBecomesCapturers()
    {
        var w = new CtlWorld();
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        Assert.Null(w.Ctl.HolderOf(Emp));
        Assert.Null(w.Eco.OwnerOf(w.Eco.Find(yard)!)); // serves nobody until then
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Null(cap.From);
        Assert.Equal(Side.Collective, w.Eco.OwnerOf(w.Eco.Find(yard)!));
        var t = Assert.Single(w.Events<FacilityTransferred>());
        Assert.Equal((yard, (Side?)null, Side.Collective), (t.Facility, t.From, t.To));
    }

    // C4: capture while a mine is under construction: ProjectCancelled, no refund, maintenance freed
    [Fact]
    public void C4_UnfinishedFacility_Cancelled_NoRefund_MaintenanceFreed()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        int mine = w.S.W.Mine(Emp, completed: false);
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard, completed: false); // maintenance 13 in the costs file
        w.Eco.SetPools(Side.Hand, 5, 7); w.Eco.SetPools(Side.Collective, 1, 2);
        int loadBefore = w.Eco.Load(Side.Hand);
        Assert.Equal(13, loadBefore);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        var cancelled = w.Events<UnfinishedFacilityCancelled>().Select(e => e.Object).ToList();
        Assert.Equal(new[] { mine, yard }, cancelled);
        Assert.Null(w.Eco.Find(mine)); Assert.Null(w.Eco.Find(yard));
        Assert.Equal(0, w.Eco.Load(Side.Hand));
        Assert.Equal(0, w.Eco.Load(Side.Collective)); // not carried over to the new holder
        Assert.Equal((5, 7), (w.Eco.Raw(Side.Hand), w.Eco.Refined(Side.Hand)));   // no refund
        Assert.Equal((1, 2), (w.Eco.Raw(Side.Collective), w.Eco.Refined(Side.Collective)));
        Assert.Empty(w.Events<FacilityTransferred>());
    }

    // C5: capture while an Mandate is queued at the old holder's yard: the order is cancelled (by the construction module), no refund; the
    // yard is the new holder's, idle. B5a, 2026-10-17: the old holder's finished regiment on the world is destroyed (section 5; it was untouched).
    [Fact]
    public void C5_QueuedOrderOfOldHolder_Cancelled_YardGoesIdleToNewHolder()
    {
        var w = new CtlWorld(construction: true);
        w.Hold(Emp, Side.Hand, 80);
        int yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        int army = w.Eco.AddObject(Side.Hand, "Tranches", Emp);                          // completed regiment of the old holder: destroyed (B5a)
        w.Eco.SetPools(Side.Hand, 0, 1000);
        w.Build!.GrantResearch(Side.Hand, "Mandate");
        w.Build.SubmitBuild(Side.Hand, Emp, YardFamily.Shipyard, "Mandate", 1);
        w.Run(1);
        var mandate = w.Eco.Objects.Single(o => o.Class == "Mandate").Id;
        Assert.NotNull(w.Build.ProjectAt(Emp, YardFamily.Shipyard));
        int refinedBefore = w.Eco.Refined(Side.Hand);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Null(w.Eco.Find(mandate));
        Assert.Null(w.Build.ProjectAt(Emp, YardFamily.Shipyard));
        Assert.Null(w.Eco.Find(army));
        Assert.Equal(army, Assert.Single(w.Events<UnitLostAtCapture>()).Object);
        Assert.True(w.Eco.Refined(Side.Hand) <= refinedBefore); // no refund
        Assert.Equal(Side.Collective, w.Eco.OwnerOf(w.Eco.Find(yard)!));
        Assert.True(w.Eco.Find(yard)!.Completed);
        Assert.False(w.Eco.Find(yard)!.InLine);
        Assert.Equal(0, w.Eco.Find(yard)!.Due);
        var c = Assert.Single(w.Events<Praedra.Core.Construction.ProjectCancelled>()); // one cancellation: 6.6 found nothing left
        Assert.Equal(("SystemLost", Emp), (c.Reason, c.System));
        Assert.Contains(mandate, c.Units.Items);
    }

    // C6: Hand-held, A = 70 (Hand support 30), no uprising, Collective captures: A unchanged, N = 0, no uprising
    [Fact]
    public void C6_SupportUnchanged_NewHolderSupport70_NoUprising()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 30);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(Emp));
        Assert.Equal(70, w.A(Emp));
        Assert.Equal(70, w.Eco.SupportOf(Emp, Side.Collective));
        Assert.Equal(0, w.S.Sup.RequiredGarrison(Emp));
        Assert.False(w.S.Sup.IsUprising(Emp));
        Assert.Empty(w.Events<UprisingStarted>());
        Assert.Empty(w.Events<SupportChanged>());
    }

    // C7: Hand-held, A = 20; Collective captures with 0 regiments: N = 4, UprisingStarted at once, inside the capture (B5a, 2026-10-17: was slot 6.1
    // of the same Step; section 4 step 2.7), arm draw rand(0..70). Captured in slot 5.2, the way M3's assault calls it.
    [Fact]
    public void C7_FreshlyCapturedHostileWorld_UprisingAtOnce()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80); // A = 20
        w.Stub.Expect(0, 70, 5);
        int drawsInCapture = -1;
        w.InAssaultSlot(ctx =>
        {
            int before = w.Stub.Requests.Count;
            Assert.True(w.Ctl.SetHolder(ctx, Emp, Side.Collective, CaptureCause.Assault));
            drawsInCapture = w.Stub.Requests.Count - before;
        });
        w.Run(1);
        Assert.Equal(1, drawsInCapture); // the uprising's arm draw is made by the capture (B5a section 7)
        Assert.Equal(20, w.A(Emp));
        Assert.Equal(4, SupportModule.RequiredGarrison(w.S.Sup.Params, 20, false, false)); // N before the uprising doubles it
        Assert.Equal(8, w.S.Sup.RequiredGarrison(Emp));
        var up = Assert.Single(w.Events<UprisingStarted>());
        Assert.Equal((Emp, Side.Collective, 1), (up.System, up.Holder, up.Day));
        Assert.Equal(1, w.Stub.Count(0, 70)); // the arm draw of timer 0x38d
        Assert.Equal(1 + 30 + 5, w.Game.State.Support.Systems[Emp].IncidentDue);
        // order in the Step: the capture event comes before the uprising
        Assert.True(w.Game.Events.ToList().FindIndex(e => e is SystemCaptured) < w.Game.Events.ToList().FindIndex(e => e is UprisingStarted));
        Assert.Equal(CaptureCause.Assault, Assert.Single(w.Events<SystemCaptured>()).Cause);
    }

    // C8: capture of a Hand system that was in uprising: flag, rebel side and timers cleared; UprisingEnded before the new holder is evaluated
    [Fact]
    public void C8_UprisingCleared_UprisingEndedBeforeNewHolderEvaluated()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80); // A = 20: the new holder will need a garrison of 4
        w.S.Uprising(Emp, Side.Hand, erosionDue: 12, incidentDue: 15);
        w.Stub.Expect(0, 70, 9);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        var ended = Assert.Single(w.Events<UprisingEnded>());
        Assert.Equal("Captured", ended.Reason);
        var evs = w.Game.Events.ToList();
        int iCap = evs.FindIndex(e => e is SystemCaptured), iEnd = evs.FindIndex(e => e is UprisingEnded), iNew = evs.FindIndex(e => e is UprisingStarted);
        Assert.True(iCap < iEnd && iEnd < iNew);
        var st = w.Game.State.Support.Systems[Emp]; // a fresh uprising of the new holder: new side, new timers
        Assert.True(st.Uprising);
        Assert.Equal(Side.Hand, st.Collective);
        Assert.Equal(31, st.ErosionDue); // not the old 12
        Assert.Equal(1 + 30 + 9, st.IncidentDue); // not the old 15
    }

    [Fact]
    public void C8b_UprisingCleared_NewHolderContent_LeavesNoFlagAndNoTimers()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 30); // A = 70: the Collective needs no garrison
        w.S.Uprising(Emp, Side.Hand, erosionDue: 12, incidentDue: 15);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal("Captured", Assert.Single(w.Events<UprisingEnded>()).Reason);
        Assert.False(w.S.Sup.IsUprising(Emp));
        Assert.Null(w.S.Sup.StateOf(Emp)); // idle entry pruned: no timer left
        Assert.Empty(w.Events<UprisingStarted>());
    }

    // C9: support reaches 100 for the Collective on a Hand-held system: the holder stays Hand
    [Fact]
    public void C9_SupportAt100_NeverFlipsControl()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 0); // A = 100
        w.Run(50);
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(Emp));
        w.Eco.SetCollectiveSupport(Emp, 0);
        w.Run(50);
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(Emp));
        Assert.Empty(w.Events<SystemCaptured>());
    }

    // C10: neutral system, A = 90, 1000 Steps: still neutral
    [Fact]
    public void C10_NeutralSystem_StaysNeutral_ThousandSteps()
    {
        var w = new CtlWorld();
        w.Fleets.SetOwner(Emp, null);
        w.Eco.SetCollectiveSupport(Emp, 90);
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
        w.Hold(ControlModule.CapitalSystem, Side.Hand, 80);
        var seen = new List<(string, bool)>();
        w.Ctl.ControlChangeAftermath += (c, capital) => seen.Add((c.System, capital));
        w.Capture(Side.Collective, ControlModule.CapitalSystem);
        w.Hold(Emp, Side.Hand, 80);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal(new[] { (ControlModule.CapitalSystem, true), (Emp, false) }, seen);
        Assert.Equal(20, w.A(ControlModule.CapitalSystem));
        Assert.Empty(w.Events<SupportChanged>());
    }

    // C12: blockade flag recomputed: a Collective fleet at the system is no longer a blockader after the Collective captures it
    [Fact]
    public void C12_BlockadeRecomputed_CollectiveFleetNoLongerBlockader()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        w.Fleets.AddFleet(Side.Collective, "Picket", new[] { "Dodger" }, Emp);
        w.Run(1);
        Assert.Equal(Side.Collective, w.Game.State.Blockades[Emp]);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.False(w.Game.State.Blockades.ContainsKey(Emp));
        var lifted = Assert.Single(w.Events<BlockadeLifted>());
        Assert.Equal((Emp, Side.Collective), (lifted.System, lifted.Blockader));
        // and it was lifted by the capture itself, before step 5 of that Step: right after the capture event
        var evs = w.Game.Events.ToList();
        Assert.True(evs.FindIndex(e => e is SystemCaptured) < evs.FindIndex(e => e is BlockadeLifted));
    }

    [Fact]
    public void C12b_BlockadeRecomputed_OldHoldersFleetBecomesBlockader()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        w.Fleets.AddFleet(Side.Hand, "Home", new[] { "Mandate" }, Emp);
        w.Run(1);
        Assert.Empty(w.Game.State.Blockades);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal(Side.Hand, w.Game.State.Blockades[Emp]);
        Assert.Single(w.Events<BlockadeStarted>());
    }

    // C13: pool check: Collective capture of 3 mines, 2 refineries: capacity rises by the five, Hand's falls by the same, same Step
    [Fact]
    public void C13_PoolsFollowTheFacilities_SameStep()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        for (int i = 0; i < 3; i++) w.S.W.Mine(Emp);
        for (int i = 0; i < 2; i++) w.S.W.Refinery(Emp);
        int shipyard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard); // maintenance 13 moves with it
        Assert.Equal((100, 0, 13, 0), (w.Eco.Capacity(Side.Hand), w.Eco.Capacity(Side.Collective), w.Eco.Load(Side.Hand), w.Eco.Load(Side.Collective)));
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal((0, 100, 0, 13), (w.Eco.Capacity(Side.Hand), w.Eco.Capacity(Side.Collective), w.Eco.Load(Side.Hand), w.Eco.Load(Side.Collective)));
        Assert.Equal(Side.Collective, w.Eco.OwnerOf(w.Eco.Find(shipyard)!));
    }

    // a refinery waiting in the old holder's raw line moves to the new holder's line in the same Step (slot 6.5, economy sheet)
    [Fact]
    public void RefineryInRawLine_MovesToNewHoldersLine()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        w.S.W.Mine(Emp);
        int r = w.S.W.Refinery(Emp);
        w.Run(1); // the refinery asks for raw and waits
        Assert.Contains(r, w.Eco.RawLine(Side.Hand));
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.DoesNotContain(r, w.Eco.RawLine(Side.Hand));
        Assert.Contains(r, w.Eco.RawLine(Side.Collective));
    }

    // C14: scorched_earth setting saved, loaded; default on for a new game
    [Fact]
    public void C14_ScorchedEarth_DefaultOn_SavedAndLoaded()
    {
        var w = new CtlWorld();
        Assert.True(w.Ctl.ScorchedEarth(Side.Hand));
        Assert.True(w.Ctl.ScorchedEarth(Side.Collective));
        string onHash = GameSave.StateHash(w.Game);
        w.Ctl.SetScorchedEarth(Side.Hand, false);
        Assert.NotEqual(onHash, GameSave.StateHash(w.Game)); // part of the hashed state

        var loaded = GameSave.Load(GameSave.Save(w.Game));
        Assert.False(loaded.State.Control.ScorchedEarth(Side.Hand));
        Assert.True(loaded.State.Control.ScorchedEarth(Side.Collective));
        Assert.Equal(GameSave.Save(w.Game), GameSave.Save(loaded));
        Assert.True(new GameState().Control.ScorchedEarth(Side.Hand));
    }

    // C15: human Scrap of a mine on its own system, switch off: accepted (economy sheet's refund)
    [Fact]
    public void C15_HumanScrap_SwitchOff_Accepted()
    {
        var w = new CtlWorld();
        w.Ctl.SetScorchedEarth(Side.Collective, false);
        w.Hold(Ally, Side.Collective, 80);
        int mine = w.S.W.Mine(Ally);
        w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Collective, mine));
        w.Run(1);
        Assert.Null(w.Eco.Find(mine));
        Assert.Equal(10, w.Eco.Refined(Side.Collective));
        Assert.Empty(w.Game.Events.OfType<OrderRejected>());
    }

    // C17: 6 mines/refineries (20 each) and a shipyard (20): refund = 6 x 10 + 10 = 70 refined
    [Fact]
    public void C17_ScrapExample_RefundIs70()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        var ids = new List<int>();
        for (int i = 0; i < 3; i++) { ids.Add(w.S.W.Mine(Emp)); ids.Add(w.S.W.Refinery(Emp)); }
        ids.Add(w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard));
        foreach (int id in ids) w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Hand, id));
        w.Run(1);
        Assert.Equal(70, w.Eco.Refined(Side.Hand));
        Assert.Equal(7, w.Events<ObjectScrapped>().Count);
    }

    // After a scrap, the enemy gains nothing from a capture (the scorched-earth example)
    [Fact]
    public void ScorchedWorld_GivesTheCapturerNothing()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        var ids = new List<int>();
        for (int i = 0; i < 3; i++) { ids.Add(w.S.W.Mine(Emp)); ids.Add(w.S.W.Refinery(Emp)); }
        foreach (int id in ids) w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Hand, id));
        w.Run(1);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal(0, w.Eco.Capacity(Side.Collective));
        Assert.Empty(w.Events<FacilityTransferred>());
    }

    // C18: capture twice in two Steps (A to B, B to A): each re-applies the rules; facility ids unchanged; events in order
    [Fact]
    public void C18_CaptureTwice_IdsUnchanged_EventsInOrder()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        int mine = w.S.W.Mine(Emp), yard = w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        w.Capture(Side.Hand, Emp);
        w.Run(1);
        var caps = w.Events<SystemCaptured>();
        Assert.Equal(new[] { (Side.Hand, Side.Collective), (Side.Collective, Side.Hand) }, caps.Select(c => (c.From!.Value, c.To!.Value)).ToArray());
        Assert.Equal(new[] { 0, 1 }, caps.Select(c => c.Day - 1).ToArray());
        var moved = w.Events<FacilityTransferred>();
        Assert.Equal(new[] { mine, yard, mine, yard }, moved.Select(m => m.Facility).ToArray());
        Assert.Equal(new Side?[] { Side.Collective, Side.Collective, Side.Hand, Side.Hand }, moved.Select(m => m.To).ToArray());
        Assert.Equal(Side.Hand, w.Eco.OwnerOf(w.Eco.Find(mine)!));
        // per capture: SystemCaptured first, then its facility events; the Collective held a hostile world with no garrison (A = 20), so its
        // uprising (slot 6.1) is ended by the second capture, last
        Assert.Equal("SystemCaptured,FacilityTransferred,FacilityTransferred,SystemCaptured,FacilityTransferred,FacilityTransferred,UprisingEnded",
            CtlWorld.Names(CaptureEvents(w)));
    }

    // C19: captures with the same inputs run twice: identical event lists. B5a, 2026-10-17: the capture does draw (section 7, "No random draws are
    // made by capture" is wrong): both captured worlds are hostile with no garrison, so each uprising starts at once with its arm draw.
    [Fact]
    public void C19_SameInputsTwice_IdenticalEvents_NoDraws()
    {
        List<GameEvent> Play(out int draws)
        {
            var w = new CtlWorld(stub: false, seed: 7);
            w.Hold(Emp, Side.Hand, 80);
            w.Hold(Ally, Side.Collective, 80);
            w.S.W.Mine(Emp); w.S.W.Refinery(Emp); w.Eco.AddFacility(Emp, FacilityKind.Lien150);
            w.Eco.AddFacility(Emp, FacilityKind.Mine, completed: false);
            w.S.Uprising(Emp, Side.Hand, erosionDue: 10, incidentDue: 12);
            w.Fleets.AddFleet(Side.Collective, "Picket", new[] { "Dodger" }, Emp);
            int d = 0;
            w.InAssaultSlot(ctx =>
            {
                var before = w.Game.Rng.Export();
                w.Ctl.SetHolder(ctx, Emp, Side.Collective, CaptureCause.Assault);
                w.Ctl.SetHolder(ctx, Ally, Side.Hand, CaptureCause.Assault);
                d = w.Game.Rng.Export() == before ? 0 : 1;
            });
            w.Run(3);
            draws = d;
            return w.Game.Events.ToList();
        }
        var a = Play(out int da); var b = Play(out int db);
        Assert.Equal(1, da); Assert.Equal(1, db);
        Assert.Equal(a, b);
        Assert.Contains(a, e => e is FacilityTransferred);
    }

    // SetHolder to the side that already holds the system changes nothing and raises nothing
    [Fact]
    public void SetHolder_SameHolder_IsANoOp()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        w.S.W.Mine(Emp);
        w.InAssaultSlot(ctx => Assert.False(w.Ctl.SetHolder(ctx, Emp, Side.Hand, CaptureCause.Assault)));
        w.Run(1);
        Assert.Empty(w.Events<SystemCaptured>());
        Assert.Empty(w.Events<FacilityTransferred>());
    }

    // DebugSetHolder: rejected unless enabled, for a system already held, and for an unknown system
    [Fact]
    public void DebugOrder_Rejections()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        w.Ctl.DebugOrdersEnabled = false;
        w.Capture(Side.Collective, Emp);
        w.Run(1);
        Assert.Equal(RejectReason.DebugDisabled, Assert.Single(w.Events<OrderRejected>()).Reason);
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(Emp));

        w.Ctl.DebugOrdersEnabled = true;
        w.Capture(Side.Hand, Emp);
        w.Ctl.SubmitDebugSetHolder(Side.Hand, "No Such Place", Side.Hand);
        w.Run(1);
        Assert.Equal(new[] { RejectReason.DebugDisabled, RejectReason.AlreadyHolder, RejectReason.UnknownSystem },
            w.Events<OrderRejected>().Select(r => r.Reason).ToArray());
    }

    [Fact]
    public void DebugOrder_SurvivesSaveAsAQueuedOrder()
    {
        var w = new CtlWorld();
        w.Hold(Emp, Side.Hand, 80);
        long seq = w.Ctl.SubmitDebugSetHolder(Side.Collective, Emp, Side.Collective);
        var json = GameSave.Save(w.Game);
        var loaded = GameSave.Load(json);
        var order = Assert.Single(loaded.Queue);
        Assert.Equal(new DebugSetHolderOrder(seq, Side.Collective, Emp, Side.Collective), order);
    }

    // ---------------------------------------------------------------- integration: owner change mid-run keeps economy, support and fleets consistent

    private static CtlWorld Scenario()
    {
        var w = new CtlWorld(stub: false, seed: 11);
        w.Hold(Emp, Side.Hand, 40);   // A = 60
        w.Hold(Ally, Side.Collective, 70);
        for (int i = 0; i < 2; i++) { w.S.W.Mine(Emp); w.S.W.Refinery(Emp); }
        w.S.W.Mine(Ally); w.S.W.Refinery(Ally);
        w.Eco.AddFacility(Emp, FacilityKind.OrbitalShipyard);
        w.S.Regiments(Emp, Side.Hand, SupWorld.ArmyOf(Side.Hand), 2);
        w.Fleets.AddFleet(Side.Collective, "Raiders", new[] { "Dodger" }, Emp);
        w.Fleets.AddFleet(Side.Hand, "Hammer", new[] { "Mandate" }, Ally);
        return w;
    }

    private static CtlWorld Reattach(Game loaded, bool construction = false)
    {
        // attach every module to the loaded game, the way a real load does
        var p = new EconomyParams { ScrapPeriod = 1_000_000 };
        var fleets = FleetModule.Attach(loaded, TestData.RealGalaxy, Praedra.Core.Tests.Fleets.World.Ships.Value, Praedra.Core.Tests.Fleets.World.Blockade);
        var eco = EconomyModule.Attach(loaded, TestData.RealGalaxy, EcoWorld.Slots.Value, EcoWorld.Costs.Value, fleets, p);
        eco.IncidentsEnabled = false;
        var sup = SupportModule.Attach(loaded, TestData.RealGalaxy, eco, fleets, SupWorld.Weights.Value);
        var build = construction ? ConstructionModule.Attach(loaded, fleets, eco, support: sup) : null;
        var ctl = ControlModule.Attach(loaded, TestData.RealGalaxy, fleets, eco, sup, build);
        ctl.Rule = ControlRule.AssaultOnly; // as Scenario()
        ctl.DebugOrdersEnabled = true;
        return new CtlWorld(loaded, ctl, eco, fleets, build);
    }

    [Fact]
    public void Integration_OwnerChangeMidRun_EconomySupportAndFleetsStayConsistent()
    {
        var w = Scenario();
        w.Run(40);
        int capBefore = w.Eco.Capacity(Side.Hand), allyCapBefore = w.Eco.Capacity(Side.Collective);
        Assert.Equal(100, capBefore);
        Assert.Equal(50, allyCapBefore);
        Assert.Equal(Side.Collective, w.Game.State.Blockades[Emp]); // economy: blockaded mines idle
        int aAtCapture = w.A(Emp);
        w.Capture(Side.Collective, Emp);
        w.Run(1);

        // fleets: the owner changed, the blockade flag follows, the old holder's fleet at Classis is not there
        Assert.Equal(Side.Collective, w.Fleets.OwnerOf(Emp));
        Assert.False(w.Game.State.Blockades.ContainsKey(Emp));
        // economy: the facilities are the Collective's, capacity moved, no negative pool
        Assert.Equal(0, w.Eco.Capacity(Side.Hand));
        Assert.Equal(150, w.Eco.Capacity(Side.Collective));
        foreach (var f in w.Eco.Objects.Where(o => o.System == Emp && o.Category == ObjectCategory.Facility))
            Assert.Equal(Side.Collective, w.Eco.OwnerOf(f));
        Assert.True(w.Eco.Raw(Side.Hand) >= 0 && w.Eco.Refined(Side.Hand) >= 0);
        // support: stored value untouched (60 Collective); the Hand's 2 regiments are not the holder's, so N = 0 here and nothing erupts
        Assert.Equal(aAtCapture, w.A(Emp)); // the 40 blockaded days may have moved it; the capture itself does not
        Assert.DoesNotContain(w.Events<SupportChanged>(), e => e.System == Emp && e.Day == 41);
        Assert.False(w.S.Sup.IsUprising(Emp));
        // the mines now produce for the Collective: run on and see Collective-side output from Classis
        w.Run(400);
        Assert.Contains(w.Events<ResourceProduced>(), e => e.System == Emp && e.Day > 41 && e.Controller == Side.Collective);
        Assert.DoesNotContain(w.Events<ResourceProduced>(), e => e.System == Emp && e.Day > 41 && e.Controller == Side.Hand);
        // every refinery stands in at most one line, on the line of its holder
        foreach (var side in new[] { Side.Hand, Side.Collective })
            foreach (int id in w.Eco.RawLine(side)) Assert.Equal(side, w.Eco.OwnerOf(w.Eco.Find(id)!));
    }

    [Fact]
    public void Integration_SaveLoadMidRun_CaptureAfterLoadMatchesAnUninterruptedGame()
    {
        var whole = Scenario(); whole.Run(40); whole.Capture(Side.Collective, Emp); whole.Run(260);

        var first = Scenario(); first.Run(40);
        var loaded = Reattach(GameSave.Load(GameSave.Save(first.Game)));
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded.Game));
        loaded.Capture(Side.Collective, Emp);
        loaded.Run(260);
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded.Game));
    }

    [Fact]
    public void Integration_SaveLoadAfterCapture_FutureIsTheSame()
    {
        var whole = Scenario(); whole.Run(40); whole.Capture(Side.Collective, Emp); whole.Run(10); whole.Capture(Side.Hand, Emp); whole.Run(250);

        var first = Scenario(); first.Run(40); first.Capture(Side.Collective, Emp); first.Run(10);
        Assert.Equal(Side.Collective, first.Ctl.HolderOf(Emp));
        var loaded = Reattach(GameSave.Load(GameSave.Save(first.Game)));
        Assert.Equal(Side.Collective, loaded.Ctl.HolderOf(Emp));
        Assert.Equal(GameSave.StateHash(first.Game), GameSave.StateHash(loaded.Game));
        loaded.Capture(Side.Hand, Emp);
        loaded.Run(250);
        Assert.Equal(GameSave.StateHash(whole.Game), GameSave.StateHash(loaded.Game));
    }

    // ---------------------------------------------------------------- integration with construction: a running yard project and an inbound delivery

    // v6 re-pin 2026-10-24: Far was Classis (one great-route leg, 7 days at speed 100, 5 at 80); on the spiral map Cambium takes
    // the same days from Charter over two legs (a free jump to Remedium, then a minor-route leg).
    private const string Home = "Charter", Far = "Cambium";

    private static CtlWorld BuildScenario()
    {
        var w = new CtlWorld(stub: false, seed: 21, construction: true);
        w.Hold(Home, Side.Hand, 100); w.Hold(Far, Side.Hand, 100); // A = 0 at Classis: a hostile world for a Collective holder
        w.Eco.AddFacility(Home, FacilityKind.ConstructionYard);
        w.Eco.AddFacility(Far, FacilityKind.OrbitalShipyard);
        w.Eco.SetPools(Side.Hand, 0, 2000);
        w.Build!.GrantResearch(Side.Hand, "Mandate");
        w.Build.SubmitBuild(Side.Hand, Home, YardFamily.Construction, "Mine", 1, Far);                // delivery will fly Charter to Classis
        w.Build.SubmitBuild(Side.Hand, Far, YardFamily.Shipyard, "Mandate", 1);        // running yard project at Classis
        w.Fleets.AddFleet(Side.Collective, "Picket", new[] { "Dodger" }, Far);                  // blockades Classis
        return w;
    }

    [Fact]
    public void Integration_CaptureWithRunningProjectAndInboundDelivery()
    {
        var w = BuildScenario();
        while (!w.Game.Events.OfType<DeliveryDeparted>().Any()) w.Run(1);
        var dep = w.Events<DeliveryDeparted>().Single();
        Assert.Equal(Far, dep.To);
        Assert.Equal(Side.Collective, w.Game.State.Blockades[Far]);
        var mandateProject = w.Build!.ProjectAt(Far, YardFamily.Shipyard);
        Assert.NotNull(mandateProject);
        var mandateUnits = mandateProject!.Units.ToList();
        int mineId = dep.Object;
        Assert.True(w.Game.Day < dep.DueDay, "the delivery is still flying");

        w.Capture(Side.Collective, Far);
        w.Run(1);

        // construction: the project is gone with its units, once, no refund; the delivery is still alive
        var cancelled = Assert.Single(w.Events<Praedra.Core.Construction.ProjectCancelled>());
        Assert.Equal(("SystemLost", Far, YardFamily.Shipyard), (cancelled.Reason, cancelled.System, cancelled.Family));
        Assert.Equal(mandateUnits, cancelled.Units.Items.ToList());
        Assert.Null(w.Build.ProjectAt(Far, YardFamily.Shipyard));
        Assert.All(mandateUnits, id => Assert.Null(w.Eco.Find(id)));
        Assert.Single(w.Build.Deliveries);
        Assert.Empty(w.Events<UnfinishedFacilityCancelled>()); // the flying mine is construction's, not a leftover
        Assert.NotNull(w.Eco.Find(mineId));
        // economy: the shipyard is the Collective's and idle
        var yard = w.Eco.Objects.Single(o => o.System == Far && o.Class == "Orbital Shipyard");
        Assert.Equal(Side.Collective, w.Eco.OwnerOf(yard));
        Assert.Equal(0, yard.Due); Assert.False(yard.InLine);
        Assert.DoesNotContain(yard.Id, w.Build.RefinedLine(Side.Hand));
        Assert.DoesNotContain(yard.Id, w.Build.RefinedLine(Side.Collective));
        // fleets: owner and blockade flag follow
        Assert.Equal(Side.Collective, w.Fleets.OwnerOf(Far));
        Assert.False(w.Game.State.Blockades.ContainsKey(Far));
        // support: stored value untouched; the Collective, holding a world with A = 0 and no garrison, rises at 6.1 of the same Step
        Assert.Equal(0, w.S.A(Far));
        Assert.Single(w.Events<UprisingStarted>());

        // the inbound delivery is held when it reaches the lost destination (LostDestination = Pause)
        w.Run(dep.DueDay - w.Game.Day + 3);
        Assert.Equal("DestinationLost", w.Events<DeliveryHeld>().Single().Reason);
        Assert.Empty(w.Events<DeliveryArrived>());
        Assert.False(w.Eco.Find(mineId)!.Completed);
        Assert.Single(w.Events<Praedra.Core.Construction.ProjectCancelled>()); // never cancelled twice
    }

    [Fact]
    public void Integration_ConstructionCaptureSaveLoadMidRun_FutureIsTheSame()
    {
        CtlWorld RunToCapture(CtlWorld w)
        {
            while (!w.Game.Events.OfType<DeliveryDeparted>().Any()) w.Run(1);
            w.Capture(Side.Collective, Far);
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
