using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The top bar, after Paul's Paper frames "ui-update-nov-24" (Sibyl closed) and "ui-update-nov-26" (Sibyl open), as he changed them on
/// 2026-11-26: one near-black strip 40 px tall with 1 px lines between its cells (no line under it any more). Left, from the screen's edge:
/// the menu, Layers, the goal, the search. Right, to the edge: Raw, Refined and Upkeep in one cell, the day, the clock with its speeds, then
/// the advisor's cell ("Sibyl", which turns into × while her panel is open). Pixel words (Pixel Operator 16): blue-grey labels, light values.
/// A cell you click holds a 32 px chip 4 px in, with 4 px corners; the cells of words (the stocks, the day) keep 16 px each side.
/// The bar has a layer of its own over the windows, so the search results and the layers list it opens are never hidden under a window;
/// the windows themselves start under it (TopClear) and never cover it.
/// </summary>
public partial class GameScreen
{
    // ---- the frames' colours (ui-update-nov-24 and nov-26, 2026-11-26)
    static readonly Color BarBg = new("060709");       // the strip, and its chips at rest (the chips are drawn on the strip's own colour)
    static readonly Color BarEdge = new("161a22");     // the lines between the cells, and round a drop-down
    static readonly Color BarCellBg = new("060709");   // the cells inside a drop-down
    static readonly Color BarLabel = new("54638a");    // labels: GOAL, Search, Layers, Raw, DAY, 2x
    static readonly Color BarValue = new("b8c2e0");    // values (0/3, 9999), and a switch that is on (the speed, Layers open)
    static readonly Color BarMenu = new("7893c5");     // a label under the mouse (the chip stays the strip's colour: Paul 2026-11-24, "B")
    static readonly Color BarIcon = new("dddddd");     // the pixel icons of Raw, Refined and Upkeep, as the frames keep them
    static readonly Color BarIconLight = new("b8c2e0"); // the menu's icon, Layers' and the magnifier: the menu's colour in the frames
    static readonly Color BarClock = new("bfd0ff");    // the clock's icon while the days run (the frames' pause), and an icon under the mouse
    static readonly Color AdvisorCellBg = new("202a3b"); // the advisor's cell, which joins the head of her panel under it

    // ---- the frame's sizes
    const int BarBoxH = 40;       // the strip, its 1 px line included: the windows and the drop-downs start under it
    const int BarCellH = 32;      // a chip
    const int BarInset = 4;       // a chip's cell: 4 px round the chip (the 1 px line takes the last of the 4 below it)
    const int BarPad = 16;        // a cell of words (the stocks, the day): 16 px each side of them; a drop-down row's words too
    const int ChipPad = 8;        // words in a chip: 8 px each side
    const int StockGap = 16;      // between Raw, Refined and Upkeep
    const int BarSide = 16;       // the map's layer key under the bar, from the screen's left edge
    const int BarTextSize = 16;   // Pixel Operator 16: one px to a font pixel, two screen pixels on a Retina screen
    const int AdvisorCellW = 87;  // the advisor's cell, the frames' width (its words centred)
    const int SearchWide = 288, SearchNarrow = 150;   // the search chip: the frame's 288 (it drew 320 on nov-15; 290 once the advisor came)
    const int BarMinGap = 16;     // the least room left between the left and the right cells
    const int SearchResultsWidth = 360;
    const int DropRowH = 32;      // a row of the search results or the layers list: the bar's words, 32 px apart
    const int DropPad = 5;        // a drop-down's box round its black cells
    // the frame's widths of the stocks and the day, made for 9999 and 9999/9999: the bar keeps them, so it does not shift as numbers grow
    const int RawW = 78, RefinedW = 102, UpkeepW = 137, DayW = 55;

    Control _barLayer;
    HBoxContainer _barLeft, _barRight, _dayWords;
    PanelContainer _barStrip, _searchBox, _layersBox;
    ColorRect _searchEnd;   // the search's closing line: hidden once the search narrows and reaches the stocks' line
    Control _rawCell, _refinedCell, _upkeepCell;   // one stock each: its icon, name and number, in the stocks' cell
    Label _goalCaption, _goalValue, _rawCaption, _refinedCaption;
    readonly List<PanelContainer> _barTextCells = new();
    static Font _barFont;
    static Font BarFont => _barFont ??= Ui.Operator ?? ThemeDB.FallbackFont;

    const string RawTip = "Raw: ore dug by your mines. It waits for your refineries, which turn it into Refined.";
    const string RefinedTip = "Refined: the material you spend on ships, troops and buildings. Each yard takes it as it builds.";

    Control BuildTopBar()
    {
        _barLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, Name = "TopBar" };
        _barLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // the strip: the map does not take clicks under it
        _barStrip = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        _barStrip.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = BarBg, BorderColor = BarEdge, BorderWidthBottom = 1 });
        _barStrip.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        _barStrip.OffsetBottom = BarBoxH;
        _barLayer.AddChild(_barStrip);

        _barLeft = Ui.HBox(0);
        _barLeft.MouseFilter = MouseFilterEnum.Ignore;
        _barLeft.SetAnchorsPreset(LayoutPreset.TopLeft);
        _barLeft.OffsetLeft = _barLeft.OffsetRight = 0;
        _barLayer.AddChild(_barLeft);
        _barRight = Ui.HBox(0);
        _barRight.MouseFilter = MouseFilterEnum.Ignore;
        _barRight.SetAnchorsPreset(LayoutPreset.TopRight);
        _barRight.GrowHorizontal = GrowDirection.Begin;
        _barRight.OffsetLeft = _barRight.OffsetRight = 0;
        _barLayer.AddChild(_barRight);

        // ---- left, each cell closed by a line: the menu, Layers, the goal, the search
        _menuButton = BarButton("", ToggleMenu, "Menu: fleets, leaders, missions, goal, sectors, save, load, settings (Esc closes)", BarFont, BarTextSize, BarLabel, 0);
        _menuButton.SetMeta(UiTest.FullName, "Menu");
        _menuButton.CustomMinimumSize = new Vector2(BarCellH, BarCellH);
        IconOnChip(_menuButton, MenuIcon, BarIconLight, BarClock);   // the frames' three bars, as the game's pixel icon
        _barLeft.AddChild(ChipCell(_menuButton));
        BarLine(_barLeft);

        // Layers: its stacked-sheets icon, then the word (Paul chose B 2026-11-25; the frame's icon there was a copy of Refined's)
        _layersButton = BarButton("Layers", ToggleLayers, "Show or hide the map layers (V cycles them)", BarFont, BarTextSize, BarLabel, ChipPad);
        _layersButton.ToggleMode = true;
        // the stacked-sheets icon left of the word (Paul chose B 2026-11-25; the frame's icon there was a copy of Refined's): drawn with
        // square pixels, and lit with the word as the frame draws it: the label's grey at rest, the hover blue under the mouse, the value's
        // light while the list is open
        _layersTex = new[] { PixelTexture(LayersIcon, BarLabel), PixelTexture(LayersIcon, BarMenu), PixelTexture(LayersIcon, BarValue) };
        _layersIcon = PixelIcon(LayersIcon, BarLabel);
        _layersIcon.AnchorTop = _layersIcon.AnchorBottom = 0.5f;
        _layersIcon.OffsetTop = -8; _layersIcon.OffsetBottom = 8;
        _layersButton.AddChild(_layersIcon);
        PadLayers(ChipPad);
        _layersButton.MouseEntered += PaintLayers;
        _layersButton.MouseExited += PaintLayers;
        _layersButton.Toggled += _ => PaintLayers();
        _barLeft.AddChild(_layersBox = ChipCell(_layersButton));
        BarLine(_barLeft);

        _goalButton = BarButton("", ShowGoal, "How you win, and how close each side is (G).", BarFont, BarTextSize, BarLabel, ChipPad);
        _goalCaption = BarText("GOAL", BarLabel);
        _goalValue = BarText("", BarValue);
        var gw = Ui.HBox(4);
        gw.MouseFilter = MouseFilterEnum.Ignore;
        gw.Alignment = BoxContainer.AlignmentMode.Center;
        gw.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        gw.AddChild(_goalCaption);
        gw.AddChild(_goalValue);
        _goalButton.AddChild(gw);
        _goalButton.MouseEntered += () => _goalCaption.AddThemeColorOverride("font_color", BarMenu);
        _goalButton.MouseExited += () => _goalCaption.AddThemeColorOverride("font_color", BarLabel);
        _barLeft.AddChild(ChipCell(_goalButton));
        BarLine(_barLeft);

        var sc = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0, BarCellH) };
        sc.AddThemeStyleboxOverride("panel", ChipBox(BarBg, ChipPad));
        sc.MouseDefaultCursorShape = CursorShape.Ibeam;
        sc.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) FocusSearch(); };
        var sh = Ui.HBox(4);
        sh.MouseFilter = MouseFilterEnum.Ignore;
        sc.AddChild(sh);
        BuildSearch(sh);
        StyleSearch(_search);
        var lens = PixelIcon(MagnifierIcon, BarIconLight);
        lens.MouseFilter = MouseFilterEnum.Stop;
        lens.TooltipText = _search.TooltipText;
        lens.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) FocusSearch(); };
        sh.AddChild(lens);
        _barLeft.AddChild(_searchBox = ChipCell(sc));
        SearchWidth = SearchWide;
        _searchEnd = BarLine(_barLeft);

        // ---- right, each cell opened by a line: your stocks, the day, the clock, the advisor
        BarLine(_barRight);
        var stocks = Ui.HBox(StockGap);
        stocks.MouseFilter = MouseFilterEnum.Ignore;
        _barRight.AddChild(TextCell(stocks));
        (_rawCell, _rawCaption, _rawLabel) = StockItem(stocks, RawIcon, "Raw", RawTip);
        (_refinedCell, _refinedCaption, _refinedLabel) = StockItem(stocks, RefinedIcon, "Refined", RefinedTip);
        (_upkeepCell, _upkeepCaption, _upkeepLabel) = StockItem(stocks, UpkeepIcon, "Upkeep", "");
        BarLine(_barRight);

        _dayWords = Ui.HBox(4);
        _dayWords.MouseFilter = MouseFilterEnum.Ignore;
        _dayWords.Alignment = BoxContainer.AlignmentMode.Center;
        _sideLabel = BarText("DAY", BarLabel);
        _sideLabel.MouseFilter = MouseFilterEnum.Stop;   // its tooltip says which side you play
        _dayWords.AddChild(_sideLabel);
        _dayLabel = BarText("0", BarValue);
        _dayLabel.MouseFilter = MouseFilterEnum.Stop;
        _dayLabel.TooltipText = Ui.Wrap("The game day. Each day every order you gave is carried out, fleets fly, yards build. Press Play to let the days run.");
        _dayWords.AddChild(_dayLabel);
        _barRight.AddChild(TextCell(_dayWords));
        BarLine(_barRight);

        var clock = Ui.HBox(2);
        clock.MouseFilter = MouseFilterEnum.Ignore;
        // one button that plays and pauses (Paul 2026-11-14, impeccable critique): it shows what a click does, and is lit while the clock
        // runs; one day at a time is the N key
        _playButton = BarButton("", TogglePlay, "Play or pause (Space); N moves one day. The clock stops by itself after a battle, a capture or an invasion.", BarFont, BarTextSize, BarLabel, 0);
        _playButton.CustomMinimumSize = new Vector2(BarCellH, BarCellH);
        _playIcon = IconOnChip(_playButton, PlayIcon, BarIconLight, BarClock);
        clock.AddChild(_playButton);
        var sg = new ButtonGroup();
        for (int i = 0; i < Speeds.Length; i++)
        {
            int k = i;
            var c = BarButton($"{Speeds[i]}x", () => SetSpeed(k), $"A day every {SecondsPerDay / Speeds[i]:0.#} seconds (key {i + 1})", BarFont, BarTextSize, BarLabel, 0);
            c.CustomMinimumSize = new Vector2(BarCellH, BarCellH);
            c.ToggleMode = true; c.ButtonGroup = sg; c.ButtonPressed = i == _speedIndex;
            _speedChips.Add(c);
            clock.AddChild(c);
        }
        _barRight.AddChild(ChipCell(clock));
        BarLine(_barRight);

        // ---- the advisor, right of the clock (Paul 2026-11-24): Sibyl for the Hand. Her cell (the frames of 2026-11-26) fills the bar's
        // height at the right edge in the colour of her panel's head: her name in bold while the panel is closed, × while it is open, so the
        // cell and the head under it read as one tab. A click opens or closes the panel.
        _advisorButton = new Button { ToggleMode = true, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
        _advisorButton.Pressed += ToggleAdvisor;
        _advisorButton.SetMeta(UiTest.FullName, "Advisor");
        var cellBox = new StyleBoxFlat { BgColor = AdvisorCellBg };
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) _advisorButton.AddThemeStyleboxOverride(st, cellBox);
        _advisorButton.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _advisorButton.CustomMinimumSize = new Vector2(AdvisorCellW, BarBoxH);
        foreach (var c in new[] { "font_color", "font_focus_color", "font_pressed_color" }) _advisorButton.AddThemeColorOverride(c, BarValue);
        foreach (var c in new[] { "font_hover_color", "font_hover_pressed_color" }) _advisorButton.AddThemeColorOverride(c, BarClock);
        _advisorButton.MouseEntered += PaintAdvisor;
        _advisorButton.MouseExited += PaintAdvisor;
        _barRight.AddChild(_advisorButton);
        // while the panel is open, its head's colour runs over the bar's bottom line above it too, so the cell and the head are one shape
        _advisorJoin = new ColorRect { Color = AdvisorCellBg, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _advisorJoin.AnchorLeft = _advisorJoin.AnchorRight = 1;
        _advisorJoin.OffsetLeft = -DrawerWidth; _advisorJoin.OffsetRight = 0;
        _advisorJoin.OffsetTop = BarBoxH - 1; _advisorJoin.OffsetBottom = BarBoxH;
        _barLayer.AddChild(_advisorJoin);

        // ---- what the cells open: the search results and the layers list hang under them, over the windows
        BuildSearchResults(_barLayer);
        _searchResults.AddThemeStyleboxOverride("panel", BarDropBox(DropPad, DropPad));
        BuildMapTools();
        _layersCard.AddThemeStyleboxOverride("panel", BarDropBox(DropPad, DropPad));
        _barLayer.AddChild(_layersCard);

        // placed again when the screen changes size, and when the words change width (a number grows, the goal turns into a countdown);
        // that signal comes after the change, deferred, and the placing gives the same answer for the same words, so it settles
        _barLayer.Resized += PlaceBar;
        _barLeft.MinimumSizeChanged += PlaceBar;
        _barRight.MinimumSizeChanged += PlaceBar;
        _barLeft.SortChildren += PlaceDrops;
        _searchResults.VisibilityChanged += PlaceDrops;
        _layersCard.VisibilityChanged += PlaceDrops;
        return _barLayer;
    }

    // ================================================================== the pieces

    /// <summary>The 1 px line between two cells, the strip's height above its own line.</summary>
    static ColorRect BarLine(HBoxContainer into)
    {
        var line = new ColorRect { Color = BarEdge, CustomMinimumSize = new Vector2(1, BarBoxH - 1), MouseFilter = MouseFilterEnum.Ignore };
        into.AddChild(line);
        return line;
    }

    /// <summary>A cell that holds chips: 4 px round them (3 below, over the strip's line).</summary>
    static PanelContainer ChipCell(Control content)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, BarBoxH - 1) };
        var s = new StyleBoxEmpty { ContentMarginLeft = BarInset, ContentMarginRight = BarInset, ContentMarginTop = BarInset, ContentMarginBottom = BarInset - 1 };
        p.AddThemeStyleboxOverride("panel", s);
        content.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        p.AddChild(content);
        return p;
    }

    /// <summary>A cell of words (the stocks, the day): 16 px each side, the words centred on the strip's height.</summary>
    PanelContainer TextCell(Control content)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, BarBoxH - 1) };
        p.AddThemeStyleboxOverride("panel", TextCellBox(BarPad));
        content.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        p.AddChild(content);
        _barTextCells.Add(p);
        return p;
    }

    static StyleBoxEmpty TextCellBox(int padX) => new() { ContentMarginLeft = padX, ContentMarginRight = padX };

    /// <summary>A chip's look: the fill, 4 px corners, and its words' padding.</summary>
    static StyleBoxFlat ChipBox(Color fill, int padX)
    {
        var s = new StyleBoxFlat { BgColor = fill, AntiAliasing = true };
        s.SetCornerRadiusAll(4);
        s.ContentMarginLeft = padX; s.ContentMarginRight = padX; s.ContentMarginTop = 0; s.ContentMarginBottom = 0;
        return s;
    }

    /// <summary>The search chip's width (the frame's 288; narrower when the interface is larger).</summary>
    float SearchWidth
    {
        get => _searchBox.CustomMinimumSize.X - 2 * BarInset;
        set => _searchBox.CustomMinimumSize = new Vector2(value + 2 * BarInset, BarBoxH - 1);
    }

    /// <summary>The look of what a box opens under it (search results, the layers list): the box's colours, edged all round.</summary>
    static StyleBoxFlat BarDropBox(int padX, int padY)
    {
        var s = new StyleBoxFlat { BgColor = BarBg, BorderColor = BarEdge };
        s.SetBorderWidthAll(1);
        s.ContentMarginLeft = padX; s.ContentMarginRight = padX; s.ContentMarginTop = padY; s.ContentMarginBottom = padY;
        return s;
    }

    /// <summary>A black cell inside a drop-down, as the bar's boxes hold black cells: the rows sit on it.</summary>
    static PanelContainer DropCell(Control rows)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = BarCellBg });
        p.AddChild(rows);
        return p;
    }

    /// <summary>A row of the layers list (Paul 2026-11-15, "Yes A": the bar's pixel font): grey words, lighter under the mouse, light while it is on.</summary>
    Button DropRow(string text, Action onPressed, string tip)
    {
        var b = BarButton(text, onPressed, tip, BarFontFor(text), BarTextSize, BarLabel, BarPad);
        b.CustomMinimumSize = new Vector2(0, DropRowH);
        b.Alignment = HorizontalAlignment.Left;
        return b;
    }

    /// <summary>Pixel Operator, or for words with a letter it lacks (it has é and ñ, not ł or ń) <see cref="BarFontWide"/>.</summary>
    static Font BarFontFor(string text) =>
        BarFont is FontFile px && text.All(c => char.IsWhiteSpace(c) || px.HasChar(c)) ? BarFont : BarFontWide;

    static Font _barFontWide;
    /// <summary>
    /// Pixel Operator with Space Grotesk for the letters it lacks (ł, ń). Space Grotesk's line is trimmed to Pixel Operator's at the bar's
    /// size, so such a word is set on the same line as its neighbours. The search rows give both their words this face, so the name and the
    /// description stand on one line whichever has the rare letter.
    /// </summary>
    static Font BarFontWide
    {
        get
        {
            if (_barFontWide != null) return _barFontWide;
            var g = Ui.GroteskMedium;
            var trimmed = new FontVariation
            {
                BaseFont = g,
                SpacingTop = Mathf.RoundToInt(BarFont.GetAscent(BarTextSize) - g.GetAscent(BarTextSize)),
                SpacingBottom = Mathf.RoundToInt(BarFont.GetDescent(BarTextSize) - g.GetDescent(BarTextSize)),
            };
            return _barFontWide = new FontVariation { BaseFont = BarFont, Fallbacks = new Godot.Collections.Array<Font> { trimmed } };
        }
    }

    /// <summary>A chip you click. The words are grey, lighter under the mouse, and light while the switch is on. The chip itself stays black
    /// under the mouse: only the words change (Paul 2026-11-24 chose "B": a #131313 chip on black was too faint to see).</summary>
    Button BarButton(string text, Action onPressed, string tip, Font font, int size, Color color, int padX)
    {
        var b = Ui.Button(text, onPressed, tip);
        SetChipPad(b, padX);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeFontOverride("font", font);
        b.AddThemeFontSizeOverride("font_size", size);
        SetBarButtonColour(b, color);
        b.CustomMinimumSize = new Vector2(0, BarCellH);
        return b;
    }

    static void SetChipPad(Button b, int padX, int lead = 0)
    {
        var rest = ChipBox(BarBg, padX);
        rest.ContentMarginLeft += lead;   // room for an icon drawn before the words
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) b.AddThemeStyleboxOverride(st, rest);
    }

    /// <summary>The words' colour at rest; one step lighter under the mouse; light while the switch is on.</summary>
    static void SetBarButtonColour(Button b, Color color)
    {
        var hover = color == BarLabel ? BarMenu : BarValue;
        b.AddThemeColorOverride("font_color", color);
        b.AddThemeColorOverride("font_focus_color", color);
        b.AddThemeColorOverride("font_hover_color", hover);
        b.AddThemeColorOverride("font_pressed_color", BarValue);
        b.AddThemeColorOverride("font_hover_pressed_color", BarValue);
    }

    /// <summary>The goal button holds its two words as labels (a button has one colour): it takes their width, and its padding.</summary>
    void FitGoalButton()
    {
        var words = _goalCaption.GetParent<Control>();
        var pad = _goalButton.GetThemeStylebox("normal");
        _goalButton.CustomMinimumSize = new Vector2(words.GetCombinedMinimumSize().X + pad.GetMinimumSize().X, BarCellH);
    }

    static Label BarText(string text, Color color)
    {
        var l = Ui.Label(text, null, color, BarTextSize, wrap: false);
        l.AddThemeFontOverride("font", BarFont);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return l;
    }

    /// <summary>One of your stocks in the stocks' cell: its pixel icon, its name and its number. The tooltip says what it is.</summary>
    static (Control Item, Label Caption, Label Value) StockItem(HBoxContainer stocks, string[] icon, string caption, string tip)
    {
        var h = Ui.HBox(4);
        h.MouseFilter = MouseFilterEnum.Stop;
        h.TooltipText = Ui.Wrap(tip);
        h.AddChild(PixelIcon(icon, BarIcon));
        var cap = BarText(caption, BarLabel);
        h.AddChild(cap);
        var v = BarText("0", BarValue);
        h.AddChild(v);
        stocks.AddChild(h);
        return (h, cap, v);
    }

    /// <summary>A stock's tooltip, on its words and its number.</summary>
    static void SetStockTip(Control item, Label value, string tip)
    {
        item.TooltipText = Ui.Wrap(tip);
        value.TooltipText = item.TooltipText;
    }

    // ---- the top bar's 16 px pixel icons (tone grids from PixelIconData, Paul 2026-11-18: code only)

    static string[] RawIcon => PixelIconData.Bar["raw"];
    static string[] RefinedIcon => PixelIconData.Bar["refined"];
    static string[] UpkeepIcon => PixelIconData.Bar["upkeep"];
    static string[] MagnifierIcon => PixelIconData.Bar["search"];
    /// <summary>Layers: three sheets stacked, seen from above (Paul chose B 2026-11-25).</summary>
    static string[] LayersIcon => PixelIconData.Bar["layers"];
    const int IconGap = 4;   // between a 16 px icon and its word, as in the stocks

    TextureRect _layersIcon;
    ImageTexture[] _layersTex;   // at rest, under the mouse, open: as the advisor's

    /// <summary>The Layers chip's padding, with room for its icon before the word.</summary>
    void PadLayers(int padX)
    {
        SetChipPad(_layersButton, padX, 16 + IconGap);
        _layersIcon.OffsetLeft = padX;
        _layersIcon.OffsetRight = padX + 16;
    }

    /// <summary>The Layers icon takes the word's state: light while the list is open, a step lighter under the mouse, grey otherwise.</summary>
    void PaintLayers()
    {
        if (_layersIcon == null) return;
        _layersIcon.Texture = _layersTex[_layersButton.ButtonPressed ? 2 : _layersButton.IsHovered() ? 1 : 0];
    }
    /// <summary>The search's clear button, white so the field's clear-button colours tint it.</summary>
    static string[] ClearIcon => PixelIconData.Bar["clear"];
    /// <summary>The menu: three bars (Paul's frames of 2026-11-26; the game's ≡ before).</summary>
    static string[] MenuIcon => PixelIconData.Bar["menu"];
    /// <summary>The clock's button shows what a click does: play while it is paused, pause while the days run.</summary>
    static string[] PlayIcon => PixelIconData.Bar["play"];
    static string[] PauseIcon => PixelIconData.Bar["pause"];

    /// <summary>A pixel icon centred on a chip with no words (the menu, the clock): <paramref name="rest"/> at rest, <paramref name="hover"/>
    /// under the mouse. Returns the picture, so the clock can swap play for pause.</summary>
    TextureRect IconOnChip(Button chip, string[] icon, Color rest, Color hover)
    {
        var pic = PixelIcon(icon, rest);
        var c = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        c.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        c.AddChild(pic);
        chip.AddChild(c);
        var tex = new[] { PixelTexture(icon, rest), PixelTexture(icon, hover) };
        chip.MouseEntered += () => { if (chip != _playButton) pic.Texture = tex[1]; else PaintPlay(); };
        chip.MouseExited += () => { if (chip != _playButton) pic.Texture = tex[0]; else PaintPlay(); };
        return pic;
    }

    TextureRect _playIcon;
    ImageTexture[] _clockTex;   // play at rest, play under the mouse, pause (the days run): the frames draw the pause in #BFD0FF

    /// <summary>The clock's icon: play (light, lighter under the mouse) while paused, the lit pause while the days run.</summary>
    void PaintPlay()
    {
        if (_playIcon == null) return;
        _clockTex ??= new[] { PixelTexture(PlayIcon, BarIconLight), PixelTexture(PlayIcon, BarClock), PixelTexture(PauseIcon, BarClock) };
        _playIcon.Texture = _clockTex[_playing ? 2 : _playButton.IsHovered() ? 1 : 0];
    }

    Button _advisorButton;
    ColorRect _advisorJoin;

    /// <summary>The advisor's name: Sibyl, Praevisio's assistant, for the Hand (specs/creative/02-the-hand.md); the Collective has none yet.</summary>
    string AdvisorName => Me == Praedra.Core.Clock.Side.Hand ? "Sibyl" : null;

    /// <summary>The advisor button opens the window on the right where it was last left (Needs you at first), and closes it.</summary>
    void ToggleAdvisor()
    {
        if (_drawer.Visible) CloseDrawer(); else ShowTab(_tab);
        PaintAdvisor();
    }

    /// <summary>Her name in bold while her panel is closed ("Advisor" for the Collective, whose advisor has no name yet), × while it is open
    /// (the frames ui-update-nov-24 and nov-26); the words lighten under the mouse; its hover names the advisor.</summary>
    void PaintAdvisor()
    {
        if (_advisorButton == null) return;
        bool open = _drawer.Visible;
        _advisorButton.SetPressedNoSignal(open);
        if (_advisorJoin != null) _advisorJoin.Visible = open;
        string word = open ? "×" : AdvisorName ?? "Advisor";
        if (_advisorButton.Text != word)
        {
            _advisorButton.Text = word;
            _advisorButton.AddThemeFontOverride("font", open ? BarFont : Ui.OperatorBold ?? BarFont);
            _advisorButton.AddThemeFontSizeOverride("font_size", open ? 40 : BarTextSize);   // the frame's × is 40 px
        }
        string who = AdvisorName is string n ? $"{n}, your advisor" : "Your advisor";
        _advisorButton.TooltipText = Ui.Wrap($"{who}: what needs you, your fleets, leaders and missions, and the log. {(open ? "Click or Esc to close." : "Click to open.")}");
    }

    static ImageTexture PixelTexture(string[] rows, Color color)
    {
        // made in code from the tone grid, never loaded from a file (Paul 2026-11-18); tone 2 is the colour itself
        var ramp = PixelIcons.GreyRamp(color);
        var img = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                if (rows[y][x] is >= '1' and <= '4' and var t) img.SetPixel(x, y, ramp[t - '0']);
        return ImageTexture.CreateFromImage(img);
    }

    static TextureRect PixelIcon(string[] rows, Color color)
    {
        return new TextureRect
        {
            Texture = PixelTexture(rows, color),
            TextureFilter = TextureFilterEnum.Nearest,   // square pixels, as drawn
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
    }

    /// <summary>The search field in the bar's pixel words: no box of its own, the cell is its box.</summary>
    static void StyleSearch(LineEdit e)
    {
        e.PlaceholderText = "Search";
        e.TooltipText = Ui.Wrap("Find a system, one of your fleets or one of your leaders (key /). Enter picks the first. While a fleet is being moved, picking a system sends it there.");
        e.CustomMinimumSize = Vector2.Zero;
        e.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        e.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        e.AddThemeConstantOverride("minimum_character_width", 2);
        foreach (var st in new[] { "normal", "focus", "read_only" }) e.AddThemeStyleboxOverride(st, new StyleBoxEmpty());
        e.AddThemeFontOverride("font", BarFont);
        e.AddThemeFontSizeOverride("font_size", BarTextSize);
        e.AddThemeColorOverride("font_color", BarValue);
        e.AddThemeColorOverride("font_placeholder_color", BarLabel);
        e.AddThemeColorOverride("caret_color", BarValue);
        e.AddThemeColorOverride("selection_color", new Color(BarLabel, 0.35f));
        e.AddThemeIconOverride("clear", PixelTexture(ClearIcon, Colors.White));
        e.AddThemeColorOverride("clear_button_color", BarLabel);
        e.AddThemeColorOverride("clear_button_color_pressed", BarValue);
    }

    // ================================================================== placing

    /// <summary>
    /// The frame is 1440 px wide, the game's own width. A larger interface size (Settings) leaves fewer px: then the search narrows first
    /// (down to 150 px), then the stocks drop their names and keep their icons (their tooltips still name them), then the cells lose half
    /// their padding. DAY stays: its tooltip is the only place that says which side you play and against what level. Once the search
    /// narrows it takes all the room left, and the stocks' opening line closes it: a sliver of strip between two lines read as an empty
    /// cell (review 24).
    /// </summary>
    void PlaceBar() => PlaceBarIn(_barLayer?.Size.X ?? 0);

    void PlaceBarIn(float screenWidth)
    {
        if (_barLayer == null) return;
        float room = screenWidth - BarMinGap;
        _searchEnd.Visible = true;
        for (int level = 0; level <= 2; level++)
        {
            SetBarLevel(level);
            SearchWidth = SearchWide;
            float over = BarOver(room);
            if (over <= 0) break;
            if (over <= SearchWide - SearchNarrow || level == 2)
            {
                // the search reaches the stocks' line; whole pixels, measured again: the containers round their sizes up
                _searchEnd.Visible = false;
                SearchWidth = Math.Max(SearchNarrow, SearchWide - Mathf.Ceil(BarOver(screenWidth)));
                if (BarOver(screenWidth) is float left && left > 0) SearchWidth = Math.Max(SearchNarrow, SearchWidth - Mathf.Ceil(left));
                break;
            }
        }
        PlaceDrops();
    }

    float BarOver(float room) => _barLeft.GetCombinedMinimumSize().X + _barRight.GetCombinedMinimumSize().X - room;

    int _barLevel = -1;

    void SetBarLevel(int level)
    {
        if (_barLevel == level) return;
        _barLevel = level;
        _rawCaption.Visible = _refinedCaption.Visible = _upkeepCaption.Visible = level < 1;
        // the frame's widths while the names show; without them each stock takes what its words need
        _rawCell.CustomMinimumSize = new Vector2(level < 1 ? RawW : 0, 0);
        _refinedCell.CustomMinimumSize = new Vector2(level < 1 ? RefinedW : 0, 0);
        _upkeepCell.CustomMinimumSize = new Vector2(level < 1 ? UpkeepW : 0, 0);
        _dayWords.CustomMinimumSize = new Vector2(level < 2 ? DayW : 0, 0);
        foreach (var c in _barTextCells) c.AddThemeStyleboxOverride("panel", TextCellBox(level < 2 ? BarPad : BarPad / 2));
        SetChipPad(_goalButton, level < 2 ? ChipPad : ChipPad / 2);
        PadLayers(level < 2 ? ChipPad : ChipPad / 2);
        FitGoalButton();
    }

    /// <summary>The search results and the layers list hang under their chips; the map's layer key stays at the top left, under the menu.</summary>
    void PlaceDrops()
    {
        if (_barLayer == null) return;
        var origin = _barLayer.GlobalPosition;
        Hang(_searchResults, _searchBox.GlobalPosition.X - origin.X + BarInset, Math.Max(SearchResultsWidth, SearchWidth));
        Hang(_layersCard, _layersBox.GlobalPosition.X - origin.X + BarInset, _layersBox.Size.X - 2 * BarInset);
    }

    /// <summary>Puts a drop-down under its chip, at least as wide. Zero-sized offsets: it keeps exactly the size its contents need.</summary>
    static void Hang(Control c, float x, float width)
    {
        if (c == null) return;
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.CustomMinimumSize = new Vector2(width, c.CustomMinimumSize.Y);
        c.OffsetLeft = c.OffsetRight = Mathf.Round(x);
        c.OffsetTop = c.OffsetBottom = BarBoxH + Ui.Gap;
    }
}
