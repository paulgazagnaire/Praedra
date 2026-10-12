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
/// Fastest path over the lane graph. rule: m1-travel §Path rule.
/// Lowest total ticks; ties: fewest hops; then ordinal names hop by hop. Cached per (start, end, speed).
/// </summary>
public sealed class Pathfinder
{
    private readonly Galaxy _galaxy;
    private readonly Dictionary<(int, int, int), PathResult> _cache = new();

    public Pathfinder(Galaxy galaxy) { _galaxy = galaxy; }

    public Galaxy Galaxy => _galaxy;
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
        var result = Compute(start, end, speed);
        _cache[key] = result;
        return result;
    }

    private PathResult Compute(int start, int end, int speed)
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
