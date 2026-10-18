using System.Text.Json.Serialization;
using Rebellion.Core.Construction;

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
[JsonDerivedType(typeof(DebugSetHolderOrder), "DebugSetHolder")]
[JsonDerivedType(typeof(BuildOrder), "Build")]
[JsonDerivedType(typeof(StopBuildOrder), "StopBuild")]
[JsonDerivedType(typeof(CreateFleetOrder), "CreateFleet")]
[JsonDerivedType(typeof(SplitFleetOrder), "SplitFleet")]
[JsonDerivedType(typeof(TransferShipsOrder), "TransferShips")]
[JsonDerivedType(typeof(MergeFleetsOrder), "MergeFleets")]
[JsonDerivedType(typeof(MoveSquadronsOrder), "MoveSquadrons")]
[JsonDerivedType(typeof(LoadSquadronsOrder), "LoadSquadrons")]
[JsonDerivedType(typeof(UnloadSquadronsOrder), "UnloadSquadrons")]
[JsonDerivedType(typeof(DebugSetDamageOrder), "DebugSetDamage")]
[JsonDerivedType(typeof(Rebellion.Core.Defenses.BombardOrder), "Bombard")]
[JsonDerivedType(typeof(Rebellion.Core.Assault.AssaultOrder), "Assault")]
[JsonDerivedType(typeof(Rebellion.Core.Battle.SetOnContactOrder), "SetOnContact")]
[JsonDerivedType(typeof(Rebellion.Core.Fog.MoveHqOrder), "MoveHq")]
[JsonDerivedType(typeof(Rebellion.Core.Characters.AttachCharacterOrder), "AttachCharacter")]
[JsonDerivedType(typeof(Rebellion.Core.Characters.DetachCharacterOrder), "DetachCharacter")]
[JsonDerivedType(typeof(Rebellion.Core.Characters.AssignSystemGeneralOrder), "AssignSystemGeneral")]
[JsonDerivedType(typeof(Rebellion.Core.Characters.ReleaseSystemGeneralOrder), "ReleaseSystemGeneral")]
[JsonDerivedType(typeof(Rebellion.Core.Characters.MoveCharacterOrder), "MoveCharacter")]
[JsonDerivedType(typeof(Rebellion.Core.Missions.StartMissionOrder), "StartMission")]
[JsonDerivedType(typeof(Rebellion.Core.Missions.MoveGroupOrder), "MoveGroup")]
[JsonDerivedType(typeof(Rebellion.Core.Missions.CancelMissionOrder), "CancelMission")]
[JsonDerivedType(typeof(Rebellion.Core.DeathStar.DestroySystemOrder), "DestroySystem")]
public abstract record Order(long Sequence, Side Side);

/// <summary>
/// Minimal placeholder order so sequencing and rejection can be tested before fleets exist (a later package adds
/// MoveFleet). Sets a named marker to a non-negative value. Rejected: NegativeValue, or AlreadyChanged when an
/// earlier order in the same Step already set that marker (stand-in for InTransit).
/// </summary>
public sealed record SetMarkerOrder(long Sequence, Side Side, string Marker, int Value) : Order(Sequence, Side);

/// <summary>
/// Send a fleet to a system by the fastest path, through the waypoints in order when there are any (null or empty = none).
/// Checked in step 1 (when processed, not when submitted). rule: m1-clock-and-orders §Orders in (MoveFleet); m1-travel §Path rule (3) (waypoints).
/// The handler lives in Rebellion.Core.Fleets.FleetModule.
/// </summary>
public sealed record MoveFleetOrder(long Sequence, Side Side, int FleetId, string Destination, ValueList<string>? Waypoints = null) : Order(Sequence, Side);

/// <summary>
/// Scrap one completed object of the side at once (step 1). rule: m2-construction §Scrapping, m2-maintenance §Scrapping and
/// refunds. The handler lives in Rebellion.Core.Economy.EconomyModule (the construction package may take it over).
/// </summary>
public sealed record ScrapObjectOrder(long Sequence, Side Side, int ObjectId) : Order(Sequence, Side);

/// <summary>
/// Debug order: make a side the holder of a system now (a capture with cause Debug). For tests and tools only; the control module
/// rejects it unless debug orders are enabled. rule: m2-control-and-capture §How control changes (DebugSetHolder).
/// The handler lives in Rebellion.Core.Control.ControlModule.
/// </summary>
public sealed record DebugSetHolderOrder(long Sequence, Side Side, string System, Side NewHolder) : Order(Sequence, Side);
/// Build facilities, ships, fighters, regiments or special forces at a yard of the given family. Destination null = the yard's
/// own system (a facility needs one). Checked in step 1. rule: m2-construction §Build order. The handler lives in
/// Rebellion.Core.Construction.ConstructionModule. Item is a class name of unit-and-facility-costs.csv (DEFAULT: the sheet says (kind, id)).
/// </summary>
public sealed record BuildOrder(long Sequence, Side Side, string System, YardFamily Family, string Class, int Count, string? Destination)
    : Order(Sequence, Side);

/// <summary>Cancel the project of one (system, family) without a new order. rule: m2-construction §Projects and queues.</summary>
public sealed record StopBuildOrder(long Sequence, Side Side, string System, YardFamily Family) : Order(Sequence, Side);

// ---- fleet orders. rule: m3-fleets §Orders (all step 1, immediate, in order sequence; rejection changes nothing).
// Id lists are plain lists so a queued order saves and loads.

/// <summary>Take the listed ships out of their fleets and form one new fleet at their system. rule: m3-fleets §Orders (CreateFleet).</summary>
public sealed record CreateFleetOrder(long Sequence, Side Side, List<int> Ships) : Order(Sequence, Side);

/// <summary>Take the listed ships out of one fleet into a new fleet. rule: m3-fleets §Orders (SplitFleet).</summary>
public sealed record SplitFleetOrder(long Sequence, Side Side, int Fleet, List<int> Ships) : Order(Sequence, Side);

/// <summary>Move the listed ships into an existing fleet at the same system; aboard units go with their ship. rule: m3-fleets §Orders (TransferShips).</summary>
public sealed record TransferShipsOrder(long Sequence, Side Side, List<int> Ships, int ToFleet) : Order(Sequence, Side);

/// <summary>Transfer every ship of one fleet into another; the first fleet is removed. rule: m3-fleets §Orders (MergeFleets).</summary>
public sealed record MergeFleetsOrder(long Sequence, Side Side, int From, int Into) : Order(Sequence, Side);

/// <summary>Fly based squadrons (hyperdrive above 0) as a squadron-only fleet. rule: m3-fleets §Unescorted squadrons (DEFAULT).</summary>
public sealed record MoveSquadronsOrder(long Sequence, Side Side, List<int> Squadrons, string Destination) : Order(Sequence, Side);

/// <summary>Put based squadrons of the fleet's system aboard its ships; all or nothing. rule: m3-fleets §Orders (LoadSquadrons).</summary>
public sealed record LoadSquadronsOrder(long Sequence, Side Side, int Fleet, List<int> Squadrons) : Order(Sequence, Side);

/// <summary>Squadrons leave their ship and become based at the fleet's system (held by the side). rule: m3-fleets §Orders (UnloadSquadrons).</summary>
public sealed record UnloadSquadronsOrder(long Sequence, Side Side, int Fleet, List<int> Squadrons) : Order(Sequence, Side);

// ---- troop orders. rule: m3-troops-and-special-forces §Orders (step 1, immediate, in order sequence; rejection changes nothing).

/// <summary>Put landed regiments of the fleet's system aboard its ships (first free slot by ship id); all or nothing. rule: m3-troops-and-special-forces §Orders (LoadRegiments).</summary>
public sealed record LoadRegimentsOrder(long Sequence, Side Side, int Fleet, List<int> Regiments) : Order(Sequence, Side);

/// <summary>Regiments leave their ship and stand on the fleet's system (held by the side). rule: m3-troops-and-special-forces §Orders (UnloadRegiments).</summary>
public sealed record UnloadRegimentsOrder(long Sequence, Side Side, int Fleet, List<int> Regiments) : Order(Sequence, Side);

/// <summary>
/// Debug order: set a ship's hull and six damage counters. For tests and tools; rejected unless debug orders are enabled
/// (DEFAULT, the same switch as DebugSetHolder). rule: m3-ships-and-fighters §Damaged state (DebugSetDamage).
/// </summary>
public sealed record DebugSetDamageOrder(long Sequence, Side Side, int Ship, int Hull, List<int> Hits) : Order(Sequence, Side);

public static class RejectReason
{
    public const string NegativeValue = "NegativeValue";
    public const string AlreadyChanged = "AlreadyChanged";
    /// <summary>The game has a winner. rule: m5-setup-and-victory §Game over.</summary>
    public const string GameOver = "GameOver";

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
    // rule: m2-control-and-capture §How control changes (DebugSetHolder; the sheet names no codes: DEFAULT names)
    public const string DebugDisabled = "DebugDisabled";
    public const string AlreadyHolder = "AlreadyHolder";
    // rule: m2-construction §Build order (BuildOrder rejections; UnknownItem and NoProject are DEFAULT names the sheet lacks)
    public const string NoYard = "NoYard";
    public const string WrongFamily = "WrongFamily";
    public const string NotAvailable = "NotAvailable";
    public const string NotResearched = "NotResearched";
    public const string BadCount = "BadCount";
    /// <summary>The order's upkeep (maintenance x count) does not fit in the side's free capacity: refused whole (1998 error 0xf3). rule: m2-maintenance §Overdraft (B5b §7).</summary>
    public const string NoUpkeep = "NoUpkeep";
    public const string BadDestination = "BadDestination";
    public const string NoSlot = "NoSlot";
    /// <summary>A second Sensor Station on one planet. The sheet names no code: DEFAULT name. rule: m5-fog-and-hq §N4 (one per planet).</summary>
    public const string OnePerPlanet = "OnePerPlanet";
    /// <summary>A Sensor Station ordered for a system on no hyperspace route. rule: m5-fog-and-hq §N4 (movement-redesign-draft §C: "NotOnRoute").</summary>
    public const string NotOnRoute = "NotOnRoute";
    public const string UnknownItem = "UnknownItem";
    public const string NoProject = "NoProject";
    // rule: m3-fleets §Orders (fleet order rejections), m3-ships-and-fighters §Tests S13. DuplicateSquadron and EmptySelection (for
    // TransferShips) are DEFAULT names the sheets lack; DebugDisabled is shared with DebugSetHolder.
    public const string UnknownShip = "UnknownShip";
    public const string UnknownSquadron = "UnknownSquadron";
    public const string DuplicateShip = "DuplicateShip";
    public const string DuplicateSquadron = "DuplicateSquadron";
    public const string NotSameSystem = "NotSameSystem";
    public const string TooFewShips = "TooFewShips";
    public const string NoChange = "NoChange";
    public const string ShipNotInFleet = "ShipNotInFleet";
    public const string EmptySelection = "EmptySelection";
    public const string WholeFleet = "WholeFleet";
    public const string SameFleet = "SameFleet";
    public const string SquadronFleet = "SquadronFleet";
    public const string SquadronAboard = "SquadronAboard";
    public const string SquadronNotBased = "SquadronNotBased";
    public const string NotAtFleetSystem = "NotAtFleetSystem";
    public const string NoCapacity = "NoCapacity";
    public const string NotAboard = "NotAboard";
    public const string NotYourSystem = "NotYourSystem";
    public const string BadValue = "BadValue";
    // rule: m3-troops-and-special-forces §Orders. DuplicateRegiment, UnknownRegiment, AlreadyAboard are the sheet's names; EmptySelection is shared.
    public const string UnknownRegiment = "UnknownRegiment";
    public const string DuplicateRegiment = "DuplicateRegiment";
    public const string AlreadyAboard = "AlreadyAboard";
    // rule: m5-research §Mission income (CanResearch reasons; the original has two codes 0x2c and 0x32, which is which is Missing: names are ours)
    public const string NoMatchingYard = "NoMatchingYard";
    public const string NothingToResearch = "NothingToResearch";
    /// <summary>The target system was destroyed by the Death Star: it cannot be held, built on, bombarded, assaulted or targeted. rule: m5-death-star §2 effect 5.</summary>
    public const string SystemDestroyed = "SystemDestroyed";
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
