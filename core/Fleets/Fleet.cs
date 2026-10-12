using Rebellion.Core.Clock;

namespace Rebellion.Core.Fleets;

/// <summary>
/// A fleet: capital ships (or, for a squadron-only fleet, flying squadrons), a side and a place. Part of the game state and of the save.
/// rule: m3-fleets §What a fleet is (data model). Members are object ids of the one registry (<see cref="Economy.EconomyState.Objects"/>),
/// ascending, so the saved and hashed form is canonical; classes, damage and what rides aboard are read from the registry.
/// Squadrons and regiments aboard a ship are NOT members: they carry <see cref="Economy.EconObject.AboardShip"/>.
/// Systems are named, not numbered, so a save does not depend on the galaxy's index order.
/// </summary>
public sealed class Fleet
{
    public int Id { get; set; }
    /// <summary>Display only. rule: m3-fleets §Ownership, limits, side effects (fleet names are display only).</summary>
    public string Name { get; set; } = "";
    public Side Side { get; set; }
    /// <summary>Capital ship object ids, ascending. A fleet has ships, or only squadrons, never both. rule: m3-fleets §What a fleet is (4).</summary>
    public List<int> Ships { get; set; } = new();
    /// <summary>Squadron object ids, ascending; only for a squadron-only fleet while it flies (it dissolves on landing). rule: m3-fleets §Unescorted squadrons.</summary>
    public List<int> Squadrons { get; set; } = new();
    /// <summary>The side's spare fleet at its system: where new ships join. Cleared by an accepted MoveFleet or by a merge. rule: m3-fleets §New ships: the spare fleet.</summary>
    public bool Spare { get; set; }
    /// <summary>The system the fleet is at; null while it is on a lane.</summary>
    public string? At { get; set; }
    /// <summary>
    /// The system the fleet last left. Kept after a stop or an arrival so "retreat along the lane it arrived on" stays
    /// possible in M4. rule: m1-forced-stop-and-blockade §Defaults (previous system).
    /// </summary>
    public string? PreviousSystem { get; set; }
    /// <summary>Set while the fleet has an accepted order that has not finished. rule: m1-travel §Orders in transit.</summary>
    public FleetTransit? Transit { get; set; }
}

/// <summary>
/// The in-transit state: the path fixed when the order was accepted, the hop being flown and the day it ends.
/// rule: m1-travel §Path rule (5) (path fixed at order time), m1-clock-and-orders §Order of work (steps 2 to 4).
/// </summary>
public sealed class FleetTransit
{
    /// <summary>System names from start to end, inclusive.</summary>
    public List<string> Path { get; set; } = new();
    /// <summary>Ticks of each hop (Path.Count - 1 entries).</summary>
    public List<int> HopTicks { get; set; } = new();
    /// <summary>The hop being flown: from Path[HopIndex] to Path[HopIndex + 1].</summary>
    public int HopIndex { get; set; }
    /// <summary>The day the current hop ends. The fleet is placed during the Step whose K is DueDay - 1.</summary>
    public int DueDay { get; set; }
}
