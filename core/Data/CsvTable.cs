using System.Globalization;
using System.Text;

namespace Rebellion.Core.Data;

/// <summary>
/// Plain CSV reader: header row, quoted fields ("" escapes a quote, quotes may hold commas and line breaks),
/// optional byte-order mark, CRLF or LF. Numbers are always read with the invariant culture.
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns;
    private readonly List<string[]> _rows;
    private readonly List<int> _lineNumbers;
    private readonly List<string>? _rowSources;

    public string Source { get; }
    public IReadOnlyList<string> Header { get; }
    public int RowCount => _rows.Count;

    private CsvTable(string source, string[] header, List<string[]> rows, List<int> lineNumbers, List<string>? rowSources = null)
    {
        Source = source;
        Header = header;
        _rows = rows;
        _lineNumbers = lineNumbers;
        _rowSources = rowSources;
        _columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Length; i++)
        {
            if (!_columns.TryAdd(header[i], i))
                throw new DataLoadException($"{source}: duplicate column '{header[i]}' in the header.");
        }
    }

    public bool HasColumn(string name) => _columns.ContainsKey(name);

    public void Require(params string[] names)
    {
        foreach (var n in names)
            if (!_columns.ContainsKey(n))
                throw new DataLoadException($"{Source}: required column '{n}' is missing from the header.");
    }

    public string Get(int row, string column) => _rows[row][_columns[column]];

    /// <summary>Human-readable position for error messages (file name and 1-based line).</summary>
    public string Where(int row) => $"{(_rowSources == null ? Source : _rowSources[row])} line {_lineNumbers[row]}";

    public int GetInt(int row, string column)
    {
        var text = Get(row, column).Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new DataLoadException($"{Where(row)}: column '{column}' must be an integer, found '{text}'.");
        return v;
    }

    public static CsvTable Parse(string text, string source)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        var records = new List<(int line, List<string> fields)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, wasQuoted = false;
        int line = 1, recordLine = 1;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }
        void EndRecord()
        {
            EndField();
            bool blank = fields.Count == 1 && fields[0].Length == 0 && !wasQuoted;
            if (!blank) records.Add((recordLine, fields));
            fields = new List<string>();
            wasQuoted = false;
        }

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
                else { if (c == '\n') line++; field.Append(c); }
            }
            else if (c == '"' && field.Length == 0) { inQuotes = true; wasQuoted = true; }
            else if (c == ',') EndField();
            else if (c == '\r') { }
            else if (c == '\n') { EndRecord(); line++; recordLine = line; }
            else field.Append(c);
        }
        if (inQuotes) throw new DataLoadException($"{source}: a quoted field is never closed.");
        if (field.Length > 0 || fields.Count > 0) EndRecord();

        if (records.Count == 0) throw new DataLoadException($"{source}: the file is empty.");
        var header = records[0].fields.Select(h => h.Trim()).ToArray();
        var rows = new List<string[]>();
        var lines = new List<int>();
        for (int r = 1; r < records.Count; r++)
        {
            var f = records[r].fields;
            if (f.Count != header.Length)
                throw new DataLoadException($"{source} line {records[r].line}: expected {header.Length} columns, found {f.Count}.");
            rows.Add(f.ToArray());
            lines.Add(records[r].line);
        }
        return new CsvTable(source, header, rows, lines);
    }

    public static CsvTable Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>
    /// The rows of <paramref name="first"/> then those of <paramref name="second"/>, under the header of <paramref name="first"/>. The second
    /// table must have every column of the first; its other columns (a row's own source tag) are dropped. Used to merge a data/design file
    /// into the data/original file of the same name (rows the 1998 files do not hold, each keeping its tag in its own file).
    /// rule: B1-own-importer §Work 3 ("move to data/design/ files that the loaders merge").
    /// </summary>
    public static CsvTable Merge(CsvTable first, CsvTable second)
    {
        foreach (var h in first.Header)
            if (!second.HasColumn(h)) throw new DataLoadException($"{second.Source}: column '{h}' of {first.Source} is missing, so the files cannot be merged.");
        var rows = new List<string[]>(first._rows);
        var lines = new List<int>(first._lineNumbers);
        var sources = Enumerable.Range(0, first.RowCount).Select(r => first._rowSources?[r] ?? first.Source).ToList();
        for (int r = 0; r < second.RowCount; r++)
        {
            rows.Add(first.Header.Select(h => second.Get(r, h)).ToArray());
            lines.Add(second._lineNumbers[r]);
            sources.Add(second._rowSources?[r] ?? second.Source);
        }
        return new CsvTable($"{first.Source} + {second.Source}", first.Header.ToArray(), rows, lines, sources);
    }

    /// <summary>
    /// <c>original/name</c> merged with <c>design/name</c> when the design folder holds a file of that name, else the original file alone.
    /// rule: B1-own-importer §Work 3.
    /// </summary>
    public static CsvTable LoadMerged(string originalDir, string designDir, string fileName)
    {
        var o = Load(Path.Combine(originalDir, fileName));
        string d = Path.Combine(designDir, fileName);
        return File.Exists(d) ? Merge(o, Parse(File.ReadAllText(d), "design/" + fileName)) : o;
    }
}
