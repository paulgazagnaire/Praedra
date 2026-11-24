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
/// A fleet's window, Paul's Paper design (fleet-new-frame-1 to -4, 2026-11-23; export praedra/specs/ui/paper/fleet-new-2026-11-23/), in
/// the Infrastructure window's look. One column, so four fleets stand side by side. The head: the name, the window buttons, the status with
/// a 2 px bar (where it is, or how far along its trip). A strip of tabs: Command (its orders, its behaviour, its three posts, its
/// passengers), Ships, Fighters and Troops. Everything it holds is a card, three a row: a picture on the darker surface, the name under it
/// (a ship adds its cargo and hull). The buttons of a tab sit under its cards (Unload, Load, Ground at the planet). What the design left out
/// is a plain button (2026-11-22): Merge under the actions, Split and Scrap under the ship cards; right-clicks on the head and on a ship are
/// shortcuts to the same.
/// Several fleets are picked on the map with Shift+click, and a right-click on a system sends them there (Paul's pick: move only). Every
/// order is one the core already has, judged when the clock moves. Screens only.
/// </summary>
public partial class GameScreen
{
    const int FleetListW = 150;   // the Ground window's fleet rows, two to a row
    const int FleetBodyW = MgWidth - 34 - 4;   // the window's page (BodyWidth of a one-column window)
    // a card, as Paul's frames of 2026-11-23: its picture tile 96 x 48 on the darker surface, 4 px, its name; three a row, 16 px between rows.
    // Across, the design's page is 312 px (12 px between cards); here the scroll grabber keeps 4 px of it, so 9 px between cards
    const int CardW = 96, CardPicH = 48;
    const int CardGapX = (FleetBodyW - 3 * CardW) / 2, CardGapY = 16;
    const int LeaderCardH = CardPicH + 4 + 32;   // a leader's card: two lines of name (first name over last name), the empty post's size
    const int WideEmptyH = 80;   // the design's full-width "Drop regiments here to load them"

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

    /// <summary>Shift+click on a marker several fleets of yours share (Paul 2026-11-22): they all join the fleets picked, or all leave when
    /// they all were. The fleet selected before counts as the first one picked, as for one fleet.</summary>
    void ShiftPickGroup(IReadOnlyList<int> ids)
    {
        var mine = ids.Where(id => S.Game.State.Fleets.TryGetValue(id, out var f) && f.Side == Me).ToList();
        if (mine.Count == 0) return;
        var picks = _view.PickedFleets;
        if (picks.Count == 0 && _view.SelectedFleet >= 0 && !mine.Contains(_view.SelectedFleet) && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var sel) && sel.Side == Me)
            picks.Add(_view.SelectedFleet);
        if (mine.All(picks.Contains)) foreach (int id in mine) picks.Remove(id);
        else foreach (int id in mine) picks.Add(id);
        _view.QueueRedraw(); _fleets.QueueRedraw();
        if (picks.Count > 1) Toast($"{Ui.Count(picks.Count, "fleet", "fleets")} picked: right-click a system to send them there. Esc lets them go.", MsgKind.Info);
    }

    (string Text, Action Do)[] _groupMenu;
    PopupMenu _groupPopup;

    /// <summary>
    /// A click on a marker several fleets of yours share (Paul's pick 2026-11-22, "Open a list of the fleets"): a short list of them by the
    /// click, each with its ships and its behaviour; picking one selects it and opens its Fleet window (Paul 2026-11-23: "the fleet window
    /// opens", not the window on the right).
    /// </summary>
    void ShowFleetGroup(IReadOnlyList<int> ids, Vector2 at)
    {
        var items = ids.Select(id => S.Game.State.Fleets.TryGetValue(id, out var f) && f.Side == Me ? f : null).Where(f => f != null)
            .Select(f => ($"{S.NameOf(f)}   {Ui.Count(f.Ships.Count, "ship", "ships")} · {(IsElusive(f) ? "Elusive" : "Defensive")}", (Action)(() => OpenFleetFromMap(f.Id))))
            .ToArray();
        if (items.Length == 0) return;
        _groupMenu = items;
        var pm = new PopupMenu { Theme = Ui.WindowTheme };   // the windows' look, as the window menus
        for (int i = 0; i < items.Length; i++) pm.AddItem(items[i].Item1, i);
        pm.IdPressed += id => items[(int)id].Item2();
        pm.PopupHide += () => { if (_groupPopup == pm) _groupPopup = null; pm.QueueFree(); };
        AddChild(pm);
        _groupPopup = pm;
        pm.Popup(new Rect2I((Vector2I)(_view.GetGlobalTransform() * at + new Vector2(10, 6)), Vector2I.Zero));
    }

    /// <summary>A fleet of yours opened from the map (a row of a group's list, or a double-click on its marker, Paul 2026-11-23): it is
    /// selected on the map and its Fleet window opens; the window on the right stays as it was.</summary>
    void OpenFleetFromMap(int id)
    {
        if (!S.Game.State.Fleets.TryGetValue(id, out var f) || f.Side != Me) return;
        _view.SelectedFleet = id;
        OnViewChanged();
        OpenMgmt(MgKind.Fleet, fleet: id);
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
        // the tab counts, as the design: ships, and fighters and troops out of their room ("8/16"). The ship count takes the colour of its
        // most damaged ship (amber under half its hull, red under a quarter), as the ship rows do, so damage shows on any tab (UI review 6)
        float worst = WorstHull(f);
        Color? shipsInk = HullInk(worst);
        FleetStrip(w,
            ("Command", null, null, () => CommandTab(f, riders)),
            ("Ships", f.Ships.Count.ToString(), shipsInk, () => ShipsTab(f, still, picked)),
            ("Fighters", RoomCount(fighters, Fm.FreeBerths(f) + aboardF.Count), null, () => FightersAboardTab(f, aboardF)),
            ("Troops", RoomCount(regiments, Fm.FreeTroopSlots(f) + regiments), null, () => TroopsAboardTab(f, aboardR.Select(a => a.Regiment).ToList())));
        // the tinted count is never colour alone (C6, UI review 7): its cell says the damage in words, as the side panel's Ships line
        if (shipsInk != null && w.Pages.TryGetValue("Ships", out var shipsTab))
            shipsTab.Tab.TooltipText = Ui.Wrap(HullWords(HullState(f)).Trim() + " Amber: a ship under half its hull. Red: under a quarter.");
    }

    /// <summary>A hull's colour from what is left of it: red under a quarter, amber under half, else none (the caller's own ink).</summary>
    internal static Color? HullInk(float share) => share < 0.25f ? Ui.Danger : share < 0.5f ? Ui.Warn : null;

    /// <summary>"8/16" (what it carries out of its room); just the number with no room; nothing with neither.</summary>
    static string RoomCount(int have, int room) => room <= 0 ? (have == 0 ? null : have.ToString()) : $"{have}/{room}";

    // ================================================================== the head

    /// <summary>
    /// The head, on the darker surface with 24 px all round: the name in BitPotion 48 px with ⋮⋮ – × in the design's grey, then the status
    /// line and its bar. The ⋮⋮ moves the window, as on every other window. A right-click anywhere on the head is a shortcut to Merge (also
    /// in Command) and to scrapping the fleet (also on Ships).
    /// </summary>
    void FleetHead(MgWin w, Fleet f, bool still)
    {
        var head = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };   // a left drag still moves the window (the head under it)
        head.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        head.SetMeta(UiTest.FullName, $"Fleet head {f.Id}");
        RightClickMenu(head, () => HeadMenu(f).ToArray());
        var pad = Pad(FleetHeadPad, FleetHeadPad, FleetHeadPad, FleetHeadPad);
        head.AddChild(pad);
        var parts = Boxed(new VBoxContainer(), 24);
        pad.AddChild(parts);

        // ⋮⋮ moves the window, as on every other window (it no longer drags to merge: Merge is a button in Command, 2026-11-22)
        var grip = MoveGrip();
        grip.AddThemeColorOverride("font_color", Look.Text);   // the design's grey (its dots stay the mono ⋮⋮: Space Grotesk has no such glyph)

        var row = Boxed(new HBoxContainer(), NameGap);
        string upper = S.NameOf(f).ToUpperInvariant();
        float room = WidthOf(w) - 2 * EdgeW - 2 * FleetHeadPad - NameGap - 3 * WinButton - 2 * 4;
        int size = Ui.NameSize(upper, room);
        if (Renaming(w)) row.AddChild(RenameBox(f, size));   // its name is being typed (GameScreen.FleetRename)
        else
        {
            var font = Ui.NameFont(size, upper);
            var name = Look.Words(upper, size, Look.Text);
            name.AddThemeFontOverride("font", font);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            name.ClipText = true;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.SetMeta(UiTest.FullName, $"Fleet name {f.Id}");
            float wordsW = font.GetStringSize(upper, HorizontalAlignment.Left, -1, size).X;
            RenameOnDoubleClick(name, w, f, wordsW, cut: wordsW > room);
            row.AddChild(name);
        }
        row.AddChild(WinButtons(w, grip, grey: true));
        parts.AddChild(row);
        if (!w.Min) parts.AddChild(FleetStatus(f, still, WidthOf(w) - 2 * EdgeW - 2 * FleetHeadPad));
        w.Head.AddChild(head);
    }

    /// <summary>
    /// "Status:" on the left and what the fleet is doing on the right, then at most one countdown ("Moving to Tiryal / ETA: 29 days",
    /// "Repairing at Charter / 8 days left"). The 2 px bar shows only under a countdown and measures it (Paul 2026-11-22: "In orbit shouldn't
    /// have a progress bar... Damaged, should display a repair progress bar"); with none its 2 px stay empty so the tabs never jump. Then each
    /// order it has, with Cancel. Its strength, slowest ship and anything else that holds are in the hover. Design:
    /// praedra/specs/ui/fleet-status-2026-11-22/status-design.md (UI review 6).
    /// </summary>
    /// <summary>The fleet head's padding all round (the design's 24 px), the window's edge (1 px each side) and the gap between the status words.</summary>
    const int FleetHeadPad = 24, EdgeW = 1, StatusGap = 8;

    Control FleetStatus(Fleet f, bool still, float lineW)
    {
        var st = StatusOf(f, still);
        var v = Boxed(new VBoxContainer(), 4);
        v.MouseFilter = MouseFilterEnum.Pass;
        v.TooltipText = Ui.Wrap(st.Tip);
        v.SetMeta(UiTest.FullName, $"Fleet status {f.Id}");
        var line = Boxed(new HBoxContainer(), StatusGap);
        line.AddChild(Look.Words("Status:", 12, Look.Text, true));
        // what and where, then the countdown on the same line when both fit ("Repairing at Charter / 12 days left"); when they don't
        // ("Blockading Brushfield / Repair: 240 days"), the countdown drops to a line of its own, so the place is never cut for it
        float Wide(string t, bool medium = false) => (medium ? Ui.GroteskMedium : Ui.Grotesk).GetStringSize(t, HorizontalAlignment.Left, -1, 12).X;
        float need = Wide("Status:", true) + st.Words.Select((t, i) => StatusGap + Wide(t) + (i > 0 ? Wide("/") + StatusGap : 0)).Sum();
        bool split = st.Words.Count > 1 && need > lineW;
        Label Part(int i)
        {
            var l = Look.Words(st.Words[i], 12, i == 0 && st.Note ? Ui.Warn : Look.Quiet);
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            l.HorizontalAlignment = HorizontalAlignment.Right;
            if (i == 0)
            {
                // the first part takes the room left, against the right edge, and is cut with "…" rather than widen the window
                l.ClipText = true;
                l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            }
            else l.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            return l;
        }
        line.AddChild(Part(0));
        for (int i = 1; i < st.Words.Count && !split; i++)
        {
            line.AddChild(Look.Words("/", 12, Look.Quiet));
            line.AddChild(Part(i));
        }
        v.AddChild(line);
        for (int i = 1; i < st.Words.Count && split; i++) v.AddChild(Part(i));
        var bar = new ThinBar { Share = st.Share ?? 0, Fill = st.Ink, Track = st.Share == null ? Colors.Transparent : Look.Divider, CustomMinimumSize = new Vector2(0, 2) };
        bar.SetMeta(UiTest.FullName, $"Fleet status bar {f.Id}");
        bar.SetMeta("shown", st.Share != null);
        v.AddChild(bar);
        var orders = S.PendingOrdersOf(f.Id, incoming: true).ToList();
        if (orders.Count > 0)
        {
            var ov = Boxed(new VBoxContainer(), 4);
            foreach (var (seq, pend) in orders) OrderLine(seq, $"Ordered: {pend}", 12, ov);
            v.AddChild(Ui.Space(4));
            v.AddChild(ov);
        }
        return v;
    }

    /// <summary>A fleet's status: the words (what and where, then one countdown), the bar's share (null = no bar) and colour, whether the
    /// first words are today's amber note, and the hover.</summary>
    record struct FleetState(List<string> Words, float? Share, Color Ink, bool Note, string Tip);

    /// <summary>
    /// What the fleet is doing. The first part is the first that holds: moving, merging, leaving, today's note (retreated, held, stopped,
    /// could not join), blockading, repairing, in orbit. The countdown is the trip, else the repair, else the harvest wait. Everything else
    /// that holds goes in the hover. Design: specs/ui/fleet-status-2026-11-22/status-design.md §2.
    /// </summary>
    FleetState StatusOf(Fleet f, bool still)
    {
        string slow = SlowestShip(f);
        var rep = RepairOf(f);
        int hurt = DamagedShips(f);
        string more = $"\nStrength {Fm.StrengthOf(f):0}: how hard it hits, its damage rate against three Mandates with its damage counted (three Decrees score about 65)."
            + (slow != null ? $"\nSlowest ship: {slow}, which sets the fleet's speed." : "");
        if (f.Transit is { } t)
        {
            string to = t.Path[^1];
            int total = Math.Max(1, t.HopTicks.Sum()), left = DaysLeft(t);
            string go = f.JoinFleet is int j && j > 0 && S.Game.State.Fleets.TryGetValue(j, out var jf) && jf.Side == Me ? $"Moving to join {S.NameOf(jf)}" : $"Moving to {to}";
            // rule: m4-retreat-damage-repair §5: no repair while moving
            string hurtWords = hurt > 0 ? $" Its {Ui.Count(hurt, "damaged ship", "damaged ships")} repair only once it stops." : "";
            return new(new List<string> { go, left <= 1 ? "ETA: tomorrow" : $"ETA: {left} days" }, 1f - left / (float)total, Ui.You, false,
                $"{S.NameOf(f)} is flying to {to}: it arrives on day {GameSession.ArrivalDay(t)}. The bar shows how far along it is." + hurtWords + more);
        }
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto))
            return new(new List<string> { char.ToUpperInvariant(mto.Words[0]) + mto.Words[1..] }, null, Ui.You, false, $"{S.NameOf(f)} {mto.Words} when the clock moves." + more);
        // every ship moved into other fleets here: it acts as a merge (Paul 2026-11-24). rule: m3-fleets §Fleet invariant 1
        if (AllShipsLeave(f))
            return new(new List<string> { $"Merges into {LeavesInto(f)}" }, null, Ui.You, false,
                $"All of {S.NameOf(f)}'s ships join {LeavesInto(f)} when the clock moves, so {S.NameOf(f)} is then gone, as after a merge; its leaders join the fleet its last ship joins. Cancel a ship's move to keep it." + more);
        if (S.PlannedMoveOf(f.Id) is { } pm)
        {
            int days = Math.Max(1, pm.ArrivalDay - S.Game.Day);
            string stops = rep is { } r0 && Math.Max(r0.Days, r0.FighterDays) > 0 ? $" Leaving stops its repairs ({Ui.Count(Math.Max(r0.Days, r0.FighterDays), "day", "days")} from whole here)." : "";
            return new(new List<string> { $"Leaves for {pm.To}", $"ETA: {Ui.Count(days, "day", "days")}" }, null, Ui.You, false,
                $"{S.NameOf(f)} leaves for {pm.To} when the clock moves and arrives on day {pm.ArrivalDay}." + stops + more);
        }

        // at a system: the first words, then the countdown (the repair, else the harvest wait)
        string first, tip;
        bool note = false;
        if (TodayNote(f) is { } tn) { first = tn.Words; tip = tn.Tip; note = true; }
        else if (Blockading(f)) { first = $"Blockading {f.At}"; tip = $"{S.NameOf(f)} blockades {f.At}: the enemy's mines, refineries and yards there are stopped while it stays."; }
        else if (rep != null) { first = $"Repairing at {f.At}"; tip = ""; }
        else { first = $"In orbit at {f.At}"; tip = $"{S.NameOf(f)} is in orbit at {f.At}."; }
        var words = new List<string> { first };
        float? share = null;
        var ink = Ui.You;
        if (rep is { } r)
        {
            string where = r.Fast ? $"{S.NameOf(f)} is repaired at your shipyard at {f.At}, every {Ui.Count(r.Period, "day", "days")}"
                : $"No working shipyard of yours at {f.At}, so a repair comes only every {r.Period} days";
            string Countdown(int d) => first.StartsWith("Repairing", StringComparison.Ordinal) ? $"{Ui.Count(d, "day", "days")} left" : $"Repair: {Ui.Count(d, "day", "days")}";
            string craft = r.FighterDays > 0 ? $" Its fighters get one craft back per repair: {Ui.Count(r.CraftLost, "craft", "craft")} short, all back on day {S.Game.Day + r.FighterDays}." : "";
            // the design's order (UI review 7): the hull's days with the most damaged ship's bar; else the broken parts, no bar; else the
            // fighters' days, no bar (the bar is a ship's hull, never the fighters)
            if (r.Days > 0)
            {
                // the bar is the most damaged ship's hull: the one the days count for, coloured as the Ships tab and the side panel (UI review 6)
                words.Add(Countdown(r.Days));
                share = r.Worst;
                ink = HullInk(r.Worst) ?? Ui.You;
                tip += (tip == "" ? "" : " ") + $"{where}: its hull is whole on day {S.Game.Day + r.Days}. The bar is its most damaged ship's hull." + (r.Fast ? " Leaving stops the repairs." : "")
                    + (r.Broken > 0 ? $" {Ui.Count(r.Broken, "broken part", "broken parts")}: each repair may fix one, by chance." : "") + craft;
            }
            else if (r.Broken > 0)
            {
                // hull whole, only broken parts: each repair fixes one only on a dice roll (rule: m4-retreat-damage-repair §5), so no day
                words.Add(Ui.Count(r.Broken, "part broken", "parts broken"));
                tip += (tip == "" ? "" : " ") + $"The hull is whole. {where}, and each may fix one broken part, by chance." + craft;
            }
            else
            {
                words.Add(Countdown(r.FighterDays));
                tip += (tip == "" ? "" : " ") + $"Every ship is whole. {where}." + craft;
            }
        }
        else if (S.HarvesterRechargingUntil(f) is int ready && ready > S.Game.Day)
        {
            int cd = Math.Max(1, S.Host.Harvester.Params.CooldownDays);
            words.Add($"Harvest in {Ui.Count(ready - S.Game.Day, "day", "days")}");
            share = 1f - (ready - S.Game.Day) / (float)cd;
            tip += " " + S.HarvestWaitText(ready);
        }
        return new(words, share, ink, note, tip + more);
    }

    /// <summary>The lowest hull share among the fleet's ships (1 when all are whole or it has none).</summary>
    float WorstHull(Fleet f) => f.Ships.Select(S.Host.Economy.Find)
        .Select(o => o != null && Fm.ClassOf(o) is { } c && c.Hull > 0 ? o.Hull / (float)c.Hull : 1f).DefaultIfEmpty(1f).Min();

    /// <summary>Ships of the fleet that have lost hull or have a broken part.</summary>
    int DamagedShips(Fleet f) => f.Ships.Select(S.Host.Economy.Find).Count(o => o != null && Fm.ClassOf(o) is { } c && (o.Hull < c.Hull || (o.Hits?.Any(h => h > 0) ?? false)));

    /// <summary>
    /// The fleet's repair, read from the core: none while moving; a ship mends a tenth of its class hull per repair, every 2 days at a
    /// working shipyard of yours and every 30 days anywhere else; fighters get one craft back per repair; broken parts come back only by
    /// chance. Days = until its slowest ship's hull is whole (0 when only parts are broken); FighterDays = until every craft is back;
    /// Worst = the lowest hull share of its ships. rule: m4-retreat-damage-repair §5 (RepairModule.DaysToRepair, FastRepair, Period).
    /// </summary>
    (int Days, float Worst, int Broken, int FighterDays, int CraftLost, bool Fast, int Period)? RepairOf(Fleet f)
    {
        if (!Still(f)) return null;
        var rp = S.Host.Repair;
        var eco = S.Host.Economy;
        bool fast = rp.FastRepair(f.At, f.Side);
        if (rp.Rules.RepairNeedsYard && !fast) return null;
        int days = 0, broken = 0, fdays = 0, lost = 0;
        float worst = 1f;
        bool any = false;
        // the days come from the core's own repair query (Ponytail on #358: the screen never copies the timer)
        foreach (var o in f.Ships.Select(eco.Find))
        {
            if (o == null || Fm.ClassOf(o) is not { Hull: > 0 } c) continue;
            int hits = o.Hits?.Sum() ?? 0;
            broken += hits;
            if (hits > 0) any = true;
            if (o.Hull >= c.Hull) continue;
            any = true;
            days = Math.Max(days, rp.DaysToRepair(o, f.At) ?? 0);
            worst = Math.Min(worst, o.Hull / (float)c.Hull);
        }
        foreach (var q in FightersAboard(f).Concat(f.Squadrons.Select(eco.Find)))
        {
            if (q == null || q.CraftLost <= 0) continue;
            any = true;
            lost += q.CraftLost;
            fdays = Math.Max(fdays, rp.DaysToRepair(q, f.At) ?? 0);
        }
        return any ? (days, worst, broken, fdays, lost, fast, rp.Period(true, fast)) : null;
    }

    /// <summary>Your fleet stops the enemy's economy where it stands. rule: m1-forced-stop-and-blockade §Update 2026-10-12 (economic blockade).</summary>
    bool Blockading(Fleet f) => Still(f) && S.OwnerOf(f.At) is Side owner && owner != Me && S.Host.Fleets.IsEconomicBlockade(f.At, owner);

    /// <summary>
    /// What happened to the fleet on the last day, in amber words, until the clock moves on: it retreated, Hold beams kept it in a battle,
    /// an enemy stopped it, or the fleet it was to join had gone. The news keeps the story after that.
    /// </summary>
    (string Words, string Tip)? TodayNote(Fleet f)
    {
        var events = S.Fog.EventsFor(Me);
        int day = S.Game.Day - 1;
        (int Rank, string Words, string Tip)? best = null;
        void Take(int rank, string words, string tip) { if (best == null || rank < best.Value.Rank) best = (rank, words, tip); }
        for (int k = events.Count - 1; k >= 0 && events[k].Day >= day; k--)
            switch (events[k])
            {
                case FleetRetreated r when r.Fleet == f.Id:
                    Take(0, $"Retreated to {r.To}", $"{S.NameOf(f)} left the battle at {r.From} today and jumped to {r.To}. Its order is cancelled."); break;
                case FleetCreated c when c.Fleet == f.Id && c.Cause == FleetCreateCause.HoldBeamHeld:
                    Take(1, $"Held at {c.System}", $"Enemy Hold beams kept these ships in the battle at {c.System} when the rest of their fleet jumped away. They are now {S.NameOf(f)}."); break;
                case ForcedStop x when x.Fleet == f.Id:
                    Take(2, $"Stopped at {x.System}", $"An enemy fleet stopped {S.NameOf(f)} at {x.System} today. Its trip is cancelled: give it a new order."); break;
                case FleetJoinFailed j when j.Fleet == f.Id:
                    string into = S.Game.State.Fleets.TryGetValue(j.Into, out var jf) ? S.NameOf(jf) : "its fleet";
                    Take(3, $"Could not join {into}", $"{S.NameOf(f)} reached {j.System}, but {into} was not there, so they did not merge."); break;
            }
        return best is { } b ? (b.Words, b.Tip) : null;
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
    /// then scrap the whole fleet. A shortcut: the same is on the Merge and Scrap fleet buttons.</summary>
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
    /// A strip cell's name: light on the open cell and under the mouse, the muted grey on the others (R16, Paul 18:57: "the open tab is
    /// barely lighter than the rest"). The new colors frames draw every name light, but their cells differ by 1.06:1 (#131313 on #0B0B0D),
    /// so the name is what tells the open tab (review 23; Paul's A/B pending). Its count keeps its own colour. Returns the repaint, for a
    /// cell pressed with no signal.
    /// </summary>
    static Action CellWords(Button cell, Label words)
    {
        bool over = false;
        void Paint() => words.AddThemeColorOverride("font_color", cell.ButtonPressed || over ? Look.Text : Look.Muted);
        cell.Toggled += _ => Paint();
        cell.MouseEntered += () => { over = true; Paint(); };
        cell.MouseExited += () => { over = false; Paint(); };
        Paint();
        return Paint;   // for a cell pressed without its signal
    }

    /// <summary>
    /// The tabs as the design's strip: cells 1 px apart on the line colour, the open one on the lighter surface so it joins the page under
    /// it; the name, and its count in the dim grey. Every page is built (so every drop place exists), only the open one shows. While a unit
    /// is dragged over a cell, it opens, so a drop can reach a page that was hidden.
    /// </summary>
    void FleetStrip(MgWin w, params (string Name, string Count, Color? Ink, Action Build)[] tabs)
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
        foreach (var (name, count, ink, build) in tabs)
        {
            string tabName = name;
            var b = new Button { ToggleMode = true, ButtonGroup = group, ButtonPressed = name == w.Tab, FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand };
            b.AddThemeStyleboxOverride("normal", Look.Box(Look.Bg));
            b.AddThemeStyleboxOverride("hover", Look.Box(Look.CellHover));
            b.AddThemeStyleboxOverride("pressed", Look.Box(Look.Band));
            b.AddThemeStyleboxOverride("hover_pressed", Look.Box(Look.Band));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.SetMeta(UiTest.FullName, $"Tab {w.Key} {name}");
            var row = Boxed(new HBoxContainer(), 8);
            var l = Look.Words(name, 12, Look.Text);
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(l);
            CellWords(b, l);
            if (count != null)
            {
                var c = Look.Words(count, 12, ink ?? Look.Quiet);   // the status grey (Paul 2026-11-25: #7D8699)
                c.SetMeta(UiTest.FullName, $"Tab {w.Key} {name} count");
                row.AddChild(c);
            }
            // as the design: Command and Ships fit their words (Ships at least 72 px), the others share the rest (Fighters, Troops, Fleets)
            Fill(b, row, 8, 8, name == "Ships" ? new Vector2(72, 0) : null);
            b.SizeFlagsHorizontal = name is "Fighters" or "Troops" or "Fleets" ? SizeFlags.ExpandFill : SizeFlags.Fill;
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
        var posts = Boxed(new HBoxContainer(), CardGapX);
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
            if (Cm.HolderOf(f.Id, post) is { } holder) z.Flow.AddChild(PostLeader(holder, f));
            else z.Flow.AddChild(EmptyCard(DropLeader));
            DragOnly(z.Box, f, $"to make them {GameSession.PostWords(post)}");
            col.AddChild(z.Box);
            posts.AddChild(col);
        }
        cmd.AddChild(posts);

        var pas = FleetSection("Passengers", 16, null, "Leaders who ride the fleet with no post: they travel with it.");
        var pz = DropZone($"Fleet {f.Id} Passengers", "take aboard", d => RefuseFleetPost(d, f, CharacterPost.None), d => DropFleetPost(d, f, CharacterPost.None));
        CardFlow(pz.Flow);
        foreach (var c in riders) pz.Flow.AddChild(PostLeader(c, f));
        if (riders.Count == 0) pz.Flow.AddChild(EmptyCard(DropLeader));
        DragOnly(pz.Box, f, "to take them aboard");
        pas.AddChild(pz.Box);

        // leaders and fleets sent to join it from other systems (m5 §Moving a character alone, m3 §Joining a fleet on arrival)
        var ls = Cm.All.Where(c => c.Side == Me && c.JoinFleet == f.Id && c.JoinByOrder && c.Transit != null).OrderBy(c => c.Id).ToList();
        var fs = S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.JoinFleet == f.Id && x.Transit != null).OrderBy(x => x.Id).ToList();
        if (ls.Count + fs.Count > 0)
        {
            var way = FleetSection("On the way", 16, (ls.Count + fs.Count).ToString(), $"Sent to join {S.NameOf(f)} from other systems. They take their place when they arrive.");
            var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
            bool hand = Me == Side.Hand;
            foreach (var c in ls)
            {
                string name = S.CharName(c.Id);
                flow.AddChild(FleetCard(LeaderPic(c.Id, Ui.You), GameSession.CardName(name), Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), ghost: true));
            }
            foreach (var x in fs) flow.AddChild(FleetCard(UnitPic(UnitIcon.Kind.Ship, hand, 2, Ui.You), S.NameOf(x), Arrives(x.Transit, $"{Ui.Count(x.Ships.Count, "ship", "ships")}, to merge"), ghost: true));
            way.AddChild(flow);
        }
        // where the leaders to drag stand, and where they land (UI review 4), as the design's Ground button of the other tabs
        if (GroundButton(f, "Command") is { } ground) Add(ground);
    }

    /// <summary>
    /// The fleet's orders, as the design: Bombardment ▾ (military, civilian or everything), Invasion, Move; and the harvester's strike when
    /// it is in the fleet. A button that can't act is greyed and its hover says why. The core judges each order when the clock moves.
    /// rule: m3-fleets §Orders, m4-bombardment, m4-invasion, m5-harvester §2.
    /// </summary>
    Control FleetActions(Fleet f)
    {
        // Paul's row (2026-11-22, Q2 B): Bombardment ▾, Invasion, Move; Merge alone under it (and the harvester's strike beside it)
        var bar = Boxed(new VBoxContainer(), 8);
        var hit = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        var go = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (var row in new[] { hit, go }) { row.AddThemeConstantOverride("h_separation", 8); row.AddThemeConstantOverride("v_separation", 8); bar.AddChild(row); }
        bool still = Still(f);
        string travelling = $"Travelling: give this order when it reaches {f.Transit?.Path[^1] ?? "its destination"}.";
        // a fleet that merges away today takes no order of its own: the core would refuse it once the fleet is gone (P2, review 18)
        string gone = MergesAwayWhy(f);
        bool canHit = gone == null && CanAttackHere(f);
        int regs = S.Host.Troops.RegimentsAboard(f).Count;
        string hitWhy = gone ?? (canHit ? null : AttackWhyNot(f));

        hit.AddChild(MenuAction("Bombardment", "Fleet Bombard", !canHit ? Array.Empty<(string, Action)>() : new (string, Action)[]
        {
            ("Military targets: regiments and defences", () => { Bombard(f, BombardCategory.Military); MgRefreshSoon(); }),
            ("Civilian targets: the planet's support", () => { Bombard(f, BombardCategory.Civilian); MgRefreshSoon(); }),
            ("Everything", () => { Bombard(f, BombardCategory.General); MgRefreshSoon(); }),
        }, hitWhy ?? "Bombard the system from orbit: its regiments and defences, its support, or everything."));
        hit.AddChild(FleetAction("Invasion", "Fleet Invade", () => { Invade(f); MgRefreshSoon(); },
            hitWhy ?? (regs > 0 ? $"Lands the {Ui.Count(regs, "regiment", "regiments")} aboard to take the planet: land more than the defenders have." : "No regiments aboard: load some at one of your planets first (Troops tab)."),
            !canHit || regs == 0));
        int fid = f.Id;
        hit.AddChild(FleetAction("Move", "Fleet Move", () => { if (S.Game.State.Fleets.TryGetValue(fid, out var ff)) { CloseSectorView(); StartMove(ff); } },
            gone ?? (still ? "Pick a destination on the map. To send several fleets at once, Shift+click them on the map and right-click a system." : travelling), !still || gone != null));
        // Merge, as Bombardment: a short list, the fleets here first (they join at once), then the others closest first (they fly here)
        var merge = still && gone == null ? MergeLines(f) : new List<(string Text, Action Do)>();
        go.AddChild(MenuAction("Merge", "Fleet Merge", merge.Select(l => (l.Text, (Action)(() => { l.Do(); MgRefreshSoon(); }))).ToArray(),
            gone != null ? gone
            : !still ? travelling
            : merge.Count == 0 ? "No other fleet of yours can join this one now: none can reach it, or each already has an order today."
            : $"Another fleet joins {S.NameOf(f)}: a fleet here joins at once, one elsewhere flies here and joins when it arrives."));

        // the harvester's strike
        if (still && S.HoldsHarvester(f))
        {
            bool struck = S.PendingOf(f.Id).Any(p => p.StartsWith("Destroy System", StringComparison.Ordinal));
            int? readyDay = S.HarvesterRechargingUntil(f);
            go.AddChild(FleetAction("Destroy System...", "Fleet Destroy", () => AskDestroySystem(f.Id),
                gone ?? (S.IsDestroyed(f.At) ? $"{f.At} is already destroyed." + (readyDay is int rd0 ? " " + S.HarvestWaitText(rd0) : "") : struck ? "The strike is ordered: it happens as soon as the clock moves." : readyDay is int rd ? S.HarvestWaitText(rd) : "Wipes out the system the harvester is at. Asks first."),
                gone != null || S.IsDestroyed(f.At) || struck || readyDay != null, Ui.Danger));
        }
        return bar;
    }

    /// <summary>This fleet's choice when an enemy fleet arrives, Defensive (fight) or Elusive (pull back), as the frames' switch (2026-11-24):
    /// one outline, 4 px corners, the chosen half filled #36363C. rule: m4-battle-classic §1.3 (per fleet; in a battle with both, the Elusive
    /// fleets leave in their first step and the rest fight).</summary>
    Control BehaviorRow(Fleet f)
    {
        bool Elusive() => IsElusive(f);
        bool retreat = Elusive();
        var sw = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, CustomMinimumSize = new Vector2(0, ButtonH) };
        var frame = ButtonBox(Look.Outline);
        frame.ContentMarginLeft = frame.ContentMarginRight = frame.ContentMarginTop = frame.ContentMarginBottom = 2;
        sw.AddThemeStyleboxOverride("panel", frame);
        var row = Boxed(new HBoxContainer(), 2);
        sw.AddChild(row);
        foreach (var (word, on, tip) in new[]
        {
            ("Defensive", false, "This fleet stays and fights an enemy fleet that arrives."),
            ("Elusive", true, "This fleet pulls back when an enemy fleet arrives, instead of fighting. Your Defensive fleets there stay and fight without it. An enemy ship that blocks retreat can keep it in the fight."),
        })
        {
            bool r = on, chosen = retreat == on;
            var b = new Button { FocusMode = FocusModeEnum.None, TooltipText = Ui.Wrap(tip + (chosen ? " Chosen." : "")),
                MouseDefaultCursorShape = chosen ? CursorShape.Arrow : CursorShape.PointingHand, SizeFlagsVertical = SizeFlags.ExpandFill };
            var clear = new Color(0, 0, 0, 0);
            StyleBoxFlat Seg(Color fill) { var x = Look.Box(fill); x.SetCornerRadiusAll(SegmentRadius); x.AntiAliasing = true; return x; }
            var rest = chosen ? Look.Outline : clear;
            b.AddThemeStyleboxOverride("normal", Seg(rest));
            b.AddThemeStyleboxOverride("hover", Seg(chosen ? Look.Outline : Look.Line));
            b.AddThemeStyleboxOverride("pressed", Seg(Look.Outline));
            b.AddThemeStyleboxOverride("hover_pressed", Seg(Look.Outline));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            var l = Look.Words(word, 12, Look.Text, true);
            l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            Fill(b, l, 8, 7);
            b.Pressed += () => { if (r != Elusive()) { SetRetreat(f, r); MgRefreshSoon(); } };
            b.SetMeta(UiTest.FullName, $"Behavior {word}");
            b.SetMeta("chosen", chosen);
            row.AddChild(b);
        }
        return sw;
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
        // each line says who moves and when (UI review 1, 2026-11-22): the other fleet comes to this one, this one stays
        if (here.Count > 1) lines.Add(($"All {here.Count} fleets here join now", () => MergeInto(f, here)));
        foreach (var x in here) lines.Add(($"{S.NameOf(x)} joins now (here)", () => MergeInto(f, new List<Fleet> { x })));
        foreach (var (x, plan) in far)
            lines.Add(($"{S.NameOf(x)} flies from {x.At}, joins in {Ui.Count(plan.HopTicks.Sum(), "day", "days")}" + (plan.Red ? " (known trap on the way)" : ""), () => MergeInto(f, new List<Fleet> { x })));
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
            if (_moved.TryGetValue(d.Key, out var already)) { Toast($"{S.NameOf(from)} already {already.Words}.", MsgKind.Warn); continue; }
            string why = RefuseShipsInto(d, into);
            if (why == "") continue;
            if (why != null) { Toast($"{S.NameOf(from)}: {why}", MsgKind.Warn); continue; }
            DropShipsInto(d, into);
            sent++;
        }
        if (sent > 0) RefreshMgmt();
    }

    // ================================================================== buttons and cards in the design's look

    /// <summary>The design's button: a 1 px #36363C outline, 8 px inside, 12 px words. Brighter outline under the mouse, the words greyed
    /// with the reason in the hover when it can't act; <paramref name="ink"/> for a meaning colour (the harvester's red).</summary>
    static Button FleetAction(string words, string key, Action act, string tip, bool disabled = false, Color? ink = null, bool menu = false, Color? hoverInk = null, string[] icon = null)
    {
        var b = new Button { FocusMode = FocusModeEnum.None, Disabled = disabled, TooltipText = Ui.Wrap(tip),
            MouseDefaultCursorShape = disabled ? CursorShape.Arrow : CursorShape.PointingHand };
        foreach (var (state, line) in new[] { ("normal", Look.Outline), ("hover", Look.Soft), ("pressed", Look.Text), ("hover_pressed", Look.Text), ("disabled", Look.Line) })
            b.AddThemeStyleboxOverride(state, ButtonBox(line));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var c = disabled ? Look.Dim : ink ?? Look.Text;
        var h = Boxed(new HBoxContainer(), 8);
        if (icon != null)
        {
            // the design's 16 px glyph before the words, flat in the words' grey (the Ground button's turret)
            var pic = IconPic.Of(icon, 1, c, flat: true);
            pic.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(pic);
        }
        var text = Look.Words(words, 12, c, true);   // the frames' buttons say it in Medium
        text.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(text);
        if (menu) { var arrow = Look.Words("▾", 12, c, true); arrow.SizeFlagsVertical = SizeFlags.ShrinkCenter; h.AddChild(arrow); }
        if (hoverInk is Color hi && !disabled)
        {
            // a button whose harm is only shown at the moment you commit (Scrap: red under the mouse, normal ink at rest; UI review 1)
            b.MouseEntered += () => text.AddThemeColorOverride("font_color", hi);
            b.MouseExited += () => text.AddThemeColorOverride("font_color", c);
        }
        Fill(b, h, 8, 8, new Vector2(0, ButtonH));
        b.Pressed += act;
        b.SetMeta(UiTest.FullName, key);
        return b;
    }

    /// <summary>A button's outline: 1 px, 4 px corners (the new colors frames; Paul 2026-11-24, "follow the frames").</summary>
    static StyleBoxFlat ButtonBox(Color line, Color? fill = null, int radius = ButtonRadius)
    {
        var b = Look.Box(fill ?? new Color(0, 0, 0, 0), line, 1, 1, 1, 1);
        b.SetCornerRadiusAll(radius);
        b.AntiAliasing = true;   // a rounded corner needs it; the straight edges stay on whole pixels
        return b;
    }

    /// <summary>The frames' button: 40 px high, 4 px corners; inside a switch, 2 px corners.</summary>
    const int ButtonH = 40, ButtonRadius = 4, SegmentRadius = 2;

    /// <summary>An order button that opens a short list of choices under it (the bombard targets).</summary>
    Button MenuAction(string words, string key, (string Text, Action Do)[] lines, string tip)
    {
        Button b = null;
        b = FleetAction(words, key, () => PopMenu(b, lines), tip, lines.Length == 0, menu: true);
        _menus.AddOrUpdate(b, () => lines);   // for the playtest: RunMenu
        return b;
    }

    /// <summary>A short list under <paramref name="at"/>. With no line, <paramref name="none"/> shows greyed, so the click still answers.</summary>
    void PopMenu(Control at, (string Text, Action Do)[] items, string none = null)
    {
        if (items.Length == 0 && none == null) return;
        var pm = new PopupMenu { Theme = ThemeAt(at) };   // the window's look
        if (items.Length == 0) { pm.AddItem(none, 0); pm.SetItemDisabled(0, true); }
        for (int i = 0; i < items.Length; i++) pm.AddItem(items[i].Text, i);
        pm.IdPressed += id => items[(int)id].Do();
        pm.PopupHide += () => pm.QueueFree();
        AddChild(pm);
        var r = at.GetGlobalRect();
        pm.Popup(new Rect2I((Vector2I)(r.Position + new Vector2(0, r.Size.Y + 2)), Vector2I.Zero));
    }

    /// <summary>A card's frame: none at rest, as the design (a card is its picture tile and its words); picked, a 1 px line in your blue
    /// drawn 3 px outside it, so the card never moves and the line clears the picture.</summary>
    static StyleBoxFlat CardBox(Color line)
    {
        var b = Look.Box(new Color(0, 0, 0, 0), line, 1, 1, 1, 1);
        b.ContentMarginLeft = b.ContentMarginRight = b.ContentMarginTop = b.ContentMarginBottom = 0;
        b.SetExpandMarginAll(3);
        return b;
    }

    /// <summary>
    /// A card from the design (a ship, a post, a passenger, a squadron, a regiment): its picture centred on the darker surface, 96 x 48,
    /// then its name, centred, two lines at most; a short dim line under that when it has something to say.
    /// </summary>
    PanelContainer FleetCard(Control pic, string name, string sub = null, bool ghost = false, int nameLines = 2, params Control[] more)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(CardW, 0) };
        p.SetMeta("card", "card");
        p.AddThemeStyleboxOverride("panel", CardBox(new Color(0, 0, 0, 0)));
        var v = Boxed(new VBoxContainer(), 4);
        p.AddChild(v);
        // the dark tile behind the picture (Paul 2026-11-24, "I need the dark tile behind pictures everywhere for now"); the sprite itself
        // stays transparent
        var tile = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(CardW, CardPicH) };
        tile.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.AddChild(pic);
        tile.AddChild(center);
        v.AddChild(tile);
        v.AddChild(CardLine(KeepNumeral(name), Look.Text, nameLines));
        if (sub != null) v.AddChild(CardLine(sub, ghost ? Pending : Look.Dim, 1));
        foreach (var m in more) v.AddChild(m);
        if (ghost) p.Modulate = new Color(1, 1, 1, 0.62f);
        return p;
    }

    /// <summary>A line of a card: 12 px, centred on the card's width, cut with "…" past <paramref name="lines"/> lines.</summary>
    static Label CardLine(string words, Color ink, int lines)
    {
        var t = Look.Words(words, 12, ink);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.CustomMinimumSize = new Vector2(CardW, 0);
        if (lines > 1) { t.AutowrapMode = TextServer.AutowrapMode.Word; t.MaxLinesVisible = lines; }
        else t.ClipText = true;
        t.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return t;
    }

    /// <summary>An empty place: dashed #36363C, its words centred. A card's size by default (the design's "Drop a leader here", 96 x 84);
    /// the Fighters and Troops tabs pass the page's width (the design's "Drop regiments here to load them", 80 px high). A label, so a drop
    /// shown before the day applies it takes its place.</summary>
    static Label EmptyCard(string words, float width = CardW, float height = LeaderCardH)
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

    /// <summary>Cards in a flow, three a row: <see cref="CardGapX"/> across, 16 px between rows.</summary>
    static HFlowContainer CardFlow(HFlowContainer flow)
    {
        flow.AddThemeConstantOverride("h_separation", CardGapX); flow.AddThemeConstantOverride("v_separation", CardGapY);
        return flow;
    }

    /// <summary>A leader's picture: their role's sprite at 2 px a pixel, on nothing (Paul 2026-11-23: a hero has a character picture,
    /// transparent background). It replaced the initials in a ring.</summary>
    Control LeaderPic(int id, Color ink) =>
        LeaderSprite(id) is { } grid ? IconPic.Of(grid, 2, ink) : new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(32, 32) };

    /// <summary>A unit's pixel icon at 2 px a pixel (icons are code only, Paul 2026-11-18): its class's own sprite when one is drawn
    /// (Paul 2026-11-23, one picture per class), else its kind's.</summary>
    static Control UnitPic(UnitIcon.Kind kind, bool hand, int size4, Color ink, string cls = null) =>
        PixelIcons.Unit(kind, hand, size4, cls) is { } grid ? IconPic.Of(grid, 2, ink) : new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(32, 32) };

    /// <summary>A leader as a card: their picture and name. Drags unless an order already moves them today, or they cannot act; their
    /// skills and the posts they can hold are in the hover.</summary>
    Control LeaderCard(CharacterRecord c)
    {
        string name = S.CharName(c.Id);
        var d = new Drag('L', c.Id);
        if (_moved.ContainsKey(d.Key)) return new Control { Visible = false };   // it already stands where it was dropped
        bool can = c.Status == CharacterStatus.Free && c.Transit == null && !Cm.IsOnMission(c.Id);
        string sub = c.Status != CharacterStatus.Free ? GameSession.StatusWords(c) : Cm.IsOnMission(c.Id) ? "on a mission" : null;
        var card = FleetCard(LeaderPic(c.Id, can ? Ui.You : Look.Soft), GameSession.CardName(name), sub);
        bool mission = CanJoin(c);
        card.TooltipText = Ui.Wrap($"{S.CharTitle(c.Id)}. {S.SkillsOf(c)}\n{S.CapabilityOf(c)}\nCan hold: {LeaderPosts(c)}." + (can ? "\nDrag onto a post or a fleet." : "")
            + (mission ? " Right-click to send them on a mission." : ""));
        card.SetMeta(UiTest.FullName, "Leader " + name);
        if (can) MakeDraggable(card, new List<Drag> { d }, name);
        if (mission) MissionMenu(card, c.System, c.Id, null);
        return card;
    }

    /// <summary>A leader on a post or among the passengers: their card; they leave the fleet by being dragged off it.</summary>
    Control PostLeader(CharacterRecord c, Fleet f)
    {
        var card = LeaderCard(c);
        if (!card.Visible) return card;
        card.TooltipText += LeadersHere(f) is { } at ? $"\nTo land them, drag them onto {at}."
            : Still(f) ? $"\nNothing is left of {f.At} to land on." : "\nThey can land once the fleet stops.";
        return card;
    }

    /// <summary>Where this fleet's leaders come from and land, in the words on screen ("Leaders in Ground at Charter"): the Command tab
    /// of the Ground window of the planet where the fleet stands, yours or not (the core lets a passenger off anywhere a fleet stands still,
    /// rule: m5-characters §Posts, DetachCharacter; QA on #357). Null in flight or at a harvested world: no leader lands there.</summary>
    string LeadersHere(Fleet f) => Still(f) && !S.IsDestroyed(f.At) ? $"Leaders in Ground at {f.At}" : null;

    /// <summary>
    /// A post or Passengers takes a leader only by a drag, never by a click or a menu (Paul 2026-11-22: moving a leader is too important to
    /// be quick or automatic). Its hover says how.
    /// </summary>
    void DragOnly(Control zone, Fleet f, string what) =>
        zone.TooltipText = Ui.Wrap(LeadersHere(f) is { } at ? $"Drag a leader here from {at} {what}. One from another system flies here first."
            : $"Drag a leader here {what}. One from another system flies here first.");

    /// <summary>A squadron or regiment as a card (the Fighters and Troops tabs): its icon, its class, and a short line (a damaged hull,
    /// "flies along").</summary>
    PanelContainer UnitCard(EconObject o, string sub, bool can) =>
        FleetCard(UnitPic(KindOf(o.Category, o.Class), Me == Side.Hand, SizeOfClass(o.Class), can ? Ui.You : Look.Soft, o.Class), o.Class, sub);

    /// <summary>What a drop into one of this window's places shows until the clock moves: the card or row it will be, faded.</summary>
    Control FleetGhost(Drag d, string sub)
    {
        bool hand = Me == Side.Hand;
        switch (d.Kind)
        {
            case 'L': string name = S.CharName(d.Id); return FleetCard(LeaderPic(d.Id, Ui.You), GameSession.CardName(name), sub, ghost: true);
            case 'M': return FleetCard(UnitPic(UnitIcon.Kind.Ship, hand, 2, Ui.You), ThingName(d), sub, ghost: true);
            case 'S' when S.Host.Economy.Find(d.Id) is { } ship: { var r = ShipCard(ship); r.Modulate = new Color(1, 1, 1, 0.62f); return r; }
            default:
                var o = S.Host.Economy.Find(d.Id);
                return FleetCard(UnitPic(o == null ? UnitIcon.Kind.Regiment : KindOf(o.Category, o.Class), hand, SizeOfClass(o?.Class), Ui.You, o?.Class), o?.Class ?? "unit", sub, ghost: true);
        }
    }

    // ================================================================== Ships: one card per ship

    /// <summary>
    /// One card per ship, three a row (Paul's frame 2, 2026-11-23): its picture, its class, what it carries out of its room, its hull. A
    /// click picks it, Shift+click picks several: split them off or scrap them together (the buttons under the cards), or drag them onto
    /// another fleet's window. A right-click splits it off or scraps it. Its damage, in words, is in its hover. While one of its ships is
    /// dragged, a dashed place under the cards takes it as a new fleet.
    /// </summary>
    void ShipsTab(Fleet f, bool still, List<EconObject> picked)
    {
        var eco = S.Host.Economy;
        var sz = DropZone($"Fleet {f.Id} Ships", "join", d => RefuseShipsInto(d, f), d => DropShipsInto(d, f));
        CardFlow(sz.Flow);
        var ships = f.Ships.Select(eco.Find).Where(o => o != null && !_moved.ContainsKey(new Drag('S', o.Id).Key) && !_scrapped.Contains(o.Id)).ToList();
        foreach (var o in ships.GroupBy(o => o.Class).SelectMany(g => g))
        {
            var d = new Drag('S', o.Id);
            var card = ShipCard(o);
            card.SetMeta(UiTest.FullName, d.Key);
            // this ship first, then the same class beside it: a plain drag takes this one, Shift takes them all
            var same = ships.Where(x => x.Class == o.Class && x.Id != o.Id).ToList();
            var drags = new List<Drag> { d };
            drags.AddRange(same.Select(x => new Drag('S', x.Id)));
            MakeDraggable(card, drags, o.Class);
            card.TooltipText = Ui.Wrap(ShipTip(o) + "\nClick to pick it; Shift+click to pick several. Drag to another fleet's window"
                + (same.Count > 0 ? $" (hold Shift as you start the drag to move all {same.Count + 1} {o.Class})" : "") + "."
                + (still ? "\nRight-click: split it off or scrap it." : ""));
            var one = o;
            if (still) RightClickMenu(card, () => ShipMenu(f, one).ToArray());
            sz.Flow.AddChild(card);
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
        ShipActions(f, picked, still);
    }

    /// <summary>A ship as the design's card: its picture, its class, then what it carries out of its room (a fighter and a regiment, each
    /// with "6/6") and its hull out of its class's ("2750/2750", amber under half, red under a quarter), all in the dim grey.</summary>
    PanelContainer ShipCard(EconObject o)
    {
        var sc = Fm.ClassOf(o);
        var cap = Boxed(new HBoxContainer(), 4);
        cap.Alignment = BoxContainer.AlignmentMode.Center;
        cap.CustomMinimumSize = new Vector2(CardW, 16);
        string side = Me == Side.Hand ? "hand" : "collective";
        var ink = Look.Dim;   // the frame's #454545 (Paul 2026-11-25 chose "B", over #7D8699 that he had picked on 2026-11-23, backlog R9)
        void Cargo(string icon, int have, int room)
        {
            if (room <= 0) return;
            if (cap.GetChildCount() > 0) cap.AddChild(new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(6, 0) });
            var i = IconPic.Of(PixelIconData.Units[$"{icon}-{side}"], 1, ink, flat: true);   // the design's flat silhouettes
            i.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cap.AddChild(i);
            cap.AddChild(Look.Words($"{have}/{room}", 12, ink));
        }
        if (sc != null)
        {
            Cargo("fighter", Fm.UnitsAboard(o.Id, ObjectCategory.Fighter).Count(), sc.FighterCapacity);
            Cargo("regiment", Fm.UnitsAboard(o.Id, ObjectCategory.Regiment).Count(), sc.TroopCapacity);
        }
        if (cap.GetChildCount() == 0) cap.AddChild(Look.Words("no cargo", 12, ink));   // every card the same height, so the rows line up
        int max = sc?.Hull ?? o.Hull;
        var hull = CardLine($"{o.Hull}/{max}", HullInk(max <= 0 ? 1 : o.Hull / (float)max) ?? ink, 1);
        // the class on one line (cut with "…"), so every ship card has the same height and the rows line up
        return FleetCard(UnitPic(UnitIcon.Kind.Ship, Me == Side.Hand, SizeOfClass(o.Class), Ui.You, o.Class), o.Class, nameLines: 1, more: new Control[] { cap, hull });
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

    /// <summary>
    /// Under the ship cards, always (where the design puts a tab's buttons): Split and Scrap, so nobody has to find them in a right-click (2026-11-22). With no ship picked, Split
    /// says how to pick and Scrap takes the whole fleet; with ships picked, both act on those, and their words say how many.
    /// rule: m3-fleets §Orders (SplitFleet), m2-maintenance §Scrapping and refunds.
    /// </summary>
    void ShipActions(Fleet f, List<EconObject> picked, bool still)
    {
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        string travelling = $"Travelling: give this order when it reaches {f.Transit?.Path[^1] ?? "its destination"}.";
        if (picked.Count == 0)
        {
            bar.AddChild(FleetAction("Split off", "Fleet Split", () => { },
                !still ? travelling : f.Ships.Count < 2 ? "A fleet of one ship can't be split." : "Click a ship to pick it (Shift+click for more), then split them off into a new fleet here.", true));
            var all = f.Ships.Select(S.Host.Economy.Find).Where(o => o != null && Scrappable(o) && !_scrapped.Contains(o.Id)).ToList();
            bar.AddChild(FleetAction("Scrap fleet...", "Fleet Scrap fleet", () => AskScrap(all, $"all {Ui.Count(all.Count, "ship", "ships")} of {S.NameOf(f)}", f.At),
                !still ? travelling : $"Scraps every ship of {S.NameOf(f)}. Asks first. Half the refined cost comes back and their upkeep is freed.", !still || all.Count == 0, hoverInk: Ui.Danger));
        }
        else
        {
            var ds = picked.Select(o => new Drag('S', o.Id)).ToList();
            string n = Ui.Count(picked.Count, "ship", "ships");
            string why = !still ? travelling : picked.Count >= f.Ships.Count - ShipsLeaving(f) ? "A fleet keeps at least one ship: leave one out of the pick." : null;
            bar.AddChild(FleetAction($"Split {n} off", "Fleet Split", () => { foreach (var d in ds) if (!_moved.ContainsKey(d.Key) && RefuseSplit(d, f) == null) DropSplit(d, f); _picked.RemoveAll(ds.Contains); RefreshMgmt(); },
                why ?? $"They become a new fleet here: {PickedWords(picked)}. Or drag them onto another fleet's window to send them there.", why != null));
            bar.AddChild(FleetAction($"Scrap {n}...", "Fleet Scrap ships", () => AskScrap(picked, PickedWords(picked), f.At),
                still ? "Asks first. Half the refined cost comes back and their upkeep is freed." : travelling, !still, hoverInk: Ui.Danger));
        }
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
        if (aboard.Count + f.Squadrons.Count == 0)
            qz.Flow.AddChild(EmptyCard(berths > 0 ? "Drop squadrons here to load them" : "No carrier in this fleet", FleetBodyW, WideEmptyH));
        Add(qz.Box);
        CargoActions(f, fighters: true, aboard);
    }

    /// <summary>The design's "Ground at Charter" (its turret before the words): the planet's window, where the fleet takes its leaders,
    /// fighters and troops from, opened on the matching tab (every planet's window has the same tabs, 2026-11-24). Null while the fleet
    /// travels.</summary>
    Button GroundButton(Fleet f, string tab)
    {
        if (!Still(f)) return null;
        // leaders land and board on any planet (its Command tab); troops and fighters only on one you hold
        bool leaders = tab == "Command";
        bool held = leaders ? LeadersHere(f) != null : !S.IsDestroyed(f.At) && S.OwnerOf(f.At) == Me;
        string what = leaders ? "leaders" : tab == "Fighters" ? "fighters" : "troops";
        var b = FleetAction($"Ground at {f.At}", $"Ground at {f.At}", () => OpenMgmt(MgKind.Ground, f.At, tab: S.IsDestroyed(f.At) ? null : tab),
            held ? $"Opens {tab} in Ground at {f.At}: drag its {what} onto this fleet, and this fleet's back onto the planet."
                 : S.IsDestroyed(f.At) ? $"Nothing is left of {f.At}: nothing lands or boards here."
                 : $"The Ground window of {f.At}, with what your side last saw there. You don't hold this planet: your {what} don't land on it or board from it (your leaders do, in its Command tab).",
            icon: PixelIconData.Tile["troops"]);
        b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        return b;
    }

    void TroopsAboardTab(Fleet f, List<EconObject> aboard)
    {
        int room = Fm.FreeTroopSlots(f);
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        CardFlow(tz.Flow);
        UnitTiles(tz.Flow, aboard.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), f.At, UnitCard);
        if (aboard.Count == 0) tz.Flow.AddChild(EmptyCard(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet", FleetBodyW, WideEmptyH));
        Add(tz.Box);
        CargoActions(f, fighters: false, aboard);
    }

    /// <summary>
    /// Under the cards of the Fighters and Troops tabs, as the design: one click loads what stands at the planet, or lands what is aboard
    /// (these were in the side panel's fleet page, 2026-11-22), then Ground at the planet. Troops load leaving the garrison the planet needs; "Load all" takes the rest too and says
    /// the planet may revolt. rule: m2-support (garrison), m3-fleets §Orders (load and unload).
    /// </summary>
    void CargoActions(Fleet f, bool fighters, List<EconObject> aboard)
    {
        if (!Still(f)) return;
        string at = f.At;
        bool mine = S.OwnerOf(at) == Me;
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        if (fighters)
        {
            var based = FightersBasedAt(at);
            int berths = Fm.FreeBerths(f), n = Math.Min(based.Count, berths);
            if (n > 0)
                bar.AddChild(FleetAction($"Load {n}", $"Load fighters: {n}", () => { LoadFighters(f, based.Take(n).Select(o => o.Id).ToList()); MgRefreshSoon(); },
                    $"Puts {Ui.Count(n, "squadron", "squadrons")} based at {at} aboard the fleet's carriers" + (based.Count > n ? $" ({based.Count} are based here, room for {berths})." : ".")));
            if (aboard.Count > 0)
                bar.AddChild(FleetAction($"Unload {aboard.Count} at {at}", "Unload fighters", () => { UnloadFighters(f, aboard.Select(o => o.Id).ToList()); MgRefreshSoon(); },
                    mine ? $"Bases the {Ui.Count(aboard.Count, "squadron", "squadrons")} aboard on {at}." : $"Fighters are based only on a planet you hold, and {at} isn't yours.", !mine));
        }
        else
        {
            int room = Fm.FreeTroopSlots(f);
            if (mine && room > 0)
            {
                var avail = S.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == Me && o.System == at && o.AboardShip == 0)
                    .OrderBy(o => o.Id).Select(o => o.Id).ToList();
                int nAll = Math.Min(avail.Count, room);
                int need = S.Host.Support.RequiredGarrison(at); // rule: m2-support (garrison); taking regiments below it risks a revolt
                int nSafe = Math.Min(Math.Max(0, avail.Count - need), room);
                if (nAll == 0)
                    bar.AddChild(FleetAction("Load", "Load troops: none", () => { }, $"No regiments of yours stand at {at} to load.", true));
                if (nSafe > 0)
                    bar.AddChild(FleetAction($"Load {nSafe}, keep the garrison", $"Load troops: {nSafe}", () => { LoadTroops(f, avail.Take(nSafe).ToList()); MgRefreshSoon(); },
                        $"Takes {nSafe} of the {avail.Count} regiments here (room for {room}) and leaves the {need} that {at} needs to keep order."));
                if (nAll > nSafe)
                    bar.AddChild(FleetAction($"Load all {nAll}", $"Load every regiment: {nAll}", () => { LoadTroops(f, avail.Take(nAll).ToList()); MgRefreshSoon(); },
                        $"Takes every regiment there is room for. {at} needs {need} to keep order: with fewer, its people can rise up and its mines and yards stop.", false, Ui.Warn));
            }
            if (aboard.Count > 0)
                bar.AddChild(FleetAction($"Unload {aboard.Count} at {at}", "Unload all troops", () => { UnloadTroops(f, aboard.Select(o => o.Id).ToList()); MgRefreshSoon(); },
                    mine ? $"Lands the {Ui.Count(aboard.Count, "regiment", "regiments")} on {at}: they join its garrison."
                    : S.IsDestroyed(at) ? $"Nowhere to land them: {at} is destroyed."
                    : $"Regiments land only on a planet you hold. To take {at}, use Invasion in Command.", !mine));
        }
        if (GroundButton(f, fighters ? "Fighters" : "Troops") is { } ground) bar.AddChild(ground);
        if (bar.GetChildCount() > 0) Add(bar);
    }

    string Arrives(FleetTransit t, string what)
    {
        int left = DaysLeft(t);
        return (what != null ? what + ", " : "") + (left <= 1 ? "arrives tomorrow" : $"{left} days");
    }
}
