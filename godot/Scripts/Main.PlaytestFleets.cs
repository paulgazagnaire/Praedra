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
/// tabs so four stand side by side; then his Paper design, fleet-new-frame-1 and -2). F opens the selected fleet's window: its status,
/// its tabs with Command first, the orders and the fleets' behaviour there; a ship's row shows what it carries and its hull, its hover its
/// damage; Shift+click a second ship and the two can be split off together; a right-click on the head lists the fleets here first, then
/// the others closest first, and merges; Shift+click on the map picks a second fleet and a right-click sends both. Every click is a real
/// button's Pressed signal or the map's own handler.
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

        // ---- F opens the selected fleet's window: its name and status, its tabs with Command in front, the orders there
        SelectFleetByList(gs, a);
        gs.PressKey(Key.F); gs.PlayRefresh();
        string key = $"Fleet {a.Id}";
        Expect(gs.MgmtKeys.Contains(key), $"F opens the selected fleet's window ({string.Join(", ", gs.MgmtKeys)})");
        if (!gs.MgmtKeys.Contains(key)) return;
        string text = gs.MgText(key);
        Expect(text.Contains(a.Name.ToUpperInvariant()) && text.Contains("Status:") && text.Contains($"In orbit at {a.At}"), "the head: the fleet's name, and its status saying where it is");
        Expect(gs.CardTip($"Fleet status {a.Id}").Contains("Strength") && gs.CardTip($"Fleet status {a.Id}").Contains("Slowest ship"), "the status's hover gives its strength and its slowest ship");
        Expect(string.Join(",", gs.MgTabs(key)) == "Command,Ships,Fighters,Troops" && gs.MgTab(key) == "Command", $"the tabs are Command, Ships, Fighters and Troops, Command in front: {string.Join(", ", gs.MgTabs(key))}");
        Expect(text.Contains("Fleet actions") && text.Contains("Fleet behavior") && text.Contains("Fleet command") && text.Contains("Passengers"), "Command: actions, behaviour, the three posts and the passengers");
        Expect(gs.Has("Fleet Bombard") && gs.Has("Fleet Invade") && gs.Has("Fleet Move") && gs.Has("Behavior Defensive") && gs.Has("Behavior Elusive"), "the actions Bombardment, Invasion and Move, and the behaviour Defensive or Elusive");
        // merge, split and scrap are buttons, not only right-clicks (2026-11-22, follow-up 1): Merge with the actions, Split and Scrap on Ships
        Expect(gs.Enabled("Fleet Merge") && gs.MenuOf("Fleet Merge").Any(l => l.StartsWith($"{b.Name} joins now (here)", StringComparison.Ordinal)),
            $"Merge sits with the actions and lists {b.Name}, standing with it: {string.Join(" | ", gs.MenuOf("Fleet Merge"))}");
        Expect(gs.MenuOf($"Fleet head {a.Id}").Any(l => l.StartsWith("Scrap the whole fleet", StringComparison.Ordinal)), "a right-click on the head still scraps the fleet (a shortcut)");
        var (want, width) = gs.MgFit(key);
        Expect(width == 344 && want <= width, $"the window is one column wide, so four stand side by side, and nothing in it is wider ({want:0} in {width:0})");
        // Move: the windows step aside while the destination is picked, and Esc brings them back
        Expect(gs.Click("Fleet Move") && gs.InMoveMode && !gs.MgShown, "Move: the windows step aside so the map can be clicked");
        gs.PressKey(Key.Escape);
        Expect(!gs.InMoveMode && gs.MgShown && gs.MgmtKeys.Contains(key), "Esc cancels the move and the windows come back, none closed");

        // ---- ships: one row each, with its cargo and hull; its damage in its hover; a click picks it
        var eco = s.Host.Economy;
        Expect(gs.Click($"Tab {key} Ships") && gs.MgTab(key) == "Ships", "a click on the Ships tab shows it");
        Expect(gs.Has("Fleet Split") && !gs.Enabled("Fleet Split") && gs.Enabled("Fleet Scrap fleet"), "over the rows: Split off (greyed until a ship is picked) and Scrap fleet...");
        var carrier = a.Ships.Select(eco.Find).FirstOrDefault(o => o != null && fm.ClassOf(o) is { } c && c.FighterCapacity + c.TroopCapacity > 0);
        if (carrier != null)
        {
            var cc = fm.ClassOf(carrier)!;
            string room = cc.FighterCapacity > 0 ? $"{fm.UnitsAboard(carrier.Id, ObjectCategory.Fighter).Count()}/{cc.FighterCapacity}" : $"{fm.UnitsAboard(carrier.Id, ObjectCategory.Regiment).Count()}/{cc.TroopCapacity}";
            Expect(gs.MgPageText(key, "Ships").Contains(room), $"a ship's row says what it carries out of its room ({room})");
        }
        var ship = eco.Find(a.Ships[0])!;
        var sc = fm.ClassOf(ship)!;
        // damage, test only: half its hull and two hits on its weapons, put back after
        int hull0 = ship.Hull; var hits0 = ship.Hits?.ToList();
        ship.Hull = sc.Hull / 2;
        if (ship.Hits is { Count: > 1 }) ship.Hits[1] = 2;
        gs.MgText(key);
        Expect(gs.MgPageText(key, "Ships").Contains($"{sc.Hull / 2}/{sc.Hull}"), "its row shows the hull it has left");
        Expect(ship.Hits is not { Count: > 1 } || gs.CardTip($"S:{ship.Id}").Contains("weapons (2)"), $"its hover says its damage in words ({gs.CardTip($"S:{ship.Id}")})");
        ship.Hull = hull0; if (hits0 != null) ship.Hits = hits0;
        Expect(gs.MenuOf($"S:{ship.Id}").Any(l => l.StartsWith("Split it off", StringComparison.Ordinal)) && gs.MenuOf($"S:{ship.Id}").Any(l => l.StartsWith("Scrap this one", StringComparison.Ordinal)),
            $"a right-click on a ship splits it off or scraps it: {string.Join(" | ", gs.MenuOf($"S:{ship.Id}"))}");
        gs.PlayPick($"S:{ship.Id}", false); gs.PlayRefresh();
        gs.MgText(key);
        Expect(gs.PickedKeys.Contains($"S:{ship.Id}") && gs.MgTab(key) == "Ships", "a click on a ship picks it, and the ships stay in front");
        // a window that closes while the windows step aside for a move (its fleet merged away or lost) leaves the other windows' picks
        // alone (Ponytail on #348)
        gs.ShotWindow("fleet", bid.ToString());
        Expect(gs.Click("Fleet Move") && !gs.MgShown, "Move with two fleet windows open: both step aside");
        gs.PlayCloseTop();
        Expect(gs.PickedKeys.Contains($"S:{ship.Id}"), "a window closing meanwhile keeps the ship picked in the other");
        gs.PressKey(Key.Escape);
        Expect(gs.MgShown && !gs.InMoveMode && gs.MgmtKeys.Contains(key), "Esc cancels the move and the other window is back");
        if (a.Ships.Count >= 3)
        {
            gs.PlayPick($"S:{a.Ships[1]}", true); gs.PlayRefresh();
            gs.MgText(key);
            Expect(gs.MgTab(key) == "Ships" && gs.Enabled("Fleet Split") && gs.Enabled("Fleet Scrap ships"), $"Shift+click a second ship: the two can be split off or scrapped together ({gs.MgTab(key)})");
            int q0 = s.QueuedOrders;
            Expect(gs.Click("Fleet Split") && s.QueuedOrders == q0 + 1, "Split them off sends one split order");
            gs.MgText(key);   // as the next frame would
            Expect(gs.MgText(key).Contains("Ordered: Split 2 ships off") && gs.Click($"Cancel: Split 2 ships off {a.Name}") && s.QueuedOrders == q0, "the head shows the order, and its Cancel takes it back");
        }
        gs.ClearUnitPicks();
        gs.MgText(key);
        Expect(gs.Has("Fleet Split") && !gs.Enabled("Fleet Split") && gs.Has("Fleet Scrap fleet"), "with the ships let go, Split greys out and Scrap takes the whole fleet again");

        // ---- a right-click on the head merges: the fleets here first, then the others, closest first, each with its trip time
        var menu = gs.MenuOf($"Fleet head {a.Id}").ToList();
        int hereAt = menu.FindIndex(l => l.StartsWith($"{b.Name} joins now (here)", StringComparison.Ordinal));
        Expect(hereAt >= 0, $"the head's menu lists {b.Name}, standing with it: {string.Join(" | ", menu)}");
        var far = still.FirstOrDefault(f => f.At != a.At && s.Host.PlanMove(me, f.Id, a.At!, null) is { Ok: true });
        if (far != null)
        {
            int farAt = menu.FindIndex(l => l.StartsWith($"{far.Name} flies from {far.At}, joins in ", StringComparison.Ordinal));
            Expect(farAt > hereAt, $"and {far.Name} at {far.At}, after the fleets here, with its trip time");
        }
        int ships0 = a.Ships.Count;
        Expect(gs.RunMenu($"Fleet head {a.Id}", $"{b.Name} joins now (here)"), $"Merge {b.Name} into {a.Name}");
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
