using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Rebellion.Core.Characters;
using Rebellion.Core.Economy;
using Rebellion.Core.Missions;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// Missions on the screen (rule: m5-missions): "Send on a mission..." (pick the team at one system, the kind, the target with its route and
/// arrival day, confirm), the Missions list with Cancel, and the special forces at a system. What a team may do is asked of the core's read-only
/// dry run (<see cref="MissionModule.CheckStart"/>); the core still judges the order when the next day starts. Only the player's own missions
/// and units are shown (rule: m5-fog-and-hq §N8).
/// </summary>
public partial class GameScreen
{
    bool _missionTab;   // the right-hand panel shows the Missions list
    MissionPlan _plan;  // the "Send on a mission" planner, or null
    bool _pickMission;  // waiting for the target click
    Button _missionButton;

    MissionModule Mm => S.Missions;

    /// <summary>The planner's choices. Home is where the team stands; the team members must all be there (rule: m5-missions §Orders NotTogether).</summary>
    sealed class MissionPlan
    {
        public string Home;
        public MissionKind? Kind;
        public readonly SortedSet<int> Chars = new(), Sfs = new();
        public string Target;
        public int? TargetChar;
    }

    const string MissionAdvice = "Worth it now: Espionage (see what an enemy system holds) and Diplomacy (win a neutral system's support before you take it). Sabotage is very hard: the team is usually caught.";

    /// <summary>The kinds the screens offer, in this order; story and travel records are never offered (rule: m5-missions §Mission records: hidden).</summary>
    static readonly MissionKind[] OfferedKinds =
    {
        MissionKind.Espionage, MissionKind.Diplomacy, MissionKind.Reconnaissance, MissionKind.Sabotage, MissionKind.InciteUprising, MissionKind.SubdueUprising,
        MissionKind.Rescue, MissionKind.ShipResearch, MissionKind.FacilityResearch, MissionKind.TroopResearch,
        MissionKind.Recruitment, MissionKind.Abduction, MissionKind.Assassination, MissionKind.DeathStarSabotage, MissionKind.JediTraining,
    };

    // ================================================================== Missions list (top bar, key M)

    void ToggleMissions()
    {
        if (!_missionTab)
        {
            _view.SelectedFleet = -1; _view.Select(-1);
            _lastSelectedSystem = -1; _lastSelectedFleet = -1;
            _charTab = false; RefreshCharButton();
        }
        _missionTab = !_missionTab;
        _plan = null; if (_pickMission) CancelMove();
        _panelDirty = true; _panelTimer = 0;
        RefreshMissionButton();
    }

    void RefreshMissionButton() { if (_missionButton != null) _missionButton.SetPressedNoSignal(_missionTab); }

    void MissionsPanel()
    {
        Line("Your missions", null, 22);
        Line(MissionAdvice, Dim, 13);
        Line("Send a team from the Characters list (Send NAME on a mission...) or from the panel of the system where it stands. Only your own missions are listed.", Dim, 13);
        Add(new HSeparator());
        var pending = S.PendingMissions.ToList();
        var list = S.MyMissions;
        if (pending.Count == 0 && list.Count == 0) Line("No mission under way.", Dim);
        foreach (var p in pending)
        {
            Line($"{GameSession.KindWords(p.Kind)} on {p.Target}", Pending, 16);
            Line($"Team: {S.TeamWords(p.Characters, p.SpecialForces)}", null, 13);
            Line($"Waiting for the next day: leaves {p.Home} then, arrives day {p.ArrivalDay}.", Pending, 13);
        }
        foreach (var m in list) MissionBlock(m);
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Act("Close the Missions list", ToggleMissions, "Back to the map panel (M)");
    }

    void MissionBlock(MissionGroup m)
    {
        Add(new Control { CustomMinimumSize = new Vector2(0, 4) });
        string target = m.Target;
        Act($"{GameSession.KindWords(m.Kind)} on {target}", () => { if (G.TryFindSystem(target, out int i)) _view.Centre(i); }, "Centre the map on the target");
        Line($"Team: {S.TeamOf(m)}", null, 13);
        Line(char.ToUpperInvariant(S.PhaseWords(m)[0]) + S.PhaseWords(m)[1..] + ".", m.Phase == MissionPhase.Working ? Good : null, 13);
        if (m.Phase is MissionPhase.ReturnPending or MissionPhase.Returning) return;
        bool pending = S.CancelPending(m);
        if (pending) { Line("Waiting for the next day: cancel this mission.", Pending, 13); return; }
        var mm = m;
        Act($"Cancel the {GameSession.KindWords(m.Kind)} mission on {target}", () => CancelMission(mm),
            m.Phase == MissionPhase.Working ? "The team stops and comes home. Starts when the next day begins." : "A mission can be cancelled once the team is at the target.",
            m.Phase != MissionPhase.Working);
    }

    void CancelMission(MissionGroup m)
    {
        S.SubmitCancelMission(m);
        Sent($"cancel the {GameSession.KindWords(m.Kind)} mission on {m.Target}", m.Target);
    }

    // ================================================================== special forces at a system (own units only)

    void SpecialForcesAtSystem(string name)
    {
        var units = MySpecialForcesAt(name);
        if (units.Count == 0) return;
        Heading("Special forces");
        Line("Agents trained at a Training Facility. They can go on missions with or without characters.", Dim, 13);
        foreach (var o in units) Line($"{S.SpecialForceName(o.Id)} - {SpecialForceState(o)}. {SpecialForceSkills(o)}", SfReady(o) ? null : Dim, 13);
    }

    List<EconObject> MySpecialForcesAt(string name) =>
        S.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.SpecialForce && o.Side == Me && o.System == name && o.AboardShip == 0).OrderBy(o => o.Id).ToList();

    bool SfReady(EconObject o) => o.Completed && !Mm.IsSpecialForceBusy(o.Id) && S.PendingMissionOfSpecialForce(o.Id) == null;

    string SpecialForceState(EconObject o) =>
        !o.Completed ? "in training"
        : S.MissionOfSpecialForce(o.Id) is { } m ? $"on a {GameSession.KindWords(m.Kind)} mission"
        : S.PendingMissionOfSpecialForce(o.Id) is { } p ? $"leaves on a {GameSession.KindWords(p.Kind)} mission next day"
        : "ready";

    string SpecialForceSkills(EconObject o)
    {
        var k = S.Host.Troops.Data.SpecialForce(o.Class);
        if (k == null) return "";
        var kinds = OfferedKinds.Where(x => Mm.Data.TryGet(x, out var r) && r.AvailableTo(Me) && (k.MissionBit & r.SpecialForceMask) != 0).Select(GameSession.KindWords).ToList();
        return $"Espionage {k.Espionage}, combat {k.Combat}, leadership {k.Leadership}. Missions: {(kinds.Count == 0 ? "none" : string.Join(", ", kinds))}.";
    }

    // ================================================================== the planner

    /// <summary>Characters who can join a team now: the core's availability (free, placed, not aboard a fleet, no system post, not on a mission) and standing still. rule: m5-missions §Who can go.</summary>
    bool CanJoin(CharacterRecord c) => Cm.IsAvailable(c) && c.Transit == null && c.System != null && S.PendingMissionOf(c.Id) == null;

    bool CanSendFrom(string system) =>
        S.MyCharacters.Any(c => c.System == system && CanJoin(c)) || MySpecialForcesAt(system).Any(SfReady);

    void OpenPlanner(string home, int? character = null, int? unit = null)
    {
        _plan = new MissionPlan { Home = home };
        if (character is int c) _plan.Chars.Add(c);
        if (unit is int u) _plan.Sfs.Add(u);
        _charTab = false; _missionTab = false; RefreshCharButton(); RefreshMissionButton();
        _view.SelectedFleet = -1; _view.Select(-1);
        _lastSelectedSystem = -1; _lastSelectedFleet = -1;
        _panelDirty = true; _panelTimer = 0;
    }

    void ClosePlanner()
    {
        if (_pickMission) CancelMove();
        _plan = null;
        _panelDirty = true; _panelTimer = 0;
    }

    StartMissionOrder PlanOrder(MissionKind kind, string target, int? targetChar) =>
        new(0, Me, kind, _plan.Chars.ToList(), _plan.Sfs.ToList(), new List<int>(), targetChar == null ? target : null, targetChar);

    // Target-stage reasons: everything before the target passed, so the team can do this kind somewhere.
    static bool TargetStage(string reason) => reason is null or MissionReject.BadTarget or MissionReject.NoDeathStar or Rebellion.Core.Clock.RejectReason.Unreachable
        or Rebellion.Core.Clock.RejectReason.NoMatchingYard or Rebellion.Core.Clock.RejectReason.NothingToResearch;

    /// <summary>Why this team cannot do the kind (plain words, naming the member), or null when it can (the target is picked next).</summary>
    string KindRefusal(MissionKind k)
    {
        if (S.KindBlockedReason(k) is string blocked) return blocked;
        if (_plan.Chars.Count + _plan.Sfs.Count == 0) return "pick at least one team member first";
        var r = Mm.CheckStart(PlanOrder(k, null, null)).Reason;
        if (TargetStage(r)) return null;
        if (r == MissionReject.NotAbleToDo)
        {
            var who = _plan.Chars.Where(id => Cm.Get(id) is { } c && !Mm.CanDo(c, k)).Select(S.CharName).ToList();
            return $"{string.Join(", ", who)} {(who.Count == 1 ? "has" : "have")} no skill for it ({SkillFor(k)})";
        }
        if (r == MissionReject.NotEligibleSpecialForce)
        {
            var rec = Mm.Data.Get(k);
            var who = _plan.Sfs.Where(id => S.Host.Economy.Find(id) is { } o && (S.Host.Troops.Data.SpecialForce(o.Class) is not { } sf || (sf.MissionBit & rec.SpecialForceMask) == 0)).Select(S.SpecialForceName).ToList();
            return $"{string.Join(", ", who)} cannot do this kind of mission";
        }
        if (r == MissionReject.RuleMissing) return "not available yet: the rule book does not cover it";
        return GameSession.WhyMission(r);
    }

    static string SkillFor(MissionKind k) => k switch
    {
        MissionKind.Diplomacy => "needs diplomacy",
        MissionKind.Espionage or MissionKind.Reconnaissance => "needs espionage",
        MissionKind.Sabotage => "needs combat and espionage",
        MissionKind.Rescue or MissionKind.Abduction or MissionKind.Assassination => "needs combat",
        MissionKind.InciteUprising or MissionKind.SubdueUprising or MissionKind.Recruitment => "needs leadership",
        MissionKind.ShipResearch => "needs ship design",
        MissionKind.FacilityResearch => "needs facility design",
        MissionKind.TroopResearch => "needs troop training",
        _ => "needs a skill",
    };

    void MissionPlanner()
    {
        var p = _plan;
        string home = p.Home;
        Line($"Send a mission from {home}", null, 22);
        Line(MissionAdvice, Dim, 13);
        Add(new HSeparator());

        // ---- 1. team: own characters and special forces at the home system
        Heading("1. Team (all at " + home + ")");
        // drop members who are no longer here or free (a day may have passed)
        p.Chars.RemoveWhere(id => Cm.Get(id) is not { } c || c.System != home || !CanJoin(c));
        p.Sfs.RemoveWhere(id => S.Host.Economy.Find(id) is not { } o || o.System != home || !SfReady(o));
        var here = S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead && Cm.LocationOf(c) == home).ToList();
        foreach (var c in here)
        {
            string n = S.CharName(c.Id);
            string skills = $"diplomacy {Cm.EffectiveSkill(c, Skill.Diplomacy)}, espionage {Cm.EffectiveSkill(c, Skill.Espionage)}, combat {Cm.EffectiveSkill(c, Skill.Combat)}, leadership {Cm.EffectiveSkill(c, Skill.Leadership)}";
            if (CanJoin(c)) TeamBox($"{n}  ({skills})", p.Chars, c.Id);
            else Line($"{n} cannot go: {NotJoinReason(c)}", Dim, 13);
        }
        foreach (var o in MySpecialForcesAt(home))
        {
            if (SfReady(o)) TeamBox($"{S.SpecialForceName(o.Id)}  ({SpecialForceSkills(o)})", p.Sfs, o.Id);
            else Line($"{S.SpecialForceName(o.Id)} cannot go: {SpecialForceState(o)}", Dim, 13);
        }
        if (here.Count == 0 && MySpecialForcesAt(home).Count == 0) Line("None of your characters or special forces is here.", Dim);

        // ---- 2. kind: what the core allows this team; the rest greyed with a one-line reason
        Heading("2. Mission");
        foreach (var k in OfferedKinds)
        {
            if (!Mm.Data.TryGet(k, out var rec) || rec.Hidden || !rec.AvailableTo(Me)) continue; // other side's kinds are not shown
            var kk = k;
            string why = KindRefusal(k);
            bool chosen = p.Kind == k;
            var b = Act($"{(chosen ? "> " : "")}{GameSession.KindWords(k)}{(why != null ? " - " + why : "")}", () => ChooseKind(kk), GameSession.KindHelp(k), why != null);
            if (chosen) b.AddThemeColorOverride("font_color", Pending);
        }
        if (p.Kind is MissionKind kind && KindRefusal(kind) != null) { p.Kind = null; p.Target = null; p.TargetChar = null; }

        // ---- 3. target
        if (p.Kind is not MissionKind sel) { CloseRow(); return; }
        Heading("3. Target");
        Line(GameSession.KindHelp(sel), Dim, 13);
        if (sel == MissionKind.DeathStarSabotage) Line(S.EnemyDeathStarNote, Dim, 13); // rule: m5-death-star §5
        if (sel == MissionKind.Rescue)
        {
            var prisoners = S.MyCharacters.Where(c => c.Status == CharacterStatus.Captured && c.HeldAt != null).ToList();
            if (prisoners.Count == 0) Line("None of your characters is a prisoner.", Dim);
            foreach (var c in prisoners)
            {
                var cc = c;
                Act($"Rescue {S.CharName(c.Id)} (held at {c.HeldAt})", () => { p.TargetChar = cc.Id; p.Target = cc.HeldAt; _panelDirty = true; _panelTimer = 0; });
            }
        }
        else Act(_pickMission ? "Cancel target pick" : p.Target == null ? "Pick the target system..." : $"Change the target ({p.Target})...", () => { if (_pickMission) CancelMove(); else StartMissionPick(); },
            "Then click a system on the map. The route and the arrival day show before you choose.");

        if (p.Target != null)
        {
            var chk = Mm.CheckStart(PlanOrder(sel, p.Target, p.TargetChar));
            var rec = Mm.Data.Get(sel);
            if (chk.Reason != null) Line($"{p.Target}: {GameSession.WhyMission(chk.Reason)}.", Bad);
            else
            {
                Line(chk.Ticks == 0 ? $"Target: {chk.Target}. The team is already there and starts at once."
                    : $"Target: {chk.Target}. {chk.Ticks} days on the way, arrives day {chk.ArrivalDay}.", Good);
                Line($"First result between day {chk.ArrivalDay + rec.TimerMin} and day {chk.ArrivalDay + rec.TimerMin + rec.TimerSpread}{(rec.Repeats ? "; then it keeps working until you cancel it" : "")}." +
                     (rec.Foilable ? " Enemy units at the target may catch the team." : ""), Dim, 13);
            }
            Add(new Control { CustomMinimumSize = new Vector2(0, 4) });
            Act($"Send: {GameSession.KindWords(sel)} on {p.Target}", ConfirmMission, "Starts when the next day begins. The core judges the order then; a refusal comes back in Messages.", chk.Reason != null);
        }
        CloseRow();
    }

    void CloseRow()
    {
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Act("Close without sending", ClosePlanner, "Esc");
    }

    void TeamBox(string text, SortedSet<int> set, int id)
    {
        var cb = new CheckBox { Text = text, ButtonPressed = set.Contains(id), FocusMode = FocusModeEnum.None, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cb.AddThemeFontSizeOverride("font_size", 13);
        cb.Toggled += on => { if (on) set.Add(id); else set.Remove(id); _panelDirty = true; _panelTimer = 0; };
        Add(cb);
    }

    string NotJoinReason(CharacterRecord c)
    {
        if (c.Status != CharacterStatus.Free) return GameSession.StatusWords(c);
        if (S.MissionOf(c.Id) != null) return "already on a mission";
        if (S.PendingMissionOf(c.Id) != null) return "already leaves on a mission next day";
        if (c.AttachedFleet != 0) return "aboard a fleet (detach first)";
        if (S.SystemPostOf(c) != null) return "system General here (release the post first)";
        if (c.Transit != null) return "about to travel";
        return "busy";
    }

    void ChooseKind(MissionKind k)
    {
        if (_plan == null) return;
        if (_plan.Kind != k) { _plan.Kind = k; _plan.TargetChar = null; if (k == MissionKind.Rescue) _plan.Target = null; }
        _panelDirty = true; _panelTimer = 0;
    }

    void StartMissionPick()
    {
        if (_plan?.Kind == null) return;
        _pickMission = true; _moveFleet = -1; _moveHq = false; _moveChar = -1;
        _view.PickMode = true;
        _moveBanner = $"{GameSession.KindWords(_plan.Kind.Value)}: click the target system (Esc cancels)";
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void UpdateMissionPreview()
    {
        if (_plan?.Kind is not MissionKind k) { CancelMove(); return; }
        int h = _view.Hovered;
        string kw = GameSession.KindWords(k);
        if (h < 0) { _view.PathPreview = null; _moveBanner = $"{kw}: click the target system (Esc cancels)"; RefreshTop(); return; }
        string to = G.Systems[h].Name;
        var chk = Mm.CheckStart(PlanOrder(k, to, null));
        if (chk.Reason != null) { _view.PathPreview = null; _moveBanner = $"{kw} on {to}: {GameSession.WhyMission(chk.Reason)}."; }
        else
        {
            _view.PathPreview = chk.Path.Select(n => G.TryFindSystem(n, out int i) ? i : -1).Where(i => i >= 0).ToList();
            _view.PathColor = new Color(0.8f, 0.6f, 1f, 0.95f);
            _moveBanner = chk.Ticks == 0 ? $"{kw} on {to}: the team is already there. Click to choose." : $"{kw} on {to}: {chk.Ticks} days, arrives day {chk.ArrivalDay}. Click to choose.";
        }
        RefreshTop();
    }

    void OnMissionPicked(int idx)
    {
        if (_plan?.Kind is not MissionKind k) { CancelMove(); return; }
        string to = G.Systems[idx].Name;
        var chk = Mm.CheckStart(PlanOrder(k, to, null));
        if (chk.Reason != null) { Toast($"{GameSession.KindWords(k)} on {to}: {GameSession.WhyMission(chk.Reason)}."); return; }
        _plan.Target = to; _plan.TargetChar = null;
        CancelMove();
    }

    void ConfirmMission()
    {
        var p = _plan;
        if (p?.Kind is not MissionKind k || p.Target == null) return;
        var chk = Mm.CheckStart(PlanOrder(k, p.Target, p.TargetChar));
        if (chk.Reason != null) { Toast(GameSession.WhyMission(chk.Reason)); return; }
        S.SubmitMission(k, p.Chars.ToList(), p.Sfs.ToList(), chk.Target, p.TargetChar, p.Home, chk.ArrivalDay);
        string team = S.TeamWords(p.Chars, p.Sfs);
        _plan = null;
        _missionTab = true; RefreshMissionButton();
        Sent($"{GameSession.KindWords(k)} on {chk.Target} by {team}, arrives day {chk.ArrivalDay}", chk.Target);
    }
}
