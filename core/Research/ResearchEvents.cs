using Rebellion.Core.Clock;

namespace Rebellion.Core.Research;

/// <summary>
/// A side reached a research level ("Research complete", original messages 0x125 ship, 0x126 troop, 0x127 facility; the name is ours).
/// Classes = the side's classes at that level, ordinal order (now buildable). One event per rung crossed, ascending.
/// rule: m5-research §The level check (Event out).
/// </summary>
public sealed record ResearchLevelReached(int Index, int Day, Side Side, ResearchKind ResearchKind, int Level, ValueList<string> Classes)
    : GameEvent(Index, Day, nameof(ResearchLevelReached));
