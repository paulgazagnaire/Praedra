using System;
using System.Collections.Generic;
using System.Linq;
using Praedra.Core.AI;
using Praedra.Core.Assault;
using Praedra.Core.Battle;
using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using Praedra.Core.Support;
using Praedra.Core.Troops;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

public enum MsgKind { Info, Good, Bad, Warn, Battle }

/// <summary>One line of the message log. <see cref="System"/> is the place the click centres the map on; <see cref="Detail"/> is a longer report.</summary>
public sealed record Message(int Day, string Text, string System, MsgKind Kind, string Title = null, string Detail = null, bool Combat = false);

/// <summary>What one day did, for the game loop.</summary>
public sealed class StepOutcome
{
    public List<Message> New { get; } = new();
    /// <summary>Why the loop should pause now (null = keep going).</summary>
    public string PauseReason { get; set; }
    /// <summary>A report to show at once (battle, capture), or null.</summary>
    public Message Popup { get; set; }
    public Side? Winner { get; set; }
}

/// <summary>
/// The running game as the UI sees it: the core's host, which side the player is, the opponent, the message log and the player's queued orders.
/// It never decides a rule: it submits orders, calls opponent.PlanDay() and Game.Step(), and turns the core's events into plain text.
/// </summary>
public sealed partial class GameSession
{
    public GameHost Host { get; }
    public GameData Data => Host.Data;
    public Game Game => Host.Game;
    public Side Player { get; }
    /// <summary>How well the computer opponent plays (kept in the save's first line).</summary>
    public AiLevel Level { get; }
    public Side Enemy => Player == Side.Hand ? Side.Collective : Side.Hand;
    public SimpleAi Opponent { get; }
    /// <summary>Test only (the headless playtest): the computer side plans nothing, so a test fleet it would scrap or move stays put.</summary>
    public bool OpponentIdle { get; set; }
    public List<Message> Messages { get; } = new();
    public Side? Winner { get; private set; }

    // What each order the player queued was, by sequence number, so a refusal can be explained in words.
    readonly Dictionary<long, string> _pending = new();
    readonly Dictionary<long, int> _pendingFleet = new();
    readonly Dictionary<int, PlannedMove> _plannedMoves = new();
    // Character orders: which character each queued order is about, and the sequences that are character orders (their refusals use WhyCharacter).
    readonly Dictionary<long, int> _pendingChar = new();
    readonly HashSet<long> _charOrders = new();
    readonly Dictionary<int, PlannedMove> _plannedCharMoves = new();
    int _printed; // index into the fog's event feed for the player's side (EventsFor), not the unfiltered list
    // Names as they were before the day ran (a destroyed ship or a dissolved fleet is gone afterwards).
    readonly Dictionary<int, string> _classOf = new();
    readonly Dictionary<int, (string Name, Side Side)> _fleetOf = new();
    readonly Dictionary<int, int> _fleetOfUnit = new();
    // Battles being collected while the events of one day are read.
    readonly Dictionary<string, BattleDraft> _battles = new(StringComparer.Ordinal);
    string _lastContact;

    sealed class BattleDraft
    {
        public List<int> Collective = new(), Hand = new();
        public List<(int Unit, Side Side)> Lost = new();
        public List<string> Retreats = new();
    }

    public GameSession(GameHost host, Side player, AiLevel level = AiLevel.Medium)
    {
        Host = host; Player = player; Level = level;
        Opponent = GameBoot.CreateOpponent(host, player == Side.Hand ? Side.Collective : Side.Hand, level);
        _printed = host.Fog.EventsFor(player).Count;
        Snapshot();
    }

    /// <summary>
    /// A new game. <paramref name="seed"/> null = a fresh seed from the wall clock, so every game deals a different map (the core must not read the clock;
    /// the viewer may). A fixed seed (tests, smoke, playtest, `--seed N`) reproduces a game. rule: m5-setup-and-victory §Mode Anchored (the deal is seeded)
    /// </summary>
    public static GameSession New(GameData data, Side player, Preset preset, Praedra.Core.Battle.BattleRuleset rules = Praedra.Core.Battle.BattleRuleset.New, int? seed = null) =>
        new(GameBoot.NewGame(data, player, preset, seed ?? FreshSeed(), rules), player, GameBoot.AiLevelFor(preset));

    /// <summary>A seed for a new game from the wall clock (viewer only).</summary>
    public static int FreshSeed() => (int)(Godot.Time.GetUnixTimeFromSystem() % int.MaxValue);

    public static GameSession Load(GameData data, Side player, string json, AiLevel level = AiLevel.Medium) =>
        new(GameBoot.LoadGame(data, json), player, level);

    /// <summary>The core's save plus the battle ruleset header, so Load fights by the same rules.</summary>
    public string SaveText() => Host.SaveWithSettings();

    /// <summary>The space battle rules of this game.</summary>
    public Praedra.Core.Battle.BattleRuleset Rules => Host.Battles.Params.Ruleset;

    // ------------------------------------------------------------------ queries the panels use

    public Side? OwnerOf(string system) => Host.Fleets.OwnerOf(system);
    public string NameOf(Side s) => s == Side.Hand ? "Hand" : "Collective";
    public string You(Side s) => s == Player ? $"{NameOf(s)} (you)" : NameOf(s);

    public int QueuedOrders => Game.Queue.Count(o => o.Side == Player);

    // ------------------------------------------------------------------ fog of war: what the player's side knows (rule: m5-fog-and-hq §N8)

    public FogModule Fog => Host.Fog;

    /// <summary>Is the system explored in the player's picture? Unexplored systems show nothing but their name and lanes.</summary>
    public bool Explored(string system) => Fog.IsExplored(Player, system);

    /// <summary>Does the player's side see the system live today (holds it, or has a fleet there)?</summary>
    public bool Sees(string system) => Fog.Sees(Player, system);

    /// <summary>The player's picture of a system (null when unexplored).</summary>
    public SystemPicture Picture(string system) => Fog.Picture(Player, system);

    /// <summary>
    /// The holder as the player's side knows it: the player's own systems always (own state is truth), otherwise the holder in the side's
    /// picture (possibly out of date). <c>known</c> is false when the picture has no owner group yet (unexplored).
    /// </summary>
    public (bool Known, Side? Owner) KnownOwnerOf(string system)
    {
        if (OwnerOf(system) == Player) return (true, Player);
        var o = Picture(system)?.Owner;
        if (o == null) return (false, null);
        // The loser is told at once (LossNotice), so a picture can only say "yours" for a system that is yours; guard anyway.
        return (true, o.Value.Owner == Player ? null : o.Value.Owner);
    }

    /// <summary>The day the player's side last looked at the system (live look or a report), or null.</summary>
    public int? LastSeenDay(string system)
    {
        var p = Picture(system);
        if (p == null) return null;
        int? best = p.LastLookDay;
        foreach (int? st in new[] { p.Owner?.Stamp, p.Status?.Stamp, p.Facilities?.Stamp, p.Fleets?.Stamp })
            if (st is int v && (best == null || v > best)) best = v;
        return best;
    }

    /// <summary>The enemy fleets the player's side sees now at a system (class counts only, no names or ids).</summary>
    public IReadOnlyList<FleetPicture> VisibleEnemyFleetsAt(string system) =>
        Fog.VisibleFleets(Player).Where(v => v.System == system).Select(v => v.Fleet).ToList();

    /// <summary>The Hearth as the Collective's own object (status, where, route). Read only for a Collective player: it is that side's own.</summary>
    public HqState OwnHq => Player == Side.Collective ? Game.State.Scenario.Hq : null;

    /// <summary>The day the HQ arrives, while it is moving.</summary>
    public static int HqArrivalDay(HqState h)
    {
        int day = h.DueDay;
        for (int i = h.HopIndex + 1; i < h.HopTicks.Count; i++) day += h.HopTicks[i];
        return day;
    }

    /// <summary>
    /// The player's own headquarters system (Hand: Charter; Collective: where the HQ stands), or null (the HQ is moving or destroyed).
    /// </summary>
    public string MyHqSystem => Player == Side.Hand ? GameBoot.HandCapital
        : (OwnHq.Status == HqStatus.AtRest ? OwnHq.System : null);

    /// <summary>
    /// The enemy's headquarters as the player's side knows it: the Collective always knows Charter; the Hand only the system its own
    /// picture shows the Hearth at (KnownHq, rule: m5-fog-and-hq §N5), else null = unknown.
    /// </summary>
    public string EnemyHqSystem => Player == Side.Collective ? GameBoot.HandCapital : Fog.KnownHq(Side.Hand)?.System;

    /// <summary>A MoveHq order queued and not yet judged (destination), or null.</summary>
    public string PendingHqMove { get; private set; }

    /// <summary>Queue the MoveHq order (Collective). The core judges it when the next day starts. rule: m5-fog-and-hq §F6.</summary>
    public long SubmitMoveHq(string destination)
    {
        PendingHqMove = destination;
        return Track(Fog.SubmitMoveHq(Player, destination), $"Move the HQ to {destination}");
    }

    // ------------------------------------------------------------------ orders

    /// <summary>Remember what a queued player order was, for the refusal message. Returns the sequence number for chaining.</summary>
    public long Track(long sequence, string what)
    {
        _pending[sequence] = what;
        return sequence;
    }

    /// <summary>Like <see cref="Track"/>, and remember which fleet the order belongs to (shown as "waiting for the next day" on the fleet).</summary>
    public long TrackFleet(int fleet, long sequence, string what)
    {
        _pendingFleet[sequence] = fleet;
        return Track(sequence, what);
    }

    /// <summary>Plain-language orders given to this fleet that the core will carry out when the next day starts.</summary>
    public IEnumerable<string> PendingOf(int fleet) =>
        _pendingFleet.Where(kv => kv.Value == fleet && _pending.ContainsKey(kv.Key)).OrderBy(kv => kv.Key).Select(kv => _pending[kv.Key]);

    /// <summary>A move order that was given but not yet started (it starts with the next day).</summary>
    public readonly record struct PlannedMove(string To, IReadOnlyList<string> Path, int Days, int ArrivalDay);

    public void PlanMove(int fleet, PlannedMove m) => _plannedMoves[fleet] = m;
    public PlannedMove? PlannedMoveOf(int fleet) => _plannedMoves.TryGetValue(fleet, out var m) ? m : null;
    public IEnumerable<KeyValuePair<int, PlannedMove>> PlannedMoves => _plannedMoves;

    public void Say(string text, MsgKind kind = MsgKind.Info, string system = null) =>
        Messages.Add(new Message(Game.Day, text, system, kind));

    // ------------------------------------------------------------------ the day

    /// <summary>One day: the opponent plans, the core steps, the events become messages. The only thing that advances the game.</summary>
    public StepOutcome StepDay()
    {
        Snapshot();
        if (!OpponentIdle) Opponent.PlanDay();
        Game.Step();
        var outcome = new StepOutcome();
        ReadEvents(outcome);
        // Every order given before this day was judged in this Step: nothing is "waiting" any more.
        _pending.Clear(); _pendingFleet.Clear(); _plannedMoves.Clear(); PendingHqMove = null;
        _pendingChar.Clear(); _charOrders.Clear(); _plannedCharMoves.Clear();
        _pendingMissions.Clear(); _missionOrders.Clear();
        Winner = GameBoot.WinnerOf(Host);
        if (Winner is Side w)
        {
            outcome.Winner = w;
            outcome.PauseReason = outcome.PauseReason ?? "victory";
        }
        return outcome;
    }

    void Snapshot()
    {
        _classOf.Clear(); _fleetOf.Clear(); _fleetOfUnit.Clear();
        foreach (var o in Host.Economy.Objects)
            if (o.Category is ObjectCategory.CapitalShip or ObjectCategory.Fighter or ObjectCategory.Regiment or ObjectCategory.SpecialForce or ObjectCategory.Facility)
                _classOf[o.Id] = o.Class;
        SnapshotMissions();
        foreach (var f in Game.State.Fleets.Values)
        {
            _fleetOf[f.Id] = (f.Name, f.Side);
            foreach (int id in f.Ships) _fleetOfUnit[id] = f.Id;
            foreach (int id in f.Squadrons) _fleetOfUnit[id] = f.Id;
        }
    }

    string FleetName(int id) => _fleetOf.TryGetValue(id, out var f) ? f.Name : (Game.State.Fleets.TryGetValue(id, out var g) ? g.Name : $"fleet {id}");
    Side? FleetSide(int id) => _fleetOf.TryGetValue(id, out var f) ? f.Side : (Game.State.Fleets.TryGetValue(id, out var g) ? g.Side : null);
    string ClassName(int id) => _classOf.TryGetValue(id, out var c) ? c : $"unit {id}";

    string Group(IEnumerable<int> units)
    {
        var parts = units.GroupBy(ClassName).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} x {g.Key}");
        string s = string.Join(", ", parts);
        return s.Length == 0 ? "nothing" : s;
    }

    string FleetsOf(IEnumerable<int> units) =>
        string.Join(", ", units.Where(_fleetOfUnit.ContainsKey).Select(u => FleetName(_fleetOfUnit[u])).Distinct().OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>The words for a core refusal reason.</summary>
    public static string Why(string reason) => reason switch
    {
        RejectReason.UnknownFleet => "that fleet no longer exists",
        RejectReason.NotYours => "it is not yours",
        RejectReason.InTransit => "the fleet is travelling (wait until it arrives)",
        RejectReason.Immobile => "it cannot move (no working hyperdrive)",
        RejectReason.AlreadyThere => "it is already there",
        RejectReason.UnknownSystem => "unknown system",
        RejectReason.Unreachable => "there is no route there",
        RejectReason.NoYard => "this system has no suitable yard",
        RejectReason.WrongFamily => "this yard cannot build that",
        RejectReason.NotAvailable => "your side cannot build that",
        RejectReason.NotResearched => "not researched yet",
        RejectReason.BadCount => "bad amount",
        RejectReason.BadDestination => "bad destination (it must be one of your systems)",
        RejectReason.NoSlot => "no free slot on the planet for it",
        RejectReason.OnePerPlanet => "a planet can hold only one Sensor Station",
        RejectReason.UnknownItem => "unknown item",
        RejectReason.NoProject => "nothing is being built there",
        RejectReason.UnknownObject => "unknown object",
        RejectReason.NotCompleted => "not finished yet",
        RejectReason.UnknownShip => "unknown ship",
        RejectReason.UnknownSquadron => "unknown squadron",
        RejectReason.NotSameSystem => "they are not at the same system",
        RejectReason.TooFewShips => "the fleet would be left without ships",
        RejectReason.NoChange => "nothing would change",
        RejectReason.EmptySelection => "nothing selected",
        RejectReason.WholeFleet => "that is the whole fleet (select fewer ships)",
        RejectReason.SameFleet => "that is the same fleet",
        RejectReason.NoCapacity => "no room aboard",
        RejectReason.NotAboard => "not aboard",
        RejectReason.NotYourSystem => "it must be one of your systems",
        RejectReason.UnknownRegiment => "unknown regiment",
        RejectReason.AlreadyAboard => "already aboard",
        RejectReason.BadValue => "bad value",
        BattleReject.AiSide => "the computer side cannot set that",
        BombardReject.NotAtSystem => "the fleet is not at that system",
        BombardReject.OwnSystem => "you cannot attack your own system",
        BombardReject.EnemyFleetPresent => "an enemy fleet is in the way (win the battle first)",
        BombardReject.NoBombardPower => "this fleet cannot bombard",
        BombardReject.OrderPending => "that fleet already has an attack order",
        HqRejectReason.NotCollective => "only the Collective has a headquarters to move",
        HqRejectReason.NoHq => "there is no headquarters any more",
        HqRejectReason.NotHeld => "the headquarters can only move to a system you hold",
        HqRejectReason.Pinned => "a large enemy blockade pins the headquarters where it is (break the blockade first)",
        HqRejectReason.NoFunds => "not enough refined material to move the headquarters",
        AssaultReject.NoTroops => "no regiments aboard",
        AssaultReject.FullyShielded => "the planet's shields are too strong (bombard the shield generators first)",
        Praedra.Core.Harvester.HarvesterReject.NoHarvester => "the fleet holds no finished harvester",
        Praedra.Core.Harvester.HarvesterReject.AlreadyDestroyed => "that system is already destroyed",
        Praedra.Core.Harvester.HarvesterReject.HarvesterLimit => "only one harvester per system: this system already has one (build it at another shipyard)",
        "SystemDestroyed" => "that system was destroyed by the harvester",
        _ => reason,
    };

    /// <summary>The words for a refused character order (the shared reasons read differently for a character). rule: m5-characters-and-talent §Posts (Orders), §Moving a character alone.</summary>
    public static string WhyCharacter(string reason) => reason switch
    {
        CharacterReject.UnknownCharacter => "that character is not in the game",
        CharacterReject.NotFree => "the character is not free (injured, captured or dead)",
        CharacterReject.NotAtSystem => "the character is not at that system (wait until it gets there)",
        CharacterReject.FleetInTransit => "the fleet is travelling (wait until it arrives)",
        CharacterReject.AlreadyAttached => "the character is already aboard a fleet (detach it first)",
        CharacterReject.OnMission => "the character is on a mission",
        CharacterReject.HoldsSystemPost => "the character is a system General (release that post first)",
        CharacterReject.NoCapability => "the character cannot hold that post",
        CharacterReject.PostTaken => "that post is already taken",
        CharacterReject.NotAttached => "the character is not aboard a fleet",
        CharacterReject.AlreadyHoldsSystemPost => "the character is already the General of another system",
        CharacterReject.NoSystemGeneral => "that system has no General",
        CharacterReject.Attached => "the character is aboard a fleet (detach it first, or move the fleet)",
        CharacterReject.NoPath => "there is no route there",
        RejectReason.InTransit => "the character is already travelling",
        RejectReason.AlreadyThere => "the character is already there",
        RejectReason.NotYourSystem => "the system must be one of yours",
        _ => Why(reason),
    };

    // ------------------------------------------------------------------ characters (the player's own side only: rule m5-fog-and-hq §N8 OwnState)

    public CharacterModule Chars => Host.Characters;

    public string CharName(int id) => Chars.Data.TryGet(id, out var c) ? c.Name : $"character {id}";

    /// <summary>The player's characters in play, by name. Characters of the other side are never listed: the fog API does not say where they are.</summary>
    public IReadOnlyList<CharacterRecord> MyCharacters =>
        Chars.InPlay.Where(c => c.Side == Player).OrderBy(c => CharName(c.Id), StringComparer.Ordinal).ThenBy(c => c.Id).ToList();

    /// <summary>The system the character is General of, or null.</summary>
    public string SystemPostOf(CharacterRecord c) =>
        Game.State.Characters.SystemGenerals.Where(kv => kv.Value == c.Id).Select(kv => kv.Key).FirstOrDefault();

    /// <summary>Remember a queued character order (and the fleet it concerns, or -1) for the panels and the refusal message.</summary>
    public long TrackCharacter(int character, int fleet, long sequence, string what)
    {
        _pendingChar[sequence] = character;
        _charOrders.Add(sequence);
        if (fleet >= 0) _pendingFleet[sequence] = fleet;
        return Track(sequence, what);
    }

    public IEnumerable<string> PendingOfCharacter(int character) =>
        _pendingChar.Where(kv => kv.Value == character && _pending.ContainsKey(kv.Key)).OrderBy(kv => kv.Key).Select(kv => _pending[kv.Key]);

    public void PlanCharacterMove(int character, PlannedMove m) => _plannedCharMoves[character] = m;
    public PlannedMove? PlannedCharacterMoveOf(int character) => _plannedCharMoves.TryGetValue(character, out var m) ? m : null;

    /// <summary>The last day of a trip (the next stop plus the remaining hops).</summary>
    public static int ArrivalDay(FleetTransit t)
    {
        int day = t.DueDay;
        for (int h = t.HopIndex + 1; h < t.HopTicks.Count; h++) day += t.HopTicks[h];
        return day;
    }

    public static string StatusWords(CharacterRecord c) => c.Status switch
    {
        CharacterStatus.Injured => c.Injury > 0 ? $"injured ({c.Injury} injury points)" : "injured",
        CharacterStatus.Captured => "captured",
        CharacterStatus.Dead => "dead",
        _ => "free",
    };

    /// <summary>Where one of the player's characters is, in words (system, fleet, or travelling with the arrival day).</summary>
    public string WhereIsCharacter(CharacterRecord c)
    {
        if (c.Status == CharacterStatus.Dead) return c.System != null ? $"fell at {c.System}" : "dead";
        if (c.Status == CharacterStatus.Captured) return $"held prisoner at {c.HeldAt ?? "an unknown place"} by the {(c.Captor is Side cap ? NameOf(cap) : "enemy")}";
        if (MissionOf(c.Id) is { } mg) return MissionWhere(mg);
        if (c.AttachedFleet != 0 && Game.State.Fleets.TryGetValue(c.AttachedFleet, out var f))
        {
            if (f.Transit is { } ft && f.At == null) return $"aboard fleet {f.Name}, travelling to {ft.Path[^1]}, arrives day {ArrivalDay(ft)}";
            return $"aboard fleet {f.Name} at {f.At}";
        }
        if (c.Transit is { } t && c.System == null) return $"travelling alone to {t.Path[^1]}, arrives day {ArrivalDay(t)}";
        if (PlannedCharacterMoveOf(c.Id) is { } pm) return $"at {c.System}; leaves when the next day starts for {pm.To}, arrives day {pm.ArrivalDay}";
        if (c.Transit is { } t2) return $"at {c.System}; leaving for {t2.Path[^1]}, arrives day {ArrivalDay(t2)}";
        return $"at {c.System}";
    }

    /// <summary>The system to centre the map on for a character (where it is, or the next stop of its trip), or null.</summary>
    public string MapPlaceOf(CharacterRecord c)
    {
        var at = Chars.LocationOf(c);
        if (at != null) return at;
        if (MissionOf(c.Id) is { } mg) return MissionMapPlace(mg);
        if (c.AttachedFleet != 0 && Game.State.Fleets.TryGetValue(c.AttachedFleet, out var f) && f.Transit is { } ft) return ft.Path[ft.HopIndex + 1];
        return c.Transit is { } t ? t.Path[Math.Min(t.HopIndex + 1, t.Path.Count - 1)] : c.System;
    }

    /// <summary>The character's post in words. rule: m5-characters-and-talent §Posts.</summary>
    public string RoleOf(CharacterRecord c)
    {
        if (SystemPostOf(c) is string sys) return $"system General of {sys}";
        if (c.AttachedFleet != 0)
        {
            string fn = FleetName(c.AttachedFleet);
            return c.Post == CharacterPost.None ? $"passenger on fleet {fn}" : $"fleet {PostWords(c.Post)} of {fn}";
        }
        return "no post";
    }

    /// <summary>The skills the sheet uses (leadership feeds the posts), and Talent rank only when the side knows the character is a talented character. rule: m5-characters-and-talent §Posts, §Talent (Rank).</summary>
    public string SkillsOf(CharacterRecord c)
    {
        string s = $"Leadership {Chars.EffectiveSkill(c, Skill.Leadership)}, combat {Chars.EffectiveSkill(c, Skill.Combat)}, diplomacy {Chars.EffectiveSkill(c, Skill.Diplomacy)}, espionage {Chars.EffectiveSkill(c, Skill.Espionage)}";
        if (c.TalentHolder && c.TalentDiscovered && Chars.RankOf(c) is TalentRank r) s += $"; Talent: {RankWords(r)} ({c.EffectiveTalent})";
        return s;
    }

    /// <summary>Which posts the character's record allows. rule: m5-characters-and-talent §Posts (Capability).</summary>
    public string CapabilityOf(CharacterRecord c)
    {
        var cls = Chars.ClassOf(c);
        var can = new List<string>();
        if (cls.CanAdmiral) can.Add("Admiral");
        if (cls.CanCommander) can.Add("Commander");
        if (cls.CanGeneral) can.Add("General");
        return can.Count == 0 ? "Cannot hold a post (may ride a fleet as a passenger)." : "Can be " + string.Join(", ", can) + ".";
    }

    public static string PostWords(CharacterPost p) => p switch
    {
        CharacterPost.Admiral => "Admiral",
        CharacterPost.Commander => "Commander",
        CharacterPost.General => "General",
        _ => "passenger",
    };

    static string PostKindWords(PostKind p) => p switch
    {
        PostKind.Admiral => "a fleet Admiral",
        PostKind.Commander => "a fleet Commander",
        PostKind.General => "a fleet General",
        _ => "a system General",
    };

    /// <summary>Classic text names of the ranks. rule: m5-characters-and-talent §Talent (Rank).</summary>
    public static string RankWords(TalentRank r) => r switch
    {
        TalentRank.Deviation => "Deviation",
        TalentRank.Anomaly => "Anomaly",
        TalentRank.Unforecastable => "Unforecastable",
        _ => "Latent",
    };

    static string SensorSizeWords(SensorSize s) => s switch
    {
        SensorSize.Few => "a small",
        SensorSize.Squadron => "a squadron-sized",
        _ => "a large",
    };

    static string At(string system) => system == null ? "" : $" at {system}";

    static string CauseWords(string cause) => cause switch
    {
        CharacterCause.Battle => " in battle",
        CharacterCause.Harvester => " by the harvester",
        CharacterCause.Uprising => " in an uprising",
        CharacterCause.Assault => " in an invasion",
        CharacterCause.Mission => " on a mission",
        CharacterCause.FleetDissolved => " (the fleet is gone)",
        CharacterCause.FleetMoved => " (the fleet moved off)",
        CharacterCause.Merged => " (the fleets merged)",
        CharacterCause.HolderChanged => " (the system changed hands)",
        CharacterCause.SystemDestroyed => " (the system was destroyed)",
        CharacterCause.Injured => " (injured)",
        CharacterCause.Captured => " (captured)",
        CharacterCause.Killed => " (killed)",
        _ => "",
    };

    string FleetSystem(int fleet) => Game.State.Fleets.TryGetValue(fleet, out var f) ? f.At : null;

    const string HqOrderPrefix = "Move the HQ";
    static bool IsHqOrder(string what) => what.StartsWith(HqOrderPrefix, StringComparison.Ordinal);

    /// <summary>The words for a refused MoveHq order (the shared reasons read differently for the HQ).</summary>
    public static string WhyHq(string reason) => reason switch
    {
        RejectReason.InTransit => "the headquarters is already moving",
        RejectReason.AlreadyThere => "the headquarters is already there",
        RejectReason.Unreachable => "there is no route there",
        _ => Why(reason),
    };

    static string HqCauseWords(string cause) => cause switch
    {
        FogModule.CauseCaptured => "the system was captured",
        FogModule.CauseDestinationLost => "its destination was lost while it travelled",
        FogModule.CauseSystemDestroyed => "the system was destroyed",
        _ => cause,
    };

    // ------------------------------------------------------------------ events -> messages

    // Only the events the player's side may know: the fog's filtered feed (rule: m5-fog-and-hq §N8 event audience). A battle always has both
    // sides there, so its report still reaches the player.
    void ReadEvents(StepOutcome o)
    {
        var events = Fog.EventsFor(Player);
        for (; _printed < events.Count; _printed++)
        {
            bool combat = events[_printed] is Contact or ForcedStop or FleetRetreated or BattleEnded or SystemCaptured
                or BombardmentStarted or BombardmentResolved or BombardCancelled or AssaultStarted or AssaultResolved or AssaultCancelled
                or Praedra.Core.Harvester.SystemDestroyed or Praedra.Core.Harvester.FleetDestroyedByHarvester;
            foreach (var m0 in Describe(events[_printed], o))
            {
                var m = combat ? m0 with { Combat = true } : m0;
                Messages.Add(m);
                o.New.Add(m);
            }
        }
        if (Messages.Count > 3000) Messages.RemoveRange(0, Messages.Count - 2000);
    }

    bool Mine(Side? s) => s == Player;

    IEnumerable<Message> Describe(GameEvent e, StepOutcome o)
    {
        int d = e.Day;
        switch (e)
        {
            case OrderRejected r when _pending.TryGetValue(r.Sequence, out var what):
            {
                _pending.Remove(r.Sequence);
                string why = _missionOrders.Contains(r.Sequence) ? WhyMission(r.Reason) : _charOrders.Contains(r.Sequence) ? WhyCharacter(r.Reason) : IsHqOrder(what) ? WhyHq(r.Reason) : Why(r.Reason);
                yield return new Message(d, $"Order refused: {what}. Reason: {why}.", null, MsgKind.Bad);
                break;
            }
            case OrderAccepted a when _pending.TryGetValue(a.Sequence, out var what) && IsHqOrder(what) && a.Path != null:
                _pending.Remove(a.Sequence);
                yield return new Message(d, $"HQ moving to {a.Path.Items[^1]}, arrives day {a.ArrivalDay} ({a.TotalTicks} days on the lanes).", a.Path.Items[^1], MsgKind.Good);
                break;
            case OrderAccepted a when _pending.TryGetValue(a.Sequence, out var what):
                _pending.Remove(a.Sequence);
                if (a.Path != null)
                    yield return new Message(d, $"{what}: {a.TotalTicks} days, arrives day {a.ArrivalDay}.", a.Path.Items[^1], MsgKind.Info);
                break;

            // ---- fleets
            case FleetArrived f when Mine(FleetSide(f.Fleet)):
                yield return new Message(d, $"Fleet {FleetName(f.Fleet)} arrived at {f.System}.", f.System, MsgKind.Info);
                break;
            case FleetArrived f when OwnerOf(f.System) == Player:
                yield return new Message(d, $"Enemy fleet arrived at {f.System}.", f.System, MsgKind.Warn); // no enemy fleet names (§N8)
                break;
            case FleetSighted x when OwnerOf(x.System) != Player: // at your own systems FleetArrived already says it
                yield return new Message(d, $"Enemy fleet sighted at {x.System}: {x.Detail}.", x.System, MsgKind.Warn);
                break;
            case SensorReport x: // the core sends it to the station's owner only (rule: m5-fog-and-hq §N4)
                yield return new Message(d, $"Sensors: {SensorSizeWords(x.Size)} {(x.FleetSide == Side.Hand ? "Hand" : "Collective")} fleet heading for {x.System}, expected on day {x.ReachDay}.", x.System, MsgKind.Warn);
                break;
            case OwnerChangeSeen x:
                yield return new Message(d, $"News from {x.System}: now held by {(x.Now is Side n ? "the " + NameOf(n) : "nobody")} (was {(x.Was is Side was ? "the " + NameOf(was) : "nobody")}).", x.System, MsgKind.Info);
                break;

            // ---- the Hearth (HqArrived reaches the Collective only; the departure is told by the accepted order above)
            case HqArrived h:
                yield return new Message(d, $"Your headquarters arrived at {h.System}.", h.System, MsgKind.Good);
                break;
            case HearthTipGiven x: // the core sends it to the spy's side only (rule: m5-fog-and-hq §N6a)
                yield return new Message(d, $"A spy heard where the Collective headquarters is: {x.System}.", x.System, MsgKind.Good);
                break;
            case HarvesterQueuedSeen x: // the seeing side only (rule: m5-fog-and-hq §N6b)
                yield return new Message(d, $"Intelligence reports a harvester under construction at {x.System}.", x.System, MsgKind.Bad);
                break;
            case HqSectorClueGiven x: // the spy's side only (rule: m5-fog-and-hq §N6, §N6a)
                yield return new Message(d, $"A spy heard the Collective headquarters is somewhere in sector {x.Sector}.", null, MsgKind.Good);
                break;
            case HqDestroyed h:
                yield return new Message(d, $"The Collective headquarters was destroyed at {h.System} ({HqCauseWords(h.Cause)}).", h.System, Player == Side.Hand ? MsgKind.Good : MsgKind.Bad);
                break;
            case ForcedStop x when Mine(FleetSide(x.Fleet)):
                yield return new Message(d, $"Fleet {FleetName(x.Fleet)} was stopped at {x.System} by an enemy fleet.", x.System, MsgKind.Warn);
                break;
            case BlockadeStarted b when OwnerOf(b.System) == Player && b.Blockader != Player:
                yield return new Message(d, $"{b.System} is blockaded by the enemy: mines and yards there stop.", b.System, MsgKind.Bad);
                break;
            case BlockadeLifted b when OwnerOf(b.System) == Player && b.Blockader != Player:
                yield return new Message(d, $"The blockade of {b.System} is over.", b.System, MsgKind.Good);
                break;
            case Contact c:
            {
                string key = $"{c.System}|{c.HandFleets}|{c.CollectiveFleets}";
                if (key != _lastContact)
                {
                    _lastContact = key;
                    yield return new Message(d, $"Enemy forces meet at {c.System}.", c.System, MsgKind.Warn);
                }
                break;
            }
            case FleetRetreated r:
                if (_battles.TryGetValue(r.From, out var rb))
                    rb.Retreats.Add($"{FleetName(r.Fleet)} retreated to {r.To}");
                if (Mine(FleetSide(r.Fleet)))
                    yield return new Message(d, $"Fleet {FleetName(r.Fleet)} retreated from {r.From} to {r.To}.", r.From, MsgKind.Warn);
                break;

            // ---- battle
            case BattleStarted b:
                _battles[b.System] = new BattleDraft { Collective = b.CollectiveUnits.Items.ToList(), Hand = b.HandUnits.Items.ToList() };
                o.PauseReason ??= "battle";
                break;
            case UnitDestroyed u when _battles.TryGetValue(u.System, out var bd):
                bd.Lost.Add((u.Unit, bd.Collective.Contains(u.Unit) ? Side.Collective : Side.Hand));
                break;
            case BattleEnded be:
                yield return BattleReport(be, o);
                break;

            // ---- characters: only the feed the player's side may see (rule: m5-fog-and-hq §N8 event audience; m5-characters-and-talent §Events)
            case CharacterCreated c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} joined your side{At(c.System)}." : $"A new {NameOf(c.Side)} leader, {CharName(c.Character)}, appeared{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Good : MsgKind.Info);
                break;
            case CharacterArrived c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} arrived at {c.System}." : $"Enemy leader {CharName(c.Character)} arrived at {c.System}.", c.System, Mine(c.Side) ? MsgKind.Info : MsgKind.Warn);
                break;
            case CharacterAttached c when Mine(c.Side):
                yield return new Message(d, c.Cause == CharacterCause.Merged
                    ? $"{CharName(c.Character)} moved to fleet {FleetName(c.Fleet)} (the fleets merged){(c.Post == CharacterPost.None ? "" : $" as {PostWords(c.Post)}")}."
                    : c.Post == CharacterPost.None ? $"{CharName(c.Character)} boarded fleet {FleetName(c.Fleet)} as a passenger."
                    : $"{CharName(c.Character)} joined fleet {FleetName(c.Fleet)} as {PostWords(c.Post)}.", FleetSystem(c.Fleet), MsgKind.Good);
                break;
            case CharacterDetached c when Mine(c.Side) && c.Cause is not (CharacterCause.Injured or CharacterCause.Captured or CharacterCause.Killed):
                yield return new Message(d, $"{CharName(c.Character)} left fleet {FleetName(c.Fleet)}{At(c.System)}{CauseWords(c.Cause)}.", c.System, c.Cause == CharacterCause.Order ? MsgKind.Info : MsgKind.Warn);
                break;
            case PostVacated p when Mine(p.Side) && (p.Post == PostKind.SystemGeneral || p.Cause is not (CharacterCause.Order or CharacterCause.Injured or CharacterCause.Captured or CharacterCause.Killed)):
                // a detach by order, an injury, a capture and a death have their own line; this tells the rest (a release, a merge, a lost system)
                yield return new Message(d, $"{CharName(p.Character)} is no longer {PostKindWords(p.Post)}{CauseWords(p.Cause)}.", null, p.Cause == CharacterCause.Order ? MsgKind.Info : MsgKind.Warn);
                break;
            case SystemGeneralAssigned g when Mine(g.Side):
                yield return new Message(d, $"{CharName(g.Character)} is now the General of {g.System}: the defenders there fight better in an invasion.", g.System, MsgKind.Good);
                break;
            case CharacterInjured c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was injured{At(c.System)}{CauseWords(c.Cause)}." : $"Enemy leader {CharName(c.Character)} was injured{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Bad : MsgKind.Good);
                break;
            case CharacterHealed c when Mine(c.Side):
                yield return new Message(d, $"{CharName(c.Character)} has recovered from the injuries.", null, MsgKind.Good);
                break;
            case CharacterCaptured c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was captured by the {NameOf(c.Captor)}{At(c.System)}."
                    : c.Captor == Player ? $"You captured {CharName(c.Character)} of the {NameOf(c.Side)}{At(c.System)}."
                    : $"{CharName(c.Character)} was captured{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Bad : MsgKind.Good);
                break;
            case PrisonerLanded c when Mine(c.Side) || c.Captor == Player:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)}, a prisoner, was taken off a ship and is now held on {c.System}."
                    : $"Your fleet put the prisoner {CharName(c.Character)} ashore on {c.System}.", c.System, Mine(c.Side) ? MsgKind.Info : MsgKind.Good);
                break;
            case CharacterFreed c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was freed{At(c.System)}." : $"Enemy leader {CharName(c.Character)} was freed{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Good : MsgKind.Bad);
                break;
            case CharacterEscaped c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} escaped from prison{At(c.System)}." : $"Enemy leader {CharName(c.Character)} escaped from your prison{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Good : MsgKind.Bad);
                break;
            case CharacterKilled c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was killed{At(c.System)}{CauseWords(c.Cause)}." : $"Enemy leader {CharName(c.Character)} was killed{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Bad : MsgKind.Good);
                break;
            case TalentTrainingResult t when Mine(t.Side):
                yield return new Message(d, t.Success
                    ? $"Train Talent: {CharName(t.Student)} grew in Talent under {CharName(t.Trainer)} (Talent now {t.NewTalent})."
                    : $"Train Talent: {CharName(t.Student)} made no progress with {CharName(t.Trainer)} this time.", null, t.Success ? MsgKind.Good : MsgKind.Info);
                break;
            case TalentRankChanged r when Mine(r.Side) && r.To is TalentRank to:
                yield return new Message(d, $"{CharName(r.Character)} is now a {RankWords(to)}.", null, MsgKind.Good);
                break;
            case TalentDiscovered f:
                yield return new Message(d, Mine(f.Side) ? $"{CharName(f.Character)} shows Talent! Train Talent is now possible." : $"Enemy leader {CharName(f.Character)} shows Talent.", null, Mine(f.Side) ? MsgKind.Good : MsgKind.Warn);
                break;

            // ---- research
            case Praedra.Core.Research.ResearchLevelReached r when r.Side == Player && r.Classes.Items.Any():
                yield return new Message(d, $"Research: {r.ResearchKind} level {r.Level} reached. You can now build: {string.Join(", ", r.Classes.Items)}.", null, MsgKind.Good);
                break;

            // ---- control, support
            case SystemCaptured c:
            {
                // (B5a) a world can now go neutral (To = null) or join a side by support
                string who = c.To == Player ? "You captured" : (c.From == Player ? "You lost" : c.To is Side to ? $"The {NameOf(to)} captured" : "Neutral now:");
                var kind = c.To == Player ? MsgKind.Good : (c.From == Player ? MsgKind.Bad : MsgKind.Info);
                var m = new Message(d, $"{who} {c.System}.", c.System, kind);
                if (c.To == Player || c.From == Player) { o.PauseReason ??= "captured"; o.Popup ??= m with { Title = "System captured", Detail = m.Text }; }
                yield return m;
                break;
            }
            case UprisingStarted u when u.Holder == Player:
                yield return new Message(d, $"Uprising on {u.System}! The people rise against you. Station more regiments there.", u.System, MsgKind.Bad);
                break;
            case UprisingStarted u:
                yield return new Message(d, $"Uprising against the {NameOf(u.Holder)} on {u.System}.", u.System, MsgKind.Info);
                break;
            case UprisingEnded u when OwnerOf(u.System) == Player:
                yield return new Message(d, $"The uprising on {u.System} is over.", u.System, MsgKind.Good);
                break;
            case RegimentDestroyed r when r.Side == Player:
                yield return new Message(d, $"A {r.Class} was destroyed on {r.System}.", r.System, MsgKind.Bad);
                break;
            case FacilityDestroyed f when OwnerOf(f.System) == Player:
                yield return new Message(d, $"A facility was destroyed on {f.System} ({ReasonWords(f.Reason)}).", f.System, MsgKind.Bad);
                break;
            case Disaster x when OwnerOf(x.System) == Player:
                yield return new Message(d, $"Disaster on {x.System}: {x.Destroyed.Items.Count()} facilities lost.", x.System, MsgKind.Bad);
                break;

            // ---- economy
            case AutoscrapWarning w when w.Side == Player:
                yield return new Message(d, "Maintenance is over the limit: units will be scrapped. Build refineries or scrap ships.", null, MsgKind.Warn);
                break;
            case ObjectScrapped s when s.Side == Player:
                yield return new Message(d, $"A {s.Class} was scrapped ({s.Reason}).", null, MsgKind.Warn);
                break;

            // ---- construction
            case ProjectStarted p when p.Side == Player:
                yield return new Message(d, $"Construction started at {p.System}: {p.Count} x {p.Class}.", p.System, MsgKind.Info);
                break;
            case ProjectCancelled p when p.Side == Player && p.Reason != "Replaced":
                yield return new Message(d, $"Construction at {p.System} was cancelled ({(p.Reason == "SystemDestroyed" ? "the system was destroyed" : p.Reason)}).", p.System, MsgKind.Warn);
                break;
            case UnitCompleted u when u.Side == Player:
                yield return new Message(d, $"{u.Class} completed at {u.Yard}.", u.Yard, MsgKind.Good);
                break;
            case DeliveryArrived u when ObjectIsMine(u.Object):
                yield return new Message(d, $"{u.Class} delivered to {u.System}.", u.System, MsgKind.Good);
                break;
            case DeliveryRefused u when u.Side == Player:
                yield return new Message(d, $"{u.Class} could not be delivered to {u.System} (refunded {u.Refund}).", u.System, MsgKind.Bad);
                break;

            // ---- troops
            case RegimentsLoaded t when Mine(FleetSide(t.Fleet)):
                yield return new Message(d, $"{t.Regiments.Items.Count()} regiment(s) loaded onto {FleetName(t.Fleet)} at {t.System}.", t.System, MsgKind.Info);
                break;
            case RegimentsUnloaded t when Mine(FleetSide(t.Fleet)):
                yield return new Message(d, $"{t.Regiments.Items.Count()} regiment(s) unloaded from {FleetName(t.Fleet)} at {t.System}.", t.System, MsgKind.Info);
                break;

            // ---- bombardment
            case BombardmentStarted b:
                if (b.Side == Player || OwnerOf(b.System) == Player)
                    yield return new Message(d, $"{FleetName(b.Fleet)} ({NameOf(b.Side)}) bombards {b.System}.", b.System, b.Side == Player ? MsgKind.Info : MsgKind.Bad);
                break;
            case BombardmentResolved b:
            {
                string text = b.Hits == 0
                    ? $"Bombardment of {b.System}: the shields held, no damage."
                    : $"Bombardment of {b.System}: {b.Hits} hit(s); {b.Regiments} regiment(s) and {b.Facilities} facilit(ies) destroyed" +
                      (b.Energy + b.Raw > 0 ? $", {b.Energy} energy and {b.Raw} raw slot(s) lost." : ".");
                yield return new Message(d, text, b.System, OwnerOf(b.System) == Player ? MsgKind.Bad : MsgKind.Info);
                break;
            }
            case BombardCancelled b when Mine(FleetSide(b.Fleet)):
                yield return new Message(d, $"Bombardment of {b.System} by {FleetName(b.Fleet)} was cancelled ({Why(b.Reason)}).", b.System, MsgKind.Warn);
                break;

            // ---- assault
            case AssaultStarted a:
            {
                bool youDefend = OwnerOf(a.System) == Player && a.Side != Player;
                var m = new Message(d, youDefend ? $"The {NameOf(a.Side)} is invading {a.System}! {a.A0} attackers against your {a.D0} defenders." :
                    $"Invasion of {a.System}: {a.A0} attackers against {a.D0} defenders.", a.System, youDefend ? MsgKind.Bad : MsgKind.Info);
                if (youDefend) o.PauseReason ??= "invaded";
                if (youDefend || a.Side == Player) o.PauseReason ??= "invasion";
                yield return m;
                break;
            }
            case AssaultResolved a:
            {
                bool won = a.Result == "Won";
                string text = $"Invasion of {a.System} by the {NameOf(a.Side)}: {(won ? "the attackers won" : "the defenders held")}. " +
                              $"Attackers {a.A0} -> {a.A} regiments, defenders {a.D0} -> {a.D}.";
                bool involved = a.Side == Player || OwnerOf(a.System) == Player || (won && false);
                var kind = (a.Side == Player) == won ? MsgKind.Good : MsgKind.Bad;
                var m = new Message(d, text, a.System, kind, "Invasion report", text);
                if (involved) o.Popup ??= m;
                yield return m;
                break;
            }
            case AssaultCancelled a when Mine(FleetSide(a.Fleet)):
                yield return new Message(d, $"Invasion of {a.System} by {FleetName(a.Fleet)} was cancelled ({Why(a.Reason)}).", a.System, MsgKind.Warn);
                break;

            // ---- the harvester (GameSession.Harvester.cs); the clock pauses on a destroyed system like on a capture
            case Praedra.Core.Harvester.SystemDestroyed:
                foreach (var m in DescribeHarvester(e))
                {
                    o.PauseReason ??= "destroyed";
                    o.Popup ??= m with { Title = "System destroyed", Detail = m.Text + " The planet and everything on it are gone for good." };
                    yield return m;
                }
                break;
            case Praedra.Core.Harvester.IHarvesterEvent or Praedra.Core.Harvester.DestroySystemCancelled:
                foreach (var m in DescribeHarvester(e)) yield return m;
                break;

            // ---- missions (GameSession.Missions.cs)
            default:
                foreach (var m in DescribeMission(e)) yield return m;
                break;
        }
    }

    bool ObjectIsMine(int objectId) => Host.Economy.Find(objectId)?.Side == Player;

    static string ReasonWords(DestroyReason r) => r switch
    {
        DestroyReason.OverEnergy => "not enough energy slots",
        DestroyReason.OverRaw => "not enough raw slots",
        DestroyReason.Disaster => "disaster",
        DestroyReason.UprisingIncident => "uprising",
        DestroyReason.Bombardment => "bombardment",
        DestroyReason.Assault => "invasion",
        DestroyReason.Sabotage => "sabotage by enemy agents",
        DestroyReason.Harvester => "harvester",
        _ => r.ToString(),
    };

    Message BattleReport(BattleEnded e, StepOutcome o)
    {
        var r = e.Result;
        _battles.TryGetValue(r.System, out var draft);
        draft ??= new BattleDraft();
        _battles.Remove(r.System);

        string SideLine(Side s, List<int> units)
        {
            string fleets = FleetsOf(units);
            return $"{You(s)}: {(fleets.Length > 0 ? fleets + " - " : "")}{Group(units)}";
        }
        string Lost(Side s)
        {
            var ids = draft.Lost.Where(l => l.Side == s).Select(l => l.Unit).ToList();
            return ids.Count == 0 ? "no losses" : $"lost {ids.Count}: {Group(ids)}";
        }
        string Outcome(BattleOutcome oc) => oc switch
        {
            BattleOutcome.Victorious => "victorious",
            BattleOutcome.Annihilated => "destroyed",
            BattleOutcome.Retreated => "retreated",
            _ => "no decision",
        };
        string verdict = r.Collective == BattleOutcome.Victorious ? "The Collective won."
            : r.Hand == BattleOutcome.Victorious ? "The Hand won."
            : "Nobody won.";
        bool youWon = (Player == Side.Collective ? r.Collective : r.Hand) == BattleOutcome.Victorious;
        bool youLost = (Player == Side.Collective ? r.Hand : r.Collective) == BattleOutcome.Victorious;
        string detail =
            $"Battle of {r.System}, day {e.Day}\n\n" +
            SideLine(Side.Collective, draft.Collective) + "\n" +
            SideLine(Side.Hand, draft.Hand) + "\n\n" +
            $"{verdict}\n" +
            $"Collective ({Outcome(r.Collective)}): {Lost(Side.Collective)}; {r.CollectiveSurvivors} survivor(s).\n" +
            $"Hand ({Outcome(r.Hand)}): {Lost(Side.Hand)}; {r.HandSurvivors} survivor(s).";
        if (draft.Retreats.Count > 0) detail += "\n\n" + string.Join("\n", draft.Retreats) + ".";
        string line = $"Battle at {r.System}: {(youWon ? "you won" : youLost ? "you lost" : verdict.TrimEnd('.').ToLowerInvariant())}. " +
                      $"Collective losses {r.CollectiveLosses}, Hand losses {r.HandLosses}. (click for the report)";
        var m = new Message(e.Day, line, r.System, MsgKind.Battle, $"Battle at {r.System}", detail);
        o.Popup = m;
        o.PauseReason = "battle";
        return m;
    }
}
