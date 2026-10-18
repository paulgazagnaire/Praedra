using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Support;

namespace Rebellion.Core.Control;

/// <summary>What a capture hook is told. Old and New are null for neutral.</summary>
public sealed record CaptureInfo(StepContext Ctx, string System, Side? Old, Side? New, CaptureCause Cause);

/// <summary>
/// How control of a system is decided. Derived (1998, DEFAULT in Classic and New, Dev lead 2026-10-17): the holder is worked out from the regiments
/// standing on the planet and, when nobody's stand there, from support (60 or more joins a side). AssaultOnly (the rebuild before B5a): support
/// never changes control, only a won assault, the Death Star's revolt and the debug order do. rule: m2-control-and-capture §How control changes
/// (B5a section 10, decision 1).
/// </summary>
public enum ControlRule { Derived, AssaultOnly }

/// <summary>
/// Who holds a system and what happens when that changes. The holder lives in <see cref="GameState.SystemOwners"/> (a system not listed is
/// neutral). Under <see cref="ControlRule.Derived"/> there is no "set holder" call: <see cref="RecomputeControl"/> works the holder out after
/// every support change of a system (synchronous, from <see cref="SupportModule.ChangeSupport"/>) and after every change in the regiments
/// standing on it (seen after the orders and after every slot of the same Step, <see cref="Game.RegisterSettle"/>). A changed holder runs the
/// flip (B5a sections 4 and 5), then the queued aftermath (character loyalty, the sector step: section 6). Holds data and settings only; the
/// state lives in <see cref="GameState"/>. After <see cref="GameSave.Load"/>, attach again to the loaded game. Attach after the fleet, economy,
/// support and construction modules. rule: m2-control-and-capture (B5a, 2026-10-17).
/// </summary>
public sealed class ControlModule
{
    /// <summary>The capital system, for the capital hook only (B5a section 6.3: Coruscant, system id 0x109). Name from the planets file.</summary>
    public const string CapitalSystem = "Coruscant";

    /// <summary>
    /// A world with no regiments joins the side whose support there is at least this (GNPRTB 7760 = 60 in every column, [DAT-own]); between the two
    /// thresholds it is neutral. rule: m2-control-and-capture §How control changes (B5a section 1, <c>0x55a080</c>).
    /// </summary>
    public const int JoinSupport = 60;

    // ---- the sector step after a flip (B5a section 6; SDPRTB values equal in every column, [DAT-own]). rule: m2-control-and-capture §After the flip
    /// <summary>Support alone (no regiments before or after): per side involved (SDPRTB 5152 = 1), threshold 5153 = 100.</summary>
    public const int SectorStepSupportPerSide = 1, SectorStepSupportThreshold = 100;
    /// <summary>None to troops (5184 = 0, 5185 = 101): nothing.</summary>
    public const int SectorStepTroopsArrive = 0;
    /// <summary>Troops to none (5186 = 10, 5187 = 101): the holder's regiments left or died and support handed the world over.</summary>
    public const int SectorStepTroopsLeft = 10, SectorStepAlwaysThreshold = 101;
    /// <summary>Troops to troops (5188 = 0, 5189 = 101): nothing.</summary>
    public const int SectorStepAssault = 0;

    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly SupportModule _support;
    private readonly ConstructionModule? _construction;

    /// <summary>The control rule. DEFAULT Derived (1998) in both rulesets. Not part of the save: it is configuration.</summary>
    public ControlRule Rule { get; set; } = ControlRule.Derived;

    /// <summary>
    /// The debug orders (DebugSetHolder under AssaultOnly; DebugSetSupport and DebugSetTroops under both) are accepted only when this is on.
    /// DEFAULT off (a player must never be able to capture by order). Not part of the save: it is configuration.
    /// </summary>
    public bool DebugOrdersEnabled { get; set; }

    /// <summary>
    /// The capital hook (B5a section 6.3): on every holder change, with true when the system is <see cref="CapitalSystem"/>. The Coruscant
    /// amounts are read (SDPRTB 5154: 10 if the Alliance is involved plus 5 if the Empire is; 5155: 100 / 90) but its scope (<c>0x55e010</c>)
    /// and the extra test <c>0x5180e0</c> are not, so DEFAULT: nothing subscribes and no support changes. Open question 4.
    /// </summary>
    public event Action<CaptureInfo, bool>? ControlChangeAftermath;

    /// <summary>
    /// Raised inside every flip, after the uprising check and the blockade (the queued object events of the 1998 flip). The fog module listens: the
    /// loser's LossNotice and the Rebel HQ, destroyed when its world goes to the Empire or neutral. rule: m5-fog-and-hq §F2, §F7; B5a section 5.
    /// </summary>
    public event Action<CaptureInfo>? HolderChanged;

    /// <summary>
    /// The characters at a world that changed hands (B5a section 5): prisoners held there for the old holder go free, and characters not of the new
    /// holder leave (never captured, never harmed). Given (ctx, system, old, new). Null = nothing (no character module). The character module sets it.
    /// </summary>
    public Action<StepContext, string, Side?, Side?>? CharactersAtCapture { get; set; }

    private ControlModule(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ConstructionModule? construction)
    {
        _game = game; _galaxy = galaxy; _fleets = fleets; _eco = eco; _support = support; _construction = construction;
    }

    /// <summary>
    /// Register the debug order handlers and the regiment watch on <paramref name="game"/>, and hook the support module's ChangeSupport. Attach
    /// after the fleet, economy, support and (if used) construction modules; the construction module is optional.
    /// </summary>
    public static ControlModule Attach(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support,
        ConstructionModule? construction = null)
    {
        var m = new ControlModule(game, galaxy, fleets, eco, support, construction);
        game.RegisterOrderHandler<DebugSetHolderOrder>(m.HandleDebugSetHolder);
        game.RegisterOrderHandler<DebugSetSupportOrder>(m.HandleDebugSetSupport);
        game.RegisterOrderHandler<DebugSetTroopsOrder>(m.HandleDebugSetTroops);
        game.RegisterSettle(m.BeginStep, m.Settle);
        game.RegisterTickStart(_ => { m._stepUsedAlliance = false; m._stepUsedEmpire = false; });
        support.AfterSupportChange = m.OnSupportChanged;
        return m;
    }

    private ControlState C => _game.State.Control;
    private EconomyState E => _game.State.Economy;
    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

    // ================================================================== queries and settings

    /// <summary>The holder of a system, null = neutral. rule: m2-control-and-capture §Control (Holder).</summary>
    public Side? HolderOf(string system) => _fleets.OwnerOf(system);

    /// <summary>The scorched-earth switch of a side (read by the M6 AI only). rule: m2-control-and-capture §Scorched earth.</summary>
    public bool ScorchedEarth(Side side) => C.ScorchedEarth(side);

    /// <summary>Setup only: set the switch of a side. Saved with the game.</summary>
    public void SetScorchedEarth(Side side, bool on) => C.SetScorchedEarth(side, on);

    /// <summary>
    /// Was the system ever held (NeverBeenControlled clear)? A held system is; a neutral one only if it lost a holder. rule: B5a section 4 step 2.3.
    /// </summary>
    public bool EverHeld(string system) => HolderOf(system) != null || (C.EverHeld?.Contains(system) ?? false);

    /// <summary>
    /// The side a world with no regiments belongs to: Alliance at Alliance support 60 or more, Empire at 40 or less, else neutral (null). An
    /// unpopulated world is neutral. rule: m2-control-and-capture §How control changes (B5a section 1, S(L) <c>0x55a080</c>, GNPRTB 7760).
    /// </summary>
    public Side? SupportSide(string system)
    {
        if (!_eco.SlotsOf(system).Populated) return null;
        int a = _eco.SupportOf(system, Side.Alliance);
        if (a >= JoinSupport) return Side.Alliance;
        if (100 - a >= JoinSupport) return Side.Empire;
        return null;
    }

    /// <summary>
    /// Can a completed facility of this class serve the side? The class's side flags in the costs file ([DAT-own] MANFACSD, PROFACSD, DEFFACSD):
    /// the Alliance needs the Alliance flag, the Empire the Empire flag, neutral (null) needs both (<c>0x4f27d0</c>). So the Death Star Shield
    /// (Empire only) dies when the Alliance takes its world or the world goes neutral; every other class serves both. rule: B5a section 5.
    /// </summary>
    public bool ServesSide(string facilityClass, Side? side)
    {
        var row = _eco.Costs.Get(facilityClass);
        return side switch { Side.Alliance => row.Alliance, Side.Empire => row.Empire, _ => row.Alliance && row.Empire };
    }

    // ================================================================== the regiment watch (B5a section 2)

    // Bits per system of the Usable regiments standing on the planet: 1 = an Alliance one, 2 = an Empire one. "Usable" = finished and not in
    // transit or aboard: Completed, on the system, not aboard a ship (B5a section 1: 0x4f7b80; regiments aboard ships are children of the ship).
    private const int AllianceBit = 1, EmpireBit = 2;
    private Dictionary<string, int> _presence = new(StringComparer.Ordinal); // lookup only; the regiments as control last saw them. Not saved.
    // The limiter of the support-only sector step (0x518040): one per losing side per tick; reset before each player order and at the start of the
    // day's rule pass (Game.RegisterTickStart). Not saved (a save falls between Steps, and each Step starts a fresh tick).
    // rule: m2-control-and-capture §After the flip (B5a section 6; RE lead correction 2026-10-17: per tick, not per day)
    private bool _stepUsedAlliance, _stepUsedEmpire;

    private Dictionary<string, int> PresenceAll()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only, never iterated unsorted
        foreach (var o in E.Objects.Values)
            if (o.Category == ObjectCategory.Regiment && o.Completed && o.AboardShip == 0 && o.System is string s && o.Side is Side side)
                d[s] = d.GetValueOrDefault(s) | (side == Side.Alliance ? AllianceBit : EmpireBit);
        return d;
    }

    private int PresenceAt(string system)
    {
        int bits = 0;
        foreach (var o in E.Objects.Values)
            if (o.Category == ObjectCategory.Regiment && o.Completed && o.AboardShip == 0 && o.System == system && o.Side is Side side)
                bits |= side == Side.Alliance ? AllianceBit : EmpireBit;
        return bits;
    }

    private void BeginStep(StepContext ctx)
    {
        if (Rule == ControlRule.Derived) _presence = PresenceAll();
    }

    // After the orders and after every slot: every system whose standing regiments changed is recomputed, in system name order (the 1998 game
    // queues the recompute from the regiment hook 0x508660 and runs it the same day; B5a section 2, inferred same day).
    private void Settle(StepContext ctx)
    {
        if (Rule != ControlRule.Derived) return;
        var now = PresenceAll();
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kv in now) if (_presence.GetValueOrDefault(kv.Key) != kv.Value) changed.Add(kv.Key);
        foreach (var kv in _presence) if (!now.ContainsKey(kv.Key) && kv.Value != 0) changed.Add(kv.Key);
        if (changed.Count == 0) return;
        foreach (var sys in changed) Recompute(ctx, sys, now.GetValueOrDefault(sys));
        _presence = PresenceAll(); // a flip may have destroyed regiments
    }

    private void OnSupportChanged(StepContext ctx, string system)
    {
        if (Rule == ControlRule.Derived) RecomputeControl(ctx, system);
    }

    // ================================================================== RecomputeControl (B5a section 1)

    /// <summary>
    /// Work out who holds <paramref name="system"/> now and run the flip if that changed: the military side from the Usable regiments standing on
    /// the planet (one side's only: that side; none: none; both: the previous military side), then holder = military side, or the support side
    /// when there is none. Does nothing under <see cref="ControlRule.AssaultOnly"/> or on a destroyed system. Returns true if the holder changed.
    /// rule: m2-control-and-capture §How control changes (B5a section 1, <c>0x50b610</c>).
    /// </summary>
    public bool RecomputeControl(StepContext ctx, string system)
    {
        if (Rule != ControlRule.Derived) return false;
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        return Recompute(ctx, system, PresenceAt(system));
    }

    private static int SideNumber(Side? s) => s switch { Side.Alliance => 1, Side.Empire => 2, _ => 0 };
    private static Side? FromNumber(int n) => n switch { 1 => Side.Alliance, 2 => Side.Empire, _ => null };
    private static int SingleMilitary(int bits) => bits == AllianceBit ? 1 : bits == EmpireBit ? 2 : 0;

    private int PreviousMilitary(string system) =>
        C.Contested != null && C.Contested.TryGetValue(system, out var v) ? v : SingleMilitary(_presence.GetValueOrDefault(system));

    private bool Recompute(StepContext ctx, string system, int bits)
    {
        if (_eco.IsDestroyed(system)) { _presence[system] = bits; return false; } // rule: m5-death-star §2 effect 5: a destroyed system cannot be held
        int prev = PreviousMilitary(system);
        int mil = bits == (AllianceBit | EmpireBit) ? prev : SingleMilitary(bits); // both present: unchanged
        if (bits == (AllianceBit | EmpireBit)) { C.Contested ??= new SortedDictionary<string, int>(StringComparer.Ordinal); C.Contested[system] = mil; }
        else if (C.Contested != null) { C.Contested.Remove(system); if (C.Contested.Count == 0) C.Contested = null; }
        _presence[system] = bits;
        Side? holder = mil != 0 ? FromNumber(mil) : SupportSide(system);
        Side? old = HolderOf(system);
        if (holder == old) return false;
        var cause = mil != 0 ? CaptureCause.Assault : prev != 0 ? CaptureCause.TroopsLeft : CaptureCause.Support;
        Flip(ctx, system, old, holder, cause, prev, mil);
        Drain(ctx);
        return true;
    }

    // ================================================================== setup (B5a section 9)

    /// <summary>
    /// The 1998 full refresh at the end of setup (stage 0x17, <c>0x508250</c>): every system's holder is worked out from its regiments and support.
    /// Returns the systems whose holder it changed (none for a correct deal: B5a-14), in name order, after applying them silently (setup raises no
    /// events). Does nothing under AssaultOnly. rule: m2-control-and-capture §How control changes (B5a section 2 "full refresh").
    /// </summary>
    public IReadOnlyList<string> SetupNewGame()
    {
        var changed = new List<string>();
        if (Rule != ControlRule.Derived) return changed;
        var now = PresenceAll();
        foreach (var star in _galaxy.Systems) // name order
        {
            string sys = star.Name;
            if (_eco.IsDestroyed(sys)) continue;
            int bits = now.GetValueOrDefault(sys);
            Side? holder = bits == AllianceBit ? Side.Alliance : bits == EmpireBit ? Side.Empire : bits == 0 ? SupportSide(sys) : HolderOf(sys);
            if (bits == (AllianceBit | EmpireBit))
            {
                C.Contested ??= new SortedDictionary<string, int>(StringComparer.Ordinal);
                C.Contested[sys] = SideNumber(HolderOf(sys));
            }
            if (holder == HolderOf(sys)) continue;
            changed.Add(sys);
            _fleets.SetOwner(sys, holder);
        }
        return changed;
    }

    // ================================================================== the flip (B5a sections 4 and 5)

    private readonly List<(CaptureInfo Info, int Prev, int Mil)> _aftermath = new(); // queued 0x31b work, in flip order
    private bool _draining;

    // rule: m2-control-and-capture §What happens at the moment of capture (B5a section 4, 0x510d70 / 0x510820; section 5 per child, id order as the
    // stand-in for child order). The flip writes no support. Events: SystemCaptured first.
    private void Flip(StepContext ctx, string system, Side? old, Side? holder, CaptureCause cause, int prevMil, int newMil)
    {
        int day = ctx.Day + 1;
        var info = new CaptureInfo(ctx, system, old, holder, cause);

        // 2. The system takes the new holder's side. A world that loses its holder stays "ever held" (NeverBeenControlled stays clear).
        _fleets.SetOwner(system, holder);
        if (old != null && holder == null) (C.EverHeld ??= new SortedSet<string>(StringComparer.Ordinal)).Add(system);
        else if (holder != null && C.EverHeld != null) { C.EverHeld.Remove(system); if (C.EverHeld.Count == 0) C.EverHeld = null; }
        ctx.Emit((i, _) => new SystemCaptured(i, day, system, old, holder, cause));

        // 2.1 Production queues (MANMGRSD 5-7) belong to the system: they pass to the new holder with every product destroyed, no refund
        // (0x52b790, reason 22; the refund routine pays only for finished objects). The construction module cancels each project of a side that no
        // longer holds the system (its ProjectCancelled, reason SystemLost). Deliveries heading here keep the LostDestination rule.
        _construction?.CancelProjectsOnCapture(ctx, system);

        // 2.1 Facilities (0x4f8680): a finished one takes the new side if its class can serve it, else it is destroyed (reason 3). Unfinished ones
        // that no project or delivery owns (setup leftovers) go with the queue, no refund. Mines and refineries restart (B5b: not in this batch).
        foreach (var o in _eco.Objects.Where(x => x.System == system && x.Category == ObjectCategory.Facility).ToList()) // id order
        {
            int id = o.Id; string cls = o.Class;
            if (!o.Completed && _construction?.OwnsObject(id) == true) continue; // a queued unit or a delivery in flight: construction's
            if (!o.Completed) { _eco.RemoveObject(id); ctx.Emit((i, _) => new UnfinishedFacilityCancelled(i, day, id, cls, system, old)); continue; }
            if (!ServesSide(cls, holder)) { _eco.RemoveObject(id); ctx.Emit((i, _) => new FacilityRemoved(i, day, id, cls, system)); continue; }
            ctx.Emit((i, _) => new FacilityTransferred(i, day, id, cls, system, old, holder));
        }

        // 2.1 Finished regiments standing on the planet (0x4f8680) and special forces there (0x503d90) that cannot serve the new holder are destroyed
        // (reason 3). Their classes serve one side only, so every one not of the new holder dies; a special force away on a mission is skipped. Under
        // derived control an old-side regiment is rarely still there. Squadrons based there get Autorouting (where they go was not found: DEFAULT,
        // they stay). Fleets in orbit are not touched.
        var onMission = SpecialForcesOnMission();
        foreach (var o in _eco.Objects.Where(x => x.System == system && x.AboardShip == 0 && x.Completed && x.Side != holder
                     && (x.Category == ObjectCategory.Regiment || (x.Category == ObjectCategory.SpecialForce && !onMission.Contains(x.Id)))).ToList()) // id order
        {
            int id = o.Id; string cls = o.Class; var owner = o.Side;
            _eco.RemoveObject(id);
            ctx.Emit((i, _) => new UnitLostAtCapture(i, day, id, cls, system, owner));
        }

        // 2.1 Characters (0x4ef260): prisoners freed, characters not of the new holder leave; never captured, never harmed.
        CharactersAtCapture?.Invoke(ctx, system, old, holder);

        // 2.5 Uprising := 0, then 2.7 the garrison need for the new holder: below it an uprising starts at once (its one draw, B5c). 2.6 smuggling
        // is computed from the holder when a cycle ends (nothing stored). 2.8 the informant timer is B5c's (not built).
        _support.ClearUprisingOnCapture(ctx, system);
        _support.CheckUprisingNow(ctx, system);

        // 2.9 Blockade recomputed (event 0x311), and the presence timer 0x386 follows the flag at once (armed when on, disarmed when off: a flip after
        // slot 6.1 would otherwise leave a blockade without its timer until the next day's 6.1).
        _fleets.RefreshBlockadeFlag(ctx, system, day);
        _support.RefreshPresenceNow(ctx, system);

        // The queued object events: the loser's LossNotice, the Rebel HQ (destroyed when its world goes to the Empire or neutral; its loyalty draw).
        HolderChanged?.Invoke(info);

        // 3. Event 0x31b, queued: character loyalty, the sector step, the capital hook.
        _aftermath.Add((info, prevMil, newMil));
    }

    private HashSet<int> SpecialForcesOnMission()
    {
        var set = new HashSet<int>(); // membership only
        foreach (var m in _game.State.Missions.Missions.Values) foreach (var id in m.AllSpecialForces) set.Add(id);
        return set;
    }

    // Queued events run after the current handler (B5a section 6). A support change made by a sector step can flip another world; its aftermath
    // joins the end of the queue.
    private void Drain(StepContext ctx)
    {
        if (_draining) return;
        _draining = true;
        try
        {
            while (_aftermath.Count > 0)
            {
                var (info, prev, mil) = _aftermath[0];
                _aftermath.RemoveAt(0);
                int day = ctx.Day + 1;
                ctx.Emit((i, _) => new HolderChangeAftermath(i, day, info.System, info.Old, info.New, info.Cause)); // character loyalty (B2c)
                if (Rule == ControlRule.Derived) SectorStep(ctx, info, prev, mil); // AssaultOnly keeps the old rule: no support change at capture
                ControlChangeAftermath?.Invoke(info, info.System == CapitalSystem);
            }
        }
        finally { _draining = false; }
    }

    // ================================================================== the sector step (B5a section 6, 0x517aa0 -> 0x55de50 -> 0x518be0)

    // rule: m2-control-and-capture §After the flip. Every system of the world's sector (name order, the world itself included) that is populated,
    // was ever held and whose losing side's support is below the threshold gets ChangeSupport toward the gaining side (through the normal damping).
    private void SectorStep(StepContext ctx, CaptureInfo info, int prevMil, int newMil)
    {
        Side? old = info.Old, holder = info.New;
        if (old == holder) return;
        Side gainer = holder ?? Other(old!.Value);
        Side loser = old ?? Other(gainer);
        int amount, threshold;
        if (prevMil == 0 && newMil == 0)
        {
            amount = SectorStepSupportPerSide * ((old != null ? 1 : 0) + (holder != null ? 1 : 0));
            threshold = SectorStepSupportThreshold;
            if (old is Side lost) // one per tick per losing side (0x518040; the counters reset at each tick start); none for a world coming from neutral
            {
                if (lost == Side.Alliance) { if (_stepUsedAlliance) return; _stepUsedAlliance = true; }
                else { if (_stepUsedEmpire) return; _stepUsedEmpire = true; }
            }
        }
        else if (prevMil == 0) { amount = SectorStepTroopsArrive; threshold = SectorStepAlwaysThreshold; }
        else if (newMil == 0) { amount = SectorStepTroopsLeft; threshold = SectorStepAlwaysThreshold; }
        else { amount = SectorStepAssault; threshold = SectorStepAlwaysThreshold; }
        if (amount == 0) return; // the applier skips the amount 0 cases
        var star = _galaxy.Get(info.System);
        if (star.IsOutsideSectors) return; // no sector, nothing to walk (DEFAULT)
        var sector = _galaxy.Sectors.First(s => s.Name == star.Sector);
        foreach (var idx in sector.Members) // name order
        {
            string name = _galaxy.Systems[idx].Name;
            if (_eco.IsDestroyed(name) || !_eco.SlotsOf(name).Populated || !EverHeld(name)) continue;
            if (_eco.SupportOf(name, loser) >= threshold) continue;
            _support.ChangeSupport(ctx, name, gainer, amount, SupportCause.ControlChange);
        }
    }

    // ================================================================== AssaultOnly: the old capture call

    /// <summary>
    /// Under <see cref="ControlRule.AssaultOnly"/> only: make <paramref name="newHolder"/> the holder now and run the flip with the given cause.
    /// Returns false and changes nothing if the side already holds the system, the system is destroyed, or the rule is Derived (there is no
    /// "set holder" in 1998: B5a section 1). rule: m2-control-and-capture §How control changes (the rule before B5a, kept behind the switch).
    /// </summary>
    public bool SetHolder(StepContext ctx, string system, Side newHolder, CaptureCause cause)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (Rule != ControlRule.AssaultOnly) return false;
        if (_eco.IsDestroyed(system)) return false; // rule: m5-death-star §2 effect 5
        Side? old = HolderOf(system);
        if (old == newHolder) return false;
        int bits = PresenceAt(system);
        int prev = SingleMilitary(_presence.GetValueOrDefault(system));
        int mil = bits == (AllianceBit | EmpireBit) ? SideNumber(newHolder) : SingleMilitary(bits);
        _presence[system] = bits;
        Flip(ctx, system, old, newHolder, cause, prev, mil);
        Drain(ctx);
        return true;
    }

    // ================================================================== debug orders (B5a section 9: no DebugSetHolder under derived control)

    // rule: m2-control-and-capture §How control changes (debug order for tests). AssaultOnly only: under derived control a holder cannot be set.
    private OrderResult HandleDebugSetHolder(StepContext ctx, DebugSetHolderOrder o)
    {
        if (!DebugOrdersEnabled) return OrderResult.Reject(RejectReason.DebugDisabled);
        if (Rule != ControlRule.AssaultOnly) return OrderResult.Reject(RejectReason.NotUnderDerivedControl);
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (_eco.IsDestroyed(o.System)) return OrderResult.Reject(RejectReason.SystemDestroyed); // rule: m5-death-star §2 effect 5 (cannot be held)
        if (HolderOf(o.System) == o.NewHolder) return OrderResult.Reject(RejectReason.AlreadyHolder);
        SetHolder(ctx, o.System, o.NewHolder, CaptureCause.Debug);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    /// <summary>Queue a DebugSetHolder order (AssaultOnly only; validated in the next Step). Returns its sequence number.</summary>
    public long SubmitDebugSetHolder(Side side, string system, Side newHolder) =>
        _game.Submit(seq => new DebugSetHolderOrder(seq, side, system, newHolder));

    /// <summary>Queue a DebugSetSupport order: the system's Alliance support becomes the value (through ChangeSupport, undamped), and control follows.</summary>
    public long SubmitDebugSetSupport(Side side, string system, int allianceSupport) =>
        _game.Submit(seq => new DebugSetSupportOrder(seq, side, system, allianceSupport));

    /// <summary>Queue a DebugSetTroops order: the regiments standing on the system become <paramref name="count"/> of <paramref name="regimentClass"/> for <paramref name="troopSide"/>, and control follows.</summary>
    public long SubmitDebugSetTroops(Side side, string system, Side troopSide, string regimentClass, int count) =>
        _game.Submit(seq => new DebugSetTroopsOrder(seq, side, system, troopSide, regimentClass, count));

    private OrderResult HandleDebugSetSupport(StepContext ctx, DebugSetSupportOrder o)
    {
        if (!DebugOrdersEnabled) return OrderResult.Reject(RejectReason.DebugDisabled);
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (_eco.IsDestroyed(o.System)) return OrderResult.Reject(RejectReason.SystemDestroyed);
        if (o.AllianceSupport is < 0 or > 100) return OrderResult.Reject(RejectReason.BadValue);
        int now = _eco.SupportOf(o.System, Side.Alliance);
        if (o.AllianceSupport != now) _support.ChangeSupport(ctx, o.System, Side.Alliance, o.AllianceSupport - now, SupportCause.Other, dampen: false);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    /// <summary>
    /// The DebugSetTroops effect, callable from a test hook inside a Step: every regiment standing on the system is removed (RegimentDestroyed reason
    /// "Debug"), <paramref name="count"/> finished regiments of the class are placed for <paramref name="side"/>, then control is recomputed.
    /// </summary>
    public void DebugApplyTroops(StepContext ctx, string system, Side side, string regimentClass, int count)
    {
        int day = ctx.Day + 1;
        foreach (var r in _eco.Objects.Where(x => x.Category == ObjectCategory.Regiment && x.System == system && x.AboardShip == 0).ToList())
        {
            int id = r.Id; string cls = r.Class; var rs = r.Side ?? side;
            _eco.RemoveObject(id);
            ctx.Emit((i, _) => new RegimentDestroyed(i, day, id, cls, system, rs, "Debug"));
        }
        for (int n = 0; n < count; n++) _eco.AddObject(side, regimentClass, system);
        if (Rule == ControlRule.Derived) RecomputeControl(ctx, system);
        else if (count > 0 && HolderOf(system) != side) SetHolder(ctx, system, side, CaptureCause.Debug);
    }

    private OrderResult HandleDebugSetTroops(StepContext ctx, DebugSetTroopsOrder o)
    {
        if (!DebugOrdersEnabled) return OrderResult.Reject(RejectReason.DebugDisabled);
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (_eco.IsDestroyed(o.System)) return OrderResult.Reject(RejectReason.SystemDestroyed);
        if (o.Count < 0 || (o.Count > 0 && (!_eco.Costs.TryGet(o.RegimentClass, out var row) || row.Category != ObjectCategory.Regiment)))
            return OrderResult.Reject(RejectReason.BadValue);
        DebugApplyTroops(ctx, o.System, o.TroopSide, o.RegimentClass, o.Count);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }
}
