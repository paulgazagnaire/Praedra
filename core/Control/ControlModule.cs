using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Support;

namespace Rebellion.Core.Control;

/// <summary>What a capture hook is told. Old is null for a neutral system.</summary>
public sealed record CaptureInfo(StepContext Ctx, string System, Side? Old, Side New, CaptureCause Cause);

/// <summary>
/// Who holds a system and what happens when that changes. The holder lives in <see cref="GameState.SystemOwners"/> (a system not listed
/// is neutral); support never changes it, only <see cref="SetHolder"/> does. Holds data and settings only; the state lives in
/// <see cref="GameState"/>. After <see cref="GameSave.Load"/>, attach again to the loaded game. Attach after the fleet, economy and
/// support modules. Capture draws ZERO random numbers. rule: m2-control-and-capture.
/// </summary>
public sealed class ControlModule
{
    /// <summary>The capital system, for the aftermath hook only (setup-victory/05 10.4: Coruscant special case). Name from the planets file.</summary>
    public const string CapitalSystem = "Coruscant";

    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly SupportModule _support;
    private readonly ConstructionModule? _construction;

    /// <summary>
    /// The DebugSetHolder order is accepted only when this is on. DEFAULT off (the sheet says a debug order exists "for tests"; a player
    /// must never be able to capture by order). Not part of the save: it is configuration.
    /// </summary>
    public bool DebugOrdersEnabled { get; set; }

    /// <summary>
    /// Step 7 hook (rule: m2-control-and-capture §What happens at the moment of capture, 7; C11): the control-change aftermath is traced
    /// only as numbers (SDPRTB 5152 = 1 per side, 2 for a flip, threshold 5153 = 100; Coruscant 5154 = 10 + 5, 5155 = 100 / 90, sector
    /// scope). Whose support moves and which way is NOT traced, so DEFAULT: nothing subscribes and no support changes at capture.
    /// The bool is true when the system is <see cref="CapitalSystem"/>. Open question 4.
    /// </summary>
    public event Action<CaptureInfo, bool>? ControlChangeAftermath;

    /// <summary>
    /// Raised at the end of every <see cref="SetHolder"/> that changed the holder, in the same call. The fog module listens: the loser's
    /// LossNotice and the Rebel HQ capture rule. rule: m5-fog-and-hq §F2 LossNotice, §F7 Capture (HqLostOnHolderChange).
    /// </summary>
    public event Action<CaptureInfo>? HolderChanged;

    private ControlModule(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ConstructionModule? construction)
    {
        _game = game; _galaxy = galaxy; _fleets = fleets; _eco = eco; _support = support; _construction = construction;
    }

    /// <summary>Register the DebugSetHolder handler on <paramref name="game"/>. Attach after the fleet, economy, support and (if used) construction modules; the construction module is optional.</summary>
    public static ControlModule Attach(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support,
        ConstructionModule? construction = null)
    {
        var m = new ControlModule(game, galaxy, fleets, eco, support, construction);
        game.RegisterOrderHandler<DebugSetHolderOrder>(m.HandleDebugSetHolder);
        return m;
    }

    private ControlState C => _game.State.Control;

    // ================================================================== queries and settings

    /// <summary>The holder of a system, null = neutral. rule: m2-control-and-capture §Control (Holder).</summary>
    public Side? HolderOf(string system) => _fleets.OwnerOf(system);

    /// <summary>The scorched-earth switch of a side (read by the M6 AI only). rule: m2-control-and-capture §Scorched earth.</summary>
    public bool ScorchedEarth(Side side) => C.ScorchedEarth(side);

    /// <summary>Setup only: set the switch of a side. Saved with the game.</summary>
    public void SetScorchedEarth(Side side, bool on) => C.SetScorchedEarth(side, on);

    // ================================================================== DebugSetHolder

    // rule: m2-control-and-capture §How control changes (debug order for tests). Applied in step 1, so slot 6.1 of the same Step sees it.
    private OrderResult HandleDebugSetHolder(StepContext ctx, DebugSetHolderOrder o)
    {
        if (!DebugOrdersEnabled) return OrderResult.Reject(RejectReason.DebugDisabled);
        if (!_galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (HolderOf(o.System) == o.NewHolder) return OrderResult.Reject(RejectReason.AlreadyHolder);
        SetHolder(ctx, o.System, o.NewHolder, CaptureCause.Debug);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    /// <summary>Queue a DebugSetHolder order (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitDebugSetHolder(Side side, string system, Side newHolder) =>
        _game.Submit(seq => new DebugSetHolderOrder(seq, side, system, newHolder));

    // ================================================================== the capture

    /// <summary>
    /// The control-change function M3's assault calls (slot 5.2) when the assault is won: the system changes to
    /// <paramref name="newHolder"/> at once, not queued, and everything below runs in this one call. Returns false and changes nothing if
    /// the side already holds the system. Neutral is never a target (no cause in M2 makes a system neutral; the Death Star destroys a
    /// system, it does not capture it). No random draws. rule: m2-control-and-capture §What happens at the moment of capture.
    /// Tags: the whole capture sequence is Lead (unverified vs original) except that uprising and support never change control
    /// (Traced); the default for every untraced step is the smallest change that keeps the state consistent.
    /// </summary>
    public bool SetHolder(StepContext ctx, string system, Side newHolder, CaptureCause cause)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        Side? old = HolderOf(system);
        if (old == newHolder) return false;
        int day = ctx.Day + 1;
        var info = new CaptureInfo(ctx, system, old, newHolder, cause);

        // The holder changes first; facilities belong to the holder of their system, so they change side with it. (Control
        // is only ever changed here: uprising and support never flip it. Traced.)
        _fleets.SetOwner(system, newHolder);
        ctx.Emit((i, _) => new SystemCaptured(i, day, system, old, newHolder, cause)); // event 10: SystemCaptured first

        // 3. Projects of the old holder at this system (queued builds): cancelled by the construction module, no refund, maintenance
        // freed; deliveries heading here follow its LostDestination rule when they arrive. Construction owns these effects.
        _construction?.CancelProjectsOnCapture(ctx, system);

        // 1 and 2. Facilities, in id order. Completed: change side with the system unless the class cannot serve the new side. Unfinished
        // ones that no project or delivery owns (setup leftovers): cancelled, no refund. (Lead, facility-ownership.md; economy/07, economy/01,
        // planets/07; DEFAULT for unfinished.) Mines and refineries keep their cycle timers (no reset); a refinery in the raw line
        // moves to the new holder's line at slot 6.5.
        foreach (var o in _eco.Objects.Where(x => x.System == system && x.Category == ObjectCategory.Facility).ToList()) // id order
        {
            int id = o.Id; string cls = o.Class;
            if (!o.Completed && _construction?.OwnsObject(id) == true) continue; // a queued unit or a delivery in flight: construction's
            if (!o.Completed) { _eco.RemoveObject(id); ctx.Emit((i, _) => new UnfinishedFacilityCancelled(i, day, id, cls, system, old)); continue; }
            if (!ServesSide(cls, newHolder)) { _eco.RemoveObject(id); ctx.Emit((i, _) => new FacilityRemoved(i, day, id, cls, system)); continue; }
            ctx.Emit((i, _) => new FacilityTransferred(i, day, id, cls, system, old, newHolder));
        }

        // 4 and 5. Regiments, fleets and characters of the old holder: not touched (DEFAULT; M3 decides, Open question 2).

        // 6. Uprising flag, rebelling side and timers 0x384 and 0x38d cleared; UprisingEnded is the last event.
        _support.ClearUprisingOnCapture(ctx, system);

        // 7. Support: the stored value is not touched (DEFAULT). The aftermath is a recorded hook only.
        ControlChangeAftermath?.Invoke(info, system == CapitalSystem);

        // 8. Blockade flag recomputed now (the owner changed), not a Step later.
        _fleets.RefreshBlockadeFlag(ctx, system, day);

        // M5: the loser is told, and an at-rest Rebel HQ on the system is lost (m5-fog-and-hq §F2, §F7).
        HolderChanged?.Invoke(info);

        // 9. Economy pools: capacity and load are derived from the facilities now on each side, so they are already right.
        return true;
    }

    /// <summary>
    /// Can a completed facility of this class serve the side? Only the Death Star Shield is Empire only; every other class serves both.
    /// rule: m2-control-and-capture §What happens at the moment of capture (1) (Lead, economy/07).
    /// </summary>
    public static bool ServesSide(string facilityClass, Side side) =>
        !(facilityClass == FacilityKinds.ClassName(FacilityKind.DeathStarShield) && side == Side.Alliance);
}
