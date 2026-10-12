using Rebellion.Core.Clock;

namespace Rebellion.Core.Economy;

// Events of the economy. Day is the hook time T = K + 1 (the day-end hooks take effect when Day becomes K + 1).
// rule: m1-clock-and-orders §Events out. The slot sheet lists FacilityDestroyed, ResourceIncident and Disaster, the maintenance
// sheet ObjectScrapped and AutoscrapWarning; ResourceProduced and RefineryWaiting are additions for production and shortage.

public enum DestroyReason { OverEnergy, OverRaw, Disaster, UprisingIncident, Bombardment, Assault, Sabotage }

public enum SlotChange { None, RawMinus, EnergyMinus, RawPlus, EnergyPlus }

/// <summary>Slot 6.5. A mine cycle ended (+1 raw) or a refinery cycle ended (+1 refined). PoolSide differs from Controller when smuggled; SmugglePercent is the chance v that was rolled against (0 = no draw).</summary>
public sealed record ResourceProduced(int Index, int Day, int Facility, string System, string Resource, Side Controller, Side PoolSide, bool Diverted, int SmugglePercent)
    : GameEvent(Index, Day, nameof(ResourceProduced));

/// <summary>Slot 6.5. A refinery found no raw and joined its side's line (the shortage case).</summary>
public sealed record RefineryWaiting(int Index, int Day, int Facility, string System, Side Side)
    : GameEvent(Index, Day, nameof(RefineryWaiting));

/// <summary>rule: m2-slots-and-resources §Over-capacity destruction. Fields: facility, system, reason.</summary>
public sealed record FacilityDestroyed(int Index, int Day, int Facility, string System, DestroyReason Reason)
    : GameEvent(Index, Day, nameof(FacilityDestroyed));

/// <summary>rule: m2-slots-and-resources §Resource incident. Before and After are the changed value (also when nothing applied).</summary>
public sealed record ResourceIncident(int Index, int Day, string System, SlotChange Change, int Before, int After)
    : GameEvent(Index, Day, nameof(ResourceIncident));

/// <summary>rule: m2-slots-and-resources §Disaster incident. Destroyed = facility ids; Rolled = facilities that got a roll.</summary>
public sealed record Disaster(int Index, int Day, string System, ValueList<int> Destroyed, int Rolled)
    : GameEvent(Index, Day, nameof(Disaster));

/// <summary>rule: m2-maintenance §Scrapping and refunds. Reason is "Order" or "Autoscrap".</summary>
public sealed record ObjectScrapped(int Index, int Day, int Object, string Class, Side Side, string Reason, int Refund)
    : GameEvent(Index, Day, nameof(ObjectScrapped));

/// <summary>rule: m2-maintenance §Rule (New), Smart rule 1: only yards were left to scrap.</summary>
public sealed record AutoscrapWarning(int Index, int Day, Side Side, int Object)
    : GameEvent(Index, Day, nameof(AutoscrapWarning));
