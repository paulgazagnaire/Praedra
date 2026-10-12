using System.Globalization;
using Rebellion.Core.Data;

namespace Rebellion.Core.Map;

/// <summary>Loader limits. rule: m1-galaxy-data §Validation checks (5, 6, 7). Data values, not buried constants.</summary>
public sealed record GalaxyRules
{
    /// <summary>Sector cap (Decided, Economy thread; planets/14).</summary>
    public int MaxSectorSize { get; init; } = 10;
    /// <summary>Map is 0..1023 on both axes (planets/11).</summary>
    public int MapMax { get; init; } = 1023;
    /// <summary>The only systems allowed outside sectors (Decided, planets/14). They carry no lanes.</summary>
    public IReadOnlyList<string> Outsiders { get; init; } = new[] { "Csilla", "Ilum" };

    public static readonly GalaxyRules Default = new();
}

/// <summary>Counts the real data must match, so a silent edit shows up. rule: m1-galaxy-data §Validation checks (9).</summary>
public sealed record ExpectedGalaxyCounts(int Systems, int Sectors, int Lanes, int Tier1Lanes, int Tier2Lanes, int Tier3Lanes)
{
    public static ExpectedGalaxyCounts Parse(string text, string source = "expected-galaxy-counts.csv")
    {
        var t = CsvTable.Parse(text, source);
        t.Require("item", "count");
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++) d[t.Get(r, "item").Trim()] = t.GetInt(r, "count");
        int Need(string k) => d.TryGetValue(k, out var v) ? v : throw new DataLoadException($"{source}: item '{k}' is missing.");
        return new ExpectedGalaxyCounts(Need("systems"), Need("sectors"), Need("lanes"),
            Need("tier1_lanes"), Need("tier2_lanes"), Need("tier3_lanes"));
    }

    public static ExpectedGalaxyCounts Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>Throws with a clear message if the galaxy differs from these counts.</summary>
    public void Check(Galaxy g)
    {
        var diffs = new List<string>();
        void C(string what, int expected, int actual)
        {
            if (expected != actual) diffs.Add($"{what}: expected {expected}, found {actual}");
        }
        C("systems", Systems, g.Systems.Count);
        C("sectors", Sectors, g.Sectors.Count);
        C("lanes", Lanes, g.Lanes.Count);
        C("tier 1 lanes", Tier1Lanes, g.Lanes.Count(l => l.Tier == 1));
        C("tier 2 lanes", Tier2Lanes, g.Lanes.Count(l => l.Tier == 2));
        C("tier 3 lanes", Tier3Lanes, g.Lanes.Count(l => l.Tier == 3));
        if (diffs.Count > 0)
            throw new DataLoadException("Galaxy counts differ from the expected-counts file (edit that file on purpose if the data changed): "
                + string.Join("; ", diffs) + ".");
    }
}

/// <summary>Reads the planets and lanes files into an immutable <see cref="Galaxy"/>. rule: m1-galaxy-data.</summary>
public static class GalaxyLoader
{
    public const string PlanetsFile = "extended-galaxy-v4-planets.csv";
    public const string LanesFile = "extended-galaxy-v4-lanes.csv";
    public const string TiersFile = "lane-tiers.csv";
    public const string CountsFile = "expected-galaxy-counts.csv";

    private static readonly string[] CoreColumns = { "name", "sector", "group", "map_x", "map_y" };

    /// <summary>Loads the real data from a folder (data/design) and checks the expected counts.</summary>
    public static Galaxy LoadFolder(string designDir)
    {
        var g = Load(File.ReadAllText(Path.Combine(designDir, PlanetsFile)),
                     File.ReadAllText(Path.Combine(designDir, LanesFile)),
                     LaneTiers.Load(Path.Combine(designDir, TiersFile)));
        ExpectedGalaxyCounts.Load(Path.Combine(designDir, CountsFile)).Check(g);
        return g;
    }

    public static Galaxy Load(string planetsCsv, string lanesCsv, LaneTiers tiers, GalaxyRules? rules = null)
    {
        rules ??= GalaxyRules.Default;
        var warnings = new List<string>();

        // ---- planets ----
        var pt = CsvTable.Parse(planetsCsv, PlanetsFile);
        pt.Require(CoreColumns);
        var carriedNames = pt.Header.Where(h => !CoreColumns.Contains(h)).OrderBy(h => h, StringComparer.Ordinal).ToArray();

        var raw = new List<(string name, int row)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int r = 0; r < pt.RowCount; r++)
        {
            var name = pt.Get(r, "name").Trim();
            // check 1: names unique and not empty
            if (name.Length == 0) throw new DataLoadException($"{pt.Where(r)}: system name is empty.");
            if (!seen.Add(name)) throw new DataLoadException($"{pt.Where(r)}: duplicate system name '{name}'.");
            raw.Add((name, r));
        }
        raw.Sort((x, y) => string.CompareOrdinal(x.name, y.name));

        var systems = new List<StarSystem>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var (name, r) = raw[i];
            int x = pt.GetInt(r, "map_x"), y = pt.GetInt(r, "map_y");
            // check 5: integers in 0..MapMax
            if (x < 0 || x > rules.MapMax || y < 0 || y > rules.MapMax)
                throw new DataLoadException($"{pt.Where(r)}: system '{name}' position ({x},{y}) is outside 0..{rules.MapMax}.");
            var carried = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in carriedNames) carried[c] = pt.Get(r, c);
            systems.Add(new StarSystem(i, name, pt.Get(r, "sector").Trim(), pt.Get(r, "group").Trim(), x, y, carried));
        }
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in systems) index[s.Name] = s.Index;

        // check 7 (first half): outsiders
        foreach (var s in systems)
        {
            bool shouldBeOutside = rules.Outsiders.Contains(s.Name, StringComparer.Ordinal);
            if (shouldBeOutside != s.IsOutsideSectors)
                throw new DataLoadException(shouldBeOutside
                    ? $"System '{s.Name}' must be outside sectors (label '{Galaxy.OutsideSectorsLabel}') but is in sector '{s.Sector}'."
                    : $"System '{s.Name}' is labelled outside sectors, but only {string.Join(" and ", rules.Outsiders)} may be.");
        }

        // sectors: check 6
        var sectorMembers = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var s in systems)
        {
            if (s.IsOutsideSectors) continue;
            if (s.Sector.Length == 0) throw new DataLoadException($"System '{s.Name}' has no sector.");
            if (!sectorMembers.TryGetValue(s.Sector, out var list)) sectorMembers[s.Sector] = list = new List<int>();
            list.Add(s.Index);
        }
        var sectors = new List<Sector>();
        foreach (var (sname, members) in sectorMembers)
        {
            if (members.Count > rules.MaxSectorSize)
                throw new DataLoadException($"Sector '{sname}' has {members.Count} systems; the limit is {rules.MaxSectorSize}.");
            var group = systems[members[0]].Group;
            foreach (var m in members)
                if (systems[m].Group != group)
                    throw new DataLoadException($"Sector '{sname}' mixes groups: '{systems[members[0]].Name}' is '{group}' but '{systems[m].Name}' is '{systems[m].Group}'.");
            sectors.Add(new Sector(sname, group, members)); // members already in index (= ordinal name) order
        }

        // ---- lanes ----
        var lt = CsvTable.Parse(lanesCsv, LanesFile);
        lt.Require("from", "to", "kind", "routes");
        var lanes = new List<Lane>();
        var pairs = new HashSet<(int, int)>();
        for (int r = 0; r < lt.RowCount; r++)
        {
            string from = lt.Get(r, "from").Trim(), to = lt.Get(r, "to").Trim();
            string kind = lt.Get(r, "kind").Trim(), routes = lt.Get(r, "routes").Trim();
            string label = $"lane {from}-{to}";
            // check 2
            if (!index.TryGetValue(from, out var ia))
                throw new DataLoadException($"{lt.Where(r)}: {label}: unknown system '{from}'.");
            if (!index.TryGetValue(to, out var ib))
                throw new DataLoadException($"{lt.Where(r)}: {label}: unknown system '{to}'.");
            // check 3
            if (ia == ib) throw new DataLoadException($"{lt.Where(r)}: {label}: a lane cannot join a system to itself.");
            var key = ia < ib ? (ia, ib) : (ib, ia);
            if (!pairs.Add(key))
                throw new DataLoadException($"{lt.Where(r)}: {label}: this pair of systems already has a lane (either direction).");
            // check 4
            if (!tiers.IsKnownKind(kind))
                throw new DataLoadException($"{lt.Where(r)}: {label}: unknown kind '{kind}' (known: {string.Join(", ", tiers.KnownKinds)}).");
            int tier = tiers.TierOfKind(kind);
            if ((tier == tiers.Tier1) != (routes.Length > 0))
                throw new DataLoadException($"{lt.Where(r)}: {label}: 'routes' must be filled exactly for tier 1 kinds (kind '{kind}', routes '{routes}').");
            var sa = systems[key.Item1]; var sb = systems[key.Item2];
            long dx = sa.MapX - sb.MapX, dy = sa.MapY - sb.MapY;
            int baseDist = (int)(Isqrt(dx * dx + dy * dy) / Travel.TravelFormula.DistanceDivisor);
            lanes.Add(new Lane(sa.Index, sb.Index, sa.Name, sb.Name, kind, routes, tier, tiers.FactorOfTier(tier), baseDist));
        }
        lanes.Sort((p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B.CompareTo(q.B));

        // neighbours sorted by neighbour ordinal name (= index order)
        var nb = new List<Neighbour>[systems.Count];
        for (int i = 0; i < nb.Length; i++) nb[i] = new List<Neighbour>();
        foreach (var l in lanes) { nb[l.A].Add(new Neighbour(l.B, l)); nb[l.B].Add(new Neighbour(l.A, l)); }
        var neighbours = new Neighbour[systems.Count][];
        for (int i = 0; i < nb.Length; i++)
            neighbours[i] = nb[i].OrderBy(n => n.System).ToArray();

        // check 7 (second half): outsiders have no lanes, others have at least one
        foreach (var s in systems)
        {
            int count = neighbours[s.Index].Length;
            if (s.IsOutsideSectors && count > 0)
                throw new DataLoadException($"System '{s.Name}' is outside sectors and must have no lanes, but has {count}.");
            if (!s.IsOutsideSectors && count == 0)
                throw new DataLoadException($"System '{s.Name}' is inside sector '{s.Sector}' and must have at least one lane.");
        }

        // check 8: one connected component among in-sector systems
        var inSector = systems.Where(s => !s.IsOutsideSectors).Select(s => s.Index).ToList();
        if (inSector.Count > 0)
        {
            var reached = Reach(neighbours, systems.Count, inSector[0], -1);
            var missing = inSector.Where(i => !reached[i]).ToList();
            if (missing.Count > 0)
                throw new DataLoadException($"The lane graph is not connected: {missing.Count} system(s) cannot be reached from '{systems[inSector[0]].Name}', e.g. '{systems[missing[0]].Name}'.");
        }

        // check 10: warnings only
        var chokes = ArticulationPoints(neighbours, systems.Count);
        foreach (var s in systems)
        {
            if (s.Carried.TryGetValue("lane_count", out var lc) && lc.Length > 0
                && int.TryParse(lc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored)
                && stored != neighbours[s.Index].Length)
                warnings.Add($"System '{s.Name}': stored lane_count {stored} differs from recomputed {neighbours[s.Index].Length}.");
            if (s.Carried.TryGetValue("chokepoint", out var ck) && ck.Length > 0
                && int.TryParse(ck, NumberStyles.Integer, CultureInfo.InvariantCulture, out var storedCk)
                && (storedCk != 0) != chokes[s.Index])
                warnings.Add($"System '{s.Name}': stored chokepoint {storedCk} differs from recomputed {(chokes[s.Index] ? 1 : 0)}.");
        }

        return new Galaxy(systems, sectors, lanes, neighbours, warnings);
    }

    /// <summary>Integer square root (floor).</summary>
    public static long Isqrt(long n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        long r = (long)Math.Sqrt(n);
        while (r * r > n) r--;
        while ((r + 1) * (r + 1) <= n) r++;
        return r;
    }

    private static bool[] Reach(Neighbour[][] nb, int n, int start, int removed)
    {
        var seen = new bool[n];
        var stack = new Stack<int>();
        seen[start] = true; stack.Push(start);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            foreach (var e in nb[u])
            {
                if (e.System == removed || seen[e.System]) continue;
                seen[e.System] = true; stack.Push(e.System);
            }
        }
        return seen;
    }

    /// <summary>
    /// Systems whose removal splits the lane graph (among systems that have lanes). Brute force: simple and obviously right.
    /// rule: m1-galaxy-data §What the loader reads (chokepoint is recomputed, never trusted).
    /// </summary>
    private static bool[] ArticulationPoints(Neighbour[][] nb, int n)
    {
        var result = new bool[n];
        var withLanes = Enumerable.Range(0, n).Where(i => nb[i].Length > 0).ToList();
        foreach (var v in withLanes)
        {
            var rest = withLanes.Where(i => i != v).ToList();
            if (rest.Count == 0) continue;
            var reached = Reach(nb, n, rest[0], v);
            result[v] = rest.Any(i => !reached[i]);
        }
        return result;
    }

    /// <summary>Recomputed chokepoints as a sorted list of system indices (m1-galaxy-data test G4).</summary>
    public static IReadOnlyList<int> Chokepoints(Galaxy g)
    {
        var nb = new Neighbour[g.Systems.Count][];
        for (int i = 0; i < nb.Length; i++) nb[i] = g.NeighboursOf(i).ToArray();
        var flags = ArticulationPoints(nb, nb.Length);
        return Enumerable.Range(0, nb.Length).Where(i => flags[i]).ToList();
    }
}
