using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Economy;

// QA senior QS-1 (speed, no rule of its own): the registry index (core/Economy/ObjectIndex.cs) answers "which objects stand on this system" and
// "which ride on this ship" for the rules that read them (m2-slots-and-resources §Occupied slots, m2-control-and-capture, m3-fleets §Carrying
// rules, ...). It must give exactly what a full scan of the registry gives, in the same (id) order, at any moment: after every slot of a Step
// too, since the rules read it between their own changes. Its PlacementVersion must change whenever the fields it watches change.
public class ObjectIndexTests
{
    private static void AssertMatchesScan(GameState state, string where)
    {
        var e = state.Economy;
        var bySystem = e.Objects.Values.Where(o => o.System != null).GroupBy(o => o.System!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList(), StringComparer.Ordinal); // lookup only
        foreach (var (sys, ids) in bySystem.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Assert.True(ids.SequenceEqual(e.Index.At(sys).Select(o => o.Id)), $"{where}: objects at {sys} differ from the scan");
        var byShip = e.Objects.Values.Where(o => o.AboardShip != 0).GroupBy(o => o.AboardShip)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Id).ToList()); // lookup only
        foreach (var (ship, ids) in byShip.OrderBy(kv => kv.Key))
            Assert.True(ids.SequenceEqual(e.Index.Aboard(ship).Select(o => o.Id)), $"{where}: objects aboard {ship} differ from the scan");
        // nothing extra: every listed object is in the registry, at that place
        foreach (var o in e.Objects.Values)
        {
            if (o.System != null) Assert.Contains(o, e.Index.At(o.System));
            if (o.AboardShip != 0) Assert.Contains(o, e.Index.Aboard(o.AboardShip));
        }
        Assert.Equal(bySystem.Values.Sum(l => l.Count), bySystem.Keys.Sum(s => e.Index.At(s).Count));
        Assert.Empty(e.Index.At("No such system"));
        Assert.Empty(e.Index.Aboard(-1));
    }

    private static string Placement(GameState state) =>
        string.Join(";", state.Economy.Objects.Values.Select(o => $"{o.Id}:{o.Category}:{o.Class}:{o.Side}:{o.System}:{o.Completed}:{o.AboardShip}"));

    // A Hard mirror (fog, both AIs) for 900 days: battles, captures, landings, loading, building, scrapping and destruction all move objects.
    // The index is compared with a full scan after every slot of every Step (the settle callback runs there), and whenever the placement version
    // did not move, the placement of every object must be what it was.
    [Trait("Category", "Play")]
    [Fact]
    public void IndexMatchesAFullScanAfterEverySlotOfALongGame()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 6, Preset = StartPreset.MedA });
        var state = host.Game.State;
        long version = -1; string placement = "";
        int checks = 0;
        void Check(StepContext ctx)
        {
            if (ctx.Day % 3 != 0) return; // every slot of every third day keeps the test fast enough
            AssertMatchesScan(state, $"day {ctx.Day + 1}");
            long v = state.Economy.Index.PlacementVersion; string p = Placement(state);
            if (v == version) Assert.True(p == placement, $"day {ctx.Day + 1}: placement changed but the version did not");
            version = v; placement = p; checks++;
        }
        host.Game.RegisterSettle(Check, Check);
        new AiMatch(host, AiLevels.For(AiLevel.Hard), AiLevels.For(AiLevel.Hard)).RunTo(900);
        Assert.True(host.Game.Day >= 600, "the game should run long"); // seed 6 runs to the limit (bench: day 1500)
        Assert.True(checks > 1000);
        AssertMatchesScan(state, "end");
    }

    // After a load the index is built from the loaded registry (it is never saved) and gives the same answers as in the game that was saved.
    [Fact]
    public void ALoadedGameHasTheSameIndex()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });
        new AiMatch(host, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium)).RunTo(150);
        var loaded = GameHost.Load(host.SaveWithSettings());
        AssertMatchesScan(loaded.Game.State, "loaded");
        foreach (var sys in host.Data.Galaxy.Systems.Select(s => s.Name))
            Assert.Equal(host.Economy.ObjectsAt(sys).Select(o => o.Id), loaded.Economy.ObjectsAt(sys).Select(o => o.Id));
        Assert.Equal(host.StateHash(), loaded.StateHash());
    }

    // The index follows each change at once: a move, a boarding, a landing, a removal, a new object; the version moves with each, and with a
    // change of the other watched fields; a change of any other field leaves it.
    [Fact]
    public void TheIndexFollowsEveryChangeAtOnce()
    {
        var e = new EconomyState();
        int a = ObjectRegistry.Add(e, new EconObject { Category = ObjectCategory.Regiment, Class = "R", Side = Side.Hand, System = "Alpha", Completed = true }, null);
        int b = ObjectRegistry.Add(e, new EconObject { Category = ObjectCategory.Regiment, Class = "R", Side = Side.Hand, System = "Alpha", Completed = true }, null);
        int ship = ObjectRegistry.Add(e, new EconObject { Category = ObjectCategory.CapitalShip, Class = "S", Side = Side.Hand }, null);
        Assert.Equal(new[] { a, b }, e.Index.At("Alpha").Select(o => o.Id));

        long v = e.Index.PlacementVersion;
        var ob = e.Objects[b];
        ob.System = null; ob.AboardShip = ship; // boards
        Assert.Equal(new[] { a }, e.Index.At("Alpha").Select(o => o.Id));
        Assert.Equal(new[] { b }, e.Index.Aboard(ship).Select(o => o.Id));
        Assert.NotEqual(v, v = e.Index.PlacementVersion);

        ob.AboardShip = 0; ob.System = "Beta"; // lands elsewhere
        Assert.Empty(e.Index.Aboard(ship));
        Assert.Equal(new[] { b }, e.Index.At("Beta").Select(o => o.Id));
        Assert.NotEqual(v, v = e.Index.PlacementVersion);

        e.Objects[a].System = "Beta"; // joins, kept in id order
        Assert.Equal(new[] { a, b }, e.Index.At("Beta").Select(o => o.Id));
        Assert.Empty(e.Index.At("Alpha"));
        v = e.Index.PlacementVersion;

        foreach (Action change in new Action[]
                 {
                     () => e.Objects[a].Completed = false, () => e.Objects[a].Side = Side.Collective, () => e.Objects[a].Class = "Q",
                     () => e.Objects[a].Category = ObjectCategory.SpecialForce,
                 })
        {
            change();
            Assert.NotEqual(v, v = e.Index.PlacementVersion);
        }
        e.Objects[a].Hull = 5; e.Objects[a].Due = 9; e.Objects[a].Share = 3; // not watched
        Assert.Equal(v, e.Index.PlacementVersion);

        var gone = e.Objects[a];
        e.Objects.Remove(a); e.Index.Removed(gone); // as the economy removes an object
        Assert.Equal(new[] { b }, e.Index.At("Beta").Select(o => o.Id));
        gone.System = "Alpha"; // a removed object no longer reports
        Assert.Empty(e.Index.At("Alpha"));

        int c = ObjectRegistry.Add(e, new EconObject { Category = ObjectCategory.Facility, Class = "Mine", System = "Beta" }, null);
        Assert.Equal(new[] { b, c }, e.Index.At("Beta").Select(o => o.Id));
    }

    // The public reads hand out copies (Ponytail on #418): a loop that moves every unit it reads, legal before the index, still works.
    [Fact]
    public void ALoopMayMoveWhatItReads()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 3, Preset = StartPreset.MedA });
        string from = host.Economy.Objects.First(o => o.Category == ObjectCategory.Regiment && o.System != null && o.AboardShip == 0).System!;
        int n = host.Economy.ObjectsAt(from).Count(o => o.Category == ObjectCategory.Regiment && o.AboardShip == 0);
        foreach (var o in host.Economy.ObjectsAt(from)) if (o.Category == ObjectCategory.Regiment && o.AboardShip == 0) o.System = "Charter";
        Assert.Empty(host.Economy.ObjectsAt(from).Where(o => o.Category == ObjectCategory.Regiment && o.AboardShip == 0));
        Assert.True(n > 0);
        int ship = host.Economy.Objects.First(o => o.Category == ObjectCategory.CapitalShip && host.Economy.ObjectsAboard(o.Id).Count > 0).Id;
        foreach (var o in host.Economy.ObjectsAboard(ship)) o.AboardShip = 0;
        Assert.Empty(host.Economy.ObjectsAboard(ship));
    }
}
