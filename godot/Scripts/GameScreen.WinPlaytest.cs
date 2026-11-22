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

/// <summary>The management windows' hooks for the headless playtest and the screenshots. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    // ================================================================== for the playtest

    /// <summary>Drop a chip on a zone as the mouse would: returns null when it was done, else why not.</summary>
    internal string MgDrop(string chipKey, string zoneKey)
    {
        NewDay();
        var z = _zones.FirstOrDefault(x => x.Key == zoneKey && IsInstanceValid(x.Box));
        if (z == null) return "no such zone: " + zoneKey;
        var all = Drag.ParseAll(chipKey);
        if (all.Count == 0) return "bad chip: " + chipKey;
        _dragTake = all.ToList();
        var (_, why) = JudgeAll(z);
        if (why != null) return why == "" ? "already there" : why;
        DropAll(z, all);   // as the mouse's drop: what went is no longer picked, what could not stays picked
        foreach (var w in _mgWins.ToList()) RebuildMg(w);
        return null;
    }

    /// <summary>The words of the right-click menu on the button named <paramref name="fullName"/>, or none.</summary>
    internal IReadOnlyList<string> MenuOf(string fullName) =>
        MenuCard(fullName) is { } b && _menus.TryGetValue(b, out var items) ? items().Select(i => i.Text).ToList() : new List<string>();

    /// <summary>The card with a right-click menu named <paramref name="fullName"/>: first a card whose full name is exactly that (unit cards are
    /// panels: "F:1" must not find "F:12"), else the first button whose words start with it.</summary>
    Control MenuCard(string fullName)
    {
        static Control Walk(Node n, string name)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is Control k && k.IsVisibleInTree() && k.HasMeta(UiTest.FullName) && k.GetMeta(UiTest.FullName).AsString() == name) return k;
                if (Walk(c, name) is { } r) return r;
            }
            return null;
        }
        return Walk(this, fullName) ?? UiTest.Find<BaseButton>(this, fullName);
    }

    /// <summary>For a screenshot: the list a button or a place opens (Merge ▾, a fleet's post), a few frames in, once the windows are laid
    /// out, so it shows under its button as the mouse would open it (UI review 1: a list opened before layout drew in the wrong place).</summary>
    public async void ShotMenu(string fullName)
    {
        for (int i = 0; i < 12; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (MenuCard(fullName) is not { } c || !_menus.TryGetValue(c, out var lines)) { GD.PrintErr("no menu " + fullName); return; }
        PopMenu(c, lines(), _menuNone.TryGetValue(c, out var none) ? none() : "Nothing to pick here now.");
    }

    /// <summary>The hover words of the card (a ship row, a leader card, a fleet's status) whose full name is exactly <paramref name="fullName"/>, on one line.</summary>
    internal string CardTip(string fullName) => (MenuCard(fullName)?.TooltipText ?? "").Replace('\n', ' ');

    /// <summary>Right-click the button named <paramref name="fullName"/> and pick the menu line starting with <paramref name="item"/>.</summary>
    /// <summary>Shift+click on a fleet's marker on the map, as the mouse would (several fleets picked on the map).</summary>
    internal void PlayShiftClickFleet(int id) => ShiftPickOnMap(id);

    /// <summary>A right-click on a system on the map, as the mouse would.</summary>
    internal void PlayRightClick(string system) { if (G.TryFindSystem(system, out int i)) OnMapRightClick(i); }

    /// <summary>Let every picked unit card go, as Esc would.</summary>
    internal void ClearUnitPicks() => ClearPicks();

    /// <summary>The hover words of the first button whose words or full name start with <paramref name="prefix"/>, on one line.</summary>
    internal string TipOf(string prefix) => (UiTest.Find<BaseButton>(ClickRoot, prefix)?.TooltipText ?? "").Replace('\n', ' ');

    internal bool RunMenu(string fullName, string item)
    {
        if (MenuCard(fullName) is not { } b || !_menus.TryGetValue(b, out var lines)) return false;
        foreach (var (text, act) in lines()) if (text.StartsWith(item, StringComparison.Ordinal)) { act(); return true; }
        return false;
    }

    /// <summary>The words a yard cell shows under its name, and those it shows while the mouse is on it (null: the same).</summary>
    internal (string Normal, string Hover) CellWords(string fullName) =>
        UiTest.Find<BaseButton>(this, fullName) is HoverWordsButton h ? (h.Normal ?? h.Target?.Text, h.Hover) : (null, null);

    /// <summary>The Infrastructure window's open cell and page, the build cards it shows, and how wide its content wants to be.</summary>
    internal (string Cell, int Page, List<(string Name, bool Usable)> Cards, float Want, float Width)? InfraState(string system)
    {
        if (_mgWins.FirstOrDefault(w => w.Kind == MgKind.Infra && w.System == system) is not { } w) return null;
        RebuildMg(w);
        return (w.Shows?.ToString(), w.Page, _shownBuildRows.Select(b => (b.Row.Name, b.Usable)).ToList(),
            Math.Max(w.Head.GetCombinedMinimumSize().X, w.Body.GetCombinedMinimumSize().X + 2 * WinPad) + 2, WidthOf(w));
    }

    /// <summary>The yards that can build for a planet with no construction yard, soonest first, and the one picked.</summary>
    internal (List<string> Yards, string Picked) InfraSuppliers(string dest) => (SuppliersFor(dest).Select(x => x.Yard).ToList(), PickedSupplier(dest)?.Yard);

    /// <summary>A window's width, and how wide its head and its shown page want to be (a page wider than the window would widen it).</summary>
    internal (float Want, float Width) MgFit(string key)
    {
        if (_mgWins.FirstOrDefault(w => w.Key == key) is not { } w) return (0, 0);
        RebuildMg(w);
        return (Math.Max(w.Head.GetCombinedMinimumSize().X, w.Body.GetCombinedMinimumSize().X + 2 * WinPad) + 2, WidthOf(w));
    }

    /// <summary>The windows are shown (they step aside while a fleet's destination is picked).</summary>
    internal bool MgShown => _mgLayer.Visible;

    internal bool MgHasZone(string zoneKey) => _zones.Any(x => x.Key == zoneKey && IsInstanceValid(x.Box));

    internal string MgText(string key)
    {
        if (_mgWins.FirstOrDefault(w => w.Key == key) is not { } w) return "";
        RebuildMg(w);   // as the next frame would
        return LabelsIn(w.Head) + "\n" + string.Join("\n", w.Pages.Values.Select(p => PageLabels(p.Page))) + "\n" + LabelsIn(w.Body);
    }

    /// <summary>Every label of a tab, shown or not (the playtest reads tabs that are not in front).</summary>
    static string PageLabels(Control page)
    {
        bool was = page.Visible;
        page.Visible = true;
        string text = LabelsIn(page);
        page.Visible = was;
        return text;
    }

    /// <summary>The labels of one tab of a window, shown or not (a window's other tabs, such as a system's Lore, can name the same people).</summary>
    internal string MgPageText(string key, string page)
    {
        if (_mgWins.FirstOrDefault(w => w.Key == key) is not { } w) return "";
        RebuildMg(w);
        return w.Pages.TryGetValue(page, out var p) ? PageLabels(p.Page) : "";
    }

    /// <summary>
    /// User story W9 and QA-104: a short note (an order sent, the interdictor warning after a drop) is drawn over the open windows, and a
    /// note that names a system takes the click there. Godot draws and picks the control under the mouse in tree order, the last one on
    /// top; ZIndex changes only the drawing, which is how QA-104 got through. A headless run gets no mouse at all (the window is minimised
    /// and every event comes back handled), so this checks the order the mouse follows: the note's layer comes after the open windows and
    /// the sector window and before the dialogs, it lets the mouse through where it has no note, and nothing on the way has a ZIndex.
    /// Then the two times the layer steps aside: a drag lets the drop through to the window under a note, and a dialog has the notes
    /// drawn over it but takes their clicks. Null when all holds, else what is wrong.
    /// </summary>
    internal string NoteOverWindowsProblem(string text, string system)
    {
        Toast(text, MsgKind.Warn, system);
        HideCoveredNotes();
        var card = _toasts[^1].Card;
        // where a control sits among the screen's own layers (-1 when it is not on the screen), and whether a ZIndex on its way there
        // would draw it in another order than the mouse picks it
        int Layer(Node n) { while (n != null && n.GetParent() != this) n = n.GetParent(); return n?.GetIndex() ?? -1; }
        bool Lifted(Node n) { for (; n != null && n != this; n = n.GetParent()) if (n is CanvasItem ci && ci.ZIndex != 0) return true; return false; }
        int note = Layer(card);
        var wins = _mgWins.Where(w => IsInstanceValid(w.Panel) && w.Panel.IsVisibleInTree()).Select(w => w.Panel).ToList();
        string why =
            wins.Count == 0 ? "no window is open" :
            note < 0 ? "the note is not on the screen" :
            wins.Any(p => Layer(p) >= note) ? "a window comes after the note, so the window takes its click" :
            _secWin != null && Layer(_secWin) >= note ? "the sector window comes after the note" :
            Layer(_modalLayer) <= note || Layer(_menuPopup) <= note ? "the note comes after the dialogs or the menu" :
            _noteLayer.MouseFilter != MouseFilterEnum.Ignore || _toastBox.MouseFilter != MouseFilterEnum.Ignore ? "the notes' layer catches the mouse where it has no note" :
            card.MouseFilter != MouseFilterEnum.Stop ? "a note that names a system does not take the click" :
            Lifted(card) ? "a ZIndex on the note's way draws it in another order than the mouse picks it" :
            !card.IsVisibleInTree() || _toastBox.Modulate.A < 1 || _noteLayer.Modulate.A < 1 ? "the note is hidden" :
            !ToastText.Contains(text) ? "the note's words are missing" : null;
        var through = MouseBehaviorRecursiveEnum.Disabled;
        SyncNoteLayer(true);
        why ??= _noteLayer.MouseBehaviorRecursive != through ? "while a chip is dragged the note still catches the drop" : null;
        ShowModal(_howPanel); SyncNoteLayer(false);
        why ??= _noteLayer.ZIndex <= 0 || _noteLayer.MouseBehaviorRecursive != through ? "with a dialog open the note is not drawn over it, or takes clicks" : null;
        HideModal(_howPanel); SyncNoteLayer(false);
        why ??= _noteLayer.ZIndex != 0 || _noteLayer.MouseBehaviorRecursive == through ? "after the dialog or the drag the note does not take its click again" : null;
        card.QueueFree(); _toasts.RemoveAt(_toasts.Count - 1);
        return why;
    }

    /// <summary>Close every management window; their buttons leave the tree now, not at the end of the frame, so a later click can't reach them.</summary>
    internal void MgCloseAll() { foreach (var w in _mgWins.ToList()) { CloseMgmt(w); w.Panel.GetParent()?.RemoveChild(w.Panel); } }

    /// <summary>For --dump-ui: the windows on screen by name (each management window by its key, the sector window, the right-hand panel, the whole screen).</summary>
    internal List<(string, Control)> ShotPanels()
    {
        var l = new List<(string, Control)> { ("Screen", this) };
        foreach (var w in _mgWins) l.Add((w.Key, w.Panel));
        if (SectorViewOpen && _secWin != null) l.Add(("Sector", _secWin));
        if (_drawer.Visible) l.Add(("Panel", _drawer));
        return l;
    }

    internal string MgTab(string key) => _mgWins.FirstOrDefault(w => w.Key == key)?.Tab;
    internal IReadOnlyList<string> MgTabs(string key) => _mgWins.FirstOrDefault(w => w.Key == key)?.Pages.Keys.ToList() ?? new List<string>();

    /// <summary>For screenshots: show the drop zones lit as if this chip were being dragged.</summary>
    internal void MgShowDrag(string chipKey)
    {
        // "L:?" picks the first leader of yours standing where the first window is (screenshots don't know ids)
        if (chipKey == "L:?" && _mgWins.FirstOrDefault(x => x.System != null) is { } w)
            chipKey = S.MyCharacters.Where(c => S.Host.Characters.LocationOf(c) == w.System && c.AttachedFleet == 0).OrderBy(c => c.Id).Select(c => $"L:{c.Id}").FirstOrDefault() ?? chipKey;
        // "S:?" the first ship of the first fleet window
        if (chipKey == "S:?" && _mgWins.FirstOrDefault(x => x.Kind == MgKind.Fleet) is { } fw && S.Game.State.Fleets.TryGetValue(fw.Fleet, out var ff) && ff.Ships.Count > 0)
            chipKey = $"S:{ff.Ships[0]}";
        if (Drag.Parse(chipKey) is { } d) { _dragging = d; _dragTake = Drag.ParseAll(chipKey).ToList(); _dragCount = _dragTake.Count; _shotDrag = d; LightZones(d); }
    }
    Drag? _shotDrag;

    /// <summary>The hint shown while dragging <paramref name="chipKey"/> over a zone, as the mouse would see it (for the playtest).</summary>
    internal string PlayDropHint(string chipKey, string zoneKey)
    {
        var z = _zones.FirstOrDefault(x => x.Key == zoneKey && IsInstanceValid(x.Box));
        var all = Drag.ParseAll(chipKey);
        if (z == null || all.Count == 0) return "no such zone or chip";
        _dragTake = all.ToList();
        _dragCount = all.Count;
        _dragMixed = all.Select(ThingName).Distinct().Count() > 1;
        var (fit, why) = JudgeAll(z);
        return why ?? DropWords(all[0], z, fit);
    }

    /// <summary>Close the window on top as its X button does (for the playtest).</summary>
    internal void PlayCloseTop() => CloseTopMgmt();   // a screenshot holds a drag without a mouse
}
