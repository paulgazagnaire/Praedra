using System.Globalization;
using Praedra.Core.Data;

namespace Praedra.Core.Map;

/// <summary>
/// People per system, from data/design/system-population.csv (columns system, people, source; copied from the lore's popB as whole people).
/// A system without a row has 0 people. Read-only data, not saved. Used by the harvest yield (rule: m5-harvester §2 effect 8) and the harvest
/// report (rule: m5-harvester §7).
/// </summary>
public sealed class SystemPopulation
{
    public const string FileName = "system-population.csv";
    private readonly SortedDictionary<string, long> _people;

    private SystemPopulation(SortedDictionary<string, long> people) { _people = people; }

    /// <summary>The people of a system; 0 for a system not in the file.</summary>
    public long PeopleOf(string system) => _people.TryGetValue(system, out long n) ? n : 0;

    /// <summary>No system has people (every harvest yield is 0).</summary>
    public static SystemPopulation Empty => new(new SortedDictionary<string, long>(StringComparer.Ordinal));

    public static SystemPopulation Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static SystemPopulation Parse(string text, string source = FileName)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("system", "people");
        var d = new SortedDictionary<string, long>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            string name = t.Get(r, "system").Trim();
            if (!long.TryParse(t.Get(r, "people").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                throw new DataLoadException($"{t.Where(r)}: '{name}' has people '{t.Get(r, "people")}', not a whole number of 0 or more.");
            if (!d.TryAdd(name, n)) throw new DataLoadException($"{t.Where(r)}: system '{name}' appears twice.");
        }
        return new SystemPopulation(d);
    }
}
