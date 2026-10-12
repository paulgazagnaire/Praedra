using Rebellion.Core.Clock;

namespace Rebellion.Core.Economy;

/// <summary>
/// The 1998 start rolls for a system's slots, kept behind <see cref="SlotSource.Rolled"/>. Draw order (DEFAULT, the original's
/// internal order is not traced): populated roll, energy rolls, raw rolls, then raw is clamped to energy.
/// rule: m2-slots-and-resources §Rule (New) SlotSource; §Numbers (Original start rolls, Traced economy/01).
/// </summary>
public static class SlotRoller
{
    public static SlotState Roll(GameRng rng, bool core, EconomyParams p)
    {
        int energy, raw;
        bool populated = rng.NextInt(0, 99) < (core ? p.CorePopulatedPercent : p.RimPopulatedPercent);
        if (core)
        {
            energy = p.CoreEnergyBase + rng.NextInt(0, p.CoreEnergySpread);
            raw = rng.NextInt(0, p.CoreRawSpread) + p.CoreRawBase;
        }
        else
        {
            energy = p.RimEnergyBase + rng.NextInt(0, p.RimEnergySpreadA) + rng.NextInt(0, p.RimEnergySpreadB);
            raw = rng.NextInt(0, p.RimRawSpread) + p.RimRawBase;
        }
        energy = Math.Min(energy, p.SlotCap);
        raw = Math.Min(Math.Min(raw, p.SlotCap), energy); // raw is clamped to energy (Traced)
        return new SlotState { Energy = energy, Raw = raw, Populated = populated };
    }
}
