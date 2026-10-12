using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Support;

namespace Rebellion.Core.Setup;

/// <summary>One side's numbers on one day, read from the game state and the event list (nothing is changed, nothing drawn).</summary>
public sealed record SideStats(
    Side Side, int Day, int Systems, int Raw, int Refined, int Capacity, int Load, int Free,
    int Mines, int Refineries, int Yards, int OtherFacilities, int Ships, int Fighters, int Regiments,
    int UprisingsActive, int ItemsBuilt, int ObjectsScrapped)
{
    public int Facilities => Mines + Refineries + Yards + OtherFacilities;

    public static SideStats Of(GameHost host, Side side)
    {
        var eco = host.Economy; var game = host.Game;
        int mines = 0, refs = 0, yards = 0, other = 0, ships = 0, fighters = 0, regiments = 0;
        foreach (var o in eco.Objects)
        {
            if (!o.Completed || eco.OwnerOf(o) != side) continue;
            switch (o.Category)
            {
                case ObjectCategory.Facility:
                    if (o.Class == FacilityKinds.ClassName(FacilityKind.Mine)) mines++;
                    else if (o.Class == FacilityKinds.ClassName(FacilityKind.Refinery)) refs++;
                    else if (FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)) yards++;
                    else other++;
                    break;
                case ObjectCategory.CapitalShip: ships++; break;
                case ObjectCategory.Fighter: fighters++; break;
                case ObjectCategory.Regiment: regiments++; break;
            }
        }
        int systems = game.State.SystemOwners.Count(kv => kv.Value == side);
        int uprisings = game.State.Support.Systems.Count(kv => kv.Value.Uprising && game.State.SystemOwners.TryGetValue(kv.Key, out var h) && h == side);
        int built = game.Events.OfType<UnitCompleted>().Count(e => e.Side == side);
        int scrapped = game.Events.OfType<ObjectScrapped>().Count(e => e.Side == side);
        return new SideStats(side, game.Day, systems, eco.Raw(side), eco.Refined(side), eco.Capacity(side), eco.Load(side), eco.Free(side),
            mines, refs, yards, other, ships, fighters, regiments, uprisings, built, scrapped);
    }

    public override string ToString() =>
        $"day {Day,4} {Side,-8} systems {Systems,2}  raw {Raw,4}  refined {Refined,5}  maint free {Free,5} (cap {Capacity,4} load {Load,4})  " +
        $"facilities {Facilities,3} (mines {Mines}, refineries {Refineries}, yards {Yards}, other {OtherFacilities})  ships {Ships,2} fighters {Fighters,2} regiments {Regiments,3}  " +
        $"uprisings {UprisingsActive}  built {ItemsBuilt,3}  scrapped {ObjectsScrapped,3}";
}
