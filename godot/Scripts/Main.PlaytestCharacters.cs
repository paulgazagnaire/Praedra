using System;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Playtest of the character screens (rule: m5-characters-and-force): the Characters list, moving a character alone, attaching it to a fleet,
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
        Expect(gs.Click("Characters") && gs.CharactersOpen, "click Characters in the top bar");
        gs.PlayRefresh();
        string panel = gs.PanelText();
        string all = panel + "\n" + string.Join("\n", gs.ButtonTexts());
        Expect(panel.Contains("Your characters"), "the Characters list opens");
        var mine = me == Side.Collective ? new[] { "Nobody", "Ledger" } : new[] { "Cassia Ravilla" };
        foreach (var n in mine) Expect(all.Contains(n), $"the Characters list shows {n}");
        var theirs = me == Side.Collective ? new[] { "Cassia Ravilla", "The Chair" } : new[] { "Nobody", "Ledger", "Mae Abara" };
        Expect(!theirs.Any(all.Contains), "no enemy leader is listed (the side cannot see them)");
        Expect(panel.Contains("Where: ") && panel.Contains("Role: ") && panel.Contains("Leadership "), "where, role and skills are shown");
        Expect(panel.Contains("Force: Jedi"), "the Force rank of the known Jedi is shown");
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

        // ---------------------------------------------------------------- attach it to the fleet, with a second, conflicting order the same day
        SelectFleetByList(gs, fleet);
        Expect(!gs.CharactersOpen && gs.PanelText().Contains("Officers"), "selecting a fleet closes the list and shows its Officers");
        var cls = cm.ClassOf(mover);
        var post = new[] { CharacterPost.Admiral, CharacterPost.Commander, CharacterPost.General }.FirstOrDefault(p => cls.Can(p) && cm.HolderOf(fleet.Id, p) == null);
        string label = post == CharacterPost.None ? $"Attach {mname} as a passenger" : $"Attach {mname} as {GameSession.PostWords(post)}";
        Expect(gs.Click(label), $"click '{label}'");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"Waiting for the next day: Attach {mname} to {fleet.Name}"), "the fleet panel shows the waiting attach");
        Expect(gs.Click($"Attach {mname} as a passenger"), "click a second attach for the same character before the day starts");
        int before = s.Messages.Count;
        gs.PlayDay();
        var news = s.Messages.Skip(before).Select(m => m.Text).ToList();
        Expect(mover.AttachedFleet == fleet.Id && mover.Post == post, $"{mname} is aboard {fleet.Name} as {GameSession.PostWords(post)}");
        Expect(news.Any(t => t.StartsWith($"Order refused: Attach {mname} to {fleet.Name}", StringComparison.Ordinal) && t.Contains("already aboard a fleet")),
            "the second order is refused with a plain reason: " + string.Join(" | ", news));
        Expect(news.Any(t => t.StartsWith(mname, StringComparison.Ordinal) && (t.Contains("joined fleet " + fleet.Name) || t.Contains("boarded fleet " + fleet.Name))), "message: the character joined the fleet");
        SelectFleetByList(gs, fleet);
        string fp = gs.PanelText();
        Expect(post == CharacterPost.None ? fp.Contains("Passengers: ") && fp.Contains(mname) : fp.Contains($"{GameSession.PostWords(post)}: {mname} (leadership"), "the fleet panel shows the character: " + fp.Replace("\n", " / "));
        Expect(gs.Click("Characters") && gs.CharactersOpen, "open the Characters list again");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"aboard fleet {fleet.Name} at {fleet.At}"), "the list says the character is aboard the fleet");
        Expect(gs.Click("Close the Characters list") && !gs.CharactersOpen, "close the Characters list");

        // detach again (leaves the later checks a fleet without riders)
        SelectFleetByList(gs, fleet);
        Expect(gs.Click($"Detach {mname}"), $"click 'Detach {mname}'");
        gs.PlayDay();
        Expect(mover.AttachedFleet == 0 && mover.System == fleet.At && mover.Post == CharacterPost.None, "detached: the character stays at the system");

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
        Expect(gs.PanelText().Contains("System General: none"), $"the panel of {gsys} says it has no General");
        Expect(gs.Click($"Make {gname} the General of {gsys}"), $"click 'Make {gname} the General of {gsys}'");
        before = s.Messages.Count;
        gs.PlayDay();
        Expect(cm.SystemGeneralOf(gsys)?.Id == gen.Id, $"{gname} is the General of {gsys}");
        Expect(s.Messages.Skip(before).Any(m => m.Text.StartsWith($"{gname} is now the General of {gsys}", StringComparison.Ordinal)), "message: the new General");
        gs.SelectSystem(gsys); gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"System General: {gname} (leadership"), "the system panel shows the General");
        Expect(gs.Click($"Release {gname} as General of {gsys}"), "click Release");
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
        if (!gs.CharactersOpen) gs.Click("Characters");
        gs.PlayRefresh();
        Expect(gs.Click($"Move {name}..."), $"click 'Move {name}...' in the Characters list");
        Expect(gs.Banner.Contains("Move " + name), "the banner asks for a destination: " + gs.Banner);
        int arrive = s.Game.Day + fm.FindPath(from, to, cm.Params.CharacterSpeed).TotalTicks;
        gs.HoverTextFor(to);
        Expect(gs.Banner.Contains($"{name} to {to}") && gs.Banner.Contains($"arrives day {arrive}"), "the preview shows the route and the arrival day: " + gs.Banner);
        gs.PickSystem(to);
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"Waiting for the next day: Move {name} to {to}") && gs.PanelText().Contains($"leaves when the next day starts for {to}, arrives day {arrive}"),
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
