using Praedra.Core.Clock;

namespace Praedra.Core.Fog;

/// <summary>
/// The fog-of-war state: each side's picture of the galaxy, its sighting log and its clues, plus the systems dealt "explored for all" at
/// setup (the espionage extras pool reads them). Saved and hashed; every collection sorted (ordinal names) so the form is canonical.
/// rule: m5-fog-and-hq §F1 (the data model), §N3 (the sighting log), §N9 (determinism).
/// </summary>
public sealed class FogState
{
    public SideKnowledge Hand { get; init; } = new();
    public SideKnowledge Collective { get; init; } = new();
    /// <summary>Systems with <c>start_explored_all</c> = 1 (setup data). rule: m5-fog-and-hq §F2 Espionage (pool "explored class").</summary>
    public SortedSet<string> StartExploredAll { get; init; } = new(StringComparer.Ordinal);

    public SideKnowledge Of(Side s) => s == Side.Hand ? Hand : Collective;

    internal void CopyFrom(FogState o)
    {
        Hand.CopyFrom(o.Hand);
        Collective.CopyFrom(o.Collective);
        foreach (var s in o.StartExploredAll) StartExploredAll.Add(s);
    }
}

/// <summary>One side's knowledge: a picture per system it has ever written, the sighting log and the HQ sector clues.</summary>
public sealed class SideKnowledge
{
    /// <summary>System pictures by name. A system not listed has never been written: <c>Explored</c> false, nothing known.</summary>
    public SortedDictionary<string, SystemKnowledge> Systems { get; init; } = new(StringComparer.Ordinal);
    /// <summary>The sighting log, in the order written (day, then slot order, then system name, then fleet id). rule: m5-fog-and-hq §N3.</summary>
    public List<Sighting> Sightings { get; set; } = new();
    /// <summary>HQ sector clues (only with <see cref="FogParams.SectorSuspicion"/> on), in the order given. rule: m5-fog-and-hq §N6.</summary>
    public List<HqSectorClue> Clues { get; set; } = new();
    /// <summary>
    /// The Sensor Station warnings already given whose reach day is still ahead: one per (fleet, station system, reach day), so a trip is
    /// warned once. Sorted by reach day, system, fleet id; dropped at slot 3.1 once the reach day has come. Saved and hashed; never shown
    /// (no read API exposes it: the fleet id stays hidden). Null when empty, and then left out of the saved form, so games without a
    /// warning in flight save and hash exactly as before. rule: m5-fog-and-hq §N4, §N9.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SensorWarning>? SensorWarnings { get; set; }

    internal SystemKnowledge Touch(string system)
    {
        if (!Systems.TryGetValue(system, out var k)) Systems[system] = k = new SystemKnowledge();
        return k;
    }

    internal void CopyFrom(SideKnowledge o)
    {
        foreach (var kv in o.Systems) Systems[kv.Key] = kv.Value.Clone();
        Sightings = o.Sightings.Select(s => s.Clone()).ToList();
        Clues = o.Clues.ToList();
        SensorWarnings = o.SensorWarnings is { Count: > 0 } sw
            ? sw.Select(w => new SensorWarning { FleetId = w.FleetId, System = w.System, ReachDay = w.ReachDay }).ToList() : null;
    }
}

/// <summary>A Sensor Station warning already given: the fleet, the station system and the day the fleet reaches it. rule: m5-fog-and-hq §N4.</summary>
public sealed class SensorWarning
{
    public int FleetId { get; set; }
    public string System { get; set; } = "";
    public int ReachDay { get; set; }
}

/// <summary>
/// A side's picture of one system (<c>SystemKnowledge</c>): five groups, each with the day it was last written (null = never). A picture
/// with <see cref="Explored"/> false shows nothing. Ship detail is class counts only, never the other side's fleet or ship ids (DEFAULT).
/// rule: m5-fog-and-hq §F1.
/// </summary>
public sealed class SystemKnowledge
{
    /// <summary>The "explored" bit of the side's view. Traced (ai/02 §2: +0x88 bit 1).</summary>
    public bool Explored { get; set; }
    /// <summary>Last day the side had live access (F3 kinds 1 to 3) or received a group C or E reveal here. Null = never. rule: §F1, §N1.</summary>
    public int? LastLookDay { get; set; }

    // ---- group A: status (populated, slot values). DEFAULT: energy and raw slot totals are part of the status (see the build notes).
    public int? StampA { get; set; }
    public bool Populated { get; set; }
    /// <summary>The side has been told the system was destroyed by the harvester (reveal kind Destroyed, "Planet Destroyed"); permanent. rule: m5-harvester §2 effect 6.</summary>
    public bool Destroyed { get; set; }
    public int Energy { get; set; }
    public int Raw { get; set; }

    // ---- group B: owner and support
    public int? StampB { get; set; }
    /// <summary>Holder when last seen; null = neutral.</summary>
    public Side? Owner { get; set; }
    /// <summary>Collective support 0..100 when last seen (Hand = 100 minus it).</summary>
    public int CollectiveSupport { get; set; }

    // ---- group C: facilities and defences, and HqHere
    public int? StampC { get; set; }
    /// <summary>Completed facilities by class name, with counts.</summary>
    public SortedDictionary<string, int> Facilities { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Facilities not yet completed (queued production at the system).</summary>
    public int FacilitiesQueued { get; set; }
    /// <summary>
    /// The other side had a harvester under construction here (queued, not finished) at the last group C write; same stamp as group C. Left out of
    /// the saved form while false, so older pictures save and hash as before. rule: m5-fog-and-hq §N6b.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HarvesterQueued { get; set; }
    /// <summary>The picture holds the HQ child. Written only by a reveal that writes group C (or by Presence with PresenceRevealsHq). rule: §N5.</summary>
    public bool HqHere { get; set; }
    /// <summary>The day HqHere was last written (by a group C reveal or a Presence with PresenceRevealsHq). KnownHq reports it.</summary>
    public int? HqStamp { get; set; }

    // ---- group D: ground forces (regiments and special forces standing on the system), per side and class, and the characters present.
    public int? StampD { get; set; }
    public List<GroundPicture> Ground { get; set; } = new();
    /// <summary>
    /// Group D characters: the characters (either side) on the system when D was last written, sorted by id; written with the rest of D (same
    /// reveals, same <see cref="StampD"/>), so they go stale with it. Null when none, and then left out of the saved form, so pictures without
    /// characters save and hash as before. rule: m5-fog-and-hq §F1 group D ("characters present", Manual p.69, p.121).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<CharacterPicture>? Characters { get; set; }

    // ---- group E: the other side's fleets present when last looked
    public int? StampE { get; set; }
    public List<FleetPicture> Fleets { get; set; } = new();

    internal SystemKnowledge Clone() => new()
    {
        Explored = Explored, LastLookDay = LastLookDay,
        StampA = StampA, Populated = Populated, Destroyed = Destroyed, Energy = Energy, Raw = Raw,
        StampB = StampB, Owner = Owner, CollectiveSupport = CollectiveSupport,
        StampC = StampC, Facilities = new SortedDictionary<string, int>(Facilities, StringComparer.Ordinal), FacilitiesQueued = FacilitiesQueued, HarvesterQueued = HarvesterQueued, HqHere = HqHere, HqStamp = HqStamp,
        StampD = StampD, Ground = Ground.Select(g => g.Clone()).ToList(), Characters = Characters?.Select(c => c.Clone()).ToList(),
        StampE = StampE, Fleets = Fleets.Select(f => f.Clone()).ToList(),
    };
}

/// <summary>Ground units of one side on a system, class counts. rule: m5-fog-and-hq §F1 group D.</summary>
public sealed class GroundPicture
{
    public Side Side { get; set; }
    public SortedDictionary<string, int> Units { get; set; } = new(StringComparer.Ordinal);
    public int Total => Units.Values.Sum();
    internal GroundPicture Clone() => new() { Side = Side, Units = new SortedDictionary<string, int>(Units, StringComparer.Ordinal) };
}

/// <summary>
/// One character as seen in group D: id, side and status (Free, Injured, or Captured when held prisoner on the system). rule: m5-fog-and-hq
/// §F1 group D.
/// </summary>
public sealed class CharacterPicture
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public Praedra.Core.Characters.CharacterStatus Status { get; set; }
    internal CharacterPicture Clone() => new() { Id = Id, Side = Side, Status = Status };
}

/// <summary>One fleet as seen: side, ship class counts and fighter squadron class counts (flying or aboard). No fleet id. rule: §F1 group E.</summary>
public sealed class FleetPicture
{
    public Side Side { get; set; }
    public SortedDictionary<string, int> Ships { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Squadrons { get; set; } = new(StringComparer.Ordinal);
    public int ShipCount => Ships.Values.Sum();
    public int SquadronCount => Squadrons.Values.Sum();
    internal FleetPicture Clone() => new()
    {
        Side = Side, Ships = new SortedDictionary<string, int>(Ships, StringComparer.Ordinal), Squadrons = new SortedDictionary<string, int>(Squadrons, StringComparer.Ordinal),
    };
    public override string ToString() =>
        string.Join(", ", Ships.Select(kv => $"{kv.Value} {kv.Key}").Concat(Squadrons.Select(kv => $"{kv.Value} {kv.Key}")));
}

/// <summary>Kinds of sighting. SensorArrival (name kept so saves stay readable) is a Sensor Station warning of a fleet heading for the
/// station's system. rule: m5-fog-and-hq §N3, §N4.</summary>
public enum SightingKind { Fleet, SensorArrival, AgentDetected }

/// <summary>How big a fleet a Sensor Station warns of: all it says about the fleet. rule: m5-fog-and-hq §N4 (size class).</summary>
public enum SensorSize { Few, Squadron, Large }

/// <summary>One sighting-log entry: {Day, System, Kind, Side, Detail}. A fleet sighting holds class counts, never a fleet id. rule: §N3.</summary>
public sealed class Sighting
{
    public int Day { get; set; }
    public string System { get; set; } = "";
    public SightingKind Kind { get; set; }
    /// <summary>The side of what was seen (the fleet's or the agent's side).</summary>
    public Side Side { get; set; }
    /// <summary>Kind Fleet only: what the fleet held. Null for the other kinds.</summary>
    public FleetPicture? Detail { get; set; }
    /// <summary>Kind SensorArrival only: the size class. Null for the other kinds (left out of the saved form, so other logs are unchanged). rule: §N4 S2.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SensorSize? Size { get; set; }
    /// <summary>Kind SensorArrival only: the day the fleet is expected to reach <see cref="System"/> (the station's system). Null for the other kinds. rule: §N4.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? ReachDay { get; set; }
    internal Sighting Clone() => new() { Day = Day, System = System, Kind = Kind, Side = Side, Detail = Detail?.Clone(), Size = Size, ReachDay = ReachDay };
}

/// <summary>"The HQ is in this sector", given by an espionage success when <see cref="FogParams.SectorSuspicion"/> is on. rule: §N6.</summary>
public sealed record HqSectorClue(string Sector, int Day);

/// <summary>Where the Hearth is. None = the game has no HQ (a bare host or the test campaign). rule: m5-fog-and-hq §F5.</summary>
public enum HqStatus { None, AtRest, InTransit, Destroyed }

/// <summary>
/// The Hearth as one state record (DEFAULT: kept outside the facility registry, so scrapping, autoscrap and assault collateral cannot touch
/// it). Owner is always the Collective. While in transit it has the same shape as a fleet transit. Saved and hashed. rule: m5-fog-and-hq §F5.
/// </summary>
public sealed class HqState
{
    public HqStatus Status { get; set; }
    /// <summary>The system it stands at (AtRest), or where it stood when destroyed (Destroyed); null in transit or when there is none.</summary>
    public string? System { get; set; }
    // ---- in transit (InTransit only)
    public string? From { get; set; }
    public List<string> Path { get; set; } = new();
    public List<int> HopTicks { get; set; } = new();
    public int HopIndex { get; set; }
    /// <summary>The day the current hop ends; the HQ reaches Path[HopIndex + 1] in the Step whose K is DueDay - 1.</summary>
    public int DueDay { get; set; }
    /// <summary>This transit is a flee (§F7 Flee), not an ordered move. Left out of the save while false.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Fleeing { get; set; }
    // ---- destroyed
    public string? DestroyedCause { get; set; }
    public int? DestroyedDay { get; set; }

    internal void CopyFrom(HqState o)
    {
        Status = o.Status; System = o.System; From = o.From; Path = o.Path.ToList(); HopTicks = o.HopTicks.ToList(); HopIndex = o.HopIndex;
        DueDay = o.DueDay; Fleeing = o.Fleeing; DestroyedCause = o.DestroyedCause; DestroyedDay = o.DestroyedDay;
    }

    /// <summary>Setup: the HQ stands at <paramref name="system"/> (null = no HQ). Raises nothing. rule: §F5 PlaceHq.</summary>
    internal void Place(string? system)
    {
        CopyFrom(new HqState());
        if (system != null) { Status = HqStatus.AtRest; System = system; }
    }
}
