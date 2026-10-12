using System.Globalization;
using Rebellion.Core.Data;

namespace Rebellion.Core.Repair;

/// <summary>
/// The data table <c>RepairRules</c> (file data/design/repair-rules.csv, key,value,source): where, how often and how much a ship is repaired
/// in slot 6.8. Values come from the file, never from code. rule: m4-retreat-damage-repair §5 Strategic repair (Data values).
/// </summary>
public sealed record RepairRules(bool RepairNeedsYard, int RepairCadenceDays, int RepairHullDivisor, int RepairHitsPerCall)
{
    public const string FileName = "repair-rules.csv";

    public static RepairRules Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static RepairRules Parse(string text, string source = FileName)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("key", "value");
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            var key = t.Get(r, "key").Trim();
            if (!values.TryAdd(key, t.Get(r, "value").Trim())) throw new DataLoadException($"{t.Where(r)}: duplicate key '{key}'.");
        }
        string Need(string k) => values.TryGetValue(k, out var v) ? v : throw new DataLoadException($"{source}: missing key '{k}'.");
        int Int(string k, int min)
        {
            var s = Need(k);
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                throw new DataLoadException($"{source}: key '{k}' must be an integer, found '{s}'.");
            if (i < min) throw new DataLoadException($"{source}: key '{k}' must be at least {min}, found {i}.");
            return i;
        }
        bool Bool(string k)
        {
            var s = Need(k);
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new DataLoadException($"{source}: key '{k}' must be true or false, found '{s}'.");
        }
        return new RepairRules(Bool("RepairNeedsYard"), Int("RepairCadenceDays", 1), Int("RepairHullDivisor", 1), Int("RepairHitsPerCall", 0));
    }
}
