using Rebellion.Core.Clock;

namespace Rebellion.Core.Fog;

/// <summary>
/// The fog-of-war state: each side's picture of the galaxy, its sighting log and its clues, plus the systems dealt "explored for all" at
/// setup (the espionage extras pool reads them). Saved and hashed; every collection sorted (ordinal names) so the form is canonical.
/// rule: m5-fog-and-hq §F1 (the data model), §N3 (the sighting log), §N9 (determinism).
/// </summary>
public sealed class FogState
{
    public SideKnowledge Empire { get; init; } = new();
    public SideKnowledge Alliance { get; init; } = new();
    /// <summary>Systems with <c>start_explored_all</c> = 1 (setup data). rule: m5-fog-and-hq §F2 Espionage (pool "explored class").</summary>
    public SortedSet<string> StartExploredAll { get; init; } = new(StringComparer.Ordinal);

    public SideKnowledge Of(Side s) => s == Side.Empire ? Empire : Alliance;

    internal void CopyFrom(FogState o)
    {
        Empire.CopyFrom(o.Empire);
        Alliance.CopyFrom(o.Alliance);
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
    }
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
    public int Energy { get; set; }
    public int Raw { get; set; }

    // ---- group B: owner and support
    public int? StampB { get; set; }
    /// <summary>Holder when last seen; null = neutral.</summary>
    public Side? Owner { get; set; }
    /// <summary>Alliance support 0..100 when last seen (Empire = 100 minus it).</summary>
    public int AllianceSupport { get; set; }

    // ---- group C: facilities and defences, and HqHere
    public int? StampC { get; set; }
    /// <summary>Completed facilities by class name, with counts.</summary>
    public SortedDictionary<string, int> Facilities { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Facilities not yet completed (queued production at the system).</summary>
    public int FacilitiesQueued { get; set; }
    /// <summary>The picture holds the HQ child. Written only by a reveal that writes group C (or by Presence with PresenceRevealsHq). rule: §N5.</summary>
    public bool HqHere { get; set; }
    /// <summary>The day HqHere was last written (by a group C reveal or a Presence with PresenceRevealsHq). KnownHq reports it.</summary>
    public int? HqStamp { get; set; }

    // ---- group D: ground forces (regiments and special forces standing on the system), per side and class. Characters are M5 missions'.
    public int? StampD { get; set; }
    public List<GroundPicture> Ground { get; set; } = new();

    // ---- group E: the other side's fleets present when last looked
    public int? StampE { get; set; }
    public List<FleetPicture> Fleets { get; set; } = new();

    internal SystemKnowledge Clone() => new()
    {
        Explored = Explored, LastLookDay = LastLookDay,
        StampA = StampA, Populated = Populated, Energy = Energy, Raw = Raw,
        StampB = StampB, Owner = Owner, AllianceSupport = AllianceSupport,
        StampC = StampC, Facilities = new SortedDictionary<string, int>(Facilities, StringComparer.Ordinal), FacilitiesQueued = FacilitiesQueued, HqHere = HqHere, HqStamp = HqStamp,
        StampD = StampD, Ground = Ground.Select(g => g.Clone()).ToList(),
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

/// <summary>Kinds of sighting. rule: m5-fog-and-hq §N3.</summary>
public enum SightingKind { Fleet, SensorArrival, AgentDetected }

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
    internal Sighting Clone() => new() { Day = Day, System = System, Kind = Kind, Side = Side, Detail = Detail?.Clone() };
}

/// <summary>"The HQ is in this sector", given by an espionage success when <see cref="FogParams.SectorSuspicion"/> is on. rule: §N6.</summary>
public sealed record HqSectorClue(string Sector, int Day);

/// <summary>Where the Rebel HQ is. None = the game has no HQ (a bare host or the test campaign). rule: m5-fog-and-hq §F5.</summary>
public enum HqStatus { None, AtRest, InTransit, Destroyed }

/// <summary>
/// The Rebel HQ as one state record (DEFAULT: kept outside the facility registry, so scrapping, autoscrap and assault collateral cannot touch
/// it). Owner is always the Alliance. While in transit it has the same shape as a fleet transit. Saved and hashed. rule: m5-fog-and-hq §F5.
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
    // ---- destroyed
    public string? DestroyedCause { get; set; }
    public int? DestroyedDay { get; set; }

    internal void CopyFrom(HqState o)
    {
        Status = o.Status; System = o.System; From = o.From; Path = o.Path.ToList(); HopTicks = o.HopTicks.ToList(); HopIndex = o.HopIndex;
        DueDay = o.DueDay; DestroyedCause = o.DestroyedCause; DestroyedDay = o.DestroyedDay;
    }

    /// <summary>Setup: the HQ stands at <paramref name="system"/> (null = no HQ). Raises nothing. rule: §F5 PlaceHq.</summary>
    internal void Place(string? system)
    {
        CopyFrom(new HqState());
        if (system != null) { Status = HqStatus.AtRest; System = system; }
    }
}
