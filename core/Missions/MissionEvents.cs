using Rebellion.Core.Clock;

namespace Rebellion.Core.Missions;

// ---- orders. rule: m5-missions §Orders (all step 1, in order sequence; a rejection changes nothing; the same entry point for player and AI).
// Id lists are plain lists so a queued order saves and loads.

/// <summary>
/// Start a mission: the team (characters by record id, special forces by object id), the decoys, and the target (a system, or a character for
/// Rescue, Abduction and Assassination; a facility id only with SabotageTargetMode Chosen). rule: m5-missions §Orders (StartMission).
/// </summary>
public sealed record StartMissionOrder(long Sequence, Side Side, MissionKind Kind, List<int> Characters, List<int> SpecialForces,
    List<int> Decoys, string? TargetSystem, int? TargetCharacter = null, int? Facility = null, List<int>? DecoyCharacters = null) : Order(Sequence, Side);

/// <summary>Move a group of characters and special forces like a mission group (record 0x01): no table, no detection. rule: m5-missions §Orders (MoveGroup).</summary>
public sealed record MoveGroupOrder(long Sequence, Side Side, List<int> Characters, List<int> SpecialForces, string Destination) : Order(Sequence, Side);

/// <summary>Cancel a working mission: the iteration is dropped and the team returns. rule: m5-missions §Orders (CancelMission).</summary>
public sealed record CancelMissionOrder(long Sequence, Side Side, int Mission) : Order(Sequence, Side);

/// <summary>The rejection reasons of the mission orders (the sheet's names; RuleMissing marks a mission the rule book cannot run yet). rule: m5-missions §Orders.</summary>
public static class MissionReject
{
    public const string UnknownKind = "UnknownKind", HiddenKind = "HiddenKind", WrongSide = "WrongSide", UnknownMember = "UnknownMember",
        DuplicateMember = "DuplicateMember", NoTeam = "NoTeam", NotFree = "NotFree", NotTogether = "NotTogether", NotAbleToDo = "NotAbleToDo",
        NotEligibleSpecialForce = "NotEligibleSpecialForce", BadDecoy = "BadDecoy", TooManyMembers = "TooManyMembers", BadTarget = "BadTarget",
        NoDeathStar = "NoDeathStar", TargetProtected = "TargetProtected", UnknownMission = "UnknownMission", RuleMissing = "RuleMissing";
}

/// <summary>Why the character sheet's exposure hook is called. rule: m5-missions §4.</summary>
public enum ExposureCause { Foiled, Failed }

// ---- events. rule: m5-missions §1 to §6 (MissionLanded, MissionFoiled, MissionFailed, MissionIterationDone, MissionEnded, MissionReturned,
// SystemRevealed, SabotageNoTarget). Audience (m5-fog-and-hq §N8): the fog shows an event to its Side and to a side with live access to its
// System. The mission side's own events name the target as Target (not System) so only that side sees them: the defender learns of an agent only
// through a detection (fog §N7 3, AgentDetected) or the visible effects (support, facilities, characters). MissionFoiled carries System: the
// defender caught the team and sees it if it has live access there. Day is the hook time K + 1, except MissionStarted and MissionDeparted (Day K).

/// <summary>Step 1: a mission (or a MoveGroup) was accepted. Home is where the team stands (the order's home), named Home so only the mission side sees the event (fog §N7).</summary>
public sealed record MissionStarted(int Index, int Day, int Mission, Side Side, MissionKind MissionKind, string Home, string Target,
    int? TargetCharacter, ValueList<int> Characters, ValueList<int> SpecialForces, ValueList<int> Decoys, int TotalTicks, int ArrivalDay)
    : GameEvent(Index, Day, nameof(MissionStarted));

/// <summary>Step 2: the group left on its way out (Returning false) or home (Returning true).</summary>
public sealed record MissionDeparted(int Index, int Day, int Mission, Side Side, string From, string Destination, bool Returning, int TotalTicks, int ArrivalDay)
    : GameEvent(Index, Day, nameof(MissionDeparted));

/// <summary>Step 3: the group landed at its target; the first iteration is due on <see cref="Due"/>. rule: §1.</summary>
public sealed record MissionLanded(int Index, int Day, int Mission, Side Side, string Target, int Due) : GameEvent(Index, Day, nameof(MissionLanded));

/// <summary>One decoy's try against one defender (a = the decoy draw). rule: §3 decoy phase.</summary>
public sealed record MissionDecoyRoll(int Index, int Day, int Mission, Side Side, int Defender, int Decoy, int X, int Chance, int Roll, bool Decoyed)
    : GameEvent(Index, Day, nameof(MissionDecoyRoll));

/// <summary>One defender's detection roll on the walk. rule: §3 detection walk.</summary>
public sealed record MissionDetectionRoll(int Index, int Day, int Mission, Side Side, int Defender, int X, int Chance, int Roll, bool Detected)
    : GameEvent(Index, Day, nameof(MissionDetectionRoll));

/// <summary>What foiled a mission. rule: m5-missions §3 (B2b §1: sensing, betrayal, then the detection walk).</summary>
public enum MissionFoilCause { Detected, ForceSensed, Betrayed }

/// <summary>
/// The mission was foiled: no success roll, no effect; the mission ends Foiled. Detected: Defender and DefenderClass name the unit (Character 0).
/// ForceSensed: Character is the first senser whose hit foiled it; Betrayed: Character is the traitor (Defender 0, DefenderClass ""). rule: §4.
/// </summary>
public sealed record MissionFoiled(int Index, int Day, int Mission, Side Side, string System, int Defender, string DefenderClass,
    MissionFoilCause Cause = MissionFoilCause.Detected, int Character = 0, int EndCode = 4)
    : GameEvent(Index, Day, nameof(MissionFoiled));

/// <summary>
/// The escape duel of one spotted member (a failed decoy, or a team member of a foiled check) against one guard: x = member combat - combat of the
/// guard's commander, p = RLEVADTB(x), escaped if Roll &lt; p; else a character is captured and a special force destroyed. System is the place of
/// the check. rule: m5-missions §4 (Traced B2d §5: 0x5888f0).
/// </summary>
public sealed record MissionDuel(int Index, int Day, int Mission, Side Side, string System, int Member, bool IsCharacter, bool Decoy, int Guard,
    int X, int Chance, int Roll, bool Escaped)
    : GameEvent(Index, Day, nameof(MissionDuel));

/// <summary>
/// The cap roll of a key character who lost the escape duel under <see cref="KeyDuelCapture.Capped"/>: captured if Roll &lt; Cap (his cap_percent
/// in the key-target file), else not captured (he keeps the duel's Hurt). rule: m5-missions §4, §6a (KeyDuelCapture, 2026-10-18).
/// </summary>
public sealed record MissionKeyDuelCaptureRoll(int Index, int Day, int Mission, Side Side, string System, int Member, Side Captor, int Cap, int Roll,
    bool Captured)
    : GameEvent(Index, Day, nameof(MissionKeyDuelCaptureRoll));

/// <summary>One Force sensing draw: group Force user Member against enemy Senser, chance p = Force + Force - 100 (only drawn when p > 0). rule: §3 Force sensing.</summary>
public sealed record MissionForceSenseRoll(int Index, int Day, int Mission, Side Side, string Target, int Member, int Senser, int Chance, int Roll, bool Sensed)
    : GameEvent(Index, Day, nameof(MissionForceSenseRoll));

/// <summary>A senser sensed a group member. A team member foils the mission (Foiled true); a sensed decoy foils nothing. rule: §3 Force sensing step 4.</summary>
public sealed record MissionForceSensed(int Index, int Day, int Mission, Side Side, string Target, int Member, bool Decoy, int Senser, bool Foiled)
    : GameEvent(Index, Day, nameof(MissionForceSensed));

/// <summary>
/// Two Force users learned of each other (0x55ff60): each records the other and both sides are told, so the event has no Side field.
/// rule: §3 Force sensing step 4.
/// </summary>
public sealed record ForceUserDiscovered(int Index, int Day, int Mission, int Member, Side MemberSide, int Senser, Side SenserSide)
    : GameEvent(Index, Day, nameof(ForceUserDiscovered));

/// <summary>One member's betrayal draw: chance 100 - loyalty, drawn even when 0. rule: §3 Betrayal.</summary>
public sealed record MissionBetrayalRoll(int Index, int Day, int Mission, Side Side, string Target, int Member, bool IsCharacter, int Chance, int Roll, bool Betrays)
    : GameEvent(Index, Day, nameof(MissionBetrayalRoll));

/// <summary>A member betrayed the mission (it is foiled). rule: §3 Betrayal.</summary>
public sealed record MissionBetrayed(int Index, int Day, int Mission, Side Side, string Target, int Member, bool IsCharacter)
    : GameEvent(Index, Day, nameof(MissionBetrayed));

/// <summary>A member identified the traitor (information only; both sides are told, so no Side field). rule: §3 Betrayal (0x4ee5c0).</summary>
public sealed record TraitorIdentified(int Index, int Day, int Mission, int Identifier, int Traitor, bool TraitorIsCharacter, Side MissionSide)
    : GameEvent(Index, Day, nameof(TraitorIdentified));

/// <summary>One member's success roll (Member = character id or special force object id). Chance -1 = automatic (Reconnaissance). rule: §2 step 2.</summary>
public sealed record MissionMemberRoll(int Index, int Day, int Mission, Side Side, int Member, bool IsCharacter, int Input, int Chance, int Roll, bool Success)
    : GameEvent(Index, Day, nameof(MissionMemberRoll));

/// <summary>An iteration succeeded (Effects = how many times its effect applies). rule: §2 step 3.</summary>
public sealed record MissionSucceeded(int Index, int Day, int Mission, Side Side, MissionKind MissionKind, string Target, int Effects)
    : GameEvent(Index, Day, nameof(MissionSucceeded));

/// <summary>A non-repeating mission was not detected and no member succeeded. rule: §4 Failed.</summary>
public sealed record MissionFailed(int Index, int Day, int Mission, Side Side, MissionKind MissionKind, string Target) : GameEvent(Index, Day, nameof(MissionFailed));

/// <summary>A repeating mission finished an iteration and stays Working; the next one is due on <see cref="Due"/>. rule: §2 step 5.</summary>
public sealed record MissionIterationDone(int Index, int Day, int Mission, Side Side, string Target, bool Success, int Due) : GameEvent(Index, Day, nameof(MissionIterationDone));

/// <summary>The mission ended (its group now returns). rule: §5.</summary>
public sealed record MissionEnded(int Index, int Day, int Mission, Side Side, MissionKind MissionKind, string Target, MissionOutcome Outcome) : GameEvent(Index, Day, nameof(MissionEnded));

/// <summary>The returning group arrived (or stayed: no system held) and its members are free at <see cref="At"/>. rule: §5.</summary>
public sealed record MissionReturned(int Index, int Day, int Mission, Side Side, string At) : GameEvent(Index, Day, nameof(MissionReturned));

/// <summary>A MoveGroup arrived; its members are free at <see cref="At"/>. rule: §Orders (MoveGroup).</summary>
public sealed record MissionGroupArrived(int Index, int Day, int Mission, Side Side, string At) : GameEvent(Index, Day, nameof(MissionGroupArrived));

/// <summary>An Espionage or Reconnaissance success taught the side about the target (the fog wrote the picture). rule: §6.</summary>
public sealed record SystemRevealed(int Index, int Day, int Mission, Side Side, string Target, MissionKind Source) : GameEvent(Index, Day, nameof(SystemRevealed));

/// <summary>A Sabotage success found no completed facility to destroy (no draw). rule: §6 Sabotage.</summary>
public sealed record SabotageNoTarget(int Index, int Day, int Mission, Side Side, string Target) : GameEvent(Index, Day, nameof(SabotageNoTarget));

/// <summary>A Recruitment success brought a pool character into play (the character module also raises CharacterCreated). rule: §6 Recruitment.</summary>
public sealed record CharacterRecruited(int Index, int Day, int Mission, Side Side, int Character, string Target) : GameEvent(Index, Day, nameof(CharacterRecruited));

/// <summary>A research member's skill rose (GNPRTB 6158). rule: §6 Research.</summary>
public sealed record MissionSkillGained(int Index, int Day, int Mission, Side Side, int Character, Characters.Skill Skill, int NewValue)
    : GameEvent(Index, Day, nameof(MissionSkillGained));
