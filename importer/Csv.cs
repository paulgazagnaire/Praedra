using System.Text;

namespace Rebellion.Importer;

/// <summary>A CSV file in memory: a header row and data rows. Raw table rows may be shorter than the header (seed family entries vary).</summary>
public sealed class CsvFile
{
    public string Name { get; }
    public string[] Header { get; }
    public List<string[]> Rows { get; }
    private readonly Dictionary<string, int> _col;

    public CsvFile(string name, string[] header, List<string[]> rows)
    {
        Name = name; Header = header; Rows = rows;
        _col = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        for (int i = 0; i < header.Length; i++)
            if (!_col.TryAdd(header[i], i)) throw new ImportException($"{name}: duplicate column '{header[i]}'.");
    }

    public int Col(string column) => _col.TryGetValue(column, out var i) ? i : throw new ImportException($"{Name}: no column '{column}'.");

    public string Get(int row, string column)
    {
        int c = Col(column);
        return c < Rows[row].Length ? Rows[row][c] : throw new ImportException($"{Name} row {row + 1}: no value in column '{column}'.");
    }

    public long Long(int row, string column) =>
        long.TryParse(Get(row, column), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
            : throw new ImportException($"{Name} row {row + 1}: column '{column}' is not an integer ('{Get(row, column)}').");

    public int Int(int row, string column) => checked((int)Long(row, column));
}

/// <summary>
/// CSV text in the style of Python's csv module (the reference reader's output): comma separated, a field is quoted only when it holds a comma,
/// a quote or a line break, quotes doubled inside. Lines end with LF.
/// </summary>
public static class Csv
{
    public const string NewLine = "\n";

    public static string Field(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    public static string Write(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", header.Select(Field))).Append(NewLine);
        foreach (var r in rows) sb.Append(string.Join(",", r.Select(Field))).Append(NewLine);
        return sb.ToString();
    }

    public static void WriteFile(string path, CsvFile f) =>
        File.WriteAllText(path, Write(f.Header, f.Rows), new UTF8Encoding(false));

    /// <summary>Parse CSV text; the first record is the header unless <paramref name="headerless"/>. Rows may have fewer fields than the header, never more.</summary>
    public static CsvFile Parse(string name, string text, bool headerless = false)
    {
        var records = Records(text);
        if (headerless)
        {
            int w = records.Count == 0 ? 0 : records.Max(r => r.Length);
            return new CsvFile(name, Enumerable.Range(0, w).Select(i => "c" + i).ToArray(), records);
        }
        if (records.Count == 0) throw new ImportException($"{name}: empty file.");
        var header = records[0];
        var rows = records.Skip(1).ToList();
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Length > header.Length) throw new ImportException($"{name} row {i + 1}: {rows[i].Length} fields, the header has {header.Length}.");
        return new CsvFile(name, header, rows);
    }

    public static CsvFile Load(string path, bool headerless = false) =>
        Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path, Encoding.UTF8), headerless);

    private static List<string[]> Records(string text)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) { inQuotes = true; any = true; }
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); any = true; }
            else if (c == '\r') { }
            else if (c == '\n')
            {
                if (any || field.Length > 0) { fields.Add(field.ToString()); records.Add(fields.ToArray()); }
                fields.Clear(); field.Clear(); any = false;
            }
            else { field.Append(c); any = true; }
        }
        if (inQuotes) throw new ImportException("a quoted CSV field is never closed.");
        if (any || field.Length > 0) { fields.Add(field.ToString()); records.Add(fields.ToArray()); }
        return records;
    }
}
