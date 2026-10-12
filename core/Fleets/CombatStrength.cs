using Rebellion.Core.Ships;

namespace Rebellion.Core.Fleets;

/// <summary>
/// The one place that measures combat strength and decides "armed".
/// rule: m1-forced-stop-and-blockade §Update 2026-10-12 (combat strength behind one function), §What "armed" means.
/// </summary>
public static class CombatStrength
{
    /// <summary>
    /// Combat strength of a force: PLACEHOLDER (lead decision 2026-10-12): build cost as a strength proxy until M4
    /// (space-combat/11 section 10); the printed attack strength is decorative in the original. The sum of
    /// refined_material_cost of the armed ships and squadrons that carry at least one turbolaser or ion cannon (any arc); laser-only boats (Corellian Corvette, fighters) count 0. Ship count is never used. rule: space-combat/07 last section (stand-in until m4-battle-new §12 CombatStrength). <paramref name="versus"/> is unused now and exists so M4 can make the measure relative.
    /// Every test that depends on the strength level is marked "M4 re-check".
    /// </summary>
    public static long Of(IEnumerable<ShipClass> forces, IEnumerable<ShipClass>? versus = null)
    {
        long sum = 0;
        foreach (var s in forces)
            if (IsArmed(s) && s.HasHeavyWeapon) sum += s.RefinedMaterialCost; // rule: space-combat/07 last section (stand-in until m4-battle-new §12 CombatStrength)
        return sum;
    }

    /// <summary>A ship or squadron is armed when its overall_attack_strength is above 0. rule: §What "armed" means.</summary>
    public static bool IsArmed(ShipClass s) => s.OverallAttackStrength > 0;

    /// <summary>A force is armed if at least one ship or squadron in it is. rule: §What "armed" means.</summary>
    public static bool IsArmed(IEnumerable<ShipClass> forces) => forces.Any(IsArmed);
}
