using Praedra.Core.Battle;
using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Support;
using static Praedra.Core.Tests.Missions.MissionWorld;
using Praedra.Core.Tests.Control;

namespace Praedra.Core.Tests.Missions;

/// <summary>
/// B2c: the four triggers that move loyalty and the per-system loss tallies they read. Foreman is the Collective minor, Admiral Decimus Varo the Hand minor,
/// Nobody is unable to betray (never moves). Battle and blockade events are emitted as the battle and blockade code emits them, through a
/// StepContext, so the observer runs exactly as in a Step. rule: m5-characters-and-force §Loyalty and betrayal (Traced, B2c).
/// </summary>
public class B2cLoyaltyTriggerTests
{
    private static MissionWorld World(CharacterParams? ch = null, int foreman = 100, int varo = 100)
    {
        var w = new MissionWorld(characters: ch);
        w.Agent(Foreman, H).Loyalty = foreman;
        w.Agent(Varo, X).Loyalty = varo;
        w.Agent(Nobody, H).Loyalty = 100;
        return w;
    }

    private static void Emit(MissionWorld w, Func<int, int, GameEvent> make) => new StepContext(w.Game, w.Game.Day).Emit(make);

    private static int Loy(MissionWorld w, int id) => w.Ch.Get(id)!.Loyalty;

    /// <summary>Emit a battle at T: start, one UnitDestroyed (Combat) per lost object, end.</summary>
    private static void Battle(MissionWorld w, IEnumerable<int> lost, bool start = true)
    {
        if (start) Emit(w, (i, d) => new BattleStarted(i, d, T, new ValueList<int>(Array.Empty<int>()), new ValueList<int>(Array.Empty<int>())));
        foreach (var u in lost) Emit(w, (i, d) => new UnitDestroyed(i, d, T, u, 1, UnitLossReason.Combat));
        Emit(w, (i, d) => new BattleEnded(i, d, new BattleResult(T, 10, BattleOutcome.Indecisive, BattleOutcome.Indecisive, 0, 0, 0, 0)));
    }

    private static int[] Ships(MissionWorld w, Side side, params string[] classes) => classes.Select(c => w.Unit(side, c, T)).ToArray();

    private static void RegimentLost(MissionWorld w, Side side, string cls, string reason, int n = 1)
    {
        for (int k = 0; k < n; k++) Emit(w, (i, d) => new RegimentDestroyed(i, d, 0, cls, T, side, reason));
    }

    // B2c-1
    [Fact]
    public void B2c1_CaptureShiftsTowardTheNewOwner_OneDraw()
    {
        var w = World();
        w.Hold(T, Side.Collective, 60);
        w.Stub!.Expect(0, 5, 3);
        int before = w.Stub.Count(0, 5);
        w.Host.Control.SubmitCapture(Side.Hand, T) /* (B5a, 2026-10-17) Derived control: troops take the world */;
        w.Game.Step();
        Assert.Single(w.Events<SystemCaptured>());
        Assert.Equal(1, w.Stub.Count(0, 5) - before);
        Assert.Equal(97, Loy(w, Foreman));
        Assert.Equal(100, Loy(w, Varo)); // clamped
        Assert.Equal(100, Loy(w, Nobody));
        Assert.Single(w.Events<LoyaltyChanged>());
    }

    // B2c-2: our core has no event for a system turning neutral (SystemCaptured.To cannot be neutral; the harvester clears the owner
    // without one), so Classic's "nothing" holds by construction: a holder cleared to neutral draws nothing and moves nothing.
    // (B5a, 2026-10-17) a flip to neutral now has its events (SystemCaptured and HolderChangeAftermath with To = null); the loyalty trigger
    // still reacts only to a side, so the expectation stands. This test clears the owner by hand, without a flip.
    [Fact]
    public void B2c2_ToNeutral_NoDrawNoChange()
    {
        var w = World();
        w.Hold(T, Side.Hand, 40);
        w.Game.Step();
        int before = w.Stub!.Count(0, 5);
        w.Hold(T, null, 40);
        w.Game.Step();
        Assert.Equal(before, w.Stub.Count(0, 5));
        Assert.Empty(w.Events<LoyaltyChanged>());
        Assert.Equal(100, Loy(w, Foreman));
    }

    // B2c-3
    [Fact]
    public void B2c3_HqDestroyed_CollectiveLosesK()
    {
        var w = World(varo: 97);
        w.Stub!.Expect(0, 5, 2);
        int before = w.Stub.Count(0, 5);
        Emit(w, (i, d) => new HqDestroyed(i, d, "Test", H));
        Assert.Equal(1, w.Stub.Count(0, 5) - before);
        Assert.Equal(98, Loy(w, Foreman));
        Assert.Equal(99, Loy(w, Varo));
        Assert.Equal(100, Loy(w, Nobody));
    }

    // B2c-4
    [Fact]
    public void B2c4_HandAlone_ShiftIsSix()
    {
        var w = World();
        w.Host.Fleets.AddFleet(Side.Hand, "F", new[] { "Decree" }, T);
        var lost = Ships(w, Side.Collective, "Barricade", "Ark", "Ark")
            .Concat(Ships(w, Side.Hand, "Decree")).ToArray();
        Assert.Equal(6, w.Host.Loyalty.LossShift(LoyaltyModule.SideNumberHand, 194, 30));
        Battle(w, lost);
        Assert.Equal(new[] { 194, 30, 0 }, w.Host.Loyalty.TallyOf(T)!.Battle);
        Assert.Equal(94, Loy(w, Foreman));
        Assert.Equal(100, Loy(w, Varo));
    }

    // B2c-5
    [Fact]
    public void B2c5_CollectiveAlone_HeavierLosses_ShiftIsMinusTwo()
    {
        var w = World();
        w.Host.Fleets.AddFleet(Side.Collective, "F", new[] { "Ark" }, T);
        var lost = Ships(w, Side.Collective, "Barricade", "Crowbar", "Nest")
            .Concat(Ships(w, Side.Hand, "Injunction", "Bond", "Bond")).ToArray();
        Battle(w, lost);
        Assert.Equal(new[] { 150, 40, 0 }, w.Host.Loyalty.TallyOf(T)!.Battle);
        Assert.Equal(98, Loy(w, Foreman));
        Assert.Equal(100, Loy(w, Varo));
    }

    // B2c-6
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void B2c6_BothOrNeitherPresent_NoShift(bool both)
    {
        var w = World();
        if (both)
        {
            w.Host.Fleets.AddFleet(Side.Collective, "A", new[] { "Ark" }, T);
            w.Host.Fleets.AddFleet(Side.Hand, "E", new[] { "Decree" }, T);
        }
        Battle(w, Ships(w, Side.Collective, "Barricade", "Barricade"));
        Assert.Empty(w.Events<LoyaltyChanged>());
        Assert.Equal(100, Loy(w, Foreman));
    }

    // B2c-7
    [Fact]
    public void B2c7_OnlyFightersDied_TalliesZero_NoChange()
    {
        var w = World();
        w.Host.Fleets.AddFleet(Side.Hand, "F", new[] { "Decree" }, T);
        Battle(w, Ships(w, Side.Collective, "Swift", "Swift", "Plough"));
        Assert.Equal(new[] { 0, 0, 0 }, w.Host.Loyalty.TallyOf(T)!.Battle);
        Assert.Empty(w.Events<LoyaltyChanged>());
    }

    // B2c-8
    [Fact]
    public void B2c8_BlockadeBombardsAndLeaves_DefenderGains_ShiftZero()
    {
        var w = World();
        w.Hold(T, Side.Collective, 60);
        Emit(w, (i, d) => new BlockadeStarted(i, d, T, Side.Hand));
        RegimentLost(w, Side.Collective, RebArmy, "Bombardment", 2);
        var t = w.Host.Loyalty.TallyOf(T)!;
        Assert.Equal(LoyaltyModule.SideNumberHand, t.Blockader);
        Assert.Equal(new[] { 10, 0, 0 }, t.Bombardment);
        Assert.Equal(0, w.Host.Loyalty.LossShift(LoyaltyModule.SideNumberCollective, 10, 0));
        Emit(w, (i, d) => new BlockadeLifted(i, d, T, Side.Hand));
        Assert.Empty(w.Events<LoyaltyChanged>());
    }

    // B2c-9
    [Theory]
    [InlineData(4, 100)]
    [InlineData(20, 97)]
    public void B2c9_BlockaderTakesTheSystem_BlockaderGains(int collectiveRegiments, int foreman)
    {
        var w = World();
        w.Hold(T, Side.Collective, 60);
        Emit(w, (i, d) => new BlockadeStarted(i, d, T, Side.Hand));
        RegimentLost(w, Side.Hand, Storm, "AssaultDuel");
        RegimentLost(w, Side.Collective, RebArmy, "AssaultBattery", collectiveRegiments);
        Assert.Equal(new[] { 5 * collectiveRegiments, 6, 0 }, w.Host.Loyalty.TallyOf(T)!.Assault);
        w.Host.Fleets.SetOwner(T, Side.Hand);
        Emit(w, (i, d) => new BlockadeLifted(i, d, T, Side.Hand));
        Assert.Equal(foreman, Loy(w, Foreman));
        Assert.Equal(100, Loy(w, Varo));
    }

    // B2c-10
    [Fact]
    public void B2c10_SecondBattleStartsFromZero()
    {
        var w = World();
        w.Host.Fleets.AddFleet(Side.Hand, "F", new[] { "Decree" }, T);
        var first = Ships(w, Side.Collective, "Barricade");
        Emit(w, (i, d) => new BattleStarted(i, d, T, new ValueList<int>(Array.Empty<int>()), new ValueList<int>(Array.Empty<int>())));
        foreach (var u in first) Emit(w, (i, d) => new UnitDestroyed(i, d, T, u, 1, UnitLossReason.Combat));
        Assert.Equal(110, w.Host.Loyalty.TallyOf(T)!.Battle[0]);
        Battle(w, Ships(w, Side.Collective, "Ark", "Ark"));
        Assert.Equal(new[] { 84, 0, 0 }, w.Host.Loyalty.TallyOf(T)!.Battle);
        Assert.Equal(98, Loy(w, Foreman)); // 84 / 50 + 84 / 50 = 2, not the 6 of 194
    }

    // B2c-11
    [Fact]
    public void B2c11_SwitchOff_NoTriggerDrawsOrShifts()
    {
        var w = World(new CharacterParams { LoyaltyTriggers = false });
        w.Hold(T, Side.Collective, 60);
        w.Game.Step();
        int before = w.Stub!.Count(0, 5);
        w.Host.Control.SubmitCapture(Side.Hand, T) /* (B5a, 2026-10-17) Derived control: troops take the world */;
        w.Game.Step();
        Assert.Single(w.Events<SystemCaptured>());
        Emit(w, (i, d) => new HqDestroyed(i, d, "Test", H));
        Assert.Equal(before, w.Stub.Count(0, 5));
        w.Host.Fleets.AddFleet(Side.Hand, "F", new[] { "Decree" }, T);
        Battle(w, Ships(w, Side.Collective, "Barricade"));
        Emit(w, (i, d) => new BlockadeStarted(i, d, T, Side.Hand));
        RegimentLost(w, Side.Collective, RebArmy, "AssaultBattery", 20);
        Emit(w, (i, d) => new BlockadeLifted(i, d, T, Side.Hand));
        Assert.Empty(w.Events<LoyaltyChanged>());
        Assert.Null(w.Host.Loyalty.TallyOf(T));
        Assert.Equal(100, Loy(w, Foreman));
    }

    // B2c §Work: the tallies are saved and in the state hash
    [Fact]
    public void TalliesAreSavedAndHashed()
    {
        var w = World();
        string h0 = w.Host.StateHash();
        Emit(w, (i, d) => new BlockadeStarted(i, d, T, Side.Hand));
        RegimentLost(w, Side.Collective, RebArmy, "Bombardment", 3);
        string h1 = w.Host.StateHash();
        Assert.NotEqual(h0, h1);
        w.Reload();
        Assert.Equal(h1, w.Host.StateHash());
        var t = w.Host.Loyalty.TallyOf(T)!;
        Assert.Equal(new[] { 15, 0, 0 }, t.Bombardment);
        Assert.Equal(LoyaltyModule.SideNumberHand, t.Blockader);
    }
}
