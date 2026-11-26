using System.Collections.Generic;
using System.Linq;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;

namespace PraedraViewer;

// The stats of a class for its hover card (Paul 2026-11-19: role, stats and a picture for everything the player can build or train).
// Every number is read from the game's own class tables (data/classic, through the core), never typed here. Screens only.
public partial class GameScreen
{
    /// <summary>
    /// The stats of a class as label and value pairs, plus <paramref name="extra"/>: a sentence for what doesn't fit a pair (the missions a
    /// special force can join, what a mine or refinery adds). Ships and fighters: m3-ships-and-fighters §Class data, m4-battle-classic §4;
    /// regiments and special forces: m3-troops-and-special-forces; defences: m3-planetary-defenses; bombing defence: m3-bombardment §Resolution 4.
    /// </summary>
    internal List<(string, string)> ClassStats(CostRow r, int days, out string extra)
    {
        extra = null;
        var s = new List<(string, string)> { ("Refined cost", r.Cost.ToString()), ("Upkeep", r.Maintenance.ToString()), ("Days here", $"~{days}") };
        void Add(string label, int value) { if (value > 0) s.Add((label, value.ToString())); }
        switch (r.Category)
        {
            case ObjectCategory.CapitalShip or ObjectCategory.Fighter when Fm.ShipClasses.TryGetValue(r.Name, out var sc):
            {
                // the arc values are firepower ratings, not gun counts (m4-battle-classic §4: counts are read only through f / N)
                var a = sc.GunArcs;
                int heavy = 0, pulse = 0, light = 0;
                for (int i = 0; i < Praedra.Core.Ships.ShipArcs.Count; i++) { heavy += a[i].HeavyCannon; pulse += a[i].Ion; light += a[i].Laser; }
                if (sc.IsCapital) Add("Hull", sc.Hull);
                else Add("Craft", sc.SquadronSize);
                Add("Shields", sc.ShieldStrength);
                Add("Heavy fire", heavy);
                Add("Pulse fire", pulse);
                Add("Light fire", light);
                if (sc.IsCapital && heavy + pulse + light > 0)
                    s.Add(("Fire front, sides, rear", $"{a.Fore.Total}, {a.Port.Total + a.Starboard.Total}, {a.Aft.Total}"));
                Add("Torpedoes", sc.Torpedoes);
                Add("Fighter room", sc.FighterCapacity);
                Add("Regiment room", sc.TroopCapacity);
                Add("Bombardment", sc.Bombardment);
                Add("Hold beam", sc.HoldBeamPower);
                Add("Battle speed", sc.SubLight);
                Add("Detection", sc.Detection);
                // rule: m1-travel §Rule: the hyperdrive rating is the percent of normal travel time (bigger is slower; 0 = cannot fly alone)
                s.Add(("Trip time", sc.Hyperdrive > 0 ? $"{sc.Hyperdrive}% of normal" : "needs a carrier"));
                if (sc.GravityWellProjector > 0) extra = "Interdictor: pulls enemy fleets out of the deep where it stands.";
                break;
            }
            case ObjectCategory.Regiment when S.Host.Troops.Data.Regiment(r.Name) is { } rc:
                Add("Attack", rc.Attack);
                Add("Defence", rc.Defense);
                Add("Bombing defence", rc.BombardmentDefense);
                Add("Detection", rc.Detection);
                break;
            case ObjectCategory.SpecialForce when S.Host.Troops.Data.SpecialForce(r.Name) is { } k:
            {
                Add("Espionage", k.Espionage);
                Add("Combat", k.Combat);
                Add("Leadership", k.Leadership);
                var kinds = OfferedKinds.Where(x => Mm.Data.TryGet(x, out var rec) && rec.AvailableTo(Me) && (k.MissionBit & rec.SpecialForceMask) != 0)
                    .Select(x => GameSession.KindWords(x, Me)).ToList();
                extra = kinds.Count == 0 ? "Joins no mission." : $"Missions: {string.Join(", ", kinds)}.";
                break;
            }
            case ObjectCategory.Facility:
                if (S.Host.Defenses.Data.TryGetClass(r.Name, out var dc))
                {
                    if (dc.Job == DefenseJob.Battery) Add("Attack", dc.Attack);
                    else Add("Shield", dc.Shield);
                    Add("Bombing defence", dc.BombardDefense);
                }
                else
                {
                    try { Add("Bombing defence", S.Host.Defenses.Data.FacilityDefense(r.Name)); } catch (System.ArgumentException) { }
                    if (FacilityKinds.TryParse(r.Name, out var fk) && fk is FacilityKind.Mine or FacilityKind.Refinery)
                        extra = $"A mine and a refinery together add {S.Host.Economy.Params.CapacityPerFacility} upkeep room.";
                }
                break;
        }
        return s;
    }
}
