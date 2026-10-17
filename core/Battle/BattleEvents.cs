using Rebellion.Core.Clock;

namespace Rebellion.Core.Battle;

// Events of the battle (step 5, before slots 5.1 and 5.2). Day is K + 1 like the other step 3 to 5 events.
// Order for one battle: BattleStarted, BattleChoice (Alliance, Empire), then per destroyed unit UnitDestroyed / RetreatRefused / RetreatHeldByTractor / FleetRetreated /
// SquadronsRelocated in the order they happen, then BattleEnded. rule: m4-battle-classic §10; m4-retreat-damage-repair §Where it runs.

/// <summary>SetOnContact: a side's standing answer to the Battle Alert. Order (step 1). rule: m4-battle-classic §1.3.</summary>
public sealed record SetOnContactOrder(long Sequence, Side Side, OnContactMode Mode) : Order(Sequence, Side);

/// <summary>Reject codes of SetOnContact. rule: m4-battle-classic §1.3 (the AI side is fixed to Simulate; the codes are DEFAULT names).</summary>
public static class BattleReject
{
    public const string AiSide = "AiSide";
}

/// <summary>Battle begins. Fields: system, object ids of each side's units (ships, then squadrons).</summary>
public sealed record BattleStarted(int Index, int Day, string System, ValueList<int> AllianceUnits, ValueList<int> EmpireUnits)
    : GameEvent(Index, Day, nameof(BattleStarted));

/// <summary>What a side answered at the alert.</summary>
public sealed record BattleChoice(int Index, int Day, string System, Side Side, OnContactMode Mode)
    : GameEvent(Index, Day, nameof(BattleChoice));

/// <summary>A unit (ship, squadron, or a regiment aboard a lost ship) left the game. Second 0 = at the alert or its retreat call.</summary>
public sealed record UnitDestroyed(int Index, int Day, string System, int Unit, int Second, UnitLossReason Reason)
    : GameEvent(Index, Day, nameof(UnitDestroyed));

/// <summary>First refused retreat of the battle.</summary>
public sealed record RetreatRefused(int Index, int Day, string System, Side Side, RetreatRefusal Reason)
    : GameEvent(Index, Day, nameof(RetreatRefused));

/// <summary>New rules: an enemy tractor beam held a ship that tried to retreat; it stays in the battle. Fields: ship, holder, battle second. rule: m4-retreat-damage-repair §6.</summary>
public sealed record RetreatHeldByTractor(int Index, int Day, string System, int Ship, int Holder, int Second)
    : GameEvent(Index, Day, nameof(RetreatHeldByTractor));

/// <summary>A fleet jumped to a neighbouring system. Fields: fleet, from, to, ships and squadrons now in it (aboard ones included).</summary>
public sealed record FleetRetreated(int Index, int Day, int Fleet, string From, string To, ValueList<int> Ships, ValueList<int> Squadrons)
    : GameEvent(Index, Day, nameof(FleetRetreated));

/// <summary>Based or lone squadrons that jumped and are now based at the destination.</summary>
public sealed record SquadronsRelocated(int Index, int Day, ValueList<int> Squadrons, string To)
    : GameEvent(Index, Day, nameof(SquadronsRelocated));

/// <summary>The outcome record of a battle. rule: m4-battle-classic §8.</summary>
public sealed record BattleResult(string System, int Seconds, BattleOutcome Alliance, BattleOutcome Empire,
    int AllianceLosses, int EmpireLosses, int AllianceSurvivors, int EmpireSurvivors);

/// <summary>Battle over.</summary>
public sealed record BattleEnded(int Index, int Day, BattleResult Result)
    : GameEvent(Index, Day, nameof(BattleEnded));
