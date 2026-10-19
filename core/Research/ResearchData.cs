using System.Globalization;
using Praedra.Core.Data;
using Praedra.Core.Economy;

namespace Praedra.Core.Research;

/// <summary>The three research tracks, in the fixed order Ship, Troop, Facility (the Classic kinds). rule: m5-research §State per side.</summary>
public enum ResearchKind { Ship = 0, Troop = 1, Facility = 2 }

/// <summary>
/// One row of the research ladders (data/classic/research-unlocks.csv, read from the Classic files by importer/, then the rows of
/// data/design/research-unlocks.csv). <see cref="Catalogue"/> is the row's position in the file: the catalogue order of m5-research §The level check step 4 (DEFAULT).
/// rule: m5-research §Which track an item belongs to, §Ladders.
/// </summary>
public sealed record ResearchRow(int Catalogue, ResearchKind Kind, string Name, bool Alliance, bool Empire, int Cost, int Order, int Difficulty);

/// <summary>
/// The data table <c>ResearchRules</c> (data/design/research-rules.csv, key,value,source): the facility tick timer, points per yard, the
/// mission point roll and the Sensor Station tier 2 rung. Values come from the file, never from code. rule: m5-research §Numbers.
/// </summary>
public sealed record ResearchRules(int TickArmingDay, int TickDelayDays, int TickSpanDays, int PointsPerYard,
    int MissionPointsBase, int MissionPointsSpan, int SensorTier2Order, int SensorTier2BuildCost)
{
    public const string FileName = "research-rules.csv";

    public static ResearchRules Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static ResearchRules Parse(string text, string source = FileName)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("key", "value");
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            var key = t.Get(r, "key").Trim();
            if (!values.TryAdd(key, t.Get(r, "value").Trim())) throw new DataLoadException($"{t.Where(r)}: duplicate key '{key}'.");
        }
        int Int(string k, int min)
        {
            if (!values.TryGetValue(k, out var s)) throw new DataLoadException($"{source}: missing key '{k}'.");
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                throw new DataLoadException($"{source}: key '{k}' must be an integer, found '{s}'.");
            if (i < min) throw new DataLoadException($"{source}: key '{k}' must be at least {min}, found {i}.");
            return i;
        }
        return new ResearchRules(Int("TickArmingDay", 0), Int("TickDelayDays", 1), Int("TickSpanDays", 0), Int("PointsPerYard", 0),
            Int("MissionPointsBase", 0), Int("MissionPointsSpan", 0), Int("SensorTier2Order", 1), Int("SensorTier2BuildCost", 1));
    }
}

/// <summary>
/// The research ladders and rules, read once. Every class of the costs file must have exactly one ladder row with the same research order
/// (checked at load, so the two copies of the order cannot drift). rule: m5-research §Ladders (data, never hard-coded).
/// </summary>
public sealed class ResearchData
{
    public const string LadderFileName = "research-unlocks.csv";
    /// <summary>The class name of the Sensor Station tier 2 row (only present when SensorTier2Research is on). rule: m5-research §Sensor Station tiers.</summary>
    public const string AdvancedSensorStation = "Advanced Sensor Station";

    /// <summary>Ladder rows in catalogue (file) order.</summary>
    public IReadOnlyList<ResearchRow> Rows { get; }
    public ResearchRules Rules { get; }

    public ResearchData(IReadOnlyList<ResearchRow> rows, ResearchRules rules) { Rows = rows; Rules = rules; }

    /// <summary>The original ladder merged with the design rows (rule: B1-own-importer §Work 3), and the rules.</summary>
    public static ResearchData Load(string original, string design, CostData costs) =>
        new(ParseLadder(CsvTable.LoadMerged(original, design, LadderFileName), costs),
            ResearchRules.Load(Path.Combine(design, ResearchRules.FileName)));

    /// <summary>
    /// Parse the ladder file. research_family 0x28 = Ship, 0x29 = Troop, 0x2a = Facility (economy/14 §2.5). With <paramref name="costs"/>
    /// given, every row must name a costs class with the same research order, and every costs class must have a row.
    /// </summary>
    public static List<ResearchRow> ParseLadder(string text, string source, CostData? costs) => ParseLadder(CsvTable.Parse(text, source), costs);

    public static List<ResearchRow> ParseLadder(CsvTable t, CostData? costs)
    {
        string source = t.Source;
        t.Require("research_family", "name", "alliance", "empire", "cost", "level", "rdiff");
        var rows = new List<ResearchRow>();
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            string fam = t.Get(r, "research_family").Trim(), name = t.Get(r, "name").Trim();
            var kind = fam.Split(' ')[0] switch
            {
                "0x28" => ResearchKind.Ship,
                "0x29" => ResearchKind.Troop,
                "0x2a" => ResearchKind.Facility,
                _ => throw new DataLoadException($"{t.Where(r)}: unknown research family '{fam}'."),
            };
            int order = t.GetInt(r, "level"), diff = t.GetInt(r, "rdiff"), cost = t.GetInt(r, "cost");
            if (order < 0 || diff < 0) throw new DataLoadException($"{t.Where(r)}: '{name}' has a negative research order or difficulty.");
            if (!seen.Add(name)) throw new DataLoadException($"{t.Where(r)}: class name '{name}' appears twice.");
            if (costs != null)
            {
                if (!costs.TryGet(name, out var c)) throw new DataLoadException($"{t.Where(r)}: '{name}' is not a class of {CostData.FileName}.");
                if (c.ResearchOrder != order)
                    throw new DataLoadException($"{t.Where(r)}: '{name}' has research order {order} but {CostData.FileName} says {c.ResearchOrder}.");
            }
            rows.Add(new ResearchRow(r, kind, name, t.GetInt(r, "alliance") != 0, t.GetInt(r, "empire") != 0, cost, order, diff));
        }
        if (costs != null)
            foreach (var c in costs.Rows)
                if (!seen.Contains(c.Name)) throw new DataLoadException($"{source}: class '{c.Name}' of {CostData.FileName} has no research row.");
        return rows;
    }
}
