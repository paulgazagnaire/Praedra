using Rebellion.Core.Clock;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using static Rebellion.Core.Tests.Missions.MissionWorld;

namespace Rebellion.Core.Tests.Missions;

// B1-own-importer QA Q4 to Q7: the full mission tables and the decoy table read from the 1998 files (data/original/mission-tables.csv).
public class OriginalTablesTests
{
    private static int Step(string table, int x) =>
        MissionModule.Lookup(GameData.Default.Missions.Table(table), x, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue);

    // B1-own-importer Q5: Espionage at skill 15, 50 and 100 in Step mode (the DEFAULT lookup)
    [Fact]
    public void Q5_EspionageStepLookup() => Assert.Equal((10, 50, 99), (Step("ESPIMSTB", 15), Step("ESPIMSTB", 50), Step("ESPIMSTB", 100)));

    // B1-own-importer Q7: Diplomacy at -35 (row -39) and 45 (row 40)
    [Fact]
    public void Q7_DiplomacyStepLookup() => Assert.Equal((1, 60), (Step("DIPLMSTB", -35), Step("DIPLMSTB", 45)));

    // B1-own-importer Q4: every success table, FOILTB and the decoy table loaded in full
    [Fact]
    public void Q4_FullTablesLoaded()
    {
        var m = GameData.Default.Missions;
        Assert.Equal((10, 12, 12, 12, 12, 11, 12, 12, 13, 13, 14, 14),
            (m.Table("DIPLMSTB").Count, m.Table("ESPIMSTB").Count, m.Table("SBTGMSTB").Count, m.Table("RESCMSTB").Count, m.Table("DSSBMSTB").Count,
             m.Table("RCRTMSTB").Count, m.Table("ABDCMSTB").Count, m.Table("ASSNMSTB").Count, m.Table("INCTMSTB").Count, m.Table("SUBDMSTB").Count,
             m.Foil.Count, m.DecoyTable!.Count));
    }

    // B1-own-importer Q6: decoy table chances at x = 40, 0 and -20
    [Fact]
    public void Q6_DecoyTableChances()
    {
        var d = GameData.Default.Missions.DecoyTable!;
        int L(int x) => MissionModule.Lookup(d, x, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue);
        Assert.Equal((90, 50, 10), (L(40), L(0), L(-20)));
    }

    // B1-own-importer Q6: with the shipped data a mission with one decoy is accepted and its MissionDecoyRoll uses the decoy table
    // (Infiltrators espionage 60 against one Imperial Fleet Regiment, detection 20: x = 40, chance 90)
    [Fact]
    public void Q6_OneDecoyIsAcceptedWithTheShippedData()
    {
        var w = new MissionWorld();
        Assert.NotNull(w.Host.Data.Missions.DecoyTable);
        w.Hold(T, Side.Empire, 0);
        w.Agent(Leia, T, esp: 50);
        int inf = w.Unit(Side.Alliance, Infiltrators, T);
        w.Unit(Side.Empire, ImpFleet, T);
        w.Stub!.Expect(0, 99, Bt(2, 89, 99)); // B2b: two betrayal draws (Leia, the decoy) first
        var seq = w.Start(Side.Alliance, MissionKind.Espionage, new[] { Leia }, T, decoys: new[] { inf });
        w.Game.Step();
        Assert.Null(w.Reason(seq));
        w.RunIteration(w.LastMission);
        var d = Assert.Single(w.Events<MissionDecoyRoll>());
        Assert.Equal((40, 90, 89, true), (d.X, d.Chance, d.Roll, d.Decoyed));
    }
}
