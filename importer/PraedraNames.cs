namespace Praedra.Importer;

/// <summary>
/// The Praedra names for Classic's placeholder names (data/design/name-migrations.csv: kind, old, new). Stage 2 writes the drop-in tables
/// with the Praedra names; the raw files keep Classic's own text. A cell equal to an old name becomes the new name, nothing else changes.
/// tools/names/apply_names.py applies the same table to the other data files and the code.
/// </summary>
public static class PraedraNames
{
    public const string TableFile = "name-migrations.csv";

    public const string RosterFile = "roster.csv", CutFile = "cut-classes.csv";

    /// <summary>
    /// Classic classes and mission kinds cut from Praedra (data/design/cut-classes.csv, by Classic name): the drop-in tables leave out every row
    /// whose name (or, in mission tables, kind) is one of them; the raw files keep them.
    /// </summary>
    public static IReadOnlySet<string> LoadCut()
    {
        var t = Csv.Load(DesignFile(CutFile));
        return Enumerable.Range(0, t.Rows.Count).Select(r => t.Get(r, "name")).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The CSV text without the rows whose "name" cell, or "kind" cell in mission-records.csv and mission-foilable.csv, is cut.</summary>
    public static string DropCut(string file, string text, IReadOnlySet<string> cut)
    {
        var t = Csv.Parse(file, text);
        var cols = t.Header.Select((h, i) => (h, i)).Where(x => x.h == "name" || (x.h == "kind" && file.StartsWith("mission-", StringComparison.Ordinal)))
            .Select(x => x.i).ToList();
        return Csv.Write(t.Header, t.Rows.Where(r => !cols.Any(i => cut.Contains(r[i]))));
    }

    /// <summary>Reads the table from data/design of the repository that holds Praedra.sln (searched upwards from the program and the working folder).</summary>
    public static IReadOnlyDictionary<string, string> Load() => Parse(Csv.Load(DesignFile(TableFile)));

    /// <summary>
    /// The ids of the characters in the game (data/design/roster.csv). Characters left out of the roster were cut (heroes/refined-roster,
    /// Decided Paul) and carry no Praedra name, so the drop-in characters.csv leaves them out; the raw files keep them.
    /// </summary>
    public static IReadOnlySet<int> LoadRoster()
    {
        var t = Csv.Load(DesignFile(RosterFile));
        return Enumerable.Range(0, t.Rows.Count).Select(r => t.Int(r, "id")).ToHashSet();
    }

    private static string DesignFile(string file)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Praedra.sln")))
                    return Path.Combine(dir.FullName, "data", "design", file);
        throw new ImportException($"Praedra.sln not found above the program or working folder, so data/design/{file} cannot be read.");
    }

    public static IReadOnlyDictionary<string, string> Parse(CsvFile t)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int r = 0; r < t.Rows.Count; r++)
            if (!map.TryAdd(t.Get(r, "old"), t.Get(r, "new"))) throw new ImportException($"{TableFile} row {r + 1}: '{t.Get(r, "old")}' appears twice.");
        return map;
    }

    /// <summary>Columns never renamed: a mission kind is not a place. Same list as apply_names.py SKIP_COLUMNS.</summary>
    public static readonly IReadOnlySet<string> SkipColumns = new HashSet<string>(StringComparer.Ordinal) { "kind" };

    /// <summary>The CSV text with every cell that equals an old name replaced by its new name (except in <see cref="SkipColumns"/>).</summary>
    public static string Rename(string file, string text, IReadOnlyDictionary<string, string> names)
    {
        var t = Csv.Parse(file, text);
        var skip = t.Header.Select((h, i) => SkipColumns.Contains(h) ? i : -1).Where(i => i >= 0).ToHashSet();
        return Csv.Write(t.Header, t.Rows.Select(r => r.Select((c, i) => !skip.Contains(i) && names.TryGetValue(c, out var n) ? n : c)));
    }
}
