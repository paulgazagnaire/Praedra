using Praedra.Core.AI;
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
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;

namespace Praedra.Core.Tests.Control;

/// <summary>
/// (B5a, 2026-10-17) Under <c>ControlRule = Derived</c> a world is taken by putting regiments on it, not by DebugSetHolder (refused). These helpers
/// replace the old debug capture in tests that only need "the side now holds the world": the regiments standing there become six Army regiments of
/// the side (six = the garrison need at support 0, so the capture itself starts no uprising and draws nothing). rule: m2-control-and-capture
/// §Who holds a system (B5a section 1).
/// </summary>
internal static class DerivedCapture
{
    public const int Regiments = 6;

    public static string ArmyOf(Side side) => side == Side.Collective ? "Militia" : "Tranches";

    /// <summary>Queue the capture for the next Step (a DebugSetTroops order).</summary>
    public static long SubmitCapture(this ControlModule c, Side side, string system) =>
        c.SubmitDebugSetTroops(side, system, side, ArmyOf(side), Regiments);

    /// <summary>Capture now, inside a Step (a hook).</summary>
    public static void CaptureNow(this ControlModule c, StepContext ctx, string system, Side side) =>
        c.DebugApplyTroops(ctx, system, side, ArmyOf(side), Regiments);
}

/// <summary>
/// A full <see cref="GameHost"/> on the real galaxy with the derived control rule (the DEFAULT), debug orders on, incidents off and autoscrap out of
/// reach. The Classis Sector is the stage. v6 re-pin 2026-10-24: on the spiral map it holds 9 populated inner-rim worlds (Grauburg, Classis,
/// Beryl, Fiscus, Vallum, Cinnabar, Altamesa, Marmor, Loess); Hiberna and Bina Major moved to other sectors, so Beryl and Altamesa take their parts.
/// </summary>
internal sealed class DerivedWorld
{
    public const string Corellia = "Classis", Drall = "Beryl", Poderis = "Altamesa", Vagran = "Loess", Selonia = "Marmor";
    public const string ImpArmy = "Tranches", RebArmy = "Militia";
    public const int Lando = 834, Piett = 898;

    public GameHost H { get; }
    public Game Game => H.Game;
    public ControlModule Ctl => H.Control;

    public DerivedWorld(ulong seed = 1, GameSettings? settings = null)
    {
        H = GameHost.New(seed, settings ?? Settings());
        H.Construction.RefuseOverdrawOrders = false; // B5b 2026-10-18 (as CtlWorld): these worlds have no mines (capacity 0); control, not upkeep, is the subject
    }

    public static GameSettings Settings(ControlRule rule = ControlRule.Derived, HarvesterParams? ds = null) => new()
    {
        IncidentsEnabled = false, DebugOrdersEnabled = true, ControlRule = rule, Economy = new EconomyParams { ScrapPeriod = 1_000_000 }, Harvester = ds,
    };

    /// <summary>Owner (null = neutral) and Collective support, set up between Steps.</summary>
    public void Hold(string system, Side? side, int collectiveSupport)
    {
        H.Fleets.SetOwner(system, side);
        H.Economy.SetCollectiveSupport(system, collectiveSupport);
    }

    public int A(string system) => H.Economy.SupportOf(system, Side.Collective);
    public List<int> Regiments(string system, Side side, int n) =>
        Enumerable.Range(0, n).Select(_ => H.Economy.AddObject(side, side == Side.Hand ? ImpArmy : RebArmy, system)).ToList();
    public void Step(int n = 1) { for (int i = 0; i < n; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();
    public List<SupportChanged> SectorSteps() => Events<SupportChanged>().Where(e => e.Cause == SupportCause.ControlChange).ToList();

    /// <summary>Run <paramref name="a"/> once in slot <paramref name="slot"/> of the next Step.</summary>
    public void InSlot(string slot, Action<StepContext> a)
    {
        bool done = false;
        Game.RegisterHook(slot, ctx => { if (!done) { done = true; a(ctx); } });
    }

    public CharacterRecord Character(int id, string system)
    {
        var c = H.Characters.Get(id) ?? H.Characters.Create(H.Characters.Data.Get(id), new StubRng());
        H.Characters.Place(id, system);
        return c;
    }
}

// B5a, who holds a system: tests B5a-1 to B5a-14 of specs/reverse/batches/B5a-who-holds-a-system.md, plus the harvester rules the Dev lead
// decided with it (HarvesterFallout Classic, the strike-2 revolt under derived control) and the AI's last-regiment guard.
public class B5aControlTests
{
    private const string C = DerivedWorld.Corellia, D = DerivedWorld.Drall, T = DerivedWorld.Poderis, V = DerivedWorld.Vagran;

    // B5a-1 (sections 1, 6): a neutral world with no regiments at Collective support 59; one support change of +1 makes it Collective in the same call,
    // cause Support; the sector step gives +1 to the Collective in every ever-held populated system of the sector, the world itself included.
    [Fact]
    public void B5a1_NeutralWorldJoinsAtSixty_SameCall_SectorPlusOne()
    {
        var w = new DerivedWorld();
        w.Hold(C, null, 59);
        w.Hold(D, Side.Hand, 30);   // ever held (held now)
        w.Hold(T, Side.Collective, 70); // ever held
        w.Hold(V, null, 50);          // never held: skipped
        w.InSlot("2", ctx => w.H.Support.ChangeSupport(ctx, C, Side.Collective, 1, SupportCause.Diplomacy));
        w.Step();
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal((C, (Side?)null, (Side?)Side.Collective, CaptureCause.Support), (cap.System, cap.From, cap.To, cap.Cause));
        var evs = w.Game.Events.ToList();
        int iSup = evs.FindIndex(e => e is SupportChanged s && s.System == C && s.Cause == SupportCause.Diplomacy);
        Assert.Equal(iSup + 1, evs.IndexOf(cap)); // inside the support change
        Assert.Equal(new[] { (T, 71), (D, 31), (C, 61) }, w.SectorSteps().Select(s => (s.System, s.New)).ToArray());
        Assert.Equal(50, w.A(V));
    }

    // B5a-2 (section 1): a Hand world with no regiments at Collective support 35: to 42 it goes neutral, to 60 it joins the Collective.
    [Fact]
    public void B5a2_HandWorldWithoutTroops_NeutralAt42_CollectiveAt60()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 35);
        w.Ctl.SubmitDebugSetSupport(Side.Hand, C, 42);
        w.Step();
        Assert.Null(w.Ctl.HolderOf(C));
        Assert.Equal((Side.Hand, (Side?)null, CaptureCause.Support), Assert.Single(w.Events<SystemCaptured>()) is var c1 ? (c1.From!.Value, c1.To, c1.Cause) : default);
        w.Ctl.SubmitDebugSetSupport(Side.Hand, C, 60);
        w.Step();
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        var c2 = w.Events<SystemCaptured>()[1];
        Assert.Equal(((Side?)null, (Side?)Side.Collective, CaptureCause.Support), (c2.From, c2.To, c2.Cause));
    }

    // B5a-3 (sections 1, 6): a Hand world with one Hand regiment at Collective support 100 stays Hand; when the regiment boards a ship the
    // world is Collective at once, and every ever-held populated system of the sector gains 10 Collective support (5 in a harvester sector).
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 5)]
    public void B5a3_LastRegimentBoards_WorldFlips_SectorPlusTen(bool harvesterSector, int gain)
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 30);
        w.Hold(D, Side.Hand, 30);
        int reg = w.Regiments(C, Side.Hand, 1)[0];
        int fleet = w.H.Fleets.AddFleet(Side.Hand, "Lift", new[] { "Mandate" }, C);
        w.Ctl.SubmitDebugSetSupport(Side.Hand, C, 100);
        w.Step();
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(C)); // troops hold a world at any support
        Assert.Empty(w.Events<SystemCaptured>());
        if (harvesterSector) w.H.Support.SetHarvesterSector("Classis Sector", true);
        w.H.Troops.SubmitLoad(Side.Hand, fleet, new[] { reg });
        w.Step();
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal((Side.Hand, (Side?)Side.Collective, CaptureCause.TroopsLeft), (cap.From!.Value, cap.To, cap.Cause));
        Assert.Equal(30 + gain, w.A(D));
        Assert.Contains(w.SectorSteps(), s => s.System == D && s.New - s.Old == gain);
        Assert.Equal(100, w.A(C)); // already at the top
    }

    // B5a-4 (sections 1, 6): a Hand world at Hand support 45 with one regiment; the regiment is destroyed: the world goes neutral and every
    // system of the sector gains 10 for the Collective. The world itself is in its sector: at Collective 65 it then joins the Collective by support
    // (a second flip, +1 sector step; no limit for a world coming from neutral).
    [Fact]
    public void B5a4_GarrisonDestroyed_Neutral_SectorPlusTen_ThenTheWorldJoins()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 55);
        w.Hold(D, Side.Hand, 20);
        int reg = w.Regiments(C, Side.Hand, 1)[0];
        w.Step();
        w.InSlot("6.3", ctx => // an uprising incident code 2 kills the last regiment, as the support module does it
        {
            w.H.Economy.RemoveObject(reg);
            ctx.Emit((i, _) => new RegimentDestroyed(i, ctx.Day + 1, reg, DerivedWorld.ImpArmy, C, Side.Hand));
        });
        w.Step();
        var caps = w.Events<SystemCaptured>();
        Assert.Equal(2, caps.Count);
        Assert.Equal((Side.Hand, (Side?)null, CaptureCause.TroopsLeft), (caps[0].From!.Value, caps[0].To, caps[0].Cause));
        Assert.Equal(((Side?)null, (Side?)Side.Collective, CaptureCause.Support), (caps[1].From, caps[1].To, caps[1].Cause));
        var steps = w.SectorSteps();
        Assert.Equal(new[] { (D, 10), (C, 10), (D, 1), (C, 1) }, steps.Select(s => (s.System, s.New - s.Old)).ToArray());
        Assert.Equal((66, 31), (w.A(C), w.A(D)));
    }

    // Puts n regiments of the side aboard enough Bulk Transports (6 troop slots each) of a new fleet at the system.
    private static (int Fleet, List<int> Regs) Transports(DerivedWorld w, Side side, string system, int n)
    {
        int ships = (n + 5) / 6;
        int fleet = w.H.Fleets.AddFleet(side, "Landing", Enumerable.Repeat("Barge", ships), system);
        var shipIds = w.Game.State.Fleets[fleet].Ships.ToList();
        var regs = new List<int>();
        for (int k = 0; k < n; k++)
        {
            int id = w.H.Economy.AddObject(side, side == Side.Hand ? DerivedWorld.ImpArmy : DerivedWorld.RebArmy, null);
            var o = w.H.Economy.Find(id)!;
            o.AboardShip = shipIds[k / 6]; o.System = null;
            regs.Add(id);
        }
        return (fleet, regs);
    }

    // B5a-5 (sections 3, 6): assault with 9 attackers and 0 defenders: 6 land, 3 stay aboard, the holder is the attacker, no sector change.
    [Fact]
    public void B5a5_Assault_NineAttackers_SixLand_HolderAttacker_NoSectorStep()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 30);
        w.Hold(D, Side.Hand, 30);
        var (fleet, regs) = Transports(w, Side.Collective, C, 9);
        w.H.Assault.SubmitAssault(Side.Collective, fleet, C);
        w.Step();
        var landed = Assert.Single(w.Events<Praedra.Core.Assault.AssaultLanded>());
        Assert.Equal(regs.Take(6), landed.Regiments.Items);
        Assert.All(regs.Skip(6), id => Assert.NotEqual(0, w.H.Economy.Find(id)!.AboardShip));
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        Assert.Equal(CaptureCause.Assault, Assert.Single(w.Events<SystemCaptured>()).Cause);
        Assert.Empty(w.SectorSteps());
        Assert.Equal((30, 30), (w.A(C), w.A(D)));
    }

    // B5a-6 (sections 3, 6): assault on a world held by support with no regiments: won at once (no duel), holder = attacker, no sector change.
    [Fact]
    public void B5a6_AssaultOnSupportHeldWorld_WonAtOnce_NoSectorStep()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 20);
        var (fleet, _) = Transports(w, Side.Collective, C, 1);
        w.H.Assault.SubmitAssault(Side.Collective, fleet, C);
        w.Step();
        var res = Assert.Single(w.Events<Praedra.Core.Assault.AssaultResolved>());
        Assert.Equal(("Won", 0, 0), (res.Result, res.D0, res.Duels));
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        Assert.Empty(w.SectorSteps());
    }

    // The assault's defenders are the holder's regiments only (B5a section 3; m3-assault step 0).
    [Fact]
    public void B5a_AssaultDefendersAreTheHoldersOnly()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 30);
        w.Regiments(C, Side.Hand, 2);
        var (fleet, _) = Transports(w, Side.Collective, C, 3);
        w.H.Assault.SubmitAssault(Side.Collective, fleet, C);
        w.Step();
        Assert.Equal(2, Assert.Single(w.Events<Praedra.Core.Assault.AssaultStarted>()).D0);
    }

    // B5a-7 (section 1): both sides' regiments on a Hand-held world: the holder is unchanged (and stays so through a save and load, QA Q46);
    // when the Hand ones die the holder is the Collective.
    [Fact]
    public void B5a7_BothSidesPresent_HolderUnchanged_ThenHandsDie_Collective()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 70); // Collective 70: support alone would make it Collective
        var imps = w.Regiments(C, Side.Hand, 2);
        w.Step();
        w.InSlot("2", _ => w.Regiments(C, Side.Collective, 1)); // scripted: a Collective regiment appears
        w.Step();
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(C));
        Assert.Equal(2, w.Game.State.Control.Contested![C]); // previous military side: the Hand

        var loaded = GameHost.Load(w.H.SaveWithSettings(), DerivedWorld.Settings());
        loaded.Game.Step();
        Assert.Equal(Side.Hand, loaded.Control.HolderOf(C));
        Assert.Empty(loaded.Game.Events.OfType<SystemCaptured>());

        w.InSlot("2", _ => { foreach (var id in imps) w.H.Economy.RemoveObject(id); });
        w.Step();
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        var cap = Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal(CaptureCause.Assault, cap.Cause); // troops to troops
        Assert.Empty(w.SectorSteps());
        Assert.Null(w.Game.State.Control.Contested);
    }

    // B5a-8 (section 5): the Hand takes the HQ world: the HQ is destroyed. Separately the HQ world with no Collective regiment drops to Collective
    // support 59: neutral, and the HQ is destroyed.
    [Fact]
    public void B5a8_HqDestroyedWhenTheHandTakesItsWorld_OrItGoesNeutral()
    {
        foreach (var neutral in new[] { false, true })
        {
            var w = new DerivedWorld();
            w.Hold(C, Side.Collective, 60);
            w.Game.State.Scenario.Hq.Status = HqStatus.AtRest;
            w.Game.State.Scenario.Hq.System = C;
            if (neutral) w.Ctl.SubmitDebugSetSupport(Side.Collective, C, 59);
            else w.Ctl.SubmitDebugSetTroops(Side.Hand, C, Side.Hand, DerivedWorld.ImpArmy, 1);
            w.Step();
            Assert.Equal(neutral ? null : Side.Hand, w.Ctl.HolderOf(C));
            var d = Assert.Single(w.Events<HqDestroyed>());
            // QA-11 (2026-10-18): the cause says how the world was lost; troops landing is a capture, support alone is LostSupport
            Assert.Equal((neutral ? FogModule.CauseLostSupport : FogModule.CauseCaptured, C), (d.Cause, d.System));
            Assert.Equal(HqStatus.Destroyed, w.Game.State.Scenario.Hq.Status);
        }
    }

    // B5a-9 (section 5): the Collective takes a world with a harvester shield and a Mordant Lien-150: shield destroyed, Mordant Lien-150 Collective. The same world going
    // neutral instead: shield destroyed, Mordant Lien-150 neutral.
    [Fact]
    public void B5a9_ShieldDestroyed_Kdy150ChangesSideOrGoesNeutral()
    {
        foreach (var neutral in new[] { false, true })
        {
            var w = new DerivedWorld();
            w.Hold(C, Side.Hand, 35);
            int shield = w.H.Economy.AddFacility(C, FacilityKind.HarvesterShield);
            int kdy = w.H.Economy.AddFacility(C, FacilityKind.Kdy150);
            if (neutral) w.Ctl.SubmitDebugSetSupport(Side.Hand, C, 50);
            else w.Ctl.SubmitDebugSetTroops(Side.Collective, C, Side.Collective, DerivedWorld.RebArmy, 1);
            w.Step();
            Side? to = neutral ? null : Side.Collective;
            Assert.Equal(to, w.Ctl.HolderOf(C));
            Assert.Null(w.H.Economy.Find(shield));
            Assert.Equal(shield, Assert.Single(w.Events<FacilityRemoved>()).Facility);
            Assert.Equal(to, w.H.Economy.OwnerOf(w.H.Economy.Find(kdy)!));
            var t = Assert.Single(w.Events<FacilityTransferred>());
            Assert.Equal((kdy, (Side?)Side.Hand, to), (t.Facility, t.From, t.To));
        }
    }

    // B5a-10 (section 5): a capture with an ISD queued at the world's shipyard: the ISD is destroyed, no refund; the yard and its queue are the new
    // holder's, empty. A busy queue elsewhere whose destination is the captured world keeps its target. (The "idle queue elsewhere pointing at the
    // world" has no counterpart here: an idle queue keeps no destination in the rebuild.)
    [Fact]
    public void B5a10_QueuedIsdDestroyed_NoRefund_YardEmptyForNewHolder()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 30); w.Hold(D, Side.Hand, 30);
        int yard = w.H.Economy.AddFacility(C, FacilityKind.OrbitalShipyard);
        w.H.Economy.AddFacility(D, FacilityKind.TrainingFacility);
        w.H.Economy.SetPools(Side.Hand, 0, 1000);
        w.H.Construction.GrantResearch(Side.Hand, "Mandate");
        w.H.Construction.SubmitBuild(Side.Hand, C, YardFamily.Shipyard, "Mandate", 1);
        w.H.Construction.SubmitBuild(Side.Hand, D, YardFamily.Training, DerivedWorld.ImpArmy, 2, C);
        w.Step();
        int isd = w.H.Economy.Objects.Single(o => o.Class == "Mandate").Id;
        int refined = w.H.Economy.Refined(Side.Hand);
        w.Ctl.SubmitDebugSetTroops(Side.Collective, C, Side.Collective, DerivedWorld.RebArmy, 1);
        w.Step();
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        Assert.Null(w.H.Economy.Find(isd));
        Assert.Null(w.H.Construction.ProjectAt(C, YardFamily.Shipyard));
        Assert.True(w.H.Economy.Refined(Side.Hand) <= refined); // no refund
        Assert.Equal(Side.Collective, w.H.Economy.OwnerOf(w.H.Economy.Find(yard)!));
        Assert.Equal(C, w.H.Construction.ProjectAt(D, YardFamily.Training)!.Destination);
    }

    // B5a-11 (section 5): the Hand takes a Collective world: the Collective's Wreckers there (not on a mission) are destroyed, the ones on a mission
    // survive; the Collective character there leaves and is not captured; a prisoner held there for the old side goes free.
    [Fact]
    public void B5a11_OldSideSpecialForcesDie_CharactersLeave_PrisonersFreed()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Collective, 70);
        w.Hold(T, Side.Collective, 70); // somewhere for the Collective to go
        int idle = w.H.Economy.AddObject(Side.Collective, "Wreckers", C);
        int working = w.H.Economy.AddObject(Side.Collective, "Wreckers", C);
        w.Game.State.Missions.Missions[9000] = new MissionGroup
        {
            Id = 9000, Kind = MissionKind.Sabotage, Side = Side.Collective, SpecialForces = new() { working }, Home = T, Target = C,
            Phase = MissionPhase.Working, Departed = true, Path = new() { C }, Due = 100_000,
        };
        var lando = w.Character(DerivedWorld.Lando, C);
        var piett = w.Character(DerivedWorld.Piett, C);
        w.H.Characters.Capture(null, piett, Side.Collective, C, CharacterCause.Debug);
        w.Ctl.SubmitDebugSetTroops(Side.Hand, C, Side.Hand, DerivedWorld.ImpArmy, 1);
        w.Step();
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(C));
        Assert.Null(w.H.Economy.Find(idle));
        Assert.NotNull(w.H.Economy.Find(working));
        Assert.Equal(idle, Assert.Single(w.Events<UnitLostAtCapture>()).Object);
        Assert.Equal(CharacterStatus.Free, lando.Status);
        Assert.NotEqual(C, lando.System);
        Assert.Contains(w.Events<CharacterDeparted>(), e => e.Character == DerivedWorld.Lando);
        Assert.Equal(CharacterStatus.Free, piett.Status);
        Assert.Contains(w.Events<CharacterFreed>(), e => e.Character == DerivedWorld.Piett);
        Assert.Equal(C, w.H.Characters.LocationOf(piett)); // of the new holder's side: stays
        // (B5a, 2026-10-18, Dev lead) the leaving character now uses Autoroute (QA-9, #69): step 3, the nearest Collective world, at character
        // travel speed, so he arrives when his trip ends instead of "the next day".
        Assert.Equal(T, lando.Transit!.Path[^1]);
        for (int d = 0; d < 400 && lando.Transit != null; d++) w.Step();
        Assert.Equal(T, lando.System);
    }

    // B5a-12 (section 6, as reworded after the RE lead's correction 2026-10-17: the limiter resets per tick, not per day). Two Collective worlds of
    // one sector go neutral by support in the same daily rule pass: only the first gives the +1 sector step (toward the Hand, the enemy of the
    // side that lost it); on the next day a third does. A player order that flips a fourth world by support gives its step (a fresh count per
    // order), and a fifth flipped in that day's rule pass gives its step too (the rule pass starts a fresh count).
    [Fact]
    public void B5a12_SupportOnlyStep_OncePerTickPerLosingSide()
    {
        const string S = DerivedWorld.Selonia;
        var w = new DerivedWorld();
        foreach (var s in new[] { C, D, T, V, S }) w.Hold(s, Side.Collective, 70);
        w.InSlot("6.2", ctx =>
        {
            w.H.Support.ChangeSupport(ctx, C, Side.Collective, -12, SupportCause.Other, dampen: false);
            w.H.Support.ChangeSupport(ctx, D, Side.Collective, -12, SupportCause.Other, dampen: false);
        });
        w.Step();
        Assert.Equal(new[] { C, D }, w.Events<SystemCaptured>().Where(e => e.To == null && e.Cause == CaptureCause.Support).Select(e => e.System).ToArray());
        // Corellia's step only (Beryl is still at 70 when it comes; its own flip in the same tick gives none). Steps come in name order:
        // Praedra names 2026-10-25: Altamesa (T), Beryl (D), Classis (C), Loess (V), Marmor (S).
        Assert.Equal(new[] { (T, 69), (D, 69), (C, 57), (V, 69), (S, 69) }, w.SectorSteps().Select(s => (s.System, s.New)).ToArray());
        Assert.Equal(57, w.A(D));
        w.InSlot("6.2", ctx => w.H.Support.ChangeSupport(ctx, T, Side.Collective, -12, SupportCause.Other, dampen: false));
        w.Step();
        Assert.Null(w.Ctl.HolderOf(T));
        Assert.Equal(new[] { (T, 56), (D, 56), (C, 56), (V, 68), (S, 68) }, w.SectorSteps().Skip(5).Select(s => (s.System, s.New)).ToArray());
        int before = w.SectorSteps().Count;
        w.Ctl.SubmitDebugSetSupport(Side.Collective, V, 58);
        w.InSlot("6.2", ctx => w.H.Support.ChangeSupport(ctx, S, Side.Collective, -12, SupportCause.Other, dampen: false));
        w.Step();
        Assert.Equal(new[] { V, S }, w.Events<SystemCaptured>().Skip(3).Select(e => e.System).ToArray());
        Assert.Equal(2, w.SectorSteps().Skip(before).Count(e => e.System == C)); // two steps on day 3: the order's and the rule pass's
    }

    // B5a-13 (section 8): scrapping a mine of a blockaded system and a regiment on a system in uprising is accepted, refund floor(cost / 2); the HQ,
    // a character and a special force cannot be scrapped (the HQ and characters are not registry objects: UnknownObject; a special force: CannotScrap).
    [Fact]
    public void B5a13_ScrapUnderBlockadeAndUprising_Accepted_HqCharacterSpecialForceRefused()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 80);
        w.Hold(D, Side.Hand, 30);
        int mine = w.H.Economy.AddFacility(C, FacilityKind.Mine);
        w.H.Fleets.AddFleet(Side.Collective, "Blockade", new[] { "Dodger" }, C);
        int reg = w.Regiments(D, Side.Hand, 2)[0];
        int sf = w.H.Economy.AddObject(Side.Hand, "Closers", D);
        w.Step();
        Assert.True(w.H.Fleets.IsBlockadedClassic(C));
        w.H.Support.StartUprisingNow(new StepContext(w.Game, w.Game.Day), D);
        Assert.True(w.H.Support.IsUprising(D));
        int refined = w.H.Economy.Refined(Side.Hand);
        var piett = w.Character(DerivedWorld.Piett, D);
        foreach (var id in new[] { mine, reg, sf, piett.Id }) w.Game.Submit(seq => new ScrapObjectOrder(seq, Side.Hand, id));
        w.Step();
        var scrapped = w.Events<ObjectScrapped>();
        Assert.Equal(new[] { (mine, 10), (reg, w.H.Economy.Costs.Get(DerivedWorld.ImpArmy).Cost / 2) }, scrapped.Select(s => (s.Object, s.Refund)).ToArray());
        Assert.Equal(new[] { RejectReason.CannotScrap, RejectReason.UnknownObject }, w.Events<OrderRejected>().Select(r => r.Reason).ToArray());
        Assert.NotNull(w.H.Economy.Find(sf));
        Assert.True(w.H.Economy.Refined(Side.Hand) >= refined + 10);
    }

    // B5a-14 (section 9): after NewGame.Create, the control recompute on every system changes no holder (all presets, 16 seeds).
    [Fact]
    public void B5a14_NewGame_RecomputeChangesNoHolder()
    {
        foreach (var preset in Enum.GetValues<StartPreset>())
            for (int seed = 1; seed <= 16; seed++)
            {
                // The deal does not depend on the control rule: deal under AssaultOnly (no refresh), then run the Derived refresh by hand.
                var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = preset, Settings = new GameSettings { ControlRule = ControlRule.AssaultOnly } });
                host.Control.Rule = ControlRule.Derived;
                var changed = host.Control.SetupNewGame();
                Assert.True(changed.Count == 0, $"{preset} seed {seed}: {string.Join(", ", changed)}");
            }
    }

    // The harvester's second strike in a sector under derived control (m5-death-star §2 effect 7, New, B5a section 9): the support change to 100
    // alone turns a neutral world and an ungarrisoned Hand world Collective; a garrisoned Hand world stays Hand and riots.
    [Fact]
    public void B5a_HarvesterRevolt_DerivedControl()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Hand, 30); w.Hold(D, null, 50); w.Hold(T, Side.Hand, 30);
        w.Regiments(T, Side.Hand, 1);
        w.Game.State.Harvester.StrikesInSector["Classis Sector"] = 1;
        w.InSlot("5.1", ctx => w.H.Harvester.GetType().GetMethod("SectorReaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(w.H.Harvester, new object[] { ctx, ctx.Day + 1, DerivedWorld.Vagran }));
        w.Step();
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(C));
        Assert.Equal(Side.Collective, w.Ctl.HolderOf(D));
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(T));
        Assert.True(w.H.Support.IsUprising(T));
    }

    // HarvesterFallout = Classic (; B5c-11's case): a planet destroyed by the Hand costs the Hand 10 on every core world of the galaxy, 2 on a
    // rim world where its support is below 90 (a rim world at 95 is unchanged, at 80 it goes to 78); never-held worlds are unchanged.
    [Fact]
    public void B5a_HarvesterFalloutClassic_GalaxyWide()
    {
        var w = new DerivedWorld(settings: DerivedWorld.Settings(ds: new HarvesterParams { Fallout = HarvesterFallout.Classic }));
        const string rimHigh = "Drift Harbour", rimLow = "The Thaw", coreFar = "Charter", never = "Navalia";
        w.Hold(coreFar, Side.Hand, 10); w.Regiments(coreFar, Side.Hand, 1);
        w.Hold(rimHigh, Side.Hand, 5); w.Hold(rimLow, Side.Hand, 20);
        w.Hold(never, null, 50);
        w.InSlot("5.1", ctx => w.H.Harvester.GetType().GetMethod("SectorReaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(w.H.Harvester, new object[] { ctx, ctx.Day + 1, DerivedWorld.Vagran }));
        w.Step();
        Assert.Equal(20, w.A(coreFar));
        Assert.Equal(5, w.A(rimHigh));
        Assert.Equal(22, w.A(rimLow));
        Assert.Equal(50, w.A(never));
        Assert.Empty(w.Events<SectorStruck>());
    }

    // B5c-11 (corrected 2026-10-18): in the harvester's own sector the Classic fallout is halved by the damping (a Hand loss raises the Collective
    // share): a core world -5 instead of -10, a rim world at Hand support 80 goes to 79 instead of 78; full elsewhere.
    [Fact]
    public void B5c11_HarvesterFalloutClassic_HalvedInTheHarvesterSector()
    {
        var w = new DerivedWorld(settings: DerivedWorld.Settings(ds: new HarvesterParams { Fallout = HarvesterFallout.Classic }));
        const string rimInSector = "The Thaw", rimElsewhere = "Drift Harbour", coreElsewhere = "Charter";
        const string coreInSector = "Hiberna"; // v6 re-pin 2026-10-24: D (Beryl) is an inner-rim world now; Hiberna is still core (flags are per system)
        w.Hold(coreInSector, Side.Hand, 10); w.Regiments(coreInSector, Side.Hand, 1); // core, in the flagged sector
        w.Hold(coreElsewhere, Side.Hand, 10); w.Regiments(coreElsewhere, Side.Hand, 1);
        w.Hold(rimInSector, Side.Hand, 20); w.Hold(rimElsewhere, Side.Hand, 20);
        w.InSlot("5.1", ctx =>
        {
            w.H.Support.SetHarvesterSystem(coreInSector, true); w.H.Support.SetHarvesterSystem(rimInSector, true); // the harvester's sector (flags per system)
            w.H.Harvester.GetType().GetMethod("SectorReaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(w.H.Harvester, new object[] { ctx, ctx.Day + 1, DerivedWorld.Vagran });
        });
        w.Step();
        Assert.Equal((15, 20), (w.A(coreInSector), w.A(coreElsewhere)));
        Assert.Equal((21, 22), (w.A(rimInSector), w.A(rimElsewhere)));
    }

    // B5a section 5 (Dev lead 2026-10-18): the old side's squadrons based at a world that changes hands leave for the nearest system their side holds
    // (one squadron-only fleet, flying as MoveSquadrons would); an unfinished squadron stays.
    [Fact]
    public void B5a_OldSideSquadronsLeave_UnfinishedStay()
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Collective, 50); w.Regiments(C, Side.Collective, 1);
        w.Hold(T, Side.Collective, 80);
        int based = w.H.Economy.AddObject(Side.Collective, "Swift", C);
        int unfinished = w.H.Economy.AddObject(Side.Collective, "Swift", C, completed: false);
        w.InSlot("5.2", ctx => w.Ctl.DebugApplyTroops(ctx, C, Side.Hand, DerivedWorld.ImpArmy, 6));
        w.Step();
        Assert.Equal(Side.Hand, w.Ctl.HolderOf(C));
        var created = Assert.Single(w.Events<FleetCreated>(), e => e.Cause == FleetCreateCause.Squadrons);
        var fleet = w.Game.State.Fleets[created.Fleet];
        Assert.Equal((Side.Collective, C, T), (fleet.Side, created.System, fleet.Transit!.Path[^1]));
        Assert.Equal(new[] { based }, fleet.Squadrons.ToArray());
        Assert.Null(w.H.Economy.Find(based)!.System);
        Assert.Equal(C, w.H.Economy.Find(unfinished)!.System);
    }

    // B5a section 5 (Dev lead 2026-10-18, AI trainer crash): the squadron fleet takes off at the capture (on the lane, PreviousSystem set, a
    // FleetDeparted) and arrives at its held system; a capture between step 2 and step 4 no longer crashes the forced-stop step.
    [Theory]
    [InlineData("3.1")]
    [InlineData("5.2")]
    [InlineData("6.2")]
    public void B5a_SquadronFleetDepartsAtCapture_AndArrives(string slot)
    {
        var w = new DerivedWorld();
        w.Hold(C, Side.Collective, 50); w.Regiments(C, Side.Collective, 1);
        w.Hold(T, Side.Collective, 80);
        int based = w.H.Economy.AddObject(Side.Collective, "Swift", C);
        w.InSlot(slot, ctx => { if (w.Ctl.HolderOf(C) == Side.Collective) w.Ctl.DebugApplyTroops(ctx, C, Side.Hand, DerivedWorld.ImpArmy, 6); });
        w.Step();
        var created = Assert.Single(w.Events<FleetCreated>(), e => e.Cause == FleetCreateCause.Squadrons);
        var fleet = w.Game.State.Fleets[created.Fleet];
        Assert.Null(fleet.At);
        Assert.Equal(C, fleet.PreviousSystem);
        Assert.Single(w.Events<FleetDeparted>(), e => e.Fleet == created.Fleet && e.From == C);
        Assert.True(fleet.Transit!.DueDay > w.Game.Day - 1); // due on a later Step's step 3, never in the past
        for (int i = 0; i < 400 && fleet.Transit != null; i++) w.Step();
        Assert.Null(fleet.Transit);
        Assert.Equal(T, fleet.At);
        Assert.Contains(based, fleet.Squadrons);
    }

    // B6b §3 (Dev lead 2026-10-18): the AI's garrison floor. Wish below 70 support: min((79 - s) / 10, 6), doubled in an uprising; 9 on the special
    // world; the target is the largest of the wish, the base and the rule's need. B6b-3, B6b-5 and B6b-7.
    [Fact]
    public void B6b_AiGarrisonFloor_WishBaseAndNoMoveBelowIt()
    {
        var w = new DerivedWorld();
        var ai = new SimpleAi(w.H, Side.Collective, new SimpleAiParams { ClassicGarrisonFloor = true }); // the floor is DEFAULT off with Derived (2026-10-18): on explicitly
        int Target(string sys, int support, bool uprising = false)
        {
            w.Hold(sys, Side.Collective, support);
            if (uprising) w.H.Support.StartUprisingNow(new StepContext(w.Game, w.Game.Day), sys);
            return ai.GarrisonTarget(sys);
        }
        Assert.Equal(0, Target(C, 75));
        Assert.Equal(1, Target(D, 65));
        Assert.Equal(2, Target(T, 55));
        Assert.Equal(4, Target(V, 30));
        Assert.Equal(8, Target(DerivedWorld.Selonia, 30, uprising: true));
        Assert.Equal(12, Target("Magnetite III", 5, uprising: true)); // B6b-4: min(7, 6) x 2
        var hq = w.H.HqSystem ?? "The Shoal";
        if (w.H.HqSystem == null) w.H.Game.State.Scenario.HqSystem = hq;
        Assert.Equal(9, Target(hq, 90));                       // B6b-5: the special world
        // QA-10: while the HQ is on the way, the special base is at its destination (regiments are ordered there before it arrives)
        var state = w.Game.State.Scenario.Hq;
        state.Status = HqStatus.InTransit; state.From = hq; state.System = null; state.Path = new List<string> { hq, C };
        Assert.Equal(9, Target(C, 90));
        state.Place(hq);
        // B6b-7: a world at support 55 with 2 regiments and target 2: none is spare; a third is
        w.Regiments(T, Side.Collective, 2);
        Assert.Empty(ai.SpareRegimentIds(T));
        w.Regiments(T, Side.Collective, 1);
        Assert.Single(ai.SpareRegimentIds(T));
        // off: the rule's need and the B5a guard (1 below 60)
        var old = new SimpleAi(w.H, Side.Collective, new SimpleAiParams { ClassicGarrisonFloor = false });
        Assert.Equal((0, 1), (old.GarrisonTarget(D), old.GarrisonTarget(T)));
    }
}
