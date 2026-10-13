namespace Rebellion.Core.DeathStar;

/// <summary>
/// The Death Star's saved state: the systems that were destroyed (forever) and the strikes counted per sector over the whole game. Sorted
/// collections only (ordinal names), so the saved and hashed form is canonical. Both are in the save and the state hash.
/// rule: m5-death-star §Interfaces (new core state: per system <c>Destroyed</c>, per sector <c>StrikesInSector</c>).
/// </summary>
public sealed class DeathStarState
{
    /// <summary>Systems that are Destroyed (a system not listed is intact). A destroyed system stays on the map as a passable debris field. rule: m5-death-star §2 effect 5.</summary>
    public SortedSet<string> Destroyed { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Strikes so far per sector name, for the whole game (counts strikes, not remaining systems). Only sectors with at least one strike are
    /// listed. rule: m5-death-star §2 effect 7 (StrikesInSector).
    /// </summary>
    public SortedDictionary<string, int> StrikesInSector { get; init; } = new(StringComparer.Ordinal);

    public bool IsDestroyed(string system) => Destroyed.Contains(system);

    internal void CopyFrom(DeathStarState o)
    {
        foreach (var s in o.Destroyed) Destroyed.Add(s);
        foreach (var kv in o.StrikesInSector) StrikesInSector[kv.Key] = kv.Value;
    }
}
