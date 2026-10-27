using Praedra.Core.Characters;

namespace Praedra.Core.Harvester;

/// <summary>
/// What a destroyed planet does to support elsewhere. Classic: galaxy-wide, the destroyer loses 10 on every Heartland world (GNPRTB 5123/5124,
/// threshold 101) and 2 on outer worlds where its support is below 90 (5121/5122), skipping unpopulated and never-held worlds, through the normal
/// damping (B5c section 3 and decision 1). New = Paul's rule (m5-harvester §2 effect 7: 10 in the sector at the first strike, the second strike
/// in a sector turns the rest of it). DEFAULT New (Paul's decision stands as the game's rule; Dev lead 2026-10-17).
/// </summary>
public enum HarvesterFallout { Classic, New }

/// <summary>
/// The switches and numbers of m5-harvester, each with the sheet's DEFAULT and tag. Settings, not saved. A switch whose "on" has no rule is
/// refused at attach with <see cref="RuleMissingException"/> (CLAUDE.md hard rule 7). The 50 percent minor-character kill chance
/// (<c>HarvesterMinorKillPct</c>) is <see cref="CharacterParams.HarvesterHitPercent"/>, where the characters sheet keeps it.
/// rule: m5-harvester §1, §2 legality and effect 7, §4 (TrenchRun).
/// </summary>
public sealed record HarvesterParams
{
    /// <summary>harvesters a side may hold at one system (in a fleet there, or queued there) before a yard refuses another: HarvesterCapPerSystem. DEFAULT (from the Traced capacity method, medium confidence on scope). rule: m5-harvester §1.</summary>
    public int CapPerSystem { get; init; } = 1;

    /// <summary>Trench run in auto-resolved battles. DEFAULT off; on has no rule (Missing: ships/12 asks for a design), so it is refused. rule: m5-harvester §4.</summary>
    public bool TrenchRun { get; init; }

    /// <summary>Support change for every other system of a sector at its first strike, in Collective points: HarvesterSectorPenalty = 10 (Classic's already halved value; Paul replaced the galaxy-wide drop and the -20 on the target). Traced; Paul. rule: m5-harvester §2 effect 7.</summary>
    public int SectorPenalty { get; init; } = 10;

    /// <summary>The strike in a sector (counted for the whole game) from which every remaining system turns fully Collective: the second. Paul. rule: m5-harvester §2 effect 7.</summary>
    public int RevoltFromStrike { get; init; } = 2;

    /// <summary>Collective support the revolt sets (the maximum of the support scale). Paul ("full Collective support"). rule: m5-harvester §2 effect 7.</summary>
    public int RevoltCollectiveSupport { get; init; } = 100;

    /// <summary>The fallout of a destroyed planet: Classic (, galaxy-wide) or New (Paul's sector rule). DEFAULT New. rule: m5-harvester §2 effect 7 (B5a, 2026-10-17).</summary>
    public HarvesterFallout Fallout { get; init; } = HarvesterFallout.New;

    /// <summary>Classic fallout on a Heartland world: the destroyer loses this much (GNPRTB 5123 = 10), threshold 5124 = 101 (always). [DAT-own] via B5c section 3.</summary>
    public int ClassicHeartlandLoss { get; init; } = 10;
    public int ClassicHeartlandThreshold { get; init; } = 101;
    /// <summary>Classic fallout on a outer world: the destroyer loses this much (GNPRTB 5121 = 2) only where its support is below 5122 = 90. [DAT-own] via B5c section 3.</summary>
    public int ClassicReachLoss { get; init; } = 2;
    public int ClassicReachThreshold { get; init; } = 90;

    /// <summary>
    /// The harvester's upkeep (maintenance load), replacing the class's Classic value (600, unit-and-facility-costs.csv). New, Decided (Paul
    /// 2026-10-27: "harvester upkeep cost to 450, let's try with this value", after QA-55: no Hand fits 600 under its upkeep capacity). Null =
    /// the Classic value. DEFAULT 450. rule: m5-harvester §1 Upkeep.
    /// </summary>
    public int? Upkeep { get; init; } = 450;

    /// <summary>
    /// Spike the Harvester also targets a harvester still being built: legal when the other side has one unfinished in a yard project at the system,
    /// and a success with no finished harvester there destroys it (no refund, progress lost). New, Decided (Paul 2026-10-27, "Spike it in the yard");
    /// DEFAULT on, off = Classic (only a finished harvester). rule: m5-harvester §5 (Spike in the yard).
    /// </summary>
    public bool SpikeInYard { get; init; } = true;

    /// <summary>Throw <see cref="RuleMissingException"/> for every switch value the sheet refuses. rule: m5-harvester §4 (TrenchRun: no code until a rule exists).</summary>
    public void Validate()
    {
        if (TrenchRun) throw new RuleMissingException("TrenchRun", "m5-harvester");
        if (Upkeep < 0) throw new ArgumentOutOfRangeException(nameof(Upkeep), "Upkeep cannot be negative.");
        if (CapPerSystem < 1) throw new ArgumentOutOfRangeException(nameof(CapPerSystem), "At least one harvester per system must be allowed.");
    }
}
