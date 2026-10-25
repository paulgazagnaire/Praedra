using Praedra.Core.Clock;

namespace Praedra.Core.Harvester;

// Events of the harvester. Day is K + 1 (slot 5.1 and the construction hooks take effect when Day becomes K + 1). Every event that implements
// IHarvesterEvent is a galaxy notice both sides see (fog does not hide it). rule: m5-harvester §1 (HarvesterDeployed), §2 (SystemDestroyed), §5.

/// <summary>Marks the events both sides are told of ("New harvester Deployed", "Planet Destroyed", "Spike the Harvesterd"): the fog audience is both. rule: m5-harvester §1, §2 effect 6.</summary>
public interface IHarvesterEvent { }

/// <summary>Order: the harvester in fleet <paramref name="Fleet"/> destroys the system the fleet is at. Checked in step 1 and again at slot 5.1. rule: m5-harvester §2.</summary>
public sealed record DestroySystemOrder(long Sequence, Side Side, int Fleet) : Order(Sequence, Side);

/// <summary>Rejection reasons of Destroy System that the shared list lacks (the sheet's names). UnknownFleet and NotYours are shared. rule: m5-harvester §1, §2 legality.</summary>
public static class HarvesterReject
{
    public const string NotAtSystem = "NotAtSystem";
    public const string NoHarvester = "NoHarvester";
    public const string AlreadyDestroyed = "AlreadyDestroyed";
    public const string HarvesterLimit = "HarvesterLimit";
    /// <summary>A second Destroy System of the same fleet in one Step (DEFAULT name, as the bombardment's check 6).</summary>
    public const string OrderPending = "OrderPending";
}

/// <summary>A harvester was completed (or delivered) and stands at <see cref="System"/>. Both sides. rule: m5-harvester §1 ("New harvester Deployed").</summary>
public sealed record HarvesterDeployed(int Index, int Day, Side Side, int Ship, string System) : GameEvent(Index, Day, nameof(HarvesterDeployed)), IHarvesterEvent;

/// <summary>A harvester left the game. Cause is "Battle" (normal ship loss), "Sabotage" (Spike the Harvester) or "Harvester" (it stood at a system another harvester destroyed). Both sides. rule: m5-harvester §4, §5.</summary>
public sealed record HarvesterDestroyed(int Index, int Day, Side Side, int Ship, string? System, string Cause) : GameEvent(Index, Day, nameof(HarvesterDestroyed)), IHarvesterEvent;

/// <summary>Slot 5.1: an accepted Destroy System no longer held when its turn came. No other change, no draw. Reason as for a rejection. rule: m5-harvester §2.</summary>
public sealed record DestroySystemCancelled(int Index, int Day, int Fleet, Side Side, string System, string Reason) : GameEvent(Index, Day, nameof(DestroySystemCancelled));

/// <summary>A fleet of the other side (or a neutral one) at the system was destroyed with everything aboard: Units are the ship, squadron and aboard unit ids. Both sides. rule: m5-harvester §2 effect 1.</summary>
public sealed record FleetDestroyedByHarvester(int Index, int Day, string System, int Fleet, Side FleetSide, ValueList<int> Units) : GameEvent(Index, Day, nameof(FleetDestroyedByHarvester)), IHarvesterEvent;

/// <summary>A regiment, special force or based squadron of the planet was removed with it. Facilities raise FacilityDestroyed (reason Harvester). Both sides. rule: m5-harvester §2 effect 2.</summary>
public sealed record PlanetUnitDestroyed(int Index, int Day, string System, int Object, string Class, Side? Owner) : GameEvent(Index, Day, nameof(PlanetUnitDestroyed)), IHarvesterEvent;

/// <summary>The system is destroyed. Both sides (the galaxy notice "Planet Destroyed"). rule: m5-harvester §2 effect 6.</summary>
public sealed record SystemDestroyed(int Index, int Day, string System, Side AttackerSide, Side? PriorHolder) : GameEvent(Index, Day, nameof(SystemDestroyed)), IHarvesterEvent;

/// <summary>The sector reacted to a strike: Strike is the number of strikes in the sector so far (this one included), Systems the other non-destroyed systems that changed (name order). Both sides. rule: m5-harvester §2 effect 7.</summary>
public sealed record SectorStruck(int Index, int Day, string Sector, int Strike, ValueList<string> Systems) : GameEvent(Index, Day, nameof(SectorStruck)), IHarvesterEvent;
