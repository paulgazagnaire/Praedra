using Rebellion.Core.AI;
using Rebellion.Core.Clock;
using Rebellion.Core.Fog;
using Rebellion.Core.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// The simple AI behind the fog (SimpleAiParams.UseFog): it reads only its side's KnowledgeView. rule: m5-fog-and-hq §N8 (the AI plays fair,
// Decided), §N5 (KnownHq). With UseFog off (DEFAULT for now) the AI is unchanged (the other AI tests).
public class SimpleAiFogTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiFogTests(ITestOutputHelper o) { _out = o; }

    private static readonly SimpleAiParams Fog = new() { UseFog = true };

    private static (AiMatch M, GameHost H) Match(int seed, Side? passive = null, Action<GameHost>? setup = null)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
        setup?.Invoke(h);
        return (new AiMatch(h, Fog, Fog, passive), h);
    }

    [Fact]
    public void UseFogIsOffByDefault() => Assert.False(new SimpleAiParams().UseFog);

    // m5-fog-and-hq §N5: KnownEnemyHq is set only when the Empire's own picture shows the HQ.
    [Fact]
    public void KnownEnemyHqComesOnlyFromTheEmpiresPicture()
    {
        var (m, h) = Match(1);
        m.Empire.KnownEnemyHq = h.HqSystem; // a caller's hint is ignored with fog
        m.Step();
        Assert.Null(m.Empire.KnownEnemyHq);
        string hq = h.HqSystem!;
        bool done = false;
        h.Game.RegisterHook("6.8", ctx => { if (!done) { done = true; h.Fog.EspionageSuccess(ctx, Side.Empire, hq); } });
        m.Step(); // the reveal lands at the end of this Step
        m.Step(); // planned with the new picture
        Assert.Equal(hq, m.Empire.KnownEnemyHq);
    }

    // m5-fog-and-hq FN1 with the AI: two games that differ only in where the (unseen) HQ stands give the Empire AI the same view, so the
    // same orders, day after day, as long as its knowledge is the same (its spies may find out, SimpleAiParams.UseCharactersAndMissions:
    // measured, the knowledge parts on day 135 on seed 2, when a look at the HQ candidate Dagobah reports an HQ in one game only).
    [Fact]
    public void FN1_EmpireAiOrdersDoNotDependOnAnUnseenHq()
    {
        var (a, ha) = Match(2, passive: Side.Alliance);
        string other = ha.Game.State.SystemOwners.Where(kv => kv.Value == Side.Alliance && kv.Key != ha.HqSystem && !ha.Fog.IsExplored(Side.Empire, kv.Key))
            .Select(kv => kv.Key).First();
        var (b, hb) = Match(2, passive: Side.Alliance, setup: h => h.Fog.PlaceHq(other));
        Assert.NotEqual(ha.HqSystem, hb.HqSystem);
        int same = 0;
        for (int day = 0; day < 250; day++)
        {
            a.Step(); b.Step();
            Assert.Equal(a.Empire.Submitted.Select(o => o.Kind + " " + o.Detail), b.Empire.Submitted.Select(o => o.Kind + " " + o.Detail));
            if (ha.Fog.KnowledgeHash(Side.Empire) != hb.Fog.KnowledgeHash(Side.Empire)) break; // the Empire found out: the games may part now
            same++;
            if (ha.Victory.Winner != null || hb.Victory.Winner != null) break;
        }
        Assert.True(same >= 100, $"the knowledge parted on day {same}");
        Assert.True(a.TotalSent > 20, $"sent {a.TotalSent} day {ha.Game.Day} winner {ha.Victory.Winner}");
    }

    // m5-fog-and-hq §N9: same seed, same game; a game loaded in the middle continues the same way every time.
    // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns the fix): the AI is NOT memoryless. It reads the recent event
    // log (bombardments of the last 30 days, retreats, lost battles, foiled looks), and a save keeps no events, so after a load the AI
    // forgets them. On v5 seed 3 a pile that shelled Desevro before day 100 lands on day 102 in the uninterrupted game and shells again in
    // the loaded one. The test now pins that two loads of the same save play the same; "loaded = uninterrupted" is the trainer's target.
    [Fact]
    public void FogGamesAreDeterministicAndSurviveSaveLoad()
    {
        var (a, ha) = Match(3);
        a.RunTo(200);
        var (b, hb) = Match(3);
        b.RunTo(100);
        string save = hb.Save();
        var loaded = GameHost.Load(save);
        var c = new AiMatch(loaded, Fog, Fog);
        c.RunTo(200);
        var loaded2 = GameHost.Load(save);
        new AiMatch(loaded2, Fog, Fog).RunTo(200);
        Assert.Equal(loaded.StateHash(), loaded2.StateHash());
        var (d, hd) = Match(3);
        d.RunTo(200);
        Assert.Equal(ha.StateHash(), hd.StateHash());
        _out.WriteLine($"orders {a.TotalSent} rejected {a.TotalRejected}; explored E {ha.Data.Galaxy.Systems.Count(s => ha.Fog.IsExplored(Side.Empire, s.Name))} A {ha.Data.Galaxy.Systems.Count(s => ha.Fog.IsExplored(Side.Alliance, s.Name))}");
        Assert.True(a.TotalRejected * 20 <= a.TotalSent, $"{a.TotalRejected} of {a.TotalSent} orders rejected");
    }
}
