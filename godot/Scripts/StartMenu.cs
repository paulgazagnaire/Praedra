using System;
using Godot;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>The start screen: choose a side and a difficulty, start a new game, load a save, quit.</summary>
public partial class StartMenu : Control
{
    public event Action<Side, Preset, Rebellion.Core.Battle.BattleRuleset> NewGameRequested;
    public event Action<string> LoadRequested;   // a save path
    public event Action QuitRequested;

    Side _side = Side.Alliance;
    Preset _preset = Preset.Medium;
    Rebellion.Core.Battle.BattleRuleset _rules = Rebellion.Core.Battle.BattleRuleset.New; // DEFAULT New (m4-battle-new; Paul 2026-10-18)
    Label _sideHint;

    static readonly string[] SideBlurb =
    {
        "The Galactic Empire: strong fleets, and a galaxy to keep in line. Crush the Rebellion.",
        "The Rebel Alliance: few ships, many friends. Win the people over and take down the Empire.",
    };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = new Color(0.03f, 0.04f, 0.08f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);

        var title = new Label { Text = "REBELLION", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 56);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.35f));
        box.AddChild(title);
        var sub = new Label { Text = "a galactic civil war", HorizontalAlignment = HorizontalAlignment.Center };
        sub.Modulate = new Color(1, 1, 1, 0.6f);
        box.AddChild(sub);
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 14) });

        box.AddChild(new Label { Text = "Your side" });
        var sideRow = new HBoxContainer();
        sideRow.AddThemeConstantOverride("separation", 8);
        box.AddChild(sideRow);
        var sideGroup = new ButtonGroup();
        AddToggle(sideRow, sideGroup, "Alliance", _side == Side.Alliance, () => SetSide(Side.Alliance));
        AddToggle(sideRow, sideGroup, "Empire", _side == Side.Empire, () => SetSide(Side.Empire));
        _sideHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 44) };
        _sideHint.Modulate = new Color(1, 1, 1, 0.7f);
        box.AddChild(_sideHint);
        SetSide(_side);

        box.AddChild(new Label { Text = "Difficulty" });
        var presetRow = new HBoxContainer();
        presetRow.AddThemeConstantOverride("separation", 8);
        box.AddChild(presetRow);
        var presetGroup = new ButtonGroup();
        AddToggle(presetRow, presetGroup, "Easy", false, () => _preset = Preset.Easy);
        AddToggle(presetRow, presetGroup, "Medium", true, () => _preset = Preset.Medium);
        AddToggle(presetRow, presetGroup, "Hard", false, () => _preset = Preset.Hard);

        box.AddChild(new Label { Text = "Space battle rules" });
        var rulesRow = new HBoxContainer();
        rulesRow.AddThemeConstantOverride("separation", 8);
        box.AddChild(rulesRow);
        var rulesGroup = new ButtonGroup();
        AddToggle(rulesRow, rulesGroup, GameBoot.RulesName(Rebellion.Core.Battle.BattleRuleset.Classic), false, () => _rules = Rebellion.Core.Battle.BattleRuleset.Classic);
        AddToggle(rulesRow, rulesGroup, GameBoot.RulesName(Rebellion.Core.Battle.BattleRuleset.New), true, () => _rules = Rebellion.Core.Battle.BattleRuleset.New);
        var rulesHint = new Label { Text = "Classic plays as the 1998 game did. New fights gun by gun: the weapons your ships carry decide who wins.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 44) };
        rulesHint.Modulate = new Color(1, 1, 1, 0.7f);
        box.AddChild(rulesHint);

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        var start = Big("New Game", () => NewGameRequested?.Invoke(_side, _preset, _rules));
        box.AddChild(start);

        var cont = Big("Continue (autosave)", () => LoadRequested?.Invoke(GameBoot.AutoSaveSlot));
        cont.Disabled = !GameBoot.SaveExists(GameBoot.AutoSaveSlot);
        box.AddChild(cont);
        var load = Big("Load saved game", () => LoadRequested?.Invoke(GameBoot.SaveSlot));
        load.Disabled = !GameBoot.SaveExists(GameBoot.SaveSlot);
        box.AddChild(load);
        box.AddChild(Big("Quit", () => QuitRequested?.Invoke()));

        if (GameBoot.PlaceholderMode)
        {
            var note = new Label
            {
                Text = "Test build: the computer opponent is not playing yet, so the other side sits still.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            note.Modulate = new Color(1f, 0.8f, 0.4f, 0.8f);
            box.AddChild(note);
        }
    }

    void SetSide(Side s)
    {
        _side = s;
        _sideHint.Text = SideBlurb[s == Side.Empire ? 0 : 1];
    }

    static void AddToggle(HBoxContainer row, ButtonGroup group, string text, bool pressed, Action on)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonGroup = group, ButtonPressed = pressed, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 38) };
        b.Pressed += on;
        row.AddChild(b);
    }

    static Button Big(string text, Action on)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 44) };
        b.Pressed += on;
        return b;
    }
}
