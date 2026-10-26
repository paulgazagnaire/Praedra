using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Praedra.Core.Victory;
using Praedra.Core.Tests.Control;

namespace Praedra.Core.Tests.Setup;

// m5-setup-and-victory §Victory, capital and control (Decided Paul 2026-10-26, "Capital + 70%"): with the enemy capital gone, holding at least
// DominanceSharePercent of the systems the two sides hold for DominanceDays days running wins. Tests V20 to V25.
public class DominanceVictoryTests
{
    private const string FileHq = "Altofen"; // the day-one files' Hearth (NewGameTests)

    private static GameHost Make(StartPreset p, VictoryRules rules) =>
        NewGame.Create(GameData.Default, new NewGameOptions { Seed = 1, Preset = p, Settings = new GameSettings { DealMode = DealMode.File, Victory = rules } });

    private static void CaptureOnNextStep(GameHost h, IEnumerable<string> systems, Side side)
    {
        int day = h.Game.Day; var list = systems.ToList();
        h.Game.RegisterHook("5.2", ctx => { if (ctx.Day == day) foreach (var s in list) h.Control.CaptureNow(ctx, s, side); });
    }

    private static List<string> HeldBy(GameHost h, Side s) =>
        h.Game.State.SystemOwners.Where(kv => kv.Value == s).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    private static int Share(GameHost h, Side s) => HeldBy(h, s).Count * 100 / (HeldBy(h, Side.Hand).Count + HeldBy(h, Side.Collective).Count);

    [Fact] // V20
    public void HandWinsAfterHoldingTheShareForTheWholeRunOnceTheHearthIsGone()
    {
        var h = Make(StartPreset.MedA, new VictoryRules { DominanceDays = 5 });
        // the Hearth and half the other Collective worlds: the Hearth is destroyed (capture, m5-fog-and-hq §F7), the leaders are free
        var take = HeldBy(h, Side.Collective).Where(s => s != FileHq).Where((_, i) => i % 2 == 0).Append(FileHq);
        CaptureOnNextStep(h, take, Side.Hand);
        h.Game.Step();
        Assert.Null(h.Victory.Winner); // standard victory still needs the two leaders captive
        Assert.True(Share(h, Side.Hand) >= 70, $"Hand share {Share(h, Side.Hand)}%");
        var started = h.Game.Events.OfType<DominanceChanged>().Single();
        Assert.Equal(Side.Hand, started.Leader);
        Assert.Equal(started.StartDay + 5, started.DueDay);
        Assert.Equal(started.DueDay, h.Victory.DominanceDueDay);
        while (h.Victory.Winner == null && h.Game.Day < 20) h.Game.Step();
        Assert.Equal(Side.Hand, h.Victory.Winner);
        var won = h.Game.Events.OfType<GameWon>().Single();
        Assert.Equal(started.DueDay, won.Day);
        Assert.Contains("after the Hearth fell", won.Reason);
    }

    [Fact] // V21
    public void NoRunWhileTheEnemyCapitalStands()
    {
        var h = Make(StartPreset.MedA, new VictoryRules { DominanceDays = 5 });
        CaptureOnNextStep(h, HeldBy(h, Side.Collective).Where(s => s != FileHq).Where((_, i) => i % 2 == 0), Side.Hand);
        for (int i = 0; i < 10; i++) h.Game.Step();
        Assert.Null(h.Victory.Winner);
        Assert.Empty(h.Game.Events.OfType<DominanceChanged>());
        Assert.Null(h.Victory.DominanceDueDay);
    }

    [Fact] // V22
    public void FallingBelowTheShareBreaksTheRun()
    {
        var h = Make(StartPreset.MedA, new VictoryRules { DominanceDays = 5 });
        CaptureOnNextStep(h, HeldBy(h, Side.Collective).Where(s => s != FileHq).Where((_, i) => i % 2 == 0).Append(FileHq), Side.Hand);
        h.Game.Step();
        // hand a third of the Hand's worlds (never Charter) to the Collective: below 70%
        CaptureOnNextStep(h, HeldBy(h, Side.Hand).Where(s => s != "Charter").Where((_, i) => i % 3 == 0), Side.Collective);
        h.Game.Step();
        Assert.True(Share(h, Side.Hand) < 70, $"Hand share {Share(h, Side.Hand)}%");
        var changes = h.Game.Events.OfType<DominanceChanged>().ToList();
        Assert.Equal(2, changes.Count);
        Assert.Null(changes[1].Leader);
        Assert.Null(h.Victory.DominanceDueDay);
        for (int i = 0; i < 10; i++) h.Game.Step();
        Assert.Null(h.Victory.Winner);
    }

    [Fact] // V23
    public void CollectiveWinsAfterCharterFallsAndItHoldsTheShare()
    {
        var h = Make(StartPreset.MedA, new VictoryRules { DominanceDays = 5 });
        // Charter and all but a tenth of the Hand's worlds go to the Collective; Ravilla and Chair stay free
        var take = HeldBy(h, Side.Hand).Where((s, i) => s == "Charter" || i % 10 != 0);
        CaptureOnNextStep(h, take, Side.Collective);
        h.Game.Step();
        Assert.Null(h.Victory.Winner);
        Assert.True(Share(h, Side.Collective) >= 70, $"Collective share {Share(h, Side.Collective)}%");
        while (h.Victory.Winner == null && h.Game.Day < 20) h.Game.Step();
        Assert.Equal(Side.Collective, h.Victory.Winner);
        Assert.Contains("after Charter fell", h.Game.Events.OfType<GameWon>().Single().Reason);
    }

    [Fact] // V24
    public void ZeroShareTurnsTheRuleOff()
    {
        var h = Make(StartPreset.MedA, new VictoryRules { DominanceDays = 5, DominanceSharePercent = 0 });
        CaptureOnNextStep(h, HeldBy(h, Side.Collective).Where(s => s != FileHq).Where((_, i) => i % 2 == 0).Append(FileHq), Side.Hand);
        for (int i = 0; i < 10; i++) h.Game.Step();
        Assert.Null(h.Victory.Winner);
        Assert.Empty(h.Game.Events.OfType<DominanceChanged>());
    }

    [Fact] // V25
    public void SaveKeepsTheRunAndLeavesItOutWhenUnset()
    {
        var fresh = Make(StartPreset.MedA, new VictoryRules());
        Assert.DoesNotContain("Dominance", fresh.Save());
        var h = Make(StartPreset.MedA, new VictoryRules { DominanceDays = 5 });
        CaptureOnNextStep(h, HeldBy(h, Side.Collective).Where(s => s != FileHq).Where((_, i) => i % 2 == 0).Append(FileHq), Side.Hand);
        h.Game.Step(); h.Game.Step();
        var l = GameHost.Load(h.SaveWithSettings()); // the rules travel with the settings
        Assert.Equal(h.StateHash(), l.StateHash());
        Assert.Equal(h.Victory.DominanceDueDay, l.Victory.DominanceDueDay);
    }
}
