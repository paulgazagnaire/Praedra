namespace Praedra.Core.Missions;

/// <summary>
/// The life of a mission: Outbound (accepted, flying to the target), Working (landed, a loop of iterations), ReturnPending (ended; the group leaves in
/// the next step 2), Returning (flying home). Done missions leave the state. rule: m5-missions §Rule (Classic) phases.
/// </summary>
public enum MissionPhase { Outbound, Working, ReturnPending, Returning }

/// <summary>How a mission ended. rule: m5-missions §5 (outcomes).</summary>
public enum MissionOutcome
{
    None, Succeeded, Failed, Foiled, Cancelled, TargetInvalid,
    /// <summary>The target system was destroyed by the Death Star. rule: m5-death-star §2 effect 3 (cause TargetDestroyed).</summary>
    TargetDestroyed,
}

/// <summary>
/// One mission group (saved and hashed). Ids are characters (record ids) and special forces (object ids), each list ascending. The group's trip is
/// stored here, not on its members: while a mission flies its characters and special forces stand on no system (System null); while it works they
/// stand on the target. rule: m5-missions §Orders, §1, §5.
/// </summary>
public sealed class MissionGroup
{
    public int Id { get; set; }
    public MissionKind Kind { get; set; }
    public Clock.Side Side { get; set; }
    public List<int> Characters { get; set; } = new();
    public List<int> SpecialForces { get; set; } = new();
    public List<int> DecoyCharacters { get; set; } = new();
    public List<int> DecoySpecialForces { get; set; } = new();
    /// <summary>The system the order was given from (the Return target). rule: §5.</summary>
    public string Home { get; set; } = "";
    /// <summary>The system the mission works at (for a character target: where that character was at order time).</summary>
    public string Target { get; set; } = "";
    public int? TargetCharacter { get; set; }
    /// <summary>The facility named by a Sabotage order with SabotageTargetMode Chosen.</summary>
    public int? Facility { get; set; }
    public MissionPhase Phase { get; set; }
    /// <summary>The group has left (step 2 done) on its current trip.</summary>
    public bool Departed { get; set; }
    /// <summary>The current trip: path from start to end (one name when the group is already there), its total ticks and the arrival day A.</summary>
    public List<string> Path { get; set; } = new();
    public int TotalTicks { get; set; }
    public int ArrivalDay { get; set; }
    /// <summary>The hook time of the next iteration (Working). rule: §1 (due = A + T), §2 step 5.</summary>
    public int Due { get; set; }
    public int Iterations { get; set; }
    public MissionOutcome Outcome { get; set; }
    /// <summary>
    /// Prisoners the team holds (the captured list +0x1cc): taken by a member of this group (Abduction, the kidnap of Assassination, a member as
    /// captor). They travel with the group and are held where it ends. rule: m5-missions §6 (B2e §3 "Where a new prisoner is held" step 4; QA-7).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Captives { get; set; }
    /// <summary>The character a Recruitment success brought into play (RecruitedCharacterKey); null = none. rule: m5-missions §6 Recruitment (2026-10-18).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? RecruitedCharacterKey { get; set; }

    internal MissionGroup Clone() => new()
    {
        Id = Id, Kind = Kind, Side = Side, Characters = new(Characters), SpecialForces = new(SpecialForces), DecoyCharacters = new(DecoyCharacters),
        DecoySpecialForces = new(DecoySpecialForces), Home = Home, Target = Target, TargetCharacter = TargetCharacter, Facility = Facility, Phase = Phase,
        Departed = Departed, Path = new(Path), TotalTicks = TotalTicks, ArrivalDay = ArrivalDay, Due = Due, Iterations = Iterations, Outcome = Outcome,
        Captives = Captives == null ? null : new(Captives), RecruitedCharacterKey = RecruitedCharacterKey,
    };

    /// <summary>Every unit in the group: team and decoys.</summary>
    public IEnumerable<int> AllCharacters => Characters.Concat(DecoyCharacters);
    public IEnumerable<int> AllSpecialForces => SpecialForces.Concat(DecoySpecialForces);
    public bool HasTeam => Characters.Count + SpecialForces.Count > 0;
}

/// <summary>Every running mission by id (ids from the shared object counter). Saved and hashed. rule: m5-missions §Orders (MissionId).</summary>
public sealed class MissionState
{
    public SortedDictionary<int, MissionGroup> Missions { get; init; } = new();

    internal void CopyFrom(MissionState o)
    {
        foreach (var kv in o.Missions) Missions[kv.Key] = kv.Value.Clone();
    }
}
