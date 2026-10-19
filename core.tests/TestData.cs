using Praedra.Core.Map;

namespace Praedra.Core.Tests;

/// <summary>Finds the repo's data folders and builds small synthetic galaxies for tests.</summary>
internal static class TestData
{
    public static readonly string RepoRoot = FindRoot();
    public static readonly string DesignDir = Path.Combine(RepoRoot, "data", "design");
    public static readonly string ClassicDir = Path.Combine(RepoRoot, "data", "classic");

    /// <summary>
    /// CSV text of data/classic/<paramref name="file"/> merged with data/design/<paramref name="file"/> (the loaders' view, rule: B1-own-importer
    /// §Work 3), under the original header, for tests that edit the real file as text.
    /// </summary>
    public static string MergedText(string file)
    {
        var t = Praedra.Core.Data.CsvTable.LoadMerged(ClassicDir, DesignDir, file);
        static string Q(string v) => v.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
        return string.Join("\n", new[] { string.Join(",", t.Header) }
            .Concat(Enumerable.Range(0, t.RowCount).Select(r => string.Join(",", t.Header.Select(h => Q(t.Get(r, h))))))) + "\n";
    }

    private static readonly Lazy<Galaxy> _real = new(() => GalaxyLoader.LoadFolder(DesignDir));
    public static Galaxy RealGalaxy => _real.Value;

    public static string PlanetsText => File.ReadAllText(Path.Combine(DesignDir, GalaxyLoader.PlanetsFile));
    public static string LanesText => File.ReadAllText(Path.Combine(DesignDir, GalaxyLoader.LanesFile));
    public static LaneTiers Tiers => LaneTiers.Load(Path.Combine(DesignDir, GalaxyLoader.TiersFile));

    /// <summary>Synthetic galaxy: systems (name, x, y) all in sector "S", lanes (a, b) all kind 'generated' (tier 3).</summary>
    public static Galaxy Synthetic((string name, int x, int y)[] systems, (string a, string b)[] lanes)
    {
        var p = "name,sector,group,map_x,map_y\n" + string.Join("\n", systems.Select(s => $"{s.name},S,core,{s.x},{s.y}"));
        var l = "from,to,kind,routes\n" + string.Join("\n", lanes.Select(x => $"{x.a},{x.b},generated,"));
        return GalaxyLoader.Load(p, l, Tiers);
    }

    /// <summary>Synthetic galaxy with chosen lane kinds: (a, b, kind); a "route" kind gets the route name "Test Run" (tier 1, factor 300).</summary>
    public static Galaxy Synthetic((string name, int x, int y)[] systems, (string a, string b, string kind)[] lanes)
    {
        var p = "name,sector,group,map_x,map_y\n" + string.Join("\n", systems.Select(s => $"{s.name},S,core,{s.x},{s.y}"));
        var l = "from,to,kind,routes\n" + string.Join("\n", lanes.Select(x => $"{x.a},{x.b},{x.kind},{(x.kind.StartsWith("route") ? "Test Run" : "")}"));
        return GalaxyLoader.Load(p, l, Tiers);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Praedra.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Praedra.sln not found above the test output folder.");
    }
}
