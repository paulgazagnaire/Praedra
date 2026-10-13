using Rebellion.Core.Characters;

namespace Rebellion.Core.DeathStar;

/// <summary>
/// The switches and numbers of m5-death-star, each with the sheet's DEFAULT and tag. Settings, not saved. A switch whose "on" has no rule is
/// refused at attach with <see cref="RuleMissingException"/> (CLAUDE.md hard rule 7). The 50 percent minor-character kill chance
/// (<c>DeathStarMinorKillPct</c>) is <see cref="CharacterParams.DeathStarHitPercent"/>, where the characters sheet keeps it.
/// rule: m5-death-star §1, §2 legality and effect 7, §4 (TrenchRun).
/// </summary>
public sealed record DeathStarParams
{
    /// <summary>Death Stars a side may hold at one system (in a fleet there, or queued there) before a yard refuses another: DeathStarCapPerSystem. DEFAULT (from the Traced capacity method, medium confidence on scope). rule: m5-death-star §1.</summary>
    public int CapPerSystem { get; init; } = 1;

    /// <summary>When true an enemy Death Star Shield facility at the target rejects Destroy System (ShieldProtected). DEFAULT false (Traced: nothing in the destroy path reads it; Missing: a play test). rule: m5-death-star §2 legality 6.</summary>
    public bool ShieldBlocksDestroy { get; init; }

    /// <summary>Trench run in auto-resolved battles. DEFAULT off; on has no rule (Missing: ships/12 asks for a design), so it is refused. rule: m5-death-star §4.</summary>
    public bool TrenchRun { get; init; }

    /// <summary>Support change for every other system of a sector at its first strike, in Alliance points: DeathStarSectorPenalty = 10 (the original's already halved value; Paul replaced the galaxy-wide drop and the -20 on the target). Traced; Paul. rule: m5-death-star §2 effect 7.</summary>
    public int SectorPenalty { get; init; } = 10;

    /// <summary>The strike in a sector (counted for the whole game) from which every remaining system turns fully Alliance: the second. Paul. rule: m5-death-star §2 effect 7.</summary>
    public int RevoltFromStrike { get; init; } = 2;

    /// <summary>Alliance support the revolt sets (the maximum of the support scale). Paul ("full Alliance support"). rule: m5-death-star §2 effect 7.</summary>
    public int RevoltAllianceSupport { get; init; } = 100;

    /// <summary>Throw <see cref="RuleMissingException"/> for every switch value the sheet refuses. rule: m5-death-star §4 (TrenchRun: no code until a rule exists).</summary>
    public void Validate()
    {
        if (TrenchRun) throw new RuleMissingException("TrenchRun", "m5-death-star");
        if (CapPerSystem < 1) throw new ArgumentOutOfRangeException(nameof(CapPerSystem), "At least one Death Star per system must be allowed.");
    }
}
