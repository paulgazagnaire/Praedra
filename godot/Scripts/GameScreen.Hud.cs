using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Two of the floating pieces of Paul's mockup (2026-10-28). Bottom centre, the selection card: what you clicked in one glance (owner,
/// support, troops, fleets; for a fleet where it is and where it goes), with its main action and Details, which opens the window.
/// Bottom right, the message corner: one short card with the latest message. The kind strip that was over it (BAT FLT SYS PRD MSN) is
/// gone (Paul 2026-11-14, impeccable critique): the whole log, sorted by kind, is one key away (E, or Log in the menu).
/// </summary>
public partial class GameScreen
{
    // ================================================================== the selection card

    PanelContainer _selCard;
    Label _selName, _selTag;
    HBoxContainer _selFacts, _selActions;

    void BuildSelectionCard()
    {
        _selCard = Ui.Hud(Ui.Pad, 10);
        _selCard.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _selCard.GrowHorizontal = GrowDirection.Both; _selCard.GrowVertical = GrowDirection.Begin;
        _selCard.OffsetBottom = -Edge; _selCard.OffsetTop = -Edge;
        _selCard.CustomMinimumSize = new Vector2(420, 0);
        _selCard.Visible = false;
        _mapHolder.AddChild(_selCard);
        var v = Ui.VBox(Ui.Block);
        _selCard.AddChild(v);
        var head = Ui.HBox(Ui.Block);
        _selName = Ui.Label("", "Title", Ui.Text, 20, wrap: false);
        _selName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_selName);
        _selTag = Ui.Label("", "StatLabel", wrap: false);
        _selTag.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(_selTag);
        v.AddChild(head);
        _selFacts = Ui.HBox(Ui.Group);
        v.AddChild(_selFacts);
        _selActions = Ui.HBox(Ui.Block);
        v.AddChild(_selActions);
    }

    /// <summary>The card shows when something is selected, the window is closed and no move is being planned.</summary>
    bool SelectionCardWanted => !_drawer.Visible && (_view.Selected >= 0 || OwnSelectedFleet() != null);

    Fleet OwnSelectedFleet() =>
        _view.SelectedFleet >= 0 && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var f) && f.Side == Me ? f : null;

    void RefreshSelectionCard()
    {
        if (_selCard == null) return;
        _selCard.Visible = SelectionCardWanted && !InMoveMode;
        if (!_selCard.Visible) return;
        Ui.Clear(_selFacts);
        Ui.Clear(_selActions);
        if (OwnSelectedFleet() is { } f) FleetCard(f);
        else SystemCard(_view.Selected);
    }

    void Fact(string caption, string value, Color? color = null)
    {
        var box = Ui.VBox(1);
        box.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        box.AddChild(Ui.Label(caption, "StatLabel", wrap: false));
        var l = Ui.Label(value, null, color ?? Ui.Text, Ui.Small, wrap: false);
        l.AddThemeFontOverride("font", Ui.Mono);
        box.AddChild(l);
        _selFacts.AddChild(box);
    }

    /// <summary>A button of the card. Its full name starts with "Card:" so the playtest never takes it for the window's button of the same words.</summary>
    Button CardButton(string text, Action act, string tip, string variation = null)
    {
        var b = Ui.Button(text, act, tip, variation ?? "Chip");
        b.SetMeta(UiTest.FullName, "Card: " + text);
        _selActions.AddChild(b);
        return b;
    }

    void Tag(string text, Color color)
    {
        _selTag.Text = text;
        _selTag.AddThemeColorOverride("font_color", color);
    }

    void SystemCard(int idx)
    {
        string name = G.Systems[idx].Name;
        _selName.Text = name;
        var row = SectorRow(idx);
        bool mine = S.OwnerOf(name) == Me;
        if (S.IsDestroyed(name)) Tag("DESTROYED", Ui.Danger);
        else if (name == S.MyHqSystem) Tag(Me == Side.Hand ? "YOUR CAPITAL" : "YOUR HQ", Ui.Gold);
        else if (name == S.EnemyHqSystem) Tag(Me == Side.Hand ? "COLLECTIVE HQ" : "ENEMY CAPITAL", Ui.Enemy);   // the drawer's words
        else if (mine) Tag("YOURS", Ui.You);
        else if (S.Picture(name) == null) Tag("UNEXPLORED", Ui.Text3);
        else if (S.Sees(name)) Tag("SEEN NOW", Ui.Text2);
        else Tag(S.LastSeenDay(name) is int d ? $"LAST SEEN D{d}" : "SEEN LONG AGO", Ui.Warn);

        string owner = row.Owner is Side o ? (o == Me ? $"{S.NameOf(o)} (you)" : S.NameOf(o)) : S.Picture(name) == null ? "?" : row.Known ? "nobody" : "?";
        Fact("OWNER", owner, row.Owner is Side so ? Ui.OwnerColor(so, Me) : Ui.Text2);
        Fact("SUPPORT", row.Support, row.SupportColor);
        Fact(mine ? "TROOPS" : "GARRISON", row.Troops, row.TroopsColor);
        int ownFleets = S.Game.State.Fleets.Values.Count(x => x.Side == Me && x.At == name);
        int enemy = S.Sees(name) ? S.VisibleEnemyFleetsAt(name).Count : 0;
        Fact("FLEETS", ownFleets + enemy == 0 ? "none" : string.Join(", ", new[] { ownFleets > 0 ? $"{ownFleets} yours" : null, enemy > 0 ? $"{enemy} enemy" : null }.Where(x => x != null)),
            enemy > 0 ? Ui.Enemy : null);

        // the management windows open from any system, with or without its sector window (Paul 2026-11-02: "not tied to a sector")
        if (!S.IsDestroyed(name))
            CardButton("Ground", () => OpenMgmt(MgKind.Ground, name), "Leaders, regiments, fighters and agents here: drag them between windows");
        if (mine && !S.IsDestroyed(name))
            CardButton("Infra", () => OpenMgmt(MgKind.Infra, name), "Buildings, slots and what this planet builds");
        if (ownFleets > 0)
            CardButton("Fleets", () => OpenFromTile(name, "fleets"), "Open the windows of your fleets here");
        _selActions.AddChild(Ui.Grow());
        CardButton("Details →", () => { _sysTab = SysTab.Overview; ShowTab(DrawerTab.Selected); }, "Everything about this system, in the window");
    }

    void FleetCard(Fleet f)
    {
        _selName.Text = f.Name;
        Tag("YOUR FLEET", Ui.You);
        if (f.At != null) Fact("AT", f.At);
        if (f.Transit is { } t && f.At == null) { Fact("TO", t.Path[^1]); Fact("ARRIVES", $"day {GameSession.ArrivalDay(t)}"); }
        else if (S.PlannedMoveOf(f.Id) is { } pm) { Fact("TO", pm.To, Ui.Warn); Fact("ARRIVES", $"day {pm.ArrivalDay}", Ui.Warn); }
        Fact("SHIPS", f.Ships.Count.ToString());
        Fact("STRENGTH", $"{Fm.StrengthOf(f):0}");
        if (f.At != null)
            CardButton(_moveFleet == f.Id ? "Cancel move" : "Move...", () => { if (_moveFleet == f.Id) CancelMove(); else StartMove(f); },
                "Pick a destination on the map: the route and the arrival day show before you confirm", "Primary");
        int fid = f.Id;
        CardButton("Window", () => OpenMgmt(MgKind.Fleet, fleet: fid), "The fleet's window: drag leaders onto its posts, troops and fighters aboard, ships between fleets");
        _selActions.AddChild(Ui.Grow());
        CardButton("Details →", () => ShowTab(DrawerTab.Selected), "Ships, troops, officers, split and merge in the panel; attack and the other orders one click on");
    }
}
