using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Fleets;

namespace Praedra.Core.Characters;

/// <summary>
/// Characters: the roster rolled at creation, posts (fleet Admiral, Commander, General; system General) and the three leadership hooks, riding a
/// fleet and moving alone, the New-rule Navigator, the state machine (free, injured, captured, dead) with its six functions, the harvester's
/// roll on characters, uprising incident effects 3 to 5, Talent score, rank and the Train Talent roll. Holds data and settings only; the state
/// lives in <see cref="GameState.Characters"/>. After <see cref="GameSave.Load"/>, attach again. Every loop is in ascending character id
/// (m1-clock rule 5). rule: m5-characters-and-talent (module CharacterModule, core/Characters/).
///
/// Missing rules are not invented: Talent detection, Talent and skill growth outside training and the encounter, assault harm and blockade escape
/// risk are switches whose "on" is refused (<see cref="CharacterParams.Validate"/>). Injury points, healing, escape, liberation, the talented
/// encounter, Talent awakening and the Seat of Power are Traced (B2e).
/// </summary>
public sealed partial class CharacterModule
{
    private readonly Game _game;
    private readonly FleetModule _fleets;
    public CharacterData Data { get; }
    public CharacterParams Params { get; }

    /// <summary>Is the character on a mission? m5-missions sets it; no mission exists yet, so the default answers false. rule: §Availability for missions.</summary>
    public Func<int, bool> IsOnMission { get; set; } = _ => false;
    /// <summary>Called when a capture or death ends a character's mission membership (m5-missions sets it). rule: §Capture, injury, death, freeing 2 and 3.</summary>
    public Action<StepContext?, CharacterRecord>? MissionMembershipEnded { get; set; }
    /// <summary>
    /// Refuses a death (the Kill becomes an Injure). The victory module sets it for <c>KeyDeathFallback = Immortal</c> (m5-setup-and-victory,
    /// Proposal, off by default). Null = never refused.
    /// </summary>
    public Func<CharacterRecord, bool>? DeathRefused { get; set; }
    /// <summary>
    /// A character's injury went up while he lives: a mission member whose record can resign (MISSNSD word 15) leaves his mission
    /// (MissionResignRequest, 0x534640). m5-missions sets it. rule: §Capture, injury, death, freeing (B2e §2 "When Injury changes").
    /// </summary>
    public Action<StepContext?, CharacterRecord>? InjuredResign { get; set; }
    /// <summary>
    /// A new prisoner whose captor is a character on mission <c>id</c> joins that mission as a captive (its captured list, 0x534390) unless he
    /// already stands on ground of the captor's side and is not the target of an Abduction (0x546370 step 4). Returns true when he joined.
    /// m5-missions sets it; null = never. rule: §Capture (B2e §3 "Where a new prisoner is held" step 4; QA-7).
    /// </summary>
    public Func<StepContext?, CharacterRecord, int, bool>? JoinAsCaptive { get; set; }
    /// <summary>The captor's usable special forces aboard the ships of a fleet (QA-7 DEFAULT for an escape from aboard). GameHost sets it. 0 when unset.</summary>
    public Func<Side, int, int> SpecialForcesInFleet { get; set; } = (_, _) => 0;
    /// <summary>The captor's usable special forces directly aboard one ship (Q73, an escape from a ship that caught him). GameHost sets it. 0 when unset.</summary>
    public Func<Side, int, int> SpecialForcesOnShip { get; set; } = (_, _) => 0;
    /// <summary>Where the mission holding the character was to end (its return place, 0x534390); null when he is on none. m5-missions sets it. QA-9.</summary>
    public Func<int, string?>? MissionReturnPlace { get; set; }
    /// <summary>
    /// The ESCAPETB points (input, percent), ascending: <see cref="CharacterParams.EscapeTable"/> if given, else the game data's table (GameHost
    /// sets it from mission-tables.csv). Null: an escape attempt is refused RuleMissing. rule: §Escape (B2e §3 step 5).
    /// </summary>
    public IReadOnlyList<(int Input, int Percent)>? EscapeTable { get; set; }

    private CharacterModule(Game game, FleetModule fleets, CharacterData data, CharacterParams p)
    {
        _game = game; _fleets = fleets; Data = data; Params = p;
    }

    /// <summary>
    /// Register the five orders, the departure (2), arrival (3) and day-end (6.10) hooks, and the fleet hooks (Navigator speed, MoveFleet accepted,
    /// fleet removed). Throws <see cref="RuleMissingException"/> for a refused switch. rule: §Where it runs in the day Step.
    /// </summary>
    public static CharacterModule Attach(Game game, FleetModule fleets, CharacterData data, CharacterParams? p = null)
    {
        p ??= new CharacterParams();
        p.Validate();
        var m = new CharacterModule(game, fleets, data, p);
        m.EscapeTable = p.EscapeTable;
        game.RegisterOrderHandler<AttachCharacterOrder>(m.HandleAttach);
        game.RegisterOrderHandler<DetachCharacterOrder>(m.HandleDetach);
        game.RegisterOrderHandler<AssignSystemGeneralOrder>(m.HandleAssign);
        game.RegisterOrderHandler<ReleaseSystemGeneralOrder>(m.HandleRelease);
        game.RegisterOrderHandler<MoveCharacterOrder>(m.HandleMove);
        game.RegisterHook("2", m.Departures);
        game.RegisterHook("3", m.Arrivals);
        game.RegisterHook("6.10", m.DayEnd);
        fleets.SpeedAdjust = m.NavigatorAdjust;
        fleets.MoveAccepted += m.OnMoveAccepted;
        fleets.FleetRemoving += m.OnFleetRemoving;
        return m;
    }

    private GameState State => _game.State;
    private CharacterState S => _game.State.Characters;

    // ================================================================== queries

    public IReadOnlyCollection<CharacterRecord> All => S.Characters.Values;
    public CharacterRecord? Get(int id) => S.Characters.TryGetValue(id, out var c) ? c : null;
    public CharacterClass ClassOf(CharacterRecord c) => Data.Get(c.Id);
    /// <summary>Placed characters (in play), by id.</summary>
    public IEnumerable<CharacterRecord> InPlay => S.Characters.Values.Where(c => c.Placed);
    /// <summary>The side's recruitment pool: roster characters not placed, by id. rule: m5-setup-and-victory §P9 (an extra), Open question 13.</summary>
    public IReadOnlyList<CharacterRecord> PoolOf(Side side) => S.Characters.Values.Where(c => !c.Placed && c.Side == side).ToList();
    /// <summary>
    /// The side's Recruitment pool: its minor characters not yet placed, counting only in-game roster rows, in id order. Majors are never
    /// recruited. rule: m5-missions §6 Recruitment (Recruitment pick, 2026-10-18; [EXE-own] 0x56b9a0, 0x55fc80).
    /// </summary>
    public IReadOnlyList<CharacterRecord> RecruitPoolOf(Side side) => S.Characters.Values
        .Where(c => !c.Placed && c.Side == side && Data.TryGet(c.Id, out var cls) && cls.InGame && cls.Kind == CharacterKind.Minor).ToList(); // SortedDictionary: id order

    /// <summary>The side's pool-empty flag: set when a Recruitment took the last member of its pool. rule: m5-missions §6 Recruitment (2026-10-18).</summary>
    public bool PoolEmpty(Side side) => side == Side.Collective ? S.CollectivePoolEmpty : S.HandPoolEmpty;

    /// <summary>
    /// The character sheet's <c>Recruit(side, system)</c>, the setup extras pick (m5-setup-and-victory §P9 "An extra") with the place given:
    /// the mission's own target system (it must be a system). <c>m</c> = <see cref="RecruitPoolOf"/> size; <c>m = 0</c>: nobody, no draw.
    /// Else one draw <c>r = rand(0..m-1)</c> and pool member <c>r</c> is placed free at the system, his skills and Talent as the extras have them
    /// (rolled by the creation rule, m5-characters-and-talent §Rolling the stats). If <c>m</c> was 1 the side's pool-empty flag is set.
    /// rule: m5-missions §6 Recruitment (Recruitment pick, 2026-10-18; [EXE-own] 0x56b9a0, 0x55fc80).
    /// </summary>
    public CharacterRecord? Recruit(StepContext ctx, Side side, string system)
    {
        if (!_fleets.Galaxy.TryFindSystem(system, out _)) return null; // rule: the place must be a system
        var pool = RecruitPoolOf(side);
        int m = pool.Count;
        if (m == 0) return null; // nobody is recruited and no draw
        var c = pool[ctx.Rng.NextInt(0, m - 1)];
        Place(c.Id, system, ctx);
        if (m == 1) { if (side == Side.Collective) S.CollectivePoolEmpty = true; else S.HandPoolEmpty = true; }
        return c;
    }

    /// <summary>Victory reads "is captured" (status Captured; the captor is always the other side). rule: m5-setup-and-victory §Victory.</summary>
    public bool IsCaptured(int id) => Get(id) is { Status: CharacterStatus.Captured };
    public bool IsDead(int id) => Get(id) is { Status: CharacterStatus.Dead };

    /// <summary>The system a fleet stands at, or the one it last left while on a lane.</summary>
    private static string? PlaceOfFleet(Fleet f) => f.At ?? (f.Transit is { } t ? t.Path[t.HopIndex] : f.PreviousSystem);

    /// <summary>
    /// Where a character is located: a prisoner at <see cref="CharacterRecord.HeldAt"/>; an attached character at its fleet's system (null while
    /// the fleet is in transit); a lone character at its system (null while travelling). rule: §Riding; §Capture (a captive is located at held_at).
    /// </summary>
    public string? LocationOf(CharacterRecord c)
    {
        if (c.Status == CharacterStatus.Captured)
            return c.HeldAboard != 0 ? (State.Fleets.TryGetValue(c.HeldAboard, out var hf) ? hf.At : null) : c.HeldAt; // QA-7: aboard = where his fleet is
        if (c.AttachedFleet != 0) return State.Fleets.TryGetValue(c.AttachedFleet, out var f) ? f.At : null;
        return c.System;
    }

    private string? HeldSystemPost(CharacterRecord c) => S.SystemGenerals.FirstOrDefault(kv => kv.Value == c.Id).Key;

    /// <summary>Free, placed, not attached, not a system General, not on a mission. m5-missions must call it. rule: §Availability for missions.</summary>
    public bool IsAvailable(CharacterRecord c) =>
        c.Placed && c.Status == CharacterStatus.Free && c.AttachedFleet == 0 && HeldSystemPost(c) == null && !IsOnMission(c.Id);

    // ---- leadership hooks. rule: §Posts (the three hooks, exact): the effective leadership of the post's Free holder, or 0; no draws.

    /// <summary>Effective leadership <c>B + B * LeadershipAdjustment / 100</c> (the Seat of Power). rule: §Posts (the three hooks); B2e §1, §6.</summary>
    public int EffectiveLeadership(CharacterRecord c) => EffectiveSkill(c, Skill.Leadership);

    /// <summary>The one reader of a skill for every rule (missions, duels, B2b, the AI). rule: §Talent (effects; Traced B2e §1).</summary>
    // rule: m5-characters-and-talent §Talent (New, Paul 2026-10-18; Classic = 100): Talent share is <see cref="CharacterParams.TalentSkillPercent"/>.
    public int EffectiveSkill(CharacterRecord c, Skill s) => c.EffectiveSkill(s, Params.TalentSkillPercent);

    private CharacterRecord? PostHolder(int fleetId, CharacterPost post) =>
        S.Characters.Values.FirstOrDefault(c => c.AttachedFleet == fleetId && c.Post == post && post != CharacterPost.None);

    private int HolderLeadership(int fleetId, CharacterPost post) =>
        PostHolder(fleetId, post) is { Status: CharacterStatus.Free } c ? EffectiveLeadership(c) : 0;

    /// <summary>Bombardment multiplier input. rule: §Posts (Admiral; m3-bombardment L / 40 + 1).</summary>
    public int AdmiralLeadership(Fleet f) => HolderLeadership(f.Id, CharacterPost.Admiral);
    /// <summary>Landing regiments' bonus L / 30. rule: §Posts (General of a fleet; m3-assault Resolution 3).</summary>
    public int FleetGeneralLeadership(Fleet f) => HolderLeadership(f.Id, CharacterPost.General);
    /// <summary>Defenders' bonus L / 30. rule: §Posts (General of a system).</summary>
    public int SystemGeneralLeadership(string system) =>
        S.SystemGenerals.TryGetValue(system, out var id) && Get(id) is { Status: CharacterStatus.Free } c ? EffectiveLeadership(c) : 0;

    /// <summary>The fleet post holder (any status), or null.</summary>
    public CharacterRecord? HolderOf(int fleetId, CharacterPost post) => PostHolder(fleetId, post);
    public CharacterRecord? SystemGeneralOf(string system) => S.SystemGenerals.TryGetValue(system, out var id) ? Get(id) : null;
    public IEnumerable<CharacterRecord> AttachedTo(int fleetId) => S.Characters.Values.Where(c => c.AttachedFleet == fleetId);

    // ---- Talent. rule: §Talent

    /// <summary>Rank from the effective Talent (null = not a talented character). rule: §Talent (Rank), CH7.</summary>
    public TalentRank? RankOf(CharacterRecord c) => RankFor(c.TalentHolder, c.EffectiveTalent);

    public TalentRank? RankFor(bool talented, int effectiveTalent)
    {
        if (!talented) return null;
        if (effectiveTalent >= Params.TalentUnforecastableMin) return TalentRank.Unforecastable;
        if (effectiveTalent >= Params.TalentAnomalyMin) return TalentRank.Anomaly;
        if (effectiveTalent >= Params.TalentDeviationMin) return TalentRank.Deviation;
        return TalentRank.Latent;
    }

    /// <summary>
    /// The eligibility the Train Talent mission checks: the trainer has the trainer flag, is Free and available (and a Knight with
    /// <see cref="CharacterParams.TrainerNeedsAnomaly"/>); the student is a discovered talented character, Free, same side, at the trainer's system.
    /// rule: §Talent (Eligibility).
    /// </summary>
    public bool CanTrain(CharacterRecord trainer, CharacterRecord student)
    {
        if (!IsTrainer(trainer) || !IsAvailable(trainer)) return false;
        if (Params.TrainerNeedsAnomaly && trainer.EffectiveTalent < Params.TalentAnomalyMin) return false;
        return student.Id != trainer.Id && student.Placed && student.TalentHolder && student.TalentDiscovered && student.Status == CharacterStatus.Free
            && student.Side == trainer.Side && LocationOf(student) != null && LocationOf(student) == LocationOf(trainer);
    }

    /// <summary>
    /// Can train and awaken: the data flag <c>trainer</c> under <see cref="TrainerPick.Fixed"/> (Classic), the setup draw under Random (New).
    /// rule: §Talent (Eligibility); §Rule (New) 4.
    /// </summary>
    public bool IsTrainer(CharacterRecord c) => Params.TrainerPick == TrainerPick.Random ? c.Talented : ClassOf(c).TalentTrainer;

    // ================================================================== creation and setup (outside a Step)

    /// <summary>
    /// Roll one character from its record with the game generator, in the fixed draw order: the seven skills (each only if its variance is above
    /// 0), loyalty (same), the awakening (no draw for known Talent, probability 0 or 100+), then Talent level (draw only if its variance is above
    /// 0). No clamp. The character is created unplaced. rule: §Rolling the stats (Traced for Talent, DEFAULT for skills), CH1, CH2.
    /// </summary>
    public CharacterRecord Create(CharacterClass cls, GameRng rng)
    {
        var c = new CharacterRecord { Id = cls.Id, Side = cls.Side };
        for (int i = 0; i < CharacterClass.SkillCount; i++)
            c.Skills.Add(cls.SkillBase[i] + (cls.SkillVariance[i] > 0 ? rng.NextInt(0, cls.SkillVariance[i]) : 0));
        c.Loyalty = cls.LoyaltyBase + (cls.LoyaltyVariance > 0 ? rng.NextInt(0, cls.LoyaltyVariance) : 0);
        bool user;
        if (cls.KnownTalent) user = true;
        else if (cls.TalentProbability <= 0) user = false;
        else if (cls.TalentProbability >= 100) user = true;
        else user = rng.NextInt(0, 99) < cls.TalentProbability; // flagging roll's form [inferred]
        c.TalentHolder = user;
        c.TalentDiscovered = cls.KnownTalent; // Classic text: Nobody, Ravilla and The Chair are public talented characters
        c.TalentExperience = 0; c.TalentTraining = 0;
        if (c.TalentHolder && c.TalentDiscovered) RollTalentExperience(c, rng); // B2e §5: only once aware (known Talent: at creation, inferred)
        S.Characters[c.Id] = c;
        return c;
    }

    /// <summary>
    /// TalentPotential and TalentAware both set with TalentExperience still 0: <c>TalentExperience := tlvl + rand(0..tlvlv)</c>
    /// (record words 30, 31), the draw only when the variance is above 0. rule: §Talent (Traced B2e §5, 0x4eee60).
    /// </summary>
    private void RollTalentExperience(CharacterRecord c, GameRng rng)
    {
        if (!c.TalentHolder || !c.TalentDiscovered || c.TalentExperience != 0) return;
        var cls = ClassOf(c);
        c.TalentExperience = cls.TalentLevelBase + (cls.TalentLevelVariance > 0 ? rng.NextInt(0, cls.TalentLevelVariance) : 0);
    }

    /// <summary>Create every roster character (in_game) in ascending id, from the game generator. rule: §Rolling the stats (creation draw order).</summary>
    public void CreateAll()
    {
        foreach (var cls in Data.Roster) Create(cls, _game.Rng);
    }

    /// <summary>Put a created character at a system (setup, or a recruitment inside a Step: then <c>CharacterCreated</c> is raised).</summary>
    public void Place(int id, string system, StepContext? ctx = null)
    {
        if (!_fleets.Galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        var c = Get(id) ?? throw new ArgumentException($"Character {id} was not created.");
        c.Placed = true; c.System = system; c.AttachedFleet = 0; c.Post = CharacterPost.None;
        if (ctx != null) { int day = ctx.Day + 1; ctx.Emit((i, _) => new CharacterCreated(i, day, id, c.Side, system)); }
    }

    /// <summary>Put a created character aboard a fleet as a passenger (setup: "aboard ship j"; the module stores the fleet only). rule: m5-setup-and-victory §P9.</summary>
    public void PlaceInFleet(int id, int fleetId)
    {
        var f = State.Fleets.TryGetValue(fleetId, out var x) ? x : throw new ArgumentException($"Unknown fleet {fleetId}.");
        var c = Get(id) ?? throw new ArgumentException($"Character {id} was not created.");
        c.Placed = true; c.System = f.At; c.AttachedFleet = fleetId; c.Post = CharacterPost.None;
    }


    /// <summary>Setup or tests: give a character a fleet post without the order's checks (raises nothing).</summary>
    public void SetPost(int id, int fleetId, CharacterPost post)
    {
        PlaceInFleet(id, fleetId);
        Get(id)!.Post = post;
    }

    /// <summary>
    /// The starting characters (m5-setup-and-victory P9, Traced 0x55f7b0): create the roster (the creation draws), then follow start-cast.csv in
    /// file order: fixed systems, the HQ, <c>RandomLocation</c> characters, and each side's extras. Draws: as the sheet's draw order.
    /// </summary>
    public void SetupNewGame(string? hqSystem)
    {
        var rng = _game.Rng;
        CreateAll();
        foreach (var row in Data.StartCast)
        {
            switch (row.Rule)
            {
                case StartRule.FixedSystem: Place(row.Character, row.Place); break;
                case StartRule.FixedHq: Place(row.Character, hqSystem ?? throw new InvalidOperationException("Setup: no Hearth system for the FixedHq character.")); break;
                case StartRule.RandomLocation:
                    if (row.Officer && Params.StartCastOfficers == StartCastOfficers.None) break; // the officer stays in the pool
                    PlaceAt(row.Character, RandomLocation(Data.Get(row.Character).Side, rng));
                    break;
                case StartRule.Extras:
                    var side = row.Place == "Hand" ? Side.Hand : row.Place == "Collective" ? Side.Collective : throw new InvalidOperationException($"start-cast: extras side '{row.Place}'.");
                    for (int k = 0; k < Params.StartExtrasPerSide; k++)
                    {
                        var loc = RandomLocation(side, rng);
                        var pool = PoolOf(side); // id order
                        if (pool.Count == 0) continue; // nothing placed and no further draw
                        PlaceAt(pool[rng.NextInt(0, pool.Count - 1)].Id, loc);
                    }
                    break;
            }
        }
        if (Params.TrainerPick == TrainerPick.Random) PickTalented(rng);
        RefreshSeatOfPower(); // rule: §Seat of Power (B2e §6), The Chair's start place
    }

    /// <summary>
    /// Random trainers (New, Paul 2026-10-18), after the start cast: for the Collective then the Hand (DEFAULT order), up to TalentedPerSide draws
    /// k = rand(0..n-1) over that side's placed characters in id order, without replacement; each drawn one is Talented, a talented character and aware,
    /// with Talent = rand(TalentedMin..TalentedMax) unless his own is higher (Q16 DEFAULT): one more draw each. The pool leaves out the already
    /// strong (own Talent at TalentedMin or more) and the never_talented characters (Q17, Paul 2026-10-18). rule: §Rule (New) 4.
    /// </summary>
    private void PickTalented(GameRng rng)
    {
        foreach (var side in new[] { Side.Collective, Side.Hand })
        {
            var pool = S.Characters.Values.Where(c => c.Placed && c.Side == side
                && !(Params.TalentedSkipStrong && c.EffectiveTalent >= Params.TalentedMin)   // Q17 (Paul 2026-10-18): never the already strong
                && !(Params.TalentedSkipFlagged && ClassOf(c).NeverTalented)).ToList();          // Q17 (Paul 2026-10-18): never the Repossessors; id order
            for (int k = 0; k < Params.TalentedPerSide && pool.Count > 0; k++)
            {
                var c = pool[rng.NextInt(0, pool.Count - 1)];
                pool.Remove(c);
                int f = Params.TalentedMin + (Params.TalentedMax > Params.TalentedMin ? rng.NextInt(0, Params.TalentedMax - Params.TalentedMin) : 0);
                c.Talented = true; c.TalentHolder = true; c.TalentDiscovered = true;
                c.TalentExperience = Math.Max(c.TalentExperience, f - c.TalentTraining);
            }
        }
    }

    private void PlaceAt(int id, (string? System, int Fleet) loc)
    {
        if (loc.System != null) Place(id, loc.System); else PlaceInFleet(id, loc.Fleet);
    }

    /// <summary>
    /// Traced 0x562710: S = systems held by the side (ordinal), F = its Usable fleets (fleet id): at a system, not en route, holding at least one
    /// completed capital ship (B4 §9); r = rand(0..F+S-1); a system, or a fleet and then j = rand(0..c-1) over its completed capital ships for the
    /// ship aboard (drawn so the sequence matches; only the fleet is stored). rule: m5-setup-and-victory §P9 (RandomLocation).
    /// </summary>
    public (string? System, int Fleet) RandomLocation(Side side, GameRng rng)
    {
        var systems = State.SystemOwners.Where(kv => kv.Value == side).Select(kv => kv.Key).ToList(); // SortedDictionary: ordinal
        int Usable(Fleet f) => f.Ships.Count(id => State.Economy.Objects.TryGetValue(id, out var o) && o.Completed);
        var fleets = State.Fleets.Values.Where(f => f.Side == side && f.At != null && f.Transit == null && Usable(f) > 0).ToList(); // fleet id order
        int n = systems.Count + fleets.Count;
        if (n == 0) throw new InvalidOperationException($"Setup: {side} holds no system and no fleet for RandomLocation.");
        int r = rng.NextInt(0, n - 1);
        if (r < systems.Count) return (systems[r], 0);
        var f = fleets[r - systems.Count];
        rng.NextInt(0, Usable(f) - 1);
        return (null, f.Id);
    }

    // ================================================================== other sheets' effects

    /// <summary>
    /// The harvester's roll on the characters of a destroyed system (called by the harvester module at slot 5.1): candidates are characters
    /// located there that ride no fleet (standing, or held as prisoners), Free, Injured or Captured, both sides with
    /// <see cref="CharacterParams.HarvesterHitsOwnCharacters"/> (else not the attacker's). Riders of a fleet that died with the system are already
    /// detached and stand there; riders of the attacker's own fleet stay aboard and are not candidates. In id order each MINOR draws
    /// r = rand(1..100) and is hit if r &lt;= <see cref="CharacterParams.HarvesterHitPercent"/> (50): Injure then Kill (a captive is killed without
    /// the injury). Majors cost no draw. Then the system's General post is cleared.
    /// rule: m5-harvester §2 effect 3 (rand(1..100) &lt;= HarvesterMinorKillPct, minors only, draw order by id);
    /// m5-characters-and-talent §The harvester's roll on characters (CH19: the same chance, there written rand(0..99) &lt; 50).
    /// </summary>
    public void HarvesterStrike(StepContext ctx, string system, Side attacker)
    {
        var cands = S.Characters.Values.Where(c => c.Placed && ClassOf(c).Kind == CharacterKind.Minor
            && c.Status is CharacterStatus.Free or CharacterStatus.Injured or CharacterStatus.Captured
            && c.AttachedFleet == 0 && LocationOf(c) == system && (Params.HarvesterHitsOwnCharacters || c.Side != attacker)).ToList();
        foreach (var c in cands)
        {
            int r = ctx.Rng.NextInt(HarvesterDieMin, HarvesterDieMax);
            if (r > Params.HarvesterHitPercent) continue;
            // the hit: the Hurt roll on a free one (B2e Work 2: callers that pass no amount use Hurt), then the kill if he did not die of it
            if (c.Status != CharacterStatus.Captured) Injure(ctx, c, CharacterCause.Harvester);
            if (c.Status != CharacterStatus.Dead) Kill(ctx, c, CharacterCause.Harvester); // reason 10
        }
        if (S.SystemGenerals.TryGetValue(system, out var gid) && Get(gid) is { } g)
        {
            S.SystemGenerals.Remove(system);
            int day = ctx.Day + 1;
            ctx.Emit((i, _) => new PostVacated(i, day, g.Id, g.Side, PostKind.SystemGeneral, CharacterCause.SystemDestroyed));
        }
    }

    /// <summary>The harvester's die is rand(1..100), a percent roll. rule: m5-harvester §2 effect 3 (Traced shape, GNPRTB 0x606).</summary>
    private const int HarvesterDieMin = 1, HarvesterDieMax = 100;

    /// <summary>
    /// What happens to the characters that survived the harvester's roll on a destroyed system (called after <see cref="HarvesterStrike"/>): every
    /// character located there that rides no fleet is moved. A Free or Injured one of the attacker's side boards the attacking fleet
    /// <paramref name="attackerFleet"/> (a passenger, no post; DEFAULT). Any other goes to the nearest system its side holds (fewest lane steps,
    /// ties by system index = name order), leaving now and arriving the next day (DEFAULT); with no system left to go to it is captured by the
    /// attacker (DEFAULT). A prisoner stays a prisoner and is held at the nearest system its captor holds at once, or where it is when the captor
    /// holds none (our DEFAULT: the sheet names no rule for prisoners). The destroyed system itself never counts as held.
    /// rule: m5-harvester §2 effect 3 (every character that survives is moved).
    /// </summary>
    public void HarvesterRelocate(StepContext ctx, string system, Side attacker, int attackerFleet)
    {
        int day = ctx.Day + 1;
        var movers = S.Characters.Values.Where(c => c.Placed && c.Status is CharacterStatus.Free or CharacterStatus.Injured or CharacterStatus.Captured
            && c.AttachedFleet == 0 && c.HeldAboard == 0 && c.Transit == null && LocationOf(c) == system).ToList(); // id order (a prisoner aboard stays with his fleet, QA-7)
        foreach (var c in movers)
        {
            if (c.Status == CharacterStatus.Captured)
            {
                if (NearestHeldSystem(system, c.Captor ?? (c.Side == Side.Hand ? Side.Collective : Side.Hand)) is { } held) { c.HeldAt = held; c.System = held; }
                continue;
            }
            if (c.Side == attacker && State.Fleets.TryGetValue(attackerFleet, out var fleet) && fleet.At == system)
            {
                c.AttachedFleet = fleet.Id; c.Post = CharacterPost.None; c.System = system;
                int fid = fleet.Id; var side = c.Side;
                ctx.Emit((i, _) => new CharacterAttached(i, day, c.Id, side, fid, CharacterPost.None, CharacterCause.Harvester));
                continue;
            }
            var dest = NearestHeldSystem(system, c.Side);
            if (dest == null) { Capture(ctx, c, c.Side == Side.Hand ? Side.Collective : Side.Hand, system, CharacterCause.Harvester); continue; }
            // one hop of one tick: it leaves now (slot 5.1 of Step K) and arrives in step 3 of Step K + 1, "the next day"
            c.System = null;
            c.Transit = new FleetTransit { Path = new List<string> { system, dest }, HopTicks = new List<int> { HarvesterFlightTicks }, HopIndex = 0, DueDay = ctx.Day + 1 + HarvesterFlightTicks };
            string from = system, to = dest; var cs = c.Side;
            ctx.Emit((i, _) => new CharacterDeparted(i, day, c.Id, cs, from, HarvesterFlightTicks));
        }
    }

    /// <summary>Ticks of the relocation flight of a survivor: it arrives the next day. rule: m5-harvester §2 effect 3 (DEFAULT, "arriving the next day").</summary>
    private const int HarvesterFlightTicks = 1;

    /// <summary>
    /// The system <paramref name="side"/> holds that is fewest lane steps from <paramref name="from"/> (breadth-first over every lane, through any
    /// system; ties by system index, which is name order); the start itself never counts. Null if the side holds no reachable system.
    /// rule: m5-harvester §2 effect 3 (nearest system its side holds, fewest lane steps, ties by system id).
    /// </summary>
    private string? NearestHeldSystem(string from, Side side) => _fleets.NearestHeldSystem(from, side);

    /// <summary>
    /// Uprising incident effects (m2-uprisings-and-garrison, slot 6.3): code 3 = Injure one of the holder's Free characters located at the system
    /// (attached ones count), k = rand(0..n-1), none and no draw if n = 0 (Injure is the Hurt roll on his own combat, B2e Work 2); code 4 = Free one prisoner held there by the holder, k = rand(0..n-1);
    /// code 5 = free all of them, no draw. rule: §What other sheets' effects call (DEFAULT "where", Open question 9); CH26, CH27.
    /// </summary>
    public void UprisingIncident(StepContext ctx, int day, string system, Side holder, int code)
    {
        switch (code)
        {
            case 3:
                {
                    var free = S.Characters.Values.Where(c => c.Placed && c.Side == holder && c.Status == CharacterStatus.Free && LocationOf(c) == system).ToList();
                    if (free.Count == 0) return;
                    Injure(ctx, free[ctx.Rng.NextInt(0, free.Count - 1)], CharacterCause.Uprising);
                    return;
                }
            case 4:
            case 5:
                {
                    var held = S.Characters.Values.Where(c => c.Status == CharacterStatus.Captured && c.Captor == holder && c.HeldAt == system).ToList();
                    if (held.Count == 0) return;
                    if (code == 4) Free(ctx, held[ctx.Rng.NextInt(0, held.Count - 1)], CharacterCause.Uprising);
                    else foreach (var c in held) Free(ctx, c, CharacterCause.Uprising);
                    return;
                }
        }
    }

    /// <summary>
    /// A system changed holder (ControlModule.SetHolder): the old holder's system General is cleared (PostVacated) and stays free at the system;
    /// the fleet's General is not appointed (DEFAULT). rule: §Posts end (fills m2-control-and-capture step 5); CH28.
    /// </summary>
    public void OnHolderChanged(CaptureInfo info)
    {
        if (S.SystemGenerals.TryGetValue(info.System, out var id) && Get(id) is { } c && c.Side != info.New)
        {
            S.SystemGenerals.Remove(info.System);
            int day = info.Ctx.Day + 1;
            info.Ctx.Emit((i, _) => new PostVacated(i, day, c.Id, c.Side, PostKind.SystemGeneral, CharacterCause.HolderChanged));
        }
        Liberate(info.Ctx, info.System);
        RefreshSeatOfPower();
    }

    /// <summary>
    /// Liberation (character vtable +0xb0 = 0x4ef260, called for every object of a system that changed side): every prisoner held at
    /// <paramref name="system"/>, not on a mission, whose captor is not the system's side now (retaken, or neutral) is freed, cause Liberation,
    /// in id order, no draw. A free character left on a planet that changed side would set off by himself (Autorouting): where to is not read,
    /// DEFAULT he stays. rule: §Capture, injury, death, freeing (Traced B2e §3 "Liberation").
    /// </summary>
    public void Liberate(StepContext ctx, string system)
    {
        var owner = _fleets.OwnerOf(system);
        foreach (var c in S.Characters.Values.Where(c => c.Status == CharacterStatus.Captured && c.HeldAt == system && c.Captor != owner
            && !IsOnMission(c.Id)).ToList())
            Free(ctx, c, CharacterCause.Liberation);
    }

    /// <summary>
    /// The characters at a world that changed hands, inside the flip (B5a section 5, handler <c>0x4ef260</c>, in id order as the stand-in for child
    /// order): every prisoner held there, not on a mission, whose captor is not the new holder is freed (<see cref="Liberate"/>, cause Liberation);
    /// then every character standing there (not riding a fleet, not travelling, not dead, not on a mission) who is not of the new holder leaves,
    /// never captured and never harmed, by <see cref="Autoroute"/> (B2e §3's seven steps, QA-9; a freed prisoner is already autorouted by Free).
    /// The old General's post is cleared afterwards by <see cref="OnHolderChanged"/> (whose own
    /// Liberate call then finds nobody left to free).
    /// ControlModule.CharactersAtCapture calls it. rule: m2-control-and-capture §What happens at the moment of capture (B5a, 2026-10-17).
    /// </summary>
    public void OnCapture(StepContext ctx, string system, Side? old, Side? holder)
    {
        Liberate(ctx, system); // B2e's liberation, at the flip (the world's holder is already the new one; also when it went neutral)
        var leaving = S.Characters.Values.Where(c => c.Placed && c.Side != holder && c.System == system).ToList(); // id order
        foreach (var c in leaving) Autoroute(ctx, c); // B2e §3 / QA-9: Autoroute keeps its own tests (free or injured, no fleet, not travelling, no mission)
    }

    // ================================================================== Navigator and fleet hooks (§Navigator, §Riding)

    /// <summary>
    /// New rule (NavigatorBonus): S' = S * best / 100 and, only when best &lt; 100, S' = max(25, S'); best = the smallest navigator speed among the
    /// Free characters attached to the fleet (any post). No stacking. An immobile fleet stays immobile (DEFAULT). rule: §Navigator; CH15 to CH18.
    /// </summary>
    public int NavigatorAdjust(Fleet f, int speed)
    {
        if (!Params.NavigatorBonus || speed <= 0) return speed;
        int best = Travel.TravelFormula.DefaultSpeed;
        foreach (var c in S.Characters.Values)
            if (c.AttachedFleet == f.Id && c.Status == CharacterStatus.Free) best = Math.Min(best, ClassOf(c).NavigatorSpeed);
        if (best >= Travel.TravelFormula.DefaultSpeed) return speed;
        return Math.Max(Params.NavigatorFloor, speed * best / Travel.TravelFormula.PercentDivisor);
    }

    // OfficersRideFleet = false: every attached character is detached at the departure system when the MoveFleet is accepted. rule: §Riding; CH14.
    private void OnMoveAccepted(StepContext ctx, Fleet f)
    {
        if (Params.OfficersRideFleet) return;
        foreach (var c in S.Characters.Values.Where(c => c.AttachedFleet == f.Id).ToList()) Detach(ctx, ctx.Day, c, CharacterCause.FleetMoved);
    }

    // A fleet leaves the state. Merged or emptied by a transfer: riders attach to the receiving fleet; a post it already has is lost (the incoming
    // holder becomes a None rider). Any other cause: detached, free at the fleet's system (CharactersLostWithFleet off). rule: §Riding; CH29, CH30.
    private void OnFleetRemoving(StepContext? ctx, int day, Fleet f, DissolveCause cause, int? into)
    {
        // QA-7 DEFAULTs (B2e Work 12): prisoners aboard follow a merge or transfer into the receiving fleet; a holding fleet that leaves the state any
        // other way frees them as the free characters aboard are released (CharactersLostWithFleet off): at the fleet's system. Never put ashore.
        foreach (var c in S.Characters.Values.Where(c => c.Status == CharacterStatus.Captured && c.HeldAboard == f.Id).ToList())
        {
            if (into is int dest && State.Fleets.ContainsKey(dest)) c.HeldAboard = dest;
            else { c.HeldAboard = 0; c.HeldAboardShip = 0; c.HeldAt = PlaceOfFleet(f); c.System = c.HeldAt; Free(ctx, c, CharacterCause.HoldingFleetLost); }
        }
        var riders = S.Characters.Values.Where(c => c.AttachedFleet == f.Id).ToList();
        if (riders.Count == 0) return;
        if (into is int to && State.Fleets.TryGetValue(to, out var target))
        {
            foreach (var c in riders)
            {
                if (c.Post != CharacterPost.None && PostHolder(to, c.Post) != null)
                {
                    var post = (PostKind)((int)c.Post - 1);
                    Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, post, CharacterCause.Merged));
                    c.Post = CharacterPost.None;
                }
                c.AttachedFleet = to; c.System = target.At;
                var p = c.Post;
                Emit(ctx, (i, _) => new CharacterAttached(i, day, c.Id, c.Side, to, p, CharacterCause.Merged));
            }
            return;
        }
        foreach (var c in riders) Detach(ctx, day, c, CharacterCause.FleetDissolved);
    }

    // ================================================================== orders (step 1)

    private bool Known(int id, out CharacterRecord c) => S.Characters.TryGetValue(id, out c!) && c.Placed;
    private static bool Moving(Fleet f) => f.Transit != null || f.At == null;

    // rule: §Posts (AttachCharacter); CH11
    private OrderResult HandleAttach(StepContext ctx, AttachCharacterOrder o)
    {
        if (!Known(o.Character, out var c)) return OrderResult.Reject(CharacterReject.UnknownCharacter);
        if (!State.Fleets.TryGetValue(o.Fleet, out var f)) return OrderResult.Reject(RejectReason.UnknownFleet);
        if (c.Side != o.Side || f.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (c.Status != CharacterStatus.Free) return OrderResult.Reject(CharacterReject.NotFree);
        if (Moving(f)) return OrderResult.Reject(CharacterReject.FleetInTransit);
        if (c.Transit != null || LocationOf(c) != f.At) return OrderResult.Reject(CharacterReject.NotAtSystem);
        if (c.AttachedFleet != 0) return OrderResult.Reject(CharacterReject.AlreadyAttached);
        if (IsOnMission(c.Id)) return OrderResult.Reject(CharacterReject.OnMission);
        if (HeldSystemPost(c) != null) return OrderResult.Reject(CharacterReject.HoldsSystemPost);
        if (!ClassOf(c).Can(o.Post)) return OrderResult.Reject(CharacterReject.NoCapability);
        if (o.Post != CharacterPost.None && PostHolder(f.Id, o.Post) != null) return OrderResult.Reject(CharacterReject.PostTaken);

        c.AttachedFleet = f.Id; c.Post = o.Post; c.System = f.At;
        int day = ctx.Day, fid = f.Id; var post = o.Post;
        ctx.Emit((i, _) => new CharacterAttached(i, day, c.Id, c.Side, fid, post, CharacterCause.Order));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Posts (DetachCharacter); CH34
    private OrderResult HandleDetach(StepContext ctx, DetachCharacterOrder o)
    {
        if (!Known(o.Character, out var c)) return OrderResult.Reject(CharacterReject.UnknownCharacter);
        if (c.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (c.AttachedFleet == 0) return OrderResult.Reject(CharacterReject.NotAttached);
        if (State.Fleets.TryGetValue(c.AttachedFleet, out var f) && Moving(f)) return OrderResult.Reject(CharacterReject.FleetInTransit);
        Detach(ctx, ctx.Day, c, CharacterCause.Order);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Posts (AssignSystemGeneral)
    private OrderResult HandleAssign(StepContext ctx, AssignSystemGeneralOrder o)
    {
        if (!Known(o.Character, out var c)) return OrderResult.Reject(CharacterReject.UnknownCharacter);
        if (!_fleets.Galaxy.TryFindSystem(o.System, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        if (c.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (c.Status != CharacterStatus.Free) return OrderResult.Reject(CharacterReject.NotFree);
        if (c.Transit != null || LocationOf(c) != o.System) return OrderResult.Reject(CharacterReject.NotAtSystem);
        if (c.AttachedFleet != 0) return OrderResult.Reject(CharacterReject.AlreadyAttached);
        if (IsOnMission(c.Id)) return OrderResult.Reject(CharacterReject.OnMission);
        if (!ClassOf(c).CanGeneral) return OrderResult.Reject(CharacterReject.NoCapability);
        if (!State.SystemOwners.TryGetValue(o.System, out var holder) || holder != c.Side) return OrderResult.Reject(RejectReason.NotYourSystem);
        if (S.SystemGenerals.ContainsKey(o.System)) return OrderResult.Reject(CharacterReject.PostTaken);
        if (HeldSystemPost(c) != null) return OrderResult.Reject(CharacterReject.AlreadyHoldsSystemPost);

        S.SystemGenerals[o.System] = c.Id;
        int day = ctx.Day; string sys = o.System;
        ctx.Emit((i, _) => new SystemGeneralAssigned(i, day, c.Id, c.Side, sys));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Posts (ReleaseSystemGeneral)
    private OrderResult HandleRelease(StepContext ctx, ReleaseSystemGeneralOrder o)
    {
        if (!S.SystemGenerals.TryGetValue(o.System, out var id) || Get(id) is not { } c) return OrderResult.Reject(CharacterReject.NoSystemGeneral);
        if (c.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        S.SystemGenerals.Remove(o.System);
        int day = ctx.Day;
        ctx.Emit((i, _) => new PostVacated(i, day, c.Id, c.Side, PostKind.SystemGeneral, CharacterCause.Order));
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Moving a character alone (MoveCharacter); m1-travel path rule and per-hop formula at speed 100; CH13
    private OrderResult HandleMove(StepContext ctx, MoveCharacterOrder o)
    {
        if (!Known(o.Character, out var c)) return OrderResult.Reject(CharacterReject.UnknownCharacter);
        if (c.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (c.Status is not (CharacterStatus.Free or CharacterStatus.Injured)) return OrderResult.Reject(CharacterReject.NotFree); // an injured character may move (DEFAULT)
        if (c.AttachedFleet != 0) return OrderResult.Reject(CharacterReject.Attached);
        if (IsOnMission(c.Id)) return OrderResult.Reject(CharacterReject.OnMission);
        if (c.Transit != null || c.System == null) return OrderResult.Reject(RejectReason.InTransit);
        if (c.System == o.System) return OrderResult.Reject(RejectReason.AlreadyThere);
        var path = _fleets.FindPath(c.System, o.System, Params.CharacterSpeed);
        if (!path.Ok) return OrderResult.Reject(path.Status == Travel.PathStatus.AlreadyThere ? RejectReason.AlreadyThere : CharacterReject.NoPath);

        var names = path.Names(_fleets.Galaxy).ToList();
        c.Transit = new FleetTransit { Path = names, HopTicks = path.HopTicks.ToList(), HopIndex = 0, DueDay = ctx.Day + path.HopTicks[0] };
        int total = path.TotalTicks, arrival = ctx.Day + path.TotalTicks;
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, null, new ValueList<string>(names), total, arrival));
    }

    // ================================================================== slots 2, 3, 6.10

    // rule: §Where it runs (slot 2: departures of characters, after the fleets, character id)
    private void Departures(StepContext ctx)
    {
        foreach (var c in S.Characters.Values)
        {
            if (c.Transit == null || c.System == null) continue;
            string from = c.System; int first = c.Transit.HopTicks[0];
            c.System = null;
            ctx.Emit((i, d) => new CharacterDeparted(i, d, c.Id, c.Side, from, first));
        }
    }

    // rule: §Where it runs (slot 3: arrivals, after the fleets, character id). A lone character is never stopped on the way
    // (CharactersStoppedInTransit off): at a passing system its next hop starts at once.
    private void Arrivals(StepContext ctx)
    {
        int day = ctx.Day + 1;
        foreach (var c in S.Characters.Values)
        {
            if (c.Transit is not { } t || c.System != null || t.DueDay != day) continue;
            string sys = t.Path[t.HopIndex + 1];
            if (t.HopIndex + 1 == t.Path.Count - 1)
            {
                c.Transit = null; c.System = sys;
                ctx.Emit((i, _) => new CharacterArrived(i, day, c.Id, c.Side, sys));
                // QA-9 (B2e §3 Autoroute step 4): he joins the fleet he set off for if it is still there (a rider, no post)
                if (c.JoinFleet is int jf and not 0 && State.Fleets.TryGetValue(jf, out var jfl) && jfl.At == sys && jfl.Side == c.Side
                    && c.Status is CharacterStatus.Free or CharacterStatus.Injured)
                {
                    c.AttachedFleet = jf; c.Post = CharacterPost.None;
                    ctx.Emit((i, _) => new CharacterAttached(i, day, c.Id, c.Side, jf, CharacterPost.None, CharacterCause.Autoroute));
                }
                c.JoinFleet = 0;
                continue;
            }
            t.HopIndex++;
            t.DueDay = day + t.HopTicks[t.HopIndex];
        }
        RefreshSeatOfPower(); // rule: §Seat of Power (B2e §6: set when a character arrives somewhere, 0x561470)
    }

    /// <summary>
    /// Slot 6.10, the character timers (B2e §7), each in character id order: healing (no draws), escape attempts (the attempt draw, then the next
    /// delay draw), Talent awakening (Talent roll of each awakened character), then the Seat of Power. A timer acts only the day after its
    /// flag turned on (DEFAULT day alignment) and only while the flag stays on; a spread of 0 makes no draw. rule: §Where it runs (6.10).
    /// </summary>
    private void DayEnd(StepContext ctx)
    {
        int k = ctx.Day;
        foreach (var c in S.Characters.Values.Where(c => c.Placed && c.Status != CharacterStatus.Dead).ToList()) HealTick(ctx, c, k);
        if (Params.LandPrisoners)
            foreach (var c in S.Characters.Values.Where(c => c.Status == CharacterStatus.Captured && c.HeldAboard != 0).ToList()) LandTick(ctx, c);
        if (Params.CaptiveEscape)
            foreach (var c in S.Characters.Values.Where(c => c.Status == CharacterStatus.Captured && c.NextEscapeDay != 0 && k >= c.NextEscapeDay).ToList())
            {
                EscapeAttempt(ctx, c);
                int delay = EscapeDelay(ctx.Rng); // the next delay, drawn right after the attempt (B2e §3, Work 11 item 4)
                c.NextEscapeDay = c.Status == CharacterStatus.Captured ? k + delay : 0;
            }
        if (Params.TalentDiscovery == TalentDiscoveryMode.Trainer)
            foreach (var t in S.Characters.Values.Where(c => c.Placed).ToList()) AwakenTick(ctx, t, k);
        // QA-9 (B2e §3): a character who waited for a pending battle is routed again (DEFAULT: at slot 6.10 of each later day)
        foreach (var c in S.Characters.Values.Where(c => c.AutoroutePending).ToList()) Autoroute(ctx, c);
        RefreshSeatOfPower();
    }

    /// <summary>
    /// Prisoners land (New, Paul 2026-10-18): a prisoner held aboard a fleet that stands at a system (not travelling) held by his captor's side and not
    /// blockaded goes ashore there: held on that planet, no draw, escape timer unchanged, event PrisonerLanded. Runs at slot 6.10 before the escape
    /// attempts, in character id order (DEFAULT timing). Off (Classic): never. rule: m5-characters-and-talent §Capture, injury, death, freeing 7.
    /// </summary>
    private void LandTick(StepContext ctx, CharacterRecord c)
    {
        if (c.Captor is not Side captor || !State.Fleets.TryGetValue(c.HeldAboard, out var f) || f.Transit != null || f.At is not { } at) return;
        if (_fleets.OwnerOf(at) != captor || State.Blockades.ContainsKey(at)) return;
        int fleet = f.Id, id = c.Id, day = ctx.Day + 1; var side = c.Side;
        c.HeldAboard = 0; c.HeldAboardShip = 0; c.HeldAt = at; c.System = at;
        ctx.Emit((i, _) => new PrisonerLanded(i, day, id, side, captor, at, fleet));
    }

    /// <summary>
    /// Healing (+0xac): CanHeal = the character's place belongs to his side (his fleet when he rides one, else the system's holder), not captured,
    /// not dead; Healing = CanHeal, not travelling, Injury &gt; 0. While Healing, every GNPRTB 2561 / 2562 (1) days: Injury -= 2 with FastHeal
    /// (Talent &gt;= GNPRTB 2560 = 80; GNPRTB 2563) else 1 (2564), not below 0; at 0 he is healed. rule: §Capture, injury (Traced B2e §2, 0x4ef030, 0x4ef100).
    /// </summary>
    private void HealTick(StepContext ctx, CharacterRecord c, int k)
    {
        bool fast = c.EffectiveTalent >= Params.FastHealMinTalent;
        if (!(c.Injury > 0 && CanHeal(c))) { c.NextHealDay = 0; return; }
        if (c.NextHealDay == 0) { c.NextHealDay = k + (fast ? Params.HealPeriodFast : Params.HealPeriod); return; } // the flag turned on
        if (k < c.NextHealDay) return;
        c.Injury = Math.Max(0, c.Injury - (fast ? Params.HealFast : Params.HealSlow));
        if (c.Injury == 0) Heal(ctx, c);
        else c.NextHealDay = k + (fast ? Params.HealPeriodFast : Params.HealPeriod);
    }

    private bool CanHeal(CharacterRecord c)
    {
        if (c.Status is CharacterStatus.Captured or CharacterStatus.Dead) return false;
        if (c.AttachedFleet != 0)
            return State.Fleets.TryGetValue(c.AttachedFleet, out var f) && f.At != null && f.Transit == null && f.Side == c.Side;
        return c.Transit == null && c.System != null && _fleets.OwnerOf(c.System) == c.Side;
    }

    /// <summary>
    /// One escape attempt (0x561990): avg = total effective combat of the captor's characters directly on the place (riding no fleet, not
    /// travelling, not on a mission, not captured, alive) / their number (0 if none); sf = the captor's special forces directly on the place (not
    /// aboard a ship, not on a mission). Held aboard a ship that caught him with no commander (Q73, B2e §3): his place is that ship, so avg = 0
    /// (no character stands directly on a ship) and sf = the captor's special forces aboard it; once that ship has left his fleet (a transfer;
    /// DEFAULT, not read) the fleet counts. x = combat + espionage - avg - sf (the prisoner's effective values); one draw, escapes if
    /// rand(0..99) &lt; ESCAPETB(x), the common step lookup. On success (still captured, not on a mission): Escape. rule: §Escape (Traced B2e §3).
    /// </summary>
    private void EscapeAttempt(StepContext ctx, CharacterRecord c)
    {
        if (c.Captor is not Side captor || StandingPlace(c) is not { } place || (c.HeldAt == null && c.HeldAboard == 0)) return; // a team captive has no place
        int fleet = c.HeldAboard;
        // rule: §Escape (B2e §3 "A prisoner held aboard stays aboard", Q73): caught by a ship with no commander, his place is that ship
        int ship = fleet != 0 && c.HeldAboardShip != 0 && State.Fleets.TryGetValue(fleet, out var hf) && hf.Ships.Contains(c.HeldAboardShip) ? c.HeldAboardShip : 0;
        // QA-7 DEFAULT (B2e Work 12): held aboard by a commander, avg and sf count the captor's characters and special forces attached to that fleet
        var guards = ship != 0 ? new List<CharacterRecord>() : S.Characters.Values.Where(g => g.Placed && g.Side == captor && g.Status is CharacterStatus.Free or CharacterStatus.Injured
            && !IsOnMission(g.Id) && (fleet != 0 ? g.AttachedFleet == fleet : g.AttachedFleet == 0 && g.Transit == null && g.System == place)).ToList();
        int avg = guards.Count == 0 ? 0 : guards.Sum(g => EffectiveSkill(g, Skill.Combat)) / guards.Count;
        int sf = ship != 0 ? SpecialForcesOnShip(captor, ship) : fleet != 0 ? SpecialForcesInFleet(captor, fleet) : SpecialForcesOnPlanet(captor, place);
        int x = EffectiveSkill(c, Skill.Combat) + EffectiveSkill(c, Skill.Espionage) - avg - sf;
        int p = EscapePercent(x);
        int r = ctx.Rng.NextInt(0, PercentDieMax);
        bool ok = r < p;
        int id = c.Id, day = ctx.Day + 1; var side = c.Side;
        ctx.Emit((i, _) => new EscapeAttempted(i, day, id, side, place, avg, sf, x, p, r, ok));
        if (ok && !IsOnMission(c.Id)) Escape(ctx, c);
    }

    /// <summary>The captor's usable special forces directly on the planet (0x504cc0); GameHost sets it (the troop and mission modules know them). 0 when unset.</summary>
    public Func<Side, string, int> SpecialForcesOnPlanet { get; set; } = (_, _) => 0;

    /// <summary>
    /// The awakening timer of a trainer (DiscoveringTalentHolder = the record's trainer, Talent &gt;= GNPRTB 2580 = 80, not on a mission, alive,
    /// not captured; 0x4ef070), firing every GNPRTB 2582 (1) days: each character of the trainer's side directly on the trainer's place (the same
    /// fleet when he rides one), alive, not captured, not on a mission, not the trainer, with TalentPotential and not TalentAware, in id order: Ledger
    /// only when the trainer is Nobody and Nobody knows his heritage (she then learns hers); then aware, awakened by the trainer, Talent rolled. No draw
    /// but Talent roll. rule: §Talent (Traced B2e §5, 0x560a30).
    /// </summary>
    private void AwakenTick(StepContext ctx, CharacterRecord t, int k)
    {
        bool on = IsTrainer(t) && t.EffectiveTalent >= Params.TrainerMinTalent && !IsOnMission(t.Id)
            && t.Status is CharacterStatus.Free or CharacterStatus.Injured;
        if (!on) { t.NextAwakenDay = 0; return; }
        int Delay() => Params.AwakenPeriod + (Params.AwakenSpread > 0 ? ctx.Rng.NextInt(0, Params.AwakenSpread) : 0);
        if (t.NextAwakenDay == 0) { t.NextAwakenDay = k + Delay(); return; }
        if (k < t.NextAwakenDay) return;
        t.NextAwakenDay = k + Delay();
        int day = k + 1;
        foreach (var y in S.Characters.Values.Where(y => y.Placed && y.Id != t.Id && y.Side == t.Side
            && y.Status is CharacterStatus.Free or CharacterStatus.Injured && !IsOnMission(y.Id) && y.TalentHolder && !y.TalentDiscovered
            && (t.AttachedFleet != 0 ? y.AttachedFleet == t.AttachedFleet : y.AttachedFleet == 0 && y.Transit == null && t.Transit == null && t.System != null && y.System == t.System)).ToList())
        {
            if (y.Id == Params.Ledger)
            {
                if (t.Id != Params.Nobody || !t.KnowsHeritage) continue;
                if (!y.KnowsHeritage) { y.KnowsHeritage = true; Heritage(ctx, day, y); } // 0x56fe90
            }
            var before = RankOf(y);
            y.TalentDiscovered = true; y.AwakenedBy = t.Id;
            RollTalentExperience(y, ctx.Rng);
            int yid = y.Id; var ys = y.Side;
            ctx.Emit((i, _) => new TalentDiscovered(i, day, yid, ys, CharacterCause.Awakened));
            RankEvent(ctx, day, y, before);
        }
    }

    /// <summary>
    /// The Seat of Power (0x5617b0, 0x5618d0): on while The Chair is placed, alive, not captured, stands (riding no fleet, not travelling) on the
    /// Seat of Power system and the Hand holds it, and <see cref="CharacterParams.SeatOfPower"/>. Every Hand-only character's
    /// LeadershipAdjustment is then GNPRTB 3076 (50), else 0. No draws. Recomputed at setup, arrivals, captures, freeings, deaths, holder changes
    /// and day-end. rule: §Seat of Power (Traced B2e §6; Charter inferred).
    /// </summary>
    public void RefreshSeatOfPower()
    {
        bool on = SeatOfPowerOn;
        foreach (var c in S.Characters.Values)
            c.LeadershipAdjustment = on && Data.TryGet(c.Id, out var cls) && cls.Side == Side.Hand ? Params.SeatOfPowerAdjustment : 0;
    }

    public bool SeatOfPowerOn =>
        Params.SeatOfPower && Get(Params.Chair) is { Placed: true } e && e.Status is CharacterStatus.Free or CharacterStatus.Injured
        && e.AttachedFleet == 0 && e.Transit == null && e.System == Params.SeatOfPowerSystem && _fleets.OwnerOf(Params.SeatOfPowerSystem) == Side.Hand;

    /// <summary>
    /// ESCAPETB lookup: the percent of the largest table point at or below the input; below the first point, the first point's value (the
    /// common step lookup, B2a). No table: refused RuleMissing. rule: §Escape (B2e §3 step 5); CH25.
    /// </summary>
    public int EscapePercent(int input)
    {
        var t = EscapeTable ?? throw new RuleMissingException("ESCAPETB");
        int pct = t[0].Percent;
        foreach (var (inp, p) in t) if (input >= inp) pct = p;
        return pct;
    }
}

/// <summary>
/// The captor unit of a capture (B2e §3 "Where a new prisoner is held", 0x546370; QA-7): the fleet it is in (a capital ship's fleet, or the fleet a
/// captor character, squadron or regiment rides or is aboard; 0 = none), else the planet it stands on; <see cref="Mission"/> = the mission a captor
/// character is on (0 = none); <see cref="MissionInvolved"/> = the prisoner or the captor is on a mission (a duel prisoner always was a team member,
/// even when his Hurt roll already made him resign; B2e §3 step 2).
/// </summary>
/// <see cref="Ship"/> = the captor ship when a ship with no commander caught him (it, or the ship a captor unit is aboard; 0 = none, Q73).
public readonly record struct CaptorUnit(int Fleet, string? System, int Mission = 0, bool MissionInvolved = false, int Ship = 0);
