using Rebellion.Core.Ships;

namespace Rebellion.Core.Fleets;

/// <summary>
/// The one place that measures combat strength and decides "armed".
/// rule: m1-forced-stop-and-blockade §Update 2026-10-12 (combat strength behind one function), §What "armed" means.
/// </summary>
public static class CombatStrength
{
    /// <summary>
    /// Combat strength of a force: PLACEHOLDER until M4 provides the real measure (space-combat/11 section 10).
    /// DEFAULT (sheet): the sum of overall_attack_strength of the armed ships, capital ships and squadrons alike.
    /// Ship count is never used. <paramref name="versus"/> is unused now and exists so M4 can make the measure relative.
    /// Every test that depends on the strength level is marked "M4 re-check".
    /// </summary>
    public static long Of(IEnumerable<ShipClass> forces, IEnumerable<ShipClass>? versus = null)
    {
        long sum = 0;
        foreach (var s in forces)
            if (IsArmed(s)) sum += s.OverallAttackStrength;
        return sum;
    }

    /// <summary>A ship or squadron is armed when its overall_attack_strength is above 0. rule: §What "armed" means.</summary>
    public static bool IsArmed(ShipClass s) => s.OverallAttackStrength > 0;

    /// <summary>A force is armed if at least one ship or squadron in it is. rule: §What "armed" means.</summary>
    public static bool IsArmed(IEnumerable<ShipClass> forces) => forces.Any(IsArmed);
}
