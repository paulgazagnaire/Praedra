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

/// <summary>A system's windows: its Ground window (tabs), its Lore tab, its Infrastructure window and the harvested card. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    // ---- a system's window: tabs for its leaders, troops, fighters, fleets in orbit and buildings

    void SystemWin(MgWin w)
    {
        string n = w.System;
        if (S.IsDestroyed(n)) { HarvestedWin(w, n); return; }
        bool mine = !S.IsDestroyed(n) && S.OwnerOf(n) == Me;
        string sub = null;
        PanelContainer badge = null;
        if (mine)
        {
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            sub = $"Loyalty {S.Host.Economy.SupportOf(n, Me)}% · {Ui.Count(have, "regiment", "regiments")}{(need == 0 ? "" : $" of {need} needed")}";
            if (have < need) badge = Ui.Tag("UNDER-GARRISONED", Ui.Warn);
            if (S.Host.Support.IsUprising(n)) badge = Ui.Tag("UPRISING", Ui.Danger, true);
        }
        MgHead(w, $"Ground · {RegionOf(n)}", n, mine ? Ui.Text : Ui.Text2, sub, badge);
        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        if (!mine)
        {
            // what your side knows of a planet it doesn't hold
            Tabs(w, w.Body,
                ("Ground", null, () => PaneForces(n)),
                ("Fleets", own.Count, () => OrbitList(n, own)),
                ("Lore", null, () => LoreTab(n)));
            InfraLink(n);
            return;
        }
        var gen = Cm.SystemGeneralOf(n);
        if (gen != null && gen.Side != Me) gen = null;
        var here = S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead && c.AttachedFleet == 0 && c.Transit == null && Cm.LocationOf(c) == n && c.Id != gen?.Id)
            .OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        var regs = S.Host.Troops.RegimentsOnSystem(n).Where(o => o.Side == Me).OrderBy(o => o.Id).ToList();
        var sf = MySpecialForcesAt(n);
        var based = FightersBasedAt(n);
        Tabs(w, w.Body,
            ("Leaders", here.Count + (gen != null ? 1 : 0), () => LeadersTab(n, gen, here)),
            ("Troops", regs.Count + sf.Count, () => TroopsTab(n, regs, sf)),
            ("Fighters", based.Count, () => FightersTab(n, based)),
            ("Fleets", own.Count, () => OrbitList(n, own)),
            ("Lore", null, () => LoreTab(n)));
        InfraLink(n);
    }

    // ---- a system's Lore tab (Paul 2026-11-04: "a lore tab for each system... we'll have the content later"): the Lore Master's text,
    // from data/design/system-lore.csv. Lore is common knowledge, so every side reads it, fog or not; a system without any says so.
    void LoreTab(string n, bool harvested = false)
    {
        var e = SystemLore.Of(n);
        if (e == null) { Note("The story of this system is still to be written."); return; }
        void Part(string head, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Section(head);
            Line(text, Ui.Text2, Ui.Small);
        }
        if (harvested)
        {
            // the record of what the harvest took: the world as it was and its story, no living people, moons or trade
            Part("Before the harvest", e.World);
            Part("Story", e.Story);
            Part("History", e.History);
            return;
        }
        Part("The world", e.World);
        Part("From space", e.FromSpace);
        Part("People", e.People);
        Part("Moons", e.Moons);
        Part("Resources", e.Resources);
        Part("Story", e.Story);
        Part("History", e.History);
    }

    // A harvested world (Paul 2026-11-04: "what happens to the system after being harvested?"): no tabs, no buildings, no Infrastructure link;
    // its name, the day, what it means, and your fleets standing in the debris. rule: m5-harvester §2 effect 5.
    void HarvestedWin(MgWin w, string n)
    {
        MgHead(w, $"Harvested · {RegionOf(n)}", n, Ui.Danger, S.DestroyedWords(n), Ui.Tag("HARVESTED", Ui.Danger, true));
        if (PlanetArt.HarvestedOf(n) is Texture2D rem)
            Add(new TextureRect { Texture = rem, TextureFilter = TextureFilterEnum.Nearest, CustomMinimumSize = new Vector2(PlanetArt.Dots, PlanetArt.Dots),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        Line("The planet and everything on it are gone for good: no people, buildings or troops. Nobody holds it, and nobody can build, invade or send missions here.", Ui.Text2, Ui.Small);
        Line("It stays on the map as a field of debris. Fleets can still pass through it and stop here.", Ui.Text3, Ui.Small);
        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        if (own.Count > 0) OrbitList(n, own);
        LoreTab(n, harvested: true);
    }

    void InfraLink(string n) => MgLinks(($"Infrastructure at {n}", () => OpenMgmt(MgKind.Infra, n), "Yards, what the planet builds, slots and buildings"));

    // ---- a system's Infrastructure window (Paul 2026-11-03 chose to keep it a window of its own): yards, build tiles, slots, buildings

    void InfraWin(MgWin w)
    {
        string n = w.System;
        if (S.IsDestroyed(n)) { HarvestedWin(w, n); return; } // already open when the harvester struck: no dead slots (Ponytail on #241)
        bool mine = S.OwnerOf(n) == Me;
        var eco = S.Host.Economy;
        MgHead(w, $"Infrastructure · {RegionOf(n)}", n, mine ? Ui.Text : Ui.Text2,
            mine ? $"{eco.EnergyUsed(n)} of {eco.SlotsOf(n).Energy} energy slots used · {Ui.Count(eco.OfKindAt(n, FacilityKind.Mine), "mine", "mines")} · {Ui.Count(eco.OfKindAt(n, FacilityKind.Refinery), "refinery", "refineries")}" : null);
        if (mine) BuildTab(n); else PaneBuildings(n);
        MgLinks(($"Ground at {n}", () => OpenMgmt(MgKind.Ground, n), "Leaders, troops and fighters on the planet, and the fleets in orbit"));
    }

    void LeadersTab(string n, CharacterRecord gen, List<CharacterRecord> here)
    {
        var row = Ui.HBox(Ui.Block);
        // the system's General: one post, held by a leader standing here
        var gcol = Ui.VBox(4);
        gcol.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        gcol.AddChild(Caption("General", null, "The system's General adds their leadership to the defenders when the planet is invaded."));
        var gz = DropZone($"Ground {n} General", "make General", d => RefuseSystemGeneral(d, n), d => DropSystemGeneral(d, n));
        gz.Box.CustomMinimumSize = new Vector2(TileW + 14, 0);
        if (gen != null) gz.Flow.AddChild(LeaderChip(gen, "General of " + n));
        else gz.Flow.AddChild(EmptySlot("Empty"));
        gcol.AddChild(gz.Box);
        row.AddChild(gcol);
        // leaders standing here (not on a fleet, not the General)
        var lcol = Ui.VBox(4);
        lcol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lcol.AddChild(Caption("Leaders on the planet", null));
        var lz = DropZone($"Ground {n} Leaders", "land", d => RefuseToGround(d, n, 'L'), d => DropToGround(d, n));
        foreach (var c in here) lz.Flow.AddChild(LeaderChip(c, null));
        if (here.Count == 0) lz.Flow.AddChild(EmptySlot("No leader of yours stands here"));
        lcol.AddChild(lz.Box);
        row.AddChild(lcol);
        Add(row);
        OnTheWay(S.MyCharacters.Where(c => c.Transit is { } t && t.Path.Count > 0 && t.Path[^1] == n && c.JoinFleet == 0), n);
        if (CanSendFrom(n)) MgLinks(("Send a mission...", () => { CloseSectorView(); OpenPlanner(n); }, "Pick a team standing here, the kind of mission and the target."));
    }

    void TroopsTab(string n, List<EconObject> regs, List<EconObject> sf)
    {
        int need = S.Host.Support.RequiredGarrison(n);
        Section("Regiments", need > 0 ? $"{regs.Count} / {need} needed" : regs.Count.ToString(), "Below 60% loyalty a planet needs regiments to stay calm: one for every 10 points under 60. Too few and it rises up.");
        var rz = DropZone($"Ground {n} Regiments", "land", d => RefuseToGround(d, n, 'R'), d => DropToGround(d, n));
        UnitTiles(rz.Flow, regs.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), n);
        if (regs.Count == 0) rz.Flow.AddChild(EmptySlot("No regiments"));
        Add(rz.Box);
        // special forces: they never ride fleets (m3-troops-and-special-forces); dragged to another system's window they go there (a Move mission)
        Section("Special forces", sf.Count == 0 ? null : sf.Count.ToString(), "Agents never ride fleets. Drag one onto another system's Troops tab to send it there.");
        var sz = DropZone($"Ground {n} SpecialForces", "send here", d => RefuseToGround(d, n, 'F'), d => DropToGround(d, n));
        foreach (var o in sf)
        {
            Drag? d = SfReady(o) ? new Drag('F', o.Id) : null;
            if (d is { } dd && _moved.ContainsKey(dd.Key)) continue;
            var t = Tile(UnitIcon.Kind.Agent, Me == Side.Hand, 1, S.SpecialForceName(o.Id), SpecialForceState(o), 1, d == null ? Ui.Text3 : Ui.You);
            t.TooltipText = Ui.Wrap(S.SpecialForceName(o.Id) + (d != null ? "\nDrag it onto another system to send it there." : ""));
            if (d is { } drag) { t.SetMeta(UiTest.FullName, drag.Key); MakeDraggable(t, new List<Drag> { drag }, S.SpecialForceName(o.Id)); }
            sz.Flow.AddChild(t);
        }
        if (sf.Count == 0) sz.Flow.AddChild(EmptySlot("No special forces"));
        Add(sz.Box);
    }

    void FightersTab(string n, List<EconObject> based)
    {
        Section("Fighters based here", based.Count == 0 ? null : based.Count.ToString(), "Squadrons based on the planet defend it. Drag them onto a fleet's Fighters tab to load them on a carrier.");
        var fz = DropZone($"Ground {n} Fighters", "base here", d => RefuseToGround(d, n, 'Q'), d => DropToGround(d, n));
        UnitTiles(fz.Flow, based.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o))), n);
        if (based.Count == 0) fz.Flow.AddChild(EmptySlot("No squadrons"));
        Add(fz.Box);
    }

    /// <summary>Your fleets in orbit: a row each (drop ships, troops, fighters or leaders on it), with a button to open its window.</summary>
    void OrbitList(string n, List<Fleet> own)
    {
        if (own.Count == 0) { Add(EmptySlot("No fleets of yours here. Fleets you send here show up in this list.")); return; }
        Section("Your fleets in orbit", own.Count.ToString());
        foreach (var f in own) Add(FleetRow(f, false, null));
    }

    /// <summary>
    /// One fleet as a row: its mark, name, ships and strength. A drop zone: ships and whole fleets join it, troops and fighters board it,
    /// leaders ride it. In a fleet window it picks the fleet shown (<paramref name="pick"/>); elsewhere it has an Open button.
    /// </summary>
    Control FleetRow(Fleet f, bool selected, Action pick)
    {
        var z = DropZone($"Fleet {f.Id} Row", $"send to {f.Name}", d => d.Kind switch
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
        if (selected) z.Box.AddThemeStyleboxOverride("panel", Ui.Box(new Color(Ui.You, 0.12f), 8, new Color(Ui.You, 0.6f), 1, 6, 6));
        z.Box.SetMeta("selected", selected);
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Ignore;
        h.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var size = f.Ships.Select(id => S.Host.Economy.Find(id)?.Class).Where(c => c != null).Select(SizeOfClass).DefaultIfEmpty(1).Max();
        var icon = UnitIcon.For(UnitIcon.Kind.Ship, Me == Side.Hand, Ui.You, size, new Vector2(46, 26));
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var v = Ui.VBox(0);
        v.MouseFilter = MouseFilterEnum.Ignore;
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var nm = Ui.Label(f.Name, null, Ui.Text, Ui.Small, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.ClipText = true;
        v.AddChild(nm);
        string state = f.Transit is { } t ? $"to {t.Path[^1]}" : pick != null ? $"str {Fm.StrengthOf(f):0}" : $"strength {Fm.StrengthOf(f):0}";
        var sl = Ui.Label($"{Ui.Count(f.Ships.Count, "ship", "ships")} · {state}", null, Ui.Text3, Ui.Tiny, wrap: false);
        sl.ClipText = true;
        v.AddChild(sl);
        h.AddChild(v);
        int fid = f.Id;
        if (pick == null)
        {
            var open = Ui.Button("Open", () => OpenMgmt(MgKind.Fleet, fleet: fid), $"Open {f.Name}'s window", "Chip");
            open.SetMeta(UiTest.FullName, $"Open {f.Name}");
            open.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(open);
        }
        z.Flow.AddChild(h);
        z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        z.Box.MouseDefaultCursorShape = CursorShape.PointingHand;
        z.Box.TooltipText = Ui.Wrap($"{f.Name}. Drop ships, troops, fighters or leaders here to send them to it." + (pick != null && !selected ? " Click to show it." : ""));
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
