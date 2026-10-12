using Rebellion.Core.Map;

namespace Rebellion.Core.Travel;

/// <summary>Pure travel arithmetic. rule: m1-travel §Per-hop formula used by the engine, §Rule (Classic), §Rule (New).</summary>
public static class TravelFormula
{
    /// <summary>Distance divisor (GNPRTB 5120). Traced, m1-travel §Numbers.</summary>
    public const int DistanceDivisor = 5;
    /// <summary>Speed for unrated units (GNPRTB 1) and regiments, facilities, characters. Traced.</summary>
    public const int DefaultSpeed = 100;
    /// <summary>Percent divisor of a navigator rating. Traced (same 100 as the speed divisor).</summary>
    public const int PercentDivisor = 100;
    /// <summary>Minimum days for one hop. Traced.</summary>
    public const int MinHopTicks = 1;
    /// <summary>Navigator floor on fleet speed. Proposal (heroes/mission-speed item 7); DEFAULT 25.</summary>
    public const int NavigatorFloor = 25;

    /// <summary>base = isqrt(dx^2 + dy^2) / 5 (integer). rule: m1-travel §Per-hop formula, step 1.</summary>
    public static int BaseDistance(int ax, int ay, int bx, int by)
    {
        long dx = ax - bx, dy = ay - by;
        return (int)(GalaxyLoader.Isqrt(dx * dx + dy * dy) / DistanceDivisor);
    }

    /// <summary>hop_ticks = max(1, base * speed / F), one integer division. rule: m1-travel §Per-hop formula, step 2.</summary>
    public static int HopTicks(int baseDistance, int speed, int factorPercent)
    {
        if (factorPercent <= 0) throw new ArgumentOutOfRangeException(nameof(factorPercent));
        long t = (long)baseDistance * speed / factorPercent;
        return (int)Math.Max(MinHopTicks, t);
    }

    public static int HopTicks(Lane lane, int speed) => HopTicks(lane.BaseDistance, speed, lane.FactorPercent);

    /// <summary>
    /// Fleet speed from speed ratings (bigger is slower; 0 = immobile). Capital ships decide: the largest rating, and any
    /// capital with rating 0 makes the fleet immobile. Squadrons carried aboard are ignored. A fleet with no capital ships
    /// uses the squadron ratings the same way. A fleet with neither uses <see cref="DefaultSpeed"/>.
    /// rule: m1-travel §Rule (Classic), §Numbers (Fleet speed).
    /// </summary>
    public static int FleetSpeed(IEnumerable<int> capitalSpeeds, IEnumerable<int> squadronSpeeds)
    {
        var caps = capitalSpeeds.ToList();
        var pool = caps.Count > 0 ? caps : squadronSpeeds.ToList();
        if (pool.Count == 0) return DefaultSpeed;
        return pool.Contains(0) ? 0 : pool.Max();
    }

    /// <summary>
    /// fleet_speed = slowest_ship_speed * navigator / 100 (integer), then max(.., floor). An immobile fleet stays immobile.
    /// rule: m1-travel §Rule (New). Best single navigator only (Decided); the caller passes that one value.
    /// </summary>
    public static int NavigatorSpeed(int fleetSpeed, int navigatorSpeed, int floor = NavigatorFloor)
    {
        if (fleetSpeed <= 0) return 0;
        return Math.Max(floor, (int)((long)fleetSpeed * navigatorSpeed / PercentDivisor));
    }
}
