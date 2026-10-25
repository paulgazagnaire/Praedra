using Praedra.Core.Clock;
using Praedra.Core.Fleets;

namespace Praedra.Core.Characters;

/// <summary>Traced: captured is +0xac bit 0, destroyed is +0x50 bit 3; injured and captured bar posts and missions. rule: m5-characters-and-force §The character object.</summary>
public enum CharacterStatus { Free, Injured, Captured, Dead }

/// <summary>A fleet post (None = a passenger). rule: m5-characters-and-force §Posts.</summary>
public enum CharacterPost { None, Admiral, Commander, General }

/// <summary>Any post, for <see cref="PostVacated"/>: the three fleet posts and the system's General.</summary>
public enum PostKind { Admiral, Commander, General, SystemGeneral }

/// <summary>Talent rank from the effective Talent (display and eligibility only). rule: m5-characters-and-force §Talent (Rank).</summary>
public enum TalentRank { Latent, Deviation, Anomaly, Unforecastable }

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
    /// <c>TalentExperience</c> (+0x8e): rolled <c>tlvl + rand(0..tlvlv)</c> when the character has both TalentPotential and
    /// TalentAware (0x4eee60), and raised by the Talent encounter. 0 until then: a talented character has Talent 0 until he is aware. Was <c>TalentBase</c>.
    /// rule: m5-characters-and-force §Talent (Traced B2e §1, §5).
    /// </summary>
    public int TalentExperience { get; set; }
    /// <summary><c>TalentTraining</c> (+0x90), written only by Train Talent. Was <c>TalentAcquired</c>. rule: §Talent (Traced B2e §1).</summary>
    public int TalentTraining { get; set; }
    /// <summary>TalentPotential (+0xac bit 0x20): the character has Talent, known or not. rule: §Rolling the stats (awakening), §Talent.</summary>
    public bool TalentHolder { get; set; }
    /// <summary>TalentAware (+0xac bit 0x10): the character knows he has Talent (trainable, Talent rolled). rule: §Talent (Talent discovery; B2e §5).</summary>
    public bool TalentDiscovered { get; set; }
    /// <summary>Drawn talented at setup: can train and awaken under <see cref="TrainerPick.Random"/>. rule: §Rule (New) 4.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Talented { get; set; }
    /// <summary>
    /// Injury points (+0x94, 0..32767): they come off effective combat and heal 1 a day (2 with Talent 80+) while Healing. Injured status = points
    /// above 0 while not captured or dead. Replaces <c>injured_days_left</c>. rule: §Capture, injury, death, freeing (Traced B2e §2).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Injury { get; set; }
    /// <summary>LeadershipAdjustment (+0x92, percent): 50 for Hand-only characters while the Seat of Power is on, else 0. rule: §Seat of Power (B2e §6).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int LeadershipAdjustment { get; set; }
    /// <summary>
    /// The pair id (character data +0x34) stamped on both characters when they discovered each other (0x55ff60), from
    /// <see cref="CharacterState.NextPairId"/>; the encounter needs the same non-zero id on both. 0 = none. rule: §Talent (B2e §4).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int PairId { get; set; }
    /// <summary>KnowsHeritage (Nobody and Ledger, +0xb0 bit 0x1; name inferred from the string order). rule: §Talent (B2e §4, §5).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool KnowsHeritage { get; set; }
    /// <summary>The trainer who awakened this character (+0xa4; record id, 0 = none). rule: §Talent (B2e §5).</summary>
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
    /// <summary>The awakening timer (event 0x389) of a trainer whose DiscoveringTalentHolder flag is on; 0 = off. Same alignment. rule: §Talent (B2e §5, §7).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int NextAwakenDay { get; set; }
    /// <summary>
    /// The enemy talented character this character sensed or was sensed by (record id; 0 = none). Traced +0x9c "TalentHolderDiscoveredKey" (B2b, 0x4ee500).
    /// Cleared by the Talent encounter (B2e §4 step 6). Omitted from the save while 0, so older saves and hashes are unchanged. rule: m5-missions §3 Talent sensing.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int DiscoveredTalentHolder { get; set; }
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
    /// <summary>
    /// The fleet a prisoner is held aboard (0 = on the planet <see cref="HeldAt"/>, or with a mission team). A prisoner aboard has no HeldAt: he is
    /// located where his fleet is and travels with it. rule: m5-characters-and-force §Capture (B2e §3 "Where a new prisoner is held", Work 12; QA-7).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int HeldAboard { get; set; }
    /// <summary>
    /// The ship that caught him with no commander aboard (0 = held by the fleet). His escape place is that ship while it stays in his fleet.
    /// rule: m5-characters-and-force §Escape (B2e §3 "A prisoner held aboard stays aboard"; Q73).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int HeldAboardShip { get; set; }
    /// <summary>Where the last mission a capture ended was to return (Autoroute step 2; ParentAtMissionCompletion +0x70). QA-9, B2e Work 13.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnPlace { get; set; }
    /// <summary>Autorouting waits for a pending battle at his system (re-checked at slot 6.10). QA-9.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoroutePending { get; set; }
    /// <summary>The fleet an autorouted character joins on arrival (Autoroute step 4; 0 = none). QA-9.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int JoinFleet { get; set; }
    /// <summary>A lone character's trip, exactly as a fleet's. rule: m5-characters-and-force §Moving a character alone.</summary>
    public FleetTransit? Transit { get; set; }

    /// <summary>Talent <c>F</c> (+0x8c) = TalentExperience + TalentTraining (0x4eee40). rule: §Talent (Traced B2e §1).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int EffectiveTalent => TalentExperience + TalentTraining;
    /// <summary>The rolled (base) skill (+0x58..+0x64). Rules read <see cref="EffectiveSkill"/>; this is for growth and display of the base.</summary>
    public int BaseSkill(Skill s) => Skills[(int)s];

    /// <summary>
    /// The effective skill every rule reads (B2b, B2d, mission odds, the AI): diplomacy and espionage <c>B + B * F * pct / 10000</c>; the three
    /// research skills <c>B</c>; combat <c>max(0, B + B * F * pct / 10000 - Injury)</c>; leadership <c>B + B * LeadershipAdjustment / 100</c>.
    /// Integer division; <paramref name="forceSkillPercent"/> is <see cref="CharacterParams.TalentSkillPercent"/> (100 = Classic, bit-identical to
    /// <c>B + B * F / 100</c>). Callers go through <see cref="CharacterModule.EffectiveSkill"/>.
    /// rule: m5-characters-and-force §Talent (effects; Traced B2e §1: 0x4eecb0, 0x4eecf0, 0x4eed30..0x4eed70, 0x4eed90, 0x4eede0).
    /// </summary>
    public int EffectiveSkill(Skill s, int forceSkillPercent)
    {
        int b = Skills[(int)s], f = EffectiveTalent;
        // rule: m5-characters-and-force §Talent (New, Paul 2026-10-18; Classic = 100)
        int force = b * f * forceSkillPercent / (PercentDivisor * PercentDivisor);
        return s switch
        {
            Skill.Diplomacy or Skill.Espionage => b + force,
            Skill.Combat => Math.Max(0, b + force - Injury),
            Skill.Leadership => b + b * LeadershipAdjustment / PercentDivisor,
            _ => b,
        };
    }

    /// <summary>Percent divisor of the effective-skill formulas (0x53e940, 0x53e900). Traced B2e §1.</summary>
    public const int PercentDivisor = 100;

    internal CharacterRecord Clone() => new()
    {
        Id = Id, Side = Side, Placed = Placed, Skills = new List<int>(Skills), Loyalty = Loyalty, TalentExperience = TalentExperience, TalentTraining = TalentTraining,
        TalentHolder = TalentHolder, TalentDiscovered = TalentDiscovered, Talented = Talented, DiscoveredTalentHolder = DiscoveredTalentHolder, IdentifiedTraitor = IdentifiedTraitor, Status = Status, System = System, AttachedFleet = AttachedFleet, Post = Post,
        Captor = Captor, HeldAt = HeldAt, HeldAboard = HeldAboard, HeldAboardShip = HeldAboardShip, ReturnPlace = ReturnPlace, AutoroutePending = AutoroutePending, JoinFleet = JoinFleet, Injury = Injury, LeadershipAdjustment = LeadershipAdjustment, PairId = PairId, KnowsHeritage = KnowsHeritage,
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
    /// <summary>The last pair id handed out (the game-wide counter of 0x55ff60); 0 = none yet. rule: m5-characters-and-force §Talent (B2e §4).</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int NextPairId { get; set; }
    /// <summary>
    /// The side's pool-empty flags: set when a Recruitment success took the last member of the side's recruit pool (m = 1). Omitted from the save
    /// while false. rule: m5-missions §6 Recruitment (Recruitment pick, 2026-10-18; [EXE-own] 0x56b9a0, 0x55fc80).
    /// </summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CollectivePoolEmpty { get; set; }
    /// <summary>See <see cref="CollectivePoolEmpty"/>.</summary>
    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HandPoolEmpty { get; set; }

    internal void CopyFrom(CharacterState o)
    {
        NextPairId = o.NextPairId; CollectivePoolEmpty = o.CollectivePoolEmpty; HandPoolEmpty = o.HandPoolEmpty;
        foreach (var kv in o.Characters) Characters[kv.Key] = kv.Value.Clone();
        foreach (var kv in o.SystemGenerals) SystemGenerals[kv.Key] = kv.Value;
    }
}
