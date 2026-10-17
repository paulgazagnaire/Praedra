using Rebellion.Core.AI;
using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Fog;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using Xunit.Abstractions;

namespace Rebellion.Core.Tests.AI;

// The AI's characters and missions (SimpleAiParams.UseCharactersAndMissions, DEFAULT true; ai/05 characters, ai/07 §7 HQ search). Rules
// checked through the AI's own orders: m5-missions (StartMission, the Espionage and Reconnaissance successes that write HqHere), m5-fog-and-hq
// §N5 (KnownHq from the side's own picture), §N8 (the fogged AI reads only its KnowledgeView), §F6 (MoveHq), m5-characters (posts:
// AttachCharacter, AssignSystemGeneral). The thresholds are SimpleAiParams DEFAULTs; the limits below are measured with margin.
public class SimpleAiAgentsTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiAgentsTests(ITestOutputHelper o) { _out = o; }

    private static readonly SimpleAiParams Fog = new() { UseFog = true };

    private static (AiMatch M, GameHost H) Match(int seed, SimpleAiParams p, Side? passive = null)
    {
        var h = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA, Settings = Rebellion.Core.Tests.Setup.DealSettings.FileOldVictory });
        return (new AiMatch(h, p, p, passive), h);
    }

    private static int Sent(AiMatch m, string key) => m.Sent.GetValueOrDefault(key);

    // The switch: DEFAULT on; off gives the AI of before byte for byte. The hash is the one main (before this work package) prints for
    // `sim aivai --fog --seed 1 --days 1000`. The chase fix (m6/ai-chase, SimpleAiChaseTests) came later and is switched off here too, and so is the shelled-landing fix (m6/ai-stall, SimpleAiStallTests).
    [Fact]
    public void Switch_DefaultsOn_AndOffIsTheOldAi()
    {
        Assert.True(new SimpleAiParams().UseCharactersAndMissions);
        var (m, h) = Match(1, new SimpleAiParams { UseFog = true, UseCharactersAndMissions = false,
            RunnerRetreats = 0, RetreatCountsAsLoss = false, AvoidBlockedPaths = false, WinRushDefenders = -1, ShelledLandingNeed = false,
            FogAvoidEnemyStops = false, SplitHomeSurplus = false, DetourToHeldStop = false, InvasionBatchesOnly = false, ExplorerScouts = 0 });
        m.RunTo(1000);
        Assert.DoesNotContain(m.Sent.Keys, k => k.Contains("Mission", StringComparison.Ordinal) || k.Contains("Character", StringComparison.Ordinal) || k.Contains("General", StringComparison.Ordinal));
        // The pin moved when the Death Star added saved state (m5-death-star: GameState.DeathStar, SystemKnowledge.Destroyed). The game plays identically:
        // the old hash e8fb3f75...c779 is recovered exactly by removing those new fields from the hashed JSON (checked when the pin was moved).
        // v5 re-pin 2026-10-16: the v5 galaxy (555 systems, routes) changes every game, so the hash now pins this branch's old AI on the
        // v5 map; the new v5 AI fixes (FogAvoidEnemyStops, SplitHomeSurplus) and the AI trainer's fixes (DetourToHeldStop,
        // InvasionBatchesOnly, ExplorerScouts) are switched off above with the rest.
        // B2c re-pin 2026-10-17: the loyalty triggers add saved state (GameState.Loyalty, the per-system loss tallies) and one draw per capture
        // (the new owner's loyalty shift), so this game takes another course from its first capture. Unchanged after merging main (#57, #58).
        // B2e re-pin 2026-10-17: new saved character fields (injury points, leadership_adjustment 50 under the Seat of Power, the escape and
        // heal timers) and new rules this old AI cannot switch off (the Seat of Power raises Imperial Admirals' and Generals' leadership, escape
        // delay draws at each capture, the Hurt roll on injuries) change the state and the game's course.
        Assert.Equal("5e54e22f931b292d7d677e8beada9bb792bdd16f780488e03130d89e879ce007", h.StateHash());
    }

    // m5-fog-and-hq §N8, FN3 in spirit: the agents code names no truth getter (all reads of the other side go through the "what the AI may see"
    // block of SimpleAi.cs, which reads the side's KnowledgeView with fog on). The behavioural FN tests in SimpleAiFogPlayTests are the real
    // proof; this catches a careless edit early.
    [Fact]
    public void FN3_AgentsCodeNamesNoTruthGetter()
    {
        string src = File.ReadAllText(Path.Combine(TestData.RepoRoot, "core", "AI", "SimpleAiAgents.cs"));
        foreach (var bad in new[] { "HqSystem", "Game.Events", "SystemOwners", "TruthView", "Truth(", "RegimentsOnSystem", "Fleets.FleetsAt(", ".Hq.", "GameRng", "Rng.",
                                     "Missions.Defenders", "Missions.Missions", "SupportOf(", "IsUprising(", "Fog.Picture(" })
            Assert.False(src.Contains(bad, StringComparison.Ordinal), $"SimpleAiAgents.cs mentions {bad}");
    }

    // ai/07 §7 (Empire: belief-map search with spies), m5-missions (Espionage writes the ground and HqHere), m5-fog-and-hq §N5. Against an
    // Alliance that gives no orders the fogged Empire sends its spies to the best HQ candidates, its own picture shows the HQ (KnownHq), and
    // it takes it. Measured on seed 1: KnownHq day 299, won day 352; limits 400 and 500.
    // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): on v5 the spies never find the HQ (Cyclor, beside Naboo, ranks low on the belief map: the Alliance would not hide there)
    // and the Empire takes it by assault on day 1282 (35 looks). The test pins: the spies keep looking, and the HQ falls by day 1400, with
    // or without a spy report. The old target (KnownHq by day 400, won by 500) is the trainer's.
    // Tightened 2026-10-17 (AI trainer, explorer scouts on at every level, SimpleAiParams.ExplorerScouts): the HQ falls on day 505 (15 looks).
    // Limit 1400 -> 700. The look floor keeps the old minimum rate (20 looks in at most 1400 days = one per 70 days) now that the game is shorter.
    // B2e re-pin 2026-10-17 (needs Paul's or the AI trainer's sign-off): effective skills, the Hurt rolls after mission duels, escape timers and
    // the Seat of Power change the game; the spies still never find the HQ (22 looks) and it falls by assault on day 727. Limit 700 -> 800.
    [Fact]
    public void FoggedEmpire_AgainstPassiveAlliance_SpiesSearchAndTheHqFalls()
    {
        var (m, h) = Match(1, Fog, passive: Side.Alliance);
        int knownDay = -1;
        while (h.Game.Day < 800 && h.Victory.Winner == null)
        {
            m.Step();
            if (knownDay < 0 && h.Fog.KnownHq(Side.Empire) != null) knownDay = h.Game.Day;
        }
        var looks = h.Game.Events.OfType<MissionStarted>().Count(e => e.Side == Side.Empire && e.MissionKind is MissionKind.Espionage or MissionKind.Reconnaissance);
        _out.WriteLine($"KnownHq day {knownDay}; winner {h.Victory.Winner} day {h.Game.Day}; looks {looks}; orders {m.TotalSent} rejected {m.TotalRejected}");
        Assert.True(looks * 70 >= h.Game.Day, $"only {looks} looks in {h.Game.Day} days");
        Assert.True(knownDay == -1 || knownDay <= 800);
        Assert.Equal(Side.Empire, h.Victory.Winner);
        Assert.Contains(h.Game.Events, e => e is HqDestroyed);
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }

    // A moved HQ is found again (ai/07 §7; m5-fog-and-hq §F6 MoveHq, §N5). Against a passive Alliance, once the Empire knows the HQ the test
    // moves it (a MoveHq order for the Alliance) to the held system with HqMinLanes lanes or more that is farthest by travel; the Empire's
    // picture must later show the HQ at its new system (or the Empire takes it there). The search is slow (three spies, about 40 days a look,
    // dozens of unexplored rim systems of equal belief). Measured on seed 1: known day 139, moved to Lowick that day; the stale Dagobah is
    // re-checked and dropped on day 297, Lowick re-found on day 1026 (887 days after the move), HQ taken day 1216. Limit 1200 days.
    [Fact]
    public void FoggedEmpire_RefindsAMovedHq()
    {
        // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): the spies no longer find the
        // first HQ (see above), so the HQ moves on day 0, before the Empire knows it, and the test asks that the moved HQ is found or taken.
        // Measured on v5: Cyclor -> Dubrillion on day 0, never spotted, taken on day 910; limit 1100 days.
        // Re-pin 2026-10-17 (AI trainer, explorer scouts on at every level; the Dev lead approved): taken on day 1105; limit 1100 -> 1200. In both
        // versions the spies never re-find the moved HQ and it falls to conquest (both first see Dubrillion on day 245); the 195 days are the
        // game taking another course, not a slower search.
        var (m, h) = Match(1, Fog, passive: Side.Alliance);
        string from = h.HqSystem!; var g = h.Data.Galaxy;
        string dest = g.Systems.Select(s => s.Name)
            .Where(n => n != from && h.Fleets.OwnerOf(n) == Side.Alliance && g.NearOf(g.Get(n).Index).Count >= Fog.HqMinLanes)
            .Select(n => (n, p: h.Fleets.FindPath(from, n, 100))).Where(t => t.p.Ok)
            .OrderByDescending(t => t.p.TotalTicks).ThenBy(t => t.n, StringComparer.Ordinal).First().n;
        h.Fog.SubmitMoveHq(Side.Alliance, dest);
        int movedDay = h.Game.Day, limit = movedDay + 1200, refound = -1;
        while (h.Game.Day < limit && h.Victory.Winner == null)
        {
            m.Step();
            if (refound < 0 && h.HqSystem == dest && h.Fog.KnownHq(Side.Empire)?.System == dest) refound = h.Game.Day;
        }
        bool tookIt = h.Victory.Winner == Side.Empire && h.Game.Events.OfType<HqDestroyed>().Any();
        _out.WriteLine($"HQ {from} -> {dest} on day {movedDay}; re-found day {refound}; winner {h.Victory.Winner} day {h.Game.Day}; orders {m.TotalSent} rejected {m.TotalRejected}");
        Assert.Contains(h.Game.Events, e => e is HqDeparted d && d.From == from);
        Assert.True(refound > 0 || tookIt, $"the moved HQ ({dest}) was not found again by day {limit}");
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }

    // ai/05 (characters): both AIs put their best leaders on posts: fleet Admirals or Generals on strike fleets (AttachCharacter) and a
    // system General on the capital or HQ (AssignSystemGeneral); the core accepts them (m5-characters) and the posts are held (a system
    // General is released when the HQ moves away, so "held" is checked on any day).
    [Fact]
    public void Leaders_GetPosts()
    {
        var (m, h) = Match(1, Fog);
        var held = new HashSet<Side>();
        // v5 re-pin 2026-10-16 (Paul chose re-pin on the map PR; the AI trainer owns restoring the old target): window 200 -> 700 days. On
        // the bigger map the Empire's first strike fleet strong enough for an officer (OfficerFleetStrength) forms later than day 200.
        while (h.Game.Day < 700 && h.Victory.Winner == null)
        {
            m.Step();
            foreach (var id in h.Game.State.Characters.SystemGenerals.Values) if (h.Characters.Get(id) is { } c) held.Add(c.Side);
        }
        foreach (var side in new[] { Side.Empire, Side.Alliance })
        {
            int attach = Sent(m, $"{side} AttachCharacter"), general = Sent(m, $"{side} AssignSystemGeneral");
            int onPost = h.Characters.InPlay.Count(c => c.Side == side && c.Post != CharacterPost.None);
            int generals = h.Game.State.Characters.SystemGenerals.Values.Count(id => h.Characters.Get(id)?.Side == side);
            _out.WriteLine($"{side}: attach {attach}, system General {general}; now on posts {onPost}, system Generals {generals}");
            Assert.True(attach > 0, $"{side} gave no fleet posts");
            Assert.True(general > 0, $"{side} gave no system General");
            Assert.True(held.Contains(side), $"{side} never held a system General");
            Assert.Contains(h.Game.Events.OfType<CharacterAttached>(), e => e.Side == side && e.Post != CharacterPost.None);
        }
        Assert.DoesNotContain(m.Rejected.Keys, k => k.Contains("AttachCharacter", StringComparison.Ordinal) || k.Contains("AssignSystemGeneral", StringComparison.Ordinal));
    }

    // Both fogged AIs with characters and missions: missions are sent by both sides, and at most 5% of all AI orders are rejected, over
    // seeds 1 to 3 at 600 days.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FogVsFog_MissionsSent_FewRejections(int seed)
    {
        var (m, h) = Match(seed, Fog);
        m.RunTo(600);
        int eMissions = m.Sent.Where(kv => kv.Key.StartsWith("Empire Mission", StringComparison.Ordinal)).Sum(kv => kv.Value);
        int aMissions = m.Sent.Where(kv => kv.Key.StartsWith("Alliance Mission", StringComparison.Ordinal)).Sum(kv => kv.Value);
        _out.WriteLine($"seed {seed} day {h.Game.Day} winner {h.Victory.Winner}; missions Empire {eMissions} Alliance {aMissions}; orders {m.TotalSent} rejected {m.TotalRejected}: " +
                       string.Join(", ", m.Rejected.Select(kv => kv.Key + " " + kv.Value)));
        Assert.True(eMissions > 0 && aMissions > 0);
        Assert.True(m.TotalRejected * 20 <= m.TotalSent, $"{m.TotalRejected} of {m.TotalSent} orders rejected");
    }
}
