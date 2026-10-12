using Rebellion.Core.Clock;

namespace Rebellion.Core.Defenses;

// Events of the bombardment. Day is K + 1 (slot 5.1 events carry day = K + 1 like arrivals). Order of emission for one resolution:
// BombardmentStarted, then per hit TargetHit + (RegimentDestroyed | FacilityDestroyed | ResourceReduced), then BatteryReturnFire,
// SupportChanged (m2), BombardmentResolved. rule: m3-bombardment §Where it runs.

/// <summary>Why a bombardment is refused or cancelled. The strings are the sheet's reasons. rule: m3-bombardment §Order and preconditions.</summary>
public static class BombardReject
{
    public const string NotAtSystem = "NotAtSystem";
    public const string OwnSystem = "OwnSystem";
    public const string EnemyFleetPresent = "EnemyFleetPresent";
    public const string NoBombardPower = "NoBombardPower";
    public const string OrderPending = "OrderPending";
}

/// <summary>Slot 5.1. P = power, S = shield strength, Hits = max(0, P - S). rule: m3-bombardment §Where it runs.</summary>
public sealed record BombardmentStarted(int Index, int Day, string System, int Fleet, Side Side, BombardCategory Category, int P, int S, int Hits)
    : GameEvent(Index, Day, nameof(BombardmentStarted));

/// <summary>One hit. TargetKind is Regiment, Facility, Energy or Raw (Id 0 for the last two). Roll is rand(1..10); destroyed when it is above Defense.</summary>
public sealed record TargetHit(int Index, int Day, string TargetKind, int Id, int Defense, int Roll, bool Destroyed)
    : GameEvent(Index, Day, nameof(TargetHit));

/// <summary>A hit reduced the system's Energy or Raw by one (floor 0). Resource is "Energy" or "Raw".</summary>
public sealed record ResourceReduced(int Index, int Day, string System, string Resource)
    : GameEvent(Index, Day, nameof(ResourceReduced));

/// <summary>The hook for the batteries' return fire: every completed battery (KDY, LNR) at the start of the bombardment. Changes no ship in M3 (DEFAULT).</summary>
public sealed record BatteryReturnFire(int Index, int Day, string System, ValueList<int> Facilities)
    : GameEvent(Index, Day, nameof(BatteryReturnFire));

/// <summary>Slot 5.1, last event of one resolution. Counts are what the hits destroyed (over-capacity collateral is in its own FacilityDestroyed events).</summary>
public sealed record BombardmentResolved(int Index, int Day, string System, int Hits, int Regiments, int Facilities, int Energy, int Raw)
    : GameEvent(Index, Day, nameof(BombardmentResolved));

/// <summary>Slot 5.1: an accepted order no longer holds when its turn came. No other change, no draw. Reason as for a rejection.</summary>
public sealed record BombardCancelled(int Index, int Day, int Fleet, string System, string Reason)
    : GameEvent(Index, Day, nameof(BombardCancelled));
