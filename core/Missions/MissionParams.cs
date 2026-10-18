using Rebellion.Core.Characters;

namespace Rebellion.Core.Missions;

/// <summary>How a *MSTB (and the decoy) table is read between rows. rule: m5-missions §Mission tables (MstbLookup).</summary>
public enum MstbLookupMode
{
    /// <summary>DEFAULT, the original: the last row whose threshold is at or below x, for every table (Traced B2a: 0x55bed0 -> 0x58b7e0 -> 0x595090).</summary>
    Step,
    /// <summary>Piecewise linear between rows, truncating toward zero (Open Rebellion's wording, unverified vs original).</summary>
    Linear,
}

/// <summary>Below the first row of a table. FirstRowValue (DEFAULT) is the original (Traced B2a: 0x595090; 0x53e340 always finds a row). rule: m5-missions §Mission tables (MstbBelowFirstRow).</summary>
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

/// <summary>Where the Force sensers of a mission check stand. rule: m5-missions §3 Force sensing (B2b §2 "Where"; the scopes per kind and phase: B2d §3).</summary>
public enum ForceSenseScope
{
    /// <summary>
    /// DEFAULT: the check's own scopes (B2b §2 "Where" read with B2d §3): characters on the planet when the check guards the planet, aboard fleets at
    /// the place when it guards the orbit.
    /// </summary>
    CheckScope,
    /// <summary>Every qualified character at the place of the check, on the planet or aboard any fleet there (B2b's interim DEFAULT).</summary>
    Both,
    /// <summary>Only characters on the planet (riding no fleet).</summary>
    Planet,
    /// <summary>Only characters aboard fleets at the system.</summary>
    Fleets,
}

/// <summary>Which mission kinds run the betrayal check (the original's test, mission vtable +0x1c8, was not read). rule: m5-missions §3 Betrayal.</summary>
public enum BetrayalKinds
{
    /// <summary>DEFAULT: the kinds with Foilable = true.</summary>
    Foilable,
    /// <summary>Every kind that iterates.</summary>
    All,
}

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
    /// <summary>
    /// The mission check runs at all four points (leaving: planet then orbit; arriving: orbit; each job: next to the target). DEFAULT true (Traced
    /// B2d §1, §2). False: only the job check, for comparison runs. rule: m5-missions §1.
    /// </summary>
    public bool MissionGuardChecks { get; init; } = true;
    /// <summary>
    /// The guard lists carry no side filter, so own units in scope guard too (Traced B2d §3 at code level, not seen in play). DEFAULT true; false = enemy
    /// units only. rule: m5-missions §3 Guards.
    /// </summary>
    public bool GuardsAnySide { get; init; } = true;
    /// <summary>Only special forces may be decoys. DEFAULT true (Traced for the AI path only).</summary>
    public bool DecoysFromSpecialForcesOnly { get; init; } = true;
    /// <summary>Team size cap, 0 = none. DEFAULT 0 (Missing).</summary>
    public int MaxTeamMembers { get; init; }
    /// <summary>Decoy cap, 0 = none. DEFAULT 0 (Missing; the original probably caps at (field + 2) / 2, field not identified).</summary>
    public int MaxDecoys { get; init; }
    /// <summary>Regiments aboard guarding ships are guards (Traced B2d §3: flag +0x2c of the orbit scope). DEFAULT true; false = ships only.</summary>
    public bool CountAboardUnits { get; init; } = true;
    /// <summary>Diplomacy needs the uprising flag off. DEFAULT true ("calm" is Missing).</summary>
    public bool DiplomacyNeedsCalm { get; init; } = true;
    /// <summary>The target-system term of the Recruitment success input, not read yet. rule: m5-missions §Defaults (RecruitSystemTerm), Missing rules 10.</summary>
    public const int RecruitSystemTermDefault = 0;
    /// <summary>Subtracted from leadership in the Recruitment input. DEFAULT <see cref="RecruitSystemTermDefault"/> (Missing).</summary>
    public int RecruitSystemTerm { get; init; } = RecruitSystemTermDefault;
    public InciteInputMode InciteInputMode { get; init; } = InciteInputMode.LeadershipMinusControllerSupport;
    public InciteSupportTiming InciteSupportTiming { get; init; } = InciteSupportTiming.PerIteration;
    /// <summary>A Subdue success clears the uprising flag at once. DEFAULT false.</summary>
    public bool SubdueEndsUprising { get; init; }
    public SabotageSkill SabotageSkill { get; init; } = SabotageSkill.Average;
    public SabotageTargetMode SabotageTargetMode { get; init; } = SabotageTargetMode.RandomFacility;
    /// <summary>Call the character sheet's OnTeamExposed for a failed team (a foiled team duels instead, B2d §6). DEFAULT true (a no-op until that sheet defines a rule).</summary>
    public bool ExposureHooks { get; init; } = true;

    /// <summary>Key targets of Assassination (the exposure gate, one capped roll, kidnap or kill). DEFAULT true (Decided Paul 2026-10-16). rule: m5-missions §6a.</summary>
    public bool KeyTargetRules { get; init; } = true;
    /// <summary>A key-target team qualifies for kill or kidnap if one character has at least this combat. Proposal, m5-missions §6a, missions/09 §4.</summary>
    public int KeyTeamMinCombat { get; init; } = 60;
    /// <summary>... and at least this espionage (the same character). Proposal, m5-missions §6a, missions/09 §4.</summary>
    public int KeyTeamMinEspionage { get; init; } = 60;
    /// <summary>Percent of successful qualified attempts that kidnap instead of kill. Decided Paul 2026-10-16 ("very very low"), value 3 Proposal; m5-missions §6a.</summary>
    public int KeyKidnapPercent { get; init; } = 3;

    // ---- Force sensing and betrayal (B2b). rule: m5-missions §3 Force sensing, Betrayal.
    /// <summary>Enemy Force users at the target can sense the group (0x589e40). DEFAULT true (Traced B2b).</summary>
    public bool ForceSensing { get; init; } = true;
    /// <summary>Every member may betray on each check (0x589f10). DEFAULT true (Traced B2b).</summary>
    public bool Betrayal { get; init; } = true;
    /// <summary>DEFAULT CheckScope (the planet and fleet scopes of 0x589a40, B2d §3).</summary>
    public ForceSenseScope ForceSenseScope { get; init; } = ForceSenseScope.CheckScope;
    /// <summary>DEFAULT Foilable.</summary>
    public BetrayalKinds BetrayalKinds { get; init; } = BetrayalKinds.Foilable;
    /// <summary>Lowest Force of a group member who can be sensed. GNPRTB 3585 = 1 (Traced B2b, 0x5888a0).</summary>
    public int SensedMinForce { get; init; } = 1;
    /// <summary>Lowest Force of a senser. GNPRTB 3586 = 60 (Traced B2b, 0x588840).</summary>
    public int SenserMinForce { get; init; } = 60;
    /// <summary>Sensing chance constant: p = Force(member) + Force(senser) + this. GNPRTB 3587 = -100 (Traced B2b, 0x55e4d0).</summary>
    public int SenseConstant { get; init; } = -100;
    /// <summary>Luke Skywalker, taken first among sensers and among group Force users (key 0x32000242: record 0x242 = 578). Traced B2b, 0x58a9e0.</summary>
    public int SenseFirstCharacter { get; init; } = 578;
    /// <summary>Darth Vader, taken second (key 0x35000281: record 0x281 = 641). Traced B2b, 0x58ac50.</summary>
    public int SenseSecondCharacter { get; init; } = 641;

    // ---- switches whose other value has no rule (refused)
    /// <summary>DEFAULT true; false has no rule (refused).</summary>
    public bool MissionsIgnoreForcedStop { get; init; } = true;
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
        if (!MissionsIgnoreForcedStop) throw new RuleMissingException("MissionsIgnoreForcedStop=false", sheet);
        if (UprisingFailsMissions) throw new RuleMissingException("UprisingFailsMissions", sheet);
        if (InformantIncident) throw new RuleMissingException("InformantIncident", sheet);
        if (StoryMissions) throw new RuleMissingException("StoryMissions", sheet);
        if (NewMissions) throw new RuleMissingException("NewMissions", sheet);
        if (MaxTeamMembers < 0 || MaxDecoys < 0) throw new ArgumentException("MaxTeamMembers and MaxDecoys must be 0 (no cap) or more.");
    }
}
