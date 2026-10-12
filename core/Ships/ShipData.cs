using Rebellion.Core.Data;

namespace Rebellion.Core.Ships;

[Flags]
public enum ShipSide { None = 0, Alliance = 1, Empire = 2 }

/// <summary>
/// The ship-class fields the core needs so far (travel, forced stop, ship object, carrying capacity). NOTE: loaded from data/interim/ (copies of the spec CSVs, ships/data/*.csv);
/// unverified vs original until the importer produces data/original/.
/// </summary>
public sealed record ShipClass(
    string Name,
    bool IsCapital,
    ShipSide Side,
    int Hyperdrive,
    int HyperdriveIfDamaged,
    int GravityWellProjector,
    int OverallAttackStrength,
    int RefinedMaterialCost,
    bool HasHeavyWeapon,
    int Hull = 0,
    int FighterCapacity = 0,
    int TroopCapacity = 0,
    int Id = 0,
    int ShieldStrength = 0)
{
    /// <summary>
    /// Speed rating (percent of normal time, bigger is slower; 0 = cannot move).
    /// rule: m1-travel §Rule (Classic); DEFAULT: a damaged ship with hyperdrive_if_damaged = 0 uses its normal rating.
    /// </summary>
    public int Speed(bool damaged) => damaged && HyperdriveIfDamaged > 0 ? HyperdriveIfDamaged : Hyperdrive;
}

/// <summary>Loader for the interim capital-ships.csv and fighters.csv (unverified vs original).</summary>
public static class ShipData
{
    public const string CapitalShipsFile = "capital-ships.csv";
    public const string FightersFile = "fighters.csv";

    // Turbolaser and ion cannon arcs (laser cannons are not heavy weapons). rule: space-combat/07 last section.
    private static readonly string[] HeavyArcs =
    {
        "turbolaser_fore", "turbolaser_aft", "turbolaser_port", "turbolaser_starboard",
        "ion_cannon_fore", "ion_cannon_aft", "ion_cannon_port", "ion_cannon_starboard",
    };

    public static IReadOnlyList<ShipClass> LoadCapitalShips(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileName(path), isCapital: true);

    public static IReadOnlyList<ShipClass> LoadFighters(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileName(path), isCapital: false);

    /// <summary>Capital ships and squadrons together, sorted by ordinal name.</summary>
    public static IReadOnlyDictionary<string, ShipClass> LoadAll(string interimDir)
    {
        var all = new SortedDictionary<string, ShipClass>(StringComparer.Ordinal);
        foreach (var s in LoadCapitalShips(Path.Combine(interimDir, CapitalShipsFile))
                     .Concat(LoadFighters(Path.Combine(interimDir, FightersFile))))
            if (!all.TryAdd(s.Name, s)) throw new DataLoadException($"Ship class '{s.Name}' appears in both ship files.");
        return all;
    }

    public static IReadOnlyList<ShipClass> Parse(string text, string source, bool isCapital)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("name", "is_alliance", "is_empire", "hyperdrive", "hyperdrive_if_damaged", "overall_attack_strength", "refined_material_cost");
        t.Require(HeavyArcs);
        bool hasGw = t.HasColumn("gravity_well_projector"); // fighters.csv has none: 0
        // rule: m3-ships-and-fighters §Class data: hull, fighter_capacity and troop_capacity are ship columns; fighters have none (0).
        bool hasHull = t.HasColumn("hull"), hasFc = t.HasColumn("fighter_capacity"), hasTc = t.HasColumn("troop_capacity");
        bool hasId = t.HasColumn("id"), hasShield = t.HasColumn("shield_strength");
        var list = new List<ShipClass>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            var name = t.Get(r, "name").Trim();
            if (name.Length == 0) throw new DataLoadException($"{t.Where(r)}: ship class name is empty.");
            if (!seen.Add(name)) throw new DataLoadException($"{t.Where(r)}: duplicate ship class '{name}'.");
            var side = (t.GetInt(r, "is_alliance") != 0 ? ShipSide.Alliance : 0) | (t.GetInt(r, "is_empire") != 0 ? ShipSide.Empire : 0);
            int hd = t.GetInt(r, "hyperdrive"), hdd = t.GetInt(r, "hyperdrive_if_damaged");
            if (hd < 0 || hdd < 0) throw new DataLoadException($"{t.Where(r)}: '{name}' has a negative hyperdrive rating.");
            list.Add(new ShipClass(name, isCapital, side, hd, hdd,
                hasGw ? t.GetInt(r, "gravity_well_projector") : 0, t.GetInt(r, "overall_attack_strength"),
                t.GetInt(r, "refined_material_cost"), HeavyArcs.Any(c => t.GetInt(r, c) > 0),
                hasHull ? t.GetInt(r, "hull") : 0, hasFc ? t.GetInt(r, "fighter_capacity") : 0, hasTc ? t.GetInt(r, "troop_capacity") : 0,
                hasId ? t.GetInt(r, "id") : 0, hasShield ? t.GetInt(r, "shield_strength") : 0));
        }
        return list;
    }
}
