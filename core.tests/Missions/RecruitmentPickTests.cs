using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using Praedra.Core.Tests.Economy;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-missions §Tests MI61 to MI65: the Recruitment success pick (Recruitment pick, 2026-10-18; [EXE-own] 0x56b9a0, 0x55fc80). The pick is the
// setup extras pick (m5-setup-and-victory §P9 "An extra") at the mission's target system: pool = the side's unplaced in-game minors by id,
// m = its size, m = 0: nobody and no draw; else one draw rand(0..m-1); m = 1 sets the side's pool-empty flag.
public class RecruitmentPickTests
{
    private const int Keth = 832, Wake = 837; // characters.csv

    /// <summary>T held by the Collective, Ledger (leadership 80) at T, and the given characters created but not placed (the pool).</summary>
    private static MissionWorld World(params int[] pool)
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Collective, 100);
        w.Agent(Ledger, T, lead: 80);
        foreach (int id in pool) w.Ch.Create(w.Ch.Data.Get(id), new StubRng());
        return w;
    }

    /// <summary>Order a Recruitment on T with Ledger, the success roll 0 (RCRTMSTB(80) = 60), and run its iteration. Returns the mission id.</summary>
    private static int RunRecruitment(MissionWorld w)
    {
        w.Stub!.Expect(0, 99, 0);
        Assert.Null(w.Try(s => new StartMissionOrder(s, Side.Collective, MissionKind.Recruitment, new() { Ledger }, new(), new(), T)));
        int id = w.LastMission;
        w.RunIteration(id);
        Assert.Equal(MissionOutcome.Succeeded, w.Events<MissionEnded>().Single().Outcome);
        return id;
    }

    // m5-missions MI61: an empty pool recruits nobody and makes no draw (the draws equal those of a pool of 3 less its single rand(0..2))
    [Fact]
    public void MI61_EmptyPool_NobodyAndNoDraw()
    {
        var empty = World();
        int id = RunRecruitment(empty);
        Assert.Empty(empty.Events<CharacterRecruited>());
        Assert.Null(empty.M.Get(id)!.RecruitedCharacterKey);
        Assert.False(empty.Ch.PoolEmpty(Side.Collective)); // only m = 1 sets the flag

        var three = World(Keth, Foreman, Wake);
        three.Stub!.Expect(0, 2, 1);
        RunRecruitment(three);
        Assert.Equal(1, three.Stub.Count(0, 2));
        var withoutPick = three.Stub.Requests.ToList();
        withoutPick.Remove((0, 2));
        Assert.Equal(withoutPick, empty.Stub!.Requests);
    }

    // m5-missions MI62: one in the pool: he is recruited (draw rand(0..0)) and the side's pool-empty flag is set; with two the flag stays off
    [Fact]
    public void MI62_OneInPool_SetsPoolEmptyFlag()
    {
        var w = World(Foreman);
        int id = RunRecruitment(w);
        Assert.Equal(1, w.Stub!.Count(0, 0));
        Assert.Equal(Foreman, w.M.Get(id)!.RecruitedCharacterKey);
        Assert.True(w.Ch.PoolEmpty(Side.Collective));
        Assert.False(w.Ch.PoolEmpty(Side.Hand));
        Assert.Empty(w.Ch.RecruitPoolOf(Side.Collective));

        var two = World(Keth, Foreman);
        RunRecruitment(two);
        Assert.False(two.Ch.PoolEmpty(Side.Collective));
    }

    // m5-missions MI63: pool Keth, Foreman, Wake (id order), draw r = 1: Foreman is placed free at the target system T, with the skills the creation
    // rule rolled (base + rand(0..variance), m5-characters-and-talent §Rolling the stats), the recruit recorded on the mission
    [Fact]
    public void MI63_PickAtTargetSystem_WithRolledSkills()
    {
        var w = new MissionWorld();
        w.Hold(T, Side.Collective, 100);
        w.Agent(Ledger, T, lead: 80);
        w.Ch.Create(w.Ch.Data.Get(Keth), new StubRng());
        // Foreman: variance draws diplomacy 30, espionage 30, facility 60, combat 30, leadership 20 get 5, 6, 7, 8, 9; then the awakening draw 99 (no user)
        w.Ch.Create(w.Ch.Data.Get(Foreman), new StubRng().Queue(5, 6, 7, 8, 9, 99));
        w.Ch.Create(w.Ch.Data.Get(Wake), new StubRng());
        w.Stub!.Expect(0, 2, 1);
        int id = RunRecruitment(w);

        var c = w.Ch.Get(Foreman)!;
        Assert.True(c.Placed);
        Assert.Equal((CharacterStatus.Free, T, 0), (c.Status, c.System, c.AttachedFleet));
        Assert.Equal(new[] { 70 + 5, 70 + 6, 0, 0, 40 + 7, 70 + 8, 80 + 9 }, c.Skills);
        Assert.False(c.TalentHolder);
        Assert.Equal(Foreman, w.M.Get(id)!.RecruitedCharacterKey);
        var ev = w.Events<CharacterRecruited>().Single();
        Assert.Equal((Foreman, T, Side.Collective, id), (ev.Character, ev.Target, ev.Side, ev.Mission));
        Assert.Equal(Foreman, w.Events<CharacterCreated>().Single().Character);
        Assert.False(w.Ch.Get(Keth)!.Placed);
        Assert.False(w.Ch.Get(Wake)!.Placed);
    }

    // m5-missions MI64: majors are never recruited and only in-game roster rows count: with Nobody (major) unplaced the pool is empty (no draw);
    // adding Foreman gives m = 1 (draw rand(0..0)) and Foreman. The cut characters are no longer in the data at all (Praedra names stage 2), so
    // the roster check has no record to test here; CharacterSetupTests.Roster_RefinedFilmEra checks they are gone.
    [Fact]
    public void MI64_MajorsAndCutCharactersAreNeverPicked()
    {
        var w = World(Nobody);
        Assert.Equal(CharacterKind.Major, w.Ch.Data.Get(Nobody).Kind);
        Assert.Empty(w.Ch.RecruitPoolOf(Side.Collective));
        RunRecruitment(w);
        Assert.Empty(w.Events<CharacterRecruited>());
        Assert.False(w.Ch.Get(Nobody)!.Placed);

        var w2 = World(Nobody, Foreman);
        int id = RunRecruitment(w2);
        Assert.Equal(1, w2.Stub!.Count(0, 0));
        Assert.Equal(Foreman, w2.M.Get(id)!.RecruitedCharacterKey);
        Assert.False(w2.Ch.Get(Nobody)!.Placed);
        Assert.True(w2.Ch.PoolEmpty(Side.Collective)); // m counted Foreman only
    }

    // m5-missions MI65: the recruit, the mission's RecruitedCharacterKey and the pool-empty flag survive save and load (same state hash)
    [Fact]
    public void MI65_SurvivesSaveLoad()
    {
        var w = World(Foreman);
        int id = RunRecruitment(w);
        string hash = w.Host.StateHash();
        w.Reload();
        Assert.Equal(hash, w.Host.StateHash());
        Assert.Equal(Foreman, w.M.Get(id)!.RecruitedCharacterKey);
        Assert.True(w.Ch.PoolEmpty(Side.Collective));
        Assert.Equal(T, w.Ch.Get(Foreman)!.System);
        Assert.NotNull(w.M.Recruit); // the host wires the pick again after a load
    }
}
