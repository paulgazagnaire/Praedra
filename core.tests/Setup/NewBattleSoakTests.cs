using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Praedra.Core.Support;

namespace Praedra.Core.Tests.Setup;

// P4.3: the 1000-day scripted test campaign with BattleParams.Ruleset = New (m4-battle-new, shared plumbing m4-battle-classic §1, §10).
// Every daily invariant of CampaignInvariants must stay green; the scripted fights happen, and the run is deterministic.
public class NewBattleSoakTests
{
    private static GameSettings Settings() => new() { SupportRuleset = GarrisonRuleset.Classic, Battle = new BattleParams { Ruleset = BattleRuleset.New } };

    private static TestCampaign Run(bool check)
    {
        var c = TestCampaign.New(1, Settings());
        if (check) CampaignInvariants.Check(c.Host, "day 0");
        for (int i = 0; i < CampaignHarness.Days; i++)
        {
            c.Step();
            if (check) CampaignInvariants.Check(c.Host, $"New battles, day {c.Game.Day}");
        }
        return c;
    }

    // m4-battle-new (module plumbing) + m2-mines-refineries-pools P21 style soak
    [Fact]
    public void ThousandDaysWithTheNewBattleEngineKeepEveryInvariant()
    {
        var c = Run(check: true);
        Assert.Equal(CampaignHarness.Days, c.Host.Game.Day);
        Assert.Contains(c.Host.Game.Events, e => e is BattleStarted);
        Assert.Contains(c.Host.Game.Events, e => e is BattleEnded);
    }

    // m1-clock-and-orders rule 10: same seed, same hash
    [Fact]
    public void NewBattleRunIsDeterministic() =>
        Assert.Equal(Run(check: false).Host.StateHash(), Run(check: false).Host.StateHash());
}
