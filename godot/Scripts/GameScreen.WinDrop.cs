using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using RejectReason = Praedra.Core.Clock.RejectReason;
using UnloadRegimentsOrder = Praedra.Core.Clock.UnloadRegimentsOrder;
using LoadRegimentsOrder = Praedra.Core.Clock.LoadRegimentsOrder;
using UnloadSquadronsOrder = Praedra.Core.Clock.UnloadSquadronsOrder;
using LoadSquadronsOrder = Praedra.Core.Clock.LoadSquadronsOrder;
using TransferShipsOrder = Praedra.Core.Clock.TransferShipsOrder;
using MergeFleetsOrder = Praedra.Core.Clock.MergeFleetsOrder;
using SplitFleetOrder = Praedra.Core.Clock.SplitFleetOrder;
using SendRegimentOrder = Praedra.Core.Clock.SendRegimentOrder;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>What a drop may do, asked of the core's own order checks, and the orders a drop sends. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    // ================================================================== what a drop may do (the core judges again when the clock moves)

    /// <summary>Where a dragged thing is now: its system, and its fleet (0 when on the ground).</summary>
    (string System, int Fleet) WhereNow(Drag d)
    {
        var eco = S.Host.Economy;
        switch (d.Kind)
        {
            case 'L':
                var c = Cm.Get(d.Id);
                return c == null ? (null, 0) : (Cm.LocationOf(c), c.AttachedFleet);
            case 'M':
                return S.Game.State.Fleets.TryGetValue(d.Id, out var mf) ? (mf.At, mf.Id) : (null, 0);
            default:
                var o = eco.Find(d.Id);
                if (o == null) return (null, 0);
                if (d.Kind == 'S') { var sf = S.Game.State.Fleets.Values.FirstOrDefault(f => f.Ships.Contains(o.Id)); return (sf?.At, sf?.Id ?? 0); }
                if (o.AboardShip == 0) return (o.System, 0);
                var hf = S.Game.State.Fleets.Values.FirstOrDefault(f => f.Ships.Contains(o.AboardShip));
                return (hf?.At, hf?.Id ?? 0);
        }
    }

    static bool Still(Fleet f) => f.At != null && f.Transit == null;

    readonly Dictionary<string, int> _leaving = new(StringComparer.Ordinal); // regiments boarding from each planet today (lookup only)

    int Promised(int fleet) => _pendingIn.TryGetValue(fleet, out int n) ? n : 0;

    // ------------------------------------------------------------------ the core's own order checks
    // A drop is refused for the same reasons the core would refuse its order at dawn: each Refuse* below asks the core check for that order
    // (FleetModule/TroopModule/CharacterModule Check*), then adds only what the core can't know: today's other drops and nicer words.

    /// <summary>The words for a core refusal (null stays null: the order would be accepted).</summary>
    static string Words(string reason, bool character = false) =>
        reason == null ? null : "Can't: " + (character ? GameSession.WhyCharacter(reason) : GameSession.Why(reason)) + ".";

    static List<int> One(int id) => new() { id };

    string CheckUnloadOne(char kind, int fleet, int id) => kind == 'R'
        ? S.Host.Troops.CheckUnloadRegiments(new UnloadRegimentsOrder(0, Me, fleet, One(id)))
        : Fm.CheckUnloadSquadrons(new UnloadSquadronsOrder(0, Me, fleet, One(id)));

    string CheckLoadOne(char kind, int fleet, int id) => kind == 'R'
        ? S.Host.Troops.CheckLoadRegiments(new LoadRegimentsOrder(0, Me, fleet, One(id)))
        : Fm.CheckLoadSquadrons(new LoadSquadronsOrder(0, Me, fleet, One(id)));

    string RefuseFleetPost(Drag d, Fleet f, CharacterPost post)
    {
        if (d.Kind != 'L') return "";
        var c = Cm.Get(d.Id);
        if (c == null) return "";
        if (c.AttachedFleet == f.Id && c.Post == post) return "";
        if (!Still(f)) return $"{f.Name} is travelling: send leaders when it stands still.";
        if (post != CharacterPost.None && !Cm.ClassOf(c).Can(post)) return $"{S.CharName(c.Id)} can't be {GameSession.PostWords(post)}.";
        var (at, _) = WhereNow(d);
        if (at != f.At) return CanTravel(c, f.At);   // another system: they set off and take the post on arrival
        // the drop first takes them off their post and lands the post's holder (LeaveCurrentPost, Detach), then attaches
        return Words(Cm.CheckAttach(new AttachCharacterOrder(0, Me, c.Id, f.Id, post), leavesPostsFirst: true), character: true);
    }

    /// <summary>Why a leader can't set off for <paramref name="to"/> now (null: they can). rule: m5-characters-and-talent §Moving a character alone.</summary>
    string CanTravel(CharacterRecord c, string to)
    {
        string name = S.CharName(c.Id);
        if (c.Status != CharacterStatus.Free || c.Transit != null || Cm.IsOnMission(c.Id)) return $"{name} is busy.";
        if (c.AttachedFleet != 0 && S.Game.State.Fleets.TryGetValue(c.AttachedFleet, out var of) && !Still(of)) return $"{name}'s fleet is travelling.";
        if (Cm.LocationOf(c) is not string from) return $"{name} is busy.";
        return Fm.FindPath(from, to, Cm.Params.CharacterSpeed).Ok ? null : $"No route from {from} to {to}.";
    }

    int TravelDays(CharacterRecord c, string to) => Cm.LocationOf(c) is string from && Fm.FindPath(from, to, Cm.Params.CharacterSpeed) is { Ok: true } p ? p.TotalTicks : 0;

    /// <summary>A leader leaves any post and sets off for <paramref name="to"/>, joining <paramref name="f"/> there in <paramref name="post"/> (no fleet: they land).</summary>
    void SendLeader(CharacterRecord c, string to, Fleet f, CharacterPost post, string zone)
    {
        LeaveCurrentPost(c);
        int cid = c.Id, fid = f?.Id ?? 0, days = TravelDays(c, to);
        string name = S.CharName(cid);
        string role = f == null ? $"to {to}" : post == CharacterPost.None ? $"to ride {f.Name}" : $"to be {GameSession.PostWords(post)} of {f.Name}";
        S.TrackCharacter(cid, -1, S.Game.Submit(seq => new MoveCharacterOrder(seq, Me, cid, to, fid, post)), $"Send {name} {role}");
        Sent($"send {name} {role} at {to}", to);
        _moved[new Drag('L', cid).Key] = ($"on the way {role}, {Ui.Count(days, "day", "days")}", zone);
    }

    void DropFleetPost(Drag d, Fleet f, CharacterPost post)
    {
        var c = Cm.Get(d.Id);
        string zone = post == CharacterPost.None ? $"Fleet {f.Id} Passengers" : $"Fleet {f.Id} {post}";
        if (WhereNow(d).System != f.At) { SendLeader(c, f.At, f, post, zone); return; }
        LeaveCurrentPost(c);
        if (post != CharacterPost.None && Cm.HolderOf(f.Id, post) is { } holder && holder.Id != c.Id)
        {
            Detach(holder, f); // the old holder stays at the system, on the ground
            _moved[new Drag('L', holder.Id).Key] = ($"lands at {f.At}", $"Ground {f.At} Leaders");
        }
        Attach(c, f, post);
        _moved[d.Key] = (post == CharacterPost.None ? $"{f.Name}, passenger" : $"{GameSession.PostWords(post)} of {f.Name}", zone);
    }

    /// <summary>A leader leaves the fleet post or the system General's post they hold, so a new post can be given in the same day.</summary>
    void LeaveCurrentPost(CharacterRecord c)
    {
        if (c.AttachedFleet != 0 && S.Game.State.Fleets.TryGetValue(c.AttachedFleet, out var of)) Detach(c, of);
        if (S.SystemPostOf(c) is string gs) ReleaseGeneral(c, gs);
    }

    string RefuseSystemGeneral(Drag d, string n)
    {
        if (d.Kind != 'L') return "";
        var c = Cm.Get(d.Id);
        if (c == null || S.SystemPostOf(c) == n) return "";
        var (at, _) = WhereNow(d);
        if (at != n) return $"{S.CharName(c.Id)} is not at {n}: drop them on its leaders to send them there first.";
        if (!Cm.ClassOf(c).CanGeneral) return $"{S.CharName(c.Id)} can't be a General.";
        // the drop first frees them and the post (LeaveCurrentPost, ReleaseGeneral), then assigns
        return Words(Cm.CheckAssign(new AssignSystemGeneralOrder(0, Me, c.Id, n), leavesPostsFirst: true), character: true);
    }

    void DropSystemGeneral(Drag d, string n)
    {
        var c = Cm.Get(d.Id);
        LeaveCurrentPost(c);
        if (Cm.SystemGeneralOf(n) is { } old && old.Id != c.Id && old.Side == Me)
        {
            ReleaseGeneral(old, n);
            _moved[new Drag('L', old.Id).Key] = ($"steps down at {n}", $"Ground {n} Leaders");
        }
        AssignGeneral(c, n);
        _moved[d.Key] = ($"General of {n}", $"Ground {n} General");
    }

    /// <summary>Onto the planet: a leader leaves their post, a regiment or squadron lands. Only on a planet you hold.</summary>
    string RefuseToGround(Drag d, string n, char kind)
    {
        if (d.Kind != kind) return "";
        var (at, fleet) = WhereNow(d);
        if (kind == 'L')
        {
            var c = Cm.Get(d.Id);
            if (c == null) return "";
            if (at != n) return CanTravel(c, n);   // another system: they set off
            if (fleet == 0 && S.SystemPostOf(c) == null) return "";
        }
        else if (kind == 'F') return at == n ? "" : SfCanTravel(d, n);
        else if (at != n)
        {
            if (kind == 'R') return RegimentCanGo(d, n);
            if (fleet != 0) return $"Land it at {at} first, or move its fleet.";
            return SquadronCanFly(d, n);
        }
        else if (fleet == 0) return "";
        if (fleet != 0 && S.Game.State.Fleets.TryGetValue(fleet, out var f) && !Still(f)) return "The fleet is travelling.";
        if (kind == 'L') return null;
        // rule: m3-troops-and-special-forces §Orders (UnloadRegiments, switch UnloadOnlyAtHeldSystem); m3-fleets §Orders (UnloadSquadrons)
        string why = CheckUnloadOne(kind, fleet, d.Id);
        if (why == RejectReason.NotYourSystem) return $"Land troops only on a planet you hold. To take {n}, invade from the fleet's page.";
        return Words(why);
    }

    /// <summary>A regiment goes alone to another system its side holds; anywhere else it needs a ship. rule: m3-troops-and-special-forces §Orders (SendRegiment).</summary>
    string RegimentCanGo(Drag d, string n)
    {
        string why = S.Host.Construction.CheckSendRegiment(new SendRegimentOrder(0, Me, d.Id, n));
        if (why == RejectReason.NotYourSystem) return $"{n} isn't yours: regiments need a ship to go there. Load them on a fleet.";
        return Words(why);
    }

    /// <summary>Squadrons based at one system fly alone to another (they base there). rule: m3-fleets §Unescorted squadrons (Immobile at speed 0).</summary>
    string SquadronCanFly(Drag d, string n)
    {
        if (S.Host.Economy.Find(d.Id) is not { } o || o.System is not string from) return "";
        int speed = Fm.SpeedOfShip(o);
        if (speed <= 0) return $"{o.Class} can't fly without a carrier: load it on a fleet.";
        return Fm.FindPath(from, n, speed).Ok ? null : $"No route from {from} to {n}.";
    }

    /// <summary>A special force moves to another system as a Move mission. rule: m5-missions §Orders (MoveGroup).</summary>
    string SfCanTravel(Drag d, string n)
    {
        if (S.Host.Economy.Find(d.Id) is not { } o) return "";
        return SfReady(o) ? null : $"{S.SpecialForceName(o.Id)} is busy.";
    }

    void DropToGround(Drag d, string n)
    {
        var (at, fleet) = WhereNow(d);
        if (d.Kind == 'L' && at != n) { SendLeader(Cm.Get(d.Id), n, null, CharacterPost.None, $"Ground {n} Leaders"); return; }
        if (d.Kind == 'L') { LeaveCurrentPost(Cm.Get(d.Id)); _moved[d.Key] = ($"lands at {n}", $"Ground {n} Leaders"); return; }
        if (d.Kind == 'F')
        {
            int id = d.Id;
            S.Track(S.Game.Submit(seq => new MoveGroupOrder(seq, Me, new List<int>(), new List<int> { id }, n)), $"Send {S.SpecialForceName(id)} to {n}");
            Sent($"send {S.SpecialForceName(id)} to {n}", n);
            _moved[d.Key] = ($"on the way to {n}", $"Ground {n} SpecialForces");
            return;
        }
        if (d.Kind == 'R' && at != n)   // a regiment goes alone to a system we hold. rule: m3-troops-and-special-forces §Orders (SendRegiment)
        {
            int id = d.Id;
            var order = new SendRegimentOrder(0, Me, id, n);
            int days = S.Host.Construction.SendRegimentDays(order);
            WarnTraps(S.Host.Construction.SendRegimentRoute(order), $"The {ThingName(d)}'s way to {n}");
            S.Track(S.Game.Submit(seq => order with { Sequence = seq }), $"Send the {ThingName(d)} to {n}");
            Sent($"send the {ThingName(d)} from {at} to {n}", n);
            _moved[d.Key] = ($"on the way to {n}, {Ui.Count(days, "day", "days")}", $"Ground {n} Regiments");
            if (fleet == 0) WarnGarrison(at);
            return;
        }
        if (fleet == 0)   // fighters based elsewhere fly over
        {
            var o = S.Host.Economy.Find(d.Id);
            int days = Fm.FindPath(at, n, Fm.SpeedOfShip(o)) is { Ok: true } p ? p.TotalTicks : 0;
            S.Track(Fm.SubmitMoveSquadrons(Me, new[] { d.Id }, n), $"Fly the {o.Class} to {n}");
            Sent($"fly the {o.Class} from {at} to {n}", n);
            _moved[d.Key] = ($"on the way to {n}, {Ui.Count(days, "day", "days")}", $"Ground {n} Fighters");
            return;
        }
        var f = S.Game.State.Fleets[fleet];
        if (d.Kind == 'R') UnloadTroops(f, new List<int> { d.Id });
        else UnloadFighters(f, new List<int> { d.Id });
        _moved[d.Key] = ($"lands at {n}", d.Kind == 'R' ? $"Ground {n} Regiments" : $"Ground {n} Fighters");
    }

    /// <summary>Onto a fleet: a regiment or squadron boards it (from the planet, or from another fleet here by landing first).</summary>
    string RefuseOnto(Drag d, Fleet f, char kind)
    {
        if (d.Kind != kind) return "";
        var (at, fleet) = WhereNow(d);
        if (fleet == f.Id) return "";
        if (!Still(f)) return $"{f.Name} is travelling.";
        if (at != f.At) return $"{ThingName(d)} is not at {f.At}.";
        // from another fleet the drop unloads first, then loads (rule: m3-fleets §Orders, m3-troops-and-special-forces §Orders)
        string why = fleet != 0 ? CheckUnloadOne(kind, fleet, d.Id) : CheckLoadOne(kind, f.Id, d.Id);
        if (why == RejectReason.NotYourSystem) return "Move it through the planet: only on a planet you hold.";
        if (why != null && why != RejectReason.NoCapacity) return Words(why);
        int room = (kind == 'R' ? Fm.FreeTroopSlots(f) : Fm.FreeBerths(f)) - Promised(f.Id);
        if (room <= 0) return kind == 'R' ? $"{f.Name} has no troop room left." : $"{f.Name} has no hangar room left.";
        return null;
    }

    void DropOnto(Drag d, Fleet f)
    {
        var (_, fleet) = WhereNow(d);
        if (fleet != 0 && S.Game.State.Fleets.TryGetValue(fleet, out var from))
        {
            if (d.Kind == 'R') UnloadTroops(from, new List<int> { d.Id }); else UnloadFighters(from, new List<int> { d.Id });
        }
        if (d.Kind == 'R') LoadTroops(f, new List<int> { d.Id }); else LoadFighters(f, new List<int> { d.Id });
        _pendingIn[f.Id] = Promised(f.Id) + 1;
        _moved[d.Key] = ($"boards {f.Name}", d.Kind == 'R' ? $"Fleet {f.Id} Troops" : $"Fleet {f.Id} Fighters");
        if (d.Kind == 'R' && fleet == 0) WarnGarrison(f.At);
    }

    /// <summary>A lone traveller's way crosses a known enemy interdictor: say so in red (as the fleet move preview does). rule: m1-travel §Path rule (5).</summary>
    void WarnTraps(IReadOnlyList<string> route, string what)
    {
        var traps = S.Host.KnownTraps(Me, route);
        if (traps.Count == 0) return;
        Toast($"{what} passes {string.Join(", ", traps.Select(t => t.System))}, where an enemy interdictor was seen: it may be caught there.", MsgKind.Warn);
    }

    /// <summary>One more regiment leaves the planet <paramref name="at"/> today: warn when its garrison drops too low.</summary>
    void WarnGarrison(string at)
    {
        _leaving[at] = (_leaving.TryGetValue(at, out int gone) ? gone : 0) + 1;
        if (S.Host.Support.RegimentCount(at) - _leaving[at] < S.Host.Support.RequiredGarrison(at))
            Toast($"{at} is now short of troops: it may rise up.", MsgKind.Warn);
    }

    /// <summary>Ships: from another fleet here (a transfer), or a whole fleet dragged by its grip (a merge).</summary>
    string RefuseShipsInto(Drag d, Fleet f)
    {
        if (d.Kind is not ('S' or 'M')) return "";
        var (at, fleet) = WhereNow(d);
        if (fleet == f.Id) return "";
        if (!Still(f)) return $"{f.Name} is travelling.";
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var itself)) return $"{f.Name} itself {itself.Words}.";
        if (!S.Game.State.Fleets.TryGetValue(fleet, out var from) || !Still(from)) return $"{ThingName(d)}'s fleet is travelling.";
        if (at != f.At)
        {
            if (d.Kind == 'S')
            {
                // a capital ship splits off, flies there and joins on arrival. rule: m3-fleets §Orders (SplitFleet: sending ships)
                if (_moved.TryGetValue(new Drag('M', from.Id).Key, out var src)) return $"{from.Name} itself {src.Words}.";
                if (from.Ships.Count - ShipsLeaving(from) <= 1) return $"It is {from.Name}'s last ship: drag {from.Name} by its grip (⠿) to send the whole fleet.";
                return Words(Fm.CheckSplitFleet(new SplitFleetOrder(0, Me, from.Id, One(d.Id), f.At, f.Id)));
            }
            return Fm.FindPath(from.At, f.At, Fm.SpeedOf(from)).Ok ? null : $"{from.Name} can't reach {f.At}.";   // it flies there and merges on arrival
        }
        if (d.Kind == 'S' && from.Ships.Count - ShipsLeaving(from) <= 1) return $"It is {from.Name}'s last ship: drag {from.Name} by its grip (⠿) to merge the fleets.";
        // rule: m3-fleets §Orders (TransferShips, MergeFleets)
        return Words(d.Kind == 'S' ? Fm.CheckTransferShips(new TransferShipsOrder(0, Me, One(d.Id), f.Id)) : Fm.CheckMergeFleets(new MergeFleetsOrder(0, Me, from.Id, f.Id)));
    }

    int ShipsLeaving(Fleet f) => f.Ships.Count(id => _moved.ContainsKey(new Drag('S', id).Key));

    void DropShipsInto(Drag d, Fleet f)
    {
        var (_, fleet) = WhereNow(d);
        var from = S.Game.State.Fleets[fleet];
        if (d.Kind == 'M' && from.At != f.At)
        {
            // rule: m3-fleets §Orders (MoveFleet: joining a fleet on arrival)
            int days = Fm.FindPath(from.At, f.At, Fm.SpeedOf(from)) is { Ok: true } p ? p.TotalTicks : 0;
            S.TrackFleet(from.Id, Fm.SubmitMove(Me, from.Id, f.At, null, f.Id), $"Fly {from.Name} to {f.At} and merge into {f.Name}");
            Sent($"fly {from.Name} to {f.At} to merge into {f.Name}", f.At);
            _moved[d.Key] = ($"flies to join {f.Name}, {Ui.Count(days, "day", "days")}", $"Fleet {f.Id} Ships");
            return;
        }
        if (d.Kind == 'S' && from.At != f.At)
        {
            // rule: m3-fleets §Orders (SplitFleet: sending ships)
            int id = d.Id;
            var order = new SplitFleetOrder(0, Me, from.Id, One(id), f.At, f.Id);
            int days = Fm.SplitSendDays(order);
            WarnTraps(Fm.SplitSendRoute(order), $"The {ThingName(d)}'s way to {f.At}");
            S.TrackFleet(from.Id, S.Game.Submit(seq => order with { Sequence = seq }), $"Send the {ThingName(d)} from {from.Name} to join {f.Name}");
            Sent($"send the {ThingName(d)} from {from.Name} to join {f.Name} at {f.At}", f.At);
            _moved[d.Key] = ($"flies to join {f.Name}, {Ui.Count(days, "day", "days")}", $"Fleet {f.Id} Ships");
            return;
        }
        if (d.Kind == 'M') { Merge(from, f); _moved[d.Key] = ($"merges into {f.Name}", $"Fleet {f.Id} Ships"); return; }
        S.TrackFleet(from.Id, Fm.SubmitTransferShips(Me, new[] { d.Id }, f.Id), $"Move a ship from {from.Name} into {f.Name}");
        Sent($"move the {ThingName(d)} from {from.Name} into {f.Name}", f.At);
        _moved[d.Key] = ($"joins {f.Name}", $"Fleet {f.Id} Ships");
    }

    string RefuseSplit(Drag d, Fleet f)
    {
        if (d.Kind != 'S') return "";
        var (_, fleet) = WhereNow(d);
        if (fleet != f.Id) return "Split ships off their own fleet: drop them on its New fleet.";
        // QA-82: a split given after today's merge of this fleet would run after it, on a fleet that is gone by then
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var itself)) return $"{f.Name} itself {itself.Words}.";
        if (f.Ships.Count - ShipsLeaving(f) <= 1) return "A fleet keeps at least one ship.";
        // the drop withdraws today's split of this fleet and sends one with every ship so far (DropSplit). rule: m3-fleets §Orders (SplitFleet)
        var ships = _splits.TryGetValue(f.Id, out var prev) ? new List<int>(prev.Ships) { d.Id } : One(d.Id);
        return Words(Fm.CheckSplitFleet(new SplitFleetOrder(0, Me, f.Id, ships)));
    }

    readonly Dictionary<int, (long Seq, List<int> Ships)> _splits = new(); // today's split order per fleet (lookup only)

    void DropSplit(Drag d, Fleet f)
    {
        var ships = new List<int> { d.Id };
        if (_splits.TryGetValue(f.Id, out var prev) && S.Withdraw(prev.Seq)) ships.InsertRange(0, prev.Ships);   // rule: m1-clock-and-orders §Orders in (withdraw)
        _splits[f.Id] = (S.TrackFleet(f.Id, Fm.SubmitSplitFleet(Me, f.Id, ships), ships.Count == 1 ? $"Split a ship off {f.Name}" : $"Split {ships.Count} ships off {f.Name}"), ships);
        Sent($"split the {ThingName(d)} off {f.Name} into a new fleet", f.At);
        _moved[d.Key] = ("forms a new fleet", $"Fleet {f.Id} NewFleet");
    }

    string RegionOf(string n)
    {
        if (SectorOf(n) is not string sec) return "Outer space";
        var s = G.Sectors.FirstOrDefault(x => x.Name == sec);
        string region = s?.Group switch { "heartland" => "Heartland", "midreach" => "Midreach", "farreach" => "Farreach", var g => g ?? "" };
        return $"{region} · {sec}";
    }

    /// <summary>A drop is its own feedback (the chip moves at once): the "order given" toast stays quiet meanwhile.</summary>
    internal bool MgDropping { get; private set; }
    void RunDrop(Action drop) { MgDropping = true; try { RecordDrop(drop); } finally { MgDropping = false; } }
}
