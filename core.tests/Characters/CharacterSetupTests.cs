using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;
using Rebellion.Core.Victory;
using static Rebellion.Core.Tests.Characters.CharWorld;

namespace Rebellion.Core.Tests.Characters;

/// <summary>The starting characters (m5-setup-and-victory P9), the roster data, and the character terms of victory.</summary>
public class CharacterSetupTests
{
    private static GameHost Make(int seed = 1, GameSettings? s = null) => NewGame.Create(new NewGameOptions { Seed = seed, Settings = s });

    // m5-characters-and-force §The character object; Decided roster (heroes/refined-roster, added-heroes)
    [Fact]
    public void Roster_RefinedFilmEra()
    {
        var d = GameData.Default.Characters;
        Assert.Equal(76, d.All.Count);
        Assert.Equal(65, d.Roster.Count);
        foreach (var cut in new[] { "Thrawn", "Pellaeon", "Garm Bel Iblis", "Daala", "Zuggs", "Niles Ferrier", "Grammel", "Griff", "Klev", "Ma'w'shiye", "Kaiya Andrimetrum" })
            Assert.DoesNotContain(d.Roster, c => c.Name == cut);
        foreach (var hunter in new[] { "Boba Fett", "Bossk", "Dengar", "Black Krrsantan" })
        {
            var c = d.Roster.Single(x => x.Name == hunter);
            Assert.Equal(Side.Empire, c.Side);
            Assert.False(c.CanAdmiral || c.CanCommander || c.CanGeneral); // no officer roles, like Chewbacca
        }
        Assert.Equal(50, d.Get(Han).NavigatorSpeed);
        Assert.Equal(100, d.Get(Luke).NavigatorSpeed);
        Assert.True(d.Get(Luke).JediTrainer && d.Get(Vader).JediTrainer);
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
        foreach (var side in new[] { Side.Empire, Side.Alliance })
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

    // m5-setup-and-victory SV44: same seed same cast; the character draws come before the economy and research arming
    [Fact]
    public void P9_Deterministic()
    {
        string Cast(GameHost h) => string.Join(";", h.Characters.InPlay.Select(c => $"{c.Id}@{c.System}/{c.AttachedFleet}:{string.Join(",", c.Skills)}:{c.EffectiveForce}"));
        Assert.Equal(Cast(Make(5)), Cast(Make(5)));
        Assert.Equal(Make(5).StateHash(), Make(5).StateHash());
        Assert.NotEqual(Cast(Make(5)), Cast(Make(6)));
    }

    // m5-setup-and-victory §P9 (StartCastOfficers None, StartExtrasPerSide 0)
    [Fact]
    public void P9_Switches()
    {
        var h = Make(1, new GameSettings { Characters = new CharacterParams { StartCastOfficers = StartCastOfficers.None, StartExtrasPerSide = 0 } });
        var emp = h.Characters.InPlay.Where(c => c.Side == Side.Empire).Select(c => c.Id).ToList();
        Assert.Equal(new[] { Palpatine, Vader }, emp);
        Assert.Equal(7, h.Characters.InPlay.Count(c => c.Side == Side.Alliance));
    }

    // m5-setup-and-victory §P9 (RandomLocation): r over systems then fleets; a fleet costs a second draw j
    [Fact]
    public void RandomLocation_DrawsSystemThenFleetShip()
    {
        var w = new CharWorld();
        w.Hold(Hoth, Side.Empire); w.Hold(Yavin, Side.Empire);
        int f = w.Fleet(Side.Empire, Coruscant, Isd, 3);
        var stub = new Rebellion.Core.Tests.Economy.StubRng().Queue(1, 2, 1);
        Assert.Equal((Yavin, 0), w.Ch.RandomLocation(Side.Empire, stub));
        Assert.Equal(((string?)null, f), w.Ch.RandomLocation(Side.Empire, stub));
        Assert.Equal(new[] { (0, 2), (0, 2), (0, 2) }, stub.Requests.Select(r => (r.Min, r.Max)));
    }

    // m5-setup-and-victory §Victory: the character terms, behind StandardVictory (DEFAULT off here, see VictoryRules)
    [Fact]
    public void Victory_StandardNeedsTheKeyCharactersCaptured()
    {
        var off = Make(2);
        off.Fleets.SetOwner(Coruscant, Side.Alliance);
        off.Game.Step();
        Assert.Equal(Side.Alliance, off.Victory.Winner); // HQ-only flavour (current default)

        var h = Make(2, new GameSettings { Victory = new VictoryRules { StandardVictory = true } });
        h.Fleets.SetOwner(Coruscant, Side.Alliance);
        h.Game.Step();
        Assert.Null(h.Victory.Winner); // Vader and Palpatine are free
        bool done = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (done) return; done = true;
            h.Characters.Capture(ctx, h.Characters.Get(Vader)!, Side.Alliance, null, CharacterCause.Mission);
            h.Characters.Capture(ctx, h.Characters.Get(Palpatine)!, Side.Alliance, null, CharacterCause.Mission);
        });
        h.Game.Step();
        Assert.Equal(Side.Alliance, h.Victory.Winner);
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
            waive.Characters.Capture(ctx, waive.Characters.Get(Palpatine)!, Side.Alliance, null, CharacterCause.Mission);
        });
        waive.Game.Step();
        Assert.Equal(CharacterStatus.Dead, waive.Characters.Get(Vader)!.Status);
        Assert.True(waive.Victory.KeysCaptured(Side.Alliance));
    }

    // m5-characters-and-force §Where it runs: the simple AI ignores characters and the game keeps running with them
    [Fact]
    public void Ai_RunsWithCharacters()
    {
        var h = Make(4);
        var ai = new Rebellion.Core.AI.AiMatch(h);
        ai.RunTo(120);
        Assert.All(h.Characters.InPlay, c => Assert.NotEqual(CharacterStatus.Dead, c.Status));
    }
}
