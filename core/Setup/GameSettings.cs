using Praedra.Core.Assault;
using Praedra.Core.Construction;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;

namespace Praedra.Core.Setup;

/// <summary>
/// Every switch of the rule sheets that is a game setting, with the sheet's DEFAULT as the default value (a test checks each one against
/// the module's own default, so the two cannot drift apart). Settings are configuration: they are not part of a save, so
/// <see cref="GameHost.Load"/> takes them again. The number tables (<see cref="Economy"/>, <see cref="Support"/>, <see cref="Construction"/>)
/// are null for "the module's default numbers". rule: CLAUDE.md hard rule 7 (undecided rules are switches with the sheet's DEFAULT).
/// </summary>
public sealed record GameSettings
{
    // ---- setup. rule: m5-setup-and-victory §P1 Deal (DealMode)
    /// <summary>Anchored (DEFAULT, Decided Paul 2026-10-16, m5-setup-and-victory §Mode Anchored) or File (the five fixed day-one CSVs plus fixed-owners.csv, the build before that date).</summary>
    public DealMode DealMode { get; init; } = DealMode.Anchored;
    /// <summary>Numbers and switches of the Anchored deal. Null = the sheet's DEFAULTs.</summary>
    public DealParams? Deal { get; init; }
    /// <summary>
    /// Place the Classic fixed Hearth forces (<c>hq_unit</c> rows of new-game-forces.csv: 2 Collective Army, 1 Deckhands, 4 Swift,
    /// 2 Plough, standing on the HQ system) at setup. DEFAULT off: the Classic setup (part B of batch B4) turns it on together with
    /// <see cref="MinGarrison"/> 0; on the hand-made forces table alone the Classic garrison rule only makes the Hand richer.
    /// rule: m5-setup-and-victory §P6, §P7 ([EXE-own] 0x51ad90; B4 §7 step 2).
    /// </summary>
    public bool HqFixedForces { get; init; }
    /// <summary>
    /// Minimum garrison per held system at setup; null = <c>min_garrison</c> of new-game-forces.csv (1 until the Classic setup). Classic = 0
    /// (B4 §6, [EXE-own] 0x51ab20): the Classic setup (part B) sets 0 together with <see cref="HqFixedForces"/>. rule: m5-setup-and-victory §P6.
    /// </summary>
    public int? MinGarrison { get; init; }

    // ---- travel. rule: m1-travel §Rule (Decided Paul 2026-10-16)
    /// <summary>Routes (DEFAULT, New: free travel plus faster hyperspace routes) or Straight (Classic yardstick: the Classic straight line, no routes).</summary>
    public Praedra.Core.Travel.TravelMode Travel { get; init; } = Praedra.Core.Travel.TravelMode.Routes;

    // ---- fleets. rule: m1-forced-stop-and-blockade §Pull-out (Decided Paul 2026-10-16: only gravity-well ships stop fleets in flight)
    public ForcedStopTrigger ForcedStopTrigger { get; init; } = ForcedStopTrigger.GravityWellShip;

    // ---- economy. rule: m2-slots-and-resources, m2-mines-refineries-pools, m2-maintenance
    /// <summary>Both incident timers run. rule: m2-slots-and-resources §Do incidents run on pinned values.</summary>
    public bool IncidentsEnabled { get; init; } = true;
    /// <summary>rule: m2-mines-refineries-pools §Blockade and uprising.</summary>
    public bool BlockadeStopsMining { get; init; } = true;
    /// <summary>An uprising stops mines and refineries like a blockade. Classic = on; DEFAULT on (B5b §3, decision 2, Dev lead 2026-10-18). rule: m2-mines-refineries-pools §Blockade and uprising.</summary>
    public bool UprisingStopsMining { get; init; } = true;
    /// <summary>Suspended mines and refineries leave the capacity sums. Classic = on (B5b §3); DEFAULT off (Blockade pause, 2026-10-18; Paul's economy/02 blockade rule is the DEFAULT, Classic is the switch). rule: m2-maintenance §Capacity.</summary>
    public bool SuspendedLeaveCapacity { get; init; }
    /// <summary>A suspended mine's or refinery's running cycle: Pause (DEFAULT) or Abandon (Classic, B5b §3). rule: m2-mines-refineries-pools §Blockade and uprising (Blockade pause, 2026-10-18).</summary>
    public StopMode MineStopMode { get; init; } = StopMode.Pause;
    /// <summary>Build orders over the free upkeep are refused whole. Classic = refused; DEFAULT refused (B5b §7, decision 1, Dev lead 2026-10-18). rule: m2-maintenance §Overdraft.</summary>
    public bool RefuseOverdrawOrders { get; init; } = true;
    /// <summary>Armed (DEFAULT, Classic) or FreeRunning autoscrap timer (B5b §6). rule: m2-maintenance §Overdraft and autoscrap.</summary>
    public AutoscrapTimer AutoscrapTimer { get; init; } = AutoscrapTimer.Armed;
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
    /// <summary>A new order cancels the project in progress (the rule before B5b). DEFAULT false: it queues behind (B5b §7, inferred). rule: m2-construction §Projects and queues.</summary>
    public bool NewOrderReplacesProject { get; init; }
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
    /// <summary>A suspended yard's running cycle: Pause (DEFAULT, Paul economy/02 "shipyards are paused") or Abandon (Classic, B5b §3). rule: m2-construction §Suspension (Blockade pause, 2026-10-18).</summary>
    public StopMode YardStopMode { get; init; } = StopMode.Pause;
    public ConstructionParams? Construction { get; init; }

    // ---- troops. rule: m3-troops-and-special-forces §Defaults
    /// <summary>UnloadRegiments only at a system the side holds (a neutral or enemy system is the assault's). rule: m3-troops-and-special-forces §Defaults (DEFAULT).</summary>
    public bool UnloadOnlyAtHeldSystem { get; init; } = true;

    // ---- control. rule: m2-control-and-capture
    /// <summary>
    /// How control is decided: Derived (Classic: troops on the planet, else support 60 or more) or AssaultOnly (the rule before B5a: support never
    /// changes control). DEFAULT Derived (Paul's card 2026-10-18 20:42: the only change that made the Collective AI hard to kill), with the AI's
    /// Classic garrison floor off (<see cref="Praedra.Core.AI.SimpleAiParams.ClassicGarrisonFloor"/>); AssaultOnly is the new-game option.
    /// rule: m2-control-and-capture §How control changes (B5a decision 1).
    /// </summary>
    public Praedra.Core.Control.ControlRule ControlRule { get; init; } = Praedra.Core.Control.ControlRule.Derived;
    /// <summary>The debug orders (DebugSetHolder under AssaultOnly, DebugSetSupport, DebugSetTroops, DebugSetDamage) are accepted only when this is on.</summary>
    public bool DebugOrdersEnabled { get; init; }

    // ---- defenses and bombardment. rule: m3-planetary-defenses §Defaults for undecided items; m3-bombardment §Defaults for undecided items
    /// <summary>Number tables and switches of the defense queries (assault gate, Death Star Shield counts for the gate). Null = the module's defaults.</summary>
    public DefenseParams? Defense { get; init; }
    /// <summary>
    /// Numbers and switches of the bombardment (admiral and General steps, die, Energy and Raw defence, the -20, the critical roll; the B7 switches
    /// BombardWastedHits, BatteryReturnFire, BombardFleetsTogether, BombardDamageScales, MilitaryCollateral on and HqBombardable off, the same in
    /// Classic and New, Dev lead 2026-10-19). Null = the module's defaults. rule: B7-bombardment §Collisions.
    /// </summary>
    public BombardParams? Bombard { get; init; }

    // ---- the Death Star. rule: m5-death-star §Interfaces, §2, §4
    /// <summary>Death Star switches and numbers (cap per system, ShieldBlocksDestroy, TrenchRun, sector penalty). Null = the sheet's DEFAULTs. Refused values throw at attach.</summary>
    public Praedra.Core.DeathStar.DeathStarParams? DeathStar { get; init; }

    // ---- assault. rule: m3-assault §Defaults for undecided items
    /// <summary>Number table and switches of the assault (die, leadership step, thresholds, collateral mode, landing cap, passes, enemy fleet blocks). Null = the module's defaults.</summary>
    public AssaultParams? Assault { get; init; }

    // ---- battle. rule: m4-battle-classic §Defaults for undecided items; m4-retreat-damage-repair §Defaults
    /// <summary>Switches and numbers of the Classic battle and the retreat (SubsystemRolls, task forces, tactic, RetreatMode, DeathStarStalemate, AI sides). Null = the module's defaults.</summary>
    public Praedra.Core.Battle.BattleParams? Battle { get; init; }

    // ---- research. rule: m5-research §Defaults for undecided items
    /// <summary>Research switches (facility and mission income, cost and mission scaling, one class per rung, Sensor Station tier 2). Null = the module's defaults.</summary>
    public Praedra.Core.Research.ResearchParams? Research { get; init; }

    // ---- characters. rule: m5-characters-and-force §Defaults for undecided items
    /// <summary>Character switches and numbers (Navigator, ride fleet, death, injury days, escape, Force ranks, start extras...). Null = the sheet's DEFAULTs. Refused values throw at attach.</summary>
    public Praedra.Core.Characters.CharacterParams? Characters { get; init; }

    // ---- missions. rule: m5-missions §Defaults for undecided items
    /// <summary>Mission switches (table lookup, success rule, decoys, caps, Incite and Subdue modes, Sabotage skill and target, exposure hooks...). Null = the sheet's DEFAULTs. Refused values throw at attach.</summary>
    public Praedra.Core.Missions.MissionParams? Missions { get; init; }

    // ---- victory. rule: setup-victory/04-victory-conditions (HQ-Only flavour)
    /// <summary>Victory switches. Null = all on (DEFAULT).</summary>
    public Praedra.Core.Victory.VictoryRules? Victory { get; init; }

    // ---- fog of war and the Hearth. rule: m5-fog-and-hq §Defaults for undecided items
    /// <summary>Fog switches (ruleset, Core reports, passing fleets, recon, presence reveals HQ, sector clue, observed window, start full copy). Null = the sheet's DEFAULTs.</summary>
    public Praedra.Core.Fog.FogParams? Fog { get; init; }
    /// <summary>HQ move and loss switches and numbers (speed, cost, departure refusal, arrival support effect, capture rule). Null = the sheet's DEFAULTs.</summary>
    public Praedra.Core.Fog.HqParams? Hq { get; init; }
}
