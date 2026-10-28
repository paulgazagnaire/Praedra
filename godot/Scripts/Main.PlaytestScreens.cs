using System;
using System.Linq;
using Godot;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the screen itself (the layout of 2026-10-28): the drawer tabs, Needs you and "Run until something needs me", search, the map
/// layers, the sector windows, the goal, the settings, quick save, Esc, and the colours (blue is you, orange the enemy). A fresh Hand game, as
/// Paul plays it. Every action is a click on the real button or a key press; the core is only read to check the result.
/// </summary>
public partial class Main
{
    void PlaytestScreens()
    {
        GD.Print("=== PLAYTEST screens (Hand) ===");
        ShowMenu();
        Expect(UiTest.Click(_screen, "Hand") && UiTest.Click(_screen, "New Game"), "a new Hand game");
        var gs = Gs;
        if (gs == null) return;
        var s = gs.S; var me = s.Player;
        if (gs.HowToVisible) gs.Click("Got it");
        gs.PlayRefresh();

        // ---------------------------------------------------------------- the frame
        foreach (var tab in new[] { "Needs you", "Selected", "Fleets", "Leaders", "Missions", "Log" })
            Expect(gs.Has(tab), $"the drawer has the {tab} tab");
        Expect(gs.View.SideColor(me) == Ui.You && gs.View.SideColor(s.Enemy) == Ui.Enemy, "the map draws you blue and the enemy orange");
        Expect(gs.TopText.StartsWith("Day 0"), "the top bar shows the day: " + gs.TopText);

        // ---------------------------------------------------------------- Needs you
        gs.OpenTab("Needs");
        var titles = gs.AttentionTitles;
        GD.Print("  needs you: " + string.Join(" | ", titles));
        Expect(gs.PanelText().Contains("Needs you"), "the Needs you tab opens");
        Expect(gs.Has("Run until something needs me"), "it offers Run until something needs me");
        int day0 = s.Game.Day;
        Expect(gs.Click("Run until something needs me") && gs.Playing, "Run until something needs me starts the clock");
        for (int i = 0; i < 400 && gs.Playing && s.Winner == null; i++) gs.Frame(0.25);
        Expect(!gs.Playing && s.Game.Day > day0, $"it stopped by itself on day {s.Game.Day}: {gs.Banner}");
        if (gs.ReportVisible) gs.Click("Close");
        gs.PlayRefresh();

        // ---------------------------------------------------------------- search
        gs.SetSearch("chart");
        Expect(gs.SearchHits.Count > 0 && gs.SearchHits[0].StartsWith("Charter", StringComparison.Ordinal), "search 'chart' finds Charter first: " + string.Join(" | ", gs.SearchHits));
        gs.SubmitSearch(); gs.PlayRefresh();
        Expect(gs.SelectedSystemName == "Charter" && gs.TabName == "Selected", "Enter selects Charter and shows it");
        Expect(gs.PanelText().Contains("Charter"), "the Selected tab shows Charter");
        var anyFleet = s.Game.State.Fleets.Values.Where(f => f.Side == me).OrderBy(f => f.Id).First();
        gs.SetSearch(anyFleet.Name);
        Expect(gs.SearchHits.Any(h => h.StartsWith(anyFleet.Name + "   fleet", StringComparison.Ordinal)), $"search finds the fleet {anyFleet.Name}");
        string unexplored = gs.G.Systems.Select(x => x.Name).Where(n => !s.Explored(n)).OrderBy(n => n, StringComparer.Ordinal).First();
        gs.SetSearch(unexplored);
        Expect(gs.SearchHits.Any(h => h.StartsWith(unexplored, StringComparison.Ordinal) && h.Contains("unexplored")), $"search says {unexplored} is unexplored, nothing more");
        gs.SetSearch("");

        // search while moving a fleet: the picked system becomes the destination
        var mover = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && s.Host.Fleets.SpeedOf(f) > 0 && !s.PendingOf(f.Id).Any()).OrderBy(f => f.Id).FirstOrDefault();
        if (mover != null)
        {
            string dest = s.Game.State.SystemOwners.Where(kv => kv.Value == me && kv.Key != mover.At).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault(n => s.Host.PlanMove(me, mover.Id, n) is { Ok: true, Red: false });
            if (dest != null)
            {
                gs.SelectFleet(mover.Id, true); gs.PlayRefresh();
                Expect(gs.Click("Move..."), "Move...");
                int q = s.QueuedOrders;
                gs.SetSearch(dest); gs.SubmitSearch();
                Expect(s.QueuedOrders == q + 1 && s.PlannedMoveOf(mover.Id)?.To == dest, $"searching {dest} while moving sends the fleet there");
                Expect(gs.ToastText.Contains($"{mover.Name} to {dest}: arrives day"), "a toast confirms the order with the arrival day: " + gs.ToastText);
            }
        }

        // ---------------------------------------------------------------- map layers
        string home = s.MyHqSystem ?? GameBoot.HandCapital;
        Expect(gs.LayerName == "Owners" && gs.MarkOf(home) == null, "the map starts on the Owners layer, with no halos");
        gs.ShotLayer("Support");
        Expect(gs.MarkOf(home) != null, "Support layer: your capital has a halo");
        Expect(gs.MarkOf(unexplored) == null, "Support layer: an unexplored system has none (nothing is known there)");
        gs.PressKey(Key.V);
        Expect(gs.LayerName == "Trouble", "V moves to the next layer: " + gs.LayerName);
        var idleYard = s.Game.State.SystemOwners.Where(kv => kv.Value == me).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(n => new[] { Praedra.Core.Construction.YardFamily.Shipyard, Praedra.Core.Construction.YardFamily.Construction, Praedra.Core.Construction.YardFamily.Training }
                .Any(f => s.Host.Economy.Objects.Any(o => o.System == n && o.Completed && o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)
                    && Praedra.Core.Construction.Yards.FamilyOf(k) == f) && s.Host.Construction.ProjectAt(n, f) is not { Units.Count: > 0 }));
        gs.ShotLayer("Yards");
        if (idleYard != null) Expect(gs.MarkOf(idleYard) is { } m && m.Color == Ui.Warn, $"Yards layer: the idle yard at {idleYard} is marked yellow");
        gs.ShotLayer("Intel");
        Expect(gs.MarkOf(home) != null && gs.MarkOf(unexplored) == null, "Intel age layer: live where you see, nothing where you never looked");
        gs.ShotLayer("Owners");

        // ---------------------------------------------------------------- sector windows
        Expect(!gs.SectorDockOpen, "the sector windows start closed");
        gs.PressKey(Key.S);
        Expect(gs.SectorDockOpen && gs.SectorWindows.Count == 2, "S opens two sector windows under the map");
        var w0 = gs.SectorWindows[0];
        var rows = w0.Rows().ToList();
        GD.Print($"  sector {w0.Sector}: " + string.Join(" / ", rows.Take(6)));
        Expect(rows.Count > 0 && rows.All(r => gs.G.Systems.Any(x => x.Name == r.Split(" | ")[0] && x.Sector == w0.Sector)), $"the first window lists the systems of {w0.Sector}");
        var hidden = gs.SectorWindows.SelectMany(w => w.Rows()).FirstOrDefault(r => !s.Explored(r.Split(" | ")[0]) && s.OwnerOf(r.Split(" | ")[0]) != me);
        if (hidden != null) Expect(hidden.EndsWith("| unexplored", StringComparison.Ordinal), "an unexplored row shows nothing but 'unexplored': " + hidden);
        string rowSys = rows[0].Split(" | ")[0];
        Expect(w0.Click(rowSys) && gs.SelectedSystemName == rowSys, $"clicking the row of {rowSys} selects it");
        gs.PressKey(Key.S);
        Expect(!gs.SectorDockOpen, "S closes them");

        // ---------------------------------------------------------------- sectors on the map
        var homeSys = gs.G.Systems.First(x => x.Name == home);
        string sec = homeSys.IsOutsideSectors ? gs.G.Systems.First(x => !x.IsOutsideSectors).Sector : homeSys.Sector;
        string inSec = gs.G.Systems.First(x => !x.IsOutsideSectors && x.Sector == sec).Name;
        Expect(gs.SectorUnder(inSec) == sec, $"pointing inside {sec} finds that sector: {gs.SectorUnder(inSec)}");
        Expect(gs.SectorUnderScreen(new Vector2(-5000, -5000)) == null, "pointing at empty space far off the map finds no sector");
        gs.ShotSectorHover(sec.Replace(' ', '_'));
        Expect(gs.HoverCardText.Contains(sec) && gs.HoverCardText.Contains("Click to open"), "hovering a sector names it and says a click opens it: " + gs.HoverCardText.Replace("\n", " / "));
        gs.ClickSector(sec);
        Expect(gs.SectorDockOpen && gs.SectorWindows.Any(w => w.Sector == sec), $"clicking {sec} on the map opens its sector table");
        gs.PressKey(Key.S);
        Expect(!gs.SectorDockOpen, "S closes it again");

        // ---------------------------------------------------------------- the goal
        Expect(gs.GoalButtonText.StartsWith("Goal: 0/2 leaders held", StringComparison.Ordinal), "the Goal button counts the leaders you hold: " + gs.GoalButtonText);
        Expect(gs.Click("Goal") && gs.GoalVisible, "the Goal button opens the goal");
        string goal = gs.GoalText;
        Expect(goal.Contains("Take the Hearth") && goal.Contains("Hold Nobody prisoner") && goal.Contains("Hold Mae Abara prisoner"), "the Hand's win terms are listed: " + goal.Replace("\n", " / "));
        Expect(goal.Contains("Where it is: unknown"), "the Hearth's place is unknown at the start");
        Expect(goal.Contains("Cassia Ravilla") && goal.Contains("The Chair") && goal.Contains("Charter"), "what the Collective needs from you is listed");
        Expect(goal.Contains("70%") || goal.Contains($"{s.Host.Victory.Rules.DominanceSharePercent}%"), "the win by holding the galaxy is explained");
        gs.PressKey(Key.Escape);
        Expect(!gs.GoalVisible && !gs.AnyModal, "Esc closes it");

        // ---------------------------------------------------------------- settings
        Expect(gs.Click("Menu") && gs.Click("Settings...") && gs.SettingsVisible, "Menu, Settings... opens the settings");
        bool before = UiSettings.PauseUprising;
        Expect(gs.SetCheck("an uprising starts", !before) && UiSettings.PauseUprising == !before, "a pause switch can be ticked");
        gs.SetCheck("an uprising starts", before);
        gs.PressKey(Key.Escape);
        Expect(!gs.SettingsVisible, "Esc closes the settings");

        // ---------------------------------------------------------------- quick save, Esc
        Forget(GameBoot.SaveSlot);
        gs.PressKey(Key.S, ctrl: true);
        Expect(GameBoot.SaveExists(GameBoot.SaveSlot) && gs.ToastText.Contains("Game saved in slot 1"), "Ctrl+S saves to slot 1: " + gs.ToastText);
        gs.SelectSystem(home); gs.PlayRefresh();
        gs.PressKey(Key.Escape); gs.PlayRefresh();
        Expect(gs.SelectedSystemIndex < 0, "Esc clears the selection");
        gs.PressKey(Key.C);
        Expect(gs.CharactersOpen, "C opens Leaders");
        gs.PressKey(Key.C);
        Expect(gs.TabName == "Needs", "C again goes back to Needs you");
        ShowMenu();
    }
}
