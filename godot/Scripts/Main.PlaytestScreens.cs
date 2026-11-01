using System;
using System.Linq;
using Godot;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the screen itself (the layout of 2026-10-28): the drawer tabs, Needs you and "Run until something needs me", search, the map
/// layers, the sector window, the goal, the settings, quick save, Esc, and the colours (blue is you, orange the enemy). A fresh Hand game, as
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
        Expect(!gs.WindowOpen, "the map starts with no window open");
        Expect(gs.PillText.Contains("Play"), "the pill under the search says to press Play: " + gs.PillText);
        Expect(gs.MessageCornerText.Length > 0, "the message corner shows the latest message: " + gs.MessageCornerText.Replace("\n", " / "));
        Expect(gs.Click("Menu") && gs.MenuVisible, "the menu button opens the menu");
        foreach (var item in new[] { "Needs you", "Fleets", "Leaders", "Missions", "Messages", "Sector", "Save game...", "Settings..." })
            Expect(gs.Has(item), $"the menu offers {item}");
        gs.PressKey(Key.Escape);
        Expect(!gs.MenuVisible, "Esc closes the menu");
        Expect(gs.View.SideColor(me) == Ui.You && gs.View.SideColor(s.Enemy) == Ui.Enemy, "the map draws you blue and the enemy orange");
        Expect(gs.TopText.StartsWith("Day 0"), "the clock shows the day: " + gs.TopText);

        // ---------------------------------------------------------------- a click on the map: the card, then the window on demand
        string cardSys = s.MyHqSystem ?? GameBoot.HandCapital;
        if (gs.G.TryFindSystem(cardSys, out int cardIdx)) gs.View.Select(cardIdx);
        gs.PlayRefresh();
        Expect(gs.SelectionCardOpen && !gs.WindowOpen && gs.SelectionCardText.Contains(cardSys) && gs.SelectionCardText.Contains("SUPPORT"),
            "a map click shows the selection card, not the window: " + gs.SelectionCardText.Replace("\n", " / "));
        Expect(gs.Click("Card: Details"), "click Details on the card");
        gs.PlayRefresh();
        Expect(gs.WindowOpen && gs.TabName == "Selected" && gs.PanelText().Contains(cardSys), "Details on the card opens the window on that system");
        Expect(!gs.SelectionCardOpen, "the card steps aside while the window is open");
        foreach (var tab in new[] { "Needs you", "Selected", "Fleets", "Leaders", "Missions", "Log" })
            Expect(gs.Has(tab), $"the window has the {tab} tab");
        Expect(gs.Click("Close window") && !gs.WindowOpen && gs.SelectionCardOpen, "the window's cross closes it and the card comes back");
        gs.PressKey(Key.Escape); gs.PlayRefresh();
        Expect(!gs.SelectionCardOpen && gs.View.Selected < 0, "Esc clears the selection");

        // ---------------------------------------------------------------- Needs you
        gs.OpenTab("Needs");
        var titles = gs.AttentionTitles;
        GD.Print("  needs you: " + string.Join(" | ", titles));
        Expect(gs.PanelText().Contains("Needs you"), "the Needs you tab opens");
        Expect(gs.Has("Run until something needs me"), "it offers Run until something needs me");
        int day0 = s.Game.Day;
        Expect(gs.Click("Run until something needs me") && gs.Playing, "Run until something needs me starts the clock");
        for (int i = 0; i < 400 && gs.Playing && s.Winner == null; i++) gs.Frame(0.25); // 10 days a second, whatever the speed chip
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

        // ---------------------------------------------------------------- the sector window (Paul 2026-10-31)
        Expect(!gs.SectorViewOpen, "the sector window starts closed");
        gs.PressKey(Key.S);
        string homeSec = gs.SectorOf(home);
        Expect(gs.SectorViewOpen && gs.SectorViewName == homeSec, $"S opens the sector of the headquarters ({homeSec}): {gs.SectorViewName}");
        var cards = gs.SectorViewCards.ToList();
        var members = gs.G.Systems.Where(x => !x.IsOutsideSectors && x.Sector == homeSec).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Expect(cards.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(members), $"one card for each of the {members.Count} systems of {homeSec}");
        Expect(cards.Count > 0 && cards[0] == home, "the headquarters comes first: " + string.Join(", ", cards));
        Expect(gs.SectorWindowWidth <= gs.ScreenWidth / 2 + 1, $"the sector window takes half the screen at most ({gs.SectorWindowWidth} of {gs.ScreenWidth})");
        string hc = gs.SectorCardText(home), tip = gs.SectorCardTip(home);
        GD.Print("  tile " + home + ": " + hc.Replace("\n", " / ") + " | " + tip.Replace("\n", " / "));
        Expect(hc.Contains(home) && !hc.Any(char.IsDigit), "the headquarters tile shows its name and no numbers: " + hc.Replace("\n", " / "));
        Expect(tip.Contains("Loyalty") && tip.Contains("Energy") && tip.Contains("Raw") && tip.Contains("Troops"), "its tooltip has the numbers: loyalty, energy, raw, troops");
        Expect(PlanetArt.Of(home) != null && PlanetArt.KindOf(home) != null, $"{home} has its Planet Maker picture and a world kind ({PlanetArt.KindOf(home)})");
        Expect(gs.G.Systems.All(x => PlanetArt.Of(x.Name) != null), "every system of the map has a planet picture");
        var unseen = cards.FirstOrDefault(c => !s.Explored(c) && s.OwnerOf(c) != me);
        if (unseen != null) Expect(gs.SectorCardText(unseen).Trim() == unseen && gs.SectorCardTip(unseen).Contains("unexplored") && !gs.SectorCardTip(unseen).Contains("Loyalty"),
            $"an unexplored tile ({unseen}) shows its name and planet only");
        gs.ShotSectorPane(home, "buildings"); gs.Step0();
        Expect(gs.SectorViewFocus == home && gs.SectorViewPane == GameScreen.SecPane.Buildings, "a tile opens the system window on Buildings");
        string det = gs.SectorDetailText;
        Expect(det.Contains("Mines") && det.Contains("Energy slots"), "the Buildings pane shows mines and slots: " + det.Split('\n').Take(8).Aggregate((x, y) => x + " / " + y));
        gs.ShotSectorPane(home, "fleets"); gs.Step0();
        Expect(gs.SectorViewPane == GameScreen.SecPane.Fleets && gs.SectorDetailText.Contains("Your fleets"), "the Fleets pane shows the fleets in orbit");
        gs.ShotSectorPane(home, "forces"); gs.Step0();
        Expect(gs.SectorViewPane == GameScreen.SecPane.Forces && gs.SectorDetailText.Contains("Garrison"), "the Forces pane shows the garrison");
        gs.PressKey(Key.Escape);
        Expect(gs.SectorViewOpen && gs.SectorViewFocus == null, "Esc closes the system window first");
        gs.PressKey(Key.Right);
        Expect(gs.SectorViewOpen && gs.SectorViewName != homeSec, "the Right arrow steps to the next sector: " + gs.SectorViewName);
        gs.PressKey(Key.Left);
        Expect(gs.SectorViewName == homeSec, "and Left steps back");
        gs.PressKey(Key.Escape);
        Expect(!gs.SectorViewOpen, "Esc closes the sector window");

        // ---------------------------------------------------------------- sectors on the map
        var homeSys = gs.G.Systems.First(x => x.Name == home);
        string sec = homeSys.IsOutsideSectors ? gs.G.Systems.First(x => !x.IsOutsideSectors).Sector : homeSys.Sector;
        string inSec = gs.G.Systems.First(x => !x.IsOutsideSectors && x.Sector == sec).Name;
        Expect(gs.SectorUnder(inSec) == sec, $"pointing inside {sec} finds that sector: {gs.SectorUnder(inSec)}");
        Expect(gs.SectorUnderScreen(new Vector2(-5000, -5000)) == null, "pointing at empty space far off the map finds no sector");
        gs.ShotSectorHover(sec.Replace(' ', '_'));
        Expect(gs.HoverCardText.Contains(sec) && gs.HoverCardText.Contains("Click to open"), "hovering a sector names it and says a click opens it: " + gs.HoverCardText.Replace("\n", " / "));
        gs.ClickSector(sec);
        Expect(gs.SectorViewOpen && gs.SectorViewName == sec, $"clicking {sec} on the map opens its sector window");
        gs.PressKey(Key.S);
        Expect(!gs.SectorViewOpen, "S closes it again");

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
        Expect(!gs.WindowOpen && gs.SelectedSystemIndex >= 0, "Esc first closes the window");
        gs.PressKey(Key.Escape); gs.PlayRefresh();
        Expect(gs.SelectedSystemIndex < 0, "Esc again clears the selection");
        gs.PressKey(Key.C);
        Expect(gs.CharactersOpen, "C opens Leaders");
        gs.PressKey(Key.C);
        Expect(!gs.CharactersOpen && !gs.WindowOpen, "C again closes the window");
        ShowMenu();
    }
}
