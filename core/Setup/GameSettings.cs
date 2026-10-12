using Rebellion.Core.Construction;
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
    /// <summary>Research stub: off = AllAvailable (DEFAULT since M3), on = RespectResearchOrder. rule: m3-ships-and-fighters §Sides and availability.</summary>
    public bool RespectResearchOrder { get; init; }
    /// <summary>The Death Star class row can be built. rule: m3-ships-and-fighters §Death Star as an ordinary class row.</summary>
    public bool DeathStarClassBuildable { get; init; } = true;
    /// <summary>Yards stop under the Classic blockade flag instead of the economic blockade. rule: m2-construction §Suspension.</summary>
    public bool UseClassicBlockadeFlag { get; init; }
    public ConstructionParams? Construction { get; init; }

    // ---- control. rule: m2-control-and-capture
    /// <summary>The DebugSetHolder order is accepted only when this is on.</summary>
    public bool DebugOrdersEnabled { get; init; }
}
