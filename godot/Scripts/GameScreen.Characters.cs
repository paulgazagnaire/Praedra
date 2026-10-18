using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Rebellion.Core.Characters;
using Rebellion.Core.Fleets;

namespace RebellionViewer;

/// <summary>
/// Characters on the screen: the Characters list, the officers of a fleet, the General of a system, and moving a character alone. The rules are
/// the core's (rule: m5-characters-and-force); this only reads the player's own characters and queues the five orders. Characters of the other
/// side are never shown in the panels: the fog of war API does not say where they are (rule: m5-fog-and-hq §N8).
/// </summary>
public partial class GameScreen
{
    bool _charTab;     // the right-hand panel shows the Characters list
    int _moveChar = -1; // a character waiting for its destination click
    Button _charButton;

    CharacterModule Cm => S.Host.Characters;

    static readonly Color Pending = new(1f, 0.85f, 0.4f);
    static readonly Color RouteBlue = new(0.55f, 0.85f, 1f, 0.95f);

    const string LeaderHelp = "Leaders give bonuses: a fleet's Admiral multiplies its bombardment, a fleet's General helps its regiments in an invasion, and a system's General helps the defenders. Commanders have no effect yet.";

    // ================================================================== the Characters list

    void ToggleCharacters()
    {
        _plan = null; if (_pickMission) CancelMove();
        if (!_charTab)
        {
            _missionTab = false; RefreshMissionButton();
            // clear the selection first so the selection change does not close the list again
            _view.SelectedFleet = -1; _view.Select(-1);
            _lastSelectedSystem = -1; _lastSelectedFleet = -1;
        }
        _charTab = !_charTab;
        _panelDirty = true; _panelTimer = 0;
        RefreshCharButton();
    }

    void RefreshCharButton()
    {
        if (_charButton != null) _charButton.SetPressedNoSignal(_charTab);
    }

    void CharactersPanel()
    {
        var list = S.MyCharacters;
        Line("Your characters", null, 22);
        Line(LeaderHelp, Dim, 13);
        Line("Select a fleet to attach a character standing at the same system; select one of your systems to name its General. Enemy leaders are not shown: your side cannot see them.", Dim, 13);
        Add(new HSeparator());
        if (list.Count == 0) Line("none", Dim);
        foreach (var c in list.Where(c => c.Status != CharacterStatus.Dead)) CharacterBlock(c);
        var dead = list.Where(c => c.Status == CharacterStatus.Dead).ToList();
        if (dead.Count > 0)
        {
            Heading("Fallen");
            foreach (var c in dead) Line($"{S.CharName(c.Id)} - dead ({S.WhereIsCharacter(c)})", Dim);
        }
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Act("Close the Characters list", ToggleCharacters, "Back to the map panel (C)");
    }

    void CharacterBlock(CharacterRecord c)
    {
        string name = S.CharName(c.Id);
        var status = c.Status;
        int id = c.Id;
        Add(new Control { CustomMinimumSize = new Vector2(0, 4) });
        Act($"{name}  -  {GameSession.StatusWords(c)}", () => GoToCharacter(id), "Centre the map where this character is");
        Line($"Where: {S.WhereIsCharacter(c)}", status == CharacterStatus.Free ? null : Bad, 13);
        Line($"Role: {S.RoleOf(c)}", S.RoleOf(c) == "no post" ? Dim : Good, 13);
        Line(S.SkillsOf(c), Dim, 13);
        Line(S.CapabilityOf(c), Dim, 12);
        foreach (var p in S.PendingOfCharacter(id)) Line($"Waiting for the next day: {p}", Pending, 13);
        if (CanMoveAlone(c)) Act($"Move {name}...", () => StartCharMove(id), "Then click a destination system. The route and the arrival day are shown before you confirm.");
        if (CanJoin(c)) Act($"Send {name} on a mission...", () => OpenPlanner(c.System, id), "Pick the kind of mission, more team members standing at the same system, and the target.");
        else if (S.PendingMissionOf(id) is { } pm) Line($"Waiting for the next day: {GameSession.KindWords(pm.Kind)} on {pm.Target}", Pending, 13);
    }

    /// <summary>What the core's MoveCharacter accepts (free or injured, standing alone, not travelling); the core still judges the order.</summary>
    bool CanMoveAlone(CharacterRecord c) =>
        (c.Status is CharacterStatus.Free or CharacterStatus.Injured) && c.AttachedFleet == 0 && c.Transit == null && c.System != null && !Cm.IsOnMission(c.Id);

    void GoToCharacter(int id)
    {
        if (Cm.Get(id) is not { } c) return;
        string sys = S.MapPlaceOf(c);
        if (sys != null && G.TryFindSystem(sys, out int idx)) _view.Centre(idx);
    }

    string LeaderLine(CharacterRecord c) =>
        c == null ? "none" : $"{S.CharName(c.Id)} (leadership {c.EffectiveSkill(Skill.Leadership)}){(c.Status == CharacterStatus.Free ? "" : " - " + GameSession.StatusWords(c) + ", gives no bonus")}";

    // ================================================================== a fleet's officers

    void OfficersSection(Fleet f)
    {
        Heading("Officers");
        Line("The Admiral's leadership multiplies the fleet's bombardment; the fleet General helps its regiments in an invasion; Commanders have no effect yet.", Dim, 13);
        foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.Commander, CharacterPost.General })
            Line($"{GameSession.PostWords(post)}: {LeaderLine(Cm.HolderOf(f.Id, post))}");
        var riders = Cm.AttachedTo(f.Id).OrderBy(c => c.Id).ToList();
        var passengers = riders.Where(c => c.Post == CharacterPost.None).ToList();
        if (passengers.Count > 0) Line($"Passengers: {string.Join(", ", passengers.Select(c => S.CharName(c.Id)))}");
        foreach (var p in riders.SelectMany(c => S.PendingOfCharacter(c.Id))) Line($"Waiting for the next day: {p}", Pending, 13);

        bool moving = f.At == null || f.Transit != null;
        foreach (var c in riders)
        {
            var rc = c;
            Act($"Detach {S.CharName(c.Id)}", () => Detach(rc, f), "The character leaves the fleet and stays at this system.", moving);
        }
        if (moving) { Line("The fleet is travelling: officers can join or leave when it arrives.", Dim, 13); return; }

        // own characters standing free at this system (the core's AttachCharacter checks the rest)
        var here = S.MyCharacters.Where(c => c.Status == CharacterStatus.Free && c.AttachedFleet == 0 && c.Transit == null && c.System == f.At).ToList();
        if (here.Count == 0) { Line($"No other character of yours is at {f.At}.", Dim, 13); return; }
        Line($"Your characters at {f.At} can join this fleet:", Dim, 13);
        foreach (var c in here)
        {
            var rc = c;
            var cls = Cm.ClassOf(c);
            string name = S.CharName(c.Id);
            if (S.SystemPostOf(c) is string gsys)
            {
                Line($"{name} is the General of {gsys}: release that post first.", Dim, 13);
                continue;
            }
            foreach (var post in new[] { CharacterPost.Admiral, CharacterPost.Commander, CharacterPost.General, CharacterPost.None })
            {
                if (!cls.Can(post)) continue;
                var pp = post;
                var holder = post == CharacterPost.None ? null : Cm.HolderOf(f.Id, post);
                string label = post == CharacterPost.None ? $"Attach {name} as a passenger" : $"Attach {name} as {GameSession.PostWords(post)}";
                Act(label, () => Attach(rc, f, pp),
                    holder != null ? $"{S.CharName(holder.Id)} already holds this post." : $"Leadership {c.EffectiveSkill(Skill.Leadership)}. Starts when the next day begins.", holder != null);
            }
        }
    }

    void Attach(CharacterRecord c, Fleet f, CharacterPost post)
    {
        string name = S.CharName(c.Id);
        string what = post == CharacterPost.None ? $"Attach {name} to {f.Name} as a passenger" : $"Attach {name} to {f.Name} as {GameSession.PostWords(post)}";
        int cid = c.Id, fid = f.Id;
        S.TrackCharacter(cid, fid, S.Game.Submit(seq => new AttachCharacterOrder(seq, Me, cid, fid, post)), what);
        Sent(char.ToLowerInvariant(what[0]) + what[1..], f.At);
    }

    void Detach(CharacterRecord c, Fleet f)
    {
        string name = S.CharName(c.Id);
        int cid = c.Id;
        S.TrackCharacter(cid, f.Id, S.Game.Submit(seq => new DetachCharacterOrder(seq, Me, cid)), $"Detach {name} from {f.Name}");
        Sent($"detach {name} from {f.Name}", f.At);
    }

    // ================================================================== a system's General and the characters there

    void CharactersAtSystem(string name, bool mine)
    {
        var here = S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead && Cm.LocationOf(c) == name).ToList();
        var gen = Cm.SystemGeneralOf(name);
        if (gen != null && gen.Side != Me) gen = null; // only the player's own
        if (!mine && here.Count == 0 && gen == null) return;
        Heading("Characters");
        if (mine || gen != null)
        {
            Line($"System General: {LeaderLine(gen)}", gen == null ? Dim : Good);
            if (gen == null) Line("A system General's leadership helps the defenders when this planet is invaded.", Dim, 13);
            else
            {
                var g = gen;
                foreach (var p in S.PendingOfCharacter(g.Id)) Line($"Waiting for the next day: {p}", Pending, 13);
                Act($"Release {S.CharName(g.Id)} as General of {name}", () => ReleaseGeneral(g, name), "The post becomes empty; the character stays here.");
            }
        }
        if (here.Count == 0) { Line("None of your characters is here.", Dim, 13); return; }
        foreach (var c in here)
        {
            var rc = c;
            string cname = S.CharName(c.Id);
            string where = c.Status == CharacterStatus.Captured ? "prisoner of the enemy" : c.AttachedFleet != 0 ? S.RoleOf(c) : S.RoleOf(c) == "no post" ? "standing here" : S.RoleOf(c);
            Line($"{cname} - {GameSession.StatusWords(c)}, {where}. Leadership {c.EffectiveSkill(Skill.Leadership)}.", c.Status == CharacterStatus.Free ? null : Bad, 13);
            if (c.Id != gen?.Id) foreach (var p in S.PendingOfCharacter(c.Id)) Line($"Waiting for the next day: {p}", Pending, 13);
            bool standing = c.Status == CharacterStatus.Free && c.AttachedFleet == 0 && c.Transit == null;
            if (mine && gen == null && standing && Cm.ClassOf(c).CanGeneral && S.SystemPostOf(c) == null)
                Act($"Make {cname} the General of {name}", () => AssignGeneral(rc, name), "Starts when the next day begins.");
            if (CanMoveAlone(c)) Act($"Move {cname}...", () => StartCharMove(rc.Id), "Then click a destination system. The route and the arrival day are shown before you confirm.");
        }
    }

    void AssignGeneral(CharacterRecord c, string system)
    {
        string name = S.CharName(c.Id);
        int cid = c.Id;
        S.TrackCharacter(cid, -1, S.Game.Submit(seq => new AssignSystemGeneralOrder(seq, Me, cid, system)), $"Make {name} the General of {system}");
        Sent($"make {name} the General of {system}", system);
    }

    void ReleaseGeneral(CharacterRecord c, string system)
    {
        string name = S.CharName(c.Id);
        S.TrackCharacter(c.Id, -1, S.Game.Submit(seq => new ReleaseSystemGeneralOrder(seq, Me, system)), $"Release {name} as General of {system}");
        Sent($"release {name} as General of {system}", system);
    }

    // ================================================================== moving a character alone (rule: m5-characters-and-force §Moving a character alone)

    void StartCharMove(int id)
    {
        _moveChar = id; _moveFleet = -1; _moveHq = false;
        _view.PickMode = true;
        _moveBanner = $"Move {S.CharName(id)}: click a destination system (Esc cancels)";
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void UpdateCharPreview()
    {
        if (Cm.Get(_moveChar) is not { } c || c.System == null || c.Transit != null) { CancelMove(); return; }
        string name = S.CharName(c.Id);
        int h = _view.Hovered;
        if (h < 0 || G.Systems[h].Name == c.System)
        {
            _view.PathPreview = null;
            _moveBanner = $"Move {name}: click a destination system (Esc cancels)";
        }
        else
        {
            string to = G.Systems[h].Name;
            var pr = Fm.FindPath(c.System, to, Cm.Params.CharacterSpeed); // the path the core's MoveCharacter will use
            if (pr.Ok)
            {
                _view.PathPreview = pr.Systems;
                _view.PathColor = RouteBlue;
                _moveBanner = $"{name} to {to}: {pr.TotalTicks} days, arrives day {S.Game.Day + pr.TotalTicks}. Click to confirm.";
            }
            else { _view.PathPreview = null; _moveBanner = $"No route from {c.System} to {to}."; }
        }
        RefreshTop();
    }

    void OnCharPicked(int idx)
    {
        if (Cm.Get(_moveChar) is not { } c || c.System == null) { CancelMove(); return; }
        string to = G.Systems[idx].Name;
        if (to == c.System) return;
        string name = S.CharName(c.Id);
        int cid = c.Id;
        var pr = Fm.FindPath(c.System, to, Cm.Params.CharacterSpeed);
        S.TrackCharacter(cid, -1, S.Game.Submit(seq => new MoveCharacterOrder(seq, Me, cid, to)), $"Move {name} to {to}");
        if (pr.Ok) S.PlanCharacterMove(cid, new GameSession.PlannedMove(to, pr.Systems.Select(i => G.Systems[i].Name).ToList(), pr.TotalTicks, S.Game.Day + pr.TotalTicks));
        S.Say($"Order sent: move {name} to {to}.", MsgKind.Info, to);
        AddLogItem(S.Messages[^1]);
        CancelMove();
    }

    /// <summary>Routes of the player's characters travelling alone (and the trips that start next day), for the map. Own side only.</summary>
    IEnumerable<(string Label, IReadOnlyList<string> Path, int HopIndex)> CharacterRoutes()
    {
        foreach (var c in S.MyCharacters)
        {
            if (c.AttachedFleet == 0 && c.Transit is { } t)
                yield return ($"{S.CharName(c.Id)} arrives day {GameSession.ArrivalDay(t)}", t.Path, t.HopIndex);
            else if (S.PlannedCharacterMoveOf(c.Id) is { } pm)
                yield return ($"{S.CharName(c.Id)} arrives day {pm.ArrivalDay}", pm.Path, 0);
        }
    }
}
