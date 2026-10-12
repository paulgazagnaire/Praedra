using System.Text.Json.Serialization;

namespace Rebellion.Core.Clock;

public enum Side { Empire = 0, Alliance = 1 }

/// <summary>
/// An order is data: {sequence, side, type, parameters}. The core assigns Sequence on submit.
/// rule: m1-clock-and-orders §Orders in
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetMarkerOrder), "SetMarker")]
[JsonDerivedType(typeof(MoveFleetOrder), "MoveFleet")]
[JsonDerivedType(typeof(ScrapObjectOrder), "ScrapObject")]
public abstract record Order(long Sequence, Side Side);

/// <summary>
/// Minimal placeholder order so sequencing and rejection can be tested before fleets exist (a later package adds
/// MoveFleet). Sets a named marker to a non-negative value. Rejected: NegativeValue, or AlreadyChanged when an
/// earlier order in the same Step already set that marker (stand-in for InTransit).
/// </summary>
public sealed record SetMarkerOrder(long Sequence, Side Side, string Marker, int Value) : Order(Sequence, Side);

/// <summary>
/// Send a fleet to a system by the fastest lane path. Checked in step 1 (when processed, not when submitted).
/// rule: m1-clock-and-orders §Orders in (MoveFleet). The handler lives in Rebellion.Core.Fleets.FleetModule.
/// </summary>
public sealed record MoveFleetOrder(long Sequence, Side Side, int FleetId, string Destination) : Order(Sequence, Side);

/// <summary>
/// Scrap one completed object of the side at once (step 1). rule: m2-construction §Scrapping, m2-maintenance §Scrapping and
/// refunds. The handler lives in Rebellion.Core.Economy.EconomyModule (the construction package may take it over).
/// </summary>
public sealed record ScrapObjectOrder(long Sequence, Side Side, int ObjectId) : Order(Sequence, Side);

public static class RejectReason
{
    public const string NegativeValue = "NegativeValue";
    public const string AlreadyChanged = "AlreadyChanged";

    // rule: m1-clock-and-orders §Orders in (MoveFleet rejections)
    public const string UnknownFleet = "UnknownFleet";
    public const string NotYours = "NotYours";
    public const string InTransit = "InTransit";
    public const string Immobile = "Immobile";
    public const string AlreadyThere = "AlreadyThere";
    public const string UnknownSystem = "UnknownSystem";
    public const string Unreachable = "Unreachable";
    // rule: m2-construction §Scrapping (ScrapObject rejections; the sheet names no codes: DEFAULT names)
    public const string UnknownObject = "UnknownObject";
    public const string NotCompleted = "NotCompleted";
    /// <summary>No handler is registered for this order type (e.g. a save with fleet orders loaded before the fleet module was attached).</summary>
    public const string UnknownOrderType = "UnknownOrderType";
}

/// <summary>
/// Event record {index, day, kind, ...}. Index counts up for the whole game in emission order.
/// Events never change state and are not saved. rule: m1-clock-and-orders §Events out
/// </summary>
public abstract record GameEvent(int Index, int Day, string Kind);

/// <summary>
/// Fleet, Path, TotalTicks and ArrivalDay are filled for MoveFleet only.
/// rule: m1-clock-and-orders §Events out (OrderAccepted: sequence, fleet, path, total ticks, arrival day).
/// </summary>
public sealed record OrderAccepted(int Index, int Day, long Sequence, int? Fleet = null, ValueList<string>? Path = null,
    int TotalTicks = 0, int ArrivalDay = 0) : GameEvent(Index, Day, nameof(OrderAccepted));

public sealed record OrderRejected(int Index, int Day, long Sequence, string Reason) : GameEvent(Index, Day, nameof(OrderRejected));
