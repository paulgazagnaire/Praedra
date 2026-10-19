using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Ships;

namespace Praedra.Core.Battle;

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
                    else { u.HullMax = u.Hull = ClassicRates.SquadronHullMax(p, cls); u.Shield = cls.ShieldStrength; } // whole squadron: squadron_size x 8 (96)
                    list.Add(u);
                }
            }
        return list;
    }

    /// <summary>
    /// Run one battle. Alliance units come first. With ClassicBattleRng = Game the battle generator is seeded with <paramref name="seed"/> (Classic ignores it).
    /// <paramref name="alert"/> null = sides without a controller (threshold <see cref="BattleParams.RetreatThreshold"/>); <paramref name="ai"/> marks AI sides. <see cref="BattleParams.Ruleset"/> picks the
    /// engine; for New, unit ids are interleaved between the sides (neither is processed first, m4-battle-new §3.2), <paramref name="carry"/> puts each side's
    /// squadrons aboard its carriers in id order (else they are based), and <paramref name="prepare"/> may change the units (hull, counters) before the start.
    /// </summary>
    public static (ClassicBattleResult Result, IReadOnlyList<BattleUnit> Units) Run(BattleParams p, IReadOnlyDictionary<string, ShipClass> classes,
        IEnumerable<(string Class, int Count)> alliance, IEnumerable<(string Class, int Count)> empire, ulong seed,
        bool allianceCanLand = true, bool empireCanLand = true, OnContactMode[]? alert = null,
        NewBattleData? data = null, bool carry = false, Action<IReadOnlyList<BattleUnit>>? prepare = null, bool[]? ai = null)
    {
        if (p.Ruleset == BattleRuleset.New)
        {
            var (nb, units) = PrepareNew(p, classes, alliance, empire, seed, allianceCanLand, empireCanLand, alert, data, carry, prepare);
            return (nb.Run(), units);
        }
        int id = 1;
        var a = Units(p, classes, Side.Alliance, alliance, ref id);
        var e = Units(p, classes, Side.Empire, empire, ref id, a.Count);
        var all = a.Concat(e).ToList();
        var hasDest = new bool[2];
        hasDest[(int)Side.Alliance] = allianceCanLand; hasDest[(int)Side.Empire] = empireCanLand;
        if (carry) { Carry(a); Carry(e); }
        prepare?.Invoke(all);
        var battle = new ClassicBattle(p, all, new GameRng(seed), hasDest, alert, ai);
        return (battle.Run(), all);
    }

    /// <summary>The New battle ready to run (so a caller can set the tactic, the tick hook or the event log first). See <see cref="Run"/> for the parameters.</summary>
    public static (NewBattle Battle, IReadOnlyList<BattleUnit> Units) PrepareNew(BattleParams p, IReadOnlyDictionary<string, ShipClass> classes,
        IEnumerable<(string Class, int Count)> alliance, IEnumerable<(string Class, int Count)> empire, ulong seed,
        bool allianceCanLand = true, bool empireCanLand = true, OnContactMode[]? alert = null,
        NewBattleData? data = null, bool carry = false, Action<IReadOnlyList<BattleUnit>>? prepare = null, bool logEvents = false)
    {
        int id = 1;
        var a = Units(p, classes, Side.Alliance, alliance, ref id);
        var e = Units(p, classes, Side.Empire, empire, ref id, a.Count);
        var all = a.Concat(e).ToList();
        var hasDest = new bool[2];
        hasDest[(int)Side.Alliance] = allianceCanLand; hasDest[(int)Side.Empire] = empireCanLand;
        Interleave(a, e);
        if (carry) { Carry(a); Carry(e); }
        prepare?.Invoke(all);
        return (new NewBattle(p, data ?? NewBattleData.Default, all, new GameRng(seed), hasDest, alert, logEvents), all);
    }

    // rule: m4-battle-new §3.2: ids in creation order, both sides interleaved (ships first, then squadrons)
    private static void Interleave(List<BattleUnit> a, List<BattleUnit> e)
    {
        int next = 1;
        foreach (bool capital in new[] { true, false })
        {
            var la = a.Where(u => u.IsCapital == capital).ToList(); var le = e.Where(u => u.IsCapital == capital).ToList();
            for (int i = 0; i < Math.Max(la.Count, le.Count); i++)
            {
                if (i < la.Count) la[i].ObjectId = next++;
                if (i < le.Count) le[i].ObjectId = next++;
            }
        }
    }

    // squadrons ride the side's carriers in id order, up to each carrier's berths (the rest stay based)
    private static void Carry(List<BattleUnit> side)
    {
        var carriers = side.Where(u => u.IsCapital && u.Class.FighterCapacity > 0).OrderBy(u => u.ObjectId).ToList();
        var used = new Dictionary<int, int>(); // lookup only
        foreach (var s in side.Where(u => !u.IsCapital).OrderBy(u => u.ObjectId))
        {
            var c = carriers.FirstOrDefault(x => used.GetValueOrDefault(x.ObjectId) < x.Class.FighterCapacity);
            if (c == null) break;
            used[c.ObjectId] = used.GetValueOrDefault(c.ObjectId) + 1;
            s.Carrier = c.ObjectId;
        }
    }
}
