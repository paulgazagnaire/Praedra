using Praedra.Core.AI;
using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Missions;
using Praedra.Core.Research;
using Praedra.Core.Setup;
using Xunit.Abstractions;

namespace Praedra.Core.Tests.AI;

// rule: ai tuning, the best navy (AI trainer 2026-11-26; Paul 2026-11-26: whichever side the AI plays, it researches and builds every ship it
// can and strives for the best navy; better tech is no change of doctrine). Over 10 Medium mirrors (MedE) the Hand built 117 Statutes and
// 410 Threshers but never a Decree II, Injunction, Mandate II or Edict; the Collective never a Barricade. The causes: one or three shipyards
// per system (a big ship takes longer than MaxBuildDays), both sides at their room cap with refined unspent, few construction yards, one ship
// research mission. The switches below are off by default and on at Medium and Hard.
public class SimpleAiNavyTests
{
    private readonly ITestOutputHelper _out;
    public SimpleAiNavyTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void NavySwitchesAreOffByDefault()
    {
        var p = new SimpleAiParams();
        Assert.Equal(0, p.WorldsPerConstructionYard);
        Assert.False(p.YardsFromMargin);
        Assert.Equal(0, p.ShipyardsPerSystem);
        Assert.False(p.AdvancedShipyards);
        Assert.Equal(0, p.RefitBelowPercent);
        Assert.Equal(1, p.ShipResearchMissions);
        Assert.Equal(0, p.ShipResearchValue);
        Assert.Equal(0, p.HoldShipPercent);
    }

    [Theory]
    [InlineData(AiLevel.Medium)]
    [InlineData(AiLevel.Hard)]
    public void MediumAndHardStriveForTheBestNavy(AiLevel level)
    {
        var p = AiLevels.For(level);
        Assert.True(p.WorldsPerConstructionYard > 0 && p.YardsFromMargin && p.ShipyardsPerSystem > p.YardsPerSystem && p.AdvancedShipyards);
        Assert.True(p.RefitBelowPercent > 0 && p.ShipResearchMissions > 1 && p.HoldShipPercent > 0);
        Assert.Equal(0, AiLevels.For(AiLevel.Easy).HoldShipPercent); // Easy keeps the old navy
    }

    // Seed 1 MedE, Medium mirror, both sides given the whole ship ladder and 5,000 refined on day 0 (test setup), MaxBuildDays out of the way:
    // no transports wanted (test setup): the Barricade (Hold beam power 6) is never the best strength for its price (the Homeward is), so only HoldShipPercent orders one.
    private int BarricadesBy(int day, int holdPercent, BattleRuleset ruleset = BattleRuleset.New)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE, Settings = new GameSettings { Battle = new BattleParams { Ruleset = ruleset } } });
        foreach (var side in new[] { Side.Hand, Side.Collective })
        {
            host.Research.SetLevel(side, ResearchKind.Ship, 11);
            host.Economy.SetPools(side, host.Economy.Raw(side), 5000);
        }
        // no transports first (TroopCapacityTarget 0), so the first orders are warships
        var p = AiLevels.For(AiLevel.Medium) with { MaxBuildDays = 5000, HoldShipPercent = holdPercent, RefitBelowPercent = 0, TroopCapacityTarget = 0 };
        var m = new AiMatch(host, p, p);
        m.RunTo(day);
        int n = host.Economy.Objects.Count(o => o.Class == "Barricade" && host.Economy.OwnerOf(o) == Side.Collective);
        _out.WriteLine($"hold {holdPercent}% {ruleset}: {n} Barricades by day {host.Game.Day}");
        return n;
    }

    [Fact]
    public void HoldShipsAreBuiltOnlyWithTheShareOnAndInTheNewBattle()
    {
        Assert.Equal(0, BarricadesBy(150, 0));
        Assert.True(BarricadesBy(150, 50) > 0, "no Barricade with HoldShipPercent 50");
        Assert.Equal(0, BarricadesBy(150, 50, BattleRuleset.Classic)); // rule: m4-retreat-damage-repair §6: Classic Hold beams do not hold a leaving ship
    }

    // WorldsPerConstructionYard: one construction yard per few populated worlds (plus one), capped. Seed 1 MedE, Medium mirror, the Hand given
    // 3,000 refined on day 0 (test setup) so ExtraYardRefinedReserve is no limit: by day 200 it has more construction yards (built or ordered).
    private int HandConstructionYards(int worldsPer)
    {
        var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
        host.Economy.SetPools(Side.Hand, host.Economy.Raw(Side.Hand), 3000);
        var p = AiLevels.For(AiLevel.Medium) with { WorldsPerConstructionYard = worldsPer, MaxConstructionYards = 30, YardsFromMargin = worldsPer > 0 };
        var m = new AiMatch(host, p, p);
        m.RunTo(200);
        int n = host.Economy.Objects.Count(o => o.Class == "Construction Yard" && host.Economy.OwnerOf(o) == Side.Hand);
        _out.WriteLine($"one per {worldsPer} worlds: {n} Hand construction yards by day 200");
        return n;
    }

    [Fact]
    public void MoreWorldsMeanMoreConstructionYards()
    {
        int off = HandConstructionYards(0), on = HandConstructionYards(5);
        Assert.True(on > off, $"{on} construction yards with one per 5 worlds, {off} without");
    }

    // ShipResearchMissions: up to that many ship research missions out at once, sharing sites with a shipyard when there are fewer sites. A team
    // needs Ship design skill, so seed 1 MedE, Medium mirror, gives every Hand character on the map some (test setup).
    [Fact]
    public void SeveralShipResearchMissionsGoOut()
    {
        int Most(int missions)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
            // test setup: every Hand character on the map gets Ship design 40 (a team needs the skill: m5-missions §Research)
            foreach (var c in host.Characters.All.Where(c => c.Side == Side.Hand && c.Placed).OrderBy(c => c.Id))
                c.Skills[(int)Praedra.Core.Characters.Skill.ShipDesign] = Math.Max(c.Skills[(int)Praedra.Core.Characters.Skill.ShipDesign], 40);
            var p = AiLevels.For(AiLevel.Medium) with { ShipResearchMissions = missions, ShipResearchValue = 600 };
            var m = new AiMatch(host, p, p);
            int most = 0;
            while (host.Game.Day < 300 && host.Victory.Winner == null)
            {
                m.Step();
                most = Math.Max(most, host.Missions.Missions.Count(g => g.Side == Side.Hand && g.Kind == MissionKind.ShipResearch));
            }
            _out.WriteLine($"ShipResearchMissions {missions}: at most {most} Hand ship research missions out by day 300");
            return most;
        }
        Assert.True(Most(1) <= 1);
        Assert.True(Most(3) >= 2);
    }

    // RefitBelowPercent: at the room cap with refined to spare, idle weak warships are scrapped for the best one (a Scrap order naming the refit).
    // Seed 1 MedE, Medium mirror, the Collective given the whole ship ladder, 5,000 refined and four more shipyards at one of its shipyards on
    // day 0, and refineries taken away until less than a Homeward's upkeep (90) is free (test setup: the room cap of a grown side).
    [Fact]
    public void AtTheRoomCapWeakWarshipsAreRefitted()
    {
        int Refits(int below)
        {
            var host = NewGame.Create(new NewGameOptions { Seed = 1, Preset = StartPreset.MedE });
            host.Research.SetLevel(Side.Collective, ResearchKind.Ship, 11);
            var eco = host.Economy;
            eco.SetPools(Side.Collective, eco.Raw(Side.Collective), 5000);
            string stack = eco.Objects.Where(o => o.Class == "Orbital Shipyard" && o.Completed && eco.OwnerOf(o) == Side.Collective).Select(o => o.System!)
                .OrderBy(x => x, StringComparer.Ordinal).First();
            for (int i = 0; i < 4; i++) eco.AddFacility(stack, FacilityKind.OrbitalShipyard); // a stack where the Homeward finishes within MaxBuildDays
            foreach (var id in eco.Objects.Where(o => o.Class == "Refinery" && eco.OwnerOf(o) == Side.Collective).Select(o => o.Id).OrderBy(i => i).ToList())
                if (eco.Free(Side.Collective) >= 90) eco.RemoveObject(id);
            Assert.InRange(eco.Free(Side.Collective), 0, 89);
            var p = AiLevels.For(AiLevel.Medium) with { RefitBelowPercent = below, TroopCapacityTarget = 0 };
            var m = new AiMatch(host, p, p);
            int n = 0;
            while (host.Game.Day < 300 && host.Victory.Winner == null)
            {
                m.Step();
                n += m.Collective.Submitted.Count(o => o.Kind == "Scrap" && o.Detail.Contains("(refit for "));
            }
            _out.WriteLine($"RefitBelowPercent {below}: {n} refit scraps by day 300");
            return n;
        }
        Assert.Equal(0, Refits(0));
        Assert.True(Refits(50) > 0);
    }
}
