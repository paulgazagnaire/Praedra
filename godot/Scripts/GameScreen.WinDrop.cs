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
        if (!Still(f)) return $"{S.NameOf(f)} is travelling: send leaders when it stands still.";
        if (MergesAwayWhy(f) is string gone) return gone;
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
        ReplaceLeaderMove(c.Id); // the last move given wins (QA-100)
        LeaveCurrentPost(c);
        int cid = c.Id, fid = f?.Id ?? 0, days = TravelDays(c, to);
        string name = S.CharName(cid);
        string role = f == null ? $"to {to}" : post == CharacterPost.None ? $"to ride {S.NameOf(f)}" : $"to be {GameSession.PostWords(post)} of {S.NameOf(f)}";
        long seq = S.TrackCharacter(cid, -1, S.Game.Submit(q => new MoveCharacterOrder(q, Me, cid, to, fid, post)), $"Send {name} {role}");
        if (Cm.LocationOf(c) is string from && Fm.FindPath(from, to, Cm.Params.CharacterSpeed) is { Ok: true } pr)
            S.PlanCharacterMove(cid, new GameSession.PlannedMove(to, pr.Systems.Select(i => G.Systems[i].Name).ToList(), pr.TotalTicks, S.Game.Day + pr.TotalTicks, seq));
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
        _moved[d.Key] = (post == CharacterPost.None ? $"{S.NameOf(f)}, passenger" : $"{GameSession.PostWords(post)} of {S.NameOf(f)}", zone);
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
        else if (kind == 'F')
        {
            if (fleet == 0) return at == n ? "" : SfCanTravel(d, n);
            // aboard a fleet (New: m5-missions §Spying from orbit 1): it lands where the fleet stands, held or not
            if (at == null || (S.Game.State.Fleets.TryGetValue(fleet, out var sff) && !Still(sff))) return "Its fleet is travelling.";
            if (at != n) return $"Land it at {at} first, or move its fleet.";
            return Words(Mm.CheckUnloadSpecialForces(new UnloadSpecialForcesOrder(0, Me, fleet, One(d.Id))));
        }
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
        if (d.Kind == 'F' && fleet != 0)   // it lands from its fleet. rule: m5-missions §Spying from orbit 1 (UnloadSpecialForces)
        {
            int id = d.Id;
            var from = S.Game.State.Fleets[fleet];
            S.TrackFleet(from.Id, S.Game.Submit(seq => new UnloadSpecialForcesOrder(seq, Me, from.Id, One(id))), $"Land {S.SpecialForceName(id)} from {S.NameOf(from)}");
            Sent($"land {S.SpecialForceName(id)} from {S.NameOf(from)} at {n}", n);
            _moved[d.Key] = ($"lands at {n}", $"Ground {n} SpecialForces");
            return;
        }
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

    /// <summary>Special forces dropped onto <paramref name="f"/> today: they take seats before the clock moves.</summary>
    int SfBoarding(Fleet f) => _moved.Values.Count(m => m.Zone == $"Fleet {f.Id} SpecialForces");

    /// <summary>
    /// Onto a fleet: a special force boards it where it stands, from the planet or from another fleet here by landing first; at most
    /// FleetSpecialForcesMax aboard (New: m5-missions §Spying from orbit 1, Paul 2026-11-27, "9 max per fleet"). The core's own check
    /// (MissionModule.CheckLoadSpecialForces) judges; the seats taken by today's other drops are added here.
    /// </summary>
    string RefuseSfOnto(Drag d, Fleet f)
    {
        if (d.Kind != 'F') return "";
        var (at, fleet) = WhereNow(d);
        if (fleet == f.Id) return "";
        if (!Still(f)) return $"{S.NameOf(f)} is travelling.";
        if (MergesAwayWhy(f) is string gone) return gone;
        if (S.Host.Economy.Find(d.Id) is not { } o) return "";
        string name = S.SpecialForceName(o.Id);
        if (!SfReady(o)) return $"{name} is busy.";
        if (at == null) return $"{name}'s fleet is travelling.";
        if (at != f.At) return $"{name} is at {at}: special forces board a fleet where it stands. Move it to {f.At} first.";
        string why = fleet != 0 ? Mm.CheckUnloadSpecialForces(new UnloadSpecialForcesOrder(0, Me, fleet, One(d.Id)))
            : Mm.CheckLoadSpecialForces(new LoadSpecialForcesOrder(0, Me, f.Id, One(d.Id)));
        if (why != null && why != RejectReason.NoCapacity) return Words(why);
        int max = Mm.Params.FleetSpecialForcesMax;
        if (Mm.SpecialForcesAboard(f).Count + SfBoarding(f) >= max) return $"{S.NameOf(f)} already carries {max} special forces, its most.";
        return null;
    }

    void DropSfOnto(Drag d, Fleet f)
    {
        var (_, fleet) = WhereNow(d);
        int id = d.Id;
        string name = S.SpecialForceName(id);
        if (fleet != 0 && S.Game.State.Fleets.TryGetValue(fleet, out var from))   // from another fleet here: it lands first (orders run in the order given)
            S.TrackFleet(from.Id, S.Game.Submit(seq => new UnloadSpecialForcesOrder(seq, Me, from.Id, One(id))), $"Land {name} from {S.NameOf(from)}");
        S.TrackFleet(f.Id, S.Game.Submit(seq => new LoadSpecialForcesOrder(seq, Me, f.Id, One(id))), $"Take {name} aboard {S.NameOf(f)}");
        Sent($"take {name} aboard {S.NameOf(f)}", f.At);
        _moved[d.Key] = ($"boards {S.NameOf(f)}", $"Fleet {f.Id} SpecialForces");
    }

    /// <summary>Onto a fleet: a regiment or squadron boards it (from the planet, or from another fleet here by landing first).</summary>
    string RefuseOnto(Drag d, Fleet f, char kind)
    {
        if (d.Kind != kind) return "";
        var (at, fleet) = WhereNow(d);
        if (fleet == f.Id) return "";
        if (!Still(f)) return $"{S.NameOf(f)} is travelling.";
        if (MergesAwayWhy(f) is string gone) return gone;
        if (at != f.At) return $"{ThingName(d)} is not at {f.At}.";
        // from another fleet the drop unloads first, then loads (rule: m3-fleets §Orders, m3-troops-and-special-forces §Orders)
        string why = fleet != 0 ? CheckUnloadOne(kind, fleet, d.Id) : CheckLoadOne(kind, f.Id, d.Id);
        if (why == RejectReason.NotYourSystem) return "Move it through the planet: only on a planet you hold.";
        if (why != null && why != RejectReason.NoCapacity) return Words(why);
        int room = (kind == 'R' ? Fm.FreeTroopSlots(f) : Fm.FreeBerths(f)) - Promised(f.Id);
        if (room <= 0) return kind == 'R' ? $"{S.NameOf(f)} has no troop room left." : $"{S.NameOf(f)} has no hangar room left.";
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
        _moved[d.Key] = ($"boards {S.NameOf(f)}", d.Kind == 'R' ? $"Fleet {f.Id} Troops" : $"Fleet {f.Id} Fighters");
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
        if (!Still(f)) return $"{S.NameOf(f)} is travelling.";
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var itself)) return $"{S.NameOf(f)} itself {itself.Words}.";
        if (!S.Game.State.Fleets.TryGetValue(fleet, out var from) || !Still(from)) return $"{ThingName(d)}'s fleet is travelling.";
        // a fleet whose every ship leaves today is gone tomorrow, as after a merge (m3-fleets §Fleet invariant 1): nothing more joins it or leaves it whole
        if (MergesAwayWhy(f) is string gone) return gone;
        if (d.Kind == 'M' && AllShipsLeave(from)) return $"All of {S.NameOf(from)}'s ships already join {LeavesInto(from)}.";
        // a fleet with a move given today is in transit once the day's moves start, before ships change fleets (step 1, in order given):
        // the core would refuse the transfer, merge or split (InTransit), or the join would miss it. rule: m3-fleets §Orders (TransferShips, MergeFleets)
        if (S.PlannedMoveOf(f.Id) is { } tm) return $"{S.NameOf(f)} leaves for {tm.To} today: cancel its move first.";
        if ((d.Kind == 'S' || at == f.At) && S.PlannedMoveOf(from.Id) is { } fm) return $"{S.NameOf(from)} leaves for {fm.To} today: cancel its move first.";
        if (at != f.At)
        {
            if (d.Kind == 'S')
            {
                // a capital ship splits off, flies there and joins on arrival. rule: m3-fleets §Orders (SplitFleet: sending ships)
                if (_moved.TryGetValue(new Drag('M', from.Id).Key, out var src)) return $"{S.NameOf(from)} itself {src.Words}.";
                if (from.Ships.Count - ShipsLeaving(from) <= 1) return $"It is {S.NameOf(from)}'s last ship: to send the whole fleet, use Merge in the other fleet's window.";
                return Words(Fm.CheckSplitFleet(new SplitFleetOrder(0, Me, from.Id, One(d.Id), f.At, f.Id)));
            }
            return Fm.FindPath(from.At, f.At, Fm.SpeedOf(from)).Ok ? null : $"{S.NameOf(from)} can't reach {f.At}.";   // it flies there and merges on arrival
        }
        // the last ship may go too: its fleet is then gone, as after a merge, and its leaders join the fleet it joins (Paul 2026-11-24).
        // rule: m3-fleets §Fleet invariant 1 (a fleet that loses its last member is removed); m3-fleets §Orders (TransferShips, MergeFleets)
        return Words(d.Kind == 'S' ? Fm.CheckTransferShips(new TransferShipsOrder(0, Me, One(d.Id), f.Id)) : Fm.CheckMergeFleets(new MergeFleetsOrder(0, Me, from.Id, f.Id)));
    }

    int ShipsLeaving(Fleet f) => f.Ships.Count(id => _moved.ContainsKey(new Drag('S', id).Key));

    /// <summary>Every ship of the fleet leaves it today (moved into other fleets here) and none comes in today (a ship or a fleet here
    /// moved into it, given earlier, keeps it alive; one given later is refused): the fleet is gone when the clock moves.</summary>
    bool AllShipsLeave(Fleet f) => f.Ships.Count > 0 && ShipsLeaving(f) == f.Ships.Count
        && !_moved.Values.Any(m => m.Zone == $"Fleet {f.Id} Ships" && (m.Words.StartsWith("joins ", StringComparison.Ordinal) || m.Words.StartsWith("merges into ", StringComparison.Ordinal)));

    /// <summary>Why the fleet takes no new order today: it merges into another fleet here when the clock moves (a merge given, or every
    /// ship moved out), so the core would refuse the order. Null when it stays. rule: m3-fleets §Fleet invariant 1, §Orders (MergeFleets)</summary>
    string MergesAwayWhy(Fleet f)
    {
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var m) && m.Words.StartsWith("merges into", StringComparison.Ordinal))
            return $"{S.NameOf(f)} {m.Words} today. Cancel the merge to keep it.";
        if (AllShipsLeave(f)) return $"{S.NameOf(f)} merges into {LeavesInto(f)} today. Cancel a ship's move to keep it.";
        return null;
    }

    /// <summary>The names of the fleets the leaving ships of <paramref name="f"/> join, in ship order ("TESTFLEET", "A and B").</summary>
    string LeavesInto(Fleet f)
    {
        var names = new List<string>();
        foreach (var id in f.Ships)
            if (_moved.TryGetValue(new Drag('S', id).Key, out var m) && m.Zone.StartsWith("Fleet ", StringComparison.Ordinal)
                && int.TryParse(m.Zone.Split(' ')[1], out int to) && S.Game.State.Fleets.TryGetValue(to, out var tf) && !names.Contains(S.NameOf(tf)))
                names.Add(S.NameOf(tf));
        return names.Count == 0 ? "other fleets" : names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    void DropShipsInto(Drag d, Fleet f)
    {
        var (_, fleet) = WhereNow(d);
        var from = S.Game.State.Fleets[fleet];
        if (d.Kind == 'M' && from.At != f.At)
        {
            // rule: m3-fleets §Orders (MoveFleet: joining a fleet on arrival)
            int days = Fm.FindPath(from.At, f.At, Fm.SpeedOf(from)) is { Ok: true } p ? p.TotalTicks : 0;
            ReplaceFleetMove(from.Id); // the last move given wins (QA-100)
            var plan = S.Host.PlanMove(Me, from.Id, f.At, null);
            long seq = S.TrackFleet(from.Id, Fm.SubmitMove(Me, from.Id, f.At, null, f.Id), $"Fly {S.NameOf(from)} to {f.At} and merge into {S.NameOf(f)}");
            if (plan.Ok) S.PlanMove(from.Id, new GameSession.PlannedMove(f.At, plan.Path.ToList(), plan.HopTicks.Sum(), plan.ArrivalDay, seq));
            Sent($"fly {S.NameOf(from)} to {f.At} to merge into {S.NameOf(f)}", f.At);
            _moved[d.Key] = ($"flies to join {S.NameOf(f)}, {Ui.Count(days, "day", "days")}", $"Fleet {f.Id} Ships");
            return;
        }
        if (d.Kind == 'S' && from.At != f.At)
        {
            // rule: m3-fleets §Orders (SplitFleet: sending ships)
            int id = d.Id;
            var order = new SplitFleetOrder(0, Me, from.Id, One(id), f.At, f.Id);
            int days = Fm.SplitSendDays(order);
            WarnTraps(Fm.SplitSendRoute(order), $"The {ThingName(d)}'s way to {f.At}");
            S.TrackFleet(from.Id, S.Game.Submit(seq => order with { Sequence = seq }), $"Send the {ThingName(d)} from {S.NameOf(from)} to join {S.NameOf(f)}");
            Sent($"send the {ThingName(d)} from {S.NameOf(from)} to join {S.NameOf(f)} at {f.At}", f.At);
            _moved[d.Key] = ($"flies to join {S.NameOf(f)}, {Ui.Count(days, "day", "days")}", $"Fleet {f.Id} Ships");
            return;
        }
        if (d.Kind == 'M') { Merge(from, f); _moved[d.Key] = ($"merges into {S.NameOf(f)}", $"Fleet {f.Id} Ships"); return; }
        bool last = from.Ships.Count - ShipsLeaving(from) == 1;
        // one order per pair of fleets, so one line and one Cancel: today's transfer from this fleet into that one is taken back and given
        // again with this ship too, as DropSplit does. rule: m1-clock-and-orders §Orders in (withdraw); m3-fleets §Orders (TransferShips)
        var pair = (from.Id, f.Id);
        var ships = new List<int> { d.Id };
        if (_transfers.TryGetValue(pair, out var prev) && S.Withdraw(prev.Seq)) ships.InsertRange(0, prev.Ships);
        string words = ships.Count == 1 ? $"The {ThingName(d)} from {S.NameOf(from)} joins {S.NameOf(f)}" : $"{ships.Count} ships from {S.NameOf(from)} join {S.NameOf(f)}";
        _transfers[pair] = (S.TrackFleet(from.Id, Fm.SubmitTransferShips(Me, ships, f.Id), words, into: f.Id), ships);
        _moved[d.Key] = ($"joins {S.NameOf(f)}", $"Fleet {f.Id} Ships");
        Sent($"move the {ThingName(d)} from {S.NameOf(from)} into {S.NameOf(f)}" + (last ? $". It was {S.NameOf(from)}'s last ship: {S.NameOf(from)} is gone when the clock moves, as after a merge" : ""), f.At);
    }

    string RefuseSplit(Drag d, Fleet f)
    {
        if (d.Kind != 'S') return "";
        var (_, fleet) = WhereNow(d);
        if (fleet != f.Id) return "Split ships off their own fleet: drop them on its New fleet.";
        // QA-82: a split given after today's merge of this fleet would run after it, on a fleet that is gone by then
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var itself)) return $"{S.NameOf(f)} itself {itself.Words}.";
        if (f.Ships.Count - ShipsLeaving(f) <= 1) return "A fleet keeps at least one ship.";
        // the drop withdraws today's split of this fleet and sends one with every ship so far (DropSplit). rule: m3-fleets §Orders (SplitFleet)
        var ships = _splits.TryGetValue(f.Id, out var prev) ? new List<int>(prev.Ships) { d.Id } : One(d.Id);
        return Words(Fm.CheckSplitFleet(new SplitFleetOrder(0, Me, f.Id, ships)));
    }

    readonly Dictionary<int, (long Seq, List<int> Ships)> _splits = new(); // today's split order per fleet (lookup only)
    readonly Dictionary<(int From, int Into), (long Seq, List<int> Ships)> _transfers = new(); // today's ship transfer per pair of fleets here

    void DropSplit(Drag d, Fleet f)
    {
        var ships = new List<int> { d.Id };
        if (_splits.TryGetValue(f.Id, out var prev) && S.Withdraw(prev.Seq)) ships.InsertRange(0, prev.Ships);   // rule: m1-clock-and-orders §Orders in (withdraw)
        _splits[f.Id] = (S.TrackFleet(f.Id, Fm.SubmitSplitFleet(Me, f.Id, ships), ships.Count == 1 ? $"Split a ship off {S.NameOf(f)}" : $"Split {ships.Count} ships off {S.NameOf(f)}"), ships);
        Sent($"split the {ThingName(d)} off {S.NameOf(f)} into a new fleet", f.At);
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
