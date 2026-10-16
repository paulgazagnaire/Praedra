using System.Globalization;
using Rebellion.Core.Clock;
using Rebellion.Core.Data;

namespace Rebellion.Core.Missions;

/// <summary>
/// The mission kinds, numbered by their MISSNSD record id (Traced, missions/01). Only the agent missions, Reconnaissance, the three Research
/// missions and Move/Return run in this core; the story records exist so the data loads and are refused <c>HiddenKind</c>.
/// rule: m5-missions §Mission records (data).
/// </summary>
public enum MissionKind
{
    Move = 0x01, Return = 0x02, Autorouting = 0x03, Adrift = 0x04,
    Diplomacy = 0x10, Rescue = 0x11, Sabotage = 0x12, Espionage = 0x13, Reconnaissance = 0x15, Recruitment = 0x16, Abduction = 0x17,
    ShipResearch = 0x20, FacilityResearch = 0x21, TroopResearch = 0x22,
    InciteUprising = 0x40, DeathStarSabotage = 0x41, JediTraining = 0x42, Dagobah = 0x43, Palace = 0x44, Vacation = 0x45, Sabbatical = 0x46,
    SubdueUprising = 0x80, Assassination = 0x81, Pickup = 0x82, Bounty = 0x83,
}

/// <summary>
/// One MISSNSD row (data/interim/mission-records.csv, Traced unless its source says otherwise) plus its Foilable flag (data/design/mission-foilable.csv,
/// the sheet's DEFAULT fall-back table). Timer = <c>TimerMin + rand(0..TimerSpread)</c> days. rule: m5-missions §Mission records (data), §3 (Foilable).
/// </summary>
public sealed record MissionRecord(MissionKind Kind, bool Alliance, bool Empire, int TimerMin, int TimerSpread, bool Hidden, bool Repeats,
    string? Table, int SpecialForceMask, bool Foilable)
{
    public bool AvailableTo(Side side) => side == Side.Alliance ? Alliance : Empire;
}

/// <summary>One row of a lookup table: at or above <see cref="Threshold"/> the value is <see cref="Value"/> (a percent). rule: m5-missions §Mission tables.</summary>
public readonly record struct TableRow(int Threshold, int Value);

/// <summary>
/// The numbers of the mission sheet that are data (data/interim/mission-numbers.csv, key,value,source; each row carries its tag).
/// rule: m5-missions §Numbers; CLAUDE.md hard rule 6.
/// </summary>
public sealed record MissionNumbers(
    int RollDie, int FoilBelowFirstRow, int DetectionConstant, int DecoyOfficerPercent, int DetectionOfficerPercent,
    int DiplomacyOwnLow, int DiplomacyOwnHigh, int DiplomacyNeutralLow, int DiplomacyNeutralHigh, int SubdueLow, int SubdueHigh,
    int InciteSupport, int AgentLeadershipDivisor, int ResearchSkillGain, int GroupSpeed, int HanMissionSpeed, int HanCharacterId)
{
    public const string FileName = "mission-numbers.csv";

    public static MissionNumbers Parse(string text, string source = FileName)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("key", "value");
        var v = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        for (int r = 0; r < t.RowCount; r++)
            if (!v.TryAdd(t.Get(r, "key").Trim(), t.GetInt(r, "value"))) throw new DataLoadException($"{t.Where(r)}: duplicate key.");
        int I(string k) => v.TryGetValue(k, out var x) ? x : throw new DataLoadException($"{source}: missing key '{k}'.");
        return new MissionNumbers(I("RollDie"), I("FoilBelowFirstRow"), I("DetectionConstant"), I("DecoyOfficerPercent"), I("DetectionOfficerPercent"),
            I("DiplomacyOwnLow"), I("DiplomacyOwnHigh"), I("DiplomacyNeutralLow"), I("DiplomacyNeutralHigh"), I("SubdueLow"), I("SubdueHigh"),
            I("InciteSupport"), I("AgentLeadershipDivisor"), I("ResearchSkillGain"), I("GroupSpeed"), I("HanMissionSpeed"), I("HanCharacterId"));
    }
}

/// <summary>
/// Every mission data file, read once and immutable: the MISSNSD records, the Foilable fall-back, the *MSTB tables (anchors only until the importer
/// reads the DAT files: Missing 1), FOILTB (Traced, complete), the numbers, and the decoy table. The decoy tables FDECOYTB / TDECOYTB are Missing
/// (no values are written anywhere in the specs: Missing 2), so <see cref="DecoyTable"/> is null in the shipped data and a decoy is refused
/// <c>RuleMissing</c>; tests pass a synthetic table. rule: m5-missions §Mission records, §Mission tables, §3, §Missing rules 1 and 2.
/// </summary>
public sealed class MissionData
{
    public const string RecordsFile = "mission-records.csv", TablesFile = "mission-tables.csv", FoilableFile = "mission-foilable.csv",
        KeyTargetsFile = "assassination-key-targets.csv";
    public const string FoilTable = "FOILTB";

    private readonly SortedDictionary<MissionKind, MissionRecord> _records;
    private readonly SortedDictionary<string, IReadOnlyList<TableRow>> _tables;

    public MissionNumbers Numbers { get; }
    /// <summary>
    /// Key targets of Assassination: character roster id to cap percent (data/design/assassination-key-targets.csv). Empty = none. Lookup only, never iterated.
    /// rule: m5-missions §6a (Decided Paul 2026-10-16; numbers Proposal, missions/09 §4).
    /// </summary>
    public IReadOnlyDictionary<int, int> KeyTargets { get; }
    /// <summary>The decoy table (FDECOYTB and TDECOYTB are byte-identical: one table). Null = Missing: decoys are refused RuleMissing.</summary>
    public IReadOnlyList<TableRow>? DecoyTable { get; }

    public MissionData(IEnumerable<MissionRecord> records, IReadOnlyDictionary<string, IReadOnlyList<TableRow>> tables, MissionNumbers numbers,
        IReadOnlyList<TableRow>? decoyTable = null, IReadOnlyDictionary<int, int>? keyTargets = null)
    {
        KeyTargets = keyTargets ?? new Dictionary<int, int>();
        _records = new SortedDictionary<MissionKind, MissionRecord>();
        foreach (var r in records)
            if (!_records.TryAdd(r.Kind, r)) throw new DataLoadException($"missions: kind {r.Kind} appears twice.");
        _tables = new SortedDictionary<string, IReadOnlyList<TableRow>>(StringComparer.Ordinal);
        foreach (var kv in tables) _tables[kv.Key] = Check(kv.Key, kv.Value);
        foreach (var r in _records.Values)
            if (r.Table != null && !_tables.ContainsKey(r.Table)) throw new DataLoadException($"missions: kind {r.Kind} names table '{r.Table}' which is not loaded.");
        if (!_tables.ContainsKey(FoilTable)) throw new DataLoadException($"missions: table {FoilTable} is missing.");
        Numbers = numbers;
        DecoyTable = decoyTable == null ? null : Check("decoy", decoyTable);
    }

    private static IReadOnlyList<TableRow> Check(string name, IReadOnlyList<TableRow> rows)
    {
        if (rows.Count == 0) throw new DataLoadException($"missions: table {name} has no rows.");
        for (int i = 1; i < rows.Count; i++)
            if (rows[i].Threshold <= rows[i - 1].Threshold) throw new DataLoadException($"missions: table {name} is not sorted by threshold.");
        return rows.ToList();
    }

    public IReadOnlyCollection<MissionRecord> Records => _records.Values;
    public bool TryGet(MissionKind kind, out MissionRecord record) => _records.TryGetValue(kind, out record!);
    public MissionRecord Get(MissionKind kind) => _records.TryGetValue(kind, out var r) ? r : throw new KeyNotFoundException($"No mission record {kind}.");
    public IReadOnlyList<TableRow> Table(string name) => _tables.TryGetValue(name, out var t) ? t : throw new KeyNotFoundException($"No mission table {name}.");
    public IReadOnlyList<TableRow> Foil => _tables[FoilTable];

    /// <summary>The same data with a decoy table (tests; or once the importer reads FDECOYTB).</summary>
    public MissionData WithDecoyTable(IReadOnlyList<TableRow>? decoy) => new(_records.Values, _tables, Numbers, decoy, KeyTargets);

    /// <summary>The same data with other key targets (tests).</summary>
    public MissionData WithKeyTargets(IReadOnlyDictionary<int, int> keyTargets) => new(_records.Values, _tables, Numbers, DecoyTable, keyTargets);

    public static MissionData Load(string interim, string design) => Parse(
        File.ReadAllText(Path.Combine(interim, RecordsFile)), File.ReadAllText(Path.Combine(interim, TablesFile)),
        File.ReadAllText(Path.Combine(interim, MissionNumbers.FileName)), File.ReadAllText(Path.Combine(design, FoilableFile)),
        File.ReadAllText(Path.Combine(design, KeyTargetsFile)));

    /// <summary>rule: m5-missions §6a (assassination-key-targets.csv: character, name, cap_percent, source).</summary>
    public static IReadOnlyDictionary<int, int> ParseKeyTargets(string csv)
    {
        var k = CsvTable.Parse(csv, KeyTargetsFile);
        k.Require("character", "name", "cap_percent", "source");
        var d = new Dictionary<int, int>(); // lookup only
        for (int r = 0; r < k.RowCount; r++)
        {
            int cap = k.GetInt(r, "cap_percent");
            if (cap < 0 || cap > 100) throw new DataLoadException($"{k.Where(r)}: cap_percent {cap} is not a percent.");
            if (!d.TryAdd(k.GetInt(r, "character"), cap)) throw new DataLoadException($"{k.Where(r)}: duplicate character.");
        }
        return d;
    }

    public static MissionData Parse(string recordsCsv, string tablesCsv, string numbersCsv, string foilableCsv, string? keyTargetsCsv = null)
    {
        var f = CsvTable.Parse(foilableCsv, FoilableFile);
        f.Require("kind", "foilable");
        var foil = new Dictionary<MissionKind, bool>(); // lookup only
        for (int r = 0; r < f.RowCount; r++) foil[ParseKind(f, r)] = f.GetInt(r, "foilable") == 1;

        var t = CsvTable.Parse(recordsCsv, RecordsFile);
        t.Require("dat_id", "kind", "alliance", "empire", "timer_min", "timer_spread", "hidden", "repeats", "table", "special_force_mask");
        var recs = new List<MissionRecord>();
        for (int r = 0; r < t.RowCount; r++)
        {
            var kind = ParseKind(t, r);
            if (Hex(t, r, "dat_id") != (int)kind) throw new DataLoadException($"{t.Where(r)}: dat_id does not match kind {kind}.");
            if (!foil.TryGetValue(kind, out var fo)) throw new DataLoadException($"{FoilableFile}: no row for kind {kind}.");
            string table = t.Get(r, "table").Trim();
            int min = t.GetInt(r, "timer_min"), spread = t.GetInt(r, "timer_spread");
            if (min < 0 || spread < 0) throw new DataLoadException($"{t.Where(r)}: negative timer.");
            recs.Add(new MissionRecord(kind, t.GetInt(r, "alliance") == 1, t.GetInt(r, "empire") == 1, min, spread,
                t.GetInt(r, "hidden") == 1, t.GetInt(r, "repeats") == 1, table.Length == 0 ? null : table, Hex(t, r, "special_force_mask"), fo));
        }

        var tt = CsvTable.Parse(tablesCsv, TablesFile);
        tt.Require("table", "threshold", "value");
        var tables = new SortedDictionary<string, List<TableRow>>(StringComparer.Ordinal);
        for (int r = 0; r < tt.RowCount; r++)
        {
            string name = tt.Get(r, "table").Trim();
            int v = tt.GetInt(r, "value");
            if (v < 0 || v > 100) throw new DataLoadException($"{tt.Where(r)}: value {v} is not a percent.");
            if (!tables.TryGetValue(name, out var list)) tables[name] = list = new List<TableRow>();
            list.Add(new TableRow(tt.GetInt(r, "threshold"), v));
        }
        return new MissionData(recs, tables.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TableRow>)kv.Value, StringComparer.Ordinal),
            MissionNumbers.Parse(numbersCsv), null, keyTargetsCsv == null ? null : ParseKeyTargets(keyTargetsCsv));
    }

    private static MissionKind ParseKind(CsvTable t, int r) =>
        Enum.TryParse<MissionKind>(t.Get(r, "kind").Trim(), out var k) && Enum.IsDefined(k) ? k : throw new DataLoadException($"{t.Where(r)}: unknown mission kind '{t.Get(r, "kind")}'.");

    private static int Hex(CsvTable t, int row, string col)
    {
        string v = t.Get(row, col).Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        return int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int x) ? x
            : throw new DataLoadException($"{t.Where(row)}: '{t.Get(row, col)}' is not a hex number.");
    }
}
