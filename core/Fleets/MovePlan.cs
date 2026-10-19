using Praedra.Core.Travel;

namespace Praedra.Core.Fleets;

/// <summary>A system on a planned path where the planning side's picture shows an enemy gravity-well ship, and the day that picture was taken. rule: m1-travel §Path rule (3).</summary>
public sealed record KnownTrap(string System, int SeenDay);

/// <summary>
/// The answer to PlanMove: what a MoveFleet order with these waypoints would fly if accepted at the next Step. Path and hops are the exact
/// ones the order takes (same function). RouteHop[i] is true when hop i flies along a hyperspace route link (the fast stretch), false for a
/// free straight jump. ArrivalDay is the day the fleet reaches the destination if nothing pulls it out. Traps are the systems passed on the
/// way (not the start, not the destination) where the side's own picture shows an enemy gravity-well ship; unknown ones are not listed
/// (fog). rule: m1-travel §Path rule (3) (known traps, waypoints) and (5) (preview).
/// </summary>
public sealed record MovePlan(PathStatus Status, IReadOnlyList<string> Path, IReadOnlyList<int> HopTicks, IReadOnlyList<bool> RouteHop,
    int ArrivalDay, IReadOnlyList<KnownTrap> Traps)
{
    public bool Ok => Status == PathStatus.Ok;
    public bool Red => Traps.Count > 0;
}
