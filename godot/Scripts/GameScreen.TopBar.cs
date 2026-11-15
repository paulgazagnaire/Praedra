using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The top bar, after Paul's Paper frame "ui-update-nov-15" (2026-11-15): a dark strip across the top of the map with boxes hanging from
/// it. Left: the menu and the goal, then the search and the map layers. Right: Raw, Refined and Upkeep, then the day and the clock with
/// its speeds. Pixel text (BitPotion 24): grey labels, light values, on black cells. No dividers: the groups of boxes stand apart.
/// The bar has a layer of its own over the windows, so the search results and the layers list it opens are never hidden under a window;
/// the windows themselves start under it (TopClear) and never cover it.
/// </summary>
public partial class GameScreen
{
    // ---- the frame's colours
    static readonly Color BarBg = new("0b0b0d");       // the strip and the boxes
    static readonly Color BarEdge = new("181b21");     // their 1 px edge
    static readonly Color BarCellBg = new("000000");   // the cells inside the boxes
    static readonly Color BarLabel = new("84878e");    // labels: GOAL, Search, Layers, Raw, DAY, 1x
    static readonly Color BarValue = new("dfdfdf");    // values (0/3, 9999), and a switch that is on (the speed, Layers open, the clock running)
    static readonly Color BarMenu = new("b0b2b7");     // the menu's ≡, and a label under the mouse
    static readonly Color BarIcon = new("dddddd");     // the pixel icons of Raw, Refined and Upkeep

    // ---- the frame's sizes
    const int BarStrip = 32;      // the strip's height
    const int BarBoxH = 49;       // a box: 4 px above its cell, the 40 px cell, 4 px and the 1 px edge below
    const int BarCellH = 40;
    const int BarPad = 16;        // a text cell: 16 px each side of its words
    const int BarSide = 16;       // from the screen's left and right edges
    const int BarGroupGap = 32;   // between groups of boxes
    const int BarTextSize = 24;   // BitPotion 24: 1.5 px to a font pixel, 3 screen pixels on a Retina screen
    const int SearchWide = 320, SearchNarrow = 150;
    const int BarMinGap = 16;     // the least room left between the left and the right boxes
    const int SearchResultsWidth = 360;
    // the frame's widths of the stock and day boxes, made for 9999 and 9999/9999: the bar keeps them, so it does not shift as numbers grow
    const int RawBoxW = 120, RefinedBoxW = 144, UpkeepBoxW = 179, DayBoxW = 97;

    Control _barLayer;
    HBoxContainer _barLeft, _barRight;
    PanelContainer _barStrip, _searchBox, _layersBox, _dayBox, _rawCell, _refinedCell, _upkeepCell;
    Label _goalCaption, _goalValue, _rawCaption, _refinedCaption;
    readonly List<PanelContainer> _barTextCells = new();
    static Font _barFont;
    static Font BarFont => _barFont ??= Ui.Pixel ?? ThemeDB.FallbackFont;

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
        _barStrip.OffsetBottom = BarStrip;
        _barLayer.AddChild(_barStrip);

        _barLeft = Ui.HBox(BarGroupGap);
        _barLeft.MouseFilter = MouseFilterEnum.Ignore;
        _barLeft.SetAnchorsPreset(LayoutPreset.TopLeft);
        _barLeft.OffsetLeft = _barLeft.OffsetRight = BarSide;
        _barLayer.AddChild(_barLeft);
        _barRight = Ui.HBox(BarGroupGap);
        _barRight.MouseFilter = MouseFilterEnum.Ignore;
        _barRight.SetAnchorsPreset(LayoutPreset.TopRight);
        _barRight.GrowHorizontal = GrowDirection.Begin;
        _barRight.OffsetLeft = _barRight.OffsetRight = -BarSide;
        _barLayer.AddChild(_barRight);

        // ---- the menu and the goal
        var g = BarGroup(_barLeft);
        _menuButton = BarButton("≡", ToggleMenu, "Menu: fleets, leaders, missions, goal, sectors, save, load, settings (Esc closes)", Ui.MonoMedium, 22, BarMenu, 0);
        _menuButton.SetMeta(UiTest.FullName, "Menu");
        _menuButton.CustomMinimumSize = new Vector2(BarCellH, BarCellH);
        g.AddChild(BarBox(_menuButton));
        _goalButton = BarButton("", ShowGoal, "How you win, and how close each side is (G).", BarFont, BarTextSize, BarLabel, BarPad);
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
        g.AddChild(BarBox(_goalButton));

        // ---- the search and the map layers
        g = BarGroup(_barLeft);
        var sc = BarCell(BarPad);
        sc.MouseDefaultCursorShape = CursorShape.Ibeam;
        sc.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) FocusSearch(); };
        var sh = Ui.HBox(4);
        sh.MouseFilter = MouseFilterEnum.Ignore;
        sc.AddChild(sh);
        BuildSearch(sh);
        StyleSearch(_search);
        var lens = PixelIcon(MagnifierIcon, BarLabel);
        lens.MouseFilter = MouseFilterEnum.Stop;
        lens.TooltipText = _search.TooltipText;
        lens.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) FocusSearch(); };
        sh.AddChild(lens);
        g.SortChildren += PlaceDrops;   // the layers box moves when the search narrows
        g.AddChild(_searchBox = BarBox(sc));
        _searchBox.CustomMinimumSize = new Vector2(SearchWide, BarBoxH);
        _layersButton = BarButton("Layers", ToggleLayers, "Show or hide the map layers (V cycles them)", BarFont, BarTextSize, BarLabel, BarPad);
        _layersButton.ToggleMode = true;
        g.AddChild(_layersBox = BarBox(_layersButton));

        // ---- your stocks
        g = BarGroup(_barRight);
        (_rawCell, _rawCaption, _rawLabel) = StockCell(g, RawIcon, "Raw", RawTip);
        (_refinedCell, _refinedCaption, _refinedLabel) = StockCell(g, RefinedIcon, "Refined", RefinedTip);
        (_upkeepCell, _upkeepCaption, _upkeepLabel) = StockCell(g, UpkeepIcon, "Upkeep", "");

        // ---- the day, the clock and its speeds
        g = BarGroup(_barRight);
        var dc = BarCell(BarPad);
        var dh = Ui.HBox(4);
        dh.MouseFilter = MouseFilterEnum.Ignore;
        dh.Alignment = BoxContainer.AlignmentMode.Center;
        dc.AddChild(dh);
        _sideLabel = BarText("DAY", BarLabel);
        _sideLabel.MouseFilter = MouseFilterEnum.Stop;   // its tooltip says which side you play
        dh.AddChild(_sideLabel);
        _dayLabel = BarText("0", BarValue);
        _dayLabel.MouseFilter = MouseFilterEnum.Stop;
        _dayLabel.TooltipText = Ui.Wrap("The game day. Each day every order you gave is carried out, fleets fly, yards build. Press Play to let the days run.");
        dh.AddChild(_dayLabel);
        _barTextCells.Add(dc);
        g.AddChild(_dayBox = BarBox(dc));
        var clock = Ui.HBox(2);
        // one button that plays and pauses (Paul 2026-11-14, impeccable critique): it shows what a click does, and is lit while the clock
        // runs; one day at a time is the N key
        _playButton = BarButton("▶", TogglePlay, "Play or pause (Space); N moves one day. The clock stops by itself after a battle, a capture or an invasion.", Ui.Mono, 16, BarLabel, 0);
        _playButton.CustomMinimumSize = new Vector2(BarCellH, BarCellH);
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
        g.AddChild(BarBox(clock));

        // ---- what the boxes open: the search results and the layers list hang under their boxes, over the windows
        BuildSearchResults(_barLayer);
        _searchResults.AddThemeStyleboxOverride("panel", BarDropBox(6, 8));
        BuildMapTools();
        _layersCard.AddThemeStyleboxOverride("panel", BarDropBox(4, 4));
        _barLayer.AddChild(_layersCard);

        // placed again when the screen changes size, and when the words change width (a number grows, the goal turns into a countdown);
        // that signal comes after the change, deferred, and the placing gives the same answer for the same words, so it settles
        _barLayer.Resized += PlaceBar;
        _barLeft.MinimumSizeChanged += PlaceBar;
        _barRight.MinimumSizeChanged += PlaceBar;
        _barLeft.SortChildren += PlaceDrops;
        _searchResults.VisibilityChanged += PlaceDrops;
        _layersCard.VisibilityChanged += PlaceDrops;
        _searchResults.Resized += PlaceNotes;   // more or fewer results
        _layersCard.Resized += PlaceNotes;
        return _barLayer;
    }

    // ================================================================== the pieces

    /// <summary>A group of boxes that touch: their 1 px edges overlap, as in the frame.</summary>
    static HBoxContainer BarGroup(HBoxContainer into)
    {
        var g = Ui.HBox(-1);
        g.MouseFilter = MouseFilterEnum.Ignore;
        into.AddChild(g);
        return g;
    }

    /// <summary>A box hanging from the top: the strip's colour, a 1 px edge left, right and below, 4 px round its cell.</summary>
    static PanelContainer BarBox(Control content)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var s = new StyleBoxFlat { BgColor = BarBg, BorderColor = BarEdge, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthBottom = 1 };
        s.ContentMarginLeft = 5; s.ContentMarginRight = 5; s.ContentMarginTop = 4; s.ContentMarginBottom = 5;
        p.AddThemeStyleboxOverride("panel", s);
        p.CustomMinimumSize = new Vector2(0, BarBoxH);
        p.AddChild(content);
        return p;
    }

    /// <summary>The look of what a box opens under it (search results, the layers list): the box's colours, edged all round.</summary>
    static StyleBoxFlat BarDropBox(int padX, int padY)
    {
        var s = new StyleBoxFlat { BgColor = BarBg, BorderColor = BarEdge };
        s.SetBorderWidthAll(1);
        s.ContentMarginLeft = padX; s.ContentMarginRight = padX; s.ContentMarginTop = padY; s.ContentMarginBottom = padY;
        return s;
    }

    static StyleBoxFlat BarCellBox(int padX)
    {
        var s = new StyleBoxFlat { BgColor = BarCellBg };
        s.ContentMarginLeft = padX; s.ContentMarginRight = padX; s.ContentMarginTop = 0; s.ContentMarginBottom = 0;
        return s;
    }

    /// <summary>A black cell that shows words (the stocks, the day, the search).</summary>
    static PanelContainer BarCell(int padX)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0, BarCellH) };
        p.AddThemeStyleboxOverride("panel", BarCellBox(padX));
        return p;
    }

    /// <summary>A black cell you click. The words are grey, lighter under the mouse, and light while the switch is on.</summary>
    Button BarButton(string text, Action onPressed, string tip, Font font, int size, Color color, int padX)
    {
        var b = Ui.Button(text, onPressed, tip);
        var cell = BarCellBox(padX);
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) b.AddThemeStyleboxOverride(st, cell);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeFontOverride("font", font);
        b.AddThemeFontSizeOverride("font_size", size);
        SetBarButtonColour(b, color);
        b.CustomMinimumSize = new Vector2(0, BarCellH);
        return b;
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

    /// <summary>One of your stocks: its pixel icon, its name and its number. The tooltip says what it is.</summary>
    (PanelContainer Cell, Label Caption, Label Value) StockCell(HBoxContainer group, string[] icon, string caption, string tip)
    {
        var c = BarCell(BarPad);
        c.TooltipText = Ui.Wrap(tip);
        var h = Ui.HBox(4);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.Alignment = BoxContainer.AlignmentMode.Center;
        c.AddChild(h);
        h.AddChild(PixelIcon(icon, BarIcon));
        var cap = BarText(caption, BarLabel);
        h.AddChild(cap);
        var v = BarText("0", BarValue);
        h.AddChild(v);
        _barTextCells.Add(c);
        group.AddChild(BarBox(c));
        return (c, cap, v);
    }

    /// <summary>A stock's tooltip, on its cell and its number.</summary>
    static void SetStockTip(PanelContainer cell, Label value, string tip)
    {
        cell.TooltipText = Ui.Wrap(tip);
        value.TooltipText = cell.TooltipText;
    }

    // ---- the frame's 16 px pixel icons, drawn one square per pixel ('#')

    static readonly string[] RawIcon =
    {
        "                ", "                ", "      ##        ", "     #  ##      ", "    #     ##    ", "   #        #   ", "   #     # #    ", "  #  #  #   #   ",
        "   #  ##   #    ", "    #    ##     ", "    #   #       ", "     # #        ", "      #         ", "                ", "                ", "                ",
    };
    static readonly string[] RefinedIcon =
    {
        "                ", "                ", "        ##      ", "       #  #     ", "      #    #    ", "     #      #   ", "    #      # #  ", "   #      #  #  ",
        "  # #    #   #  ", "  #     #   #   ", "  #   ##   #    ", "   #   #  #     ", "    #    #      ", "     #  #       ", "      ##        ", "                ",
    };
    static readonly string[] UpkeepIcon =
    {
        "                ", "                ", "         ##     ", "        #       ", "        # #  #  ", "        # ## #  ", "       #     #  ", "      #   ###   ",
        "   ###   #      ", "  #     #       ", "  # # ##        ", "     # #        ", "       #        ", "     ##         ", "                ", "                ",
    };
    static readonly string[] MagnifierIcon =
    {
        "                ", "                ", "                ", "     ###        ", "    #   #       ", "   #     #      ", "  #       #     ", "  #       #     ",
        "  #       #     ", "   #     #      ", "    #   # #     ", "     ###   #    ", "            #   ", "             #  ", "                ", "                ",
    };

    /// <summary>The search's clear button: a pixel cross in the icons' style (the theme's own cross is a heavy one), white so the
    /// field's clear-button colours tint it.</summary>
    static readonly string[] ClearIcon =
    {
        "                ", "                ", "                ", "                ", "    #      #    ", "     #    #     ", "      #  #      ", "       ##       ",
        "       ##       ", "      #  #      ", "     #    #     ", "    #      #    ", "                ", "                ", "                ", "                ",
    };

    static ImageTexture PixelTexture(string[] rows, Color color)
    {
        var img = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                if (rows[y][x] == '#') img.SetPixel(x, y, color);
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
    /// their padding. DAY stays: its tooltip is the only place that says which side you play and against what level.
    /// </summary>
    void PlaceBar() => PlaceBarIn(_barLayer?.Size.X ?? 0);

    void PlaceBarIn(float screenWidth)
    {
        if (_barLayer == null) return;
        float room = screenWidth - 2 * BarSide - BarMinGap;
        for (int level = 0; level <= 2; level++)
        {
            SetBarLevel(level);
            _searchBox.CustomMinimumSize = new Vector2(SearchWide, BarBoxH);
            float over = BarOver(room);
            if (over <= 0) break;
            if (over <= SearchWide - SearchNarrow || level == 2)
            {
                // whole pixels, measured again: the containers round their sizes up
                _searchBox.CustomMinimumSize = new Vector2(Math.Max(SearchNarrow, SearchWide - Mathf.Ceil(over)), BarBoxH);
                if (BarOver(room) is float left && left > 0)
                    _searchBox.CustomMinimumSize = new Vector2(Math.Max(SearchNarrow, _searchBox.CustomMinimumSize.X - Mathf.Ceil(left)), BarBoxH);
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
        int pad = level < 2 ? BarPad : BarPad / 2;
        // the frame's widths while the names show; without them each box takes what its words need
        SetBoxWidth(_rawCell, level < 1 ? RawBoxW : 0);
        SetBoxWidth(_refinedCell, level < 1 ? RefinedBoxW : 0);
        SetBoxWidth(_upkeepCell, level < 1 ? UpkeepBoxW : 0);
        SetBoxWidth(_dayBox, level < 2 ? DayBoxW : 0);
        foreach (var c in _barTextCells) c.AddThemeStyleboxOverride("panel", BarCellBox(pad));
        foreach (var b in new[] { _goalButton, _layersButton })
            foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" }) b.AddThemeStyleboxOverride(st, BarCellBox(pad));
        FitGoalButton();
    }

    /// <summary>Sets a box's least width; given a cell, its box's.</summary>
    static void SetBoxWidth(PanelContainer cellOrBox, int width)
    {
        var box = cellOrBox.GetParent() is PanelContainer outer ? outer : cellOrBox;
        box.CustomMinimumSize = new Vector2(width, BarBoxH);
    }

    /// <summary>The search results and the layers list hang under their boxes; the map's layer key stays at the top left, under the menu.</summary>
    void PlaceDrops()
    {
        if (_barLayer == null) return;
        var origin = _barLayer.GlobalPosition;
        Hang(_searchResults, _searchBox.GlobalPosition.X - origin.X, Math.Max(SearchResultsWidth, _searchBox.Size.X));
        Hang(_layersCard, _layersBox.GlobalPosition.X - origin.X, _layersBox.Size.X);
        PlaceNotes();   // the notes go below an open drop-down
    }

    /// <summary>Puts a drop-down under its box, at least as wide. Zero-sized offsets: it keeps exactly the size its contents need.</summary>
    static void Hang(Control c, float x, float width)
    {
        if (c == null) return;
        c.SetAnchorsPreset(LayoutPreset.TopLeft);
        c.CustomMinimumSize = new Vector2(width, c.CustomMinimumSize.Y);
        c.OffsetLeft = c.OffsetRight = Mathf.Round(x);
        c.OffsetTop = c.OffsetBottom = BarBoxH + Ui.Gap;
    }
}
