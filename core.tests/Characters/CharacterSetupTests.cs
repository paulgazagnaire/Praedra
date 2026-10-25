using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Praedra.Core.Victory;
using static Praedra.Core.Tests.Characters.CharWorld;

namespace Praedra.Core.Tests.Characters;

/// <summary>The starting characters (m5-setup-and-victory P9), the roster data, and the character terms of victory.</summary>
public class CharacterSetupTests
{
    private static GameHost Make(int seed = 1, GameSettings? s = null) => NewGame.Create(new NewGameOptions { Seed = seed, Settings = s });

    // m5-characters-and-force §The character object; Decided roster (heroes/refined-roster, added-heroes)
    [Fact]
    public void Roster_RefinedFilmEra()
    {
        var d = GameData.Default.Characters;
        // The 11 cut characters (ids below) are left out of the data altogether (Praedra names stage 2, 2026-10-25: cut, so never named).
        Assert.Equal(65, d.All.Count);
        Assert.Equal(65, d.Roster.Count);
        foreach (var cut in new[] { 844, 854, 857, 904, 905, 906, 907, 911, 913, 914, 915 })
            Assert.False(d.TryGet(cut, out _));
        foreach (var hunter in new[] { "Usura", "Mora", "Damnum", "Pignus" })
        {
            var c = d.Roster.Single(x => x.Name == hunter);
            Assert.Equal(Side.Hand, c.Side);
            Assert.False(c.CanAdmiral || c.CanCommander || c.CanGeneral); // no officer roles, like Oti
        }
        Assert.Equal(50, d.Get(Han).NavigatorSpeed);
        Assert.Equal(100, d.Get(Luke).NavigatorSpeed);
        Assert.True(d.Get(Luke).TalentTrainer && d.Get(Vader).TalentTrainer);
        Assert.All(d.All.Where(c => !c.Added), c => Assert.Equal((100, 0), (c.LoyaltyBase, c.LoyaltyVariance)));
    }

    // m5-setup-and-victory SV43 and P9: the fixed cast, extras per side, no cut character, nobody twice
    [Fact]
    public void P9_StartingCast()
    {
        var h = Make();
        var ch = h.Characters;
        Assert.Equal(Coruscant, ch.LocationOf(ch.Get(Palpatine)!));
        Assert.Equal(h.HqSystem, ch.LocationOf(ch.Get(MonMothma)!));
        foreach (var id in new[] { Leia, Luke, Han, Wedge, Chewbacca, Dodonna }) Assert.Equal(Yavin, ch.LocationOf(ch.Get(id)!));
        foreach (var id in new[] { Vader, 896, 897, Piett, Veers, 908 }) Assert.True(ch.Get(id)!.Placed);
        foreach (var side in new[] { Side.Hand, Side.Collective })
            Assert.Equal(7 + 4, ch.InPlay.Count(c => c.Side == side));
        Assert.Equal(65, ch.All.Count); // the whole roster is created (unplaced = the recruitment pool)
        foreach (var c in ch.InPlay)
        {
            Assert.True(ch.Data.Get(c.Id).InGame);
            if (c.AttachedFleet != 0) Assert.Equal(c.Side, h.Game.State.Fleets[c.AttachedFleet].Side);
            else Assert.True(h.Game.State.SystemOwners.ContainsKey(c.System!) || c.Id is Palpatine or MonMothma || c.System == Yavin);
            Assert.Equal(CharacterPost.None, c.Post); // nobody holds a post on day 0
        }
        Assert.Empty(h.Game.Events); // setup raises no per-object events
    }

    // m5-characters-and-force CH39 (Rule New 4, Paul 2026-10-18): TrainerPick Random (DEFAULT) draws 3 talented placed characters per side, each an
    // aware talented character with Talent 80..120 (or his own if higher), and only they can train; Fixed (Classic) draws nothing and Luke and Vader train
    [Fact]
    public void CH39_RandomTalentedTrainers()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var h = Make(seed);
            var ch = h.Characters;
            foreach (var side in new[] { Side.Collective, Side.Hand })
            {
                var t = ch.InPlay.Where(c => c.Side == side && c.Talented).ToList();
                Assert.Equal(3, t.Count);
                Assert.All(t, c => Assert.True(c.TalentHolder && c.TalentDiscovered && c.EffectiveTalent >= 80 && ch.IsTrainer(c)));
            }
            Assert.All(ch.All.Where(c => !c.Talented), c => Assert.False(ch.IsTrainer(c)));
        }
        var picks = new[] { 1, 2, 3, 4 }.Select(seed => string.Join(",", Make(seed).Characters.All.Where(c => c.Talented).Select(c => c.Id))).Distinct().Count();
        Assert.True(picks > 1); // the pick changes with the seed

        var classic = Make(1, new GameSettings { Characters = new CharacterParams { TrainerPick = TrainerPick.Fixed } }).Characters;
        Assert.DoesNotContain(classic.All, c => c.Talented);
        Assert.Equal(new[] { Luke, Vader }.OrderBy(x => x), classic.All.Where(classic.IsTrainer).Select(c => c.Id).OrderBy(x => x));
    }

    // m5-characters-and-force CH40 (Q17, Paul 2026-10-18 cards "Skip the strong" and "Leave out hunters"): the draw never takes anyone already at
    // Talent 80+ (the Emperor and Vader keep their Talent but never train) nor a character flagged never_talented (the Repossessors); Leia and
    // Oti stay in the pool. With both switches off the strong can be drawn again.
    [Fact]
    public void CH40_TalentedDrawSkipsTheStrongAndTheHunters()
    {
        var hunters = new[] { 928, 929, 930, 931 }; // Usura, Mora, Damnum, Pignus (roster.csv never_talented = 1)
        var first = Make(1).Characters;
        Assert.All(hunters, id => Assert.True(first.Data.Get(id).NeverTalented));
        Assert.False(first.Data.Get(577).NeverTalented); // Leia
        foreach (int seed in Enumerable.Range(1, 12))
        {
            var ch = Make(seed).Characters;
            Assert.All(ch.All.Where(c => c.Talented), c => Assert.DoesNotContain(c.Id, hunters.Append(Palpatine).Append(Vader)));
            Assert.Equal(3, ch.InPlay.Count(c => c.Side == Side.Hand && c.Talented));
            Assert.True(ch.Get(Vader)!.EffectiveTalent >= 80);
            Assert.False(ch.IsTrainer(ch.Get(Vader)!));
        }
        var open = new CharacterParams { TalentedSkipStrong = false, TalentedSkipFlagged = false };
        Assert.Contains(Enumerable.Range(1, 30), seed =>
            Make(seed, new GameSettings { Characters = open }).Characters.All.Any(c => c.Talented && (c.Id == Vader || c.Id == Palpatine)));
    }

    // m5-setup-and-victory SV44: same seed same cast; the character draws come before the economy and research arming
    [Fact]
    public void P9_Deterministic()
    {
        string Cast(GameHost h) => string.Join(";", h.Characters.InPlay.Select(c => $"{c.Id}@{c.System}/{c.AttachedFleet}:{string.Join(",", c.Skills)}:{c.EffectiveTalent}"));
        Assert.Equal(Cast(Make(5)), Cast(Make(5)));
        Assert.Equal(Make(5).StateHash(), Make(5).StateHash());
        Assert.NotEqual(Cast(Make(5)), Cast(Make(6)));
    }

    // m5-setup-and-victory §P9 (StartCastOfficers None, StartExtrasPerSide 0)
    [Fact]
    public void P9_Switches()
    {
        var h = Make(1, new GameSettings { Characters = new CharacterParams { StartCastOfficers = StartCastOfficers.None, StartExtrasPerSide = 0 } });
        var emp = h.Characters.InPlay.Where(c => c.Side == Side.Hand).Select(c => c.Id).ToList();
        Assert.Equal(new[] { Palpatine, Vader }, emp);
        Assert.Equal(7, h.Characters.InPlay.Count(c => c.Side == Side.Collective));
    }

    // m5-setup-and-victory §P9 (RandomLocation): r over systems then fleets; a fleet costs a second draw j
    [Fact]
    public void RandomLocation_DrawsSystemThenFleetShip()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Hand); w.Hold(Yavin, Side.Hand);
        int f = w.Fleet(Side.Hand, Coruscant, Isd, 3);
        var stub = new Praedra.Core.Tests.Economy.StubRng().Queue(1, 2, 1);
        Assert.Equal((Hoth, 0), w.Ch.RandomLocation(Side.Hand, stub));
        Assert.Equal(((string?)null, f), w.Ch.RandomLocation(Side.Hand, stub));
        Assert.Equal(new[] { (0, 2), (0, 2), (0, 2) }, stub.Requests.Select(r => (r.Min, r.Max)));
    }

    // m5-setup-and-victory §Victory: the character terms, behind StandardVictory (DEFAULT on since 2026-10-16, see VictoryRules)
    [Fact]
    public void Victory_StandardNeedsTheKeyCharactersCaptured()
    {
        var off = Make(2, OldVictory.Settings); // HQ-only set explicitly: standard victory is the default now
        off.Fleets.SetOwner(Coruscant, Side.Collective);
        off.Game.Step();
        Assert.Equal(Side.Collective, off.Victory.Winner); // HQ-only flavour (explicit)

        var h = Make(2); // the default: standard victory
        h.Fleets.SetOwner(Coruscant, Side.Collective);
        h.Game.Step();
        Assert.Null(h.Victory.Winner); // Vader and Palpatine are free
        bool done = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (done) return; done = true;
            h.Characters.Capture(ctx, h.Characters.Get(Vader)!, Side.Collective, null, CharacterCause.Mission);
            h.Characters.Capture(ctx, h.Characters.Get(Palpatine)!, Side.Collective, null, CharacterCause.Mission);
        });
        h.Game.Step();
        Assert.Equal(Side.Collective, h.Victory.Winner);
    }

    // m5-setup-and-victory §Victory (KeyDeathFallback): Immortal turns a key character's death into an injury; Waive counts it as captured
    [Fact]
    public void Victory_KeyDeathFallbacks()
    {
        var imm = Make(3, new GameSettings
        {
            Characters = new CharacterParams { MajorsCanDie = true },
            Victory = new VictoryRules { StandardVictory = true, KeyDeathFallback = KeyDeathFallback.Immortal },
        });
        bool once = false;
        imm.Game.RegisterHook("5.2", ctx => { if (!once) { once = true; imm.Characters.Kill(ctx, imm.Characters.Get(Luke)!, CharacterCause.Mission); } });
        imm.Game.Step();
        Assert.NotEqual(CharacterStatus.Dead, imm.Characters.Get(Luke)!.Status);

        var waive = Make(3, new GameSettings
        {
            Characters = new CharacterParams { MajorsCanDie = true },
            Victory = new VictoryRules { StandardVictory = true, KeyDeathFallback = KeyDeathFallback.WaiveCondition },
        });
        bool once2 = false;
        waive.Game.RegisterHook("5.2", ctx =>
        {
            if (once2) return; once2 = true;
            waive.Characters.Kill(ctx, waive.Characters.Get(Vader)!, CharacterCause.Mission);
            waive.Characters.Capture(ctx, waive.Characters.Get(Palpatine)!, Side.Collective, null, CharacterCause.Mission);
        });
        waive.Game.Step();
        Assert.Equal(CharacterStatus.Dead, waive.Characters.Get(Vader)!.Status);
        Assert.True(waive.Victory.KeysCaptured(Side.Collective));
    }

    // m5-characters-and-force §Where it runs: the simple AI ignores characters and the game keeps running with them
    [Fact]
    public void Ai_RunsWithCharacters()
    {
        var h = Make(4);
        var ai = new Praedra.Core.AI.AiMatch(h);
        ai.RunTo(120);
        Assert.All(h.Characters.InPlay, c => Assert.NotEqual(CharacterStatus.Dead, c.Status));
    }
}
