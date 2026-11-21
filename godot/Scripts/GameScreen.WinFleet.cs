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
/// A fleet's window (Paul 2026-11-21: "a fleet (single fleet) window that works really well", then "tabs and only one column width so I
/// can open 4 different fleets side by side"). One column. On top: its numbers, one bar of orders (move, merge with another fleet,
/// bombard, invade, scrap) and the orders it has. Under them, tabs: its ships, one card each with its hull and what it carries; the ship
/// picked, with its damage in words, its cargo and its class's numbers; its fighters, troops and leaders. Several fleets are picked on the
/// map with Shift+click, and a right-click on a system sends them there (Paul's pick: move only). Every order is one the core already has,
/// judged when the clock moves. Screens only.
/// </summary>
public partial class GameScreen
{
    const int FleetListW = 150;   // the Ground window's fleet rows, two to a row
    const int FleetBodyW = MgWidth - 34 - 4;   // the window's page (BodyWidth of a one-column window)
    const int ShipTileW = (FleetBodyW - 2 * 6) / 3;   // a ship card, a little wider than a tile for its cargo: three a row

    static readonly string[] HitNames = { "shield generator", "weapons", "hold beam", "sub-light engines", "hyperdrive", "damage control" };

    /// <summary>The ship picks each window last showed: a new pick brings its tab to the front.</summary>
    readonly Dictionary<string, string> _shipPickSeen = new();

    List<Fleet> MyFleets() => S.Game.State.Fleets.Values.Where(f => f.Side == Me).OrderBy(f => f.Id).ToList();

    /// <summary>F: the window of the fleet selected on the map; with none selected, the drawer's list of fleets.</summary>
    internal void OpenFleets()
    {
        if (S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var f) && f.Side == Me) OpenMgmt(MgKind.Fleet, fleet: f.Id);
        else ShowTab(DrawerTab.Fleets);
    }

    // ================================================================== several fleets on the map (Paul 2026-11-21: move only)

    /// <summary>Shift+click on one of your fleets on the map: it joins the fleets picked, or leaves them. The fleet selected before counts
    /// as the first one picked.</summary>
    void ShiftPickOnMap(int id)
    {
        if (!S.Game.State.Fleets.TryGetValue(id, out var f) || f.Side != Me) return;
        var picks = _view.PickedFleets;
        if (picks.Count == 0 && _view.SelectedFleet >= 0 && _view.SelectedFleet != id && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var sel) && sel.Side == Me)
            picks.Add(_view.SelectedFleet);
        if (!picks.Add(id)) picks.Remove(id);
        _view.QueueRedraw(); _fleets.QueueRedraw();
        if (picks.Count > 1) Toast($"{Ui.Count(picks.Count, "fleet", "fleets")} picked: right-click a system to send them there. Esc lets them go.", MsgKind.Info);
    }

    /// <summary>Let the fleets picked on the map go. True if there were any.</summary>
    bool ClearMapPicks()
    {
        if (_view.PickedFleets.Count == 0) return false;
        _view.PickedFleets.Clear();
        _view.QueueRedraw(); _fleets.QueueRedraw();
        return true;
    }

    /// <summary>
    /// A right-click on a system sends the fleets picked on the map there (the selected fleet leads if it is one of them), or the fleet
    /// selected when none is picked, each its own way, as Move and a click would: a path through a known trap still waits for a choice.
    /// Shift+right-click starts the move with that system as a waypoint; while a fleet move is being chosen, a right-click is a left click
    /// (Shift adds a waypoint, a plain one sends). rule: m1-travel §Path rule.
    /// </summary>
    void OnMapRightClick(int idx)
    {
        if (idx < 0) return;
        if (InMoveMode) { if (_moveFleet >= 0) OnSystemPicked(idx); return; }
        var picks = _view.PickedFleets;
        var ids = picks.Count == 0 ? (_view.SelectedFleet >= 0 ? new List<int> { _view.SelectedFleet } : new List<int>())
            : picks.OrderBy(x => x == _view.SelectedFleet ? 0 : 1).ThenBy(x => x).ToList();   // a fleet Shift+clicked out stays out (Ponytail on #348)
        string to = G.Systems[idx].Name;
        var fs = ids.Select(i => S.Game.State.Fleets.TryGetValue(i, out var f) ? f : null).Where(f => f != null && f.Side == Me && Still(f) && f.At != to).ToList();
        if (fs.Count == 0) return;
        StartMoveMany(fs);
        OnSystemPicked(idx);
    }

    // ================================================================== the window

    /// <summary>
    /// One column (Paul 2026-11-21: "tabs and only one column width so I can open 4 different fleets side by side"). On top: its numbers,
    /// its orders and the orders it has. Under them, tabs: its ships (a card each), the ship picked, its fighters, its troops, its leaders.
    /// A plain click on a ship brings the ship's tab forward; Shift+click keeps the cards in front and picks several, to split them off or
    /// scrap them together.
    /// </summary>
    void FleetWin(MgWin w, Fleet f)
    {
        bool still = Still(f);
        string where = still ? $"Fleet · at {f.At}" : f.Transit is { } t ? $"Fleet · to {t.Path[^1]}, arrives day {GameSession.ArrivalDay(t)}" : "Fleet";
        // the grip: drag the whole fleet onto another fleet's window to merge them, the windows side by side
        var grip = WindowGlyph("⋮⋮");
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.TooltipText = Ui.Wrap($"Drag {f.Name} onto another fleet's window to merge it into that fleet.");
        if (still && !_moved.ContainsKey(new Drag('M', f.Id).Key)) MakeDraggable(grip, new List<Drag> { new('M', f.Id) }, f.Name);
        else grip.AddThemeColorOverride("font_color", Ui.Dimmed);
        MgHead(w, where, f.Name, Ui.Text2, grip: grip);
        w.Head.AddChild(OnContactRow());

        var aboardF = FightersAboard(f);
        var aboardR = S.Host.Troops.RegimentsAboard(f);
        var riders = Cm.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None).OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        int posts = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.Count(p => Cm.HolderOf(f.Id, p) != null);
        var eco = S.Host.Economy;
        var picked = LivePicks().Where(d => d.Kind == 'S' && f.Ships.Contains(d.Id)).Select(d => eco.Find(d.Id)).Where(o => o != null).ToList();
        int fighters = aboardF.Count + f.Squadrons.Count, regiments = aboardR.Count;

        if (S.PlannedMoveOf(f.Id) is { } pm) Line($"Leaves for {pm.To} when the clock moves, arrives day {pm.ArrivalDay}.", Ui.Text3, Ui.Small);
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto)) Line($"This fleet {mto.Words}.", Pending, Ui.Small);
        // one row, so four windows side by side line up; the ships are counted on their tab
        Add(Numbers(
            ("Strength", $"{Fm.StrengthOf(f):0}", "How hard the fleet hits: its damage rate against a reference force of three Mandates, with its damage counted. Three Decrees score about 65."),
            ("Slowest", SlowestShip(f) ?? "–", "The slowest ship sets the fleet's speed."),
            ("Fighters", Room(fighters, Fm.FreeBerths(f) + aboardF.Count), "Fighter squadrons aboard its carriers, and berths in all."),
            ("Troops", Room(regiments, Fm.FreeTroopSlots(f) + regiments), "Regiments aboard, and places in all.")));
        Add(ActionBar(w, f));
        foreach (var (seq, pend) in S.PendingOrdersOf(f.Id)) OrderLine(seq, $"Ordered: {pend}", Ui.Small);

        // the tabs; a plain click on one of its ships brings the ship's tab forward, Shift+click keeps the cards in front
        var tabsBox = Ui.VBox(Ui.Block);
        Add(tabsBox);
        var tabs = new List<(string Name, int? Count, Action Build)> { ("Ships", f.Ships.Count, () => ShipsTab(f, still, picked)) };
        if (picked.Count == 1) tabs.Add(("Ship", null, () => ShipDetail(f, picked[0])));
        tabs.Add(("Fighters", fighters, () => FightersAboardTab(f, aboardF)));
        tabs.Add(("Troops", regiments, () => TroopsAboardTab(f, aboardR.Select(a => a.Regiment).ToList())));
        tabs.Add(("Leaders", posts + riders.Count, () => CommandTab(f, riders)));
        string pickKey = string.Join(",", picked.Select(o => o.Id));
        if (!_shipPickSeen.TryGetValue(w.Key, out var seen) || seen != pickKey)
        {
            _shipPickSeen[w.Key] = pickKey;
            if (picked.Count == 1 && !_pickShift) w.Tab = "Ship";
            else if (picked.Count > 1) w.Tab = "Ships";
        }
        Tabs(w, tabsBox, tabs.ToArray());
    }

    /// <summary>Fight or retreat when an enemy arrives: one setting for every fleet of yours, so it sits in the window's head.</summary>
    Control OnContactRow()
    {
        var row = Ui.HBox(8);
        row.MouseFilter = MouseFilterEnum.Ignore;
        var cap = Ui.Label("ALL FLEETS, IF AN ENEMY COMES", null, Ui.Text3, Ui.Tiny, wrap: false);
        cap.AddThemeFontOverride("font", Ui.Caps);
        cap.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        cap.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cap.MouseFilter = MouseFilterEnum.Stop;
        cap.TooltipText = "One setting for all your fleets: change it in any fleet's window.";
        row.AddChild(cap);
        bool retreat = _retreatPending ?? S.Host.Battles.OnContactOf(Me) == OnContactMode.Retreat;
        var group = new ButtonGroup();
        foreach (var (word, on, tip) in new[] { ("Fight", false, "All your fleets fight an enemy fleet that arrives."), ("Retreat", true, "All your fleets pull back to a neighbouring system when an enemy arrives, instead of fighting.") })
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

    // ================================================================== the orders

    /// <summary>
    /// The fleet's orders in two rows of buttons: Move, Merge with another fleet, Scrap; then Bombard, Invade, the harvester's strike. A button
    /// that can't act is greyed and its hover says why. The core judges each order when the clock moves.
    /// rule: m3-fleets §Orders, m4-bombardment, m4-invasion, m5-harvester §2, m2-maintenance §Scrapping and refunds.
    /// </summary>
    Control ActionBar(MgWin w, Fleet f)
    {
        // two rows: the fleet itself (move, merge, scrap), then the attack on the system it is at (bombard, invade, the harvester's strike)
        var rows = Ui.VBox(8);
        rows.MouseFilter = MouseFilterEnum.Ignore;
        HFlowContainer Row()
        {
            var r = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
            r.AddThemeConstantOverride("h_separation", 8); r.AddThemeConstantOverride("v_separation", 8);
            rows.AddChild(r);
            return r;
        }
        var bar = Row();
        var attack = Row();
        bool still = Still(f);
        string travelling = $"Travelling: give this order when it reaches {f.Transit?.Path[^1] ?? "its destination"}.";

        // move
        int fid = f.Id;
        bar.AddChild(FleetAction("Move...", "Fleet Move", () => { if (S.Game.State.Fleets.TryGetValue(fid, out var ff)) { CloseSectorView(); StartMove(ff); } },
            still ? "Pick a destination on the map. To send several fleets at once, Shift+click them on the map and right-click a system." : travelling, !still));

        // merge with another fleet: one here joins at once, one elsewhere flies here and joins
        var lines = still ? MergeLines(f) : new List<(string, Action)>();
        bar.AddChild(MenuAction("Merge with", "Fleet Merge", lines.ToArray(),
            !still ? travelling : lines.Count == 0 ? "You have no other fleet standing still to merge with this one."
            : $"Pick a fleet to join {f.Name}: one at {f.At} joins at once, one elsewhere flies to {f.At} and joins on arrival."));

        // bombard and invade the system it is at
        bool canHit = CanAttackHere(f);
        int regs = S.Host.Troops.RegimentsAboard(f).Count;
        string hitWhy = canHit ? null : AttackWhyNot(f);
        // scrap
        var ships = still ? f.Ships.Select(S.Host.Economy.Find).Where(o => o != null && !_scrapped.Contains(o.Id)).ToList() : new List<EconObject>();
        bar.AddChild(FleetAction("Scrap...", "Fleet Scrap", () => AskScrap(ships, $"all {ships.Count} ships of {f.Name}", f.At),
            ships.Count == 0 ? "Travelling: a fleet can be scrapped where it stands." : "Asks first. Half the refined cost comes back and the upkeep is freed. Fighters and regiments aboard stay at the system. To scrap one ship, right-click it.",
            ships.Count == 0, Ui.Danger));

        attack.AddChild(MenuAction("Bombard", "Fleet Bombard", !canHit ? Array.Empty<(string, Action)>() : new (string, Action)[]
        {
            ("Military targets: regiments and defences", () => { Bombard(f, BombardCategory.Military); MgRefreshSoon(); }),
            ("Civilian targets: the planet's support", () => { Bombard(f, BombardCategory.Civilian); MgRefreshSoon(); }),
            ("Everything", () => { Bombard(f, BombardCategory.General); MgRefreshSoon(); }),
        }, hitWhy ?? "Bombard weakens the defences from orbit."));
        attack.AddChild(FleetAction(regs > 0 ? $"Invade with {Ui.Count(regs, "regiment", "regiments")}" : "Invade", "Fleet Invade",
            () => { Invade(f); MgRefreshSoon(); },
            hitWhy ?? (regs > 0 ? "Lands the regiments aboard to take the planet: land more than the defenders have." : "No regiments aboard: load some at one of your planets first (Troops tab)."),
            !canHit || regs == 0));

        // the harvester's strike
        if (still && S.HoldsHarvester(f))
        {
            bool struck = S.PendingOf(f.Id).Any(p => p.StartsWith("Destroy System", StringComparison.Ordinal));
            int? readyDay = S.HarvesterRechargingUntil(f);
            attack.AddChild(FleetAction("Destroy System...", "Fleet Destroy", () => AskDestroySystem(f.Id),
                S.IsDestroyed(f.At) ? $"{f.At} is already destroyed." + (readyDay is int rd0 ? " " + S.HarvestWaitText(rd0) : "") : struck ? "The strike is ordered: it happens as soon as the clock moves." : readyDay is int rd ? S.HarvestWaitText(rd) : "Wipes out the system the harvester is at. Asks first.",
                S.IsDestroyed(f.At) || struck || readyDay != null, Ui.Danger));
        }
        return rows;
    }

    /// <summary>
    /// The fleets that can merge with this one, closest first: those standing with it join at once, the others fly to it and join on
    /// arrival, each line with its trip time and a known trap on the way. rule: m3-fleets §Orders (MergeFleets; MoveFleet joining a fleet).
    /// </summary>
    List<(string Text, Action Do)> MergeLines(Fleet f)
    {
        var others = MyFleets().Where(x => x.Id != f.Id && Still(x) && !_moved.ContainsKey(new Drag('M', x.Id).Key) && RefuseShipsInto(new Drag('M', x.Id), f) == null).ToList();
        var here = others.Where(x => x.At == f.At).ToList();
        var far = others.Where(x => x.At != f.At).Select(x => (F: x, Plan: S.Host.PlanMove(Me, x.Id, f.At, null))).Where(p => p.Plan.Ok)
            .OrderBy(p => p.Plan.HopTicks.Sum()).ThenBy(p => p.F.Id).ToList();
        var lines = new List<(string, Action)>();
        if (here.Count > 1) lines.Add(($"All {here.Count} fleets here, at once", () => MergeInto(f, here)));
        foreach (var x in here) lines.Add(($"{x.Name}: here, joins at once", () => MergeInto(f, new List<Fleet> { x })));
        foreach (var (x, plan) in far)
            lines.Add(($"{x.Name}: at {x.At}, flies here in {Ui.Count(plan.HopTicks.Sum(), "day", "days")}" + (plan.Red ? ", known trap on the way" : ""), () => MergeInto(f, new List<Fleet> { x })));
        return lines;
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
    void MergeInto(Fleet into, List<Fleet> others)
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
        if (sent > 0) RefreshMgmt();
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

    /// <summary>An order button that opens a short list of choices under it (the bombard targets, the fleets to merge with).</summary>
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
    /// One card per ship (Paul 2026-11-14), each with its hull and what it carries (Paul 2026-11-21). A plain click on a ship brings its
    /// own tab forward: its damage, its cargo, its class's numbers. Shift+click keeps the cards in front and picks several: over the cards,
    /// split them off or scrap them; or drag them onto another fleet's window.
    /// </summary>
    void ShipsTab(Fleet f, bool still, List<EconObject> picked)
    {
        var eco = S.Host.Economy;
        if (picked.Count > 1) PickedShips(f, picked, still);
        else Line(picked.Count == 1 ? "Shift+click more ships to split them off or scrap them together." : "Click a ship for its damage, its cargo and its class. Shift+click to pick several.", Ui.Dimmed, Ui.Tiny);
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
    /// One ship: its class and number, its hull, its damage in words, whether it is being repaired, what it carries, then the numbers
    /// of its class (the same for every ship of the class, so they come last).
    /// rule: m3-ships-and-fighters §Damaged state, §Class data; m4-retreat-damage-repair §5.
    /// </summary>
    void ShipDetail(Fleet f, EconObject o)
    {
        var sc = Fm.ClassOf(o);
        if (sc == null) return;
        Section($"{o.Class} #{o.Id}", $"hull {o.Hull} of {sc.Hull}", ClassTip(o.Class));
        Add(HullBar(o.Hull, sc.Hull, FleetBodyW));
        var hits = (o.Hits ?? new List<int>()).Select((h, i) => (h, i)).Where(x => x.h > 0 && x.i < HitNames.Length).Select(x => $"{HitNames[x.i]} ({x.h})").ToList();
        Line(hits.Count == 0 ? "No damage." : $"Damaged: {string.Join(", ", hits)}." + (o.Hits is { Count: > 4 } hh && hh[4] > 0 ? " A damaged hyperdrive makes it slower." : ""),
            hits.Count == 0 ? Ui.Text3 : Ui.Warn, Ui.Small);
        if (o.RepairMode != 0) Line("Being repaired.", Ui.Text3, Ui.Small);

        // what it carries
        var sq = Fm.UnitsAboard(o.Id, ObjectCategory.Fighter).OrderBy(x => x.Id).ToList();
        var rg = Fm.UnitsAboard(o.Id, ObjectCategory.Regiment).OrderBy(x => x.Id).ToList();
        if (sc.FighterCapacity + sc.TroopCapacity > 0)
        {
            var parts = new List<string>();
            if (sc.FighterCapacity > 0) parts.Add($"{sq.Count}/{sc.FighterCapacity} fighters");
            if (sc.TroopCapacity > 0) parts.Add($"{rg.Count}/{sc.TroopCapacity} regiments");
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

        // the class's numbers
        if (S.Host.Economy.Costs.TryGet(o.Class, out var row))
        {
            var stats = ClassStats(row, 0, out string extra).Where(s => s.Item1 is not ("Refined cost" or "Days here")).ToList();
            Section($"Every {o.Class}", null, "The numbers of its class.");
            var grid = new GridContainer { Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
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
    }

    /// <summary>Several ships of the fleet picked, over its ship cards: split them off into a new fleet, or scrap them.</summary>
    void PickedShips(Fleet f, List<EconObject> ships, bool still)
    {
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        var ds = ships.Select(o => new Drag('S', o.Id)).ToList();
        string n = Ui.Count(ships.Count, "ship", "ships");
        string why = !still ? "The fleet is travelling." : ships.Count >= f.Ships.Count - ShipsLeaving(f) ? "A fleet keeps at least one ship: leave one out of the pick." : null;
        bar.AddChild(FleetAction($"Split {n} off", "Fleet Split", () => { foreach (var d in ds) if (!_moved.ContainsKey(d.Key) && RefuseSplit(d, f) == null) DropSplit(d, f); _picked.RemoveAll(ds.Contains); RefreshMgmt(); },
            why ?? $"They become a new fleet here: {PickedWords(ships)}. Or drag them onto another fleet's window to send them there.", why != null));
        bar.AddChild(FleetAction($"Scrap {n}...", "Fleet Scrap ships", () => AskScrap(ships, PickedWords(ships), f.At),
            still ? "Asks first. Half the refined cost comes back." : "The fleet is travelling.", !still, Ui.Danger));
        Add(bar);
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
        GroundLink(f);
    }

    /// <summary>The planet's window, where the fleet loads its fighters and troops from.</summary>
    void GroundLink(Fleet f)
    {
        if (Still(f)) MgLinks(($"Ground at {f.At}", () => OpenMgmt(MgKind.Ground, f.At), "The planet's window: drag its leaders, troops and fighters onto this fleet"));
    }

    void TroopsAboardTab(Fleet f, List<EconObject> aboard)
    {
        int room = Fm.FreeTroopSlots(f);
        Section("Troops aboard", room + aboard.Count == 0 ? "no transport" : $"{aboard.Count} · room for {Math.Max(0, room - Promised(f.Id))}");
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        UnitTiles(tz.Flow, aboard.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), f.At);
        if (aboard.Count == 0) tz.Flow.AddChild(EmptySlot(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet"));
        Add(tz.Box);
        GroundLink(f);
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
