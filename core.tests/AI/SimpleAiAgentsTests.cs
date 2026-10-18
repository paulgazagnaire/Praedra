using Rebellion.Core.AI;
using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
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
            FogAvoidEnemyStops = false, SplitHomeSurplus = false, DetourToHeldStop = false, InvasionBatchesOnly = false, ExplorerScouts = 0, FogGroundForgetDays = 0, LeaderMarks = false, OriginalGarrisonFloor = false });
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
        // B4 re-pin 2026-10-18: setup now names fleets "Fleet n" and capital ships "<class> n" (B4 §12, display only; the ship name is saved
        // state). The game plays identically: with the naming step removed the hash is exactly B2e's 5e54e22f...e007 (checked when re-pinned).
        // B3 re-pin 2026-10-18: the Classic battle now follows the traced original (m4-battle-classic, batch B3) and AI sides use the AI threshold
        // of B6a §2, which changes every battle and so the game; the old AI is still the one played (all switches above unchanged).
        // Reveal re-pin 2026-10-18 (AI trainer, Dev lead's ask): FogParams.SetupRevealsFacilities is now DEFAULT on (1998), so the day-0
        // pictures hold the core systems' facilities. With the reveal off the hash is exactly B3's 12fcdbf1...334b (checked when re-pinned).
        // B5b re-pin 2026-10-18: production and upkeep follow the 1998 rules (batch B5b): build orders over the free upkeep are refused
        // (NoUpkeep), blockades and uprisings stop mines and refineries and take them out of capacity, every restart draws a startup
        // cycle, captured mines reset, shares are dealt incrementally (new saved fields WorkSide, Share, Startup), the autoscrap timer is
        // armed at the overdraft and new orders queue behind. These are core rules the old AI cannot switch off, so the game takes another course.
        // B5a re-pin 2026-10-17: derived control (ControlRule.Derived, the DEFAULT; the old AI cannot switch it off) recomputes every holder after
        // setup, lets worlds go neutral or join a side by support, hands a world over when its last regiment leaves, runs the sector steps, frees
        // and sends away characters at each flip, and adds saved state (ControlState.Contested, EverHeld): the game takes another course from
        // setup. Seed 1, 1000 days, as before (no seed change); re-measured on top of B3. The B6b garrison floor (OriginalGarrisonFloor) is off above
        // with the other AI switches; its off path keeps the B5a last-regiment guard (the hash is unchanged by that switch and by the squadrons
        // leaving a flipped world, checked 2026-10-18). Re-measured on top of B5b (main 682cd31), 2026-10-18.
        // AssaultOnly re-pin 2026-10-18 (Dev lead): ControlRule is DEFAULT AssaultOnly until the AI copes with Derived (QA 16-seed: 0/16 Empire
        // wins), so this game no longer runs derived control (the Derived hash was 78d21c94...d3ec). It still differs from main's edab04cb...c500
        // (the branch adds saved state, ControlState, and changes that run under both rules; not separated). Seed 1, 1000 days, on main 5437780 merged.
        // Blockade pause re-pin 2026-10-18 (Dev lead; Paul's economy/02 blockade rule is the DEFAULT, 1998 is the switch): SuspendedLeaveCapacity is
        // now DEFAULT off and MineStopMode / YardStopMode DEFAULT Pause, core rules this old AI cannot switch off. With the three 1998 switches set
        // (SuspendedLeaveCapacity on, both modes Abandon) the hash is exactly the AssaultOnly pin 1e675764...ba62 (checked when re-pinned, main eb722bd merged).
        // re-pinned 2026-10-18: fog group D stores characters (m5-fog-and-hq). New saved state (SystemKnowledge.Characters) in every picture
        // whose group D saw a character; this old AI (characters and missions off) does not read it. Was d05f61ca...80c3.
        Assert.Equal("9188df4d2fa5ca65ee8a06428c73cb2a92991f0bb869d1cc6369ecbcdf5005ff", h.StateHash());
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

    // QA-8, B6c-15 in play (SimpleAiParams.LeaderMarks, DEFAULT off since 2026-10-18, QA-12; on here): while the Empire holds Coruscant the Emperor is kept there (Emperor home),
    // so the Seat of Power stays on (m5-characters-and-force §Seat of Power); the six story characters are never commanders and the Emperor
    // is never a diplomat (B6c-14). Fogged Medium mirror, seeds 1 to 8, 1500 days: on 1500 of 1500 days on every seed with the
    // switch; 15 to 103 days without it, and the Emperor held by the Alliance on seeds 3, 4 and 6 (56, 203 and 84 days).
    [Fact]
    public void LeaderMarks_EmperorHome_TheSeatOfPowerStaysOn()
    {
        Assert.False(new SimpleAiParams().LeaderMarks);
        var games = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var (m, h) = Match(seed, Fog with { LeaderMarks = true });
            int on = 0, days = 0;
            while (h.Game.Day < 700 && h.Victory.Winner == null) { m.Step(); days++; if (h.Characters.SeatOfPowerOn) on++; }
            // B6c-14 in play: the six story characters are never made commanders, the Emperor is never a diplomat
            var cp = h.Characters.Params;
            var story = new HashSet<int> { cp.Luke, cp.Leia, cp.Vader, cp.Emperor, new SimpleAiParams().MonMothmaId, h.Missions.Data.Numbers.HanCharacterId };
            var ev = h.Game.Events;
            int commands = ev.OfType<CharacterAttached>().Count(e => e.Day > 0 && e.Post != CharacterPost.None && story.Contains(e.Character))
                         + ev.OfType<SystemGeneralAssigned>().Count(e => story.Contains(e.Character));
            int emperorDiplomacy = ev.OfType<MissionStarted>().Count(e => e.MissionKind == MissionKind.Diplomacy && e.Characters.Items.Contains(cp.Emperor));
            return (Seed: seed, On: on, Days: days, Commands: commands, EmperorDiplomacy: emperorDiplomacy);
        });
        foreach (var g in games)
        {
            _out.WriteLine($"seed {g.Seed}: Seat of Power on {g.On} of {g.Days} days; story commanders {g.Commands}; Emperor diplomacy {g.EmperorDiplomacy}");
            Assert.True(g.On * 20 >= g.Days * 19, $"seed {g.Seed}: Seat of Power on only {g.On} of {g.Days} days");
            Assert.Equal(0, g.Commands);
            Assert.Equal(0, g.EmperorDiplomacy);
        }
    }

    // QA-7 follow-up (2026-10-18): since #69 a duel prisoner is held aboard the captor's fleet. The AI's Rescue planner looked only at held_at
    // (a planet), so such a prisoner was never rescued; it now targets him where that fleet stands (m5-missions TargetReason: LocationOf, the
    // prisoner can be rescued only while his fleet stands at a system). An Empire character (Vader on seed 1) is captured aboard an Alliance fleet on day 31.
    [Fact]
    public void Rescue_TargetsAPrisonerHeldAboardTheCaptorFleet()
    {
        var (m, h) = Match(1, new SimpleAiParams(), passive: Side.Alliance); // a passive Alliance: its fleets stay put
        m.RunTo(30);
        var cp = h.Characters.Params;
        // the smallest Alliance fleet standing away from the HQ (Crait on seed 1; at the HQ the odds stay below the AI's floor)
        var fleet = h.Game.State.Fleets.Values.Where(f => f.Side == Side.Alliance && f.At != null && f.At != h.HqSystem && f.Ships.Count > 0)
            .OrderBy(f => f.Ships.Count).ThenBy(f => f.Id).First();
        var prisoner = h.Characters.InPlay.Where(c => c.Side == Side.Empire && c.Status == CharacterStatus.Free && c.Transit == null && c.Id != cp.Emperor
                                                      && !h.Missions.IsOnMission(c.Id))
            .OrderBy(c => c.Id).First();
        bool done = false;
        h.Game.RegisterHook("5.2", ctx =>
        {
            if (done) return; done = true;
            h.Characters.Capture(ctx, prisoner, Side.Alliance, null, "test", canEscape: false, unit: new CaptorUnit(fleet.Id, fleet.At, MissionInvolved: true));
        });
        m.Step();
        Assert.Equal((CharacterStatus.Captured, fleet.Id, (string?)null), (prisoner.Status, prisoner.HeldAboard, prisoner.HeldAt));
        int rescues = 0;
        for (int d = 0; d < 150 && rescues == 0; d++)
        {
            m.Step();
            rescues += m.Empire.Submitted.Count(o => o.Kind == "MissionRescue" && o.Detail.Contains($"to {fleet.At} (character {prisoner.Id})", StringComparison.Ordinal));
        }
        Assert.True(rescues > 0, $"no Rescue of {prisoner.Id} held aboard fleet {fleet.Id} at {fleet.At}");
    }

    // ai/07 §7 (Empire: belief-map search with spies), m5-missions (Espionage writes the ground and HqHere), m5-fog-and-hq §N5. Against an
    // Alliance that gives no orders the fogged Empire sends its spies to the best HQ candidates and takes the HQ. Measured on the 360-system
    // map (seed 1): KnownHq day 299, won day 352. On v5 the spies never find the HQ (Cyclor, beside Naboo, ranks low on the belief map: the
    // Alliance would not hide there), on any of seeds 1 to 8, and the HQ falls to conquest; the old target (KnownHq by day 400) stays the
    // trainer's.
    // Over seeds 1 to 8 (MultiSeed), the day the HQ falls:
    //   8a378a8 (pre-B2e): 508, 634, 811, 514, 514, 616, 796, 787   (looks 15 to 36)
    //   9ec544f (B2e):     757, 475, 637, 745, 718, 814, 538, 643   (looks 17 to 31)
    // Floor: the HQ falls by day 1000 on at least 7 of 8. On every seed the spies keep looking (at least one look per 70 days, the old rate
    // of 20 looks in 1400 days) and at most 1 order in 20 is rejected.
    // Replaces (AI trainer, 2026-10-18, the Dev lead's ask) the seed-1 limit (1400, 700, then 800 on B2e), which moved with every new draw.
    [Fact]
    public void FoggedEmpire_AgainstPassiveAlliance_SpiesSearchAndTheHqFalls()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var (m, h) = Match(seed, Fog, passive: Side.Alliance);
            while (h.Game.Day < 1000 && h.Victory.Winner == null) m.Step();
            var looks = h.Game.Events.OfType<MissionStarted>().Count(e => e.Side == Side.Empire && e.MissionKind is MissionKind.Espionage or MissionKind.Reconnaissance);
            bool fell = h.Victory.Winner == Side.Empire && h.Game.Events.Any(e => e is HqDestroyed);
            return (Seed: seed, Fell: fell, h.Game.Day, Looks: looks, m.TotalSent, m.TotalRejected);
        });
        foreach (var g in games) _out.WriteLine($"seed {g.Seed}: HQ fell {g.Fell} day {g.Day}; looks {g.Looks}; orders {g.TotalSent} rejected {g.TotalRejected}");
        Assert.True(games.Count(g => g.Fell) >= 7, $"the HQ fell by day 1000 on only {games.Count(g => g.Fell)} of 8 seeds");
        foreach (var g in games)
        {
            Assert.True(g.Looks * 70 >= g.Day, $"seed {g.Seed}: only {g.Looks} looks in {g.Day} days");
            Assert.True(g.TotalRejected * 20 <= g.TotalSent, $"seed {g.Seed}: {g.TotalRejected} of {g.TotalSent} orders rejected");
        }
    }

    // A moved HQ is found again (ai/07 §7; m5-fog-and-hq §F6 MoveHq, §N5). Against a passive Alliance the test moves the HQ on day 0 (a
    // MoveHq order for the Alliance) to the held system with HqMinLanes lanes or more that is farthest by travel; the Empire's picture must
    // later show the HQ at its new system, or the Empire takes it there. On the 360-system map the Empire knew the HQ first (seed 1: known
    // day 139, Lowick re-found 887 days after the move); on v5 the spies never find the first HQ (above), so the HQ moves before the
    // Empire knows it.
    // Over seeds 1 to 8 (MultiSeed; Cyclor -> Dubrillion on every seed), the day the moved HQ is re-found or taken:
    //   8a378a8 (pre-B2e): taken 838, 781, 1480, 1006, 985, 982, 640, 724   (never re-found by a look)
    //   9ec544f (B2e):     taken 637, 886, 724, 682, re-found 1136, re-found 1237, taken 1063, 730
    // Floor: re-found or taken within 1300 days on at least 6 of 8. The spies seldom re-find it: what this pins is that the Empire gets
    // the moved HQ in the end, mostly by conquest.
    // Replaces (AI trainer, 2026-10-18, the Dev lead's ask) the seed-1 limit (1100, then 1200).
    [Fact]
    public void FoggedEmpire_RefindsAMovedHq()
    {
        var games = MultiSeed.Play(MultiSeed.OneToEight, seed =>
        {
            var (m, h) = Match(seed, Fog, passive: Side.Alliance);
            string from = h.HqSystem!; var g = h.Data.Galaxy;
            string dest = g.Systems.Select(s => s.Name)
                .Where(n => n != from && h.Fleets.OwnerOf(n) == Side.Alliance && g.NearOf(g.Get(n).Index).Count >= Fog.HqMinLanes)
                .Select(n => (n, p: h.Fleets.FindPath(from, n, 100))).Where(t => t.p.Ok)
                .OrderByDescending(t => t.p.TotalTicks).ThenBy(t => t.n, StringComparer.Ordinal).First().n;
            h.Fog.SubmitMoveHq(Side.Alliance, dest);
            int limit = h.Game.Day + 1300, refound = -1;
            while (h.Game.Day < limit && h.Victory.Winner == null)
            {
                m.Step();
                if (refound < 0 && h.HqSystem == dest && h.Fog.KnownHq(Side.Empire)?.System == dest) refound = h.Game.Day;
            }
            bool tookIt = h.Victory.Winner == Side.Empire && h.Game.Events.OfType<HqDestroyed>().Any();
            bool departed = h.Game.Events.Any(e => e is HqDeparted d && d.From == from);
            return (Seed: seed, From: from, Dest: dest, Departed: departed, Refound: refound, TookIt: tookIt, h.Game.Day, m.TotalSent, m.TotalRejected);
        });
        foreach (var g in games) _out.WriteLine($"seed {g.Seed}: HQ {g.From} -> {g.Dest}; re-found day {g.Refound}; taken {g.TookIt} day {g.Day}; orders {g.TotalSent} rejected {g.TotalRejected}");
        int got = games.Count(g => g.Refound > 0 || g.TookIt);
        Assert.True(got >= 6, $"the moved HQ was re-found or taken within 1300 days on only {got} of 8 seeds");
        foreach (var g in games)
        {
            Assert.True(g.Departed, $"seed {g.Seed}: the HQ did not leave {g.From}");
            Assert.True(g.TotalRejected * 20 <= g.TotalSent, $"seed {g.Seed}: {g.TotalRejected} of {g.TotalSent} orders rejected");
        }
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

    // QA-6: B6c §7 (the 1998 AI's Sabotage, Recon, espionage-team and Abduction/Assassination filters admit special forces, inferred from the
    // mask), B6b §9 (it keeps at least 8 special forces), m5-missions §Special forces (a special force is a team member only of the kinds whose
    // record mask holds its class bit). With SimpleAiParams.SpecialForceAgents (DEFAULT off) on, each side keeps SpecialForcesWanted special forces
    // of its agent classes and sends them alone on planned missions; off, no special force goes on a mission (the HQ-hunt droids are Hard's).
    // Measured on 146027a + this change, seeds 1 to 4, fog, 1000 days (special-force missions Empire / Alliance): 12/2, 0/1, 18/3, 16/3;
    // kinds Subdue (most), Espionage, Abduction. Floor: both sides send one on at least 3 of 4 seeds; only agent classes go; at most 1
    // order in 20 rejected. Off: none on seed 1. AI Recruitment (QA-12, MaxRecruiters 2) is off here: with it on, the recruited minors take
    // most jobs and the special forces go 0/0, 6/0, 1/0, 4/1 by day 1000 (1c3c353), so this test measures the special forces without it.
    [Fact]
    public void SpecialForceAgents_AreBuiltAndGoOnMissionsTheirMaskAllows()
    {
        Assert.False(new SimpleAiParams().SpecialForceAgents);
        var p = Fog with { SpecialForceAgents = true, MaxRecruiters = 0 }; // recruits take the jobs first (QA-12 numbers below)
        var games = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var (m, h) = Match(seed, p);
            m.RunTo(1000);
            var sf = h.Game.Events.OfType<MissionStarted>().Where(e => e.SpecialForces.Items.Count > 0).ToList();
            var built = h.Game.Events.OfType<UnitCompleted>().ToDictionary(e => e.Object, e => e.Class); // a special force on a mission was built in play
            var classes = sf.SelectMany(e => e.SpecialForces.Items).Select(id => built.GetValueOrDefault(id, "start force")).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
            bool agentClassesOnly = sf.All(e => e.SpecialForces.Items.All(id => built.TryGetValue(id, out var c) && p.AgentForceClasses(e.Side).Contains(c)));
            return (Seed: seed, Empire: sf.Count(e => e.Side == Side.Empire), Alliance: sf.Count(e => e.Side == Side.Alliance),
                Kinds: string.Join(",", sf.Select(e => e.MissionKind).Distinct().OrderBy(k => k)), Classes: string.Join(",", classes),
                AgentOnly: agentClassesOnly, m.TotalSent, m.TotalRejected);
        });
        foreach (var g in games)
            _out.WriteLine($"seed {g.Seed}: special-force missions Empire {g.Empire} Alliance {g.Alliance}; kinds {g.Kinds}; classes {g.Classes}; orders {g.TotalSent} rejected {g.TotalRejected}");
        Assert.All(games, g => Assert.True(g.AgentOnly, $"seed {g.Seed}: a special force outside the agent classes went on a mission"));
        Assert.All(games, g => Assert.True(g.TotalRejected * 20 <= g.TotalSent, $"seed {g.Seed}: {g.TotalRejected} of {g.TotalSent} orders rejected"));
        Assert.True(games.Count(g => g.Empire > 0 && g.Alliance > 0) >= 3, "both sides send special forces on at least 3 of 4 seeds");

        var (mo, ho) = Match(1, p with { SpecialForceAgents = false });
        mo.RunTo(1000);
        Assert.DoesNotContain(ho.Game.Events.OfType<MissionStarted>(), e => e.SpecialForces.Items.Count > 0);
    }

    // QA-12, B6c §7 list item 1 (Recruitment: up to 2 running while the side has anyone left to recruit, leadership at least 80) and the Emperor-
    // home planner's start test (ctrl+0xa4 == 0: nobody left to recruit). m5-missions §6 Recruitment places the recruit at the target system.
    // Seed 1, fog, leader marks on, by day 700: the Empire's pool (19) empties on day 280, the Alliance's (24) on day 357; at most 2 running per
    // side; the Seat of Power is on 360 of the 360 days from 60 days after the Empire's pool empties.
    // Checked: never more than MaxRecruiters Recruitments running per side; each recruiter has leadership at least RecruitMinLeadership when
    // sent; both pools empty by day 700; no Recruitment is sent once a side's pool is empty; after the Empire's pool empties the Seat of Power is on.
    [Fact]
    public void Recruitment_TwoAtATime_UntilThePoolIsEmpty_ThenTheEmperorGoesHome()
    {
        var p = new SimpleAiParams();
        Assert.Equal((2, 80, true), (p.MaxRecruiters, p.RecruitMinLeadership, p.EmperorHomeAfterRecruiting));
        var (m, h) = Match(1, Fog with { LeaderMarks = true }); // the Emperor-home planner is a leader-marks rule (DEFAULT off)
        var running = new Dictionary<int, Side>(); // mission id -> side (lookup only)
        int maxE = 0, maxA = 0, weak = 0, afterEmpty = 0, seatDays = 0, seatOn = 0;
        int? emptyE = null, emptyA = null;
        int seen = 0;
        while (h.Game.Day < 700 && h.Victory.Winner == null)
        {
            m.Step();
            int today = h.Game.Day;
            var fresh = h.Game.Events.Skip(seen).ToList(); seen += fresh.Count;
            foreach (var e in fresh.OfType<MissionStarted>().Where(e => e.MissionKind == MissionKind.Recruitment))
            {
                running[e.Mission] = e.Side;
                if (e.Characters.Items.Any(id => h.Characters.Get(id)!.EffectiveSkill(Skill.Leadership) < p.RecruitMinLeadership)) weak++;
                if ((e.Side == Side.Empire ? emptyE : emptyA) != null) afterEmpty++;
            }
            foreach (var e in fresh.OfType<MissionEnded>()) running.Remove(e.Mission);
            maxE = Math.Max(maxE, running.Values.Count(s => s == Side.Empire));
            maxA = Math.Max(maxA, running.Values.Count(s => s == Side.Alliance));
            if (emptyE == null && h.Characters.RecruitPoolOf(Side.Empire).Count == 0) emptyE = today;
            if (emptyA == null && h.Characters.RecruitPoolOf(Side.Alliance).Count == 0) emptyA = today;
            if (emptyE is int d0 && today > d0 + 60) { seatDays++; if (h.Characters.SeatOfPowerOn) seatOn++; }
        }
        _out.WriteLine($"pools empty: Empire day {emptyE}, Alliance day {emptyA}; most running {maxE}/{maxA}; weak recruiters {weak}; sent after empty {afterEmpty}; Seat of Power on {seatOn} of {seatDays} days");
        Assert.True(maxE <= p.MaxRecruiters && maxA <= p.MaxRecruiters, $"{maxE}/{maxA} Recruitments running at once");
        Assert.Equal(0, weak);
        Assert.Equal(0, afterEmpty);
        Assert.True(emptyE != null && emptyA != null, $"pools not empty by day 700: Empire {emptyE}, Alliance {emptyA}");
        Assert.True(seatDays > 0 && seatOn * 20 >= seatDays * 19, $"Seat of Power on {seatOn} of {seatDays} days after the Empire's pool emptied");
    }
}

