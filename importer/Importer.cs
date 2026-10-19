namespace Praedra.Importer;

/// <summary>The importer's three entry points. Stage 2 always reads the raw CSVs from disk, so data/classic/*.csv depend on raw/ only. rule: B1-own-importer §Work 1-3.</summary>
public static class Importer
{
    public const string RawFolder = "raw";

    /// <summary>Stage 1 (game files to out/raw) then stage 2 (out/raw to out).</summary>
    public static void FromGame(string gameDir, string outDir)
    {
        var raw = RawStage.FromGame(gameDir);
        Clean(outDir);
        RawStage.Write(Path.Combine(outDir, RawFolder), raw);
        DropInStage.Write(Path.Combine(outDir, RawFolder), outDir);
    }

    /// <summary>Stage 2 only, from a folder of raw CSVs (copied to out/raw when it is another folder).</summary>
    public static void FromRaw(string rawDir, string outDir)
    {
        string target = Path.Combine(outDir, RawFolder);
        if (Path.GetFullPath(rawDir).TrimEnd(Path.DirectorySeparatorChar) != Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar))
        {
            var files = Directory.GetFiles(rawDir, "*.csv").OrderBy(p => p, StringComparer.Ordinal).Select(p => Csv.Load(p)).ToList();
            Clean(outDir);
            RawStage.Write(target, files);
        }
        DropInStage.Write(target, outDir);
    }

    /// <summary>The golden files plus our headers as out/raw, then stage 2.</summary>
    public static void FromGolden(string goldenDir, string outDir)
    {
        var raw = RawStage.FromGolden(goldenDir);
        Clean(outDir);
        RawStage.Write(Path.Combine(outDir, RawFolder), raw);
        DropInStage.Write(Path.Combine(outDir, RawFolder), outDir);
    }

    /// <summary>Remove the CSVs the importer owns (out/*.csv and out/raw/*.csv) so a re-run leaves no stale file.</summary>
    private static void Clean(string outDir)
    {
        foreach (var d in new[] { outDir, Path.Combine(outDir, RawFolder) })
            if (Directory.Exists(d))
                foreach (var f in Directory.GetFiles(d, "*.csv")) File.Delete(f);
    }

    /// <summary>data/classic under the folder holding Praedra.sln (searched upwards from the working folder).</summary>
    public static string DefaultOutDir()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Praedra.sln"))) return Path.Combine(dir.FullName, "data", "classic");
        throw new ImportException("Praedra.sln not found above the working folder; pass the out folder.");
    }
}
