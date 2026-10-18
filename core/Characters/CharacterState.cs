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
    /// <summary>
    /// <c>ForceExperience</c> (+0x8e): rolled <c>jedi_level + rand(0..jedi_level_variance)</c> when the character has both ForcePotential and
    /// ForceAware (0x4eee60), and raised by the Force-user encounter. 0 until then: a Force user has Force 0 until he is aware. Was <c>ForceBase</c>.
    /// rule: m5-characters-and-force §Force (Traced B2e §1, §5).
    /// </summary>
    public int ForceExperience { get; set; }
    /// <summary><c>ForceTraining</c> (+0x90), written only by Jedi Training. Was <c>ForceAcquired</c>. rule: §Force (Traced B2e §1).</summary>
    public int ForceTraining { get; set; }
    /// <summary>ForcePotential (+0xac bit 0x20): the character has the Force, known or not. rule: §Rolling the stats (awakening), §Force.</summary>
    public bool ForceUser { get; set; }
    /// <summary>ForceAware (+0xac bit 0x10): the character knows he has the Force (trainable, Force rolled). rule: §Force (Force discovery; B2e §5).</summary>
    public bool ForceDiscovered { get; set; }
    /// <summary>
    /// Injury points (+0x94, 0..32767): they come off effective combat and heal 1 a day (2 with Force 80+) while Healing. Injured status = points
    /// above 0 while not captured or dead. Replaces <c>injured_days_left</c>. rule: §Capture, injury, death, freeing (Traced B2e §2).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Injury { get; set; }
    /// <summary>LeadershipAdjustment (+0x92, percent): 50 for Empire-only characters while the Seat of Power is on, else 0. rule: §Seat of Power (B2e §6).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int LeadershipAdjustment { get; set; }
    /// <summary>
    /// The pair id (character data +0x34) stamped on both characters when they discovered each other (0x55ff60), from
    /// <see cref="CharacterState.NextPairId"/>; the encounter needs the same non-zero id on both. 0 = none. rule: §Force (B2e §4).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int PairId { get; set; }
    /// <summary>KnowsHeritage (Luke and Leia, +0xb0 bit 0x1; name inferred from the string order). rule: §Force (B2e §4, §5).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool KnowsHeritage { get; set; }
    /// <summary>The trainer who awakened this character (+0xa4; record id, 0 = none). rule: §Force (B2e §5).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int AwakenedBy { get; set; }
    /// <summary>
    /// The day (the Step's day counter) of the prisoner's next escape attempt; 0 = none (not captured, or a capture that cannot escape, such as a
    /// story capture). Drawn <c>100 + rand(0..1000)</c> at the capture and after each attempt. rule: §Escape (Traced B2e §3, §7).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int NextEscapeDay { get; set; }
    /// <summary>
    /// The Healing timer (event 0x388): the day it fires next; 0 = the Healing flag is off. Set to day + period when the flag turns on (seen at a
    /// day-end), so the first point heals the day after (DEFAULT day alignment). rule: §Capture, injury, death, freeing (B2e §2, §7).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int NextHealDay { get; set; }
    /// <summary>The awakening timer (event 0x389) of a trainer whose DiscoveringForceUser flag is on; 0 = off. Same alignment. rule: §Force (B2e §5, §7).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int NextAwakenDay { get; set; }
    /// <summary>
    /// The enemy Force user this character sensed or was sensed by (record id; 0 = none). Traced +0x9c "ForceUserDiscoveredKey" (B2b, 0x4ee500).
    /// Cleared by the Force-user encounter (B2e §4 step 6). Omitted from the save while 0, so older saves and hashes are unchanged. rule: m5-missions §3 Force sensing.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int DiscoveredForceUser { get; set; }
    /// <summary>The traitor this character identified (record id; 0 = none). Traced +0xa0 "TraitorDiscoveredKey" (B2b, 0x4ee5c0). Omitted while 0. rule: m5-missions §3 Betrayal.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int IdentifiedTraitor { get; set; }
    public CharacterStatus Status { get; set; }
    /// <summary>Where a standing (not attached, not travelling) character is; for an attached one the system it was attached at (not read).</summary>
    public string? System { get; set; }
    /// <summary>The fleet the character rides (0 = none).</summary>
    public int AttachedFleet { get; set; }
    public CharacterPost Post { get; set; }
    public Side? Captor { get; set; }
    public string? HeldAt { get; set; }
    /// <summary>A lone character's trip, exactly as a fleet's. rule: m5-characters-and-force §Moving a character alone.</summary>
    public FleetTransit? Transit { get; set; }

    /// <summary>Force <c>F</c> (+0x8c) = ForceExperience + ForceTraining (0x4eee40). rule: §Force (Traced B2e §1).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int EffectiveForce => ForceExperience + ForceTraining;
    /// <summary>The rolled (base) skill (+0x58..+0x64). Rules read <see cref="EffectiveSkill"/>; this is for growth and display of the base.</summary>
    public int BaseSkill(Skill s) => Skills[(int)s];

    /// <summary>
    /// The effective skill every rule reads (B2b, B2d, mission odds, the AI): diplomacy and espionage <c>B + B * F / 100</c>; the three research
    /// skills <c>B</c>; combat <c>max(0, B + B * F / 100 - Injury)</c>; leadership <c>B + B * LeadershipAdjustment / 100</c>. Integer division.
    /// rule: m5-characters-and-force §Force (effects; Traced B2e §1: 0x4eecb0, 0x4eecf0, 0x4eed30..0x4eed70, 0x4eed90, 0x4eede0).
    /// </summary>
    public int EffectiveSkill(Skill s)
    {
        int b = Skills[(int)s], f = EffectiveForce;
        return s switch
        {
            Skill.Diplomacy or Skill.Espionage => b + b * f / PercentDivisor,
            Skill.Combat => Math.Max(0, b + b * f / PercentDivisor - Injury),
            Skill.Leadership => b + b * LeadershipAdjustment / PercentDivisor,
            _ => b,
        };
    }

    /// <summary>Percent divisor of the effective-skill formulas (0x53e940, 0x53e900). Traced B2e §1.</summary>
    public const int PercentDivisor = 100;

    internal CharacterRecord Clone() => new()
    {
        Id = Id, Side = Side, Placed = Placed, Skills = new List<int>(Skills), Loyalty = Loyalty, ForceExperience = ForceExperience, ForceTraining = ForceTraining,
        ForceUser = ForceUser, ForceDiscovered = ForceDiscovered, DiscoveredForceUser = DiscoveredForceUser, IdentifiedTraitor = IdentifiedTraitor, Status = Status, System = System, AttachedFleet = AttachedFleet, Post = Post,
        Captor = Captor, HeldAt = HeldAt, Injury = Injury, LeadershipAdjustment = LeadershipAdjustment, PairId = PairId, KnowsHeritage = KnowsHeritage,
        AwakenedBy = AwakenedBy, NextEscapeDay = NextEscapeDay, NextHealDay = NextHealDay, NextAwakenDay = NextAwakenDay,
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
    /// <summary>The last pair id handed out (the game-wide counter of 0x55ff60); 0 = none yet. rule: m5-characters-and-force §Force (B2e §4).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int NextPairId { get; set; }

    internal void CopyFrom(CharacterState o)
    {
        NextPairId = o.NextPairId;
        foreach (var kv in o.Characters) Characters[kv.Key] = kv.Value.Clone();
        foreach (var kv in o.SystemGenerals) SystemGenerals[kv.Key] = kv.Value;
    }
}
