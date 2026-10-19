using Praedra.Core.Clock;

namespace Praedra.Core.Control;

// Events of control and capture. Day is the hook time T = K + 1, like the economy and support events.
// rule: m2-control-and-capture §What happens at the moment of capture (10: event order).

/// <summary>
/// Why a system changed holder. Under the derived control rule (B5a, DEFAULT) the cause is read from the military side before and after
/// (system +0x84 bits 4-5): Support = no regiments before or after (the world joined a side or declared neutrality by support alone);
/// TroopsLeft = the holder's regiments left or died and support handed the world over; Assault = regiments now hold it (none or other troops
/// before). Under <c>ControlRule.AssaultOnly</c> the caller names the cause: Assault (M3), Debug (the test order), DeathStarRevolt.
/// rule: m2-control-and-capture §How control changes (B5a section 4, Work 8).
/// </summary>
public enum CaptureCause
{
    Debug, Assault,
    /// <summary>A neutral system of a sector struck twice by the Death Star turns Alliance (AssaultOnly only). rule: m5-death-star §2 effect 7.</summary>
    DeathStarRevolt,
    /// <summary>Support alone moved the world: no regiments on it before or after. Headlines "Joins", "Joins Enemy", "Declares Neutrality".</summary>
    Support,
    /// <summary>The last regiments of the holder left or died, and support decided the new holder.</summary>
    TroopsLeft,
}

/// <summary>
/// From and To are null for neutral (a world can now go neutral: B5a). rule: m2-control-and-capture §How control changes
/// (SystemCaptured{system, from, to, cause}).
/// </summary>
public sealed record SystemCaptured(int Index, int Day, string System, Side? From, Side? To, CaptureCause Cause)
    : GameEvent(Index, Day, nameof(SystemCaptured));

/// <summary>
/// The queued aftermath of a holder change (Classic rule event 0x31b, handler <c>0x515c00</c>): raised after the flip and the events it queued, before
/// the sector step. The character loyalty draw (B2c) reacts to it. rule: m2-control-and-capture §After the flip (B5a section 6).
/// </summary>
public sealed record HolderChangeAftermath(int Index, int Day, string System, Side? From, Side? To, CaptureCause Cause)
    : GameEvent(Index, Day, nameof(HolderChangeAftermath));

/// <summary>A completed regiment, special force or other unit on the world whose class cannot serve the new holder was destroyed at the flip (reason 3). rule: B5a section 5.</summary>
public sealed record UnitLostAtCapture(int Index, int Day, int Object, string Class, string System, Side? Owner)
    : GameEvent(Index, Day, nameof(UnitLostAtCapture));

/// <summary>A completed facility went to the new holder with its system (it carries no side of its own). From and To are null for neutral.</summary>
public sealed record FacilityTransferred(int Index, int Day, int Facility, string Class, string System, Side? From, Side? To)
    : GameEvent(Index, Day, nameof(FacilityTransferred));

/// <summary>A completed facility whose class cannot serve the new side was removed (Death Star Shield when the Alliance takes it or it goes neutral). rule: B5a section 5.</summary>
public sealed record FacilityRemoved(int Index, int Day, int Facility, string Class, string System)
    : GameEvent(Index, Day, nameof(FacilityRemoved));

/// <summary>
/// An unfinished facility on the captured system that no construction project owns (a setup leftover) was cancelled: no refund,
/// maintenance freed. Owner is the side that held the system (null if neutral). Projects themselves are cancelled by the construction
/// module (its own ProjectCancelled, reason SystemLost).
/// </summary>
public sealed record UnfinishedFacilityCancelled(int Index, int Day, int Object, string Class, string System, Side? Owner)
    : GameEvent(Index, Day, nameof(UnfinishedFacilityCancelled));
