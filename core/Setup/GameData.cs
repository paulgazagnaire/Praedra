using Rebellion.Core.Construction;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Ships;
using Rebellion.Core.Support;
using Rebellion.Core.Troops;

namespace Rebellion.Core.Setup;

/// <summary>
/// Every data file the game loads, read once and immutable: galaxy, ship classes, costs, blockade numbers, garrison weights, slot
/// values, the starting-facilities list and the troop and special-force classes. One place that knows the file names and folders, so a new game, a loaded game, the
/// runner and the tests all read the same things. Data values come from files, never from code. rule: CLAUDE.md "Every number comes
/// from data or a named constant with its source tag".
/// </summary>
public sealed record GameData(
    Galaxy Galaxy,
    IReadOnlyDictionary<string, ShipClass> Ships,
    BlockadeData Blockade,
    SlotValues Slots,
    CostData Costs,
    GarrisonWeights Weights,
    IReadOnlyList<StartingFacility> StartingFacilities,
    TroopData Troops,
    DefenseData Defenses)
{
    private static readonly Lazy<GameData> _default = new(() => Load(FindRepoRoot()));

    /// <summary>The repository's own data (found by walking up from the program folder to Rebellion.sln), loaded once and shared.</summary>
    public static GameData Default => _default.Value;

    /// <summary>Read the data folders under <paramref name="repoRoot"/>: <c>data/design</c> and <c>data/interim</c>.</summary>
    public static GameData Load(string repoRoot)
    {
        string design = Path.Combine(repoRoot, "data", "design"), interim = Path.Combine(repoRoot, "data", "interim");
        var galaxy = GalaxyLoader.LoadFolder(design);
        return new GameData(
            galaxy,
            ShipData.LoadAll(interim),
            BlockadeData.Load(Path.Combine(design, BlockadeData.FileName)),
            SlotValues.Load(Path.Combine(design, SlotValues.File), galaxy),
            CostData.Load(Path.Combine(interim, CostData.FileName)),
            GarrisonWeights.Load(Path.Combine(design, GarrisonWeights.FileName)),
            Construction.StartingFacilities.Load(Path.Combine(design, Construction.StartingFacilities.FileName)),
            TroopData.Load(interim, design),
            DefenseData.Load(interim));
    }

    /// <summary>The folder that holds Rebellion.sln, searching upwards from the program's folder and then the working folder.</summary>
    public static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Rebellion.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Rebellion.sln not found above the program or working folder; pass the repository root to GameData.Load.");
    }
}
