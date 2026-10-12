using Rebellion.Core.Clock;

namespace Rebellion.Core.Construction;

/// <summary>The three yard families (the 1998 family ids). rule: m2-construction §Yard families.</summary>
public enum YardFamily
{
    /// <summary>0x28 Orbital Shipyard: capital ships and fighters.</summary>
    Shipyard = 0x28,
    /// <summary>0x29 Training Facility: regiments and special forces.</summary>
    Training = 0x29,
    /// <summary>0x2a Construction Yard: facilities (mines, refineries, yards, defences).</summary>
    Construction = 0x2a,
}

/// <summary>
/// The one active project of a (system, family): the units of the last accepted order, built one after another. Units are
/// economy object ids (all of them exist from acceptance: DEFAULT, m2-construction §Build order). Units[0] is the current one.
/// rule: m2-construction §Projects and queues.
/// </summary>
public sealed class Project
{
    public Side Side { get; set; }
    public string System { get; set; } = "";
    public YardFamily Family { get; set; }
    /// <summary>Where finished units go (the yard's own system, or another system of the side).</summary>
    public string Destination { get; set; } = "";
    /// <summary>Object ids not yet finished, in build order.</summary>
    public List<int> Units { get; set; } = new();
    /// <summary>Points of the current unit (one per finished yard cycle).</summary>
    public int Progress { get; set; }
}

/// <summary>
/// A finished unit travelling along lanes to its destination. Not a fleet: no contact, no battle, never "present" for the forced
/// stop or the blockade flag; it can only be held. rule: m2-construction §Completion and delivery (Proposal for the object).
/// </summary>
public sealed class Delivery
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int Object { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>System names from the yard to the destination, fixed at departure.</summary>
    public List<string> Path { get; set; } = new();
    public List<int> HopTicks { get; set; } = new();
    /// <summary>Path position last reached (or left). While <see cref="InFlight"/>, the hop Path[Index] to Path[Index + 1] is being flown.</summary>
    public int Index { get; set; }
    public bool InFlight { get; set; }
    /// <summary>The day the hop in flight ends (placed in the Step whose K is DueDay - 1).</summary>
    public int DueDay { get; set; }
    /// <summary>Sitting at Path[Index] and not allowed to go on (enemy fleet, or destination lost).</summary>
    public bool Held { get; set; }
}

/// <summary>One side's construction data: the refined line (yards waiting, first in first out) and the research grants.</summary>
public sealed class SideConstruction
{
    /// <summary>Yard object ids waiting for refined, first in first out. rule: m2-mines-refineries-pools §Asking for raw / serving a line.</summary>
    public List<int> RefinedLine { get; set; } = new();
    /// <summary>Class names granted by setup or a test whatever the research level (ordinal order). rule: m5-research §Availability test (step 3).</summary>
    public List<string> Granted { get; set; } = new();

    internal void CopyFrom(SideConstruction o) { RefinedLine = new List<int>(o.RefinedLine); Granted = new List<string>(o.Granted); }
}

/// <summary>All construction state. Sorted collections only, so the saved and hashed form is canonical.</summary>
public sealed class ConstructionState
{
    /// <summary>The id the next delivery gets. DEFAULT: ids start at 1.</summary>
    public int NextDeliveryId { get; set; } = 1;
    /// <summary>Projects by system name (ordinal), then family id (0x28, 0x29, 0x2a): the processing order of slot 6.6.b.</summary>
    public SortedDictionary<string, SortedDictionary<int, Project>> Projects { get; init; } = new(StringComparer.Ordinal);
    public SortedDictionary<int, Delivery> Deliveries { get; init; } = new();
    public SideConstruction Empire { get; init; } = new();
    public SideConstruction Alliance { get; init; } = new();

    public SideConstruction Of(Side s) => s == Side.Empire ? Empire : Alliance;

    public Project? Find(string system, YardFamily family) =>
        Projects.TryGetValue(system, out var by) && by.TryGetValue((int)family, out var p) ? p : null;

    public void Set(Project p)
    {
        if (!Projects.TryGetValue(p.System, out var by)) Projects[p.System] = by = new SortedDictionary<int, Project>();
        by[(int)p.Family] = p;
    }

    public void Remove(Project p)
    {
        if (!Projects.TryGetValue(p.System, out var by)) return;
        by.Remove((int)p.Family);
        if (by.Count == 0) Projects.Remove(p.System);
    }

    /// <summary>Every project in processing order (system ordinal, family id).</summary>
    public List<Project> AllProjects() => Projects.Values.SelectMany(d => d.Values).ToList();

    internal void CopyFrom(ConstructionState o)
    {
        NextDeliveryId = o.NextDeliveryId;
        foreach (var by in o.Projects)
            foreach (var kv in by.Value)
            {
                if (!Projects.TryGetValue(by.Key, out var mine)) Projects[by.Key] = mine = new SortedDictionary<int, Project>();
                mine[kv.Key] = kv.Value;
            }
        foreach (var kv in o.Deliveries) Deliveries[kv.Key] = kv.Value;
        Empire.CopyFrom(o.Empire);
        Alliance.CopyFrom(o.Alliance);
    }
}
