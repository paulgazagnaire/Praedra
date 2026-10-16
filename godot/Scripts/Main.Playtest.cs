using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>
/// Headless playtest (<c>--playtest</c>): plays whole games through the real screens. Every action is a click on the real button (the same
/// Pressed signal a mouse raises) or a call of the handler the map calls; the core is only read to decide where to click.
/// </summary>
public partial class Main
{
    int _fails;
    int _checks;

    void Expect(bool ok, string what)
    {
        _checks++;
        if (ok) GD.Print("  ok: " + what);
        else { _fails++; GD.PrintErr("  PLAYTEST FAIL: " + what); }
    }

    static void Forget(string userPath)
    {
        string abs = ProjectSettings.GlobalizePath(userPath);
        if (System.IO.File.Exists(abs)) System.IO.File.Delete(abs);
    }

    void Playtest()
    {
        GameBoot.SaveSlot = "user://playtest_save1.json";
        GameBoot.AutoSaveSlot = "user://playtest_autosave.json";
        GameBoot.HowToFlag = "user://playtest_howto.txt";
        foreach (var side in new[] { Side.Alliance, Side.Empire })
        {
            Forget(GameBoot.SaveSlot); Forget(GameBoot.AutoSaveSlot); Forget(GameBoot.HowToFlag);
            try { PlaytestSide(side); }
            catch (Exception e) { _fails++; GD.PrintErr($"PLAYTEST CRASH ({side}): {e}"); }
        }
        Forget(GameBoot.SaveSlot); Forget(GameBoot.AutoSaveSlot); Forget(GameBoot.HowToFlag);
        GD.Print(_fails == 0 ? $"PLAYTEST PASSED ({_checks} checks)" : $"PLAYTEST FAILED: {_fails} of {_checks} checks");
        GetTree().Quit(_fails == 0 ? 0 : 1);
    }

    GameScreen Gs => _screen as GameScreen;

    /// <summary>Runs days (as the Play button would) until the condition holds; false if it never did.</summary>
    bool RunUntil(GameScreen gs, Func<bool> done, int maxDays)
    {
        for (int i = 0; i < maxDays && !done(); i++)
        {
            if (gs.S.Winner != null) break;
            gs.PlayDay();
        }
        return done();
    }

    static Fleet FleetNamed(GameSession s, string name) => s.Game.State.Fleets.Values.FirstOrDefault(f => f.Name == name && f.Side == s.Player);

    void SelectFleetByList(GameScreen gs, Fleet f)
    {
        gs.PlayRefresh();
        Expect(gs.Click(f.Name), $"click '{f.Name}' in the Your fleets list");
        gs.PlayRefresh();
    }

    void PlaytestSide(Side side)
    {
        GD.Print($"=== PLAYTEST as {side} ===");
        // ---------------------------------------------------------------- start screen -> new game
        ShowMenu();
        var menu = _screen as StartMenu;
        Expect(menu != null, "start screen shows");
        Expect(UiTest.Click(menu, side.ToString()), $"pick {side}");
        Expect(UiTest.Click(menu, "Medium"), "pick Medium");
        // Space battle rules: Classic is the default; the Empire run picks New (m4-battle-new ruleset switch).
        var wantRules = side == Side.Empire ? Rebellion.Core.Battle.BattleRuleset.New : Rebellion.Core.Battle.BattleRuleset.Classic;
        if (wantRules == Rebellion.Core.Battle.BattleRuleset.New) Expect(UiTest.Click(menu, "New (weapons matter)"), "pick the New battle rules");
        Expect(UiTest.Click(menu, "New Game"), "click New Game");
        var gs = Gs;
        Expect(gs != null, "the game screen opened");
        if (gs == null) return;
        var s = gs.S;
        var me = s.Player;
        Expect(me == side, "you play " + side);
        Expect(s.Rules == wantRules && s.Host.Battles.Params.Ruleset == wantRules, "the chosen battle rules reached the core: " + s.Rules);

        // ---------------------------------------------------------------- how to play, speed, play button
        Expect(gs.HowToVisible, "How to play shows at the first new game");
        Expect(gs.Click("Got it"), "close the How to play overlay");
        Expect(!gs.HowToVisible && !GameBoot.HowToUnseen(), "How to play closed and remembered");
        Expect(gs.Click("How to play"), "How to play button opens it again");
        Expect(gs.HowToVisible, "overlay visible");
        gs.Click("Got it");
        Expect(gs.SpeedValue == 5, "default speed is 5x");
        Expect(gs.TopTips.All(t => !string.IsNullOrEmpty(t)), "top bar numbers have tooltips");
        Expect(gs.Banner.Contains("Play"), "banner tells a new player to press Play");
        Expect(gs.Click("Play"), "press Play");
        gs.Frame(1.0);
        Expect(s.Game.Day == 5, $"one second at 5x = 5 days (day {s.Game.Day})");
        Expect(gs.Click("Pause"), "press Pause");
        Expect(!gs.Playing, "paused");
        Expect(gs.Click("2x") && gs.SpeedValue == 2 && gs.Click("5x") && gs.SpeedValue == 5, "speed buttons work");

        PlaytestFog(gs);
        if (side == Side.Empire) PlaytestEmpireHqUnknown(gs);
        else PlaytestMoveHq(gs);
        PlaytestCharacters(gs);
        PlaytestMissions(gs);

        // ---------------------------------------------------------------- select the troop fleet, move it to where the regiments are
        var eco = s.Host.Economy; var fm = s.Host.Fleets;
        var troopFleet = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && fm.FreeTroopSlots(f) > 0).OrderByDescending(f => fm.FreeTroopSlots(f)).First();
        GD.Print($"troop fleet: {troopFleet.Name} at {troopFleet.At}, room {fm.FreeTroopSlots(troopFleet)}");
        SelectFleetByList(gs, troopFleet);
        Expect(gs.Has("Move..."), "fleet panel has Move...");

        string regSys = eco.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == me && o.AboardShip == 0 && o.System != null)
            .GroupBy(o => o.System).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).Where(sys => sys == troopFleet.At || fm.FindPath(troopFleet.At!, sys, fm.SpeedOf(troopFleet)).Ok)
            .OrderBy(sys => sys == troopFleet.At ? 0 : fm.FindPath(troopFleet.At!, sys, fm.SpeedOf(troopFleet)).TotalTicks).First();
        GD.Print($"regiments at {regSys}");
        if (regSys != troopFleet.At)
        {
            Expect(gs.Click("Move..."), "click Move...");
            gs.PickSystem(regSys);
            Expect(s.QueuedOrders == 1 && gs.TopText.Contains("1 order waiting"), "top bar shows the waiting order: " + gs.TopText);
            gs.PlayRefresh();
            Expect(gs.FleetListTexts().Any(t => t.Contains(troopFleet.Name) && t.Contains("moving to " + regSys)), "fleet list says 'moving to " + regSys + ", arrives day N'");
            Expect(gs.PanelText().Contains("Waiting for the next day") || gs.PanelText().Contains("Will leave"), "fleet panel shows the pending move");
            Expect(s.PlannedMoves.Any(), "planned route recorded for drawing");
            gs.PlayDay();
            Expect(troopFleet.Transit != null || troopFleet.At == regSys, "fleet is under way after the day starts");
            Expect(!s.PlannedMoves.Any(), "plan cleared once the move started");
            gs.PlayRefresh();
            Expect(gs.FleetListTexts().Any(t => t.Contains("moving to " + regSys)), "fleet list still shows the trip while under way");
            Expect(RunUntil(gs, () => troopFleet.At == regSys, 400), $"fleet reached {regSys} (day {s.Game.Day})");
        }

        // ---------------------------------------------------------------- load regiments
        SelectFleetByList(gs, troopFleet);
        Expect(gs.Enabled("Load troops:") || gs.Enabled("Load every"), "a Load button is available at " + regSys);
        if (!gs.Click("Load troops:")) gs.Click("Load every");
        Expect(s.QueuedOrders >= 1, "load order queued");
        gs.PlayDay();
        var aboard = s.Host.Troops.RegimentsAboard(troopFleet);
        Expect(aboard.Count > 0, $"regiments are aboard ({aboard.Count})");
        SelectFleetByList(gs, troopFleet);
        Expect(gs.PanelText().Contains("Regiments aboard: ") && !gs.PanelText().Contains("Regiments aboard: none"), "panel lists the regiments aboard");

        // unload and load again (the Unload button)
        SelectFleetByList(gs, troopFleet);
        Expect(gs.Click("Unload all troops"), "click Unload all troops");
        gs.PlayDay();
        Expect(s.Host.Troops.RegimentsAboard(troopFleet).Count == 0, "regiments are back on the planet");
        SelectFleetByList(gs, troopFleet);
        if (!gs.Click("Load troops:")) gs.Click("Load every");
        gs.PlayDay();
        Expect(s.Host.Troops.RegimentsAboard(troopFleet).Count > 0, "loaded again");

        // ---------------------------------------------------------------- go to the nearest enemy/neutral system with no enemy fleet; bombard; invade
        string target = s.Game.State.SystemOwners.Where(kv => kv.Value != me).Select(kv => kv.Key)
            .Concat(gs.G.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == null))
            .Distinct().OrderBy(n => n, StringComparer.Ordinal)
            .Where(n => n != troopFleet.At && !s.Game.State.Fleets.Values.Any(f => f.Side != me && f.At == n) && fm.FindPath(troopFleet.At!, n, fm.SpeedOf(troopFleet)).Ok)
            .OrderBy(n => fm.FindPath(troopFleet.At!, n, fm.SpeedOf(troopFleet)).TotalTicks).First();
        var tprev = s.OwnerOf(target);
        GD.Print($"invasion target {target} (owner {(tprev?.ToString() ?? "neutral")})");
        SelectFleetByList(gs, troopFleet);
        Expect(gs.Click("Move..."), "click Move... for the target");
        gs.PickSystem(target);
        gs.PlayDay();
        Expect(RunUntil(gs, () => troopFleet.At == target, 400), $"fleet reached {target} (day {s.Game.Day})");
        SelectFleetByList(gs, troopFleet);
        Expect(gs.Has("Bombard: military"), "Bombard buttons show at an enemy/neutral system");
        Expect(gs.Click("Bombard: military"), "click Bombard");
        gs.PlayRefresh();
        Expect(gs.PanelText().Contains("Waiting for the next day"), "pending bombard shown on the fleet");
        Expect(!gs.Enabled("Invade"), "Invade is greyed out while a bombard order is waiting (the core allows one attack order per fleet)");
        gs.PlayDay();
        Expect(s.Messages.Any(m => m.Text.Contains("ombard") && m.Text.Contains(target)), "bombardment happened or was explained");
        SelectFleetByList(gs, troopFleet);
        Expect(gs.Enabled("Invade"), "Invade is enabled with regiments aboard");
        gs.Click("Invade");
        int msgBefore = s.Messages.Count;
        for (int i = 0; i < 8; i++) { gs.PlayDay(); if (s.Messages.Skip(msgBefore).Any(m => m.Text.StartsWith("Invasion of", StringComparison.Ordinal))) break; }
        GD.Print("  messages since the invade click: " + string.Join(" | ", s.Messages.Skip(msgBefore).Select(m => $"d{m.Day} {m.Text}")));
        Expect(s.Messages.Skip(msgBefore).Any(m => m.Text.Contains("nvasion of " + target) || m.Text.Contains("nvade") || m.Text.Contains("Order refused")), "invasion produced a result or a refusal message");
        var inv = s.Messages.Skip(msgBefore).FirstOrDefault(m => m.Title == "Invasion report");
        if (inv != null)
        {
            Expect(gs.ClickLog(m => m.Title == "Invasion report"), "click the invasion line in the log");
            Expect(gs.ReportVisible && gs.ReportText.Contains("Invasion"), "invasion report popup opens");
            gs.Click("Close");
            Expect(!gs.ReportVisible, "report closes");
        }
        else GD.Print("  note: no invasion report (invasion refused or not yet resolved): " + string.Join(" | ", s.Messages.Skip(msgBefore).Select(m => m.Text)));

        // ---------------------------------------------------------------- retreat switch, split and merge
        var splitFleet = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Ships.Count > 1).OrderBy(f => f.Id).FirstOrDefault();
        if (splitFleet != null)
        {
            SelectFleetByList(gs, splitFleet);
            Expect(gs.SetCheck("Retreat", true), "tick 'Retreat instead of fighting'");
            int n0 = s.Game.State.Fleets.Values.Count(f => f.Side == me); int sid = splitFleet.Id;
            Expect(gs.TickShip(0), "tick a ship to split off");
            Expect(gs.Click("Split the ticked"), "click Split");
            int refusedBefore = s.Messages.Count;
            gs.PlayDay();
            Expect(s.Game.State.Fleets.Values.Count(f => f.Side == me) == n0 + 1, "split made a new fleet");
            Expect(!s.Messages.Skip(refusedBefore).Any(m => m.Text.StartsWith("Order refused: Change the battle", StringComparison.Ordinal)), "retreat switch accepted");
            SelectFleetByList(gs, splitFleet);
            Expect(gs.SetCheck("Retreat", false), "untick retreat");
            var newest = s.Game.State.Fleets.Values.Where(f => f.Side == me).OrderByDescending(f => f.Id).First();
            SelectFleetByList(gs, newest);
            if (gs.Click("Merge this fleet into"))
            {
                gs.PlayDay();
                Expect(s.Game.State.Fleets.Values.Count(f => f.Side == me) == n0, "merge folded it back");
            }
            else Expect(false, "merge button available for the new fleet");
        }

        // ---------------------------------------------------------------- a battle: send the strongest other fleet onto the nearest enemy fleet
        // The deal differs per seed, so the opponent's fleets may all be on the move right now: let days pass until one stands at a system.
        RunUntil(gs, () => s.Game.State.Fleets.Values.Any(f => f.Side != me && f.At != null), 400);
        var enemyFleets = s.Game.State.Fleets.Values.Where(f => f.Side != me && f.At != null).ToList();
        var warFleet = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At != null && f.Id != troopFleet.Id && f.Ships.Count > 0 && fm.SpeedOf(f) > 0).OrderByDescending(f => fm.StrengthOf(f)).First();
        int battlesBefore = s.Messages.Count(m => m.Kind == MsgKind.Battle);
        bool gotBattle = false;
        for (int attempt = 0; attempt < 4 && !gotBattle; attempt++)
        {
            var foe = s.Game.State.Fleets.Values.Where(f => f.Side != me && f.At != null && fm.FindPath(warFleet.At ?? troopFleet.At!, f.At!, fm.SpeedOf(warFleet)).Ok)
                .OrderBy(f => fm.FindPath(warFleet.At!, f.At!, fm.SpeedOf(warFleet)).TotalTicks).FirstOrDefault();
            if (foe == null) break;
            if (warFleet.At == null) RunUntil(gs, () => warFleet.At != null, 400);
            if (warFleet.At == foe.At) { gotBattle = s.Messages.Count(m => m.Kind == MsgKind.Battle) > battlesBefore; break; }
            GD.Print($"battle attempt {attempt + 1}: {warFleet.Name} ({warFleet.At}) -> enemy '{foe.Name}' at {foe.At}");
            SelectFleetByList(gs, warFleet);
            Expect(gs.Click("Move..."), "click Move... onto the enemy fleet");
            gs.PickSystem(foe.At!);
            int guard = 0;
            while (guard++ < 500 && s.Winner == null)
            {
                bool cont = gs.PlayDay();
                if (!cont && s.Messages.Count(m => m.Kind == MsgKind.Battle) > battlesBefore) { gotBattle = true; break; }
                if (warFleet.At != null && warFleet.At == foe.At) break;
            }
        }
        if (gotBattle)
        {
            Expect(gs.ReportVisible && gs.ReportText.Contains("Battle of"), "the battle report popup opened by itself");
            Expect(gs.Banner.Contains("battle"), "game paused with a battle note: " + gs.Banner);
            gs.Click("Close");
            Expect(!gs.ReportVisible, "report closed");
            Expect(gs.ClickLog(m => m.Kind == MsgKind.Battle), "click a battle line in Messages");
            Expect(gs.ReportVisible && gs.ReportText.Contains("Battle of"), "log click reopens the battle report");
            GD.Print(gs.ReportText);
            gs.Click("Close");
            gs.SetFilter("Battles");
            Expect(gs.LogRows > 0 && gs.LogRows <= s.Messages.Count, $"Battles filter shows {gs.LogRows} rows");
            gs.SetFilter("My systems");
            Expect(gs.LogRows > 0, $"My systems filter shows {gs.LogRows} rows");
            gs.SetFilter("All");
            Expect(gs.LogRows >= 1, "All filter back");
        }
        else Expect(false, "got a battle by sending a fleet onto an enemy fleet");

        FinishMissions();

        // ---------------------------------------------------------------- build a ship and a facility
        string yardSys = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System != null && o.Class.Contains("Shipyard") && s.OwnerOf(o.System) == me)
            .Select(o => o.System).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        Expect(yardSys != null, "I own a system with a shipyard");
        if (yardSys != null)
        {
            gs.SelectSystem(yardSys); gs.PlayRefresh();
            Expect(gs.Has("v  Facilities") || gs.Has(">  Facilities"), "Build menu has a Facilities section");
            Expect(gs.Has(">  Ships") && gs.Has(">  Fighters") && gs.Has(">  Troops"), "Build menu has collapsible Ships / Fighters / Troops sections");
            Expect(gs.Click(">  Ships"), "open the Ships section");
            gs.PlayRefresh();
            if (me == Side.Empire) Expect(gs.ButtonTexts().Any(t => t.StartsWith("Death Star   ", StringComparison.Ordinal)), "the Death Star is in the Empire's Ships build list");
            else Expect(!gs.ButtonTexts().Any(t => t.StartsWith("Death Star   ", StringComparison.Ordinal)), "the Alliance's Ships build list has no Death Star");
            var shipBtn = gs.ButtonTexts().FirstOrDefault(t => t.Contains("refined, upkeep") && !t.Contains("(not researched)") && t.Contains("   ") && !Rebellion.Core.Economy.FacilityKinds.TryParse(t[..t.IndexOf("   ", StringComparison.Ordinal)], out _)); // any researched ship
            Expect(shipBtn != null, "a basic ship can be chosen in the Ships list");
            if (shipBtn != null)
            {
                int before = s.Messages.Count;
                Expect(gs.Click(shipBtn[..shipBtn.IndexOf("   ", StringComparison.Ordinal)]), "click the ship to build it");
                gs.PlayDay();
                Expect(s.Messages.Skip(before).Any(m => m.Text.StartsWith("Construction started at " + yardSys, StringComparison.Ordinal)), "the shipyard started building it: " + string.Join(" | ", s.Messages.Skip(before).Select(m => m.Text)));
            }
            Expect(gs.Click(">  Troops"), "open the Troops section");
            gs.PlayRefresh();
            gs.Click(">  Fighters"); gs.PlayRefresh();
        }
        // facility: a system I own with a construction yard and a free energy slot
        string facSys = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System != null && o.Class.Contains("Construction Yard") && s.OwnerOf(o.System) == me)
            .Select(o => o.System).Distinct().OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault(x => eco.FreeEnergy(x) > 0);
        Expect(facSys != null, "I own a system with a construction yard and a free energy slot");
        if (facSys != null)
        {
            gs.SelectSystem(facSys); gs.PlayRefresh();
            if (!gs.Has("Refinery")) { gs.Click("v  Facilities"); gs.Click(">  Facilities"); gs.PlayRefresh(); }
            Expect(gs.Has("Refinery"), "Refinery is offered at " + facSys);
            int before = s.Messages.Count;
            Expect(gs.Click("Refinery"), "click Refinery to build it");
            gs.PlayDay();
            Expect(s.Messages.Skip(before).Any(m => m.Text.Contains("Construction started at " + facSys) && m.Text.Contains("Refinery")), "construction of the Refinery started");
            Expect(gs.PanelText().Contains("is building") || gs.PanelText().Contains("Under construction"), "system panel shows it being built");
        }

        // ---------------------------------------------------------------- save, then load with the confirm dialog
        int dayAtSave = s.Game.Day; string hashAtSave = s.Host.StateHash();
        Expect(gs.Click("Save"), "click Save");
        Expect(GameBoot.SaveExists(GameBoot.SaveSlot), "save file written");
        for (int i = 0; i < 12; i++) gs.PlayDay();
        Expect(gs.Click("Load"), "click Load");
        Expect(gs.ConfirmVisible, "Load asks for confirmation first");
        Expect(gs.Click("Cancel") && !gs.ConfirmVisible && gs.S == s && s.Game.Day == dayAtSave + 12, "Cancel keeps the running game");
        Expect(gs.Click("Load") && gs.ConfirmVisible, "Load again");
        Expect(gs.Click("Yes, load"), "confirm");
        var gs2 = Gs;
        Expect(gs2 != null && gs2 != gs, "a fresh game screen replaced the old one");
        Expect(gs2.S.Game.Day == dayAtSave && gs2.S.Host.StateHash() == hashAtSave && gs2.S.Player == me, $"loaded game is the saved one (day {gs2.S.Game.Day})");
        gs = gs2; s = gs.S;
        Expect(s.Rules == wantRules, "the battle rules survive Save and Load: " + s.Rules);
        Expect(!gs.HowToVisible, "loading does not show How to play");
        // menu: "Menu" asks first
        Expect(gs.Click("Menu") && gs.ConfirmVisible, "Menu asks first");
        Expect(gs.Click("Cancel"), "cancel leaving");

        PlaytestDeathStar(gs);

        // ---------------------------------------------------------------- run to day 400 against the AI
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int pauses = 0;
        int evStart = s.Game.Events.Count, msgStart = s.Messages.Count;
        while (s.Game.Day < 400 && s.Winner == null)
        {
            if (!gs.PlayDay()) pauses++;
            if (gs.ReportVisible) gs.Click("Close");
        }
        // The core's unfiltered event list is read here only to check the screens: the AI acted, and the player was told only what its
        // side may know (rule: m5-fog-and-hq §N8 event audience).
        var runEvents = Enumerable.Range(evStart, s.Game.Events.Count - evStart).Select(i => (Ev: s.Game.Events[i], Aud: s.Game.AudienceOf(i))).ToList();
        int enemyActs = runEvents.Count(x => x.Ev is Rebellion.Core.Control.SystemCaptured c && c.To == s.Enemy
                                          || x.Ev is FleetDeparted fd && s.Game.State.Fleets.TryGetValue(fd.Fleet, out var ef) && ef.Side == s.Enemy);
        Expect(enemyActs > 0, $"the AI opponent did things ({enemyActs} enemy fleet departures and captures)");
        int myBit = Rebellion.Core.Victory.ScenarioState.Bit(me);
        var hiddenCaptures = runEvents.Where(x => x.Ev is Rebellion.Core.Control.SystemCaptured && (x.Aud & myBit) == 0).Select(x => (Rebellion.Core.Control.SystemCaptured)x.Ev).ToList();
        var newMsgs = s.Messages.Skip(msgStart).ToList();
        Expect(!hiddenCaptures.Any(c => newMsgs.Any(m => m.Day == c.Day && m.Text.Contains("captured " + c.System))),
            $"no message about the {hiddenCaptures.Count} captures the player's side could not see");
        GD.Print($"ran to day {s.Game.Day} in {sw.ElapsedMilliseconds} ms, {pauses} auto-pauses, {s.Messages.Count} messages, winner {(s.Winner?.ToString() ?? "none")}");
        Expect(s.Game.Day >= 400 || s.Winner != null, "reached day 400 (or someone won)");
        var owners = s.Game.State.SystemOwners.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count());
        GD.Print("systems: " + string.Join(", ", owners.Select(kv => $"{kv.Key}={kv.Value}")));
        Expect(GameBoot.SaveExists(GameBoot.AutoSaveSlot), "autosave written during the run");
        Expect(gs.LogRows > 0 && gs.LogRows <= 400, "log stays capped");

        // ---------------------------------------------------------------- victory screen (core debug order, test only)
        PlaytestEnding(gs, true);
        // ---------------------------------------------------------------- defeat screen on a fresh game
        ShowMenu();
        UiTest.Click(_screen, side.ToString()); UiTest.Click(_screen, "New Game");
        PlaytestEnding(Gs, false);
        ShowMenu();
        Expect(UiTest.Click(_screen, "Continue"), "Continue (autosave) is enabled on the start screen");
        Expect(Gs != null && Gs.S.Game.Day > 0 && Gs.S.Player == side && !Gs.HowToVisible, $"Continue loads the autosave (day {Gs?.S.Game.Day})");
        ShowMenu();
        Expect(UiTest.Click(_screen, "Load saved game") && Gs != null && Gs.S.Game.Day > 0, "Load saved game from the start screen");
        ShowMenu();
    }

    // ---------------------------------------------------------------- fog of war (rule: m5-fog-and-hq §F3, §N8)

    /// <summary>An enemy fleet the player's side does not see is neither drawn nor listed; what is drawn is only what the side sees.</summary>
    void PlaytestFog(GameScreen gs)
    {
        var s = gs.S; var me = s.Player;
        gs.PlayRefresh();
        var drawn = gs.DrawnFleetIds();
        Expect(drawn.Count > 0 && drawn.All(id => s.Game.State.Fleets[id].Side == me), $"only own fleets are drawn from the core's fleet list ({drawn.Count})");
        var markers = gs.EnemyMarkerSystems();
        Expect(markers.All(s.Sees), $"enemy fleet markers only at systems the side sees ({markers.Count})");
        // the core is read only to find a test case: an enemy fleet at a system the player's side cannot see
        var hidden = s.Game.State.Fleets.Values.Where(f => f.Side != me && f.At != null && !s.Sees(f.At)).OrderBy(f => f.Id).FirstOrDefault();
        Expect(hidden != null, "the start position has an enemy fleet the player cannot see");
        if (hidden != null)
        {
            GD.Print($"hidden enemy fleet: '{hidden.Name}' at {hidden.At}");
            Expect(!drawn.Contains(hidden.Id) && !markers.Contains(hidden.At), $"the unseen enemy fleet at {hidden.At} is not drawn");
            gs.SelectSystem(hidden.At); gs.PlayRefresh();
            string panel = gs.PanelText() + "\n" + string.Join("\n", gs.ButtonTexts());
            Expect(!panel.Contains(hidden.Name) && !panel.Contains("[enemy] "), $"the system panel of {hidden.At} does not list the unseen fleet");
            Expect(panel.Contains("Last seen day") || panel.Contains("Unexplored") || panel.Contains("Not seen since"), $"the panel says how old the picture of {hidden.At} is");
            gs.SelectFleet(hidden.Id, false); gs.PlayRefresh();
            Expect(!gs.PanelText().Contains(hidden.Name), "an enemy fleet cannot be selected by id");
        }
        string unexplored = gs.G.Systems.Select(x => x.Name).Where(n => !s.Explored(n) && s.OwnerOf(n) != me).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        Expect(unexplored != null, "some systems are unexplored at the start");
        if (unexplored != null)
        {
            Expect(gs.DrawnUnknown(unexplored), $"{unexplored} is drawn as unexplored");
            Expect(gs.HoverTextFor(unexplored).Contains("unexplored"), "hovering it says unexplored: " + gs.HoverTextFor(unexplored));
            gs.SelectSystem(unexplored); gs.PlayRefresh();
            Expect(gs.PanelText().Contains("Unexplored") && !gs.PanelText().Contains("Owner:"), "its panel shows no owner, only 'Unexplored'");
        }
        gs.View.Select(-1);
    }

    /// <summary>Empire: the Rebel HQ is not tagged and the Enemy HQ button says unknown while the Empire's picture has not shown it.</summary>
    void PlaytestEmpireHqUnknown(GameScreen gs)
    {
        var s = gs.S;
        Expect(s.Fog.KnownHq(Side.Empire) == null, "the Empire does not know the Rebel HQ at the start");
        Expect(!gs.MapTags().Any(t => t.Text.Contains("REBEL HQ")), "no REBEL HQ (TARGET) tag on the map");
        Expect(gs.ButtonTexts().Contains("Enemy HQ: unknown"), "the Enemy HQ button says unknown");
        Expect(gs.Click("Enemy HQ: unknown") && gs.ToastText.Contains("unknown"), "pressing it says the HQ location is unknown: " + gs.ToastText);
        Expect(gs.MapTags().Any(t => t.Text == "YOUR CAPITAL" && t.System == GameBoot.EmpireCapital), "your capital is tagged");
    }

    /// <summary>Alliance: Move HQ... with a refused order (plain reason), then a real move that arrives.</summary>
    void PlaytestMoveHq(GameScreen gs)
    {
        var s = gs.S; var me = s.Player; var fm = s.Host.Fleets;
        string hq = s.MyHqSystem;
        Expect(hq != null && gs.MapTags().Any(t => t.System == hq && t.Text == "YOUR HQ"), $"your HQ is tagged at {hq}");
        Expect(gs.ButtonTexts().Contains("Enemy HQ"), "the Alliance knows the enemy capital");
        Expect(gs.Click("My HQ"), "click My HQ");
        gs.PlayRefresh();
        Expect(gs.Has("Move HQ..."), "the HQ system panel offers Move HQ...");
        int speed = s.Fog.HqRules.Speed;
        string notHeld = gs.G.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) != me && fm.FindPath(hq, n, speed).Ok)
            .OrderBy(n => fm.FindPath(hq, n, speed).TotalTicks).ThenBy(n => n, StringComparer.Ordinal).First();
        string dest = s.Game.State.SystemOwners.Where(kv => kv.Value == me && kv.Key != hq).Select(kv => kv.Key).Where(n => fm.FindPath(hq, n, speed).Ok)
            .OrderBy(n => fm.FindPath(hq, n, speed).TotalTicks).ThenBy(n => n, StringComparer.Ordinal).First();
        GD.Print($"HQ at {hq}: refused try {notHeld}, move to {dest}");

        // refused: a system the Alliance does not hold
        Expect(gs.Click("Move HQ..."), "click Move HQ...");
        Expect(gs.Banner.Contains("Move HQ"), "banner asks for a destination: " + gs.Banner);
        gs.HoverTextFor(notHeld);
        Expect(gs.Banner.Contains("not yours"), "the preview warns the destination is not held: " + gs.Banner);
        int before = s.Messages.Count;
        gs.PickSystem(notHeld);
        Expect(s.PendingHqMove == notHeld && s.QueuedOrders == 1, "MoveHq order queued");
        gs.PlayDay();
        Expect(s.Messages.Skip(before).Any(m => m.Text.StartsWith("Order refused: Move the HQ to " + notHeld, StringComparison.Ordinal) && m.Text.Contains("only move to a system you hold")),
            "refusal shown with a plain reason: " + string.Join(" | ", s.Messages.Skip(before).Select(m => m.Text)));
        Expect(s.MyHqSystem == hq, "the HQ stayed");

        // accepted
        gs.Click("My HQ"); gs.PlayRefresh();
        Expect(gs.Click("Move HQ..."), "click Move HQ... again");
        gs.HoverTextFor(dest);
        Expect(gs.Banner.Contains("HQ to " + dest) && gs.Banner.Contains("arrives day"), "preview shows route and arrival day: " + gs.Banner);
        before = s.Messages.Count;
        gs.PickSystem(dest);
        gs.PlayRefresh();
        Expect(gs.HqStatusText.Contains(dest), "HQ line says the move is waiting: " + gs.HqStatusText);
        gs.PlayDay();
        var h = s.OwnHq;
        Expect(h.Status == Rebellion.Core.Fog.HqStatus.InTransit || s.MyHqSystem == dest, $"the HQ left {hq} (status {h.Status})");
        Expect(s.Messages.Skip(before).Any(m => m.Text.StartsWith("HQ moving to " + dest + ", arrives day", StringComparison.Ordinal)), "message 'HQ moving to X, arrives day N': " + string.Join(" | ", s.Messages.Skip(before).Select(m => m.Text)));
        if (h.Status == Rebellion.Core.Fog.HqStatus.InTransit)
        {
            int arrive = GameSession.HqArrivalDay(h);
            Expect(gs.HqStatusText == $"HQ moving to {dest}, arrives day {arrive}.", "HQ line: " + gs.HqStatusText);
            Expect(!gs.MapTags().Any(t => t.Text == "YOUR HQ"), "no HQ tag while it travels");
            Expect(gs.Click("My HQ") && gs.ToastText.Contains("HQ moving to " + dest), "My HQ while moving says where it goes: " + gs.ToastText);
            Expect(RunUntil(gs, () => s.MyHqSystem == dest, 200), $"the HQ arrived at {dest} (day {s.Game.Day}, expected {arrive})");
            Expect(s.Game.Day == arrive, $"on the announced day {arrive}");
        }
        Expect(s.Messages.Any(m => m.Text == $"Your headquarters arrived at {dest}."), "arrival message");
        Expect(gs.MapTags().Any(t => t.System == dest && t.Text == "YOUR HQ") && gs.HqStatusText == "", "the HQ tag moved and the HQ line cleared");
    }

    void PlaytestEnding(GameScreen gs, bool playerWins)
    {
        var s = gs.S; var me = s.Player;
        if (s.Winner != null)
        {
            Expect(gs.VictoryVisible, "game already ended and the end screen shows");
            if (!playerWins) return;
            Expect(gs.VictoryText.Contains(s.Winner == me ? "VICTORY" : "DEFEAT"), "end screen text: " + gs.VictoryText.Replace("\n", " "));
            return;
        }
        s.Host.Control.DebugOrdersEnabled = true; // test-only path: the core's debug order, off in the real game
        Side winner = playerWins ? me : s.Enemy;
        string goal = winner == Side.Alliance ? "Coruscant" : s.Host.HqSystem;
        s.Host.Control.SubmitDebugSetHolder(winner, goal, winner);
        // Standard victory (the default since 2026-10-16) also needs the two enemy leaders captive: test-only, straight through the character module.
        foreach (var k in s.Host.Victory.Keys.Where(k => k.Standard && k.Side == winner))
            if (s.Host.Characters.Get(k.Character) is { } c) s.Host.Characters.Capture(null, c, winner, null, "Playtest");
        gs.PlayDay();
        Expect(s.Winner == winner, $"core declares {winner} the winner ({GameBoot.WinReason(s.Host)})");
        Expect(gs.VictoryVisible, "victory screen shows");
        Expect(gs.VictoryText.Contains(playerWins ? "VICTORY" : "DEFEAT"), "it says " + (playerWins ? "VICTORY" : "DEFEAT"));
        Expect(!gs.Playing, "the clock is stopped after the end");
        if (playerWins)
        {
            Expect(gs.Click("Back to the start screen"), "click Back to the start screen");
            Expect(_screen is StartMenu, "start screen is back");
        }
    }
}
