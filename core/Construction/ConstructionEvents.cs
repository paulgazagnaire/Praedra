using Praedra.Core.Clock;

namespace Praedra.Core.Construction;

// Events of construction. rule: m1-clock-and-orders §Events out. Day is K for step-1 events, and the hook time T = K + 1 for slot 6.6
// and for the delivery events of steps 3 and 4 (the day the change takes effect). The sheet names DeliveryRefused only; the rest are additions.

/// <summary>Step 1. A build order was accepted; Units are the object ids created now (queued).</summary>
public sealed record ProjectStarted(int Index, int Day, Side Side, string System, YardFamily Family, string Class, int Count, string Destination, ValueList<int> Units)
    : GameEvent(Index, Day, nameof(ProjectStarted));

/// <summary>A project ended before it finished: Reason is Replaced (new order), Stopped (StopBuild), YardLost or SystemLost. Units are the removed object ids.</summary>
public sealed record ProjectCancelled(int Index, int Day, Side Side, string System, YardFamily Family, string Reason, ValueList<int> Units)
    : GameEvent(Index, Day, nameof(ProjectCancelled));

/// <summary>One unfinished unit left its project (sabotage); the project goes on. rule: m5-harvester §5 (Spike in the yard).</summary>
public sealed record UnitRemoved(int Index, int Day, Side Side, string System, YardFamily Family, int Object, string Reason)
    : GameEvent(Index, Day, nameof(UnitRemoved));

/// <summary>Slot 6.6.b. A yard found no refined and joined its side's refined line.</summary>
public sealed record YardWaiting(int Index, int Day, int Yard, string System, Side Side)
    : GameEvent(Index, Day, nameof(YardWaiting));

/// <summary>Slot 6.6.a. A unit reached its cost. DeliveryId 0 = it appears at the yard's system now; otherwise it left as that delivery.</summary>
public sealed record UnitCompleted(int Index, int Day, int Object, string Class, Side Side, string Yard, string Destination, int DeliveryId)
    : GameEvent(Index, Day, nameof(UnitCompleted));

/// <summary>Slot 6.6.a. A delivery left the yard. DueDay is the day its first hop ends.</summary>
public sealed record DeliveryDeparted(int Index, int Day, int Delivery, int Object, string Class, string From, string To, ValueList<string> Path, int FirstHopTicks, int DueDay)
    : GameEvent(Index, Day, nameof(DeliveryDeparted));

/// <summary>Step 4. A delivery went on from an intermediate system. Fields: delivery, system.</summary>
public sealed record DeliveryPassed(int Index, int Day, int Delivery, int Object, string System)
    : GameEvent(Index, Day, nameof(DeliveryPassed));

/// <summary>Step 3 or 4 (or 6.6.a). A delivery is held at a system: Reason FleetPresent (D2) or DestinationLost. Blockers are the enemy fleet ids (FleetPresent).</summary>
public sealed record DeliveryHeld(int Index, int Day, int Delivery, int Object, string System, string Reason, ValueList<int> Blockers)
    : GameEvent(Index, Day, nameof(DeliveryHeld));

/// <summary>Step 4. A held delivery went on.</summary>
public sealed record DeliveryResumed(int Index, int Day, int Delivery, int Object, string System)
    : GameEvent(Index, Day, nameof(DeliveryResumed));

/// <summary>Step 3 or 4. A delivery reached its destination and the unit appeared (a facility installed).</summary>
public sealed record DeliveryArrived(int Index, int Day, int Delivery, int Object, string Class, string System)
    : GameEvent(Index, Day, nameof(DeliveryArrived));

/// <summary>Step 3 or 4. A facility arrived where it cannot stand (system not the side's, slot gone): scrapped with half refund. rule: m2-construction §Completion and delivery.</summary>
public sealed record DeliveryRefused(int Index, int Day, int Delivery, int Object, string Class, string System, Side Side, int Refund)
    : GameEvent(Index, Day, nameof(DeliveryRefused));
