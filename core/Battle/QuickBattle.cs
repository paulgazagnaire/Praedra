using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Ships;

namespace Rebellion.Core.Battle;

/// <summary>
/// A Classic battle between two compositions of ship classes with no game around it (synthetic system: every retreat has a destination unless
/// told otherwise). Used by the runner's <c>battle</c> command and by tests. Squadrons are based (no carrier). rule: m4-battle-classic §2, §7.
/// </summary>
public static class QuickBattle
{
    /// <summary>Make the units of one side: capital ships first, then squadrons, in the given order; object ids start at <paramref name="firstId"/>.</summary>
    public static List<BattleUnit> Units(BattleParams p, IReadOnlyDictionary<string, ShipClass> classes, Side side,
        IEnumerable<(string Class, int Count)> composition, ref int firstId, int indexOffset = 0)
    {
        var comp = composition.ToList();
        var list = new List<BattleUnit>();
        foreach (bool capital in new[] { true, false })
            foreach (var (name, count) in comp)
            {
                if (!classes.TryGetValue(name, out var cls)) throw new ArgumentException($"Unknown ship class '{name}'.");
                if (cls.IsCapital != capital) continue;
                for (int i = 0; i < count; i++)
                {
                    var u = new BattleUnit { Index = indexOffset + list.Count, ObjectId = firstId++, Side = side, Class = cls, IsCapital = capital };
                    if (capital) { u.HullMax = u.Hull = cls.Hull; u.Shield = cls.ShieldStrength; }
                    else { u.HullMax = u.Hull = p.SquadronHull; u.Shield = cls.ShieldStrength; }
                    list.Add(u);
                }
            }
        return list;
    }

    /// <summary>Run one battle. Alliance units come first. The battle generator is seeded with <paramref name="seed"/>.</summary>
    public static (ClassicBattleResult Result, IReadOnlyList<BattleUnit> Units) Run(BattleParams p, IReadOnlyDictionary<string, ShipClass> classes,
        IEnumerable<(string Class, int Count)> alliance, IEnumerable<(string Class, int Count)> empire, ulong seed,
        bool allianceCanLand = true, bool empireCanLand = true, OnContactMode[]? alert = null)
    {
        int id = 1;
        var a = Units(p, classes, Side.Alliance, alliance, ref id);
        var e = Units(p, classes, Side.Empire, empire, ref id, a.Count);
        var all = a.Concat(e).ToList();
        var hasDest = new bool[2];
        hasDest[(int)Side.Alliance] = allianceCanLand; hasDest[(int)Side.Empire] = empireCanLand;
        var battle = new ClassicBattle(p, all, new GameRng(seed), hasDest, alert);
        return (battle.Run(), all);
    }
}
