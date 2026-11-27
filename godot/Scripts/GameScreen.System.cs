using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Construction;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The system panel (Selected tab). One of your systems is a summary: its head, the links to its Infrastructure and Ground windows, what is
/// wrong, then its yards, fleets, leaders, troops and slots; building and moving units happen in those windows. A system you don't hold shows
/// only your side's picture of it, with the age of every part (rule: m5-fog-and-hq §N2, §N8).
/// </summary>
public partial class GameScreen
{
    void SystemPanel(int idx)
    {
        var sys = G.Systems[idx];
        string name = sys.Name;
        var eco = S.Host.Economy;
        bool mine = S.OwnerOf(name) == Me; // own holdings are the side's own state (truth)
        var (ownerKnown, owner) = S.KnownOwnerOf(name);
        var pic = S.Picture(name);
        bool sees = S.Sees(name);
        string sector = sys.IsOutsideSectors ? "Outside the sectors" : sys.Sector;

        var tags = new List<(string, Color)>();
        if (name == S.MyHqSystem) tags.Add((Me == Side.Hand ? "YOUR CAPITAL" : "YOUR HQ", Ui.Gold));
        if (name == S.EnemyHqSystem) tags.Add((Me == Side.Hand ? "COLLECTIVE HQ" : "ENEMY CAPITAL", Ui.Enemy));
        if (S.IsDestroyed(name)) tags.Add(("DESTROYED", Ui.Danger));
        else if (mine) { if (name != S.MyHqSystem) tags.Add(("YOURS", Ui.You)); } // the capital tag already says it
        else if (pic == null) tags.Add(("UNEXPLORED", Ui.Text3));
        else if (!ownerKnown) tags.Add(("OWNER ?", Ui.Text3));
        else if (owner == null) tags.Add(("NOBODY'S", Ui.Neutral));
        else tags.Add(("ENEMY", Ui.Enemy));
        // the Ground window's head (Paul 2026-11-24: "the Selected tab still uses the old layout inside the new frame -> it should be updated"):
        // the sector in small capitals, the name in the pixel capitals with its tag, then one line of what matters
        string sub = $"{RouteWords(idx)}.";
        if (mine)
        {
            int have0 = S.Host.Support.RegimentCount(name), need0 = S.Host.Support.RequiredGarrison(name);
            sub = $"Held by you · Loyalty {eco.SupportOf(name, Me)}% · {Ui.Count(have0, "regiment", "regiments")}{(need0 == 0 ? "" : $" of {need0} needed")}";
        }
        SystemHead(sector, name, sub, tags.ToArray());
        if (name == S.EnemyHqSystem) Line(Me == Side.Hand ? "The Collective headquarters: take it to win (with the two leaders, see Goal)." : "The Hand's capital: take it to win (with the two leaders, see Goal).", Ui.Enemy, Ui.Small);

        // ---- destroyed by the harvester: nothing is left to see, build, invade or target. rule: m5-harvester §2 effect 5
        if (S.IsDestroyed(name)) { DestroyedPanel(name); return; }

        // ---- fog of war: anything not yours shows only the side's picture, with its age. rule: m5-fog-and-hq §N2, §N8
        if (!mine && pic == null)
        {
            using (Card(Look.Bg, Look.Outline, 8))
            {
                Line("Unexplored. You know nothing about this system yet: who holds it, what is built there or who is there.", Look.Text, 13);
                Note("A fleet there shows who holds it and the fleets in orbit; a Reconnaissance mission shows its buildings too, an Espionage mission its troops as well. Systems you hold are seen in full; the rest show what you saw last.");
            }
            CharactersAtSystem(name, false);
            FleetsHere(name, sees, null);
            return;
        }
        if (!mine) { ForeignSystem(name, sees, ownerKnown, owner, pic); return; }

        // ---- your system at a glance (Paul 2026-11-24, A: the Selected tab sums the system up; building happens in its Infrastructure
        // window and units move in its Ground window, so the windows are one click away and nothing here repeats them)
        SystemLinks(name, true);
        int sup = eco.SupportOf(name, Me);
        int have = S.Host.Support.RegimentCount(name), need = S.Host.Support.RequiredGarrison(name);
        var enemy = S.VisibleEnemyFleetsAt(name);
        bool uprising = S.Host.Support.IsUprising(name);
        bool blockaded = S.Game.State.Blockades.TryGetValue(name, out var blockader);
        if (have < need || uprising || blockaded || enemy.Count > 0 || sup < 40)
            using (Card(Look.Bg, have < need && !uprising && enemy.Count == 0 && !blockaded ? new Color(Ui.Warn, 0.6f) : new Color(Ui.Danger, 0.7f), 8))
            {
                if (uprising) Line("Uprising: the people are in revolt. Yards and mines stop until it ends. Station more regiments, or send a leader on Subdue Uprising.", Bad, 13);
                if (blockaded) Line(blockader == Me ? "Blockaded by you: mines and yards here stop." : $"Blockaded by the {S.NameOf(blockader)}: mines and yards here stop until their fleet is driven off.", Bad, 13);
                if (enemy.Count > 0) Line($"Enemy fleet here now: {string.Join("; ", enemy)}", Bad, 13);
                if (sup < 40) Line($"Loyalty {sup}%: the people lean to the {S.NameOf(S.Enemy)}. Low loyalty needs more troops to keep order.", Bad, 13);
                if (have < need)
                {
                    Line($"Short of troops: {have} of {need} regiments. Below 60% loyalty a system needs one regiment for every 10 points under 60, or it revolts.", Ui.Warn, 13);
                    GarrisonFix(name, need - have);
                }
            }

        // what you act on first comes first (review 21): the yards, the fleets, the leaders and their missions; then the troops in one
        // line, and the slots, which are planning facts the Infrastructure window opens on
        YardsHere(name);
        FleetsHere(name, true, pic);
        CharactersAtSystem(name, true);
        SpecialForcesAtSystem(name);
        if (CanSendFrom(name)) Act($"Send a mission from {name}...", () => OpenPlanner(name), "Pick a team standing here (characters and special forces), the kind of mission and the target.");
        GroundHere(name);
        SlotsHere(name);
        // the Hearth: the Collective can move it (rule: m5-fog-and-hq §F6)
        if (Me == Side.Collective && name == S.MyHqSystem) HqSection(name);
    }

    /// <summary>The way to the system's two windows, as the Infrastructure window's own links: outlined, its picture and its name.</summary>
    void SystemLinks(string name, bool mine)
    {
        var h = Boxed(new HBoxContainer(), 8);
        if (mine) h.AddChild(OutlinedButton(PixelIconData.Tile["buildings"], "Infrastructure", $"Infrastructure at {name}", () => OpenMgmt(MgKind.Infra, name),
            $"The Infrastructure window of {name}: its buildings, its slots, and what its yards build."));
        h.AddChild(OutlinedButton(PixelIconData.Tile["troops"], "Ground", $"Ground at {name}", () => OpenMgmt(MgKind.Ground, name),
            $"The Ground window of {name}: leaders, troops and fighters on the planet, and the fleets in orbit. Drag them from window to window."));
        Add(h);
    }

    /// <summary>
    /// Each yard here on one row, as the Infrastructure window's yard cells say it: its picture on the dark tile, its name, and "Idle",
    /// "Building: X, 4 days left" or "Ordered: X" with a 2 px bar. A click opens the Infrastructure window on that yard. Under them, only
    /// the buildings that are not yards (the rows already name those).
    /// </summary>
    void YardsHere(string name)
    {
        var fams = YardFamiliesAt(name).ToList();
        var eco = S.Host.Economy;
        int built = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.System == name && o.Completed);
        Heading("Yards");
        if (fams.Count == 0) { Lead("No yard here. Its Infrastructure window can still order buildings from a planet that has a construction yard."); return; }
        var idle = IdleYardFamilies(name);
        foreach (var fam in fams)
        {
            var p = S.Host.Construction.ProjectAt(name, fam);
            var queued = QueuedBuilds(name, fam);
            string state, right = "";
            Color ink = Look.Text;
            float share = 0;
            if (p is { Units.Count: > 0 })
            {
                string cur = eco.Find(p.Units[0])?.Class ?? "?";
                int cost = CostOf(cur), left = BuildDaysAt(name, fam, cost - p.Progress);
                state = $"Building: {cur}";
                right = left <= 0 ? "done next cycle" : $"{left} {Plural(left, "day", "days")} left";
                share = cost <= 0 ? 0 : (float)p.Progress / cost;
            }
            else if (queued.Count > 0) { state = "Ordered: " + string.Join(", ", queued.Select(o => o.Count > 1 ? $"{o.Count} {o.Class}" : o.Class)); right = "next day"; }
            else { state = idle.Contains(fam) ? "Idle" : "Nothing to build"; ink = Ui.Warn; }
            var tab = fam switch { YardFamily.Shipyard => InfraTab.Shipyard, YardFamily.Training => InfraTab.Training, _ => InfraTab.Construction };
            var kind = fam switch { YardFamily.Shipyard => UnitIcon.Kind.Shipyard, YardFamily.Training => UnitIcon.Kind.Training, _ => UnitIcon.Kind.Construction };
            var f = fam;
            Add(PictureRow(IconPic.Of(PixelIcons.Building(kind) ?? PixelIcons.Building(UnitIcon.Kind.Building), 2, Look.Text), FamilyName(fam), state, ink, right, share,
                $"Selected yard {name} {FamilyName(fam)}", () => OpenInfraOn(name, tab, f),
                $"Opens the Infrastructure window of {name} on its {FamilyName(fam).ToLowerInvariant()}: what it can build, and its work."));
        }
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == name).ToList();
        var other = here.Where(o => o.Completed && !(FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k))).Select(o => o.Class).ToList();
        var building = here.Where(o => !o.Completed).Select(o => o.Class).ToList();
        if (other.Count > 0 || building.Count > 0)
            Lead(string.Join(" ", new[] { other.Count > 0 ? $"Other buildings: {Tally(other)}." : null, building.Count > 0 ? $"Under construction: {Tally(building)}." : null }.Where(x => x != null)));
    }

    /// <summary>Opens the Infrastructure window of a system on one of its cells (a yard).</summary>
    void OpenInfraOn(string name, InfraTab tab, YardFamily fam)
    {
        _buildSection = SectionOfFamily(fam);   // a new window opens on the yard last opened
        OpenMgmt(MgKind.Infra, name);
        if (_mgWins.FirstOrDefault(x => x.Kind == MgKind.Infra && x.System == name) is { } w && w.Shows != tab) { w.Shows = tab; RebuildMg(w); }
    }

    /// <summary>The two slot gauges of the Infrastructure window, side by side (one under the other when the page is too narrow).</summary>
    void SlotsHere(string name)
    {
        var eco = S.Host.Economy;
        var slots = eco.SlotsOf(name);
        var built = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == name && o.Completed).ToList();
        int eBuilt = built.Count, eUsed = eco.EnergyUsed(name);
        int rBuilt = built.Count(o => FacilityKinds.TryParse(o.Class, out var k) && k == FacilityKind.Mine), rUsed = eco.RawUsed(name), rTotal = eco.EffectiveRaw(name);
        Heading("Slots");
        var v = new HFlowContainer();
        v.AddThemeConstantOverride("h_separation", 32);
        v.AddThemeConstantOverride("v_separation", 16);
        v.AddChild(Gauge("System energy", eBuilt, eUsed, slots.Energy, Look.Accent, Look.AccentDeep,
            $"System energy: {eUsed} of {slots.Energy} slots taken, {Math.Max(0, slots.Energy - eUsed)} free. Every building takes one energy slot.\nBright: built. Mid: ordered or being built. Dark: free."));
        v.AddChild(Gauge("Raw material", rBuilt, rUsed, rTotal, Look.Raw, Look.RawDeep,
            $"Raw material: {rUsed} of {rTotal} raw slots taken by mines, {Math.Max(0, rTotal - rUsed)} free. A mine needs a raw slot as well as an energy slot."));
        Add(v);
    }

    /// <summary>The troops on the planet in one line each (review 21: the head already counts the regiments), and the fighters based here with
    /// their order. Moving and scrapping units is done in the Ground window.</summary>
    void GroundHere(string name)
    {
        var regs = S.Host.Troops.RegimentsOnSystem(name);
        var mineRegs = regs.Where(r => r.Side == Me).ToList();
        if ((_into ?? _pv).GetChildCount() > 0) Add(Ui.Space(WinGap - Ui.Block * 2));   // a part of its own, as under a title
        Tip(Line($"Regiments: {(mineRegs.Count == 0 ? "none" : Tally(mineRegs.Select(r => r.Class)))}", Look.Text, 13),
            "Your regiments here keep order and defend the planet. Train them in the Infrastructure window; move them by drag, or scrap one with a right-click, in the Ground window.");
        foreach (var g in regs.Where(r => r.Side != Me).GroupBy(r => r.Side).OrderBy(g => g.Key))
            Line($"{(g.Key == null ? "Neutral" : S.You(g.Key.Value))} regiments: {Tally(g.Select(r => r.Class))}", Ui.Text2, 13);
        var based = FightersBasedAt(name);
        if (based.Count == 0) return;
        Tip(Line($"Fighters based here: {Tally(based.Select(o => o.Class))}", Look.Text, 13),
            "Squadrons on the planet, not aboard a ship. A fleet with hangar room can load them; those with a hyperdrive can also fly on their own.");
        // the same rating the core flies them at: a damaged hyperdrive can be 0 (rule: m3-ships-and-fighters §Hyperdrive and speed)
        var mobile = based.Where(o => Fm.ClassOf(o) != null && Fm.SpeedOfShip(o) > 0).Select(o => o.Id).ToList();
        if (mobile.Count > 0)
            Act($"Fly {Ui.Count(mobile.Count, "squadron", "squadrons")} to...", () => StartFighterMove(name, mobile),
                "They fly as one group at the speed of the slowest, then land and stay based where they arrive." + (mobile.Count < based.Count ? " The others have no working hyperdrive: a carrier must take them." : ""));
    }

    /// <summary>"Pacifiers 13 · Tranches 1": each name, then how many, as the part titles say it (review 21: "13 x Pacifiers, Tranches."
    /// read as 13 of each).</summary>
    static string Tally(IEnumerable<string> names) =>
        string.Join(" · ", names.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

    /// <summary>A line that explains itself on hover.</summary>
    static Label Tip(Label l, string tip)
    {
        l.TooltipText = Ui.Wrap(tip); l.MouseFilter = MouseFilterEnum.Stop; l.MouseDefaultCursorShape = CursorShape.Help;
        return l;
    }

    /// <summary>Too few troops: one click trains the missing regiments here, when the planet has a training facility.</summary>
    void GarrisonFix(string system, int missing)
    {
        if (!HasYard(system, YardFamily.Training)) { Note("No training facility here: bring regiments by fleet (Load troops elsewhere, then Unload all troops here)."); return; }
        var row = CheapestRegiment();
        if (row == null) return;
        Act($"Train {missing} x {row.Name} here", () => SubmitBuild(system, YardFamily.Training, row, missing),
            $"Costs {row.Cost} refined each and {row.Maintenance} upkeep each. They join the garrison when trained.", false, "WarnButton");
    }

    CostRow CheapestRegiment() =>
        S.Host.Economy.Costs.Rows.Where(r => r.Category == ObjectCategory.Regiment && (Me == Side.Hand ? r.Hand : r.Collective) && r.Cost > 0 && S.Host.Construction.IsResearched(Me, r.Name))
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>A system you don't hold: your picture of it, every part with its age, and how to take it.</summary>
    void ForeignSystem(string name, bool sees, bool ownerKnown, Side? owner, Praedra.Core.Fog.SystemPicture pic)
    {
        int? last = S.LastSeenDay(name);
        // the age of the picture decides how much to trust it: live (green), recent (plain), old (yellow)
        int age = last is int l0 ? S.Game.Day - l0 : int.MaxValue;
        bool old = !sees && age > 10;
        SystemLinks(name, false);
        // how fresh your picture is, in the windows' look: the dark surface, a line in the colour of its age (live, recent, old)
        using (Card(Look.Bg, sees ? new Color(Ui.Good, 0.6f) : old ? new Color(Ui.Warn, 0.6f) : Look.Outline, 8))
            Line(sees ? "Seen live now (you have a fleet here)."
                : last is int ld ? $"Last seen day {ld} ({Ui.DaysAgo(age)}). Fleets there are not seen: it may have changed since." : "Not seen since the start of the war.",
                sees ? Ui.Good : old ? Ui.Warn : Look.Text, 13);
        Line(!ownerKnown ? "Owner: unknown" : owner == null ? "Owner: nobody (neutral)" : $"Owner: {S.You(owner.Value)}", !ownerKnown || owner == null ? Ui.Text2 : Ui.Enemy);
        if (pic.Owner is { } po)
        {
            int s = Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport;
            Stat("Loyalty to you", $"{s}%  (seen day {po.Stamp})", s >= 50 ? Ui.You : Ui.Text2, $"What the people thought of you when you last looked. A {DiplomacyWord} mission raises it.");
        }
        if (sees)
        {
            if (S.Host.Support.IsUprising(name)) Line("Uprising: the people here are in revolt.", Bad);
            if (S.Game.State.Blockades.TryGetValue(name, out var blockader)) Line(blockader == Me ? "Blockaded by you: mines and yards here stop." : $"Blockaded by the {S.NameOf(blockader)}.", Bad);
        }
        if (pic.Status is { } st)
            Stat("Slots", $"{st.Value.Energy} energy, {st.Value.Raw} raw{(st.Value.Populated ? "" : " (unpopulated)")}", Ui.Text2);

        Heading("Buildings");
        if (pic.Facilities is { } fac)
            Line((fac.Value.Count == 0 ? "none" : string.Join(", ", fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))) + $"   (seen day {fac.Stamp})", Ui.Text2);
        else Line("never seen (a Reconnaissance or Espionage mission shows them)", Dim);
        if (pic.HarvesterQueued is { Value: true } hq) Line($"A harvester was being built here (seen day {hq.Stamp}).", Bad);

        RegimentsHere(name, false, pic);
        LeadersSeenAt(pic);
        CharactersAtSystem(name, false);
        if (CanSendFrom(name)) Act($"Send a mission from {name}...", () => OpenPlanner(name), "Pick a team standing here (characters and special forces), the kind of mission and the target.");
        FleetsHere(name, sees, pic);

        Heading("How to take it");
        if (S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == name))
            Note(owner != null ? "Bombard it, then invade with troops aboard your fleet: both are in the fleet's Orders tab (select your fleet here, then Attack, retreat, scrap...)."
                : "Nobody holds this planet. Invade with regiments aboard from your fleet's Orders tab (select your fleet here, then Attack, retreat, scrap...).");
        else Note(owner == null && ownerKnown ? $"Nobody holds this planet. Send a fleet with regiments aboard, then press Invade; or win the people over with {DiplomacyWord} first. You can only build on systems you own."
            : "You can only build on systems you own. To take it, send a fleet with regiments aboard, beat any fleet there, and press Invade.");
    }

    /// <summary>The enemy leaders your picture of a system shows (group D), by name, with the day you saw them.</summary>
    void LeadersSeenAt(Praedra.Core.Fog.SystemPicture pic)
    {
        if (pic?.Characters is not { } seen) return;
        var enemy = seen.Value.Where(c => c.Side != Me).ToList();
        if (enemy.Count == 0) return;
        Heading("Enemy leaders seen here");
        foreach (var c in enemy)
            Line($"{S.CharName(c.Id)} ({S.NameOf(c.Side)}){(c.Status == Praedra.Core.Characters.CharacterStatus.Free ? "" : " - " + c.Status.ToString().ToLowerInvariant())}   (seen day {seen.Stamp})", Ui.Enemy, Ui.Small);
    }

    void RegimentsHere(string name, bool mine, Praedra.Core.Fog.SystemPicture pic)
    {
        Heading("Regiments on the planet");
        if (mine)
        {
            var regs = S.Host.Troops.RegimentsOnSystem(name);
            if (regs.Count == 0) Line("none", Dim);
            foreach (var g in regs.GroupBy(r => r.Side).OrderBy(g => g.Key))
            {
                if (g.Key == Me) { ScrapRows(g, name); continue; } // yours: one row per class, each can be scrapped (story C9)
                Line($"{(g.Key == null ? "Neutral" : S.You(g.Key.Value))}: {Counts(g.Select(r => r.Class))}", Ui.Text2);
            }
        }
        else if (pic?.Ground is { } gr)
        {
            if (gr.Value.All(x => x.Total == 0)) Line($"none seen (day {gr.Stamp})", Dim);
            foreach (var g in gr.Value.Where(x => x.Total > 0))
                Line($"{S.You(g.Side)}: {string.Join(", ", g.Units.Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))}   (seen day {gr.Stamp})", Ui.Text2);
        }
        else Line("unknown", Dim);
    }

    void DestroyedPanel(string name)
    {
        using (Card(Look.Bg, new Color(Ui.Danger, 0.7f), 8))
        {
            Line(S.DestroyedWords(name), new Color("ffc2c2"), 17);
            Line("The planet and everything on it are gone for good: no buildings, troops or people. Nobody holds this system and nobody can build on it, invade it, bombard it or send missions there.", Ui.Text2, Ui.Small);
            Line("It stays on the map as a field of debris. Fleets can still pass through it and stop there.", Ui.Text2, Ui.Small);
        }
        FleetsHere(name, true, S.Picture(name));
    }

    /// <summary>Own fleets here (clickable), then the enemy fleets the side sees now, or the ones it saw last time it looked.</summary>
    void FleetsHere(string name, bool live, Praedra.Core.Fog.SystemPicture pic)
    {
        Heading("Fleets here");
        var ownHere = S.Game.State.Fleets.Values.Where(f => f.At == name && f.Side == Me).OrderBy(f => f.Id).ToList();
        foreach (var f in ownHere) FleetRow(f);
        if (live)
        {
            var enemy = S.VisibleEnemyFleetsAt(name);
            foreach (var e in enemy) Line($"Enemy fleet here now ({S.NameOf(e.Side)}): {e}", Bad);
            if (ownHere.Count == 0 && enemy.Count == 0) Line("none", Dim);
        }
        else
        {
            if (ownHere.Count == 0) Line("none of yours", Dim);
            if (pic?.Fleets is { } seen && seen.Value.Count > 0)
                foreach (var e in seen.Value) Line($"Enemy fleet, seen day {seen.Stamp} ({S.NameOf(e.Side)}): {e}", Ui.Text2);
            else Line("Enemy fleets: unknown (you have no eyes here).", Dim);
        }
    }

    // ================================================================== the Hearth (Collective)

    void HqSection(string at)
    {
        Heading("Headquarters");
        Note("Your HQ is here. If the Hand takes this system, the HQ is lost (and with the two leaders captured, you lose). The Hand does not know where it is unless its spies find it.");
        if (S.PendingHqMove is string to) { OrderLine(S.PendingHqSeq, $"Ordered: move the HQ to {to}."); return; }
        bool pinned = S.Fog.IsPinned(at);
        Act(_moveHq ? "Cancel HQ move" : "Move HQ...", () => { if (_moveHq) CancelMove(); else StartHqMove(); },
            "Then click one of your systems. The HQ travels on the lanes (it cannot be attacked on the way) and arrives on the day shown.");
        if (pinned) Line("A large enemy blockade here pins the HQ: the move will be refused until the blockade is broken.", Bad);
    }
}
