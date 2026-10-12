using System;
using System.Collections.Generic;
using System.Linq;
using Rebellion.Core.Assault;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Setup;
using Rebellion.Core.Support;
using Rebellion.Core.Troops;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

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
public sealed class GameSession
{
    public GameHost Host { get; }
    public GameData Data => Host.Data;
    public Game Game => Host.Game;
    public Side Player { get; }
    public Side Enemy => Player == Side.Empire ? Side.Alliance : Side.Empire;
    public IOpponent Opponent { get; }
    public List<Message> Messages { get; } = new();
    public int Autosaves { get; private set; }
    public Side? Winner { get; private set; }

    // What each order the player queued was, by sequence number, so a refusal can be explained in words.
    readonly Dictionary<long, string> _pending = new();
    readonly Dictionary<long, int> _pendingFleet = new();
    readonly Dictionary<int, PlannedMove> _plannedMoves = new();
    int _printed;
    // Names as they were before the day ran (a destroyed ship or a dissolved fleet is gone afterwards).
    readonly Dictionary<int, string> _classOf = new();
    readonly Dictionary<int, (string Name, Side Side)> _fleetOf = new();
    readonly Dictionary<int, int> _fleetOfUnit = new();
    // Battles being collected while the events of one day are read.
    readonly Dictionary<string, BattleDraft> _battles = new(StringComparer.Ordinal);
    string _lastContact;

    sealed class BattleDraft
    {
        public List<int> Alliance = new(), Empire = new();
        public List<(int Unit, Side Side)> Lost = new();
        public List<string> Retreats = new();
    }

    public GameSession(GameHost host, Side player)
    {
        Host = host; Player = player;
        Opponent = GameBoot.CreateOpponent(host, player == Side.Empire ? Side.Alliance : Side.Empire);
        _printed = host.Game.Events.Count;
        Snapshot();
    }

    public static GameSession New(GameData data, Side player, Preset preset) =>
        new(GameBoot.NewGame(data, player, preset), player);

    public static GameSession Load(GameData data, Side player, string json) =>
        new(GameBoot.LoadGame(data, json), player);

    public string SaveText() => Host.Save();

    // ------------------------------------------------------------------ queries the panels use

    public Side? OwnerOf(string system) => Host.Fleets.OwnerOf(system);
    public string NameOf(Side s) => s == Side.Empire ? "Empire" : "Alliance";
    public string You(Side s) => s == Player ? $"{NameOf(s)} (you)" : NameOf(s);

    public int QueuedOrders => Game.Queue.Count(o => o.Side == Player);

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
        Opponent.PlanDay();
        Game.Step();
        var outcome = new StepOutcome();
        ReadEvents(outcome);
        // Every order given before this day was judged in this Step: nothing is "waiting" any more.
        _pending.Clear(); _pendingFleet.Clear(); _plannedMoves.Clear();
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
            if (o.Category is ObjectCategory.CapitalShip or ObjectCategory.Fighter or ObjectCategory.Regiment or ObjectCategory.SpecialForce)
                _classOf[o.Id] = o.Class;
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
        AssaultReject.NoTroops => "no regiments aboard",
        AssaultReject.FullyShielded => "the planet's shields are too strong (bombard the shield generators first)",
        _ => reason,
    };

    // ------------------------------------------------------------------ events -> messages

    void ReadEvents(StepOutcome o)
    {
        var events = Game.Events;
        for (; _printed < events.Count; _printed++)
        {
            bool combat = events[_printed] is Contact or ForcedStop or FleetRetreated or BattleEnded or SystemCaptured
                or BombardmentStarted or BombardmentResolved or BombardCancelled or AssaultStarted or AssaultResolved or AssaultCancelled;
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
                _pending.Remove(r.Sequence);
                yield return new Message(d, $"Order refused: {what}. Reason: {Why(r.Reason)}.", null, MsgKind.Bad);
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
                yield return new Message(d, $"Enemy fleet {FleetName(f.Fleet)} arrived at {f.System}.", f.System, MsgKind.Warn);
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
                string key = $"{c.System}|{c.EmpireFleets}|{c.AllianceFleets}";
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
                _battles[b.System] = new BattleDraft { Alliance = b.AllianceUnits.Items.ToList(), Empire = b.EmpireUnits.Items.ToList() };
                o.PauseReason ??= "battle";
                break;
            case UnitDestroyed u when _battles.TryGetValue(u.System, out var bd):
                bd.Lost.Add((u.Unit, bd.Alliance.Contains(u.Unit) ? Side.Alliance : Side.Empire));
                break;
            case BattleEnded be:
                yield return BattleReport(be, o);
                break;

            // ---- control, support
            case SystemCaptured c:
            {
                string who = c.To == Player ? "You captured" : (c.From == Player ? "You lost" : $"The {NameOf(c.To)} captured");
                var kind = c.To == Player ? MsgKind.Good : (c.From == Player ? MsgKind.Bad : MsgKind.Info);
                var m = new Message(d, $"{who} {c.System}.", c.System, kind);
                if (c.To == Player || c.From == Player) { o.PauseReason ??= "captured"; o.Popup ??= m with { Title = "System captured", Detail = m.Text }; }
                yield return m;
                break;
            }
            case UprisingStarted u when u.Holder == Player:
                yield return new Message(d, $"Uprising on {u.System}! The people rebel against you. Station more regiments there.", u.System, MsgKind.Bad);
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
                yield return new Message(d, $"Construction at {p.System} was cancelled ({p.Reason}).", p.System, MsgKind.Warn);
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
        string verdict = r.Alliance == BattleOutcome.Victorious ? "The Alliance won."
            : r.Empire == BattleOutcome.Victorious ? "The Empire won."
            : "Nobody won.";
        bool youWon = (Player == Side.Alliance ? r.Alliance : r.Empire) == BattleOutcome.Victorious;
        bool youLost = (Player == Side.Alliance ? r.Empire : r.Alliance) == BattleOutcome.Victorious;
        string detail =
            $"Battle of {r.System}, day {e.Day}\n\n" +
            SideLine(Side.Alliance, draft.Alliance) + "\n" +
            SideLine(Side.Empire, draft.Empire) + "\n\n" +
            $"{verdict}\n" +
            $"Alliance ({Outcome(r.Alliance)}): {Lost(Side.Alliance)}; {r.AllianceSurvivors} survivor(s).\n" +
            $"Empire ({Outcome(r.Empire)}): {Lost(Side.Empire)}; {r.EmpireSurvivors} survivor(s).";
        if (draft.Retreats.Count > 0) detail += "\n\n" + string.Join("\n", draft.Retreats) + ".";
        string line = $"Battle at {r.System}: {(youWon ? "you won" : youLost ? "you lost" : verdict.TrimEnd('.').ToLowerInvariant())}. " +
                      $"Alliance losses {r.AllianceLosses}, Empire losses {r.EmpireLosses}. (click for the report)";
        var m = new Message(e.Day, line, r.System, MsgKind.Battle, $"Battle at {r.System}", detail);
        o.Popup = m;
        o.PauseReason = "battle";
        return m;
    }
}
