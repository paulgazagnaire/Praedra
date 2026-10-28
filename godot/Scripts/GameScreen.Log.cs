using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Messages (user stories G1, G2, G4): the Log tab with every message by kind, the latest four on the map's bottom-left corner, and a toast
/// for the ones that matter (a refused order, a loss, a warning, a breakthrough). Clicking a message goes to its system and opens its report.
/// </summary>
public partial class GameScreen
{
    ItemList _log;
    readonly List<Message> _allMessages = new();      // everything (capped), oldest first
    readonly List<Message> _logMessages = new();      // what the list shows under the current filter
    readonly List<Button> _filterButtons = new();
    string _logFilter = "All";
    const int TickerRows = 4;

    static readonly (string Name, string Tip)[] Filters =
    {
        ("All", "Every message"),
        ("Battles", "Fights: battles, bombardments, invasions, captures, harvests"),
        ("Fleets", "Fleet arrivals, retreats, blockades, troops loaded"),
        ("Systems", "Your systems: uprisings, losses, the headquarters, the race to win"),
        ("Build", "Construction, research, upkeep"),
        ("Leaders", "Leaders and missions"),
        ("Intel", "What your side saw or heard: sightings, sensors, spies"),
        ("Orders", "Your orders: sent, refused, accepted"),
        ("My systems", "Only things that happen at your own systems"),
    };

    Control BuildLogTab()
    {
        var host = Ui.VBox(8);
        host.SizeFlagsVertical = SizeFlags.ExpandFill;
        var margin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", 14); margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 12); margin.AddThemeConstantOverride("margin_bottom", 12);
        var v = Ui.VBox(8);
        v.SizeFlagsVertical = SizeFlags.ExpandFill;
        margin.AddChild(v);
        v.AddChild(Ui.Label("Messages", "Title"));
        v.AddChild(Ui.Label("Click a message to go there; a battle or invasion opens its report.", "Hint"));
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 4); flow.AddThemeConstantOverride("v_separation", 4);
        var group = new ButtonGroup();
        foreach (var (name, tip) in Filters)
        {
            string n = name;
            var b = Ui.Chip(name, () => SetLogFilter(n), tip, name == _logFilter, group);
            _filterButtons.Add(b);
            flow.AddChild(b);
        }
        v.AddChild(flow);
        _log = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, AutoHeight = false, FocusMode = FocusModeEnum.None, SameColumnWidth = true };
        _log.AddThemeFontSizeOverride("font_size", 14);
        _log.ItemClicked += OnLogClicked;
        v.AddChild(_log);
        host.AddChild(margin);
        return host;
    }

    static Color KindColor(MsgKind k) => k switch
    {
        MsgKind.Good => new Color("8fe0b0"),
        MsgKind.Bad => new Color("ff9a9a"),
        MsgKind.Warn => new Color("f5d27a"),
        MsgKind.Battle => new Color("9cc9f5"),
        _ => Ui.Text2,
    };

    bool PassesFilter(Message m) => _logFilter switch
    {
        "All" => true,
        "My systems" => m.System == null || S.OwnerOf(m.System) == S.Player,
        "Battles" => m.Combat || m.Kind == MsgKind.Battle || m.Cat == MsgCat.Battles,
        _ => m.Cat.ToString() == _logFilter,
    };

    void SetLogFilter(string f)
    {
        _logFilter = f;
        foreach (var b in _filterButtons) b.SetPressedNoSignal(b.Text == f);
        _log.Clear(); _logMessages.Clear();
        foreach (var m in _allMessages) if (PassesFilter(m)) AppendLogRow(m);
        ScrollLogToEnd();
    }

    /// <param name="fresh">true for a message the day just produced (it may get a toast), false for the player's own notes and old ones.</param>
    void AddLogItem(Message m, bool fresh)
    {
        if (_log == null) return;
        _allMessages.Add(m);
        if (_allMessages.Count > MaxLogItems) _allMessages.RemoveAt(0);
        if (PassesFilter(m)) { AppendLogRow(m); if (_logHost.Visible) ScrollLogToEnd(); }
        if (fresh && ToastWorthy(m)) Toast(m.Text, m.Kind, m.System);
        RefreshTicker();
    }

    static readonly HashSet<string> GoodNews = new(StringComparer.Ordinal)
    {
        "ResearchLevelReached", "HearthTipGiven", "HqSectorClueGiven", "MissionSucceeded", "CharacterFreed", "CharacterEscaped", "TalentDiscovered", "DominanceChanged",
    };

    /// <summary>A refused order, a loss, a warning, or real good news: the rest stays in the log.</summary>
    static bool ToastWorthy(Message m) =>
        m.Kind is MsgKind.Bad || (m.Kind == MsgKind.Warn && m.Tag != "Contact") || (m.Tag != null && GoodNews.Contains(m.Tag) && m.Kind != MsgKind.Info);

    void AppendLogRow(Message m)
    {
        _logMessages.Add(m);
        _log.AddItem($"Day {m.Day}   {m.Text}");
        int idx = _log.ItemCount - 1;
        _log.SetItemCustomFgColor(idx, KindColor(m.Kind));
        _log.SetItemTooltip(idx, m.System != null ? Ui.Wrap(m.Text + (m.Detail != null ? "\nClick for the report." : "\nClick to go there.")) : Ui.Wrap(m.Text));
        if (_log.ItemCount > MaxLogItems) { _log.RemoveItem(0); _logMessages.RemoveAt(0); }
    }

    void ScrollLogToEnd() => Callable.From(() => { if (_log == null) return; var sb = _log.GetVScrollBar(); sb.Value = sb.MaxValue; }).CallDeferred();

    void OnLogClicked(long index, Vector2 pos, long button)
    {
        if (index < 0 || index >= _logMessages.Count) return;
        OpenMessage(_logMessages[(int)index]);
    }

    void OpenMessage(Message m)
    {
        if (m.System != null && G.TryFindSystem(m.System, out int idx)) { _view.Centre(idx); _view.Select(idx); }
        if (m.Detail != null) ShowReport(m);
    }

    /// <summary>The latest messages on the map's bottom-left corner.</summary>
    void RefreshTicker()
    {
        if (_ticker == null) return;
        Ui.Clear(_ticker);
        foreach (var m in _allMessages.Skip(Math.Max(0, _allMessages.Count - TickerRows)))
        {
            var mm = m;
            var b = Ui.Button($"Day {m.Day}   {m.Text}", () => OpenMessage(mm), null, "Row");
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.CustomMinimumSize = new Vector2(440, 24);
            b.AddThemeFontSizeOverride("font_size", Ui.Small);
            b.AddThemeColorOverride("font_color", KindColor(m.Kind));
            b.TooltipText = Ui.Wrap(m.Text);
            _ticker.AddChild(b);
        }
        _tickerCard.Visible = !InMoveMode && _ticker.GetChildCount() > 0;
    }
}
