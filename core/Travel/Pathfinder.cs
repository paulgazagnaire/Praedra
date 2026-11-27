using Praedra.Core.Map;

namespace Praedra.Core.Travel;

/// <summary>InTransit: only from a fleet's move plan (the fleet already flies an order). rule: m1-travel §Orders in transit.</summary>
public enum PathStatus { Ok, Unreachable, AlreadyThere, Immobile, UnknownSystem, InTransit }

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
/// Routes (New): free straight-line travel anywhere, plus the lanes, which are faster on the stretch between their two ends.
/// Straight (Classic): the Classic straight line, start to destination in one leg (Traced: the original has no lanes, planets/11).
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
/// Cached per (start, speed) for the search and per (start, end, speed) for results. The caches, the leg tables and the cache of
/// searches that avoid systems are derived from the galaxy alone (never from game state), so they only save time: same answers.
/// </summary>
public sealed class Pathfinder
{
    private readonly Galaxy _galaxy;
    private readonly Dictionary<(int, int, int), PathResult> _cache = new();
    private readonly Dictionary<(int, int), Tree> _trees = new();
    private readonly int[] _routeSystems;
    private readonly int[,] _dist;
    // Speed lookups (derived from the galaxy, never saved; QA senior QS-1): off-route base (map distance / 5) by a * n + b; position of a
    // system among the route systems (-1 = off route); per speed, the ticks of the leg between two route systems by position (Leg).
    private readonly int[] _base;
    private readonly int[] _routePos;
    private readonly Dictionary<int, int[]> _routeLegs = new();
    // Searches that avoid systems, by (start, speed, avoided set): the same fleet re-plans the same way round day after day. Bounded: cleared when full.
    private readonly Dictionary<string, Tree> _avoidTrees = new(StringComparer.Ordinal);
    private const int AvoidTreesMax = 1024;

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
        _base = new int[n * n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++) _base[a * n + b] = _dist[a, b] / TravelFormula.DistanceDivisor;
        _routePos = new int[n];
        Array.Fill(_routePos, -1);
        for (int i = 0; i < _routeSystems.Length; i++) _routePos[_routeSystems[i]] = i;
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

    /// <summary>
    /// The fastest path that makes no stop at the <paramref name="avoid"/> systems on the way (the start and the destination are never
    /// avoided). Not cached. Routes mode only; the other modes ignore <paramref name="avoid"/>. Used by the AI to go around known traps
    /// (movement-redesign-draft §D route planning: the AI uses the same router). Proposal (lead 2026-10-16).
    /// </summary>
    public PathResult FindAvoiding(int start, int end, int speed, IReadOnlyCollection<int> avoid)
    {
        if (avoid.Count == 0 || Mode != TravelMode.Routes) return Find(start, end, speed);
        int n = _galaxy.Systems.Count;
        if (start < 0 || start >= n || end < 0 || end >= n) return PathResult.Rejected(PathStatus.UnknownSystem);
        if (speed <= 0) return PathResult.Rejected(PathStatus.Immobile);
        if (start == end) return PathResult.Rejected(PathStatus.AlreadyThere);
        var skip = new HashSet<int>(avoid); skip.Remove(start); skip.Remove(end); // lookup only
        // the tree depends on (start, speed, the avoided set) only; the end is avoided by no one, as the start (removed above)
        string key = start + "/" + speed + "/" + string.Join(",", skip.OrderBy(i => i));
        if (!_avoidTrees.TryGetValue(key, out var tree))
        {
            if (_avoidTrees.Count >= AvoidTreesMax) _avoidTrees.Clear();
            _avoidTrees[key] = tree = BuildTree(start, speed, skip);
        }
        return Walk(tree, start, end, speed);
    }

    /// <summary>Ticks of an off-route leg: the original formula, max(1, (isqrt / 5) * speed / 100). rule: m1-travel §Per-leg formula (Traced).</summary>
    private int OffRoute(int a, int b, int speed) =>
        TravelFormula.HopTicks(_base[a * _routePos.Length + b], speed, TravelFormula.PercentDivisor);

    /// <summary>
    /// <see cref="Leg"/> between every two route systems for one speed, by route position (row * route count + column). Built once per speed;
    /// the same values as <see cref="Leg"/> (off-route, or the first route link of the pair when faster).
    /// </summary>
    private int[] RouteLegs(int speed)
    {
        if (_routeLegs.TryGetValue(speed, out var hit)) return hit;
        int r = _routeSystems.Length;
        var legs = new int[r * r];
        for (int i = 0; i < r; i++)
            for (int j = 0; j < r; j++) legs[i * r + j] = OffRoute(_routeSystems[i], _routeSystems[j], speed);
        for (int i = 0; i < r; i++)
        {
            int a = _routeSystems[i];
            var seen = new HashSet<int>(); // lookup only: FindLane takes the first link to a neighbour
            foreach (var nb in _galaxy.NeighboursOf(a))
            {
                int j = _routePos[nb.System];
                if (!seen.Add(nb.System) || j < 0) continue;
                legs[i * r + j] = Math.Min(legs[i * r + j], TravelFormula.RouteLegTicks(_dist[a, nb.System], speed, nb.Lane.FactorPercent));
            }
        }
        _routeLegs[speed] = legs;
        return legs;
    }

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
        var tree = BuildTree(start, speed, null);
        _trees[(start, speed)] = tree;
        return tree;
    }

    private Tree BuildTree(int start, int speed, HashSet<int>? skip)
    {
        int n = _galaxy.Systems.Count;
        var ticks = new long[n]; var legs = new int[n]; var prev = new int[n];
        Array.Fill(ticks, long.MaxValue); Array.Fill(prev, -1);
        var nodes = _routeSystems.Contains(start) ? _routeSystems : _routeSystems.Append(start).OrderBy(i => i).ToArray();
        if (skip != null) nodes = nodes.Where(i => !skip.Contains(i)).ToArray(); // an avoided system is reached only as the destination (below)
        var done = new bool[n];
        ticks[start] = 0;
        // Leg(u, v) from the per-speed table between route systems; a pair with an off-route start has no link, so it is off-route.
        // Each round settles the smallest (ticks, legs, nodes order) of the nodes not yet settled, then relaxes them in nodes order; the next
        // round's pick is found during that same pass (a node's value is final for the round once relaxed), so one pass per round.
        var table = RouteLegs(speed); int r = _routeSystems.Length;
        var open = (int[])nodes.Clone(); int count = open.Length; // the nodes not settled, nodes order
        int pick = -1; // position in open of the next node to settle
        for (int k = 0; k < count; k++)
        {
            int c = open[k];
            if (ticks[c] != long.MaxValue && (pick < 0 || ticks[c] < ticks[open[pick]] || (ticks[c] == ticks[open[pick]] && legs[c] < legs[open[pick]]))) pick = k;
        }
        while (pick >= 0)
        {
            int u = open[pick];
            Array.Copy(open, pick + 1, open, pick, count - pick - 1); count--;
            done[u] = true;
            int pu = _routePos[u], lu = legs[u] + 1; long tu = ticks[u];
            pick = -1;
            for (int k = 0; k < count; k++)
            {
                int v = open[k], pv = _routePos[v];
                long t = tu + (pu >= 0 && pv >= 0 ? table[pu * r + pv] : OffRoute(u, v, speed));
                long tv = ticks[v];
                if (t < tv || (t == tv && (lu < legs[v] || (lu == legs[v] && u < prev[v])))) { ticks[v] = tv = t; legs[v] = lu; prev[v] = u; }
                if (tv != long.MaxValue && (pick < 0 || tv < ticks[open[pick]] || (tv == ticks[open[pick]] && legs[v] < legs[open[pick]]))) pick = k;
            }
        }
        var settled = new List<int>(nodes.Length); // nodes order
        foreach (int u in nodes) if (done[u]) settled.Add(u);
        for (int e = 0; e < n; e++)
        {
            if (done[e]) continue;
            // off-route legs u -> e; the map distance is symmetric, so e's row of the base table is read (one row, in memory order)
            long te = ticks[e]; int le = legs[e], pe = prev[e], row = e * n;
            foreach (int u in settled)
            {
                long t = ticks[u] + TravelFormula.HopTicks(_base[row + u], speed, TravelFormula.PercentDivisor);
                int l = legs[u] + 1;
                if (t < te || (t == te && (l < le || (l == le && u < pe)))) { te = t; le = l; pe = u; }
            }
            ticks[e] = te; legs[e] = le; prev[e] = pe;
        }
        return new Tree { Ticks = ticks, Legs = legs, Prev = prev };
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
