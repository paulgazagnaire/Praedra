using Rebellion.Core.Clock;
using Rebellion.Core.Data;

namespace Rebellion.Core.Setup;

/// <summary>The five day-one start presets (setup-victory/09 §3 totals). Suffix a = player is the Alliance, e = player is the Empire.</summary>
public enum StartPreset { Easy, MedA, MedE, HardA, HardE }

public static class StartPresets
{
    public static string Key(StartPreset p) => p switch
    {
        StartPreset.Easy => "easy", StartPreset.MedA => "med_a", StartPreset.MedE => "med_e",
        StartPreset.HardA => "hard_a", StartPreset.HardE => "hard_e", _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static StartPreset Parse(string key) => key.Trim().ToLowerInvariant() switch
    {
        "easy" => StartPreset.Easy, "med_a" => StartPreset.MedA, "med_e" => StartPreset.MedE,
        "hard_a" => StartPreset.HardA, "hard_e" => StartPreset.HardE,
        _ => throw new ArgumentException($"Unknown preset '{key}' (easy, med_a, med_e, hard_a, hard_e)."),
    };

    /// <summary>The side the preset is meant for. Easy: Alliance (DEFAULT; the file has no player column).</summary>
    public static Side PlayerSide(StartPreset p) => p is StartPreset.MedE or StartPreset.HardE ? Side.Empire : Side.Alliance;
}

/// <summary>One row of a day-one file (setup-victory/09 §1).</summary>
public sealed record DayOneRow(string Name, Side? Owner, int AllianceSupport, bool Weak, bool ExploredAll, bool ExploredOwner, bool IsHq);

/// <summary>One fleet row of new-game-forces.csv: ships and squadrons by class and count, placed at the side's base or second site.</summary>
public sealed record ForceFleet(Side Side, string Name, bool AtBase, IReadOnlyList<string> Ships, IReadOnlyList<string> Squadrons);

/// <summary>The starting-forces table <c>new-game-forces.csv</c> (all DEFAULT, playable slice 2026-10-12): numbers per side.</summary>
public sealed class ForcesTable
{
    public const string FileName = "new-game-forces.csv";
    private readonly Dictionary<(Side, string), string> _params = new();
    public List<(Side Side, string Kind, int Count)> BaseFacilities { get; } = new();
    public List<(Side Side, string Kind, int Count)> SiteFacilities { get; } = new();
    public List<ForceFleet> Fleets { get; } = new();

    public string Str(Side s, string key) => _params.TryGetValue((s, key), out var v) ? v : throw new DataLoadException($"{FileName}: missing param '{key}' for {s}.");
    public int Int(Side s, string key) => int.Parse(Str(s, key), System.Globalization.CultureInfo.InvariantCulture);

    public static ForcesTable Load(string path)
    {
        var t = CsvTable.Load(path);
        t.Require("kind", "side", "key", "value", "source");
        var f = new ForcesTable();
        for (int r = 0; r < t.RowCount; r++)
        {
            string kind = t.Get(r, "kind").Trim(), key = t.Get(r, "key").Trim(), value = t.Get(r, "value").Trim();
            var sides = t.Get(r, "side").Trim() switch
            {
                "Empire" => new[] { Side.Empire }, "Alliance" => new[] { Side.Alliance }, "*" => new[] { Side.Empire, Side.Alliance },
                var x => throw new DataLoadException($"{t.Where(r)}: side must be Empire, Alliance or *, not '{x}'."),
            };
            foreach (var side in sides)
                switch (kind)
                {
                    case "param": f._params[(side, key)] = value; break;
                    case "base_facility": f.BaseFacilities.Add((side, key, int.Parse(value))); break;
                    case "site_facility": f.SiteFacilities.Add((side, key, int.Parse(value))); break;
                    case "fleet":
                        {
                            var at = key.Split('@');
                            var parts = value.Split('/');
                            f.Fleets.Add(new ForceFleet(side, at[0], at.Length < 2 || at[1] == "base", Expand(parts[0]), parts.Length > 1 ? Expand(parts[1]) : Array.Empty<string>()));
                            break;
                        }
                    default: throw new DataLoadException($"{t.Where(r)}: unknown kind '{kind}'.");
                }
        }
        return f;
    }

    /// <summary>"A*2;B" -> A, A, B.</summary>
    private static List<string> Expand(string list)
    {
        var res = new List<string>();
        foreach (var item in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split('*');
            int n = p.Length > 1 ? int.Parse(p[1]) : 1;
            for (int i = 0; i < n; i++) res.Add(p[0].Trim());
        }
        return res;
    }
}

/// <summary>The data files of a new game: the five day-one CSVs and the starting forces. Read once. Not part of <see cref="GameData"/> (additive).</summary>
public sealed class NewGameData
{
    public IReadOnlyDictionary<StartPreset, IReadOnlyList<DayOneRow>> DayOne { get; }
    public ForcesTable Forces { get; }
    /// <summary>fixed-owners.csv: (system, owner or null = neutral). rule: m5-setup-and-victory §Fixed owners (Decided).</summary>
    public IReadOnlyList<(string System, Side? Owner)> FixedOwners { get; }

    private NewGameData(IReadOnlyDictionary<StartPreset, IReadOnlyList<DayOneRow>> dayOne, ForcesTable forces, IReadOnlyList<(string, Side?)> fixedOwners)
    { DayOne = dayOne; Forces = forces; FixedOwners = fixedOwners; }

    private static readonly Lazy<NewGameData> _default = new(() => Load(GameData.FindRepoRoot()));
    public static NewGameData Default => _default.Value;

    public static NewGameData Load(string repoRoot)
    {
        string design = Path.Combine(repoRoot, "data", "design");
        var dict = new Dictionary<StartPreset, IReadOnlyList<DayOneRow>>();
        foreach (StartPreset p in Enum.GetValues<StartPreset>())
            dict[p] = LoadDayOne(Path.Combine(design, "day-one", $"day-one-{StartPresets.Key(p)}-seed1.csv"));
        var ft = CsvTable.Load(Path.Combine(design, "fixed-owners.csv"));
        ft.Require("system", "owner");
        var fixedOwners = new List<(string, Side?)>();
        for (int r = 0; r < ft.RowCount; r++)
            fixedOwners.Add((ft.Get(r, "system").Trim(), ft.Get(r, "owner").Trim() switch
            { "Empire" => Side.Empire, "Alliance" => Side.Alliance, "Neutral" => null, var x => throw new DataLoadException($"{ft.Where(r)}: owner '{x}'.") }));
        return new NewGameData(dict, ForcesTable.Load(Path.Combine(design, ForcesTable.FileName)), fixedOwners);
    }

    private static IReadOnlyList<DayOneRow> LoadDayOne(string path)
    {
        var t = CsvTable.Load(path);
        t.Require("name", "start_owner", "start_alliance_support", "start_weak", "start_explored_all", "start_explored_owner", "is_hq");
        var rows = new List<DayOneRow>();
        for (int r = 0; r < t.RowCount; r++)
        {
            Side? owner = t.Get(r, "start_owner").Trim() switch
            {
                "Empire" => Side.Empire, "Alliance" => Side.Alliance, "neutral" => null,
                var x => throw new DataLoadException($"{t.Where(r)}: start_owner '{x}'."),
            };
            rows.Add(new DayOneRow(t.Get(r, "name").Trim(), owner, t.GetInt(r, "start_alliance_support"), t.GetInt(r, "start_weak") == 1,
                t.GetInt(r, "start_explored_all") == 1, t.GetInt(r, "start_explored_owner") == 1, t.GetInt(r, "is_hq") == 1));
        }
        return rows;
    }
}
