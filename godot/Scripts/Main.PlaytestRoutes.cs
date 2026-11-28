using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the move preview's trap warning and shift-click waypoints (rule: m1-travel §Path rule (3), (5); Paul 2026-10-16: the game
/// proposes the fastest path, turns it red when it crosses a known enemy interdictor, never detours on its own, and waypoints force any path).
/// A fresh Collective game: a Hand Subpoena is planted, in sight, on the way of a long move; the preview must turn red and name it,
/// a waypoint around it must clear the warning, a waypoint onto it must keep the warning, and the order sent must fly the previewed path.
/// </summary>
public partial class Main
{
    void PlaytestTrapsAndWaypoints()
    {
        GD.Print("=== PLAYTEST traps and waypoints ===");
        ShowMenu();
        var menu = _screen as StartMenu;
        Expect(menu != null && UiTest.Click(menu, "Collective") && UiTest.Click(menu, "Medium") && UiTest.Click(menu, "New Game"), "a new Collective game for the trap test");
        var gs = Gs;
        if (gs == null) return;
        var s = gs.S; var me = s.Player; var fm = s.Host.Fleets; var host = s.Host;
        if (gs.HowToVisible) gs.Click("Got it");

        var mover = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && fm.SpeedOf(f) > 0).OrderBy(f => f.Id).FirstOrDefault();
        Expect(mover != null, "a fleet that can move");
        if (mover == null) return;
        // a destination whose fastest path stops at least twice on the way
        string dest = gs.G.Systems.Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(n => host.PlanMove(me, mover.Id, n) is { Ok: true } p && p.Path.Count >= 4 && !p.Red);
        Expect(dest != null, "a destination with stops on the way");
        if (dest == null) return;
        var clean = host.PlanMove(me, mover.Id, dest);
        string trap = clean.Path[clean.Path.Count / 2];
        string origin = mover.At!;
        GD.Print($"  {mover.Name}: {mover.At} -> {dest} by {string.Join(", ", clean.Path)}; interdictor planted at {trap}");

        var before = gs.PlanWithWaypoints(mover.Id, Array.Empty<string>(), dest, confirm: false);
        Expect(!before.Red && !before.Banner.Contains("TRAP"), "with no known interdictor the preview is not red: " + before.Banner);

        // a destination picked with Shift held (Paul's habit from picking fleets) is only a waypoint: the bar says "Not sent yet" and how
        // to send; a second Shift+click on it, or Enter, sends the move (Paul 2026-11-24: "my fleet doesn't leave")
        int q0 = s.QueuedOrders;
        SelectFleetByList(gs, mover);
        Expect(gs.Click("Move..."), "click Move... for the Shift+click check");
        gs.ShiftPickSystem(dest);
        Expect(s.QueuedOrders == q0 && gs.OrderBarText.Contains("Not sent yet") && gs.OrderBarText.Contains($"press Enter to send it to {dest}"),
            "a Shift+click on the destination only adds a waypoint and the bar says it is not sent yet: " + gs.OrderBarText.Replace("\n", " / "));
        gs.ShiftPickSystem(dest);
        Expect(s.QueuedOrders == q0 + 1 && s.PlannedMoveOf(mover.Id) is { } shiftSent && shiftSent.To == dest && gs.OrderBarText == "",
            $"a second Shift+click on {dest} sends the move there");
        Expect(s.Messages.Any(m => m.Text == $"Order sent: move {mover.Name} to {dest}."), "the order is not sent via its own destination: " + s.Messages[^1].Text);
        gs.TakeBackMove(mover.Id);
        Expect(gs.Click("Move..."), "click Move... for the Enter check");
        gs.ShiftPickSystem(dest);
        gs.PressKey(Key.Enter);
        Expect(s.QueuedOrders == q0 + 1 && s.PlannedMoveOf(mover.Id) is { } enterSent && enterSent.To == dest, "Enter sends the move to the last waypoint");
        gs.TakeBackMove(mover.Id);
        Expect(s.QueuedOrders == q0, "the test's move is taken back");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);

        s.OpponentIdle = true; // the Hand AI would fly the planted ship away before the picture is taken
        fm.SetOwner(trap, me); // the Collective holds it, so it sees the fleets there live
        fm.AddFleet(Side.Hand, "Net", new[] { "Subpoena" }, trap);
        gs.PlayDay(); // the side's picture is taken in the day step
        var red = gs.PlanWithWaypoints(mover.Id, Array.Empty<string>(), dest, confirm: false);
        Expect(red.Red && red.Banner.Contains("TRAP") && red.Banner.Contains(trap) && red.Traps.Contains(trap),
            $"the preview turns red, names {trap} and circles it: " + red.Banner);
        Expect(red.Path.SequenceEqual(clean.Path), "the game does not detour on its own: the red path is still the fastest one");

        // a waypoint that steers around the trap
        string around = gs.G.Systems.Select(x => x.Name).Where(n => n != mover.At && n != dest && n != trap).OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(n => host.PlanMove(me, mover.Id, dest, new[] { n }) is { Ok: true, Red: false } p && p.Path.Contains(n));
        Expect(around != null, "some waypoint avoids the trap");
        if (around != null)
        {
            var steer = gs.PlanWithWaypoints(mover.Id, new[] { around }, dest, confirm: false);
            Expect(!steer.Red && steer.Path.Contains(around) && !steer.Path.Contains(trap) && steer.Banner.Contains("via " + around),
                $"a waypoint at {around} clears the warning and the path goes through it: " + steer.Banner);
        }

        // a waypoint onto the trap: the player may fly into it on purpose, the warning stays
        var into = gs.PlanWithWaypoints(mover.Id, new[] { trap }, dest, confirm: false);
        Expect(into.Red && into.Path.Contains(trap), "a waypoint onto the trap keeps the red warning: " + into.Banner);

        // the same seen fleet warns a leader sent there alone: caught on arrival if it stays (PR #440)
        PlaytestCaughtWarning(gs, trap);

        // a plain click on the red path sends nothing: the bar at the bottom offers Go anyway / Go around / Cancel; Go around flies a trap-free path
        int queued = s.QueuedOrders;
        SelectFleetByList(gs, mover);
        Expect(gs.Click("Move..."), "click Move...");
        gs.HoverTextFor(dest);
        gs.PickSystem(dest);
        Expect(s.QueuedOrders == queued, "a click on a red path sends no order");
        Expect(gs.OrderBarText.Contains("TRAP") && gs.Has("Go anyway") && gs.Has("Go around via") && gs.Has("Cancel (Esc)"),
            "the order bar names the trap and offers Go anyway, Go around and Cancel: " + gs.OrderBarText.Replace("\n", " / ") + " | " + string.Join(" | ", gs.ButtonTexts().Where(t => t.StartsWith("Go"))));
        Expect(gs.Click("Go around via"), "click Go around");
        var planned = s.PlannedMoveOf(mover.Id);
        var plannedPath = planned?.Path ?? (IReadOnlyList<string>)new List<string>();
        Expect(s.QueuedOrders == queued + 1 && planned != null && !plannedPath.Contains(trap), "Go around sends the order on a path without the trap: " + string.Join(", ", plannedPath));
        Expect(s.Messages.Any(m => m.Text.StartsWith($"Order sent: move {mover.Name} to {dest} via ", StringComparison.Ordinal)), "the order message names the waypoint");
        gs.PlayDay();
        var f = s.Game.State.Fleets.GetValueOrDefault(mover.Id);
        Expect(f?.Transit != null && planned != null && f.Transit.Path.SequenceEqual(plannedPath), $"the fleet flies the previewed path: {string.Join(", ", f?.Transit?.Path ?? new List<string>())}");

        // several fleets sent together (picked on the map, sent by a right-click): each flies its own path, so each is checked; one on a
        // red path keeps the click from sending, and the bar can send only the ones with no trap (test only: two fleets of the mover's
        // first ship class, one where the mover started, so its path crosses the trap, one at the waypoint that steered around it)
        if (around == null) return;
        string cls = s.Host.Economy.Find(mover.Ships[0])?.Class ?? "";
        int cid = fm.AddFleet(me, "Clear Way", new[] { cls }, around);
        int rid = fm.AddFleet(me, "Red Way", new[] { cls }, origin);
        if (host.PlanMove(me, cid, dest) is not { Ok: true, Red: false } || host.PlanMove(me, rid, dest) is not { Ok: true, Red: true })
        {
            GD.Print("  (no clean and red pair for the several-fleets trap check on this map)");
            return;
        }
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        SelectFleetByList(gs, s.Game.State.Fleets[cid]);
        gs.HoverTextFor(dest);   // the map takes in the selection before the Shift+click
        gs.PlayShiftClickFleet(rid);
        Expect(gs.View.PickedFleets.SetEquals(new[] { cid, rid }), "Shift+click a second fleet on the map: both are picked, the selected one first");
        queued = s.QueuedOrders;
        gs.PlayRightClick(dest);
        Expect(s.QueuedOrders == queued && gs.OrderBarText.Contains("TRAP") && gs.OrderBarText.Contains("Red Way") && !gs.OrderBarText.Contains("Clear Way out"),
            "the second fleet's own path crosses the trap: the click sends nothing and the bar names it: " + gs.OrderBarText.Replace("\n", " / "));
        Expect(gs.Has("Go anyway") && gs.Has("Send only the 1 fleet with no trap") && !gs.Has("Go around via"),
            "the bar offers Go anyway and Send only the fleet with no trap: " + string.Join(" | ", gs.ButtonTexts().Where(t => t.StartsWith("Go") || t.StartsWith("Send"))));
        Expect(gs.Click("Send only the 1 fleet with no trap") && s.PlannedMoveOf(cid)?.To == dest && s.PlannedMoveOf(rid) == null && s.QueuedOrders == queued + 1,
            "Send only the safe ones sends Clear Way and keeps Red Way where it is");
        gs.ShotWindow("fleet", cid.ToString()); gs.MgText($"Fleet {cid}");
        Expect(gs.Click($"Cancel: Move Clear Way to {dest}") && s.QueuedOrders == queued, "and its order can be cancelled in its window");
        int open = gs.MgmtCount;
        gs.PressKey(Key.Escape);
        Expect(gs.View.PickedFleets.Count == 0 && gs.MgmtCount == open, "Esc lets the fleets picked on the map go before it closes a window");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
    }

    /// <summary>
    /// A leader sent alone to a system where the player's picture shows an armed enemy fleet is told, on the drag hint and on the map's move bar,
    /// that they are caught on arrival if it stays; a system with no enemy fleet in sight says nothing (rule: m5-characters-and-talent §Moving a
    /// character alone, Caught on arrival, switch FleetsCatchLoneLeaders DEFAULT on; the Dev lead's ask from Ponytail on #440). The planted Net
    /// at <paramref name="trap"/> is that fleet; the move is previewed, never sent.
    /// </summary>
    void PlaytestCaughtWarning(GameScreen gs, string trap)
    {
        var s = gs.S; var me = s.Player; var fm = s.Host.Fleets; var cm = s.Host.Characters;
        bool ArmedHere(Side side, string at) => s.Game.State.Fleets.Values.Any(f => f.Side == side && f.At == at && f.Transit == null
            && Praedra.Core.Fleets.CombatStrength.IsArmed(fm.Classes(f)));
        Expect(cm.Params.FleetsCatchLoneLeaders, "fleets catch lone leaders by default");
        Expect(ArmedHere(me == Side.Hand ? Side.Collective : Side.Hand, trap) && !ArmedHere(me, trap) && s.VisibleEnemyFleetsAt(trap).Count > 0,
            $"the Net at {trap} is armed and in sight, and no armed fleet of yours is there");
        var lone = s.MyCharacters.Where(c => c.Status == Praedra.Core.Characters.CharacterStatus.Free && c.AttachedFleet == 0 && c.Transit == null
                && c.System != null && c.System != trap && s.SystemPostOf(c) == null && fm.FindPath(c.System, trap, cm.Params.CharacterSpeed).Ok)
            .OrderBy(c => c.Id).FirstOrDefault();
        Expect(lone != null, $"a free leader who can travel to {trap}");
        if (lone == null) return;
        string name = s.CharName(lone.Id);
        string words = $"Armed enemy fleet at {trap}: if it stays, {name} is caught on arrival.";
        Expect(gs.CaughtWarning(name, trap) == words, $"the warning for {trap}: {gs.CaughtWarning(name, trap) ?? "none"}");
        string calm = s.Data.Galaxy.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == me && n != trap && !ArmedHere(me, n) && s.VisibleEnemyFleetsAt(n).Count == 0)
            .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        if (calm != null) Expect(gs.CaughtWarning(name, calm) == null, $"no warning for {calm}, where no enemy fleet is in sight");

        // the drag hint: dropping the leader on the Leaders of the planet there adds the warning, amber, under what the drop does
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        gs.ShotWindow("ground", trap); gs.PlayRefresh();
        string hint = gs.PlayDropHint($"L:{lone.Id}", $"Ground {trap} Leaders");
        Expect(hint.EndsWith("\n" + words, StringComparison.Ordinal), $"dragging {name} over {trap}'s Leaders warns: " + hint.Replace("\n", " / "));
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);

        // the map's move bar: the trip on the first line, the warning on an amber second line; Esc sends nothing
        int queued = s.QueuedOrders;
        if (!gs.CharactersOpen) gs.ClickViaMenu("Leaders");
        gs.PlayRefresh();
        Expect(gs.Click($"Move {name}..."), $"click 'Move {name}...' in the Leaders tab");
        gs.HoverTextFor(trap);
        Expect(gs.OrderBarText.EndsWith("Click to confirm.\n" + words, StringComparison.Ordinal) && gs.OrderBodyColor == Ui.Warn,
            $"the move bar warns on an amber second line: " + gs.OrderBarText.Replace("\n", " / "));
        if (calm != null && calm != lone.System)
        {
            gs.HoverTextFor(calm);
            Expect(gs.OrderBarText.TrimEnd('\n').EndsWith("Click to confirm.", StringComparison.Ordinal) && gs.OrderBodyColor != Ui.Warn,
                $"hovering {calm} drops the warning: " + gs.OrderBarText.Replace("\n", " / "));
        }
        gs.PressKey(Key.Escape);
        Expect(s.QueuedOrders == queued && lone.Transit == null, "Esc cancels: no order sent");
        if (gs.CharactersOpen) gs.PressKey(Key.C);
        gs.View.Select(-1); gs.PlayRefresh();
    }
}
