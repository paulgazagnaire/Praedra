using Praedra.Core.Clock;
using Praedra.Core.Data;
using Praedra.Core.Economy;
using Praedra.Core.Travel;

namespace Praedra.Core.Construction;

/// <summary>What happens to a delivery whose destination is no longer controlled by the ordering side. rule: m2-construction §Lead review note, C20.</summary>
public enum LostDestinationRule
{
    /// <summary>DEFAULT (lead review note 2026-10-12, task brief): the delivery is held at the last system before the destination (or at the destination if already there) until the side controls it again. It never lands on an enemy-held or neutral system.</summary>
    Pause = 0,
    /// <summary>The sheet body (C20): a facility is refused on arrival (scrapped, half refund); ships, squadrons, regiments and special forces appear anyway.</summary>
    Refuse = 1,
}

/// <summary>
/// Every number of the construction sheet, with its tag. Data values, not code. rule: m2-construction §Numbers.
/// Tags: Traced = read in the Classic binary or data; DEFAULT = the sheet's choice.
/// </summary>
public sealed record ConstructionParams
{
    /// <summary>Days of one yard cycle, standard yard (MANFACSD). Traced.</summary>
    public int StandardCycleDays { get; init; } = 4;
    /// <summary>Days of one yard cycle, advanced yard (MANFACSD). Traced.</summary>
    public int AdvancedCycleDays { get; init; } = 2;
    /// <summary>Refined a yard takes to start one cycle (FUN_00530820). Traced.</summary>
    public int RefinedPerCycle { get; init; } = 1;
    /// <summary>Points a finished cycle adds to the current unit. Traced.</summary>
    public int PointsPerCycle { get; init; } = 1;
    /// <summary>Delivery speed rating of units with no hyperdrive rating of their own (facilities, regiments, special forces, squadrons): GNPRTB 1. DEFAULT (Traced for GNPRTB 1).</summary>
    public int UnratedDeliverySpeed { get; init; } = TravelFormula.DefaultSpeed;
}

/// <summary>Yard families and cycle times. rule: m2-construction §Yard families.</summary>
public static class Yards
{
    /// <summary>The family a yard facility belongs to.</summary>
    public static YardFamily FamilyOf(FacilityKind k) => k switch
    {
        FacilityKind.OrbitalShipyard or FacilityKind.AdvancedShipyard => YardFamily.Shipyard,
        FacilityKind.TrainingFacility or FacilityKind.AdvancedTrainingFacility => YardFamily.Training,
        FacilityKind.ConstructionYard or FacilityKind.AdvancedConstructionYard => YardFamily.Construction,
        _ => throw new ArgumentException($"{k} is not a yard."),
    };

    public static bool IsAdvanced(FacilityKind k) =>
        k is FacilityKind.AdvancedShipyard or FacilityKind.AdvancedTrainingFacility or FacilityKind.AdvancedConstructionYard;

    /// <summary>The family that builds an object category: facilities by construction yards, ships and fighters by shipyards, regiments and special forces by training facilities.</summary>
    public static YardFamily BuiltBy(ObjectCategory c) => c switch
    {
        ObjectCategory.Facility => YardFamily.Construction,
        ObjectCategory.CapitalShip or ObjectCategory.Fighter => YardFamily.Shipyard,
        _ => YardFamily.Training,
    };
}

/// <summary>One line of the starting-facilities data list: a finished facility on a system at the start of a game.</summary>
public sealed record StartingFacility(string System, FacilityKind Kind, Side? Side);

/// <summary>The data list <c>starting-facilities</c> (planets/07, Decided): seven Orbital Shipyards. rule: m2-construction §Starting yards.</summary>
public static class StartingFacilities
{
    public const string FileName = "starting-facilities.csv";

    public static IReadOnlyList<StartingFacility> Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static IReadOnlyList<StartingFacility> Parse(string text, string source)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("system", "facility", "side");
        var list = new List<StartingFacility>();
        for (int r = 0; r < t.RowCount; r++)
        {
            string facility = t.Get(r, "facility").Trim(), side = t.Get(r, "side").Trim();
            if (!FacilityKinds.TryParse(facility, out var kind)) throw new DataLoadException($"{t.Where(r)}: unknown facility '{facility}'.");
            Side? s = side switch
            {
                "Hand" => Side.Hand,
                "Collective" => Side.Collective,
                "Neutral" => null,
                _ => throw new DataLoadException($"{t.Where(r)}: side must be Hand, Collective or Neutral, not '{side}'."),
            };
            list.Add(new StartingFacility(t.Get(r, "system").Trim(), kind, s));
        }
        return list;
    }
}
