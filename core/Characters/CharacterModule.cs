using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Characters;

/// <summary>
/// Characters: the roster rolled at creation, posts (fleet Admiral, Commander, General; system General) and the three leadership hooks, riding a
/// fleet and moving alone, the New-rule Navigator, the state machine (free, injured, captured, dead) with its six functions, the Death Star's
/// roll on characters, uprising incident effects 3 to 5, the Force score, rank and the Jedi Training roll. Holds data and settings only; the state
/// lives in <see cref="GameState.Characters"/>. After <see cref="GameSave.Load"/>, attach again. Every loop is in ascending character id
/// (m1-clock rule 5). rule: m5-characters-and-force (module CharacterModule, core/Characters/).
///
/// Missing rules are not invented: loyalty and betrayal, Force detection, Force and skill growth, the Seat of Power, injury healing time, prisoner
/// escape inputs, assault harm and blockade escape risk are switches whose "on" is refused (<see cref="CharacterParams.Validate"/>).
/// </summary>
public sealed class CharacterModule
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
        if (c.Status == CharacterStatus.Captured) return c.HeldAt;
        if (c.AttachedFleet != 0) return State.Fleets.TryGetValue(c.AttachedFleet, out var f) ? f.At : null;
        return c.System;
    }

    private string? HeldSystemPost(CharacterRecord c) => S.SystemGenerals.FirstOrDefault(kv => kv.Value == c.Id).Key;

    /// <summary>Free, placed, not attached, not a system General, not on a mission. m5-missions must call it. rule: §Availability for missions.</summary>
    public bool IsAvailable(CharacterRecord c) =>
        c.Placed && c.Status == CharacterStatus.Free && c.AttachedFleet == 0 && HeldSystemPost(c) == null && !IsOnMission(c.Id);

    // ---- leadership hooks. rule: §Posts (the three hooks, exact): the effective leadership of the post's Free holder, or 0; no draws.

    /// <summary>Skill leadership + SeatOfPowerBonus (always 0: Missing, switch refused). rule: §Posts (the three hooks).</summary>
    public int EffectiveLeadership(CharacterRecord c) => c.Skill(Skill.Leadership) + 0;

    private CharacterRecord? PostHolder(int fleetId, CharacterPost post) =>
        S.Characters.Values.FirstOrDefault(c => c.AttachedFleet == fleetId && c.Post == post && post != CharacterPost.None);

    private int HolderLeadership(int fleetId, CharacterPost post) =>
        PostHolder(fleetId, post) is { Status: CharacterStatus.Free } c ? EffectiveLeadership(c) : 0;

    /// <summary>Bombardment multiplier input. rule: §Posts (Admiral; m3-bombardment L / 40 + 1).</summary>
    public int AdmiralLeadership(Fleet f) => HolderLeadership(f.Id, CharacterPost.Admiral);
    /// <summary>Landing regiments' bonus L / 30. rule: §Posts (General of a fleet; m3-assault Resolution 3).</summary>
    public int FleetGeneralLeadership(Fleet f) => HolderLeadership(f.Id, CharacterPost.General);
    /// <summary>Nothing reads it in the strategic game (DEFAULT no effect; M4 may). rule: §Posts (Commander).</summary>
    public int CommanderLeadership(Fleet f) => HolderLeadership(f.Id, CharacterPost.Commander);
    /// <summary>Defenders' bonus L / 30. rule: §Posts (General of a system).</summary>
    public int SystemGeneralLeadership(string system) =>
        S.SystemGenerals.TryGetValue(system, out var id) && Get(id) is { Status: CharacterStatus.Free } c ? EffectiveLeadership(c) : 0;

    /// <summary>The fleet post holder (any status), or null.</summary>
    public CharacterRecord? HolderOf(int fleetId, CharacterPost post) => PostHolder(fleetId, post);
    public CharacterRecord? SystemGeneralOf(string system) => S.SystemGenerals.TryGetValue(system, out var id) ? Get(id) : null;
    public IEnumerable<CharacterRecord> AttachedTo(int fleetId) => S.Characters.Values.Where(c => c.AttachedFleet == fleetId);

    // ---- Force. rule: §Force

    /// <summary>Rank from the effective Force (null = not a Force user). rule: §Force (Rank), CH7.</summary>
    public ForceRank? RankOf(CharacterRecord c) => RankFor(c.ForceUser, c.EffectiveForce);

    public ForceRank? RankFor(bool forceUser, int effectiveForce)
    {
        if (!forceUser) return null;
        if (effectiveForce >= Params.ForceMasterMin) return ForceRank.Master;
        if (effectiveForce >= Params.ForceKnightMin) return ForceRank.Knight;
        if (effectiveForce >= Params.ForceStudentMin) return ForceRank.Student;
        return ForceRank.Trainee;
    }

    /// <summary>
    /// The eligibility the Jedi Training mission checks: the trainer has the trainer flag, is Free and available (and a Knight with
    /// <see cref="CharacterParams.TrainerNeedsKnight"/>); the student is a discovered Force user, Free, same side, at the trainer's system.
    /// rule: §Force (Eligibility).
    /// </summary>
    public bool CanTrain(CharacterRecord trainer, CharacterRecord student)
    {
        if (!ClassOf(trainer).JediTrainer || !IsAvailable(trainer)) return false;
        if (Params.TrainerNeedsKnight && trainer.EffectiveForce < Params.ForceKnightMin) return false;
        return student.Id != trainer.Id && student.Placed && student.ForceUser && student.ForceDiscovered && student.Status == CharacterStatus.Free
            && student.Side == trainer.Side && LocationOf(student) != null && LocationOf(student) == LocationOf(trainer);
    }

    // ================================================================== creation and setup (outside a Step)

    /// <summary>
    /// Roll one character from its record with the game generator, in the fixed draw order: the seven skills (each only if its variance is above
    /// 0), loyalty (same), the awakening (no draw for known Jedi, probability 0 or 100+), then the Force level (draw only if its variance is above
    /// 0). No clamp. The character is created unplaced. rule: §Rolling the stats (Traced for Force, DEFAULT for skills), CH1, CH2.
    /// </summary>
    public CharacterRecord Create(CharacterClass cls, GameRng rng)
    {
        var c = new CharacterRecord { Id = cls.Id, Side = cls.Side };
        for (int i = 0; i < CharacterClass.SkillCount; i++)
            c.Skills.Add(cls.SkillBase[i] + (cls.SkillVariance[i] > 0 ? rng.NextInt(0, cls.SkillVariance[i]) : 0));
        c.Loyalty = cls.LoyaltyBase + (cls.LoyaltyVariance > 0 ? rng.NextInt(0, cls.LoyaltyVariance) : 0);
        bool user;
        if (cls.KnownJedi) user = true;
        else if (cls.ForceProbability <= 0) user = false;
        else if (cls.ForceProbability >= 100) user = true;
        else user = rng.NextInt(0, 99) < cls.ForceProbability; // flagging roll's form [inferred]
        c.ForceUser = user;
        c.ForceBase = user ? cls.ForceLevelBase + (cls.ForceLevelVariance > 0 ? rng.NextInt(0, cls.ForceLevelVariance) : 0) : 0; // Traced 0x4eee60
        c.ForceAcquired = 0;
        c.ForceDiscovered = cls.KnownJedi; // Original text: Luke, Vader and the Emperor are public Force users
        S.Characters[c.Id] = c;
        return c;
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

    /// <summary>Setup or tests: make a character General of a system without the order's checks (raises nothing).</summary>
    public void SetSystemGeneral(int id, string system) => S.SystemGenerals[system] = id;

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
                case StartRule.FixedHq: Place(row.Character, hqSystem ?? throw new InvalidOperationException("Setup: no Rebel HQ system for the FixedHq character.")); break;
                case StartRule.RandomLocation:
                    if (row.Officer && Params.StartCastOfficers == StartCastOfficers.None) break; // the officer stays in the pool
                    PlaceAt(row.Character, RandomLocation(Data.Get(row.Character).Side, rng));
                    break;
                case StartRule.Extras:
                    var side = row.Place == "Empire" ? Side.Empire : row.Place == "Alliance" ? Side.Alliance : throw new InvalidOperationException($"start-cast: extras side '{row.Place}'.");
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
    }

    private void PlaceAt(int id, (string? System, int Fleet) loc)
    {
        if (loc.System != null) Place(id, loc.System); else PlaceInFleet(id, loc.Fleet);
    }

    /// <summary>
    /// Traced 0x562710: S = systems held by the side (ordinal), F = its fleets with at least one capital ship (fleet id); r = rand(0..F+S-1); a
    /// system, or a fleet and then j = rand(0..ships-1) for the ship aboard (drawn so the sequence matches; only the fleet is stored).
    /// rule: m5-setup-and-victory §P9 (RandomLocation).
    /// </summary>
    public (string? System, int Fleet) RandomLocation(Side side, GameRng rng)
    {
        var systems = State.SystemOwners.Where(kv => kv.Value == side).Select(kv => kv.Key).ToList(); // SortedDictionary: ordinal
        var fleets = State.Fleets.Values.Where(f => f.Side == side && f.Ships.Count > 0).ToList(); // fleet id order
        int n = systems.Count + fleets.Count;
        if (n == 0) throw new InvalidOperationException($"Setup: {side} holds no system and no fleet for RandomLocation.");
        int r = rng.NextInt(0, n - 1);
        if (r < systems.Count) return (systems[r], 0);
        var f = fleets[r - systems.Count];
        rng.NextInt(0, f.Ships.Count - 1);
        return (null, f.Id);
    }

    // ================================================================== the state machine (§Capture, injury, death, freeing)

    // Every post the character holds ends (fleet post, system General), then it is detached; events PostVacated and CharacterDetached.
    private void VacateAll(StepContext? ctx, int day, CharacterRecord c, string cause)
    {
        if (HeldSystemPost(c) is { } sys)
        {
            S.SystemGenerals.Remove(sys);
            Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, PostKind.SystemGeneral, cause));
        }
        Detach(ctx, day, c, cause);
    }

    private void Detach(StepContext? ctx, int day, CharacterRecord c, string cause)
    {
        if (c.AttachedFleet == 0) return;
        string? sys = State.Fleets.TryGetValue(c.AttachedFleet, out var f) ? PlaceOfFleet(f) : c.System;
        if (c.Post != CharacterPost.None)
        {
            var post = (PostKind)((int)c.Post - 1);
            Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, post, cause));
            c.Post = CharacterPost.None;
        }
        int fid = c.AttachedFleet;
        c.AttachedFleet = 0; c.System = sys;
        Emit(ctx, (i, _) => new CharacterDetached(i, day, c.Id, c.Side, fid, sys, cause));
    }

    private static void Emit(StepContext? ctx, Func<int, int, GameEvent> make) => ctx?.Emit(make);

    /// <summary>Where the character stands now for the state functions (a rider in transit: the system its fleet last left; a lone traveller: the system it last left).</summary>
    private string? StandingPlace(CharacterRecord c)
    {
        if (c.Status == CharacterStatus.Captured) return c.HeldAt;
        if (c.AttachedFleet != 0 && State.Fleets.TryGetValue(c.AttachedFleet, out var f)) return PlaceOfFleet(f);
        return c.System ?? (c.Transit is { } t ? t.Path[t.HopIndex] : null);
    }

    /// <summary>
    /// Only a Free character: status Injured for <see cref="CharacterParams.InjuryDays"/>, every post ends and it is detached (it stays at that
    /// system). Event CharacterInjured. No draws. rule: §Capture, injury, death, freeing 1; CH22.
    /// </summary>
    public bool Injure(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (c.Status != CharacterStatus.Free || !c.Placed) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        string? sys = StandingPlace(c);
        c.Status = CharacterStatus.Injured;
        c.InjuredDaysLeft = Params.InjuryDays;
        Emit(ctx, (i, _) => new CharacterInjured(i, day, c.Id, c.Side, sys, cause));
        VacateAll(ctx, day, c, CharacterCause.Injured);
        return true;
    }

    /// <summary>
    /// Only a Free or Injured character of the other side: status Captured, held at <paramref name="heldAt"/> (DEFAULT: where it stood), posts end,
    /// detached, a trip ends, mission membership ends. Event CharacterCaptured. rule: §Capture, injury, death, freeing 2; CH21.
    /// </summary>
    public bool Capture(StepContext? ctx, CharacterRecord c, Side captor, string? heldAt, string cause)
    {
        if (!c.Placed || c.Status is not (CharacterStatus.Free or CharacterStatus.Injured) || captor == c.Side) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        string? at = heldAt ?? StandingPlace(c);
        VacateAll(ctx, day, c, CharacterCause.Captured);
        c.Transit = null;
        c.Status = CharacterStatus.Captured; c.Captor = captor; c.HeldAt = at; c.System = at; c.InjuredDaysLeft = 0;
        MissionMembershipEnded?.Invoke(ctx, c);
        Emit(ctx, (i, _) => new CharacterCaptured(i, day, c.Id, c.Side, captor, at, cause));
        return true;
    }

    /// <summary>
    /// Not if already Dead. A major with <see cref="CharacterParams.MajorsCanDie"/> off (DEFAULT), a minor with <see cref="CharacterParams.MinorsCanDie"/>
    /// off, or a death refused by <see cref="DeathRefused"/>: the call becomes <see cref="Injure"/>. Else status Dead (never removed), posts end,
    /// detached, a trip ends, mission membership ends, captor cleared. Event CharacterKilled. rule: §Capture, injury, death, freeing 3; CH20.
    /// </summary>
    public bool Kill(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (!c.Placed || c.Status == CharacterStatus.Dead) return false;
        bool canDie = ClassOf(c).Kind == CharacterKind.Major ? Params.MajorsCanDie : Params.MinorsCanDie;
        if (!canDie || DeathRefused?.Invoke(c) == true) return Injure(ctx, c, cause);
        int day = (ctx?.Day ?? _game.Day) + 1;
        string? at = StandingPlace(c);
        VacateAll(ctx, day, c, CharacterCause.Killed);
        c.Transit = null;
        c.Status = CharacterStatus.Dead; c.Captor = null; c.HeldAt = null; c.System = at; c.InjuredDaysLeft = 0;
        MissionMembershipEnded?.Invoke(ctx, c);
        Emit(ctx, (i, _) => new CharacterKilled(i, day, c.Id, c.Side, at, cause));
        return true;
    }

    /// <summary>A prisoner freed (Rescue, an uprising, escape): status Free at held_at, captor cleared. Event CharacterFreed (CharacterEscaped for cause Escape). rule: §4, §6.</summary>
    public bool Free(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (c.Status != CharacterStatus.Captured) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        string? at = c.HeldAt;
        c.Status = CharacterStatus.Free; c.Captor = null; c.System = at; c.HeldAt = null;
        if (cause == CharacterCause.Escape) Emit(ctx, (i, _) => new CharacterEscaped(i, day, c.Id, c.Side, at)); // Original text "has escaped from imprisonment at"
        else Emit(ctx, (i, _) => new CharacterFreed(i, day, c.Id, c.Side, at, cause));
        return true;
    }

    /// <summary>Escape = Free with cause Escape. rule: §Capture, injury, death, freeing 6.</summary>
    public bool Escape(StepContext? ctx, CharacterRecord c) => Free(ctx, c, CharacterCause.Escape);

    /// <summary>An injury ends: status Free. Event CharacterHealed (Original text "has been healed of all injuries"). rule: §5.</summary>
    public bool Heal(StepContext? ctx, CharacterRecord c)
    {
        if (c.Status != CharacterStatus.Injured) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        c.Status = CharacterStatus.Free; c.InjuredDaysLeft = 0;
        Emit(ctx, (i, _) => new CharacterHealed(i, day, c.Id, c.Side));
        return true;
    }

    // ================================================================== other sheets' effects

    /// <summary>
    /// The Death Star's roll on the characters of a destroyed system (called by the Death Star order's resolution at slot 5.1, which is not built
    /// yet): candidates are minors located there (standing, riding, held), Free, Injured or Captured, both sides with
    /// <see cref="CharacterParams.DeathStarHitsOwnCharacters"/> (else not the attacker's); in id order draw r = rand(0..99), hit if r &lt; 50:
    /// Injure then Kill (a captive is killed without the injury). Majors cost no draw. Then the system's General post is cleared.
    /// rule: §The Death Star's roll on characters; CH19.
    /// </summary>
    public void DeathStarStrike(StepContext ctx, string system, Side attacker)
    {
        var cands = S.Characters.Values.Where(c => c.Placed && ClassOf(c).Kind == CharacterKind.Minor
            && c.Status is CharacterStatus.Free or CharacterStatus.Injured or CharacterStatus.Captured
            && LocationOf(c) == system && (Params.DeathStarHitsOwnCharacters || c.Side != attacker)).ToList();
        foreach (var c in cands)
        {
            int r = ctx.Rng.NextInt(0, 99);
            if (r >= Params.DeathStarHitPercent) continue;
            if (c.Status != CharacterStatus.Captured) Injure(ctx, c, CharacterCause.DeathStar);
            Kill(ctx, c, CharacterCause.DeathStar); // reason 10
        }
        if (S.SystemGenerals.TryGetValue(system, out var gid) && Get(gid) is { } g)
        {
            S.SystemGenerals.Remove(system);
            int day = ctx.Day + 1;
            ctx.Emit((i, _) => new PostVacated(i, day, g.Id, g.Side, PostKind.SystemGeneral, CharacterCause.SystemDestroyed));
        }
    }

    /// <summary>
    /// Uprising incident effects (m2-uprisings-and-garrison, slot 6.3): code 3 = Injure one of the holder's Free characters located at the system
    /// (attached ones count), k = rand(0..n-1), none and no draw if n = 0; code 4 = Free one prisoner held there by the holder, k = rand(0..n-1);
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
        if (!S.SystemGenerals.TryGetValue(info.System, out var id) || Get(id) is not { } c || c.Side == info.New) return;
        S.SystemGenerals.Remove(info.System);
        int day = info.Ctx.Day + 1;
        info.Ctx.Emit((i, _) => new PostVacated(i, day, c.Id, c.Side, PostKind.SystemGeneral, CharacterCause.HolderChanged));
    }

    // ================================================================== Force (§Force)

    /// <summary>
    /// One Jedi Training round (the mission calls it at the end of its timer): for each student in id order, gap = trainer - student (effective);
    /// draw r = rand(0..99) always; success if gap &gt; 0 and r &lt; min(gap, 100); on success draw g = rand(0..gap * 50 / 100) and add it to the
    /// student's acquired Force. Events ForceTrainingResult, then ForceRankChanged if the rank changed. No student reaches the trainer.
    /// rule: §Force (Jedi Training: the roll), Traced 0x55c7c0, 0x53e2f0, 0x55ca20 (chance reading [inferred]); CH3 to CH6.
    /// </summary>
    public void ForceTrainingRound(StepContext ctx, CharacterRecord trainer, IEnumerable<CharacterRecord> students)
    {
        int day = ctx.Day + 1;
        foreach (var s in students.OrderBy(x => x.Id).ToList())
        {
            var before = RankOf(s);
            int gap = trainer.EffectiveForce - s.EffectiveForce;
            int r = ctx.Rng.NextInt(0, 99);
            bool success = gap > 0 && r < Math.Min(gap, Params.TrainingChanceCap);
            int gain = 0;
            if (success)
            {
                gain = ctx.Rng.NextInt(0, gap * Params.TrainingGainPercent / 100);
                s.ForceAcquired += gain;
            }
            int sid = s.Id, tid = trainer.Id, nf = s.EffectiveForce, g2 = gain; var side = s.Side;
            ctx.Emit((i, _) => new ForceTrainingResult(i, day, tid, sid, side, gap, r, success, g2, nf));
            var after = RankOf(s);
            if (after != before) ctx.Emit((i, _) => new ForceRankChanged(i, day, sid, side, before, after));
        }
    }

    /// <summary>
    /// A story discovery ("Leia Uses Force", Dagobah; m5-missions calls it): a hidden Force user becomes discovered. Event ForceDiscovered.
    /// With <see cref="ForceDiscoveryMode.Story"/> (DEFAULT) this and the known Jedi at creation are the only discoveries. rule: §Force (Force discovery).
    /// </summary>
    public bool DiscoverForce(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (!c.ForceUser || c.ForceDiscovered) return false;
        c.ForceDiscovered = true;
        int day = (ctx?.Day ?? _game.Day) + 1;
        Emit(ctx, (i, _) => new ForceDiscovered(i, day, c.Id, c.Side, cause));
        return true;
    }

    /// <summary>
    /// Raise one skill (m5-missions: a successful research member gains GNPRTB 6158 = 1 in that research skill; the original's character-side check
    /// before the gain is Missing, so no cap). Not the refused SkillGrowth switch (growth outside research missions). rule: m5-missions §6 Research.
    /// </summary>
    public void GainSkill(CharacterRecord c, Skill skill, int amount) => c.Skills[(int)skill] += amount;

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
                continue;
            }
            t.HopIndex++;
            t.DueDay = day + t.HopTicks[t.HopIndex];
        }
    }

    // rule: §Injury time, §Escape (slot 6.10, character id): injuries tick and heal; then, if CaptiveEscape, each captive rolls.
    private void DayEnd(StepContext ctx)
    {
        foreach (var c in S.Characters.Values.Where(c => c.Status == CharacterStatus.Injured).ToList())
        {
            if (c.InjuredDaysLeft > 0) c.InjuredDaysLeft--;
            if (c.InjuredDaysLeft == 0) Heal(ctx, c);
        }
        if (!Params.CaptiveEscape) return;
        foreach (var c in S.Characters.Values.Where(c => c.Status == CharacterStatus.Captured).ToList())
        {
            int r = ctx.Rng.NextInt(0, 99);
            if (r < EscapePercent(Params.EscapeInput!(c))) Escape(ctx, c);
        }
    }

    /// <summary>
    /// ESCAPETB lookup: the percent of the largest table point at or below the input (the first point below it). The interpolation between points
    /// is not known (DEFAULT step lookup, as the incident tables). rule: §Escape; CH25.
    /// </summary>
    public int EscapePercent(int input)
    {
        var t = Params.EscapeTable!;
        int pct = t[0].Percent;
        foreach (var (inp, p) in t) if (input >= inp) pct = p;
        return pct;
    }
}
