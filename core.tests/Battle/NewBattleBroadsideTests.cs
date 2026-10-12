using Rebellion.Core.Battle;
using Rebellion.Core.Clock;

namespace Rebellion.Core.Tests.Battle;

// Tests of m4-battle-new section 5 (closing range) and section 6 (facing) on ships whose strongest guns are on the broadside (Dauntless Cruiser).
// Finding of the "4 Dauntless v 2 Imperial II" bug hunt: the facing, arc mapping and data copy are correct; the Dauntless lose because they never get in range (section 5).
public class NewBattleBroadsideTests
{
    private const string Dauntless = "Dauntless Cruiser", ImperialII = "Imperial II Star Destroyer";

    private static (NewBattle B, ClassicBattleResult R) Run(NewBattleParams np, ulong seed)
    {
        var p = new BattleParams { Ruleset = BattleRuleset.New, New = np };
        var (nb, _) = QuickBattle.PrepareNew(p, NewBattleTests.Ships, new[] { (Dauntless, 4) }, new[] { (ImperialII, 2) }, seed, logEvents: true);
        return (nb, nb.Run());
    }

    private static int ShotsBy(NewBattle nb, Side side)
    {
        var sideOf = nb.Units.ToDictionary(u => u.ObjectId, u => u.Side);
        return nb.Log!.Count(l => (l.Kind == "hit" || l.Kind == "miss" || l.Kind == "damage") && sideOf[l.Unit] == side);
    }

    // m4-battle-new §6 broadside: ships whose heavy guns are port and starboard turn broadside and fire them once every pair is inside range.
    // StandOffSlack 20 (instead of the sheet's 16) puts every large-large pair (offsets 6 + 6) inside both sides' turbolaser range at the StandOff distance.
    [Fact]
    public void Broadside_FourDauntlessTurnBroadsideAndFireWhenInRange_AndBeatTwoImperialII()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (nb, r) = Run(new NewBattleParams { StandOffSlack = 20 }, seed);
            var sideOf = nb.Units.ToDictionary(u => u.ObjectId, u => u.Side);
            Assert.Contains(nb.Log!, l => l.Kind == "turn" && l.Detail == "broadside" && sideOf[l.Unit] == Side.Alliance);
            Assert.True(ShotsBy(nb, Side.Alliance) > 0, $"seed {seed}");
            Assert.Equal(BattleOutcome.Victorious, r.AllianceOutcome);
        }
    }

    // m4-battle-new §5 closing range, sheet-literal behaviour (pins the cause of the bug report; revisit when the sheet changes):
    // the Imperial II (turbolaser range 70) sets D = 70 - 16 = 54 (larger desiredD than the Dauntless 65 - 16 = 49, and equal sublight), so the pair range is
    // 54 + 6 + 6 = 66, one DAT unit beyond the Dauntless turbolasers (65) and far beyond their ions (40): the Dauntless never have a target and never turn.
    [Fact]
    public void ClosingRange_DefaultSheetValues_DauntlessSitOneUnitOutsideTheirOwnTurbolaserRange()
    {
        var (nb, r) = Run(new NewBattleParams(), 1);
        Assert.Equal(80, nb.StartDistance);   // longest range present (70) + 10
        Assert.Equal(54, nb.Distance);        // settled at the Imperial II desired distance
        Assert.Equal(0, ShotsBy(nb, Side.Alliance));
        Assert.True(ShotsBy(nb, Side.Empire) > 0);
        Assert.DoesNotContain(nb.Log!, l => l.Kind == "turn");
        Assert.Equal(BattleOutcome.Victorious, r.EmpireOutcome);
    }
}
