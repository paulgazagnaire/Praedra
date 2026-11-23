using System;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Economy;

namespace PraedraViewer;

/// <summary>
/// Playtest of the management windows (Paul, 2026-11-02): open a system's Ground window and a fleet's window side by side, then drag a
/// leader onto the fleet's posts and back, through the system General's post, troops aboard and back, a ship split off and merged back, and
/// a refused drop with its reason. A drop is the same call the mouse makes; the core is only read to choose what to drag and to check that
/// the next day did what the drop said.
/// </summary>
public partial class Main
{
    void PlaytestWindows(GameScreen gs)
    {
        var s = gs.S; var me = s.Player; var cm = s.Host.Characters; var fm = s.Host.Fleets;
        GD.Print("--- management windows");
        bool Free(CharacterRecord c) => c.Status == CharacterStatus.Free && c.Transit == null && !cm.IsOnMission(c.Id) && cm.LocationOf(c) != null;
        // a fleet standing still with a free leader of yours at a system you hold. This runs after 400 days against the real AI, which may have
        // sunk every fleet that stood by a leader (QA-98), so the test plants its own two-transport fleet by the first free leader (test only,
        // like the harvester fleet): the checks no longer depend on what the AI did.
        // and at a system no enemy fleet stands at or flies to: a battle there in the next days would sink the planted fleet (Hold beams 2026-11-22:
        // the Collective then attacked Charter on day 401). Falls back to any such leader.
        bool Safe(string? at) => at != null && !s.Game.State.Fleets.Values.Any(f => f.Side != me
            && (f.At == at || f.Transit is { } tr && tr.Path.Skip(tr.HopIndex + 1).Contains(at)));
        var leads = s.MyCharacters.Where(c => Free(c) && c.AttachedFleet == 0 && s.OwnerOf(cm.LocationOf(c)) == me).OrderBy(c => c.Id).ToList();
        var lead = leads.FirstOrDefault(c => Safe(cm.LocationOf(c))) ?? leads.FirstOrDefault();
        Expect(lead != null, "a free leader of yours stands on a system you hold");
        if (lead == null) return;
        string transport = me == Praedra.Core.Clock.Side.Hand ? "Bond II" : "Barge";
        var fleet = s.Game.State.Fleets[fm.AddFleet(me, "Window Test", new[] { transport, transport }, cm.LocationOf(lead))];
        string sys = fleet.At, lname = s.CharName(lead.Id);
        int fid = fleet.Id;
        GD.Print($"  {lname} and {fleet.Name} at {sys}");

        // ---------------------------------------------------------------- open the two windows side by side
        gs.ShotWindow("ground", sys); gs.PlayRefresh();
        string ground = gs.MgText($"Ground {sys}");
        Expect(gs.MgmtKeys.Contains($"Ground {sys}") && gs.MgTab($"Ground {sys}") == "Leaders" && ground.Contains("LEADERS ON THE PLANET") && ground.Contains("YOUR FLEETS IN ORBIT"),
            "the system window opens on its Leaders tab, and its Fleets tab lists the fleets in orbit");
        if (s.OwnerOf(sys) == me)
            Expect(string.Join(",", gs.MgTabs($"Ground {sys}")) == "Leaders,Troops,Fighters,Fleets,Lore", $"its tabs: {string.Join(", ", gs.MgTabs($"Ground {sys}"))}");
        // the Lore tab (Paul 2026-11-04): the Lore Master's text for this system
        var lore = SystemLore.Of(sys);
        Expect(lore != null && gs.Click($"Tab Ground {sys} Lore") && gs.MgTab($"Ground {sys}") == "Lore"
            && gs.MgText($"Ground {sys}") is var lt && lt.Contains("THE WORLD") && lt.Contains(lore.World) && lt.Contains(lore.Story),
            $"the Lore tab shows {sys}'s world and story");
        Expect(gs.Click($"Tab Ground {sys} Fleets") && gs.MgTab($"Ground {sys}") == "Fleets", "a click on the Fleets tab shows it");
        Expect(gs.Click($"Open {fleet.Name}"), $"the Fleets tab's Open button opens {fleet.Name}");
        gs.PlayRefresh();
        Expect(gs.MgmtCount == 2 && gs.MgHasZone($"Fleet {fid} Admiral") && gs.MgHasZone($"Fleet {fid} Passengers"), "the fleet window opens beside it, with its posts and passengers");
        // a hover card stays while you read it when a day passes (Paul 2026-11-19), and the window catches up once the mouse moves off
        var (tipHeld, tipCaught, tipText) = gs.PlayTipHold($"Ground {sys}");
        Expect(tipHeld && tipCaught, $"a hover card in the system window stays up when a day passes, and the window updates after (held {tipHeld}, caught up {tipCaught}, card \"{tipText?.Split('\n')[0]}\")");
        string noteWhy = gs.NoteOverWindowsProblem("Test note", sys);
        Expect(noteWhy == null, "a short note is drawn over the open windows, and one that names a system takes the click there, not the window under it"
            + $" (story W9, QA-104){(noteWhy == null ? "" : ": " + noteWhy)}");

        // ---------------------------------------------------------------- a leader onto a post, then back onto the planet
        var cls = cm.ClassOf(lead);
        var post = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.FirstOrDefault(cls.Can);
        string zone = post == CharacterPost.None ? $"Fleet {fid} Passengers" : $"Fleet {fid} {post}";
        string why = gs.MgDrop($"L:{lead.Id}", zone);
        Expect(why == null, $"drop {lname} on {zone}: {why ?? "sent"}");
        // the Leaders tab only: Charter's Lore tab names The Chair (route thinning 2026-11-11 made The Chair the leader picked here)
        Expect(!gs.MgPageText($"Ground {sys}", "Leaders").Contains(lname) && gs.MgText($"Fleet {fid}").Contains(lname), "the drop shows at once: the chip leaves the planet and stands in the fleet");
        Expect(gs.MgDrop($"L:{lead.Id}", $"Fleet {fid} Passengers") != null, "a chip already moved today can't be dropped again");
        gs.PlayDay();
        Expect(lead.AttachedFleet == fid && lead.Post == post, $"next day {lname} is aboard {fleet.Name} as {GameSession.PostWords(post)}");
        why = gs.MgDrop($"L:{lead.Id}", $"Ground {sys} Leaders");
        Expect(why == null, $"drop {lname} back on the planet: {why ?? "sent"}");
        gs.PlayDay();
        Expect(lead.AttachedFleet == 0 && cm.LocationOf(lead) == sys, $"next day {lname} stands on {sys} again");

        // ---------------------------------------------------------------- through the system General's post: two orders from one drop
        if (s.OwnerOf(sys) == me && cls.CanGeneral && cm.SystemGeneralOf(sys) == null)
        {
            Expect(gs.MgDrop($"L:{lead.Id}", $"Ground {sys} General") == null, $"drop {lname} on the General's post of {sys}");
            gs.PlayDay();
            Expect(cm.SystemGeneralOf(sys)?.Id == lead.Id, $"{lname} is the General of {sys}");
            Expect(gs.MgDrop($"L:{lead.Id}", zone) == null, $"drop the General straight onto {fleet.Name}");
            gs.PlayDay();
            Expect(cm.SystemGeneralOf(sys) == null && lead.AttachedFleet == fid, "one drop: the General steps down and joins the fleet the same day");
            Expect(gs.MgDrop($"L:{lead.Id}", $"Ground {sys} Leaders") == null, "and back to the planet");
            gs.PlayDay();
        }

        // ---------------------------------------------------------------- a leader at another system sets off and joins on arrival
        var far = s.MyCharacters.Where(c => Free(c) && cm.LocationOf(c) != sys && c.AttachedFleet == 0 && s.SystemPostOf(c) == null).OrderBy(c => c.Id)
            .FirstOrDefault(c => fm.FindPath(cm.LocationOf(c), sys, cm.Params.CharacterSpeed).Ok);
        if (far != null)
        {
            string fname = s.CharName(far.Id);
            string r = gs.MgDrop($"L:{far.Id}", $"Fleet {fid} Passengers");
            Expect(r == null, $"drop {fname} from {cm.LocationOf(far)} on {fleet.Name}: {r ?? "sent"}");
            Expect(gs.MgText($"Fleet {fid}").Contains("on the way"), "the fleet window shows them on the way");
            // QA-100: a drag that sends a leader travelling is a move like a map move, so a later move replaces it
            Expect(s.PlannedCharacterMoveOf(far.Id) is { } fp && fp.To == sys && s.WhatOf(fp.Seq) != null, "the drag records the leader's trip, so a later move can replace it");
            int qd = s.QueuedOrders;
            Expect(gs.MgDrop($"L:{far.Id}", $"Fleet {fid} Passengers") != null || s.QueuedOrders == qd, "dropping them again does not queue a second trip");
            gs.PlayDay(); gs.PlayRefresh();
            Expect(far.Transit != null && far.JoinFleet == fid && gs.MgText($"Fleet {fid}").Contains("On the way"), $"next day {fname} travels to {sys}, listed under On the way");
        }

        // ---------------------------------------------------------------- a regiment aboard and back
        var reg = s.Host.Troops.RegimentsOnSystem(sys).FirstOrDefault(o => o.Side == me);
        if (reg != null && fm.FreeTroopSlots(fleet) > 0 && s.OwnerOf(sys) == me)
        {
            Expect(gs.MgDrop($"R:{reg.Id}", $"Fleet {fid} Troops") == null, "drop a regiment onto the fleet's troops");
            gs.PlayDay();
            Expect(reg.AboardShip != 0 && fleet.Ships.Contains(reg.AboardShip), "next day it is aboard");
            string back = gs.MgDrop($"R:{reg.Id}", $"Ground {sys} Regiments");
            if (s.OwnerOf(sys) != me)   // the day may have lost the planet: then landing is refused, with a reason
                Expect(back != null && back.Contains("planet you hold"), $"{sys} was lost overnight, so landing is refused: {back}");
            else
            {
                Expect(back == null, "drop it back on the planet: " + (back ?? "sent"));
                gs.PlayDay();
                Expect(reg.AboardShip == 0 && reg.System == sys, "next day it stands on the planet again");
            }
        }
        else if (reg != null && fm.FreeTroopSlots(fleet) == 0)
            Expect(gs.MgDrop($"R:{reg.Id}", $"Fleet {fid} Troops")?.Contains("no troop room") == true, "a fleet with no troop room refuses regiments");

        // ---------------------------------------------------------------- Command (Paul's design 2026-11-21): the actions, the behaviour, scrap on the head
        Expect(string.Join(",", gs.MgTabs($"Fleet {fid}")) == "Command,Ships,Fighters,Troops", $"the fleet's tabs: {string.Join(", ", gs.MgTabs($"Fleet {fid}"))}");
        Expect(gs.Click($"Tab Fleet {fid} Command") && !gs.MgText($"Fleet {fid}").Contains("all fleets") && gs.Has("Behavior Defensive") && gs.Has("Behavior Elusive"),
            "Command: Defensive or Elusive, this fleet's own (no \"all fleets\" note)");
        Expect(gs.Has("Fleet Move") && gs.Has("Fleet Bombard") && gs.Has("Fleet Invade"), "the actions: Bombardment, Invasion, Move");
        if (fleet.At != null && fleet.Transit == null) Expect(gs.MenuOf($"Fleet head {fid}").Any(l => l.StartsWith("Scrap the whole fleet", StringComparison.Ordinal)), "the head's right-click scraps the fleet");
        {
            int q0 = s.QueuedOrders;
            bool wasRetreat = s.Host.Battles.OnContactOfFleet(fid) == Praedra.Core.Battle.OnContactMode.Retreat;
            var others = s.Game.State.Fleets.Values.Where(o => o.Side == me && o.Id != fid).ToDictionary(o => o.Id, o => s.Host.Battles.OnContactOfFleet(o.Id));
            string flip = wasRetreat ? "Defensive" : "Elusive", back = wasRetreat ? "Elusive" : "Defensive";
            if (gs.ReportVisible) gs.Click("Close");   // the night's report, if one popped up, is closed first, as a player would
            Expect(gs.Click($"Behavior {flip}") && s.QueuedOrders == q0 + 1, $"Orders: {flip} sends the order (orders waiting {q0} -> {s.QueuedOrders})");
            gs.PlayDay();
            if (gs.ReportVisible) gs.Click("Close");
            Expect((s.Host.Battles.OnContactOfFleet(fid) == Praedra.Core.Battle.OnContactMode.Retreat) == !wasRetreat, $"after the day the fleet is {flip}");
            Expect(others.All(kv => !s.Game.State.Fleets.ContainsKey(kv.Key) || s.Host.Battles.OnContactOfFleet(kv.Key) == kv.Value), $"the other {others.Count} fleet(s) keep their own setting");
            gs.Click($"Tab Fleet {fid} Command");
            int q1 = s.QueuedOrders;
            Expect(gs.Click($"Behavior {back}") && s.QueuedOrders == q1 + 1, $"Orders: {back} sends it back (orders waiting {q1} -> {s.QueuedOrders})");
            gs.PlayDay();
            Expect((s.Host.Battles.OnContactOfFleet(fid) == Praedra.Core.Battle.OnContactMode.Retreat) == wasRetreat, "the last choice holds after the day");
        }

        // ---------------------------------------------------------------- ships split off (two drops, one new fleet), then merged back from the head's menu
        if (fleet.Ships.Count > 1)
        {
            for (int i = 0; i < 5 && gs.AnyModal; i++) gs.PressKey(Key.Escape);   // a report the days opened goes first (a system lost overnight)
            int ship = fleet.Ships[^1];
            int ship2 = fleet.Ships.Count > 2 ? fleet.Ships[^2] : 0;
            var before = s.Game.State.Fleets.Keys.ToHashSet();
            // Paul 2026-11-08 ("Fix the screen"): a drop's order can be cancelled until the clock moves, and its chip goes back
            int q0 = s.QueuedOrders;
            Expect(gs.MgDrop($"S:{ship}", $"Fleet {fid} NewFleet") == null && s.QueuedOrders == q0 + 1, "drop a ship on New fleet: one split order");
            gs.MgText($"Fleet {fid}");
            Expect(gs.Click($"Cancel: Split a ship off {fleet.Name}"), "the split shows in the fleet window with a Cancel button");
            Expect(s.QueuedOrders == q0, "Cancel takes the split back");
            Expect(gs.MgDrop($"S:{ship}", $"Fleet {fid} NewFleet") == null, "drop a ship on New fleet (the chip is back, so it can be dropped again)");
            if (ship2 != 0) Expect(gs.MgDrop($"S:{ship2}", $"Fleet {fid} NewFleet") == null, "drop a second ship on New fleet the same day");
            gs.PlayDay();
            var made = s.Game.State.Fleets.Values.FirstOrDefault(f => !before.Contains(f.Id) && f.Ships.Contains(ship));
            Expect(made != null, "next day the ship is a fleet of its own");
            if (ship2 != 0) Expect(made != null && made.Ships.Contains(ship2) && s.Game.State.Fleets.Keys.Count(k => !before.Contains(k)) == 1, "two ships dropped the same day make one new fleet (QA)");
            if (made != null)
            {
                gs.MgText($"Fleet {fid}");
                int qm = s.QueuedOrders;
                Expect(gs.RunMenu($"Fleet head {fid}", $"{made.Name} joins now (here)") && s.QueuedOrders == qm + 1, $"{fleet.Name}'s head menu lists {made.Name}, standing with it: one merge order");
                gs.ShotWindow("fleet", made.Id.ToString()); gs.PlayRefresh();
                Expect(gs.MgDrop($"M:{fid}", $"Fleet {made.Id} Ships") != null, "the other fleet can't be merged back into it the same day (QA)");
                gs.PlayDay(); gs.PlayRefresh();
                Expect(!s.Game.State.Fleets.ContainsKey(made.Id) && fleet.Ships.Contains(ship), "next day it has merged back");
                Expect(!gs.MgmtKeys.Contains($"Fleet {made.Id}"), "the merged fleet's window closes by itself");
            }
        }

        // ---------------------------------------------------------------- two ships dropped on New fleet the same day make one fleet (QA on #227)
        var big = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && f.Ships.Count >= 3).OrderBy(f => f.Id).FirstOrDefault();
        if (big != null)
        {
            gs.ShotWindow("fleet", big.Id.ToString()); gs.PlayRefresh();
            int a1 = big.Ships[^1], a2 = big.Ships[^2];
            var before2 = s.Game.State.Fleets.Keys.ToHashSet();
            Expect(gs.MgDrop($"S:{a1}", $"Fleet {big.Id} NewFleet") == null && gs.MgDrop($"S:{a2}", $"Fleet {big.Id} NewFleet") == null, $"drop two ships of {big.Name} on New fleet the same day");
            gs.PlayDay();
            var two = s.Game.State.Fleets.Values.Where(f => !before2.Contains(f.Id)).ToList();
            Expect(two.Count == 1 && two[0].Ships.Contains(a1) && two[0].Ships.Contains(a2), $"next day they make one new fleet ({two.Count} made)");
            if (two.Count == 1) { gs.ShotWindow("fleet", two[0].Id.ToString()); gs.PlayRefresh(); gs.MgDrop($"M:{two[0].Id}", $"Fleet {big.Id} Ships"); gs.PlayDay(); }
        }
        else GD.Print("  (no fleet of three ships: the two-ship split is not checked on this side)");

        // ---------------------------------------------------------------- rename a fleet by double-clicking its name (Paul 2026-11-23)
        // Enter or a click elsewhere keeps the name, Esc and an empty box keep the old one; the new name shows at once, the day applies it
        // rule: m3-fleets §Orders (RenameFleet)
        var named = s.Game.State.Fleets.Values.Where(f => f.Side == me).OrderBy(f => f.Id).FirstOrDefault();
        if (named != null)
        {
            string was = named.Name, key = $"Fleet {named.Id}";
            gs.ShotWindow("fleet", named.Id.ToString()); gs.PlayRefresh();
            Expect(!gs.PlayRenameOpen(named.Id, pastWords: true) && !gs.RenameOpen, "a double-click past the fleet's name leaves it as it is (the head still folds the window there)");
            Expect(gs.PlayRenameOpen(named.Id) && gs.RenameBoxText == was, $"a double-click on the name turns it into a box holding \"{was}\"");
            gs.PlayRenameType("\u00C9b\u00E8ne Fist");
            Expect(gs.RenameBoxText == "Ebene Fist", $"accented letters become plain ones as they are typed (got \"{gs.RenameBoxText}\")");
            gs.PlayRenameType("Fist \u2713 \u4E00");
            Expect(gs.RenameBoxText == "Fist  ", $"letters the font has not got are left out (got \"{gs.RenameBoxText}\")");
            gs.PlayRenameType(new string('x', 30));
            Expect(gs.RenameBoxText?.Length == Praedra.Core.Fleets.FleetModule.MaxFleetNameLength, "a name stops at 24 letters");
            int q0 = s.QueuedOrders;
            gs.PlayRenameEsc();
            Expect(!gs.RenameOpen && s.QueuedOrders == q0 && s.NameOf(named) == was, "Esc keeps the old name and sends nothing");
            Expect(gs.PlayRenameOpen(named.Id), "the box opens again");
            gs.PlayRenameType("   ");
            gs.PlayRenameClickElsewhere();
            Expect(!gs.RenameOpen && s.QueuedOrders == q0 && s.NameOf(named) == was, "an empty name keeps the old one and sends nothing");
            Expect(gs.PlayRenameOpen(named.Id), "the box opens again");
            gs.PlayRenameEnter();
            Expect(!gs.RenameOpen && s.QueuedOrders == q0, "Enter on the unchanged name sends nothing");
            Expect(gs.PlayRenameOpen(named.Id), "the box opens again");
            gs.PlayRenameType("  Iron Fist ");
            gs.PlayRenameEnter();
            Expect(!gs.RenameOpen && s.QueuedOrders == q0 + 1 && s.NameOf(named) == "Iron Fist" && named.Name == was,
                "Enter sends one RenameFleet order; the new name (trimmed) shows at once, the fleet keeps its own until the day");
            Expect(gs.MgText(key).Contains("IRON FIST") && gs.FleetListTexts().Any(t => t.StartsWith("Iron Fist\n", StringComparison.Ordinal)),
                "the window's head and the fleet list show the new name before the day runs");
            gs.PlayDay(); gs.PlayRefresh();
            Expect(named.Name == "Iron Fist" && s.NameOf(named) == "Iron Fist", $"next day the fleet is called Iron Fist (it is {named.Name})");
            Expect(gs.PlayRenameOpen(named.Id) && gs.RenameBoxText == "Iron Fist", "the box holds the name as it is spelled, not in capitals");
            gs.PlayRenameType(was);
            gs.PlayRenameClickElsewhere();
            Expect(!gs.RenameOpen && s.Game.Queue.Count(o => o is Praedra.Core.Clock.RenameFleetOrder r && r.Fleet == named.Id) == 1 && s.NameOf(named) == was,
                "a click elsewhere keeps the typed name: one order, shown at once");
            gs.PlayDay(); gs.PlayRefresh();
            Expect(named.Name == was, $"next day it is {was} again");
        }

        // ---------------------------------------------------------------- a regiment goes alone to another system you hold, never to one you don't (Paul 2026-11-02)
        var cons = s.Host.Construction;
        var lone = s.Game.State.Economy.Objects.Values.Where(o => o.Side == me && o.Category == ObjectCategory.Regiment && o.Completed && o.System != null && o.AboardShip == 0)
            .OrderBy(o => o.Id).FirstOrDefault();
        if (lone != null)
        {
            string home = lone.System;
            string held = s.Data.Galaxy.Systems.Select(x => x.Name).Where(n => n != home && s.OwnerOf(n) == me).OrderBy(n => n)
                .FirstOrDefault(n => cons.CheckSendRegiment(new Praedra.Core.Clock.SendRegimentOrder(0, me, lone.Id, n)) == null);
            if (held != null)
            {
                gs.ShotWindow("ground", held); gs.PlayRefresh();
                string r = gs.MgDrop($"R:{lone.Id}", $"Ground {held} Regiments");
                Expect(r == null, $"drop a regiment from {home} on {held}'s regiments: {r ?? "sent"}");
                Expect(gs.MgText($"Ground {held}").Contains("on the way"), "the system window shows it on the way");
                gs.PlayDay();
                Expect(cons.Deliveries.Any(d => d.Object == lone.Id && d.To == held), $"next day it travels alone to {held}");
                Expect(gs.MoverLabels().Any(l => l.Contains($"to {held}")), $"the map shows the regiment on its way to {held}: {string.Join(" | ", gs.MoverLabels())}");
                // Layers > Deliveries hides it and shows it again (Paul 2026-11-17)
                bool lit = gs.PressDeliveries();
                Expect(!lit && !gs.MoverLabels().Any(l => l.Contains($"to {held}")), "Layers > Deliveries off hides the regiment on its way");
                lit = gs.PressDeliveries();
                Expect(lit && gs.MoverLabels().Any(l => l.Contains($"to {held}")), "Layers > Deliveries on shows it again");
            }
            var other = s.Game.State.Economy.Objects.Values.Where(o => o.Side == me && o.Category == ObjectCategory.Regiment && o.Completed && o.System != null && o.AboardShip == 0)
                .OrderBy(o => o.Id).FirstOrDefault();
            string enemy = other == null ? null : s.Data.Galaxy.Systems.Select(x => x.Name).Where(n => n != other.System && s.OwnerOf(n) != me).OrderBy(n => n).FirstOrDefault();
            if (enemy != null)
            {
                gs.ShotWindow("ground", enemy); gs.PlayRefresh();
                string no = gs.MgDrop($"R:{other.Id}", $"Ground {enemy} Regiments");
                // a planet you don't hold shows no regiments to drop on; where it does, the drop says why
                Expect(no != null && (no.Contains("need a ship") || no.StartsWith("no such zone")), $"a regiment alone can't go to {enemy}, not yours: {no}");
                Expect(cons.CheckSendRegiment(new Praedra.Core.Clock.SendRegimentOrder(0, me, other.Id, enemy)) == Praedra.Core.Clock.RejectReason.NotYourSystem, "the core refuses it too");
            }
        }

        (int Fly, Praedra.Core.Fleets.Fleet Dst, int One)? joinCheck = null;
        // ---------------------------------------------------------------- one capital ship flies alone to a fleet in another system and joins it
        var mine = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && f.Ships.Count > 0).OrderBy(f => f.Id).ToList();
        var send = mine.Where(f => f.Ships.Count >= 2)
            .SelectMany(f => mine.Where(t => t.At != f.At).Select(t => (f, t)))
            .FirstOrDefault(p => fm.CheckSplitFleet(new Praedra.Core.Clock.SplitFleetOrder(0, me, p.f.Id, new() { p.f.Ships[^1] }, p.t.At, p.t.Id)) == null);
        if (send.f != null)
        {
            var (src, dst) = send;
            int one = src.Ships[^1];
            gs.ShotWindow("fleet", dst.Id.ToString()); gs.PlayRefresh();
            string r = gs.MgDrop($"S:{one}", $"Fleet {dst.Id} Ships");
            Expect(r == null, $"drop one ship of {src.Name} ({src.At}) on {dst.Name} ({dst.At}): {r ?? "sent"}");
            gs.PlayDay();
            var flying = s.Game.State.Fleets.Values.FirstOrDefault(f => f.Ships.Contains(one));
            // a one-day trip has already joined by the next day (QA-98: the planted fleet stands next to the target)
            Expect(flying != null && (flying.Id == dst.Id || flying.Id != src.Id && flying.JoinFleet == dst.Id && (flying.Transit != null || flying.At == dst.At)),
                $"next day it has split off and flies to join {dst.Name} (or has joined it)");
            if (flying != null && flying.Id != src.Id && flying.Id != dst.Id) joinCheck = (flying.Id, dst, one);
        }
        else GD.Print("  (no fleet of two ships with another fleet elsewhere: the lone-ship send is not checked on this side)");

        // ---------------------------------------------------------------- Esc closes the top window first
        for (int i = 0; i < 5 && gs.AnyModal; i++) gs.PressKey(Key.Escape);   // a report the days opened goes first
        int open = gs.MgmtCount;
        gs.PressKey(Key.Escape);
        Expect(gs.MgmtCount == open - 1, $"Esc closes the window on top ({open} -> {gs.MgmtCount}: {string.Join(", ", gs.MgmtKeys)})");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);

        // ---------------------------------------------------------------- the sector tile opens them
        gs.TileClick(sys, "infra"); gs.PlayRefresh();
        Expect(gs.MgmtKeys.Contains($"Infra {sys}") && gs.MgText($"Infra {sys}").Contains(s.OwnerOf(sys) == me ? "System energy" : "Energy slots"), "a click on a tile's dots opens its Infrastructure window");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        PlaytestInfra(gs, sys);
        gs.TileClick(sys, "infra"); gs.PlayRefresh();
        gs.TileClick(sys, "fleets"); gs.PlayRefresh();
        Expect(gs.MgmtKeys.Contains($"Fleet {fid}"), "a click on its triangles opens the fleet windows");
        gs.TileClick(sys, "ground"); gs.PlayRefresh();
        Expect(gs.MgmtKeys.Contains($"Ground {sys}"), "a click elsewhere on the tile opens its Ground window");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);

        // ---------------------------------------------------------------- painted unit pictures (art/units/units.json; Lictors first, Paul 2026-11-06)
        Expect(UnitArt.Has("Lictors") && UnitArt.Thumb("Lictors") != null && UnitArt.Card("Lictors", "tip") != null, "the Lictors have their painted picture, tile crop and hover card");
        string cap = Praedra.Core.Control.ControlModule.CapitalSystem;
        if (me == Praedra.Core.Clock.Side.Hand && s.OwnerOf(cap) == Praedra.Core.Clock.Side.Hand)
        {
            // the Lictors' build card needs a Training facility at Charter, which 400 AI days, or a raid in the days above, may have destroyed
            // (QA-98): plant one right here, with no day between (test only)
            if (s.Host.Economy.OfKindAt(cap, FacilityKind.TrainingFacility) == 0 && s.Host.Economy.FreeEnergy(cap) > 0)
            { s.Host.Economy.AddFacility(cap, FacilityKind.TrainingFacility); gs.ForgetYardIndex(); }
            gs.OpenMgmt(GameScreen.MgKind.Infra, cap); gs.PlayRefresh(); gs.Click($"Yard {cap} Training"); gs.PlayRefresh();
            string troops = gs.MgText($"Infra {cap}"); // rebuilds the window as the next frame would, so the Troops tab's cards exist
            var lictors = UiTest.Find<Button>(gs, "Build Lictors") as InfoButton;
            string lcard = CardText(lictors?.Make?.Invoke());
            Expect(lcard.Contains("Lictors") && lcard.Contains("Attack") && lcard.Contains("Defence") && lcard.Contains("Refined cost"),
                "the Lictors' build card hover shows their painted picture, role, description and stats (Paul 2026-11-19): " + lcard.Replace("\n", " / ") + " | " + troops.Replace("\n", " / "));
            // a ship's card, as the shipyard's build card makes it (Charter itself has no shipyard)
            var bondRow = s.Host.Economy.Costs.Rows.First(r => r.Name == "Bond");
            string bcard = CardText(UnitInfo.Card("Bond", "Bond", PixelIcons.Building(UnitIcon.Kind.Building), Ui.You, gs.ClassStats(bondRow, 40, out _), ""));
            Expect(bcard.Contains("Hull") && bcard.Contains("Regiment room") && bcard.Contains("Trip time") && UnitInfo.Of("Bond") is { } be && bcard.Contains(be.Role),
                "a ship's hover card shows its role and its stats: " + bcard.Replace("\n", " / "));
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }
        // every class either side can build or train has its role and description (data/design/unit-descriptions.csv, from the lore)
        {
            var missing = s.Host.Economy.Costs.Rows.Where(r => r.Cost > 0).Select(r => r.Name).Where(n => UnitInfo.Of(n) is not { Role.Length: > 0, Description.Length: > 0 }).ToList();
            Expect(missing.Count == 0, "every class that can be built or trained has a role and a description: missing " + string.Join(", ", missing));
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }
        // every unit class has its own pixel picture, and every leader the picture of their side and role (Paul 2026-11-23: "a ship has a
        // ship icon, a hero a character icon", one picture per class, for the whole game)
        {
            static System.Collections.Generic.IReadOnlyDictionary<string, string[]> SetOf(Praedra.Core.Economy.ObjectCategory c) => c switch
            {
                Praedra.Core.Economy.ObjectCategory.CapitalShip => PixelIconData.Ships, Praedra.Core.Economy.ObjectCategory.Fighter => PixelIconData.Craft,
                Praedra.Core.Economy.ObjectCategory.Regiment => PixelIconData.Troops, _ => PixelIconData.Agents,
            };
            var noSprite = s.Host.Economy.Costs.Rows.Where(r => r.Category != Praedra.Core.Economy.ObjectCategory.Facility)
                .Where(r => !SetOf(r.Category).ContainsKey(PixelIcons.Slug(r.Name))).Select(r => r.Name).ToList();
            Expect(noSprite.Count == 0, "every ship, fighter, regiment and special force class has its own pixel picture: missing " + string.Join(", ", noSprite));
            var noFace = s.Host.Characters.Data.All.Where(k => PixelIcons.Leader(k.Side == Praedra.Core.Clock.Side.Hand, PixelIcons.RoleOf(k.Rank, k.CanAdmiral, k.CanGeneral)) == null)
                .Select(k => k.Name).ToList();
            Expect(noFace.Count == 0, "every leader has a picture for their side and role: missing " + string.Join(", ", noFace));
            var roles = s.Host.Characters.Data.All.GroupBy(k => (k.Side, PixelIcons.RoleOf(k.Rank, k.CanAdmiral, k.CanGeneral))).Select(g => $"{g.Key.Side} {g.Key.Item2} {g.Count()}");
            GD.Print("  leader pictures by side and role: " + string.Join(", ", roles.OrderBy(x => x, StringComparer.Ordinal)));
        }

        // ---------------------------------------------------------------- a stack of regiments dragged with Shift: every one boards (Paul 2026-11-03)
        var stackFleet = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && s.OwnerOf(f.At) == me && fm.FreeTroopSlots(f) >= 2)
            .OrderBy(f => f.Id).FirstOrDefault(f => s.Host.Troops.RegimentsOnSystem(f.At).Count(o => o.Side == me) >= 2);
        if (stackFleet != null)
        {
            var two = s.Host.Troops.RegimentsOnSystem(stackFleet.At).Where(o => o.Side == me).OrderBy(o => o.Id).Take(2).ToList();
            gs.ShotWindow("fleet", $"{stackFleet.Id}:Troops"); gs.PlayRefresh();
            // QA-111: a ship picked first, then a regiment: the troops box takes the drag, says how many may go, and the ship stays picked
            string tz = $"Fleet {stackFleet.Id} Troops";
            int ship = stackFleet.Ships.OrderBy(x => x).First();
            gs.PlayPick($"S:{ship}", false); gs.PlayPick($"R:{two[0].Id}", true);
            string mix = gs.PlayDragPayload($"S:{ship}");
            string hint = gs.PlayDropHint(mix, tz);
            Expect(hint.StartsWith("Drop to load: up to 1 of 2"), $"a ship and a regiment picked, dragged by the ship over the troops: the hint says what may go ({hint})");
            string mr = gs.MgDrop(mix, tz);
            Expect(mr == null && gs.PickedKeys.SequenceEqual(new[] { $"S:{ship}" }), $"the drop sends the regiment and the ship stays picked: {mr ?? "sent"}, picked {string.Join(" ", gs.PickedKeys)}");
            gs.PlayPick($"S:{ship}", false);
            string r = gs.MgDrop($"R:{two[0].Id},{two[1].Id}", tz);
            Expect(r == null, $"drop a stack of two regiments on {stackFleet.Name}'s troops: {r ?? "sent"}");
            gs.PlayDay();
            Expect(two.All(o => o.AboardShip != 0 && stackFleet.Ships.Contains(o.AboardShip)), "next day both are aboard");
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }
        else GD.Print("  (no fleet with room for two regiments where two stand: the stack drop is not checked on this side)");

        // ---------------------------------------------------------------- pick with click and Shift+click, drag one, all go (Paul 2026-11-19)
        var pickAt = s.Data.Galaxy.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == me).OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(n => s.Host.Troops.RegimentsOnSystem(n).Count(o => o.Side == me && o.AboardShip == 0) >= 3);
        if (pickAt != null)
        {
            var three = s.Host.Troops.RegimentsOnSystem(pickAt).Where(o => o.Side == me && o.AboardShip == 0).OrderBy(o => o.Id).Take(3).ToList();
            gs.ShotWindow("troops", pickAt); gs.PlayRefresh();
            gs.PlayPick($"R:{three[0].Id}", false); gs.PlayPick($"R:{three[2].Id}", true);
            Expect(gs.PickedKeys.SequenceEqual(new[] { $"R:{three[0].Id}", $"R:{three[2].Id}" }), $"at {pickAt} a click picks a regiment and Shift+click adds another: " + string.Join(" ", gs.PickedKeys));
            gs.PlayPick($"R:{three[1].Id}", true); gs.PlayPick($"R:{three[1].Id}", true);
            Expect(gs.PickedKeys.Count == 2, "Shift+click on a picked one takes it out again");
            string payload = gs.PlayDragPayload($"R:{three[2].Id}");
            Expect(payload == $"R:{three[2].Id},{three[0].Id}", "dragging a picked card carries every picked unit, itself first: " + payload);
            Expect(gs.PlayDragPayload($"R:{three[1].Id}") == $"R:{three[1].Id}", "dragging a card that is not picked carries only it");
            var room = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At == pickAt && f.Transit == null && fm.FreeTroopSlots(f) >= 2).OrderBy(f => f.Id).FirstOrDefault();
            if (room != null)
            {
                gs.ShotWindow("fleet", $"{room.Id}:Troops"); gs.PlayRefresh();
                string pr = gs.MgDrop(payload, $"Fleet {room.Id} Troops");
                Expect(pr == null, $"drop the two picked regiments on {room.Name}'s troops: {pr ?? "sent"}");
                gs.PlayDay();
                Expect(new[] { three[0], three[2] }.All(o => o.AboardShip != 0 && room.Ships.Contains(o.AboardShip)) && three[1].AboardShip == 0, "next day both picked ones are aboard, the third is not");
                Expect(gs.PickedKeys.Count == 0, "a drop lets the picked units go");
                while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
                gs.ShotWindow("troops", pickAt); gs.PlayRefresh();
            }
            else GD.Print($"  (no fleet with room for two regiments at {pickAt}: the picked drop is not checked on this side)");
            gs.PlayPick($"R:{three[1].Id}", false); gs.PressKey(Key.Escape);
            Expect(gs.PickedKeys.Count == 0 && gs.MgmtCount > 0, "Escape lets the picked units go before it closes a window");
            // QA-112: closing another window keeps the pick; closing the one its cards are in lets it go
            gs.PlayPick($"R:{three[1].Id}", false);
            gs.ShotWindow("infra", pickAt); gs.PlayRefresh();
            gs.PlayCloseTop(); gs.PlayRefresh();
            Expect(gs.PickedKeys.Count == 1, "closing a window the picked card is not in keeps it picked");
            while (gs.MgmtCount > 0) gs.PlayCloseTop();
            Expect(gs.PickedKeys.Count == 0, "closing the window a picked card is in lets it go");
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }
        else GD.Print("  (no system of yours with three regiments on the ground: picking is not checked on this side)");

        // ---------------------------------------------------------------- scrap from a tile: once only (QA-87), and the garrison warning (QA-88)
        var scrapAt = s.Data.Galaxy.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == me).OrderBy(n => n)
            .FirstOrDefault(n => s.Host.Troops.RegimentsOnSystem(n).Count(o => o.Side == me && o.AboardShip == 0) >= 2);
        if (scrapAt != null)
        {
            var regsHere = s.Host.Troops.RegimentsOnSystem(scrapAt).Where(o => o.Side == me && o.AboardShip == 0).OrderBy(o => o.Id).ToList();
            var victim = regsHere[0];
            bool short1 = s.Host.Support.RegimentCount(scrapAt) - 1 < s.Host.Support.RequiredGarrison(scrapAt);
            Expect(gs.ScrapQuestion(victim.Id).Contains("short of troops") == short1, $"the scrap question at {scrapAt} warns of the garrison exactly when it falls short ({short1})");
            gs.ShotWindow("troops", scrapAt); gs.PlayRefresh();
            gs.ScrapNow(victim.Id);
            string again = gs.MgDrop($"R:{victim.Id}", $"Ground {scrapAt} Regiments");
            Expect(again != null && again.Contains("scrapped"), $"a regiment scrapped today can't be moved or scrapped again: {again}");
            int before = s.Messages.Count(m => m.Text.Contains("unknown object"));
            gs.ScrapNow(victim.Id);
            gs.PlayDay();
            Expect(s.Host.Economy.Find(victim.Id) == null && s.Messages.Count(m => m.Text.Contains("unknown object")) == before, "next day it is gone, with no refused second order (QA-87)");
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }

        // ---------------------------------------------------------------- the lone ship's arrival is told as a join (QA-86); last, as it plays days
        if (joinCheck is var (jFly, jDst, jOne))
        {
            for (int k = 0; k < 80 && s.Game.State.Fleets.TryGetValue(jFly, out var fl) && fl.Transit != null; k++) gs.PlayDay();
            gs.PlayDay();
            if (!s.Game.State.Fleets.ContainsKey(jFly) && jDst.Ships.Contains(jOne))   // QA-86: told as a join, not as a strange fleet arriving
                Expect(s.Messages.Any(m => m.Text.Contains($"joined {jDst.Name} at {jDst.At}")) && !s.Messages.Any(m => m.Text == $"Fleet {jFly} arrived at {jDst.At}."),
                    $"the log says the ship joined {jDst.Name}, not that Fleet {jFly} arrived");
        }
        PlaytestFleets(gs);
    }

    /// <summary>Every label's text in a hover card, top to bottom ("" for none).</summary>
    static string CardText(Control c)
    {
        if (c == null) return "";
        var sb = new System.Text.StringBuilder();
        void Walk(Node n) { if (n is Label l) sb.Append(l.Text).Append('\n'); foreach (var k in n.GetChildren()) Walk(k); }
        Walk(c);
        c.Free();
        return sb.ToString();
    }
}
