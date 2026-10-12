using Rebellion.Core.Assault;
using Rebellion.Core.Construction;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;

namespace Rebellion.Core.Setup;

/// <summary>
/// Every switch of the rule sheets that is a game setting, with the sheet's DEFAULT as the default value (a test checks each one against
/// the module's own default, so the two cannot drift apart). Settings are configuration: they are not part of a save, so
/// <see cref="GameHost.Load"/> takes them again. The number tables (<see cref="Economy"/>, <see cref="Support"/>, <see cref="Construction"/>)
/// are null for "the module's default numbers". rule: CLAUDE.md hard rule 7 (undecided rules are switches with the sheet's DEFAULT).
/// </summary>
public sealed record GameSettings
{
    // ---- fleets. rule: m1-forced-stop-and-blockade §The alternative trigger, as a switch
    public ForcedStopTrigger ForcedStopTrigger { get; init; } = ForcedStopTrigger.PaulRule;

    // ---- economy. rule: m2-slots-and-resources, m2-mines-refineries-pools, m2-maintenance
    /// <summary>Both incident timers run. rule: m2-slots-and-resources §Do incidents run on pinned values.</summary>
    public bool IncidentsEnabled { get; init; } = true;
    /// <summary>rule: m2-mines-refineries-pools §Blockade and uprising.</summary>
    public bool BlockadeStopsMining { get; init; } = true;
    /// <summary>rule: m2-maintenance §Rule (New) AutoscrapMode.</summary>
    public AutoscrapMode AutoscrapMode { get; init; } = AutoscrapMode.Classic;
    /// <summary>rule: m2-slots-and-resources §Rule (New) SlotSource.</summary>
    public SlotSource SlotSource { get; init; } = SlotSource.Pinned;
    public EconomyParams? Economy { get; init; }

    // ---- support. rule: m2-uprisings-and-garrison, m2-popular-support
    /// <summary>Classic = flat regiment count, New = weighted (the ruleset switch).</summary>
    public GarrisonRuleset SupportRuleset { get; init; } = GarrisonRuleset.Classic;
    /// <summary>The 30-day presence (blockade) timer. rule: m2-popular-support §Defaults (blockade_support_effect).</summary>
    public bool BlockadeSupportEffect { get; init; } = true;
    public SupportParams? Support { get; init; }

    // ---- construction. rule: m2-construction
    /// <summary>Deliveries pause at an armed enemy fleet. rule: m2-construction §Completion and delivery.</summary>
    public bool DeliveryForcedStop { get; init; } = true;
    /// <summary>rule: m2-construction §Lead review note, C20.</summary>
    public LostDestinationRule LostDestination { get; init; } = LostDestinationRule.Pause;
    public bool UnitDestinationMustBeControlled { get; init; } = true;
    /// <summary>
    /// The research gate: on = an item needs its research order at most the side's level in its track (or a setup grant); off = AllAvailable
    /// (the M3 stub). DEFAULT on once research is built (m5-research §Defaults; Paul decides the flip, question 1).
    /// rule: m5-research §Availability test.
    /// </summary>
    public bool RespectResearchOrder { get; init; } = true;
    /// <summary>The Death Star class row can be built. rule: m3-ships-and-fighters §Death Star as an ordinary class row.</summary>
    public bool DeathStarClassBuildable { get; init; } = true;
    /// <summary>Yards stop under the Classic blockade flag instead of the economic blockade. rule: m2-construction §Suspension.</summary>
    public bool UseClassicBlockadeFlag { get; init; }
    public ConstructionParams? Construction { get; init; }

    // ---- troops. rule: m3-troops-and-special-forces §Defaults
    /// <summary>UnloadRegiments only at a system the side holds (a neutral or enemy system is the assault's). rule: m3-troops-and-special-forces §Defaults (DEFAULT).</summary>
    public bool UnloadOnlyAtHeldSystem { get; init; } = true;

    // ---- control. rule: m2-control-and-capture
    /// <summary>The DebugSetHolder order is accepted only when this is on.</summary>
    public bool DebugOrdersEnabled { get; init; }

    // ---- defenses and bombardment. rule: m3-planetary-defenses §Defaults for undecided items; m3-bombardment §Defaults for undecided items
    /// <summary>Number tables and switches of the defense queries (assault gate, Death Star Shield counts for the gate). Null = the module's defaults.</summary>
    public DefenseParams? Defense { get; init; }
    /// <summary>Number table of the bombardment (admiral step, die, Energy and Raw defense, the -20). Null = the module's defaults.</summary>
    public BombardParams? Bombard { get; init; }

    // ---- assault. rule: m3-assault §Defaults for undecided items
    /// <summary>Number table and switches of the assault (die, leadership step, thresholds, collateral mode, landing cap, passes, enemy fleet blocks). Null = the module's defaults.</summary>
    public AssaultParams? Assault { get; init; }

    // ---- battle. rule: m4-battle-classic §Defaults for undecided items; m4-retreat-damage-repair §Defaults
    /// <summary>Switches and numbers of the Classic battle and the retreat (SubsystemRolls, task forces, tactic, RetreatMode, DeathStarStalemate, AI sides). Null = the module's defaults.</summary>
    public Rebellion.Core.Battle.BattleParams? Battle { get; init; }

    // ---- research. rule: m5-research §Defaults for undecided items
    /// <summary>Research switches (facility and mission income, cost and mission scaling, one class per rung, Sensor Station tier 2). Null = the module's defaults.</summary>
    public Rebellion.Core.Research.ResearchParams? Research { get; init; }

    // ---- characters. rule: m5-characters-and-force §Defaults for undecided items
    /// <summary>Character switches and numbers (Navigator, ride fleet, death, injury days, escape, Force ranks, start extras...). Null = the sheet's DEFAULTs. Refused values throw at attach.</summary>
    public Rebellion.Core.Characters.CharacterParams? Characters { get; init; }

    // ---- missions. rule: m5-missions §Defaults for undecided items
    /// <summary>Mission switches (table lookup, success rule, decoys, caps, Incite and Subdue modes, Sabotage skill and target, exposure hooks...). Null = the sheet's DEFAULTs. Refused values throw at attach.</summary>
    public Rebellion.Core.Missions.MissionParams? Missions { get; init; }

    // ---- victory. rule: setup-victory/04-victory-conditions (HQ-Only flavour)
    /// <summary>Victory switches. Null = all on (DEFAULT).</summary>
    public Rebellion.Core.Victory.VictoryRules? Victory { get; init; }

    // ---- fog of war and the Rebel HQ. rule: m5-fog-and-hq §Defaults for undecided items
    /// <summary>Fog switches (ruleset, Core reports, passing fleets, recon, presence reveals HQ, sector clue, observed window, start full copy). Null = the sheet's DEFAULTs.</summary>
    public Rebellion.Core.Fog.FogParams? Fog { get; init; }
    /// <summary>HQ move and loss switches and numbers (speed, cost, departure refusal, arrival support effect, capture rule). Null = the sheet's DEFAULTs.</summary>
    public Rebellion.Core.Fog.HqParams? Hq { get; init; }
}
