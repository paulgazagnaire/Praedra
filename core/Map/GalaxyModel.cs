namespace Praedra.Core.Map;

/// <summary>One system (a place you can own, fly to and fight over). rule: m1-galaxy-data §What the loader reads.</summary>
public sealed class StarSystem
{
    /// <summary>Position in the galaxy's ordinal-name order. Stable for a given data set.</summary>
    public int Index { get; }
    public string Name { get; }
    /// <summary>Sector name, or <see cref="Galaxy.OutsideSectorsLabel"/>.</summary>
    public string Sector { get; }
    public string Group { get; }
    public int MapX { get; }
    public int MapY { get; }
    /// <summary>All other planets-file columns (canon_x, origin, tier, affiliation, ...). Carried, never used for rules.</summary>
    public IReadOnlyDictionary<string, string> Carried { get; }

    public StarSystem(int index, string name, string sector, string group, int mapX, int mapY,
        IReadOnlyDictionary<string, string> carried)
    {
        Index = index; Name = name; Sector = sector; Group = group; MapX = mapX; MapY = mapY; Carried = carried;
    }

    public bool IsOutsideSectors => Sector == Galaxy.OutsideSectorsLabel;
    public override string ToString() => Name;
}

/// <summary>A neighbourhood of up to 10 systems. rule: m1-galaxy-data §Derived fields (Sector members).</summary>
public sealed class Sector
{
    public string Name { get; }
    public string Group { get; }
    /// <summary>Member system indices, sorted by system name (ordinal).</summary>
    public IReadOnlyList<int> Members { get; }

    public Sector(string name, string group, IReadOnlyList<int> members)
    {
        Name = name; Group = group; Members = members;
    }
}

/// <summary>
/// An undirected route link (still called a lane in code): two systems joined by an official hyperspace route, major (tier 1)
/// or minor (tier 2). Since 2026-10-16 ships fly anywhere; a link only makes the stretch between its two ends faster. Identity is the pair of names (m1-galaxy-data DEFAULT: no lane id). A is the smaller name (ordinal).
/// </summary>
public sealed class Lane
{
    public int A { get; }
    public int B { get; }
    public string NameA { get; }
    public string NameB { get; }
    public string Kind { get; }
    public string Routes { get; }
    /// <summary>1, 2 or 3. rule: m1-galaxy-data §Derived fields (Lane tier).</summary>
    public int Tier { get; }
    /// <summary>Percent from the tier table (data/design/lane-tiers.csv).</summary>
    public int FactorPercent { get; }
    /// <summary>isqrt(dx*dx+dy*dy) / 5. rule: m1-galaxy-data §Derived fields (Lane base distance).</summary>
    public int BaseDistance { get; }

    public Lane(int a, int b, string nameA, string nameB, string kind, string routes, int tier, int factorPercent, int baseDistance)
    {
        A = a; B = b; NameA = nameA; NameB = nameB; Kind = kind; Routes = routes;
        Tier = tier; FactorPercent = factorPercent; BaseDistance = baseDistance;
    }

    public int Other(int system) => system == A ? B : A;
}

/// <summary>A lane seen from one of its ends.</summary>
public readonly record struct Neighbour(int System, Lane Lane);

/// <summary>Immutable galaxy: systems sorted by ordinal name, sectors, lanes and neighbour lists sorted by name.</summary>
public sealed class Galaxy
{
    /// <summary>Sector label for the two outsiders. rule: m1-galaxy-data §What the loader reads.</summary>
    public const string OutsideSectorsLabel = "none (outside sectors)";

    private readonly Dictionary<string, int> _byName;
    private readonly Neighbour[][] _neighbours;
    private readonly int[][] _near;

    public IReadOnlyList<StarSystem> Systems { get; }
    public IReadOnlyList<Sector> Sectors { get; }
    public IReadOnlyList<Lane> Lanes { get; }
    /// <summary>Non-fatal findings, e.g. stored lane_count/chokepoint differing from the recomputed values (check 10).</summary>
    public IReadOnlyList<string> Warnings { get; }

    internal Galaxy(IReadOnlyList<StarSystem> systems, IReadOnlyList<Sector> sectors, IReadOnlyList<Lane> lanes,
        Neighbour[][] neighbours, int[][] near, IReadOnlyList<string> warnings)
    {
        Systems = systems; Sectors = sectors; Lanes = lanes; _neighbours = neighbours; _near = near; Warnings = warnings;
        _byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in systems) _byName[s.Name] = s.Index;
    }

    public bool TryFindSystem(string name, out int index) => _byName.TryGetValue(name, out index);

    public StarSystem Get(string name) => Systems[_byName[name]];

    /// <summary>Lanes at a system, sorted by neighbour name (ordinal). rule: m1-galaxy-data §Derived fields (Neighbours).</summary>
    public IReadOnlyList<Neighbour> NeighboursOf(int system) => _neighbours[system];

    /// <summary>
    /// Nearby systems, sorted by name (ordinal): the map-reading neighbourhood used where the rules once counted lane hops (AI reach,
    /// setup pockets, character moves). One "step" = a route link or a short free jump. rule: m1-galaxy-data §Derived fields (Nearby).
    /// </summary>
    public IReadOnlyList<int> NearOf(int system) => _near[system];

    /// <summary>True when the system lies on a hyperspace route (has at least one route link). rule: m1-travel §Rule, m5-fog-and-hq N4 (Sensor Stations).</summary>
    public bool OnRoute(int system) => _neighbours[system].Length > 0;

    /// <summary>The route link between two systems, or null.</summary>
    public Lane? FindLane(int a, int b)
    {
        foreach (var n in _neighbours[a]) if (n.System == b) return n.Lane;
        return null;
    }

    public Lane? FindLane(string a, string b)
    {
        if (!_byName.TryGetValue(a, out var ia) || !_byName.TryGetValue(b, out var ib)) return null;
        foreach (var n in _neighbours[ia]) if (n.System == ib) return n.Lane;
        return null;
    }
}
