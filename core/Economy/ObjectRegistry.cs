using Praedra.Core.Ships;

namespace Praedra.Core.Economy;

/// <summary>
/// The shared object id counter and the creation of registry objects, used by <see cref="EconomyModule"/> and by
/// <see cref="Fleets.FleetModule"/> (which can run without the economy attached, in tests and in the viewer). One counter gives every
/// ship, squadron, regiment, special force, facility and fleet a fresh id in creation order.
/// rule: m3-ships-and-fighters §The ship object (one shared object id counter); m3-fleets §What a fleet is (fleet ids from the shared counter).
/// </summary>
public static class ObjectRegistry
{
    /// <summary>Damage counters per ship, in this order: shield generator, weapons, tractor, sub-light, hyperdrive, damage control. rule: m3-ships-and-fighters §Damaged state.</summary>
    public const int HitCounters = 6;
    /// <summary>Index of the hyperdrive counter. A ship is damaged for travel when it is above 0. rule: m3-ships-and-fighters §Damaged state (DEFAULT).</summary>
    public const int HyperdriveCounter = 4;
    /// <summary>Index of the tractor counter. rule: m3-ships-and-fighters §Damaged state; m4-retreat-damage-repair §6 (a working tractor has it at 0).</summary>
    public const int TractorCounter = 2;
    /// <summary>Highest value of one damage counter. rule: m3-ships-and-fighters §Damaged state.</summary>
    public const int MaxHit = 15;

    /// <summary>The next unused id (and advance the counter). rule: m3-ships-and-fighters §The ship object.</summary>
    public static int NextId(EconomyState e) => e.NextObjectId++;

    /// <summary>Reserve an explicit id (setup and tests): it must be free; the counter moves past it.</summary>
    public static int Take(EconomyState e, int id)
    {
        if (e.Objects.ContainsKey(id)) throw new ArgumentException($"Object id {id} is taken.");
        e.NextObjectId = Math.Max(e.NextObjectId, id + 1);
        return id;
    }

    /// <summary>Put an object in the registry under a fresh or explicit id. A capital ship gets its starting hull and counters.</summary>
    public static int Add(EconomyState e, EconObject o, int? id, ShipClass? shipClass = null)
    {
        int newId = id is int x ? Take(e, x) : NextId(e);
        o.Id = newId;
        if (o.Category == ObjectCategory.CapitalShip)
        {
            o.Hull = shipClass?.Hull ?? 0; // rule: m3-ships-and-fighters §Damaged state (hull starts at the class maximum)
            o.Hits = new List<int>(new int[HitCounters]);
        }
        e.Objects[newId] = o;
        return newId;
    }

    /// <summary>A ship is damaged for travel when its hyperdrive counter is above 0. rule: m3-ships-and-fighters §Damaged state (DEFAULT).</summary>
    public static bool IsDamagedForTravel(EconObject o) => o.Hits is { Count: > HyperdriveCounter } h && h[HyperdriveCounter] > 0;
}
