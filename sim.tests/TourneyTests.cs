using Praedra.Core.AI;

namespace Praedra.Sim.Tests;

// sim tourney (m6 AI trainer, ai/12 P11 headless AI-vs-AI harness): --hand-params / --collective-params set AI values for whichever level
// plays that side, on top of --a-params / --b-params. A tool, not a game rule (no sheet).
public class TourneyTests
{
    private static readonly SimpleAiParams A = Tourney.WithOverrides(AiLevels.For(AiLevel.Medium), "ScoutCount=7");
    private static readonly SimpleAiParams B = Tourney.WithOverrides(AiLevels.For(AiLevel.Hard), "ScoutCount=9");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SideParams_GoToTheSideWhicheverLevelPlaysIt(bool aIsHand)
    {
        var (e, a) = Tourney.SideParams(A, B, "FleetMemory=true", "MilitaryEvery=5")(aIsHand);
        Assert.True(e.FleetMemory);
        Assert.False(a.FleetMemory);
        Assert.Equal(5, a.MilitaryEvery);
        Assert.Equal((aIsHand ? A : B).MilitaryEvery, e.MilitaryEvery);
        Assert.Equal(aIsHand ? 7 : 9, e.ScoutCount); // the level's own values and --a-params / --b-params stay
        Assert.Equal(aIsHand ? 9 : 7, a.ScoutCount);
    }

    [Fact]
    public void SideParams_WithoutSideValues_AreTheLevelsAsBefore()
    {
        var sides = Tourney.SideParams(A, B, null, null);
        Assert.Equal((A, B), sides(true));
        Assert.Equal((B, A), sides(false));
    }

    // QA-28: a bad side value stopped the run inside the parallel games (exit 134, the stack trace twice). Now it is an ArgumentException
    // (sim prints "Error: ..." and the usage, exit 2) before any game, as for --a-params / --b-params.
    [Theory]
    [InlineData("Foo=1", "SimpleAiParams has no Foo.")]
    [InlineData("ScoutCount=abc", "ScoutCount must be an integer, not 'abc'.")]
    [InlineData("ScoutCount=", "ScoutCount must be an integer, not ''.")]
    [InlineData("ScoutCount", "ScoutCount needs a value (ScoutCount=...).")]
    [InlineData("FleetMemory=yes", "FleetMemory must be true or false, not 'yes'.")]
    public void SideParams_ABadValue_IsAnArgumentErrorBeforeAnyGame(string spec, string message)
    {
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Tourney.SideParams(A, B, spec, null)).Message);
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Tourney.SideParams(A, B, null, spec)).Message);
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Tourney.WithOverrides(A, spec)).Message);
    }

    [Fact]
    public void Run_ABadSideValue_IsAnArgumentErrorBeforeAnyGame()
    {
        var e = Assert.Throws<ArgumentException>(() => Tourney.Run(new[] { "--seeds", "1", "--days", "1", "--hand-params", "Foo=1" }));
        Assert.Equal("SimpleAiParams has no Foo.", e.Message);
    }
}
