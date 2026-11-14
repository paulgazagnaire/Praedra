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

    /// <summary>A leader as a tile: initials, name, the posts they can hold. Drags unless an order already moves them today, or they cannot act.</summary>
    Control LeaderChip(CharacterRecord c, string role)
    {
        string name = S.CharName(c.Id);
        var d = new Drag('L', c.Id);
        if (_moved.ContainsKey(d.Key)) return new Control { Visible = false };   // it already stands where it was dropped
        bool can = c.Status == CharacterStatus.Free && c.Transit == null && !Cm.IsOnMission(c.Id);
        string sub = c.Status != CharacterStatus.Free ? GameSession.StatusWords(c) : Cm.IsOnMission(c.Id) ? "on a mission" : LeaderPosts(c);
        var tile = Tile(null, false, 0, name, sub, 1, can ? Ui.You : Ui.Text3, avatar: Initials(name));
        tile.TooltipText = Ui.Wrap($"{name}. {S.SkillsOf(c)}\n{S.CapabilityOf(c)}{(role != null ? $"\n{role}." : "")}" + (can ? "\nDrag onto a post or a fleet." : ""));
        tile.SetMeta(UiTest.FullName, "Leader " + name);
        if (can) MakeDraggable(tile, new List<Drag> { d }, name);
        return tile;
    }

    /// <summary>The posts a leader can hold, short enough for a tile (their leadership is in the tooltip).</summary>
    string LeaderPosts(CharacterRecord c)
    {
        if (c == null) return null;
        var cls = Cm.ClassOf(c);
        var can3 = new[] { cls.CanAdmiral ? "Admiral" : null, cls.CanGeneral ? "General" : null, cls.CanCommander ? "Commander" : null }.Where(x => x != null).ToArray();
        return can3.Length == 3 ? "any post" : can3.Length == 0 ? "passenger" : string.Join(", ", can3);
    }

    static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    string HullWords(EconObject o) => Fm.ClassOf(o) is { } sc && o.Hull < sc.Hull ? $"hull {o.Hull * 100 / Math.Max(1, sc.Hull)}%" : null;

    /// <summary>A place nobody fills yet: its words in the darkest grey in a dashed frame (Paul 2026-11-14). <paramref name="slot"/>: one
    /// post (the General, an Admiral), drawn the size of a tile.</summary>
    Label EmptySlot(string text, bool slot = false)
    {
        var l = Ui.Label(text, null, Ui.Dimmed, Ui.Tiny, wrap: false);
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
            Callable.From((Func<Vector2, Variant, bool>)((_, data) => Drag.Parse(data) is { } d && Judge(zone, d) == null)),
            Callable.From((Action<Vector2, Variant>)((_, data) =>
            {
                // a stack drops unit by unit, each judged after the ones before it (room fills up)
                bool any = false;
                foreach (var d in Drag.ParseAll(data)) if (Judge(zone, d) == null) { RunDrop(() => drop(d)); any = true; }
                if (any) { RefreshMgmt(); RefreshSectorView(); }
            })));
        return (box, flow);
    }

    /// <summary>Whether a drop may happen here (null), quietly not (""), or not, and why.</summary>
    string Judge(Zone z, Drag d) => d.Kind is not ('L' or 'M') && _scrapped.Contains(d.Id) ? $"{ThingName(d)} is being scrapped." : _moved.TryGetValue(d.Key, out var m) ? (z.Refuse(d) == "" ? "" : $"{ThingName(d)} already moves today: {m.Words}.") : z.Refuse(d);

    /// <summary>
    /// A zone's look (Paul 2026-11-14, kept simple): a plain zone draws nothing, only what stands in it; a fleet's row has a thin line, the
    /// row shown in a fleet window a #909EBD one; a slot for a new fleet is dashed. While a chip is dragged, the zones it can go to are
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
            string why = d is { } dd ? Judge(z, dd) : "";
            bool? ok = why == null ? true : why == "" ? null : false; // where it already is stays quiet
            z.Box.AddThemeStyleboxOverride("panel", ZoneStyle(ok, look));
            z.Box.Modulate = ok == false ? new Color(1, 1, 1, 0.55f) : Colors.White;
        }
    }

    Drag? _dragging;

    int _dragCount = 1;

    /// <summary>
    /// A tile drags one unit; with Shift held when the drag starts, the whole stack (every unit the tile counts). The preview is the tile's
    /// picture with the count.
    /// </summary>
    void MakeDraggable(Control c, IReadOnlyList<Drag> ds, string label)
    {
        if (ds.Count == 0) return;
        c.MouseDefaultCursorShape = CursorShape.Drag;
        c.SetDragForwarding(Callable.From((Func<Vector2, Variant>)(_ =>
        {
            var take = Input.IsKeyPressed(Key.Shift) ? ds : ds.Take(1).ToList();
            _dragging = take[0];
            _dragCount = take.Count;
            using var look = Ui.Window();   // the tile under the mouse looks like the one in the window
            var prev = DragTile(take[0], take.Count > 1 ? "all of them" : null, take.Count, ghost: false);
            prev.Theme = Ui.WindowTheme;
            prev.Modulate = new Color(1, 1, 1, 0.9f);
            prev.Rotation = -0.03f;
            c.SetDragPreview(prev);
            return $"{take[0].Kind}:{string.Join(",", take.Select(x => x.Id))}";
        })), new Callable(), new Callable());
    }

    /// <summary>While dragging: a small label by the mouse says what dropping here would do, or why it can't.</summary>
    void DragHint()
    {
        var mouse = _mgLayer.GetGlobalMousePosition();
        var z = _zones.LastOrDefault(z => IsInstanceValid(z.Box) && z.Box.IsVisibleInTree() && z.Box.GetGlobalRect().HasPoint(mouse));
        if (z == null || _dragging is not { } d) { _dragHint.Visible = false; return; }
        string why = Judge(z, d);
        if (why == "") { _dragHint.Visible = false; return; }
        _dragHintText.Text = why ?? DropWords(d, z);
        _dragHintText.AddThemeColorOverride("font_color", why == null ? Ui.WinActive : Ui.Warn);
        _dragHint.Visible = true;
        _dragHint.ResetSize();
        _dragHint.Position = ClampWin(mouse + new Vector2(18, 22), _dragHint.Size) with { Y = Math.Min(mouse.Y + 22, _mgLayer.Size.Y - 40) };
    }

    string DropWords(Drag d, Zone z) => $"Drop to {z.Verb}: {(_dragCount > 1 ? $"{_dragCount} × " : "")}{ThingName(d)}";

    string ThingName(Drag d) => d.Kind switch
    {
        'L' => S.CharName(d.Id),
        'M' => S.Game.State.Fleets.TryGetValue(d.Id, out var f) ? f.Name : "fleet",
        'F' => S.SpecialForceName(d.Id),
        _ => S.Host.Economy.Find(d.Id)?.Class ?? "unit",
    };
}
