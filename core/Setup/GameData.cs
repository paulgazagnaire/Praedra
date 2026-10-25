using Praedra.Core.Construction;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Ships;
using Praedra.Core.Support;
using Praedra.Core.Troops;

namespace Praedra.Core.Setup;

/// <summary>
/// Every data file the game loads, read once and immutable: galaxy, ship classes, costs, blockade numbers, garrison weights, slot
/// values, the starting-facilities list, the troop and special-force classes, the repair rules, the New battle tables, the research ladders and rules, the characters and the mission records and tables, and the old-to-new name table for older saves. One place that knows the file names and folders, so a new game, a loaded game, the
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
    DefenseData Defenses,
    Repair.RepairRules Repair,
    Battle.NewBattleData NewBattle,
    Research.ResearchData Research,
    Characters.CharacterData Characters,
    Missions.MissionData Missions,
    NameMigrations Names)
{
    private static readonly Lazy<GameData> _default = new(() => Load(FindRepoRoot()));

    /// <summary>The repository's own data (found by walking up from the program folder to Praedra.sln), loaded once and shared.</summary>
    public static GameData Default => _default.Value;

    /// <summary>Read the data folders under <paramref name="repoRoot"/>: <c>data/design</c> and <c>data/classic</c> (copied from the Classic files; some
    /// original files are merged with the design file of the same name). rule: B1-own-importer §Work 3, 4.</summary>
    public static GameData Load(string repoRoot)
    {
        string design = Path.Combine(repoRoot, "data", "design"), original = Path.Combine(repoRoot, "data", "classic");
        var galaxy = GalaxyLoader.LoadFolder(design);
        var costs = CostData.Load(original, design);
        return new GameData(
            galaxy,
            ShipData.LoadAll(original),
            BlockadeData.Load(Path.Combine(design, BlockadeData.FileName)),
            SlotValues.Load(Path.Combine(design, SlotValues.File), galaxy),
            costs,
            GarrisonWeights.Load(Path.Combine(design, GarrisonWeights.FileName)),
            Construction.StartingFacilities.Load(Path.Combine(design, Construction.StartingFacilities.FileName)),
            TroopData.Load(original, design),
            DefenseData.Load(original, design),
            Praedra.Core.Repair.RepairRules.Load(Path.Combine(design, Praedra.Core.Repair.RepairRules.FileName)),
            Battle.NewBattleData.Load(design),
            Praedra.Core.Research.ResearchData.Load(original, design, costs),
            Praedra.Core.Characters.CharacterData.Load(original, design),
            Praedra.Core.Missions.MissionData.Load(original, design),
            NameMigrations.Load(Path.Combine(design, NameMigrations.FileName)));
    }

    /// <summary>The folder that holds Praedra.sln, searching upwards from the program's folder and then the working folder.</summary>
    public static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Praedra.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Praedra.sln not found above the program or working folder; pass the repository root to GameData.Load.");
    }
}
