using System;
using System.Linq;
using Godot;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the screen itself (the layout of 2026-10-28): the drawer tabs, Needs you, search, the map
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
        Expect(gs.PillText.Contains("Play"), "the pill under the top bar says to press Play: " + gs.PillText);
        string notesWhy = gs.NotesCornerProblem();
        Expect(notesWhy == null, "news has one place, the bottom-right corner: one-line notes that fade, no card left from older news (map declutter, pick 7)"
            + (notesWhy != null ? ": " + notesWhy : ""));
        Expect(GameScreen.NeedsCount(1, 0) == "1 urgent" && GameScreen.NeedsCount(0, 2) == "2 things to watch" && GameScreen.NeedsCount(1, 2) == "1 urgent, 2 to watch",
            "the pill says only the parts that are not zero: " + GameScreen.NeedsCount(1, 0));
        Expect(GameScreen.PopsUp(MsgKind.Bad) && !GameScreen.PopsUp(MsgKind.Warn) && !GameScreen.PopsUp(MsgKind.Info) && GameScreen.PopsUp(MsgKind.Good, "MissionSucceeded"),
            "only red news and real good news pop up; amber news stays in Needs you and the Log");
        Expect(gs.Click("Menu") && gs.MenuVisible, "the menu button opens the menu");
        foreach (var item in new[] { "Needs you", "Fleets", "Leaders", "Missions", "Log", "Sector", "Go to your HQ", "Save game...", "Settings..." })
            Expect(gs.Has(item), $"the menu offers {item}");
        gs.PressKey(Key.Escape);
        Expect(!gs.MenuVisible, "Esc closes the menu");
        Expect(gs.View.SideColor(me) == Ui.You && gs.View.SideColor(s.Enemy) == Ui.Enemy, "the map draws you blue and the enemy orange");
        Expect(gs.TopText.StartsWith("Day 0"), "the clock shows the day: " + gs.TopText);
        string barWhy = gs.TopBarProblem();
        Expect(barWhy == null, "the top bar as Paul drew it (ui-update-nov-15): stocks, day and clock in it, over the windows, and it fits at every interface size" + (barWhy != null ? ": " + barWhy : ""));
        // rule: none (screen only). Paul 2026-11-24: the window on the right becomes the advisor's (Sibyl for the Hand), opened from a button right of the clock
        Expect(!gs.WindowOpen && !gs.AdvisorLit, "the game opens with the advisor's window shut and its button unlit");
        Expect(gs.Click("Advisor") && gs.WindowOpen && gs.AdvisorLit, "the advisor button right of the clock opens the window on the right and stays lit");
        Expect(gs.AdvisorTip.Contains(me == Side.Hand ? "Sibyl" : "Your advisor") && gs.AdvisorTip.Contains("close"), "its hover names the advisor and says a click closes it: " + gs.AdvisorTip);
        Expect(gs.Click("Advisor") && !gs.WindowOpen && !gs.AdvisorLit, "a second click on the advisor closes the window and unlights the button");
        gs.Click("Advisor");
        // rule: none (screen only). Paul 2026-11-24: her window in the Fleet window's look: her name, her status line, the strip of tabs with counts
        Expect(gs.AdvisorHead == (me == Side.Hand ? "SIBYL" : "ADVISOR") && (me != Side.Hand || gs.AdvisorStatus.Length > 0),
            $"the window's head names the advisor, with her status for the Hand: {gs.AdvisorHead} / {gs.AdvisorStatus}");
        Expect(gs.Has($"Fleets {s.Game.State.Fleets.Values.Count(f => f.Side == me)}"), "the strip's Fleets tab carries the fleet count");
        if (me == Side.Hand)
        {
            // Paul 2026-11-24 ("we're missing the controls. Add them KISS"): her two switches in her window's head, one state with Menu > Settings
            Expect(gs.AdvisorStatus == "Manage garrisons off · Manage production off", "her head shows her two switches, both off at a new game: " + gs.AdvisorStatus);
            Expect(gs.AdvisorSwitch("Manage garrisons", true) && s.SibylOn(Praedra.Core.AI.SibylJob.ManageGarrisons) && !s.Advisor.IsOn(Praedra.Core.AI.SibylJob.ManageGarrisons),
                "Manage garrisons can be ticked in her head; it takes effect when the day runs");
            Expect(gs.AdvisorSwitch("Manage garrisons", false) && !s.SibylOn(Praedra.Core.AI.SibylJob.ManageGarrisons), "and unticked again");
        }
        else Expect(gs.AdvisorStatus == "", "the Collective's advisor has no switches");
        gs.PressKey(Key.Escape);
        Expect(!gs.WindowOpen && !gs.AdvisorLit, "Esc closes the advisor's window and the button goes dark with it");
        string dialogWhy = gs.DialogFitProblem();
        Expect(dialogWhy == null, "every dialog fits the 1029 x 579 screen of 140% text, its body scrolling under the head (QA-105)" + (dialogWhy != null ? ": " + dialogWhy : ""));

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
        Expect(gs.TabName == "Needs" && (gs.PanelText().Contains("urgent") || gs.PanelText().Contains("to watch") || gs.PanelText().Contains("Nothing")), "the Needs you tab opens on its count line: " + gs.PanelText()[..Math.Min(80, gs.PanelText().Length)]);
        Expect(!gs.Has("Run until something needs me"), "Run until something needs me is gone (Paul 2026-11-24)");
        // a badly damaged fleet (at most half its hull left) is a Needs you item (Paul 2026-11-24); its hulls are put back after
        var hurt = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.Ships.Count > 0).OrderBy(f => f.Id).FirstOrDefault();
        if (hurt != null)
        {
            var hulls = hurt.Ships.Select(s.Host.Economy.Find).Where(o => o != null && s.Host.Fleets.ClassOf(o) is { Hull: > 0 }).Select(o => (o, o.Hull)).ToList();
            foreach (var (o, _) in hulls) o.Hull = Math.Max(1, s.Host.Fleets.ClassOf(o)!.Hull * 30 / 100);
            gs.PlayRefresh();
            Expect(gs.AttentionTitles.Contains($"{s.NameOf(hurt)} is badly damaged"), $"a fleet at 30% hull is in Needs you: " + string.Join(" | ", gs.AttentionTitles));
            foreach (var (o, h) in hulls) o.Hull = h;
            gs.PlayRefresh();
            Expect(!gs.AttentionTitles.Contains($"{s.NameOf(hurt)} is badly damaged"), "and leaves it once whole again");
        }

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
                // Paul 2026-11-08 ("Fix the screen"): the order looks done at once and can be taken back until the clock moves
                gs.PlayRefresh();
                Expect(gs.PanelText().Contains($"Ordered: Move {mover.Name} to {dest}") && gs.PanelText().Contains($"Leaving for {dest}, arrives day"), "the fleet panel shows the move as given: " + gs.PanelText().Replace("\n", " / "));
                Expect(gs.TopText.Contains("given, cancel until Play"), "the top bar counts the orders given: " + gs.TopText);
                Expect(gs.Click($"Cancel: Move {mover.Name} to {dest}"), "the order has a Cancel button");
                gs.PlayRefresh();
                Expect(s.QueuedOrders == q && s.PlannedMoveOf(mover.Id) == null && !gs.PanelText().Contains("Ordered: "), "Cancel takes the order back and drops its route");
                Expect(s.Messages.Any(m => m.Text == $"Order cancelled: Move {mover.Name} to {dest}."), "the log says the order was cancelled");
                // QA-99: a second move given before the clock moves replaces the first (the core would keep the first)
                string dest2 = s.Game.State.SystemOwners.Where(kv => kv.Value == me && kv.Key != mover.At && kv.Key != dest).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)
                    .FirstOrDefault(n => s.Host.PlanMove(me, mover.Id, n) is { Ok: true, Red: false });
                if (dest2 != null)
                {
                    gs.SelectFleet(mover.Id, true); gs.PlayRefresh();
                    Expect(gs.Click("Move..."), "Move... (first)"); gs.SetSearch(dest); gs.SubmitSearch();
                    gs.SelectFleet(mover.Id, true); gs.PlayRefresh();
                    Expect(gs.Click("Move..."), "Move... (second)"); gs.SetSearch(dest2); gs.SubmitSearch();
                    gs.PlayRefresh();
                    Expect(s.QueuedOrders == q + 1 && s.PendingOf(mover.Id).Single() == $"Move {mover.Name} to {dest2}" && s.PlannedMoveOf(mover.Id)?.To == dest2,
                        $"the second move replaces the first: one order, to {dest2}");
                    Expect(gs.Click($"Cancel: Move {mover.Name} to {dest2}"), "cancel it");
                    Expect(s.QueuedOrders == q, "nothing left queued");
                }
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
        Expect(gs.MarkedSectorName == homeSec, $"the map marks the open sector (QA-74): {gs.MarkedSectorName}");
        Expect(gs.BottomLeftShown != gs.SectorOnLeft, "the bottom-left box (the legend) hides while the window sits over it (QA-73)");
        string hc = gs.SectorCardText(home), tip = gs.SectorCardTip(home);
        GD.Print("  tile " + home + ": " + hc.Replace("\n", " / ") + " | " + tip.Replace("\n", " / "));
        Expect(hc.Contains(home) && !hc.Any(char.IsDigit), "the headquarters tile shows its name and no numbers: " + hc.Replace("\n", " / "));
        Expect(tip.Contains("Loyalty") && tip.Contains("Energy") && tip.Contains("Raw") && tip.Contains("Troops"), "its tooltip has the numbers: loyalty, energy, raw, troops");
        var td = gs.SectorTileData(home);
        Expect(td != null && td.You == Ui.You && td.Enemy == Ui.Enemy && td.NameColor == Ui.You,
            "the sector tile draws you blue and the enemy orange, like the map (Paul 2026-11-11)");
        Expect(gs.G.Systems.All(x => PlanetLook.Of(x.Name) != null), "every system of the map has its Planet Maker settings, so its planet is drawn live");
        Expect(td.Planet is ViewportTexture, $"{home}'s tile shows its planet drawn live by the Planet Maker shader, not a baked picture");
        Expect(!ResourceLoader.Exists("res://art/planets/atlas.png") && !ResourceLoader.Exists("res://art/planets/atlas-harvested.png"), "no baked planet pictures ship with the game (Paul 2026-11-18)");
        Expect(gs.G.Systems.All(x => SystemLore.Of(x.Name) != null), "every system of the map has its lore (data/design/system-lore.csv)");
        var unseen = cards.FirstOrDefault(c => !s.Explored(c) && s.OwnerOf(c) != me);
        if (unseen != null) Expect(gs.SectorCardText(unseen).Trim() == unseen && gs.SectorCardTip(unseen).Contains("unexplored") && !gs.SectorCardTip(unseen).Contains("Loyalty"),
            $"an unexplored tile ({unseen}) shows its name and planet only");
        if (unseen != null && gs.SectorTileData(unseen) is { } ud)
            Expect(ud.Planet is ViewportTexture && ud.PlanetTint != Colors.White, $"an unexplored tile ({unseen}) shows its planet drawn live but still and greyed");
        gs.PressKey(Key.Right);
        Expect(gs.SectorViewOpen && gs.SectorViewName != homeSec, "the Right arrow steps to the next sector: " + gs.SectorViewName);
        gs.PressKey(Key.Left);
        Expect(gs.SectorViewName == homeSec, "and Left steps back");
        gs.PressKey(Key.Escape);
        Expect(!gs.SectorViewOpen, "Esc closes the sector window");
        Expect(gs.MarkedSectorName == null && gs.BottomLeftShown, "closing it unmarks the sector and brings back the bottom-left box");

        // ---------------------------------------------------------------- sectors on the map
        var homeSys = gs.G.Systems.First(x => x.Name == home);
        string sec = homeSys.IsOutsideSectors ? gs.G.Systems.First(x => !x.IsOutsideSectors).Sector : homeSys.Sector;
        string inSec = gs.G.Systems.First(x => !x.IsOutsideSectors && x.Sector == sec).Name;
        Expect(gs.SectorUnder(inSec) == sec, $"pointing inside {sec} finds that sector: {gs.SectorUnder(inSec)}");
        Expect(gs.SectorUnderScreen(new Vector2(-5000, -5000)) == null, "pointing at empty space far off the map finds no sector");
        gs.ShotSectorHover(sec.Replace(' ', '_'));
        Expect(gs.HoverWords == "" && !gs.HoverCardShown, "pointing at a sector's open space shows no card: its outline, name and system names say it (pick 8): " + gs.HoverWords);
        gs.ClickSector(sec);
        Expect(gs.SectorViewOpen && gs.SectorViewName == sec, $"clicking {sec} on the map opens its sector window");
        gs.PressKey(Key.S);
        Expect(!gs.SectorViewOpen, "S closes it again");

        PlaytestDeclutter(gs, home);

        // ---------------------------------------------------------------- the goal
        Expect(gs.GoalButtonText.StartsWith("Goal: 0/3 terms met", StringComparison.Ordinal), "the Goal button counts the capital and the leaders you hold: " + gs.GoalButtonText);
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
        // Sibyl, the Hand's advisor (rule: m6-advisor-sibyl): her two switches are in the settings, and a click only marks the wish for today
        Expect(gs.SetCheck("Manage production", true) && s.SibylOn(Praedra.Core.AI.SibylJob.ManageProduction) && !s.Advisor.IsOn(Praedra.Core.AI.SibylJob.ManageProduction),
            "Sibyl's Manage production can be ticked; it takes effect when the day runs");
        Expect(gs.AdvisorStatus.Contains("Manage production on"), "her window's switch follows at once: " + gs.AdvisorStatus);
        gs.SetCheck("Manage production", false);
        Expect(!s.SibylOn(Praedra.Core.AI.SibylJob.ManageProduction) && gs.SetCheck("Manage garrisons", false), "and unticked again, next to Manage garrisons");
        gs.PressKey(Key.Escape);
        Expect(!gs.SettingsVisible, "Esc closes the settings");
        {
            // on a game of its own: switched on, she says so, and from the next day orders mines or refineries at the idle construction yard
            var hs = GameSession.New(s.Data, Side.Hand, Preset.Medium, seed: 1);
            hs.SetSibyl(Praedra.Core.AI.SibylJob.ManageProduction, true);
            var said = new System.Collections.Generic.List<Message>();
            for (int d = 0; d < 3; d++) said.AddRange(hs.StepDay().New);
            Expect(said.Any(m => m.Text == "Sibyl will manage your production from tomorrow."), "Sibyl says when she starts");
            var built = said.FirstOrDefault(m => m.Text.StartsWith("Sibyl: a ", StringComparison.Ordinal));
            Expect(built != null && (built.Text.Contains(" Construction Yard ordered at ") || built.Text.Contains(" Mine ordered at ") || built.Text.Contains(" Refinery ordered at ")) && built.System != null,
                "Sibyl orders a construction yard, mine or refinery the next day: " + (built?.Text ?? "none"));
            Expect(!said.Any(m => m.Text.StartsWith("Order refused", StringComparison.Ordinal)), "and it is not refused");
        }

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
