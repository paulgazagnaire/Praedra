using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Research;
using Praedra.Core.Support;
using Praedra.Core.Troops;

namespace Praedra.Core.Missions;

/// <summary>
/// Agent missions: teams of characters and special forces with decoys, the StartMission / MoveGroup / CancelMission orders, travel as a group,
/// landing and the iteration timer, the decoy phase and the detection walk (FOILTB, missions/08), the per-member success rolls (*MSTB), the effects
/// through the other modules' hooks (support, facilities, characters, research, fog), the return trip, and the incident-score terms of working
/// Incite and Subdue missions. Holds data and settings only; the state lives in <see cref="GameState.Missions"/>. After <see cref="GameSave.Load"/>,
/// attach again. Every loop is in mission id order (m1-clock rule 5). rule: m5-missions (whole sheet); slot 5.3 (Lead notes).
///
/// Not invented (refused or a no-op hook, listed in the sheet's Missing rules): the decoy table values (decoys refused RuleMissing while
/// <see cref="MissionData.DecoyTable"/> is null), the recruit pick (Recruitment refused RuleMissing while <see cref="Recruit"/> is null; GameHost wires it), the capture
/// rule for an exposed team (<see cref="OnTeamExposed"/>, null = no-op as the sheet says), the harvester (<see cref="HasHarvester"/> false until it
/// exists), Train Talent (each sheet says the other owns it: refused RuleMissing), story missions (hidden).
/// </summary>
public sealed partial class MissionModule
{
    // Causes passed to the character and support functions. rule: §6 (Capture(target, S, Abduction), Kill(target, Assassination), Free(prisoner)).
    public const string CauseAbduction = "Abduction", CauseAssassination = "Assassination", CauseRescue = "Rescue", CauseSubdue = "Subdue",
        CauseKidnap = "Kidnap", // rule: m5-missions §6a.4 (Capture(target, Hand, Kidnap))
        CauseMissionDuel = "MissionDuel"; // rule: m5-missions §4 (B2d §5: Capture(c, captor side, held at the check's place, MissionDuel))

    /// <summary>
    /// The kind word of a mission (data block +0x34, reader 0x520e50): one nibble per check point, phase 2 (bits 0-3), 3, 7, 9 (bits 12-15). Every
    /// mission's init writes 0x4112 (0x593a80 -> 0x5236e0), Adrift 0x4103 (0x576960 -> 0x576a20), Move is created with 0 (0x547480). rule: m5-missions
    /// §1 (Traced B2d §2).
    /// </summary>
    public const int KindWordMission = 0x4112, KindWordAdrift = 0x4103, KindWordMove = 0;
    /// <summary>DEFFACSD family of the Mordant Lien-150 ion cannon: an own one at the place stops the orbit guards of a leaving or arriving check (0x5275d0, range [0x22, 0x23)). rule: B2d §3.</summary>
    public const int IonCannonFamily = 0x22;

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
    /// The character sheet's <c>OnTeamExposed(c, capturingSide, cause)</c> for a failed team (a foiled team fights the escape duel instead, B2d §6).
    /// That sheet defines no rule yet, so this is null and the call is a no-op (m5-missions §4). rule: §4, §Interfaces.
    /// </summary>
    public Action<StepContext, CharacterRecord, Side, ExposureCause>? OnTeamExposed { get; set; }
    /// <summary>The DEFFACSD family of a facility class (0 = not a defense facility); the host wires it from the defense data. Null = no ion cannon anywhere. rule: B2d §3 (ownIon).</summary>
    public Func<string, int>? FacilityFamily { get; set; }
    /// <summary>
    /// The character sheet's <c>Recruit(side, system)</c>: take one pool member and make him or her a free character at the system. GameHost wires
    /// <see cref="CharacterModule.Recruit"/> (the setup extras pick, 2026-10-18); while null a Recruitment order is refused RuleMissing. rule: §6 Recruitment.
    /// </summary>
    public Func<StepContext, Side, string, CharacterRecord?>? Recruit { get; set; }
    /// <summary>
    /// <c>HasHarvester(owner, system)</c>: a completed harvester of <paramref name="owner"/> stands at the system. Null = false until the harvester
    /// exists in the core (M4/M6). rule: §Targets ("HasHarvester returns false").
    /// </summary>
    public Func<Side, string, bool>? HasHarvester { get; set; }
    /// <summary><c>Harvester.Destroy(system, Sabotage)</c> (owner given). Null = nothing to destroy. rule: §6 Spike the Harvester.</summary>
    public Action<StepContext, string, Side>? DestroyHarvester { get; set; }

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
        characters.InjuredResign = m.OnInjured;
        characters.JoinAsCaptive = m.JoinAsCaptive;
        characters.MissionReturnPlace = id => m.S.Missions.Values.FirstOrDefault(g => g.Characters.Contains(id) || g.DecoyCharacters.Contains(id))?.Home;
        characters.SpecialForcesInFleet = m.SpecialForcesInFleet;
        characters.SpecialForcesOnShip = m.SpecialForcesOnShip;
        fog.AgentsAt = m.AgentsAt;
        support.AgentIncidentTerm = m.IncidentTerm;
        support.IncidentFired = m.OnIncident;
        return m;
    }

    private MissionState S => _game.State.Missions;
    private EconomyState E => _game.State.Economy;
    private static Side Other(Side s) => s == Side.Hand ? Side.Collective : Side.Hand;

    // ================================================================== queries

    public IReadOnlyCollection<MissionGroup> Missions => S.Missions.Values;
    public MissionGroup? Get(int id) => S.Missions.TryGetValue(id, out var m) ? m : null;

    /// <summary>Is the character in a mission group (team or decoy, from acceptance until the group is back)? rule: §Who can go ("leaves its place").</summary>
    public bool IsOnMission(int character) => S.Missions.Values.Any(m => m.Characters.Contains(character) || m.DecoyCharacters.Contains(character)
        || m.Captives?.Contains(character) == true); // QA-7: a team's captive is in the mission too (the liberation check skips him)

    /// <summary>The mission whose team or decoys hold <paramref name="character"/> (0 = none).</summary>
    public int MissionOf(int character) => S.Missions.Values.FirstOrDefault(m => m.Characters.Contains(character) || m.DecoyCharacters.Contains(character))?.Id ?? 0;

    /// <summary>
    /// The character module's <c>JoinAsCaptive</c>: a new prisoner taken by a member of mission <paramref name="mission"/> joins its captured list,
    /// unless he already stands on ground of the captor's side and is not the target of an Abduction (0x546370 step 4, 0x534390). rule: §6 (B2e
    /// §3 "Where a new prisoner is held"; QA-7).
    /// </summary>
    public bool JoinAsCaptive(StepContext? ctx, CharacterRecord c, int mission)
    {
        if (!S.Missions.TryGetValue(mission, out var m)) return false;
        bool onCaptorGround = c.HeldAboard == 0 && c.HeldAt is { } at && _fleets.OwnerOf(at) == m.Side;
        if (onCaptorGround && !(m.Kind == MissionKind.Abduction && m.TargetCharacter == c.Id)) return false;
        (m.Captives ??= new()).Add(c.Id);
        return true;
    }

    /// <summary>
    /// The group leaves the state: its captives are held where it ends (<paramref name="at"/>; inferred, B2e §3) and get the liberation test there.
    /// rule: §5 (QA-7).
    /// </summary>
    private void RemoveGroup(StepContext? ctx, MissionGroup m, string? at)
    {
        S.Missions.Remove(m.Id);
        if (m.Captives == null) return;
        foreach (var id in m.Captives.OrderBy(x => x)) if (_ch.Get(id) is { } c) _ch.HoldCaptive(ctx, c, at);
    }

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

    /// <summary>
    /// FOILTB through the common lookup: the last row at or below x, the first row below it (99 below -20). rule: §3, §Mission tables (Traced B2a:
    /// 0x595090; 0x53e340 always finds a row).
    /// </summary>
    public int FoilChance(int x) => Lookup(Data.Foil, x, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue);

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
    /// <remarks>
    /// Talent is the current Talent (+0x8c, our EffectiveTalent), 0 for a character who is not a talented character and for a special force; Loyalty is the
    /// character's loyalty, or the special-force class's (SPECFCSD word 26, 100, variance 0). rule: §3 Talent sensing, Betrayal (Traced B2b).
    /// </remarks>
    private readonly record struct Agent(bool IsCharacter, int Id, int Espionage, int Combat, int Leadership, CharacterRecord? C, int Talent = 0, int Loyalty = 0);

    private Agent? CharAgent(int id) => _ch.Get(id) is { } c
        ? new Agent(true, id, _ch.EffectiveSkill(c, Skill.Espionage), _ch.EffectiveSkill(c, Skill.Combat), _ch.EffectiveSkill(c, Skill.Leadership), c, c.EffectiveTalent, c.Loyalty) : null; // B2e §1: effective skills

    private Agent? SfAgent(int id) => E.Objects.TryGetValue(id, out var o) && _troops.Data.SpecialForce(o.Class) is { } k
        ? new Agent(false, id, k.Espionage, k.Combat, k.Leadership, null, 0, k.LoyaltyBase) : null;

    /// <summary>Non-decoy members: characters in ascending id, then special forces in ascending object id. rule: §2 step 2.</summary>
    private List<Agent> Team(MissionGroup m) =>
        m.Characters.Select(CharAgent).Concat(m.SpecialForces.Select(SfAgent)).Where(a => a != null).Select(a => a!.Value).ToList();

    private List<Agent> Decoys(MissionGroup m) =>
        m.DecoyCharacters.Select(CharAgent).Concat(m.DecoySpecialForces.Select(SfAgent)).Where(a => a != null).Select(a => a!.Value).ToList();

    /// <summary>
    /// One guard: the unit, its class detection, and its commander's espionage and combat (0 when none). <see cref="Ship"/>: the ship a prisoner it
    /// catches is held aboard when it has no commander (the capital ship itself, or the ship a unit is aboard; 0 = none). rule: §3 Guards, Officer;
    /// m5-characters-and-talent §Escape (B2e §3, Q73).
    /// </summary>
    public sealed record Defender(EconObject Unit, int Detection, int OfficerEspionage, int OfficerCombat = 0, int Fleet = 0, int Ship = 0);

    /// <summary>The check points of a mission: phases 2 and 3 (leaving, at the origin), 7 (arriving) and 9 (each job). rule: §1 (Traced B2d §1).</summary>
    public enum CheckPoint { LeavePlanet = 0, LeaveOrbit = 1, Arrive = 2, Job = 3 }

    /// <summary>The kind of a check (one nibble of the kind word): 1 orbit, 2 planet, 3 orbit then planet in pass 2, 4 next to the target. rule: §1 (B2d §2).</summary>
    public enum CheckKind { None = 0, Orbit = 1, Planet = 2, OrbitThenPlanet = 3, NextToTarget = 4 }

    /// <summary>Which members count and which guards stand, for one check pass. rule: §3 (Traced B2d §3: 0x589a40, 0x5883b0).</summary>
    public readonly record struct Scope(bool PlanetMembers, bool OrbitMembers, bool PlanetGuards, bool OrbitGuards);

    public static int KindWord(MissionKind k) => k switch { MissionKind.Move => KindWordMove, MissionKind.Adrift => KindWordAdrift, _ => KindWordMission };
    public static CheckKind KindAt(int word, CheckPoint p) => (CheckKind)((word >> (4 * (int)p)) & 0xF);

    /// <summary>
    /// Kind 4's target position (0x521160, 0x4f6b50): in orbit for Spike the Harvester and for a target character riding a fleet; else on the planet
    /// (the system itself, a facility, a character on the surface). rule: §3 (Traced B2d §3).
    /// </summary>
    private bool TargetInOrbit(MissionGroup m) =>
        m.Kind == MissionKind.SpikeTheHarvester || (m.TargetCharacter is int tid && _ch.Get(tid) is { AttachedFleet: not 0 });

    /// <summary>
    /// The scopes of one check pass at <paramref name="place"/> (B2d §3): planet (kind 2): members on the planet; guards = the special forces on the
    /// planet if the other side holds it. Orbit (kind 1): members aboard, plus those on the planet when the mission travels; guards = the capital
    /// ships of every fleet there (and the regiments aboard) if the other side has a fleet there and the mission side has no Mordant Lien-150 there. Next to the
    /// target (kind 4): every member; guards as planet (target on the planet) or as orbit without the ion test (target in orbit).
    /// </summary>
    public Scope ScopeOf(MissionGroup m, CheckKind kind, string place)
    {
        Side s = m.Side, e = Other(s);
        bool enemyHeld = _fleets.OwnerOf(place) == e;
        bool enemyFleets = _game.State.Fleets.Values.Any(f => f.At == place && f.Side == e && f.Ships.Count + f.Squadrons.Count > 0);
        bool ownIon = FacilityFamily != null && E.Objects.Values.Any(o => o.Category == ObjectCategory.Facility && o.Completed && o.Side == s
            && o.System == place && FacilityFamily(o.Class) == IonCannonFamily);
        bool travels = m.Home != m.Target;
        return kind switch
        {
            CheckKind.Planet => new Scope(true, false, enemyHeld, false),
            CheckKind.Orbit => new Scope(travels, true, false, enemyFleets && !ownIon),
            CheckKind.NextToTarget => TargetInOrbit(m) ? new Scope(true, true, false, enemyFleets) : new Scope(true, true, enemyHeld, false),
            _ => default,
        };
    }

    /// <summary>
    /// The guards of a check pass, by object id (our DEFAULT order): with the planet scope, the completed special forces on the planet; with the orbit
    /// scope, the completed capital ships of the fleets at the place and the regiments aboard them (CountAboardUnits). Of any side with GuardsAnySide
    /// (DEFAULT), else not of <paramref name="side"/>; never the mission's own members. Planetary regiments, fighter squadrons, special forces aboard
    /// ships, facilities and characters never guard. Officer: the Admiral of the fleet for a ship, its General for a regiment aboard, none (0) for a
    /// special force on the planet (DEFAULT, the slot is not read). A special force has no detection column (DEFAULT 0). rule: §3 Guards (Traced B2d §3:
    /// iterator 0x587640, flags +0x28, +0x2c, +0x30).
    /// </summary>
    public List<Defender> Guards(Side side, string place, Scope sc, MissionGroup? m = null)
    {
        var shipFleet = new Dictionary<int, Fleet>(); // lookup only, never iterated
        foreach (var f in _game.State.Fleets.Values)
            if (f.At == place) foreach (var sh in f.Ships) shipFleet[sh] = f;
        bool Ok(EconObject o) => Params.GuardsAnySide || o.Side != side;
        var list = new List<Defender>();
        foreach (var o in E.Objects.Values) // SortedDictionary: id order
        {
            if (!o.Completed || !Ok(o)) continue;
            if (sc.PlanetGuards && o.Category == ObjectCategory.SpecialForce && o.AboardShip == 0 && o.System == place
                && !(m != null && (m.SpecialForces.Contains(o.Id) || m.DecoySpecialForces.Contains(o.Id))))
                list.Add(new Defender(o, 0, 0, 0));
            else if (sc.OrbitGuards && o.Category == ObjectCategory.CapitalShip && shipFleet.TryGetValue(o.Id, out var f))
            {
                var adm = Officer(_ch.HolderOf(f.Id, CharacterPost.Admiral));
                list.Add(new Defender(o, ShipDetection(o), Esp(adm), Com(adm), f.Id, adm == null ? o.Id : 0));
            }
            else if (sc.OrbitGuards && Params.CountAboardUnits && o.Category == ObjectCategory.Regiment && o.AboardShip != 0 && shipFleet.TryGetValue(o.AboardShip, out var rf))
            {
                var gen = Officer(_ch.HolderOf(rf.Id, CharacterPost.General));
                list.Add(new Defender(o, _troops.Data.Regiment(o.Class)?.Detection ?? 0, Esp(gen), Com(gen), rf.Id, gen == null ? o.AboardShip : 0));
            }
        }
        return list;
    }

    /// <summary>
    /// The guards of one check pass of <paramref name="m"/>: <see cref="Guards"/>, and for the job check of a key-target mission under
    /// <see cref="KeyTargetGuards.Full"/> (DEFAULT) also every unit of <see cref="FullGuards"/> not already there, by object id. rule: m5-missions
    /// §3, §6a (Key-target protections, 2026-10-18).
    /// </summary>
    private List<Defender> CheckGuards(MissionGroup m, CheckKind kind, string place, Scope sc)
    {
        var list = Guards(m.Side, place, sc, m);
        if (kind != CheckKind.NextToTarget || Params.KeyTargetGuards != KeyTargetGuards.Full || !IsKeyTarget(m.Kind, m.TargetCharacter, out _)) return list;
        var have = new HashSet<int>(list.Select(d => d.Unit.Id)); // membership only, never iterated
        list.AddRange(FullGuards(m.Side, place).Where(d => have.Add(d.Unit.Id)));
        return list.OrderBy(d => d.Unit.Id).ToList();
    }

    /// <summary>
    /// The guard set before B2d (PR #61): every completed capital ship of a fleet at <paramref name="place"/>, fighter squadron (aboard those ships,
    /// in a fleet there, or based there) and regiment (landed, or aboard ships there with CountAboardUnits) not of <paramref name="side"/>, by object
    /// id. Officer: the Free Admiral of the unit's fleet (ships, squadrons), the Free General of the fleet (regiments aboard) or of the system
    /// (landed regiments); a based squadron has none. Captor unit: that fleet, else the planet. rule: m5-missions §3, §6a (Key-target protections,
    /// 2026-10-18; the walk of 8a378a8^).
    /// </summary>
    private List<Defender> FullGuards(Side side, string place)
    {
        var shipFleet = new Dictionary<int, Fleet>(); // lookup only, never iterated
        var sqFleet = new Dictionary<int, Fleet>();   // lookup only
        foreach (var f in _game.State.Fleets.Values)
        {
            if (f.At != place) continue;
            foreach (var sh in f.Ships) shipFleet[sh] = f;
            foreach (var q in f.Squadrons) sqFleet[q] = f;
        }
        Defender Of(EconObject o, int det, CharacterRecord? officer, int fleet)
        {
            var c = Officer(officer);
            int ship = c != null || fleet == 0 ? 0 : o.Category == ObjectCategory.CapitalShip ? o.Id : o.AboardShip; // Q73: no commander, the ship holds him
            return new Defender(o, det, Esp(c), Com(c), fleet, ship);
        }
        var list = new List<Defender>();
        foreach (var o in E.Objects.Values) // SortedDictionary: id order
        {
            if (!o.Completed || o.Side == side) continue;
            switch (o.Category)
            {
                case ObjectCategory.CapitalShip when shipFleet.TryGetValue(o.Id, out var f):
                    list.Add(Of(o, ShipDetection(o), _ch.HolderOf(f.Id, CharacterPost.Admiral), f.Id));
                    break;
                case ObjectCategory.Fighter:
                    if (o.AboardShip != 0 && shipFleet.TryGetValue(o.AboardShip, out var cf)) list.Add(Of(o, ShipDetection(o), _ch.HolderOf(cf.Id, CharacterPost.Admiral), cf.Id));
                    else if (sqFleet.TryGetValue(o.Id, out var sf)) list.Add(Of(o, ShipDetection(o), _ch.HolderOf(sf.Id, CharacterPost.Admiral), sf.Id));
                    else if (o.AboardShip == 0 && o.System == place) list.Add(Of(o, ShipDetection(o), null, 0));
                    break;
                case ObjectCategory.Regiment:
                    int det = _troops.Data.Regiment(o.Class)?.Detection ?? 0;
                    if (o.AboardShip != 0) { if (Params.CountAboardUnits && shipFleet.TryGetValue(o.AboardShip, out var rf)) list.Add(Of(o, det, _ch.HolderOf(rf.Id, CharacterPost.General), rf.Id)); }
                    else if (o.System == place) list.Add(Of(o, det, _ch.SystemGeneralOf(place), 0));
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// The usable special forces of <paramref name="side"/> directly on <paramref name="system"/>: completed, not aboard a ship, not in a mission
    /// group (0x504cc0). The character module's escape attempt reads it. rule: m5-characters-and-talent §Escape (Traced B2e §3 step 3).
    /// </summary>
    public int SpecialForcesOnPlanet(Side side, string system) =>
        E.Objects.Values.Count(o => o.Completed && o.Side == side && o.Category == ObjectCategory.SpecialForce && o.AboardShip == 0 && o.System == system
            && !IsSpecialForceBusy(o.Id));

    /// <summary>
    /// The usable special forces of <paramref name="side"/> aboard the ships of fleet <paramref name="fleet"/>: completed, not in a mission group.
    /// The escape attempt of a prisoner held aboard reads it. rule: m5-characters-and-talent §Escape (QA-7 DEFAULT, B2e Work 12).
    /// </summary>
    public int SpecialForcesInFleet(Side side, int fleet)
    {
        if (!_game.State.Fleets.TryGetValue(fleet, out var f)) return 0;
        var ships = new HashSet<int>(f.Ships); // membership only, never iterated
        return E.Objects.Values.Count(o => o.Completed && o.Side == side && o.Category == ObjectCategory.SpecialForce && o.AboardShip != 0
            && ships.Contains(o.AboardShip) && !IsSpecialForceBusy(o.Id));
    }

    /// <summary>
    /// The usable special forces of <paramref name="side"/> directly aboard ship <paramref name="ship"/>: completed, not in a mission group (0x504cc0
    /// on the ship). The escape attempt of a prisoner a ship caught with no commander reads it. rule: m5-characters-and-talent §Escape (B2e §3, Q73).
    /// </summary>
    public int SpecialForcesOnShip(Side side, int ship) =>
        E.Objects.Values.Count(o => o.Completed && o.Side == side && o.Category == ObjectCategory.SpecialForce && o.AboardShip == ship && !IsSpecialForceBusy(o.Id));

    /// <summary>
    /// The guards a planet-target mission of <paramref name="side"/> meets at <paramref name="system"/>: those of the arriving check (orbit) and of the
    /// job check, by object id. For the AI's risk estimate. rule: §3 Guards.
    /// </summary>
    public List<Defender> Defenders(Side side, string system)
    {
        var probe = new MissionGroup { Side = side, Kind = MissionKind.Espionage, Home = "", Target = system };
        return Guards(side, system, ScopeOf(probe, CheckKind.Orbit, system), probe)
            .Concat(Guards(side, system, ScopeOf(probe, CheckKind.NextToTarget, system), probe))
            .GroupBy(d => d.Unit.Id).Select(g => g.First()).OrderBy(d => d.Unit.Id).ToList();
    }

    private int ShipDetection(EconObject o) => _fleets.ClassOf(o)?.Detection ?? 0;
    private static CharacterRecord? Officer(CharacterRecord? c) => c is { Status: CharacterStatus.Free } ? c : null;
    private int Esp(CharacterRecord? c) => c is { Status: CharacterStatus.Free } ? _ch.EffectiveSkill(c, Skill.Espionage) : 0;
    private int Com(CharacterRecord? c) => c is { Status: CharacterStatus.Free } ? _ch.EffectiveSkill(c, Skill.Combat) : 0;

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
        MissionKind.Diplomacy => _ch.EffectiveSkill(c, Skill.Diplomacy) > 0,
        MissionKind.Rescue or MissionKind.Abduction or MissionKind.Assassination => _ch.EffectiveSkill(c, Skill.Combat) > 0,
        MissionKind.Sabotage => SabotageSkillOf(_ch.EffectiveSkill(c, Skill.Combat), _ch.EffectiveSkill(c, Skill.Espionage)) > 0,
        MissionKind.SpikeTheHarvester => (_ch.EffectiveSkill(c, Skill.Combat) + _ch.EffectiveSkill(c, Skill.Espionage)) / 2 > 0,
        MissionKind.Espionage or MissionKind.Reconnaissance => _ch.EffectiveSkill(c, Skill.Espionage) > 0,
        MissionKind.Recruitment or MissionKind.InciteUprising or MissionKind.SubdueUprising => _ch.EffectiveSkill(c, Skill.Leadership) > 0,
        MissionKind.ShipResearch or MissionKind.FacilityResearch or MissionKind.TroopResearch => _ch.EffectiveSkill(c, ResearchSkill(k)!.Value) > 0,
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

    // Midwife's 50 when he is in the group, free, and no special force (team or decoy) is in it; else 100. rule: §1 (GNPRTB 3083; planets/11).
    private int GroupSpeed(IEnumerable<int> chars, int specialForces)
    {
        var n = Data.Numbers;
        bool midwife = specialForces == 0 && chars.Any(id => id == n.MidwifeCharacterId && _ch.Get(id) is { Status: CharacterStatus.Free });
        return midwife ? n.RunnerLines : n.GroupSpeed;
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
                ? tc.Side == side && tc.Status == CharacterStatus.Captured && tc.Captor == Other(side) && _ch.LocationOf(tc) != null // QA-7: aboard, only while his fleet stands at a system
                : tc.Side == Other(side) && tc.Status == CharacterStatus.Free && tc.Transit == null && _ch.LocationOf(tc) != null;
            if (!ok) return MissionReject.BadTarget;
            if (missionSystem != null && _ch.LocationOf(tc) != missionSystem) return MissionReject.BadTarget;
            return null;
        }
        if (system == null || !_fleets.Galaxy.TryFindSystem(system, out _)) return MissionReject.BadTarget;
        if (_eco.IsDestroyed(system)) return RejectReason.SystemDestroyed; // rule: m5-harvester §2 effect 5 (cannot be targeted by missions)
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
            case MissionKind.SpikeTheHarvester: return HasHarvester?.Invoke(Other(side), system) == true ? null : MissionReject.NoHarvester;
            default: return MissionReject.BadTarget;
        }
    }

    /// <summary>Is the character a key target of Assassination or Abduction now (switch on, in the data file)? rule: m5-missions §6a.</summary>
    public bool IsKeyTarget(MissionKind k, int? targetChar, out int cap)
    {
        cap = 0;
        return k is MissionKind.Assassination or MissionKind.Abduction && Params.KeyTargetRules && targetChar is int id && Data.KeyTargets.TryGetValue(id, out cap);
    }

    // rule: m5-missions §3, §6a (Key-target protections, 2026-10-18): with KeyTargetBaseSkills (DEFAULT on) a key-target mission reads its
    // characters' espionage and combat without Talent term of B2e §1 (espionage B, combat max(0, B - Injury)); special forces, other missions
    // and the switch off read the Agent's effective skills.
    private bool KeyBase(MissionGroup m, Agent a) => a.IsCharacter && Params.KeyTargetBaseSkills && IsKeyTarget(m.Kind, m.TargetCharacter, out _);
    private int KeyEsp(MissionGroup m, Agent a) => KeyBase(m, a) ? a.C!.BaseSkill(Skill.Espionage) : a.Espionage;
    private int KeyCom(MissionGroup m, Agent a) => KeyBase(m, a) ? Math.Max(0, a.C!.BaseSkill(Skill.Combat) - a.C!.Injury) : a.Combat;

    /// <summary>
    /// rule: m5-missions §6a.1 exposure gate: TargetProtected if the key target rides a fleet, or stands at the Hearth system (HQ at rest there)
    /// and the Collective's garrison there covers the required garrison N (m2-uprisings-and-garrison: G10 >= 10 * N, the same check as the uprising refresh).
    /// Checked at the order and at each iteration start, not at landing.
    /// </summary>
    private string? KeyGate(MissionKind k, int? targetChar)
    {
        if (!IsKeyTarget(k, targetChar, out _) || _ch.Get(targetChar!.Value) is not { } tc) return null;
        if (tc.AttachedFleet != 0) return MissionReject.TargetProtected;
        // rule: m5-missions §6a (Abduction paragraph): the protected capital is the Hearth system for a Collective target, Charter for a Hand target (Lead DEFAULT)
        var hq = _game.State.Scenario.Hq;
        string? capital = tc.Side == Side.Collective ? (hq.Status == HqStatus.AtRest ? hq.System : null) : Control.ControlModule.CapitalSystem;
        if (capital != null && _ch.LocationOf(tc) == capital && _fleets.OwnerOf(capital) == tc.Side
            && _support.Garrison10(capital) >= 10 * _support.RequiredGarrison(capital))
            return MissionReject.TargetProtected;
        return null;
    }

    private string? TargetReason(MissionGroup m) => TargetReason(m.Kind, m.Side, m.Target, m.TargetCharacter, m.TargetCharacter != null ? m.Target : null);

    // ================================================================== orders (step 1)

    /// <summary>
    /// The outcome of a StartMission dry run: the rejection reason (null = the order would be accepted now), and for an accepted order the
    /// home system, the target system, the path, its ticks and the arrival day A if it were judged in the next Step. Read-only.
    /// </summary>
    public sealed record StartCheck(string? Reason, string? Home = null, string? Target = null, IReadOnlyList<string>? Path = null, int Ticks = 0, int ArrivalDay = 0);

    /// <summary>
    /// Judge a StartMission order as step 1 would if the next Step began now, without changing anything (no draw, no id, no event): the same
    /// checks in the same order as the handler. For the screens (which kinds a team can do, a target's route and arrival day). rule: §Orders (StartMission).
    /// </summary>
    public StartCheck CheckStart(StartMissionOrder o)
    {
        var (reason, plan) = Validate(o);
        if (reason != null) return new StartCheck(reason);
        var p = plan!.Value;
        int ticks = p.Trip.Ticks;
        return new StartCheck(null, p.Home, p.Target, p.Trip.Path.ToList(), ticks, ticks == 0 ? _game.Day + 1 : _game.Day + ticks);
    }

    private readonly record struct StartPlan(string Home, string Target, int? Facility, (List<string> Path, int Ticks) Trip, bool CharTarget,
        List<int> Chars, List<int> Sfs, List<int> DChars, List<int> DSfs);

    // rule: §Orders (StartMission), rejections in the sheet's order. RuleMissing (ours) comes before the target checks: a decoy while the decoy
    // table is Missing, Recruitment while the recruit pick is Missing; Train Talent right after WrongSide (the two sheets each name the other as owner).
    // Shared by the handler and CheckStart: reads only.
    private (string? Reason, StartPlan? Plan) Validate(StartMissionOrder o)
    {
        if (!Data.TryGet(o.Kind, out var rec)) return (MissionReject.UnknownKind, null);
        if (rec.Hidden) return (MissionReject.HiddenKind, null);
        if (!rec.AvailableTo(o.Side)) return (MissionReject.WrongSide, null);
        if (o.Kind == MissionKind.TrainTalent) return (MissionReject.RuleMissing, null);
        var chars = o.Characters ?? new List<int>(); var sfs = o.SpecialForces ?? new List<int>();
        var dSfs = o.Decoys ?? new List<int>(); var dChars = o.DecoyCharacters ?? new List<int>();
        if (CheckMembers(o.Side, chars, sfs, dChars, dSfs, out var home) is { } bad) return (bad, null);
        foreach (var id in chars) if (!CanDo(_ch.Get(id)!, o.Kind)) return (MissionReject.NotAbleToDo, null);
        foreach (var id in sfs)
            if (_troops.Data.SpecialForce(E.Objects[id].Class) is not { } k || (k.MissionBit & rec.SpecialForceMask) == 0)
                return (MissionReject.NotEligibleSpecialForce, null);
        bool anyDecoy = dSfs.Count + dChars.Count > 0;
        if (anyDecoy && !rec.Foilable) return (MissionReject.BadDecoy, null);
        if (dChars.Count > 0 && Params.DecoysFromSpecialForcesOnly) return (MissionReject.BadDecoy, null);
        if ((Params.MaxTeamMembers > 0 && chars.Count + sfs.Count > Params.MaxTeamMembers) || (Params.MaxDecoys > 0 && dSfs.Count + dChars.Count > Params.MaxDecoys))
            return (MissionReject.TooManyMembers, null);
        if (anyDecoy && Data.DecoyTable == null) return (MissionReject.RuleMissing, null);
        if (o.Kind == MissionKind.Recruitment && Recruit == null) return (MissionReject.RuleMissing, null);

        bool charTarget = o.Kind is MissionKind.Rescue or MissionKind.Abduction or MissionKind.Assassination;
        if (TargetReason(o.Kind, o.Side, o.TargetSystem, o.TargetCharacter, null) is { } tr) return (tr, null);
        if (KeyGate(o.Kind, o.TargetCharacter) is { } protectedReason) return (protectedReason, null); // rule: m5-missions §6a.1
        string target = charTarget ? _ch.LocationOf(_ch.Get(o.TargetCharacter!.Value)!)! : _fleets.Galaxy.Get(o.TargetSystem!).Name;
        int? facility = null;
        if (o.Kind == MissionKind.Sabotage && Params.SabotageTargetMode == SabotageTargetMode.Chosen)
        {
            if (o.Facility is not int fid || !E.Objects.TryGetValue(fid, out var fo) || fo.Category != ObjectCategory.Facility || !fo.Completed || fo.System != target)
                return (MissionReject.BadTarget, null);
            facility = fid;
        }
        var path = Trip(home!, target, GroupSpeed(chars.Concat(dChars), sfs.Count + dSfs.Count));
        if (path == null) return (RejectReason.Unreachable, null);
        return (null, new StartPlan(home!, target, facility, path.Value, charTarget, chars, sfs, dChars, dSfs));
    }

    private OrderResult HandleStart(StepContext ctx, StartMissionOrder o)
    {
        var (reason, plan) = Validate(o);
        if (reason != null) return OrderResult.Reject(reason);
        var (home, target, facility, path, charTarget, chars, sfs, dChars, dSfs) = plan!.Value;

        var m = new MissionGroup
        {
            Id = ObjectRegistry.NextId(E), Kind = o.Kind, Side = o.Side,
            Characters = chars.OrderBy(x => x).ToList(), SpecialForces = sfs.OrderBy(x => x).ToList(),
            DecoyCharacters = dChars.OrderBy(x => x).ToList(), DecoySpecialForces = dSfs.OrderBy(x => x).ToList(),
            Home = home, Target = target, TargetCharacter = charTarget ? o.TargetCharacter : null, Facility = facility,
            Phase = MissionPhase.Outbound,
        };
        SetTrip(m, path, ctx.Day);
        S.Missions[m.Id] = m;
        Started(ctx, m);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence));
    }

    // rule: §Orders (MoveGroup): the members' checks, the Move record's special-force mask (all but Repossessors), then UnknownSystem,
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

    /// <summary>A capture or death ended a character's membership (the character module calls it). rule: m5-characters-and-talent §Capture 2, 3.</summary>
    public void OnMembershipEnded(StepContext? ctx, CharacterRecord c)
    {
        foreach (var m in S.Missions.Values)
        {
            m.Characters.Remove(c.Id);
            m.DecoyCharacters.Remove(c.Id);
            m.Captives?.Remove(c.Id);
        }
    }

    /// <summary>
    /// The harvester destroyed <paramref name="system"/>: every group that is working there ends as a failure with cause TargetDestroyed
    /// (MissionEnded) and leaves the state; a group about to return from there just leaves the state. Their characters stay on the system as free
    /// characters for the harvester's roll on characters (their special forces go with the planet). A group still flying out lands later and ends
    /// the same way (<see cref="Land"/>). rule: m5-harvester §2 effect 3 (running missions end as failures with cause TargetDestroyed, team members
    /// handled as characters on the planet).
    /// </summary>
    public void OnSystemDestroyed(StepContext ctx, string system)
    {
        int t = ctx.Day + 1;
        foreach (var m in S.Missions.Values.Where(m => m.Target == system).ToList()) // mission id order
        {
            if (m.Phase == MissionPhase.Working)
            {
                int id = m.Id; var side = m.Side; var kind = m.Kind;
                ctx.Emit((i, _) => new MissionEnded(i, t, id, side, kind, system, MissionOutcome.TargetDestroyed));
                RemoveGroup(ctx, m, system);
            }
            else if (m.Phase == MissionPhase.ReturnPending) RemoveGroup(ctx, m, system);
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
                // rule: §1 (Traced B2d §1): phases 2 and 3 check the team at the origin, before it leaves (planet, then orbit)
                if (Params.MissionGuardChecks && m.Kind != MissionKind.Move)
                {
                    var rec = Data.Get(m.Kind);
                    if (RunCheck(ctx, m, k + 1, CheckPoint.LeavePlanet, rec) || RunCheck(ctx, m, k + 1, CheckPoint.LeaveOrbit, rec)) continue;
                }
                m.Departed = true;
                if (m.TotalTicks > 0) PlaceMembers(m, null);
                Departed(ctx, m, false);
            }
            else if (m.Phase == MissionPhase.ReturnPending)
            {
                if (Empty(m)) { RemoveGroup(ctx, m, m.Target); continue; }
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
    /// (the group stays). rule: §5 (DEFAULT; Classic's Return and Adrift targets are Missing).
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
        RemoveGroup(ctx, m, at);
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
                if (Empty(m)) { RemoveGroup(ctx, m, m.Path[^1]); continue; }
                Release(ctx, m, m.Path[^1], t);
            }
            else if (m.Phase == MissionPhase.Outbound) Land(ctx, m, t);
        }
    }

    // rule: §1 Step 3: the group lands, draws the first timer T = min + rand(0..spread) (no draw for a zero spread), due = A + T, MissionLanded.
    // A character target that is no longer there ends the mission TargetInvalid (no draw). The arriving check (phase 7, orbit) runs before the
    // timer (phase 8). rule: §1 (Traced B2d §1).
    private void Land(StepContext ctx, MissionGroup m, int t)
    {
        // rule: m5-harvester §2 effect 3: a mission whose target was destroyed on the way ends as a failure (cause TargetDestroyed)
        if (m.Kind != MissionKind.Move && _eco.IsDestroyed(m.Target)) { End(ctx, m, MissionOutcome.TargetDestroyed, t); return; }
        PlaceMembers(m, m.Target);
        if (m.Kind == MissionKind.Move) { Release(ctx, m, m.Target, t); return; }
        if (!m.HasTeam) { End(ctx, m, MissionOutcome.Cancelled, t); return; }
        if (m.TargetCharacter != null && TargetReason(m) != null) { End(ctx, m, MissionOutcome.TargetInvalid, t); return; }
        var rec = Data.Get(m.Kind);
        if (Params.MissionGuardChecks && RunCheck(ctx, m, t, CheckPoint.Arrive, rec)) return;
        m.Due = t + Timer(ctx, rec);
        m.Phase = MissionPhase.Working;
        int id = m.Id, due = m.Due; var side = m.Side; string target = m.Target;
        ctx.Emit((i, _) => new MissionLanded(i, t, id, side, target, due));
    }

    private static int Timer(StepContext ctx, MissionRecord rec) => rec.TimerMin + (rec.TimerSpread > 0 ? ctx.Rng.NextInt(0, rec.TimerSpread) : 0);
}
