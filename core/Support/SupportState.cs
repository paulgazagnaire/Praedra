using Praedra.Core.Clock;

namespace Praedra.Core.Support;

/// <summary>
/// One system's uprising flag and timers. Hook times: a timer armed in Step K with period P is due at (K + 1) + P and fires in the
/// Step where K + 1 equals it; 0 = not armed. rule: m2-popular-support §Periodic effects; m2-uprisings-and-garrison §Uprising start, keep, end.
/// </summary>
public sealed class SystemSupportState
{
    public bool Uprising { get; set; }
    /// <summary>The rebelling side (the other side than the holder when the uprising started). Meaningful while <see cref="Uprising"/>.</summary>
    public Side Collective { get; set; }
    /// <summary>Timer 0x384, uprising erosion.</summary>
    public int ErosionDue { get; set; }
    /// <summary>Timer 0x38d, incident.</summary>
    public int IncidentDue { get; set; }
    /// <summary>Timer 0x386, presence (blockade).</summary>
    public int PresenceDue { get; set; }
    /// <summary>The blockading side the presence timer was armed for.</summary>
    public Side PresenceSide { get; set; }

    public bool IsIdle => !Uprising && ErosionDue == 0 && IncidentDue == 0 && PresenceDue == 0;
}

/// <summary>
/// Support-package state (the support value itself lives in <see cref="Economy.EconomyState.CollectiveSupport"/>). Sorted
/// collections only, so the saved and hashed form is canonical. Idle entries are removed.
/// </summary>
public sealed class SupportState
{
    public SortedDictionary<string, SystemSupportState> Systems { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Systems carrying the harvester sector flag (every system of the sector). rule: m2-popular-support §harvester sector flag.</summary>
    public SortedSet<string> Harvester { get; init; } = new(StringComparer.Ordinal);

    internal SystemSupportState Touch(string system)
    {
        if (!Systems.TryGetValue(system, out var s)) Systems[system] = s = new SystemSupportState();
        return s;
    }

    internal void Prune(string system)
    {
        if (Systems.TryGetValue(system, out var s) && s.IsIdle) Systems.Remove(system);
    }

    internal void CopyFrom(SupportState o)
    {
        foreach (var kv in o.Systems) Systems[kv.Key] = kv.Value;
        foreach (var s in o.Harvester) Harvester.Add(s);
    }
}
