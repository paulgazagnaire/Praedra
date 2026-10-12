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

// ---- M3 fleet events. rule: m3-fleets §Where it runs and what it draws (event table). Day is K for orders (step 1) and K + 1 for
// the hooks (steps 3 to 5, slot 6.6.a), like the events above.

/// <summary>Why a spare or other new fleet was made. rule: m3-fleets §Where it runs (FleetCreated).</summary>
public enum FleetCreateCause { Spare, CreateFleet, SplitFleet, Squadrons }

/// <summary>Why a fleet ceased to exist. rule: m3-fleets §Where it runs (FleetDissolved).</summary>
public enum DissolveCause { Empty, Merged, SquadronsArrived }

/// <summary>How a new ship reached its system. rule: m3-fleets §Where it runs (ShipJoinedFleet).</summary>
public enum ShipJoinCause { Completed, Delivered }

/// <summary>Slot 6.6.a (built at the destination) or step 3 (delivery). Fields: ship, class, system, fleet, cause.</summary>
public sealed record ShipJoinedFleet(int Index, int Day, int Ship, string Class, string System, int Fleet, ShipJoinCause Cause)
    : GameEvent(Index, Day, nameof(ShipJoinedFleet));

/// <summary>The spare fleet, CreateFleet, SplitFleet or MoveSquadrons. Fields: fleet, system, members (ship ids, or squadron ids for a squadron-only fleet), cause.</summary>
public sealed record FleetCreated(int Index, int Day, int Fleet, string System, ValueList<int> Members, FleetCreateCause Cause)
    : GameEvent(Index, Day, nameof(FleetCreated));

/// <summary>TransferShips and MergeFleets. Fields: ships, fleets they left (ids ascending, none for ships that were in no fleet), fleet they joined.</summary>
public sealed record ShipsTransferred(int Index, int Day, ValueList<int> Ships, ValueList<int> FromFleets, int ToFleet)
    : GameEvent(Index, Day, nameof(ShipsTransferred));

/// <summary>A fleet became empty, was merged away, or a squadron-only fleet landed. Fields: fleet, cause.</summary>
public sealed record FleetDissolved(int Index, int Day, int Fleet, DissolveCause Cause)
    : GameEvent(Index, Day, nameof(FleetDissolved));

/// <summary>LoadSquadrons. Fields: squadrons, fleet, ship each squadron went to (same order as squadrons), system.</summary>
public sealed record SquadronsLoaded(int Index, int Day, ValueList<int> Squadrons, int Fleet, ValueList<int> Ships, string System)
    : GameEvent(Index, Day, nameof(SquadronsLoaded));

/// <summary>UnloadSquadrons. Fields: squadrons, fleet, ship each came from (same order as squadrons), system.</summary>
public sealed record SquadronsUnloaded(int Index, int Day, ValueList<int> Squadrons, int Fleet, ValueList<int> Ships, string System)
    : GameEvent(Index, Day, nameof(SquadronsUnloaded));

/// <summary>
/// Squadrons became based at a system: a squadron-only fleet landed (ship 0 for each), or their carrier was scrapped (the carrier's id).
/// Fields: squadrons, fleet they left, ship each rode (0 = none), system.
/// </summary>
public sealed record SquadronsBased(int Index, int Day, ValueList<int> Squadrons, int Fleet, ValueList<int> Ships, string System)
    : GameEvent(Index, Day, nameof(SquadronsBased));
