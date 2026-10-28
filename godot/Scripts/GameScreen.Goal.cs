using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The goal (user story G3: "how close am I to winning, and how close is the enemy?"). The top bar's Goal button says it in a few words;
/// the Goal window lists each win condition with a tick or a cross. The conditions are the core's (rule: m5-setup-and-victory §Victory);
/// what is shown about the enemy is only what the player's side knows (rule: m5-fog-and-hq §N8): the enemy's systems are counted from the
/// picture, enemy leaders are placed where they were last seen, and the race to win by control is told to both sides (DominanceChanged).
/// </summary>
public partial class GameScreen
{
    Praedra.Core.Victory.VictoryModule Vm => S.Host.Victory;

    /// <summary>The key leaders the player must hold prisoner (k.Side is the side that needs them captive).</summary>
    IEnumerable<int> MyTerms => Vm.Keys.Where(k => k.Standard && k.Side == Me).Select(k => k.Character).OrderBy(x => x);
    /// <summary>The player's own key leaders the enemy must hold.</summary>
    IEnumerable<int> EnemyTerms => Vm.Keys.Where(k => k.Standard && k.Side == S.Enemy).Select(k => k.Character).OrderBy(x => x);

    bool InMyPrison(int id) => Cm.Get(id) is { Status: CharacterStatus.Captured } c && c.Captor == Me;
    bool KeyDead(int id) => Cm.Get(id) is { Status: CharacterStatus.Dead };
    bool KeyMet(int id) => InMyPrison(id) || (KeyDead(id) && Vm.Rules.KeyDeathFallback == Praedra.Core.Victory.KeyDeathFallback.WaiveCondition);

    /// <summary>Where the player last saw an enemy leader: (system, day), or null.</summary>
    (string System, int Day)? LastSeen(int id) =>
        S.Fog.CharactersSeen(Me).Where(x => x.Character.Id == id).OrderByDescending(x => x.Stamp).ThenBy(x => x.System, StringComparer.Ordinal)
            .Select(x => ((string, int)?)(x.System, x.Stamp)).FirstOrDefault();

    void RefreshGoalButton()
    {
        if (_goalButton == null) return;
        string text; string variation = null; string tip;
        if (Vm.DominanceDueDay is int due && Vm.DominanceLeader is Side lead)
        {
            int left = Math.Max(0, due - S.Game.Day);
            if (lead == Me) { text = $"You win in {Ui.Count(left, "day", "days")}"; variation = "Primary"; tip = $"Hold {Vm.Rules.DominanceSharePercent}% of the systems the two sides hold until day {due} and you win."; }
            else { text = $"Enemy wins in {Ui.Count(left, "day", "days")}"; variation = "DangerButton"; tip = $"The {S.NameOf(lead)} holds {Vm.Rules.DominanceSharePercent}% of the systems after your capital fell. Take systems back before day {due}."; }
        }
        else
        {
            var terms = MyTerms.ToList();
            int met = terms.Count(KeyMet);
            text = Vm.Rules.StandardVictory && terms.Count > 0 ? $"Goal: {met}/{terms.Count} leaders held" : "Goal";
            tip = text + ". How you win, and how close each side is (G).";
        }
        // the clock card keeps it short (GOAL 0/2, WIN IN 12D); the full words are the tooltip's and the playtest's
        _goalButton.SetMeta(UiTest.FullName, text);
        _goalButton.Text = Vm.DominanceDueDay is int d2 && Vm.DominanceLeader is Side l2 ? $"{(l2 == Me ? "WIN" : "LOSE")} IN {Math.Max(0, d2 - S.Game.Day)}D"
            : text.StartsWith("Goal: ", StringComparison.Ordinal) ? "GOAL " + text[6..].Split(' ')[0] : "GOAL";
        _goalButton.ThemeTypeVariation = variation ?? "Chip";
        _goalButton.TooltipText = Ui.Wrap(tip);
    }

    void ShowGoal()
    {
        var v = ModalBox(_goalPanel);
        bool hand = Me == Side.Hand;
        var head = Ui.HBox(10);
        head.AddChild(Ui.Label("How you win", "Title", Ui.Gold, 28));
        head.AddChild(Ui.Grow());
        head.AddChild(Ui.Tag(hand ? "YOU ARE THE HAND" : "YOU ARE THE COLLECTIVE", Ui.You));
        v.AddChild(head);

        // ---- the standard win: the enemy capital AND its key leaders
        var c1 = GoalCard(v, "Win: take their capital and hold their leaders", "All of these at once.");
        if (hand)
        {
            var hq = S.Fog.KnownHq(Me);
            // HqDestroyed is told to both sides (rule: m5-fog-and-hq §F7), so the Hand may know the Hearth is gone
            var truth = S.Game.State.Scenario.Hq;
            bool gone = truth.Status == Praedra.Core.Fog.HqStatus.Destroyed;
            Check(c1, gone, "Take the Hearth, the Collective headquarters",
                gone ? $"Done: it fell at {truth.System}."
                : hq == null ? "Where it is: unknown. Explore Collective systems and send spies to find it."
                : $"Last seen at {hq.System}, day {hq.Stamp}. It can move: look again before you strike.", gone ? truth.System : hq?.System);
        }
        else
        {
            var (known, owner) = S.KnownOwnerOf(GameBoot.HandCapital);
            bool held = known && owner == Me;
            Check(c1, held, "Take Charter, the Hand capital",
                held ? "You hold it." : "Charter is heavily defended: bring a large fleet and many regiments.", GameBoot.HandCapital);
        }
        foreach (int id in MyTerms)
        {
            string name = S.CharName(id);
            string how = InMyPrison(id) ? $"In your prison{(Cm.Get(id)?.HeldAt is string at ? $" at {at}" : "")}. Keep them there."
                : KeyDead(id) ? "Dead: no longer needed."
                : LastSeen(id) is { } ls ? $"Free. Last seen at {ls.System}, day {ls.Day}. Capture them in a battle where they stand, or with an Abduction mission."
                : "Free, whereabouts unknown. Capture them in a battle where they stand, or with an Abduction mission.";
            Check(c1, KeyMet(id), $"Hold {name} prisoner", how, LastSeen(id)?.System);
        }

        // ---- capital and control
        int mine = S.Game.State.SystemOwners.Count(kv => kv.Value == Me);
        int theirs = 0, unknown = 0;
        foreach (var sys in G.Systems)
        {
            var (known, owner) = S.KnownOwnerOf(sys.Name);
            if (!known) unknown++;
            else if (owner == S.Enemy) theirs++;
        }
        int pct = Vm.Rules.DominanceSharePercent;
        var c2 = GoalCard(v, "Or: hold the galaxy", $"Once the enemy capital has fallen, hold {pct}% of the systems the two sides hold, for {Vm.Rules.DominanceDays} days.");
        if (pct > 0)
        {
            // the run counts only once the enemy capital is gone (rule: m5-setup-and-victory §Victory, capital and control)
            bool capitalGone = hand ? S.Game.State.Scenario.Hq.Status == Praedra.Core.Fog.HqStatus.Destroyed : S.KnownOwnerOf(GameBoot.HandCapital) is (true, var co) && co != S.Enemy;
            c2.AddChild(Ui.Row("Systems you hold", $"{mine} of {G.Systems.Count}"));
            c2.AddChild(Ui.Row("Enemy systems you know of", theirs.ToString(), null, $"Only what your side has seen: {unknown} systems you have never explored or whose owner you do not know."));
            if (Vm.DominanceDueDay is int due && Vm.DominanceLeader is Side lead)
                c2.AddChild(Ui.Label(lead == Me ? $"Running: you win on day {due} ({Ui.InDays(due - S.Game.Day)}) if you keep it."
                    : $"DANGER: the {S.NameOf(lead)} wins on day {due} ({Ui.InDays(due - S.Game.Day)}). Take systems back to break its hold.", "Strong", lead == Me ? Ui.Good : Ui.Danger));
            else if (!capitalGone)
                c2.AddChild(Ui.Label(hand ? "Not running yet: the Hearth still stands." : "Not running yet: the Hand still holds Charter.", "Hint"));
            else
            {
                int share = mine + theirs == 0 ? 0 : mine * 100 / (mine + theirs);
                c2.AddChild(Ui.Bar(share / 100f, Ui.You, 8, new Color(Ui.Enemy, 0.55f)));
                c2.AddChild(Ui.Label($"Your share by what you know: about {share}% (need {pct}%). The count that decides is the true one, so take more than you need.", "Hint"));
            }
        }
        else c2.AddChild(Ui.Label("Off in this game.", "Hint"));
        c2.AddChild(Ui.Label("Either side also wins at once when the other holds no system.", "Hint"));

        // ---- what the enemy needs from you
        var c3 = GoalCard(v, "What the enemy needs", hand ? "The Collective wins by taking Charter and holding these two prisoner." : "The Hand wins by taking the Hearth and holding these two prisoner.");
        if (hand)
        {
            var (known, owner) = S.KnownOwnerOf(GameBoot.HandCapital);
            Check(c3, !(known && owner == Me), "Charter", known && owner == Me ? "Still yours." : "Lost!", GameBoot.HandCapital, danger: true);
        }
        else
        {
            var h = S.OwnHq;
            Check(c3, h == null, "The Hearth", h == null ? "Lost!" : h.Status == Praedra.Core.Fog.HqStatus.InTransit ? HqMovingText(h) + "." : $"Safe at {h.System}.", h?.System, danger: true);
        }
        foreach (int id in EnemyTerms)
        {
            var c = Cm.Get(id);
            bool caught = c is { Status: CharacterStatus.Captured };
            string how = caught ? $"Prisoner{(c.HeldAt != null ? $" at {c.HeldAt}" : "")}. Send a Rescue mission." : c is { Status: CharacterStatus.Dead } ? "Dead: no longer counts." : $"Free, {S.WhereIsCharacter(c)}.";
            Check(c3, caught, S.CharName(id), how, caught ? c.HeldAt : S.MapPlaceOf(c), danger: true);
        }

        var close = Ui.Button("Close (Esc)", () => HideModal(_goalPanel), null, "Primary");
        close.CustomMinimumSize = new Vector2(0, 40);
        v.AddChild(close);
        ShowModal(_goalPanel);
    }

    VBoxContainer GoalCard(VBoxContainer into, string title, string sub)
    {
        var card = Ui.Card(Ui.Panel, Ui.Line, 14, 8);
        var cv = Ui.VBox(6);
        card.AddChild(cv);
        cv.AddChild(Ui.Label(title, "Subtitle"));
        if (sub != null) cv.AddChild(Ui.Label(sub, "Hint"));
        into.AddChild(card);
        return cv;
    }

    /// <summary>One condition: a tick (done) or an empty ring, the condition, how it stands, and a link to the place.</summary>
    void Check(VBoxContainer into, bool done, string what, string how, string system, bool danger = false)
    {
        var h = Ui.HBox(10);
        var color = danger ? (done ? Ui.Danger : Ui.Good) : done ? Ui.Good : Ui.Text3;
        h.AddChild(new Glyph(done ? (danger ? Glyph.Kind.Cross : Glyph.Kind.DoubleRing) : Glyph.Kind.Ring, color, 14));
        var tv = Ui.VBox(1);
        tv.AddChild(Ui.Label(what, "Strong", danger && done ? Ui.Danger : null));
        tv.AddChild(Ui.Label(how, null, Ui.Text2, Ui.Small));
        h.AddChild(tv);
        if (system != null)
        {
            var sys = system;
            var go = Ui.Button("Show", () => { HideModal(_goalPanel); GoTo(sys, true); }, $"Centre the map on {sys}", "Link");
            go.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(go);
        }
        into.AddChild(h);
    }
}
