using System.Globalization;
using Praedra.Core.Data;

namespace Praedra.Core.Repair;

/// <summary>
/// The data table <c>RepairRules</c> (file data/design/repair-rules.csv, key,value,source): where, how often and how much a unit is repaired
/// in slot 6.8. Values come from the file, never from code. rule: m4-retreat-damage-repair §5 Strategic repair (Data values; Traced, B3 §15).
/// </summary>
/// <param name="RepairNeedsYard">Repair only with FastRepair (own usable shipyard). Traced: false (repair works anywhere).</param>
/// <param name="RepairPeriodDays">Days between repair turns of a capital ship (GNPRTB 2053).</param>
/// <param name="FastRepairPeriodDays">The same with FastRepair (GNPRTB 2052).</param>
/// <param name="SquadronRepairPeriodDays">Days between repair turns of a squadron (GNPRTB 4609).</param>
/// <param name="SquadronFastRepairPeriodDays">The same with FastRepair (GNPRTB 4608).</param>
/// <param name="RepairHullDivisor">A turn takes trunc(class hull / this) off the hull damage.</param>
public sealed record RepairRules(bool RepairNeedsYard, int RepairPeriodDays, int FastRepairPeriodDays, int SquadronRepairPeriodDays,
    int SquadronFastRepairPeriodDays, int RepairHullDivisor)
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
        return new RepairRules(Bool("RepairNeedsYard"), Int("RepairPeriodDays", 1), Int("FastRepairPeriodDays", 1),
            Int("SquadronRepairPeriodDays", 1), Int("SquadronFastRepairPeriodDays", 1), Int("RepairHullDivisor", 1));
    }
}
