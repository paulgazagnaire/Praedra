using Rebellion.Core.Setup;
using Rebellion.Core.Victory;

namespace Rebellion.Core.Tests;

/// <summary>
/// The victory rules of before 2026-10-16 (HQ-only flavour, a dead key character never counts). Tests whose subject is not victory, but which
/// need a game that ends by taking the HQ or Coruscant, pass these explicitly instead of being re-pinned to the new default
/// (StandardVictory on, KeyDeathFallback WaiveCondition; m5-setup-and-victory §Defaults for undecided items).
/// </summary>
public static class OldVictory
{
    public static VictoryRules Rules => new() { StandardVictory = false, KeyDeathFallback = KeyDeathFallback.None };
    public static GameSettings Settings => new() { Victory = Rules };
}
