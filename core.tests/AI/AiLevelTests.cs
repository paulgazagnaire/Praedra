using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// Difficulty levels of the computer opponent (core/AI/AiLevel.cs). The rules: ai/09 §6 (difficulty is an AI parameter set, not a head start),
// ai/12 D3 and economy-ai D4 (Paul 2026-10-12: Hard is smarter, never a cheat; the AI plays fair with fog of war at every level).
public class AiLevelTests
{
    private readonly ITestOutputHelper _out;
    public AiLevelTests(ITestOutputHelper o) { _out = o; }

    // ai/12 D3: every level reads only its side's fog-of-war view.
    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Medium)]
    [InlineData(AiLevel.Hard)]
    public void EveryLevel_PlaysWithFog(AiLevel level) => Assert.True(AiLevels.For(level).UseFog);

    // ai/09 §6: Medium is the DEFAULT AI (the SimpleAiParams defaults with fog on), so the play tests of the default AI measure Medium.
    [Fact]
    public void Medium_IsTheDefaultAi()
    {
        var d = new SimpleAiParams { UseFog = true }; var m = AiLevels.For(AiLevel.Medium);
        foreach (var prop in typeof(SimpleAiParams).GetProperties().Where(p => p.PropertyType.IsValueType || p.PropertyType == typeof(double?)))
            Assert.True(Equals(prop.GetValue(d), prop.GetValue(m)), $"{prop.Name}: default {prop.GetValue(d)}, Medium {prop.GetValue(m)}");
    }

    // ai/09 §6 Easy column: slower reviews, pick noise, one offensive at a time, fewer scouts and spies; nothing that changes the rules or the view.
    [Fact]
    public void Easy_IsHandicappedOnlyByJudgment()
    {
        var m = AiLevels.For(AiLevel.Medium); var e = AiLevels.For(AiLevel.Easy);
        Assert.True(e.MilitaryEvery > m.MilitaryEvery && e.ProductionEvery > m.ProductionEvery && e.EconomyEvery > m.EconomyEvery);
        Assert.True(e.PickNoisePercent > 0 && m.PickNoisePercent == 0);
        Assert.Equal(1, e.MaxOffensives);
        Assert.True(e.ScoutCount < m.ScoutCount && e.HqSpies < m.HqSpies);
        Assert.Equal(m.UseFog, e.UseFog);
    }

    // ai/12 D3 (Paul 2026-10-12: Hard is smarter, never a cheat): Hard changes only judgment, a military review every day and probe droids on the
    // HQ hunt (bought at the normal price); every other parameter, the view included, is Medium's.
    [Fact]
    public void Hard_IsSmarterNotRicher()
    {
        var m = AiLevels.For(AiLevel.Medium); var h = AiLevels.For(AiLevel.Hard);
        Assert.True(h.MilitaryEvery < m.MilitaryEvery);
        Assert.True(h.HuntDroids > 0 && m.HuntDroids == 0);
        var changed = new HashSet<string> { nameof(SimpleAiParams.MilitaryEvery), nameof(SimpleAiParams.HuntDroids) };
        foreach (var prop in typeof(SimpleAiParams).GetProperties().Where(p => !changed.Contains(p.Name) && (p.PropertyType.IsValueType || p.PropertyType == typeof(string))))
            Assert.True(Equals(prop.GetValue(m), prop.GetValue(h)), $"{prop.Name}: Medium {prop.GetValue(m)}, Hard {prop.GetValue(h)}");
    }

    // A level sits on top of the base parameters the caller gives (a test or the runner may turn other switches).
    [Fact]
    public void Level_KeepsTheBaseParameters()
    {
        var b = new SimpleAiParams { UseFog = true, UseDeathStar = false };
        Assert.False(AiLevels.For(AiLevel.Easy, b).UseDeathStar);
        Assert.Equal(b, AiLevels.For(AiLevel.Medium, b));
    }

    [Theory]
    [InlineData("easy", AiLevel.Easy)]
    [InlineData("Normal", AiLevel.Medium)]
    [InlineData("MEDIUM", AiLevel.Medium)]
    [InlineData("hard", AiLevel.Hard)]
    public void Parse_ReadsTheLevelNames(string word, AiLevel level) => Assert.Equal(level, AiLevels.Parse(word));

    // ai/09 §6 row "concurrent offensive jobs": with MaxOffensives 1 the side never has two armed fleets (scouts aside) flying at once to
    // systems it does not hold. Measured on seed 2: 1 at most over 500 days.
    [Fact]
    public void Easy_RunsAtMostOneOffensiveAtATime()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 2, Preset = StartPreset.MedA });
        var easy = AiLevels.For(AiLevel.Easy);
        var m = new AiMatch(h, easy, easy);
        int worst = 0, launches = 0;
        for (int day = 0; day < 500; day++)
        {
            m.Step();
            foreach (var side in new[] { Side.Empire, Side.Alliance })
            {
                int n = h.Game.State.Fleets.Values.Count(f => f.Side == side && f.Ships.Count > 0 && f.Transit is { } t && h.Fleets.OwnerOf(t.Path[^1]) != side
                                                             && h.Fleets.StrengthOf(f) > 0 && !IsScout(h, f));
                worst = Math.Max(worst, n);
            }
            launches += m.Empire.Submitted.Concat(m.Alliance.Submitted).Count(o => o.Kind == "Move");
        }
        _out.WriteLine($"most armed fleets flying to systems not held at once: {worst}; moves {launches}");
        Assert.True(worst <= 1, $"{worst} armed fleets flew to systems not held at once");
        Assert.True(launches > 0);
    }

    private static bool IsScout(GameHost h, Rebellion.Core.Fleets.Fleet f) =>
        f.Ships.Count == 1 && h.Economy.Find(f.Ships[0]) is { } o && o.Class is "Galleon" or "Medium Transport";
}
