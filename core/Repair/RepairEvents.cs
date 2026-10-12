using Rebellion.Core.Clock;

namespace Rebellion.Core.Repair;

/// <summary>
/// Slot 6.8. A ship was repaired this day: Hull is the new hull, HitsRemaining the sum of its six hit counters after the repair.
/// Day is the hook time T = K + 1. rule: m4-retreat-damage-repair §Where it runs and what it draws (ShipRepaired).
/// </summary>
public sealed record ShipRepaired(int Index, int Day, int Ship, int Hull, int HitsRemaining)
    : GameEvent(Index, Day, nameof(ShipRepaired));
