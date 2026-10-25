namespace Praedra.Core.Fog;

/// <summary>Classic = the Classic rule; New = Classic plus ages, the Observed level and passing fleets that explore. rule: m5-fog-and-hq §Rule (New).</summary>
public enum FogRuleset { Classic, New }

/// <summary>Which units count in the HQ departure refusal. rule: m5-fog-and-hq §F6 (HqDepartureOperands), Contradictions 2.</summary>
public enum HqDepartureOperands { Blockaders, Alliance, Off }

/// <summary>When an at-rest HQ is lost with its system. rule: m5-fog-and-hq §F7 (HqLostOnHolderChange), Open question 1.</summary>
public enum HqLostOnHolderChange { Never, AssaultOnly, AnyCause }

/// <summary>What happens to an HQ whose destination is no longer Alliance-held on arrival. rule: §F6 (HqLostDestination).</summary>
public enum HqLostDestination { DestroyOnArrival, Return }

/// <summary>The coarse "HQ is in sector X" clue. rule: §N6 (SectorSuspicion).</summary>
public enum SectorSuspicion { Off, OnEspionage }

/// <summary>
/// Every switch and number of the fog-of-war and Rebel HQ sheet, each with the sheet's DEFAULT. Configuration, not saved (pass it again on
/// load through <see cref="Setup.GameSettings.Fog"/>). rule: m5-fog-and-hq §Defaults for undecided items; CLAUDE.md hard rule 7.
/// Not built (the sheet says so, or the data does not exist yet): informants (InformantsEnabled false), intel noise (off), HQ activity
/// leaks and the plausible-HQ list.
/// </summary>
public sealed record FogParams
{
    /// <summary>rule: §Defaults FogRuleset = New.</summary>
    public FogRuleset Ruleset { get; init; } = FogRuleset.New;
    /// <summary>The galaxy graph is public to both sides. rule: §F1 LanesPublic (the core never fogs the graph; the switch is recorded only).</summary>
    public bool LanesPublic { get; init; } = true;
    /// <summary>Core-group systems report owner and support to both sides every Step. rule: §F2 CoreReport (Manual p.69).</summary>
    public bool CoreReportsInstantly { get; init; } = true;
    /// <summary>A fleet passing a system explores it and is logged. DEFAULT true in New; Classic forces it off. rule: §Defaults PassingFleetsExplore.</summary>
    public bool PassingFleetsExplore { get; init; } = true;
    /// <summary>An agent on a mission gives live access (needs m5-missions' AgentsAt). rule: §F3 3.</summary>
    public bool AgentPresenceCounts { get; init; } = true;
    /// <summary>A recon success writes group C including HqHere. rule: §F2 Recon (Lead).</summary>
    public bool ReconRevealsFacilities { get; init; } = true;
    /// <summary>A fleet placed at the HQ system reveals it. rule: §N6, Contradictions 1.</summary>
    public bool PresenceRevealsHq { get; init; }
    /// <summary>rule: §N6, Open question 3.</summary>
    public SectorSuspicion SectorSuspicion { get; init; } = SectorSuspicion.Off;
    /// <summary>Days a look stays "Observed", and the age at which a sighting is dropped. Proposal. rule: §N1, §N3.</summary>
    public int ObservedWindowDays { get; init; } = 30;
    /// <summary>One HQ: a reveal that finds it clears the side's other HqHere flags. rule: §N5.</summary>
    public bool SingleHqCleanup { get; init; } = true;
    /// <summary>Systems each side gets a FullCopy of at setup (matched by name, skipped when not in the galaxy). Traced: The Shoal to the Empire. rule: §F4 3.</summary>
    public IReadOnlyList<string> StartFullCopyEmpire { get; init; } = new[] { "The Shoal" };
    public IReadOnlyList<string> StartFullCopyAlliance { get; init; } = Array.Empty<string>();
    /// <summary>
    /// The Setup reveal also writes group C (facilities) for the start_explored_all systems (never D or E). Classic = on (B4 §11, [EXE-own]
    /// 0x51b990, 0x539ac0). DEFAULT on (Classic); AI measured 2026-10-18: floors pass, Medium mirror 12/16 either way. rule: m5-fog-and-hq §F2 Setup, §F4 1.
    /// </summary>
    public bool SetupRevealsFacilities { get; init; } = true;
    /// <summary>The galaxy group whose systems report instantly (planets file group label). rule: §F2 CoreReport ("Group is Core"), Contradictions 9.</summary>
    public string CoreGroup { get; init; } = "core";

    // ---- espionage extra systems. rule: §F2 Espionage success 2 (GNPRTB 6176 to 6181, Traced)
    /// <summary>Rich target (holds the HQ): N = rand(0..RichSpread) + RichBase. GNPRTB 6181 = 4, 6180 = 1.</summary>
    public int EspionageRichSpread { get; init; } = 4;
    public int EspionageRichBase { get; init; } = 1;
    /// <summary>Empire-held Charter: GNPRTB 6179 = 4, 6178 = 1.</summary>
    public int EspionageCapitalSpread { get; init; } = 4;
    public int EspionageCapitalBase { get; init; } = 1;
    /// <summary>Other targets: GNPRTB 6177 = 0, 6176 = 1.</summary>
    public int EspionageOtherSpread { get; init; } = 0;
    public int EspionageOtherBase { get; init; } = 1;
    // ---- Sensor Station. rule: m5-fog-and-hq §N4 (route-only, N-day warning; movement-redesign-draft §C). The 2026-10-12 lane-hop range
    // (SensorRangeHops) and the 2-hop Advanced tier (AdvancedSensorEnabled) are retired with the lanes (movement-redesign-draft §C); FogParams
    // is configuration, never saved, so the two properties are removed outright.
    /// <summary>How many days ahead a station warns of an other-side fleet that will reach its system. Decided (Paul 2026-10-16, "10 days"). rule: §N4.</summary>
    public int SensorWarningDays { get; init; } = 10;
    /// <summary>
    /// A fleet whose trip ENDS at the station's system after an off-route jump into it is reported (the station watches its own system's
    /// approaches). Proposal (lead, DEFAULT true) for Paul to veto: movement-redesign-draft §C says "off-route fleets are invisible". rule: §N4.
    /// </summary>
    public bool SensorSeesOffRouteArrivals { get; init; } = true;
    /// <summary>Largest capital-ship total reported as Few (1 to FewMax). Proposal (mine), §N4 size class.</summary>
    public int SensorFewMax { get; init; } = 3;
    /// <summary>Largest total reported as Squadron (FewMax + 1 to SquadronMax); above is Large. Proposal (mine), §N4 size class.</summary>
    public int SensorSquadronMax { get; init; } = 9;

    /// <summary>The capital for the espionage "rich" test (planets file name).</summary>
    public string CapitalSystem { get; init; } = "Charter";

    /// <summary>The Sensor Station effect is on in New and off in Classic. rule: §Rule (New) intro.</summary>
    public bool SensorsWork => Ruleset == FogRuleset.New;

    /// <summary>True when the passing-fleet rule is on for the ruleset in force.</summary>
    public bool PassingExplores => Ruleset == FogRuleset.New && PassingFleetsExplore;
}

/// <summary>
/// The Rebel HQ numbers and switches. rule: m5-fog-and-hq §F6, §F7, §Numbers, §Defaults (HQ line).
/// </summary>
public sealed record HqParams
{
    /// <summary>Hop speed of the HQ: the GNPRTB 1 default speed (Traced constant; the HQ class value is NOT TRACED).</summary>
    public int Speed { get; init; } = 100;
    /// <summary>Refined material paid at acceptance; 0 = none found (NOT TRACED, DEFAULT). Above 0 the order is rejected NoFunds when short.</summary>
    public int MoveCost { get; init; }
    /// <summary>The destination needs a free energy slot (NoSlot). NOT TRACED, DEFAULT off.</summary>
    public bool UsesFacilitySlot { get; init; }
    /// <summary>The destination must be Alliance-held (NotHeld). DEFAULT; the player rule is NOT TRACED.</summary>
    public bool DestinationMustBeHeld { get; init; } = true;
    public HqDepartureOperands DepartureOperands { get; init; } = HqDepartureOperands.Blockaders;
    /// <summary>Departure refusal: Base - ShipWeight x ships - FighterWeight x fighters &lt; Limit. 100 and 31 Traced-medium; 5 and 2 = GNPRTB 7684, 7685.</summary>
    public int DepartureBase { get; init; } = 100;
    public int DepartureShipWeight { get; init; } = 5;
    public int DepartureFighterWeight { get; init; } = 2;
    public int DepartureLimit { get; init; } = 31;
    /// <summary>The HQ in transit is not stopped by enemy fleets (DEFAULT true; false is not built).</summary>
    public bool IgnoresForcedStop { get; init; } = true;
    /// <summary>Alliance support change in every populated system of the destination sector on arrival: GNPRTB 5127 = -10 (Traced).</summary>
    public int ArrivalSupportDelta { get; init; } = -10;
    public HqLostOnHolderChange LostOnHolderChange { get; init; } = HqLostOnHolderChange.AnyCause;
    /// <summary>Return is a switch value only (not built in the first pass: treated as DestroyOnArrival, see the build notes).</summary>
    public HqLostDestination LostDestination { get; init; } = HqLostDestination.DestroyOnArrival;
}
