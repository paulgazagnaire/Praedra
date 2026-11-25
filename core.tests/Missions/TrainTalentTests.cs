using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using Praedra.Core.Setup;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

// m5-characters-and-talent §Rule (New) 4b (fixed trainer pairs, Paul 2026-11-25) and §Talent (Train Talent: the team), tests CH41 to CH43.
public class TrainTalentTests
{
    private const int Lucrio = 919;

    private static CharacterRecord Talent(MissionWorld w, int id, string at, int talent)
    {
        var c = w.Agent(id, at);
        c.TalentHolder = true; c.TalentDiscovered = true; c.TalentExperience = talent; c.TalentTraining = 0;
        return c;
    }

    private static string? Order(MissionWorld w, Side side, string target, params int[] team) =>
        w.Try(seq => new StartMissionOrder(seq, side, MissionKind.TrainTalent, team.ToList(), new List<int>(), new List<int>(), target));

    // m5-characters-and-talent CH41 (Rule New 4b): only The Chair, Cassia Ravilla, Nobody and Ledger train, all aware at their listed Talent
    // (Nobody 150 and Ledger 120: Paul's card "Match the Hand"); the draw still makes 3 talented per side, students only, never a listed trainer
    [Fact]
    public void CH41_FixedTrainerPairs()
    {
        Assert.Equal(TrainerPick.Pair, new CharacterParams().TrainerPick);
        foreach (int seed in new[] { 1, 2, 3, 4 })
        {
            var ch = NewGame.Create(new NewGameOptions { Seed = seed }).Characters;
            Assert.Equal(new[] { Ledger, Nobody, Chair, Ravilla }, ch.All.Where(ch.IsTrainer).Select(c => c.Id).OrderBy(x => x));
            Assert.Equal((150, 120, 150, 120), (ch.Get(Nobody)!.EffectiveTalent, ch.Get(Ledger)!.EffectiveTalent, ch.Get(Chair)!.EffectiveTalent, ch.Get(Ravilla)!.EffectiveTalent));
            foreach (var side in new[] { Side.Collective, Side.Hand })
            {
                var drawn = ch.InPlay.Where(c => c.Side == side && c.Talented).ToList();
                Assert.Equal(3, drawn.Count);
                Assert.All(drawn, c => Assert.False(ch.IsTrainer(c) || ch.Data.IsListedTrainer(c.Id)));
                Assert.All(drawn, c => Assert.True(c.TalentHolder && c.TalentDiscovered));
            }
        }
    }

    // m5-characters-and-talent CH42 (Train Talent: the team): a student alone is NoTrainer, a trainer alone NoStudent, a system the side does not
    // hold BadTarget; trainer + student accepted; The Chair can take Cassia as a student; special forces cannot join (mask 0)
    [Fact]
    public void CH42_TheTeam()
    {
        var w = new MissionWorld(seed: 7);
        w.Hold(T, Side.Hand, 0);
        w.Hold(X, Side.Collective, 100);
        Talent(w, Chair, T, 150);
        Talent(w, Lucrio, T, 90);
        Assert.Equal(MissionReject.NoTrainer, Order(w, Side.Hand, T, Lucrio));
        Assert.Equal(MissionReject.NoStudent, Order(w, Side.Hand, T, Chair));
        Assert.Equal(MissionReject.BadTarget, Order(w, Side.Hand, X, Chair, Lucrio));
        int sf = w.Unit(Side.Hand, Commandos, T);
        Assert.Equal(MissionReject.NotEligibleSpecialForce,
            w.Try(seq => new StartMissionOrder(seq, Side.Hand, MissionKind.TrainTalent, new List<int> { Chair, Lucrio }, new List<int> { sf }, new List<int>(), T)));
        Assert.Null(Order(w, Side.Hand, T, Chair, Lucrio));

        var w2 = new MissionWorld(seed: 7);
        w2.Hold(T, Side.Hand, 0);
        Talent(w2, Chair, T, 150);
        Talent(w2, Ravilla, T, 120);
        var trainer = w2.M.TrainerOf(new[] { Ravilla, Chair });
        Assert.Equal(Chair, trainer!.Id);
        Assert.Equal(new[] { Ravilla }, w2.M.StudentsOf(new[] { Ravilla, Chair }, trainer).Select(c => c.Id));
        Assert.Null(Order(w2, Side.Hand, T, Chair, Ravilla));

        var w3 = new MissionWorld(seed: 7); // the Collective's pair
        w3.Hold(T, Side.Collective, 100);
        Talent(w3, Nobody, T, 150);
        Talent(w3, MaeAbara, T, 90);
        Assert.Null(Order(w3, Side.Collective, T, Nobody, MaeAbara));
    }

    // m5-characters-and-talent CH43: at the end of the timer one TalentTrainingResult per student (gap 60), MissionSucceeded, ends Succeeded, and
    // the student never reaches the trainer; the system lost before the end: TargetInvalid and no Talent roll
    [Fact]
    public void CH43_TheRoundAtTheEnd()
    {
        var w = new MissionWorld(seed: 11);
        w.Hold(T, Side.Hand, 0);
        Talent(w, Chair, T, 150);
        var lucrio = Talent(w, Lucrio, T, 90);
        Assert.Null(Order(w, Side.Hand, T, Chair, Lucrio));
        int m = w.LastMission;
        w.RunUntilWorking(m);
        w.RunIteration(m);
        var r = Assert.Single(w.Events<TalentTrainingResult>());
        Assert.Equal((Chair, Lucrio, 60), (r.Trainer, r.Student, r.Gap));
        Assert.Contains(w.Events<MissionSucceeded>(), e => e.Mission == m);
        Assert.Equal(MissionOutcome.Succeeded, w.Events<MissionEnded>().Single(e => e.Mission == m).Outcome);
        Assert.True(lucrio.EffectiveTalent < 150);

        var lost = new MissionWorld(seed: 11);
        lost.Hold(T, Side.Hand, 0);
        Talent(lost, Chair, T, 150);
        Talent(lost, Lucrio, T, 90);
        Assert.Null(Order(lost, Side.Hand, T, Chair, Lucrio));
        int m2 = lost.LastMission;
        lost.RunUntilWorking(m2);
        lost.Hold(T, Side.Collective, 100);
        lost.RunIteration(m2);
        Assert.Empty(lost.Events<TalentTrainingResult>());
        Assert.Equal(MissionOutcome.TargetInvalid, lost.Events<MissionEnded>().Single(e => e.Mission == m2).Outcome);
    }

    // m5-characters-and-talent CH44 (Rule New 4b for games under way): a save pinned to TrainerPick Random (before 2026-11-25) loads as Pair with
    // the trainer pairs set up (The Chair trains; Nobody 150, Ledger 120 and aware); a Pair save loads unchanged (same state hash)
    [Fact]
    public void CH44_OldRandomSaveLoadsWithThePairs()
    {
        var old = NewGame.Create(new NewGameOptions { Seed = 3, Settings = new GameSettings { Characters = new CharacterParams { TrainerPick = TrainerPick.Random } } });
        Assert.False(old.Characters.IsTrainer(old.Characters.Get(Chair)!));
        string save = old.SaveWithSettings();
        Assert.Contains("\"TrainerPick\":\"Random\"", save);
        var loaded = GameHost.Load(save);
        var ch = loaded.Characters;
        Assert.Equal(TrainerPick.Pair, ch.Params.TrainerPick);
        Assert.Equal(new[] { Ledger, Nobody, Chair, Ravilla }, ch.All.Where(ch.IsTrainer).Select(c => c.Id).OrderBy(x => x));
        Assert.Equal((150, 120), (ch.Get(Nobody)!.EffectiveTalent, ch.Get(Ledger)!.EffectiveTalent));

        var fresh = NewGame.Create(new NewGameOptions { Seed = 3 });
        Assert.Equal(fresh.StateHash(), GameHost.Load(fresh.SaveWithSettings()).StateHash());
    }

    // m5-characters-and-talent CH45 (Rule (New), training speed, Paul 2026-11-25 card B): the DEFAULT is Classic speed (chance 100, gain 50), so
    // The Chair (150) training a student at 90 is a 60% chance to gain up to 30; a save pinned to the old 50 / 25 loads at Classic speed, any
    // other pinned pair loads unchanged
    [Fact]
    public void CH45_ClassicTrainingSpeedAndOldSaves()
    {
        var fresh = NewGame.Create(new NewGameOptions { Seed = 3 });
        Assert.Equal((60, 30), fresh.Characters.TrainingOdds(60));
        Assert.Equal((1, 0), fresh.Characters.TrainingOdds(1));
        Assert.Equal((0, 0), fresh.Characters.TrainingOdds(0));

        var slow = NewGame.Create(new NewGameOptions { Seed = 3, Settings = new GameSettings { Characters = new CharacterParams { TalentTrainingChancePercent = 50, TalentTrainingGainPercent = 25 } } });
        Assert.Equal((30, 15), slow.Characters.TrainingOdds(60));
        var loaded = GameHost.Load(slow.SaveWithSettings()).Characters.Params;
        Assert.Equal((100, 50), (loaded.TalentTrainingChancePercent, loaded.TalentTrainingGainPercent));

        var other = NewGame.Create(new NewGameOptions { Seed = 3, Settings = new GameSettings { Characters = new CharacterParams { TalentTrainingChancePercent = 70, TalentTrainingGainPercent = 25 } } });
        var kept = GameHost.Load(other.SaveWithSettings()).Characters.Params;
        Assert.Equal((70, 25), (kept.TalentTrainingChancePercent, kept.TalentTrainingGainPercent));
    }
}
