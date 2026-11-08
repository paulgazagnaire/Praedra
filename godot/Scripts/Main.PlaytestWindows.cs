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
        var lead = s.MyCharacters.Where(c => Free(c) && c.AttachedFleet == 0 && s.OwnerOf(cm.LocationOf(c)) == me).OrderBy(c => c.Id).FirstOrDefault();
        Expect(lead != null, "a free leader of yours stands on a system you hold");
        if (lead == null) return;
        string transport = me == Praedra.Core.Clock.Side.Hand ? "Bond II" : "Barge";
        var fleet = s.Game.State.Fleets[fm.AddFleet(me, "Window Test", new[] { transport, transport }, cm.LocationOf(lead))];
        // the Lictors' build card below needs a Training facility at Charter, which 400 AI days may have destroyed (QA-98): plant one (test only)
        string capital = Praedra.Core.Control.ControlModule.CapitalSystem;
        if (me == Praedra.Core.Clock.Side.Hand && s.OwnerOf(capital) == me && s.Host.Economy.FreeEnergy(capital) > 0)
            s.Host.Economy.AddFacility(capital, FacilityKind.TrainingFacility);
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

        // ---------------------------------------------------------------- a leader onto a post, then back onto the planet
        var cls = cm.ClassOf(lead);
        var post = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.FirstOrDefault(cls.Can);
        string zone = post == CharacterPost.None ? $"Fleet {fid} Passengers" : $"Fleet {fid} {post}";
        string why = gs.MgDrop($"L:{lead.Id}", zone);
        Expect(why == null, $"drop {lname} on {zone}: {why ?? "sent"}");
        Expect(!gs.MgText($"Ground {sys}").Contains(lname) && gs.MgText($"Fleet {fid}").Contains(lname), "the drop shows at once: the chip leaves the planet and stands in the fleet");
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
            gs.PlayDay(); gs.PlayRefresh();
            Expect(far.Transit != null && far.JoinFleet == fid && gs.MgText($"Fleet {fid}").Contains("ON THE WAY"), $"next day {fname} travels to {sys}, listed under On the way");
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

        // ---------------------------------------------------------------- ships split off (two drops, one new fleet), then merged back by the grip
        if (fleet.Ships.Count > 1)
        {
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
                gs.ShotWindow("fleet", made.Id.ToString()); gs.PlayRefresh();
                Expect(gs.MgDrop($"M:{made.Id}", $"Fleet {fid} Ships") == null, $"drag {made.Name} by its grip onto {fleet.Name}'s ships");
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
        Expect(gs.MgmtKeys.Contains($"Infra {sys}") && gs.MgText($"Infra {sys}").Contains("Energy slots"), "a click on a tile's dots opens its Infrastructure window");
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
            gs.OpenMgmt(GameScreen.MgKind.Infra, cap); gs.PlayRefresh(); gs.Click("Troops"); gs.PlayRefresh();
            string troops = gs.MgText($"Infra {cap}"); // rebuilds the window as the next frame would, so the Troops tab's cards exist
            Expect(UiTest.Find<Button>(gs, "Build Lictors") is ArtButton, "the Lictors' build card shows their picture and its hover card: " + troops.Replace("\n", " / "));
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }

        // ---------------------------------------------------------------- a stack of regiments dragged with Shift: every one boards (Paul 2026-11-03)
        var stackFleet = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && s.OwnerOf(f.At) == me && fm.FreeTroopSlots(f) >= 2)
            .OrderBy(f => f.Id).FirstOrDefault(f => s.Host.Troops.RegimentsOnSystem(f.At).Count(o => o.Side == me) >= 2);
        if (stackFleet != null)
        {
            var two = s.Host.Troops.RegimentsOnSystem(stackFleet.At).Where(o => o.Side == me).OrderBy(o => o.Id).Take(2).ToList();
            gs.ShotWindow("fleet", $"{stackFleet.Id}:Troops"); gs.PlayRefresh();
            string r = gs.MgDrop($"R:{two[0].Id},{two[1].Id}", $"Fleet {stackFleet.Id} Troops");
            Expect(r == null, $"drop a stack of two regiments on {stackFleet.Name}'s troops: {r ?? "sent"}");
            gs.PlayDay();
            Expect(two.All(o => o.AboardShip != 0 && stackFleet.Ships.Contains(o.AboardShip)), "next day both are aboard");
            while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
        }
        else GD.Print("  (no fleet with room for two regiments where two stand: the stack drop is not checked on this side)");

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
    }
}
