using Rebellion.Core.Clock;

namespace Rebellion.Core.Characters;

// ---- orders. rule: m5-characters-and-force §Posts (Orders), §Moving a character alone. All step 1, immediate, in order sequence; a rejected
// order changes nothing; the order's side must be the character's side.

/// <summary>Attach a character to a fleet at its system, with a post (None = a passenger). rule: §Posts (AttachCharacter).</summary>
public sealed record AttachCharacterOrder(long Sequence, Side Side, int Character, int Fleet, CharacterPost Post) : Order(Sequence, Side);

/// <summary>Detach a character from its fleet; it stays at the fleet's system. rule: §Posts (DetachCharacter).</summary>
public sealed record DetachCharacterOrder(long Sequence, Side Side, int Character) : Order(Sequence, Side);

/// <summary>Make a character the General of a system its side holds. rule: §Posts (AssignSystemGeneral).</summary>
public sealed record AssignSystemGeneralOrder(long Sequence, Side Side, int Character, string System) : Order(Sequence, Side);

/// <summary>Empty a system's General post. rule: §Posts (ReleaseSystemGeneral).</summary>
public sealed record ReleaseSystemGeneralOrder(long Sequence, Side Side, string System) : Order(Sequence, Side);

/// <summary>Send a lone character to a system at speed 100. rule: §Moving a character alone (MoveCharacter).</summary>
public sealed record MoveCharacterOrder(long Sequence, Side Side, int Character, string System) : Order(Sequence, Side);

/// <summary>Rejection reasons of the character orders (the sheet's names). rule: m5-characters-and-force §Posts (Orders), §Moving a character alone.</summary>
public static class CharacterReject
{
    public const string UnknownCharacter = "UnknownCharacter";
    public const string NotFree = "NotFree";
    public const string NotAtSystem = "NotAtSystem";
    public const string FleetInTransit = "FleetInTransit";
    public const string AlreadyAttached = "AlreadyAttached";
    public const string OnMission = "OnMission";
    public const string HoldsSystemPost = "HoldsSystemPost";
    public const string NoCapability = "NoCapability";
    public const string PostTaken = "PostTaken";
    public const string NotAttached = "NotAttached";
    public const string AlreadyHoldsSystemPost = "AlreadyHoldsSystemPost";
    public const string NoSystemGeneral = "NoSystemGeneral";
    public const string Attached = "Attached";
    public const string NoPath = "NoPath";
}

/// <summary>Why a state change happened (callers pass it). rule: m5-characters-and-force §Capture, injury, death, freeing.</summary>
public static class CharacterCause
{
    public const string Mission = "Mission", Battle = "Battle", DeathStar = "DeathStar", Uprising = "Uprising", Assault = "Assault",
        Blockade = "Blockade", Story = "Story", Debug = "Debug", Escape = "Escape";
    /// <summary>B2e: a prisoner freed because his place changed side; the Force-user encounter; a trainer's awakening.</summary>
    public const string Liberation = "Liberation", ForceEncounter = "ForceEncounter", Awakened = "Awakened";
    /// <summary>QA-7: the fleet holding a prisoner left the state (he is treated as a free character aboard it).</summary>
    public const string HoldingFleetLost = "HoldingFleetLost";
    /// <summary>QA-9: Autorouting (a freed, escaped or displaced character joins the fleet he set off for).</summary>
    public const string Autoroute = "Autoroute";
    // causes of a post ending or a detachment (§Posts end)
    public const string Order = "Order", Injured = "Injured", Captured = "Captured", Killed = "Killed", FleetDissolved = "FleetDissolved",
        FleetMoved = "FleetMoved", Merged = "Merged", HolderChanged = "HolderChanged", SystemDestroyed = "SystemDestroyed";
}

// ---- events. Every event names the character's Side (and a System when it has one) so the fog's audience rule (m5-fog-and-hq §N8) shows it
// to that side and to a side with live access to the system. rule: m5-characters-and-force §Where it runs in the day Step (Events).

/// <summary>A character came into play outside setup (setup raises no per-object events, m5-setup-and-victory P11).</summary>
public sealed record CharacterCreated(int Index, int Day, int Character, Side Side, string? System) : GameEvent(Index, Day, nameof(CharacterCreated));
public sealed record CharacterDeparted(int Index, int Day, int Character, Side Side, string From, int FirstHopTicks) : GameEvent(Index, Day, nameof(CharacterDeparted));
public sealed record CharacterArrived(int Index, int Day, int Character, Side Side, string System) : GameEvent(Index, Day, nameof(CharacterArrived));
public sealed record CharacterAttached(int Index, int Day, int Character, Side Side, int Fleet, CharacterPost Post, string Cause) : GameEvent(Index, Day, nameof(CharacterAttached));
public sealed record CharacterDetached(int Index, int Day, int Character, Side Side, int Fleet, string? System, string Cause) : GameEvent(Index, Day, nameof(CharacterDetached));
public sealed record PostVacated(int Index, int Day, int Character, Side Side, PostKind Post, string Cause) : GameEvent(Index, Day, nameof(PostVacated));
public sealed record SystemGeneralAssigned(int Index, int Day, int Character, Side Side, string System) : GameEvent(Index, Day, nameof(SystemGeneralAssigned));
/// <summary>Injury points added (Points) and the total now (Injury). rule: §Capture, injury, death, freeing (B2e §2).</summary>
public sealed record CharacterInjured(int Index, int Day, int Character, Side Side, string? System, string Cause, int Points = 0, int Injury = 0) : GameEvent(Index, Day, nameof(CharacterInjured));
public sealed record CharacterHealed(int Index, int Day, int Character, Side Side) : GameEvent(Index, Day, nameof(CharacterHealed));
/// <summary>A capture; HeldAboard = the fleet he is held aboard (0 = on the planet System, or with the captor's mission team when Mission is set). QA-7.</summary>
public sealed record CharacterCaptured(int Index, int Day, int Character, Side Side, Side Captor, string? System, string Cause, int HeldAboard = 0, int Mission = 0) : GameEvent(Index, Day, nameof(CharacterCaptured));
/// <summary>A prisoner held aboard <paramref name="Fleet"/> was put ashore at <paramref name="System"/>, a system his captor holds (LandPrisoners, New).</summary>
public sealed record PrisonerLanded(int Index, int Day, int Character, Side Side, Side Captor, string System, int Fleet) : GameEvent(Index, Day, nameof(PrisonerLanded));
public sealed record CharacterFreed(int Index, int Day, int Character, Side Side, string? System, string Cause) : GameEvent(Index, Day, nameof(CharacterFreed));
public sealed record CharacterEscaped(int Index, int Day, int Character, Side Side, string? System) : GameEvent(Index, Day, nameof(CharacterEscaped));
public sealed record CharacterKilled(int Index, int Day, int Character, Side Side, string? System, string Cause) : GameEvent(Index, Day, nameof(CharacterKilled));
/// <summary>One student's Jedi Training roll. rule: §Force (Jedi Training: the roll).</summary>
public sealed record ForceTrainingResult(int Index, int Day, int Trainer, int Student, Side Side, int Gap, int R, bool Success, int Gain, int NewForce)
    : GameEvent(Index, Day, nameof(ForceTrainingResult));
/// <summary>Rank null = not a Force user.</summary>
public sealed record ForceRankChanged(int Index, int Day, int Character, Side Side, ForceRank? From, ForceRank? To) : GameEvent(Index, Day, nameof(ForceRankChanged));
public sealed record ForceDiscovered(int Index, int Day, int Character, Side Side, string Cause) : GameEvent(Index, Day, nameof(ForceDiscovered));
/// <summary>
/// A character's loyalty moved (ShiftLoyalty; the setter 0x5341a0 tells both sides, so the event has no Side field and goes to both).
/// rule: m5-characters-and-force §Loyalty.
/// </summary>
public sealed record LoyaltyChanged(int Index, int Day, int Character, Side CharacterSide, int From, int To) : GameEvent(Index, Day, nameof(LoyaltyChanged));
/// <summary>A prisoner's escape attempt (ESCAPETB). rule: §Escape (Traced B2e §3).</summary>
public sealed record EscapeAttempted(int Index, int Day, int Character, Side Side, string System, int CaptorAverageCombat, int CaptorSpecialForces, int X, int Chance, int Roll, bool Escaped)
    : GameEvent(Index, Day, nameof(EscapeAttempted));
/// <summary>
/// A Force-user encounter was resolved (0x560200; both sides get the message, so no Side field). rule: m5-characters-and-force §Force (Traced B2e §4).
/// </summary>
public sealed record ForceEncounterResolved(int Index, int Day, int Mission, int Member, Side MemberSide, int Other, Side OtherSide, bool MemberEscaped,
    bool MemberHurt, bool OtherHurt) : GameEvent(Index, Day, nameof(ForceEncounterResolved));
/// <summary>A character's Force experience grew (the encounter). rule: §Force (B2e §4 step 7).</summary>
public sealed record ForceGained(int Index, int Day, int Character, Side Side, int Gain, int NewForce, string Cause) : GameEvent(Index, Day, nameof(ForceGained));
/// <summary>Luke (or Leia) learned his heritage. rule: §Force (B2e §4, §5).</summary>
public sealed record HeritageLearned(int Index, int Day, int Character, Side Side) : GameEvent(Index, Day, nameof(HeritageLearned));
