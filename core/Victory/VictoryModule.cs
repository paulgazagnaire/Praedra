using Rebellion.Core.Clock;

namespace Rebellion.Core.Victory;

/// <summary>The game ended. Raised once. Day is the hook time T = K + 1 like the economy events. rule: setup-victory/04-victory-conditions §3.</summary>
public sealed record GameWon(int Index, int Day, Side Winner, string Reason) : GameEvent(Index, Day, nameof(GameWon));

/// <summary>
/// The switches of the simple (HQ-Only flavoured) victory. The four win switches are on by default.
/// Standard victory (the four key characters captured, setup-victory/04 §3) is behind <see cref="StandardVictory"/>, on by default; with it off only the HQ-Only conditions run (04 §3 HQ-Only mode).
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
    /// <summary>
    /// Standard victory (m5-setup-and-victory: HqOnlyVictory = false): the Empire's HQ term also needs Luke Skywalker and Mon Mothma captured, the
    /// Alliance's Coruscant term needs Darth Vader and Emperor Palpatine captured (victory-rules.csv, CharacterCaptured rows). The setup sheet's
    /// DEFAULT is standard (on since 2026-10-16): missions that capture characters now exist (Abduction, and the Kidnap of m5-missions §6a).
    /// HQ-only (this switch false) stays a switch but is not shown on the new-game screen (m5-setup-and-victory §Defaults).
    /// </summary>
    public bool StandardVictory { get; init; } = true;
    /// <summary>What a dead key character does to standard victory. DEFAULT WaiveCondition (Decided Paul 2026-10-16; original: None, the term can never be met). rule: m5-setup-and-victory §Defaults for undecided items (KeyDeathFallback).</summary>
    public KeyDeathFallback KeyDeathFallback { get; init; } = KeyDeathFallback.WaiveCondition;
}

/// <summary>rule: m5-setup-and-victory §Victory (KeyDeathFallback). None = original; WaiveCondition and Immortal are Proposals (08 P6 a, b).</summary>
public enum KeyDeathFallback { None, WaiveCondition, Immortal }

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

    /// <summary>The character terms of standard victory (victory-rules.csv). Set by the host from the character data.</summary>
    public IReadOnlyList<Characters.VictoryKey> Keys { get; set; } = Array.Empty<Characters.VictoryKey>();
    /// <summary>Is the character a captive now (the character module's query). Null = no character module: nobody is captured.</summary>
    public Func<int, bool>? IsCaptured { get; set; }
    public Func<int, bool>? IsDead { get; set; }

    /// <summary>The character terms of a side's standard rule set are all met (a dead key counts only with WaiveCondition). rule: m5-setup-and-victory §Victory.</summary>
    public bool KeysCaptured(Side side) => Keys.Where(k => k.Standard && k.Side == side)
        .All(k => IsCaptured?.Invoke(k.Character) == true || (Rules.KeyDeathFallback == KeyDeathFallback.WaiveCondition && IsDead?.Invoke(k.Character) == true));

    /// <summary>KeyDeathFallback = Immortal: a key character of a live standard rule set cannot die (the character module turns the Kill into an Injure).</summary>
    public bool RefusesDeath(Characters.CharacterRecord c) =>
        Rules.StandardVictory && Rules.KeyDeathFallback == KeyDeathFallback.Immortal && S.Winner == null && Keys.Any(k => k.Standard && k.Character == c.Id);

    private void Check(StepContext ctx)
    {
        if (!S.Active || S.Winner != null) return;
        var owners = ctx.State.SystemOwners;
        bool Holds(string sys, Side side) => owners.TryGetValue(sys, out var o) && o == side;

        string? allianceReason = null, empireReason = null;
        bool keysA = !Rules.StandardVictory || KeysCaptured(Side.Alliance), keysE = !Rules.StandardVictory || KeysCaptured(Side.Empire);
        if (Rules.AllianceWinsOnCoruscant && keysA && Holds(CoruscantName, Side.Alliance)) allianceReason = Rules.StandardVictory ? "Alliance holds Coruscant with Vader and Palpatine captured" : "Alliance captured Coruscant";
        if (allianceReason == null && Rules.AllianceWinsWhenEmpireHasNoSystems && !owners.Values.Any(v => v == Side.Empire)) allianceReason = "Empire holds no system";
        // rule: setup-victory/04 §3 (the destroyed bit; a missing HQ counts as destroyed); m5-fog-and-hq §F7 (capture destroys it, DEFAULT AnyCause).
        // An at-rest HQ on a system the Alliance no longer holds also counts (HqLostOnHolderChange = Never leaves it standing there).
        var hq = S.Hq;
        if (!keysE) { }
        else if (Rules.EmpireWinsOnHqLost && hq.Status == Fog.HqStatus.Destroyed)
            empireReason = hq.DestroyedCause switch
            {
                Fog.FogModule.CauseCaptured => $"Empire captured the Rebel HQ at {hq.System}",
                Fog.FogModule.CauseLostSupport => $"The Rebel HQ fell at {hq.System}: the people turned from the Alliance", // QA-11 (B5a)
                Fog.FogModule.CauseTroopsLeft => $"The Rebel HQ fell at {hq.System}: its last Alliance troops were gone", // QA-11 (B5a)
                _ => $"The Rebel HQ was destroyed at {hq.System} ({hq.DestroyedCause})",
            };
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
