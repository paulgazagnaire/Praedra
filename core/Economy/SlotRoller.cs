using Praedra.Core.Clock;

namespace Praedra.Core.Economy;

/// <summary>
/// The Classic start rolls for a system's slots, kept behind <see cref="SlotSource.Rolled"/>. Draw order (DEFAULT, Classic's
/// internal order is not traced): populated roll, energy rolls, raw rolls, then raw is clamped to energy.
/// rule: m2-slots-and-resources §Rule (New) SlotSource; §Numbers (Classic start rolls, Traced economy/01).
/// </summary>
public static class SlotRoller
{
    public static SlotState Roll(GameRng rng, bool core, EconomyParams p)
    {
        int energy, raw;
        bool populated = rng.NextInt(0, 99) < (core ? p.HeartlandPopulatedPercent : p.ReachPopulatedPercent);
        if (core)
        {
            energy = p.HeartlandEnergyBase + rng.NextInt(0, p.HeartlandEnergySpread);
            raw = rng.NextInt(0, p.HeartlandRawSpread) + p.HeartlandRawBase;
        }
        else
        {
            energy = p.ReachEnergyBase + rng.NextInt(0, p.ReachEnergySpreadA) + rng.NextInt(0, p.ReachEnergySpreadB);
            raw = rng.NextInt(0, p.ReachRawSpread) + p.ReachRawBase;
        }
        energy = Math.Min(energy, p.SlotCap);
        raw = Math.Min(Math.Min(raw, p.SlotCap), energy); // raw is clamped to energy (Traced)
        return new SlotState { Energy = energy, Raw = raw, Populated = populated };
    }
}
