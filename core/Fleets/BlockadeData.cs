using Praedra.Core.Battle;
using Praedra.Core.Data;
using Praedra.Core.Ships;

namespace Praedra.Core.Fleets;

/// <summary>
/// Which enemy forces stop a passing fleet. A game setting, not code.
/// rule: m1-forced-stop-and-blockade §Pull-out (Decided Paul 2026-10-16: GravityWellShip is the default; the other two values are the
/// retired 2026-10-12 rules, kept as switches for comparison).
/// </summary>
public enum ForcedStopTrigger
{
    /// <summary>
    /// Retired 2026-10-16 (was the default from 2026-10-12). Stop if the enemy side at the system has a live gravity-well ship, or its combat strength
    /// there is at least T times the passing fleet's combat strength.
    /// </summary>
    PaulRule = 0,
    /// <summary>Any armed enemy fleet at the system stops the passing fleet. Earlier default (planets/10 round 2).</summary>
    AnyArmedFleet = 1,
    /// <summary>DEFAULT (Paul 2026-10-16). Only an enemy fleet with a ship that has gravity_well_projector above 0 pulls the passing fleet out of hyperspace.</summary>
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
    /// <summary>The fixed reference force E is measured against: this ship class ... DEFAULT Imperial Star Destroyer (m4-battle-new §12, open question 3).</summary>
    public string ReferenceShipClass { get; }
    /// <summary>... this many of it. DEFAULT 3. rule: m4-battle-new §12.</summary>
    public int ReferenceShipCount { get; }

    public BlockadeData(int thresholdPercent, string economicShipClass, int economicShipCount,
        string referenceShipClass = "Imperial Star Destroyer", int referenceShipCount = 3)
    {
        if (thresholdPercent < 0) throw new DataLoadException("blockade: forced_stop_threshold_percent must not be negative.");
        if (economicShipCount < 0) throw new DataLoadException("blockade: economic_blockade_ship_count must not be negative.");
        ThresholdPercent = thresholdPercent;
        EconomicShipClass = economicShipClass;
        EconomicShipCount = economicShipCount;
        if (referenceShipCount < 1) throw new DataLoadException("blockade: economic_blockade_reference_count must be at least 1.");
        ReferenceShipClass = referenceShipClass; ReferenceShipCount = referenceShipCount;
    }

    /// <summary>The reference force REF as classes (from ship data). rule: m4-battle-new §12 (REF = 3 ISD, DEFAULT).</summary>
    public IReadOnlyList<ShipClass> Reference(IReadOnlyDictionary<string, ShipClass> ships)
    {
        if (!ships.TryGetValue(ReferenceShipClass, out var cls))
            throw new DataLoadException($"{FileName}: economic_blockade_reference_class '{ReferenceShipClass}' is not a ship class in the ship data.");
        return Enumerable.Repeat(cls, ReferenceShipCount).ToList();
    }

    /// <summary>
    /// E = CombatStrength(count x the class, REF), computed from ship data, never typed in. rule: m4-battle-new §12 (E = CS(3 Victory Destroyers, 3 ISD) = 64.8).
    /// </summary>
    public double EconomicStrength(IReadOnlyDictionary<string, ShipClass> ships, CombatStrength? strength = null)
    {
        if (!ships.TryGetValue(EconomicShipClass, out var cls))
            throw new DataLoadException($"{FileName}: economic_blockade_ship_class '{EconomicShipClass}' is not a ship class in the ship data.");
        strength ??= new CombatStrength(NewBattleData.Default);
        return strength.Of(Enumerable.Repeat(cls, EconomicShipCount), Reference(ships));
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
        // the reference force is optional in the file (DEFAULT 3 Imperial Star Destroyers). rule: m4-battle-new §12
        string refClass = values.TryGetValue("economic_blockade_reference_class", out var rc) ? rc : "Imperial Star Destroyer";
        int refCount = values.ContainsKey("economic_blockade_reference_count") ? Int("economic_blockade_reference_count") : 3;
        return new BlockadeData(Int("forced_stop_threshold_percent"), Need("economic_blockade_ship_class"), Int("economic_blockade_ship_count"), refClass, refCount);
    }
}
