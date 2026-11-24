using System;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the character screens (rule: m5-characters-and-talent): the Characters list, moving a character alone, attaching it to a fleet,
/// a refused order with its plain reason, detaching, and naming and releasing a system General. Every action is a click on the real button;
/// the core is only read to choose where to click and to check the result.
/// </summary>
public partial class Main
{
    void PlaytestCharacters(GameScreen gs)
    {
        var s = gs.S; var me = s.Player; var cm = s.Host.Characters; var fm = s.Host.Fleets;
        GD.Print("--- characters");
        gs.View.Select(-1); gs.PlayRefresh();

        // ---------------------------------------------------------------- the Characters list
        Expect(gs.Click("Leaders") && gs.CharactersOpen, "click the Leaders tab");
        gs.PlayRefresh();
        string panel = gs.PanelText();
        string all = panel + "\n" + string.Join("\n", gs.ButtonTexts());
        Expect(panel.Contains("Your leaders"), "the Leaders tab opens");
        var mine = me == Side.Collective ? new[] { "Nobody", "Ledger" } : new[] { "Cassia Ravilla" };
        foreach (var n in mine) Expect(all.Contains(n), $"the Leaders tab shows {n}");
        // enemy leaders: only the ones you must capture (named by the win rules) and the ones your side has seen, placed where they were seen
        // (rule: m5-fog-and-hq §N8)
        var keys = s.Host.Victory.Keys.Where(k => k.Standard && k.Side == me).Select(k => s.CharName(k.Character)).ToList();
        foreach (var k in keys) Expect(panel.Contains(k + "  (you must capture)"), $"{k} is listed as a leader you must capture");
        var seen = s.Fog.CharactersSeen(me).Where(x => x.Character.Side != me).Select(x => x.Character.Id).ToHashSet();
        var unseen = cm.All.Where(c => c.Side != me && c.Placed && !seen.Contains(c.Id) && !keys.Contains(s.CharName(c.Id))).Select(c => s.CharName(c.Id)).ToList();
        Expect(unseen.Count > 0 && !unseen.Any(panel.Contains), $"no enemy leader your side has not seen is listed ({unseen.Count} hidden)");
        Expect(keys.All(k => seen.Contains(cm.All.First(c => s.CharName(c.Id) == k).Id) || panel.Contains("not seen yet")), "an unseen key leader says 'not seen yet', never where it is");
        Expect(panel.Contains("Leadership ") && (s.MyCharacters.All(c => s.RoleOf(c) == "no post") || s.MyCharacters.Any(c => s.RoleOf(c) != "no post" && panel.Contains(s.RoleOf(c)))), "where, any post and skills are shown");
        Expect(panel.Contains("; Talent: "), "the Talent rank of the known talented character is shown");
        GD.Print("  characters: " + string.Join(" | ", s.MyCharacters.Select(c => $"{s.CharName(c.Id)} {s.WhereIsCharacter(c)} [{s.RoleOf(c)}] {s.CapabilityOf(c)}")));

        bool CanPost(CharacterRecord c) { var k = cm.ClassOf(c); return k.CanAdmiral || k.CanCommander || k.CanGeneral; }
        bool Standing(CharacterRecord c) => c.Status == CharacterStatus.Free && c.AttachedFleet == 0 && c.Transit == null && c.System != null && s.SystemPostOf(c) == null;
        int Ticks(string a, string b) => fm.FindPath(a, b, cm.Params.CharacterSpeed) is { Ok: true } p ? p.TotalTicks : int.MaxValue;

        // ---------------------------------------------------------------- move a character alone to the nearest fleet of yours at another system
        var mover = s.MyCharacters.Where(Standing).OrderBy(c => CanPost(c) ? 0 : 1).ThenBy(c => c.Id).FirstOrDefault();
        Expect(mover != null, "a free character stands somewhere");
        if (mover == null) return;
        var fleet = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Transit == null && f.Ships.Count > 0 && f.At != mover.System && Ticks(mover.System, f.At) < int.MaxValue)
            .OrderBy(f => Ticks(mover.System, f.At)).ThenBy(f => f.Id).FirstOrDefault();
        Expect(fleet != null, "a fleet of yours stands at another system");
        if (fleet == null) return;
        string mname = s.CharName(mover.Id);
        GD.Print($"  moving {mname} from {mover.System} to {fleet.At} (fleet {fleet.Name})");
        if (!MoveCharacterThroughUi(gs, mover, fleet.At)) return;

        // ---------------------------------------------------------------- attach it to the fleet: drag it onto a post of the fleet's window
        // (the only way: Paul 2026-11-22, moving a leader is never a click in a menu)
        OpenFleetTab(gs, fleet, "Command");
        var cls = cm.ClassOf(mover);
        var post = new[] { CharacterPost.Admiral, CharacterPost.Commander, CharacterPost.General }.FirstOrDefault(p => cls.Can(p) && cm.HolderOf(fleet.Id, p) == null);
        string zone = post == CharacterPost.None ? $"Fleet {fleet.Id} Passengers" : $"Fleet {fleet.Id} {post}";
        Expect(gs.MenuOf($"Zone {zone}").Count == 0 && !gs.CardTip($"Zone {zone}").Contains("Click", StringComparison.Ordinal)
            && gs.CardTip($"Zone {zone}").StartsWith("Drag a leader here", StringComparison.Ordinal),
            $"a post offers no click menu, and its hover says to drag a leader onto it: {gs.CardTip($"Zone {zone}")}");
        string why = gs.MgDrop($"L:{mover.Id}", zone);
        Expect(why == null, $"drag {mname} onto the {(post == CharacterPost.None ? "Passengers" : GameSession.PostWords(post))} place: {why ?? "sent"}");
        Expect(gs.MgDrop($"L:{mover.Id}", $"Fleet {fleet.Id} Passengers") != null, "a leader given a post today can't be moved again before the day runs");
        SelectFleetByList(gs, fleet);
        Expect(gs.PanelText().Contains($"Ordered: Attach {mname} to {fleet.Name}"), "the fleet panel shows the attach as ordered");
        int before = s.Messages.Count;
        gs.PlayDay();
        var news = s.Messages.Skip(before).Select(m => m.Text).ToList();
        Expect(mover.AttachedFleet == fleet.Id && mover.Post == post, $"{mname} is aboard {fleet.Name} as {GameSession.PostWords(post)}");
        Expect(news.Any(t => t.StartsWith(mname, StringComparison.Ordinal) && (t.Contains("joined " + fleet.Name) || t.Contains("boarded " + fleet.Name))), "message: the character joined the fleet");
        OpenFleetTab(gs, fleet, "Command");
        // the leader's card shows the first name over the last name (Paul 2026-11-22)
        Expect(gs.MgText($"Fleet {fleet.Id}").Contains(GameSession.CardName(mname)), $"the fleet's window shows {mname} {(post == CharacterPost.None ? "aboard" : "on the post")}");
        Expect(gs.Click("Leaders") && gs.CharactersOpen, "open the Leaders tab again");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"aboard fleet {fleet.Name} at {fleet.At}"), "the list says the character is aboard the fleet");
        Expect(gs.Click("Close window") && !gs.CharactersOpen, "the window's cross closes the Leaders list");

        // detach again by dragging onto the planet's Leaders (leaves the later checks a fleet without riders)
        OpenFleetTab(gs, fleet, "Command");
        gs.ShotWindow("ground", fleet.At); gs.PlayRefresh();
        why = gs.MgDrop($"L:{mover.Id}", $"Ground {fleet.At} Leaders");
        Expect(why == null, $"drag {mname} off the fleet onto {fleet.At}'s Leaders: {why ?? "sent"}");
        gs.PlayDay();
        Expect(mover.AttachedFleet == 0 && mover.System == fleet.At && mover.Post == CharacterPost.None, "detached: the character stays at the system");
        gs.MgCloseAll();

        // a passenger lands at a planet you don't hold too (QA on #357: the core lets them off anywhere the fleet stands still, rule:
        // m5-characters §Posts, DetachCharacter): that planet's Ground window has a Leaders tab, with no General post. Test only: the system
        // is made nobody's for the drop, then given back before the day runs.
        OpenFleetTab(gs, fleet, "Command");
        Expect(gs.MgDrop($"L:{mover.Id}", $"Fleet {fleet.Id} Passengers") == null, $"{mname} boards {fleet.Name} as a passenger");
        gs.PlayDay();
        if (mover.AttachedFleet == fleet.Id)
        {
            string here = fleet.At!;
            fm.SetOwner(here, null);
            gs.MgCloseAll(); OpenFleetTab(gs, fleet, "Command"); gs.ShotWindow("ground", here); gs.PlayRefresh();
            var tabs = gs.MgTabs($"Ground {here}");
            Expect(tabs.Contains("Leaders") && !tabs.Contains("Troops"), $"a planet you don't hold: its Ground window has Leaders (and no Troops): {string.Join(", ", tabs)}");
            Expect(!gs.MgPageText($"Ground {here}", "Leaders").Contains("General"), "no General post on a planet you don't hold");
            why = gs.MgDrop($"L:{mover.Id}", $"Ground {here} Leaders");
            Expect(why == null, $"drag {mname} off the fleet onto the Leaders of a planet you don't hold: {why ?? "sent"}");
            fm.SetOwner(here, me);
            gs.PlayDay();
            Expect(mover.AttachedFleet == 0 && mover.System == here, "landed there");
        }
        gs.MgCloseAll();

        // ---------------------------------------------------------------- name a system General, then release the post
        bool Holdable(string sys) => s.OwnerOf(sys) == me && cm.SystemGeneralOf(sys) == null;
        var gen = s.MyCharacters.Where(c => Standing(c) && cm.ClassOf(c).CanGeneral && Holdable(c.System)).OrderBy(c => c.Id).FirstOrDefault();
        if (gen == null)
        {
            // a General-capable character elsewhere walks to the nearest system of yours that has no General
            var walker = s.MyCharacters.Where(c => Standing(c) && cm.ClassOf(c).CanGeneral).OrderBy(c => c.Id).FirstOrDefault();
            Expect(walker != null, "a free character who can be a General stands somewhere");
            if (walker == null) return;
            string to = s.Game.State.SystemOwners.Where(kv => kv.Value == me && kv.Key != walker.System && Holdable(kv.Key)).Select(kv => kv.Key)
                .Where(x => Ticks(walker.System, x) < int.MaxValue).OrderBy(x => Ticks(walker.System, x)).ThenBy(x => x, StringComparer.Ordinal).First();
            if (!MoveCharacterThroughUi(gs, walker, to)) return;
            gen = walker;
        }
        string gname = s.CharName(gen.Id), gsys = gen.System;
        GD.Print($"  system General: {gname} at {gsys}");
        gs.SelectSystem(gsys); gs.PlayRefresh();
        Expect(gs.PanelText().Contains("System General: none"), $"the Selected tab of {gsys} says it has no General");
        if (gs.S.Host.Economy.Objects.Any(o => o.Category == Praedra.Core.Economy.ObjectCategory.Fighter && o.Side == gs.S.Player && o.System == gsys && o.AboardShip == 0 && o.Completed))
            Expect(gs.PanelText().Contains("Fighters based here"), $"the Selected tab of {gsys} lists the fighters based there");
        // a General is seated only by a drag in Ground at the system, never by a click in the side panel (Paul 2026-11-22, UI review 4)
        Expect(!gs.PanelText().Contains($"Make {gname} the General") && gs.PanelText().Contains($"To seat or release a General, drag them in Leaders, Ground at {gsys}"),
            "the Selected tab has no click to seat a General, and says where to drag");
        gs.ShotWindow("ground", gsys); gs.PlayRefresh();
        why = gs.MgDrop($"L:{gen.Id}", $"Ground {gsys} General");
        Expect(why == null, $"drag {gname} onto the General post of {gsys}: {why ?? "sent"}");
        gs.MgCloseAll();
        before = s.Messages.Count;
        gs.PlayDay();
        Expect(cm.SystemGeneralOf(gsys)?.Id == gen.Id, $"{gname} is the General of {gsys}");
        Expect(s.Messages.Skip(before).Any(m => m.Text.StartsWith($"{gname} is now the General of {gsys}", StringComparison.Ordinal)), "message: the new General");
        gs.SelectSystem(gsys); gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"System General: {gname} (leadership"), "the system panel shows the General");
        Expect(!gs.PanelText().Contains($"Release {gname} as General"), "the Selected tab has no click to release the General");
        gs.ShotWindow("ground", gsys); gs.PlayRefresh();
        why = gs.MgDrop($"L:{gen.Id}", $"Ground {gsys} Leaders");
        Expect(why == null, $"drag {gname} off the General post onto {gsys}'s Leaders: {why ?? "sent"}");
        gs.MgCloseAll();
        before = s.Messages.Count;
        gs.PlayDay();
        Expect(cm.SystemGeneralOf(gsys) == null, "the post is empty again");
        Expect(s.Messages.Skip(before).Any(m => m.Text == $"{gname} is no longer a system General."), "message: the General was released");
        gs.View.Select(-1); gs.PlayRefresh();
    }

    /// <summary>Characters list, Move NAME..., hover (route and arrival day), click, and run the days until it arrives on the announced day.</summary>
    bool MoveCharacterThroughUi(GameScreen gs, CharacterRecord c, string to)
    {
        var s = gs.S; var cm = s.Host.Characters; var fm = s.Host.Fleets;
        string name = s.CharName(c.Id), from = c.System;
        if (!gs.CharactersOpen) gs.ClickViaMenu("Leaders");
        gs.PlayRefresh();
        Expect(gs.Click($"Move {name}..."), $"click 'Move {name}...' in the Leaders tab");
        Expect(gs.Banner.Contains("Move " + name), "the banner asks for a destination: " + gs.Banner);
        int arrive = s.Game.Day + fm.FindPath(from, to, cm.Params.CharacterSpeed).TotalTicks;
        gs.HoverTextFor(to);
        Expect(gs.Banner.Contains($"{name} to {to}") && gs.Banner.Contains($"arrives day {arrive}"), "the preview shows the route and the arrival day: " + gs.Banner);
        gs.PickSystem(to);
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"Ordered: Move {name} to {to}") && gs.PanelText().Contains($"leaving for {to}, arrives day {arrive}"),
            "the list shows the waiting move and its arrival day");
        int before = s.Messages.Count;
        gs.PlayDay();
        var news = s.Messages.Skip(before).Select(m => m.Text).ToList();
        Expect(news.Any(t => t.StartsWith($"Move {name} to {to}: ", StringComparison.Ordinal) && t.Contains($"arrives day {arrive}")), "message with the trip and arrival day: " + string.Join(" | ", news));
        if (c.Transit != null)
        {
            gs.PlayRefresh();
            Expect(gs.PanelText().Contains($"travelling alone to {to}, arrives day {arrive}"), "the list says the character is travelling");
        }
        bool there = RunUntil(gs, () => c.System == to && c.Transit == null, 400);
        Expect(there, $"{name} arrived at {to} (day {s.Game.Day})");
        Expect(s.Game.Day == arrive, $"on the announced day {arrive}");
        Expect(s.Messages.Any(m => m.Text == $"{name} arrived at {to}."), "arrival message");
        return there;
    }
}
