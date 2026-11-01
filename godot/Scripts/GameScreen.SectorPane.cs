using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;
/// <summary>The sector window, part three: the system's own window (Buildings, Fleets, Forces). See GameScreen.SectorView.cs.</summary>
public partial class GameScreen
{
    // ================================================================== the system's own window

    void DetailHead(string n)
    {
        bool mine = S.OwnerOf(n) == Me;
        var row = Ui.HBox(Ui.Pad);
        _secDetailHead.AddChild(row);
        // an unexplored system shows an empty frame: what world it is stays hidden until someone looks (Paul, 2026-11-01)
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(64, 64), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        frame.AddThemeStyleboxOverride("panel", Ui.Box(Ui.PlanetSky, 8, new Color(1, 1, 1, 0.08f), 1, 0, 0));
        if (WorldKnown(n) && PlanetArt.Of(n) is Texture2D tex)
            frame.AddChild(new TextureRect { Texture = tex, TextureFilter = TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore });
        row.AddChild(frame);
        var names = Ui.VBox(2);
        names.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var t = Ui.Label(n, "Title", Ui.Text, 22, wrap: false);
        t.ClipText = true;
        names.AddChild(t);
        var (known, owner) = S.KnownOwnerOf(n);
        string who = S.IsDestroyed(n) ? "Destroyed" : mine ? $"Yours · {PlanetArt.KindOf(n) ?? "system"}" : S.Picture(n) == null ? "Unexplored" : !known ? "Owner unknown" : owner == null ? "Nobody's" : $"Held by the {S.NameOf(owner.Value)}";
        names.AddChild(Ui.Label(who, null, S.IsDestroyed(n) ? Ui.Danger : Ui.OwnerColor(mine ? Me : owner, Me), Ui.Small, wrap: false));
        row.AddChild(names);
        row.AddChild(Ui.Grow());
        var map = Ui.Button("Map", () => ShowOnMap(n), "Close this window and show the system on the map (double-click a card does the same)", "Chip");
        map.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(map);
        var close = Ui.Button("✕", CloseSystemPane, "Close the system window", "Clock");
        close.SetMeta(UiTest.FullName, "Close the system window");
        close.CustomMinimumSize = new Vector2(30, 30);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(close);

        var tabs = Ui.HBox(4);
        var group = new ButtonGroup();
        foreach (var (pane, word) in new[] { (SecPane.Buildings, "Buildings"), (SecPane.Fleets, "Fleets"), (SecPane.Forces, "Forces") })
        {
            var p = pane;
            var b = Ui.Chip(word, () => Later(() => { _secPane = p; _secDetailScroll.ScrollVertical = 0; RebuildSectorView(); }), null, _secPane == pane, group);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(0, 34);
            tabs.AddChild(b);
        }
        _secDetailHead.AddChild(Ui.Space(2));
        _secDetailHead.AddChild(tabs);
    }

    void PaneBuildings(string n)
    {
        if (S.IsDestroyed(n)) { Note("The planet and everything on it are gone. Nothing can be built here."); return; }
        if (S.OwnerOf(n) != Me)
        {
            var pic = S.Picture(n);
            if (pic == null) { Note("Unexplored: you don't know what is built here. Send a fleet or a spy to look."); return; }
            Heading("Buildings seen");
            if (pic.Facilities is { } fac)
            {
                if (fac.Value.Count == 0) Line($"none (seen day {fac.Stamp})", Dim);
                foreach (var kv in fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal)) Stat(kv.Key, kv.Value.ToString());
                Note($"As seen on day {fac.Stamp}. It may have changed since.");
            }
            else Line("unknown (nobody has looked at the ground yet)", Dim);
            if (pic.Status is { } ss) { Heading("Slots"); Stat("Energy slots", ss.Value.Energy.ToString()); Stat("Raw material slots", ss.Value.Raw.ToString()); }
            Note("You can only build on systems you hold.");
            return;
        }
        var eco = S.Host.Economy;
        // what the planet gives: the mines dig raw, the refineries turn raw into refined; both add room for upkeep
        using (Card())
        {
            int mines = eco.OfKindAt(n, FacilityKind.Mine), refs = eco.OfKindAt(n, FacilityKind.Refinery);
            int sup = Math.Clamp(eco.SupportOf(n, Me), eco.Params.SupportFloor, eco.Params.SupportCeiling);
            Stat("Mines", mines == 0 ? "none" : $"{mines}   about +{RawPerMonth(n)} raw / 30 days", RawPerMonth(n) > 0 ? Ui.Good : null,
                "Each mine digs one raw material every cycle. The cycle is longer when support is low and when the mine carries a big share of your upkeep.");
            Stat("Refineries", refs == 0 ? "none" : $"{refs}   about +{RefinedPerMonth(n)} refined / 30 days", RefinedPerMonth(n) > 0 ? Ui.Good : null,
                "Each refinery turns one raw material into one refined every cycle, when your side has raw in store. Refined pays for everything you build.");
            Note($"At {sup}% support a fresh cycle here takes {EconomyModule.CycleDays(eco.Params, sup, 0)} days. Every mine and refinery also adds {eco.Params.CapacityPerFacility} to your upkeep room (with as many of the other kind).");
            int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
            if (smug > 0) Line($"Smugglers may take a cycle's output {smug}% of the time. More support, or troops, fighters and ships here, scare them off.", Ui.Warn, Ui.Small);
            if (S.Game.State.Blockades.ContainsKey(n)) Line("Blockaded: the mines and yards here stop until the enemy fleet is driven off.", Bad, Ui.Small);
            if (S.Host.Support.IsUprising(n)) Line("Uprising: the mines and yards here stop until it ends.", Bad, Ui.Small);
        }
        var slots = eco.SlotsOf(n);
        Stat("Energy slots", $"{eco.EnergyUsed(n)} used of {slots.Energy}, {eco.FreeEnergy(n)} free", null, "Every building takes one energy slot.");
        Stat("Raw material slots", $"{eco.RawUsed(n)} used of {eco.EffectiveRaw(n)}, {eco.FreeRaw(n)} free", null, "A mine also needs a raw material slot. A planet never has more usable raw slots than energy slots.");
        Heading("Being built here");
        BuildQueues(n);
        Heading("Buildings");
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == n).ToList();
        if (here.Count(o => o.Completed) == 0) Line("none", Dim);
        else ScrapRows(here.Where(o => o.Completed), n);
        var building = here.Where(o => !o.Completed).ToList();
        if (building.Count > 0) Line($"Under construction: {Counts(building.Select(o => o.Class))}", Dim, Ui.Small);
        Heading("Build");
        // open the build table on what this planet can build: no shipyard here means start on Facilities
        if (_buildSection is "Ships" or "Fighters" && !HasYard(n, Praedra.Core.Construction.YardFamily.Shipyard)
            || _buildSection == "Troops" && !HasYard(n, Praedra.Core.Construction.YardFamily.Training)) _buildSection = "Facilities";
        BuildMenu(n);
        if (Me == Side.Collective && n == S.MyHqSystem) HqSection(n);
    }

    void PaneFleets(string n)
    {
        var pic = S.Picture(n);
        bool live = S.Sees(n) || S.OwnerOf(n) == Me;
        var own = S.Game.State.Fleets.Values.Where(f => f.At == n && f.Side == Me).OrderBy(f => f.Id).ToList();
        Heading(own.Count == 0 ? "Your fleets" : $"Your fleets ({own.Count})");
        if (own.Count == 0) Line("None in orbit.", Dim);
        foreach (var f in own) OrbitFleetCard(f);
        var heading = live ? "Enemy fleets here now" : "Enemy fleets";
        Heading(heading);
        if (live)
        {
            var enemy = S.VisibleEnemyFleetsAt(n);
            if (enemy.Count == 0) Line("None.", Dim);
            foreach (var e in enemy)
                using (Card(Ui.DangerDeep, new Color(Ui.Danger, 0.5f)))
                {
                    Line($"{S.NameOf(e.Side)}: {Ui.Count(e.ShipCount, "ship", "ships")}, {Ui.Count(e.SquadronCount, "squadron", "squadrons")}", Ui.Text);
                    Line(e.ToString(), Ui.Text2, Ui.Small);
                }
        }
        else if (pic?.Fleets is { } seen)
        {
            if (seen.Value.Count == 0) Line($"None seen on day {seen.Stamp}.", Dim);
            foreach (var e in seen.Value) Line($"{S.NameOf(e.Side)}, seen day {seen.Stamp}: {e}", Ui.Text2, Ui.Small);
            Note("You have no eyes here now: enemy fleets may have come or gone since.");
        }
        else Note("You have no eyes here: send a fleet, or build a Sensor Station on a route nearby.");
    }

    /// <summary>One of your fleets in orbit: what it is made of, its strength and troops aboard, and a button to command it.</summary>
    void OrbitFleetCard(Fleet f)
    {
        var eco = S.Host.Economy;
        using (Card(new Color(Ui.YouDeep, 0.55f), new Color(Ui.You, 0.35f)))
        {
            var h = Ui.HBox(8);
            var name = Ui.Label(f.Name, null, Ui.Text, Ui.Body, wrap: false);
            name.AddThemeFontOverride("font", Ui.SemiBold);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            h.AddChild(name);
            var str = Ui.Label($"strength {Fm.StrengthOf(f):0}", null, Ui.Text2, Ui.Small, wrap: false);
            str.AddThemeFontOverride("font", Ui.Num);
            h.AddChild(str);
            Add(h);
            Line(Counts(f.Ships.Select(id => eco.Find(id)?.Class ?? "?")), Ui.Text2, Ui.Small);
            if (f.Squadrons.Count > 0) Line("Fighters: " + Counts(f.Squadrons.Select(id => eco.Find(id)?.Class ?? "?")), Ui.Text2, Ui.Small);
            var aboard = S.Host.Troops.RegimentsAboard(f);
            if (aboard.Count > 0) Line("Troops aboard: " + Counts(aboard.Select(a => a.Regiment.Class)), Ui.Text2, Ui.Small);
            if (S.PlannedMoveOf(f.Id) is { } pm) Line($"Leaving for {pm.To}, arrives day {pm.ArrivalDay}", Pending, Ui.Small);
            var fid = f.Id;
            var b = Ui.Button("Command this fleet", () => { CloseSectorView(); SelectFleet(fid, true); }, "Close the sector window and select the fleet: move, attack, bombard, invade, load troops.", "Primary");
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(0, 34);
            Add(b);
        }
    }

    void PaneForces(string n)
    {
        if (S.IsDestroyed(n)) { Note("Nothing is left here."); CharactersAtSystem(n, false); return; }
        var pic = S.Picture(n);
        if (S.OwnerOf(n) == Me)
        {
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            using (Card(have < need ? Ui.WarnDeep : Ui.Panel, have < need ? new Color(Ui.Warn, 0.5f) : Ui.Line))
            {
                Stat("Garrison", need == 0 ? $"{Ui.Count(have, "regiment", "regiments")}, none needed" : $"{have} of {need} regiments needed", have < need ? Ui.Warn : Ui.Text,
                    "Below 60% support a system needs troops to stay calm: one regiment for every 10 points under 60. Too few and it revolts: its yards and mines stop.");
                if (have < need) GarrisonFix(n, need - have);
            }
            RegimentsHere(n, true, pic);
            FightersHere(n);
            CharactersAtSystem(n, true);
            SpecialForcesAtSystem(n);
        }
        else
        {
            if (pic == null) Note("Unexplored: you don't know who is on the ground here.");
            else { RegimentsHere(n, false, pic); LeadersSeenAt(pic); }
            CharactersAtSystem(n, false);
        }
        if (CanSendFrom(n)) { Add(Ui.Space(4)); Act($"Send a mission from {n}...", () => { CloseSectorView(); OpenPlanner(n); }, "Pick a team standing here (characters and special forces), the kind of mission and the target."); }
    }
}
