using System.Text.Json.Serialization;

namespace Rebellion.Core.Clock;

public enum Side { Empire = 0, Alliance = 1 }

/// <summary>
/// An order is data: {sequence, side, type, parameters}. The core assigns Sequence on submit.
/// rule: m1-clock-and-orders §Orders in
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetMarkerOrder), "SetMarker")]
public abstract record Order(long Sequence, Side Side);

/// <summary>
/// Minimal placeholder order so sequencing and rejection can be tested before fleets exist (a later package adds
/// MoveFleet). Sets a named marker to a non-negative value. Rejected: NegativeValue, or AlreadyChanged when an
/// earlier order in the same Step already set that marker (stand-in for InTransit).
/// </summary>
public sealed record SetMarkerOrder(long Sequence, Side Side, string Marker, int Value) : Order(Sequence, Side);

public static class RejectReason
{
    public const string NegativeValue = "NegativeValue";
    public const string AlreadyChanged = "AlreadyChanged";
}

/// <summary>
/// Event record {index, day, kind, ...}. Index counts up for the whole game in emission order.
/// Events never change state and are not saved. rule: m1-clock-and-orders §Events out
/// </summary>
public abstract record GameEvent(int Index, int Day, string Kind);
public sealed record OrderAccepted(int Index, int Day, long Sequence) : GameEvent(Index, Day, nameof(OrderAccepted));
public sealed record OrderRejected(int Index, int Day, long Sequence, string Reason) : GameEvent(Index, Day, nameof(OrderRejected));
