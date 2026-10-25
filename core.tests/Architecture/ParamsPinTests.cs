using Praedra.Core.Assault;
using Praedra.Core.Characters;
using Praedra.Core.Construction;
using Praedra.Core.Data;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using Praedra.Core.Support;
using Praedra.Core.Travel;

namespace Praedra.Core.Tests.Architecture;

/// <summary>
/// Every named GNPRTB / SDPRTB constant in core pinned to the Classic file (data/classic/gnprtb.csv, sdprtb.csv, written by importer/).
/// rule: B1-own-importer §Tests ("Pin every GNPRTB / SDPRTB number in core to the file"); CLAUDE.md hard rule 6.
/// </summary>
public class ParamsPinTests
{
    private static SortedDictionary<int, int[]> Table(string file)
    {
        var t = CsvTable.Load(Path.Combine(TestData.ClassicDir, file));
        var vs = t.Header.Where(h => h.StartsWith('v')).ToList();
        var d = new SortedDictionary<int, int[]>();
        for (int r = 0; r < t.RowCount; r++) d[t.GetInt(r, "key")] = vs.Select(v => t.GetInt(r, v)).ToArray();
        return d;
    }

    /// <summary>(where, value in core, table, key, sign): the file value of column v0 times sign must equal the core value; every column must agree.</summary>
    private static IEnumerable<(string Where, int Core, string Table, int Key, int Sign)> Pins()
    {
        var eco = new EconomyParams(); var sup = new SupportParams(); var ch = new CharacterParams(); var fog = new FogParams(); var hq = new HqParams();
        var con = new ConstructionParams(); var asl = new AssaultParams(); var def = new DefenseParams(); var bomb = new BombardParams(); var deal = new DealParams();
        const string G = "gnprtb.csv", S = "sdprtb.csv";
        // m1-travel
        yield return ("TravelFormula.DistanceDivisor", TravelFormula.DistanceDivisor, G, 5120, 1);
        yield return ("TravelFormula.DefaultSpeed", TravelFormula.DefaultSpeed, G, 1, 1);
        yield return ("CharacterParams.CharacterSpeed", ch.CharacterSpeed, G, 1, 1);
        yield return ("ConstructionParams.UnratedDeliverySpeed", con.UnratedDeliverySpeed, G, 1, 1);
        yield return ("HqParams.Speed", hq.Speed, G, 1, 1);
        // m2 economy
        yield return ("EconomyParams.LoadBrakeDivisor", eco.LoadBrakeDivisor, G, 4096, 1);
        yield return ("EconomyParams.SlowdownNumerator", eco.SlowdownNumerator, G, 7763, 1);
        yield return ("EconomyParams.SmugglingTiers[0].MaxSupport", eco.SmugglingTiers[0].MaxSupport, G, 7736, 1);
        yield return ("EconomyParams.SmugglingTiers[0].Percent", eco.SmugglingTiers[0].Percent, G, 7737, 1);
        yield return ("EconomyParams.SmugglingTiers[1].MaxSupport", eco.SmugglingTiers[1].MaxSupport, G, 7734, 1);
        yield return ("EconomyParams.SmugglingTiers[1].Percent", eco.SmugglingTiers[1].Percent, G, 7735, 1);
        yield return ("EconomyParams.SmugglingTiers[2].MaxSupport", eco.SmugglingTiers[2].MaxSupport, G, 7732, 1);
        yield return ("EconomyParams.SmugglingTiers[2].Percent", eco.SmugglingTiers[2].Percent, G, 7733, 1);
        yield return ("EconomyParams.ShipPresence", eco.ShipPresence, G, 7686, 1);
        yield return ("EconomyParams.FighterPresence", eco.FighterPresence, G, 7687, 1);
        yield return ("EconomyParams.RegimentPresence", eco.RegimentPresence, G, 7688, 1);
        yield return ("EconomyParams.HarvesterRegimentMultiplier", eco.HarvesterRegimentMultiplier, G, 7680, 1);
        yield return ("EconomyParams.ResourceIntervalMin", eco.ResourceIntervalMin, G, 7719, 1);
        yield return ("EconomyParams.ResourceIntervalSpread", eco.ResourceIntervalSpread, G, 7720, 1);
        yield return ("EconomyParams.DisasterIntervalMin", eco.DisasterIntervalMin, G, 7717, 1);
        yield return ("EconomyParams.DisasterIntervalSpread", eco.DisasterIntervalSpread, G, 7718, 1);
        yield return ("EconomyParams.DisasterDestroyPercent", eco.DisasterDestroyPercent, G, 7716, 1);
        yield return ("EconomyParams.CorePopulatedPercent", eco.CorePopulatedPercent, G, 7730, 1);
        yield return ("EconomyParams.RimPopulatedPercent", eco.RimPopulatedPercent, G, 7731, 1);
        yield return ("EconomyParams.CoreEnergyBase", eco.CoreEnergyBase, G, 7721, 1);
        yield return ("EconomyParams.CoreEnergySpread", eco.CoreEnergySpread, G, 7722, 1);
        yield return ("EconomyParams.CoreRawBase", eco.CoreRawBase, G, 7723, 1);
        yield return ("EconomyParams.CoreRawSpread", eco.CoreRawSpread, G, 7724, 1);
        yield return ("EconomyParams.RimRawBase", eco.RimRawBase, G, 7728, 1);
        yield return ("EconomyParams.RimRawSpread", eco.RimRawSpread, G, 7729, 1);
        yield return ("EconomyParams.RimEnergyBase", eco.RimEnergyBase, G, 7725, 1);
        yield return ("EconomyParams.RimEnergySpreadA", eco.RimEnergySpreadA, G, 7726, 1);
        yield return ("EconomyParams.RimEnergySpreadB", eco.RimEnergySpreadB, G, 7727, 1);
        yield return ("EconomyParams.ScrapPeriod", eco.ScrapPeriod, G, 7168, 1);
        // m2 support and garrison
        yield return ("SupportParams.HarvesterDampingDivisor", sup.HarvesterDampingDivisor, G, 7681, 1);
        yield return ("SupportParams.ErosionPeriod", sup.ErosionPeriod, G, 7698, 1);
        yield return ("SupportParams.ErosionAmount", sup.ErosionAmount, G, 7697, 1);
        yield return ("SupportParams.PresencePeriod (7694)", sup.PresencePeriod, G, 7694, 1);
        yield return ("SupportParams.PresencePeriod (7696)", sup.PresencePeriod, G, 7696, 1);
        yield return ("SupportParams.PresenceGain", sup.PresenceGain, G, 7693, 1);
        yield return ("SupportParams.PresenceLoss", sup.PresenceLoss, G, 7695, 1);
        yield return ("SupportParams.HandPassiveGain", sup.HandPassiveGain, G, 7691, 1);
        yield return ("SupportParams.GarrisonFreeSupport", sup.GarrisonFreeSupport, G, 7761, 1);
        yield return ("SupportParams.GarrisonStep (cited as -10)", sup.GarrisonStep, G, 7762, -1);
        yield return ("SupportParams.UprisingMultiplier", sup.UprisingMultiplier, G, 7682, 1);
        yield return ("SupportParams.HarvesterGarrisonDivisor", sup.HarvesterGarrisonDivisor, G, 7680, 1);
        yield return ("SupportParams.HarvesterScoreMultiplier", sup.HarvesterScoreMultiplier, G, 7680, 1);
        yield return ("SupportParams.IncidentIntervalMin", sup.IncidentIntervalMin, G, 7701, 1);
        yield return ("SupportParams.IncidentIntervalSpread", sup.IncidentIntervalSpread, G, 7702, 1);
        yield return ("SupportParams.DieBase", sup.DieBase, G, 7707, 1);
        yield return ("SupportParams.DieSpread", sup.DieSpread, G, 7708, 1);
        // m3 assault, defenses, bombardment
        yield return ("AssaultParams.LeadershipStep", asl.LeadershipStep, G, 0x201, 1);
        yield return ("AssaultParams.AttackerDiesAtMost", asl.AttackerDiesAtMost, G, 0x203, 1);
        yield return ("AssaultParams.DefenderDiesAtLeast", asl.DefenderDiesAtLeast, G, 0x204, 1);
        yield return ("AssaultParams.CollateralPercent", asl.CollateralPercent, G, 0x205, 1);
        yield return ("DefenseParams.ShotChanceDivisor", def.ShotChanceDivisor, G, 0x200, 1);
        yield return ("BombardParams.CivilianSupportChange", bomb.CivilianSupportChange, G, 7705, 1);
        // B7-bombardment §7 (the strike's numbers)
        yield return ("BombardParams.GeneralStep", bomb.GeneralStep, G, 1536, 1);
        yield return ("BombardParams.AdmiralStep", bomb.AdmiralStep, G, 1537, 1);
        yield return ("BombardParams.DieMin", bomb.DieMin, G, 1538, 1);
        yield return ("BombardParams.DieMax", bomb.DieMax, G, 1539, 1);
        yield return ("BombardParams.EnergyDefense", bomb.EnergyDefense, G, 1540, 1);
        yield return ("BombardParams.RawDefense", bomb.RawDefense, G, 1541, 1);
        yield return ("BombardParams.CriticalCap", bomb.CriticalCap, G, 2048, 1);
        yield return ("BombardParams.CriticalRollMax", bomb.CriticalRollMax, G, 2049, 1);
        yield return ("BombardParams.CriticalAt", bomb.CriticalAt, G, 2050, 1);
        yield return ("BombardParams.CriticalTableMax", bomb.CriticalTableMax, G, 2051, 1);
        // m5 characters
        yield return ("CharacterParams.HarvesterHitPercent", ch.HarvesterHitPercent, G, 0x606, 1);
        yield return ("CharacterParams.ClassicTrainingGainPercent", CharacterParams.ClassicTrainingGainPercent, G, 6173, 1); // the New DEFAULT TalentTrainingGainPercent is 25 (Paul 2026-10-18)
        yield return ("CharacterParams.TalentAnomalyMin (2560)", ch.TalentAnomalyMin, G, 2560, 1);
        yield return ("CharacterParams.TalentAnomalyMin (2580)", ch.TalentAnomalyMin, G, 2580, 1);
        yield return ("CharacterParams.TalentUnforecastableMin", ch.TalentUnforecastableMin, G, 2581, 1);
        // m5 fog and HQ
        yield return ("FogParams.EspionageRichSpread", fog.EspionageRichSpread, G, 6181, 1);
        yield return ("FogParams.EspionageRichBase", fog.EspionageRichBase, G, 6180, 1);
        yield return ("FogParams.EspionageCapitalSpread", fog.EspionageCapitalSpread, G, 6179, 1);
        yield return ("FogParams.EspionageCapitalBase", fog.EspionageCapitalBase, G, 6178, 1);
        yield return ("FogParams.EspionageOtherSpread", fog.EspionageOtherSpread, G, 6177, 1);
        yield return ("FogParams.EspionageOtherBase", fog.EspionageOtherBase, G, 6176, 1);
        yield return ("HqParams.DepartureShipWeight", hq.DepartureShipWeight, G, 7684, 1);
        yield return ("HqParams.DepartureFighterWeight", hq.DepartureFighterWeight, G, 7685, 1);
        yield return ("HqParams.ArrivalSupportDelta", hq.ArrivalSupportDelta, G, 5127, 1);
        // m5 setup (Anchored deal)
        yield return ("DealParams.NeutralCoreSpread", deal.NeutralCoreSpread, G, 7764, 1);
        yield return ("DealParams.StrongBase", deal.StrongBase, S, 0x1e02, 1);
        yield return ("DealParams.BandSpread (0x1e03)", deal.BandSpread, S, 0x1e03, 1);
        yield return ("DealParams.WeakBase", deal.WeakBase, S, 0x1e04, 1);
        yield return ("DealParams.BandSpread (0x1e05)", deal.BandSpread, S, 0x1e05, 1);
        // m5-missions §3 Talent sensing (B2b)
        var mis = new Praedra.Core.Missions.MissionParams();
        yield return ("MissionParams.SensedMinTalent", mis.SensedMinTalent, G, 3585, 1);
        yield return ("MissionParams.SenserMinTalent", mis.SenserMinTalent, G, 3586, 1);
        yield return ("MissionParams.SenseConstant", mis.SenseConstant, G, 3587, 1);
        // m5-characters-and-force §Loyalty and betrayal (B2c)
        yield return ("CharacterParams.LossShiftDivisor", ch.LossShiftDivisor, S, 2560, 1);
        yield return ("CharacterParams.CaptureShiftSpread", ch.CaptureShiftSpread, S, 2561, 1);
        yield return ("CharacterParams.CaptureShiftBase", ch.CaptureShiftBase, S, 2562, 1);
        yield return ("CharacterParams.HqShiftBound", ch.HqShiftBound, G, 2592, 1);
        yield return ("CharacterParams.HqShiftBase", ch.HqShiftBase, G, 2593, 1);
    }

    // B1-own-importer Tests (pin): each named constant equals its GNPRTB / SDPRTB value, table named; flat across every column
    [Fact]
    public void EveryNamedConstantEqualsTheFile()
    {
        var tables = new SortedDictionary<string, SortedDictionary<int, int[]>>(StringComparer.Ordinal)
        {
            ["gnprtb.csv"] = Table("gnprtb.csv"), ["sdprtb.csv"] = Table("sdprtb.csv"),
        };
        var bad = new List<string>();
        int n = 0;
        foreach (var p in Pins())
        {
            n++;
            if (!tables[p.Table].TryGetValue(p.Key, out var vs)) { bad.Add($"{p.Where}: {p.Table} has no key {p.Key}"); continue; }
            if (vs.Distinct().Count() != 1) bad.Add($"{p.Where}: {p.Table} key {p.Key} differs by column ({string.Join(" ", vs)})");
            else if (vs[0] * p.Sign != p.Core) bad.Add($"{p.Where} = {p.Core}, {p.Table} key {p.Key} = {vs[0]}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.True(n >= 60, $"only {n} pins");
    }

    // B1-own-importer Tests (pin): the mission numbers that are one GNPRTB value come from the file through mission-numbers.csv
    [Fact]
    public void MissionNumbersEqualTheFile()
    {
        var g = Table("gnprtb.csv");
        var m = GameData.Default.Missions.Numbers;
        Assert.Equal((g[3584][0], g[3588][0], g[3589][0], g[6145][0], g[6144][0], g[6158][0], g[1][0], g[3083][0]),
            (m.DetectionConstant, m.DecoyOfficerPercent, m.DetectionOfficerPercent, m.InciteSupport, m.AgentLeadershipDivisor, m.ResearchSkillGain,
             m.GroupSpeed, m.RunnerLines));
    }
}
