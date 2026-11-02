using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;
/// <summary>A system's buildings (the Infrastructure window) and, for a system you don't hold, what your side knows of its ground (the Ground
/// window). See GameScreen.Windows.cs.</summary>
public partial class GameScreen
{
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
