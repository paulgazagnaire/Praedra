using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

namespace PraedraViewer;

/// <summary>
/// Playtest of the Fleets window (Paul 2026-11-21: merge fleets, pick single ships, see their stats and what they carry, give one or
/// several fleets an order): F opens it; a click picks one fleet, Shift+click adds one or takes it out, on the list and on the map; a
/// ship's card shows what it carries, a click shows its damage and its class; several ships split off; Merge joins the fleets picked,
/// there or by flying to the first; Move and a right-click on the map send every fleet picked. Every click is a real button's Pressed signal.
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

        // ---- F opens the window; one fleet shown, every fleet listed
        gs.PressKey(Key.F); gs.PlayRefresh();
        string key = gs.MgmtKeys.FirstOrDefault(k => k.StartsWith("Fleet ", StringComparison.Ordinal));
        Expect(key != null, $"F opens the Fleets window ({string.Join(", ", gs.MgmtKeys)})");
        if (key == null) return;
        gs.PlayPickFleet(a.Id, false); gs.PlayRefresh();
        key = $"Fleet {a.Id}";
        string text = gs.MgText(key);
        var mine = s.Game.State.Fleets.Values.Where(f => f.Side == me).ToList();
        Expect(mine.All(f => text.Contains(f.Name)), "every fleet of yours is in the list");
        Expect(text.Contains(a.Name.ToUpperInvariant()) && text.Contains("SHIPS") && text.Contains("STRENGTH") && text.Contains("SLOWEST"), "a click shows the fleet: its name and its numbers");
        Expect(gs.FleetPicks.SequenceEqual(new[] { a.Id }) && gs.View.PickedFleets.SetEquals(new[] { a.Id }), "it alone is picked, and the map draws it as picked");

        // ---- Shift+click adds a fleet, and takes it out
        gs.PlayPickFleet(b.Id, true); gs.PlayRefresh();
        text = gs.MgText($"Fleet {b.Id}");
        Expect(gs.FleetPicks.SequenceEqual(new[] { a.Id, b.Id }) && text.Contains("2 FLEETS") && text.Contains("FLEETS PICKED"), "Shift+click adds a second fleet: the window shows both");
        Expect(gs.Has($"Fleets Merge") && gs.Enabled("Fleets Merge") && gs.TipOf("Fleets Merge").Contains(a.Name), $"Merge offers to join them into {a.Name}, the first picked");
        gs.PlayPickFleet(b.Id, true); gs.PlayRefresh();
        Expect(gs.FleetPicks.SequenceEqual(new[] { a.Id }), "Shift+click again takes it out");
        // the same from the map: Shift+click on a fleet's marker
        gs.PlayShiftClickFleet(b.Id); gs.PlayRefresh();
        Expect(gs.FleetPicks.Contains(b.Id) && gs.View.PickedFleets.Contains(b.Id), "Shift+click on its marker on the map adds it too");
        gs.PlayShiftClickFleet(b.Id); gs.PlayRefresh();
        Expect(!gs.FleetPicks.Contains(b.Id), "and takes it out");

        // ---- ships: one card each, with its cargo; a click shows the ship
        gs.PlayPickFleet(a.Id, false); gs.PlayRefresh();
        var eco = s.Host.Economy;
        var carrier = a.Ships.Select(eco.Find).FirstOrDefault(o => o != null && fm.ClassOf(o) is { } c && c.FighterCapacity + c.TroopCapacity > 0);
        if (carrier != null)
        {
            var cc = fm.ClassOf(carrier)!;
            string room = cc.FighterCapacity > 0 ? $"{fm.UnitsAboard(carrier.Id, ObjectCategory.Fighter).Count()}/{cc.FighterCapacity}" : $"{fm.UnitsAboard(carrier.Id, ObjectCategory.Regiment).Count()}/{cc.TroopCapacity}";
            Expect(gs.MgPageText(key, "Ships").Contains(room), $"a ship's card says what it carries out of its room ({room})");
        }
        var ship = eco.Find(a.Ships[0])!;
        var sc = fm.ClassOf(ship)!;
        // damage, test only: half its hull and two hits on its weapons, put back after
        int hull0 = ship.Hull; var hits0 = ship.Hits?.ToList();
        ship.Hull = sc.Hull / 2;
        if (ship.Hits is { Count: > 1 }) ship.Hits[1] = 2;
        gs.PlayPick($"S:{ship.Id}", false); gs.PlayRefresh();
        string page = gs.MgPageText(key, "Ships");
        Expect(page.Contains($"hull {sc.Hull / 2} of {sc.Hull}") && (ship.Hits is not { Count: > 1 } || page.Contains("weapons (2)")), "a click on a ship shows its hull and its damage in words");
        Expect(page.Contains($"EVERY {ship.Class.ToUpperInvariant()}") && page.Contains("Trip time"), "and the numbers of its class");
        Expect(page.Contains("IT CARRIES") || page.Contains("It carries no fighters"), "and what it carries");
        ship.Hull = hull0; if (hits0 != null) ship.Hits = hits0;
        if (a.Ships.Count >= 3)
        {
            gs.PlayPick($"S:{a.Ships[1]}", true); gs.PlayRefresh();
            Expect(gs.MgPageText(key, "Ships").Contains("2 SHIPS PICKED") && gs.Enabled("Fleets Split"), "Shift+click a second ship: they can be split off together");
            int q0 = s.QueuedOrders;
            Expect(gs.Click("Fleets Split") && s.QueuedOrders == q0 + 1, "Split them off sends one split order");
            gs.MgText(key);   // as the next frame would
            Expect(gs.Click($"Cancel: Split 2 ships off {a.Name}") && s.QueuedOrders == q0, "and its Cancel takes it back");
        }
        gs.ClearUnitPicks();

        // ---- Merge: the fleets picked join the first; one standing with it merges at once
        gs.PlayPickFleet(a.Id, false); gs.PlayPickFleet(b.Id, true); gs.PlayRefresh();
        int ships0 = a.Ships.Count;
        Expect(gs.Click("Fleets Merge"), $"Merge into {a.Name}");
        gs.PlayDay(); gs.PlayRefresh();
        Expect(!s.Game.State.Fleets.ContainsKey(bid) && a.Ships.Count == ships0 + 1, $"next day {b.Name} has merged into {a.Name}");
        Expect(gs.MgmtKeys.Contains($"Fleet {a.Id}") && gs.FleetPicks.SequenceEqual(new[] { a.Id }), "the window shows the merged fleet");

        // ---- one Move for several fleets, and the map's right-click
        still = Still();
        if (still.Count >= 2)
        {
            var x = still[0]; var y = still[1];
            string to = s.Data.Galaxy.Systems.Select(z => z.Name).Where(n => n != x.At && n != y.At && !s.IsDestroyed(n))
                .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault(n => s.Host.PlanMove(me, x.Id, n, null) is { Ok: true, Red: false } && s.Host.PlanMove(me, y.Id, n, null) is { Ok: true, Red: false });
            if (to != null)
            {
                gs.PlayPickFleet(x.Id, false); gs.PlayPickFleet(y.Id, true); gs.PlayRefresh();
                Expect(gs.Click("Fleets Move"), "Move for the two fleets picked");
                gs.PickSystem(to);
                Expect(s.PlannedMoveOf(x.Id)?.To == to && s.PlannedMoveOf(y.Id)?.To == to, $"one click on {to} sends both");
                gs.MgText($"Fleet {y.Id}");   // as the next frame would
                Expect(gs.Click($"Cancel: Move {x.Name} to {to}") && gs.Click($"Cancel: Move {y.Name} to {to}") && s.PlannedMoveOf(x.Id) == null && s.PlannedMoveOf(y.Id) == null, "each order can be cancelled");
                gs.PlayRightClick(to);
                Expect(s.PlannedMoveOf(x.Id)?.To == to && s.PlannedMoveOf(y.Id)?.To == to, $"a right-click on {to} on the map sends both too");
                gs.MgText($"Fleet {y.Id}");
                gs.Click($"Cancel: Move {x.Name} to {to}"); gs.Click($"Cancel: Move {y.Name} to {to}");
            }
            else GD.Print("  (no system both fleets reach without a known trap: the shared move is not checked on this side)");
        }
        else GD.Print("  (one fleet standing still: the shared move is not checked on this side)");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        Expect(gs.View.PickedFleets.Count == 0, "closing the window lets the map forget the fleets picked");
    }
}
