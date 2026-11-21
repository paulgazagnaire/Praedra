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
/// A fleet's window, Paul's Paper design (fleet-new-frame-1 and -2, 2026-11-21), in the Infrastructure window's look. One column, so four
/// fleets stand side by side. The head: the name, the window buttons, the status with a 2 px bar (where it is, or how far along its trip).
/// A strip of tabs: Command (its orders, its behaviour, its three posts, its passengers), Ships (one row per ship: hull and cargo),
/// Fighters and Troops (what it carries out of its room). What the design leaves out stays reachable without new chrome: a right-click on
/// the head merges other fleets into it or scraps it, a right-click on a ship splits it off or scraps it, a ship's hover says its damage.
/// Several fleets are picked on the map with Shift+click, and a right-click on a system sends them there (Paul's pick: move only). Every
/// order is one the core already has, judged when the clock moves. Screens only.
/// </summary>
public partial class GameScreen
{
    const int FleetListW = 150;   // the Ground window's fleet rows, two to a row
    const int FleetBodyW = MgWidth - 34 - 4;   // the window's page (BodyWidth of a one-column window)
    const int CardSpace = 16;   // between cards, as in the design
    const int CardW = (FleetBodyW - 2 * CardSpace) / 3;   // a leader, squadron or regiment card: three a row
    const int CardH = 120;
    const int ShipPicW = 80;   // the ship row's picture cell

    static readonly string[] HitNames = { "shield generator", "weapons", "hold beam", "sub-light engines", "hyperdrive", "damage control" };

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
    /// Paul's design: the head (name, buttons, status), the strip of tabs, then the open tab on the lighter surface. Command is first: the
    /// orders are there. A ship picked keeps the Ships tab in front; Shift+click picks several, to split them off or scrap them together.
    /// </summary>
    void FleetWin(MgWin w, Fleet f)
    {
        bool still = Still(f);
        FleetHead(w, f, still);
        if (w.Min) return;

        var aboardF = FightersAboard(f);
        var aboardR = S.Host.Troops.RegimentsAboard(f);
        var riders = Cm.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None).OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        var eco = S.Host.Economy;
        var picked = LivePicks().Where(d => d.Kind == 'S' && f.Ships.Contains(d.Id)).Select(d => eco.Find(d.Id)).Where(o => o != null).ToList();
        int fighters = aboardF.Count + f.Squadrons.Count, regiments = aboardR.Count;
        // the tab counts, as the design: ships, and fighters and troops out of their room ("8/16")
        FleetStrip(w,
            ("Command", null, () => CommandTab(f, riders)),
            ("Ships", f.Ships.Count.ToString(), () => ShipsTab(f, still, picked)),
            ("Fighters", RoomCount(fighters, Fm.FreeBerths(f) + aboardF.Count), () => FightersAboardTab(f, aboardF)),
            ("Troops", RoomCount(regiments, Fm.FreeTroopSlots(f) + regiments), () => TroopsAboardTab(f, aboardR.Select(a => a.Regiment).ToList())));
    }

    /// <summary>"8/16" (what it carries out of its room); just the number with no room; nothing with neither.</summary>
    static string RoomCount(int have, int room) => room <= 0 ? (have == 0 ? null : have.ToString()) : $"{have}/{room}";

    // ================================================================== the head

    /// <summary>
    /// The head, on the darker surface with 24 px all round: the name in BitPotion 48 px with ⋮⋮ – × in the design's grey, then the status
    /// line and its bar. The ⋮⋮ drags the fleet onto another fleet's window to merge them. A right-click anywhere on the head offers what
    /// the design has no button for: merge another fleet into this one, scrap it.
    /// </summary>
    void FleetHead(MgWin w, Fleet f, bool still)
    {
        var head = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };   // a left drag still moves the window (the head under it)
        head.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        head.SetMeta(UiTest.FullName, $"Fleet head {f.Id}");
        RightClickMenu(head, () => HeadMenu(f).ToArray());
        var pad = Pad(24, 24, 24, 24);
        head.AddChild(pad);
        var parts = Boxed(new VBoxContainer(), 24);
        pad.AddChild(parts);

        var grip = WindowGlyph("⋮⋮");
        grip.AddThemeColorOverride("font_color", Look.Text);   // the design's grey (its dots stay the mono ⋮⋮: Space Grotesk has no such glyph)
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.TooltipText = Ui.Wrap($"Drag {f.Name} onto another fleet's window to merge it into that fleet. Right-click this head to merge other fleets into {f.Name}, or to scrap it.");
        if (still && !_moved.ContainsKey(new Drag('M', f.Id).Key)) MakeDraggable(grip, new List<Drag> { new('M', f.Id) }, f.Name);
        else grip.AddThemeColorOverride("font_color", Look.Dim);

        var row = Boxed(new HBoxContainer(), NameGap);
        string upper = f.Name.ToUpperInvariant();
        float room = WidthOf(w) - 2 - 48 - NameGap - 3 * WinButton - 2 * 4;
        int size = Ui.NameSize(upper, room);
        var name = Look.Words(upper, size, Look.Text);
        name.AddThemeFontOverride("font", Ui.NameFont(size, upper));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.AddChild(name);
        row.AddChild(WinButtons(w, grip, grey: true));
        parts.AddChild(row);
        if (!w.Min) parts.AddChild(FleetStatus(f, still));
        w.Head.AddChild(head);
    }

    /// <summary>
    /// "Status:" on the left and what the fleet is doing on the right ("Moving to Tiryal / ETA: 29 days"), the 2 px bar under it (how far
    /// along its trip, in your blue), then each order it has, with Cancel. Its strength and slowest ship are in the hover.
    /// </summary>
    Control FleetStatus(Fleet f, bool still)
    {
        var (words, share, tip) = StatusOf(f, still);
        var v = Boxed(new VBoxContainer(), 4);
        v.MouseFilter = MouseFilterEnum.Pass;
        v.TooltipText = Ui.Wrap(tip);
        v.SetMeta(UiTest.FullName, $"Fleet status {f.Id}");
        var line = Boxed(new HBoxContainer(), 8);
        line.AddChild(Look.Words("Status:", 12, Look.Text, true));
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0) line.AddChild(Look.Words("/", 12, Look.Muted));
            var l = Look.Words(words[i], 12, Look.Muted);
            if (i == 0)
            {
                // the first part takes the room left, against the right edge, and is cut with "…" rather than widen the window
                l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                l.HorizontalAlignment = HorizontalAlignment.Right;
                l.ClipText = true;
                l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            }
            line.AddChild(l);
        }
        v.AddChild(line);
        v.AddChild(new ThinBar { Share = share, Fill = Ui.You, Track = Look.Divider, CustomMinimumSize = new Vector2(0, 2) });
        var orders = S.PendingOrdersOf(f.Id).ToList();
        if (orders.Count > 0)
        {
            var ov = Boxed(new VBoxContainer(), 4);
            foreach (var (seq, pend) in orders) OrderLine(seq, $"Ordered: {pend}", 12, ov);
            v.AddChild(Ui.Space(4));
            v.AddChild(ov);
        }
        return v;
    }

    /// <summary>What the fleet is doing, in a few words; how far along its trip (0 to 1); and the hover, with its strength and slowest ship.</summary>
    (List<string> Words, float Share, string Tip) StatusOf(Fleet f, bool still)
    {
        string slow = SlowestShip(f);
        string more = $"\nStrength {Fm.StrengthOf(f):0}: how hard it hits, its damage rate against three Mandates with its damage counted (three Decrees score about 65)."
            + (slow != null ? $"\nSlowest ship: {slow}, which sets the fleet's speed." : "");
        if (f.Transit is { } t)
        {
            string to = t.Path[^1];
            int total = Math.Max(1, t.HopTicks.Sum()), left = DaysLeft(t);
            string go = f.JoinFleet is int j && j > 0 && S.Game.State.Fleets.TryGetValue(j, out var jf) && jf.Side == Me ? $"Moving to join {jf.Name}" : $"Moving to {to}";
            return (new List<string> { go, left <= 1 ? "ETA: tomorrow" : $"ETA: {left} days" }, 1f - left / (float)total,
                $"{f.Name} is flying to {to}: it arrives on day {GameSession.ArrivalDay(t)}. The bar shows how far along it is." + more);
        }
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto))
            return (new List<string> { char.ToUpperInvariant(mto.Words[0]) + mto.Words[1..] }, 0, $"{f.Name} {mto.Words} when the clock moves." + more);
        if (S.PlannedMoveOf(f.Id) is { } pm)
        {
            int days = Math.Max(1, pm.ArrivalDay - S.Game.Day);
            return (new List<string> { $"Leaves for {pm.To}", $"ETA: {days} days" }, 0, $"{f.Name} leaves for {pm.To} when the clock moves and arrives on day {pm.ArrivalDay}." + more);
        }
        return (new List<string> { $"In orbit at {f.At}" }, 0, $"{f.Name} is in orbit at {f.At}." + more);
    }

    /// <summary>Days until a trip ends: what is left of this hop and the hops after it.</summary>
    int DaysLeft(FleetTransit t) => t == null ? 0 : Math.Max(0, t.DueDay - S.Game.Day) + t.HopTicks.Skip(t.HopIndex + 1).Sum();

    /// <summary>The class of the fleet's slowest ship: the one with the largest trip time (rule: m1-travel §Rule, the slowest ship sets the speed).</summary>
    string SlowestShip(Fleet f)
    {
        var eco = S.Host.Economy;
        return f.Ships.Select(eco.Find).Where(o => o != null).OrderByDescending(Fm.SpeedOfShip).ThenBy(o => o.Class, StringComparer.Ordinal).FirstOrDefault()?.Class;
    }

    /// <summary>The head's right-click: merge another fleet into this one (those here join at once, the others fly here, closest first),
    /// then scrap the whole fleet. What the design has no button for.</summary>
    List<(string Text, Action Do)> HeadMenu(Fleet f)
    {
        if (!S.Game.State.Fleets.TryGetValue(f.Id, out var live) || !Still(live)) return new List<(string, Action)>();
        var lines = MergeLines(live);
        var ships = live.Ships.Select(S.Host.Economy.Find).Where(o => o != null && !_scrapped.Contains(o.Id)).ToList();
        if (ships.Count > 0) lines.Add(("Scrap the whole fleet...", () => AskScrap(ships, $"all {ships.Count} ships of {live.Name}", live.At)));
        return lines;
    }

    // ================================================================== the strip of tabs

    /// <summary>
    /// The tabs as the design's strip: cells 1 px apart on the line colour, the open one on the lighter surface so it joins the page under
    /// it; the name, and its count in the dim grey. Every page is built (so every drop place exists), only the open one shows. While a unit
    /// is dragged over a cell, it opens, so a drop can reach a page that was hidden.
    /// </summary>
    void FleetStrip(MgWin w, params (string Name, string Count, Action Build)[] tabs)
    {
        if (w.Tab == null || !tabs.Any(t => t.Name == w.Tab)) w.Tab = tabs[0].Name;
        var strip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var lines = Look.Box(Look.Line);
        lines.ContentMarginTop = 1;   // the line above the cells
        strip.AddThemeStyleboxOverride("panel", lines);
        var h = Boxed(new HBoxContainer(), 1);   // the line between cells
        strip.AddChild(h);
        var group = new ButtonGroup();
        var prev = _into;
        foreach (var (name, count, build) in tabs)
        {
            string tabName = name;
            var b = new Button { ToggleMode = true, ButtonGroup = group, ButtonPressed = name == w.Tab, FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand };
            b.AddThemeStyleboxOverride("normal", Look.Box(Look.Bg));
            b.AddThemeStyleboxOverride("hover", Look.Box(new Color("17171a")));
            b.AddThemeStyleboxOverride("pressed", Look.Box(Look.Band));
            b.AddThemeStyleboxOverride("hover_pressed", Look.Box(Look.Band));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.SetMeta(UiTest.FullName, $"Tab {w.Key} {name}");
            var row = Boxed(new HBoxContainer(), 8);
            var l = Look.Words(name, 12, Look.Text);
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(l);
            if (count != null) row.AddChild(Look.Words(count, 12, Look.Dim));
            // as the design: Command and Ships fit their words (Ships at least 72 px), Fighters and Troops share the rest
            Fill(b, row, 8, 8, name == "Ships" ? new Vector2(72, 0) : null);
            b.SizeFlagsHorizontal = name is "Fighters" or "Troops" ? SizeFlags.ExpandFill : SizeFlags.Fill;
            b.Pressed += () => { w.Tab = tabName; Fit(w); };
            // spring-loaded: hovering a dragged unit over a cell opens it
            b.SetDragForwarding(new Callable(),
                Callable.From((Func<Vector2, Variant, bool>)((_, _) =>
                {
                    if (w.Tab != tabName) { w.Tab = tabName; Callable.From(() => { if (IsInstanceValid(w.Panel)) Fit(w); }).CallDeferred(); }
                    return false;
                })), new Callable());
            h.AddChild(b);
            var page = Boxed(new VBoxContainer(), WinGap);
            page.SetMeta("page", name);
            w.Body.AddChild(page);
            w.Pages[name] = (page, b);
            _into = page;
            try { build(); } finally { _into = prev; }
        }
        w.Head.AddChild(strip);
        foreach (var (name, (page, _)) in w.Pages) page.Visible = name == w.Tab;
    }

    /// <summary>A part of a page: its title (16 px, with a dim count or note beside it) and what is under it, <paramref name="gap"/> apart.</summary>
    VBoxContainer FleetSection(string title, int gap, string note = null, string tip = null)
    {
        var v = Boxed(new VBoxContainer(), gap);
        var hd = Heading2(title, note);
        if (tip != null) { hd.TooltipText = Ui.Wrap(tip); hd.MouseFilter = MouseFilterEnum.Pass; }
        v.AddChild(hd);
        Add(v);
        return v;
    }

    // ================================================================== Command: orders, behaviour, posts, passengers

    void CommandTab(Fleet f, List<CharacterRecord> riders)
    {
        FleetSection("Fleet actions", 8).AddChild(FleetActions(f));

        // each fleet keeps its own answer (rule: m4-battle-classic §1.3, SetFleetOnContact {fleet, mode}, per fleet, Paul 2026-11-21)
        FleetSection("Fleet behavior", 8, tip: "What this fleet does when an enemy fleet arrives. Each fleet has its own setting.")
            .AddChild(BehaviorRow(f));

        var cmd = FleetSection("Fleet command", 16);
        var posts = Boxed(new HBoxContainer(), CardSpace);
        foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander })
        {
            var p = post;
            var col = Boxed(new VBoxContainer(), 8);
            col.CustomMinimumSize = new Vector2(CardW, 0);
            var cap = Look.Words(GameSession.PostWords(post), 12, Look.Text);
            cap.MouseFilter = MouseFilterEnum.Pass;
            cap.TooltipText = post switch
            {
                CharacterPost.Admiral => "The Admiral leads the fleet in space battles and bombardments.",
                CharacterPost.General => "The General leads the troops when the fleet invades.",
                _ => "The Commander has no effect yet.",
            };
            col.AddChild(cap);
            var z = DropZone($"Fleet {f.Id} {post}", $"make {GameSession.PostWords(post)}", d => RefuseFleetPost(d, f, p), d => DropFleetPost(d, f, p));
            if (Cm.HolderOf(f.Id, post) is { } holder) z.Flow.AddChild(LeaderCard(holder));
            else z.Flow.AddChild(EmptyCard(DropLeader));
            col.AddChild(z.Box);
            posts.AddChild(col);
        }
        cmd.AddChild(posts);

        var pas = FleetSection("Passengers", 16, null, "Leaders who ride the fleet with no post: they travel with it.");
        var pz = DropZone($"Fleet {f.Id} Passengers", "take aboard", d => RefuseFleetPost(d, f, CharacterPost.None), d => DropFleetPost(d, f, CharacterPost.None));
        CardFlow(pz.Flow);
        foreach (var c in riders) pz.Flow.AddChild(LeaderCard(c));
        if (riders.Count == 0) pz.Flow.AddChild(EmptyCard(DropLeader));
        pas.AddChild(pz.Box);

        // leaders and fleets sent to join it from other systems (m5 §Moving a character alone, m3 §Joining a fleet on arrival)
        var ls = Cm.All.Where(c => c.Side == Me && c.JoinFleet == f.Id && c.JoinByOrder && c.Transit != null).OrderBy(c => c.Id).ToList();
        var fs = S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.JoinFleet == f.Id && x.Transit != null).OrderBy(x => x.Id).ToList();
        if (ls.Count + fs.Count > 0)
        {
            var way = FleetSection("On the way", 16, (ls.Count + fs.Count).ToString(), $"Sent to join {f.Name} from other systems. They take their place when they arrive.");
            var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
            bool hand = Me == Side.Hand;
            foreach (var c in ls)
            {
                string name = S.CharName(c.Id);
                flow.AddChild(FleetCard(Avatar(name, Ui.You), name, Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), ghost: true));
            }
            foreach (var x in fs) flow.AddChild(FleetCard(UnitPic(UnitIcon.Kind.Ship, hand, 2, Ui.You), x.Name, Arrives(x.Transit, $"{Ui.Count(x.Ships.Count, "ship", "ships")}, to merge"), ghost: true));
            way.AddChild(flow);
        }
    }

    /// <summary>
    /// The fleet's orders, as the design: Bombardment ▾ (military, civilian or everything), Invasion, Move; and the harvester's strike when
    /// it is in the fleet. A button that can't act is greyed and its hover says why. The core judges each order when the clock moves.
    /// rule: m3-fleets §Orders, m4-bombardment, m4-invasion, m5-harvester §2.
    /// </summary>
    Control FleetActions(Fleet f)
    {
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        bool still = Still(f);
        string travelling = $"Travelling: give this order when it reaches {f.Transit?.Path[^1] ?? "its destination"}.";
        bool canHit = CanAttackHere(f);
        int regs = S.Host.Troops.RegimentsAboard(f).Count;
        string hitWhy = canHit ? null : AttackWhyNot(f);

        bar.AddChild(MenuAction("Bombardment", "Fleet Bombard", !canHit ? Array.Empty<(string, Action)>() : new (string, Action)[]
        {
            ("Military targets: regiments and defences", () => { Bombard(f, BombardCategory.Military); MgRefreshSoon(); }),
            ("Civilian targets: the planet's support", () => { Bombard(f, BombardCategory.Civilian); MgRefreshSoon(); }),
            ("Everything", () => { Bombard(f, BombardCategory.General); MgRefreshSoon(); }),
        }, hitWhy ?? "Bombard the system from orbit: its regiments and defences, its support, or everything."));
        bar.AddChild(FleetAction("Invasion", "Fleet Invade", () => { Invade(f); MgRefreshSoon(); },
            hitWhy ?? (regs > 0 ? $"Lands the {Ui.Count(regs, "regiment", "regiments")} aboard to take the planet: land more than the defenders have." : "No regiments aboard: load some at one of your planets first (Troops tab)."),
            !canHit || regs == 0));
        int fid = f.Id;
        bar.AddChild(FleetAction("Move", "Fleet Move", () => { if (S.Game.State.Fleets.TryGetValue(fid, out var ff)) { CloseSectorView(); StartMove(ff); } },
            still ? "Pick a destination on the map. To send several fleets at once, Shift+click them on the map and right-click a system." : travelling, !still));

        // the harvester's strike
        if (still && S.HoldsHarvester(f))
        {
            bool struck = S.PendingOf(f.Id).Any(p => p.StartsWith("Destroy System", StringComparison.Ordinal));
            int? readyDay = S.HarvesterRechargingUntil(f);
            bar.AddChild(FleetAction("Destroy System...", "Fleet Destroy", () => AskDestroySystem(f.Id),
                S.IsDestroyed(f.At) ? $"{f.At} is already destroyed." + (readyDay is int rd0 ? " " + S.HarvestWaitText(rd0) : "") : struck ? "The strike is ordered: it happens as soon as the clock moves." : readyDay is int rd ? S.HarvestWaitText(rd) : "Wipes out the system the harvester is at. Asks first.",
                S.IsDestroyed(f.At) || struck || readyDay != null, Ui.Danger));
        }
        return bar;
    }

    /// <summary>This fleet's choice when an enemy fleet arrives, Defensive (fight) or Elusive (pull back); the chosen one has the light outline.
    /// rule: m4-battle-classic §1.3 (per fleet; in a battle with both, the Elusive fleets leave in their first step and the rest fight).</summary>
    Control BehaviorRow(Fleet f)
    {
        var row = Boxed(new HBoxContainer(), 8);
        bool Elusive() => _retreatPending.TryGetValue(f.Id, out var p) ? p : S.Host.Battles.OnContactOfFleet(f.Id) == OnContactMode.Retreat;
        bool retreat = Elusive();
        foreach (var (word, on, tip) in new[]
        {
            ("Defensive", false, "This fleet stays and fights an enemy fleet that arrives."),
            ("Elusive", true, "This fleet pulls back when an enemy fleet arrives, instead of fighting. Your Defensive fleets there stay and fight without it. An enemy ship that blocks retreat can keep it in the fight."),
        })
        {
            bool r = on;
            var b = FleetAction(word, $"Behavior {word}", () => { if (r != Elusive()) { SetRetreat(f, r); MgRefreshSoon(); } },
                tip + (retreat == on ? " Chosen." : ""));
            if (retreat == on) b.AddThemeStyleboxOverride("normal", ButtonBox(Look.Text));
            row.AddChild(b);
        }
        return row;
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
        if (here.Count > 1) lines.Add(($"Merge all {here.Count} fleets here, at once", () => MergeInto(f, here)));
        foreach (var x in here) lines.Add(($"Merge {x.Name}: here, joins at once", () => MergeInto(f, new List<Fleet> { x })));
        foreach (var (x, plan) in far)
            lines.Add(($"Merge {x.Name}: at {x.At}, flies here in {Ui.Count(plan.HopTicks.Sum(), "day", "days")}" + (plan.Red ? ", known trap on the way" : ""), () => MergeInto(f, new List<Fleet> { x })));
        return lines;
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

    // ================================================================== buttons and cards in the design's look

    /// <summary>The design's button: a 1 px #36363C outline, 8 px inside, 12 px words. Brighter outline under the mouse, the words greyed
    /// with the reason in the hover when it can't act; <paramref name="ink"/> for a meaning colour (the harvester's red).</summary>
    static Button FleetAction(string words, string key, Action act, string tip, bool disabled = false, Color? ink = null, bool menu = false)
    {
        var b = new Button { FocusMode = FocusModeEnum.None, Disabled = disabled, TooltipText = Ui.Wrap(tip),
            MouseDefaultCursorShape = disabled ? CursorShape.Arrow : CursorShape.PointingHand };
        foreach (var (state, line) in new[] { ("normal", Look.Outline), ("hover", Look.Soft), ("pressed", Look.Text), ("hover_pressed", Look.Text), ("disabled", Look.Line) })
            b.AddThemeStyleboxOverride(state, ButtonBox(line));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var c = disabled ? Look.Dim : ink ?? Look.Text;
        var h = Boxed(new HBoxContainer(), 8);
        h.AddChild(Look.Words(words, 12, c));
        if (menu) { var arrow = Look.Words("▾", 12, c, true); arrow.SizeFlagsVertical = SizeFlags.ShrinkCenter; h.AddChild(arrow); }
        Fill(b, h, 8, 8);
        b.Pressed += act;
        b.SetMeta(UiTest.FullName, key);
        return b;
    }

    static StyleBoxFlat ButtonBox(Color line) => Look.Box(new Color(0, 0, 0, 0), line, 1, 1, 1, 1);

    /// <summary>An order button that opens a short list of choices under it (the bombard targets).</summary>
    Button MenuAction(string words, string key, (string Text, Action Do)[] lines, string tip)
    {
        Button b = null;
        b = FleetAction(words, key, () => PopMenu(b, lines), tip, lines.Length == 0, menu: true);
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

    /// <summary>A card's frame: a 1 px line, 4 px inside it. Picked: the line in your blue.</summary>
    static StyleBoxFlat CardBox(Color line)
    {
        var b = Look.Box(new Color(0, 0, 0, 0), line, 1, 1, 1, 1);
        b.ContentMarginLeft = b.ContentMarginRight = b.ContentMarginTop = b.ContentMarginBottom = 5;
        return b;
    }

    /// <summary>A ship row's frame: a 1 px line, nothing inside it (the picture cell runs to the line). Picked: the line in your blue.</summary>
    static StyleBoxFlat RowBox(Color line)
    {
        var b = Look.Box(new Color(0, 0, 0, 0), line, 1, 1, 1, 1);
        b.ContentMarginLeft = b.ContentMarginRight = b.ContentMarginTop = b.ContentMarginBottom = 1;
        return b;
    }

    /// <summary>A card from the design (a post, a passenger, a squadron, a regiment): its picture on the darker surface, 76 px high, and its
    /// name under it, centred, two lines at most; a short dim line under that when it has something to say.</summary>
    PanelContainer FleetCard(Control pic, string name, string sub = null, bool ghost = false)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(CardW, CardH) };
        p.SetMeta("card", "card");
        p.AddThemeStyleboxOverride("panel", CardBox(Look.Outline));
        var v = Boxed(new VBoxContainer(), 4);
        p.AddChild(v);
        var holder = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 76) };
        holder.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.AddChild(pic);
        holder.AddChild(center);
        v.AddChild(holder);
        var t = Look.Words(KeepNumeral(name), 12, Look.Text);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.AutowrapMode = TextServer.AutowrapMode.Word;
        t.MaxLinesVisible = 2;
        t.CustomMinimumSize = new Vector2(CardW - 10, 0);
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(t);
        if (sub != null)
        {
            var s = Look.Words(sub, 12, ghost ? Pending : Look.Dim);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            s.ClipText = true; s.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            s.CustomMinimumSize = new Vector2(CardW - 10, 0);
            v.AddChild(s);
        }
        if (ghost) p.Modulate = new Color(1, 1, 1, 0.62f);
        return p;
    }

    /// <summary>An empty place on a card's footprint: dashed #36363C, its words centred (the design's "Drop a leader here"). A label, so a
    /// drop shown before the day applies it takes its place.</summary>
    static Label EmptyCard(string words, float width = CardW, float height = CardH)
    {
        var box = new DashedBox { Ink = Look.Outline };
        box.ContentMarginLeft = box.ContentMarginRight = 12; box.ContentMarginTop = box.ContentMarginBottom = 8;
        var l = Look.Words(words, 12, Look.Text);
        l.AddThemeStyleboxOverride("normal", box);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center;
        l.CustomMinimumSize = new Vector2(width, height);
        return l;
    }

    /// <summary>Cards in a flow, 16 px apart both ways (three a row).</summary>
    static HFlowContainer CardFlow(HFlowContainer flow)
    {
        flow.AddThemeConstantOverride("h_separation", CardSpace); flow.AddThemeConstantOverride("v_separation", CardSpace);
        return flow;
    }

    /// <summary>A leader's picture until leaders have portraits: their initials in a ring (portraits stay round).</summary>
    static Control Avatar(string name, Color ink)
    {
        var ring = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(44, 44) };
        ring.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0, 0, 0, 0), 22, ink, 1, 0, 0));
        var l = Look.Words(Initials(name), 14, ink, true);
        l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center;
        ring.AddChild(l);
        return ring;
    }

    /// <summary>A unit's pixel icon at 2 px a pixel (icons are code only, Paul 2026-11-18).</summary>
    static Control UnitPic(UnitIcon.Kind kind, bool hand, int size4, Color ink) =>
        PixelIcons.Unit(kind, hand, size4) is { } grid ? IconPic.Of(grid, 2, ink) : new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(32, 32) };

    /// <summary>A leader as a card: their picture and name. Drags unless an order already moves them today, or they cannot act; their
    /// skills and the posts they can hold are in the hover.</summary>
    Control LeaderCard(CharacterRecord c)
    {
        string name = S.CharName(c.Id);
        var d = new Drag('L', c.Id);
        if (_moved.ContainsKey(d.Key)) return new Control { Visible = false };   // it already stands where it was dropped
        bool can = c.Status == CharacterStatus.Free && c.Transit == null && !Cm.IsOnMission(c.Id);
        string sub = c.Status != CharacterStatus.Free ? GameSession.StatusWords(c) : Cm.IsOnMission(c.Id) ? "on a mission" : null;
        var card = FleetCard(Avatar(name, can ? Ui.You : Look.Soft), name, sub);
        bool mission = CanJoin(c);
        card.TooltipText = Ui.Wrap($"{name}. {S.SkillsOf(c)}\n{S.CapabilityOf(c)}\nCan hold: {LeaderPosts(c)}." + (can ? "\nDrag onto a post or a fleet." : "")
            + (mission ? " Right-click to send them on a mission." : ""));
        card.SetMeta(UiTest.FullName, "Leader " + name);
        if (can) MakeDraggable(card, new List<Drag> { d }, name);
        if (mission) MissionMenu(card, c.System, c.Id, null);
        return card;
    }

    /// <summary>A squadron or regiment as a card (the Fighters and Troops tabs): its icon, its class, and a short line (a damaged hull,
    /// "flies along").</summary>
    PanelContainer UnitCard(EconObject o, string sub, bool can) =>
        FleetCard(UnitPic(KindOf(o.Category, o.Class), Me == Side.Hand, SizeOfClass(o.Class), can ? Ui.You : Look.Soft), o.Class, sub);

    /// <summary>What a drop into one of this window's places shows until the clock moves: the card or row it will be, faded.</summary>
    Control FleetGhost(Drag d, string sub)
    {
        bool hand = Me == Side.Hand;
        switch (d.Kind)
        {
            case 'L': string name = S.CharName(d.Id); return FleetCard(Avatar(name, Ui.You), name, sub, ghost: true);
            case 'M': return FleetCard(UnitPic(UnitIcon.Kind.Ship, hand, 2, Ui.You), ThingName(d), sub, ghost: true);
            case 'S' when S.Host.Economy.Find(d.Id) is { } ship: { var r = ShipRow(ship); r.Modulate = new Color(1, 1, 1, 0.62f); return r; }
            default:
                var o = S.Host.Economy.Find(d.Id);
                return FleetCard(UnitPic(o == null ? UnitIcon.Kind.Regiment : KindOf(o.Category, o.Class), hand, SizeOfClass(o?.Class), Ui.You), o?.Class ?? "unit", sub, ghost: true);
        }
    }

    // ================================================================== Ships: one row per ship

    /// <summary>
    /// One row per ship (Paul's design): its picture, its class, what it carries out of its room, its hull. A click picks it, Shift+click
    /// picks several: split them off or scrap them together (over the rows), or drag them onto another fleet's window. A right-click splits
    /// it off or scraps it. Its damage, in words, is in its hover. While one of its ships is dragged, a dashed place under the rows takes it
    /// as a new fleet.
    /// </summary>
    void ShipsTab(Fleet f, bool still, List<EconObject> picked)
    {
        var eco = S.Host.Economy;
        if (picked.Count > 1) PickedShips(f, picked, still);
        var sz = DropZone($"Fleet {f.Id} Ships", "join", d => RefuseShipsInto(d, f), d => DropShipsInto(d, f));
        sz.Flow.AddThemeConstantOverride("v_separation", 8);
        var ships = f.Ships.Select(eco.Find).Where(o => o != null && !_moved.ContainsKey(new Drag('S', o.Id).Key) && !_scrapped.Contains(o.Id)).ToList();
        foreach (var o in ships.GroupBy(o => o.Class).SelectMany(g => g))
        {
            var d = new Drag('S', o.Id);
            var row = ShipRow(o);
            row.SetMeta(UiTest.FullName, d.Key);
            // this ship first, then the same class beside it: a plain drag takes this one, Shift takes them all
            var same = ships.Where(x => x.Class == o.Class && x.Id != o.Id).ToList();
            var drags = new List<Drag> { d };
            drags.AddRange(same.Select(x => new Drag('S', x.Id)));
            MakeDraggable(row, drags, o.Class);
            row.TooltipText = Ui.Wrap(ShipTip(o) + "\nClick to pick it; Shift+click to pick several. Drag to another fleet's window"
                + (same.Count > 0 ? $" (hold Shift as you start the drag to move all {same.Count + 1} {o.Class})" : "") + "."
                + (still ? "\nRight-click: split it off or scrap it." : ""));
            var one = o;
            if (still) RightClickMenu(row, () => ShipMenu(f, one).ToArray());
            sz.Flow.AddChild(row);
        }
        Add(sz.Box);
        // the place to split ships off: shown only while one of its ships is dragged (the design has no slot for it)
        if (still && f.Ships.Count > 1)
        {
            var nz = DropZone($"Fleet {f.Id} NewFleet", "split off", d => RefuseSplit(d, f), d => DropSplit(d, f), dashed: true);
            nz.Box.SetMeta("dragonly", true);
            nz.Box.Visible = false;
            nz.Box.TooltipText = Ui.Wrap("Drop ships here to split them off into a new fleet.");
            nz.Flow.AddChild(EmptyCard("Drop here to split them off into a new fleet", FleetBodyW - 12, 64));
            Add(nz.Box);
        }
    }

    /// <summary>A ship as the design's row: the picture cell (80 px, the darker surface), then its class, "Capacity" with its fighters and
    /// regiments out of their room, and "Hull" with its hull out of its class's (amber under half, red under a quarter).</summary>
    PanelContainer ShipRow(EconObject o)
    {
        var sc = Fm.ClassOf(o);
        // more than half the page, so it takes a line of its own, and stretched across it (the place it stands in may be narrower)
        var row = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(FleetBodyW / 2 + 1, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.SetMeta("card", "row");
        row.AddThemeStyleboxOverride("panel", RowBox(Look.Edge));
        var h = Boxed(new HBoxContainer(), 0);
        row.AddChild(h);
        var cell = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(ShipPicW, 0) };
        cell.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.AddChild(UnitPic(UnitIcon.Kind.Ship, Me == Side.Hand, SizeOfClass(o.Class), Ui.You));
        cell.AddChild(center);
        h.AddChild(cell);
        var right = Pad(16, 16, 16, 16);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(right);
        var v = Boxed(new VBoxContainer(), 4);
        right.AddChild(v);
        v.AddChild(Look.Words(o.Class, 13, Look.Text, true));
        var lines = Boxed(new VBoxContainer(), 0);
        v.AddChild(lines);

        var cap = Boxed(new HBoxContainer(), 4);
        var cl = Look.Words("Capacity", 12, Look.Dim);
        cl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cap.AddChild(cl);
        string side = Me == Side.Hand ? "hand" : "collective";
        void Cargo(string icon, int have, int room)
        {
            if (room <= 0) return;
            if (cap.GetChildCount() > 1) cap.AddChild(new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(8, 0) });
            var i = IconPic.Of(PixelIconData.Units[$"{icon}-{side}"], 1, Look.Dim);
            i.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cap.AddChild(i);
            cap.AddChild(Look.Words($"{have}/{room}", 12, Look.Dim));
        }
        if (sc != null)
        {
            Cargo("fighter", Fm.UnitsAboard(o.Id, ObjectCategory.Fighter).Count(), sc.FighterCapacity);
            Cargo("regiment", Fm.UnitsAboard(o.Id, ObjectCategory.Regiment).Count(), sc.TroopCapacity);
        }
        if (cap.GetChildCount() == 1) cap.AddChild(Look.Words("none", 12, Look.Dim));
        lines.AddChild(cap);

        var hull = Boxed(new HBoxContainer(), 4);
        var hl = Look.Words("Hull", 12, Look.Dim);
        hl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        hull.AddChild(hl);
        int max = sc?.Hull ?? o.Hull;
        float r = max <= 0 ? 1 : o.Hull / (float)max;
        hull.AddChild(Look.Words($"{o.Hull}/{max}", 12, r < 0.25f ? Ui.Danger : r < 0.5f ? Ui.Warn : Look.Dim));
        lines.AddChild(hull);
        return row;
    }

    /// <summary>A ship's hover: its class and number, its damage in words, whether it is being repaired, and what its class is for.
    /// rule: m3-ships-and-fighters §Damaged state; m4-retreat-damage-repair §5.</summary>
    string ShipTip(EconObject o)
    {
        var hits = (o.Hits ?? new List<int>()).Select((h, i) => (h, i)).Where(x => x.h > 0 && x.i < HitNames.Length).Select(x => $"{HitNames[x.i]} ({x.h})").ToList();
        string tip = $"{o.Class} #{o.Id}. " + (hits.Count == 0 ? "No damage." : $"Damaged: {string.Join(", ", hits)}." + (o.Hits is { Count: > 4 } hh && hh[4] > 0 ? " A damaged hyperdrive makes it slower." : ""));
        if (o.RepairMode != 0) tip += " Being repaired.";
        if (ClassTip(o.Class) is string ct) tip += "\n" + ct;
        return tip;
    }

    /// <summary>A ship's right-click: split it (or the ships picked with it) off into a new fleet here, or scrap it, all of its class, or
    /// the ships picked. rule: m3-fleets §Orders (SplitFleet); m2-maintenance §Scrapping and refunds.</summary>
    List<(string Text, Action Do)> ShipMenu(Fleet f, EconObject o)
    {
        var lines = new List<(string, Action)>();
        if (!S.Game.State.Fleets.TryGetValue(f.Id, out var live) || !Still(live) || !live.Ships.Contains(o.Id)) return lines;
        var d = new Drag('S', o.Id);
        var picks = IsPicked(d) ? LivePicks().Where(x => x.Kind == 'S' && live.Ships.Contains(x.Id)).ToList() : new List<Drag>();
        var many = picks.Count > 1 ? picks : new List<Drag> { d };
        bool keepsOne = many.Count < live.Ships.Count - ShipsLeaving(live);
        if (keepsOne && many.All(x => !_moved.ContainsKey(x.Key) && RefuseSplit(x, live) == null))
            lines.Add((many.Count > 1 ? $"Split the {many.Count} picked off into a new fleet" : "Split it off into a new fleet",
                () => { foreach (var x in many) if (!_moved.ContainsKey(x.Key) && RefuseSplit(x, live) == null) DropSplit(x, live); _picked.RemoveAll(many.Contains); RefreshMgmt(); }));
        if (many.Count > 1)
        {
            var all = many.Select(x => S.Host.Economy.Find(x.Id)).Where(x => x != null && Scrappable(x)).ToList();
            if (all.Count > 0) lines.Add(($"Scrap the {all.Count} picked...", () => AskScrap(all, PickedWords(all), live.At)));
        }
        if (Scrappable(o)) lines.Add(("Scrap this one...", () => AskScrap(new[] { o }, $"one {o.Class}", live.At)));
        var same = live.Ships.Select(S.Host.Economy.Find).Where(x => x != null && x.Class == o.Class && Scrappable(x) && !_scrapped.Contains(x.Id)).ToList();
        if (same.Count > 1) lines.Add(($"Scrap all {same.Count} {o.Class}...", () => AskScrap(same, $"{same.Count} x {o.Class}", live.At)));
        return lines;
    }

    /// <summary>Several ships of the fleet picked, over its rows: split them off into a new fleet, or scrap them.</summary>
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

    // ================================================================== Fighters and Troops: what it carries

    void FightersAboardTab(Fleet f, List<EconObject> aboard)
    {
        var eco = S.Host.Economy;
        int berths = Fm.FreeBerths(f);
        var qz = DropZone($"Fleet {f.Id} Fighters", "load", d => RefuseOnto(d, f, 'Q'), d => DropOnto(d, f));
        CardFlow(qz.Flow);
        UnitTiles(qz.Flow, aboard.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o)))
            .Concat(f.Squadrons.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)null, o, "flies along"))), f.At, UnitCard);
        if (aboard.Count + f.Squadrons.Count == 0) qz.Flow.AddChild(EmptyCard(berths > 0 ? "Drop squadrons here to load them" : "No carrier in this fleet"));
        Add(qz.Box);
        GroundLink(f);
    }

    /// <summary>The planet's window, where the fleet loads its fighters and troops from.</summary>
    void GroundLink(Fleet f)
    {
        if (!Still(f)) return;
        var b = FleetAction($"Ground at {f.At}", $"Ground at {f.At}", () => OpenMgmt(MgKind.Ground, f.At),
            $"The Ground window of {f.At}: drag its leaders, troops and fighters onto this fleet, or this fleet's onto the planet.");
        b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        Add(b);
    }

    void TroopsAboardTab(Fleet f, List<EconObject> aboard)
    {
        int room = Fm.FreeTroopSlots(f);
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        CardFlow(tz.Flow);
        UnitTiles(tz.Flow, aboard.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), f.At, UnitCard);
        if (aboard.Count == 0) tz.Flow.AddChild(EmptyCard(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet"));
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
        int left = DaysLeft(t);
        return (what != null ? what + ", " : "") + (left <= 1 ? "arrives tomorrow" : $"{left} days");
    }
}
