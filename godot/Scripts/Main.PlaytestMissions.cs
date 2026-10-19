using System;
using System.Linq;
using Godot;
using Rebellion.Core.Characters;
using Rebellion.Core.Missions;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// Playtest of the mission screens (rule: m5-missions): the Missions list (button and key M), a kind refused with a plain reason, Diplomacy on a
/// neutral system with its outcome message within the expected days, Espionage with its result, and cancelling a mission. The Empire sends an
/// espionage mission. Every action is a click on the real button; the core is only read to choose where to click and to check the result.
/// </summary>
public partial class Main
{
    void PlaytestMissions(GameScreen gs)
    {
        var s = gs.S; var me = s.Player; var mm = s.Missions; var cm = s.Host.Characters; var fm = s.Host.Fleets;
        GD.Print("--- missions");
        gs.View.Select(-1); gs.PlayRefresh();

        // every rejection code of the mission orders has words
        var codes = typeof(MissionReject).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).ToList();
        Expect(codes.Count >= 16 && codes.All(c => GameSession.WhyMission(c) != c), $"every mission rejection code ({codes.Count}) has plain words");

        // ---------------------------------------------------------------- the Missions list: button and key M
        Expect(gs.Click("Missions") && gs.MissionsOpen, "click Missions in the top bar");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains("Your missions") && gs.PanelText().Contains("No mission under way") && gs.PanelText().Contains("Espionage"), "the Missions list opens, empty, with the advice line");
        gs.PressKey(Key.M); gs.PlayRefresh();
        Expect(!gs.MissionsOpen, "key M closes it");

        bool Free(CharacterRecord c) => cm.IsAvailable(c) && c.Transit == null && c.System != null;
        int Ticks(string a, string b) => fm.FindPath(a, b, 100) is { Ok: true } p ? p.TotalTicks : int.MaxValue;

        if (me == Side.Alliance)
        {
            // ---------------------------------------------------------------- Mon Mothma: a refused kind, then Diplomacy on a neutral system
            var mon = cm.Get(576);
            Expect(mon != null && Free(mon), "Mon Mothma is free to go on a mission");
            if (mon == null || !Free(mon)) return;
            string neutral = gs.G.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == null && s.Picture(n)?.Owner != null
                    && mm.CheckStart(new StartMissionOrder(0, me, MissionKind.Diplomacy, new() { mon.Id }, new(), new(), n)).Reason == null)
                .OrderBy(n => Ticks(mon.System, n)).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
            Expect(neutral != null, "a known neutral system is in reach for Diplomacy");
            if (neutral == null) return;
            GD.Print($"  Mon Mothma at {mon.System}: diplomacy on {neutral}");
            int dipId = SendThroughPlanner(gs, "Send Mon Mothma on a mission...", null, MissionKind.Diplomacy, neutral, checkRefusedKind: true);
            if (dipId < 0) return;
            var dip = mm.Get(dipId);
            var rec = mm.Data.Get(MissionKind.Diplomacy);
            if (dip.Phase == MissionPhase.Outbound)
            {
                if (!gs.MissionsOpen) gs.Click("Missions");
                gs.PlayRefresh();
                Expect(gs.PanelText().Contains($"Travelling out to {neutral}, arrives day {dip.ArrivalDay}"), "the Missions list shows the trip out and its arrival day: " + gs.PanelText().Replace("\n", " / "));
                Expect(gs.Has($"Cancel the Diplomacy mission on {neutral}") && !gs.Enabled($"Cancel the Diplomacy mission on {neutral}"), "Cancel is greyed out while the team travels");
                gs.Click("Close the Missions list");
            }
            Expect(OutcomeWithin(gs, "Diplomacy", neutral, dip.ArrivalDay + rec.TimerMin + rec.TimerSpread), $"an outcome message for Diplomacy on {neutral} within the expected days");

            // ---------------------------------------------------------------- cancel a working mission (the neutral one, or Diplomacy on an own system: no defenders there)
            var working = mm.Get(dipId) is { Phase: MissionPhase.Working } w ? w : null;
            if (working == null)
            {
                var leia = cm.Get(577);
                string own = leia != null && Free(leia) ? leia.System : null;
                if (own != null && s.OwnerOf(own) == me)
                {
                    int id2 = SendThroughPlanner(gs, "Send Leia Organa on a mission...", null, MissionKind.Diplomacy, own, checkRefusedKind: false);
                    if (id2 >= 0) { RunUntil(gs, () => mm.Get(id2)?.Phase == MissionPhase.Working, 30); working = mm.Get(id2); }
                }
            }
            Expect(working != null, "a mission is working at its target (to cancel it)");
            if (working != null) CancelThroughList(gs, working);

            // ---------------------------------------------------------------- Han Solo: Espionage from the system panel
            var han = cm.Get(579);
            Expect(han != null && Free(han), "Han Solo is free to go on a mission");
            if (han != null && Free(han))
            {
                string enemy = s.Game.State.SystemOwners.Where(kv => kv.Value == s.Enemy).Select(kv => kv.Key)
                    .Where(n => Ticks(han.System, n) < int.MaxValue).OrderBy(n => Ticks(han.System, n)).ThenBy(n => n, StringComparer.Ordinal).First();
                GD.Print($"  Han Solo at {han.System}: espionage on {enemy}");
                gs.SelectSystem(han.System); gs.PlayRefresh();
                Expect(gs.Has($"Send a mission from {han.System}..."), "the system panel offers Send a mission from...");
                SpyAndCheck(gs, $"Send a mission from {han.System}...", "Han Solo", enemy);
            }
            PlaytestSpecialForces(gs, "Bothan Spies");
        }
        else
        {
            // ---------------------------------------------------------------- Empire: Darth Vader (or any free spy) on Espionage
            var spy = s.MyCharacters.Where(c => Free(c) && mm.CanDo(c, MissionKind.Espionage)).OrderBy(c => c.Id == 641 ? 0 : 1).ThenByDescending(c => s.Chars.EffectiveSkill(c, Skill.Espionage)).FirstOrDefault();
            Expect(spy != null, "a free Imperial character can spy");
            if (spy == null) return;
            string name = s.CharName(spy.Id);
            string target = s.Game.State.SystemOwners.Where(kv => kv.Value == s.Enemy).Select(kv => kv.Key)
                .Concat(gs.G.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == null))
                .Where(n => n != spy.System && Ticks(spy.System, n) < int.MaxValue).OrderBy(n => Ticks(spy.System, n)).ThenBy(n => n, StringComparer.Ordinal).First();
            GD.Print($"  {name} at {spy.System}: espionage on {target}");
            gs.Click("Characters"); gs.PlayRefresh();
            Expect(gs.Has($"Send {name} on a mission..."), $"the Characters list offers Send {name} on a mission...");
            // the result is checked later (FinishMissions), so the rest of the playtest keeps its usual days
            SpyAndCheck(gs, $"Send {name} on a mission...", null, target, checkRefusedKind: true, defer: true);
        }
        gs.View.Select(-1); gs.PlayRefresh();
    }

    /// <summary>
    /// Special forces: build one at a Training Facility (Build, Troops), see it in the system panel, and pick it for a team in the planner: the
    /// kinds its mission bit allows are open, the others greyed with the unit named. rule: m5-missions §Special forces.
    /// </summary>
    void PlaytestSpecialForces(GameScreen gs, string cls)
    {
        var s = gs.S; var me = s.Player; var eco = s.Host.Economy;
        string yard = eco.Objects.Where(o => o.Category == Rebellion.Core.Economy.ObjectCategory.Facility && o.Completed && o.System != null && o.Class.Contains("Training") && s.OwnerOf(o.System) == me)
            .Select(o => o.System).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        Expect(yard != null, "you hold a system with a Training Facility");
        if (yard == null) return;
        gs.SelectSystem(yard); gs.PlayRefresh();
        if (!gs.Has(cls)) { gs.Click(">  Troops"); gs.PlayRefresh(); }
        Expect(gs.Click(cls), $"click {cls} in the Troops build list at {yard}");
        gs.PlayRefresh();
        if (gs.Has("v  Troops")) gs.Click("v  Troops"); // leave the build list folded as it was
        bool Ready() => eco.Objects.Any(o => o.Category == Rebellion.Core.Economy.ObjectCategory.SpecialForce && o.Side == me && o.Class == cls && o.Completed && o.System == yard);
        Expect(RunUntil(gs, Ready, 120), $"{cls} trained at {yard} (day {s.Game.Day})");
        if (!Ready()) return;
        var unit = eco.Objects.First(o => o.Category == Rebellion.Core.Economy.ObjectCategory.SpecialForce && o.Side == me && o.Class == cls && o.Completed && o.System == yard);
        string uname = s.SpecialForceName(unit.Id);
        gs.SelectSystem(yard); gs.PlayRefresh();
        Expect(gs.PanelText().Contains("Special forces") && gs.PanelText().Contains($"{uname} - ready"), $"the system panel lists {uname} under Special forces");
        Expect(gs.Click($"Send a mission from {yard}..."), "open the planner from the system panel");
        gs.PlayRefresh();
        Expect(gs.SetTeam(uname, true), $"tick {uname} in the team");
        gs.PlayRefresh();
        var texts = gs.ButtonTexts().ToList();
        Expect(gs.Enabled("Espionage") && texts.Any(t => t.StartsWith("Diplomacy - ", StringComparison.Ordinal) && t.Contains(uname) && t.Contains("cannot do")),
            $"{uname} can spy, and Diplomacy is greyed with the unit named: " + string.Join(" | ", texts.Where(t => t.StartsWith("Espionage") || t.StartsWith("Diplomacy"))));
        Expect(gs.Click("Close without sending") && !gs.PlannerOpen, "close the planner");
        gs.View.Select(-1); gs.PlayRefresh();
    }

    /// <summary>Espionage through the planner, then its result in Messages within the expected days (and the facilities shown on success).</summary>
    void SpyAndCheck(GameScreen gs, string open, string tick, string target, bool checkRefusedKind = false, bool defer = false)
    {
        var s = gs.S; var mm = s.Missions;
        int id = SendThroughPlanner(gs, open, tick, MissionKind.Espionage, target, checkRefusedKind);
        if (id < 0) return;
        var m = mm.Get(id); var rec = mm.Data.Get(MissionKind.Espionage);
        int lastDay = m.ArrivalDay + rec.TimerMin + rec.TimerSpread, from = s.Messages.Count;
        if (defer) { _missionFinish = () => FinishSpy(gs, id, target, lastDay, from); return; }
        Expect(OutcomeWithin(gs, "Espionage", target, lastDay), $"an Espionage result on {target} within the expected days");
        AfterSpy(gs, id, target);
    }

    Action _missionFinish;

    /// <summary>The deferred checks of PlaytestMissions (run later in the playtest).</summary>
    void FinishMissions() { var f = _missionFinish; _missionFinish = null; f?.Invoke(); }

    void FinishSpy(GameScreen gs, int id, string target, int lastDay, int from)
    {
        var s = gs.S;
        if (s.Game.Day < lastDay) { Expect(OutcomeWithin(gs, "Espionage", target, lastDay), $"an Espionage result on {target} within the expected days"); }
        else
        {
            var got = s.Messages.Skip(Math.Min(from, s.Messages.Count)).FirstOrDefault(m => m.Text.StartsWith($"Espionage on {target}", StringComparison.Ordinal));
            GD.Print($"  espionage outcome: {got?.Text ?? "none"} (day {got?.Day}, last expected {lastDay})");
            Expect(got != null && got.Day <= lastDay, $"an Espionage result on {target} within the expected days");
        }
        AfterSpy(gs, id, target);
    }

    void AfterSpy(GameScreen gs, int id, string target)
    {
        var s = gs.S; var mm = s.Missions;
        if (s.Messages.Any(x => x.Text.StartsWith($"Espionage on {target} succeeded", StringComparison.Ordinal)))
        {
            gs.SelectSystem(target); gs.PlayRefresh();
            // the scripted invasion can take the spied system before the result comes back; then the panel is the live view of our own system
            bool ours = s.OwnerOf(target) == s.Player;
            Expect(ours ? gs.PanelText().Contains("(you)") : gs.PanelText().Contains("(seen day"),
                "after the success the system panel shows what was seen (or the live view if we took it meanwhile): " + gs.PanelText().Replace("\n", " / "));
        }
        RunUntil(gs, () => mm.Get(id) == null, 120);
        Expect(mm.Get(id) == null && s.Messages.Any(x => x.Text.Contains("back at") && x.Text.Contains("free for new orders")), "the team came home (message)");
    }

    /// <summary>
    /// Open the planner with a button, optionally tick one more member, check a refused kind's reason, pick the kind and the target (hover shows
    /// route and arrival day), send, and run one day. Returns the mission id, or -1.
    /// </summary>
    int SendThroughPlanner(GameScreen gs, string open, string tick, MissionKind kind, string target, bool checkRefusedKind)
    {
        var s = gs.S; var mm = s.Missions;
        string kw = GameSession.KindWords(kind);
        if (!gs.Has(open)) { gs.Click("Characters"); gs.PlayRefresh(); }
        Expect(gs.Click(open) && gs.PlannerOpen, $"click '{open}'");
        gs.PlayRefresh();
        if (tick != null) { Expect(gs.SetTeam(tick, true), $"tick {tick} in the team"); gs.PlayRefresh(); }
        var texts = gs.ButtonTexts().ToList();
        if (checkRefusedKind)
        {
            // Recruitment runs since 2026-10-18 (m5-missions §6, the recruit pick); Jedi training is still refused by the rule book
            string blocked = texts.FirstOrDefault(t => t.StartsWith("Jedi training - ", StringComparison.Ordinal));
            Expect(blocked != null && blocked.Contains("rule book") && !gs.Enabled("Jedi training"), "Jedi training is greyed out with a plain reason: " + blocked);
            Expect(!texts.Any(t => t.StartsWith(s.Player == Side.Alliance ? "Assassination" : "Death Star", StringComparison.Ordinal)), "the other side's kind is not offered");
        }
        Expect(gs.Enabled(kw), $"{kw} is allowed for this team");
        Expect(gs.Click(kw), $"choose {kw}");
        gs.PlayRefresh();
        Expect(gs.Click("Pick the target system..."), "click Pick the target system...");
        var (team, units) = gs.PlanTeam;
        var chk = mm.CheckStart(new StartMissionOrder(0, s.Player, kind, team, units, new(), target));
        gs.HoverTextFor(target);
        Expect(gs.Banner.Contains($"{kw} on {target}") && (gs.Banner.Contains("arrives day") || gs.Banner.Contains("already there")), "the target preview shows the route and arrival day: " + gs.Banner);
        gs.PickSystem(target);
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"Target: {target}"), "the planner shows the chosen target: " + gs.PanelText().Replace("\n", " / "));
        Expect(gs.Click($"Send: {kw} on {target}"), "click Send");
        gs.PlayRefresh();
        Expect(!gs.PlannerOpen && gs.MissionsOpen && gs.PanelText().Contains($"{kw} on {target}") && gs.PanelText().Contains("Waiting for the next day"), "the Missions list shows the waiting mission");
        int before = s.Messages.Count;
        gs.PlayDay();
        var news = s.Messages.Skip(before).Select(x => x.Text).ToList();
        var started = s.MyMissions.LastOrDefault(x => x.Kind == kind && x.Target == target);
        Expect(started != null, $"the core started the {kw} mission: " + string.Join(" | ", news));
        if (started == null) return -1;
        Expect(news.Any(t => t.StartsWith($"Mission started: {kw} on {target}", StringComparison.Ordinal) && (t.Contains($"arrives day {started.ArrivalDay}") || t.Contains("already there"))),
            "message: mission started with its arrival day");
        if (chk.Reason == null) Expect(chk.ArrivalDay == started.ArrivalDay, $"the announced arrival day {chk.ArrivalDay} is the core's");
        return started.Id;
    }

    /// <summary>Runs days until a message about the mission's outcome ("KIND on TARGET ...") appears; true if it did by the last expected day.</summary>
    bool OutcomeWithin(GameScreen gs, string kind, string target, int lastDay)
    {
        var s = gs.S;
        int before = s.Messages.Count;
        bool Got() => s.Messages.Skip(before).Any(m => m.Text.StartsWith($"{kind} on {target}", StringComparison.Ordinal));
        RunUntil(gs, Got, Math.Max(1, lastDay - s.Game.Day + 1));
        var got = s.Messages.Skip(before).FirstOrDefault(m => m.Text.StartsWith($"{kind} on {target}", StringComparison.Ordinal));
        GD.Print($"  outcome (day {s.Game.Day}, last expected {lastDay}): {got?.Text ?? "none"}; also: " + string.Join(" | ", s.Messages.Skip(before).Where(m => m != got && (m.Text.Contains(target) || m.Text.Contains("agents"))).Select(m => m.Text).Take(6)));
        return got != null && got.Day <= lastDay;
    }

    void CancelThroughList(GameScreen gs, MissionGroup m)
    {
        var s = gs.S; var mm = s.Missions;
        string kw = GameSession.KindWords(m.Kind), target = m.Target; int id = m.Id;
        gs.View.Select(-1);
        if (!gs.MissionsOpen) gs.Click("Missions");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains($"At {target}, next result due day {m.Due}"), "the Missions list shows the team at the target and the next result day");
        Expect(gs.Click($"Cancel the {kw} mission on {target}"), "click Cancel");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains("Waiting for the next day: cancel this mission"), "the list shows the waiting cancel");
        int before = s.Messages.Count;
        gs.PlayDay();
        Expect(s.Messages.Skip(before).Any(x => x.Text.StartsWith($"{kw} on {target} cancelled", StringComparison.Ordinal)), "message: the mission was cancelled: " + string.Join(" | ", s.Messages.Skip(before).Select(x => x.Text)));
        Expect(RunUntil(gs, () => mm.Get(id) == null, 120), "the team came home");
        Expect(s.Messages.Any(x => x.Text.Contains("free for new orders")), "message: the team is back");
        gs.Click("Close the Missions list"); gs.PlayRefresh();
    }
}
