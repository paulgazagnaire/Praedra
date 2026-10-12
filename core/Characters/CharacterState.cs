using Rebellion.Core.Clock;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Characters;

/// <summary>Traced: captured is +0xac bit 0, destroyed is +0x50 bit 3; injured and captured bar posts and missions. rule: m5-characters-and-force §The character object.</summary>
public enum CharacterStatus { Free, Injured, Captured, Dead }

/// <summary>A fleet post (None = a passenger). rule: m5-characters-and-force §Posts.</summary>
public enum CharacterPost { None, Admiral, Commander, General }

/// <summary>Any post, for <see cref="PostVacated"/>: the three fleet posts and the system's General.</summary>
public enum PostKind { Admiral, Commander, General, SystemGeneral }

/// <summary>Jedi rank from the effective Force (display and eligibility only). rule: m5-characters-and-force §Force (Rank).</summary>
public enum ForceRank { Trainee, Student, Knight, Master }

/// <summary>
/// Saved runtime state of one character (all in the state hash). rule: m5-characters-and-force §The character object (saved runtime state).
/// <see cref="System"/> is where a free standing character is; for an attached character the location is its fleet's (see
/// <see cref="CharacterModule.LocationOf"/>), and for a prisoner it is <see cref="HeldAt"/>.
/// </summary>
public sealed class CharacterRecord
{
    public int Id { get; set; }
    public Side Side { get; set; }
    /// <summary>Placed in the game (setup or a later recruitment). Unplaced characters are the recruitment pool (m5-missions). rule: m5-setup-and-victory §P9.</summary>
    public bool Placed { get; set; }
    /// <summary>The seven skills, rolled once (order of <see cref="Skill"/>).</summary>
    public List<int> Skills { get; set; } = new();
    public int Loyalty { get; set; }
    /// <summary>Traced +0x8e, set once at creation.</summary>
    public int ForceBase { get; set; }
    /// <summary>Traced +0x90, written only by Jedi Training.</summary>
    public int ForceAcquired { get; set; }
    public bool ForceUser { get; set; }
    public bool ForceDiscovered { get; set; }
    public CharacterStatus Status { get; set; }
    /// <summary>Where a standing (not attached, not travelling) character is; for an attached one the system it was attached at (not read).</summary>
    public string? System { get; set; }
    /// <summary>The fleet the character rides (0 = none).</summary>
    public int AttachedFleet { get; set; }
    public CharacterPost Post { get; set; }
    public Side? Captor { get; set; }
    public string? HeldAt { get; set; }
    public int InjuredDaysLeft { get; set; }
    /// <summary>A lone character's trip, exactly as a fleet's. rule: m5-characters-and-force §Moving a character alone.</summary>
    public FleetTransit? Transit { get; set; }

    /// <summary>Traced +0x8c = base + acquired.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int EffectiveForce => ForceBase + ForceAcquired;
    public int Skill(Skill s) => Skills[(int)s];

    internal CharacterRecord Clone() => new()
    {
        Id = Id, Side = Side, Placed = Placed, Skills = new List<int>(Skills), Loyalty = Loyalty, ForceBase = ForceBase, ForceAcquired = ForceAcquired,
        ForceUser = ForceUser, ForceDiscovered = ForceDiscovered, Status = Status, System = System, AttachedFleet = AttachedFleet, Post = Post,
        Captor = Captor, HeldAt = HeldAt, InjuredDaysLeft = InjuredDaysLeft,
        Transit = Transit == null ? null : new FleetTransit
        { Path = new List<string>(Transit.Path), HopTicks = new List<int>(Transit.HopTicks), HopIndex = Transit.HopIndex, DueDay = Transit.DueDay },
    };
}

/// <summary>Every character by id and the system General table. Saved and hashed. rule: m5-characters-and-force §The character object.</summary>
public sealed class CharacterState
{
    public SortedDictionary<int, CharacterRecord> Characters { get; init; } = new();
    /// <summary>System name -> character id of its General (one per system). rule: §The character object (SystemGeneral table).</summary>
    public SortedDictionary<string, int> SystemGenerals { get; init; } = new(StringComparer.Ordinal);

    internal void CopyFrom(CharacterState o)
    {
        foreach (var kv in o.Characters) Characters[kv.Key] = kv.Value.Clone();
        foreach (var kv in o.SystemGenerals) SystemGenerals[kv.Key] = kv.Value;
    }
}
