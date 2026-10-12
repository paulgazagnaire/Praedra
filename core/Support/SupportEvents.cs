using Rebellion.Core.Clock;

namespace Rebellion.Core.Support;

// Events of the support package. Day is the hook time T = K + 1, like the economy events.
// rule: m2-popular-support §One entry point (SupportChanged); m2-uprisings-and-garrison §Uprising start, keep, end; §Incident.

/// <summary>Old and New are the Alliance share (0..100). Emitted only when the value moved.</summary>
public sealed record SupportChanged(int Index, int Day, string System, int Old, int New, SupportCause Cause)
    : GameEvent(Index, Day, nameof(SupportChanged));

public sealed record UprisingStarted(int Index, int Day, string System, Side Holder)
    : GameEvent(Index, Day, nameof(UprisingStarted));

/// <summary>Reason: "Garrison" (G covers 2N), "NoRequirement" (N = 0), "NoHolder", "ControlChanged", "Captured".</summary>
public sealed record UprisingEnded(int Index, int Day, string System, string Reason)
    : GameEvent(Index, Day, nameof(UprisingEnded));

/// <summary>Score and the two table codes (UPRIS1TB, UPRIS2TB) of one incident.</summary>
public sealed record IncidentRolled(int Index, int Day, string System, int Score, int Code1, int Code2)
    : GameEvent(Index, Day, nameof(IncidentRolled));

public sealed record RegimentDestroyed(int Index, int Day, int Object, string Class, string System, Side Side, string Reason = "UprisingIncident")
    : GameEvent(Index, Day, nameof(RegimentDestroyed));
