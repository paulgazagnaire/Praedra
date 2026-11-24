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

/// <summary>A system's windows: its Ground window (GameScreen.WinGround.cs), its Infrastructure window (GameScreen.WinBuild.cs), the
/// harvested card, and a fleet's row. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    // ---- a system's Ground window: the same window for every planet, yours or not (Paul 2026-11-24)

    void SystemWin(MgWin w)
    {
        string n = w.System;
        if (S.IsDestroyed(n)) { HarvestedWin(w, n); return; }
        GroundWin(w, n);
    }

    // ---- the story of a harvested world (Paul 2026-11-04 asked for a Lore tab per system; Paul 2026-11-24 removed it from the Ground window,
    // so only the harvested card keeps it): the Lore Master's text, from data/design/system-lore.csv, common knowledge, fog or not.
    void HarvestedLore(string n)
    {
        var e = SystemLore.Of(n);
        if (e == null) return;
        void Part(string head, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            FleetSection(head, 8).AddChild(Para(text));
        }
        // the record of what the harvest took: the world as it was and its story, no living people, moons or trade
        Part("Before the harvest", e.World);
        Part("Story", e.Story);
        Part("History", e.History);
    }

    // A harvested world (Paul 2026-11-04: "what happens to the system after being harvested?"): no tabs, no buildings, no Infrastructure link;
    // its name, the day, what it means, and your fleets standing in the debris. rule: m5-harvester §2 effect 5. In the Ground window's look.
    void HarvestedWin(MgWin w, string n)
    {
        var head = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        head.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        var pad = Pad(FleetHeadPad, FleetHeadPad, FleetHeadPad, FleetHeadPad);
        head.AddChild(pad);
        var parts = Boxed(new VBoxContainer(), 24);
        pad.AddChild(parts);
        parts.AddChild(WinNameRow(w, n.ToUpperInvariant(), Ui.Danger));
        if (!w.Min)
        {
            var l = Look.Words(S.DestroyedWords(n), 12, Ui.Danger);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(WidthOf(w) - 2 * EdgeW - 2 * FleetHeadPad, 0);
            parts.AddChild(l);
        }
        w.Head.AddChild(head);
        if (w.Min) return;
        // what is left of the planet, drawn live by the Planet Maker's shader (no baked pictures, Paul 2026-11-18)
        var planets = new PlanetLiveSet();
        if (planets.Of(n, true) is Texture2D rem)
        {
            var pic = Add(new TextureRect { Texture = rem, TextureFilter = TextureFilterEnum.Nearest, CustomMinimumSize = new Vector2(PlanetLive.Dots, PlanetLive.Dots),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
            pic.SetMeta(UiTest.FullName, "Harvested planet " + n);
            pic.AddChild(planets);
        }
        else planets.QueueFree();
        var what = FleetSection("Harvested", 8);
        what.AddChild(Para("The planet and everything on it are gone for good: no people, buildings or troops. Nobody holds it, and nobody can build, invade or send missions here."));
        what.AddChild(Para("It stays on the map as a field of debris. Fleets can still pass through it and stop here.", Look.Soft));
        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        if (own.Count > 0)
        {
            var ys = FleetSection("Your fleets", 16, own.Count.ToString());
            foreach (var f in own) ys.AddChild(FleetRow(f, false, null));
        }
        HarvestedLore(n);
    }

    // ---- a system's Infrastructure window (Paul 2026-11-03 chose to keep it a window of its own): the same window for every planet, yours or
    // not (Paul 2026-11-24); one you don't hold shows what your side last saw there, read only (GameScreen.WinBuild.cs)

    void InfraWin(MgWin w)
    {
        string n = w.System;
        if (S.IsDestroyed(n)) { HarvestedWin(w, n); return; } // already open when the harvester struck: no dead slots (Ponytail on #241)
        InfraOwnWin(w);
    }

    /// <summary>
    /// One fleet as a row: its mark, name, ships and strength. A drop zone: ships and whole fleets join it, troops and fighters board it,
    /// leaders ride it. In a fleet window it picks the fleet shown (<paramref name="pick"/>); elsewhere it has an Open button.
    /// </summary>
    Control FleetRow(Fleet f, bool selected, Action pick)
    {
        var z = DropZone($"Fleet {f.Id} Row", $"send to {S.NameOf(f)}", d => d.Kind switch
        {
            'S' or 'M' => RefuseShipsInto(d, f),
            'R' or 'Q' => RefuseOnto(d, f, d.Kind),
            'L' => RefuseFleetPost(d, f, CharacterPost.None),
            _ => "",
        }, d =>
        {
            if (d.Kind is 'S' or 'M') DropShipsInto(d, f);
            else if (d.Kind is 'R' or 'Q') DropOnto(d, f);
            else if (d.Kind == 'L') DropFleetPost(d, f, CharacterPost.None);
        });
        // a row with a thin line; the one shown in a fleet window is outlined #BABABA (Paul 2026-11-14: blue only means "yours")
        z.Box.SetMeta("look", selected ? "selected" : "row");
        z.Box.AddThemeStyleboxOverride("panel", ZoneStyle(null, selected ? "selected" : "row"));
        z.Box.SetMeta("selected", selected);
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // the fleet's biggest ship, by its own picture (Paul 2026-11-23, one picture per class); ties go to the first class by name
        var big = f.Ships.Select(id => S.Host.Economy.Find(id)?.Class).Where(c => c != null)
            .OrderByDescending(SizeOfClass).ThenBy(c => c, StringComparer.Ordinal).FirstOrDefault();
        var icon = UnitIcon.For(UnitIcon.Kind.Ship, Me == Side.Hand, Ui.You, big != null ? SizeOfClass(big) : 1, new Vector2(46, 26), cls: big);
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var v = Ui.VBox(0);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var nm = Ui.Label(S.NameOf(f), null, Ui.Text, Ui.Small, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.ClipText = true;
        v.AddChild(nm);
        // no bare strength number (Paul 2026-11-22, F7 answer B): the row says its ships, and where it goes when it moves
        string state = f.Transit is { } t ? $" · to {t.Path[^1]}" : "";
        var sl = Ui.Label($"{Ui.Count(f.Ships.Count, "ship", "ships")}{state}", null, Ui.Text3, Ui.Tiny, wrap: false);
        sl.ClipText = true;
        v.AddChild(sl);
        h.AddChild(v);
        int fid = f.Id;
        if (pick == null)
        {
            var open = Ui.Button("Open", () => OpenMgmt(MgKind.Fleet, fleet: fid), $"Open {S.NameOf(f)}'s window", "Chip");
            open.SetMeta(UiTest.FullName, $"Open {S.NameOf(f)}");
            open.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(open);
        }
        z.Flow.AddChild(h);
        z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        z.Box.MouseDefaultCursorShape = CursorShape.PointingHand;
        z.Box.TooltipText = Ui.Wrap($"{S.NameOf(f)}. Strength {Fm.StrengthOf(f):0}: how hard it hits (three Decrees score about 65). Drop ships, troops, fighters or leaders here to send them to it."
            + (pick != null && !selected ? " Click to show it." : ""));
        z.Box.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
            if (pick != null) pick(); else OpenMgmt(MgKind.Fleet, fleet: fid);
        };
        // the flow would stretch the row's content oddly: let the HBox fill it
        h.CustomMinimumSize = new Vector2(pick != null ? FleetListW - 16 : MgWidth - 60, 0);
        return z.Box;
    }
}
