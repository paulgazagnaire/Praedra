namespace Praedra.Core.Economy;

/// <summary>
/// The registry objects by the system they stand on (<see cref="EconObject.System"/>) and by the ship they ride on
/// (<see cref="EconObject.AboardShip"/>), each list in id order: exactly what a scan of <see cref="EconomyState.Objects"/> filtered on
/// <c>o.System == system</c> (or <c>o.AboardShip == ship</c>) gives, in the same order, without walking every object.
/// Derived data only (QA senior QS-1): never saved or hashed, built from the registry on first use and after a load, and kept up to date at
/// the places that change it: <see cref="ObjectRegistry.Add"/>, the economy's removal of an object, and the <see cref="EconObject.System"/>
/// and <see cref="EconObject.AboardShip"/> setters. So it is right at any moment, inside a Step too. A registry whose object count no longer matches is rebuilt (a safety net for
/// a change that went around those places).
/// A list must not be kept across a change of the state: read it, or copy it (ToList) before moving objects.
/// <see cref="PlacementVersion"/> changes whenever an object enters or leaves the registry or changes Category, Class, Side, System,
/// Completed or AboardShip, so a reader can keep a result computed from those fields only until it changes.
/// </summary>
internal sealed class ObjectIndex
{
    private static readonly IReadOnlyList<EconObject> None = Array.Empty<EconObject>();

    private readonly EconomyState _e;
    private Dictionary<string, List<EconObject>>? _bySystem; // lookup only, never iterated
    private Dictionary<int, List<EconObject>>? _byShip;      // lookup only, never iterated
    private int _count;
    private long _version;

    /// <summary>
    /// A number that changes whenever an object enters or leaves the registry or changes Category, Class, Side, System, Completed or
    /// AboardShip (and when the index is rebuilt). Equal numbers mean none of those changed in between.
    /// </summary>
    public long PlacementVersion
    {
        get
        {
            if (_bySystem == null || _count != _e.Objects.Count) Rebuild(); // so every object of the registry reports its changes from here on
            return _version;
        }
    }

    public ObjectIndex(EconomyState e) { _e = e; }

    /// <summary>The objects whose System is <paramref name="system"/>, id order.</summary>
    public IReadOnlyList<EconObject> At(string system)
    {
        if (_bySystem == null || _count != _e.Objects.Count) Rebuild();
        return _bySystem!.TryGetValue(system, out var l) ? l : None;
    }

    /// <summary>The objects whose AboardShip is <paramref name="ship"/> (not 0), id order.</summary>
    public IReadOnlyList<EconObject> Aboard(int ship)
    {
        if (_bySystem == null || _count != _e.Objects.Count) Rebuild();
        return _byShip!.TryGetValue(ship, out var l) ? l : None;
    }

    /// <summary>Forget everything; the next read rebuilds from the registry (after the registry was replaced, e.g. on load).</summary>
    public void Invalidate() { _bySystem = null; _version++; }

    /// <summary>An object of the registry changed a field that says where it is or what it is (other than System).</summary>
    public void Changed() => _version++;

    private void Rebuild()
    {
        var map = new Dictionary<string, List<EconObject>>(StringComparer.Ordinal);
        var ships = new Dictionary<int, List<EconObject>>();
        foreach (var o in _e.Objects.Values) // id order, so every list is in id order
        {
            o.Index = this;
            if (o.System != null) Bucket(map, o.System).Add(o);
            if (o.AboardShip != 0) Bucket(ships, o.AboardShip).Add(o);
        }
        _bySystem = map; _byShip = ships;
        _count = _e.Objects.Count;
        _version++;
    }

    private static List<EconObject> Bucket<TKey>(Dictionary<TKey, List<EconObject>> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var l)) map[key] = l = new List<EconObject>();
        return l;
    }

    /// <summary>An object entered the registry.</summary>
    public void Added(EconObject o)
    {
        o.Index = this;
        _version++;
        if (_bySystem == null) return; // built on first read
        if (o.System != null) Insert(Bucket(_bySystem, o.System), o);
        if (o.AboardShip != 0) Insert(Bucket(_byShip!, o.AboardShip), o);
        _count++;
    }

    /// <summary>An object left the registry.</summary>
    public void Removed(EconObject o)
    {
        if (o.Index == this) o.Index = null;
        _version++;
        if (_bySystem == null) return;
        if (o.System != null && _bySystem.TryGetValue(o.System, out var l)) Delete(l, o);
        if (o.AboardShip != 0 && _byShip!.TryGetValue(o.AboardShip, out var a)) Delete(a, o);
        _count--;
    }

    /// <summary>An object of the registry changed its System from <paramref name="from"/>.</summary>
    public void Moved(EconObject o, string? from)
    {
        _version++;
        if (_bySystem == null) return;
        if (from != null && _bySystem.TryGetValue(from, out var l)) Delete(l, o);
        if (o.System != null) Insert(Bucket(_bySystem, o.System), o);
    }

    /// <summary>An object of the registry changed its AboardShip from <paramref name="from"/>.</summary>
    public void Boarded(EconObject o, int from)
    {
        _version++;
        if (_bySystem == null) return;
        if (from != 0 && _byShip!.TryGetValue(from, out var l)) Delete(l, o);
        if (o.AboardShip != 0) Insert(Bucket(_byShip!, o.AboardShip), o);
    }

    private static int Find(List<EconObject> l, int id)
    {
        int lo = 0, hi = l.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, c = l[mid].Id.CompareTo(id);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }

    private static void Insert(List<EconObject> l, EconObject o)
    {
        int i = Find(l, o.Id);
        if (i < 0) l.Insert(~i, o);
    }

    private static void Delete(List<EconObject> l, EconObject o)
    {
        int i = Find(l, o.Id);
        if (i >= 0) l.RemoveAt(i);
    }
}
