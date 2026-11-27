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
}
