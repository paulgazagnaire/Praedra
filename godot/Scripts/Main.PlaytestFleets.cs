using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace PraedraViewer;

/// <summary>
/// Playtest of a fleet's window and of several fleets on the map (Paul 2026-11-21: merge fleets, pick single ships, see their stats and
/// what they carry, give one or several fleets an order; then "a fleet (single fleet) window that works really well", one column with
/// tabs so four stand side by side, and for several fleets: move only, by a right-click). F opens the selected fleet's window: its numbers
/// and its orders; a ship's card shows what it carries; a click on a ship brings its tab forward with its damage, its cargo and its class;
/// Shift+click a second one and the cards come back, to split the two off together; Merge with
/// lists the fleets here first, then the others closest first, and merges; Shift+click on the map picks a second fleet and a right-click
/// sends both. Every click is a real button's Pressed signal or the map's own handler.
/// </summary>
public partial class Main
{
    void PlaytestFleets(GameScreen gs)
    {
        var s = gs.S;
        var fm = s.Host.Fleets;
        var me = s.Player;
        for (int i = 0; i < 5 && gs.AnyModal; i++) gs.PressKey(Key.Escape);
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        List<Fleet> Still() => s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && f.Ships.Count > 0).OrderBy(f => f.Id).ToList();
        var still = Still();
        Expect(still.Count > 0, "you have a fleet standing at a system");
        if (still.Count == 0) return;

        // a second fleet beside the first, so merging there can be checked (test only: a ship of a class the first one has)
        var a = still.OrderByDescending(f => f.Ships.Count).First();
        string cls = s.Host.Economy.Find(a.Ships[0])!.Class;
        int bid = fm.AddFleet(me, "Test Pair", new[] { cls }, a.At!);
        var b = s.Game.State.Fleets[bid];

        // ---- F opens the selected fleet's window: its name, its numbers, one bar of orders
        SelectFleetByList(gs, a);
        gs.PressKey(Key.F); gs.PlayRefresh();
        string key = $"Fleet {a.Id}";
        Expect(gs.MgmtKeys.Contains(key), $"F opens the selected fleet's window ({string.Join(", ", gs.MgmtKeys)})");
        if (!gs.MgmtKeys.Contains(key)) return;
        string text = gs.MgText(key);
        Expect(text.Contains(a.Name.ToUpperInvariant()) && text.Contains("STRENGTH") && text.Contains("SLOWEST") && text.Contains("FIGHTERS") && text.Contains("TROOPS"), "the window shows the fleet: its name and its numbers");
        Expect(gs.Has("Fleet Move") && gs.Has("Fleet Merge") && gs.Has("Fleet Bombard") && gs.Has("Fleet Invade") && gs.Has("Fleet Scrap"), "the orders: Move, Merge with, Bombard, Invade, Scrap");
        Expect(string.Join(",", gs.MgTabs(key)) == "Ships,Fighters,Troops,Leaders" && gs.MgTab(key) == "Ships", $"with no ship picked, the tabs are its ships, fighters, troops and leaders, the ships in front: {string.Join(", ", gs.MgTabs(key))}");
        var (want, width) = gs.MgFit(key);
        Expect(width == 344 && want <= width, $"the window is one column wide, so four stand side by side, and nothing in it is wider ({want:0} in {width:0})");
        // Move: the windows step aside while the destination is picked, and Esc brings them back
        Expect(gs.Click("Fleet Move") && gs.InMoveMode && !gs.MgShown, "Move...: the windows step aside so the map can be clicked");
        gs.PressKey(Key.Escape);
        Expect(!gs.InMoveMode && gs.MgShown && gs.MgmtKeys.Contains(key), "Esc cancels the move and the windows come back, none closed");

        // ---- ships: one card each, with its cargo; a click on a ship brings its tab forward
        var eco = s.Host.Economy;
        var carrier = a.Ships.Select(eco.Find).FirstOrDefault(o => o != null && fm.ClassOf(o) is { } c && c.FighterCapacity + c.TroopCapacity > 0);
        if (carrier != null)
        {
            var cc = fm.ClassOf(carrier)!;
            string room = cc.FighterCapacity > 0 ? $"{fm.UnitsAboard(carrier.Id, ObjectCategory.Fighter).Count()}/{cc.FighterCapacity}" : $"{fm.UnitsAboard(carrier.Id, ObjectCategory.Regiment).Count()}/{cc.TroopCapacity}";
            Expect(text.Contains(room), $"a ship's card says what it carries out of its room ({room})");
        }
        var ship = eco.Find(a.Ships[0])!;
        var sc = fm.ClassOf(ship)!;
        // damage, test only: half its hull and two hits on its weapons, put back after
        int hull0 = ship.Hull; var hits0 = ship.Hits?.ToList();
        ship.Hull = sc.Hull / 2;
        if (ship.Hits is { Count: > 1 }) ship.Hits[1] = 2;
        gs.PlayPick($"S:{ship.Id}", false); gs.PlayRefresh();
        gs.MgText(key);
        Expect(gs.MgTab(key) == "Ship", $"a click on a ship brings its tab forward ({gs.MgTab(key)})");
        string page = gs.MgPageText(key, "Ship");
        Expect(page.Contains($"hull {sc.Hull / 2} of {sc.Hull}") && (ship.Hits is not { Count: > 1 } || page.Contains("weapons (2)")), "it shows the ship's hull and its damage in words");
        Expect(page.Contains("IT CARRIES") || page.Contains("It carries no fighters"), "and what it carries");
        Expect(page.Contains($"EVERY {ship.Class.ToUpperInvariant()}") && page.Contains("Trip time"), "and the numbers of its class");
        ship.Hull = hull0; if (hits0 != null) ship.Hits = hits0;
        if (a.Ships.Count >= 3)
        {
            gs.PlayPick($"S:{a.Ships[1]}", true); gs.PlayRefresh();
            gs.MgText(key);
            Expect(gs.MgTab(key) == "Ships" && !gs.MgTabs(key).Contains("Ship") && gs.Enabled("Fleet Split") && gs.Enabled("Fleet Scrap ships"), $"Shift+click a second ship: the ship cards come back in front, and the two can be split off or scrapped together ({gs.MgTab(key)})");
            int q0 = s.QueuedOrders;
            Expect(gs.Click("Fleet Split") && s.QueuedOrders == q0 + 1, "Split them off sends one split order");
            gs.MgText(key);   // as the next frame would
            Expect(gs.Click($"Cancel: Split 2 ships off {a.Name}") && s.QueuedOrders == q0, "and its Cancel takes it back");
        }
        gs.ClearUnitPicks();
        gs.MgText(key);
        Expect(!gs.MgTabs(key).Contains("Ship") && !gs.Has("Fleet Split"), "with the ships let go, their tab and the split go");

        // ---- Merge with: the fleets here first, then the others, closest first, each with its trip time
        var menu = gs.MenuOf("Fleet Merge").ToList();
        int hereAt = menu.FindIndex(l => l.StartsWith($"{b.Name}: here", StringComparison.Ordinal));
        Expect(hereAt >= 0, $"Merge with lists {b.Name}, standing with it: {string.Join(" | ", menu)}");
        var far = still.FirstOrDefault(f => f.At != a.At && s.Host.PlanMove(me, f.Id, a.At!, null) is { Ok: true });
        if (far != null)
        {
            int farAt = menu.FindIndex(l => l.StartsWith($"{far.Name}: at {far.At}, flies here in ", StringComparison.Ordinal));
            Expect(farAt > hereAt, $"and {far.Name} at {far.At}, after the fleets here, with its trip time");
        }
        int ships0 = a.Ships.Count;
        Expect(gs.RunMenu("Fleet Merge", $"{b.Name}: here"), $"Merge {b.Name} into {a.Name}");
        gs.PlayDay(); gs.PlayRefresh();
        Expect(!s.Game.State.Fleets.ContainsKey(bid) && a.Ships.Count == ships0 + 1, $"next day {b.Name} has merged into {a.Name}");

        // ---- several fleets on the map: Shift+click picks, a right-click sends them all (Paul: move only)
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        still = Still();
        if (still.Count >= 2)
        {
            var x = still[0]; var y = still[1];
            string to = s.Data.Galaxy.Systems.Select(z => z.Name).Where(n => n != x.At && n != y.At && !s.IsDestroyed(n))
                .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault(n => s.Host.PlanMove(me, x.Id, n, null) is { Ok: true, Red: false } && s.Host.PlanMove(me, y.Id, n, null) is { Ok: true, Red: false });
            if (to != null)
            {
                SelectFleetByList(gs, x);
                gs.HoverTextFor(to);   // the map takes in the selection before the Shift+click
                gs.PlayShiftClickFleet(y.Id);
                Expect(gs.View.PickedFleets.SetEquals(new[] { x.Id, y.Id }), $"Shift+click {y.Name} on the map: it and {x.Name}, selected, are picked");
                gs.PlayShiftClickFleet(x.Id);
                Expect(gs.View.PickedFleets.SetEquals(new[] { y.Id }), $"Shift+click {x.Name} takes it out, though it is the one selected");
                gs.PlayShiftClickFleet(x.Id);
                gs.PlayRightClick(to);
                Expect(s.PlannedMoveOf(x.Id)?.To == to && s.PlannedMoveOf(y.Id)?.To == to, $"a right-click on {to} sends both");
                foreach (var f in new[] { x, y })
                {
                    gs.ShotWindow("fleet", f.Id.ToString()); gs.MgText($"Fleet {f.Id}");
                    Expect(gs.Click($"Cancel: Move {f.Name} to {to}") && s.PlannedMoveOf(f.Id) == null, $"{f.Name}'s order can be cancelled in its window");
                }
                int open = gs.MgmtCount;
                gs.PressKey(Key.Escape);
                Expect(gs.View.PickedFleets.Count == 0 && gs.MgmtCount == open, "Esc lets the fleets picked go before it closes a window");
            }
            else GD.Print("  (no system both fleets reach without a known trap: the shared move is not checked on this side)");
        }
        else GD.Print("  (one fleet standing still: the shared move is not checked on this side)");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
    }
}
