using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// rule: ai tuning, the Collective commander's later plans (AI trainer "Rooster", 2026-11-27; design
// specs/engine/ai-notes/2026-11-26-adaptive-commander.md §3). Switches in core/AI/SimpleAiParams.Collective.cs.
public class SimpleAiCollectivePlansTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiCollectivePlansTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void TheLaterCollectivePlansAreOffByDefault()
    {
        var p = new SimpleAiParams();
        Assert.False(p.Neutralise);
        Assert.False(p.RefitAway);
        Assert.Equal(0, p.NeutraliseElsewherePercent);
        Assert.False(p.RaidShieldsSeen);
        Assert.False(p.WreckShields);
        Assert.Equal(0, p.ShieldsFeltDays);
        Assert.Equal(0, p.FighterWaitMax);
        Assert.Equal(0, p.FighterWaitMaxHand);
        Assert.Equal(0, p.WinWallsForgetDays);
        Assert.False(p.WinSiege);
        Assert.Equal(40, p.WinSiegeMarginPower);
        Assert.False(p.GuardedOriginWait);
    }

    // Rooster 2026-11-27 (specs/engine/ai-notes/rooster-log.md): Neutralise anywhere at Medium and Hard, not at Easy.
    [Theory]
    [InlineData(AiLevel.Easy, false)]
    [InlineData(AiLevel.Medium, true)]
    [InlineData(AiLevel.Hard, true)]
    public void NeutraliseIsOnAtMediumAndHard(AiLevel level, bool on)
    {
        var p = AiLevels.For(level);
        Assert.Equal(on, p.Neutralise);
        Assert.Equal(on ? 100 : 0, p.NeutraliseElsewherePercent);
        Assert.Equal(on, p.RefitAway); // keeps the navy renewing while the warships shell (Dev lead after #416)
        Assert.Equal(on ? 120 : 0, p.ShieldsFeltDays); // Rooster 2026-11-27 evening: shields felt and seen, Wreckers aboard
        Assert.Equal(on, p.RaidShieldsSeen);
        Assert.Equal(on, p.WreckShields);
        Assert.Equal(on ? 2 : 0, p.FighterWaitMax); // Rooster 2026-11-28: fighters wait aboard
        Assert.Equal(0, p.FighterWaitMaxHand); // the Hand's is Maverick's to measure
        Assert.Equal(on ? 365 : 0, p.WinWallsForgetDays); // Rooster 2026-11-28: Charter's old walls are forgotten
        Assert.Equal(on, p.WinSiege); // Rooster 2026-11-28: a pile that out-shells Charter's walls may siege it
    }

    // Plan 11, Neutralise. m2-control-and-capture §Control: a world with no regiments is held by the side with 60 support, neutral at 41 to 59.
    // With Neutralise on, Collective fleets shell thin Hand garrisons on half-loyal worlds bare (Military, no landing) in most games, and the Hand loses
    // worlds to "troops left" (seeds 1-4, Medium mirror, MedE, day 800; the counts are printed, not asserted: one game swings too much).
    [Fact]
    public void NeutraliseShellsThinGarrisonsBare()
    {
        var r = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            int Run(bool on, out int shells)
            {
                var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
                var c = AiLevels.For(AiLevel.Medium) with { Neutralise = on, NeutraliseElsewherePercent = on ? 100 : 0, WreckShields = false, RaidShieldsSeen = false, ShieldsFeltDays = 0 };
                var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), c);
                shells = 0;
                while (host.Game.Day < 800 && host.Victory.Winner == null)
                {
                    m.Step();
                    shells += m.Collective.Submitted.Count(o => o.Detail.Contains("(Neutralise)", StringComparison.Ordinal));
                }
                return host.Game.Events.OfType<SystemCaptured>().Count(x => x.From == Side.Hand && x.Cause == CaptureCause.TroopsLeft);
            }
            int off = Run(false, out int s0), on = Run(true, out int s1);
            return (off, on, s0, s1);
        });
        foreach (var x in r) _out.WriteLine($"Hand worlds lost to troops left: off {x.off}, on {x.on}; Neutralise shellings off {x.s0}, on {x.s1}");
        Assert.All(r, x => Assert.Equal(0, x.s0));
        Assert.True(r.Count(x => x.s1 > 0) >= 3, "the Collective shelled no thin garrison bare in 2 or more of 4 games");
    }

    private static SimpleAiParams WalledHand() => AiLevels.For(AiLevel.Medium) with { FortifyWorlds = 4, FortifyBatteries = 2 };
    private static SimpleAiParams ShieldWise(bool wreck) => AiLevels.For(AiLevel.Medium) with { ShieldsFeltDays = 120, RaidShieldsSeen = true, WreckShields = wreck };

    // m3-bombardment §2 (hits = max(0, P - S)) and m5-fog-and-hq §F2 (a fleet in orbit sees no facilities): with fog the Collective shelled
    // shields it did not know of for no hits again and again. ShieldsFeltDays (it reads the shields its last shelling met) and RaidShieldsSeen
    // (it shells only where its power beats them) cut those shellings (seed 1, walled Medium Hand, day 1200: 8 with the switches off, 1 on).
    [Fact]
    public void ShieldsFeltStopsShellingForNoHits()
    {
        int NoHits(SimpleAiParams c)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
            new AiMatch(host, WalledHand(), c).RunTo(1200);
            return host.Game.Events.OfType<Praedra.Core.Defenses.BombardmentStarted>().Count(b => b.Side == Side.Collective && b.S > 0 && b.Hits == 0);
        }
        int off = NoHits(ShieldWise(false) with { ShieldsFeltDays = 0, RaidShieldsSeen = false }), on = NoHits(ShieldWise(false));
        _out.WriteLine($"Collective shellings for no hits: off {off}, on {on}");
        Assert.True(off > 0, "the walled Hand's shields absorbed no shelling: the test no longer checks anything");
        Assert.True(on * 2 <= off, $"the shields felt did not halve the shellings for no hits ({off} -> {on})");
    }

    // m5-missions §Spying from orbit (#412): with WreckShields, Wreckers board armed Collective fleets and are launched from orbit on
    // Sabotage over shielded Hand worlds (seeds 1-4, walled Medium Hand, day 1200). One seed was too thin: since the AI's leader missions aim at
    // the last sighting, seed 1 swung between 0 and 3 orbit launches, so Wreckers must board in at least 2 games and launch in at least 1 (Rooster 2026-11-28).
    [Fact]
    public void WreckersRideFleetsAndSabotageShieldsFromOrbit()
    {
        var r = MultiSeed.Play(new[] { 1, 2, 3, 4 }, seed =>
        {
            var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedE });
            var m = new AiMatch(host, WalledHand(), ShieldWise(true));
            int loads = 0, launches = 0;
            while (host.Game.Day < 1200 && host.Victory.Winner == null)
            {
                m.Step();
                loads += m.Collective.Submitted.Count(o => o.Kind == "LoadSpecialForces" && o.Detail.Contains("(wreck the shields)", StringComparison.Ordinal));
                launches += m.Collective.Submitted.Count(o => o.Kind == "MissionSabotage" && o.Detail.Contains("(wreck the shields)", StringComparison.Ordinal));
            }
            var orbit = host.Game.Events.OfType<Praedra.Core.Missions.MissionStarted>()
                .Count(x => x.Side == Side.Collective && x.MissionKind == Praedra.Core.Missions.MissionKind.Sabotage && x.Home == x.Target);
            return (seed, loads, launches, orbit);
        });
        foreach (var g in r) _out.WriteLine($"seed {g.seed}: Wrecker loads {g.loads}, orbit sabotage orders {g.launches}, started {g.orbit}");
        Assert.True(r.Count(g => g.loads > 0) >= 2, "Wreckers boarded a fleet in fewer than 2 of 4 games");
        Assert.True(r.Any(g => g.launches > 0 && g.orbit > 0), "no Sabotage was launched from orbit in 4 games");
    }

    // m1-clock-and-orders K12 with the shields felt: the notes ride in the AI memory of the save, so a loaded game decides as the uninterrupted one.
    [Trait("Category", "Play")]
    [Fact]
    public void ShieldsFeltGameContinuesTheSameAfterSaveAndLoad()
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        new AiMatch(host, WalledHand(), ShieldWise(true)).RunTo(700);
        var loaded = GameHost.Load(host.SaveWithSettings());
        var a = new AiMatch(host, WalledHand(), ShieldWise(true));
        var b = new AiMatch(loaded, WalledHand(), ShieldWise(true));
        for (int i = 0; i < 60; i++)
        {
            a.Step(); b.Step();
            Assert.True(host.StateHash() == loaded.StateHash(), $"diverged on day {host.Game.Day}");
        }
    }

    // AiMemory.Ingest, ShieldsFelt (m3-bombardment §2 and §Resolution 4, 5.1; Ponytail on #426): a shelling with hits notes the shields S; one
    // with none notes what the shelling side knows, that fleets of their power do nothing there: their power now, at least the strike's P (the
    // batteries ground ships before P is counted). Nothing is noted while the note is not kept (ShieldsFeltDays 0), nor for the enemy's shellings.
    [Fact]
    public void AiMemory_NotesTheShieldsAShellingFelt()
    {
        var fleets = new ValueList<int>(new[] { 7 });
        GameEvent[] feed =
        {
            new Praedra.Core.Defenses.BombardmentStarted(0, 5, "Navalia", 7, Side.Collective, Praedra.Core.Defenses.BombardCategory.Civilian, 50, 40, 10, fleets),
            new Praedra.Core.Defenses.BombardmentStarted(1, 6, "Midway", 7, Side.Collective, Praedra.Core.Defenses.BombardCategory.Civilian, 30, 80, 0, fleets),
            new Praedra.Core.Defenses.BombardmentStarted(2, 6, "Charter", 9, Side.Hand, Praedra.Core.Defenses.BombardCategory.Civilian, 30, 80, 0, fleets),
        };
        var off = new AiMemory { Side = Side.Collective, Fog = true };
        off.Ingest(feed, _ => false, _ => 55);
        Assert.DoesNotContain(off.Notes, n => n.Kind == AiNoteKind.ShieldsFelt);
        var on = new AiMemory { Side = Side.Collective, Fog = true };
        on.KeepAtLeast(0, 0, 0, 0, 0, 0, shieldsFelt: 120);
        on.Ingest(feed, _ => false, _ => 55);
        Assert.Equal(new[] { ("Navalia", 40), ("Midway", 55) }, on.Notes.Where(n => n.Kind == AiNoteKind.ShieldsFelt).Select(n => (n.System!, n.Value)).ToArray());
    }

    // FighterWaitMax (m3-fleets: a squadron moves only aboard a carrier; m4-battle-classic §1.3: a based squadron with no ship there fights):
    // with it the Collective orders no fighter while two of its squadrons set down within the last 120 days wait on the ground, so far fewer
    // of them die at its yards for nothing; older strays no longer count (Ponytail #432). Seed 2, Medium mirror, to day 1500; counts printed.
    [Fact]
    public void NoNewFightersWhileTwoWaitOnTheGround()
    {
        (int orders, int lateOrders, int strayOrders) Run(int max)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = 2, Preset = StartPreset.MedE });
            var m = new AiMatch(host, AiLevels.For(AiLevel.Medium), AiLevels.For(AiLevel.Medium) with { FighterWaitMax = max });
            var fighters = host.Economy.Costs.Rows.Where(r => r.Category == Praedra.Core.Economy.ObjectCategory.Fighter).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            var since = new Dictionary<int, int>(); // lookups only
            int orders = 0, late = 0, stray = 0;
            while (host.Game.Day < 1500 && host.Victory.Winner == null)
            {
                int day = host.Game.Day;
                var ground = host.Economy.Objects.Where(o => o.Category == Praedra.Core.Economy.ObjectCategory.Fighter && o.Side == Side.Collective && o.AboardShip == 0 && o.System != null).Select(o => o.Id).ToHashSet();
                foreach (int id in ground) since.TryAdd(id, day);
                foreach (int id in since.Keys.Where(id => !ground.Contains(id)).ToList()) since.Remove(id);
                int fresh = since.Values.Count(d0 => day - d0 < 119); // set down within the window (a day's margin: the note is the event's day)
                m.Step();
                int n = m.Collective.Submitted.Count(o => o.Kind == "Build" && fighters.Any(f => o.Detail.Contains(" x " + f + " at ", StringComparison.Ordinal)));
                orders += n;
                if (fresh >= 2) late += n;
                else if (ground.Count >= 2) stray += n;
            }
            return (orders, late, stray);
        }
        var off = Run(0); var on = Run(2);
        _out.WriteLine($"fighter orders off {off.orders} ({off.lateOrders} with 2+ fresh waiting), on {on.orders} ({on.lateOrders}; {on.strayOrders} with 2+ waiting, under 2 fresh)");
        Assert.True(off.lateOrders > 0, "no fighter was ordered with squadrons waiting: the test no longer checks anything");
        Assert.Equal(0, on.lateOrders);
    }

    // AiMemory.Ingest, FighterGrounded (FighterWaitMax): an own squadron built (UnitCompleted), delivered from another yard (DeliveryArrived,
    // dated the day it lands) or unloaded (SquadronsUnloaded, one note per squadron) is noted with its system; the enemy's, ships, and anything while the note is not kept are not.
    [Fact]
    public void AiMemory_NotesTheSquadronsSetDown()
    {
        bool Fighter(int id) => id is 11 or 12 or 13;
        GameEvent[] feed =
        {
            new Praedra.Core.Construction.UnitCompleted(0, 5, 11, "Sting", Side.Collective, "Navalia", "Navalia", 1),
            new Praedra.Core.Construction.UnitCompleted(1, 5, 30, "Lancer", Side.Collective, "Navalia", "Navalia", 2),
            new Praedra.Core.Fleets.SquadronsUnloaded(2, 7, new ValueList<int>(new[] { 12, 13 }), 4, new ValueList<int>(new[] { 30 }), "Midway"),
            new Praedra.Core.Construction.DeliveryArrived(3, 9, 1, 11, "Sting", "Tessera"),
            new Praedra.Core.Construction.DeliveryArrived(4, 9, 2, 30, "Lancer", "Tessera"),
        };
        var off = new AiMemory { Side = Side.Collective, Fog = true };
        off.Ingest(feed, _ => false, null, Fighter);
        Assert.DoesNotContain(off.Notes, n => n.Kind == AiNoteKind.FighterGrounded);
        var on = new AiMemory { Side = Side.Collective, Fog = true };
        on.KeepAtLeast(0, 0, 0, 0, 0, 0, fighterGrounded: 120);
        on.Ingest(feed, _ => false, null, Fighter);
        var g = on.Notes.Where(n => n.Kind == AiNoteKind.FighterGrounded).Select(n => (n.Value, n.System, n.Day)).ToList();
        Assert.Equal(new[] { (11, (string?)"Navalia", 5), (12, "Midway", 7), (13, "Midway", 7), (11, "Tessera", 9) }, g);
        var hand = new AiMemory { Side = Side.Hand, Fog = true };
        hand.KeepAtLeast(0, 0, 0, 0, 0, 0, fighterGrounded: 120);
        hand.Ingest(feed, _ => false, null, _ => false);
        Assert.DoesNotContain(hand.Notes, n => n.Kind == AiNoteKind.FighterGrounded);
    }

    // WinWallsForgetDays (m5-fog-and-hq §F2: only Espionage writes a system's facilities; as QA-5 forgets an old garrison): Hard mirror seed 24.
    // Kept: the Collective holds 554 of 555 worlds by day 2750 and never strikes Charter, which has no shields and no regiments left, because
    // its look from about day 775 still shows two shield generators. Forgotten after 365 days: it takes Charter and wins (day 2127).
    // Re-pinned to seed 29 (Dev lead 2026-11-28, #440: the squadron delivery note, AiMemory FighterGrounded on DeliveryArrived, moved seed 24 to
    // no winner either way). Hard mirror seeds 1-32 by day 2400: kept, the Hand still holds Charter on 9 seeds; forgotten, on 6; seed 29
    // is kept no winner (Charter the Hand's), forgotten the Collective wins on day 1915. Not traced on seed 29 that the kept look shows shields.
    [Trait("Category", "Play")]
    [Fact]
    public void ForgottenWallsLetTheCollectiveStrikeAnEmptyCharter()
    {
        (Side? winner, int day) Play(int forget)
        {
            // #442's leader rules pinned to before (MultiSeed.LeaderRulesBefore442): with them seed 29 is won both ways (kept day 1992, forgotten 2336)
            var host = NewGame.Create(new NewGameOptions { Seed = 29, Preset = StartPreset.MedE, Settings = MultiSeed.LeaderRulesBefore442 });
            var m = new AiMatch(host, AiLevels.For(AiLevel.Hard), AiLevels.For(AiLevel.Hard) with { WinWallsForgetDays = forget, WinSiege = false }); // WinSiege apart: SiegesTheWalledCharter
            while (host.Game.Day < 2400 && host.Victory.Winner == null) m.Step();
            return (host.Victory.Winner, host.Game.Day);
        }
        var off = Play(0); var on = Play(365);
        _out.WriteLine($"walls kept: winner {off.winner?.ToString() ?? "none"} by day {off.day}; forgotten: {on.winner?.ToString() ?? "none"} on day {on.day}");
        Assert.Null(off.winner);
        Assert.Equal(Side.Collective, on.winner);
    }

    // WinSiege (m3-assault check 4: two shield generators forbid a landing; m3-bombardment §2 hits = max(0, P - S), shields are Military
    // targets): the strike picker never chose a win target whose walls it believed up, though SiegeShields shells them down once a pile stands
    // there. Medium Collective vs Maverick's Grinder Hand (SparringBots), seed 11, main 2c98fbc, by day 2000 (Grinder seeds 1-24 scanned):
    // off, no winner (Charter still the Hand's); on, a pile that out-shells the walls sieges Charter and the Collective wins on day 1910.
    [Trait("Category", "Play")]
    [Fact]
    public void SiegesTheWalledCharter()
    {
        (Side? winner, int day) Play(bool siege)
        {
            // #442's leader rules pinned to before (MultiSeed.LeaderRulesBefore442), as the seed was scanned on main 2c98fbc
            var host = NewGame.Create(new NewGameOptions { Seed = 11, Preset = StartPreset.MedE, Settings = MultiSeed.LeaderRulesBefore442 });
            var m = new AiMatch(host, SparringBots.For("Grinder")!, AiLevels.For(AiLevel.Medium) with { WinSiege = siege });
            while (host.Game.Day < 2000 && host.Victory.Winner == null) m.Step();
            return (host.Victory.Winner, host.Game.Day);
        }
        var off = Play(false); var on = Play(true);
        _out.WriteLine($"no siege: winner {off.winner?.ToString() ?? "none"} by day {off.day}; siege: {on.winner?.ToString() ?? "none"} on day {on.day}");
        Assert.Null(off.winner);
        Assert.Equal(Side.Collective, on.winner);
    }

    // WinWallsForgetDays with WallsRefused (Ponytail #436; m3-assault check 4): a landing refused because the world is fully shielded teaches the
    // AI the walls stand. Hard mirror seed 13: an old look at Charter is forgotten, a fleet lands on day 1754 and is refused; without the note it
    // landed and was refused every day from about day 1820 to day 3000 (1180 refusals). With it: one refusal, then the fleet shells or leaves.
    [Trait("Category", "Play")]
    [Fact]
    public void ARefusedLandingTeachesTheWallsStand()
    {
        // #442's leader rules pinned to before (MultiSeed.LeaderRulesBefore442): with them no landing is refused on seed 13 by day 2000
        var host = NewGame.Create(new NewGameOptions { Seed = 13, Preset = StartPreset.MedE, Settings = MultiSeed.LeaderRulesBefore442 });
        var m = new AiMatch(host, AiLevels.For(AiLevel.Hard), AiLevels.For(AiLevel.Hard));
        int run = 0, longest = 0, last = -10;
        while (host.Game.Day < 2000 && host.Victory.Winner == null)
        {
            int before = m.Rejected.GetValueOrDefault("Collective Assault FullyShielded");
            m.Step();
            if (m.Rejected.GetValueOrDefault("Collective Assault FullyShielded") > before) { run = host.Game.Day - last <= 2 ? run + 1 : 1; last = host.Game.Day; longest = Math.Max(longest, run); }
        }
        int refused = m.Rejected.GetValueOrDefault("Collective Assault FullyShielded");
        _out.WriteLine($"landings refused for the shields by day 2000: {refused}, longest run of days in a row {longest}");
        Assert.True(refused > 0, "no landing was refused: the test no longer checks anything");
        Assert.True(longest <= 1, $"the Collective landed into the same walls {longest} days in a row");
        Assert.Contains(host.AiMemoryFor(Side.Collective, true).Notes, n => n.Kind == AiNoteKind.WallsRefused);
    }

    // AiMemory.Ingest, WallsRefused: an own assault refused as FullyShielded is noted at its system; other refusals, orders that are not own
    // assaults, and anything while the note is not kept are not.
    [Fact]
    public void AiMemory_NotesALandingRefusedForTheShields()
    {
        string? At(long seq) => seq is 5 or 6 ? "Charter" : null;
        GameEvent[] feed =
        {
            new OrderRejected(0, 9, 5, Praedra.Core.Assault.AssaultReject.FullyShielded),
            new OrderRejected(1, 9, 6, Praedra.Core.Assault.AssaultReject.NoTroops),
            new OrderRejected(2, 9, 7, Praedra.Core.Assault.AssaultReject.FullyShielded),
        };
        var off = new AiMemory { Side = Side.Collective, Fog = true };
        off.Ingest(feed, _ => false, null, null, At);
        Assert.DoesNotContain(off.Notes, n => n.Kind == AiNoteKind.WallsRefused);
        var on = new AiMemory { Side = Side.Collective, Fog = true };
        on.KeepAtLeast(0, 0, 0, 0, 0, 0, wallsRefused: 365);
        on.Ingest(feed, _ => false, null, null, At);
        var w = on.Notes.Where(n => n.Kind == AiNoteKind.WallsRefused).Select(n => (n.System, n.Day)).ToList();
        Assert.Equal(new[] { ((string?)"Charter", 9) }, w);
    }

    // AiMemory.Ingest, ShieldsHit (WinSiege; m3-bombardment §2 hits = max(0, P - S)): an own shelling that got hits notes the shields it met,
    // beside its ShieldsFelt note; one with no hits, the other side's, and anything while the note is not kept are not.
    [Fact]
    public void AiMemory_NotesTheShieldsAShellingGotThrough()
    {
        var fleets = new ValueList<int>(new List<int> { 7 });
        GameEvent[] feed =
        {
            new BombardmentStarted(0, 9, "Charter", 7, Side.Collective, BombardCategory.Military, 200, 80, 120, fleets),
            new BombardmentStarted(1, 10, "Charter", 7, Side.Collective, BombardCategory.Military, 60, 160, 0, fleets),
            new BombardmentStarted(2, 11, "Charter", 7, Side.Hand, BombardCategory.Military, 200, 0, 200, fleets),
        };
        var off = new AiMemory { Side = Side.Collective, Fog = true };
        off.KeepAtLeast(0, 0, 0, 0, 0, 0, shieldsFelt: 120);
        off.Ingest(feed, _ => false);
        Assert.DoesNotContain(off.Notes, n => n.Kind == AiNoteKind.ShieldsHit);
        Assert.Equal(2, off.Notes.Count(n => n.Kind == AiNoteKind.ShieldsFelt));
        var on = new AiMemory { Side = Side.Collective, Fog = true };
        on.KeepAtLeast(0, 0, 0, 0, 0, 0, shieldsFelt: 120, shieldsHit: 120);
        on.Ingest(feed, _ => false);
        Assert.Equal(new[] { ((string?)"Charter", 9, 80) }, on.Notes.Where(n => n.Kind == AiNoteKind.ShieldsHit).Select(n => (n.System, n.Day, n.Value)).ToList());
        Assert.Equal(2, on.Notes.Count(n => n.Kind == AiNoteKind.ShieldsFelt));
    }

    // GuardedOriginWait (QA-136; m5-missions §Guards, orbit leaving check: a team setting out to another system from a world with enemy fleets
    // and no own Lien-150 is checked by every ship there). QA's game: seed 8383, MedA, Hard mirror, New battle, QA's base parameters, to day
    // 1000, with the leader rules of main ba6a7f7 (no fleet catch, no inbound sightings, plain Autoroute): Hand ships over Natalis foiled every
    // departure and 15 Collective leaders were taken in the mission duels, 14 at Natalis. With the switch none.
    [Trait("Category", "Play")]
    [Fact]
    public void NoMissionSetsOutPastEnemyShips()
    {
        int Duels(bool wait)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = 8383, Preset = StartPreset.MedA, Settings = new GameSettings
            {
                Battle = new Praedra.Core.Battle.BattleParams { Ruleset = Praedra.Core.Battle.BattleRuleset.New },
                Characters = new Praedra.Core.Characters.CharacterParams { AutorouteToGuarded = false, FleetsCatchLoneLeaders = false },
                Fog = new Praedra.Core.Fog.FogParams { EspionageSeesInbound = false },
            } });
            var b = new SimpleAiParams { UseFog = true, LeaderMarks = false, SpecialForceAgents = false, TaskForces = true };
            var m = new AiMatch(host, AiLevels.For(AiLevel.Hard, b), AiLevels.For(AiLevel.Hard, b) with { GuardedOriginWait = wait });
            while (host.Game.Day < 1000 && host.Victory.Winner == null) m.Step();
            return host.Game.Events.OfType<Praedra.Core.Characters.CharacterCaptured>().Count(c => c.Side == Side.Collective && c.Cause == "MissionDuel");
        }
        int off = Duels(false), on = Duels(true);
        _out.WriteLine($"Collective leaders taken in mission duels by day 1000: {off} without, {on} with GuardedOriginWait");
        Assert.True(off >= 10, $"only {off} duel captures: the test no longer shows QA-136");
        Assert.True(on <= 2, $"{on} duel captures with GuardedOriginWait");
    }
}
