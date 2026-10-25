using Praedra.Core.Clock;

namespace Praedra.Core.Victory;

/// <summary>The game ended. Raised once. Day is the hook time T = K + 1 like the economy events. rule: setup-victory/04-victory-conditions §3.</summary>
public sealed record GameWon(int Index, int Day, Side Winner, string Reason) : GameEvent(Index, Day, nameof(GameWon));

/// <summary>
/// The switches of the simple (HQ-Only flavoured) victory. The four win switches are on by default.
/// Standard victory (the four key characters captured, setup-victory/04 §3) is behind <see cref="StandardVictory"/>, on by default; with it off only the HQ-Only conditions run (04 §3 HQ-Only mode).
/// "Holds no system" is a New rule: the Classic victory check (0x5180e0, the only writer of the game-over bit) has no branch for a wiped-out side,
/// a galaxy share or a time limit [EXE-own, B4 §15]. It stays on (DEFAULT New, Decided extension of the playable slice 2026-10-12) so a game
/// cannot go on with a side wiped out; off is the Classic behaviour.
/// </summary>
public sealed record VictoryRules
{
    /// <summary>Hand wins when the Collective no longer holds the Hearth system. rule: setup-victory/04 §3 (hqGone), HQ-Only mode.</summary>
    public bool HandWinsOnHqLost { get; init; } = true;
    /// <summary>Collective wins when it holds Charter or Charter is destroyed. rule: setup-victory/04 §3 (cor, destroyed bit 0x200), HQ-Only mode; B4 §15 [EXE-own] (bit 3 of +0x50).</summary>
    public bool CollectiveWinsOnCoruscant { get; init; } = true;
    /// <summary>Hand wins when the Collective holds no system. New rule, not Classic (B4 §15 [EXE-own]); DEFAULT New on (Decided extension, playable slice 2026-10-12).</summary>
    public bool HandWinsWhenCollectiveHasNoSystems { get; init; } = true;
    /// <summary>Collective wins when the Hand holds no system. New rule, not Classic (B4 §15 [EXE-own]); DEFAULT New on (Decided extension, playable slice 2026-10-12).</summary>
    public bool CollectiveWinsWhenHandHasNoSystems { get; init; } = true;
    /// <summary>
    /// Standard victory (m5-setup-and-victory: HqOnlyVictory = false): the Hand's HQ term also needs Nobody and Mae Abara captured, the
    /// Collective's Charter term needs Cassia Ravilla and The Chair captured (victory-rules.csv, CharacterCaptured rows). The setup sheet's
    /// DEFAULT is standard (on since 2026-10-16): missions that capture characters now exist (Abduction, and the Kidnap of m5-missions §6a).
    /// HQ-only (this switch false) stays a switch but is not shown on the new-game screen (m5-setup-and-victory §Defaults).
    /// </summary>
    public bool StandardVictory { get; init; } = true;
    /// <summary>What a dead key character does to standard victory. DEFAULT WaiveCondition (Decided Paul 2026-10-16; classic: None, the term can never be met). rule: m5-setup-and-victory §Defaults for undecided items (KeyDeathFallback).</summary>
    public KeyDeathFallback KeyDeathFallback { get; init; } = KeyDeathFallback.WaiveCondition;
}

/// <summary>rule: m5-setup-and-victory §Victory (KeyDeathFallback). None = original; WaiveCondition and Immortal are Proposals (08 P6 a, b).</summary>
public enum KeyDeathFallback { None, WaiveCondition, Immortal }

/// <summary>
/// Checks the victory conditions at the end of every Step (slot 6.11, last the other day-end hooks). Draws no random numbers. Once a winner
/// is set it stays and no second GameWon is raised. Tie (both sides satisfied in one check): the Collective, as in the original
/// (setup-victory/04 §3 "Tie"). After <see cref="GameSave.Load"/>, attach again.
/// </summary>
public sealed class VictoryModule
{
    public const string CoruscantName = "Charter";
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
    /// <summary>The Hearth system (saved), null if none.</summary>
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

        string? collectiveReason = null, handReason = null;
        bool keysA = !Rules.StandardVictory || KeysCaptured(Side.Collective), keysE = !Rules.StandardVictory || KeysCaptured(Side.Hand);
        // rule: m5-setup-and-victory §Victory: Charter held by the Collective OR destroyed (B4 §15 [EXE-own], bit 3 of +0x50)
        bool corDestroyed = ctx.State.DeathStar.IsDestroyed(CoruscantName);
        if (Rules.CollectiveWinsOnCoruscant && keysA && (Holds(CoruscantName, Side.Collective) || corDestroyed))
            collectiveReason = corDestroyed
                ? (Rules.StandardVictory ? "Charter is destroyed with Vader and Palpatine captured" : "Charter is destroyed")
                : (Rules.StandardVictory ? "Collective holds Charter with Vader and Palpatine captured" : "Collective captured Charter");
        if (collectiveReason == null && Rules.CollectiveWinsWhenHandHasNoSystems && !owners.Values.Any(v => v == Side.Hand)) collectiveReason = "Hand holds no system";
        // rule: setup-victory/04 §3 (the destroyed bit; a missing HQ counts as destroyed); m5-fog-and-hq §F7 (capture destroys it, DEFAULT AnyCause).
        // An at-rest HQ on a system the Collective no longer holds also counts (HqLostOnHolderChange = Never leaves it standing there).
        var hq = S.Hq;
        if (!keysE) { }
        else if (Rules.HandWinsOnHqLost && hq.Status == Fog.HqStatus.Destroyed)
            handReason = hq.DestroyedCause switch
            {
                Fog.FogModule.CauseCaptured => $"Hand captured the Hearth at {hq.System}",
                Fog.FogModule.CauseLostSupport => $"The Hearth fell at {hq.System}: the people turned from the Collective", // QA-11 (B5a)
                Fog.FogModule.CauseTroopsLeft => $"The Hearth fell at {hq.System}: its last Collective troops were gone", // QA-11 (B5a)
                _ => $"The Hearth was destroyed at {hq.System} ({hq.DestroyedCause})",
            };
        else if (Rules.HandWinsOnHqLost && hq.Status == Fog.HqStatus.AtRest && hq.System != null && !Holds(hq.System, Side.Collective))
            handReason = $"Hand captured the Hearth at {hq.System}";
        if (handReason == null && Rules.HandWinsWhenCollectiveHasNoSystems && !owners.Values.Any(v => v == Side.Collective)) handReason = "Collective holds no system";

        Side winner; string reason;
        if (collectiveReason != null) { winner = Side.Collective; reason = collectiveReason; }
        else if (handReason != null) { winner = Side.Hand; reason = handReason; }
        else return;
        S.Winner = winner; S.WinReason = reason;
        int day = ctx.Day + 1;
        ctx.Emit((i, _) => new GameWon(i, day, winner, reason));
    }
}
