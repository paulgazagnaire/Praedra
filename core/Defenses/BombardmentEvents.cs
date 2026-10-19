using Rebellion.Core.Clock;

namespace Rebellion.Core.Defenses;

// Events of the bombardment. Day is K + 1 (slot 5.1 events carry day = K + 1 like arrivals). Order of emission for one strike, in the phases
// of B7-bombardment §1 (counts, return fire, hits, aftermath):
//   with BatteryReturnFire on: BatteryReturnFire, one BatteryShot per shot, one BombardShipDamaged per hit ship (+ RegimentDestroyed "ShipLost"
//   for a ship destroyed), then BombardmentStarted, CollateralHit (MilitaryCollateral), per hit TargetHit + (RegimentDestroyed |
//   FacilityDestroyed | ResourceReduced) or HitWasted, SupportChanged (m2), BombardmentResolved;
//   with BatteryReturnFire off (the m3 hook): BombardmentStarted, the hits, BatteryReturnFire, SupportChanged, BombardmentResolved.
// rule: m3-bombardment §Where it runs; B7-bombardment §1, §4, §5, §6.

/// <summary>Why a bombardment is refused or cancelled. The strings are the sheet's reasons. rule: m3-bombardment §Order and preconditions.</summary>
public static class BombardReject
{
    public const string NotAtSystem = "NotAtSystem";
    public const string OwnSystem = "OwnSystem";
    public const string EnemyFleetPresent = "EnemyFleetPresent";
    public const string NoBombardPower = "NoBombardPower";
    public const string OrderPending = "OrderPending";
    /// <summary>The flagged fleets at the system are not all of one side: nothing happens. rule: B7-bombardment §1 (cannot occur while check 3 holds).</summary>
    public const string MixedSides = "MixedSides";
}

/// <summary>
/// Slot 5.1. P = power (summed over <paramref name="Fleets"/>, after return fire), S = shield strength, Hits = max(0, P - S). Fleet is the fleet whose
/// order started the strike (the first of Fleets); Fleets lists every fleet that strikes together (one with BombardFleetsTogether off).
/// rule: m3-bombardment §Where it runs; B7-bombardment §1, §5.
/// </summary>
public sealed record BombardmentStarted(int Index, int Day, string System, int Fleet, Side Side, BombardCategory Category, int P, int S, int Hits, ValueList<int> Fleets)
    : GameEvent(Index, Day, nameof(BombardmentStarted));

/// <summary>A hit whose pick t (over the starting target count) points past the targets still standing: nothing happens, no die. rule: B7-bombardment §5.4 (C1).</summary>
public sealed record HitWasted(int Index, int Day, string System, int Pick, int Standing)
    : GameEvent(Index, Day, nameof(HitWasted));

/// <summary>
/// A military strike's accidental roll on the first civilian facility (Facility 0 when there is none: the die is still drawn and does nothing).
/// rule: B7-bombardment §5.3 (C6).
/// </summary>
public sealed record CollateralHit(int Index, int Day, string System, int Facility, int Defense, int Roll, bool Destroyed)
    : GameEvent(Index, Day, nameof(CollateralHit));

/// <summary>One battery's shot at a bombarding capital ship: Damage = attack x (L / 40 + 1); Ion for a KDY-150, else turbolaser (LNR). rule: B7-bombardment §4.</summary>
public sealed record BatteryShot(int Index, int Day, string System, int Facility, int Ship, int Damage, bool Ion)
    : GameEvent(Index, Day, nameof(BatteryShot));

/// <summary>
/// The return fire applied to one ship: the ion and turbolaser totals, its current shield S, the hull lost (stays), whether it was destroyed,
/// whether it is grounded (ion left after the shields: its own value counts 0 this strike) and the system counter raised by a critical hit
/// (0 shield recharge, 1 weapons, 2 tractor, 3 speed, 4 primary hyperdrive, 5 backup; -1 none). rule: B7-bombardment §4.
/// </summary>
public sealed record BombardShipDamaged(int Index, int Day, string System, int Ship, int Ion, int Turbolaser, int Shield, int HullLost, bool Destroyed, bool Grounded, int Critical)
    : GameEvent(Index, Day, nameof(BombardShipDamaged));

/// <summary>One hit. TargetKind is Regiment, Facility, Energy or Raw (Id 0 for the last two). Roll is rand(1..10); destroyed when it is above Defense.</summary>
public sealed record TargetHit(int Index, int Day, string TargetKind, int Id, int Defense, int Roll, bool Destroyed)
    : GameEvent(Index, Day, nameof(TargetHit));

/// <summary>A hit reduced the system's Energy or Raw by one (floor 0). Resource is "Energy" or "Raw".</summary>
public sealed record ResourceReduced(int Index, int Day, string System, string Resource)
    : GameEvent(Index, Day, nameof(ResourceReduced));

/// <summary>
/// The batteries that fire back: every completed KDY-150 then every completed LNR (BatteryReturnFire on, emitted before their shots), or with the
/// switch off the m3 hook (every completed battery, id order, after the hits; changes no ship). rule: m3-bombardment §Resolution 6; B7-bombardment §4.
/// </summary>
public sealed record BatteryReturnFire(int Index, int Day, string System, ValueList<int> Facilities)
    : GameEvent(Index, Day, nameof(BatteryReturnFire));

/// <summary>
/// Slot 5.1, last event of one resolution. Counts are what the hits and the military collateral roll destroyed (over-capacity collateral is in its
/// own FacilityDestroyed events).
/// </summary>
public sealed record BombardmentResolved(int Index, int Day, string System, int Hits, int Regiments, int Facilities, int Energy, int Raw)
    : GameEvent(Index, Day, nameof(BombardmentResolved));

/// <summary>Slot 5.1: an accepted order no longer holds when its turn came. No other change, no draw. Reason as for a rejection.</summary>
public sealed record BombardCancelled(int Index, int Day, int Fleet, string System, string Reason)
    : GameEvent(Index, Day, nameof(BombardCancelled));
