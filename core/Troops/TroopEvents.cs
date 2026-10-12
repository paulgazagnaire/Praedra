using Rebellion.Core.Clock;

namespace Rebellion.Core.Troops;

// Events of the troops package. Day is K for orders (step 1) and K + 1 for the hooks (step 3, slot 6.6.a), like the fleet events.
// rule: m3-troops-and-special-forces §Where it runs and what it draws (events).

/// <summary>A regiment came to exist for use (completed at its yard's system in 6.6.a, or delivered in step 3). Fields: id, class, system.</summary>
public sealed record RegimentCreated(int Index, int Day, int Id, string Class, string System)
    : GameEvent(Index, Day, nameof(RegimentCreated));

/// <summary>A special force came to exist for use (completed or delivered). Fields: id, class, system.</summary>
public sealed record SpecialForceCreated(int Index, int Day, int Id, string Class, string System)
    : GameEvent(Index, Day, nameof(SpecialForceCreated));

/// <summary>LoadRegiments. Fields: regiments, fleet, ship each went to (same order as regiments), system.</summary>
public sealed record RegimentsLoaded(int Index, int Day, ValueList<int> Regiments, int Fleet, ValueList<int> Ships, string System)
    : GameEvent(Index, Day, nameof(RegimentsLoaded));

/// <summary>UnloadRegiments. Fields: regiments, fleet, ship each came from (same order as regiments), system.</summary>
public sealed record RegimentsUnloaded(int Index, int Day, ValueList<int> Regiments, int Fleet, ValueList<int> Ships, string System)
    : GameEvent(Index, Day, nameof(RegimentsUnloaded));

/// <summary>
/// Regiments aboard a ship that ceased to exist (scrapped, autoscrapped, later destroyed) landed on the system the fleet was at (a fleet on a
/// lane: the system it last left). Fields: regiments, fleet (0 = the ship was in no fleet), ship each rode, system.
/// The name is a DEFAULT: the sheet lists no event for it (m3-fleets F19 only fixes the result). rule: m3-fleets §Carrying rules (DEFAULT).
/// </summary>
public sealed record RegimentsLanded(int Index, int Day, ValueList<int> Regiments, int Fleet, ValueList<int> Ships, string System)
    : GameEvent(Index, Day, nameof(RegimentsLanded));
