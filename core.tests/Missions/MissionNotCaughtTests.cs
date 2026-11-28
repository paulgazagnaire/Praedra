using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Missions;
using static Praedra.Core.Tests.Missions.MissionWorld;

namespace Praedra.Core.Tests.Missions;

public class MissionNotCaughtTests
{
    // m5-characters-and-talent CH51 (Caught on arrival; Paul 2026-11-28: a leader sent on a mission to an occupied world is not caught by the
    // fleet in orbit, the mission's own rules apply): a Collective Espionage team lands on the Hand's T, where an armed Hand fleet stands; no
    // Intercept capture on arrival or on the way home; the fleet's ships still guard T by the mission's own rules (here they spot the team).
    [Fact]
    public void CH51_AMissionTeamIsNeverCaughtOnArrival()
    {
        var w = new MissionWorld();
        w.Hold(H, Side.Collective, 100); w.Hold(T, Side.Hand, 0);
        w.Host.Fleets.AddFleet(Side.Hand, "Guard", new[] { "Mandate", "Mandate" }, T);
        w.Agent(Midwife, H, esp: 100);
        w.Start(Side.Collective, MissionKind.Espionage, new[] { Midwife }, T);
        w.Game.Step();
        int id = w.LastMission;
        for (int i = 0; i < 300 && w.M.Get(id) != null; i++) w.Game.Step();
        Assert.Contains(w.Events<MissionDetectionRoll>(), e => e.Mission == id); // the team reached T: the fleet's ships guard it by the mission's own rules
        Assert.DoesNotContain(w.Events<CharacterCaptured>(), e => e.Cause == CharacterCause.Intercept);
    }
}
