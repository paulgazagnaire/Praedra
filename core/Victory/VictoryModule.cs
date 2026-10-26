using Praedra.Core.Clock;

namespace Praedra.Core.Victory;

/// <summary>The game ended. Raised once. Day is the hook time T = K + 1 like the economy events. rule: setup-victory/04-victory-conditions §3.</summary>
public sealed record GameWon(int Index, int Day, Side Winner, string Reason) : GameEvent(Index, Day, nameof(GameWon));

/// <summary>
/// The capital-and-control run changed: Leader holds at least the share with the enemy capital gone since StartDay and wins on DueDay if it
/// keeps both, or (Leader null) the run broke. Goes to both sides, like GameWon, so the player sees the countdown. rule: m5-setup-and-victory
/// §Victory (capital and control).
/// </summary>
public sealed record DominanceChanged(int Index, int Day, Side? Leader, int StartDay, int DueDay) : GameEvent(Index, Day, nameof(DominanceChanged));

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
    public bool CollectiveWinsOnCharter { get; init; } = true;
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
    /// <summary>
    /// Capital and control (New rule, Decided Paul 2026-10-26, "Capital + 70%"): a side wins when the enemy capital is gone (the Hearth
    /// destroyed for the Hand; Charter not held by the Hand for the Collective) and it holds at least this percent of the systems the two
    /// sides hold, for <see cref="DominanceDays"/> days running. 0 = off (Classic has no such ending, B4 §15). rule: m5-setup-and-victory §Capital and control.
    /// </summary>
    public int DominanceSharePercent { get; init; } = 70;
    /// <summary>Days the capital-and-control run must last. Decided Paul 2026-10-26. rule: m5-setup-and-victory §Victory.</summary>
    public int DominanceDays { get; init; } = 180;
    /// <summary>
    /// A run survives this many days below the bar before it breaks (no notice while it lapses; it cannot win while below; on recovery its start and due day move on by the days below, with a new DominanceChanged). DEFAULT 10, a Dev
    /// lead Proposal after QA round 83 (one game started and broke a run 9 times in a year); 0 = break at once. rule: m5-setup-and-victory §Capital and control.
    /// </summary>
    public int DominanceGraceDays { get; init; } = 10;
}

/// <summary>rule: m5-setup-and-victory §Victory (KeyDeathFallback). None = original; WaiveCondition and Immortal are Proposals (08 P6 a, b).</summary>
public enum KeyDeathFallback { None, WaiveCondition, Immortal }

/// <summary>
/// Checks the victory conditions at the end of every Step (slot 6.11, last the other day-end hooks). Draws no random numbers. Once a winner
/// is set it stays and no second GameWon is raised. Tie (both sides satisfied in one check): the Collective, as in the original
/// (setup-victory/04 §3 "Gleaner"). After <see cref="GameSave.Load"/>, attach again.
/// </summary>
public sealed class VictoryModule
{
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

    /// <summary>The day the running capital-and-control run would win, or null when nobody has one. For the screens.</summary>
    public int? DominanceDueDay => S.DominanceSide != null ? S.DominanceSince + Rules.DominanceDays : null;
    /// <summary>The side on a capital-and-control run, or null.</summary>
    public Side? DominanceLeader => S.DominanceSide;

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
        bool corDestroyed = ctx.State.Harvester.IsDestroyed(Control.ControlModule.CapitalSystem);
        if (Rules.CollectiveWinsOnCharter && keysA && (Holds(Control.ControlModule.CapitalSystem, Side.Collective) || corDestroyed))
            collectiveReason = corDestroyed
                ? (Rules.StandardVictory ? "Charter is destroyed with Ravilla and Chair captured" : "Charter is destroyed")
                : (Rules.StandardVictory ? "Collective holds Charter with Ravilla and Chair captured" : "Collective captured Charter");
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

        int day = ctx.Day + 1;
        if (collectiveReason == null && handReason == null && Rules.DominanceSharePercent > 0)
            switch (Dominance(ctx, day))
            {
                case Side.Collective: collectiveReason = $"Collective held {Rules.DominanceSharePercent}% of the claimed worlds for {Rules.DominanceDays} days after Charter fell"; break;
                case Side.Hand: handReason = $"Hand held {Rules.DominanceSharePercent}% of the claimed worlds for {Rules.DominanceDays} days after the Hearth fell"; break;
            }

        Side winner; string reason;
        if (collectiveReason != null) { winner = Side.Collective; reason = collectiveReason; }
        else if (handReason != null) { winner = Side.Hand; reason = handReason; }
        else return;
        S.Winner = winner; S.WinReason = reason;
        ctx.Emit((i, _) => new GameWon(i, day, winner, reason));
    }

    /// <summary>
    /// rule: m5-setup-and-victory §Victory (capital and control). Today's leader: the side with the enemy capital gone and at least
    /// DominanceSharePercent of the systems the two sides hold (integer test, no rounding). A change of leader starts or breaks the run and
    /// raises DominanceChanged. Returns the side whose run has lasted DominanceDays, else null.
    /// </summary>
    private Side? Dominance(StepContext ctx, int day)
    {
        int hand = 0, collective = 0;
        foreach (var o in ctx.State.SystemOwners.Values)
            if (o == Side.Hand) hand++; else if (o == Side.Collective) collective++;
        int both = hand + collective, pct = Rules.DominanceSharePercent;
        bool hearthGone = S.Hq.Status == Fog.HqStatus.Destroyed;
        bool charterTaken = !(ctx.State.SystemOwners.TryGetValue(Control.ControlModule.CapitalSystem, out var c) && c == Side.Hand);
        Side? leader = both == 0 ? null
            : hearthGone && hand * 100 >= pct * both ? Side.Hand
            : charterTaken && collective * 100 >= pct * both ? Side.Collective
            : null;
        if (leader == S.DominanceSide && S.DominanceLapse != 0)
        {
            // back above inside the grace days: the days below do not count, so the run's start and due day move on by the lapse
            S.DominanceSince += day - S.DominanceLapse; S.DominanceLapse = 0;
            int start = S.DominanceSince, due = start + Rules.DominanceDays; var side = leader;
            ctx.Emit((i, _) => new DominanceChanged(i, day, side, start, due));
        }
        else if (leader != S.DominanceSide && S.DominanceSide != null && (S.DominanceLapse == 0 || day - S.DominanceLapse < Rules.DominanceGraceDays))
        {
            if (S.DominanceLapse == 0) S.DominanceLapse = day; // the run lapses: kept, silent, cannot win
            if (day - S.DominanceLapse < Rules.DominanceGraceDays) return null;
        }
        if (leader != S.DominanceSide)
        {
            S.DominanceLapse = 0;
            S.DominanceSide = leader; S.DominanceSince = leader != null ? day : 0;
            int start = S.DominanceSince, due = leader != null ? day + Rules.DominanceDays : 0;
            ctx.Emit((i, _) => new DominanceChanged(i, day, leader, start, due));
        }
        return leader != null && day - S.DominanceSince >= Rules.DominanceDays ? leader : null;
    }
}
