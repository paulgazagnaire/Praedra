using Rebellion.Core.Clock;

namespace Rebellion.Core.Assault;

// Events of the assault. Day is K + 1 (slot 5.2 events carry day = K + 1 like arrivals). Order of emission for one resolution:
// AssaultStarted, then step 1 BatteryHit (+ RegimentDestroyed), step 2 DuelResolved (+ RegimentDestroyed), step 4 CollateralDamage
// (+ FacilityDestroyed, from the economy, for over-capacity), AssaultLanded, AssaultResolved, then the capture events of the control sheet.
// rule: m3-assault §Where it runs in the day Step (Events, in emission order).

/// <summary>Why an assault is refused or cancelled. The strings are the sheet's reasons. rule: m3-assault §Order and preconditions.</summary>
public static class AssaultReject
{
    public const string NotAtSystem = "NotAtSystem";
    public const string NoTroops = "NoTroops";
    public const string OwnSystem = "OwnSystem";
    public const string FullyShielded = "FullyShielded";
    public const string EnemyFleetPresent = "EnemyFleetPresent";
    public const string OrderPending = "OrderPending";
}

/// <summary>What a duel did. rule: m3-assault §Resolution 2d.</summary>
public static class DuelOutcome
{
    public const string DefenderDestroyed = "DefenderDestroyed";
    public const string AttackerDestroyed = "AttackerDestroyed";
    public const string NobodyDies = "NobodyDies";
}

/// <summary>The start of one assault resolution: the attacking side, the pooled fleets (id order), A0 landing regiments and D0 defenders.</summary>
public sealed record AssaultStarted(int Index, int Day, string System, Side Side, ValueList<int> Fleets, int A0, int D0)
    : GameEvent(Index, Day, nameof(AssaultStarted));

/// <summary>Step 1: a defense facility's shot hit the attacker regiment <paramref name="Regiment"/> (it is destroyed next, RegimentDestroyed reason AssaultBattery).</summary>
public sealed record BatteryHit(int Index, int Day, int Facility, int Regiment)
    : GameEvent(Index, Day, nameof(BatteryHit));

/// <summary>Step 2: one duel. X = roll + attack + L_att / 30 - defense - L_def / 30. Outcome is a <see cref="DuelOutcome"/> string.</summary>
public sealed record DuelResolved(int Index, int Day, int Attacker, int Defender, int X, string Outcome)
    : GameEvent(Index, Day, nameof(DuelResolved));

/// <summary>Step 4: one collateral round. Target is "Facility" (Id = the facility, destroyed outright), "Energy" or "Raw" (Id 0); Changed is false when Energy or Raw was already 0.</summary>
public sealed record CollateralDamage(int Index, int Day, int Round, string Target, int Id, bool Changed)
    : GameEvent(Index, Day, nameof(CollateralDamage));

/// <summary>
/// Step 5, win only: these attackers stand on the system now (the sheet calls this RegimentsLanded; that name is already the troop
/// module's event for regiments of a vanished ship, so this one is AssaultLanded).
/// </summary>
public sealed record AssaultLanded(int Index, int Day, string System, Side Side, ValueList<int> Regiments)
    : GameEvent(Index, Day, nameof(AssaultLanded));

/// <summary>Last event of one resolution (before the capture events). Result is "Won" or "Failed". A and D are what is left.</summary>
public sealed record AssaultResolved(int Index, int Day, string System, Side Side, string Result, int A0, int A, int D0, int D, int Duels, int Rounds)
    : GameEvent(Index, Day, nameof(AssaultResolved));

/// <summary>Slot 5.2: an accepted order no longer holds when its turn came. No other change, no draw. Reason as for a rejection.</summary>
public sealed record AssaultCancelled(int Index, int Day, int Fleet, string System, string Reason)
    : GameEvent(Index, Day, nameof(AssaultCancelled));
