namespace Praedra.Core.Harvester;

/// <summary>
/// The harvester's saved state: the systems that were destroyed (forever) and the strikes counted per sector over the whole game. Sorted
/// collections only (ordinal names), so the saved and hashed form is canonical. Both are in the save and the state hash.
/// rule: m5-harvester §Interfaces (new core state: per system <c>Destroyed</c>, per sector <c>StrikesInSector</c>).
/// </summary>
public sealed class HarvesterState
{
    /// <summary>Systems that are Destroyed (a system not listed is intact). A destroyed system stays on the map as a passable debris field. rule: m5-harvester §2 effect 5.</summary>
    public SortedSet<string> Destroyed { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Strikes so far per sector name, for the whole game (counts strikes, not remaining systems). Only sectors with at least one strike are
    /// listed. rule: m5-harvester §2 effect 7 (StrikesInSector).
    /// </summary>
    public SortedDictionary<string, int> StrikesInSector { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The day each system was destroyed, so the screens can still say when after a load. Null until the first strike, and then omitted from the
    /// save and the hash while null, so games without a strike keep their hash. rule: m5-harvester §7 Harvest report.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, int>? DestroyedOn { get; set; }

    public bool IsDestroyed(string system) => Destroyed.Contains(system);

    /// <summary>The day the system was destroyed; null when intact (or destroyed in a save written before the day was kept).</summary>
    public int? DestroyedDay(string system) => DestroyedOn != null && DestroyedOn.TryGetValue(system, out int d) ? d : null;

    internal void CopyFrom(HarvesterState o)
    {
        foreach (var s in o.Destroyed) Destroyed.Add(s);
        foreach (var kv in o.StrikesInSector) StrikesInSector[kv.Key] = kv.Value;
        DestroyedOn = o.DestroyedOn == null ? null : new SortedDictionary<string, int>(o.DestroyedOn, StringComparer.Ordinal);
    }
}
