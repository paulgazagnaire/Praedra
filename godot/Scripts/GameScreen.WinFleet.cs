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

/// <summary>A fleet's window: the fleets at the same place on the left, the one shown on the right with its tabs. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    const int FleetListW = 150;   // two to a row in the 306 px a window keeps beside its scroll grabber

    // ---- a fleet's window: your fleets at the same place on the left, the one shown on the right with its tabs (Classic's list and detail)

    void FleetWin(MgWin w, Fleet f)
    {
        bool still = f.At != null && f.Transit == null;
        string where = still ? $"Fleet · at {f.At}" : f.Transit is { } t ? $"Fleet · travelling to {t.Path[^1]}, arrives day {GameSession.ArrivalDay(t)}" : "Fleet";
        // the grip: drag the whole fleet onto another fleet to merge them
        var grip = WindowGlyph("⋮⋮");
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.TooltipText = Ui.Wrap($"Drag {f.Name} onto another fleet to merge it into that fleet.");
        if (still && !_moved.ContainsKey(new Drag('M', f.Id).Key)) MakeDraggable(grip, new List<Drag> { new('M', f.Id) }, f.Name);
        else grip.AddThemeColorOverride("font_color", Ui.Dimmed);
        // the fleet's name is grey like every window's name (Paul 2026-11-14); its blue icons say it is yours
        MgHead(w, where, f.Name, Ui.Text2, $"{Ui.Count(f.Ships.Count, "ship", "ships")} · strength {Fm.StrengthOf(f):0} · speed {Fm.SpeedOf(f)}", grip: grip);
        if (_moved.TryGetValue(new Drag('M', f.Id).Key, out var mto)) Line($"This fleet {mto.Words}.", Pending, Ui.Small);

        // 24 px between the parts, as in Paul's guide: the list of fleets here, the tab row, the tab's page
        var split = Ui.VBox(WinGap);
        split.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // the list on top: your fleets standing at the same system (a travelling fleet stands alone), two to a row
        var mates = still ? S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.At == f.At && x.Transit == null).OrderBy(x => x.Id).ToList() : new List<Fleet> { f };
        var top = Ui.VBox(Ui.Block);
        split.AddChild(top);
        top.AddChild(Caption(still ? "Fleets here" : "Travelling", mates.Count.ToString()));
        var list = new HFlowContainer();
        list.AddThemeConstantOverride("h_separation", 6); list.AddThemeConstantOverride("v_separation", 6);
        foreach (var m in mates)
        {
            int mid = m.Id;
            var row = FleetRow(m, m.Id == f.Id, () => ShowFleetIn(w, mid));
            row.SizeFlagsHorizontal = SizeFlags.Fill;   // a fixed half: a lone row stays half wide
            row.CustomMinimumSize = new Vector2(FleetListW, 0);
            list.AddChild(row);
        }
        top.AddChild(list);
        var detail = Ui.VBox(WinGap);
        detail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        split.AddChild(detail);
        Add(split);

        var eco = S.Host.Economy;
        var riders = Cm.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None).OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        var aboardF = FightersAboard(f);
        var aboardR = S.Host.Troops.RegimentsAboard(f);
        int posts = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.Count(p => Cm.HolderOf(f.Id, p) != null);
        Tabs(w, detail,
            ("Command", posts + riders.Count, () => CommandTab(f, riders)),
            ("Ships", f.Ships.Count, () => ShipsTab(f, still)),
            ("Fighters", aboardF.Count + f.Squadrons.Count, () => FightersAboardTab(f, aboardF)),
            ("Troops", aboardR.Count, () => TroopsAboardTab(f, aboardR.Select(a => a.Regiment).ToList())));

        foreach (var (seq, pend) in S.PendingOrdersOf(f.Id)) OrderLine(seq, $"Ordered: {pend}", Ui.Small);
        int fid = f.Id;
        var links = new List<(string, Action, string)>();
        if (still) links.Add(($"{f.At}", () => OpenMgmt(MgKind.Ground, f.At), "The planet's window: drag its leaders, troops and fighters onto this fleet"));
        if (still) links.Add(("Move...", () => { var ff = S.Game.State.Fleets[fid]; CloseSectorView(); StartMove(ff); }, "Pick a destination on the map"));
        links.Add(("Orders and attack...", () => { CloseSectorView(); SelectFleet(fid, true); ShowTab(DrawerTab.Selected); }, "Bombard, invade, retreat, scrap: the fleet's full page"));
        MgLinks(links.ToArray());
    }

    /// <summary>Show another fleet in this window (a click on the list); if a window already shows it, that one comes forward.</summary>
    void ShowFleetIn(MgWin w, int fid)
    {
        if (w.Fleet == fid) return;
        if (_mgWins.FirstOrDefault(x => x.Kind == MgKind.Fleet && x.Fleet == fid) is { } other) { Front(other); return; }
        w.Fleet = fid;
        RebuildMg(w);
    }

    void CommandTab(Fleet f, List<CharacterRecord> riders)
    {
        // the three posts, side by side
        var row = Ui.HBox(8);
        foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander })
        {
            var p = post;
            var col = Ui.VBox(Ui.Block);
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            col.AddChild(Caption(GameSession.PostWords(post), null, post switch
            {
                CharacterPost.Admiral => "The Admiral leads the fleet in space battles and bombardments.",
                CharacterPost.General => "The General leads the troops when the fleet invades.",
                _ => "The Commander has no effect yet.",
            }));
            var z = DropZone($"Fleet {f.Id} {post}", $"make {GameSession.PostWords(post)}", d => RefuseFleetPost(d, f, p), d => DropFleetPost(d, f, p));
            z.Box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (Cm.HolderOf(f.Id, post) is { } holder) z.Flow.AddChild(LeaderChip(holder, null));
            else z.Flow.AddChild(EmptySlot("Empty", slot: true));
            col.AddChild(z.Box);
            row.AddChild(col);
        }
        Add(row);
        Section("Passengers", riders.Count == 0 ? null : riders.Count.ToString(), "Leaders who ride the fleet with no post: they travel with it.");
        var pz = DropZone($"Fleet {f.Id} Passengers", "take aboard", d => RefuseFleetPost(d, f, CharacterPost.None), d => DropFleetPost(d, f, CharacterPost.None));
        foreach (var c in riders) pz.Flow.AddChild(LeaderChip(c, null));
        if (riders.Count == 0) pz.Flow.AddChild(EmptySlot("None"));
        Add(pz.Box);
        // leaders and fleets sent to join it from other systems (m5 §Moving a character alone, m3 §Joining a fleet on arrival)
        OnTheWay(Cm.All.Where(c => c.Side == Me && c.JoinFleet == f.Id && c.JoinByOrder && c.Transit != null), f.At,
            S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.JoinFleet == f.Id && x.Transit != null).OrderBy(x => x.Id));
    }

    void ShipsTab(Fleet f, bool still)
    {
        var eco = S.Host.Economy;
        var sz = DropZone($"Fleet {f.Id} Ships", "join", d => RefuseShipsInto(d, f), d => DropShipsInto(d, f));
        UnitTiles(sz.Flow, f.Ships.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)new Drag('S', o.Id), o, HullWords(o))), f.At);
        if (still && f.Ships.Count > 1)
        {
            var nz = DropZone($"Fleet {f.Id} NewFleet", "split off", d => RefuseSplit(d, f), d => DropSplit(d, f), dashed: true);
            nz.Box.CustomMinimumSize = new Vector2(TileW, TileH);
            var l = EmptySlot("+ New fleet");
            l.AddThemeStyleboxOverride("normal", new StyleBoxEmpty()); // the zone itself is the dashed slot
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.CustomMinimumSize = new Vector2(TileW - 12, TileH - 12);
            nz.Flow.AddChild(l);
            nz.Box.TooltipText = Ui.Wrap("Drop ships here to split them off into a new fleet.");
            sz.Flow.AddChild(nz.Box);
        }
        Add(sz.Box);
    }

    void FightersAboardTab(Fleet f, List<EconObject> aboard)
    {
        var eco = S.Host.Economy;
        int berths = Fm.FreeBerths(f);
        Section("Fighters aboard", berths + aboard.Count == 0 ? "no hangar" : $"{aboard.Count} · room for {Math.Max(0, berths - Promised(f.Id))}");
        var qz = DropZone($"Fleet {f.Id} Fighters", "load", d => RefuseOnto(d, f, 'Q'), d => DropOnto(d, f));
        UnitTiles(qz.Flow, aboard.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o)))
            .Concat(f.Squadrons.Select(id => eco.Find(id)).Where(o => o != null).Select(o => ((Drag?)null, o, "flies along"))), f.At);
        if (aboard.Count + f.Squadrons.Count == 0) qz.Flow.AddChild(EmptySlot(berths > 0 ? "Drop squadrons here to load them" : "No carrier in this fleet"));
        Add(qz.Box);
    }

    void TroopsAboardTab(Fleet f, List<EconObject> aboard)
    {
        int room = Fm.FreeTroopSlots(f);
        Section("Troops aboard", room + aboard.Count == 0 ? "no transport" : $"{aboard.Count} · room for {Math.Max(0, room - Promised(f.Id))}");
        var tz = DropZone($"Fleet {f.Id} Troops", "load", d => RefuseOnto(d, f, 'R'), d => DropOnto(d, f));
        UnitTiles(tz.Flow, aboard.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), f.At);
        if (aboard.Count == 0) tz.Flow.AddChild(EmptySlot(room > 0 ? "Drop regiments here to load them" : "No transport in this fleet"));
        Add(tz.Box);
    }

    /// <summary>Who is travelling here (leaders, and fleets sent to join): name and arrival day. Not draggable while they travel.</summary>
    void OnTheWay(IEnumerable<CharacterRecord> leaders, string to, IEnumerable<Fleet> fleets = null)
    {
        var ls = leaders.OrderBy(c => c.Id).ToList();
        var fs = (fleets ?? Enumerable.Empty<Fleet>()).ToList();
        if (ls.Count + fs.Count == 0) return;
        Section("On the way", (ls.Count + fs.Count).ToString(), $"Sent here from other systems. They take their place when they reach {to}.");
        var flow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var c in ls)
        {
            string name = S.CharName(c.Id);
            var t = Tile(null, false, 0, name, Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), 1, Ui.You, ghost: true, avatar: Initials(name));
            flow.AddChild(t);
        }
        foreach (var x in fs) flow.AddChild(Tile(UnitIcon.Kind.Ship, Me == Side.Hand, 2, x.Name, Arrives(x.Transit, $"{Ui.Count(x.Ships.Count, "ship", "ships")}, to merge"), 1, Ui.You, ghost: true));
        Add(flow);
    }

    string Arrives(FleetTransit t, string what)
    {
        int left = t == null ? 0 : Math.Max(0, t.DueDay - S.Game.Day) + t.HopTicks.Skip(t.HopIndex + 1).Sum();
        return (what != null ? what + ", " : "") + (left <= 1 ? "arrives tomorrow" : $"{left} days");
    }
}
