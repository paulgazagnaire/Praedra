using Praedra.Core.AI;

namespace Praedra.Sim.Tests;

// sim tourney (m6 AI trainer, ai/12 P11 headless AI-vs-AI harness): --empire-params / --alliance-params set AI values for whichever level
// plays that side, on top of --a-params / --b-params. A tool, not a game rule (no sheet).
public class TourneyTests
{
    private static readonly SimpleAiParams A = Tourney.WithOverrides(AiLevels.For(AiLevel.Medium), "ScoutCount=7");
    private static readonly SimpleAiParams B = Tourney.WithOverrides(AiLevels.For(AiLevel.Hard), "ScoutCount=9");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SideParams_GoToTheSideWhicheverLevelPlaysIt(bool aIsEmpire)
    {
        var (e, a) = Tourney.SideParams(aIsEmpire, A, B, "FleetMemory=true", "MilitaryEvery=5");
        Assert.True(e.FleetMemory);
        Assert.False(a.FleetMemory);
        Assert.Equal(5, a.MilitaryEvery);
        Assert.Equal((aIsEmpire ? A : B).MilitaryEvery, e.MilitaryEvery);
        Assert.Equal(aIsEmpire ? 7 : 9, e.ScoutCount); // the level's own values and --a-params / --b-params stay
        Assert.Equal(aIsEmpire ? 9 : 7, a.ScoutCount);
    }

    [Fact]
    public void SideParams_WithoutSideValues_AreTheLevelsAsBefore()
    {
        Assert.Equal((A, B), Tourney.SideParams(true, A, B, null, null));
        Assert.Equal((B, A), Tourney.SideParams(false, A, B, null, null));
    }
}
