using System.Globalization;
using Rebellion.Core.Data;

namespace Rebellion.Core.Map;

/// <summary>
/// Lane tier table: which lane kinds belong to which tier, and the tier's speed factor in percent.
/// rule: m1-galaxy-data §Derived fields (Lane tier, Lane factor). Data file: data/design/lane-tiers.csv
/// (DEFAULT 300 / 150 / 100, Proposal planets/14).
/// </summary>
public sealed class LaneTiers
{
    /// <summary>The smallest factor the loader accepts. rule: m1-galaxy-data §Defaults (only values below 100 are refused).</summary>
    public const int MinFactorPercent = 100;

    private readonly Dictionary<string, int> _tierByKind = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _factorByTier = new();

    public int Tier1 { get; }

    private LaneTiers(IEnumerable<(int tier, int factor, string[] kinds)> rows, string source)
    {
        int lowest = int.MaxValue;
        foreach (var (tier, factor, kinds) in rows)
        {
            if (factor < MinFactorPercent)
                throw new DataLoadException($"{source}: tier {tier} factor {factor} is below the minimum {MinFactorPercent}.");
            if (!_factorByTier.TryAdd(tier, factor))
                throw new DataLoadException($"{source}: tier {tier} appears twice.");
            foreach (var k in kinds)
                if (!_tierByKind.TryAdd(k, tier))
                    throw new DataLoadException($"{source}: lane kind '{k}' is listed under two tiers.");
            lowest = Math.Min(lowest, tier);
        }
        if (_factorByTier.Count == 0) throw new DataLoadException($"{source}: no tiers defined.");
        Tier1 = lowest;
    }

    public static LaneTiers Parse(string text, string source = "lane-tiers.csv")
    {
        var t = CsvTable.Parse(text, source);
        t.Require("tier", "factor_percent", "kinds");
        var rows = new List<(int, int, string[])>();
        for (int r = 0; r < t.RowCount; r++)
            rows.Add((t.GetInt(r, "tier"), t.GetInt(r, "factor_percent"),
                t.Get(r, "kinds").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        return new LaneTiers(rows, source);
    }

    public static LaneTiers Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public bool IsKnownKind(string kind) => _tierByKind.ContainsKey(kind);
    public int TierOfKind(string kind) => _tierByKind[kind];
    public int FactorOfTier(int tier) => _factorByTier[tier];
    public IEnumerable<string> KnownKinds => _tierByKind.Keys.OrderBy(k => k, StringComparer.Ordinal);
    public IEnumerable<int> Tiers => _factorByTier.Keys.OrderBy(k => k);

    public override string ToString() => string.Join(", ", Tiers.Select(t => $"{t}={FactorOfTier(t).ToString(CultureInfo.InvariantCulture)}%"));
}
