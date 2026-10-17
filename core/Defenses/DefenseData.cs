using Rebellion.Core.Data;
using Rebellion.Core.Economy;

namespace Rebellion.Core.Defenses;

/// <summary>What a defense class does. rule: m3-planetary-defenses §The six classes (Kind column).</summary>
public enum DefenseJob { Battery, ShieldGenerator, DeathStarShield }

/// <summary>
/// One row of <c>DEFFACSD</c>. Family 34 = KDY, 35 = LNR, 36 = GenCore, 37 = Death Star Shield. Attack is the 1998 attack strength; Shield
/// is the strength subtracted from a bombardment; BombardDefense is the target's defense in a bombardment.
/// rule: m3-planetary-defenses §The six classes (Traced, matches [MANUAL] chart pp.132-133).
/// </summary>
public sealed record DefenseClass(int Id, string Name, int Family, int Cost, int Maintenance, int ResearchOrder, int ResearchDifficulty,
    int Attack, int Shield, int BombardDefense, bool EmpireOnly, DefenseJob Job);

/// <summary>
/// The defense classes and every bombardment defense value that is data: <c>defense-facilities.csv</c> (the six classes) and
/// <c>bombardment-defense.csv</c> (regiments from data/original/, read from TROOPSD by importer/; the manufacturing and production facilities
/// are DEFAULT 5 rows of data/design/bombardment-defense.csv, merged: MANFACSD/PROFACSD words 12 and 13 are not named yet, B2).
/// rule: m3-planetary-defenses §The six classes ("Load these from the importer as a data file keyed by class id"); m3-bombardment §Resolution 4.
/// </summary>
public sealed class DefenseData
{
    public const string DefensesFile = "defense-facilities.csv";
    public const string BombardmentFile = "bombardment-defense.csv";

    private readonly SortedDictionary<string, DefenseClass> _byName;
    private readonly SortedDictionary<string, int> _regiments;
    private readonly SortedDictionary<string, int> _facilities;

    private DefenseData(SortedDictionary<string, DefenseClass> byName, SortedDictionary<string, int> regiments, SortedDictionary<string, int> facilities)
    { _byName = byName; _regiments = regiments; _facilities = facilities; }

    /// <summary>The six classes in id order.</summary>
    public IEnumerable<DefenseClass> Classes => _byName.Values.OrderBy(c => c.Id);

    public bool TryGetClass(string className, out DefenseClass c) => _byName.TryGetValue(className, out c!);
    public bool IsDefense(string className) => _byName.ContainsKey(className);
    public DefenseClass Get(string className) =>
        _byName.TryGetValue(className, out var c) ? c : throw new ArgumentException($"Unknown defense class '{className}'.");

    /// <summary>Bombardment defense of a regiment class (TROOPSD). rule: m3-bombardment §Resolution 4.</summary>
    public int RegimentDefense(string className) =>
        _regiments.TryGetValue(className, out var d) ? d : throw new ArgumentException($"No bombardment defense for regiment class '{className}'.");

    /// <summary>Bombardment defense of a facility class: the defense row for a defense class, else the manufacturing and production value. rule: m3-bombardment §Resolution 4.</summary>
    public int FacilityDefense(string className)
    {
        if (_byName.TryGetValue(className, out var c)) return c.BombardDefense;
        return _facilities.TryGetValue(className, out var d) ? d : throw new ArgumentException($"No bombardment defense for facility class '{className}'.");
    }

    /// <summary>defense-facilities.csv from data/original; bombardment-defense.csv from data/original merged with data/design. rule: B1-own-importer §Work 3, 5.</summary>
    public static DefenseData Load(string originalDir, string designDir) =>
        Parse(CsvTable.Load(Path.Combine(originalDir, DefensesFile)), CsvTable.LoadMerged(originalDir, designDir, BombardmentFile));

    public static DefenseData Parse(string defensesText, string defensesSource, string bombardText, string bombardSource) =>
        Parse(CsvTable.Parse(defensesText, defensesSource), CsvTable.Parse(bombardText, bombardSource));

    public static DefenseData Parse(CsvTable t, CsvTable b)
    {
        t.Require("id", "name", "family", "cost", "maint", "research_order", "research_difficulty", "attack", "shield", "bombard_defense", "side", "kind");
        var byName = new SortedDictionary<string, DefenseClass>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            string name = t.Get(r, "name").Trim(), side = t.Get(r, "side").Trim(), kind = t.Get(r, "kind").Trim();
            var job = kind switch
            {
                "battery" => DefenseJob.Battery,
                "shield generator" => DefenseJob.ShieldGenerator,
                "death star shield" => DefenseJob.DeathStarShield,
                _ => throw new DataLoadException($"{t.Where(r)}: unknown defense kind '{kind}'."),
            };
            bool empireOnly = side switch
            {
                "both" => false, "empire" => true,
                _ => throw new DataLoadException($"{t.Where(r)}: unknown side '{side}'."),
            };
            if (!FacilityKinds.TryParse(name, out _)) throw new DataLoadException($"{t.Where(r)}: '{name}' is not a facility class.");
            int attack = t.GetInt(r, "attack"), shield = t.GetInt(r, "shield"), bd = t.GetInt(r, "bombard_defense");
            if (attack < 0 || shield < 0 || bd < 0) throw new DataLoadException($"{t.Where(r)}: '{name}' has a negative value.");
            var row = new DefenseClass(t.GetInt(r, "id"), name, t.GetInt(r, "family"), t.GetInt(r, "cost"), t.GetInt(r, "maint"),
                t.GetInt(r, "research_order"), t.GetInt(r, "research_difficulty"), attack, shield, bd, empireOnly, job);
            if (!byName.TryAdd(name, row)) throw new DataLoadException($"{t.Where(r)}: defense class '{name}' appears twice.");
        }

        b.Require("kind", "name", "bombard_defense");
        var regiments = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var facilities = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < b.RowCount; r++)
        {
            string kind = b.Get(r, "kind").Trim(), name = b.Get(r, "name").Trim();
            int d = b.GetInt(r, "bombard_defense");
            if (d < 0) throw new DataLoadException($"{b.Where(r)}: '{name}' has a negative bombardment defense.");
            var target = kind switch
            {
                "troop" => regiments, "facility" => facilities,
                _ => throw new DataLoadException($"{b.Where(r)}: unknown kind '{kind}'."),
            };
            if (!target.TryAdd(name, d)) throw new DataLoadException($"{b.Where(r)}: '{name}' appears twice.");
        }
        return new DefenseData(byName, regiments, facilities);
    }
}
