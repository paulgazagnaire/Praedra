using Rebellion.Core.Data;

namespace Rebellion.Core.Support;

/// <summary>
/// Regiment weights in tenths for the New garrison rule, from data/design/garrison-weights.csv (placeholder numbers: defense / 5,
/// units/03; to be replaced after the assault play-test). Looked up by regiment class name; a class without a row uses the
/// default. rule: m2-uprisings-and-garrison §Garrison G and the check (New).
/// </summary>
public sealed class GarrisonWeights
{
    public const string FileName = "garrison-weights.csv";
    private readonly SortedDictionary<string, int> _w10;
    private readonly int _default;

    private GarrisonWeights(SortedDictionary<string, int> w10, int def) { _w10 = w10; _default = def; }

    public int Weight10(string regimentClass) => _w10.TryGetValue(regimentClass, out var w) ? w : _default;

    /// <summary>Every class weighs the default (1.0): the New rule then behaves like Classic.</summary>
    public static GarrisonWeights Flat(int defaultWeight10 = 10) => new(new SortedDictionary<string, int>(StringComparer.Ordinal), defaultWeight10);

    public static GarrisonWeights Load(string path, int defaultWeight10 = 10) => Parse(File.ReadAllText(path), Path.GetFileName(path), defaultWeight10);

    public static GarrisonWeights Parse(string text, string source, int defaultWeight10 = 10)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("id", "name", "weight10");
        var d = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            string name = t.Get(r, "name").Trim();
            int w = t.GetInt(r, "weight10");
            if (w < 0) throw new DataLoadException($"{t.Where(r)}: '{name}' has a negative weight.");
            if (!d.TryAdd(name, w)) throw new DataLoadException($"{t.Where(r)}: class '{name}' appears twice.");
        }
        return new GarrisonWeights(d, defaultWeight10);
    }
}
