using Rebellion.Core.Ships;

namespace Rebellion.Core.Battle;

/// <summary>
/// The 1998 AI's strength number ("firepower", class value 0x4739b0, ship stat 14 at 0x484630): 100 times the B3 battle strength against capital ships,
/// in integer steps. Hull damage, shields, planet defences and planet squadrons are not in it. rule: B6a §3 [EXE-own].
/// </summary>
public static class AiFirepower
{
    /// <summary>Weapon value per gun type in an arc (100 per turbolaser or ion cannon, 100 / 6 per laser cannon, integer). rule: B6a §3.</summary>
    public const int GunValue = 100;
    /// <summary>The laser cannon divisor. rule: B6a §3.</summary>
    public const int LaserDivisor = 6;
    /// <summary>Weapon hits take a quarter each off the recharge rate (B3, 0..4 hits). rule: B6a §3.</summary>
    public const int WeaponHitDivisor = 4;
    /// <summary>The weapons counter in a ship's hits (shield gen, weapons, tractor, sub-light, hyperdrive, backup). rule: m4-battle-classic §7.</summary>
    public const int WeaponsCounter = 1;

    /// <summary>
    /// A capital ship: the arc (fore, aft, port, starboard; first wins ties) with the largest v = 100 TL + 100 ION + (100 LC) / 6, g its gun count;
    /// w = rate - rate x hits / 4; firepower = w x v / g (0 if g = 0); a ship not completed adds 0. rule: B6a §3.
    /// </summary>
    public static int Ship(ShipClass c, int weaponHits, bool completed)
    {
        if (!completed) return 0;
        int bestV = -1, bestG = 0;
        for (int i = 0; i < 4; i++) // ShipArcs index order: fore, aft, port, starboard
        {
            var a = c.GunArcs[i];
            int v = GunValue * a.Turbolaser + GunValue * a.Ion + GunValue * a.Laser / LaserDivisor;
            if (v > bestV) { bestV = v; bestG = a.Total; }
        }
        if (bestG == 0) return 0;
        int hits = Math.Clamp(weaponHits, 0, WeaponHitDivisor);
        int w = c.WeaponRechargeRate - c.WeaponRechargeRate * hits / WeaponHitDivisor;
        return w * bestV / bestG;
    }

    /// <summary>A fighter class's value on its fore arc: size x (100 ION + (100 LC) / 6) / (ION + LC) (0x4736c0). rule: B6a §3.</summary>
    public static int FighterValue(ShipClass c, int size)
    {
        var a = c.GunArcs.Fore;
        int g = a.Ion + a.Laser;
        return g == 0 ? 0 : size * (GunValue * a.Ion + GunValue * a.Laser / LaserDivisor) / g;
    }

    /// <summary>A carried squadron that is completed and not en route: fighter value x alive craft / size (integer). rule: B6a §3.</summary>
    public static int Squadron(ShipClass c, int size, int alive, bool completed) =>
        !completed || size <= 0 ? 0 : FighterValue(c, size) * Math.Clamp(alive, 0, size) / size;
}
