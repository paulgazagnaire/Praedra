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

        // ---- the map marker (Paul 2026-11-22): pointing up-left in the top-left light it is the icon as drawn; a quarter turn steps the
        // rim's light round in two 45-degree steps, then it settles in the top left
        foreach (var k in new[] { "fleet-defensive", "fleet-elusive", "fleet-unknown" })
            Expect(FleetLayer.MarkerTones(k, false, false, 0).SequenceEqual(PixelIconData.Map[k]), $"{k} pointing up-left is the icon as drawn");
        Expect(!FleetLayer.Parked("fleet-elusive").SequenceEqual(PixelIconData.Map["fleet-elusive"]) && FleetLayer.Parked("fleet-group-3").SequenceEqual(PixelIconData.Map["fleet-group-3"]),
            "off the map (the window's fleet rows, the legend) a fleet mark points up-right as parked; a numbered one stays as drawn");
        Expect(!FleetLayer.MarkerTones("fleet-defensive", true, true, 0).SequenceEqual(FleetLayer.MarkerTones("fleet-defensive", true, true, 6)),
            "the rim's light changes the pixels of a turned marker");
        var layer = new FleetLayer();
        var lights = new[] { layer.RimLight(1, true, false, 1000), layer.RimLight(1, true, true, 2000), layer.RimLight(1, true, true, 2090),
            layer.RimLight(1, true, true, 2180), layer.RimLight(1, true, true, 9000) };
        layer.Free();
        Expect(lights.SequenceEqual(new[] { 0, 6, 7, 0, 0 }), $"parked up-right then down-right: the light steps bottom-left, left, top-left ({string.Join(",", lights)})");
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
        // the status line (Paul 2026-11-22: "In orbit shouldn't have a progress bar", a damaged fleet shows its repair): put its ships whole
        // first, test only, so it is plainly in orbit; put back after
        var eco = s.Host.Economy;
        var whole = a.Ships.Select(eco.Find).Where(o => o != null).Select(o => (o!, o!.Hull, o.Hits?.ToList())).ToList();
        foreach (var (o, _, _) in whole) { o.Hull = fm.ClassOf(o)!.Hull; if (o.Hits != null) for (int k = 0; k < o.Hits.Count; k++) o.Hits[k] = 0; }
        text = gs.MgText(key);
        var stNow = gs.FleetStatusNow(a);
        Expect(text.Contains(a.Name.ToUpperInvariant()) && text.Contains("Status:") && stNow.Words.Contains(a.At!), $"the head: the fleet's name, and its status saying where it is ({stNow.Words})");
        Expect(stNow.Words.StartsWith("In orbit at", StringComparison.Ordinal) || stNow.Words.StartsWith("Blockading", StringComparison.Ordinal) ? !stNow.Bar : true,
            $"standing still and whole, it has no bar ({stNow.Words}, bar {stNow.Bar})");
        Expect(gs.CardTip($"Fleet status {a.Id}").Contains("Strength") && gs.CardTip($"Fleet status {a.Id}").Contains("Slowest ship"), "the status's hover gives its strength and its slowest ship");
        Expect(string.Join(",", gs.MgTabs(key)) == "Command,Ships,Fighters,Troops" && gs.MgTab(key) == "Command", $"the tabs are Command, Ships, Fighters and Troops, Command in front: {string.Join(", ", gs.MgTabs(key))}");
        Expect(new[] { "Fleet actions", "Fleet behavior", "Fleet command", "Passengers" }.All(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)), "Command: actions, behaviour, the three posts and the passengers");
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

        // ---- ships: one card each, three a row, with its cargo and hull; its damage in its hover; a click picks it
        Expect(gs.Click($"Tab {key} Ships") && gs.MgTab(key) == "Ships", "a click on the Ships tab shows it");
        Expect(gs.Has("Fleet Split") && !gs.Enabled("Fleet Split") && gs.Enabled("Fleet Scrap fleet"), "under the cards: Split off (greyed until a ship is picked) and Scrap fleet...");
        // Paul's frames (2026-11-23): every ship a 96 px card, three a row, the buttons under them
        var shipShape = gs.MgPageShape(key, "Ships");
        int zoneAt = shipShape.Parts.FindIndex(p => p.Contains($"Zone {key} Ships")), splitAt = shipShape.Parts.FindIndex(p => p.Contains("Fleet Split"));
        Expect(shipShape.CardWidths.Count >= a.Ships.Count && shipShape.CardWidths.All(x => x == 96)
            && shipShape.CardGap >= 0 && 3 * 96 + 2 * shipShape.CardGap <= shipShape.PageWidth && 4 * 96 + 3 * shipShape.CardGap > shipShape.PageWidth,
            $"each ship is a 96 px card, exactly three to a row ({string.Join(" ", shipShape.CardWidths)}; {shipShape.CardGap} px apart on a {shipShape.PageWidth} px page)");
        Expect(zoneAt >= 0 && splitAt > zoneAt, $"Split and Scrap sit under the ship cards: {string.Join(" / ", shipShape.Parts)}");
        foreach (var tab in new[] { "Fighters", "Troops" })
        {
            var sh = gs.MgPageShape(key, tab);
            int z = sh.Parts.FindIndex(p => p.Contains($"Zone {key} {tab}")), g = sh.Parts.FindIndex(p => p.Contains($"Ground at {a.At}"));
            Expect(z >= 0 && g > z && sh.GroundIcon && sh.CardWidths.All(x => x == 96),
                $"{tab}: 96 px cards, then the buttons under them with Ground at {a.At} and its turret: {string.Join(" / ", sh.Parts)} (icon {sh.GroundIcon})");
        }
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
        var stHurt = gs.FleetStatusNow(a);
        Expect(stHurt.Words.Contains("Repair") && stHurt.Bar, $"a damaged fleet standing still shows its repair: the days and a bar ({stHurt.Words}, bar {stHurt.Bar})");
        ship.Hull = Math.Max(1, sc.Hull / 5);
        Expect(gs.FleetStatusNow(a).ShipsInk == Ui.Danger, "a ship under a quarter of its hull turns the Ships tab's count red");
        ship.Hull = hull0; if (hits0 != null) ship.Hits = hits0;
        // only fighters short (every hull whole): the days, but no bar, which is a ship's hull (UI review 7)
        if (a.Ships.SelectMany(id => fm.UnitsAboard(id, ObjectCategory.Fighter)).OrderBy(q => q.Id).FirstOrDefault() is { } wing)
        {
            int lost0 = wing.CraftLost;
            wing.CraftLost = 2;
            gs.MgText(key);
            var stF = gs.FleetStatusNow(a);
            Expect(!stF.Bar && stF.Words.Contains(" day") && gs.CardTip($"Fleet status {a.Id}").Contains("fighters get one craft back"),
                $"only fighters short: the days, no bar, the fighters in the hover ({stF.Words}, bar {stF.Bar})");
            wing.CraftLost = lost0;
        }
        foreach (var (o, h0, k0) in whole) { o.Hull = h0; if (k0 != null) o.Hits = k0; }
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

        // ---- several fleets in orbit at one system share one marker with their number (Paul 2026-11-22); a click lists them (his pick),
        // Shift+click picks them all
        Expect(Enumerable.Range(2, 12).All(n => PixelIconData.Map.ContainsKey(FleetLayer.GroupIcon(n))), "every count of fleets from 2 to 13 has its marker");
        Expect(Enum.GetValues<MoverKind>().All(k => PixelIconData.Map.ContainsKey(FleetLayer.MoverIcon(k))), "every kind of mover has its pixel icon");
        // review 10: a numbered group is parked, so its marker must not be mirrored with its number in it (2 read as 5). Paul 2026-11-22: two
        // of yours show two marks (two squares, two chevrons, or chevron then square); more than two of yours, or more than one of the
        // enemy's, their number, the same design for both sides
        var (gk, gfx, gfy) = FleetLayer.Look(2, false, 0, FleetLayer.ParkedHeading);
        var g2 = FleetLayer.MarkerTones(gk, gfx, gfy, 0);
        string[] two = { "###", "..#", "###", "#..", "###" };
        bool upright = Enumerable.Range(0, g2.Length - 4).Any(r => Enumerable.Range(0, g2[0].Length - 2).Any(c =>
            Enumerable.Range(0, 5).All(i => Enumerable.Range(0, 3).All(j => (g2[r + i][c + j] != '.') == (two[i][j] == '#')))));
        Expect(upright, $"two enemy fleets parked together show an upright 2 ({gk}, mirrored {gfx}/{gfy})");
        Expect(PixelIconData.Map[gk].Length == 16 && PixelIconData.Map[gk].All(r => r.Length == 16), "the group icon is the fleet arrow's 16 x 16");
        Expect(FleetLayer.Look(1, false, 0, FleetLayer.ParkedHeading).Key == "fleet-unknown", "one enemy fleet is the bare arrow");
        string[] pairs = { FleetLayer.Look(2, true, 0, FleetLayer.ParkedHeading).Key, FleetLayer.Look(2, true, 1, FleetLayer.ParkedHeading).Key,
            FleetLayer.Look(2, true, 2, FleetLayer.ParkedHeading).Key };
        Expect(pairs.SequenceEqual(new[] { "fleet-pair-defensive", "fleet-pair-mixed", "fleet-pair-elusive" }) && pairs.All(PixelIconData.Map.ContainsKey),
            "two of yours: two squares, chevron and square, two chevrons: " + string.Join(", ", pairs));
        var three = FleetLayer.Look(3, true, 1, FleetLayer.ParkedHeading);
        Expect(three == ("fleet-group-3", false, false), $"three of yours show their number, never mirrored ({three})");
        var pairFlip = FleetLayer.Look(2, true, 0, FleetLayer.ParkedHeading);
        Expect(pairFlip.Fx, "a pair (no number in it) is mirrored to point up-right like one fleet, light and all");
        var pair = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null).GroupBy(f => f.At).Where(g => g.Count() >= 2)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.OrderBy(f => f.Id).ToList()).FirstOrDefault();
        if (pair != null)
        {
            var ids = pair.Select(f => f.Id).ToList();
            string at = pair[0].At;
            gs.PlayRefresh();
            Expect(gs.PlayFleetGroup(ids[0]).SequenceEqual(ids), $"the {ids.Count} fleets in orbit at {at} share a marker");
            var here = gs.PlayOwnMarkers().Where(m => ids.Contains(m.Fleet)).ToList();
            Expect(here.Count == 1 && here[0].Count == ids.Count, $"one marker at {at}, counting {ids.Count} ({here.Count} drawn)");
            var lines = gs.PlayClickFleetGroup(ids[0]);
            Expect(lines.Count == ids.Count && pair.All(f => lines.Any(l => l.StartsWith(f.Name + " ", StringComparison.Ordinal) && (l.EndsWith("Defensive") || l.EndsWith("Elusive")))),
                "a click on it lists them, each with its ships and behaviour: " + string.Join(" / ", lines));
            var last = pair[^1];
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
            if (gs.WindowOpen) gs.Click("Close window");
            Expect(gs.PlayPickFromFleetGroup(last.Name) && gs.SelectedFleetId == last.Id, $"picking {last.Name} in the list selects it");
            // Paul 2026-11-23: "the fleet window opens", not the window on the right
            Expect(gs.MgmtKeys.Contains($"Fleet {last.Id}") && !gs.WindowOpen,
                $"and opens its Fleet window, not the window on the right ({string.Join(", ", gs.MgmtKeys)}; right window {gs.WindowOpen})");
            Expect(!gs.SelectionCardOpen, "a fleet selected has no card at the bottom (Paul 2026-11-23)");
            if (gs.G.TryFindSystem(at, out int atIdx)) gs.View.FocusOn(atIdx, 2.4f);   // the words show only on screen
            gs.PlayRefresh();
            Expect(gs.MapWords.Any(w => w.StartsWith(last.Name, StringComparison.Ordinal) && w.EndsWith($" +{ids.Count - 1}", StringComparison.Ordinal)),
                $"the group's words name the selected fleet and count the others ({last.Name} ... +{ids.Count - 1}): " + string.Join(" | ", gs.MapWords));
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
            gs.PressKey(Key.Tab);
            Expect(gs.SelectedFleetId == ids[0] && !gs.WindowOpen && !gs.SelectionCardOpen,
                $"Tab steps to the next fleet there ({ids[0]}, got {gs.SelectedFleetId}) like a click: no window on the right, no card (review 13)");
            gs.PressKey(Key.Tab, shift: true);
            Expect(gs.SelectedFleetId == last.Id && !gs.WindowOpen, "Shift+Tab steps back");
            gs.PlayShiftClickGroup(ids[0]);
            Expect(ids.All(gs.View.PickedFleets.Contains), "Shift+click on the marker picks them all");
            gs.PlayShiftClickGroup(ids[0]);
            Expect(!ids.Any(gs.View.PickedFleets.Contains), "a second Shift+click lets them all go");
        }
        else GD.Print("  (no system with two fleets of yours: the shared marker is not checked on this side)");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);

        // ---- a lone fleet on the map (Paul 2026-11-23): a click selects it with no card at the bottom; a double-click opens its Fleet window
        var lone = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && gs.PlayFleetGroup(f.Id).Count == 1)
            .OrderBy(f => f.Id).FirstOrDefault();
        if (lone != null && gs.G.TryFindSystem(lone.At, out int loneIdx))
        {
            if (gs.WindowOpen) gs.Click("Close window");
            gs.View.Select(-1); gs.View.SelectedFleet = -1;
            gs.View.FocusOn(loneIdx, 2.4f);
            gs.PlayRefresh();
            if (gs.FleetMarker(lone.Id) is { } lp)
            {
                gs.PlayMapClick(lp);
                gs.PlayRefresh();
                Expect(gs.SelectedFleetId == lone.Id && !gs.SelectionCardOpen && !gs.WindowOpen && gs.MgmtCount == 0,
                    $"a click on {lone.Name} selects it, with no card at the bottom and no window ({gs.SelectionCardText})");
                gs.PlayMapClick(lp, doubleClick: true);
                gs.PlayRefresh();
                Expect(gs.MgmtKeys.Contains($"Fleet {lone.Id}") && !gs.WindowOpen, $"a double-click on {lone.Name} opens its Fleet window ({string.Join(", ", gs.MgmtKeys)})");
            }
            else Expect(false, $"{lone.Name} has a marker on the map");
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }
        else GD.Print("  (no fleet of yours alone at a system: the double-click is not checked on this side)");
    }
}
