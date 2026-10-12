using Rebellion.Core.Clock;

namespace Rebellion.Core.Fleets;

/// <summary>
/// A fleet: ships plus the squadrons aboard, a side and a place. Part of the game state and of the save.
/// rule: m3-fleets §What a fleet is (data model) (only what a fleet IS; creation, merge and transfer orders are M3).
/// Ships and squadrons are ship-class names (one entry per ship or squadron), kept in ordinal order so the saved and
/// hashed form is canonical. Systems are named, not numbered, so a save does not depend on the galaxy's index order.
/// </summary>
public sealed class Fleet
{
    public int Id { get; set; }
    /// <summary>Display only. rule: m3-fleets §Ownership, limits, side effects (fleet names are display only).</summary>
    public string Name { get; set; } = "";
    public Side Side { get; set; }
    /// <summary>Capital ship classes, one entry per ship, ordinal order.</summary>
    public List<string> Ships { get; set; } = new();
    /// <summary>Squadron classes aboard (or, for a squadron-only fleet, flying alone), one entry per squadron, ordinal order.</summary>
    public List<string> Squadrons { get; set; } = new();
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
