using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Construction;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The system panel (Selected tab). One of your systems opens with what matters most (support, garrison, problems) and splits the rest into
/// Overview, Build, Forces and Fleets so nothing needs a long scroll. A system you don't hold shows only your side's picture of it, with
/// the age of every part (rule: m5-fog-and-hq §N2, §N8).
/// </summary>
public partial class GameScreen
{
    enum SysTab { Overview, Build, Forces, Fleets }
    SysTab _sysTab = SysTab.Overview;

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
        string held = mine ? $"Held by the {S.You(Me)}, seen live." : null;
        Title(name, held != null ? $"{held} {sector}." : $"{sector}. {RouteWords(idx)}.", tags.ToArray());
        if (name == S.EnemyHqSystem) Line(Me == Side.Hand ? "The Collective headquarters: take it to win (with the two leaders, see Goal)." : "The Hand's capital: take it to win (with the two leaders, see Goal).", Ui.Enemy, Ui.Small);

        // ---- destroyed by the harvester: nothing is left to see, build, invade or target. rule: m5-harvester §2 effect 5
        if (S.IsDestroyed(name)) { DestroyedPanel(name); return; }

        // ---- fog of war: anything not yours shows only the side's picture, with its age. rule: m5-fog-and-hq §N2, §N8
        if (!mine && pic == null)
        {
            using (Card())
            {
                Line("Unexplored. You know nothing about this system yet: who holds it, what is built there or who is there.", Ui.Text2);
                Note("Send a fleet there to look, or a spy on an Espionage mission. Systems you hold or have a fleet at are seen live; the rest show what you saw last.");
            }
            CharactersAtSystem(name, false);
            FleetsHere(name, sees, null);
            return;
        }
        if (!mine) { ForeignSystem(name, sees, ownerKnown, owner, pic); return; }

        // ---- your system: the state that matters first
        int sup = eco.SupportOf(name, Me);
        int have = S.Host.Support.RegimentCount(name), need = S.Host.Support.RequiredGarrison(name);
        using (Card())
        {
            var supRow = Stat("Support for you", $"{sup}%", sup < 40 ? Ui.Enemy : sup >= 60 ? Ui.You : Ui.Text,
                $"How much the people here back you (the {S.NameOf(S.Enemy)} has {100 - sup}%). Low support needs more troops to keep order, and below a point the planet may go over to the other side.");
            Add(Ui.Bar(sup / 100f, sup < 40 ? Ui.Enemy : Ui.You, 6));
            Stat("Garrison", need == 0 ? $"{Ui.Count(have, "regiment", "regiments")}, none needed" : $"{have} of {need} regiments needed", have < need ? Ui.Warn : Ui.Text,
                "Below 60% support a system needs troops to stay calm: one regiment for every 10 points under 60. Too few and it revolts: its yards and mines stop.");
            if (have < need) GarrisonFix(name, need - have);
            if (S.Host.Support.IsUprising(name)) Line("UPRISING! The people are in revolt here: yards and mines stop until it ends. Station more regiments, or send a leader on Subdue Uprising.", Bad);
            if (S.Game.State.Blockades.TryGetValue(name, out var blockader))
                Line(blockader == Me ? "Blockaded by you: mines and yards here stop." : $"Blockaded by the {S.NameOf(blockader)}: mines and yards here stop until their fleet is driven off.", Bad);
            var enemy = S.VisibleEnemyFleetsAt(name);
            if (enemy.Count > 0) Line($"Enemy fleet here now: {string.Join("; ", enemy)}", Bad);
        }

        // ---- sub-tabs
        int ownFleets = S.Game.State.Fleets.Values.Count(f => f.At == name && f.Side == Me);
        var row = Ui.HBox(4);
        var group = new ButtonGroup();
        foreach (var (t, label) in new[] { (SysTab.Overview, "Overview"), (SysTab.Build, "Build"), (SysTab.Forces, "Forces"), (SysTab.Fleets, ownFleets > 0 ? $"Fleets here {ownFleets}" : "Fleets here") })
        {
            var tt = t;
            var b = Ui.Chip(label, () => { _sysTab = tt; _panelDirty = true; _panelTimer = 0; }, null, _sysTab == t, group);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.CustomMinimumSize = new Vector2(0, 32);
            row.AddChild(b);
        }
        Add(Ui.Space(2));
        Add(row);
        Add(Ui.Space(2));

        switch (_sysTab)
        {
            case SysTab.Build: BuildMenu(name); break;
            case SysTab.Forces:
                RegimentsHere(name, true, pic);
                CharactersAtSystem(name, true);
                SpecialForcesAtSystem(name);
                if (CanSendFrom(name)) Act($"Send a mission from {name}...", () => OpenPlanner(name), "Pick a team standing here (characters and special forces), the kind of mission and the target.");
                break;
            case SysTab.Fleets: FleetsHere(name, true, pic); break;
            default: OwnOverview(name); break;
        }
    }

    /// <summary>Overview of your system: what is being built, the facilities, the slots, and the side-only slot (move the Hearth).</summary>
    void OwnOverview(string name)
    {
        var eco = S.Host.Economy;
        Heading("Being built here");
        BuildQueues(name);
        Heading("Facilities");
        var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == name).ToList();
        Line(here.Count(o => o.Completed) == 0 ? "none" : Counts(here.Where(o => o.Completed).Select(o => o.Class)), Ui.Text2);
        var building = here.Where(o => !o.Completed).ToList();
        if (building.Count > 0) Line($"Under construction: {Counts(building.Select(o => o.Class))}", Dim, Ui.Small);
        var slots = eco.SlotsOf(name);
        Stat("Energy slots", $"{eco.EnergyUsed(name)} used of {slots.Energy}, {eco.FreeEnergy(name)} free", null, "Every facility takes one energy slot.");
        Stat("Raw material slots", $"{eco.RawUsed(name)} used of {eco.EffectiveRaw(name)}, {eco.FreeRaw(name)} free", null, "A mine also needs a raw material slot.");
        // the Hearth: the Collective can move it (rule: m5-fog-and-hq §F6)
        if (Me == Side.Collective && name == S.MyHqSystem) HqSection(name);
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
        using (Card(sees ? Ui.GoodDeep : old ? Ui.WarnDeep : Ui.Panel, sees ? new Color(Ui.Good, 0.6f) : old ? new Color(Ui.Warn, 0.6f) : Ui.Line))
            Line(sees ? "Seen live now (you have a fleet here)."
                : last is int ld ? $"Last seen day {ld} ({Ui.DaysAgo(age)}). Fleets there are not seen: it may have changed since." : "Not seen since the start of the war.",
                sees ? new Color("bff0d2") : old ? new Color("ffe7a8") : Ui.Text2);
        Line(!ownerKnown ? "Owner: unknown" : owner == null ? "Owner: nobody (neutral)" : $"Owner: {S.You(owner.Value)}", !ownerKnown || owner == null ? Ui.Text2 : Ui.Enemy);
        if (pic.Owner is { } po)
        {
            int s = Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport;
            Stat("Support for you", $"{s}%  (seen day {po.Stamp})", s >= 50 ? Ui.You : Ui.Text2, "What the people thought of you when you last looked. A Diplomacy mission raises it.");
        }
        if (sees)
        {
            if (S.Host.Support.IsUprising(name)) Line("Uprising: the people here are in revolt.", Bad);
            if (S.Game.State.Blockades.TryGetValue(name, out var blockader)) Line(blockader == Me ? "Blockaded by you: mines and yards here stop." : $"Blockaded by the {S.NameOf(blockader)}.", Bad);
        }
        if (pic.Status is { } st)
            Stat("Slots", $"{st.Value.Energy} energy, {st.Value.Raw} raw{(st.Value.Populated ? "" : " (unpopulated)")}", Ui.Text2);

        Heading("Facilities");
        if (pic.Facilities is { } fac)
            Line((fac.Value.Count == 0 ? "none" : string.Join(", ", fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))) + $"   (seen day {fac.Stamp})", Ui.Text2);
        else Line("unknown (nobody has looked at the ground yet)", Dim);
        if (pic.HarvesterQueued is { Value: true } hq) Line($"A harvester was being built here (seen day {hq.Stamp}).", Bad);

        RegimentsHere(name, false, pic);
        LeadersSeenAt(pic);
        CharactersAtSystem(name, false);
        if (CanSendFrom(name)) Act($"Send a mission from {name}...", () => OpenPlanner(name), "Pick a team standing here (characters and special forces), the kind of mission and the target.");
        FleetsHere(name, sees, pic);

        Heading("How to take it");
        if (S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == name))
            Note(owner != null ? "Bombard it, then invade with troops aboard your fleet: select your fleet here." : "Nobody holds this planet. Select your fleet here and press Invade with regiments aboard.");
        else Note(owner == null && ownerKnown ? "Nobody holds this planet. Send a fleet with regiments aboard, then press Invade; or win the people over with Diplomacy first. You can only build on systems you own."
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
                Line($"{(g.Key == null ? "Neutral" : S.You(g.Key.Value))}: {Counts(g.Select(r => r.Class))}", Ui.Text2);
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
        using (Card(Ui.DangerDeep, new Color(Ui.Danger, 0.6f)))
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
        if (S.PendingHqMove is string to) { Line($"Waiting for the next day: move the HQ to {to}.", Pending); return; }
        bool pinned = S.Fog.IsPinned(at);
        Act(_moveHq ? "Cancel HQ move" : "Move HQ...", () => { if (_moveHq) CancelMove(); else StartHqMove(); },
            "Then click one of your systems. The HQ travels on the lanes (it cannot be attacked on the way) and arrives on the day shown.");
        if (pinned) Line("A large enemy blockade here pins the HQ: the move will be refused until the blockade is broken.", Bad);
    }
}
