using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Economy;
using Rebellion.Core.Setup;
using Rebellion.Core.Victory;

namespace Rebellion.Core.Tests.Setup;

// m5 new game (setup-victory/09 day-one files, setup-victory/04 victory). Rule sheet m5-setup-and-victory not yet in the project folder; these
// tests check the day-one CSVs, the DEFAULT forces table and the HQ-Only victory.
public class NewGameTests
{
    private static GameHost Make(StartPreset p, int seed = 1, GameSettings? s = null) =>
        NewGame.Create(GameData.Default, new NewGameOptions { Seed = seed, Preset = p, Settings = s });

    private static int Held(GameHost h, Side s) => h.Game.State.SystemOwners.Count(kv => kv.Value == s);

    // totals of setup-victory/09 §3 (Alliance, Empire)
    public static IEnumerable<object[]> Totals => new[]
    {
        new object[] { StartPreset.Easy, 18, 14 }, new object[] { StartPreset.MedA, 29, 36 }, new object[] { StartPreset.MedE, 43, 22 },
        new object[] { StartPreset.HardA, 22, 43 }, new object[] { StartPreset.HardE, 50, 14 },
    };

    [Theory, MemberData(nameof(Totals))]
    public void DayOneOwnersMatchTheCsv(StartPreset p, int alliance, int empire)
    {
        var h = Make(p);
        Assert.Equal(alliance, Held(h, Side.Alliance));
        Assert.Equal(empire, Held(h, Side.Empire));
        Assert.Equal(Side.Empire, h.Control.HolderOf("Coruscant"));
        Assert.Equal("Dagobah", h.HqSystem);
        Assert.Equal(Side.Alliance, h.Control.HolderOf("Dagobah"));
        Assert.Equal(100, h.Economy.SupportOf("Yavin", Side.Alliance));
        Assert.True(h.IsExplored("Coruscant", Side.Alliance) && h.IsExplored("Coruscant", Side.Empire));
        Assert.True(h.IsExplored("Dagobah", Side.Alliance) && !h.IsExplored("Dagobah", Side.Empire));
        Assert.Null(h.Victory.Winner);
    }

    [Theory]
    [InlineData(StartPreset.Easy)] [InlineData(StartPreset.MedA)] [InlineData(StartPreset.MedE)] [InlineData(StartPreset.HardA)] [InlineData(StartPreset.HardE)]
    public void BothSidesCanPlay(StartPreset p)
    {
        var h = Make(p);
        foreach (var side in new[] { Side.Empire, Side.Alliance })
        {
            bool Has(FacilityKind k) => h.Economy.Objects.Any(o => o.Completed && o.Class == FacilityKinds.ClassName(k) && h.Economy.OwnerOf(o) == side);
            Assert.True(Has(FacilityKind.OrbitalShipyard), $"{side} yard");
            Assert.True(Has(FacilityKind.TrainingFacility), $"{side} training");
            Assert.True(Has(FacilityKind.Mine) && Has(FacilityKind.Refinery), $"{side} economy");
            Assert.True(h.Economy.Free(side) > 0, $"{side} maintenance room");
            Assert.Contains(h.Game.State.Fleets.Values, f => f.Side == side);
        }
    }

    [Fact]
    public void SameSeedSameHashAndDifferentSeedDiffers()
    {
        Assert.Equal(Make(StartPreset.MedA, 7).StateHash(), Make(StartPreset.MedA, 7).StateHash());
        Assert.NotEqual(Make(StartPreset.MedA, 7).StateHash(), Make(StartPreset.MedA, 8).StateHash());
    }

    [Theory]
    [InlineData(StartPreset.MedA)]
    [InlineData(StartPreset.HardE)]
    public void Year_RunsWithoutInvariantViolation(StartPreset p)
    {
        var h = Make(p);
        CampaignInvariants.Check(h, "day 0");
        for (int i = 0; i < 365; i++)
        {
            h.Game.Step();
            CampaignInvariants.Check(h, $"day {h.Game.Day}");
        }
        Assert.Null(h.Victory.Winner);
    }

    private static void CaptureOnNextStep(GameHost h, string system, Side side)
    {
        int day = h.Game.Day;
        h.Game.RegisterHook("5.2", ctx => { if (ctx.Day == day) h.Control.SetHolder(ctx, system, side, CaptureCause.Debug); });
    }

    [Fact]
    public void EmpireWinsWhenHqCaptured()
    {
        var h = Make(StartPreset.MedA);
        h.Game.Step(); Assert.Null(h.Victory.Winner);
        CaptureOnNextStep(h, "Dagobah", Side.Empire);
        h.Game.Step();
        Assert.Equal(Side.Empire, h.Victory.Winner);
        var won = h.Game.Events.OfType<GameWon>().Single();
        Assert.Equal(Side.Empire, won.Winner);
        h.Game.Step(); h.Game.Step();
        Assert.Single(h.Game.Events.OfType<GameWon>());
    }

    [Fact]
    public void AllianceWinsWhenCoruscantCaptured()
    {
        var h = Make(StartPreset.MedE);
        CaptureOnNextStep(h, "Coruscant", Side.Alliance);
        h.Game.Step();
        Assert.Equal(Side.Alliance, h.Victory.Winner);
        Assert.Single(h.Game.Events.OfType<GameWon>());
    }

    [Fact]
    public void SwitchesTurnConditionsOff()
    {
        var h = Make(StartPreset.MedA, 1, new GameSettings { Victory = new VictoryRules { EmpireWinsOnHqLost = false } });
        CaptureOnNextStep(h, "Dagobah", Side.Empire);
        h.Game.Step();
        Assert.Null(h.Victory.Winner);
    }

    [Fact]
    public void EmpireWinsWhenAllianceHoldsNothing()
    {
        var h = Make(StartPreset.Easy, 1, new GameSettings { Victory = new VictoryRules { EmpireWinsOnHqLost = false } });
        var held = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance).Select(kv => kv.Key).ToList();
        h.Game.RegisterHook("5.2", ctx => { foreach (var s in held) h.Control.SetHolder(ctx, s, Side.Empire, CaptureCause.Debug); });
        h.Game.Step();
        Assert.Equal(Side.Empire, h.Victory.Winner);
    }

    [Fact]
    public void BareGameHostNeverEndsOnDayOne()
    {
        var h = GameHost.New(1);
        h.Game.Step();
        Assert.Null(h.Victory.Winner);
    }

    [Fact]
    public void SaveLoadKeepsHqExploredAndWinner()
    {
        var h = Make(StartPreset.MedA);
        CaptureOnNextStep(h, "Dagobah", Side.Empire);
        h.Game.Step();
        string save = h.Save();
        var l = GameHost.Load(save);
        Assert.Equal("Dagobah", l.HqSystem);
        Assert.Equal(Side.Empire, l.Victory.Winner);
        Assert.True(l.IsExplored("Dagobah", Side.Alliance));
        Assert.Equal(h.StateHash(), l.StateHash());
        Assert.Equal(save, l.Save());
    }

    [Fact]
    public void LoadedGameContinuesLikeTheOriginal()
    {
        var a = Make(StartPreset.HardA);
        for (int i = 0; i < 30; i++) a.Game.Step();
        var b = GameHost.Load(a.Save());
        for (int i = 0; i < 30; i++) { a.Game.Step(); b.Game.Step(); }
        Assert.Equal(a.StateHash(), b.StateHash());
    }
}
