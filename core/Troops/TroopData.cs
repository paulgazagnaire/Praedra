using System.Globalization;
using Praedra.Core.Clock;
using Praedra.Core.Data;

namespace Praedra.Core.Troops;

/// <summary>
/// One regiment class (a TROOPSD row). Every column is loaded; the M3 troop rules use side, cost, maintenance and research (through the
/// construction module's cost file) and, from the ground-war sheets, attack, defense and bombardment_defense. Detection is M5;
/// uprising_defense is the loss value of a destroyed regiment (record word 12; m5-characters-and-talent §Loyalty, B2c §3). A regiment has no strength: it survives or dies. rule: m3-troops-and-special-forces §Regiment classes as data.
/// </summary>
public sealed record RegimentClass(
    int Id, string Name, Side Side, int ProductionFamily, int Cost, int Maintenance, int ResearchOrder, int ResearchDifficulty,
    int Attack, int Defense, int BombardmentDefense, int Detection, int UprisingDefense);

/// <summary>
/// One special-force class (a SPECFCSD row). The M3 engine loads every column and uses cost, maintenance and side only; skills and the
/// mission bit are for the M5 missions. rule: m3-troops-and-special-forces §Special force classes as data.
/// </summary>
public sealed record SpecialForceClass(
    int Id, string Name, Side Side, int ProductionFamily, int Cost, int Maintenance, int Espionage, int Combat, int Leadership,
    int EspionageVariance, int CombatVariance, int LeadershipVariance, int LoyaltyBase, int MissionBit, int ResearchOrder);

/// <summary>
/// The troop and special-force class tables, loaded from <c>data/classic/troop-regiments.csv</c> and <c>special-forces.csv</c> (values copied
/// from units/01 until the importer produces data/classic/; unverified vs Classic), plus the names table <c>data/design/unit-names.csv</c>
/// (the renames: names only, ids and stats keep the original). Classes are keyed by the class name used in the registry
/// (<see cref="Economy.EconObject.Class"/>), ordinal, and also listed by id. rule: m3-troops-and-special-forces §Special force classes as data (Renames).
/// </summary>
public sealed class TroopData
{
    public const string RegimentsFile = "troop-regiments.csv", SpecialForcesFile = "special-forces.csv", NamesFile = "unit-names.csv";

    /// <summary>Production family of the Training Facility (0x29 = 41). Traced.</summary>
    public const int TrainingFamily = 41;

    private readonly SortedDictionary<string, RegimentClass> _regByName;
    private readonly SortedDictionary<string, SpecialForceClass> _sfByName;
    private readonly SortedDictionary<(string Kind, int Id), string> _display;

    public IReadOnlyList<RegimentClass> Regiments { get; }
    public IReadOnlyList<SpecialForceClass> SpecialForces { get; }

    private TroopData(IReadOnlyList<RegimentClass> regs, IReadOnlyList<SpecialForceClass> sfs, SortedDictionary<(string, int), string> display)
    {
        Regiments = regs; SpecialForces = sfs; _display = display;
        _regByName = new SortedDictionary<string, RegimentClass>(regs.ToDictionary(r => r.Name), StringComparer.Ordinal);
        _sfByName = new SortedDictionary<string, SpecialForceClass>(sfs.ToDictionary(r => r.Name), StringComparer.Ordinal);
    }

    public RegimentClass? Regiment(string className) => _regByName.TryGetValue(className, out var c) ? c : null;
    public SpecialForceClass? SpecialForce(string className) => _sfByName.TryGetValue(className, out var c) ? c : null;

    /// <summary>The name shown for a regiment class (the data name: no regiment is renamed).</summary>
    public string RegimentDisplayName(RegimentClass c) => _display.TryGetValue(("troop", c.Id), out var n) ? n : c.Name;

    /// <summary>The name shown for a special-force class: the unit-names.csv entry if any (empty since the Praedra names, 2026-10-25), else the class name. Stats and ids are unchanged.</summary>
    public string SpecialForceDisplayName(SpecialForceClass c) => _display.TryGetValue(("special_force", c.Id), out var n) ? n : c.Name;

    public static TroopData Load(string originalDir, string designDir) =>
        Parse(File.ReadAllText(Path.Combine(originalDir, RegimentsFile)), File.ReadAllText(Path.Combine(originalDir, SpecialForcesFile)),
            File.ReadAllText(Path.Combine(designDir, NamesFile)));

    public static TroopData Parse(string regimentsCsv, string specialForcesCsv, string namesCsv)
    {
        var t = CsvTable.Parse(regimentsCsv, RegimentsFile);
        t.Require("id", "name", "side", "production_family", "cost", "maintenance", "research_order", "research_difficulty",
            "attack", "defense", "bombardment_defense", "detection", "uprising_defense");
        var regs = new List<RegimentClass>();
        for (int r = 0; r < t.RowCount; r++)
            regs.Add(new RegimentClass(t.GetInt(r, "id"), t.Get(r, "name").Trim(), ParseSide(t, r), t.GetInt(r, "production_family"),
                t.GetInt(r, "cost"), t.GetInt(r, "maintenance"), t.GetInt(r, "research_order"), t.GetInt(r, "research_difficulty"),
                t.GetInt(r, "attack"), t.GetInt(r, "defense"), t.GetInt(r, "bombardment_defense"), t.GetInt(r, "detection"),
                t.GetInt(r, "uprising_defense")));
        CheckUnique(regs.Select(c => c.Id), regs.Select(c => c.Name), RegimentsFile);

        var s = CsvTable.Parse(specialForcesCsv, SpecialForcesFile);
        s.Require("id", "name", "side", "production_family", "cost", "maintenance", "espionage", "combat", "leadership",
            "espionage_variance", "combat_variance", "leadership_variance", "loyalty_base", "mission_bit", "research_order");
        var sfs = new List<SpecialForceClass>();
        for (int r = 0; r < s.RowCount; r++)
            sfs.Add(new SpecialForceClass(s.GetInt(r, "id"), s.Get(r, "name").Trim(), ParseSide(s, r), s.GetInt(r, "production_family"),
                s.GetInt(r, "cost"), s.GetInt(r, "maintenance"), s.GetInt(r, "espionage"), s.GetInt(r, "combat"), s.GetInt(r, "leadership"),
                s.GetInt(r, "espionage_variance"), s.GetInt(r, "combat_variance"), s.GetInt(r, "leadership_variance"), s.GetInt(r, "loyalty_base"),
                ParseHex(s, r, "mission_bit"), s.GetInt(r, "research_order")));
        CheckUnique(sfs.Select(c => c.Id), sfs.Select(c => c.Name), SpecialForcesFile);

        var n = CsvTable.Parse(namesCsv, NamesFile);
        n.Require("kind", "id", "data_name", "display_name");
        var display = new SortedDictionary<(string, int), string>();
        for (int r = 0; r < n.RowCount; r++)
        {
            string kind = n.Get(r, "kind").Trim();
            int id = n.GetInt(r, "id");
            string dataName = n.Get(r, "data_name").Trim();
            bool known = kind == "troop" ? regs.Any(c => c.Id == id && c.Name == dataName)
                : kind == "special_force" ? sfs.Any(c => c.Id == id && c.Name == dataName) : false;
            if (!known) throw new DataLoadException($"{n.Where(r)}: no {kind} class {id} named '{dataName}'.");
            if (!display.TryAdd((kind, id), n.Get(r, "display_name").Trim())) throw new DataLoadException($"{n.Where(r)}: {kind} {id} renamed twice.");
        }
        return new TroopData(regs, sfs, display);
    }

    private static Side ParseSide(CsvTable t, int row) => t.Get(row, "side").Trim() switch
    {
        "Collective" => Side.Collective,
        "Hand" => Side.Hand,
        var x => throw new DataLoadException($"{t.Where(row)}: side '{x}' is not Collective or Hand."),
    };

    private static int ParseHex(CsvTable t, int row, string col)
    {
        string v = t.Get(row, col).Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (!int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x))
            throw new DataLoadException($"{t.Where(row)}: '{t.Get(row, col)}' is not a hex number.");
        return x;
    }

    private static void CheckUnique(IEnumerable<int> ids, IEnumerable<string> names, string source)
    {
        var il = ids.ToList(); var nl = names.ToList();
        if (il.Distinct().Count() != il.Count) throw new DataLoadException($"{source}: duplicate class id.");
        if (nl.Distinct(StringComparer.Ordinal).Count() != nl.Count) throw new DataLoadException($"{source}: duplicate class name.");
    }
}
