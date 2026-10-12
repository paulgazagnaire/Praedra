using Rebellion.Core.Data;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Fleets;

/// <summary>
/// Which enemy forces stop a passing fleet. A game setting, not code.
/// rule: m1-forced-stop-and-blockade §The alternative trigger, as a switch; §Update 2026-10-12 (PaulRule is the default).
/// </summary>
public enum ForcedStopTrigger
{
    /// <summary>
    /// DEFAULT (Paul 2026-10-12). Stop if the enemy side at the system has a live gravity-well ship, or its combat strength
    /// there is at least T times the passing fleet's combat strength.
    /// </summary>
    PaulRule = 0,
    /// <summary>Any armed enemy fleet at the system stops the passing fleet. Earlier default (planets/10 round 2).</summary>
    AnyArmedFleet = 1,
    /// <summary>Only an enemy fleet with a ship that has gravity_well_projector above 0 stops the passing fleet.</summary>
    GravityWellShip = 2,
}

/// <summary>
/// Blockade numbers read from data/design/blockade.csv: T (relative, percent) and E (absolute, built from a ship class and a count).
/// rule: m1-forced-stop-and-blockade §Update 2026-10-12 (T = 0.5, E = combat strength of 3 Victory Star Destroyers; both are data).
/// </summary>
public sealed class BlockadeData
{
    public const string FileName = "blockade.csv";

    /// <summary>T as a percent: the forced stop needs enemy strength * 100 >= ThresholdPercent * passing strength.</summary>
    public int ThresholdPercent { get; }
    /// <summary>The ship class whose strength sets E.</summary>
    public string EconomicShipClass { get; }
    /// <summary>How many of that class make E.</summary>
    public int EconomicShipCount { get; }

    public BlockadeData(int thresholdPercent, string economicShipClass, int economicShipCount)
    {
        if (thresholdPercent < 0) throw new DataLoadException("blockade: forced_stop_threshold_percent must not be negative.");
        if (economicShipCount < 0) throw new DataLoadException("blockade: economic_blockade_ship_count must not be negative.");
        ThresholdPercent = thresholdPercent;
        EconomicShipClass = economicShipClass;
        EconomicShipCount = economicShipCount;
    }

    /// <summary>E = count x CombatStrength of one ship of the class, computed from ship data, never typed in.</summary>
    public long EconomicStrength(IReadOnlyDictionary<string, ShipClass> ships)
    {
        if (!ships.TryGetValue(EconomicShipClass, out var cls))
            throw new DataLoadException($"{FileName}: economic_blockade_ship_class '{EconomicShipClass}' is not a ship class in the ship data.");
        return EconomicShipCount * CombatStrength.Of(new[] { cls });
    }

    public static BlockadeData Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static BlockadeData Parse(string text, string source = FileName)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("key", "value");
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            var key = t.Get(r, "key").Trim();
            if (!values.TryAdd(key, t.Get(r, "value").Trim())) throw new DataLoadException($"{t.Where(r)}: duplicate key '{key}'.");
        }
        string Need(string k) => values.TryGetValue(k, out var v) ? v : throw new DataLoadException($"{source}: missing key '{k}'.");
        int Int(string k)
        {
            var s = Need(k);
            return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)
                ? i : throw new DataLoadException($"{source}: key '{k}' must be an integer, found '{s}'.");
        }
        return new BlockadeData(Int("forced_stop_threshold_percent"), Need("economic_blockade_ship_class"), Int("economic_blockade_ship_count"));
    }
}
