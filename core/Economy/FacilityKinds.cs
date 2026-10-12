using Rebellion.Core.Data;

namespace Rebellion.Core.Economy;

/// <summary>
/// Every facility type. Each takes exactly one energy slot; a mine takes one raw slot as well. Construction (later) adds
/// them; M2 only needs them as types. rule: m2-slots-and-resources §Slot cost.
/// </summary>
public enum FacilityKind
{
    Mine, Refinery,
    OrbitalShipyard, TrainingFacility, ConstructionYard,
    AdvancedShipyard, AdvancedTrainingFacility, AdvancedConstructionYard,
    Kdy150, LnrSeriesI, GenCoreLevelI, DeathStarShield, LnrSeriesII, GenCoreLevelII,
}

public static class FacilityKinds
{
    // Names exactly as in data/interim/unit-and-facility-costs.csv (rows of kind facility and defense).
    private static readonly string[] Names =
    {
        "Mine", "Refinery",
        "Orbital Shipyard", "Training Facility", "Construction Yard",
        "Advanced Shipyard", "Advanced Training Facility", "Advanced Construction Yard",
        "KDY-150", "LNR Series I", "GenCore Level I", "Death Star Shield", "LNR Series II", "GenCore Level II",
    };

    /// <summary>The class name (as in the costs file) of a facility kind.</summary>
    public static string ClassName(FacilityKind k) => Names[(int)k];

    public static bool TryParse(string className, out FacilityKind kind)
    {
        int i = Array.IndexOf(Names, className);
        kind = i < 0 ? default : (FacilityKind)i;
        return i >= 0;
    }

    public static IEnumerable<FacilityKind> All => Enum.GetValues<FacilityKind>();

    /// <summary>Construction, Training and Shipyard families including the advanced ones. rule: m2-maintenance §Rule (New) Smart rule 1.</summary>
    public static bool IsYard(FacilityKind k) => k is >= FacilityKind.OrbitalShipyard and <= FacilityKind.AdvancedConstructionYard;
}

/// <summary>The kind of thing an economy object is. rule: m2-maintenance §Load (the object families).</summary>
public enum ObjectCategory { Facility = 0, CapitalShip = 1, Fighter = 2, Regiment = 3, SpecialForce = 4 }

/// <summary>One row of unit-and-facility-costs.csv. Unverified vs original (data/interim).</summary>
public sealed record CostRow(ObjectCategory Category, string Name, int Cost, int Maintenance, int ResearchOrder);

/// <summary>
/// Refined cost and maintenance per class, from data/interim/unit-and-facility-costs.csv (Traced, interim copy of the spec
/// file). Looked up by class name; names are unique across the file.
/// </summary>
public sealed class CostData
{
    public const string FileName = "unit-and-facility-costs.csv";
    private readonly SortedDictionary<string, CostRow> _rows;

    private CostData(SortedDictionary<string, CostRow> rows) { _rows = rows; }

    public CostRow Get(string className) =>
        _rows.TryGetValue(className, out var r) ? r : throw new ArgumentException($"Unknown class '{className}'.");

    public bool TryGet(string className, out CostRow row) => _rows.TryGetValue(className, out row!);

    public static CostData Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static CostData Parse(string text, string source)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("kind", "id", "name", "cost", "maint", "rorder");
        var rows = new SortedDictionary<string, CostRow>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            string kind = t.Get(r, "kind").Trim(), name = t.Get(r, "name").Trim();
            var cat = kind switch
            {
                "capital ship" => ObjectCategory.CapitalShip,
                "fighter" => ObjectCategory.Fighter,
                "troop" => ObjectCategory.Regiment,
                "special force" => ObjectCategory.SpecialForce,
                "facility" or "defense" => ObjectCategory.Facility,
                _ => throw new DataLoadException($"{t.Where(r)}: unknown kind '{kind}'."),
            };
            int cost = t.GetInt(r, "cost"), maint = t.GetInt(r, "maint");
            if (cost < 0 || maint < 0) throw new DataLoadException($"{t.Where(r)}: '{name}' has a negative cost or maintenance.");
            if (!rows.TryAdd(name, new CostRow(cat, name, cost, maint, t.GetInt(r, "rorder"))))
                throw new DataLoadException($"{t.Where(r)}: class name '{name}' appears twice.");
        }
        return new CostData(rows);
    }
}
