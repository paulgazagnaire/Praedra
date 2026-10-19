using System.Globalization;

namespace Praedra.Importer;

/// <summary>
/// Stage 1: the game files to <c>raw/&lt;FILE&gt;.csv</c>, one per .DAT (header row, then every word in file order, word 5 kept packed) and
/// <c>raw/TEXTSTRA-strings.csv</c> (id,text). Lossless: later batches name more fields from these files alone. rule: B1-own-importer §Work 1, 2.
/// </summary>
public static class RawStage
{
    public const string GDataFolder = "GData", StringsDll = "TEXTSTRA.DLL", StringsCsv = "TEXTSTRA-strings";
    public static readonly string[] StringsHeader = { "id", "text" };

    /// <summary>Read every <c>GData/*.DAT</c> and <c>TEXTSTRA.DLL</c> of a game folder, in ordinal file-name order.</summary>
    public static List<CsvFile> FromGame(string gameDir)
    {
        string gd = Path.Combine(gameDir, GDataFolder);
        if (!Directory.Exists(gd)) throw new ImportException($"{gd}: folder not found (pass the game folder that holds GData and TEXTSTRA.DLL).");
        var files = Directory.GetFiles(gd).Where(p => Path.GetExtension(p).Equals(".DAT", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p).ToUpperInvariant(), StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new ImportException($"{gd}: no .DAT files.");
        var outp = new List<CsvFile>();
        foreach (var p in files) outp.Add(FromDat(Path.GetFileNameWithoutExtension(p).ToUpperInvariant(), File.ReadAllBytes(p)));
        string dll = Path.Combine(gameDir, StringsDll);
        if (!File.Exists(dll)) throw new ImportException($"{dll}: file not found.");
        outp.Add(FromStrings(StringTable.Read(StringsDll, File.ReadAllBytes(dll))));
        return outp;
    }

    /// <summary>One .DAT file to its raw table. A name ending in SD is a static-data file, any other a table file. rule: 02-gdata-formats §1, §2.</summary>
    public static CsvFile FromDat(string file, byte[] bytes)
    {
        bool sd = file.EndsWith("SD", StringComparison.Ordinal);
        var dat = sd ? GData.ReadSd(file + ".DAT", bytes) : GData.ReadTb(file + ".DAT", bytes);
        if (dat.EntryKind != null && Layouts.TableKinds.TryGetValue(file, out var expected) && expected != dat.EntryKind)
            throw new ImportException($"{file}.DAT: entry kind '{dat.EntryKind}', the sheet says '{expected}'.");
        int width = dat.Rows.Max(r => r.Length);
        var rows = dat.Rows.Select(r => r.Select(w => w.ToString(CultureInfo.InvariantCulture)).ToArray()).ToList();
        return new CsvFile(file, Layouts.Header(file, dat.EntryKind, width), rows);
    }

    public static CsvFile FromStrings(SortedDictionary<int, string> strings) =>
        new(StringsCsv, StringsHeader, strings.Select(kv => new[] { kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value }).ToList());

    /// <summary>
    /// The reference reader's headerless output (specs/reverse/golden) plus our header row: the raw tables of the copy the golden files were
    /// read from. Used where the game files are not at hand; the golden test then checks that <see cref="FromGame"/> gives the same files.
    /// </summary>
    public static List<CsvFile> FromGolden(string goldenDir)
    {
        var outp = new List<CsvFile>();
        foreach (var p in Directory.GetFiles(goldenDir, "*.csv").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            string file = Path.GetFileNameWithoutExtension(p);
            if (file == StringsCsv)
            {
                var s = Csv.Load(p);
                if (!s.Header.SequenceEqual(StringsHeader)) throw new ImportException($"{p}: header is not id,text.");
                outp.Add(new CsvFile(file, StringsHeader, s.Rows));
                continue;
            }
            var g = Csv.Load(p, headerless: true);
            string? kind = file.EndsWith("SD", StringComparison.Ordinal) ? null
                : Layouts.TableKinds.TryGetValue(file, out var k) ? k : throw new ImportException($"{p}: table file not in the sheet's entry-kind table.");
            int width = g.Rows.Max(r => r.Length);
            outp.Add(new CsvFile(file, Layouts.Header(file, kind, width), g.Rows));
        }
        return outp;
    }

    public static void Write(string rawDir, IEnumerable<CsvFile> files)
    {
        Directory.CreateDirectory(rawDir);
        foreach (var f in files) Csv.WriteFile(Path.Combine(rawDir, f.Name + ".csv"), f);
    }
}
