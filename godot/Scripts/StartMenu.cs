using System;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>The start screen: choose a side and a difficulty, start a new game, continue from the autosave, load a save slot, quit.</summary>
public partial class StartMenu : Control
{
    public event Action<Side, Preset, Praedra.Core.Battle.BattleRuleset> NewGameRequested;
    public event Action<string> LoadRequested;   // a save path
    public event Action QuitRequested;

    Side _side = Side.Hand;
    Preset _preset = Preset.Medium;
    Praedra.Core.Battle.BattleRuleset _rules = Praedra.Core.Battle.BattleRuleset.New; // DEFAULT New (m4-battle-new; Paul 2026-10-18)
    Label _sideHint;
    VBoxContainer _slots;

    static readonly string[] SideBlurb =
    {
        "The Hand: strong fleets, and a galaxy to keep in line. Find the Hearth and crush the Collective.",
        "The Collective: few ships, many friends. Win the people over and take down the Hand.",
    };

    public override void _Ready()
    {
        Theme = Ui.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = Ui.MapBg };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var card = Ui.Card(Ui.Chrome, Ui.Line, 28, 12);
        center.AddChild(card);
        var box = Ui.VBox(10);
        box.CustomMinimumSize = new Vector2(480, 0);
        card.AddChild(box);

        var title = Ui.Label("PRAEDRA", "Title", Ui.Gold, 60);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(title);
        var sub = Ui.Label("a galactic civil war", "Hint");
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(sub);
        box.AddChild(Ui.Space(8));

        box.AddChild(Ui.Section("Your side"));
        var sideRow = Ui.HBox(8);
        box.AddChild(sideRow);
        var sideGroup = new ButtonGroup();
        AddToggle(sideRow, sideGroup, "Hand", _side == Side.Hand, () => SetSide(Side.Hand));
        AddToggle(sideRow, sideGroup, "Collective", _side == Side.Collective, () => SetSide(Side.Collective));
        _sideHint = Ui.Label("", null, Ui.Text2, Ui.Small);
        _sideHint.CustomMinimumSize = new Vector2(460, 38);
        box.AddChild(_sideHint);
        SetSide(_side);

        box.AddChild(Ui.Section("Difficulty"));
        var presetRow = Ui.HBox(8);
        box.AddChild(presetRow);
        var presetGroup = new ButtonGroup();
        AddToggle(presetRow, presetGroup, "Easy", false, () => _preset = Preset.Easy);
        AddToggle(presetRow, presetGroup, "Medium", true, () => _preset = Preset.Medium);
        AddToggle(presetRow, presetGroup, "Hard", false, () => _preset = Preset.Hard);

        box.AddChild(Ui.Section("Space battle rules"));
        var rulesRow = Ui.HBox(8);
        box.AddChild(rulesRow);
        var rulesGroup = new ButtonGroup();
        AddToggle(rulesRow, rulesGroup, GameBoot.RulesName(Praedra.Core.Battle.BattleRuleset.Classic), false, () => _rules = Praedra.Core.Battle.BattleRuleset.Classic);
        AddToggle(rulesRow, rulesGroup, GameBoot.RulesName(Praedra.Core.Battle.BattleRuleset.New), true, () => _rules = Praedra.Core.Battle.BattleRuleset.New);
        var rulesHint = Ui.Label("Classic follows the base game's rules. New fights gun by gun: the weapons your ships carry decide who wins.", null, Ui.Text2, Ui.Small);
        rulesHint.CustomMinimumSize = new Vector2(460, 38);
        box.AddChild(rulesHint);

        box.AddChild(Ui.Space(6));
        box.AddChild(Big("New Game", () => NewGameRequested?.Invoke(_side, _preset, _rules), "Primary"));
        var cont = Big("Continue (autosave)", () => LoadRequested?.Invoke(GameBoot.AutoSaveSlot));
        cont.Disabled = !GameBoot.SaveExists(GameBoot.AutoSaveSlot);
        if (GameBoot.SlotInfo(GameBoot.AutoSaveSlot) is string auto) cont.TooltipText = Ui.Wrap(auto);
        box.AddChild(cont);
        bool any = false;
        for (int n = 1; n <= GameBoot.Slots; n++) any |= GameBoot.SaveExists(GameBoot.SlotPath(n));
        var load = Big("Load saved game", ToggleSlots);
        load.Disabled = !any;
        if (!any) load.TooltipText = "No saved games yet. In a game, press Ctrl+S or use Menu, Save game.";
        box.AddChild(load);
        _slots = Ui.VBox(2);
        _slots.Visible = false;
        box.AddChild(_slots);
        box.AddChild(Big("Quit", () => QuitRequested?.Invoke()));
    }

    void ToggleSlots()
    {
        _slots.Visible = !_slots.Visible;
        Ui.Clear(_slots);
        if (!_slots.Visible) return;
        for (int n = 1; n <= GameBoot.Slots; n++)
        {
            string path = GameBoot.SlotPath(n);
            if (GameBoot.SlotInfo(path) is not string info) continue;
            var b = Ui.Button($"Load slot {n}   {info}", () => LoadRequested?.Invoke(path), null, "Row");
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(0, 34);
            _slots.AddChild(b);
        }
    }

    void SetSide(Side s)
    {
        _side = s;
        _sideHint.Text = SideBlurb[s == Side.Hand ? 0 : 1];
    }

    static void AddToggle(HBoxContainer row, ButtonGroup group, string text, bool pressed, Action on)
    {
        var b = Ui.Chip(text, on, null, pressed, group);
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        b.CustomMinimumSize = new Vector2(0, 38);
        row.AddChild(b);
    }

    static Button Big(string text, Action on, string variation = null)
    {
        var b = Ui.Button(text, on, null, variation);
        b.CustomMinimumSize = new Vector2(0, 44);
        return b;
    }
}
