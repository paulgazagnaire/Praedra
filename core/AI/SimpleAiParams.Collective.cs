namespace Praedra.Core.AI;

/// <summary>
/// The Collective commander's later plans (AI trainer "Rooster", from 2026-11-27; design specs/engine/ai-notes/2026-11-26-adaptive-commander.md
/// §3, the Collective's plans 11 to 16 and 1 to 8). AI tuning values, not rules of the game: each is a DEFAULT chosen here, off unless a level
/// turns it on. Kept apart from SimpleAiParams.cs so two AI trainers can work at once.
/// </summary>
public sealed partial record SimpleAiParams
{
    /// <summary>
    /// Neutralise (the commander's Collective plan 11, "Hollow out"): Hand worlds whose people half side with the Collective (its support
    /// NeutraliseMinSupport or more) and that hold few regiments are shelled with the Military target until no regiment stands there. Then
    /// control is worked out from support alone (m2-control-and-capture §Control: no troops, 60 or more joins that side, 41 to 59 neutral), so
    /// the world goes neutral or joins the Collective with no landing and no Collective garrison. A front whose sector fits it best runs it
    /// (FrontPlans). Off = no such plan (DEFAULT).
    /// </summary>
    public bool Neutralise { get; init; }
    /// <summary>Neutralise: the least Collective support seen on a Hand world for it to be shelled bare (41 = it goes at least neutral).</summary>
    public int NeutraliseMinSupport { get; init; } = 41;
    /// <summary>Neutralise: the most Hand regiments seen on a world for it to be worth shelling bare.</summary>
    public int NeutraliseMaxDefenders { get; init; } = 3;
    /// <summary>Neutralise: the most shellings of one world in 30 days (the fog hides whether the last regiment died).</summary>
    public int NeutraliseShellsPer30Days { get; init; } = 8;
    /// <summary>Neutralise: a world in a Neutralise front is worth this percent of a plain invasion target there (the troops it saves).</summary>
    public int NeutraliseFrontPercent { get; init; } = 300;
    /// <summary>Neutralise: a world outside a Neutralise front is worth this percent of a plain invasion target (0 = only inside the front).</summary>
    public int NeutraliseElsewherePercent { get; init; }
    /// <summary>
    /// Refit away (Rooster 2026-11-27, after #416: Neutralise keeps the warships out shelling, so the refit, which scrapped only idle ships on
    /// held worlds, found half as many; Paul 2026-11-26: the best navy): the refit (RefitBelowPercent) may also scrap a weak warship of a fleet
    /// standing on a world the side does not hold, when the ship carries nothing, no enemy fleet is seen there, no regiment is aboard and no landing of that fleet is pending (a shelling is no
    /// bar: the scrapped ship only stops adding its power). Off = only idle ships on held worlds (DEFAULT, the AI before 2026-11-27).
    /// </summary>
    public bool RefitAway { get; init; }
    /// <summary>
    /// Raid past the shields (Rooster 2026-11-27; QA of #416: RaidTarget never read the shields, so a pile under them shelled for no hits,
    /// m3-bombardment §2 hits = max(0, P - S), and Maverick's GuardRaidedShields beat the raids that way): a raid is picked and made only where
    /// the pile's power beats the shields seen, or the pile carries Wreckers (WreckShields); a garrison is shelled only where the power there beats
    /// them. Off = shellings ignore the shields (DEFAULT).
    /// </summary>
    public bool RaidShieldsSeen { get; init; }
    /// <summary>
    /// Wreck the shields (Paul 2026-11-27 on #412, special forces aboard fleets: "a raiding fleet carrying 9 saboteurs could be nasty", use it
    /// hard): armed Collective fleets take Wreckers aboard at held worlds; a pile carrying them may pick a raid or Neutralise target whose shields
    /// its power does not beat, and over it launches the Wreckers from orbit one at a time (Sabotage, half the spot chance, defences first)
    /// until the shields fall or no Wrecker is left, then shells. The preparation is visible: shields fall before the strike. Off = no Wrecker
    /// boards (DEFAULT).
    /// </summary>
    public bool WreckShields { get; init; }
    /// <summary>
    /// WreckShields, QA-134 (Wreckers rode fleets up to 340 days with nothing to wreck): Wreckers board, and stay aboard, only while a shielded
    /// enemy world is seen within raid reach of the held world the fleet stands on; else they get off there for the ground missions. Off at
    /// every level (DEFAULT): over 96 seeds it cut the Collective's sabotage successes by 40-70% and its wins by 2-4 (walled Hand 81 -> 77,
    /// Medium Hand 78 -> 76), since a fleet that leaves a quiet world often meets shields later. Wreckers riding idle are the price.
    /// </summary>
    public bool WreckNearShieldsOnly { get; init; }
    /// <summary>WreckShields: Wreckers wanted aboard each armed fleet (the rule's cap is 9 special forces a fleet, FleetSpecialForcesMax).</summary>
    public int WreckShieldsForces { get; init; } = 3;
    /// <summary>WreckShields: free Wreckers walk to the rally world while fewer than this many ride fleets or stand there (0 = they board only where they stand).</summary>
    public int WreckShieldsPool { get; init; } = 6;
    /// <summary>
    /// Shields felt (Rooster 2026-11-27; with fog a fleet in orbit sees no facilities, m5-fog-and-hq §F2, so the AI shelled shields it did not
    /// know of again and again for no hits: seed 1 vs the walled Hand, 24 Neutralise shellings in 1200 days): for this many days the side reads
    /// the shields its own last shelling of a world met (BombardmentStarted S) when that is newer than its picture; an own Sabotage success
    /// there since reads as no shields until the next shelling tells. The Collective only for now (the Hand is Maverick's to measure). 0 = the
    /// picture only (DEFAULT).
    /// </summary>
    public int ShieldsFeltDays { get; init; }
    /// <summary>
    /// Fighters wait aboard (Rooster 2026-11-28; Medium mirror, seeds 1-8, 3000 days: 269 Collective squadrons based at its shipyards with no
    /// ship there were wiped out in 61 battles that cost the Hand nothing, most of the Collective's battle losses): no new fighter order while
    /// this many of the side's squadrons wait on the ground. The Collective reads this one. 0 = no cap (DEFAULT).
    /// </summary>
    public int FighterWaitMax { get; init; }
    /// <summary>FighterWaitMax for the Hand (Maverick's to measure: the Hand builds about 32 Threshers a game by day 1000). 0 = no cap (DEFAULT).</summary>
    public int FighterWaitMaxHand { get; init; }
}
