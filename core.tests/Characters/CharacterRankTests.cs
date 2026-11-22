using Praedra.Core.Characters;

namespace Praedra.Core.Tests.Characters;

/// <summary>Display only: a leader's rank is not shown as part of their name (Paul 2026-11-22). data/design/character-ranks.csv.</summary>
public class CharacterRankTests
{
    private static CharacterData Real() => CharacterData.Load(TestData.ClassicDir, TestData.DesignDir);

    // the ranks table: the rank comes off the shown name, the full name stays for rules, saves and victory keys
    [Fact]
    public void RankIsSplitOffTheShownName()
    {
        var d = Real();
        var lucan = d.Get(897);
        Assert.Equal("Admiral Lucan Mordant", lucan.Name);
        Assert.Equal("Admiral", lucan.Rank);
        Assert.Equal("Lucan Mordant", lucan.ShownName);
        var cyprian = d.Get(896);
        Assert.Equal("Project Director", cyprian.Rank);
        Assert.Equal("Cyprian Galena", cyprian.ShownName);
    }

    // a name with no rank is shown whole
    [Fact]
    public void NameWithoutRankIsShownWhole()
    {
        var d = Real();
        var c = d.All.First(c => c.Name == "Mae Abara");
        Assert.Equal("", c.Rank);
        Assert.Equal("Mae Abara", c.ShownName);
    }

    // no shown name still starts with a known rank word, except where the rank is the name itself in the lore ("Captain Sunday",
    // specs/creative/14-characters.md heading; he is no ship's captain)
    [Fact]
    public void NoShownNameStartsWithARank()
    {
        var d = Real();
        var ranks = d.All.Select(c => c.Rank).Where(r => r.Length > 0).Distinct().ToList();
        var loreNames = new[] { "Captain Sunday" };
        foreach (var c in d.Roster.Where(c => !loreNames.Contains(c.ShownName)))
            Assert.False(ranks.Any(r => c.ShownName.StartsWith(r + " ", StringComparison.Ordinal)), $"{c.Id} '{c.ShownName}' still starts with a rank");
    }

    // the table must match the record's name exactly, so a rename can't show a wrong name
    [Fact]
    public void MismatchedRankRowFailsTheLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "praedra-ranks-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(TestData.DesignDir, "*.csv")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(dir, CharacterData.RanksFile), "id,rank,name\n897,Captain,Lucan Mordant\n");
        try { Assert.Throws<Praedra.Core.Data.DataLoadException>(() => CharacterData.Load(TestData.ClassicDir, dir)); }
        finally { Directory.Delete(dir, true); }
    }
}
