using Praedra.Core.Clock;
using Praedra.Core.Data;

namespace Praedra.Core.Characters;

public enum CharacterKind { Major, Minor }

/// <summary>The seven skills, in the fixed creation draw order. rule: m5-characters-and-force §Rolling the stats (draw order 1).</summary>
public enum Skill { Diplomacy = 0, Espionage = 1, ShipDesign = 2, TroopTraining = 3, FacilityDesign = 4, Combat = 5, Leadership = 6 }

/// <summary>
/// One character record (data, never changes): identity, skill bases and variances, loyalty, Force odds and level, the capability flags and the
/// navigator speed. rule: m5-characters-and-force §The character object. Classic values come from MJCHARSD / MNCHARSD (data/classic/characters.csv,
/// column meaning unverified vs Classic); added heroes from data/design/added-heroes.csv (Proposal stats).
/// </summary>
public sealed record CharacterClass(
    int Id, string Name, Side Side, CharacterKind Kind,
    IReadOnlyList<int> SkillBase, IReadOnlyList<int> SkillVariance,
    int LoyaltyBase, int LoyaltyVariance,
    int ForceProbability, bool KnownJedi, int ForceLevelBase, int ForceLevelVariance,
    bool CanAdmiral, bool CanCommander, bool CanGeneral, bool UnableToBetray, bool JediTrainer,
    int NavigatorSpeed, bool Added, bool InGame, bool NeverTalented = false)
{
    public const int SkillCount = 7;
    public bool Can(CharacterPost post) => post switch
    {
        CharacterPost.Admiral => CanAdmiral, CharacterPost.Commander => CanCommander, CharacterPost.General => CanGeneral, _ => true,
    };
}

public enum StartRule { FixedSystem, FixedHq, RandomLocation, Extras }

/// <summary>One row of start-cast.csv, in file order (= placement order). rule: m5-setup-and-victory §P9.</summary>
public sealed record StartCastRow(int Character, StartRule Rule, string Place, bool Officer);

/// <summary>A victory term naming a character (victory-rules.csv kind CharacterCaptured). rule: m5-setup-and-victory §Victory.</summary>
public sealed record VictoryKey(bool Standard, Side Side, int Character);

/// <summary>One added hero breaking a stat-ceiling rule (the AddedHeroLimits import check). rule: m5-characters-and-force CH32; heroes/stat-ceiling.</summary>
public sealed record HeroLimitViolation(int Character, string Name, int Rule, string Detail);

/// <summary>
/// The character data files: the 60 original records, the added heroes, the roster (which records are in the game), the navigator table, the
/// start cast and the character terms of the victory rules. Read once, immutable. rule: m5-characters-and-force §The character object.
/// </summary>
public sealed class CharacterData
{
    public const string OriginalFile = "characters.csv", AddedFile = "added-heroes.csv", RosterFile = "roster.csv",
        NavigatorFile = "navigator_speed.csv", StartCastFile = "start-cast.csv", VictoryFile = "victory-rules.csv";

    /// <summary>Every record (in the game or not), by id ascending.</summary>
    public IReadOnlyList<CharacterClass> All { get; }
    /// <summary>Records in the game (roster in_game = 1), by id ascending.</summary>
    public IReadOnlyList<CharacterClass> Roster { get; }
    public IReadOnlyList<StartCastRow> StartCast { get; }
    public IReadOnlyList<VictoryKey> VictoryKeys { get; }
    private readonly Dictionary<int, CharacterClass> _byId; // lookup only

    public CharacterData(IEnumerable<CharacterClass> all, IEnumerable<StartCastRow>? startCast = null, IEnumerable<VictoryKey>? victoryKeys = null)
    {
        All = all.OrderBy(c => c.Id).ToList();
        _byId = All.ToDictionary(c => c.Id);
        Roster = All.Where(c => c.InGame).ToList();
        StartCast = (startCast ?? Array.Empty<StartCastRow>()).ToList();
        VictoryKeys = (victoryKeys ?? Array.Empty<VictoryKey>()).ToList();
    }

    public CharacterClass Get(int id) => _byId.TryGetValue(id, out var c) ? c : throw new KeyNotFoundException($"No character record {id}.");
    public bool TryGet(int id, out CharacterClass c) => _byId.TryGetValue(id, out c!);
    public CharacterClass? ByName(string name) => All.FirstOrDefault(c => c.Name == name);

    public static CharacterData Load(string original, string design)
    {
        var nav = new Dictionary<int, int>();
        var nt = CsvTable.Load(Path.Combine(design, NavigatorFile));
        nt.Require("id", "speed");
        for (int r = 0; r < nt.RowCount; r++) nav[nt.GetInt(r, "id")] = nt.GetInt(r, "speed");

        var inGame = new Dictionary<int, bool>();
        var neverTalented = new HashSet<int>(); // membership test only
        var rt = CsvTable.Load(Path.Combine(design, RosterFile));
        rt.Require("id", "in_game");
        bool hasNever = rt.HasColumn("never_talented"); // m5-characters-and-force Q17 (Paul 2026-10-18): 1 = never in the talented draw (the bounty hunters)
        for (int r = 0; r < rt.RowCount; r++)
        {
            inGame[rt.GetInt(r, "id")] = rt.GetInt(r, "in_game") == 1;
            if (hasNever && rt.Get(r, "never_talented").Trim() == "1") neverTalented.Add(rt.GetInt(r, "id"));
        }

        var all = new List<CharacterClass>();
        all.AddRange(Rows(CsvTable.Load(Path.Combine(original, OriginalFile)), false, nav, inGame, neverTalented));
        all.AddRange(Rows(CsvTable.Load(Path.Combine(design, AddedFile)), true, nav, inGame, neverTalented));
        if (all.Select(c => c.Id).Distinct().Count() != all.Count) throw new DataLoadException("characters: a character id appears twice.");
        foreach (var id in nav.Keys.Concat(inGame.Keys))
            if (!all.Any(c => c.Id == id)) throw new DataLoadException($"characters: id {id} in {RosterFile} or {NavigatorFile} has no record.");

        var cast = new List<StartCastRow>();
        var ct = CsvTable.Load(Path.Combine(design, StartCastFile));
        ct.Require("character", "rule", "place", "officer");
        for (int r = 0; r < ct.RowCount; r++)
        {
            var rule = Enum.TryParse<StartRule>(ct.Get(r, "rule").Trim(), out var x) ? x : throw new DataLoadException($"{ct.Where(r)}: unknown rule.");
            int id = ct.GetInt(r, "character");
            if (rule != StartRule.Extras && !all.Any(c => c.Id == id && c.InGame)) throw new DataLoadException($"{ct.Where(r)}: character {id} is not in the roster.");
            cast.Add(new StartCastRow(id, rule, ct.Get(r, "place").Trim(), ct.GetInt(r, "officer") == 1));
        }

        var keys = new List<VictoryKey>();
        var vt = CsvTable.Load(Path.Combine(design, VictoryFile));
        vt.Require("mode", "side", "kind", "subject");
        for (int r = 0; r < vt.RowCount; r++)
        {
            if (vt.Get(r, "kind").Trim() != "CharacterCaptured") continue;
            int id = vt.GetInt(r, "subject");
            if (!all.Any(c => c.Id == id && c.InGame)) throw new DataLoadException($"{vt.Where(r)}: victory character {id} is not in the roster."); // m5-setup-and-victory P0.4, V18
            keys.Add(new VictoryKey(vt.Get(r, "mode").Trim() == "Standard", ParseSide(vt.Get(r, "side"), vt, r), id));
        }
        return new CharacterData(all, cast, keys);
    }

    private static Side ParseSide(string s, CsvTable t, int r) => s.Trim() switch
    {
        "Hand" => Side.Hand, "Collective" => Side.Collective, var x => throw new DataLoadException($"{t.Where(r)}: side '{x}'."),
    };

    private static readonly string[] SkillColumns = { "dip", "esp", "ship", "troop", "fac", "com", "lead" };

    private static IEnumerable<CharacterClass> Rows(CsvTable t, bool added, Dictionary<int, int> nav, Dictionary<int, bool> inGame, HashSet<int> neverTalented)
    {
        t.Require("kind", "name", "id", "collective", "hand", "loy", "loyv", "jprob", "knownjedi", "jlvl", "jlvlv", "admiral", "commander", "general", "nobetray", "trainer");
        for (int r = 0; r < t.RowCount; r++)
        {
            int id = t.GetInt(r, "id");
            bool all = t.GetInt(r, "collective") == 1, emp = t.GetInt(r, "hand") == 1;
            if (all == emp) throw new DataLoadException($"{t.Where(r)}: a character serves exactly one side.");
            var kind = t.Get(r, "kind").Trim() switch
            {
                "major" => CharacterKind.Major, "minor" => CharacterKind.Minor, var k => throw new DataLoadException($"{t.Where(r)}: kind '{k}'."),
            };
            yield return new CharacterClass(id, t.Get(r, "name").Trim(), all ? Side.Collective : Side.Hand, kind,
                SkillColumns.Select(c => t.GetInt(r, c)).ToList(), SkillColumns.Select(c => t.GetInt(r, c + "v")).ToList(),
                t.GetInt(r, "loy"), t.GetInt(r, "loyv"),
                t.GetInt(r, "jprob"), t.GetInt(r, "knownjedi") == 1, t.GetInt(r, "jlvl"), t.GetInt(r, "jlvlv"),
                t.GetInt(r, "admiral") == 1, t.GetInt(r, "commander") == 1, t.GetInt(r, "general") == 1, t.GetInt(r, "nobetray") == 1, t.GetInt(r, "trainer") == 1,
                nav.TryGetValue(id, out var s) ? s : Travel.TravelFormula.DefaultSpeed, // 100 unless listed (Proposal)
                added, inGame.TryGetValue(id, out var g) && g, neverTalented.Contains(id));
        }
    }

    // ------------------------------------------------------------------ AddedHeroLimits (import check, not a game rule)

    /// <summary>Stat-ceiling numbers (heroes/stat-ceiling, Proposal).</summary>
    public const int CeilingLeadership = 89, CeilingOther = 80, CeilingCombat = 95, CeilingForceProbability = 50, CeilingForceLevel = 40;

    /// <summary>
    /// The AddedHeroLimits check (DEFAULT on, an import check only): every added record against heroes/stat-ceiling rules 1 (leadership at most 89),
    /// 2 (one signature skill may reach 90 or more, every other at most 80), 3 (combat at most 95), 4 (Force probability at most 50, level at most
    /// 40, never known Jedi, never trainer), 6 (at most two of the three roles) and 7 (some variance). Values are base + variance. The original
    /// records are exempt. rule: m5-characters-and-force §Rolling the stats, CH32.
    /// </summary>
    public IReadOnlyList<HeroLimitViolation> AddedHeroLimitViolations()
    {
        var res = new List<HeroLimitViolation>();
        foreach (var c in All.Where(c => c.Added))
        {
            int Max(Skill s) => c.SkillBase[(int)s] + c.SkillVariance[(int)s];
            if (Max(Skill.Leadership) > CeilingLeadership) res.Add(new(c.Id, c.Name, 1, $"leadership {Max(Skill.Leadership)}"));
            var high = Enumerable.Range(0, CharacterClass.SkillCount).Select(i => (Skill)i).Where(s => Max(s) > CeilingOther).ToList();
            if (high.Count > 1) res.Add(new(c.Id, c.Name, 2, string.Join(", ", high.Select(s => $"{s} {Max(s)}"))));
            if (Max(Skill.Combat) > CeilingCombat) res.Add(new(c.Id, c.Name, 3, $"combat {Max(Skill.Combat)}"));
            if (c.ForceProbability > CeilingForceProbability || c.ForceLevelBase + c.ForceLevelVariance > CeilingForceLevel || c.KnownJedi || c.JediTrainer)
                res.Add(new(c.Id, c.Name, 4, "Force"));
            if ((c.CanAdmiral ? 1 : 0) + (c.CanCommander ? 1 : 0) + (c.CanGeneral ? 1 : 0) > 2) res.Add(new(c.Id, c.Name, 6, "three roles"));
            if (c.SkillVariance.All(v => v == 0)) res.Add(new(c.Id, c.Name, 7, "no variance"));
        }
        return res;
    }
}
