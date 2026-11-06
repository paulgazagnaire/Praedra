using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Map;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §6b (Burn the Books), tests BB1 to BB8, and the §6b guard set (BooksSkipDetection off).
public class BurnTheBooksTests
{
    private const string Charter = Praedra.Core.Control.ControlModule.CapitalSystem;
    private const string A = "A", B = "B", C = "C", D = "D";

    // Charter (not populated, so its own support stays out of BB3), A, B, C, D (populated), lanes from Charter to each.
    private static readonly Lazy<GameData> _data = new(() =>
    {
        var planets = "name,sector,group,map_x,map_y\nCharter,S,heartland,0,20\nA,S,heartland,15,20\nB,S,heartland,30,20\nC,S,heartland,15,40\nD,S,heartland,15,0\n";
        var lanes = "from,to,kind,routes\nCharter,A,generated,\nCharter,B,generated,\nCharter,C,generated,\nCharter,D,generated,\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},9,3,{(s.Name == Charter ? 0 : 1)}"));
        return MissionWorld.Data with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    });

    /// <summary>
    /// Charter held by the Hand; Mae Abara (Collective) at Charter with espionage 80 and combat 60, a talented character (TalentHolder, Talent 0 so
    /// nobody senses her) unless <paramref name="talented"/> is false (§6b BooksNeedsTalent).
    /// </summary>
    private static MissionWorld World(MissionParams? p = null, Side charterHolder = Side.Hand, bool talented = true)
    {
        var w = new MissionWorld(p, data: _data.Value);
        w.Hold(Charter, charterHolder, 20);
        w.Hold(A, Side.Hand, 40);
        w.Hold(B, Side.Hand, 60);
        w.Hold(C, null, 98);
        w.Hold(D, Side.Hand, 50);
        w.Agent(MaeAbara, Charter, esp: 80, com: 60).TalentHolder = talented;
        return w;
    }

    private static string? Order(MissionWorld w, Side side, int agent, string target) =>
        w.Try(seq => new StartMissionOrder(seq, side, MissionKind.BurnTheBooks, new List<int> { agent }, new List<int>(), new List<int>(), target));

    // m5-missions BB1: the Hand cannot order Burn the Books (WrongSide); with the switch off it does not exist (UnknownKind)
    [Fact]
    public void BB1_HandOrdersIt_WrongSide()
    {
        var w = World();
        w.Agent(Ravilla, Charter, esp: 80, com: 60);
        Assert.Equal(MissionReject.WrongSide, Order(w, Side.Hand, Ravilla, Charter));
        Assert.True(new MissionParams().BurnTheBooks);
        var off = World(new MissionParams { BurnTheBooks = false });
        Assert.Equal(MissionReject.UnknownKind, Order(off, Side.Collective, MaeAbara, Charter));
    }

    // m5-missions BB2: any system but Charter is BadTarget; Charter held by the Collective is BadTarget too
    [Fact]
    public void BB2_OnlyAHandHeldCharter()
    {
        var w = World();
        Assert.Equal(MissionReject.BadTarget, Order(w, Side.Collective, MaeAbara, A));
        Assert.Equal(MissionReject.BadTarget, Order(w, Side.Collective, MaeAbara, C));
        var held = World(charterHolder: Side.Collective);
        Assert.Equal(MissionReject.BadTarget, Order(held, Side.Collective, MaeAbara, Charter));
        Assert.Null(Order(World(), Side.Collective, MaeAbara, Charter)); // the legal order
    }

    // m5-missions BB3: a success adds 5 Collective support on every non-destroyed populated system (40, 60, 98 -> 45, 65, 100 by the clamp;
    // no harvester sector, so no damping), the destroyed system is untouched, the flag and its day are set, and a second order is BooksGone
    [Fact]
    public void BB3_SuccessSwingsEveryPopulatedSystem()
    {
        var w = World();
        w.Game.State.Harvester.Destroyed.Add(D);
        w.Agent(Midwife, Charter, esp: 50, com: 50);
        w.Stub!.Expect(0, 99, Bt(1, 0)); // the betrayal draw of the job check (B2b), then the one success roll: 0 < 3
        Assert.Null(Order(w, Side.Collective, MaeAbara, Charter));
        w.RunIteration(w.LastMission);
        var burned = w.Events<BooksBurned>().Single();
        Assert.Equal(Charter, burned.System);
        Assert.True(w.Game.State.Missions.BooksBurned);
        Assert.Equal(burned.Day, w.Game.State.Missions.BooksBurnedOn);
        var eco = w.Host.Economy;
        Assert.Equal((45, 65, 100), (eco.SupportOf(A, Side.Collective), eco.SupportOf(B, Side.Collective), eco.SupportOf(C, Side.Collective)));
        Assert.Equal(20, eco.SupportOf(Charter, Side.Collective)); // not populated
        var changes = w.Events<SupportChanged>().Where(e => e.Cause == SupportCause.BooksBurned).ToList();
        Assert.Equal(new[] { A, B, C }, changes.Select(e => e.System).ToArray()); // system id order; D destroyed, Charter not populated
        Assert.Equal(MissionReject.BooksGone, Order(w, Side.Collective, Midwife, Charter));
    }

    // m5-missions BB4: best member esp 80, combat 60, 2 Hand defence facilities at Charter: input 70 - 10 = 60, capped to 1 (BooksCapPercent, Decided
    // by Paul 2026-11-04 20:47); draw 0 succeeds, 1 fails (SBTGMSTB at 60 is 60 in data/classic/mission-tables.csv; the cap gives 1 either way)
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void BB4_InputAndCap(int roll, bool success)
    {
        var w = World();
        w.Host.Economy.AddFacility(Charter, FacilityKind.Lien150);
        w.Host.Economy.AddFacility(Charter, FacilityKind.CovenantI);
        w.Host.Economy.AddFacility(Charter, FacilityKind.Mine); // not a defence facility
        w.Agent(Midwife, Charter, esp: 10, com: 10); // a weaker second member: the one roll uses the best input
        Assert.Equal(2, w.M.BooksDefences(Charter));
        Assert.Equal(60, w.M.Lookup(w.M.Data.Table("SBTGMSTB"), 60));
        Assert.Equal(1, new MissionParams().BooksCapPercent);
        w.Stub!.Expect(0, 99, Bt(2, roll));
        long s = w.Start(Side.Collective, MissionKind.BurnTheBooks, new[] { MaeAbara, Midwife }, Charter);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.RunIteration(w.LastMission);
        var r = w.Events<MissionMemberRoll>().Single();
        Assert.Equal((MaeAbara, 60, 1, roll, success), (r.Member, r.Input, r.Chance, r.Roll, r.Success));
        Assert.Equal(success, w.Game.State.Missions.BooksBurned);
        Assert.Equal(success ? MissionOutcome.Succeeded : MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome); // not repeating
    }

    // m5-missions BB5: the flag and its day survive a save and load, with the same state hash; a game that never burned the books saves no field
    [Fact]
    public void BB5_SaveAndLoadKeepTheFlag()
    {
        var w = World();
        Assert.DoesNotContain(nameof(MissionState.BooksBurnedOn), w.Host.Save());
        w.Stub!.Expect(0, 99, Bt(1, 0));
        Assert.Null(Order(w, Side.Collective, MaeAbara, Charter));
        w.RunIteration(w.LastMission);
        int day = w.Game.State.Missions.BooksBurnedOn!.Value;
        string hash = w.Host.StateHash();
        w.Reload();
        Assert.True(w.Game.State.Missions.BooksBurned);
        Assert.Equal(day, w.Game.State.Missions.BooksBurnedOn);
        Assert.Equal(hash, w.Host.StateHash());
    }

    // m5-missions §6b Guards, BooksSkipDetection off: the job check meets the §6a Full set (a landed Hand regiment guards, which the B2d set leaves out)
    [Fact]
    public void BB_Guards_LandedRegimentGuardsTheJobCheck()
    {
        var w = World(new MissionParams { BooksSkipDetection = false });
        int reg = w.Unit(Side.Hand, ImpArmy, Charter);
        w.Stub!.Expect(0, 99, Bt(1, 99)); // betrayal, then the regiment's detection roll (99: not detected); the success roll then draws 0
        Assert.Null(Order(w, Side.Collective, MaeAbara, Charter));
        w.RunIteration(w.LastMission);
        Assert.Equal(reg, w.Events<MissionDetectionRoll>().Single().Defender);
    }

    // A Charter under heavy guard: a Hand fleet of two capital ships and four landed regiments.
    private static void Garrison(MissionWorld w)
    {
        w.Host.Fleets.AddFleet(Side.Hand, "F", new[] { "Decree", "Decree" }, Charter);
        for (int k = 0; k < 4; k++) w.Unit(Side.Hand, k % 2 == 0 ? Lictor : Storm, Charter);
    }

    // m5-missions BB6 (BooksSkipDetection DEFAULT on, Decided by Paul 2026-11-04 20:47): a heavily guarded Charter never foils the team: no
    // detection or decoy draw at all (the stub's unqueued draws are 0, which would detect at once), the one roll succeeds. Off: the first guard detects.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BB6_NoDetectionAtAGuardedCharter(bool skip)
    {
        Assert.True(new MissionParams().BooksSkipDetection);
        var w = World(new MissionParams { BooksSkipDetection = skip });
        Garrison(w);
        w.Stub!.Expect(0, 99, Bt(1, 0)); // the betrayal draw, then the success roll 0 < 1 (only reached when nothing detects)
        Assert.Null(Order(w, Side.Collective, MaeAbara, Charter));
        w.RunIteration(w.LastMission);
        if (skip)
        {
            Assert.Empty(w.Events<MissionDetectionRoll>());
            Assert.Empty(w.Events<MissionFoiled>());
            Assert.True(w.Game.State.Missions.BooksBurned);
        }
        else
        {
            Assert.Contains(w.Events<MissionDetectionRoll>(), e => e.Detected);
            Assert.Single(w.Events<MissionFoiled>());
            Assert.False(w.Game.State.Missions.BooksBurned);
        }
    }

    // m5-missions BB7 (BooksNeedsTalent DEFAULT on, Decided by Paul 2026-11-04 20:47): with no talented member the success roll is not made (no
    // draw, no MissionMemberRoll) and the iteration fails though a roll of 0 was ready; with one talented member (the weaker one) the roll is made
    // and succeeds. Off: the untalented team rolls and succeeds.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void BB7_HiddenTalentRequirement(bool needs, bool talentedSecond, bool success)
    {
        Assert.True(new MissionParams().BooksNeedsTalent);
        var w = World(new MissionParams { BooksNeedsTalent = needs }, talented: false);
        w.Agent(Midwife, Charter, esp: 10, com: 10).TalentHolder = talentedSecond;
        Assert.False(w.Ch.Get(Midwife)!.TalentDiscovered); // hidden Talent counts too
        w.Stub!.Expect(0, 99, Bt(2, 0));
        long s = w.Start(Side.Collective, MissionKind.BurnTheBooks, new[] { MaeAbara, Midwife }, Charter);
        w.Game.Step();
        Assert.Null(w.Reason(s));
        w.RunIteration(w.LastMission);
        Assert.Equal(success ? 1 : 0, w.Events<MissionMemberRoll>().Count);
        if (success) Assert.Equal(MaeAbara, w.Events<MissionMemberRoll>().Single().Member); // the best input rolls, talented or not
        Assert.Equal(success, w.Game.State.Missions.BooksBurned);
        Assert.Equal(success ? MissionOutcome.Succeeded : MissionOutcome.Failed, w.Events<MissionEnded>().Single().Outcome);
        Assert.Equal(success ? 3 : 2, w.Stub.Count(0, 99)); // two betrayal draws, plus the roll only when made
    }

    // m5-missions BB8: one team at a time (BooksOneTeam, Decided by Paul 2026-11-06): with Mae Abara's team out, a second Collective team is
    // BooksTeamOut; with the switch off it is accepted
    [Theory]
    [InlineData(true, MissionReject.BooksTeamOut)]
    [InlineData(false, null)]
    public void BB8_OneTeamAtATime(bool oneTeam, string? second)
    {
        Assert.True(new MissionParams().BooksOneTeam);
        var w = World(new MissionParams { BooksOneTeam = oneTeam });
        w.Agent(Midwife, Charter, esp: 50, com: 50).TalentHolder = true;
        Assert.Null(Order(w, Side.Collective, MaeAbara, Charter));
        Assert.Equal(second, Order(w, Side.Collective, Midwife, Charter));
    }
}
