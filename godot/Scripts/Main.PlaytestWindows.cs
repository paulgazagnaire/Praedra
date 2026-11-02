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
        // a fleet standing still with a free leader of yours at the same system (the character playtest leaves one)
        var pair = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && f.Ships.Count > 0).OrderBy(f => f.Id)
            .SelectMany(f => s.MyCharacters.Where(c => Free(c) && cm.LocationOf(c) == f.At && c.AttachedFleet != f.Id).OrderBy(c => c.Id).Select(c => (f, c)))
            .FirstOrDefault();
        Expect(pair.f != null, "a fleet and a free leader of yours stand at the same system");
        if (pair.f == null) return;
        var (fleet, lead) = pair;
        string sys = fleet.At, lname = s.CharName(lead.Id);
        int fid = fleet.Id;
        GD.Print($"  {lname} and {fleet.Name} at {sys}");

        // ---------------------------------------------------------------- open the two windows side by side
        gs.ShotWindow("ground", sys); gs.PlayRefresh();
        string ground = gs.MgText($"Ground {sys}");
        Expect(gs.MgmtKeys.Contains($"Ground {sys}") && ground.Contains("LEADERS ON THE PLANET") && ground.Contains("YOUR FLEETS IN ORBIT"), "the Ground window lists the leaders and the fleets in orbit");
        Expect(gs.Click($"Open {fleet.Name}"), $"the Ground window's Open button opens {fleet.Name}");
        gs.PlayRefresh();
        Expect(gs.MgmtCount == 2 && gs.MgHasZone($"Fleet {fid} Admiral") && gs.MgHasZone($"Fleet {fid} Passengers"), "the fleet window opens beside it, with its posts and passengers");

        // ---------------------------------------------------------------- a leader onto a post, then back onto the planet
        var cls = cm.ClassOf(lead);
        var post = new[] { CharacterPost.Admiral, CharacterPost.General, CharacterPost.Commander }.FirstOrDefault(cls.Can);
        string zone = post == CharacterPost.None ? $"Fleet {fid} Passengers" : $"Fleet {fid} {post}";
        string why = gs.MgDrop($"L:{lead.Id}", zone);
        Expect(why == null, $"drop {lname} on {zone}: {why ?? "sent"}");
        Expect(gs.MgText($"Ground {sys}").Contains("→ ") && gs.MgText($"Fleet {fid}").Contains("arrives next day"), "until the next day the chip says where it goes, and a faint copy waits where it was dropped");
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

        // ---------------------------------------------------------------- a refusal says why
        var far = s.MyCharacters.FirstOrDefault(c => Free(c) && cm.LocationOf(c) != sys && c.AttachedFleet == 0);
        if (far != null)
        {
            string r = gs.MgDrop($"L:{far.Id}", $"Fleet {fid} Passengers");
            Expect(r != null && r.Contains("is not at"), $"a leader at another system is refused with a reason: {r}");
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

        // ---------------------------------------------------------------- a ship split off, then the new fleet merged back by its grip
        if (fleet.Ships.Count > 1)
        {
            int ship = fleet.Ships[^1];
            var before = s.Game.State.Fleets.Keys.ToHashSet();
            Expect(gs.MgDrop($"S:{ship}", $"Fleet {fid} NewFleet") == null, "drop a ship on New fleet");
            gs.PlayDay();
            var made = s.Game.State.Fleets.Values.FirstOrDefault(f => !before.Contains(f.Id) && f.Ships.Contains(ship));
            Expect(made != null, "next day the ship is a fleet of its own");
            if (made != null)
            {
                gs.ShotWindow("fleet", made.Id.ToString()); gs.PlayRefresh();
                Expect(gs.MgDrop($"M:{made.Id}", $"Fleet {fid} Ships") == null, $"drag {made.Name} by its grip onto {fleet.Name}'s ships");
                gs.PlayDay(); gs.PlayRefresh();
                Expect(!s.Game.State.Fleets.ContainsKey(made.Id) && fleet.Ships.Contains(ship), "next day it has merged back");
                Expect(!gs.MgmtKeys.Contains($"Fleet {made.Id}"), "the merged fleet's window closes by itself");
            }
        }

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
        Expect(gs.MgmtKeys.Contains($"Ground {sys}"), "a click elsewhere on the tile opens the Ground window");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
    }
}
