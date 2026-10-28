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
/// Bottom right, the message corner: one short card with the latest message, and the kinds (battles, fleets, systems, production,
/// missions) to switch it to, each lit while it has news you have not looked at; Messages opens the whole log.
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
        else if (name == S.EnemyHqSystem) Tag("ENEMY HQ", Ui.Enemy);
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

        if (mine && !S.IsDestroyed(name))
            CardButton("Build", () => { _sysTab = SysTab.Build; ShowTab(DrawerTab.Selected); }, "Build ships, troops or buildings here");
        if (ownFleets > 0)
            CardButton("Fleets here", () => { _sysTab = SysTab.Fleets; ShowTab(DrawerTab.Selected); }, "Your fleets at this system");
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
        _selActions.AddChild(Ui.Grow());
        CardButton("Details →", () => ShowTab(DrawerTab.Selected), "Ships, troops, officers, split and attack, in the window");
    }

    // ================================================================== the message corner

    VBoxContainer _msgCorner;
    PanelContainer _msgCard;
    Label _msgKind, _msgTitle, _msgBody;
    Button _msgOpen;
    Message _msgShown;
    MsgCat? _msgCat;
    readonly Dictionary<MsgCat, (Button Chip, Glyph Dot)> _msgChips = new();
    readonly HashSet<MsgCat> _msgNews = new();

    static readonly (MsgCat Cat, string Short, string Tip)[] MsgKinds =
    {
        (MsgCat.Battles, "BAT", "Battles, bombardments, invasions, captures"),
        (MsgCat.Fleets, "FLT", "Fleet arrivals, retreats, blockades"),
        (MsgCat.Systems, "SYS", "Your systems: uprisings, losses, the headquarters"),
        (MsgCat.Build, "PRD", "Production: construction, research, upkeep"),
        (MsgCat.Leaders, "MSN", "Leaders and missions"),
    };

    void BuildMessageCorner()
    {
        _msgCorner = Ui.VBox(Ui.Block);
        _msgCorner.SetAnchorsPreset(LayoutPreset.BottomRight);
        _msgCorner.GrowHorizontal = GrowDirection.Begin; _msgCorner.GrowVertical = GrowDirection.Begin;
        _msgCorner.OffsetRight = -Edge; _msgCorner.OffsetBottom = -Edge; _msgCorner.OffsetLeft = -Edge; _msgCorner.OffsetTop = -Edge;
        _msgCorner.MouseFilter = MouseFilterEnum.Ignore;
        _msgCorner.CustomMinimumSize = new Vector2(310, 0);
        _mapHolder.AddChild(_msgCorner);

        var strip = Ui.Hud(4, 4);
        strip.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _msgCorner.AddChild(strip);
        var sr = Ui.HBox(2);
        strip.AddChild(sr);
        foreach (var (cat, label, tip) in MsgKinds)
        {
            var c = cat;
            var b = Ui.Button(label, () => PickMsgCat(c), tip + ". Click to show the latest; click again for all kinds.", "Layer");
            b.ToggleMode = true;
            b.CustomMinimumSize = new Vector2(42, 34);
            b.AddThemeFontSizeOverride("font_size", 11);
            var dot = new Glyph(Glyph.Kind.Dot, Ui.Warn, 6) { Visible = false };
            dot.SetAnchorsPreset(LayoutPreset.TopRight);
            dot.OffsetLeft = -12; dot.OffsetRight = -4; dot.OffsetTop = 3; dot.OffsetBottom = 11;
            b.AddChild(dot);
            _msgChips[cat] = (b, dot);
            sr.AddChild(b);
        }
        sr.AddChild(new VSeparator());
        var all = Ui.Button("Messages", () => ShowTab(DrawerTab.Log), "Every message, by kind (E)", "Chip");
        all.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        sr.AddChild(all);

        _msgCard = Ui.Hud(Ui.Pad, 10);
        _msgCard.MouseDefaultCursorShape = CursorShape.PointingHand;
        _msgCard.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && _msgShown != null) OpenMessage(_msgShown); };
        _msgCorner.AddChild(_msgCard);
        var v = Ui.VBox(Ui.Gap + 2);
        v.MouseFilter = MouseFilterEnum.Ignore;
        _msgCard.AddChild(v);
        var head = Ui.HBox(Ui.Block);
        head.MouseFilter = MouseFilterEnum.Ignore;
        _msgKind = Ui.Label("", "StatLabel", wrap: false);
        _msgKind.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_msgKind);
        _msgOpen = Ui.Button("open →", () => { if (_msgShown != null) OpenMessage(_msgShown); }, "Go there (and open the report)", "Link");
        _msgOpen.SetMeta(UiTest.FullName, "Open the latest message");
        head.AddChild(_msgOpen);
        v.AddChild(head);
        _msgTitle = Ui.Label("", "Strong", Ui.Text, 15);
        _msgTitle.CustomMinimumSize = new Vector2(280, 0);
        _msgTitle.MaxLinesVisible = 2; _msgTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(_msgTitle);
        _msgBody = Ui.Label("", null, Ui.Text3, Ui.Small);
        _msgBody.AddThemeFontOverride("font", Ui.Mono);
        _msgBody.CustomMinimumSize = new Vector2(280, 0);
        _msgBody.MaxLinesVisible = 2; _msgBody.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        v.AddChild(_msgBody);
    }

    static string KindWord(MsgCat c) => c switch
    {
        MsgCat.Battles => "BATTLE", MsgCat.Fleets => "FLEET", MsgCat.Systems => "SYSTEM", MsgCat.Build => "PRODUCTION",
        MsgCat.Leaders => "MISSION", MsgCat.Intel => "INTEL", _ => "ORDER",
    };

    void PickMsgCat(MsgCat c)
    {
        _msgCat = _msgCat == c ? null : c;
        _msgNews.Remove(c);
        RefreshMessageCorner();
    }

    /// <summary>A new message lights its kind until the player looks at it.</summary>
    void NoteMessageKind(Message m)
    {
        var c = m.Combat || m.Kind == MsgKind.Battle ? MsgCat.Battles : m.Cat;
        if (_msgChips.ContainsKey(c) && _msgCat != c) _msgNews.Add(c);
    }

    void RefreshMessageCorner()
    {
        if (_msgCorner == null) return;
        _msgCorner.Visible = !_drawer.Visible;
        foreach (var (cat, (chip, dot)) in _msgChips)
        {
            chip.SetPressedNoSignal(_msgCat == cat);
            dot.Visible = _msgNews.Contains(cat);
        }
        Message m = null;
        for (int i = _allMessages.Count - 1; i >= 0 && m == null; i--)
        {
            var x = _allMessages[i];
            if (_msgCat is not MsgCat want || x.Cat == want || (want == MsgCat.Battles && (x.Combat || x.Kind == MsgKind.Battle))) m = x;
        }
        _msgShown = m;
        _msgCard.Visible = m != null;
        if (m == null) return;
        var cat2 = m.Combat || m.Kind == MsgKind.Battle ? MsgCat.Battles : m.Cat;
        _msgKind.Text = $"{KindWord(cat2)} · D{m.Day}";
        _msgKind.AddThemeColorOverride("font_color", m.Kind switch { MsgKind.Bad => Ui.Danger, MsgKind.Warn or MsgKind.Battle => Ui.Warn, MsgKind.Good => Ui.Good, _ => Ui.Text3 });
        _msgOpen.Visible = m.System != null;
        // a message with a report has a short title and a longer text; a plain one is shown as one line
        bool titled = !string.IsNullOrEmpty(m.Title);
        _msgTitle.Text = titled ? m.Title : m.Text;
        _msgBody.Text = titled ? m.Text : "";
        _msgBody.Visible = titled;
    }
}
