using System;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the harvester screens (rule: m5-harvester). The core's setup helper puts a harvester fleet at a system (test only: a real game
/// builds one at a shipyard). The Hand presses Destroy System and confirms; the Collective sees the enemy strike on its own screen and checks that
/// Spike the Harvester can pick the system. Both then step a day and check the core, the map, the panel and the messages.
/// </summary>
public partial class Main
{
    void PlaytestHarvester(GameScreen gs)
    {
        var s = gs.S; var me = s.Player; var fm = s.Host.Fleets; var cm = s.Host.Characters; var mm = s.Missions; var ds = s.Host.Harvester;
        GD.Print("--- harvester");
        gs.View.Select(-1); gs.PlayRefresh();

        // a free Collective agent who can do Spike the Harvester (the planner test); its system picks where the harvester stands
        CharacterRecord agent = me == Side.Collective
            ? s.MyCharacters.Where(c => cm.IsAvailable(c) && c.Transit == null && c.System != null && mm.CanDo(c, MissionKind.SpikeTheHarvester)).OrderBy(c => c.Id).FirstOrDefault()
            : null;
        int Ticks(string a, string b) => fm.FindPath(a, b, 100) is { Ok: true } p ? p.TotalTicks : int.MaxValue;
        string hq = s.MyHqSystem, enemyHq = s.EnemyHqSystem;
        // a neutral system in a sector, nobody there, not a capital or HQ (destroying one would end the game)
        var cands = gs.G.Systems.Where(x => !x.IsOutsideSectors && s.OwnerOf(x.Name) == null && x.Name != hq && x.Name != enemyHq && x.Name != GameBoot.HandCapital && x.Name != "Charter"
                && !s.Game.State.Fleets.Values.Any(f => f.At == x.Name) && !s.Game.State.Harvester.IsDestroyed(x.Name)
                && (agent == null || Ticks(agent.System, x.Name) < int.MaxValue))
            .Select(x => x.Name).OrderBy(n => agent == null ? 0 : Ticks(agent.System, n)).ThenBy(n => n, StringComparer.Ordinal).ToList();
        Expect(cands.Count > 0, "a neutral sector system is free for the harvester test");
        if (cands.Count == 0) return;
        string target = cands[0];
        var star = gs.G.Get(target);
        GD.Print($"  harvester fleet at {target} (sector {star.Sector})");

        int dsFleet = fm.AddFleet(Side.Hand, "DS Strike", new[] { Praedra.Core.Harvester.HarvesterModule.ShipClass }, target);
        var fleet = s.Game.State.Fleets[dsFleet];
        Expect(s.HoldsHarvester(fleet) && ds.HasHarvester(Side.Hand, target), $"the core says the Hand has a harvester at {target}");

        if (me == Side.Hand)
        {
            // ---- Fleet panel: the button, the confirm dialog, the order
            SelectFleetByList(gs, fleet);
            Expect(gs.Has("Destroy System"), "the fleet panel of a harvester fleet shows Destroy System");
            Expect(gs.Enabled("Destroy System"), "Destroy System is enabled at a system");
            Expect(gs.Click("Destroy System"), "click Destroy System");
            Expect(gs.ConfirmVisible, "a confirm dialog opens first");
            Expect(gs.ConfirmTitle.Contains(target) && gs.ConfirmText.Contains(target) && gs.ConfirmText.Contains("destroyed for good"), "the dialog names the system and what happens: " + gs.ConfirmText);
            Expect(gs.Click("Cancel") && !gs.ConfirmVisible && !s.PendingOf(dsFleet).Any(), "Cancel sends no order");
            Expect(gs.Click("Destroy System") && gs.ConfirmVisible, "open the dialog again");
            Expect(gs.Click("Yes, destroy it"), "confirm");
            gs.PlayRefresh();
            Expect(s.PendingOf(dsFleet).Any(p => p.StartsWith("Destroy System", StringComparison.Ordinal)) && s.QueuedOrders >= 1, "the Destroy System order is queued");
            Expect(!gs.Enabled("Destroy System") && gs.PanelText().Contains("waiting for the next day"), "the button is greyed while the order waits");
        }
        // an Infrastructure window left open over the target must not keep drawing its slots after the strike (Ponytail on #241)
        if (me == Side.Hand) { gs.TileClick(target, "infra"); gs.PlayRefresh(); Expect(gs.MgmtKeys.Contains($"Infra {target}"), $"the Infrastructure window of {target} is open before the strike"); }
        var strikesBefore = ds.StrikesIn(star.Sector);
        int handRefinedBefore = s.Game.State.Economy.Pool(Side.Hand).Refined;
        int before = s.Messages.Count;
        bool? cont = null;
        if (me == Side.Collective)
        {
            // ---- Collective: the Hand's strike is queued through the core (as the computer side would), and the computer side sits out these days:
            // its upkeep scrapping would remove a 600-upkeep ship that only exists in this test.
            s.OpponentIdle = true;
            long seq = ds.SubmitDestroySystem(Side.Hand, dsFleet);
            Expect(seq > 0, "the Hand's Destroy System order is queued in the core");
            // Spike the Harvester can pick the system where the enemy harvester stands
            Expect(agent != null, "a free Collective agent can do Spike the Harvester");
            if (agent != null)
            {
                string name = s.CharName(agent.Id);
                gs.ClickViaMenu("Leaders"); gs.PlayRefresh();
                Expect(gs.Has($"Send {name} on a mission..."), $"the Leaders tab offers Send {name} on a mission...");
                int mid = SendThroughPlanner(gs, $"Send {name} on a mission...", null, MissionKind.SpikeTheHarvester, target, checkRefusedKind: false);
                Expect(mid >= 0, $"a Spike the Harvester mission to {target} was sent from the planner");
                Expect(gs.ToastText == "" || !gs.ToastText.Contains("no enemy harvester"), "no refusal toast for the target");
                gs.View.Select(-1); gs.PlayRefresh();
            }
        }
        else cont = gs.PlayDay();
        s.OpponentIdle = false;

        // ---- the day: the system is destroyed in the core, drawn as destroyed, the clock pauses, the message appears
        Expect(s.Game.State.Harvester.IsDestroyed(target) && ds.IsDestroyed(target), $"the core says {target} is destroyed");
        Expect(s.Game.Events.OfType<Praedra.Core.Harvester.SystemDestroyed>().LastOrDefault(e => e.System == target)?.YieldRefined == 900,
            $"the strike paid the Hand the harvest yield, 900 refined (pool {handRefinedBefore} -> {s.Game.State.Economy.Pool(Side.Hand).Refined})");
        Expect(ds.StrikesIn(star.Sector) == strikesBefore + 1, "the sector strike was counted");
        Expect(gs.DrawnDestroyed(target), $"{target} is drawn as destroyed");
        if (cont is bool c) Expect(!c && gs.Banner.Contains("harvester"), "the clock paused with a note: " + gs.Banner);
        var news = s.Messages.Skip(before).Select(m => m.Text).ToList();
        GD.Print("  messages: " + string.Join(" | ", news));
        Expect(news.Any(t => t.Contains(me == Side.Hand ? $"HARVEST COMPLETE: {target}" : $"{target} is gone")), "the message log carries the harvest report for " + target);
        Expect(news.Any(t => t.Contains("Sector") && (t.Contains("turns against") || t.Contains("Revolt")) || star.IsOutsideSectors), "the sector reaction is in the log");
        if (cont != null)
            Expect(gs.ReportVisible && gs.ReportText.Contains("Harvest complete") && gs.ReportText.Contains($"HARVEST COMPLETE: {target}") && gs.ReportText.Contains("Recovered: 900 refined") && (gs.ReportText.Contains("Debt written off: ") == gs.ReportText.Contains("Unregistered: ")),
                "the harvest report pops up: " + gs.ReportText.Replace("\n", " / "));
        gs.Click("Close");
        Expect(s.QueuedOrders == 0, "no order left waiting");
        if (me == Side.Hand)
        {
            string inf = gs.MgText($"Infra {target}");
            Expect(inf.Contains("HARVESTED") && !inf.Contains("energy slots") && !inf.Contains("Ground at"), "the open Infrastructure window turns into the harvested card: " + inf.Replace("\n", " / "));
        }
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        Expect(PlanetArt.HarvestedOf(target) != null && PlanetArt.HarvestedOf(target) != PlanetArt.Of(target), $"{target} has its harvested remnant picture");
        // the harvested world's tile opens one plain card: no tabs, no Infrastructure window (Paul 2026-11-04)
        gs.TileClick(target, "infra"); gs.PlayRefresh();
        string card = gs.MgText($"Ground {target}");
        Expect(gs.MgmtKeys.Contains($"Ground {target}") && !gs.MgmtKeys.Contains($"Infra {target}") && gs.MgTabs($"Ground {target}").Count() == 0
            && card.Contains("HARVESTED") && card.Contains($"on day {s.Game.State.Harvester.DestroyedDay(target)}") && !card.Contains("Infrastructure at") && card.Contains("BEFORE THE HARVEST") && !card.Contains("FROM SPACE"),
            "a harvested tile opens one plain card with the day: " + card.Replace("\n", " / "));
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        Expect(GameSession.PeopleWords(4_300_000_000) == "4.3 billion" && GameSession.PeopleWords(2_000_000_000) == "2 billion" && GameSession.PeopleWords(40_000_000) == "40 million"
            && GameSession.PeopleWords(2_500_000) == "2 million" && GameSession.PeopleWords(600_000) == "600,000", "head counts read as people read them (m5-harvester §7)");
        // m5-missions §6b: Burn the Books is offered while the books stand and the Hand holds Charter; once burned the Hand's report has no debt figure
        if (me == Side.Collective && s.OwnerOf("Charter") == Side.Hand)
            Expect(s.BooksOffered && s.KindBlockedReason(MissionKind.BurnTheBooks) == null, "Burn the Books is offered to the Collective while the Hand holds Charter");
        if (me == Side.Hand && !s.Game.State.Missions.BooksBurned)
        {
            var strike = new Praedra.Core.Harvester.SystemDestroyed(0, s.Game.Day + 1, "Charter", Side.Hand, Side.Hand, 50, true); // a report only, no state
            Expect(!s.HarvestReport(strike).Detail.Contains("not on record"), "before the burn the Hand's report counts the debt: " + s.HarvestReport(strike).Detail.Replace("\n", " / "));
            s.Game.State.Missions.BooksBurnedOn = s.Game.Day; // test only, put back at once
            string burned = s.HarvestReport(strike).Detail;
            s.Game.State.Missions.BooksBurnedOn = null;
            Expect(burned.Contains("Debt written off: not on record") || !burned.Contains("Unregistered"), "after the burn it reads Debt written off: not on record: " + burned.Replace("\n", " / "));
        }

        // ---- the map and the panel
        Expect(gs.HoverTextFor(target).Contains("destroyed"), "hovering it says destroyed: " + gs.HoverTextFor(target));
        gs.SelectSystem(target); gs.PlayRefresh();
        string panel = gs.PanelText();
        Expect(panel.Contains($"Destroyed by the harvester on day {s.Game.Day}") , "the panel says when it was destroyed: " + panel.Replace("\n", " / "));
        Expect(!panel.Contains("Build") && !gs.Has("Invade") && !gs.Has("Send a mission from") && !panel.Contains("Owner:"), "the destroyed system offers no build, invade or mission actions");
        if (me == Side.Hand)
        {
            SelectFleetByList(gs, fleet);
            Expect(gs.Has("Destroy System") && !gs.Enabled("Destroy System") && gs.PanelText().Contains("already destroyed"), "Destroy System is greyed out at a destroyed system");
            // a second order: the core refuses it and the plain reason shows in the messages
            int b2 = s.Messages.Count;
            s.SubmitDestroySystem(fleet);
            gs.PlayDay();
            Expect(s.Messages.Skip(b2).Any(m => m.Text.StartsWith("Order refused: Destroy System", StringComparison.Ordinal) && m.Text.Contains("already destroyed")),
                "a second strike is refused in plain words: " + string.Join(" | ", s.Messages.Skip(b2).Select(m => m.Text)));
            // the build menu at a yard: the harvester is offered, and a second one at a system that has one is explained
            string yard = s.Host.Economy.Objects.Where(o => o.Category == Praedra.Core.Economy.ObjectCategory.Facility && o.Completed && o.System != null && o.Class.Contains("Shipyard") && s.OwnerOf(o.System) == me)
                .Select(o => o.System).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (yard != null)
            {
                fm.AddFleet(Side.Hand, "DS Guard", new[] { Praedra.Core.Harvester.HarvesterModule.ShipClass }, yard);
                if (gs.ReportVisible) gs.Click("Close"); // a report of the day may be open over the screen
                gs.SelectSystem(yard); gs.PlayRefresh();
                gs.Click("Build"); gs.PlayRefresh();
                gs.Click("Ships"); gs.PlayRefresh();
                var row = gs.BuildRowsShown.FirstOrDefault(r => r.Name == "harvester");
                Expect(row.Name != null && !row.Usable && row.Why != null && row.Why.Contains("one per system"), "the harvester row says 'one per system' where one already stands: " + row.Why);
                Expect(!gs.Enabled("harvester"), "and it cannot be ordered there");
                // the core still refuses it if the order comes anyway (the screen only saves the player a wasted day)
                int b3 = s.Messages.Count;
                s.Track(s.Host.Construction.SubmitBuild(me, yard, Praedra.Core.Construction.YardFamily.Shipyard, "harvester", 1), "Build a harvester");
                gs.PlayDay();
                Expect(s.Messages.Skip(b3).Any(m => m.Text.StartsWith("Order refused:", StringComparison.Ordinal) && m.Text.Contains("only one harvester per system")),
                    "the core's HarvesterLimit shows in plain words: " + string.Join(" | ", s.Messages.Skip(b3).Select(m => m.Text)));
            }
        }
        else
        {
            GD.Print("  sabotage team: " + string.Join(" | ", s.Messages.Skip(Math.Max(0, before - 3)).Where(m => m.Text.Contains("harvester sabotage")).Select(m => m.Text)));
        }
        gs.View.Select(-1); gs.PlayRefresh();
    }
}
