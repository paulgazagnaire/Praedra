using Praedra.Core.Data;

namespace Praedra.Core.Ships;

/// <summary>Laser, ion and turbolaser cannon counts of one arc. rule: m4-battle-classic §4 (counts are read only through f / N).</summary>
public readonly record struct ArcCounts(int Laser, int Ion, int Turbolaser)
{
    public int Total => Laser + Ion + Turbolaser;
}

/// <summary>The four arcs in the order the battle sheet scans them: fore, aft, port, starboard. rule: m4-battle-classic §4.</summary>
public sealed record ShipArcs(ArcCounts Fore, ArcCounts Aft, ArcCounts Port, ArcCounts Starboard)
{
    public static readonly ShipArcs None = new(default, default, default, default);
    public ArcCounts this[int i] => i switch { 0 => Fore, 1 => Aft, 2 => Port, _ => Starboard };
    public const int Count = 4;
}

[Flags]
public enum ShipSide { None = 0, Collective = 1, Hand = 2 }

/// <summary>
/// The ship-class fields the core needs so far (travel, forced stop, ship object, carrying capacity, bombardment power: <c>Bombardment</c> is a capital ship's bombardment_modifier or a squadron's bombardment value). Loaded from data/classic/ (CAPSHPSD and FIGHTSD, read by
/// importer/). rule: B1-own-importer §Work 3.
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
    int ShieldStrength = 0,
    int Bombardment = 0,
    int Maneuverability = 0,
    int WeaponRechargeRate = 0,
    int ShieldRechargeRate = 0,
    int Torpedoes = 0,
    ShipArcs? Arcs = null,
    int Detection = 0,
    int TractorBeamPower = 0,
    int SubLight = 0,
    int DamageControl = 0,
    int SquadronSize = 0,
    int LaserRange = 0,
    int IonRange = 0,
    int TurbolaserRange = 0,
    int UprisingDefense = 0)
{
    /// <summary>The loss value a destroyed unit adds to its side's tally at the system: record word 12 (uprising_defense). rule: m5-characters-and-force §Loyalty (B2c §3, 0x520b60).</summary>
    public int LossValue => UprisingDefense;

    /// <summary>The four gun arcs (a squadron has only the fore arc). Never null: a class built without arcs has none.</summary>
    public ShipArcs GunArcs => Arcs ?? ShipArcs.None;

    /// <summary>
    /// Speed rating (percent of normal time, bigger is slower; 0 = cannot move).
    /// rule: m1-travel §Rule (Classic); DEFAULT: a damaged ship with hyperdrive_if_damaged = 0 uses its normal rating.
    /// </summary>
    public int Speed(bool damaged) => damaged && HyperdriveIfDamaged > 0 ? HyperdriveIfDamaged : Hyperdrive;
}

/// <summary>Loader for data/classic/capital-ships.csv and fighters.csv (written by importer/ from the Classic files).</summary>
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
    public static IReadOnlyDictionary<string, ShipClass> LoadAll(string originalDir)
    {
        var all = new SortedDictionary<string, ShipClass>(StringComparer.Ordinal);
        foreach (var s in LoadCapitalShips(Path.Combine(originalDir, CapitalShipsFile))
                     .Concat(LoadFighters(Path.Combine(originalDir, FightersFile))))
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
        // rule: m3-bombardment §Resolution 1: a capital ship's bombardment_modifier; a squadron's value is the fighters.csv column named bombardment_defense (renamed on import).
        string bombCol = isCapital ? "bombardment_modifier" : "bombardment_defense";
        bool hasBomb = t.HasColumn(bombCol);
        // rule: m4-battle-classic §4: the combat columns (recharge rates and torpedoes only where the file has them; fighters have no recharge columns)
        bool hasTractor = t.HasColumn("tractor_beam_power"); // rule: m4-retreat-damage-repair §6 (capital-ships.csv; fighters have none: 0)
        bool hasDet = t.HasColumn("detection"); // rule: m5-missions §3 Defenders (CAPSHPSD and FIGHTSD carry detection)
        // rule: m4-battle-classic §2, §5 (B3: sub-light speed, gun ranges, squadron size); m4-retreat-damage-repair §5 (damage_control)
        bool hasSub = t.HasColumn("sub_light_engine"), hasDc = t.HasColumn("damage_control"), hasSize = t.HasColumn("squadron_size");
        bool hasUd = t.HasColumn("uprising_defense"); // rule: m5-characters-and-force §Loyalty (B2c §3: the loss value, record word 12)
        bool hasMan = t.HasColumn("maneuverability"), hasWr = t.HasColumn("weapon_recharge_rate"), hasSr = t.HasColumn("shield_recharge_rate"), hasTorp = t.HasColumn("torpedoes");
        int Cnt(int r, string col) => t.HasColumn(col) ? t.GetInt(r, col) : 0;
        ArcCounts Arc(int r, string arc) => new(Cnt(r, "laser_cannon_" + arc), Cnt(r, "ion_cannon_" + arc), Cnt(r, "turbolaser_" + arc));
        var list = new List<ShipClass>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            var name = t.Get(r, "name").Trim();
            if (name.Length == 0) throw new DataLoadException($"{t.Where(r)}: ship class name is empty.");
            if (!seen.Add(name)) throw new DataLoadException($"{t.Where(r)}: duplicate ship class '{name}'.");
            var side = (t.GetInt(r, "is_alliance") != 0 ? ShipSide.Collective : 0) | (t.GetInt(r, "is_empire") != 0 ? ShipSide.Hand : 0);
            int hd = t.GetInt(r, "hyperdrive"), hdd = t.GetInt(r, "hyperdrive_if_damaged");
            if (hd < 0 || hdd < 0) throw new DataLoadException($"{t.Where(r)}: '{name}' has a negative hyperdrive rating.");
            list.Add(new ShipClass(name, isCapital, side, hd, hdd,
                hasGw ? t.GetInt(r, "gravity_well_projector") : 0, t.GetInt(r, "overall_attack_strength"),
                t.GetInt(r, "refined_material_cost"), HeavyArcs.Any(c => t.GetInt(r, c) > 0),
                hasHull ? t.GetInt(r, "hull") : 0, hasFc ? t.GetInt(r, "fighter_capacity") : 0, hasTc ? t.GetInt(r, "troop_capacity") : 0,
                hasId ? t.GetInt(r, "id") : 0, hasShield ? t.GetInt(r, "shield_strength") : 0,
                hasBomb ? t.GetInt(r, bombCol) : 0,
                hasMan ? t.GetInt(r, "maneuverability") : 0, hasWr ? t.GetInt(r, "weapon_recharge_rate") : 0,
                hasSr ? t.GetInt(r, "shield_recharge_rate") : 0, hasTorp ? t.GetInt(r, "torpedoes") : 0,
                new ShipArcs(Arc(r, "fore"), Arc(r, "aft"), Arc(r, "port"), Arc(r, "starboard")),
                hasDet ? t.GetInt(r, "detection") : 0,
                hasTractor ? t.GetInt(r, "tractor_beam_power") : 0,
                hasSub ? t.GetInt(r, "sub_light_engine") : 0, hasDc ? t.GetInt(r, "damage_control") : 0, hasSize ? t.GetInt(r, "squadron_size") : 0,
                Cnt(r, "laser_cannon_range"), Cnt(r, "ion_cannon_range"), Cnt(r, "turbolaser_range"),
                hasUd ? t.GetInt(r, "uprising_defense") : 0));
        }
        return list;
    }
}
