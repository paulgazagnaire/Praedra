using Rebellion.Core.Characters;

namespace Rebellion.Core.Missions;

/// <summary>How a *MSTB (and the decoy) table is read between rows. rule: m5-missions §Mission tables (MstbLookup).</summary>
public enum MstbLookupMode
{
    /// <summary>DEFAULT: the last row whose threshold is at or below x (how FOILTB was traced; MSTB not checked, Lead).</summary>
    Step,
    /// <summary>Piecewise linear between rows, truncating toward zero (Open Rebellion's wording, unverified vs original).</summary>
    Linear,
}

/// <summary>Below the first row of a *MSTB table. rule: m5-missions §Mission tables (MstbBelowFirstRow).</summary>
public enum MstbBelowFirstRow { FirstRowValue, Zero, Always }

/// <summary>How the members' rolls combine. rule: m5-missions §2 step 3 (SuccessRule).</summary>
public enum SuccessRule
{
    /// <summary>DEFAULT: success if any member succeeded; the effect is applied once.</summary>
    AnyMember,
    /// <summary>The effect is applied once per successful member.</summary>
    EachMember,
}

/// <summary>The Incite input. rule: m5-missions §Mission records (Incite), Contradictions 1.</summary>
public enum InciteInputMode
{
    /// <summary>DEFAULT: leadership - controller support.</summary>
    LeadershipMinusControllerSupport,
    /// <summary>missions/01 as written: (leadership - support) - rival support = leadership - 100.</summary>
    Documented,
}

/// <summary>When the Incite support effect lands. rule: m5-missions §6 (Incite Uprising).</summary>
public enum InciteSupportTiming
{
    /// <summary>DEFAULT: at the end of each successful iteration.</summary>
    PerIteration,
    /// <summary>In slot 6.3, once for every incident of a system with a working Incite mission, instead of at iteration end.</summary>
    PerIncident,
}

/// <summary>The skill fed to SBTGMSTB. rule: m5-missions §Mission records (Sabotage), Missing 11.</summary>
public enum SabotageSkill { Average, Combat }

/// <summary>Which facility a Sabotage destroys. rule: m5-missions §6 (Sabotage).</summary>
public enum SabotageTargetMode { RandomFacility, Chosen }

/// <summary>
/// Every switch and number of m5-missions §Defaults for undecided items, with the sheet's DEFAULT. Settings, not saved (pass them again on load
/// through <see cref="Setup.GameSettings.Missions"/>). A switch whose other value has no rule in the sheet ("no rule exists") is refused at attach
/// with <see cref="RuleMissingException"/>. rule: m5-missions §Defaults for undecided items; CLAUDE.md hard rule 7.
/// </summary>
public sealed record MissionParams
{
    public MstbLookupMode MstbLookup { get; init; } = MstbLookupMode.Step;
    public MstbBelowFirstRow MstbBelowFirstRow { get; init; } = MstbBelowFirstRow.FirstRowValue;
    public SuccessRule SuccessRule { get; init; } = SuccessRule.AnyMember;
    /// <summary>Run the detection walk once at landing too. DEFAULT false (Missing: the original phases are an Open Rebellion lead).</summary>
    public bool DetectionAtLanding { get; init; }
    /// <summary>Only special forces may be decoys. DEFAULT true (Traced for the AI path only).</summary>
    public bool DecoysFromSpecialForcesOnly { get; init; } = true;
    /// <summary>Team size cap, 0 = none. DEFAULT 0 (Missing).</summary>
    public int MaxTeamMembers { get; init; }
    /// <summary>Decoy cap, 0 = none. DEFAULT 0 (Missing; the original probably caps at (field + 2) / 2, field not identified).</summary>
    public int MaxDecoys { get; init; }
    /// <summary>Regiments aboard ships at the target are defenders. DEFAULT true; false = landed regiments only.</summary>
    public bool CountAboardUnits { get; init; } = true;
    /// <summary>Diplomacy needs the uprising flag off. DEFAULT true ("calm" is Missing).</summary>
    public bool DiplomacyNeedsCalm { get; init; } = true;
    /// <summary>Subtracted from leadership in the Recruitment input. DEFAULT 0 (Missing).</summary>
    public int RecruitSystemTerm { get; init; }
    public InciteInputMode InciteInputMode { get; init; } = InciteInputMode.LeadershipMinusControllerSupport;
    public InciteSupportTiming InciteSupportTiming { get; init; } = InciteSupportTiming.PerIteration;
    /// <summary>A Subdue success clears the uprising flag at once. DEFAULT false.</summary>
    public bool SubdueEndsUprising { get; init; }
    public SabotageSkill SabotageSkill { get; init; } = SabotageSkill.Average;
    public SabotageTargetMode SabotageTargetMode { get; init; } = SabotageTargetMode.RandomFacility;
    /// <summary>Call the character sheet's OnTeamExposed for a foiled or failed team. DEFAULT true (a no-op until that sheet defines a rule).</summary>
    public bool ExposureHooks { get; init; } = true;

    /// <summary>Key targets of Assassination (the exposure gate, one capped roll, kidnap or kill). DEFAULT true (Decided Paul 2026-10-16). rule: m5-missions §6a.</summary>
    public bool KeyTargetRules { get; init; } = true;
    /// <summary>A key-target team qualifies for kill or kidnap if one character has at least this combat. Proposal, m5-missions §6a, missions/09 §4.</summary>
    public int KeyTeamMinCombat { get; init; } = 60;
    /// <summary>... and at least this espionage (the same character). Proposal, m5-missions §6a, missions/09 §4.</summary>
    public int KeyTeamMinEspionage { get; init; } = 60;
    /// <summary>Percent of successful qualified attempts that kidnap instead of kill. Decided Paul 2026-10-16 ("very very low"), value 3 Proposal; m5-missions §6a.</summary>
    public int KeyKidnapPercent { get; init; } = 3;

    // ---- switches whose other value has no rule (refused)
    /// <summary>DEFAULT false; true has no rule (refused).</summary>
    public bool FoilDestroysSpecialForces { get; init; }
    /// <summary>DEFAULT true; false has no rule (refused).</summary>
    public bool MissionsIgnoreForcedStop { get; init; } = true;
    /// <summary>DEFAULT false; true has no rule (refused).</summary>
    public bool BlockadeInterceptsMissions { get; init; }
    /// <summary>DEFAULT false; true has no rule (refused).</summary>
    public bool UprisingFailsMissions { get; init; }
    /// <summary>DEFAULT false; true has no rule (refused).</summary>
    public bool InformantIncident { get; init; }
    /// <summary>DEFAULT false; true has no rule (refused).</summary>
    public bool StoryMissions { get; init; }
    /// <summary>DEFAULT false; the New (later) missions are proposals only (refused).</summary>
    public bool NewMissions { get; init; }

    /// <summary>Throw <see cref="RuleMissingException"/> for every value the sheet has no rule for. rule: §Defaults ("no rule exists").</summary>
    public void Validate()
    {
        const string sheet = "m5-missions";
        if (FoilDestroysSpecialForces) throw new RuleMissingException("FoilDestroysSpecialForces", sheet);
        if (!MissionsIgnoreForcedStop) throw new RuleMissingException("MissionsIgnoreForcedStop=false", sheet);
        if (BlockadeInterceptsMissions) throw new RuleMissingException("BlockadeInterceptsMissions", sheet);
        if (UprisingFailsMissions) throw new RuleMissingException("UprisingFailsMissions", sheet);
        if (InformantIncident) throw new RuleMissingException("InformantIncident", sheet);
        if (StoryMissions) throw new RuleMissingException("StoryMissions", sheet);
        if (NewMissions) throw new RuleMissingException("NewMissions", sheet);
        if (MaxTeamMembers < 0 || MaxDecoys < 0) throw new ArgumentException("MaxTeamMembers and MaxDecoys must be 0 (no cap) or more.");
    }
}
