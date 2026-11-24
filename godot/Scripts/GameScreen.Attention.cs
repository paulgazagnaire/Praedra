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
/// own state, its picture, its sensor log and the events told to it.
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
    const int MaxRows = 3; // a chance lists a few examples; the map layer shows the rest

    void RefreshAttention()
    {
        _attention = Attention();
        RefreshTabTitles();
        if (_tab == DrawerTab.Needs) _panelDirty = true;
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
        var enemyWinKeys = S.Host.Victory.Keys.Where(k => k.Standard && k.Side == S.Enemy).Select(k => k.Character).OrderBy(id => id).ToList();
        foreach (int id in enemyWinKeys)
            if (Cm.Get(id) is { Status: CharacterStatus.Captured } c)
                list.Add(new Item($"key-{c.Id}", Level.Danger, $"{S.CharName(c.Id)} is a prisoner{(c.HeldAt != null ? $" at {c.HeldAt}" : "")}",
                    $"The {S.NameOf(S.Enemy)} needs this leader captive to win. Send a team on a Rescue mission.", c.HeldAt, new() { ("Leaders", () => ShowTab(DrawerTab.Leaders), null) }));

        // ---- your own leaders in prison who are not a win target: amber (Paul 2026-11-24)
        foreach (var c in S.MyCharacters.Where(c => c.Status == CharacterStatus.Captured && !enemyWinKeys.Contains(c.Id)))
            list.Add(new Item($"prisoner-{c.Id}", Level.Warn, $"{S.CharName(c.Id)} is a prisoner{(c.HeldAt != null ? $" at {c.HeldAt}" : "")}",
                "Send a team on a Rescue mission to get this leader back.", c.HeldAt, new() { ("Leaders", () => ShowTab(DrawerTab.Leaders), null) }));

        LostSystems(list);
        CapitalThreat(list);
        DamagedFleets(list);

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
                     .Where(x => x.Kind == Praedra.Core.Fog.SightingKind.SensorArrival && x.ReachDay is int rd && rd >= S.Game.Day && !CapitalHeld(x.System))
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
            list.Add(new Item("upkeep-over", Level.Danger, $"Upkeep over the limit: {load} of {cap}", $"Every {eco.Params.ScrapPeriod} days the game scraps one of your {eco.AutoscrapMode switch { Praedra.Core.Economy.AutoscrapMode.Classic => "units at random", Praedra.Core.Economy.AutoscrapMode.Smart => "units (cheapest troops first)", _ => "buildings (never a mine or refinery; defences first, construction yards last), then units once no such building is left" }} until you are back under it. Build mines and refineries (each pair adds {eco.Params.CapacityPerFacility}), or choose what goes yourself: Scrap in a fleet's window or a system's Forces and Overview.", null,
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
                list.Add(new Item($"invade-{f.Id}-{at}", Level.Info, $"{S.NameOf(f)} is at {at} with troops", "No enemy fleet there. Invade now, or bombard first (the fleet's Orders).", at,
                    new() { ("Invade now", () => Invade(fl), "Primary"), ("Orders...", () => OpenMgmt(MgKind.Fleet, fleet: fl.Id, tab: "Command"), null) }));
            if (S.HoldsHarvester(f) && !S.PendingOf(f.Id).Any())
                list.Add(new Item($"harvest-{f.Id}-{at}", Level.Info, $"Your harvester is at {at}", "It can destroy the system (asks first).", at,
                    new() { ("Destroy System...", () => AskDestroySystem(fl.Id), "DangerButton"), ("Orders...", () => OpenMgmt(MgKind.Fleet, fleet: fl.Id, tab: "Command"), null) }));
        }
        var infos = new List<Item>();
        // idle yards: one item for all of them, a row per system (a long list of the same card buries what matters)
        var idleRows = new List<(string, Action)>();
        foreach (var n in mine)
        {
            var idle = IdleYardFamilies(n);
            if (idle.Count == 0 || S.Host.Support.IsUprising(n)) continue;
            string sys = n;
            var fam = idle[0];
            var tab = fam switch { YardFamily.Shipyard => InfraTab.Shipyard, YardFamily.Training => InfraTab.Training, _ => InfraTab.Construction };
            // the system, and its Infrastructure window on the first idle yard: where its work is chosen (Paul 2026-11-24, Selected sums up)
            idleRows.Add(($"{n}: {string.Join(", ", idle.Select(FamilyName))}", () => { GoTo(sys, true); OpenInfraOn(sys, tab, fam); }));
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

    // ================================================================== the four alerts Paul asked for (2026-11-24)

    /// <summary>How long a lost system stays on the list (unless you take it back). UI choice, stated in PR "Needs you: four new alerts".</summary>
    const int LostSystemDays = 30;
    /// <summary>A fleet is badly damaged when its ships have at most this share of their full hull, all ships added up.</summary>
    const int BadlyDamagedHullPercent = 50;
    /// <summary>An enemy fleet the player sees that could reach the capital within this many days (its class speed, no navigator) is a threat.</summary>
    const int CapitalThreatDays = 10;

    /// <summary>The last day a capital threat was there (null = never since this screen opened).</summary>
    int? _capitalThreatSeen;
    /// <summary>A threat stops the clock again only after it has been gone this many days, so a fleet that drops out of view for a day or two
    /// does not stop it each time (QA-122: 31 to 96 stops in 1500 days without it, a third to a half of them the same threat blinking).</summary>
    const int CapitalThreatRearmDays = 10;

    /// <summary>
    /// The day a capital threat shows after at least <see cref="CapitalThreatRearmDays"/> days without one, the clock stops, if Settings say so
    /// (DEFAULT on, Paul 2026-11-24 "Pause, on"). Called every day, even when another stop fired, so a threat seen on a day that already
    /// paused does not stop the clock again the next day (Ponytail on #378).
    /// </summary>
    string CapitalThreatPause()
    {
        var probe = new List<Item>();
        CapitalThreat(probe);
        if (probe.Count == 0) return null;
        int day = S.Game.Day;
        bool fresh = _capitalThreatSeen is not int last || day - last > CapitalThreatRearmDays;
        _capitalThreatSeen = day;
        if (!fresh || !UiSettings.PauseCapitalThreat) return null;
        return $"Paused: your capital {S.MyHqSystem} is threatened";
    }

    bool CapitalHeld(string sys) => sys == S.MyHqSystem && S.OwnerOf(sys) == Me && !S.IsDestroyed(sys);

    /// <summary>Systems you held that changed hands or were destroyed in the last <see cref="LostSystemDays"/> days and are not yours again: one red item.
    /// Read from the side's event feed, which is not saved: after a Load only losses since the load show (Ponytail on #375; accepted for now).</summary>
    void LostSystems(List<Item> list)
    {
        int from = S.Game.Day - LostSystemDays;
        var lost = new SortedDictionary<string, (int Day, string How)>(StringComparer.Ordinal);
        var ev = S.Fog.EventsFor(Me);
        for (int i = ev.Count - 1; i >= 0 && ev[i].Day >= from; i--)
        {
            (string sys, string how) = ev[i] switch
            {
                Praedra.Core.Control.SystemCaptured c when c.From == Me && c.To != Me => (c.System, c.To is Side to ? $"taken by the {S.NameOf(to)}" : "went neutral"),
                Praedra.Core.Harvester.SystemDestroyed d when d.PriorHolder == Me => (d.System, "destroyed by a harvester"),
                _ => (null, null),
            };
            if (sys == null || lost.ContainsKey(sys) || S.OwnerOf(sys) == Me) continue;
            lost[sys] = (ev[i].Day, how);
        }
        if (lost.Count == 0) return;
        var rows = lost.OrderByDescending(kv => kv.Value.Day).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => { string sys = kv.Key; return ($"{sys}: {kv.Value.How}, day {kv.Value.Day}", (Action)(() => GoTo(sys, true))); }).ToList();
        string first = lost.OrderByDescending(kv => kv.Value.Day).First().Key;
        list.Add(new Item("lost-systems", Level.Danger, lost.Count == 1 ? $"You lost {first}" : $"You lost {lost.Count} systems",
            $"In the last {LostSystemDays} days. Take them back with a fleet and troops.", lost.Count == 1 ? first : null,
            lost.Count == 1 ? new() { ("Go there", () => GoTo(first, true), null) } : new()) { Rows = lost.Count == 1 ? null : rows });
    }

    /// <summary>
    /// Your capital, while you still hold it, with a visible enemy fleet that could get there within <see cref="CapitalThreatDays"/> days, or a
    /// Sensor Station warning of a fleet heading for it: red. A fleet already there is "Enemy fleet at ..." below.
    /// </summary>
    void CapitalThreat(List<Item> list)
    {
        string cap = S.MyHqSystem;
        if (cap == null || !CapitalHeld(cap)) return;
        var near = new List<(string Sys, int Days, Praedra.Core.Fog.FleetPicture Fleet)>();
        foreach (var (sys, pic) in S.Fog.VisibleFleets(Me).Where(v => v.Fleet.Side != Me && v.System != cap))
        {
            int speed = Praedra.Core.Travel.TravelFormula.FleetSpeed(
                pic.Ships.SelectMany(kv => Enumerable.Repeat(Fm.ShipClasses.TryGetValue(kv.Key, out var c) ? c.Speed(false) : 100, kv.Value)),
                pic.Squadrons.SelectMany(kv => Enumerable.Repeat(Fm.ShipClasses.TryGetValue(kv.Key, out var c) ? c.Speed(false) : 100, kv.Value)));
            if (speed <= 0) continue;
            var path = Fm.FindPath(sys, cap, speed);
            if (path.Ok && path.TotalTicks <= CapitalThreatDays) near.Add((sys, path.TotalTicks, pic));
        }
        var warned = S.Fog.Sightings(Me, Math.Max(0, S.Game.Day - 30))
            .Where(x => x.Kind == Praedra.Core.Fog.SightingKind.SensorArrival && x.System == cap && x.ReachDay is int rd && rd >= S.Game.Day)
            .OrderByDescending(x => x.Day).FirstOrDefault();
        if (near.Count == 0 && warned == null) return;
        var lines = new List<string>();
        if (warned != null) lines.Add($"Your Sensor Station saw a {(warned.Size?.ToString() ?? "").ToLowerInvariant()} {S.NameOf(warned.Side)} fleet coming, expected day {warned.ReachDay}.");
        foreach (var n in near.OrderBy(n => n.Days).ThenBy(n => n.Sys, StringComparer.Ordinal).Take(MaxRows))
            lines.Add($"At {n.Sys}, about {Ui.InDays(n.Days)} away: {n.Fleet}.");
        if (near.Count > MaxRows) lines.Add($"And {near.Count - MaxRows} more enemy fleets close by.");
        lines.Add("Bring fleets and troops home before it falls.");
        list.Add(new Item("capital-threat", Level.Danger, $"Your capital {cap} is threatened", string.Join(" ", lines), cap,
            new() { ("Go there", () => GoTo(cap, true), null) }));
    }

    /// <summary>
    /// Your fleets whose ships have at most <see cref="BadlyDamagedHullPercent"/>% of their full hull left: red, or amber while they mend at a
    /// working shipyard of yours (fast repair).
    /// </summary>
    void DamagedFleets(List<Item> list)
    {
        var eco = S.Host.Economy;
        foreach (var f in S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.Ships.Count > 0).OrderBy(f => f.Id))
        {
            long have = 0, full = 0;
            foreach (var o in f.Ships.Select(eco.Find))
                if (o != null && Fm.ClassOf(o) is { Hull: > 0 } c) { have += Math.Max(0, o.Hull); full += c.Hull; }
            if (full == 0 || have * 100 > full * BadlyDamagedHullPercent) continue;
            int pct = (int)(have * 100 / full);
            var fl = f;
            bool mending = f.At != null && S.Host.Repair.FastRepair(f.At, Me);
            string why = mending
                ? $"Its ships have {pct}% of their hull left. It is mending at your shipyard at {f.At}" + (RepairOf(f) is { Days: > 0 } r ? $", whole in {Ui.InDays(r.Days)}." : ".")
                : $"Its ships have {pct}% of their hull left. Another battle may lose it: send it to one of your shipyards to mend.";
            var acts = new List<(string, Action, string)> { ("Fleet", () => OpenMgmt(MgKind.Fleet, fleet: fl.Id), null) };
            if (f.At != null) { string at = f.At; acts.Add(("Go there", () => GoTo(at, true), null)); }
            list.Add(new Item($"damaged-{f.Id}", mending ? Level.Warn : Level.Danger, $"{S.NameOf(f)} is badly damaged", why, f.At, acts));
        }
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
        Lead(items.Count == 0 ? "Nothing needs a decision right now."
            : urgent + watch == 0 ? "Nothing urgent." : string.Join(", ", new[] { urgent > 0 ? $"{urgent} urgent" : null, watch > 0 ? $"{watch} to watch" : null }.Where(x => x != null)) + ".");
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
                    if (!InDrawer) r.AddThemeFontSizeOverride("font_size", Ui.Small);   // in the window: the Fleet window's 12 px button words
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
            if (UiSettings.PauseEnemyAtMine && m.Tag == "FleetArrived" && m.Kind == MsgKind.Bad) return "Paused: " + m.Text.TrimEnd('.');
            if (UiSettings.PauseUprising && m.Tag == "UprisingStarted" && m.Kind == MsgKind.Bad) return $"Paused: uprising on {m.System}";
            if (UiSettings.PauseSensors && m.Tag == "SensorReport") return "Paused: " + m.Text.TrimEnd('.');
            if (UiSettings.PauseCompleted && m.Tag == "UnitCompleted") return "Paused: " + m.Text.TrimEnd('.');
            if (UiSettings.PauseMission && m.Tag is "MissionSucceeded" or "MissionFailed" or "MissionFoiled") return "Paused: " + m.Text.TrimEnd('.');
        }
        return null;
    }
}
