using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using YardSetUse = Praedra.Core.Construction.YardSetUse;

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
        var lights = new[] { layer.RimLight(1, 1, 1000), layer.RimLight(1, 3, 2000), layer.RimLight(1, 3, 2090),
            layer.RimLight(1, 3, 2180), layer.RimLight(1, 3, 9000) };
        layer.Free();
        Expect(lights.SequenceEqual(new[] { 0, 2, 1, 0, 0 }), $"parked up-right then a quarter turn to down-right: the light turns with the ship to the top right, then steps top, top-left ({string.Join(",", lights)})");
        // Paul 2026-11-25: a moving fleet's arrow points where it flies, the nearest of eight directions, never sliding sideways. Straight up,
        // right, down and left are the "-up" drawing turned by whole quarter turns (crisp pixels); its shine (the nose) leads the way
        foreach (var (h, oct, rot) in new[] { (new Vector2(0, -1), 0, 0), (new Vector2(1, -0.2f), 2, 1), (new Vector2(0.1f, 1), 4, 2), (new Vector2(-1, 0.3f), 6, 3) })
        {
            var look = FleetLayer.Look(1, true, 0, h.Normalized());
            Expect(FleetLayer.Octant(h.Normalized()) == oct && look == ("fleet-defensive-up", false, false, rot), $"heading {h}: direction {oct}, the up arrow turned {rot} quarter(s) ({look})");
            var g = FleetLayer.MarkerTones(look.Key, look.Fx, look.Fy, 0, look.Rot);
            var ink = Enumerable.Range(0, 16).SelectMany(y => Enumerable.Range(0, 16).Where(x => g[y][x] != '.').Select(x => (x, y))).ToList();
            var nose = ink.First(p => g[p.y][p.x] == '4');
            bool leads = oct switch { 0 => nose.y == ink.Min(p => p.y), 2 => nose.x == ink.Max(p => p.x), 4 => nose.y == ink.Max(p => p.y), _ => nose.x == ink.Min(p => p.x) };
            Expect(leads && ink.Count == PixelIconData.Map["fleet-defensive-up"].Sum(r => r.Count(c => c != '.')), $"heading {h}: the nose leads and no pixel is lost in the turn");
        }
        Expect(FleetLayer.Look(1, true, 1, new Vector2(1, 1).Normalized()) == ("fleet-elusive", true, true, 0) && FleetLayer.Look(1, false, 0, new Vector2(-1, 0)).Key == "fleet-unknown-up",
            "a diagonal heading mirrors the arrow as before; an enemy's bare arrow turns too");
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
        var (gk, gfx, gfy, grot) = FleetLayer.Look(2, false, 0, FleetLayer.ParkedHeading);
        var g2 = FleetLayer.MarkerTones(gk, gfx, gfy, 0, grot);
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
        Expect(three == ("fleet-group-3", false, false, 0), $"three of yours show their number, never mirrored or turned ({three})");
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
            if (gs.WindowOpen) gs.Click("Advisor");
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
            if (gs.WindowOpen) gs.Click("Advisor");
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

        ScreenFixes(gs, s, me);
    }

    /// <summary>
    /// Special forces aboard a fleet (m5-missions §Spying from orbit 1-3), in the Command tabs only (Paul 2026-11-27): the Fleet window's place
    /// for them with its N/9 count; a drag from the planet's Command tab takes them aboard, a line in the log says so; a right-click sends one
    /// on Espionage from orbit, and the planner says each guard's chance to spot it is halved; the mission is launched from the fleet; a drag
    /// back onto the Command tab of a world you don't hold lands the other one; a fleet with no ships refuses them; fleet to fleet works.
    /// </summary>
    void OrbitTeam(GameScreen gs, GameSession s, Praedra.Core.Clock.Side me)
    {
        var mm = s.Missions; var eco = s.Host.Economy;
        if (!mm.Params.OrbitMissions) { GD.Print("  (missions from orbit are off: not checked)"); return; }
        var esp = mm.Data.Get(Praedra.Core.Missions.MissionKind.Espionage);
        string cls = eco.Costs.Rows.Where(r => r.Category == Praedra.Core.Economy.ObjectCategory.SpecialForce && (me == Praedra.Core.Clock.Side.Hand ? r.Hand : r.Collective))
            .Select(r => r.Name).Where(n => s.Host.Troops.Data.SpecialForce(n) is { } k && (k.MissionBit & esp.SpecialForceMask) != 0)
            .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        string ship = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.Ships.Count > 0).OrderBy(f => f.Id).Select(f => eco.Find(f.Ships[0])?.Class).FirstOrDefault(c => c != null);
        string spot = gs.G.Systems.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault(x => s.OwnerOf(x) == null && !s.IsDestroyed(x) && !s.Game.State.Fleets.Values.Any(f => f.At == x));
        if (cls == null || ship == null || spot == null) { GD.Print($"  (no spy class, ship or quiet neutral world on this side: orbit missions not checked; {cls}, {ship}, {spot})"); return; }
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        int fid = s.Host.Fleets.AddFleet(me, "Orbit Team", new[] { ship }, spot);
        int a = eco.AddObject(me, cls, spot), b = eco.AddObject(me, cls, spot);
        string an = s.SpecialForceName(a), bn = s.SpecialForceName(b), win = $"Fleet {fid}", zone = $"Fleet {fid} SpecialForces", ground = $"Ground {spot} SpecialForces";
        gs.OpenMgmt(GameScreen.MgKind.Ground, spot, tab: "Command");
        gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: fid, tab: "Command"); gs.PlayRefresh();
        // special forces only in Command tabs, the fleet's and the planet's (Paul 2026-11-27)
        string gcmd = gs.MgPageText($"Ground {spot}", "Command"), gtroops = gs.MgPageText($"Ground {spot}", "Troops");
        Expect(gcmd.Contains("SPECIAL FORCES") && gcmd.Contains(an) && !gtroops.Contains("SPECIAL FORCES") && !gtroops.Contains(an),
            $"{spot}'s special forces stand in its Command tab, not its Troops tab: " + gcmd.Replace("\n", " / ") + " || " + gtroops.Replace("\n", " / "));
        string cmd = gs.MgPageText(win, "Command");
        int max = mm.Params.FleetSpecialForcesMax;
        // during a drag a window's page scrolls when the pointer rests near its bottom or top edge, not in the middle nor outside (review 35:
        // the special forces place sat below the fold, and nothing scrolled during a drag). A headless run has no pointer: the rule alone.
        var page = new Rect2(100, 100, 344, 580);
        float mid = GameScreen.EdgePush(page, new Vector2(200, 390)), low = GameScreen.EdgePush(page, new Vector2(200, 676)), lower = GameScreen.EdgePush(page, new Vector2(200, 679.5f)),
            high = GameScreen.EdgePush(page, new Vector2(200, 104)), outside = GameScreen.EdgePush(page, new Vector2(200, 700));
        Expect(mid == 0 && low > 0 && lower > low && high < 0 && outside == 0,
            $"a drag near a page's bottom edge scrolls it down, harder nearer the edge, near the top up, in the middle or outside not ({mid} {low} {lower} {high} {outside})");
        Expect(gs.MgHasZone(zone) && cmd.Contains("SPECIAL FORCES") && cmd.Contains($"0/{max}"), $"the Fleet window's Command tab has a place for special forces, 0/{max}: " + cmd.Replace("\n", " / "));
        string intoTroops = gs.MgJudge($"F:{a}", $"Fleet {fid} Troops");
        Expect(intoTroops != null && intoTroops.Contains("Command tab"), $"the fleet's Troops tab says where special forces ride: {intoTroops}");
        Expect(gs.MgDrop($"F:{a}", zone) == null && gs.MgDrop($"F:{b}", zone) == null, $"drag {an} and {bn} from {spot}'s Command tab onto Orbit Team");
        int from = s.Messages.Count;
        gs.PlayDay();
        var fl = s.Game.State.Fleets[fid];
        var aboard = mm.SpecialForcesAboard(fl).Select(o => o.Id).OrderBy(x => x).ToList();
        Expect(aboard.SequenceEqual(new[] { a, b }.OrderBy(x => x)), $"both ride Orbit Team the next day ({string.Join(", ", aboard)})");
        Expect(s.Messages.Skip(from).Any(m => m.Text == $"{an} boarded Orbit Team at {spot}."), "the log says each boarded: " + string.Join(" | ", s.Messages.Skip(from).Select(m => m.Text)));
        gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: fid, tab: "Command"); gs.PlayRefresh();
        cmd = gs.MgPageText(win, "Command");
        Expect(cmd.Contains($"2/{max}") && cmd.Contains(an), $"the Fleet window shows them, 2/{max}");
        Expect(gs.MgJudge($"F:{b}", ground) == null, $"{spot}'s Command tab, a world you don't hold, takes one back down: {gs.MgJudge($"F:{b}", ground)}");

        // Espionage from orbit, by a right-click on the card in the Fleet window
        var menu = gs.MenuOf($"F:{a}");
        Expect(menu.Contains("Espionage...") && gs.RunMenu($"F:{a}", "Espionage...") && gs.PlannerOpen, $"right-click {an} aboard: Espionage opens the planner ({string.Join(" | ", menu)})");
        gs.PickSystem(spot); gs.PlayRefresh();
        string plan = gs.PanelText();
        Expect(plan.Contains("From orbit: the team works from Orbit Team and never lands") && plan.Contains("halved"), "the planner says each guard's chance to spot the team is halved: " + plan.Replace("\n", " / "));
        Expect(gs.Click($"Send: Espionage on {spot}"), "send it");
        gs.PlayDay();
        var mission = s.MyMissions.LastOrDefault(x => x.SpecialForces.Contains(a));
        Expect(mission != null && mission.FromFleet == fid, $"the mission is launched from Orbit Team (from fleet {mission?.FromFleet})");

        // the other one lands on the world below
        from = s.Messages.Count;
        Expect(gs.MgDrop($"F:{b}", ground) == null, $"drag {bn} onto {spot}'s Command tab");
        gs.PlayDay();
        Expect(eco.Find(b) is { AboardShip: 0 } ob && ob.System == spot, $"{bn} stands on {spot}");
        Expect(s.Messages.Skip(from).Any(m => m.Text == $"{bn} landed from Orbit Team at {spot}."), "the log says it landed: " + string.Join(" | ", s.Messages.Skip(from).Select(m => m.Text)));

        // a fleet with no ships carries none (the core's NoCapacity, Ponytail on #415): the drop says why instead of failing quietly.
        // The ships are set aside only for this screen judgment and put back before the clock moves.
        var keep = fl.Ships.ToList(); fl.Ships.Clear();
        gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: fid, tab: "Command"); gs.PlayRefresh();
        string noShips = gs.MgJudge($"F:{b}", zone);
        fl.Ships.AddRange(keep); gs.PlayRefresh();
        Expect(noShips != null && noShips.Contains("Orbit Team has no ships"), $"a fleet with no ships refuses {bn}: {noShips}");

        // from one fleet to another here: the drop lands it, then boards it (two orders, run in the order given)
        Expect(gs.MgDrop($"F:{b}", zone) == null, $"drag {bn} back aboard Orbit Team");
        gs.PlayDay();
        int fid2 = s.Host.Fleets.AddFleet(me, "Orbit Two", new[] { ship }, spot);
        gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: fid, tab: "Command");
        gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: fid2, tab: "Command"); gs.PlayRefresh();
        string swap = gs.MgDrop($"F:{b}", $"Fleet {fid2} SpecialForces");
        Expect(swap == null, $"drag {bn} from Orbit Team onto Orbit Two: {swap}");
        gs.PlayDay();
        var fl2 = s.Game.State.Fleets[fid2];
        Expect(mm.SpecialForcesAboard(fl2).Any(o => o.Id == b) && !mm.SpecialForcesAboard(fl).Any(o => o.Id == b),
            $"{bn} rides Orbit Two the next day, not Orbit Team ({string.Join(", ", mm.SpecialForcesAboard(fl2).Select(o => o.Id))})");
        if (gs.PlannerOpen) gs.Click("Close without sending");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
    }

    void RefinedMark(GameScreen gs, GameSession s, Praedra.Core.Clock.Side me)
    {
        // which mark, from the two paces per 30 days alone. Paul 2026-11-27: a red ▼ when it falls, then "an upward trend indicator and a
        // stable indicator as well": over 105% of what is made falls, under 95% rises, between holds steady
        var W = new Func<int, int, int, GameScreen.RefinedWay, GameScreen.RefinedWay>((made, mine, hers, was) => GameScreen.TrendOf(made, mine, hers, was).Way);
        const GameScreen.RefinedWay down = GameScreen.RefinedWay.Falling, flat = GameScreen.RefinedWay.Steady, up = GameScreen.RefinedWay.Rising;
        Expect(W(36, 60, 3, flat) == down && W(70, 60, 3, flat) == up && W(64, 63, 0, flat) == flat && W(0, 0, 0, flat) == flat && W(0, 5, 0, flat) == down,
            "63 used against 36 made falls, against 70 rises, against 64 holds steady; nothing made and nothing used is steady, any use of nothing falls");
        // no blinking near balance (QA r245: 88 used against 92 made dropped the ▼ for a day or two): a mark keeps showing until balance
        Expect(W(62, 63, 0, down) == down && W(64, 63, 0, down) == flat && W(64, 63, 0, up) == up && W(62, 63, 0, up) == flat && W(62, 63, 0, flat) == flat,
            "a ▼ stays while the yards use more than is made (63 of 62), a ▲ while they use less (63 of 64); each goes back to the dash at balance, and the dash stays within 5%");
        // the words: both paces per 30 days, so made, use and the gap add up (UI review 36 P2); by how much, how long the stock lasts, the advisor's part
        string fall = gs.RefinedFlowWords(246, GameScreen.TrendOf(36, 63, 0));
        Expect(fall.Contains("Your refineries now make about 36 every 30 days. Your yards now use about 63 every 30 days. The red mark: your yards use about 27 more than your refineries make, so at this pace your 246 lasts about 273 days."),
            "the tooltip says what is made and used, by how much it falls and how long the stock lasts: " + fall);
        Expect(gs.RefinedFlowWords(0, GameScreen.TrendOf(36, 63, 0)).Contains("your stock is empty"), "an empty stock is said as empty, not as lasting 0 days");
        string rise = gs.RefinedFlowWords(246, GameScreen.TrendOf(70, 63, 0)), steady = gs.RefinedFlowWords(12, GameScreen.TrendOf(64, 63, 0)),
            dry = gs.RefinedFlowWords(0, GameScreen.TrendOf(64, 63, 0)), none = gs.RefinedFlowWords(0, GameScreen.TrendOf(0, 0, 0));
        Expect(rise.Contains("The green mark: your stock grows by about 7 every 30 days.") && !rise.Contains("red mark"), "rising, the tooltip says by how much: " + rise);
        Expect(steady.Contains("The grey dash: your yards use about what your refineries make, so your 12 holds steady.") && dry.Contains("so your stock stays empty"),
            "steady, the tooltip says the stock holds, or stays empty: " + steady + " | " + dry);
        Expect(none.Contains("Your refineries make nothing now. No yard is building now. The grey dash: nothing is made or used now, so your stock stays empty.")
                && gs.RefinedFlowWords(300, GameScreen.TrendOf(0, 0, 0)).Contains("nothing is made or used now, so your 300 holds steady."),
            "with no refinery working and no yard building it says so: " + none);
        string held = gs.RefinedFlowWords(0, GameScreen.TrendOf(40, 63, 0) with { RawShort = true });
        Expect(held.Contains("Your refineries now make about 40 every 30 days. Your mines are what holds them back: more refineries would wait for raw."),
            "when the raw holds the refineries back, the tooltip says so (UI review 37): " + held);
        if (s.HasAdvisor)
        {
            string hers = gs.RefinedFlowWords(246, GameScreen.TrendOf(36, 20, 43)), both = gs.RefinedFlowWords(246, GameScreen.TrendOf(36, 50, 13));
            Expect(hers.Contains("20 for your orders, 43 for Sibyl's") && hers.Contains("Sibyl's builds are what run it down"), "when her builds tip it, the tooltip says so: " + hers);
            Expect(both.Contains("Even without Sibyl's builds, your own orders use more"), "and when your own orders are short anyway, it says that: " + both);
        }

        // QA-132: while Sibyl builds, a yard set between her builds does not flip the mark. QA's days: the refineries make 95, five sets of hers
        // use 22 each, and a build that ends leaves its set idle at one refresh, until her next plan fills it; two such days back to back are
        // two sets, not one waiting twice (UI review 38)
        var uses = new[] { 110, 88, 110, 110, 110, 88, 88, 110, 110, 110, 110, 88, 110, 110, 88, 88, 110, 110 };
        YardSetUse U(string world, int use, bool busy, bool hers = false) => new(world, Praedra.Core.Construction.YardFamily.Shipyard, use, busy, hers);
        int Flips(bool sibyl)
        {
            var seen = new List<(int Day, IReadOnlyList<YardSetUse> Sets)>();
            var was = flat; int n = 0, ended = 0;
            for (int d = 0; d < uses.Length; d++)
            {
                int idle = uses[d] == 88 ? ended++ % 5 : -1;
                var today = Enumerable.Range(0, 5).Select(i => U($"W{i}", 22, i != idle, i != idle)).ToList();
                var (mi, he, _, _) = GameScreen.HeldUse(today, GameScreen.Yesterday(seen, 500 + d), sibyl);
                GameScreen.NoteUse(seen, 500 + d, today, sibyl);
                var w = W(95, mi, he, was);
                if (d > 0 && w != was) n++;
                was = w;
            }
            return n;
        }
        Expect(Flips(true) == 0 && Flips(false) >= 6,
            $"while Sibyl builds, a yard set between her builds leaves the ▼ alone ({Flips(true)} changes in {uses.Length} days; {Flips(false)} on today's use alone)");
        // your build at A ends and hers at C, D is lost: the next day A and C count at yesterday's use as hers (she fills them), your part is
        // today's (UI review 38 P2 1), D is not held; a second idle day is real news; a day not seen holds nothing; without her nothing is held
        var day1 = new List<YardSetUse> { U("A", 30, true), U("B", 22, true, true), U("C", 22, true, true), U("D", 7, true, true) };
        var day2 = new List<YardSetUse> { U("A", 30, false), U("B", 22, true, true), U("C", 22, false) };
        var seen2 = new List<(int Day, IReadOnlyList<YardSetUse> Sets)>();
        GameScreen.NoteUse(seen2, 600, day1, true);
        var next = GameScreen.HeldUse(day2, GameScreen.Yesterday(seen2, 601), true);
        GameScreen.NoteUse(seen2, 601, day2, true);
        var second = GameScreen.HeldUse(day2, GameScreen.Yesterday(seen2, 602), true);
        var unseen = GameScreen.HeldUse(day2, GameScreen.Yesterday(seen2, 603), true);
        var off = GameScreen.HeldUse(day2, day1, false);
        GameScreen.NoteUse(seen2, 602, day2, false);
        Expect((next.Mine, next.Hers, string.Join(",", next.Waiting), next.Today) == (0, 74, "A,C", 22)
                && (second.Mine, second.Hers, second.Waiting.Count) == (0, 22, 0) && (unseen.Hers, unseen.Waiting.Count) == (22, 0)
                && (off.Mine, off.Hers, off.Waiting.Count) == (0, 22, 0) && seen2.Count == 0,
            $"a set busy yesterday and idle today counts as hers for one day, your part is today's, a lost one and a second idle day are not held, nor without her ({next.Mine}+{next.Hers} at {string.Join(",", next.Waiting)}; {second.Mine}+{second.Hers}; {off.Mine}+{off.Hers})");
        if (s.HasAdvisor)
        {
            var f = GameScreen.TrendOf(95, 0, 110, GameScreen.RefinedWay.Falling) with { Today = 88, Waiting = new[] { "Fusoria" } };
            string between = gs.RefinedFlowWords(0, f), two = gs.RefinedFlowWords(0, f with { Waiting = new[] { "Fusoria", "Charter" } }),
                four = gs.RefinedFlowWords(0, f with { Waiting = new[] { "A", "B", "C", "D" } });
            Expect(between.Contains("While Sibyl keeps your yards busy they use about 110 every 30 days: 0 for your orders, 110 for Sibyl's. Today it is 88 every 30 days: your yards at Fusoria are between builds, and Sibyl plans their next one tomorrow.")
                    && two.Contains("your yards at Fusoria and Charter are between builds") && four.Contains("your yards at 4 worlds are between builds") && !between.Contains("now use"),
                "on a day between her builds the hover says the use the mark goes by, today's, and where (Ponytail on #427, UI review 38): " + between);
        }

        // on the game: the mark follows today's two paces, from the first refresh; 9999 and the mark keep the stock's width, so the bar does not shift
        GameScreen.RefinedWay Expected(GameScreen.RefinedWay was)   // what the paces give from the mark shown before (a mark keeps showing until balance, QA r245)
        {
            var (mine, hers) = Praedra.Core.Economy.RefinedFlow.Use(s.Host, me);
            var held = gs.RefinedTrendNow();   // the use the mark goes by: today's, and while Sibyl builds her sets between builds too (QA-132)
            bool sibyl = s.HasAdvisor && s.Advisor.IsOn(Praedra.Core.AI.SibylJob.ManageProduction);
            Expect(held.Mine == mine && (sibyl ? held.Hers >= hers : held.Hers == hers),
                $"the mark goes by today's use ({mine}+{hers}), and while Sibyl builds her sets between builds too ({held.Mine}+{held.Hers})");
            return GameScreen.TrendOf(Praedra.Core.Economy.RefinedFlow.MadePace(s.Host, me), held.Mine, held.Hers, was).Way;
        }
        gs.PlayRefresh();
        var before = gs.RefinedWayShown;
        Expect(before != null && before == Expected(before.Value), $"before the build orders the mark by Refined shows and follows the paces on day {s.Game.Day} (shown {before}): {gs.TopTips[1]}");
        // QA-131: what is made is today's pace, so refinery worlds that stop count at once, not 30 days later (all of them here: when the mines
        // are what holds the refineries back, losing one refinery world changes nothing, and the pace says so)
        var eco = s.Host.Economy;
        var worlds = eco.Objects.Where(o => o.Completed && o.Class == "Refinery" && o.System != null && eco.OwnerOf(o) == me).Select(o => o.System!).ToHashSet(StringComparer.Ordinal);
        int made = Praedra.Core.Economy.RefinedFlow.MadePace(s.Host, me);
        var was = eco.IsInUprising;
        eco.IsInUprising = sys => worlds.Contains(sys) || was(sys);
        int less = Praedra.Core.Economy.RefinedFlow.MadePace(s.Host, me);
        gs.PlayRefresh();
        string tip = gs.TopTips[1].Replace("\n", " ");   // the tooltip is wrapped
        eco.IsInUprising = was;
        gs.PlayRefresh();
        Expect(worlds.Count > 0 && less < made && (tip.Contains($"make about {less} every 30 days") || less == 0 && tip.Contains("make nothing now")),
            $"an uprising on your {worlds.Count} refinery worlds takes them out of what is made the same day ({made} -> {less}): {tip}");
        int given = gs.PlayBusyYards(20);
        var shown = gs.RefinedWayShown;
        for (int d = 0; d < 10 && shown != GameScreen.RefinedWay.Falling; d++) { gs.PlayDay(); shown = gs.RefinedWayShown; }
        Expect(shown != null && shown == Expected(shown.Value), $"the mark by Refined follows the paces on day {s.Game.Day} after {given} build orders (shown {shown}): {gs.TopTips[1]}");
        var pool = s.Game.State.Economy.Pool(me);
        int keep = pool.Refined;
        pool.Refined = 9999; gs.PlayRefresh(); gs.ForceRefinedMark();
        var (now, room) = gs.RefinedCellWidth;
        Expect(now <= room + 0.5f, $"9999 and the mark fit the Refined stock's width, so the bar does not shift ({now} of {room})");
        pool.Refined = keep; gs.PlayRefresh();
    }

    /// <summary>The screen problems of QA's Hand playthrough (2026-11-27): items 7, 11, 12 and 13.</summary>
    void ScreenFixes(GameScreen gs, GameSession s, Praedra.Core.Clock.Side me)
    {
        // item 13: at the whole-map zoom a click on your HQ's rings picks the HQ, not the dot nearest the click
        gs.View.ResetView(); gs.PlayRefresh();
        int hq = Enumerable.Range(0, gs.G.Systems.Count).FirstOrDefault(i => gs.View.IsHq?.Invoke(i) == true && gs.View.OwnerOf?.Invoke(i) == me, -1);
        if (hq >= 0)
        {
            var c = gs.View.SystemScreen(hq);
            float r = gs.View.SystemRadius + 7f;
            var dots = Enumerable.Range(0, gs.G.Systems.Count).Where(i => i != hq).Select(gs.View.SystemScreen).ToList();
            var ring = Enumerable.Range(0, 8).Select(k => c + Vector2.Right.Rotated(k * Mathf.Tau / 8) * r)
                .FirstOrDefault(p => dots.All(d => d.DistanceTo(p) > gs.View.SystemRadius + 2f), new Vector2(float.NaN, 0));
            if (!float.IsNaN(ring.X))
            {
                gs.View.Select(-1);
                gs.PlayMapClick(ring); gs.PlayRefresh();
                Expect(gs.View.Selected == hq, $"a click on {gs.G.Systems[hq].Name}'s rings at the whole-map zoom picks it (picked {(gs.View.Selected >= 0 ? gs.G.Systems[gs.View.Selected].Name : "nothing")})");
            }
            else GD.Print("  (every point of the HQ's rings is on another dot: the ring click is not checked)");
        }

        // item 12: picking a fleet from the Fleets list keeps the zoom
        var fl = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null).OrderBy(f => f.Id).FirstOrDefault();
        if (fl != null)
        {
            gs.View.ResetView(); gs.PlayRefresh();
            float z = gs.View.Zoom;
            gs.SelectFleet(fl.Id, true); gs.PlayRefresh();
            Expect(Mathf.IsEqualApprox(gs.View.Zoom, z), $"picking {fl.Name} from the list keeps the zoom ({z} -> {gs.View.Zoom})");

            // item 7: a window standing where Sibyl's panel docks steps aside when the panel opens, and goes back when it closes
            if (gs.WindowOpen) gs.Click("Advisor");
            gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: fl.Id);
            string key = $"Fleet {fl.Id}";
            gs.PlayPlaceMg(key, new Vector2(gs.DrawerLeft + 60, 60));
            var before = gs.MgRect(key).Value.Position;
            gs.ShotOpenSection("Needs"); gs.PlayRefresh();
            var aside = gs.MgRect(key).Value;
            Expect(aside.End.X <= gs.DrawerLeft + 4, $"a window over Sibyl's panel steps aside when it opens (now {aside.Position.X:0} to {aside.End.X:0}, the panel from {gs.DrawerLeft:0})");
            gs.PlayCloseDrawer(); gs.PlayRefresh();
            Expect(gs.MgRect(key).Value.Position == before, "it goes back when the panel closes");
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);

            // review 32: with no free column left of the panel, a window steps aside a head lower than the one it covers, with an edge
            var mine = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null).OrderBy(f => f.Id).Take(3).ToList();   // three: two fill the columns left of the panel, the third finds none
            if (mine.Count >= 3)
            {
                foreach (var m in mine) gs.OpenMgmt(GameScreen.MgKind.Fleet, fleet: m.Id);
                gs.PlayRefresh();
                gs.ShotOpenSection("Needs"); gs.PlayRefresh();
                var rects = mine.Select(m => (key: $"Fleet {m.Id}", r: gs.MgRect($"Fleet {m.Id}").Value)).ToList();
                Expect(rects.All(x => x.r.End.X <= gs.DrawerLeft + 4), "no window stays over Sibyl's open panel: " + string.Join(", ", rects.Select(x => $"{x.key} {x.r.Position.X:0}-{x.r.End.X:0}")));
                for (int a = 0; a < rects.Count; a++)
                    for (int b = a + 1; b < rects.Count; b++)
                    {
                        if (!rects[a].r.Intersects(rects[b].r)) continue;
                        float dy = Math.Abs(rects[a].r.Position.Y - rects[b].r.Position.Y);
                        Expect(dy >= 63, $"{rects[a].key} and {rects[b].key} overlap, one starts a head lower ({dy:0} px apart)");
                        Expect(gs.EdgeShows(rects[a].key) || gs.EdgeShows(rects[b].key), $"of {rects[a].key} and {rects[b].key}, the one on top shows its edge");
                    }
                gs.PlayCloseDrawer(); gs.PlayRefresh();
                while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
            }
        }

        // item 11: an empty General post says who can take it
        if (s.Game.State.SystemOwners.Where(kv => kv.Value == me).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault(n => s.Host.Characters.SystemGeneralOf(n) == null) is string open)
        {
            gs.OpenMgmt(GameScreen.MgKind.Ground, open, tab: "Command"); gs.PlayRefresh();
            string fold = $"Ground {open} who can be General";
            string none = gs.MgNote(fold);
            if (none != null) Expect(none.StartsWith("None", StringComparison.Ordinal), $"the empty General post of {open} says none can take it ({none})");
            else
            {
                // folded to one line (review 32), which opens the list of who can
                Expect(gs.Has(fold) && !gs.Has("General candidate"), $"the empty General post of {open} shows one folded line of who can take it");
                gs.Click(fold); gs.PlayRebuildMg();
                Expect(gs.Has("General candidate"), $"unfolded, it lists who can take the General post of {open}");
                gs.Click(fold); gs.PlayRebuildMg();
            }
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }

        // item 5 (Paul: the rule stays): a fleet in orbit sees ships, not troops, and the Ground window says so instead of "You see it now"
        var looker = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.Ships.Count > 0).OrderBy(f => f.Id).FirstOrDefault();
        string blind = gs.G.Systems.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault(x => s.OwnerOf(x) != me && s.Picture(x) is { } p && p.Ground == null && !s.IsDestroyed(x));
        if (looker != null && blind != null)
        {
            s.Host.Fleets.AddFleet(me, "Orbit Look", new[] { s.Host.Economy.Find(looker.Ships[0])!.Class }, blind);
            gs.OpenMgmt(GameScreen.MgKind.Ground, blind, tab: "Troops"); gs.PlayRefresh();
            string head = gs.MgNote($"Ground note {blind}") ?? "", troops = gs.MgNote($"Ground troops unseen {blind}") ?? "";
            if (s.KnownOwnerOf(blind) is (true, null))
            {
                // neutral, seen live: no troops can defend it (m3-assault §0), so the unknown is its defences (review 33)
                Expect(head.StartsWith("Neutral: no troops defend it", StringComparison.Ordinal), $"the head of neutral {blind} says no troops defend it ({head})");
                Expect(troops.Contains("No troops defend a neutral world") && troops.Contains("defences"), $"its Troops tab says an invasion meets only its defences ({troops})");
            }
            else
            {
                Expect(head.Contains("not its ground"), $"the head of {blind}'s Ground window says the fleet sees its orbit, not its ground ({head})");
                Expect(troops.Contains("sees ships, not troops") && troops.Contains("Espionage"), $"its Troops tab says why no troops show and what shows them ({troops})");
            }
            gs.OpenMgmt(GameScreen.MgKind.Infra, blind);
            var wide = gs.MgTooWide();
            Expect(wide.Count == 0, "no window grows past its columns (W4): " + string.Join(", ", wide));
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }

        // the red ▼ by Refined (Paul 2026-11-27): it shows when the yards use more refined than the refineries make, and its tooltip
        // says by how much, how long the stock lasts and the advisor's part; 9999 and the mark keep the frame's width, so the bar does not shift
        RefinedMark(gs, s, me);

        // special forces ride fleets and spy from orbit (Paul 2026-11-27; m5-missions §Spying from orbit): the screens
        OrbitTeam(gs, s, me);

        // item 13: the pause words that came with a report go when the report closes
        const string fought = "Paused: an invasion was fought";
        var (shown, after) = gs.PlayReportPause(fought);
        Expect(shown == fought && after != fought, $"the report's pause words go with the report (open: '{shown}', closed: '{after}')");
    }
}
