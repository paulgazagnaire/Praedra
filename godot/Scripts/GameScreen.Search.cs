using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;

namespace PraedraViewer;

/// <summary>
/// Search (user story A4): type part of a name and pick a system, one of your fleets or one of your leaders. Enter takes the first hit.
/// While a fleet waits for its destination, picking a system sends it there, so "/" + a few letters + Enter moves a fleet without hunting
/// for the dot. Enemy fleets have no names the player knows, so they are not searched (rule: m5-fog-and-hq §N8).
/// </summary>
public partial class GameScreen
{
    LineEdit _search;
    PanelContainer _searchResults;
    VBoxContainer _searchList;
    readonly List<(string Name, string Detail, Action Pick)> _hits = new();
    const int MaxHits = 9;

    void BuildSearch(Container into)
    {
        _search = new LineEdit
        {
            PlaceholderText = "Search  ( / )",
            CustomMinimumSize = new Vector2(210, 34),
            ClearButtonEnabled = true,
            TooltipText = Ui.Wrap("Find a system, one of your fleets or one of your leaders. Enter picks the first. While a fleet is being moved, picking a system sends it there."),
        };
        _search.TextChanged += _ => RefreshSearch();
        _search.TextSubmitted += _ => { if (_hits.Count > 0) PickHit(0); };
        _search.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { CloseSearch(); _search.AcceptEvent(); }
            else if (e is InputEventKey { Pressed: true, Keycode: Key.Down } && _searchList.GetChildCount() > 0) { ((Control)_searchList.GetChild(0)).GrabFocus(); _search.AcceptEvent(); }
        };
        _search.FocusExited += () => Callable.From(() => { if (!AnyHitFocused()) _searchResults.Visible = false; }).CallDeferred();
        into.AddChild(_search);
    }

    void BuildSearchResults(Control into)
    {
        _searchResults = Ui.Card(new Color(Ui.Chrome, 0.98f), Ui.LineHi, 6, 8);
        _searchResults.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _searchResults.CustomMinimumSize = new Vector2(360, 0);
        _searchResults.Visible = false;
        _searchList = Ui.VBox(0);
        _searchResults.AddChild(DropCell(_searchList));   // the box and its black cell, as the bar's (TopBar)
        into.AddChild(_searchResults);
    }

    bool AnyHitFocused() => _searchList.GetChildren().OfType<Control>().Any(c => c.HasFocus());

    void FocusSearch()
    {
        _search.GrabFocus();
        _search.SelectAll();
        RefreshSearch();
    }

    void CloseSearch()
    {
        _search.Text = "";
        _searchResults.Visible = false;
        _search.ReleaseFocus();
    }

    void RefreshSearch()
    {
        _hits.Clear();
        Ui.Clear(_searchList);
        string q = _search.Text.Trim();
        if (q.Length == 0) { _searchResults.Visible = false; return; }
        foreach (var h in Search(q).Take(MaxHits)) _hits.Add(h);
        if (_hits.Count == 0)
        {
            string none = $"Nothing called \"{q}\".";
            var l = BarText(none, BarLabel);
            l.AddThemeFontOverride("font", BarFontFor(none));
            l.CustomMinimumSize = new Vector2(0, DropRowH);
            l.VerticalAlignment = VerticalAlignment.Center;
            var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            pad.AddThemeConstantOverride("margin_left", BarPad); pad.AddThemeConstantOverride("margin_right", BarPad);
            pad.AddChild(l);
            _searchList.AddChild(pad);
            ShowSearchResults();
            return;
        }
        for (int i = 0; i < _hits.Count; i++)
        {
            int k = i;
            _searchList.AddChild(HitRow(_hits[i].Name, _hits[i].Detail, () => PickHit(k)));
        }
        ShowSearchResults();
    }

    /// <summary>
    /// A hit in the bar's pixel words (Paul 2026-11-15, "Yes A"): the name in the light colour, then what it is in grey ("yours - Charter
    /// Sector"), as the goal box shows GOAL 0/3. Under the mouse, or reached with the arrow keys, the grey words turn light too, so the whole
    /// row is lit; no background, as in the bar. A long description is cut, never the name.
    /// </summary>
    Button HitRow(string name, string detail, Action pick)
    {
        var b = BarButton("", pick, null, BarFont, BarTextSize, BarLabel, BarPad);
        b.FocusMode = FocusModeEnum.All;   // arrow keys walk the list
        b.CustomMinimumSize = new Vector2(0, DropRowH);
        b.SetMeta(UiTest.FullName, $"{name}   {detail}");
        var words = Ui.HBox(12);
        words.MouseFilter = MouseFilterEnum.Ignore;
        words.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        words.OffsetLeft = BarPad; words.OffsetRight = -BarPad;
        var n = BarText(name, BarValue);
        n.AddThemeFontOverride("font", BarFontWide);   // one face for both words and every row: they stand on one line (BarFontWide)
        var d = BarText(detail, BarLabel);
        d.AddThemeFontOverride("font", BarFontWide);
        d.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        d.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;   // cut with … ; not clipped, so an é's accent keeps its top
        words.AddChild(n);
        words.AddChild(d);
        b.AddChild(words);
        bool hover = false, focus = false;   // kept here: while their signals run, the button's own hover and focus are not yet updated
        void Lit() => d.AddThemeColorOverride("font_color", hover || focus ? BarValue : BarLabel);
        b.MouseEntered += () => { hover = true; Lit(); };
        b.MouseExited += () => { hover = false; Lit(); };
        b.FocusEntered += () => { focus = true; Lit(); };
        b.FocusExited += () => { focus = false; Lit(); };
        return b;
    }

    /// <summary>The results hang under the search box. One drop-down at a time: an open layers list closes, it would cover them.</summary>
    void ShowSearchResults()
    {
        if (_layersCard != null && _layersCard.Visible) ToggleLayers();
        _searchResults.Visible = true;
    }

    /// <summary>Hits ranked: names that start with the text before names that contain it; systems, then fleets, then leaders.</summary>
    IEnumerable<(string Name, string Detail, Action Pick)> Search(string q)
    {
        static int Rank(string name, string q) =>
            name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0
            : name.Split(' ', '-').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase)) ? 1
            : name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 9;

        var hits = new List<(int Rank, int Kind, string Name, string Detail, Action Pick)>();
        for (int i = 0; i < G.Systems.Count; i++)
        {
            string n = G.Systems[i].Name;
            int r = Rank(n, q);
            if (r == 9) continue;
            int idx = i;
            hits.Add((r, 0, n, SystemWords(n), () => PickSystem(idx)));
        }
        foreach (var f in S.Game.State.Fleets.Values.Where(f => f.Side == Me))
        {
            int r = Rank(f.Name, q);
            if (r == 9) continue;
            int id = f.Id;
            hits.Add((r, 1, f.Name, $"fleet, {StatusOf(f)}", () => { CloseSearch(); SelectFleet(id, true); }));
        }
        foreach (var c in S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead))
        {
            string n = S.CharName(c.Id);
            int r = Rank(n, q);
            if (r == 9) continue;
            int id = c.Id;
            hits.Add((r, 2, n, $"leader, {S.WhereIsCharacter(c)}", () => { CloseSearch(); ShowTab(DrawerTab.Leaders); GoToCharacter(id); }));
        }
        return hits.OrderBy(h => h.Rank).ThenBy(h => h.Kind).ThenBy(h => h.Name, StringComparer.Ordinal).Select(h => (h.Name, h.Detail, h.Pick));
    }

    /// <summary>"yours", "enemy, seen day 40", "neutral", "unexplored", with the sector: what the player knows, nothing more.</summary>
    string SystemWords(string name)
    {
        string sector = SectorOf(name) is string s ? $" - {s}" : "";
        if (S.IsDestroyed(name)) return "destroyed" + sector;
        if (!S.Explored(name)) return "unexplored" + sector;
        var (known, owner) = S.KnownOwnerOf(name);
        string who = !known ? "owner unknown" : owner == Me ? "yours" : owner == null ? "neutral" : "enemy";
        return who + sector;
    }

    void PickHit(int i)
    {
        if (i < 0 || i >= _hits.Count) return;
        _hits[i].Pick();
    }

    void PickSystem(int idx)
    {
        CloseSearch();
        if (InMoveMode) { _view.Centre(idx); OnSystemPicked(idx); return; }
        _view.Centre(idx);
        _view.Select(idx);
        OnViewChanged();
        ShowTab(DrawerTab.Selected);
    }
}
