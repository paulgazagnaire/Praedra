using Rebellion.Core.Clock;

namespace Rebellion.Core.Control;

// Events of control and capture. Day is the hook time T = K + 1, like the economy and support events.
// rule: m2-control-and-capture §What happens at the moment of capture (10: event order).

/// <summary>Why a system changed holder. Assault is M3's (slot 5.2); Debug is the M2 test order.</summary>
public enum CaptureCause { Debug, Assault }

/// <summary>From is null for a neutral system. rule: m2-control-and-capture §How control changes (SystemCaptured{system, from, to, cause}).</summary>
public sealed record SystemCaptured(int Index, int Day, string System, Side? From, Side To, CaptureCause Cause)
    : GameEvent(Index, Day, nameof(SystemCaptured));

/// <summary>A completed facility went to the new holder with its system (it carries no side of its own). From is null for a neutral system.</summary>
public sealed record FacilityTransferred(int Index, int Day, int Facility, string Class, string System, Side? From, Side To)
    : GameEvent(Index, Day, nameof(FacilityTransferred));

/// <summary>A completed facility whose class cannot serve the new side was removed (Death Star Shield, Alliance capture).</summary>
public sealed record FacilityRemoved(int Index, int Day, int Facility, string Class, string System)
    : GameEvent(Index, Day, nameof(FacilityRemoved));

/// <summary>
/// An unfinished facility, or an old-holder queued unit on the captured system, was cancelled: no refund, maintenance freed.
/// Owner is the side that held it (null on a neutral system).
/// </summary>
public sealed record ProjectCancelled(int Index, int Day, int Object, string Class, string System, Side? Owner)
    : GameEvent(Index, Day, nameof(ProjectCancelled));
