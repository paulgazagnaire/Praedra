using Rebellion.Core.Map;

namespace Rebellion.Core.Tests;

/// <summary>Finds the repo's data folders and builds small synthetic galaxies for tests.</summary>
internal static class TestData
{
    public static readonly string RepoRoot = FindRoot();
    public static readonly string DesignDir = Path.Combine(RepoRoot, "data", "design");
    public static readonly string InterimDir = Path.Combine(RepoRoot, "data", "interim");

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

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Rebellion.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rebellion.sln not found above the test output folder.");
    }
}
