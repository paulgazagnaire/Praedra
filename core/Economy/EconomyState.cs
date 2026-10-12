using Rebellion.Core.Clock;

namespace Rebellion.Core.Economy;

/// <summary>
/// An economy object: a facility, ship, fighter squadron, regiment or special force that counts for maintenance and can be
/// scrapped. Facilities have no side of their own: they belong to whoever controls their system (m2-mines-refineries-pools
/// §Side change). A facility that is not Completed is queued or in delivery: it counts for load and reserves its slot on
/// <see cref="System"/> (its destination) but is not installed. rule: m2-maintenance §Load (Existing); m2-slots-and-resources
/// §Occupied slots.
///
/// SEAM: ship and fighter objects here are not linked to the fleets of m3-fleets (fleet ships carry no object id yet), so a
/// fleet's ships do not count for load and cannot be autoscrapped until construction and fleets share one object list.
/// </summary>
public sealed class EconObject
{
    public int Id { get; set; }
    public ObjectCategory Category { get; set; }
    /// <summary>Class name as in the costs file ("Mine", "Imperial Star Destroyer", ...).</summary>
    public string Class { get; set; } = "";
    /// <summary>Owner of a non-facility object. Null for facilities (owner = owner of <see cref="System"/>).</summary>
    public Side? Side { get; set; }
    /// <summary>Facility: the system it stands on or will stand on. Regiments and special forces: where they are.</summary>
    public string? System { get; set; }
    public bool Completed { get; set; }
    // ---- mine and refinery cycle (m2-mines-refineries-pools)
    /// <summary>True once its first cycle has been started (the first-cycle roll is spent).</summary>
    public bool FirstDone { get; set; }
    /// <summary>Hook time at which the running cycle ends; 0 = no running cycle.</summary>
    public int Due { get; set; }
    /// <summary>A refinery waiting in its side's raw line.</summary>
    public bool InLine { get; set; }
}

/// <summary>A system's slot values when they differ from the data file (incidents, rolled starts, test setups).</summary>
public sealed class SlotState
{
    public int Energy { get; set; }
    public int Raw { get; set; }
    public bool Populated { get; set; } = true;
}

/// <summary>One side's pools, raw line and autoscrap timer. rule: m2-mines-refineries-pools §Pools; m2-maintenance §Overdraft.</summary>
public sealed class SidePool
{
    public int Raw { get; set; }
    public int Refined { get; set; }
    /// <summary>Hook time of the next autoscrap firing; 0 = not yet armed (first due at the scrap period).</summary>
    public int ScrapDue { get; set; }
    /// <summary>Refineries waiting for raw, first in first out (facility ids).</summary>
    public List<int> RawLine { get; set; } = new();

    internal void CopyFrom(SidePool o) { Raw = o.Raw; Refined = o.Refined; ScrapDue = o.ScrapDue; RawLine = new List<int>(o.RawLine); }
}

/// <summary>All economy state. Sorted collections only, so the saved and hashed form is canonical.</summary>
public sealed class EconomyState
{
    /// <summary>The id the next object gets. DEFAULT: ids start at 1 (no sheet says).</summary>
    public int NextObjectId { get; set; } = 1;
    public SortedDictionary<int, EconObject> Objects { get; init; } = new();
    /// <summary>Slot values that differ from the file, by system name.</summary>
    public SortedDictionary<string, SlotState> Slots { get; init; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Alliance support per system, 0..100 (Empire support = 100 - this). Systems not listed use the default. Read-only for
    /// the economy; the support package drives it. rule: m2-popular-support §What it stores.
    /// </summary>
    public SortedDictionary<string, int> AllianceSupport { get; init; } = new(StringComparer.Ordinal);
    public SidePool Empire { get; init; } = new();
    public SidePool Alliance { get; init; } = new();
    /// <summary>Hook time the resource incident timer fires; 0 = not armed.</summary>
    public int ResourceDue { get; set; }
    /// <summary>Hook time the disaster timer fires; 0 = not armed.</summary>
    public int DisasterDue { get; set; }

    public SidePool Pool(Side s) => s == Side.Empire ? Empire : Alliance;

    internal void CopyFrom(EconomyState o)
    {
        NextObjectId = o.NextObjectId;
        foreach (var kv in o.Objects) Objects[kv.Key] = kv.Value;
        foreach (var kv in o.Slots) Slots[kv.Key] = kv.Value;
        foreach (var kv in o.AllianceSupport) AllianceSupport[kv.Key] = kv.Value;
        Empire.CopyFrom(o.Empire);
        Alliance.CopyFrom(o.Alliance);
        ResourceDue = o.ResourceDue;
        DisasterDue = o.DisasterDue;
    }
}
