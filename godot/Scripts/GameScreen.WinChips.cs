using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using RejectReason = Praedra.Core.Clock.RejectReason;
using UnloadRegimentsOrder = Praedra.Core.Clock.UnloadRegimentsOrder;
using LoadRegimentsOrder = Praedra.Core.Clock.LoadRegimentsOrder;
using UnloadSquadronsOrder = Praedra.Core.Clock.UnloadSquadronsOrder;
using LoadSquadronsOrder = Praedra.Core.Clock.LoadSquadronsOrder;
using TransferShipsOrder = Praedra.Core.Clock.TransferShipsOrder;
using MergeFleetsOrder = Praedra.Core.Clock.MergeFleetsOrder;
using SplitFleetOrder = Praedra.Core.Clock.SplitFleetOrder;
using SendRegimentOrder = Praedra.Core.Clock.SendRegimentOrder;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>The chips (leaders, regiments, squadrons, ships, fleets) and the drop zones of the management windows. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    // ================================================================== chips and zones

    /// <summary>A leader as a tile: their picture, name, the posts they can hold. Drags unless an order already moves them today, or they cannot act.</summary>
    Control LeaderChip(CharacterRecord c, string role)
    {
        string name = S.CharName(c.Id);
        var d = new Drag('L', c.Id);
        if (_moved.ContainsKey(d.Key)) return new Control { Visible = false };   // it already stands where it was dropped
        bool can = c.Status == CharacterStatus.Free && c.Transit == null && !Cm.IsOnMission(c.Id);
        // no posts line under a free leader's name: right under the name it read as their rank (UI review 12); the posts are in the hover
        string sub = c.Status != CharacterStatus.Free ? GameSession.StatusWords(c) : Cm.IsOnMission(c.Id) ? "on a mission" : null;
        var tile = Tile(null, false, 0, GameSession.CardName(name), sub, 1, can ? Ui.You : Ui.Text3, avatar: LeaderSprite(c.Id));
        bool mission = CanJoin(c);
        tile.TooltipText = LeaderHover(c, role, can, mission);
        tile.SetMeta(UiTest.FullName, "Leader " + name);
        if (can) MakeDraggable(tile, new List<Drag> { d }, name);
        if (mission) MissionMenu(tile, c.System, c.Id, null);
        return tile;
    }

    /// <summary>The posts a leader can hold, short enough for a tile (their leadership is in the tooltip).</summary>
    string LeaderPosts(CharacterRecord c)
    {
        if (c == null) return null;
        var posts = S.FleetPosts;
        var can = posts.Where(p => Cm.CanHold(c, p)).Select(GameSession.PostWords).ToArray();
        return can.Length == posts.Length ? "any post" : can.Length == 0 ? "passenger" : string.Join(", ", can);
    }

    /// <summary>A leader's picture: their side's sprite for their role, a fleet officer, a general or an official (Paul 2026-11-23: a hero
    /// gets a character picture; one per role, not a face per person). It replaced the initials in a ring.</summary>
    string[] LeaderSprite(int id)
    {
        if (!Cm.Data.TryGet(id, out var k)) return null;
        return PixelIcons.Leader(k.Side == Side.Hand, PixelIcons.RoleOf(k.Rank, k.CanAdmiral, k.CanGeneral));
    }

    string HullWords(EconObject o) => Fm.ClassOf(o) is { } sc && o.Hull < sc.Hull ? $"hull {o.Hull * 100 / Math.Max(1, sc.Hull)}%" : null;

    /// <summary>An empty post says what to do with it (impeccable critique 2026-11-14: "Empty" taught nothing).</summary>
    const string DropLeader = "Drop a leader here";

    /// <summary>A place nobody fills yet: its words in the darkest grey in a dashed frame (Paul 2026-11-14). <paramref name="slot"/>: one
    /// post (the General, an Admiral), drawn the size of a tile.</summary>
    Label EmptySlot(string text, bool slot = false)
    {
        var l = Ui.Label(text, null, Ui.Dimmed, Ui.Tiny, wrap: false);
        if (slot) l.AutowrapMode = TextServer.AutowrapMode.WordSmart;   // a post is one tile wide: its words take two lines
        l.AddThemeStyleboxOverride("normal", Ui.Dashed(8, 6));
        l.CustomMinimumSize = slot ? new Vector2(TileW, TileH) : new Vector2(0, 34);
        if (slot) l.HorizontalAlignment = HorizontalAlignment.Center;
        l.VerticalAlignment = VerticalAlignment.Center;
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    (PanelContainer Box, HFlowContainer Flow) DropZone(string key, string verb, Func<Drag, string> refuse, Action<Drag> drop, bool dashed = false)
    {
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        box.SetMeta(UiTest.FullName, "Zone " + key);
        string look = dashed ? "dashed" : "plain";
        box.SetMeta("look", look);
        box.AddThemeStyleboxOverride("panel", ZoneStyle(null, look));
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(flow);
        var zone = new Zone(key, box, flow, refuse, drop, verb);
        _zones.Add(zone);
        box.SetDragForwarding(new Callable(),
            Callable.From((Func<Vector2, Variant, bool>)((_, data) => Drag.ParseAll(data).Any(d => Judge(zone, d) == null))),
            Callable.From((Action<Vector2, Variant>)((_, data) => { if (DropAll(zone, Drag.ParseAll(data)) > 0) RefreshSectorView(); })));
        return (box, flow);
    }

    /// <summary>
    /// Drop every unit of a drag on a zone, unit by unit, each judged after the ones before it (room fills up). The units that went are no
    /// longer picked; any that could not go stay picked, and a short note says how many went (QA-111). Returns how many went.
    /// </summary>
    int DropAll(Zone zone, IReadOnlyList<Drag> all)
    {
        var went = new List<Drag>();
        foreach (var d in all) if (Judge(zone, d) == null) { RunDrop(() => zone.Drop(d)); went.Add(d); }
        if (went.Count == 0) return 0;
        _picked.RemoveAll(went.Contains);
        if (went.Count < all.Count)
            Toast($"Only {went.Count} of {all.Count} could {zone.Verb} here" + (all.Any(IsPicked) ? "; the rest stay picked." : "."), MsgKind.Warn);
        RefreshMgmt();
        return went.Count;
    }

    /// <summary>What dropping the whole drag on a zone would do: how many of its units may go on their own, and, when none may, why
    /// (null when some may; "" when it is where they already are).</summary>
    (int Fit, string Why) JudgeAll(Zone z)
    {
        int fit = 0; string why = "";
        foreach (var d in _dragTake)
        {
            var w = Judge(z, d);
            if (w == null) fit++;
            else if (why == "" && w != "") why = w;
        }
        return (fit, fit > 0 ? null : why);
    }

    /// <summary>Whether a drop may happen here (null), quietly not (""), or not, and why.</summary>
    string Judge(Zone z, Drag d) => d.Kind is not ('L' or 'M') && _scrapped.Contains(d.Id) ? $"{ThingName(d)} is being scrapped." : _moved.TryGetValue(d.Key, out var m) ? (z.Refuse(d) == "" ? "" : $"{ThingName(d)} already moves today: {m.Words}.") : z.Refuse(d);

    /// <summary>
    /// A zone's look (Paul 2026-11-14, kept simple): a plain zone draws nothing, only what stands in it; a fleet's row has a thin line, the
    /// row shown in a fleet window a #BABABA one; a slot for a new fleet is dashed. While a chip is dragged, the zones it can go to are
    /// outlined blue, and the ones it can't are dimmed.
    /// </summary>
    static StyleBox ZoneStyle(bool? ok, string look)
    {
        var clear = new Color(0, 0, 0, 0);
        bool plain = look is not ("dashed" or "row" or "selected");
        if (ok == true)
        {
            // a plain zone has no inner margin, so its blue line is drawn 4 px outside it: the tiles don't move
            var lit = Ui.Box(clear, 0, Ui.You, 1, plain ? 0 : 6, plain ? 0 : 6);
            if (plain) lit.SetExpandMarginAll(4);
            return lit;
        }
        return look switch
        {
            "dashed" => Ui.Dashed(6, 6),
            // 4 px at the sides: two rows of 150 px fit beside the scroll grabber and "3 ships · str 112" is never cut
            "row" => Ui.Box(clear, 0, Ui.WinLine, 1, 4, 6),
            "selected" => Ui.Box(clear, 0, Ui.WinActive, 1, 4, 6),
            _ => new StyleBoxEmpty(),
        };
    }

    void LightZones(Drag? d)
    {
        foreach (var z in _zones)
        {
            if (!IsInstanceValid(z.Box)) continue;
            string look = z.Box.HasMeta("look") ? z.Box.GetMeta("look").AsString() : "plain";
            string why = d is { } ? JudgeAll(z).Why : "";   // a picked mix lights every zone where any of it may go (QA-111)
            bool? ok = why == null ? true : why == "" ? null : false; // where it already is stays quiet
            z.Box.AddThemeStyleboxOverride("panel", ZoneStyle(ok, look));
            z.Box.Modulate = ok == false ? new Color(1, 1, 1, 0.55f) : Colors.White;
            // a fleet's split place shows only while a ship that can go there is dragged, or once ships were dropped there today
            if (z.Box.HasMeta("dragonly")) z.Box.Visible = ok == true || z.Box.HasMeta("ghosts");
        }
    }

    Drag? _dragging;
    List<Drag> _dragTake = new();   // every unit the drag carries, _dragging first

    int _dragCount = 1;

    /// <summary>
    /// A tile drags one unit; with Shift held when the drag starts, the whole stack (every unit the tile counts). The preview is the tile's
    /// picture with the count.
    /// </summary>
    void MakeDraggable(Control c, IReadOnlyList<Drag> ds, string label)
    {
        if (ds.Count == 0) return;
        c.MouseDefaultCursorShape = CursorShape.Drag;
        var self = ds[0];
        if (Pickable(self))
        {
            // a click picks it, Shift+click adds it to the pick or takes it out (Paul 2026-11-19); a drag is not a click
            // picked: a fleet window's card gets a blue line just outside it (it has no frame at rest); other tiles take the picked look
            if (IsPicked(self) && c is PanelContainer pc)
                pc.AddThemeStyleboxOverride("panel", !c.HasMeta("card") ? PickedLook() : CardBox(Ui.You));
            Vector2? down = null;
            c.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) return;
                if (mb.Pressed) { down = mb.Position; return; }
                if (down is Vector2 at && at.DistanceTo(mb.Position) < 6) { PickClick(self, mb.ShiftPressed); c.AcceptEvent(); }
                down = null;
            };
        }
        c.SetDragForwarding(Callable.From((Func<Vector2, Variant>)(_ =>
        {
            // a picked card carries every picked unit (itself first); else Shift takes the identical units beside it, as before
            var take = TakeFor(ds, Input.IsKeyPressed(Key.Shift), out bool picked);
            bool mixed = take.Select(ThingName).Distinct().Count() > 1;
            _dragging = take[0];
            _dragTake = take;
            _dragCount = take.Count;
            _dragMixed = mixed;
            using var look = Ui.Window();   // the tile under the mouse looks like the one in the window
            var prev = DragTile(take[0], take.Count > 1 ? (picked ? $"{take.Count} picked" : "all of them") : null, take.Count, ghost: false);
            prev.Theme = Ui.WindowTheme;
            prev.Modulate = new Color(1, 1, 1, 0.9f);
            prev.Rotation = -0.03f;
            c.SetDragPreview(prev);
            return Payload(take);
        })), new Callable(), new Callable());
    }

    /// <summary>While dragging: a small label by the mouse says what dropping here would do, or why it can't.</summary>
    void DragHint()
    {
        var mouse = _mgLayer.GetGlobalMousePosition();
        var z = _zones.LastOrDefault(z => IsInstanceValid(z.Box) && z.Box.IsVisibleInTree() && z.Box.GetGlobalRect().HasPoint(mouse));
        if (z == null || _dragging is not { } d) { _dragHint.Visible = false; return; }
        var (fit, why) = JudgeAll(z);
        if (why == "") { _dragHint.Visible = false; return; }
        string caught = why == null ? DropCaught(z) : null;   // allowed, but a leader would arrive alone where an armed enemy fleet is seen
        _dragHintText.Text = why ?? DropWords(d, z, fit) + (caught != null ? "\n" + caught : "");
        _dragHintText.AddThemeColorOverride("font_color", why == null && caught == null ? Ui.WinActive : Ui.Warn);
        _dragHint.Visible = true;
        _dragHint.ResetSize();
        _dragHint.Position = ClampWin(mouse + new Vector2(18, 22), _dragHint.Size) with { Y = Math.Min(mouse.Y + 22, _mgLayer.Size.Y - 40) };
    }

    /// <summary>The first dragged leader this drop would send alone into an armed enemy fleet's reach, said as a warning; else null.
    /// rule: m5-characters-and-talent §Moving a character alone (Caught on arrival).</summary>
    string DropCaught(Zone z) =>
        _dragTake.Where(x => Judge(z, x) == null).Select(x => AloneTo(x, z) is string to ? CaughtWarning(S.CharName(x.Id), to) : null).FirstOrDefault(w => w != null);

    /// <summary>The hint over a zone that takes the drag. With several units it says "up to": room can fill before the last one, and
    /// what can't go stays picked (QA-111).</summary>
    string DropWords(Drag d, Zone z, int fit)
    {
        string what = _dragMixed ? $"{_dragCount} units" : $"{(_dragCount > 1 ? $"{_dragCount} × " : "")}{ThingName(d)}";
        if (_dragCount <= 1) return $"Drop to {z.Verb}: {what}";
        return $"Drop to {z.Verb}: up to " + (fit < _dragCount ? $"{fit} of {what}" : what);
    }

    bool _dragMixed;   // the drag carries picked units of more than one class

    string ThingName(Drag d) => d.Kind switch
    {
        'L' => S.CharName(d.Id),
        'M' => S.Game.State.Fleets.TryGetValue(d.Id, out var f) ? S.NameOf(f) : "fleet",
        'F' => S.SpecialForceName(d.Id),
        _ => S.Host.Economy.Find(d.Id)?.Class ?? "unit",
    };
}
