namespace Praedra.Core.Characters;

/// <summary>
/// One system's running loss tallies, per side number (index 0 Collective, 1 Hand, 2 neutral): battle (system data +0x64), assault (+0x44) and
/// bombardment (+0x50), and the blockader recorded when a blockade starts (+0x60; 3 = none). Saved and hashed. rule: m5-characters-and-force
/// §Loyalty and betrayal (Traced, B2c §3, §5).
/// </summary>
public sealed class SystemLossTally
{
    public int[] Battle { get; set; } = new int[3];
    public int[] Assault { get; set; } = new int[3];
    public int[] Bombardment { get; set; } = new int[3];
    public int Blockader { get; set; } = LoyaltyModule.SideNumberNeutral;

    internal SystemLossTally Clone() => new()
    {
        Battle = (int[])Battle.Clone(), Assault = (int[])Assault.Clone(), Bombardment = (int[])Bombardment.Clone(), Blockader = Blockader,
    };
}

/// <summary>The loss tallies of every system that has had one (made on first use). Saved and hashed. rule: B2c §3.</summary>
public sealed class LoyaltyState
{
    public SortedDictionary<string, SystemLossTally> Systems { get; init; } = new(StringComparer.Ordinal);

    internal void CopyFrom(LoyaltyState o)
    {
        foreach (var kv in o.Systems) Systems[kv.Key] = kv.Value.Clone();
    }
}
