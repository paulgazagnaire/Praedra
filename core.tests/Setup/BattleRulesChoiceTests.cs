using Rebellion.Core.AI;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;

namespace Rebellion.Core.Tests.Setup;

// The player picks the space battle rules at the start screen: Classic (DEFAULT) or New. rule: m4-battle-new §Rule (New) (ruleset switch, DEFAULT Classic)
public class BattleRulesChoiceTests
{
    private static GameSettings With(BattleRuleset r) => new() { Battle = new BattleParams { Ruleset = r } };

    private static GameHost NewGameWith(BattleRuleset? r) =>
        NewGame.Create(new NewGameOptions { Seed = 7, Settings = r is { } x ? With(x) : null });

    // m4-battle-new ruleset switch: DEFAULT Classic
    [Fact]
    public void DefaultNewGameIsClassic() =>
        Assert.Equal(BattleRuleset.Classic, NewGameWith(null).Battles.Params.Ruleset);

    // m4-battle-new ruleset switch: the save carries the ruleset
    [Theory]
    [InlineData(BattleRuleset.New)]
    [InlineData(BattleRuleset.Classic)]
    public void SaveWithSettingsKeepsTheRuleset(BattleRuleset r)
    {
        var host = NewGameWith(r);
        for (int i = 0; i < 5; i++) host.Game.Step();
        var loaded = GameHost.Load(host.SaveWithSettings());
        Assert.Equal(r, loaded.Battles.Params.Ruleset);
        Assert.Equal(host.StateHash(), loaded.StateHash());
    }

    // m4-battle-new ruleset switch: an old save (plain JSON, no header) loads as Classic
    [Fact]
    public void OldSaveWithoutHeaderLoadsAsClassic()
    {
        var host = NewGameWith(BattleRuleset.New);
        var loaded = GameHost.Load(host.Save());
        Assert.Equal(BattleRuleset.Classic, loaded.Battles.Params.Ruleset);
    }

    // settings passed to Load win over the header
    [Fact]
    public void ExplicitBattleSettingsWinOverTheHeader()
    {
        var host = NewGameWith(BattleRuleset.New);
        var loaded = GameHost.Load(host.SaveWithSettings(), With(BattleRuleset.Classic));
        Assert.Equal(BattleRuleset.Classic, loaded.Battles.Params.Ruleset);
    }

    private static (GameHost Host, int Battles) Play(BattleRuleset r, int days)
    {
        var host = NewGameWith(r);
        var match = new AiMatch(host, null, null);
        match.RunTo(days);
        return (host, host.Game.Events.OfType<BattleEnded>().Count());
    }

    // m4-battle-new: a new game with New fights through the New engine (the same game under Classic plays out differently) and AI vs AI runs clean
    [Fact]
    public void AiVsAiUnderNewFightsThroughTheNewEngine()
    {
        var (nw, nBattles) = Play(BattleRuleset.New, 1000);
        var (cl, _) = Play(BattleRuleset.Classic, 1000);
        Assert.True(nw.Game.Day > 0);
        Assert.True(nBattles > 0, "no battle in 1000 days");
        Assert.NotEqual(cl.StateHash(), nw.StateHash());
    }
}
