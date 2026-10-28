using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// A sector window (user story A3, Classic's way of running an empire): every system of one sector in one table, with support, troops,
/// slots, what is being built and how fresh the picture is. A sector holds at most nine systems on today's map, so the table never needs to
/// scroll. Clicking a row selects the system (or, while a move is being planned, makes it the destination). Screens only.
/// </summary>
public partial class SectorWindow : VBoxContainer
{
    public sealed class RowData
    {
        public string System;
        public Side? Owner;
        public bool Known;
        public string Support = "", Troops = "", Slots = "", Activity = "", Info = "";
        public Color? SupportColor, TroopsColor, ActivityColor, InfoColor;
    }

    readonly GameScreen _screen;
    readonly List<string> _sectors;
    readonly OptionButton _pick;
    readonly Label _summary;
    readonly Tree _tree;

    public string Sector { get; private set; }
    /// <summary>A counter, so the screen can replace the window shown longest ago.</summary>
    public long ShownAt { get; private set; }
    static long _clock;

    static readonly string[] Columns = { "System", "Support", "Troops", "Slots", "Building / fleets", "Seen" };

    public SectorWindow(GameScreen screen, List<string> sectors)
    {
        _screen = screen; _sectors = sectors;
        AddThemeConstantOverride("separation", Ui.Block);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        AddChild(head);
        _pick = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Choose the sector this window shows. Selecting a system on the map also brings up its sector." };
        _pick.AddThemeFontOverride("font", Ui.SemiBold);
        foreach (var s in sectors) _pick.AddItem(s);
        _pick.ItemSelected += i => Show(_sectors[(int)i]);
        head.AddChild(_pick);
        _summary = Ui.Label("", "Hint", wrap: false);
        _summary.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _summary.ClipText = true;
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_summary);

        var frame = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, 6, Ui.Line, 1, 0, 0));
        AddChild(frame);
        _tree = new Tree
        {
            Columns = Columns.Length, ColumnTitlesVisible = true, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row,
            FocusMode = FocusModeEnum.None, ScrollHorizontalEnabled = false, SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        for (int c = 0; c < Columns.Length; c++)
        {
            _tree.SetColumnTitle(c, Columns[c]);
            _tree.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _tree.SetColumnClipContent(c, true);
        }
        _tree.SetColumnExpand(0, true); _tree.SetColumnExpandRatio(0, 7);
        _tree.SetColumnExpand(1, false); _tree.SetColumnCustomMinimumWidth(1, 70);
        _tree.SetColumnExpand(2, false); _tree.SetColumnCustomMinimumWidth(2, 62);
        _tree.SetColumnExpand(3, false); _tree.SetColumnCustomMinimumWidth(3, 62);
        _tree.SetColumnExpand(4, true); _tree.SetColumnExpandRatio(4, 3);
        _tree.SetColumnExpand(5, false); _tree.SetColumnCustomMinimumWidth(5, 58);
        _tree.ItemSelected += OnPicked;
        frame.AddChild(_tree);
    }

    public void Show(string sector)
    {
        if (sector == null) return;
        Sector = sector;
        ShownAt = ++_clock;
        int idx = _sectors.IndexOf(sector);
        if (idx >= 0 && _pick.Selected != idx) _pick.Select(idx);
        Refresh();
    }

    public void Refresh()
    {
        if (Sector == null || !IsVisibleInTree()) return;
        var g = _screen.G;
        var sec = g.Sectors.FirstOrDefault(s => s.Name == Sector);
        if (sec == null) return;
        _tree.Clear();
        var root = _tree.CreateItem();
        var rows = sec.Members.Select(_screen.SectorRow)
            .OrderBy(r => r.Owner == _screen.S.Player ? 0 : r.Owner != null ? 1 : r.Known ? 2 : 3).ThenBy(r => r.System, StringComparer.Ordinal).ToList();
        int mine = 0, shortTroops = 0, idle = 0;
        foreach (var r in rows)
        {
            var it = _tree.CreateItem(root);
            bool own = r.Owner == _screen.S.Player;
            if (own) { mine++; if (r.TroopsColor == Ui.Warn) shortTroops++; if (r.Activity.Contains("idle")) idle++; }
            it.SetText(0, r.System);
            it.SetCustomColor(0, r.Info == "unexplored" ? Ui.Text3 : Ui.OwnerColor(r.Owner, _screen.S.Player));
            it.SetCustomFont(0, Ui.SemiBold);
            it.SetText(1, r.Support); if (r.SupportColor is Color c1) it.SetCustomColor(1, c1);
            it.SetText(2, r.Troops); if (r.TroopsColor is Color c2) it.SetCustomColor(2, c2);
            it.SetText(3, r.Slots);
            it.SetText(4, r.Activity); it.SetCustomColor(4, r.ActivityColor ?? Ui.Text2);
            it.SetText(5, r.Info); it.SetCustomColor(5, r.InfoColor ?? Ui.Text2);
            for (int c = 0; c < Columns.Length; c++) it.SetTooltipText(c, RowTip(r));
            it.SetMetadata(0, r.System);
        }
        var parts = new List<string> { $"{mine} of {rows.Count} yours" };
        if (shortTroops > 0) parts.Add($"{shortTroops} short of troops");
        if (idle > 0) parts.Add(Ui.Count(idle, "idle yard", "idle yards"));
        _summary.Text = string.Join("  ·  ", parts);
    }

    static string RowTip(RowData r) => Ui.Wrap(r.Info == "unexplored"
        ? $"{r.System}: unexplored. You know nothing about it yet.\nClick to select it; during a move, click to send the fleet there."
        : $"{r.System}: support for you {r.Support}, troops {r.Troops} (yours: have / need; others: what you saw), energy slots {r.Slots} (used / total). "
          + $"Building / fleets: {(r.Activity == "?" ? "unknown, you have no eyes there" : r.Activity)}. Seen: {r.Info}.\nClick to select it; during a move, click to send the fleet there.");

    void OnPicked()
    {
        var it = _tree.GetSelected();
        if (it == null) return;
        string sys = it.GetMetadata(0).AsString();
        Callable.From(() => { _tree.DeselectAll(); _screen.SectorRowPicked(sys); }).CallDeferred();
    }

    /// <summary>The systems listed, top to bottom (for the playtest).</summary>
    public IEnumerable<string> Rows()
    {
        var root = _tree.GetRoot();
        if (root == null) yield break;
        for (var it = root.GetFirstChild(); it != null; it = it.GetNext()) yield return it.GetText(0) + " | " + it.GetText(5);
    }

    /// <summary>Click a row as the mouse would (for the playtest).</summary>
    public bool Click(string system)
    {
        var root = _tree.GetRoot();
        for (var it = root?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.GetText(0) == system) { _screen.SectorRowPicked(system); return true; }
        return false;
    }
}
