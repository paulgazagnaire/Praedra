using System.Globalization;
using Rebellion.Core.Data;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Battle;

/// <summary>The four firing arcs, in the order of <c>weapons.csv</c>. rule: m4-battle-new §1.</summary>
public enum Arc { Fore = 0, Aft = 1, Port = 2, Starboard = 3 }

/// <summary>Weapon types of the New battle. <c>Torpedo</c> is a squadron's one salvo. rule: m4-battle-new §2.</summary>
public enum WeaponKind { Turbolaser = 0, IonCannon = 1, LaserCannon = 2, Torpedo = 3 }

/// <summary>Size class of a hull (the index into the hit-chance row): fighter squadrons, then small to huge. rule: m4-battle-new §1, §2.</summary>
public enum SizeClass { Fighter = 0, Small = 1, Medium = 2, Large = 3, Huge = 4 }

/// <summary>
/// One battery: all guns of one weapon type in one arc of one ship (one row of <c>weapons.csv</c>). A squadron's lasers and ions are batteries with
/// <c>Guns</c> = 12 (its craft); its torpedoes are not a battery (see <see cref="CombatClass.Torpedoes"/>). rule: m4-battle-new §1.
/// </summary>
public sealed record Battery(Arc Arc, WeaponKind Weapon, int Guns, double ShotDamage, int Reload, int Range);

/// <summary>
/// The combat data of one ship or squadron class: <c>combat-ship-stats.csv</c> columns plus its batteries from <c>weapons.csv</c> (file order).
/// Built once at load, never changed. rule: m4-battle-new §1.
/// </summary>
public sealed class CombatClass
{
    public required string Name { get; init; }
    public required int Id { get; init; }
    public required ShipSide Side { get; init; }
    /// <summary>Position in the table (ascending id); the index of the battle's caches.</summary>
    public required int Index { get; init; }
    /// <summary>Class cost: the weight of CombatStrength. rule: m4-battle-new §12.</summary>
    public required int Cost { get; init; }
    public required int Hull { get; init; }
    public required int ShieldPool { get; init; }
    public required double ShieldRegen { get; init; }
    public required double Deflection { get; init; }
    public required SizeClass Size { get; init; }
    public required int Sublight { get; init; }
    /// <summary>The CSV maneuver value; use <see cref="ManeuverOrOne"/> in formulas (floor 1). rule: m4-battle-new §1.</summary>
    public required int Maneuver { get; init; }
    public required int Hyperdrive { get; init; }
    public required int FighterSquadrons { get; init; }
    public required int GravityWell { get; init; }
    public required IReadOnlyList<Battery> Batteries { get; init; }
    /// <summary>Torpedoes carried by a squadron (the <c>torpedo</c> row's guns), 0 for ships. rule: m4-battle-new §1.</summary>
    public int Torpedoes { get; init; }
    public double TorpedoDamage { get; init; }
    public int TorpedoRange { get; init; }

    public bool IsSquadron => Size == SizeClass.Fighter;
    public int ManeuverOrOne => Math.Max(1, Maneuver);
    /// <summary>The longest range of any weapon of the class (batteries and torpedoes).</summary>
    public int LongestRange
    {
        get
        {
            int r = 0;
            foreach (var b in Batteries) r = Math.Max(r, b.Range);
            if (Torpedoes > 0) r = Math.Max(r, TorpedoRange);
            return r;
        }
    }
}

/// <summary>One row of the weapon table (<c>battle-new-tables.csv</c>). rule: m4-battle-new §2.</summary>
public sealed class WeaponRow
{
    public required WeaponKind Kind { get; init; }
    public required double VsShield { get; init; }
    public required double VsHull { get; init; }
    public required double VsFighter { get; init; }
    public required bool FighterUsesManeuverRatio { get; init; }
    /// <summary>Hit chance by target <see cref="SizeClass"/> (index 0 fighter .. 4 huge).</summary>
    public required double[] Hit { get; init; }
    /// <summary><c>floor(hit * 2^32)</c>: a draw hits when it is below this. rule: m4-battle-new §Random draws (float draws are integer comparisons).</summary>
    public required uint[] Threshold { get; init; }
}

/// <summary>
/// The per-weapon data of the New battle and of CombatStrength: classes (with batteries) and the weapon table, loaded by column name.
/// Source: ships/11 (<c>weapons.csv</c>, <c>combat-ship-stats.csv</c>), m4-battle-new §2. Proposal values, unverified vs original.
/// </summary>
public sealed class NewBattleData
{
    public const string WeaponsFile = "weapons.csv", StatsFile = "combat-ship-stats.csv", TablesFile = "battle-new-tables.csv";

    private static readonly Lazy<NewBattleData> _default = new(() => Load(Path.Combine(Setup.GameData.FindRepoRoot(), "data", "design")));
    /// <summary>The repository's own data, loaded once.</summary>
    public static NewBattleData Default => _default.Value;

    private readonly SortedDictionary<string, CombatClass> _byName;
    private readonly WeaponRow[] _weapons;

    public IReadOnlyDictionary<string, CombatClass> Classes => _byName;
    /// <summary>Classes in ascending table position (index order).</summary>
    public IReadOnlyList<CombatClass> ClassList { get; }
    public WeaponRow Weapon(WeaponKind k) => _weapons[(int)k];

    private NewBattleData(SortedDictionary<string, CombatClass> byName, WeaponRow[] weapons)
    {
        _byName = byName; _weapons = weapons;
        ClassList = byName.Values.OrderBy(c => c.Index).ToList();
    }

    public CombatClass Class(string name) =>
        _byName.TryGetValue(name, out var c) ? c : throw new DataLoadException($"{StatsFile}: no combat class named '{name}'.");

    public static NewBattleData Load(string designDir) => Parse(
        File.ReadAllText(Path.Combine(designDir, StatsFile)), File.ReadAllText(Path.Combine(designDir, WeaponsFile)),
        File.ReadAllText(Path.Combine(designDir, TablesFile)));

    public static NewBattleData Parse(string statsText, string weaponsText, string tablesText)
    {
        var tables = ParseTables(CsvTable.Parse(tablesText, TablesFile));
        var st = CsvTable.Parse(statsText, StatsFile);
        st.Require("name", "id", "side", "cost", "hull", "shield_pool", "shield_regen_per_s", "shield_deflection", "size_class", "sublight",
            "maneuver", "hyperdrive", "fighter_squadrons", "gravity_well");
        var wt = CsvTable.Parse(weaponsText, WeaponsFile);
        wt.Require("name", "arc", "weapon", "guns", "shot_damage", "reload_s", "range_dat");

        var batteries = new Dictionary<string, List<Battery>>(StringComparer.Ordinal); // lookup only
        var torp = new Dictionary<string, (int Count, double Damage, int Range)>(StringComparer.Ordinal); // lookup only
        for (int r = 0; r < wt.RowCount; r++)
        {
            string name = wt.Get(r, "name").Trim();
            var weapon = ParseWeapon(wt.Get(r, "weapon").Trim(), wt.Where(r));
            int guns = wt.GetInt(r, "guns"), range = wt.GetInt(r, "range_dat");
            double dmg = Dbl(wt, r, "shot_damage");
            if (guns <= 0 || dmg <= 0 || range <= 0) throw new DataLoadException($"{wt.Where(r)}: guns, shot_damage and range_dat must be positive.");
            if (weapon == WeaponKind.Torpedo)
            {
                if (!torp.TryAdd(name, (guns, dmg, range))) throw new DataLoadException($"{wt.Where(r)}: a second torpedo row for '{name}'.");
                continue;
            }
            int reload = wt.GetInt(r, "reload_s");
            if (reload <= 0) throw new DataLoadException($"{wt.Where(r)}: reload_s must be a positive whole number of seconds.");
            if (!batteries.TryGetValue(name, out var list)) batteries[name] = list = new List<Battery>();
            list.Add(new Battery(ParseArc(wt.Get(r, "arc").Trim(), wt.Where(r)), weapon, guns, dmg, reload, range));
        }

        var byName = new SortedDictionary<string, CombatClass>(StringComparer.Ordinal);
        var rows = new List<(int Id, int Row)>();
        for (int r = 0; r < st.RowCount; r++) rows.Add((st.GetInt(r, "id"), r));
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        int index = 0;
        foreach (var (id, r) in rows)
        {
            string name = st.Get(r, "name").Trim();
            string side = st.Get(r, "side").Trim();
            var shipSide = side.Equals("Alliance", StringComparison.OrdinalIgnoreCase) ? ShipSide.Alliance
                : side.Equals("Empire", StringComparison.OrdinalIgnoreCase) ? ShipSide.Empire : ShipSide.Alliance | ShipSide.Empire;
            torp.TryGetValue(name, out var t);
            var c = new CombatClass
            {
                Name = name, Id = id, Side = shipSide, Index = index++, Cost = st.GetInt(r, "cost"), Hull = st.GetInt(r, "hull"),
                ShieldPool = st.GetInt(r, "shield_pool"), ShieldRegen = Dbl(st, r, "shield_regen_per_s"), Deflection = Dbl(st, r, "shield_deflection"),
                Size = ParseSize(st.Get(r, "size_class").Trim(), st.Where(r)), Sublight = st.GetInt(r, "sublight"), Maneuver = st.GetInt(r, "maneuver"),
                Hyperdrive = st.GetInt(r, "hyperdrive"), FighterSquadrons = st.GetInt(r, "fighter_squadrons"), GravityWell = st.GetInt(r, "gravity_well"),
                Batteries = batteries.TryGetValue(name, out var bl) ? bl : new List<Battery>(),
                Torpedoes = t.Count, TorpedoDamage = t.Damage, TorpedoRange = t.Range,
            };
            if (c.Hull <= 0) throw new DataLoadException($"{st.Where(r)}: hull must be positive.");
            if (!byName.TryAdd(name, c)) throw new DataLoadException($"{st.Where(r)}: duplicate class '{name}'.");
        }
        foreach (var n in batteries.Keys.Concat(torp.Keys).OrderBy(x => x, StringComparer.Ordinal))
            if (!byName.ContainsKey(n)) throw new DataLoadException($"{WeaponsFile}: '{n}' is not in {StatsFile}.");
        return new NewBattleData(byName, tables);
    }

    private static WeaponRow[] ParseTables(CsvTable t)
    {
        t.Require("weapon", "vs_capital_shield", "vs_capital_hull", "vs_fighter", "fighter_uses_maneuver_ratio",
            "hit_huge", "hit_large", "hit_medium", "hit_small", "hit_fighter");
        var result = new WeaponRow?[4];
        for (int r = 0; r < t.RowCount; r++)
        {
            var kind = ParseWeapon(t.Get(r, "weapon").Trim(), t.Where(r));
            if (result[(int)kind] != null) throw new DataLoadException($"{t.Where(r)}: duplicate weapon row.");
            // index by SizeClass: fighter, small, medium, large, huge
            var hit = new[] { Dbl(t, r, "hit_fighter"), Dbl(t, r, "hit_small"), Dbl(t, r, "hit_medium"), Dbl(t, r, "hit_large"), Dbl(t, r, "hit_huge") };
            var thr = new uint[5];
            for (int i = 0; i < 5; i++)
            {
                if (hit[i] < 0 || hit[i] > 1) throw new DataLoadException($"{t.Where(r)}: hit chance outside 0..1.");
                // rule: m4-battle-new §Random draws (threshold = floor(hitChance * 2^32); 1.0 always hits)
                thr[i] = hit[i] >= 1.0 ? uint.MaxValue : (uint)Math.Floor(hit[i] * 4294967296.0);
            }
            result[(int)kind] = new WeaponRow
            {
                Kind = kind, VsShield = Dbl(t, r, "vs_capital_shield"), VsHull = Dbl(t, r, "vs_capital_hull"), VsFighter = Dbl(t, r, "vs_fighter"),
                FighterUsesManeuverRatio = t.GetInt(r, "fighter_uses_maneuver_ratio") != 0, Hit = hit, Threshold = thr,
            };
        }
        for (int i = 0; i < 4; i++) if (result[i] == null) throw new DataLoadException($"{TablesFile}: no row for weapon '{(WeaponKind)i}'.");
        return result!;
    }

    private static double Dbl(CsvTable t, int r, string col)
    {
        var s = t.Get(r, col).Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : throw new DataLoadException($"{t.Where(r)}: column '{col}' must be a number, found '{s}'.");
    }

    private static WeaponKind ParseWeapon(string s, string where) => s switch
    {
        "turbolaser" => WeaponKind.Turbolaser, "ion_cannon" => WeaponKind.IonCannon, "laser_cannon" => WeaponKind.LaserCannon,
        "torpedo" => WeaponKind.Torpedo, _ => throw new DataLoadException($"{where}: unknown weapon '{s}'."),
    };

    private static Arc ParseArc(string s, string where) => s switch
    {
        "fore" => Arc.Fore, "aft" => Arc.Aft, "port" => Arc.Port, "starboard" => Arc.Starboard,
        _ => throw new DataLoadException($"{where}: unknown arc '{s}'."),
    };

    private static SizeClass ParseSize(string s, string where) => s switch
    {
        "fighter" => SizeClass.Fighter, "small" => SizeClass.Small, "medium" => SizeClass.Medium, "large" => SizeClass.Large, "huge" => SizeClass.Huge,
        _ => throw new DataLoadException($"{where}: unknown size_class '{s}'."),
    };
}
