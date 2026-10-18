using Rebellion.Core.Clock;

namespace Rebellion.Core.Economy;

/// <summary>
/// An economy object: a facility, ship, fighter squadron, regiment or special force that counts for maintenance and can be
/// scrapped. Facilities have no side of their own: they belong to whoever controls their system (m2-mines-refineries-pools
/// §Side change). A facility that is not Completed is queued or in delivery: it counts for load and reserves its slot on
/// <see cref="System"/> (its destination) but is not installed. rule: m2-maintenance §Load (Existing); m2-slots-and-resources
/// §Occupied slots.
///
/// This list is the one registry of every ship, squadron, regiment and special force (m3-ships-and-fighters §The ship object): a fleet
/// holds object ids (<see cref="Fleets.Fleet.Ships"/>), so a fleet's ships count for load and can be autoscrapped like any object.
/// Place of a non-facility object: a fleet member (capital ship, <see cref="System"/> null); a squadron or regiment on a system
/// (<see cref="System"/> set, <see cref="AboardShip"/> 0) or aboard a ship (<see cref="AboardShip"/> set, <see cref="System"/> null).
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
    // ---- carrying (m3-fleets §Carrying rules)
    /// <summary>Squadron or regiment: the id of the capital ship it rides on; 0 = not aboard (it is on <see cref="System"/> or in a squadron-only fleet).</summary>
    public int AboardShip { get; set; }
    // ---- ship state (m3-ships-and-fighters §Damaged state). Capital ships only; no M3 rule changes them except DebugSetDamage.
    /// <summary>Current hull, 0 to the class maximum; starts at the maximum. 0 for everything that is not a capital ship.</summary>
    public int Hull { get; set; }
    /// <summary>Six damage counters 0 to 15 (shield generator, weapons, tractor, sub-light, hyperdrive, damage control); null for non-ships.</summary>
    public List<int>? Hits { get; set; }
    /// <summary>
    /// Squadron: craft lost in Classic battles and not yet repaired (it fights with squadron_size - this, at least 1). Saved and hashed; left out of
    /// the saved form while 0. rule: m4-battle-classic §10; m4-retreat-damage-repair §4, §5.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int CraftLost { get; set; }
    /// <summary>
    /// Strategic repair timer: 0 = not running; 1 = running at the 30-day period, 2 = at the FastRepair period. Saved and hashed (left out while 0).
    /// rule: m4-retreat-damage-repair §5.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int RepairMode { get; set; }
    /// <summary>Hook time of the next repair turn while <see cref="RepairMode"/> is not 0. rule: m4-retreat-damage-repair §5.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int RepairDue { get; set; }
    // ---- mine and refinery cycle (m2-mines-refineries-pools)
    /// <summary>True once its first cycle has been started (the first-cycle roll is spent).</summary>
    public bool FirstDone { get; set; }
    /// <summary>Hook time at which the running cycle ends; 0 = no running cycle.</summary>
    public int Due { get; set; }
    /// <summary>A refinery waiting in its side's raw line.</summary>
    public bool InLine { get; set; }
    /// <summary>
    /// Display name of a capital ship ("Corellian Corvette 1"), given at setup; null = none (shown by class). Display only, never read by a rule.
    /// Omitted from the save while null, so saves and hashes of games that name nothing are unchanged. rule: m5-setup-and-victory §P7 Names (B4 §12).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
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
