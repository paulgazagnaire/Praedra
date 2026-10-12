using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Fog;
using Rebellion.Core.Research;
using Rebellion.Core.Support;
using Rebellion.Core.Troops;

namespace Rebellion.Core.Missions;

/// <summary>
/// Agent missions: teams of characters and special forces with decoys, the StartMission / MoveGroup / CancelMission orders, travel as a group,
/// landing and the iteration timer, the decoy phase and the detection walk (FOILTB, missions/08), the per-member success rolls (*MSTB), the effects
/// through the other modules' hooks (support, facilities, characters, research, fog), the return trip, and the incident-score terms of working
/// Incite and Subdue missions. Holds data and settings only; the state lives in <see cref="GameState.Missions"/>. After <see cref="GameSave.Load"/>,
/// attach again. Every loop is in mission id order (m1-clock rule 5). rule: m5-missions (whole sheet); slot 5.3 (Lead notes).
///
/// Not invented (refused or a no-op hook, listed in the sheet's Missing rules): the decoy table values (decoys refused RuleMissing while
/// <see cref="MissionData.DecoyTable"/> is null), the recruit pick (Recruitment refused RuleMissing while <see cref="Recruit"/> is null), the capture
/// rule for an exposed team (<see cref="OnTeamExposed"/>, null = no-op as the sheet says), the Death Star (<see cref="HasDeathStar"/> false until it
/// exists), Jedi Training (each sheet says the other owns it: refused RuleMissing), story missions (hidden).
/// </summary>
public sealed class MissionModule
{
    // Causes passed to the character and support functions. rule: §6 (Capture(target, S, Abduction), Kill(target, Assassination), Free(prisoner)).
    public const string CauseAbduction = "Abduction", CauseAssassination = "Assassination", CauseRescue = "Rescue", CauseSubdue = "Subdue";

    private readonly Game _game;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly SupportModule _support;
    private readonly TroopModule _troops;
    private readonly ResearchModule _research;
    private readonly CharacterModule _ch;
    private readonly FogModule _fog;

    public MissionData Data { get; }
    public MissionParams Params { get; }

    /// <summary>
    /// The character sheet's <c>OnTeamExposed(c, capturingSide, cause)</c>: capture, injury, death and escape of a foiled or failed team and their
    /// draws. That sheet defines no rule yet, so this is null and the call is a no-op (m5-missions §4). rule: §4, §Interfaces.
    /// </summary>
    public Action<StepContext, CharacterRecord, Side, ExposureCause>? OnTeamExposed { get; set; }
    /// <summary>
    /// The character sheet's <c>Recruit(side, system)</c>: take one pool member and make him or her a free character at the system. The pick is
    /// the character sheet's and it defines none, so this is null and a Recruitment order is refused RuleMissing. rule: §6 Recruitment.
    /// </summary>
    public Func<StepContext, Side, string, CharacterRecord?>? Recruit { get; set; }
    /// <summary>
    /// <c>HasDeathStar(owner, system)</c>: a completed Death Star of <paramref name="owner"/> stands at the system. Null = false until the Death Star
    /// exists in the core (M4/M6). rule: §Targets ("HasDeathStar returns false").
    /// </summary>
    public Func<Side, string, bool>? HasDeathStar { get; set; }
    /// <summary><c>DeathStar.Destroy(system, Sabotage)</c> (owner given). Null = nothing to destroy. rule: §6 Death Star Sabotage.</summary>
    public Action<StepContext, string, Side>? DestroyDeathStar { get; set; }

    private MissionModule(Game game, FleetModule fleets, EconomyModule eco, SupportModule support, TroopModule troops, ResearchModule research,
        CharacterModule ch, FogModule fog, MissionData data, MissionParams p)
    {
        _game = game; _fleets = fleets; _eco = eco; _support = support; _troops = troops; _research = research; _ch = ch; _fog = fog; Data = data; Params = p;
    }

    /// <summary>
    /// Register the three orders, the departure (2), arrival (3) and resolution (5.3) hooks, and wire the character (IsOnMission, membership ended),
    /// fog (AgentsAt) and support (incident terms, per-incident Incite) hooks. Attach after the characters, before victory. Throws
    /// <see cref="RuleMissingException"/> for a refused switch. rule: §Where it runs in the day Step.
    /// </summary>
    public static MissionModule Attach(Game game, FleetModule fleets, EconomyModule eco, SupportModule support, TroopModule troops,
        ResearchModule research, CharacterModule characters, FogModule fog, MissionData data, MissionParams? p = null)
    {
        p ??= new MissionParams();
        p.Validate();
        var m = new MissionModule(game, fleets, eco, support, troops, research, characters, fog, data, p);
        game.RegisterOrderHandler<StartMissionOrder>(m.HandleStart);
        game.RegisterOrderHandler<MoveGroupOrder>(m.HandleMoveGroup);
        game.RegisterOrderHandler<CancelMissionOrder>(m.HandleCancel);
        game.RegisterHook("2", m.Departures);
        game.RegisterHook("3", m.Arrivals);
        game.RegisterHook("5.3", m.Resolve);
        characters.IsOnMission = m.IsOnMission;
        characters.MissionMembershipEnded = m.OnMembershipEnded;
        fog.AgentsAt = m.AgentsAt;
        support.AgentIncidentTerm = m.IncidentTerm;
        support.IncidentFired = m.OnIncident;
        return m;
    }

    private MissionState S => _game.State.Missions;
    private EconomyState E => _game.State.Economy;
    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

    // ================================================================== queries

    public IReadOnlyCollection<MissionGroup> Missions => S.Missions.Values;
    public MissionGroup? Get(int id) => S.Missions.TryGetValue(id, out var m) ? m : null;

    /// <summary>Is the character in a mission group (team or decoy, from acceptance until the group is back)? rule: §Who can go ("leaves its place").</summary>
    public bool IsOnMission(int character) => S.Missions.Values.Any(m => m.Characters.Contains(character) || m.DecoyCharacters.Contains(character));

    /// <summary>Is the special force in a mission group?</summary>
    public bool IsSpecialForceBusy(int obj) => S.Missions.Values.Any(m => m.SpecialForces.Contains(obj) || m.DecoySpecialForces.Contains(obj));

    /// <summary>The fog's hook: the side has an agent on a working mission at the system (landed, not travelling). rule: m5-fog-and-hq §F3 3.</summary>
    public bool AgentsAt(Side side, string system) =>
        S.Missions.Values.Any(m => m.Side == side && m.Phase == MissionPhase.Working && m.Target == system && (m.HasTeam || m.DecoyCharacters.Count + m.DecoySpecialForces.Count > 0));

    // ================================================================== lookups (public for tests and the runner)

    /// <summary>
    /// <c>p = Lookup(table, x)</c> with the MstbLookup and MstbBelowFirstRow settings. rule: §Mission tables (Lookup).
    /// </summary>
    public int Lookup(IReadOnlyList<TableRow> rows, int x) => Lookup(rows, x, Params.MstbLookup, Params.MstbBelowFirstRow, Data.Numbers.RollDie);

    public static int Lookup(IReadOnlyList<TableRow> rows, int x, MstbLookupMode mode, MstbBelowFirstRow below, int always = 100)
    {
        if (x < rows[0].Threshold) return below switch { MstbBelowFirstRow.Zero => 0, MstbBelowFirstRow.Always => always, _ => rows[0].Value };
        int i = 0;
        while (i + 1 < rows.Count && rows[i + 1].Threshold <= x) i++;
        if (mode == MstbLookupMode.Step || i == rows.Count - 1) return rows[i].Value;
        var (a, b) = (rows[i], rows[i + 1]);
        return a.Value + (x - a.Threshold) * (b.Value - a.Value) / (b.Threshold - a.Threshold); // C# division truncates toward zero
    }

    /// <summary>FOILTB: step lookup, "the previous row"; below the first row there is no row: 100. rule: §3 (Traced, 0x595090, 0x53e340).</summary>
    public int FoilChance(int x)
    {
        var rows = Data.Foil;
        if (x < rows[0].Threshold) return Data.Numbers.FoilBelowFirstRow;
        return Lookup(rows, x, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue);
    }

    /// <summary>
    /// The detection input: avg - detection - (team special forces) - officer * 35 / 100 - (-1); integer division truncating toward zero.
    /// rule: §3 detection walk (GNPRTB 3584, 3589).
    /// </summary>
    public int DetectionX(int teamAvgEspionage, int detection, int teamSpecialForces, int officerEspionage) =>
        teamAvgEspionage - detection - teamSpecialForces - officerEspionage * Data.Numbers.DetectionOfficerPercent / 100 - Data.Numbers.DetectionConstant;

    /// <summary>The decoy input: decoy espionage - detection - officer * 35 / 100. rule: §3 decoy phase (GNPRTB 3588).</summary>
    public int DecoyX(int decoyEspionage, int detection, int officerEspionage) =>
        decoyEspionage - detection - officerEspionage * Data.Numbers.DecoyOfficerPercent / 100;

    // ================================================================== members and defenders

    /// <summary>A team or decoy unit with the three skills the rules read (a special force uses its class columns). rule: §Special forces.</summary>
    private readonly record struct Agent(bool IsCharacter, int Id, int Espionage, int Combat, int Leadership, CharacterRecord? C);

    private Agent? CharAgent(int id) => _ch.Get(id) is { } c
        ? new Agent(true, id, c.Skill(Skill.Espionage), c.Skill(Skill.Combat), c.Skill(Skill.Leadership), c) : null;

    private Agent? SfAgent(int id) => E.Objects.TryGetValue(id, out var o) && _troops.Data.SpecialForce(o.Class) is { } k
        ? new Agent(false, id, k.Espionage, k.Combat, k.Leadership, null) : null;

    /// <summary>Non-decoy members: characters in ascending id, then special forces in ascending object id. rule: §2 step 2.</summary>
    private List<Agent> Team(MissionGroup m) =>
        m.Characters.Select(CharAgent).Concat(m.SpecialForces.Select(SfAgent)).Where(a => a != null).Select(a => a!.Value).ToList();

    private List<Agent> Decoys(MissionGroup m) =>
        m.DecoyCharacters.Select(CharAgent).Concat(m.DecoySpecialForces.Select(SfAgent)).Where(a => a != null).Select(a => a!.Value).ToList();

    /// <summary>One defender: the unit, its class detection and its officer's espionage (0 when none). rule: §3 Defenders, Officer.</summary>
    public sealed record Defender(EconObject Unit, int Detection, int OfficerEspionage);

    /// <summary>
    /// Every completed capital ship (of fleets at the system), fighter squadron (aboard those ships, in a squadron fleet there, or based there) and
    /// regiment (landed, or aboard ships there with CountAboardUnits) whose side is not <paramref name="side"/>, by object id. The officer is the
    /// Free Admiral of the unit's fleet (ships and squadrons), the Free General of the fleet (regiments aboard) or of the system (landed regiments);
    /// a based squadron has none. rule: §3 Defenders, Officer (DEFAULT).
    /// </summary>
    public List<Defender> Defenders(Side side, string system)
    {
        var shipFleet = new Dictionary<int, Fleet>(); // lookup only, never iterated
        var sqFleet = new Dictionary<int, Fleet>();   // lookup only
        foreach (var f in _game.State.Fleets.Values)
        {
            if (f.At != system) continue;
            foreach (var s in f.Ships) shipFleet[s] = f;
            foreach (var q in f.Squadrons) sqFleet[q] = f;
        }
        int Admiral(Fleet f) => Esp(_ch.HolderOf(f.Id, CharacterPost.Admiral));
        int General(Fleet f) => Esp(_ch.HolderOf(f.Id, CharacterPost.General));
        var list = new List<Defender>();
        foreach (var o in E.Objects.Values) // SortedDictionary: id order
        {
            if (!o.Completed || o.Category == ObjectCategory.Facility || o.Category == ObjectCategory.SpecialForce || o.Side == side) continue;
            switch (o.Category)
            {
                case ObjectCategory.CapitalShip when shipFleet.TryGetValue(o.Id, out var f):
                    list.Add(new Defender(o, ShipDetection(o), Admiral(f)));
                    break;
                case ObjectCategory.Fighter:
                    if (o.AboardShip != 0 && shipFleet.TryGetValue(o.AboardShip, out var cf)) list.Add(new Defender(o, ShipDetection(o), Admiral(cf)));
                    else if (sqFleet.TryGetValue(o.Id, out var sf)) list.Add(new Defender(o, ShipDetection(o), Admiral(sf)));
                    else if (o.AboardShip == 0 && o.System == system) list.Add(new Defender(o, ShipDetection(o), 0));
                    break;
                case ObjectCategory.Regiment:
                    int det = _troops.Data.Regiment(o.Class)?.Detection ?? 0;
                    if (o.AboardShip != 0) { if (Params.CountAboardUnits && shipFleet.TryGetValue(o.AboardShip, out var rf)) list.Add(new Defender(o, det, General(rf))); }
                    else if (o.System == system) list.Add(new Defender(o, det, Esp(_ch.SystemGeneralOf(system))));
                    break;
            }
        }
        return list;
    }

    private int ShipDetection(EconObject o) => _fleets.ClassOf(o)?.Detection ?? 0;
    private static int Esp(CharacterRecord? c) => c is { Status: CharacterStatus.Free } ? c.Skill(Skill.Espionage) : 0;

    // ================================================================== who can go (§Who can go)

    private static Skill? ResearchSkill(MissionKind k) => k switch
    {
        MissionKind.ShipResearch => Skill.ShipDesign, MissionKind.FacilityResearch => Skill.FacilityDesign, MissionKind.TroopResearch => Skill.TroopTraining, _ => null,
    };

    private static ResearchKind? ResearchTrack(MissionKind k) => k switch
    {
        MissionKind.ShipResearch => ResearchKind.Ship, MissionKind.FacilityResearch => ResearchKind.Facility, MissionKind.TroopResearch => ResearchKind.Troop, _ => null,
    };

    private int SabotageSkillOf(int combat, int espionage) => Params.SabotageSkill == SabotageSkill.Combat ? combat : (combat + espionage) / 2;

    /// <summary>
    /// <c>CanDo(c, K)</c>: the character data has no ability bits, so the sheet's DEFAULT applies: the skill the table reads is above 0 (Move: always).
    /// rule: §Who can go, Characters 2 (DEFAULT).
    /// </summary>
    public bool CanDo(CharacterRecord c, MissionKind k) => k switch
    {
        MissionKind.Diplomacy => c.Skill(Skill.Diplomacy) > 0,
        MissionKind.Rescue or MissionKind.Abduction or MissionKind.Assassination => c.Skill(Skill.Combat) > 0,
        MissionKind.Sabotage => SabotageSkillOf(c.Skill(Skill.Combat), c.Skill(Skill.Espionage)) > 0,
        MissionKind.DeathStarSabotage => (c.Skill(Skill.Combat) + c.Skill(Skill.Espionage)) / 2 > 0,
        MissionKind.Espionage or MissionKind.Reconnaissance => c.Skill(Skill.Espionage) > 0,
        MissionKind.Recruitment or MissionKind.InciteUprising or MissionKind.SubdueUprising => c.Skill(Skill.Leadership) > 0,
        MissionKind.ShipResearch or MissionKind.FacilityResearch or MissionKind.TroopResearch => c.Skill(ResearchSkill(k)!.Value) > 0,
        _ => true,
    };

    private bool CharacterFree(CharacterRecord c) => _ch.IsAvailable(c) && c.Transit == null && c.System != null;
    private string? Where(int character) => _ch.Get(character) is { } c ? _ch.LocationOf(c) : null;

    // Member checks shared by StartMission and MoveGroup, in the sheet's order: UnknownMember, NotYours, DuplicateMember, NoTeam, NotFree,
    // NotCompleted, NotTogether. Returns the system the group stands on.
    private string? CheckMembers(Side side, List<int> chars, List<int> sfs, List<int> dChars, List<int> dSfs, out string? at)
    {
        at = null;
        foreach (var id in chars.Concat(dChars)) if (_ch.Get(id) is not { Placed: true }) return MissionReject.UnknownMember;
        foreach (var id in sfs.Concat(dSfs)) if (!E.Objects.TryGetValue(id, out var o) || o.Category != ObjectCategory.SpecialForce) return MissionReject.UnknownMember;
        foreach (var id in chars.Concat(dChars)) if (_ch.Get(id)!.Side != side) return RejectReason.NotYours;
        foreach (var id in sfs.Concat(dSfs)) if (E.Objects[id].Side != side) return RejectReason.NotYours;
        var allC = chars.Concat(dChars).ToList(); var allS = sfs.Concat(dSfs).ToList();
        if (allC.Distinct().Count() != allC.Count || allS.Distinct().Count() != allS.Count) return MissionReject.DuplicateMember;
        if (chars.Count + sfs.Count == 0) return MissionReject.NoTeam;
        foreach (var id in allC) if (!CharacterFree(_ch.Get(id)!)) return MissionReject.NotFree;
        foreach (var id in allS) if (IsSpecialForceBusy(id)) return MissionReject.NotFree;
        foreach (var id in allS) if (!E.Objects[id].Completed) return RejectReason.NotCompleted;
        var places = allC.Select(Where).Concat(allS.Select(id => E.Objects[id].System)).Distinct().ToList();
        if (places.Count != 1 || places[0] == null) return MissionReject.NotTogether;
        at = places[0];
        return null;
    }

    // Han Solo's 50 when he is in the group, free, and no special force (team or decoy) is in it; else 100. rule: §1 (GNPRTB 3083; planets/11).
    private int GroupSpeed(IEnumerable<int> chars, int specialForces)
    {
        var n = Data.Numbers;
        bool han = specialForces == 0 && chars.Any(id => id == n.HanCharacterId && _ch.Get(id) is { Status: CharacterStatus.Free });
        return han ? n.HanMissionSpeed : n.GroupSpeed;
    }

    // ================================================================== target legality (§Targets, legality)

    /// <summary>
    /// Null when the target is legal for the kind, else the reason. Checked at Step 1 and at the start of every iteration (and, for a character
    /// target, at landing: the character must still be at the mission system). rule: §Targets, legality; §2 step 0.
    /// </summary>
    private string? TargetReason(MissionKind k, Side side, string? system, int? targetChar, string? missionSystem)
    {
        CharacterRecord? tc = targetChar is int tid ? _ch.Get(tid) : null;
        if (k is MissionKind.Rescue or MissionKind.Abduction or MissionKind.Assassination)
        {
            if (tc is not { Placed: true }) return MissionReject.BadTarget;
            bool ok = k == MissionKind.Rescue
                ? tc.Side == side && tc.Status == CharacterStatus.Captured && tc.Captor == Other(side) && tc.HeldAt != null
                : tc.Side == Other(side) && tc.Status == CharacterStatus.Free && tc.Transit == null && _ch.LocationOf(tc) != null;
            if (!ok) return MissionReject.BadTarget;
            if (missionSystem != null && _ch.LocationOf(tc) != missionSystem) return MissionReject.BadTarget;
            return null;
        }
        if (system == null || !_fleets.Galaxy.TryFindSystem(system, out _)) return MissionReject.BadTarget;
        Side? holder = _fleets.OwnerOf(system);
        switch (k)
        {
            case MissionKind.Diplomacy:
                if (holder != null && holder != side) return MissionReject.BadTarget;
                if (Params.DiplomacyNeedsCalm && _support.IsUprising(system)) return MissionReject.BadTarget;
                return null;
            case MissionKind.Recruitment: return holder == side ? null : MissionReject.BadTarget;
            case MissionKind.Sabotage: return holder == side ? MissionReject.BadTarget : null;
            case MissionKind.Espionage or MissionKind.Reconnaissance: return null;
            case MissionKind.InciteUprising: return holder == Other(side) ? null : MissionReject.BadTarget;
            case MissionKind.SubdueUprising: return holder == side && _support.IsUprising(system) ? null : MissionReject.BadTarget;
            case MissionKind.ShipResearch or MissionKind.FacilityResearch or MissionKind.TroopResearch:
                return _research.CanResearch(side, ResearchTrack(k)!.Value, system); // m5-research names
            case MissionKind.DeathStarSabotage: return HasDeathStar?.Invoke(Other(side), system) == true ? null : MissionReject.NoDeathStar;
            default: return MissionReject.BadTarget;
        }
    }

    private string? TargetReason(MissionGroup m) => TargetReason(m.Kind, m.Side, m.Target, m.TargetCharacter, m.TargetCharacter != null ? m.Target : null);

    // ================================================================== orders (step 1)

    // rule: §Orders (StartMission), rejections in the sheet's order. RuleMissing (ours) comes before the target checks: a decoy while the decoy
    // table is Missing, Recruitment while the recruit pick is Missing; Jedi Training right after WrongSide (the two sheets each name the other as owner).
    private OrderResult HandleStart(StepContext ctx, StartMissionOrder o)
    {
        if (!Data.TryGet(o.Kind, out var rec)) return OrderResult.Reject(MissionReject.UnknownKind);
        if (rec.Hidden) return OrderResult.Reject(MissionReject.HiddenKind);
        if (!rec.AvailableTo(o.Side)) return OrderResult.Reject(MissionReject.WrongSide);
        if (o.Kind == MissionKind.JediTraining) return OrderResult.Reject(MissionReject.RuleMissing);
        var chars = o.Characters ?? new List<int>(); var sfs = o.SpecialForces ?? new List<int>();
        var dSfs = o.Decoys ?? new List<int>(); var dChars = o.DecoyCharacters ?? new List<int>();
        if (CheckMembers(o.Side, chars, sfs, dChars, dSfs, out var home) is { } bad) return OrderResult.Reject(bad);
        foreach (var id in chars) if (!CanDo(_ch.Get(id)!, o.Kind)) return OrderResult.Reject(MissionReject.NotAbleToDo);
        foreach (var id in sfs)
            if (_troops.Data.SpecialForce(E.Objects[id].Class) is not { } k || (k.MissionBit & rec.SpecialForceMask) == 0)
                return OrderResult.Reject(MissionReject.NotEligibleSpecialForce);
        bool anyDecoy = dSfs.Count + dChars.Count > 0;
        if (anyDecoy && !rec.Foilable) return OrderResult.Reject(MissionReject.BadDecoy);
        if (dChars.Count > 0 && Params.DecoysFromSpecialForcesOnly) return OrderResult.Reject(MissionReject.BadDecoy);
        if ((Params.MaxTeamMembers > 0 && chars.Count + sfs.Count > Params.MaxTeamMembers) || (Params.MaxDecoys > 0 && dSfs.Count + dChars.Count > Params.MaxDecoys))
            return OrderResult.Reject(MissionReject.TooManyMembers);
        if (anyDecoy && Data.DecoyTable == null) return OrderResult.Reject(MissionReject.RuleMissing);
        if (o.Kind == MissionKind.Recruitment && Recruit == null) return OrderResult.Reject(MissionReject.RuleMissing);

        bool charTarget = o.Kind is MissionKind.Rescue or MissionKind.Abduction or MissionKind.Assassination;
        if (TargetReason(o.Kind, o.Side, o.TargetSystem, o.TargetCharacter, null) is { } tr) return OrderResult.Reject(tr);
        string target = charTarget ? _ch.LocationOf(_ch.Get(o.TargetCharacter!.Value)!)! : _fleets.Galaxy.Get(o.TargetSystem!).Name;
        int? facility = null;
        if (o.Kind == MissionKind.Sabotage && Params.SabotageTargetMode == SabotageTargetMode.Chosen)
        {
            if (o.Facility is not int fid || !E.Objects.TryGetValue(fid, out var fo) || fo.Category != ObjectCategory.Facility || !fo.Completed || fo.System != target)
                return OrderResult.Reject(MissionReject.BadTarget);
            facility = fid;
        }
        var path = Trip(home!, target, GroupSpeed(chars.Concat(dChars), sfs.Count + dSfs.Count));
        if (path == null) return OrderResult.Reject(RejectReason.Unreachable);

        var m = new MissionGroup
        {
            Id = ObjectRegistry.NextId(E), Kind = o.Kind, Side = o.Side,
            Characters = chars.OrderBy(x => x).ToList(), SpecialForces = sfs.OrderBy(x => x).ToList(),
            DecoyCharacters = dChars.OrderBy(x => x).ToList(), DecoySpecialForces = dSfs.OrderBy(x => x).ToList(),
            Home = home!, Target = target, TargetCharacter = charTarget ? o.TargetCharacter : null, Facility = facility,
            Phase = MissionPhase.Outbound,
        };
        SetTrip(m, path.Value, ctx.Day);
        S.Missions[m.Id] = m;
        Started(ctx, m);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Orders (MoveGroup): the members' checks, the Move record's special-force mask (all but Bounty Hunters), then UnknownSystem,
    // AlreadyThere, Unreachable. Characters need no skill for a Move.
    private OrderResult HandleMoveGroup(StepContext ctx, MoveGroupOrder o)
    {
        var rec = Data.Get(MissionKind.Move);
        var chars = o.Characters ?? new List<int>(); var sfs = o.SpecialForces ?? new List<int>();
        if (CheckMembers(o.Side, chars, sfs, new List<int>(), new List<int>(), out var home) is { } bad) return OrderResult.Reject(bad);
        foreach (var id in sfs)
            if (_troops.Data.SpecialForce(E.Objects[id].Class) is not { } k || (k.MissionBit & rec.SpecialForceMask) == 0)
                return OrderResult.Reject(MissionReject.NotEligibleSpecialForce);
        if (!_fleets.Galaxy.TryFindSystem(o.Destination, out _)) return OrderResult.Reject(RejectReason.UnknownSystem);
        string dest = _fleets.Galaxy.Get(o.Destination).Name;
        if (dest == home) return OrderResult.Reject(RejectReason.AlreadyThere);
        var path = Trip(home!, dest, GroupSpeed(chars, sfs.Count));
        if (path == null) return OrderResult.Reject(RejectReason.Unreachable);
        var m = new MissionGroup
        {
            Id = ObjectRegistry.NextId(E), Kind = MissionKind.Move, Side = o.Side,
            Characters = chars.OrderBy(x => x).ToList(), SpecialForces = sfs.OrderBy(x => x).ToList(),
            Home = home!, Target = dest, Phase = MissionPhase.Outbound,
        };
        SetTrip(m, path.Value, ctx.Day);
        S.Missions[m.Id] = m;
        Started(ctx, m);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Orders (CancelMission): only while Working; the group leaves in step 2 of this Step.
    private OrderResult HandleCancel(StepContext ctx, CancelMissionOrder o)
    {
        if (!S.Missions.TryGetValue(o.Mission, out var m)) return OrderResult.Reject(MissionReject.UnknownMission);
        if (m.Side != o.Side) return OrderResult.Reject(RejectReason.NotYours);
        if (m.Phase != MissionPhase.Working) return OrderResult.Reject(RejectReason.InTransit);
        End(ctx, m, MissionOutcome.Cancelled, ctx.Day);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    private void Started(StepContext ctx, MissionGroup m)
    {
        int day = ctx.Day, id = m.Id, ticks = m.TotalTicks, arr = m.ArrivalDay; var side = m.Side; var kind = m.Kind;
        string home = m.Home, target = m.Target; int? tc = m.TargetCharacter;
        var c = new ValueList<int>(m.Characters.ToList()); var s = new ValueList<int>(m.SpecialForces.ToList());
        var d = new ValueList<int>(m.DecoySpecialForces.Concat(m.DecoyCharacters).ToList());
        ctx.Emit((i, _) => new MissionStarted(i, day, id, side, kind, home, target, tc, c, s, d, ticks, arr));
    }

    // ================================================================== travel (m1-travel with the group speed; never stopped: MissionsIgnoreForcedStop)

    /// <summary>The path (names) and total ticks from one system to another at a speed; a group already there has a one-name path and 0 ticks. Null = unreachable.</summary>
    private (List<string> Path, int Ticks)? Trip(string from, string to, int speed)
    {
        if (from == to) return (new List<string> { from }, 0);
        var p = _fleets.FindPath(from, to, speed);
        return p.Ok ? (p.Names(_fleets.Galaxy).ToList(), p.TotalTicks) : null;
    }

    // Arrival day A = K + total ticks; a group already there lands with A = K + 1 in step 3 of the same Step. rule: §1 (DEFAULT for the latter).
    private static void SetTrip(MissionGroup m, (List<string> Path, int Ticks) trip, int k)
    {
        m.Path = trip.Path; m.TotalTicks = trip.Ticks; m.ArrivalDay = trip.Ticks == 0 ? k + 1 : k + trip.Ticks; m.Departed = false;
    }

    private void PlaceMembers(MissionGroup m, string? system)
    {
        foreach (var id in m.AllCharacters) if (_ch.Get(id) is { } c) c.System = system;
        foreach (var id in m.AllSpecialForces) if (E.Objects.TryGetValue(id, out var o)) o.System = system;
    }

    // Drop members that left the game (a scrapped special force; a captured or dead character is removed by OnMembershipEnded).
    private void Prune(MissionGroup m)
    {
        m.SpecialForces.RemoveAll(id => !E.Objects.ContainsKey(id));
        m.DecoySpecialForces.RemoveAll(id => !E.Objects.ContainsKey(id));
        bool Gone(int id) => _ch.Get(id) is not { Placed: true } c || c.Status is CharacterStatus.Captured or CharacterStatus.Dead;
        m.Characters.RemoveAll(Gone);
        m.DecoyCharacters.RemoveAll(Gone);
    }

    private static bool Empty(MissionGroup m) => !m.HasTeam && m.DecoyCharacters.Count + m.DecoySpecialForces.Count == 0;

    /// <summary>A capture or death ended a character's membership (the character module calls it). rule: m5-characters-and-force §Capture 2, 3.</summary>
    public void OnMembershipEnded(StepContext? ctx, CharacterRecord c)
    {
        foreach (var m in S.Missions.Values)
        {
            m.Characters.Remove(c.Id);
            m.DecoyCharacters.Remove(c.Id);
        }
    }

    // rule: §Where it runs, step 2 (after the fleets, mission id): outbound groups leave; ended groups start home (Return record 0x02).
    private void Departures(StepContext ctx)
    {
        int k = ctx.Day;
        foreach (var m in S.Missions.Values.ToList())
        {
            Prune(m);
            if (m.Phase == MissionPhase.Outbound && !m.Departed)
            {
                m.Departed = true;
                if (m.TotalTicks > 0) PlaceMembers(m, null);
                Departed(ctx, m, false);
            }
            else if (m.Phase == MissionPhase.ReturnPending)
            {
                if (Empty(m)) { S.Missions.Remove(m.Id); continue; }
                int speed = GroupSpeed(m.AllCharacters, m.AllSpecialForces.Count());
                var trip = ReturnTrip(m, speed);
                if (trip == null) // the side holds no reachable system: the group stays where it is, free (DEFAULT)
                {
                    Release(ctx, m, m.Target, k);
                    continue;
                }
                SetTrip(m, trip.Value, k);
                m.Phase = MissionPhase.Returning; m.Departed = true;
                if (m.TotalTicks > 0) PlaceMembers(m, null);
                Departed(ctx, m, true);
            }
        }
    }

    private void Departed(StepContext ctx, MissionGroup m, bool returning)
    {
        int day = ctx.Day, id = m.Id, ticks = m.TotalTicks, arr = m.ArrivalDay; var side = m.Side;
        string from = m.Path[0], to = m.Path[^1];
        ctx.Emit((i, _) => new MissionDeparted(i, day, id, side, from, to, returning, ticks, arr));
    }

    /// <summary>
    /// Home if the side still holds it; else the nearest system the side holds (fewest ticks by m1-travel, ties by name ordinal); null if none
    /// (the group stays). rule: §5 (DEFAULT; the original's Return and Adrift targets are Missing).
    /// </summary>
    private (List<string> Path, int Ticks)? ReturnTrip(MissionGroup m, int speed)
    {
        if (_fleets.OwnerOf(m.Home) == m.Side && Trip(m.Target, m.Home, speed) is { } home) return home;
        (List<string> Path, int Ticks)? best = null; string? bestName = null;
        foreach (var kv in _game.State.SystemOwners) // SortedDictionary: ordinal name order, so the first of equal ticks wins
        {
            if (kv.Value != m.Side || Trip(m.Target, kv.Key, speed) is not { } t) continue;
            if (best == null || t.Ticks < best.Value.Ticks) { best = t; bestName = kv.Key; }
        }
        return bestName == null ? null : best;
    }

    // Every member becomes free at the system; the mission leaves the state.
    private void Release(StepContext ctx, MissionGroup m, string at, int day)
    {
        PlaceMembers(m, at);
        S.Missions.Remove(m.Id);
        int id = m.Id; var side = m.Side;
        if (m.Kind == MissionKind.Move) ctx.Emit((i, _) => new MissionGroupArrived(i, day, id, side, at));
        else ctx.Emit((i, _) => new MissionReturned(i, day, id, side, at));
    }

    // rule: §Where it runs, step 3 (after the fleets and deliveries, mission id): groups land (timer draw) or finish returning.
    private void Arrivals(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var m in S.Missions.Values.ToList())
        {
            if (!m.Departed || m.ArrivalDay != t) continue;
            Prune(m);
            if (m.Phase == MissionPhase.Returning)
            {
                if (Empty(m)) { S.Missions.Remove(m.Id); continue; }
                Release(ctx, m, m.Path[^1], t);
            }
            else if (m.Phase == MissionPhase.Outbound) Land(ctx, m, t);
        }
    }

    // rule: §1 Step 3: the group lands, draws the first timer T = min + rand(0..spread) (no draw for a zero spread), due = A + T, MissionLanded.
    // A character target that is no longer there ends the mission TargetInvalid (no draw). DetectionAtLanding runs the walk once.
    private void Land(StepContext ctx, MissionGroup m, int t)
    {
        PlaceMembers(m, m.Target);
        if (m.Kind == MissionKind.Move) { Release(ctx, m, m.Target, t); return; }
        if (!m.HasTeam) { End(ctx, m, MissionOutcome.Cancelled, t); return; }
        if (m.TargetCharacter != null && TargetReason(m) != null) { End(ctx, m, MissionOutcome.TargetInvalid, t); return; }
        var rec = Data.Get(m.Kind);
        m.Due = t + Timer(ctx, rec);
        m.Phase = MissionPhase.Working;
        int id = m.Id, due = m.Due; var side = m.Side; string target = m.Target;
        ctx.Emit((i, _) => new MissionLanded(i, t, id, side, target, due));
        if (Params.DetectionAtLanding && rec.Foilable && Walk(ctx, m, t) is { } d) Foil(ctx, m, d, t);
    }

    private static int Timer(StepContext ctx, MissionRecord rec) => rec.TimerMin + (rec.TimerSpread > 0 ? ctx.Rng.NextInt(0, rec.TimerSpread) : 0);

    // ================================================================== slot 5.3: iterations

    // rule: §2 (slot 5.3 of the Step in which K + 1 == due), missions in id order.
    private void Resolve(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var m in S.Missions.Values.Where(m => m.Phase == MissionPhase.Working && m.Due == t).ToList())
            if (S.Missions.ContainsKey(m.Id) && m.Phase == MissionPhase.Working) Iterate(ctx, m, t);
    }

    private void Iterate(StepContext ctx, MissionGroup m, int t)
    {
        Prune(m);
        // A team emptied by captures elsewhere (the sheet has no rule; DEFAULT ours, listed): the job is dropped and the rest goes home.
        if (!m.HasTeam) { End(ctx, m, MissionOutcome.Cancelled, t); return; }
        // 0. legality
        if (TargetReason(m) != null) { End(ctx, m, MissionOutcome.TargetInvalid, t); return; }
        var rec = Data.Get(m.Kind);
        // 1. decoys and detection
        if (rec.Foilable && Walk(ctx, m, t) is { } d) { Foil(ctx, m, d, t); return; }

        // 2. success rolls, one draw per non-decoy member (characters by id, then special forces by id); Research draws its points right after a success
        bool recon = m.Kind == MissionKind.Reconnaissance;
        var research = ResearchSkill(m.Kind);
        int successes = 0, points = 0;
        if (recon) successes = 1; // automatic success, no draw (DEFAULT, Missing: no table)
        else
        {
            var table = rec.Table != null ? Data.Table(rec.Table) : null;
            foreach (var a in Team(m))
            {
                int input = research is Skill rs ? (a.C?.Skill(rs) ?? 0) : Input(m, a);
                int chance = research != null ? input : Lookup(table!, input);
                int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
                bool ok = r < chance;
                int id = m.Id, member = a.Id; var side = m.Side; bool isChar = a.IsCharacter;
                ctx.Emit((i, _) => new MissionMemberRoll(i, t, id, side, member, isChar, input, chance, r, ok));
                if (!ok) continue;
                successes++;
                if (research is Skill skill && a.C is { } c) // rule: §6 Research (m5-research: RollMissionPoints right after the roll; +1 skill, GNPRTB 6158)
                {
                    points += _research.RollMissionPoints(ctx);
                    _ch.GainSkill(c, skill, Data.Numbers.ResearchSkillGain);
                    int nv = c.Skill(skill);
                    ctx.Emit((i, _) => new MissionSkillGained(i, t, id, side, member, skill, nv));
                }
            }
        }

        // 3. result and 4. effects
        bool success = successes > 0;
        if (success)
        {
            int effects = recon || research != null ? 1 : Params.SuccessRule == SuccessRule.EachMember ? successes : 1;
            int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
            ctx.Emit((i, _) => new MissionSucceeded(i, t, id, side, kind, target, research != null ? successes : effects));
            if (research != null) { if (points > 0) _research.AddMissionPoints(ctx, m.Side, ResearchTrack(m.Kind)!.Value, points); }
            else for (int e = 0; e < effects; e++) Effect(ctx, m, t);
        }
        m.Iterations++;

        // 5. after the iteration
        if (rec.Repeats)
        {
            if (!S.Missions.ContainsKey(m.Id) || m.Phase != MissionPhase.Working) return;
            m.Due += Timer(ctx, rec);
            int id = m.Id, due = m.Due; var side = m.Side; string target = m.Target;
            ctx.Emit((i, _) => new MissionIterationDone(i, t, id, side, target, success, due));
            return;
        }
        if (success) { End(ctx, m, MissionOutcome.Succeeded, t); return; }
        {
            int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
            ctx.Emit((i, _) => new MissionFailed(i, t, id, side, kind, target));
        }
        Expose(ctx, m, ExposureCause.Failed);
        End(ctx, m, MissionOutcome.Failed, t);
    }

    /// <summary>
    /// The table input of one member. rule: §Mission records (skill fed to the table), §Mission records note on support: "support" is the mission
    /// side's share at the target, "rival support" 100 - it, "controller support" the holder's.
    /// </summary>
    private int Input(MissionGroup m, Agent a)
    {
        int Support() => _eco.SupportOf(m.Target, m.Side);
        int TargetCombat() => m.TargetCharacter is int tid && _ch.Get(tid) is { } tc ? tc.Skill(Skill.Combat) : 0;
        switch (m.Kind)
        {
            case MissionKind.Diplomacy: { int s = Support(); return (s - (100 - s)) + (a.C?.Skill(Skill.Diplomacy) ?? 0); }
            case MissionKind.Rescue: return a.Combat;
            case MissionKind.Sabotage: return SabotageSkillOf(a.Combat, a.Espionage);
            case MissionKind.Espionage: return a.Espionage;
            case MissionKind.Recruitment: return a.Leadership - Params.RecruitSystemTerm;
            case MissionKind.Abduction or MissionKind.Assassination: return a.Combat - TargetCombat();
            case MissionKind.InciteUprising:
                if (Params.InciteInputMode == InciteInputMode.Documented) { int s = Support(); return (a.Leadership - s) - (100 - s); }
                return a.Leadership - (_fleets.OwnerOf(m.Target) is Side h ? _eco.SupportOf(m.Target, h) : 0);
            case MissionKind.DeathStarSabotage: return (a.Combat + a.Espionage) / 2;
            case MissionKind.SubdueUprising: { int s = Support(); return ((100 - s) - s) + a.Leadership; }
            default: return 0;
        }
    }

    /// <summary>
    /// The decoy phase then the detection walk; returns the defender that detected the team, or null. Draws: one per (defender, decoy) tried, then
    /// one per defender walked up to and including the first detection. No defenders: no draw. rule: §3 (Traced shape, missions/08).
    /// </summary>
    private Defender? Walk(StepContext ctx, MissionGroup m, int t)
    {
        var defenders = Defenders(m.Side, m.Target);
        if (defenders.Count == 0) return null;
        var decoys = Decoys(m);
        if (decoys.Count > 0 && Data.DecoyTable == null) throw new RuleMissingException("DecoyTable", "m5-missions");
        int id = m.Id; var side = m.Side;
        var walked = new List<Defender>();
        foreach (var d in defenders)
        {
            bool decoyed = false;
            foreach (var dc in decoys)
            {
                int x = DecoyX(dc.Espionage, d.Detection, d.OfficerEspionage), p = Lookup(Data.DecoyTable!, x);
                int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
                bool ok = r < p;
                int def = d.Unit.Id, did = dc.Id;
                ctx.Emit((i, _) => new MissionDecoyRoll(i, t, id, side, def, did, x, p, r, ok));
                if (ok) { decoyed = true; break; }
            }
            if (!decoyed) walked.Add(d);
        }
        var team = Team(m);
        int avg = team.Count == 0 ? 0 : team.Sum(a => a.Espionage) / team.Count;
        int sfs = team.Count(a => !a.IsCharacter);
        foreach (var d in walked)
        {
            int x = DetectionX(avg, d.Detection, sfs, d.OfficerEspionage), p = FoilChance(x);
            int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
            bool hit = r < p;
            int def = d.Unit.Id;
            ctx.Emit((i, _) => new MissionDetectionRoll(i, t, id, side, def, x, p, r, hit));
            if (hit) return d;
        }
        return null;
    }

    // rule: §4 Foiled: MissionFoiled, the fog's MissionDetected (AgentDetected sighting for the defender), the exposure hooks, then the end.
    private void Foil(StepContext ctx, MissionGroup m, Defender d, int t)
    {
        int id = m.Id, def = d.Unit.Id; var side = m.Side; string target = m.Target, cls = d.Unit.Class;
        ctx.Emit((i, _) => new MissionFoiled(i, t, id, side, target, def, cls));
        _fog.OnMissionDetected(ctx, d.Unit.Side ?? Other(m.Side), m.Target);
        Expose(ctx, m, ExposureCause.Foiled);
        End(ctx, m, MissionOutcome.Foiled, t);
    }

    // rule: §4: OnTeamExposed(member, otherSide, cause) for every character team member in ascending id (switch ExposureHooks; decoys never).
    private void Expose(StepContext ctx, MissionGroup m, ExposureCause cause)
    {
        if (!Params.ExposureHooks || OnTeamExposed == null) return;
        foreach (var id in m.Characters.ToList())
            if (_ch.Get(id) is { } c) OnTeamExposed(ctx, c, Other(m.Side), cause);
    }

    // rule: §5: MissionEnded; the rest of the group becomes the Return group (it leaves in the next step 2; a Cancel's group leaves in this Step's step 2).
    private void End(StepContext ctx, MissionGroup m, MissionOutcome outcome, int day)
    {
        m.Outcome = outcome; m.Phase = MissionPhase.ReturnPending; m.Departed = false;
        int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
        ctx.Emit((i, _) => new MissionEnded(i, day, id, side, kind, target, outcome));
        Prune(m);
        if (Empty(m)) S.Missions.Remove(m.Id);
    }

    // ================================================================== effects (§6), applied in slot 5.3

    private void Effect(StepContext ctx, MissionGroup m, int t)
    {
        var n = Data.Numbers;
        int id = m.Id; var side = m.Side; string target = m.Target;
        CharacterRecord? tc = m.TargetCharacter is int tid ? _ch.Get(tid) : null;
        switch (m.Kind)
        {
            case MissionKind.Diplomacy: // own-held +1..+20, neutral +1..+10, for the mission side
                {
                    bool own = _fleets.OwnerOf(target) == side;
                    int lo = own ? n.DiplomacyOwnLow : n.DiplomacyNeutralLow, hi = own ? n.DiplomacyOwnHigh : n.DiplomacyNeutralHigh;
                    _support.ChangeSupport(ctx, target, side, lo + ctx.Rng.NextInt(0, hi - lo), SupportCause.Diplomacy);
                    break;
                }
            case MissionKind.SubdueUprising:
                _support.ChangeSupport(ctx, target, side, n.SubdueLow + ctx.Rng.NextInt(0, n.SubdueHigh - n.SubdueLow), SupportCause.Subdue);
                if (Params.SubdueEndsUprising) _support.EndUprisingNow(ctx, target, CauseSubdue);
                break;
            case MissionKind.InciteUprising:
                if (Params.InciteSupportTiming == InciteSupportTiming.PerIteration && _fleets.OwnerOf(target) is Side holder)
                    _support.ChangeSupport(ctx, target, holder, n.InciteSupport, SupportCause.IncitedUprising);
                break;
            case MissionKind.Sabotage:
                {
                    EconObject? victim = null;
                    if (Params.SabotageTargetMode == SabotageTargetMode.Chosen)
                    {
                        if (m.Facility is int fid && E.Objects.TryGetValue(fid, out var f) && f.Completed && f.System == target) victim = f;
                    }
                    else
                    {
                        var facs = E.Objects.Values.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == target).ToList(); // id order
                        if (facs.Count > 0) victim = facs[ctx.Rng.NextInt(0, facs.Count - 1)];
                    }
                    if (victim != null) _eco.Destroy(ctx, t, victim, DestroyReason.Sabotage);
                    else ctx.Emit((i, _) => new SabotageNoTarget(i, t, id, side, target));
                    break;
                }
            case MissionKind.Espionage:
                _fog.EspionageSuccess(ctx, side, target);
                ctx.Emit((i, _) => new SystemRevealed(i, t, id, side, target, MissionKind.Espionage));
                break;
            case MissionKind.Reconnaissance:
                _fog.Reveal(ctx, side, target, RevealKind.Recon);
                ctx.Emit((i, _) => new SystemRevealed(i, t, id, side, target, MissionKind.Reconnaissance));
                break;
            case MissionKind.Recruitment:
                if (Recruit?.Invoke(ctx, side, target) is { } rc) { int cid = rc.Id; ctx.Emit((i, _) => new CharacterRecruited(i, t, id, side, cid, target)); }
                break;
            case MissionKind.Rescue: if (tc != null) _ch.Free(ctx, tc, CauseRescue); break;
            case MissionKind.Abduction: if (tc != null) _ch.Capture(ctx, tc, side, null, CauseAbduction); break;
            case MissionKind.Assassination: if (tc != null) _ch.Kill(ctx, tc, CauseAssassination); break;
            case MissionKind.DeathStarSabotage: DestroyDeathStar?.Invoke(ctx, target, Other(side)); break;
        }
    }

    // ================================================================== uprisings (§7) and per-incident Incite (§6)

    /// <summary>incite_term + subdue_term of the incident score: ± sum of (member leadership / 10) over the non-decoy members of working Incite and Subdue missions. rule: §7.</summary>
    public int IncidentTerm(string system) => InciteTerm(system) + SubdueTerm(system);

    public int InciteTerm(string system) => AgentTerm(system, MissionKind.InciteUprising);
    public int SubdueTerm(string system) => -AgentTerm(system, MissionKind.SubdueUprising);

    private int AgentTerm(string system, MissionKind kind)
    {
        int sum = 0, div = Data.Numbers.AgentLeadershipDivisor;
        foreach (var m in S.Missions.Values)
            if (m.Kind == kind && m.Phase == MissionPhase.Working && m.Target == system)
                foreach (var a in Team(m)) sum += a.Leadership / div;
        return sum;
    }

    // InciteSupportTiming = PerIncident: in slot 6.3, every incident of a system with a working Incite mission costs its holder 2 support. rule: §6 Incite.
    private void OnIncident(StepContext ctx, string system, Side holder)
    {
        if (Params.InciteSupportTiming != InciteSupportTiming.PerIncident) return;
        if (!S.Missions.Values.Any(m => m.Kind == MissionKind.InciteUprising && m.Phase == MissionPhase.Working && m.Target == system)) return;
        _support.ChangeSupport(ctx, system, holder, Data.Numbers.InciteSupport, SupportCause.IncitedUprising);
    }
}
