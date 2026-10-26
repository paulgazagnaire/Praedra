using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

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

    // ai/09 §6: Medium is the SimpleAiParams defaults with fog on, plus the Collective's guerrilla switches (AI trainer 2026-10-26, Paul: Medium
    // is balanced). The switches are DEFAULT off so the play tests pinned on the default params keep measuring what they measured; so those tests
    // measure Medium's Hand, and a Collective without the guerrilla. The Hand never reads the switches.
    private static readonly string[] Guerrilla = { nameof(SimpleAiParams.SafeHearth), nameof(SimpleAiParams.Underground), nameof(SimpleAiParams.Guerrilla) };

    [Fact]
    public void Medium_IsTheDefaultAiPlusTheGuerrilla()
    {
        var d = new SimpleAiParams { UseFog = true }; var m = AiLevels.For(AiLevel.Medium);
        foreach (var prop in typeof(SimpleAiParams).GetProperties().Where(p => !Guerrilla.Contains(p.Name) && (p.PropertyType.IsValueType || p.PropertyType == typeof(double?))))
            Assert.True(Equals(prop.GetValue(d), prop.GetValue(m)), $"{prop.Name}: default {prop.GetValue(d)}, Medium {prop.GetValue(m)}");
        Assert.True(m.SafeHearth && m.Underground && m.Guerrilla);
        Assert.False(d.SafeHearth || d.Underground || d.Guerrilla);
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

    // ai/12 D3 (Paul 2026-10-12: Hard is smarter, never a cheat): Hard changes only judgment, a military review every day and probe speculators on
    // the HQ hunt (bought at the normal price); every other parameter, the view included, is Medium's. (Finishing the war when far ahead was
    // Hard only until 2026-10-18; it is now every level's DEFAULT, see SimpleAiParams.FinishWhenAheadRatio.)
    [Fact]
    public void Hard_IsSmarterNotRicher()
    {
        var m = AiLevels.For(AiLevel.Medium); var h = AiLevels.For(AiLevel.Hard);
        Assert.True(h.MilitaryEvery < m.MilitaryEvery);
        Assert.True(h.HuntSpeculators > 0 && m.HuntSpeculators == 0);
        // AI trainer 2026-10-26: the Hard Collective keeps more agents out and raids more (Collective-only values; the Hand never reads them)
        var changed = new HashSet<string> { nameof(SimpleAiParams.MilitaryEvery), nameof(SimpleAiParams.HuntSpeculators),
                                            nameof(SimpleAiParams.UndergroundForcesWanted), nameof(SimpleAiParams.UndergroundSubversion),
                                            nameof(SimpleAiParams.WatchLooks), nameof(SimpleAiParams.RaidStrikes) };
        foreach (var prop in typeof(SimpleAiParams).GetProperties().Where(p => !changed.Contains(p.Name) && (p.PropertyType.IsValueType || p.PropertyType == typeof(string))))
            Assert.True(Equals(prop.GetValue(m), prop.GetValue(h)), $"{prop.Name}: Medium {prop.GetValue(m)}, Hard {prop.GetValue(h)}");
    }

    // A level sits on top of the base parameters the caller gives (a test or the runner may turn other switches).
    [Fact]
    public void Level_KeepsTheBaseParameters()
    {
        var b = new SimpleAiParams { UseFog = true, UseHarvester = false };
        Assert.False(AiLevels.For(AiLevel.Easy, b).UseHarvester);
        Assert.Equal(b with { SafeHearth = true, Underground = true, Guerrilla = true }, AiLevels.For(AiLevel.Medium, b));
    }

    [Theory]
    [InlineData("easy", AiLevel.Easy)]
    [InlineData("Normal", AiLevel.Medium)]
    [InlineData("MEDIUM", AiLevel.Medium)]
    [InlineData("hard", AiLevel.Hard)]
    public void Parse_ReadsTheLevelNames(string word, AiLevel level) => Assert.Equal(level, AiLevels.Parse(word));

    // ai/09 §6 row "concurrent offensive jobs": with MaxOffensives 1 the side never has two armed fleets (scouts aside) flying at once to
    // systems it does not hold. Measured on seed 2: 1 at most over 500 days.
    // A leg that only stops on the way to a held system is no offensive (2026-10-17, merge of main #57 into m0/importer: on seed 2 the Hand
    // sent fleets 329 on day 391 and 557 on day 397 to relieve Ilanga, a held system under threat; the way was blocked, so each order flew the
    // first leg of the way around, to neutral Novy Rynek, and the two were counted as two offensives at once on days 397-398. Relief is allowed
    // at any count, SimpleAiParams.MaxOffensives).
    [Fact]
    public void Easy_RunsAtMostOneOffensiveAtATime()
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 2, Preset = StartPreset.MedA });
        var easy = AiLevels.For(AiLevel.Easy);
        var m = new AiMatch(h, easy, easy);
        int worst = 0, launches = 0;
        var goal = new Dictionary<int, string>(); // fleet id -> the goal of its last trip that stops on the way
        for (int day = 0; day < 500; day++)
        {
            m.Step();
            foreach (var o in m.Hand.Submitted.Concat(m.Collective.Submitted).Where(o => o.Kind == "Move"))
            {
                var w = o.Detail.Split(' ');
                int id = int.Parse(w[1]); int k = o.Detail.IndexOf(SimpleAi.OnTheWayTo, StringComparison.Ordinal);
                if (k >= 0) goal[id] = o.Detail[(k + SimpleAi.OnTheWayTo.Length)..]; else goal.Remove(id);
            }
            foreach (var side in new[] { Side.Hand, Side.Collective })
            {
                int n = h.Game.State.Fleets.Values.Count(f => f.Side == side && f.Ships.Count > 0 && f.Transit is { } t && h.Fleets.OwnerOf(t.Path[^1]) != side
                                                             && !(goal.TryGetValue(f.Id, out var g) && h.Fleets.OwnerOf(g) == side)
                                                             && h.Fleets.StrengthOf(f) > 0 && !IsScout(h, f));
                worst = Math.Max(worst, n);
            }
            launches += m.Hand.Submitted.Concat(m.Collective.Submitted).Count(o => o.Kind == "Move");
        }
        _out.WriteLine($"most armed fleets flying to systems not held at once: {worst}; moves {launches}");
        Assert.True(worst <= 1, $"{worst} armed fleets flew to systems not held at once");
        Assert.True(launches > 0);
    }

    private static bool IsScout(GameHost h, Praedra.Core.Fleets.Fleet f) =>
        f.Ships.Count == 1 && h.Economy.Find(f.Ships[0]) is { } o && o.Class is "Bond" or "Mule";
}
