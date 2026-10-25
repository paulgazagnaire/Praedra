using Praedra.Core.Data;
using Praedra.Core.Setup;
using Xunit;

namespace Praedra.Core.Tests.Setup;

// Praedra names (data/design/name-migrations.csv, tools/names/apply_names.py): the data and code use the new names; a save written before
// the rename loads with every old name replaced.
public class NameMigrationTests
{
    [Fact]
    public void TheShippedTableLoadsAndNoNewNameIsAnOldOne()
    {
        var names = GameData.Default.Names;
        Assert.True(names.Count >= 676, $"{names.Count} renames");
        Assert.Equal("Charter", names.Current("Coruscant"));
        Assert.Equal("Mandate", names.Current("Imperial Star Destroyer"));
        Assert.Equal("Charter", names.Current("Charter")); // a new name stays itself
    }

    [Fact]
    public void NoOldNameIsLeftInTheGameData()
    {
        var d = GameData.Default;
        foreach (var s in d.Galaxy.Systems)
        {
            Assert.Equal(s.Name, d.Names.Current(s.Name));
            Assert.Equal(s.Sector, d.Names.Current(s.Sector));
        }
        foreach (var c in d.Ships.Keys) Assert.Equal(c, d.Names.Current(c));
    }

    [Fact]
    public void ASaveWithTheOldNamesLoadsRenamed()
    {
        var host = GameHost.New(7);
        for (int i = 0; i < 5; i++) host.Game.Step();
        string save = host.Save();
        // write the save as it looked before the rename: every new name in a JSON string back to its old name
        string old = save;
        foreach (var (from, to) in ReadPairs())
            old = old.Replace("\"" + to + "\"", "\"" + from + "\"", StringComparison.Ordinal);
        Assert.NotEqual(save, old);
        Assert.Contains("\"Coruscant\"", old);
        var loaded = GameHost.Load(old);
        Assert.Equal(host.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void TableErrorsAreReported()
    {
        Assert.Throws<DataLoadException>(() => NameMigrations.Parse("kind,old,new\nsystem,A,B\nsystem,A,C\n"));
        Assert.Throws<DataLoadException>(() => NameMigrations.Parse("kind,old,new\nsystem,A,B\nsystem,B,C\n"));
        Assert.Throws<DataLoadException>(() => NameMigrations.Parse("kind,old,new\nsystem,A,\n"));
    }

    private static IEnumerable<(string Old, string New)> ReadPairs()
    {
        var t = CsvTable.Parse(File.ReadAllText(Path.Combine(GameData.FindRepoRoot(), "data", "design", NameMigrations.FileName)), NameMigrations.FileName);
        for (int r = 0; r < t.RowCount; r++) yield return (t.Get(r, "old"), t.Get(r, "new"));
    }
}
