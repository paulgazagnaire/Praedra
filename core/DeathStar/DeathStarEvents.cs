using Rebellion.Core.Clock;

namespace Rebellion.Core.DeathStar;

// Events of the Death Star. Day is K + 1 (slot 5.1 and the construction hooks take effect when Day becomes K + 1). Every event that implements
// IDeathStarEvent is a galaxy notice both sides see (fog does not hide it). rule: m5-death-star §1 (DeathStarDeployed), §2 (SystemDestroyed), §5.

/// <summary>Marks the events both sides are told of ("New Death Star Deployed", "Planet Destroyed", "Death Star Sabotaged"): the fog audience is both. rule: m5-death-star §1, §2 effect 6.</summary>
public interface IDeathStarEvent { }

/// <summary>Order: the Death Star in fleet <paramref name="Fleet"/> destroys the system the fleet is at. Checked in step 1 and again at slot 5.1. rule: m5-death-star §2.</summary>
public sealed record DestroySystemOrder(long Sequence, Side Side, int Fleet) : Order(Sequence, Side);

/// <summary>Rejection reasons of Destroy System that the shared list lacks (the sheet's names). UnknownFleet and NotYours are shared. rule: m5-death-star §1, §2 legality.</summary>
public static class DeathStarReject
{
    public const string NotAtSystem = "NotAtSystem";
    public const string NoDeathStar = "NoDeathStar";
    public const string AlreadyDestroyed = "AlreadyDestroyed";
    public const string ShieldProtected = "ShieldProtected";
    public const string DeathStarLimit = "DeathStarLimit";
    /// <summary>A second Destroy System of the same fleet in one Step (DEFAULT name, as the bombardment's check 6).</summary>
    public const string OrderPending = "OrderPending";
}

/// <summary>A Death Star was completed (or delivered) and stands at <see cref="System"/>. Both sides. rule: m5-death-star §1 ("New Death Star Deployed").</summary>
public sealed record DeathStarDeployed(int Index, int Day, Side Side, int Ship, string System) : GameEvent(Index, Day, nameof(DeathStarDeployed)), IDeathStarEvent;

/// <summary>A Death Star left the game. Cause is "Battle" (normal ship loss), "Sabotage" (Death Star Sabotage) or "DeathStar" (it stood at a system another Death Star destroyed). Both sides. rule: m5-death-star §4, §5.</summary>
public sealed record DeathStarDestroyed(int Index, int Day, Side Side, int Ship, string? System, string Cause) : GameEvent(Index, Day, nameof(DeathStarDestroyed)), IDeathStarEvent;

/// <summary>Slot 5.1: an accepted Destroy System no longer held when its turn came. No other change, no draw. Reason as for a rejection. rule: m5-death-star §2.</summary>
public sealed record DestroySystemCancelled(int Index, int Day, int Fleet, Side Side, string System, string Reason) : GameEvent(Index, Day, nameof(DestroySystemCancelled));

/// <summary>A fleet of the other side (or a neutral one) at the system was destroyed with everything aboard: Units are the ship, squadron and aboard unit ids. Both sides. rule: m5-death-star §2 effect 1.</summary>
public sealed record FleetDestroyedByDeathStar(int Index, int Day, string System, int Fleet, Side FleetSide, ValueList<int> Units) : GameEvent(Index, Day, nameof(FleetDestroyedByDeathStar)), IDeathStarEvent;

/// <summary>A regiment, special force or based squadron of the planet was removed with it. Facilities raise FacilityDestroyed (reason DeathStar). Both sides. rule: m5-death-star §2 effect 2.</summary>
public sealed record PlanetUnitDestroyed(int Index, int Day, string System, int Object, string Class, Side? Owner) : GameEvent(Index, Day, nameof(PlanetUnitDestroyed)), IDeathStarEvent;

/// <summary>The system is destroyed. Both sides (the galaxy notice "Planet Destroyed"). rule: m5-death-star §2 effect 6.</summary>
public sealed record SystemDestroyed(int Index, int Day, string System, Side AttackerSide, Side? PriorHolder) : GameEvent(Index, Day, nameof(SystemDestroyed)), IDeathStarEvent;

/// <summary>The sector reacted to a strike: Strike is the number of strikes in the sector so far (this one included), Systems the other non-destroyed systems that changed (name order). Both sides. rule: m5-death-star §2 effect 7.</summary>
public sealed record SectorStruck(int Index, int Day, string Sector, int Strike, ValueList<string> Systems) : GameEvent(Index, Day, nameof(SectorStruck)), IDeathStarEvent;
