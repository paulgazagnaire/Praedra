using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using static Praedra.Core.Tests.Characters.CharWorld;

namespace Praedra.Core.Tests.Characters;

// rule: m5-characters-and-talent §Posts (Commander merged into Admiral, Paul 2026-11-27): no fleet Commander; a Commander flag also allows Admiral;
// an older save's Commanders become passengers at load. CharacterParams.CommanderIntoAdmiral DEFAULT true; false = Classic.
public class CommanderIntoAdmiralTests
{
    private const int RowanFurrow = 843; // Collective, commander and general flags, no admiral flag, leadership 80 (data/classic/characters.csv)

    private static long Attach(CharWorld w, int c, int fleet, CharacterPost p) => w.Submit(seq => new AttachCharacterOrder(seq, Side.Collective, c, fleet, p));

    // m5-characters-and-talent CH46: under the DEFAULT no one is Commander, and a Commander flag makes an Admiral who counts for bombardment
    [Fact]
    public void CH46_CommanderFlagMakesAnAdmiral_NoOneIsCommander()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(RowanFurrow, Shoal);
        Assert.False(w.Ch.CanHold(w.C(RowanFurrow), CharacterPost.Commander));
        Assert.True(w.Ch.CanHold(w.C(RowanFurrow), CharacterPost.Admiral));
        Assert.True(w.Ch.CanHold(w.C(RowanFurrow), CharacterPost.General));

        long cmd = Attach(w, RowanFurrow, f, CharacterPost.Commander);
        w.Game.Step();
        Assert.Equal(CharacterReject.NoCapability, w.Reason(cmd));
        long adm = Attach(w, RowanFurrow, f, CharacterPost.Admiral);
        w.Game.Step();
        Assert.Null(w.Reason(adm));
        Assert.Equal(CharacterPost.Admiral, w.C(RowanFurrow).Post);
        Assert.Equal(80, w.Ch.AdmiralLeadership(w.Game.State.Fleets[f]));
    }

    // m5-characters-and-talent CH47: the Classic switch keeps the Commander post and the data flags as they are
    [Fact]
    public void CH47_ClassicSwitchKeepsTheCommander()
    {
        var w = new CharWorld(new CharacterParams { CommanderIntoAdmiral = false });
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(RowanFurrow, Shoal);
        long adm = Attach(w, RowanFurrow, f, CharacterPost.Admiral);
        w.Game.Step();
        Assert.Equal(CharacterReject.NoCapability, w.Reason(adm));
        long cmd = Attach(w, RowanFurrow, f, CharacterPost.Commander);
        w.Game.Step();
        Assert.Null(w.Reason(cmd));
        Assert.Equal(CharacterPost.Commander, w.C(RowanFurrow).Post);
    }

    // m5-characters-and-talent CH48: a Commander in an older save rides on as a passenger after load; a pending join as Commander becomes a plain join
    [Fact]
    public void CH48_OlderSaveCommanderBecomesAPassengerAtLoad()
    {
        var w = new CharWorld();
        int f = w.Fleet(Side.Collective, Shoal);
        w.Add(RowanFurrow, Shoal);
        w.Ch.SetPost(RowanFurrow, f, CharacterPost.Commander); // as an older save holds it
        w.Add(Keth, Thaw);
        w.C(Keth).JoinFleet = f; w.C(Keth).JoinPost = CharacterPost.Commander;
        w.Reload();
        Assert.Equal(f, w.C(RowanFurrow).AttachedFleet);
        Assert.Equal(CharacterPost.None, w.C(RowanFurrow).Post);
        Assert.Equal(CharacterPost.None, w.C(Keth).JoinPost);
        Assert.Equal(f, w.C(Keth).JoinFleet);
        Assert.Equal(0, w.Ch.RetireCommanders()); // nothing left to change
    }
}
