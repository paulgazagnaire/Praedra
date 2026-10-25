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
        GD.Print($"  {mover.Name}: {mover.At} -> {dest} by {string.Join(", ", clean.Path)}; interdictor planted at {trap}");

        var before = gs.PlanWithWaypoints(mover.Id, Array.Empty<string>(), dest, confirm: false);
        Expect(!before.Red && !before.Banner.Contains("TRAP"), "with no known interdictor the preview is not red: " + before.Banner);

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

        // the order flies the previewed path
        if (around != null)
        {
            var sent = gs.PlanWithWaypoints(mover.Id, new[] { around }, dest, confirm: true);
            Expect(s.Messages.Any(m => m.Text.StartsWith($"Order sent: move {mover.Name} to {dest} via {around}", StringComparison.Ordinal)), "the order message names the waypoint");
            gs.PlayDay();
            var f = s.Game.State.Fleets.GetValueOrDefault(mover.Id);
            Expect(f?.Transit != null && f.Transit.Path.SequenceEqual(sent.Path), $"the fleet flies the previewed path: {string.Join(", ", f?.Transit?.Path ?? new List<string>())}");
        }
    }
}
