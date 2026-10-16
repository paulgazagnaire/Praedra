using Rebellion.Core.Map;

namespace Rebellion.Core.Travel;

public enum PathStatus { Ok, Unreachable, AlreadyThere, Immobile, UnknownSystem }

/// <summary>Result of a path request: either a path or a rejection reason. rule: m1-travel §Path rule (6), §Defaults.</summary>
public sealed class PathResult
{
    public PathStatus Status { get; }
    /// <summary>System indices from start to end, inclusive. Empty unless Ok.</summary>
    public IReadOnlyList<int> Systems { get; }
    /// <summary>Ticks of each hop. Empty unless Ok.</summary>
    public IReadOnlyList<int> HopTicks { get; }
    public int TotalTicks { get; }
    public bool Ok => Status == PathStatus.Ok;
    public int Hops => HopTicks.Count;

    private PathResult(PathStatus status, IReadOnlyList<int> systems, IReadOnlyList<int> hopTicks, int total)
    {
        Status = status; Systems = systems; HopTicks = hopTicks; TotalTicks = total;
    }

    internal static PathResult Rejected(PathStatus s) => new(s, Array.Empty<int>(), Array.Empty<int>(), 0);
    internal static PathResult Found(int[] systems, int[] hopTicks) => new(PathStatus.Ok, systems, hopTicks, hopTicks.Sum());

    public IReadOnlyList<string> Names(Galaxy g) => Systems.Select(i => g.Systems[i].Name).ToList();
}

/// <summary>
/// How ships travel. rule: m1-travel §Rule (Decided, Paul 2026-10-16).
/// Routes (New): free straight-line travel anywhere, plus the hyperspace route links, which are faster on the stretch between their two ends.
/// Straight (Classic): the 1998 straight line, start to destination in one leg (Traced: the original has no lanes, planets/11).
/// </summary>
public enum TravelMode
{
    Routes,
    Straight,
    /// <summary>The 2026-10-12 rule: ships fly only along lanes. Retired by Paul 2026-10-16; kept as a switch for comparison and for the
    /// small hand-made test maps whose stop and contact checks need fleets to pass the middle system.</summary>
    Lanes,
}

/// <summary>
/// Fastest path. rule: m1-travel §Path rule.
/// Routes: legs are either off-route (any two systems, factor 100) or along a route link (its factor). An off-route leg ends only at a
/// route system, the destination or a waypoint, so a path never stops at an off-route system on the way. Lowest total ticks; ties:
/// fewest legs; then the smaller predecessor (ordinal name). Straight: one leg. Each leg is max(1, base * speed / factor) ticks.
/// Cached per (start, speed) for the search and per (start, end, speed) for results.
/// </summary>
public sealed class Pathfinder
{
    private readonly Galaxy _galaxy;
    private readonly Dictionary<(int, int, int), PathResult> _cache = new();
    private readonly Dictionary<(int, int), Tree> _trees = new();
    private readonly int[] _routeSystems;
    private readonly int[,] _dist;

    public Pathfinder(Galaxy galaxy, TravelMode mode = TravelMode.Routes)
    {
        _galaxy = galaxy; Mode = mode;
        int n = galaxy.Systems.Count;
        _routeSystems = Enumerable.Range(0, n).Where(galaxy.OnRoute).ToArray(); // index order = ordinal name order
        _dist = new int[n, n];
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                var sa = galaxy.Systems[a]; var sb = galaxy.Systems[b];
                _dist[a, b] = _dist[b, a] = TravelFormula.MapDistance(sa.MapX, sa.MapY, sb.MapX, sb.MapY);
            }
    }

    public Galaxy Galaxy => _galaxy;
    public TravelMode Mode { get; }
    public int CachedCount => _cache.Count;

    public PathResult Find(string start, string end, int speed)
    {
        if (!_galaxy.TryFindSystem(start, out var s) || !_galaxy.TryFindSystem(end, out var e))
            return PathResult.Rejected(PathStatus.UnknownSystem);
        return Find(s, e, speed);
    }

    public PathResult Find(int start, int end, int speed)
    {
        int n = _galaxy.Systems.Count;
        if (start < 0 || start >= n || end < 0 || end >= n) return PathResult.Rejected(PathStatus.UnknownSystem);
        if (speed <= 0) return PathResult.Rejected(PathStatus.Immobile); // DEFAULT: speed 0 rejects the order
        if (start == end) return PathResult.Rejected(PathStatus.AlreadyThere); // DEFAULT
        var key = (start, end, speed);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var result = Mode switch
        {
            TravelMode.Straight => PathResult.Found(new[] { start, end }, new[] { OffRoute(start, end, speed) }),
            TravelMode.Lanes => LaneCompute(start, end, speed),
            _ => Walk(TreeFor(start, speed), start, end, speed),
        };
        _cache[key] = result;
        return result;
    }

    /// <summary>Ticks of an off-route leg: the original formula, max(1, (isqrt / 5) * speed / 100). rule: m1-travel §Per-leg formula (Traced).</summary>
    private int OffRoute(int a, int b, int speed) =>
        TravelFormula.HopTicks(_dist[a, b] / TravelFormula.DistanceDivisor, speed, TravelFormula.PercentDivisor);

    /// <summary>Ticks of the leg a-b: the faster of off-route and its route link, if any. rule: m1-travel §Per-leg formula (route legs).</summary>
    private int Leg(int a, int b, int speed)
    {
        int t = OffRoute(a, b, speed);
        var lane = _galaxy.FindLane(a, b);
        return lane == null ? t : Math.Min(t, TravelFormula.RouteLegTicks(_dist[a, b], speed, lane.FactorPercent));
    }

    private sealed class Tree
    {
        public required long[] Ticks; public required int[] Legs; public required int[] Prev;
    }

    // Dense Dijkstra over the start and the route systems, then one off-route leg to every other system.
    private Tree TreeFor(int start, int speed)
    {
        if (_trees.TryGetValue((start, speed), out var hit)) return hit;
        int n = _galaxy.Systems.Count;
        var ticks = new long[n]; var legs = new int[n]; var prev = new int[n];
        Array.Fill(ticks, long.MaxValue); Array.Fill(prev, -1);
        var nodes = _routeSystems.Contains(start) ? _routeSystems : _routeSystems.Append(start).OrderBy(i => i).ToArray();
        var done = new bool[n];
        ticks[start] = 0;
        bool Better(long t, int l, int u, int v) =>
            t < ticks[v] || (t == ticks[v] && (l < legs[v] || (l == legs[v] && u < prev[v])));
        for (int round = 0; round < nodes.Length; round++)
        {
            int u = -1;
            foreach (int c in nodes) // smallest (ticks, legs, index)
                if (!done[c] && ticks[c] != long.MaxValue && (u < 0 || ticks[c] < ticks[u] || (ticks[c] == ticks[u] && legs[c] < legs[u])))
                    u = c;
            if (u < 0) break;
            done[u] = true;
            foreach (int v in nodes)
            {
                if (done[v]) continue;
                long t = ticks[u] + Leg(u, v, speed);
                if (Better(t, legs[u] + 1, u, v)) { ticks[v] = t; legs[v] = legs[u] + 1; prev[v] = u; }
            }
        }
        for (int e = 0; e < n; e++)
        {
            if (done[e]) continue;
            foreach (int u in nodes)
            {
                if (!done[u]) continue;
                long t = ticks[u] + OffRoute(u, e, speed);
                if (Better(t, legs[u] + 1, u, e)) { ticks[e] = t; legs[e] = legs[u] + 1; prev[e] = u; }
            }
        }
        var tree = new Tree { Ticks = ticks, Legs = legs, Prev = prev };
        _trees[(start, speed)] = tree;
        return tree;
    }

    private PathResult Walk(Tree tree, int start, int end, int speed)
    {
        if (tree.Ticks[end] == long.MaxValue) return PathResult.Rejected(PathStatus.Unreachable);
        var rev = new List<int> { end };
        for (int cur = end; cur != start; ) { cur = tree.Prev[cur]; rev.Add(cur); }
        rev.Reverse();
        var hops = new int[rev.Count - 1];
        bool routeEnd(int i) => _galaxy.OnRoute(i);
        for (int k = 0; k + 1 < rev.Count; k++)
        {
            int a = rev[k], b = rev[k + 1];
            // a leg into an off-route destination is always off-route; between route systems it may use the link
            hops[k] = routeEnd(a) && routeEnd(b) ? Leg(a, b, speed) : OffRoute(a, b, speed);
        }
        return PathResult.Found(rev.ToArray(), hops);
    }

    // TravelMode.Lanes: the 2026-10-12 lane graph search, unchanged.
    private PathResult LaneCompute(int start, int end, int speed)
    {
        int n = _galaxy.Systems.Count;
        var ticks = new long[n];
        var hops = new int[n];
        Array.Fill(ticks, long.MaxValue);
        // Dijkstra from the destination, cost = (ticks, hops). rule: m1-travel §Path rule step 3.
        ticks[end] = 0; hops[end] = 0;
        var queue = new PriorityQueue<int, (long, int, int)>();
        queue.Enqueue(end, (0, 0, end));
        while (queue.TryDequeue(out var u, out var pri))
        {
            if (pri.Item1 != ticks[u] || pri.Item2 != hops[u]) continue; // stale entry
            foreach (var nb in _galaxy.NeighboursOf(u))
            {
                long t = ticks[u] + TravelFormula.HopTicks(nb.Lane, speed);
                int h = hops[u] + 1;
                int v = nb.System;
                if (t < ticks[v] || (t == ticks[v] && h < hops[v]))
                {
                    ticks[v] = t; hops[v] = h;
                    queue.Enqueue(v, (t, h, v));
                }
            }
        }
        if (ticks[start] == long.MaxValue) return PathResult.Rejected(PathStatus.Unreachable);

        // Walk from the start; neighbours are sorted by ordinal name, so the first match is the smallest name.
        var path = new List<int> { start };
        var hopTicks = new List<int>();
        int cur = start;
        while (cur != end)
        {
            int next = -1, ht = 0;
            foreach (var nb in _galaxy.NeighboursOf(cur))
            {
                int v = nb.System;
                if (ticks[v] == long.MaxValue) continue;
                int t = TravelFormula.HopTicks(nb.Lane, speed);
                if (ticks[v] + t == ticks[cur] && hops[v] + 1 == hops[cur]) { next = v; ht = t; break; }
            }
            if (next < 0) throw new InvalidOperationException("Path walk lost its way; the cost table is inconsistent.");
            path.Add(next); hopTicks.Add(ht); cur = next;
        }
        return PathResult.Found(path.ToArray(), hopTicks.ToArray());
    }
}
