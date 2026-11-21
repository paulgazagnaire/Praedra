using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Battle;
using Praedra.Core.Defenses;
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

/// <summary>
/// The Fleets window (Paul 2026-11-21: "I need a better way to manage fleets... merge fleets, select individual ships, see their stats and
/// their inventory... assign one or multiple fleets an order"). Two columns: on the left every fleet of yours, grouped by danger; on the
/// right the fleet picked, or the fleets picked. A click picks one fleet, Shift+click adds one or takes it out. One action bar gives the
/// picked fleets their orders: move, merge, bombard, invade, scrap. A ship is a card with its hull and what it carries; a click on it
/// shows its stats, its damage and its cargo. Every order is one the core already has, judged when the clock moves. Screens only.
/// </summary>
public partial class GameScreen
{
    const int FleetListW = 150;   // the Ground window's fleet rows, two to a row
    const int FleetColW = 196;    // the Fleets window's list column
    const int ShipTileW = 104;    // a ship card, a little wider than a tile for its cargo: four to a row beside the list
    const int RightColW = WideWidth - 34 - 4 - FleetColW - WinGap;   // the rest of the page: the fleet or fleets picked

    /// <summary>The fleets picked in the Fleets window, in the order picked: the first is where a merge gathers.</summary>
    readonly List<int> _fleetPicks = new();

    static readonly string[] HitNames = { "shield generator", "weapons", "hold beam", "sub-light engines", "hyperdrive", "damage control" };

    /// <summary>Keep the window's fleet and the picks to fleets you still have: a fleet merged away or lost gives way to the next.</summary>
    void SettleFleets(MgWin w)
    {
        var mine = MyFleets();
        _fleetPicks.RemoveAll(id => !mine.Any(f => f.Id == id));
        if (!mine.Any(f => f.Id == w.Fleet)) w.Fleet = _fleetPicks.Count > 0 ? _fleetPicks[^1] : mine.FirstOrDefault()?.Id ?? 0;
        if (_fleetPicks.Count == 0 && w.Fleet > 0) _fleetPicks.Add(w.Fleet);
    }

    List<Fleet> MyFleets() => S.Game.State.Fleets.Values.Where(f => f.Side == Me).OrderBy(f => f.Id).ToList();

    /// <summary>Your fleets in the order a commander looks at them: facing the enemy, at enemy or neutral systems, moving, at home.</summary>
    List<(string Head, List<Fleet> Fleets)> FleetGroups(List<Fleet> mine)
    {
        bool AtEnemy(Fleet f) => f.At != null && (S.OwnerOf(f.At) != Me || S.VisibleEnemyFleetsAt(f.At).Count > 0);
        var groups = new List<(string, List<Fleet>)>
        {
            ("Facing the enemy", mine.Where(f => f.At != null && S.VisibleEnemyFleetsAt(f.At).Count > 0).ToList()),
            ("At enemy or neutral systems", mine.Where(f => f.At != null && S.VisibleEnemyFleetsAt(f.At).Count == 0 && S.OwnerOf(f.At) != Me).ToList()),
            ("Moving", mine.Where(f => f.At == null || S.PlannedMoveOf(f.Id) != null).Where(f => !AtEnemy(f) || f.At == null).ToList()),
            ("At your systems", mine.Where(f => f.At != null && !AtEnemy(f) && S.PlannedMoveOf(f.Id) == null).ToList()),
        };
        return groups.Where(g => g.Item2.Count > 0)
            .Select(g => (g.Item1, g.Item2.OrderBy(f => f.At ?? f.Transit?.Path[^1], StringComparer.Ordinal).ThenBy(f => f.Id).ToList())).ToList();
    }

    /// <summary>F: the Fleets window, showing the fleet selected on the map, else the first in the list; brought forward if open.</summary>
    internal void OpenFleets()
    {
        if (_mgWins.FirstOrDefault(x => x.Kind == MgKind.Fleet) is { } open) { Front(open); open.Min = false; RebuildMg(open); return; }
        var mine = MyFleets();
        if (mine.Count == 0) { Toast("You have no fleet.", MsgKind.Info); return; }
        int id = mine.Any(f => f.Id == _view.SelectedFleet) ? _view.SelectedFleet : FleetGroups(mine)[0].Fleets[0].Id;
        OpenMgmt(MgKind.Fleet, fleet: id);
    }

    MgWin FleetsWindow => _mgWins.FirstOrDefault(x => x.Kind == MgKind.Fleet);

    /// <summary>The Fleets window with these fleets picked (a sector tile's triangles: the fleets there); the first is shown and gathers a merge.</summary>
    void OpenFleetsPicked(List<int> ids)
    {
        if (ids.Count == 0) return;
        OpenMgmt(MgKind.Fleet, fleet: ids[0]);
        if (FleetsWindow is not { } w) return;
        _fleetPicks.AddRange(ids.Skip(1).Where(id => !_fleetPicks.Contains(id)));
        RebuildMg(w);
    }

    /// <summary>Shift+click on one of your fleets on the map: it joins the Fleets window's pick, or leaves it. With the window closed it
    /// opens with the fleet selected on the map and this one picked.</summary>
    void ShiftPickOnMap(int id)
    {
        if (!S.Game.State.Fleets.TryGetValue(id, out var f) || f.Side != Me) return;
        if (FleetsWindow is { } w) { PickFleet(w, id, true); return; }
        int first = _view.SelectedFleet;
        bool two = first >= 0 && first != id && S.Game.State.Fleets.TryGetValue(first, out var ff) && ff.Side == Me;
        OpenMgmt(MgKind.Fleet, fleet: two ? first : id);
        if (two && FleetsWindow is { } w2) PickFleet(w2, id, true);
    }

    /// <summary>A plain click on one of your fleets on the map shows it in the Fleets window, if that is open.</summary>
    void ShowSelectedInFleets()
    {
        int id = _view.SelectedFleet;
        if (id < 0 || FleetsWindow is not { } w || (_fleetPicks.Count == 1 && _fleetPicks[0] == id)) return;
        if (!S.Game.State.Fleets.TryGetValue(id, out var f) || f.Side != Me) return;
        _fleetPicks.Clear(); _fleetPicks.Add(id); w.Fleet = id;
        RebuildMg(w);
    }

    /// <summary>
    /// A right-click on a system sends the fleets picked there (the Fleets window's pick, else the fleet selected on the map), each its
    /// own way, as Move and a click would: a path through a known trap still waits for Go anyway or Go around. Shift+right-click adds a
    /// waypoint instead. rule: m1-travel §Path rule.
    /// </summary>
    void OnMapRightClick(int idx)
    {
        if (idx < 0 || InMoveMode) return;
        var ids = FleetsWindow != null ? _fleetPicks.ToList() : _view.SelectedFleet >= 0 ? new List<int> { _view.SelectedFleet } : new List<int>();
        string to = G.Systems[idx].Name;
        var fs = ids.Select(i => S.Game.State.Fleets.TryGetValue(i, out var f) ? f : null).Where(f => f != null && f.Side == Me && Still(f) && f.At != to).ToList();
        if (fs.Count == 0) return;
        StartMoveMany(fs);
        OnSystemPicked(idx);
    }

    /// <summary>The map draws the fleets picked in the Fleets window like the selected one.</summary>
    void SyncMapPicks()
    {
        var want = FleetsWindow != null ? _fleetPicks.ToHashSet() : new HashSet<int>();
        if (want.SetEquals(_view.PickedFleets)) return;
        _view.PickedFleets = want;
        _view.QueueRedraw(); _fleets.QueueRedraw();
    }

    // ================================================================== the window

    void FleetWin(MgWin w)
    {
        var mine = MyFleets();
        MgHead(w, mine.Count == 0 ? "Fleets" : $"Fleets · {Ui.Count(mine.Count, "fleet", "fleets")}, {Ui.Count(mine.Sum(f => f.Ships.Count), "ship", "ships")}", "Fleets", Ui.Text2);
        w.Head.AddChild(OnContactRow());
        if (mine.Count == 0) { Line("You have no fleet. Ships you build start a fleet at their shipyard.", Ui.Text3, Ui.Small); return; }

        var cols = Ui.HBox(WinGap);
        cols.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var left = Ui.VBox(6);
        left.SizeFlagsHorizontal = SizeFlags.Fill;
        left.CustomMinimumSize = new Vector2(FleetColW, 0);
        var right = Ui.VBox(Ui.Block);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cols.AddChild(left);
        cols.AddChild(right);
        Add(cols);

        var prev = _into;
        try
        {
            _into = left;
            FleetList(w, mine);
            _into = right;
            var picked = _fleetPicks.Select(id => S.Game.State.Fleets[id]).ToList();
            if (picked.Count > 1) ManyFleets(w, picked);
            else OneFleet(w, S.Game.State.Fleets[w.Fleet]);
        }
        finally { _into = prev; }
        SyncMapPicks();
    }

    /// <summary>Fight or retreat when an enemy arrives: one setting for every fleet of yours, so it sits in the window's head.</summary>
    Control OnContactRow()
    {
        var row = Ui.HBox(16);
        row.MouseFilter = MouseFilterEnum.Ignore;
        var cap = Ui.Label("ALL FLEETS, WHEN AN ENEMY ARRIVES", null, Ui.Text3, Ui.Tiny, wrap: false);
        cap.AddThemeFontOverride("font", Ui.Caps);
        cap.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        cap.MouseFilter = MouseFilterEnum.Stop;
        cap.TooltipText = "One setting for all your fleets.";
        row.AddChild(cap);
        bool retreat = _retreatPending ?? S.Host.Battles.OnContactOf(Me) == OnContactMode.Retreat;
        var group = new ButtonGroup();
        foreach (var (word, on, tip) in new[] { ("Fight", false, "Your fleets fight an enemy fleet that arrives."), ("Retreat", true, "Your fleets pull back to a neighbouring system when an enemy arrives, instead of fighting.") })
        {
            bool r = on;
            var b = Ui.Chip(word, () => { if (r != (_retreatPending ?? S.Host.Battles.OnContactOf(Me) == OnContactMode.Retreat)) { SetRetreat(r); MgRefreshSoon(); } }, tip, retreat == on, group);
            b.SetMeta(UiTest.FullName, $"On contact {word}");
            b.ThemeTypeVariation = "TabChip";
            b.FocusMode = FocusModeEnum.None;
            row.AddChild(b);
        }
        return row;
    }

    // ================================================================== the list

    void FleetList(MgWin w, List<Fleet> mine)
    {
        foreach (var (head, list) in FleetGroups(mine))
        {
            if (_into.GetChildCount() > 0) Add(Ui.Space(6));
            Add(Caption(head, list.Count.ToString()));
            foreach (var f in list) Add(FleetListRow(w, f));
        }
        Add(Ui.Space(6));
        var hint = Line("Click a fleet to show it. Shift+click to pick several. Drag a fleet onto another to merge them.", Ui.Dimmed, Ui.Tiny);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    /// <summary>
    /// One fleet in the list: its icon, name, ships and where it is. A click shows it, Shift+click adds it to the pick or takes it out.
    /// Drag it onto another fleet to merge them (there, or it flies to join). Ships, troops, fighters and leaders dropped on it go to it.
    /// </summary>
    Control FleetListRow(MgWin w, Fleet f)
    {
        bool picked = _fleetPicks.Contains(f.Id);
        var z = DropZone($"Fleet {f.Id} Row", $"send to {f.Name}", d => d.Kind switch
        {
            'S' or 'M' => RefuseShipsInto(d, f),
            'R' or 'Q' => RefuseOnto(d, f, d.Kind),
            'L' => RefuseFleetPost(d, f, CharacterPost.None),
            _ => "",
        }, d =>
        {
            if (d.Kind is 'S' or 'M') DropShipsInto(d, f);
            else if (d.Kind is 'R' or 'Q') DropOnto(d, f);
            else if (d.Kind == 'L') DropFleetPost(d, f, CharacterPost.None);
        });
        z.Box.SetMeta("look", picked ? "selected" : "row");
        z.Box.AddThemeStyleboxOverride("panel", ZoneStyle(null, picked ? "selected" : "row"));
        z.Box.SetMeta("selected", picked);
        z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Pass;   // drops pass on to the row
        h.CustomMinimumSize = new Vector2(FleetColW - 10, 0);
        var size = f.Ships.Select(id => S.Host.Economy.Find(id)?.Class).Where(c => c != null).Select(SizeOfClass).DefaultIfEmpty(1).Max();
        var icon = UnitIcon.For(UnitIcon.Kind.Ship, Me == Side.Hand, Ui.You, size, new Vector2(30, 20));
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var v = Ui.VBox(0);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var nm = Ui.Label(f.Name, null, Ui.Text, Ui.Small, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.ClipText = true;
        v.AddChild(nm);
        var sl = Ui.Label($"{Ui.Count(f.Ships.Count, "ship", "ships")} · {PlaceWords(f)}", null, Ui.Text3, Ui.Tiny, wrap: false);
        sl.ClipText = true;
        sl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(sl);
        h.AddChild(v);
        z.Flow.AddChild(h);

        // drag the row onto another fleet to merge (Paul's ask: merge fleets); a click is not a drag
        int fid = f.Id;
        if (Still(f) && !_moved.ContainsKey(new Drag('M', f.Id).Key)) MakeDraggable(h, new List<Drag> { new('M', f.Id) }, f.Name);
        h.MouseDefaultCursorShape = CursorShape.PointingHand;
        Vector2? down = null;
        h.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) return;
            if (mb.Pressed) { down = mb.Position; return; }
            if (down is Vector2 at && at.DistanceTo(mb.Position) < 6) { PickFleet(w, fid, mb.ShiftPressed); h.AcceptEvent(); }
            down = null;
        };
        h.SetMeta(UiTest.FullName, $"Fleet row {f.Name}");
        z.Box.TooltipText = Ui.Wrap($"{f.Name}: {Ui.Count(f.Ships.Count, "ship", "ships")}, strength {Fm.StrengthOf(f):0}, {PlaceWords(f)}.\n"
            + "Click to show it, Shift+click to add it to the fleets picked. Drag it onto another fleet to merge them. Drop ships, troops, fighters or leaders here to send them to it.");
        return z.Box;
    }

    /// <summary>"at Tiryal", "to Skopia, 3 days", "at Tiryal, leaving".</summary>
    string PlaceWords(Fleet f)
    {
        if (f.Transit is { } t) return $"to {t.Path[^1]}, {Arrives(t, null)}";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"at {f.At}, leaving for {pm.To}";
        return $"at {f.At}";
    }

    /// <summary>A click on a fleet: it alone is picked and shown. Shift+click: it joins the pick, or leaves it (one always stays).</summary>
    void PickFleet(MgWin w, int id, bool shift)
    {
        if (!shift) { _fleetPicks.Clear(); _fleetPicks.Add(id); w.Fleet = id; }
        else if (_fleetPicks.Contains(id)) { if (_fleetPicks.Count > 1) _fleetPicks.Remove(id); w.Fleet = _fleetPicks[^1]; }
        else { _fleetPicks.Add(id); w.Fleet = id; }
        SyncMapPicks();
        Callable.From(() => { if (_mgWins.Contains(w)) RebuildMg(w); }).CallDeferred();
    }

    /// <summary>For the playtest and the screenshots: a click on a fleet's row, as the mouse would.</summary>
    internal void PlayPickFleet(int id, bool shift)
    {
        if (_mgWins.FirstOrDefault(x => x.Kind == MgKind.Fleet) is { } w) { PickFleet(w, id, shift); RebuildMg(w); }
    }

    internal IReadOnlyList<int> FleetPicks => _fleetPicks.ToList();

    // ================================================================== one fleet

    void OneFleet(MgWin w, Fleet f)
    {
        bool still = Still(f);
        var aboardF = FightersAboard(f);
        var aboardR = S.Host.Troops.RegimentsAboard(f);
        var riders = Cm.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None).OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        int posts = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.Count(p => Cm.HolderOf(f.Id, p) != null);

        // the fleet's name and where it is
        var name = Ui.Label(f.Name.ToUpperInvariant(), null, Ui.Text, 32, wrap: false);
        name.AddThemeFontOverride("font", Ui.NameFont(32, f.Name.ToUpperInvariant()));
        Add(name);
        string where = still ? $"At {f.At}." : f.Transit is { } t ? $"Travelling to {t.Path[^1]}, arrives day {GameSession.ArrivalDay(t)}." : "";
        if (S.PlannedMoveOf(f.Id) is { } pm) where = $"At {f.At}. Leaves for {pm.To} when the clock moves, arrives day {pm.ArrivalDay}.";
        Line(where, Ui.Text3, Ui.Small);
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto)) Line($"This fleet {mto.Words}.", Pending, Ui.Small);

        Add(Ui.Space(4));
        Add(Numbers(
            ("Ships", f.Ships.Count.ToString(), "Capital ships in the fleet."),
            ("Fighters", Room(aboardF.Count + f.Squadrons.Count, Fm.FreeBerths(f) + aboardF.Count), "Fighter squadrons aboard its carriers, and berths in all."),
            ("Regiments", Room(aboardR.Count, Fm.FreeTroopSlots(f) + aboardR.Count), "Regiments aboard, and places in all."),
            ("Leaders", (posts + riders.Count).ToString(), "Leaders with a post in the fleet, and passengers."),
            ("Strength", $"{Fm.StrengthOf(f):0}", "How hard the fleet hits: its damage rate against a reference force of three Mandates, with its damage counted. Three Decrees score about 65."),
            ("Slowest", SlowestShip(f) ?? "–", "The slowest ship sets the fleet's speed.")));

        Add(Ui.Space(4));
        Add(ActionBar(w, new List<Fleet> { f }));
        foreach (var (seq, pend) in S.PendingOrdersOf(f.Id)) OrderLine(seq, $"Ordered: {pend}", Ui.Small);

        // the ships, what they carry and who leads them
        var tabs = Ui.VBox(Ui.Block);
        Add(Ui.Space(8));
        Add(tabs);
        Tabs(w, tabs,
            ("Ships", f.Ships.Count, () => ShipsTab(f, still)),
            ("Fighters", aboardF.Count + f.Squadrons.Count, () => FightersAboardTab(f, aboardF)),
            ("Troops", aboardR.Count, () => TroopsAboardTab(f, aboardR.Select(a => a.Regiment).ToList())),
            ("Leaders", posts + riders.Count, () => CommandTab(f, riders)));
        if (still)
        {
            MgLinks(($"Ground at {f.At}", () => OpenMgmt(MgKind.Ground, f.At), "The planet's window: drag its leaders, troops and fighters onto this fleet"));
        }
    }

    static string Room(int have, int room) => room <= 0 ? (have == 0 ? "none" : have.ToString()) : $"{have} / {room}";

    /// <summary>The class of the fleet's slowest ship: the one with the largest trip time (rule: m1-travel §Rule, the slowest ship sets the speed).</summary>
    string SlowestShip(Fleet f)
    {
        var eco = S.Host.Economy;
        return f.Ships.Select(eco.Find).Where(o => o != null).OrderByDescending(Fm.SpeedOfShip).ThenBy(o => o.Class, StringComparer.Ordinal).FirstOrDefault()?.Class;
    }

    /// <summary>A row of key numbers: a small caption over each value.</summary>
    static Control Numbers(params (string Label, string Value, string Tip)[] items)
    {
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 20); flow.AddThemeConstantOverride("v_separation", 8);
        foreach (var (label, value, tip) in items)
        {
            var v = Ui.VBox(2);
            v.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            v.MouseFilter = MouseFilterEnum.Stop;
            v.TooltipText = Ui.Wrap(tip);
            var cap = Ui.Label(label.ToUpperInvariant(), null, Ui.Text3, Ui.Tiny, wrap: false);
            cap.AddThemeFontOverride("font", Ui.Caps);
            cap.MouseFilter = MouseFilterEnum.Ignore;
            v.AddChild(cap);
            var val = Ui.Label(value, null, Ui.Text, Ui.Small, wrap: false);
            val.AddThemeFontOverride("font", Ui.Num);
            val.MouseFilter = MouseFilterEnum.Ignore;
            v.AddChild(val);
            flow.AddChild(v);
        }
        return flow;
    }

    // ================================================================== several fleets

    void ManyFleets(MgWin w, List<Fleet> fs)
    {
        var name = Ui.Label($"{fs.Count} FLEETS", null, Ui.Text, 32, wrap: false);
        name.AddThemeFontOverride("font", Ui.NameFont(32, $"{fs.Count} FLEETS"));
        Add(name);
        var places = fs.Select(f => f.At ?? (f.Transit != null ? $"on the way to {f.Transit.Path[^1]}" : "?")).Distinct().ToList();
        Line(places.Count == 1 ? $"All at {places[0]}." : $"At {Ui.Count(places.Count, "place", "places")}: {string.Join(", ", places)}.", Ui.Text3, Ui.Small);
        Add(Ui.Space(4));
        int fighters = fs.Sum(f => FightersAboard(f).Count + f.Squadrons.Count), regiments = fs.Sum(f => S.Host.Troops.RegimentsAboard(f).Count);
        Add(Numbers(
            ("Ships", fs.Sum(f => f.Ships.Count).ToString(), "Capital ships in the fleets picked."),
            ("Fighters", fighters.ToString(), "Fighter squadrons with the fleets picked."),
            ("Regiments", regiments.ToString(), "Regiments aboard the fleets picked."),
            ("Strength", $"{fs.Sum(f => Fm.StrengthOf(f)):0}", "The fleets' strengths added up.")));
        Add(Ui.Space(4));
        Add(ActionBar(w, fs));

        Section("Fleets picked", fs.Count.ToString(), "The first one picked is where a merge gathers.");
        foreach (var f in fs)
        {
            foreach (var (seq, pend) in S.PendingOrdersOf(f.Id)) OrderLine(seq, $"{f.Name}: {pend}", Ui.Small);
            var card = PickedFleetCard(w, f, f == fs[0]);
            Add(card);
        }
        Line("Shift+click a fleet in the list to add it or take it out. Click one to show only it.", Ui.Dimmed, Ui.Tiny);
    }

    /// <summary>A picked fleet in the "several" view: its name, where it is and what it holds; a click shows it alone.</summary>
    Control PickedFleetCard(MgWin w, Fleet f, bool first)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        p.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0, 0, 0, 0), 0, Ui.WinLine, 1, 10, 8));
        var h = Ui.HBox(12);
        h.MouseFilter = MouseFilterEnum.Ignore;
        p.AddChild(h);
        var size = f.Ships.Select(id => S.Host.Economy.Find(id)?.Class).Where(c => c != null).Select(SizeOfClass).DefaultIfEmpty(1).Max();
        var icon = UnitIcon.For(UnitIcon.Kind.Ship, Me == Side.Hand, Ui.You, size, new Vector2(36, 22));
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var v = Ui.VBox(0);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var nm = Ui.Label(f.Name + (first ? "  (gathers a merge)" : ""), null, Ui.Text, Ui.Small, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        v.AddChild(nm);
        int fq = FightersAboard(f).Count + f.Squadrons.Count, rg = S.Host.Troops.RegimentsAboard(f).Count;
        var words = new List<string> { PlaceWords(f), Ui.Count(f.Ships.Count, "ship", "ships") };
        if (fq > 0) words.Add(Ui.Count(fq, "fighter squadron", "fighter squadrons"));
        if (rg > 0) words.Add(Ui.Count(rg, "regiment", "regiments"));
        words.Add($"strength {Fm.StrengthOf(f):0}");
        v.AddChild(Ui.Label(string.Join(" · ", words), null, Ui.Text3, Ui.Tiny, wrap: true));
        h.AddChild(v);
        int fid = f.Id;
        p.GuiInput += e => { if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb) PickFleet(w, fid, mb.ShiftPressed); };
        p.SetMeta(UiTest.FullName, $"Picked {f.Name}");
        p.TooltipText = Ui.Wrap($"Click to show {f.Name} alone; Shift+click to take it out of the pick.");
        return p;
    }

    // ================================================================== the orders

    /// <summary>
    /// The orders for the fleets picked, as one row of buttons: Move, Merge, Bombard, Invade, the harvester's strike, Scrap. With several
    /// fleets each order goes to every fleet that can take it, and the button says how many ("Bombard 2 of 3"). The core judges each order
    /// when the clock moves. rule: m3-fleets §Orders, m4-bombardment, m4-invasion, m5-harvester §2, m2-maintenance §Scrapping and refunds.
    /// </summary>
    Control ActionBar(MgWin w, List<Fleet> fs)
    {
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        int n = fs.Count;
        string Of(int k) => n == 1 || k == 0 ? "" : k == n ? $" {k}" : $" {k} of {n}";
        var still = fs.Where(Still).ToList();

        // move
        bar.AddChild(FleetAction(n > 1 ? $"Move{Of(still.Count)}..." : "Move...", "Fleets Move", () => { CloseSectorView(); StartMoveMany(still); },
            still.Count == 0 ? "Travelling: a fleet takes a new destination when it arrives." : n > 1 ? $"Pick a destination on the map: the {still.Count} fleets standing still fly there, each as its own fleet." : "Pick a destination on the map.",
            still.Count == 0));

        // merge
        if (n == 1)
        {
            var f = fs[0];
            var here = Still(f) ? MyFleets().Where(x => x.Id != f.Id && x.At == f.At && Still(x)).ToList() : new List<Fleet>();
            var lines = here.Select(x => ($"Merge {x.Name} into {f.Name}", (Action)(() => MergeInto(w, f, new List<Fleet> { x })))).ToList();
            if (here.Count > 1) lines.Insert(0, ($"Merge all {here.Count} fleets here into {f.Name}", () => MergeInto(w, f, here)));
            bar.AddChild(MenuAction("Merge", "Fleets Merge", lines.ToArray(),
                here.Count > 0 ? $"Merge another fleet at {f.At} into {f.Name}." : $"No other fleet of yours stands at {f.At ?? "its place"}. To merge fleets that are apart, Shift+click them in the list: they fly to the first one picked and join it."));
        }
        else
        {
            var into = fs[0];
            bar.AddChild(FleetAction($"Merge into {into.Name}", "Fleets Merge", () => MergeInto(w, into, fs.Skip(1).ToList()),
                Still(into) ? $"The others join {into.Name}: at once where they stand with it, else they fly to {into.At} and join it there." : $"{into.Name}, the first one picked, is travelling: the others can't join it until it stands still.",
                !Still(into)));
        }

        // bombard and invade the system each fleet is at
        var canHit = fs.Where(CanAttackHere).ToList();
        var canLand = canHit.Where(f => S.Host.Troops.RegimentsAboard(f).Count > 0).ToList();
        string hitWhy = canHit.Count > 0 ? null : n > 1 ? "None of these fleets is at a system you don't hold, or each has an attack ordered already." : AttackWhyNot(fs[0]);
        bar.AddChild(MenuAction(n > 1 ? $"Bombard{Of(canHit.Count)}" : "Bombard", "Fleets Bombard", canHit.Count == 0 ? Array.Empty<(string, Action)>() : new (string, Action)[]
        {
            ("Military targets: regiments and defences", () => { foreach (var f in canHit) Bombard(f, BombardCategory.Military); MgRefreshSoon(); }),
            ("Civilian targets: the planet's support", () => { foreach (var f in canHit) Bombard(f, BombardCategory.Civilian); MgRefreshSoon(); }),
            ("Everything", () => { foreach (var f in canHit) Bombard(f, BombardCategory.General); MgRefreshSoon(); }),
        }, hitWhy ?? "Bombard weakens the defences from orbit." + (n > 1 ? $" {canHit.Count} of the {n} fleets are at a system you don't hold." : "")));
        int regs = canLand.Sum(f => S.Host.Troops.RegimentsAboard(f).Count);
        bar.AddChild(FleetAction(n > 1 ? $"Invade{Of(canLand.Count)}" : regs > 0 ? $"Invade with {Ui.Count(regs, "regiment", "regiments")}" : "Invade", "Fleets Invade",
            () => { foreach (var f in canLand) Invade(f); MgRefreshSoon(); },
            canLand.Count > 0 ? "Lands the regiments aboard to take the planet: land more than the defenders have." : hitWhy ?? "No regiments aboard: load some at one of your planets first (Troops tab).",
            canLand.Count == 0));

        // the harvester's strike
        if (n == 1 && Still(fs[0]) && S.HoldsHarvester(fs[0]))
        {
            var f = fs[0];
            bool struck = S.PendingOf(f.Id).Any(p => p.StartsWith("Destroy System", StringComparison.Ordinal));
            int? readyDay = S.HarvesterRechargingUntil(f);
            bar.AddChild(FleetAction("Destroy System...", "Fleets Destroy", () => AskDestroySystem(f.Id),
                S.IsDestroyed(f.At) ? $"{f.At} is already destroyed." + (readyDay is int rd0 ? " " + S.HarvestWaitText(rd0) : "") : struck ? "The strike is ordered: it happens as soon as the clock moves." : readyDay is int rd ? S.HarvestWaitText(rd) : "Wipes out the system the harvester is at. Asks first.",
                S.IsDestroyed(f.At) || struck || readyDay != null, Ui.Danger));
        }

        // scrap
        var eco = S.Host.Economy;
        var ships = still.SelectMany(f => f.Ships).Select(eco.Find).Where(o => o != null && !_scrapped.Contains(o.Id)).ToList();
        var at = still.Select(f => f.At).Distinct().ToList();
        bar.AddChild(FleetAction(n > 1 ? $"Scrap{Of(still.Count)}..." : "Scrap...", "Fleets Scrap",
            () => AskScrap(ships, n > 1 ? $"all {ships.Count} ships of {still.Count} fleets" : $"all {ships.Count} ships of {fs[0].Name}", at.Count == 1 ? at[0] : null),
            ships.Count == 0 ? "Travelling: a fleet can be scrapped where it stands." : "Asks first. Half the refined cost comes back and the upkeep is freed. Fighters and regiments aboard stay at the system. To scrap one ship, right-click it.",
            ships.Count == 0, Ui.Danger));
        return bar;
    }

    bool CanAttackHere(Fleet f) => Still(f) && !S.IsDestroyed(f.At) && S.OwnerOf(f.At) != Me
        && !S.PendingOf(f.Id).Any(p => p.StartsWith("Bombard", StringComparison.Ordinal) || p.StartsWith("Invade", StringComparison.Ordinal));

    string AttackWhyNot(Fleet f)
    {
        if (!Still(f)) return $"Travelling: give this order when it reaches {f.Transit?.Path[^1] ?? "its destination"}.";
        if (S.IsDestroyed(f.At)) return $"{f.At} is destroyed: there is nothing left to attack.";
        if (S.OwnerOf(f.At) == Me) return $"{f.At} is yours. Move to a system you don't hold to bombard or invade it.";
        return "One attack at a time: press Play (or N for one day) so this one starts, then give the next.";
    }

    /// <summary>
    /// Merge fleets into one: those standing with it join at once (MergeFleets), the others fly to it and join on arrival (MoveFleet with
    /// a fleet to join). rule: m3-fleets §Orders (MergeFleets; MoveFleet: joining a fleet on arrival).
    /// </summary>
    void MergeInto(MgWin w, Fleet into, List<Fleet> others)
    {
        int sent = 0;
        foreach (var from in others)
        {
            var d = new Drag('M', from.Id);
            if (_moved.TryGetValue(d.Key, out var already)) { Toast($"{from.Name} already {already.Words}.", MsgKind.Warn); continue; }
            string why = RefuseShipsInto(d, into);
            if (why == "") continue;
            if (why != null) { Toast($"{from.Name}: {why}", MsgKind.Warn); continue; }
            DropShipsInto(d, into);
            sent++;
        }
        if (sent == 0) return;
        _fleetPicks.Clear(); _fleetPicks.Add(into.Id); w.Fleet = into.Id;
        RefreshMgmt();
    }

    /// <summary>An order button: outlined, its words in the window's grey (or the meaning colour), greyed with the reason when it can't.</summary>
    static Button FleetAction(string words, string key, Action act, string tip, bool disabled = false, Color? ink = null)
    {
        var b = new Button { FocusMode = FocusModeEnum.None, Disabled = disabled, TooltipText = Ui.Wrap(tip),
            MouseDefaultCursorShape = disabled ? CursorShape.Arrow : CursorShape.PointingHand };
        var clear = new Color(0, 0, 0, 0);
        foreach (var (state, line) in new[] { ("normal", Look.Edge), ("hover", Look.Soft), ("pressed", Look.Text), ("hover_pressed", Look.Text), ("disabled", Look.Line) })
            b.AddThemeStyleboxOverride(state, Look.Box(clear, line, 1, 1, 1, 1));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var c = disabled ? Look.Dim : ink ?? Look.Text;
        var l = Look.Words(words, 12, c, true);
        Fill(b, l, 12, 8);
        b.Pressed += act;
        b.SetMeta(UiTest.FullName, key);
        return b;
    }

    /// <summary>An order button that opens a short list of choices under it (the bombard targets, the fleets to merge).</summary>
    Button MenuAction(string words, string key, (string Text, Action Do)[] lines, string tip)
    {
        Button b = null;
        b = FleetAction(words + " ▾", key, () => PopMenu(b, lines), tip, lines.Length == 0);
        _menus.AddOrUpdate(b, () => lines);   // for the playtest: RunMenu
        return b;
    }

    void PopMenu(Control at, (string Text, Action Do)[] items)
    {
        if (items.Length == 0) return;
        var pm = new PopupMenu();
        for (int i = 0; i < items.Length; i++) pm.AddItem(items[i].Text, i);
        pm.IdPressed += id => items[(int)id].Do();
        pm.PopupHide += () => pm.QueueFree();
        AddChild(pm);
        var r = at.GetGlobalRect();
        pm.Popup(new Rect2I((Vector2I)(r.Position + new Vector2(0, r.Size.Y + 2)), Vector2I.Zero));
    }

    // ================================================================== the tabs

    void CommandTab(Fleet f, List<CharacterRecord> riders)
    {
        // the three posts, side by side
        var row = Ui.HBox(8);
        foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander })
        {
            var p = post;
            var col = Ui.VBox(Ui.Block);
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            col.AddChild(Caption(GameSession.PostWords(post), null, post switch
            {
                CharacterPost.Admiral => "The Admiral leads the fleet in space battles and bombardments.",
                CharacterPost.General => "The General leads the troops when the fleet invades.",
                _ => "The Commander has no effect yet.",
            }));
            var z = DropZone($"Fleet {f.Id} {post}", $"make {GameSession.PostWords(post)}", d => RefuseFleetPost(d, f, p), d => DropFleetPost(d, f, p));
            z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (Cm.HolderOf(f.Id, post) is { } holder) z.Flow.AddChild(LeaderChip(holder, null));
            else z.Flow.AddChild(EmptySlot(DropLeader, slot: true));
            col.AddChild(z.Box);
            row.AddChild(col);
        }
        Add(row);
        Section("Passengers", riders.Count == 0 ? null : riders.Count.ToString(), "Leaders who ride the fleet with no post: they travel with it.");
        var pz = DropZone($"Fleet {f.Id} Passengers", "take aboard", d => RefuseFleetPost(d, f, CharacterPost.None), d => DropFleetPost(d, f, CharacterPost.None));
        foreach (var c in riders) pz.Flow.AddChild(LeaderChip(c, null));
        if (riders.Count == 0) pz.Flow.AddChild(EmptySlot("None"));
        Add(pz.Box);
        // leaders and fleets sent to join it from other systems (m5 §Moving a character alone, m3 §Joining a fleet on arrival)
        OnTheWay(Cm.All.Where(c => c.Side == Me && c.JoinFleet == f.Id && c.JoinByOrder && c.Transit != null), f.At,
            S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.JoinFleet == f.Id && x.Transit != null).OrderBy(x => x.Id));
    }

    /// <summary>
    /// One card per ship (Paul 2026-11-14), each with its hull and what it carries (Paul 2026-11-21). A click picks a ship and shows its
    /// stats, damage and cargo under the cards; Shift+click picks several, to split them off, send them to another fleet or scrap them.
    /// </summary>
    void ShipsTab(Fleet f, bool still)
    {
        var eco = S.Host.Economy;
        var sz = DropZone($"Fleet {f.Id} Ships", "join", d => RefuseShipsInto(d, f), d => DropShipsInto(d, f));
        UnitTiles(sz.Flow, f.Ships.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)new Drag('S', o.Id), o, (string)null)), f.At, ShipCardExtras);
        if (still && f.Ships.Count > 1)
        {
            var nz = DropZone($"Fleet {f.Id} NewFleet", "split off", d => RefuseSplit(d, f), d => DropSplit(d, f), dashed: true);
            nz.Box.CustomMinimumSize = new Vector2(ShipTileW, TileH);
            var l = EmptySlot("Drop ships here to split");   // says what to do, like the other drop slots; a "+" read as a button
            l.AddThemeStyleboxOverride("normal", new StyleBoxEmpty()); // the zone itself is the dashed slot
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.CustomMinimumSize = new Vector2(ShipTileW - 12, TileH - 12);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            nz.Flow.AddChild(l);
            nz.Box.TooltipText = Ui.Wrap("Drop ships here to split them off into a new fleet.");
            sz.Flow.AddChild(nz.Box);
        }
        Add(sz.Box);

        // the ship or ships picked in this fleet
        var picked = LivePicks().Where(d => d.Kind == 'S' && f.Ships.Contains(d.Id)).Select(d => eco.Find(d.Id)).Where(o => o != null).ToList();
        if (picked.Count == 1) ShipDetail(f, picked[0]);
        else if (picked.Count > 1) PickedShips(f, picked, still);
        else Line("Click a ship to see its stats, its damage and what it carries. Shift+click to pick several.", Ui.Dimmed, Ui.Tiny);
    }

    /// <summary>Under a ship card's picture: its hull as a thin bar, and what it carries (fighters and regiments, out of its room).</summary>
    void ShipCardExtras(EconObject o, VBoxContainer v)
    {
        var sc = Fm.ClassOf(o);
        if (sc == null || !sc.IsCapital) return;
        if (v.GetParent() is Control card) card.CustomMinimumSize = new Vector2(ShipTileW, TileH);
        v.AddChild(HullBar(o.Hull, sc.Hull, ShipTileW - 20));
        v.MoveChild(v.GetChild(v.GetChildCount() - 1), 1);   // under the picture, over the name
        var h = Ui.HBox(3);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.Alignment = BoxContainer.AlignmentMode.Center;
        string side = Me == Side.Hand ? "hand" : "collective";
        void Cargo(string icon, int have, int room)
        {
            if (room <= 0) return;
            var i = IconPic.Of(PixelIconData.Units[$"{icon}-{side}"], 1, have > 0 ? Ui.WinActive : Ui.WinDim, have == 0);
            i.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(i);
            var n = Ui.Label($"{have}/{room}", null, have > 0 ? Ui.Text3 : Ui.Dimmed, Ui.Tiny, wrap: false);
            n.AddThemeFontOverride("font", Ui.Num);
            n.MouseFilter = MouseFilterEnum.Ignore;
            h.AddChild(n);
        }
        Cargo("fighter", Fm.UnitsAboard(o.Id, ObjectCategory.Fighter).Count(), sc.FighterCapacity);
        if (sc.FighterCapacity > 0 && sc.TroopCapacity > 0) h.AddChild(WithWidth(Ui.Space(0), 4));
        Cargo("regiment", Fm.UnitsAboard(o.Id, ObjectCategory.Regiment).Count(), sc.TroopCapacity);
        if (h.GetChildCount() > 0) v.AddChild(h);
    }

    static Control WithWidth(Control c, int w) { c.CustomMinimumSize = new Vector2(w, c.CustomMinimumSize.Y); return c; }

    /// <summary>A hull bar: 2 px, the accent on the track; amber under half, red under a quarter.</summary>
    static Control HullBar(int hull, int max, int width)
    {
        float r = max <= 0 ? 1 : Mathf.Clamp(hull / (float)max, 0, 1);
        var track = new ColorRect { Color = Look.Edge, CustomMinimumSize = new Vector2(width, 2), MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        var fill = new ColorRect { Color = r < 0.25f ? Ui.Danger : r < 0.5f ? Ui.Warn : Look.Accent, MouseFilter = MouseFilterEnum.Ignore };
        fill.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        fill.AnchorRight = r; fill.OffsetRight = 0;
        track.AddChild(fill);
        return track;
    }

    /// <summary>
    /// One ship: its class and number, its hull, its damage in words, whether it is being repaired, the class's stats and what it carries.
    /// rule: m3-ships-and-fighters §Damaged state, §Class data; m4-retreat-damage-repair §5.
    /// </summary>
    void ShipDetail(Fleet f, EconObject o)
    {
        var sc = Fm.ClassOf(o);
        if (sc == null) return;
        Section($"{o.Class} #{o.Id}", $"hull {o.Hull} of {sc.Hull}", ClassTip(o.Class));
        Add(HullBar(o.Hull, sc.Hull, RightColW));
        var hits = (o.Hits ?? new List<int>()).Select((h, i) => (h, i)).Where(x => x.h > 0 && x.i < HitNames.Length).Select(x => $"{HitNames[x.i]} ({x.h})").ToList();
        Line(hits.Count == 0 ? "No damage." : $"Damaged: {string.Join(", ", hits)}." + (o.Hits is { Count: > 4 } hh && hh[4] > 0 ? " A damaged hyperdrive makes it slower." : ""),
            hits.Count == 0 ? Ui.Text3 : Ui.Warn, Ui.Small);
        if (o.RepairMode != 0) Line("Being repaired.", Ui.Text3, Ui.Small);

        // the class's numbers
        if (S.Host.Economy.Costs.TryGet(o.Class, out var row))
        {
            var stats = ClassStats(row, 0, out string extra).Where(s => s.Item1 is not ("Refined cost" or "Days here")).ToList();
            Section($"Every {o.Class}", null, "The numbers of its class.");
            var grid = new GridContainer { Columns = 4, MouseFilter = MouseFilterEnum.Ignore };
            grid.AddThemeConstantOverride("h_separation", 16); grid.AddThemeConstantOverride("v_separation", 4);
            foreach (var (label, value) in stats)
            {
                var l = Ui.Label(label, null, Ui.Text3, Ui.Tiny, wrap: false);
                l.MouseFilter = MouseFilterEnum.Ignore;
                grid.AddChild(l);
                var v = Ui.Label(value, null, Ui.Text, Ui.Tiny, wrap: false);
                v.AddThemeFontOverride("font", Ui.Num);
                v.MouseFilter = MouseFilterEnum.Ignore;
                grid.AddChild(v);
            }
            Add(grid);
            if (extra != null) Line(extra, Ui.Text3, Ui.Tiny);
        }
        // what it carries
        var sq = Fm.UnitsAboard(o.Id, ObjectCategory.Fighter).OrderBy(x => x.Id).ToList();
        var rg = Fm.UnitsAboard(o.Id, ObjectCategory.Regiment).OrderBy(x => x.Id).ToList();
        if (sc.FighterCapacity + sc.TroopCapacity > 0)
        {
            var parts = new List<string>();
            if (sc.FighterCapacity > 0) parts.Add($"{sq.Count} of {sc.FighterCapacity} fighter berths");
            if (sc.TroopCapacity > 0) parts.Add($"{rg.Count} of {sc.TroopCapacity} regiment places");
            Section("It carries", string.Join(" · ", parts), "Squadrons and regiments ride on one ship of the fleet. Drag one to a planet's window to unload it.");
            if (sq.Count + rg.Count == 0) Line("Nothing aboard.", Ui.Dimmed, Ui.Small);
            else
            {
                var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
                flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
                UnitTiles(flow, sq.Select(x => ((Drag?)new Drag('Q', x.Id), x, (string)null)).Concat(rg.Select(x => ((Drag?)new Drag('R', x.Id), x, (string)null))), f.At);
                Add(flow);
            }
        }
        else Line("It carries no fighters and no regiments.", Ui.Text3, Ui.Small);
    }

    /// <summary>Several ships of the fleet picked: split them off into a new fleet, or scrap them; or drag them onto another fleet.</summary>
    void PickedShips(Fleet f, List<EconObject> ships, bool still)
    {
        Section($"{ships.Count} ships picked", PickedWords(ships));
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        var ds = ships.Select(o => new Drag('S', o.Id)).ToList();
        string why = !still ? "The fleet is travelling." : ships.Count >= f.Ships.Count - ShipsLeaving(f) ? "A fleet keeps at least one ship: leave one out of the pick." : null;
        bar.AddChild(FleetAction("Split them off", "Fleets Split", () => { foreach (var d in ds) if (!_moved.ContainsKey(d.Key) && RefuseSplit(d, f) == null) DropSplit(d, f); _picked.RemoveAll(ds.Contains); RefreshMgmt(); },
            why ?? "They become a new fleet here.", why != null));
        bar.AddChild(FleetAction("Scrap them...", "Fleets Scrap ships", () => AskScrap(ships, PickedWords(ships), f.At),
            still ? "Asks first. Half the refined cost comes back." : "The fleet is travelling.", !still, Ui.Danger));
        Add(bar);
        Line("Or drag them onto a fleet in the list to send them there.", Ui.Dimmed, Ui.Tiny);
    }

    void FightersAboardTab(Fleet f, List<EconObject> aboard)
    {
        var eco = S.Host.Economy;
        int berths = Fm.FreeBerths(f);
        Section("Fighters aboard", berths + aboard.Count == 0 ? "no hangar" : $"{aboard.Count} · room for {Math.Max(0, berths - Promised(f.Id))}");
        var qz = DropZone($"Fleet {f.Id} Fighters", "load", d => RefuseOnto(d, f, 'Q'), d => DropOnto(d, f));
        UnitTiles(qz.Flow, aboard.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o)))
            .Concat(f.Squadrons.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)null, o, "flies along"))), f.At);
        if (aboard.Count + f.Squadrons.Count == 0) qz.Flow.AddChild(EmptySlot(berths > 0 ? "Drop squadrons here to load them" : "No carrier in this fleet"));
        Add(qz.Box);
    }

    void TroopsAboardTab(Fleet f, List<EconObject> aboard)
    {
        int room = Fm.FreeTroopSlots(f);
        Section("Troops aboard", room + aboard.Count == 0 ? "no transport" : $"{aboard.Count} · room for {Math.Max(0, room - Promised(f.Id))}");
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        UnitTiles(tz.Flow, aboard.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), f.At);
        if (aboard.Count == 0) tz.Flow.AddChild(EmptySlot(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet"));
        Add(tz.Box);
    }

    /// <summary>Who is travelling here (leaders, and fleets sent to join): name and arrival day. Not draggable while they travel.</summary>
    void OnTheWay(IEnumerable<CharacterRecord> leaders, string to, IEnumerable<Fleet> fleets = null)
    {
        var ls = leaders.OrderBy(c => c.Id).ToList();
        var fs = (fleets ?? Enumerable.Empty<Fleet>()).ToList();
        if (ls.Count + fs.Count == 0) return;
        Section("On the way", (ls.Count + fs.Count).ToString(), $"Sent here from other systems. They take their place when they reach {to}.");
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var c in ls)
        {
            string name = S.CharName(c.Id);
            var t = Tile(null, false, 0, name, Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), 1, Ui.You, ghost: true, avatar: Initials(name));
            flow.AddChild(t);
        }
        foreach (var x in fs) flow.AddChild(Tile(UnitIcon.Kind.Ship, Me == Side.Hand, 2, x.Name, Arrives(x.Transit, $"{Ui.Count(x.Ships.Count, "ship", "ships")}, to merge"), 1, Ui.You, ghost: true));
        Add(flow);
    }

    string Arrives(FleetTransit t, string what)
    {
        int left = t == null ? 0 : Math.Max(0, t.DueDay - S.Game.Day) + t.HopTicks.Skip(t.HopIndex + 1).Sum();
        return (what != null ? what + ", " : "") + (left <= 1 ? "arrives tomorrow" : $"{left} days");
    }
}
