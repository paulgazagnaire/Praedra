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
    /// <summary>Designed, not in the 1998 game: 1 energy slot, 10 refined, maintenance 5, one per planet. rule: m5-fog-and-hq §N4; economy/15 §2 (Paul 2026-10-16).</summary>
    SensorStation,
}

public static class FacilityKinds
{
    // Names exactly as in unit-and-facility-costs.csv (data/original, plus the Sensor Station of data/design) (rows of kind facility and defense).
    private static readonly string[] Names =
    {
        "Mine", "Refinery",
        "Orbital Shipyard", "Training Facility", "Construction Yard",
        "Advanced Shipyard", "Advanced Training Facility", "Advanced Construction Yard",
        "KDY-150", "LNR Series I", "GenCore Level I", "Death Star Shield", "LNR Series II", "GenCore Level II",
        "Sensor Station",
    };

    /// <summary>The class name (as in the costs file) of a facility kind.</summary>
    public static string ClassName(FacilityKind k) => Names[(int)k];

    public static bool TryParse(string className, out FacilityKind kind)
    {
        int i = Array.IndexOf(Names, className);
        kind = i < 0 ? default : (FacilityKind)i;
        return i >= 0;
    }

    /// <summary>Facilities of which a planet may hold only one. rule: m5-fog-and-hq §N4 (economy/15 §2 "one per planet").</summary>
    public static bool OnePerPlanet(FacilityKind k) => k == FacilityKind.SensorStation;
    /// <summary>Can be placed only on a system that lies on a hyperspace route. rule: m5-fog-and-hq §N4 (Decided, Paul 2026-10-16).</summary>
    public static bool RouteOnly(FacilityKind k) => k == FacilityKind.SensorStation;

    public static IEnumerable<FacilityKind> All => Enum.GetValues<FacilityKind>();

    /// <summary>Construction, Training and Shipyard families including the advanced ones. rule: m2-maintenance §Rule (New) Smart rule 1.</summary>
    public static bool IsYard(FacilityKind k) => k is >= FacilityKind.OrbitalShipyard and <= FacilityKind.AdvancedConstructionYard;
}

/// <summary>The kind of thing an economy object is. rule: m2-maintenance §Load (the object families).</summary>
public enum ObjectCategory { Facility = 0, CapitalShip = 1, Fighter = 2, Regiment = 3, SpecialForce = 4 }

/// <summary>One row of unit-and-facility-costs.csv (data/original, read from the 1998 files by importer/; the Sensor Station row is data/design).</summary>
public sealed record CostRow(ObjectCategory Category, string Name, int Cost, int Maintenance, int ResearchOrder,
    bool Alliance = true, bool Empire = true);

/// <summary>
/// Refined cost and maintenance per class, from data/original/unit-and-facility-costs.csv (the importer's copy of the 1998 files) merged with
/// data/design/unit-and-facility-costs.csv (classes not in the game, each row with its tag). Looked up by class name; names are unique.
/// rule: B1-own-importer §Work 3.
/// </summary>
public sealed class CostData
{
    public const string FileName = "unit-and-facility-costs.csv";
    private readonly SortedDictionary<string, CostRow> _rows;

    private CostData(SortedDictionary<string, CostRow> rows) { _rows = rows; }

    public CostRow Get(string className) =>
        _rows.TryGetValue(className, out var r) ? r : throw new ArgumentException($"Unknown class '{className}'.");

    public bool TryGet(string className, out CostRow row) => _rows.TryGetValue(className, out row!);

    /// <summary>Every row, by class name (ordinal). Read-only query for the build menu of a UI.</summary>
    public IEnumerable<CostRow> Rows => _rows.Values;

    public static CostData Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>The original file merged with the design file of the same name. rule: B1-own-importer §Work 3.</summary>
    public static CostData Load(string originalDir, string designDir) => Parse(CsvTable.LoadMerged(originalDir, designDir, FileName));

    public static CostData Parse(string text, string source) => Parse(CsvTable.Parse(text, source));

    public static CostData Parse(CsvTable t)
    {
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
            if (!rows.TryAdd(name, new CostRow(cat, name, cost, maint, t.GetInt(r, "rorder"), t.GetInt(r, "alliance") != 0, t.GetInt(r, "empire") != 0)))
                throw new DataLoadException($"{t.Where(r)}: class name '{name}' appears twice.");
        }
        return new CostData(rows);
    }
}
