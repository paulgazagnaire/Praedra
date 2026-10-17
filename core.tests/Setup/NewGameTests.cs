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
    // These tests check the fixed day-one CSVs plus fixed-owners.csv, so they run in DealMode.File (the Anchored deal is in AnchoredDealTests).
    private static GameHost Make(StartPreset p, int seed = 1, GameSettings? s = null) =>
        NewGame.Create(GameData.Default, new NewGameOptions { Seed = seed, Preset = p, Settings = (s ?? new GameSettings()) with { DealMode = DealMode.File } });

    private static int Held(GameHost h, Side s) => h.Game.State.SystemOwners.Count(kv => kv.Value == s);

    // Owners of the day-one CSV with the seven fixed owners on top (m5-setup-and-victory §Fixed owners).
    private static (int Alliance, int Empire) Expected(StartPreset p)
    {
        var owners = NewGameData.Default.DayOne[p].ToDictionary(r => r.Name, r => r.Owner);
        foreach (var (sys, o) in NewGameData.Default.FixedOwners) owners[sys] = o;
        return (owners.Values.Count(v => v == Side.Alliance), owners.Values.Count(v => v == Side.Empire));
    }
    public static IEnumerable<object[]> Totals => Enum.GetValues<StartPreset>().Select(p => new object[] { p, Expected(p).Alliance, Expected(p).Empire });

    [Theory, MemberData(nameof(Totals))]
    public void DayOneOwnersMatchTheCsv(StartPreset p, int alliance, int empire)
    {
        var h = Make(p);
        Assert.Equal(alliance, Held(h, Side.Alliance));
        Assert.Equal(empire, Held(h, Side.Empire));
        Assert.Equal(Side.Empire, h.Control.HolderOf("Coruscant"));
        Assert.Equal("Cyclor", h.HqSystem);
        Assert.Equal(Side.Alliance, h.Control.HolderOf("Cyclor"));
        Assert.Equal(100, h.Economy.SupportOf("Yavin", Side.Alliance));
        Assert.True(h.IsExplored("Coruscant", Side.Alliance) && h.IsExplored("Coruscant", Side.Empire));
        Assert.True(h.IsExplored("Cyclor", Side.Alliance) && !h.IsExplored("Cyclor", Side.Empire));
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

    [Theory, MemberData(nameof(Totals))]
    public void FixedOwnersAndSupportBands(StartPreset p, int a, int e)
    {
        var h = Make(p);
        Assert.Equal(a, Held(h, Side.Alliance)); Assert.Equal(e, Held(h, Side.Empire));
        foreach (var s in new[] { "Kuat", "Fondor", "Corellia", "Rendili" }) Assert.Equal(Side.Empire, h.Control.HolderOf(s));
        foreach (var s in new[] { "Sullust", "Mon Calamari", "Bilbringi" }) Assert.Null(h.Control.HolderOf(s));
        Assert.Equal(0, h.Economy.SupportOf("Coruscant", Side.Alliance));
        Assert.Equal(100, h.Economy.SupportOf("Cyclor", Side.Alliance));
        Assert.Equal(100, h.Economy.SupportOf("Yavin", Side.Alliance));
        // B4 2026-10-17: the garrison is exactly the P6 N for the holder's support, with no minimum (min_garrison 0 in Classic, B4 §6); before B4
        // every held system got at least one regiment, so this asserted "some regiment on Rendili".
        int rendiliN = Rebellion.Core.Support.SupportModule.RequiredGarrison(h.Support.Params, h.Economy.SupportOf("Rendili", Side.Empire), false, false);
        Assert.Equal(rendiliN, h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.System == "Rendili" && o.Side == Side.Empire));
        Assert.Contains(h.Economy.Objects, o => o.Class == "Orbital Shipyard" && o.System == "Kuat");
    }

    [Fact]
    public void GameOverFreezesStepAndRejectsOrders()
    {
        var h = Make(StartPreset.MedA, 1, OldVictory.Settings);
        CaptureOnNextStep(h, "Cyclor", Side.Empire);
        h.Game.Step();
        Assert.True(h.Game.IsOver);
        int day = h.Game.Day, events = h.Game.Events.Count;
        h.Game.Step();
        Assert.Equal(day, h.Game.Day); Assert.Equal(events, h.Game.Events.Count);
        long seq = h.Game.Submit(Side.Alliance, "x", 1);
        var rej = Assert.Single(h.Game.Events.OfType<OrderRejected>().Where(e => e.Sequence == seq));
        Assert.Equal(RejectReason.GameOver, rej.Reason);
        Assert.Empty(h.Game.Queue);
        Assert.True(GameHost.Load(h.Save()).Game.IsOver);
    }

    [Fact]
    public void VictoryIsTheLastDayEndSlot() =>
        Assert.Equal("6.11", Game.WorkList[^2]);

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
        var h = Make(StartPreset.MedA, 1, OldVictory.Settings);
        h.Game.Step(); Assert.Null(h.Victory.Winner);
        CaptureOnNextStep(h, "Cyclor", Side.Empire);
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
        var h = Make(StartPreset.MedE, 1, OldVictory.Settings);
        CaptureOnNextStep(h, "Coruscant", Side.Alliance);
        h.Game.Step();
        Assert.Equal(Side.Alliance, h.Victory.Winner);
        Assert.Single(h.Game.Events.OfType<GameWon>());
    }

    [Fact]
    public void SwitchesTurnConditionsOff()
    {
        var h = Make(StartPreset.MedA, 1, new GameSettings { Victory = new VictoryRules { EmpireWinsOnHqLost = false } });
        CaptureOnNextStep(h, "Cyclor", Side.Empire);
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
        var h = Make(StartPreset.MedA, 1, OldVictory.Settings);
        CaptureOnNextStep(h, "Cyclor", Side.Empire);
        h.Game.Step();
        string save = h.Save();
        var l = GameHost.Load(save);
        Assert.Equal("Cyclor", l.HqSystem);
        Assert.Equal(Side.Empire, l.Victory.Winner);
        Assert.True(l.IsExplored("Cyclor", Side.Alliance));
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
