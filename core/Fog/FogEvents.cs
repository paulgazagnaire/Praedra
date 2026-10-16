using Rebellion.Core.Clock;

namespace Rebellion.Core.Fog;

// Events of the fog of war and the Rebel HQ. Day is the hook time T = K + 1 (the day the Step produces), except HqDeparted (step 2, Day K,
// like FleetDeparted). rule: m5-fog-and-hq §F8 (slots), §N8 (audience).

/// <summary>How a picture was written. rule: m5-fog-and-hq §F2 (the reveal kinds).</summary>
public enum RevealKind { Setup, FullCopy, Presence, Recon, Engagement, LossNotice, CoreReport, Destroyed, OwnLive }

/// <summary>Order: move the Rebel HQ to a system. Alliance only. Checked in step 1. rule: m5-fog-and-hq §F6.</summary>
public sealed record MoveHqOrder(long Sequence, Side Side, string Destination) : Order(Sequence, Side);

/// <summary>The rejection reasons of MoveHq that the shared list lacks. rule: m5-fog-and-hq §F6 (table, checked in this order).</summary>
public static class HqRejectReason
{
    public const string NotAlliance = "NotAlliance";
    public const string NoHq = "NoHq";
    public const string NotHeld = "NotHeld";
    public const string Pinned = "Pinned";
    public const string NoFunds = "NoFunds";
}

/// <summary>A side's picture of a system became explored. Audience: that side. rule: §F8 slots 3.1 and 5.4.</summary>
public sealed record SystemExplored(int Index, int Day, Side Viewer, string System, RevealKind How) : GameEvent(Index, Day, nameof(SystemExplored));

/// <summary>A write of group B showed a different holder than the side's picture held. Audience: that side. rule: §F8 slot 5.4.</summary>
public sealed record OwnerChangeSeen(int Index, int Day, Side Viewer, string System, Side? Was, Side? Now) : GameEvent(Index, Day, nameof(OwnerChangeSeen));

/// <summary>A side saw one fleet of the other side at a system (class counts, no fleet id). Audience: the viewer. rule: §F8 slot 3.1, §N3.</summary>
public sealed record FleetSighted(int Index, int Day, Side Viewer, string System, Side FleetSide, string Detail) : GameEvent(Index, Day, nameof(FleetSighted));

/// <summary>
/// A Sensor Station of the Viewer saw a fleet of FleetSide arrive (or pass, or be stopped) one lane from it: system, side and size class
/// only, merged per (side, system, day). Audience: the Viewer (the station's owner). rule: §N4 S2, §F8 slot 3.1.
/// </summary>
public sealed record SensorReport(int Index, int Day, Side Viewer, string System, Side FleetSide, SensorSize Size) : GameEvent(Index, Day, nameof(SensorReport));

/// <summary>A side received an "HQ is in this sector" clue. Audience: that side. rule: §N6 (SectorSuspicion).</summary>
public sealed record HqSectorClueGiven(int Index, int Day, Side Viewer, string Sector) : GameEvent(Index, Day, nameof(HqSectorClueGiven));

/// <summary>An agent of the other side was detected (from m5-missions' MissionDetected). Audience: the defender. rule: §N7 3.</summary>
public sealed record AgentDetected(int Index, int Day, Side Viewer, string System, Side AgentSide) : GameEvent(Index, Day, nameof(AgentDetected));

/// <summary>Step 2: the HQ left. Audience: Alliance only. rule: §F6 Acceptance 3, §N8.</summary>
public sealed record HqDeparted(int Index, int Day, string From, ValueList<string> Path, int TotalTicks, int ArrivalDay) : GameEvent(Index, Day, nameof(HqDeparted));

/// <summary>Step 3: the HQ arrived. Audience: Alliance only. rule: §F6 Arrival, §N8.</summary>
public sealed record HqArrived(int Index, int Day, string System, string Sector) : GameEvent(Index, Day, nameof(HqArrived));

/// <summary>The HQ was destroyed. Audience: both sides. Causes: Captured (or the capture cause name), DestinationLost, SystemDestroyed. rule: §F7.</summary>
public sealed record HqDestroyed(int Index, int Day, string Cause, string System) : GameEvent(Index, Day, nameof(HqDestroyed));
