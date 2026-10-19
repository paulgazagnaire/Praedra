using Praedra.Core.Clock;

namespace Praedra.Core.Repair;

/// <summary>
/// Slot 6.8. A ship had a repair turn this day: Hull is the new hull, HitsRemaining the sum of its six hit counters after the turn.
/// Day is the hook time T = K + 1. rule: m4-retreat-damage-repair §Where it runs and what it draws (ShipRepaired).
/// </summary>
public sealed record ShipRepaired(int Index, int Day, int Ship, int Hull, int HitsRemaining)
    : GameEvent(Index, Day, nameof(ShipRepaired));

/// <summary>Slot 6.8. A squadron got one craft back: CraftLost is what it still misses. rule: m4-retreat-damage-repair §5 (squadron, per turn).</summary>
public sealed record SquadronRepaired(int Index, int Day, int Squadron, int CraftLost)
    : GameEvent(Index, Day, nameof(SquadronRepaired));
