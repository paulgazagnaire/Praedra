using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Everything that sits over the screen and stops the clock: the battle and invasion reports, the "are you sure" question, How to play, the
/// goal, saving and loading (six slots and the autosave), the settings, the end screen; and the Menu's drop-down list.
/// </summary>
public partial class GameScreen
{
    Control _modalLayer;
    CenterContainer _modalCenter;
    readonly List<Control> _modalStack = new();
    PanelContainer _reportPanel, _confirmPanel, _howPanel, _victoryPanel, _goalPanel, _savePanel, _settingsPanel;
    Label _reportTitle, _reportBody, _confirmTitle, _confirmBody;
    Button _confirmOk;
    Action _confirmAction;
    Message _reportMessage;
    PanelContainer _menuPopup;
    Control _menuCatcher;
    int _lastSlot = 1;

    void BuildModals()
    {
        // the Menu drop-down, with a catcher behind it that closes it on a click elsewhere
        _menuCatcher = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _menuCatcher.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _menuCatcher.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) ToggleMenu(); };
        AddChild(_menuCatcher);
        _menuPopup = Ui.Card(Ui.Chrome, Ui.LineHi, 8, 8);
        _menuPopup.Visible = false;
        _menuPopup.CustomMinimumSize = new Vector2(250, 0);
        AddChild(_menuPopup);
        var mv = Ui.VBox(2);
        _menuPopup.AddChild(mv);
        foreach (var (text, act, tip) in new (string, Action, string)[]
        {
            ("Save game...", () => ShowSaves(true), "Six save slots. Ctrl/Cmd+S saves to the last slot you used."),
            ("Load game...", () => ShowSaves(false), "Load one of your saves, or the autosave (every 30 days)."),
            ("Goal...", ShowGoal, "How you win, and how close each side is (G)"),
            ("Settings...", ShowSettings, "Text and panel size, and which events stop the clock"),
            ("How to play", ShowHowTo, "Short instructions (F1)"),
            ("Back to the start screen...", AskMenu, "Leaves this game (asks first)"),
        })
        {
            var b = Ui.Button(text, () => { ToggleMenu(); act(); }, tip, "Flat");
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(0, 34);
            mv.AddChild(b);
        }

        // the dialogs, over a dark veil
        _modalLayer = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _modalLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_modalLayer);
        var veil = new ColorRect { Color = new Color(0.01f, 0.015f, 0.03f, 0.66f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _modalLayer.AddChild(veil);
        _modalCenter = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        _modalCenter.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _modalLayer.AddChild(_modalCenter);

        // report
        _reportPanel = NewModal(600, Ui.LineHi);
        var rv = ModalBox(_reportPanel);
        _reportTitle = Ui.Label("", "Title");
        rv.AddChild(_reportTitle);
        var rs = new ScrollContainer { CustomMinimumSize = new Vector2(560, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _reportBody = Ui.Label("", null, Ui.Text2);
        _reportBody.CustomMinimumSize = new Vector2(550, 0);
        rs.AddChild(_reportBody);
        rv.AddChild(rs);
        var rb = Ui.HBox(8);
        rb.AddChild(Ui.Button("Go there", () => { if (_reportMessage?.System is string sys) { HideModal(_reportPanel); GoTo(sys, true); } }));
        rb.AddChild(Ui.Grow());
        var close = Ui.Button("Close (Esc)", () => HideModal(_reportPanel), null, "Primary");
        close.CustomMinimumSize = new Vector2(140, 36);
        rb.AddChild(close);
        rv.AddChild(rb);

        // confirm
        _confirmPanel = NewModal(480, Ui.Warn);
        var cv = ModalBox(_confirmPanel);
        _confirmTitle = Ui.Label("", "Title");
        cv.AddChild(_confirmTitle);
        _confirmBody = Ui.Label("", null, Ui.Text2);
        _confirmBody.CustomMinimumSize = new Vector2(440, 0);
        cv.AddChild(_confirmBody);
        var crow = Ui.HBox(8);
        cv.AddChild(crow);
        var cancel = Ui.Button("Cancel", CloseConfirm);
        cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill; cancel.CustomMinimumSize = new Vector2(0, 40);
        crow.AddChild(cancel);
        _confirmOk = Ui.Button("OK", () => { var a = _confirmAction; CloseConfirm(); a?.Invoke(); }, null, "Primary");
        _confirmOk.SizeFlagsHorizontal = SizeFlags.ExpandFill; _confirmOk.CustomMinimumSize = new Vector2(0, 40);
        crow.AddChild(_confirmOk);

        BuildHowTo();
        _goalPanel = NewModal(640, Ui.Gold);
        _savePanel = NewModal(560, Ui.LineHi);
        _settingsPanel = NewModal(520, Ui.LineHi);
        _victoryPanel = NewModal(560, Ui.Gold);
    }

    PanelContainer NewModal(int width, Color accent)
    {
        var p = Ui.Card(Ui.Chrome, accent, 22, 10);
        p.Visible = false;
        p.CustomMinimumSize = new Vector2(width, 0);
        p.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        p.MouseFilter = MouseFilterEnum.Stop;
        _modalCenter.AddChild(p);
        return p;
    }

    static VBoxContainer ModalBox(PanelContainer p)
    {
        Ui.Clear(p);
        var v = Ui.VBox(12);
        p.AddChild(v);
        return v;
    }

    void ShowModal(Control p)
    {
        _modalStack.Remove(p);
        _modalStack.Add(p);
        p.Visible = true;
        _modalCenter.MoveChild(p, _modalCenter.GetChildCount() - 1);
        _modalLayer.Visible = true;
        _menuPopup.Visible = false; _menuCatcher.Visible = false;
        if (_playing) { _playing = false; _runUntilNeeded = false; }
        RefreshTop();
    }

    void HideModal(Control p)
    {
        if (p == null) return;
        p.Visible = false;
        _modalStack.Remove(p);
        _modalLayer.Visible = _modalStack.Count > 0;
    }

    bool OverlayOpen => _modalStack.Count > 0;

    /// <summary>Esc: close the dialog on top. The end screen stays.</summary>
    bool CloseTopModal()
    {
        if (_modalStack.Count == 0) return false;
        var top = _modalStack[^1];
        if (top == _victoryPanel) return false;
        if (top == _howPanel) CloseHowTo();
        else if (top == _confirmPanel) CloseConfirm();
        else HideModal(top);
        return true;
    }

    void ToggleMenu()
    {
        bool show = !_menuPopup.Visible;
        _menuPopup.Visible = show; _menuCatcher.Visible = show;
        if (!show) return;
        var r = _menuButton.GetGlobalRect();
        _menuPopup.ResetSize();
        _menuPopup.Position = new Vector2(Math.Max(8, r.End.X - _menuPopup.GetCombinedMinimumSize().X), r.End.Y + 6);
        MoveChild(_menuCatcher, GetChildCount() - 1);
        MoveChild(_menuPopup, GetChildCount() - 1);
    }

    // ================================================================== confirm, report

    void Confirm(string title, string body, string okText, Action ok, bool danger = false)
    {
        _confirmTitle.Text = title; _confirmBody.Text = body; _confirmOk.Text = okText; _confirmAction = ok;
        _confirmOk.ThemeTypeVariation = danger ? "DangerButton" : "Primary";
        ShowModal(_confirmPanel);
    }

    void CloseConfirm() { HideModal(_confirmPanel); _confirmAction = null; }

    void ShowReport(Message m)
    {
        _reportMessage = m;
        _reportTitle.Text = m.Title ?? "Report";
        _reportBody.Text = m.Detail != null && m.Kind == MsgKind.Battle ? "Battle rules: " + GameBoot.RulesName(S.Rules) + "\n\n" + m.Detail : m.Detail ?? m.Text;
        _reportBody.GetParent<ScrollContainer>().CustomMinimumSize = new Vector2(560, Math.Min(420, 26 + 20 * _reportBody.Text.Count(c => c == '\n') + _reportBody.Text.Length / 3));
        ShowModal(_reportPanel);
        if (m.System != null && G.TryFindSystem(m.System, out int idx)) _view.Centre(idx);
    }

    void AskMenu() =>
        Confirm("Back to the start screen?", "The current game will be closed. Anything since your last save (or the autosave every 30 days) is lost.", "Yes, leave", () => BackToMenu?.Invoke());

    // ================================================================== how to play

    // The rules as the player meets them, in plain words. The win conditions are the core's (rule: m5-setup-and-victory §Victory).
    static readonly (string Head, string[] Lines)[] HowTo =
    {
        ("How you win", new[]
        {
            "THE HAND wins by taking the Collective headquarters, the Hearth, AND holding its two leaders, Nobody and Mae Abara, prisoner. First you must find the Hearth: explore Collective systems and send spies.",
            "THE COLLECTIVE wins by taking Charter, the Hand's capital, AND holding Cassia Ravilla and The Chair prisoner.",
            "A key leader who dies no longer counts. Either side also wins when the other holds no system, or by holding 70% of the systems the two sides hold for 180 days after the enemy capital fell. The Goal button (G) shows where you stand.",
        }),
        ("Time", new[]
        {
            "Press Play (Space) to run the clock, again to stop it. Keys 1-4 set the speed; N or +1 day moves one day. The clock stops by itself after a battle, a capture or an invasion (Settings can add more).",
            "Everything you order starts when the next day begins. The note at the top right counts the orders waiting.",
            "Needs you (Q) lists what wants a decision, worst first, each with its button. Run until something needs me plays until a new problem appears.",
        }),
        ("The screen", new[]
        {
            "The map fills the screen; blue is always you, orange the enemy, grey nobody, a hollow ring a place you never explored. The drawer on the right shows what you clicked (Selected) and your lists: Fleets (F), Leaders (C), Missions (M), Log (E).",
            "Map layers (V) colour the map for one question: support, trouble, idle yards, how old your picture is, slots. Sectors (S) opens tables of every system in a sector under the map. Search (/) finds systems, fleets and leaders. Legend explains the marks.",
            "Fog of war: you see live only the systems you hold or have a fleet at. Everything else shows what you saw last, with the day.",
        }),
        ("Fleets", new[]
        {
            "Click one of your fleets (blue triangle) or pick it in Fleets, press Move..., then click a destination. The bar at the bottom shows the days and the arrival day; a clean path is sent with the click.",
            "Ships fly anywhere, but routes are faster: thick lines are major routes (3x), thin ones minor (1.5x). If the path crosses a place where you saw an enemy interdictor, the bar turns red and offers Go anyway, Go around, or Cancel. Shift-click adds a waypoint, Backspace drops it.",
            "Battles happen by themselves when fleets of both sides meet. Your fleets fight, or retreat if you tick Retreat instead of fighting.",
        }),
        ("Taking a planet", new[]
        {
            "1) Select a fleet with troop room at one of your planets that has regiments, and press Load troops.",
            "2) Move it to the target. Any enemy fleet there must be beaten first. 3) Bombard to weaken the defenders if you like. 4) Press Invade: land more regiments than the defenders have.",
        }),
        ("Your planets", new[]
        {
            "Click one of your planets: Overview shows what its yards build and how long it takes; Build has Facilities, Ships, Fighters and Troops. Ships need a Shipyard, troops a Training facility, buildings a Construction yard and a free slot.",
            "MONEY: mines dig Raw, refineries turn it into Refined, which you spend. Upkeep is the running cost of everything you own; its limit grows with mines and refineries, and over it, units are scrapped.",
            "ORDER: a planet with low support needs regiments to stay calm. Too few and it revolts (red ring): its yards and mines stop. Needs you offers to train the missing ones.",
        }),
        ("Leaders and missions", new[]
        {
            "Leaders (C) lists your leaders with their skills. A fleet's Admiral boosts its bombardment, a fleet General its regiments in an invasion, a system's General the defenders there.",
            "Send a leader on a mission from Leaders or from the planet where they stand: tick the team, pick the kind and the target; the route and arrival day show first. Worth it now: Espionage (see what an enemy system holds) and Diplomacy (win a neutral system over). Sabotage is very hard.",
        }),
        ("Side-only", new[]
        {
            "THE COLLECTIVE can move the Hearth: click your HQ system, Move HQ..., then one of your systems. It cannot be attacked on the way; a big enemy blockade pins it.",
            "THE HAND can build a harvester at a shipyard (one per system). Its fleet's Destroy System wipes out the system it is at for good, and turns the sector against the Hand. The Collective stops it in battle or with a Spike the Harvester mission.",
        }),
        ("Saving", new[] { "Menu has six save slots and Load. Ctrl/Cmd+S saves to the last slot you used. The game also saves itself every 30 days; Continue on the start screen loads that." }),
    };

    void BuildHowTo()
    {
        _howPanel = NewModal(800, Ui.Gold);
        var v = ModalBox(_howPanel);
        var head = Ui.HBox(10);
        head.AddChild(Ui.Label("How to play", "Title", Ui.Gold, 28));
        head.AddChild(Ui.Grow());
        head.AddChild(Ui.Label("Battle rules: " + GameBoot.RulesName(S.Rules), "Hint", wrap: false));
        v.AddChild(head);
        var sc = new ScrollContainer { CustomMinimumSize = new Vector2(760, 470), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        v.AddChild(sc);
        var lines = Ui.VBox(6);
        lines.CustomMinimumSize = new Vector2(740, 0);
        sc.AddChild(lines);
        foreach (var (h, ls) in HowTo)
        {
            lines.AddChild(Ui.Space(4));
            lines.AddChild(Ui.Section(h));
            foreach (var l in ls) lines.AddChild(Ui.Label(l, null, Ui.Text2));
        }
        var ok = Ui.Button("Got it - let's play", CloseHowTo, null, "Primary");
        ok.CustomMinimumSize = new Vector2(0, 42);
        v.AddChild(ok);
    }

    void ShowHowTo() => ShowModal(_howPanel);

    void CloseHowTo()
    {
        HideModal(_howPanel);
        if (_showHowToOnStart) { _showHowToOnStart = false; GameBoot.MarkHowToSeen(); }
    }

    void ToggleHowTo() { if (_howPanel.Visible) CloseHowTo(); else ShowHowTo(); }

    // ================================================================== saving and loading (L1, L2)

    void ShowSaves(bool save)
    {
        var v = ModalBox(_savePanel);
        v.AddChild(Ui.Label(save ? "Save the game" : "Load a game", "Title"));
        v.AddChild(Ui.Label(save ? "Pick a slot. A used slot is overwritten (it asks first)." : $"Your current game (day {S.Game.Day}) is replaced by the one you load. It asks first.", "Hint"));
        void Row(string label, string path, bool isAuto)
        {
            string info = GameBoot.SlotInfo(path);
            var card = Ui.Card(Ui.Panel, Ui.Line, 12, 8);
            var h = Ui.HBox(10);
            card.AddChild(h);
            var tv = Ui.VBox(0);
            tv.AddChild(Ui.Label(label, "Strong", wrap: false));
            tv.AddChild(Ui.Label(info ?? "empty", "Hint", wrap: false));
            h.AddChild(tv);
            h.AddChild(Ui.Grow());
            string p = path;
            if (save)
            {
                int n = int.TryParse(label.Replace("Slot ", ""), out int k) ? k : 1;
                h.AddChild(Ui.Button($"Save here", () =>
                {
                    if (info == null) SaveTo(p, n);
                    else Confirm($"Overwrite {label}?", $"{label} holds: {info}. It will be replaced by this game (day {S.Game.Day}).", "Yes, overwrite", () => SaveTo(p, n));
                }, null, "Primary"));
            }
            else
            {
                var b = Ui.Button(isAuto ? "Load the autosave" : $"Load {label.ToLowerInvariant()}", () => Confirm($"Load {label.ToLowerInvariant()}?",
                    $"Your current game (day {S.Game.Day}) will be replaced by: {info}. Anything since your last save is lost.", "Yes, load it", () => LoadRequested?.Invoke(p)), null, "Primary", info == null);
                h.AddChild(b);
            }
            v.AddChild(card);
        }
        for (int i = 1; i <= GameBoot.Slots; i++) Row($"Slot {i}", GameBoot.SlotPath(i), false);
        if (!save) Row("Autosave", GameBoot.AutoSaveSlot, true);
        var close = Ui.Button("Close (Esc)", () => HideModal(_savePanel));
        close.CustomMinimumSize = new Vector2(0, 36);
        v.AddChild(close);
        ShowModal(_savePanel);
    }

    void SaveTo(string path, int slot)
    {
        try
        {
            GameBoot.WriteSave(path, S.SaveText(), S.Player, S.Level, S.Game.Day);
            _lastSlot = slot;
            HideModal(_savePanel);
            Toast($"Game saved in slot {slot} (day {S.Game.Day}).", MsgKind.Good);
        }
        catch (Exception e) { Toast("Save failed: " + e.Message, MsgKind.Bad); }
    }

    /// <summary>Ctrl/Cmd+S: save to the slot used last (slot 1 at first).</summary>
    public void QuickSave() => SaveTo(GameBoot.SlotPath(_lastSlot), _lastSlot);

    /// <summary>The old one-slot Save, kept for the screenshot helpers and the playtest.</summary>
    public void SaveGame() => QuickSave();

    // ================================================================== settings (G3, L4)

    void ShowSettings()
    {
        var v = ModalBox(_settingsPanel);
        v.AddChild(Ui.Label("Settings", "Title"));
        v.AddChild(Ui.Section("Size of text and panels"));
        var row = Ui.HBox(6);
        var group = new ButtonGroup();
        foreach (var sc in UiSettings.Scales)
        {
            float s = sc;
            row.AddChild(Ui.Chip($"{Mathf.RoundToInt(sc * 100)}%", () => { UiSettings.Scale = s; UiSettings.ApplyScale(GetTree().Root); UiSettings.Save(); }, null, Mathf.IsEqualApprox(sc, UiSettings.Scale), group));
        }
        v.AddChild(row);
        v.AddChild(Ui.Label("Everything grows together: the map, the text and the panels.", "Hint"));
        v.AddChild(Ui.Section("Stop the clock when"));
        void Check(string text, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Text = text, ButtonPressed = value, FocusMode = FocusModeEnum.None };
            cb.Toggled += on => { set(on); UiSettings.Save(); };
            v.AddChild(cb);
        }
        Check("a battle is fought (and show its report)", UiSettings.PauseBattle, on => UiSettings.PauseBattle = on);
        Check("a system changes hands", UiSettings.PauseCapture, on => UiSettings.PauseCapture = on);
        Check("an invasion takes place", UiSettings.PauseInvasion, on => UiSettings.PauseInvasion = on);
        Check("a harvester destroys a system", UiSettings.PauseHarvest, on => UiSettings.PauseHarvest = on);
        Check("an enemy fleet arrives at one of your systems", UiSettings.PauseEnemyAtMine, on => UiSettings.PauseEnemyAtMine = on);
        Check("an uprising starts on one of your systems", UiSettings.PauseUprising, on => UiSettings.PauseUprising = on);
        Check("a Sensor Station sees an enemy fleet coming", UiSettings.PauseSensors, on => UiSettings.PauseSensors = on);
        Check("a ship, troop or building is finished", UiSettings.PauseCompleted, on => UiSettings.PauseCompleted = on);
        Check("a mission succeeds or fails", UiSettings.PauseMission, on => UiSettings.PauseMission = on);
        var close = Ui.Button("Done (Esc)", () => HideModal(_settingsPanel), null, "Primary");
        close.CustomMinimumSize = new Vector2(0, 38);
        v.AddChild(close);
        ShowModal(_settingsPanel);
    }

    // ================================================================== the end

    void ShowVictory(Side winner)
    {
        var v = ModalBox(_victoryPanel);
        bool won = winner == S.Player;
        var t = Ui.Label(won ? "VICTORY" : "DEFEAT", "Title", won ? Ui.Gold : Ui.Danger, 54);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(t);
        var why = Ui.Label($"The {S.NameOf(winner)} has won the war, on day {S.Game.Day}. ({GameBoot.WinReason(S.Host)})", null, Ui.Text);
        why.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(why);
        int mine = S.Game.State.SystemOwners.Count(kv => kv.Value == S.Player);
        int fleets = S.Game.State.Fleets.Values.Count(f => f.Side == S.Player);
        int ships = S.Game.State.Fleets.Values.Where(f => f.Side == S.Player).Sum(f => f.Ships.Count);
        int battles = _allMessages.Count(m => m.Kind == MsgKind.Battle);
        var card = Ui.Card(Ui.Panel, Ui.Line, 14, 10);
        var cv = Ui.VBox(4);
        card.AddChild(cv);
        cv.AddChild(Ui.Row("Days of war", S.Game.Day.ToString()));
        cv.AddChild(Ui.Row("Systems you hold", mine.ToString()));
        cv.AddChild(Ui.Row("Your fleets", $"{fleets} ({ships} ships)"));
        cv.AddChild(Ui.Row("Battles in your log", battles.ToString()));
        v.AddChild(card);
        var row = Ui.HBox(8);
        var look = Ui.Button("Look at the map", () => HideModal(_victoryPanel));
        look.SizeFlagsHorizontal = SizeFlags.ExpandFill; look.CustomMinimumSize = new Vector2(0, 40);
        row.AddChild(look);
        var back = Ui.Button("Back to the start screen", () => BackToMenu?.Invoke(), null, "Primary");
        back.SizeFlagsHorizontal = SizeFlags.ExpandFill; back.CustomMinimumSize = new Vector2(0, 40);
        row.AddChild(back);
        v.AddChild(row);
        ShowModal(_victoryPanel);
    }
}
