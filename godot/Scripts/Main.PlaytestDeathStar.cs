using System;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the Death Star screens (rule: m5-death-star). The core's setup helper puts a Death Star fleet at a system (test only: a real game
/// builds one at a shipyard). The Empire presses Destroy System and confirms; the Alliance sees the enemy strike on its own screen and checks that
/// Death Star Sabotage can pick the system. Both then step a day and check the core, the map, the panel and the messages.
/// </summary>
public partial class Main
{
    void PlaytestDeathStar(GameScreen gs)
    {
        var s = gs.S; var me = s.Player; var fm = s.Host.Fleets; var cm = s.Host.Characters; var mm = s.Missions; var ds = s.Host.DeathStar;
        GD.Print("--- death star");
        gs.View.Select(-1); gs.PlayRefresh();

        // a free Alliance agent who can do Death Star Sabotage (the planner test); its system picks where the Death Star stands
        CharacterRecord agent = me == Side.Alliance
            ? s.MyCharacters.Where(c => cm.IsAvailable(c) && c.Transit == null && c.System != null && mm.CanDo(c, MissionKind.DeathStarSabotage)).OrderBy(c => c.Id).FirstOrDefault()
            : null;
        int Ticks(string a, string b) => fm.FindPath(a, b, 100) is { Ok: true } p ? p.TotalTicks : int.MaxValue;
        string hq = s.MyHqSystem, enemyHq = s.EnemyHqSystem;
        // a neutral system in a sector, nobody there, not a capital or HQ (destroying one would end the game)
        var cands = gs.G.Systems.Where(x => !x.IsOutsideSectors && s.OwnerOf(x.Name) == null && x.Name != hq && x.Name != enemyHq && x.Name != GameBoot.EmpireCapital && x.Name != "Charter"
                && !s.Game.State.Fleets.Values.Any(f => f.At == x.Name) && !s.Game.State.DeathStar.IsDestroyed(x.Name)
                && (agent == null || Ticks(agent.System, x.Name) < int.MaxValue))
            .Select(x => x.Name).OrderBy(n => agent == null ? 0 : Ticks(agent.System, n)).ThenBy(n => n, StringComparer.Ordinal).ToList();
        Expect(cands.Count > 0, "a neutral sector system is free for the Death Star test");
        if (cands.Count == 0) return;
        string target = cands[0];
        var star = gs.G.Get(target);
        GD.Print($"  Death Star fleet at {target} (sector {star.Sector})");

        int dsFleet = fm.AddFleet(Side.Empire, "DS Strike", new[] { Praedra.Core.DeathStar.DeathStarModule.ShipClass }, target);
        var fleet = s.Game.State.Fleets[dsFleet];
        Expect(s.HoldsDeathStar(fleet) && ds.HasDeathStar(Side.Empire, target), $"the core says the Empire has a Death Star at {target}");

        if (me == Side.Empire)
        {
            // ---- Fleet panel: the button, the confirm dialog, the order
            SelectFleetByList(gs, fleet);
            Expect(gs.Has("Destroy System"), "the fleet panel of a Death Star fleet shows Destroy System");
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
        if (me == Side.Alliance)
        {
            // ---- Alliance: the Empire's strike is queued through the core (as the computer side would), and the computer side sits out these days:
            // its upkeep scrapping would remove a 600-upkeep ship that only exists in this test.
            s.OpponentIdle = true;
            long seq = ds.SubmitDestroySystem(Side.Empire, dsFleet);
            Expect(seq > 0, "the Empire's Destroy System order is queued in the core");
            // Death Star Sabotage can pick the system where the enemy Death Star stands
            Expect(agent != null, "a free Alliance agent can do Death Star Sabotage");
            if (agent != null)
            {
                string name = s.CharName(agent.Id);
                gs.Click("Characters"); gs.PlayRefresh();
                Expect(gs.Has($"Send {name} on a mission..."), $"the Characters list offers Send {name} on a mission...");
                int mid = SendThroughPlanner(gs, $"Send {name} on a mission...", null, MissionKind.DeathStarSabotage, target, checkRefusedKind: false);
                Expect(mid >= 0, $"a Death Star Sabotage mission to {target} was sent from the planner");
                Expect(gs.ToastText == "" || !gs.ToastText.Contains("no enemy Death Star"), "no refusal toast for the target");
                gs.View.Select(-1); gs.PlayRefresh();
            }
        }
        else cont = gs.PlayDay();
        s.OpponentIdle = false;

        // ---- the day: the system is destroyed in the core, drawn as destroyed, the clock pauses, the message appears
        Expect(s.Game.State.DeathStar.IsDestroyed(target) && ds.IsDestroyed(target), $"the core says {target} is destroyed");
        Expect(ds.StrikesIn(star.Sector) == strikesBefore + 1, "the sector strike was counted");
        Expect(gs.DrawnDestroyed(target), $"{target} is drawn as destroyed");
        if (cont is bool c) Expect(!c && gs.Banner.Contains("Death Star"), "the clock paused with a note: " + gs.Banner);
        var news = s.Messages.Skip(before).Select(m => m.Text).ToList();
        GD.Print("  messages: " + string.Join(" | ", news));
        Expect(news.Any(t => t.Contains("destroyed " + target)), "the message log says the Death Star destroyed " + target);
        Expect(news.Any(t => t.Contains("Sector") && (t.Contains("turns against") || t.Contains("Revolt")) || star.IsOutsideSectors), "the sector reaction is in the log");
        if (cont != null) { Expect(gs.ReportVisible && gs.ReportText.Contains("System destroyed"), "a report pops up"); }
        gs.Click("Close");
        Expect(s.QueuedOrders == 0, "no order left waiting");

        // ---- the map and the panel
        Expect(gs.HoverTextFor(target).Contains("destroyed"), "hovering it says destroyed: " + gs.HoverTextFor(target));
        gs.SelectSystem(target); gs.PlayRefresh();
        string panel = gs.PanelText();
        Expect(panel.Contains($"Destroyed by the Death Star on day {s.Game.Day}") , "the panel says when it was destroyed: " + panel.Replace("\n", " / "));
        Expect(!panel.Contains("Build") && !gs.Has("Invade") && !gs.Has("Send a mission from") && !panel.Contains("Owner:"), "the destroyed system offers no build, invade or mission actions");
        if (me == Side.Empire)
        {
            SelectFleetByList(gs, fleet);
            Expect(gs.Has("Destroy System") && !gs.Enabled("Destroy System") && gs.PanelText().Contains("already destroyed"), "Destroy System is greyed out at a destroyed system");
            // a second order: the core refuses it and the plain reason shows in the messages
            int b2 = s.Messages.Count;
            s.SubmitDestroySystem(fleet);
            gs.PlayDay();
            Expect(s.Messages.Skip(b2).Any(m => m.Text.StartsWith("Order refused: Destroy System", StringComparison.Ordinal) && m.Text.Contains("already destroyed")),
                "a second strike is refused in plain words: " + string.Join(" | ", s.Messages.Skip(b2).Select(m => m.Text)));
            // the build menu at a yard: the Death Star is offered, and a second one at a system that has one is explained
            string yard = s.Host.Economy.Objects.Where(o => o.Category == Praedra.Core.Economy.ObjectCategory.Facility && o.Completed && o.System != null && o.Class.Contains("Shipyard") && s.OwnerOf(o.System) == me)
                .Select(o => o.System).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (yard != null)
            {
                fm.AddFleet(Side.Empire, "DS Guard", new[] { Praedra.Core.DeathStar.DeathStarModule.ShipClass }, yard);
                gs.SelectSystem(yard); gs.PlayRefresh();
                gs.Click(">  Ships"); gs.PlayRefresh();
                string row = gs.ButtonTexts().FirstOrDefault(t => t.StartsWith("Death Star   ", StringComparison.Ordinal));
                Expect(row != null && row.Contains("one per system"), "the Death Star row warns 'one per system' where one already stands: " + row);
                int b3 = s.Messages.Count;
                Expect(gs.Click("Death Star   "), "click the Death Star to build it anyway");
                gs.PlayDay();
                Expect(s.Messages.Skip(b3).Any(m => m.Text.StartsWith("Order refused:", StringComparison.Ordinal) && m.Text.Contains("only one Death Star per system")),
                    "the core's DeathStarLimit shows in plain words: " + string.Join(" | ", s.Messages.Skip(b3).Select(m => m.Text)));
            }
        }
        else
        {
            GD.Print("  sabotage team: " + string.Join(" | ", s.Messages.Skip(Math.Max(0, before - 3)).Where(m => m.Text.Contains("Death Star sabotage")).Select(m => m.Text)));
        }
        gs.View.Select(-1); gs.PlayRefresh();
    }
}
