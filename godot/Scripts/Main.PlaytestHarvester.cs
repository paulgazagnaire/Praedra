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
        var strikesBefore = ds.StrikesIn(star.Sector);
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
                gs.Click("Characters"); gs.PlayRefresh();
                Expect(gs.Has($"Send {name} on a mission..."), $"the Characters list offers Send {name} on a mission...");
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
        Expect(ds.StrikesIn(star.Sector) == strikesBefore + 1, "the sector strike was counted");
        Expect(gs.DrawnDestroyed(target), $"{target} is drawn as destroyed");
        if (cont is bool c) Expect(!c && gs.Banner.Contains("harvester"), "the clock paused with a note: " + gs.Banner);
        var news = s.Messages.Skip(before).Select(m => m.Text).ToList();
        GD.Print("  messages: " + string.Join(" | ", news));
        Expect(news.Any(t => t.Contains("destroyed " + target)), "the message log says the harvester destroyed " + target);
        Expect(news.Any(t => t.Contains("Sector") && (t.Contains("turns against") || t.Contains("Revolt")) || star.IsOutsideSectors), "the sector reaction is in the log");
        if (cont != null) { Expect(gs.ReportVisible && gs.ReportText.Contains("System destroyed"), "a report pops up"); }
        gs.Click("Close");
        Expect(s.QueuedOrders == 0, "no order left waiting");

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
                gs.SelectSystem(yard); gs.PlayRefresh();
                gs.Click(">  Ships"); gs.PlayRefresh();
                string row = gs.ButtonTexts().FirstOrDefault(t => t.StartsWith("harvester   ", StringComparison.Ordinal));
                Expect(row != null && row.Contains("one per system"), "the harvester row warns 'one per system' where one already stands: " + row);
                int b3 = s.Messages.Count;
                Expect(gs.Click("harvester   "), "click the harvester to build it anyway");
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
