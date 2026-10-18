using System.Text.Json.Serialization;
using Rebellion.Core.Clock;

namespace Rebellion.Core.Control;

/// <summary>
/// Per-game control state that is saved and hashed: the scorched-earth switch per side (rule: m2-control-and-capture §Scorched earth; value on,
/// both sides, Decided Paul 2026-10-12) and what the derived control rule must remember (B5a, 2026-10-17). The holder itself lives in
/// <see cref="GameState.SystemOwners"/>. The B5a fields are written only when they hold something, so a save without them loads as empty and the
/// state hash of a game that never needed them is unchanged.
/// </summary>
public sealed class ControlState
{
    public bool ScorchedEarthEmpire { get; set; } = true;
    public bool ScorchedEarthAlliance { get; set; } = true;

    /// <summary>
    /// Systems where both sides have regiments standing on the planet, with the military side they had before the second side arrived
    /// (0 = none, 1 = Alliance, 2 = Empire: the 1998 numbering). The 1998 control word keeps the previous military side in that case
    /// (system +0x84 bits 4-5, <c>0x50b610</c>). Everywhere else the military side is derived from the regiments standing there, so only this
    /// case needs a memory. rule: m2-control-and-capture §How control changes (B5a section 1).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, int>? Contested { get; set; }

    /// <summary>
    /// Neutral systems that were held once (NeverBeenControlled, system flag 0x8, cleared). A system with a holder counts as ever held without
    /// being listed. rule: m2-control-and-capture §After the flip (B5a section 6: the sector step skips never-held systems).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedSet<string>? EverHeld { get; set; }

    public bool ScorchedEarth(Side s) => s == Side.Empire ? ScorchedEarthEmpire : ScorchedEarthAlliance;

    public void SetScorchedEarth(Side s, bool on)
    {
        if (s == Side.Empire) ScorchedEarthEmpire = on; else ScorchedEarthAlliance = on;
    }

    internal void CopyFrom(ControlState o)
    {
        ScorchedEarthEmpire = o.ScorchedEarthEmpire;
        ScorchedEarthAlliance = o.ScorchedEarthAlliance;
        Contested = o.Contested == null || o.Contested.Count == 0 ? null : new SortedDictionary<string, int>(o.Contested, StringComparer.Ordinal);
        EverHeld = o.EverHeld == null || o.EverHeld.Count == 0 ? null : new SortedSet<string>(o.EverHeld, StringComparer.Ordinal);
    }
}
