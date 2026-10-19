using System.Reflection;
using System.Text.RegularExpressions;

namespace Praedra.Core.Tests.Architecture;

// engine/03 "Rules every helper follows": the core has no Godot references and no randomness or time source
// other than the seeded game generator.
public class CoreRulesTests
{
    private static readonly string CoreDir = FindCoreDir();

    [Fact]
    public void CoreAssemblyReferencesNoGodot()
    {
        var refs = typeof(CoreInfo).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");
        Assert.DoesNotContain(refs, n => n.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"\bSystem\.Random\b", "System.Random")]
    [InlineData(@"\bnew\s+Random\s*\(", "new Random(")]
    [InlineData(@"\bRandom\.Shared\b", "Random.Shared")]
    [InlineData(@"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b", "wall-clock time")]
    [InlineData(@"\bEnvironment\.TickCount", "Environment.TickCount")]
    [InlineData(@"\bStopwatch\b", "Stopwatch")]
    [InlineData(@"\bGuid\.NewGuid\b", "Guid.NewGuid")]
    [InlineData(@"\busing\s+Godot\b", "Godot namespace")]
    [InlineData(@"\.GetHashCode\(\)\s*%", "hash-based ordering")]
    public void CoreSourceUsesNoForbiddenApi(string pattern, string what)
    {
        var offenders = Directory.EnumerateFiles(CoreDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), pattern))
            .Select(f => Path.GetRelativePath(CoreDir, f))
            .ToList();
        Assert.True(offenders.Count == 0, $"{what} is not allowed in the core: {string.Join(", ", offenders)}");
    }

    private static string FindCoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Praedra.sln")))
            dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Praedra.sln not found above the test output folder.");
        return Path.Combine(dir.FullName, "core");
    }
}
