using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Construction;

/// <summary>
/// A construction yard needs no construction yard (Paul 2026-11-20): on a held system with none, a standard Construction Yard order builds
/// itself at one standard yard's pace. rule: m2-construction §Founding a construction yard.
/// </summary>
public class FoundingTests
{
    private const string Cy = "Construction Yard";

    private static (GameHost H, string World) Host(bool on = true)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = 1, Settings = new GameSettings { FoundConstructionYards = on } });
        var eco = h.Economy;
        string world = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal)
            .First(s => eco.FreeEnergy(s) >= 2 && !eco.Objects.Any(o => o.System == s && o.Class == Cy) && !h.Support.IsUprising(s)
                && !h.Construction.IsSuspendedAt(s));
        return (h, world);
    }

    private static string? Rejection(GameHost h, long seq) => h.Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq)?.Reason;

    // m2-construction §Founding a construction yard F1: accepted with no construction yard on the system; done after 10 cycles of 4 days, 1 refined each
    [Fact]
    public void F1_AConstructionYardFoundsItselfOnAWorldWithNone()
    {
        var (h, world) = Host();
        Assert.Equal(40, h.Construction.FoundingDays(10));
        Assert.True(h.Construction.CanFoundYardAt(world));
        long seq = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 1);
        h.Game.Step();
        Assert.Contains(h.Game.Events.OfType<OrderAccepted>(), e => e.Sequence == seq);
        Assert.False(h.Construction.CanFoundYardAt(world)); // one project at a time
        var done = default(UnitCompleted);
        for (int i = 0; i < 60 && done == null; i++)
        {
            h.Game.Step();
            done = h.Game.Events.OfType<UnitCompleted>().FirstOrDefault(e => e.Class == Cy && e.Yard == world);
        }
        Assert.NotNull(done);
        Assert.Contains(h.Economy.Objects, o => o.System == world && o.Class == Cy && o.Completed);
        // the new yard builds the next facility as any yard does
        long mine = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, "Refinery", 1, world);
        h.Game.Step();
        Assert.Null(Rejection(h, mine));
    }

    // F2: off (Classic) the order is refused NoYard
    [Fact]
    public void F2_OffOnlyAConstructionYardBuildsOne()
    {
        var (h, world) = Host(on: false);
        Assert.False(h.Construction.CanFoundYardAt(world));
        long seq = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 1);
        h.Game.Step();
        Assert.Equal(RejectReason.NoYard, Rejection(h, seq));
    }

    // F3: only one standard yard for the system itself; other facilities and a second founding order still need a construction yard
    [Fact]
    public void F3_OnlyTheYardItselfIsFreed()
    {
        var (h, world) = Host();
        string other = h.Game.State.SystemOwners.Where(kv => kv.Value == Side.Hand && kv.Key != world).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).First();
        long two = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 2);
        long away = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 1, other);
        long mine = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, "Mine", 1, world);
        h.Game.Step();
        Assert.Equal(RejectReason.NoYard, Rejection(h, two));
        Assert.Equal(RejectReason.NoYard, Rejection(h, away));
        Assert.Equal(RejectReason.NoYard, Rejection(h, mine));
        long first = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 1);
        long second = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 1);
        h.Game.Step();
        Assert.Null(Rejection(h, first));
        Assert.Equal(RejectReason.NoYard, Rejection(h, second));
    }

    // F4: the founding yard pays 1 refined per cycle and waits while the side has none
    [Fact]
    public void F4_ItWaitsForRefined()
    {
        var (h, world) = Host();
        h.Economy.SetPools(Side.Hand, h.Economy.Raw(Side.Hand), 0);
        long seq = h.Construction.SubmitBuild(Side.Hand, world, YardFamily.Construction, Cy, 1);
        h.Game.Step();
        Assert.Null(Rejection(h, seq));
        var p = h.Construction.ProjectAt(world, YardFamily.Construction)!;
        Assert.True(p.Founding);
        Assert.Equal(0, p.FoundDue);
        Assert.Equal(0, p.Progress);
    }
}
