using Rebellion.Core.Clock;

namespace Rebellion.Core.Fleets;

// rule: m1-clock-and-orders §Events out. Day is the day the event takes effect: K for departures, K + 1 for the
// arrival, stop, contact and blockade events of steps 3 to 5.

/// <summary>Step 2. Fields: fleet, from, first hop ticks.</summary>
public sealed record FleetDeparted(int Index, int Day, int Fleet, string From, int FirstHopTicks)
    : GameEvent(Index, Day, nameof(FleetDeparted));

/// <summary>Step 4, no stop. Fields: fleet, system.</summary>
public sealed record FleetPassed(int Index, int Day, int Fleet, string System)
    : GameEvent(Index, Day, nameof(FleetPassed));

/// <summary>Step 4. Fields: fleet, system, blocking fleet ids.</summary>
public sealed record ForcedStop(int Index, int Day, int Fleet, string System, ValueList<int> Blockers)
    : GameEvent(Index, Day, nameof(ForcedStop));

/// <summary>Step 3, the final stop of a trip. Fields: fleet, system.</summary>
public sealed record FleetArrived(int Index, int Day, int Fleet, string System)
    : GameEvent(Index, Day, nameof(FleetArrived));

/// <summary>Step 5. Fields: system, fleets of each side (ids ascending).</summary>
public sealed record Contact(int Index, int Day, string System, ValueList<int> EmpireFleets, ValueList<int> AllianceFleets)
    : GameEvent(Index, Day, nameof(Contact));

/// <summary>Step 5. Classic blockade flag turned on. Fields: system, side that blockades.</summary>
public sealed record BlockadeStarted(int Index, int Day, string System, Side Blockader)
    : GameEvent(Index, Day, nameof(BlockadeStarted));

/// <summary>Step 5. Classic blockade flag turned off. Fields: system, side that blockaded.</summary>
public sealed record BlockadeLifted(int Index, int Day, string System, Side Blockader)
    : GameEvent(Index, Day, nameof(BlockadeLifted));
