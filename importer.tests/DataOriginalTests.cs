namespace Rebellion.Importer.Tests;

/// <summary>Runs only when REBELLION_GAME_DIR names a game folder (GData/*.DAT and TEXTSTRA.DLL; the files are never committed).</summary>
public sealed class GameDirFactAttribute : FactAttribute
{
    public const string Variable = "REBELLION_GAME_DIR";

    public GameDirFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable))) Skip = $"{Variable} is not set (the game files are not in the repository).";
    }
}

/// <summary>The committed data/original against the importer. rule: B1-own-importer §Tests (golden test), §Work 2, 3.</summary>
public class DataOriginalTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Original = Path.Combine(Root, "data", "original");

    // B1-own-importer Tests (golden): the importer on the game files gives exactly the committed data/original (raw and drop-in files)
    [GameDirFact]
    public void GoldenGameFilesGiveTheCommittedDataOriginal()
    {
        string game = Environment.GetEnvironmentVariable(GameDirFactAttribute.Variable)!;
        string tmp = Path.Combine(Path.GetTempPath(), "rebellion-importer-golden-" + Environment.ProcessId);
        try
        {
            Importer.FromGame(game, tmp);
            AssertSameFolder(Original, tmp);
            AssertSameFolder(Path.Combine(Original, Importer.RawFolder), Path.Combine(tmp, Importer.RawFolder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    // B1-own-importer Work 3: stage 2 on the committed raw CSVs regenerates every committed drop-in file byte for byte
    [Fact]
    public void DropInFilesAreRegeneratedFromRaw()
    {
        foreach (var (file, text) in DropInStage.Build(Path.Combine(Original, Importer.RawFolder)))
            Assert.True(text == File.ReadAllText(Path.Combine(Original, file)), $"data/original/{file} differs from the importer's stage 2 output: re-run the importer.");
        Assert.Equal(DropInStage.Outputs.OrderBy(f => f, StringComparer.Ordinal),
            Directory.GetFiles(Original, "*.csv").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal));
    }

    // B1-own-importer Work 2: one raw file per .DAT (51 in the reference copy) plus the strings, each with a header row
    [Fact]
    public void RawFolderHasEveryFile()
    {
        var raw = Directory.GetFiles(Path.Combine(Original, Importer.RawFolder), "*.csv").Select(Path.GetFileNameWithoutExtension).ToList();
        foreach (var f in Layouts.Sd.Keys.Concat(Layouts.TableKinds.Keys).Append(RawStage.StringsCsv)) Assert.Contains(f, raw);
        foreach (var f in raw)
        {
            var t = Csv.Load(Path.Combine(Original, Importer.RawFolder, f + ".csv"));
            Assert.True(t.Rows.Count > 0, f);
        }
    }

    // B1-own-importer Work 1: FDECOYTB and TDECOYTB must be identical, else stage 2 fails
    [Fact]
    public void DecoyTablesThatDifferFail()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "rebellion-importer-decoy-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var f in Directory.GetFiles(Path.Combine(Original, Importer.RawFolder))) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
            string t = Path.Combine(tmp, "TDECOYTB.csv");
            File.WriteAllText(t, File.ReadAllText(t).Replace("1,1,-20,10", "1,1,-20,11"));
            var ex = Assert.Throws<ImportException>(() => DropInStage.Build(tmp));
            Assert.Contains("FDECOYTB and TDECOYTB differ", ex.Message);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    // B1-own-importer QA Q3 / Q4: the Rix and Garaint Force values, and every mission table complete
    [Fact]
    public void RixGaraintAndTableRowCounts()
    {
        var c = Csv.Load(Path.Combine(Original, "characters.csv"));
        int Row(string name) => Enumerable.Range(0, c.Rows.Count).Single(r => c.Get(r, "name") == name);
        Assert.Equal((918, 10, 40), (c.Int(Row("Shenir Rix"), "id"), c.Int(Row("Shenir Rix"), "jlvl"), c.Int(Row("Shenir Rix"), "jlvlv")));
        Assert.Equal((919, 30, 20), (c.Int(Row("Noval Garaint"), "id"), c.Int(Row("Noval Garaint"), "jlvl"), c.Int(Row("Noval Garaint"), "jlvlv")));

        var t = Csv.Load(Path.Combine(Original, "mission-tables.csv"));
        var counts = t.Rows.GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.Count());
        var expect = new Dictionary<string, int>
        {
            ["DIPLMSTB"] = 10, ["ESPIMSTB"] = 12, ["SBTGMSTB"] = 12, ["RESCMSTB"] = 12, ["DSSBMSTB"] = 12, ["RCRTMSTB"] = 11, ["ABDCMSTB"] = 12,
            ["ASSNMSTB"] = 12, ["INCTMSTB"] = 13, ["SUBDMSTB"] = 13, ["FOILTB"] = 14, ["FDECOYTB"] = 14, ["RLEVADTB"] = 14, ["ESCAPETB"] = 9, // B2d: the escape duel table; B2e: the prisoner escape table
        };
        foreach (var kv in expect) Assert.Equal((kv.Key, kv.Value), (kv.Key, counts[kv.Key]));
        Assert.Equal(expect.Count, counts.Count);
    }

    private static void AssertSameFolder(string expected, string actual)
    {
        var e = Directory.GetFiles(expected, "*.csv").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var a = Directory.GetFiles(actual, "*.csv").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(e, a);
        foreach (var f in e)
            Assert.True(File.ReadAllText(Path.Combine(expected, f!)) == File.ReadAllText(Path.Combine(actual, f!)), $"{f} differs from the committed copy.");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Rebellion.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rebellion.sln not found above the test output folder.");
    }
}
