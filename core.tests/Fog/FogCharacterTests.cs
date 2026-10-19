using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using Praedra.Core.Tests.Economy;
using static Praedra.Core.Tests.Fog.FogWorld;

namespace Praedra.Core.Tests.Fog;

// Characters in group D, FD1 to FD5 of m5-fog-and-hq §Tests (Map M). Group D holds "characters present" (§F1, Manual p.69, p.121).
public class FogCharacterTests
{
    /// <summary>A roster character of <paramref name="side"/> at base values (no game draw), placed at a system. Returns its id.</summary>
    private static int Person(FogWorld w, Side side, string system, int nth = 0)
    {
        var cls = w.H.Characters.Data.Roster.Where(c => c.Side == side).OrderBy(c => c.Id).ElementAt(nth);
        w.H.Characters.Create(cls, new StubRng());
        w.H.Characters.Place(cls.Id, system);
        return cls.Id;
    }

    /// <summary>An Empire espionage success on <paramref name="target"/> in the next Step; returns the stamp written.</summary>
    private static int Espionage(FogWorld w, string target)
    {
        int stamp = 0;
        w.InStep(ctx => { w.Fog.EspionageSuccess(ctx, Side.Empire, target); stamp = ctx.Day + 1; });
        w.Step();
        return stamp;
    }

    // m5-fog-and-hq FD1
    [Fact]
    public void FD1_EspionageRevealsAnEnemyCharacterAtTheTarget()
    {
        var w = new FogWorld();
        int id = Person(w, Side.Alliance, R3);
        w.Step(3);
        Assert.Null(w.Pic(Side.Empire, R3)); // never looked: nothing known
        int stamp = Espionage(w, R3);
        var ch = w.Pic(Side.Empire, R3)!.Characters!;
        Assert.Equal(stamp, ch.Stamp);
        var c = Assert.Single(ch.Value);
        Assert.Equal((id, Side.Alliance, CharacterStatus.Free), (c.Id, c.Side, c.Status));
        Assert.Contains(w.Fog.ViewFor(Side.Empire).CharactersSeen(), t => t.System == R3 && t.Character.Id == id && t.Stamp == stamp);
    }

    // m5-fog-and-hq FD2
    [Fact]
    public void FD2_HoldingTheSystemShowsCharactersLive()
    {
        var w = new FogWorld();
        int a = Person(w, Side.Alliance, R1);
        int b = Person(w, Side.Alliance, R1, 1);
        w.Step();
        var ch = w.Pic(Side.Empire, R1)!.Characters!;
        Assert.Equal(w.Game.Day, ch.Stamp);
        Assert.Equal(new[] { a, b }.OrderBy(x => x), ch.Value.Select(c => c.Id)); // sorted by id
        w.H.Characters.Place(a, C0); // a walks to C0 (Empire-held too)
        w.Step();
        Assert.Equal(new[] { b }, w.Pic(Side.Empire, R1)!.Characters!.Value.Select(c => c.Id));
        Assert.Equal(new[] { a }, w.Pic(Side.Empire, C0)!.Characters!.Value.Select(c => c.Id));
        Assert.Equal(w.Game.Day, w.Pic(Side.Empire, C0)!.Characters!.Stamp);
    }

    // m5-fog-and-hq FD3
    [Fact]
    public void FD3_AFleetArrivingAloneDoesNotRevealCharacters()
    {
        var w = new FogWorld();
        Person(w, Side.Empire, R1);
        int f = w.Fleet(Side.Alliance, R2, Corvette, 1);
        w.H.Fleets.SubmitMove(Side.Alliance, f, R1);
        w.StepTo(16);
        var p = w.Pic(Side.Alliance, R1)!;
        Assert.NotNull(p.Fleets); // Presence wrote A, B and E
        Assert.Null(p.Characters); // not D
        Assert.Null(w.Know(Side.Alliance, R1)!.Characters);
        Assert.DoesNotContain(w.Fog.ViewFor(Side.Alliance).CharactersSeen(), t => t.System == R1);
    }

    // m5-fog-and-hq FD4
    [Fact]
    public void FD4_ThePictureKeepsTheOldCharacterListWhenTheViewIsLost()
    {
        var w = new FogWorld();
        int id = Person(w, Side.Alliance, R3);
        int stamp = Espionage(w, R3);
        w.H.Characters.Place(id, R4); // the character leaves R3; the Empire does not see it
        w.Step(10);
        var ch = w.Pic(Side.Empire, R3)!.Characters!;
        Assert.Equal(stamp, ch.Stamp);
        Assert.Equal(w.Game.Day - stamp, ch.AgeDays);
        Assert.Equal(new[] { id }, ch.Value.Select(c => c.Id));
        Assert.Null(w.Pic(Side.Empire, R4));
    }

    // m5-fog-and-hq FD5
    [Fact]
    public void FD5_SaveAndLoadKeepTheCharacters()
    {
        FogWorld Make()
        {
            var w = new FogWorld();
            Person(w, Side.Alliance, R3);
            Person(w, Side.Alliance, R1, 1);
            Espionage(w, R3);
            w.Step(4);
            return w;
        }
        var whole = Make(); whole.Step(20);
        var first = Make();
        string save = first.H.Save();
        Assert.Contains("\"Characters\":[{\"Id\":", save);
        var loaded = GameHost.Load(save, first.H.Settings, FogWorld.Data);
        Assert.Equal(first.H.StateHash(), loaded.StateHash());
        foreach (var side in new[] { Side.Empire, Side.Alliance })
            Assert.Equal(first.Fog.KnowledgeHash(side), loaded.Fog.KnowledgeHash(side));
        Assert.Equal(first.Fog.ViewFor(Side.Empire).CharactersSeen().Select(t => (t.System, t.Character.Id, t.Character.Side, t.Character.Status, t.Stamp)),
            loaded.Fog.ViewFor(Side.Empire).CharactersSeen().Select(t => (t.System, t.Character.Id, t.Character.Side, t.Character.Status, t.Stamp)));
        for (int i = 0; i < 20; i++) loaded.Game.Step();
        Assert.Equal(whole.H.StateHash(), loaded.StateHash());
        foreach (var side in new[] { Side.Empire, Side.Alliance })
            Assert.Equal(whole.Fog.KnowledgeHash(side), loaded.Fog.KnowledgeHash(side));
    }
}
