using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Construction;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// "Needs you" (user stories D1-D4, K2): every problem and chance that wants a decision, worst first, each with the button that deals with
/// it. Never make the player hunt for problems (the UI principles, 06-revamped-ui.md). Built only from what the player's side may know: its
/// own state, its picture, its sensor log and the events told to it. "Run until something needs me" plays until a new red or yellow item
/// appears.
/// </summary>
public partial class GameScreen
{
    enum Level { Danger, Warn, Info }

    sealed record Item(string Key, Level Level, string Title, string Why, string System, List<(string Label, Action Do, string Variation)> Actions)
    {
        /// <summary>Optional list rows under the text (one per system for grouped items), each a click target.</summary>
        public List<(string Text, Action Do)> Rows { get; init; }
    }

    List<Item> _attention = new();
    bool _runUntilNeeded;
    HashSet<string> _runBaseline = new();
    const int MaxRows = 3; // a chance lists a few examples; the map layer shows the rest

    void RefreshAttention()
    {
        _attention = Attention();
        RefreshTabTitles();
        if (_tab == DrawerTab.Needs) _panelDirty = true;
    }

    /// <summary>The first red or yellow item that was not there when "Run until something needs me" started, or null.</summary>
    string NewAttention()
    {
        _attention = Attention();
        RefreshTabTitles();
        var fresh = _attention.FirstOrDefault(a => a.Level != Level.Info && !_runBaseline.Contains(a.Key));
        if (fresh != null) { ShowTab(DrawerTab.Needs); return fresh.Title; }
        return null;
    }

    void RunUntilNeeded()
    {
        if (S.Winner != null || OverlayOpen) return;
        _attention = Attention();
        _runBaseline = _attention.Where(a => a.Level != Level.Info).Select(a => a.Key).ToHashSet();
        _pauseNote = null;
        _playing = true;
        _runUntilNeeded = true;
        HideModal(_reportPanel);
        RefreshTop();
    }

    List<Item> Attention()
    {
        var list = new List<Item>();
        var eco = S.Host.Economy; var sup = S.Host.Support;
        var mine = S.Game.State.SystemOwners.Where(kv => kv.Value == Me).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
        List<(string, Action, string)> GoThere(string sys) => new() { ("Go there", () => GoTo(sys, true), null) };

        // ---- the war: a win by control running (told to both sides), own key leaders in prison
        if (S.Host.Victory.DominanceDueDay is int due && S.Host.Victory.DominanceLeader is Side lead)
        {
            int left = Math.Max(0, due - S.Game.Day);
            if (lead == Me) list.Add(new Item("dominance-me", Level.Info, $"You win on day {due} ({Ui.InDays(left)})", $"Keep at least {S.Host.Victory.Rules.DominanceSharePercent}% of the claimed worlds until then.", null, new() { ("Goal", ShowGoal, null) }));
            else list.Add(new Item("dominance-enemy", Level.Danger, $"The {S.NameOf(lead)} wins on day {due} ({Ui.InDays(left)})", $"It holds {S.Host.Victory.Rules.DominanceSharePercent}% of the claimed worlds after your capital fell. Take systems back to break its hold.", null, new() { ("Goal", ShowGoal, null) }));
        }
        foreach (var k in S.Host.Victory.Keys.Where(k => k.Standard && k.Side == S.Enemy).OrderBy(k => k.Character))
            if (Cm.Get(k.Character) is { Status: CharacterStatus.Captured } c)
                list.Add(new Item($"key-{c.Id}", Level.Danger, $"{S.CharName(c.Id)} is a prisoner{(c.HeldAt != null ? $" at {c.HeldAt}" : "")}",
                    $"The {S.NameOf(S.Enemy)} needs this leader captive to win. Send a team on a Rescue mission.", c.HeldAt, new() { ("Leaders", () => ShowTab(DrawerTab.Leaders), null) }));

        // ---- your systems
        foreach (var n in mine)
        {
            if (S.IsDestroyed(n)) continue;
            int have = sup.RegimentCount(n), need = sup.RequiredGarrison(n);
            var enemy = S.VisibleEnemyFleetsAt(n);
            if (enemy.Count > 0)
                list.Add(new Item($"enemy-{n}", Level.Danger, $"Enemy fleet at {n}", string.Join("; ", enemy), n, GoThere(n)));
            if (sup.IsUprising(n))
                list.Add(new Item($"uprising-{n}", Level.Danger, $"Uprising on {n}", $"Yards and mines there stop. It has {have} of {need} regiments needed.", n, FixTroops(n, Math.Max(1, need - have))));
            else if (have < need)
                list.Add(new Item($"short-{n}", Level.Warn, $"{n} is short of troops: {have} of {need}", $"At {eco.SupportOf(n, Me)}% support it needs {need}. With fewer, its people may revolt and its yards and mines stop.", n, FixTroops(n, need - have)));
            if (S.Game.State.Blockades.TryGetValue(n, out var b) && b != Me)
                list.Add(new Item($"blockade-{n}", Level.Warn, $"{n} is blockaded", "Its mines and yards stop until the enemy fleet is driven off.", n, GoThere(n)));
        }

        // ---- sensor warnings still ahead (rule: m5-fog-and-hq §N4)
        foreach (var r in S.Fog.Sightings(Me, Math.Max(0, S.Game.Day - 30))
                     .Where(x => x.Kind == Praedra.Core.Fog.SightingKind.SensorArrival && x.ReachDay is int rd && rd >= S.Game.Day)
                     .GroupBy(x => x.System).Select(g => g.OrderByDescending(x => x.Day).First()).OrderBy(x => x.ReachDay))
            list.Add(new Item($"sensor-{r.System}-{r.ReachDay}", Level.Warn, $"Enemy fleet heading for {r.System}, expected day {r.ReachDay}",
                $"Your Sensor Station saw a {(r.Size?.ToString() ?? "").ToLowerInvariant()} {S.NameOf(r.Side)} fleet coming.", r.System, GoThere(r.System)));

        // ---- a harvester the Collective can see
        if (Me == Side.Collective)
            foreach (var (sys, pic) in S.Fog.VisibleFleets(Me).Where(v => v.Fleet.Ships.ContainsKey(Praedra.Core.Harvester.HarvesterModule.ShipClass)))
                list.Add(new Item($"harvester-{sys}", Level.Danger, $"A harvester at {sys}", "It can destroy a whole system. Beat its fleet in battle, or send a Spike the Harvester mission.", sys, GoThere(sys)));

        // ---- upkeep (E6)
        int load = eco.Load(Me), cap = eco.Capacity(Me);
        if (load > cap)
            list.Add(new Item("upkeep-over", Level.Danger, $"Upkeep over the limit: {load} of {cap}", $"Every {eco.Params.ScrapPeriod} days the game scraps one of your units ({(eco.AutoscrapMode == Praedra.Core.Economy.AutoscrapMode.Classic ? "at random" : "cheapest troops first")}) until you are back under it. Build mines and refineries (each pair adds {eco.Params.CapacityPerFacility}), or choose what goes yourself: Scrap in a fleet's window or a system's Forces and Overview.", null,
                new() { ("Fleets", () => ShowTab(DrawerTab.Fleets), null) }));
        else if (cap > 0 && load * 10 >= cap * 9)
            list.Add(new Item("upkeep-near", Level.Warn, $"Upkeep near the limit: {load} of {cap}", "A build order that would pass the limit is refused. Build a mine and a refinery to raise it.", null, new()));

        // ---- yards stopped for lack of refined: they wait in line and resume as refined comes in (rule: m2-mines-refineries-pools, slot 6.6.b)
        int waitingYards = S.Host.Construction.RefinedLine(Me).Count;
        if (waitingYards > 0)
            list.Add(new Item("yards-waiting", Level.Warn, $"{Ui.Count(waitingYards, "yard is", "yards are")} waiting for refined",
                $"They stopped mid-build because your refined stock ({eco.Refined(Me)}) ran out, and go on as refined comes in. More refineries (with mines to feed them) fix it, or stop builds you need less.", null,
                new() { ("Show the Yards layer", () => SetLayer(MapLayer.Yards), null) }));

        // ---- chances: troops ready at a target, a harvester in place, idle yards, leaders with nothing to do
        foreach (var f in S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At != null).OrderBy(f => f.Id))
        {
            string at = f.At;
            if (S.OwnerOf(at) == Me || S.IsDestroyed(at)) continue;
            var fl = f;
            if (S.Host.Troops.RegimentsAboard(f).Count > 0 && S.VisibleEnemyFleetsAt(at).Count == 0 && !S.PendingOf(f.Id).Any())
                list.Add(new Item($"invade-{f.Id}-{at}", Level.Info, $"{f.Name} is at {at} with troops", "No enemy fleet there. Invade now, or bombard first.", at,
                    new() { ("Invade now", () => Invade(fl), "Primary"), ("Select the fleet", () => SelectFleet(fl.Id, true), null) }));
            if (S.HoldsHarvester(f) && !S.PendingOf(f.Id).Any())
                list.Add(new Item($"harvest-{f.Id}-{at}", Level.Info, $"Your harvester is at {at}", "It can destroy the system (asks first).", at, new() { ("Select the fleet", () => SelectFleet(fl.Id, true), null) }));
        }
        var infos = new List<Item>();
        // idle yards: one item for all of them, a row per system (a long list of the same card buries what matters)
        var idleRows = new List<(string, Action)>();
        foreach (var n in mine)
        {
            var idle = new[] { YardFamily.Shipyard, YardFamily.Construction, YardFamily.Training }.Where(fam => HasYard(n, fam) && S.Host.Construction.ProjectAt(n, fam) is not { Units.Count: > 0 }).ToList();
            if (idle.Count == 0 || S.Host.Support.IsUprising(n)) continue;
            string sys = n;
            string sec = idle[0] switch { YardFamily.Shipyard => "Ships", YardFamily.Training => "Troops", _ => "Facilities" };
            idleRows.Add(($"{n}: {string.Join(", ", idle.Select(FamilyName))}", () => { GoTo(sys, true); _sysTab = SysTab.Build; _buildSection = sec; _panelDirty = true; _panelTimer = 0; }));
        }
        if (idleRows.Count > 0)
            infos.Add(new Item("idle-yards", Level.Info, $"Idle yards at {Ui.Count(idleRows.Count, "system", "systems")}", "An idle yard builds nothing. Click a system to give it work, or keep the refined for something else.", null,
                new() { ("Show the Yards layer", () => SetLayer(MapLayer.Yards), null) }) { Rows = idleRows });
        var idleLeaders = S.MyCharacters.Where(c => c.Status == CharacterStatus.Free && c.AttachedFleet == 0 && c.Transit == null && S.SystemPostOf(c) == null
            && S.MissionOf(c.Id) == null && S.PendingMissionOf(c.Id) == null && S.PendingOfCharacter(c.Id).FirstOrDefault() == null).ToList();
        if (idleLeaders.Count > 0)
            infos.Add(new Item("idle-leaders", Level.Info, $"{Ui.Count(idleLeaders.Count, "leader has", "leaders have")} nothing to do",
                string.Join(", ", idleLeaders.Take(5).Select(c => S.CharName(c.Id))) + (idleLeaders.Count > 5 ? ", ..." : "") + ". Give them a fleet post, a General post or a mission.", null,
                new() { ("Leaders", () => ShowTab(DrawerTab.Leaders), null) }));
        list.AddRange(infos);

        return list.OrderBy(i => i.Level).ToList(); // stable: the order above within a level
    }

    List<(string, Action, string)> FixTroops(string system, int missing)
    {
        var acts = new List<(string, Action, string)>();
        var row = CheapestRegiment();
        if (row != null && HasYard(system, YardFamily.Training))
            acts.Add(($"Train {missing} x {row.Name} here", () => SubmitBuild(system, YardFamily.Training, row, missing), "WarnButton"));
        acts.Add(("Go there", () => GoTo(system, true), null));
        return acts;
    }

    // ================================================================== the tab

    void NeedsPanel()
    {
        var items = _attention;
        int urgent = items.Count(i => i.Level == Level.Danger), watch = items.Count(i => i.Level == Level.Warn);
        Title("Needs you", items.Count == 0 ? "Nothing needs a decision right now."
            : urgent + watch == 0 ? "Nothing urgent." : string.Join(", ", new[] { urgent > 0 ? $"{urgent} urgent" : null, watch > 0 ? $"{watch} to watch" : null }.Where(x => x != null)) + ".");
        var run = Act(_runUntilNeeded ? "Running until something needs you... (Space stops)" : "Run until something needs me", () => { if (_runUntilNeeded) Pause(); else RunUntilNeeded(); },
            "Plays the days at the chosen speed and stops as soon as a new red or yellow item appears here (and on battles, captures and invasions, as always).", S.Winner != null, "Primary");
        run.Alignment = HorizontalAlignment.Center;
        if (items.Count == 0)
        {
            Note("All quiet: your systems have their troops, no enemy fleet is at them, and your yards are busy.");
            return;
        }
        Level? shown = null;
        foreach (var it in items)
        {
            if (it.Level != shown)
            {
                shown = it.Level;
                Heading(it.Level switch { Level.Danger => "Urgent", Level.Warn => "To watch", _ => "Could do better" });
            }
            ItemCard(it);
        }
    }

    void ItemCard(Item it)
    {
        var (bg, line, glyph) = it.Level switch
        {
            Level.Danger => (Ui.DangerDeep, Ui.Danger, Glyph.Kind.Warn),
            Level.Warn => (Ui.WarnDeep, Ui.Warn, Glyph.Kind.Warn),
            _ => (Ui.Panel, Ui.Line, Glyph.Kind.Dot),
        };
        using (Card(bg, new Color(line, it.Level == Level.Info ? 1f : 0.55f), Ui.Block))
        {
            var h = Ui.HBox(Ui.Block);
            h.AddChild(new Glyph(glyph, it.Level == Level.Info ? Ui.You : line, 13));
            var t = Ui.Label(it.Title, "Strong", it.Level == Level.Danger ? new Color("ffc9c9") : it.Level == Level.Warn ? new Color("ffe7a8") : Ui.Text);
            h.AddChild(t);
            Add(h);
            // a problem says what is wrong; for a chance the title and the buttons say enough, and the explanation is on hover
            if (!string.IsNullOrEmpty(it.Why))
            {
                if (it.Level == Level.Info) { h.TooltipText = Ui.Wrap(it.Why); h.MouseFilter = MouseFilterEnum.Stop; }
                else Add(Ui.Label(it.Why, null, Ui.Text2, Ui.Small));
            }
            if (it.Rows != null)
            {
                var list = Ui.VBox(Ui.Gap + 2);
                foreach (var (text, act) in it.Rows.Take(MaxRows))
                {
                    var r = Ui.Button(text, act, "Show it on the map", "Row");
                    r.Alignment = HorizontalAlignment.Left;
                    r.ClipText = true;
                    r.AddThemeFontSizeOverride("font_size", Ui.Small);
                    list.AddChild(r);
                }
                Add(list);
                if (it.Rows.Count > MaxRows) Add(Ui.Label($"and {it.Rows.Count - MaxRows} more", "Hint"));
            }
            if (it.Actions.Count > 0)
            {
                var row = Ui.HBox(Ui.Block);
                foreach (var (label, act, variation) in it.Actions)
                {
                    var b = Ui.Button(label, act, null, variation);
                    b.CustomMinimumSize = new Vector2(0, 32);
                    row.AddChild(b);
                }
                Add(row);
            }
        }
    }

    // ================================================================== events the player chose to stop on (G3)

    string ChosenPause(IReadOnlyList<Message> news)
    {
        foreach (var m in news)
        {
            if (UiSettings.PauseEnemyAtMine && m.Tag == "FleetArrived" && m.Kind == MsgKind.Warn) return "Paused: " + m.Text.TrimEnd('.');
            if (UiSettings.PauseUprising && m.Tag == "UprisingStarted" && m.Kind == MsgKind.Bad) return $"Paused: uprising on {m.System}";
            if (UiSettings.PauseSensors && m.Tag == "SensorReport") return "Paused: " + m.Text.TrimEnd('.');
            if (UiSettings.PauseCompleted && m.Tag == "UnitCompleted") return "Paused: " + m.Text.TrimEnd('.');
            if (UiSettings.PauseMission && m.Tag is "MissionSucceeded" or "MissionFailed" or "MissionFoiled") return "Paused: " + m.Text.TrimEnd('.');
        }
        return null;
    }
}
