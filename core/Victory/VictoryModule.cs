using Rebellion.Core.Clock;

namespace Rebellion.Core.Victory;

/// <summary>The game ended. Raised once. Day is the hook time T = K + 1 like the economy events. rule: setup-victory/04-victory-conditions §3.</summary>
public sealed record GameWon(int Index, int Day, Side Winner, string Reason) : GameEvent(Index, Day, nameof(GameWon));

/// <summary>
/// The switches of the simple (HQ-Only flavoured) victory. Each is on by default.
/// DEFAULT omission: Classic standard victory also needs the four key characters (Luke, Mon Mothma, Vader, Palpatine captured;
/// setup-victory/04 §3); characters do not exist yet, so only the HQ-Only conditions run (04 §3 HQ-Only mode).
/// "Holds no system" is not traced in the original (04 §4 [NOT TRACED]); it is a DEFAULT of the playable slice so a game cannot go on with a side wiped out.
/// </summary>
public sealed record VictoryRules
{
    /// <summary>Empire wins when the Alliance no longer holds the Rebel HQ system. rule: setup-victory/04 §3 (hqGone), HQ-Only mode.</summary>
    public bool EmpireWinsOnHqLost { get; init; } = true;
    /// <summary>Alliance wins when it holds Coruscant. rule: setup-victory/04 §3 (cor), HQ-Only mode.</summary>
    public bool AllianceWinsOnCoruscant { get; init; } = true;
    /// <summary>Empire wins when the Alliance holds no system. DEFAULT (playable slice 2026-10-12).</summary>
    public bool EmpireWinsWhenAllianceHasNoSystems { get; init; } = true;
    /// <summary>Alliance wins when the Empire holds no system. DEFAULT (playable slice 2026-10-12).</summary>
    public bool AllianceWinsWhenEmpireHasNoSystems { get; init; } = true;
}

/// <summary>
/// Checks the victory conditions at the end of every Step (slot 6.11, last the other day-end hooks). Draws no random numbers. Once a winner
/// is set it stays and no second GameWon is raised. Tie (both sides satisfied in one check): the Alliance, as in the original
/// (setup-victory/04 §3 "Tie"). After <see cref="GameSave.Load"/>, attach again.
/// </summary>
public sealed class VictoryModule
{
    public const string CoruscantName = "Coruscant";
    private readonly Game _game;
    public VictoryRules Rules { get; }

    private VictoryModule(Game game, VictoryRules rules) { _game = game; Rules = rules; }

    public static VictoryModule Attach(Game game, VictoryRules? rules = null)
    {
        var m = new VictoryModule(game, rules ?? new VictoryRules());
        // Slot 6.11 is the last day-end slot and MUST stay last: research (6.9) and characters (6.10) come before it. rule: m5-setup-and-victory §Game over
        game.RegisterHook("6.11", m.Check);
        return m;
    }

    private ScenarioState S => _game.State.Scenario;

    /// <summary>The winner, null while the game runs.</summary>
    public Side? Winner => S.Winner;
    public string? Reason => S.WinReason;
    /// <summary>The Rebel HQ system (saved), null if none.</summary>
    public string? HqSystem => S.HqSystem;

    private void Check(StepContext ctx)
    {
        if (!S.Active || S.Winner != null) return;
        var owners = ctx.State.SystemOwners;
        bool Holds(string sys, Side side) => owners.TryGetValue(sys, out var o) && o == side;

        string? allianceReason = null, empireReason = null;
        if (Rules.AllianceWinsOnCoruscant && Holds(CoruscantName, Side.Alliance)) allianceReason = "Alliance captured Coruscant";
        if (allianceReason == null && Rules.AllianceWinsWhenEmpireHasNoSystems && !owners.Values.Any(v => v == Side.Empire)) allianceReason = "Empire holds no system";
        // rule: setup-victory/04 §3 (the destroyed bit; a missing HQ counts as destroyed); m5-fog-and-hq §F7 (capture destroys it, DEFAULT AnyCause).
        // An at-rest HQ on a system the Alliance no longer holds also counts (HqLostOnHolderChange = Never leaves it standing there).
        var hq = S.Hq;
        if (Rules.EmpireWinsOnHqLost && hq.Status == Fog.HqStatus.Destroyed)
            empireReason = hq.DestroyedCause == Fog.FogModule.CauseCaptured ? $"Empire captured the Rebel HQ at {hq.System}" : $"The Rebel HQ was destroyed at {hq.System} ({hq.DestroyedCause})";
        else if (Rules.EmpireWinsOnHqLost && hq.Status == Fog.HqStatus.AtRest && hq.System != null && !Holds(hq.System, Side.Alliance))
            empireReason = $"Empire captured the Rebel HQ at {hq.System}";
        if (empireReason == null && Rules.EmpireWinsWhenAllianceHasNoSystems && !owners.Values.Any(v => v == Side.Alliance)) empireReason = "Alliance holds no system";

        Side winner; string reason;
        if (allianceReason != null) { winner = Side.Alliance; reason = allianceReason; }
        else if (empireReason != null) { winner = Side.Empire; reason = empireReason; }
        else return;
        S.Winner = winner; S.WinReason = reason;
        int day = ctx.Day + 1;
        ctx.Emit((i, _) => new GameWon(i, day, winner, reason));
    }
}
