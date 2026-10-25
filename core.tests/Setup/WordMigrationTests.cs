using Praedra.Core.Setup;
using Xunit;

namespace Praedra.Core.Tests.Setup;

// Praedra names, stage 3 (data/design/word-migrations.csv, tools/names/rename_words.py): the side words were renamed inside identifiers
// (Side.Empire -> Side.Hand, EmpireJitterMax -> HandJitterMax); a save written before keeps its settings and state.
public class WordMigrationTests
{
    [Theory]
    [InlineData("Empire", "Hand")]
    [InlineData("Alliance", "Collective")]
    [InlineData("EmpireWinsWhenAllianceHasNoSystems", "HandWinsWhenCollectiveHasNoSystems")]
    [InlineData("RebelFriendlyBias", "CollectiveFriendlyBias")]
    [InlineData("EmpireAiKnowsRebelHq", "HandAiKnowsHearth")]
    [InlineData("allianceSupport", "collectiveSupport")]
    [InlineData("Empires", "Empires")] // a word followed by a lower-case letter is not the word
    [InlineData("Charter", "Charter")]
    [InlineData("ForceExperience", "TalentExperience")] // stage 3d: the Force words
    [InlineData("DiscoveredForceUser", "DiscoveredTalentHolder")]
    [InlineData("TalentedForceMin", "TalentedMin")]
    [InlineData("SpecialForces", "SpecialForces")] // military forces keep their word
    [InlineData("SpecialForce", "SpecialForce")]
    [InlineData("ForcedStop", "ForcedStop")]
    [InlineData("Imperial Star", "Imperial Star")] // not an identifier: names go through the name table only
    public void IdentifiersAreRenamedWordByWord(string old, string expected) =>
        Assert.Equal(expected, GameData.Default.Names.CurrentIdentifier(old));

    [Fact]
    public void ASaveWithTheOldWordsKeepsItsSettings()
    {
        var host = GameHost.New(7, new GameSettings { Deal = new DealParams { HandJitterMax = 123, CollectiveJitterMax = 45 } });
        for (int i = 0; i < 3; i++) host.Game.Step();
        string save = host.SaveWithSettings();
        // write the save as it looked before: the new side words back to the old ones in every key and value
        string old = save.Replace("\"HandJitterMax\"", "\"EmpireJitterMax\"").Replace("\"CollectiveJitterMax\"", "\"AllianceJitterMax\"")
            .Replace("\"Hand\"", "\"Empire\"").Replace("\"Collective\"", "\"Alliance\"");
        Assert.Contains("\"EmpireJitterMax\":123", old);
        var loaded = GameHost.Load(old);
        Assert.Equal(123, loaded.Settings.Deal!.HandJitterMax);
        Assert.Equal(45, loaded.Settings.Deal!.CollectiveJitterMax);
        Assert.Equal(host.StateHash(), loaded.StateHash());
    }
}
