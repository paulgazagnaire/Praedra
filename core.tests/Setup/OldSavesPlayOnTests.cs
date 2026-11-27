using Praedra.Core.AI;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Setup;

// m1-clock-and-orders K12, K16 (a loaded game continues as before), crash and softlock hunt (Paul 2026-11-27: "old saves, with every crash
// fixed"). Every save in core.tests/Setup/saves/old (QA's saves from older builds, copied from the shared qa/saves folder) loads, saves back
// to the same state, and plays 60 more days with two Medium AIs without an exception. A rule change that breaks an old save fails here.
public class OldSavesPlayOnTests
{
    private const int DaysOn = 60;

    public static IEnumerable<object[]> Saves() =>
        Directory.GetFiles(Path.Combine(TestData.RepoRoot, "core.tests", "Setup", "saves", "old"))
                 .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n! });

    [Theory]
    [Trait("Category", "Play")]
    [MemberData(nameof(Saves))]
    public void OldSaveLoadsAndPlaysOn(string file)
    {
        var host = GameHost.Load(File.ReadAllText(Path.Combine(TestData.RepoRoot, "core.tests", "Setup", "saves", "old", file)));
        Assert.Equal(host.StateHash(), GameHost.Load(host.SaveWithSettings()).StateHash());
        int start = host.Game.Day;
        var medium = AiLevels.For(AiLevel.Medium);
        var match = new AiMatch(host, medium, medium);
        while (host.Game.Day < start + DaysOn && host.Victory.Winner == null) match.Step();
        Assert.True(host.Game.Day > start);
    }
}
