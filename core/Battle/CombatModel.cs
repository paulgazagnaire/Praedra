namespace Praedra.Core.Battle;

/// <summary>Which battle engine runs when two sides meet. DEFAULT New (Paul, 2026-10-18; Classic = the Classic battle, opt-in). rule: m4-battle-new §Rule (New) (second ruleset).</summary>
public enum BattleRuleset { Classic = 0, New = 1 }

/// <summary>
/// Switches and numbers of the New (per-gun) battle, each with the sheet's DEFAULT and its tag. The weapon table and the per-class numbers are
/// data files (<see cref="NewBattleData"/>); these are the scalar constants of the sheet. rule: m4-battle-new §Numbers, §Defaults for undecided items;
/// "Lead update 2026-10-12" at the top of the sheet.
/// </summary>
public sealed record NewBattleParams
{
    // ---- switches
    /// <summary>Torpedoes may be fired at a shielded capital (x0.5). DEFAULT off: only at capitals whose shield is 0 (original rule). rule: m4-battle-new Lead update 4.</summary>
    public bool TorpedoesVsShields { get; init; }
    /// <summary>A laser shot can break through a shield by the arithmetic test (6 x 0.167 = 1.002 against deflection 1.0). DEFAULT off: lasers never penetrate a capital's shield. rule: m4-battle-new Lead update 3.</summary>
    public bool LaserPenetratesShield { get; init; }
    /// <summary>
    /// Tractor beams hold retreat: a ship with tractor_beam_power &gt; 0 and its tractor counter at 0 holds one smaller enemy ship (lower max hull) that
    /// tries to jump out of the battle, so it stays on the field. New rules only (the Classic engine never reads it; in Classic tractors do not
    /// block retreat). Decided Paul 2026-10-16 (battle only); DEFAULT on. rule: m4-retreat-damage-repair §6.
    /// </summary>
    public bool TractorBeamsHoldRetreat { get; init; } = true;

    // ---- distances and movement. rule: m4-battle-new §3, §5 (Proposal value, tune), Lead update 2
    /// <summary>The start distance is the longest weapon range present + this. Lead.</summary>
    public int StartDistanceMargin { get; init; } = 10;
    public int OffsetHuge { get; init; } = 8;
    public int OffsetLarge { get; init; } = 6;
    public int OffsetMedium { get; init; } = 4;
    public int OffsetSmall { get; init; } = 2;
    /// <summary>Share of its in-arc dps the batteries that set the reach must hold. Decided (space-combat/11 section 4 (2), 2026-10-12).</summary>
    public double ReachDpsShare { get; init; } = 0.9;
    /// <summary>Cap of the pair offsets taken off the reach. Decided (space-combat/11 section 4 (2)).</summary>
    public int ReachOffsetCap { get; init; } = 12;
    /// <summary>Margin taken off the reach. Decided (space-combat/11 section 4 (2)).</summary>
    public int ReachMargin { get; init; } = 2;
    /// <summary>The faster side's slowest sublight must be at least this many points above the other's to kite. Decided (space-combat/11 section 4 (4)).</summary>
    public int KiteSpeedEdge { get; init; } = 1;
    /// <summary>The chaser of a kite closes at this share of its closing speed. Decided (space-combat/11 section 4 (4)).</summary>
    public double KiteChaserFactor { get; init; } = 0.5;
    /// <summary>Seconds without armed contact before both sides close at full speed. Decided (space-combat/11 section 4 (6)).</summary>
    public int NoFireSeconds { get; init; } = 20;
    /// <summary>Surround distance, and the distance the no-fire safety closes to. Decided (space-combat/11 section 4 (6), (7)).</summary>
    public int SurroundDistance { get; init; } = 15;
    /// <summary>Fleet distance moves by k x slowest sublight of the closing fleet per second. Decided 1.5 (space-combat/11 section 4 (5)), tunable.</summary>
    public double ClosingK { get; init; } = 1.5;
    /// <summary>Pair range of a squadron and the unit it attacks. DEFAULT.</summary>
    public int SquadronRange { get; init; } = 5;

    // ---- facing and targets. rule: m4-battle-new §6, §7
    /// <summary>Turn time is this / maneuver seconds. Proposal value (tune).</summary>
    public int TurnTime { get; init; } = 10;
    /// <summary>Switch facing only if the other is at least this many times better. Proposal value (tune).</summary>
    public double FacingHysteresis { get; init; } = 1.3;
    /// <summary>A target is kept for this many seconds. Proposal value (tune).</summary>
    public int CommitSeconds { get; init; } = 5;
    /// <summary>Horizon of the expected damage in the target score, seconds. DEFAULT (tune).</summary>
    public int ExpectedDamageHorizon { get; init; } = 10;
    /// <summary>StandOff saturation window and the torpedo window of CombatStrength, seconds. DEFAULT (tune).</summary>
    public int KillWindow { get; init; } = 180;

    // ---- shields, fighters. rule: m4-battle-new §9, §10
    /// <summary>Seconds after the last hit before shields regenerate. Proposal value.</summary>
    public int ShieldRegenPause { get; init; } = 5;
    /// <summary>shieldFactor = base + (1 - base) x shield / pool. DEFAULT (tune).</summary>
    public double ShieldFactorBase { get; init; } = 0.5;
    public int LaunchSecond { get; init; } = 5;
    public int FlightSeconds { get; init; } = 6;
    public int SquadronCraft { get; init; } = 12;
    public int CraftHull { get; init; } = 8;
    /// <summary>A squadron with at least this many craft alive counts as whole at the end. DEFAULT.</summary>
    public int SquadronSurvivalCraft { get; init; } = 6;

    // ---- subsystem rolls. rule: m4-battle-new §8 step 4b.6, Random draws (the thresholds 50/65/80/90/95 and the cap are BattleParams', shared with Classic)
    /// <summary>The shield-generator draw is NextInt(100). Traced idea.</summary>
    public int ShieldRollSides { get; init; } = 100;
    /// <summary>The hull draw is NextInt(101). Traced idea.</summary>
    public int HullRollSides { get; init; } = 101;
    /// <summary>Minimum percent chance of a shield-generator hit. Traced-medium.</summary>
    public double ShieldGeneratorMinPercent { get; init; } = 1;

    // ---- retreat and end. rule: m4-battle-new §11
    /// <summary>Morale retreat when own / enemy strength is below this. Traced.</summary>
    public double RetreatThreshold { get; init; } = 0.33;
    /// <summary>Seconds a retreating unit stays hittable before it leaves. Traced idea.</summary>
    public int JumpSeconds { get; init; } = 5;
    public int StalemateSeconds { get; init; } = 1200;
    /// <summary>Hard cap of a battle in seconds. Decided (original cap).</summary>
    public int TickCap { get; init; } = 1800;
    /// <summary>Strength lows are compared rounded to this step. DEFAULT.</summary>
    public double StalemateRounding { get; init; } = 0.01;
}

/// <summary>
/// A ship or squadron as the strength measure sees it: class, hull ratio, weapons counter (ships), living craft and torpedoes (squadrons), and the
/// shield state (as an attacker: <see cref="ShieldFactor"/>; as a target: <see cref="ShieldUp"/>). rule: m4-battle-new §12.
/// </summary>
public readonly struct Combatant
{
    public CombatClass Class { get; init; }
    public double HullRatio { get; init; }
    public double ShieldFactor { get; init; }
    public bool ShieldUp { get; init; }
    /// <summary>hits[weapons]; 0 for a squadron.</summary>
    public int WeaponHits { get; init; }
    /// <summary>Living craft of a squadron (the gun count of its batteries).</summary>
    public int Craft { get; init; }
    public int Torpedoes { get; init; }

    /// <summary>The strategic view: shields full (so a class with a shield pool is "shield up"), shield factor 1, a squadron whole with its torpedoes. rule: m4-battle-new §12.</summary>
    public static Combatant Strategic(CombatClass c, double hullRatio = 1.0, int weaponHits = 0, int craft = 12) => new()
    {
        Class = c, HullRatio = hullRatio, ShieldFactor = 1.0, ShieldUp = c.ShieldPool > 0, WeaponHits = c.IsSquadron ? 0 : weaponHits,
        Craft = c.IsSquadron ? craft : 0, Torpedoes = c.Torpedoes,
    };
}

/// <summary>
/// The damage arithmetic shared by the battle and CombatStrength: guns, multipliers, deflection, hit chance, dps, and the strength measure itself.
/// Floating point is used where the sheet does, in a fixed evaluation order (no parallelism, only + - * / floor min max). One instance per user:
/// the lookup cache is not thread-safe. rule: m4-battle-new §1, §2, §8, §12.
/// </summary>
public sealed class CombatModel
{
    public NewBattleData Data { get; }
    public NewBattleParams P { get; }
    private readonly Dictionary<long, double> _facing = new(); // lookup only, never iterated
    private readonly double[]?[] _per;

    public CombatModel(NewBattleData data, NewBattleParams? p = null)
    {
        Data = data; P = p ?? new NewBattleParams();
        _per = new double[data.ClassList.Count * data.ClassList.Count * 2][];
    }

    /// <summary>guns_active = floor(guns x (1 - 0.25 x hits[weapons])), four hits = none; a squadron fires one gun per living craft. rule: m4-battle-new §1.</summary>
    public int GunsActive(Battery b, in Combatant a)
    {
        if (a.Class.IsSquadron) return a.Craft;
        int h = a.WeaponHits;
        return h >= 4 ? 0 : b.Guns * (4 - h) / 4; // integer form of floor(guns * (1 - 0.25 * h))
    }

    /// <summary>min(1, shooter maneuver / target maneuver), maneuver floor 1. rule: m4-battle-new §2 (Traced idea, space-combat/02).</summary>
    public double ManeuverRatio(CombatClass shooter, CombatClass target) => Math.Min(1.0, (double)shooter.ManeuverOrOne / target.ManeuverOrOne);

    public double HitChance(WeaponKind w, CombatClass target) => Data.Weapon(w).Hit[(int)target.Size];

    /// <summary>
    /// Damage of one shot (before the shooter's hull ratio) after the weapon multiplier of the layer it meets first; 0 when it cannot hurt: a laser at a
    /// shielded capital (Lead update 3), a torpedo at a shielded capital (Lead update 4), a shot at or below the target's deflection, an ion or torpedo at
    /// a squadron. Against a squadron the maneuver ratio applies to the weapons that have it. rule: m4-battle-new §2, §8 (dps), Lead updates 3 and 4.
    /// </summary>
    public double ShotValue(WeaponKind w, double shotDamage, CombatClass shooter, in Combatant target)
    {
        var row = Data.Weapon(w);
        var tc = target.Class;
        if (tc.IsSquadron)
        {
            double m = row.VsFighter;
            if (m <= 0) return 0;
            if (row.FighterUsesManeuverRatio) m *= ManeuverRatio(shooter, tc);
            return shotDamage * m;
        }
        if (target.ShieldUp)
        {
            if (w == WeaponKind.LaserCannon && !P.LaserPenetratesShield) return 0;
            if (w == WeaponKind.Torpedo && !P.TorpedoesVsShields) return 0;
            double x = shotDamage * row.VsShield;
            return x <= tc.Deflection ? 0 : x; // deflected: no damage (class deflection column)
        }
        return shotDamage * row.VsHull;
    }

    /// <summary>
    /// dps(u, target, arcs): sum over the batteries in <paramref name="arcMask"/> (bit per <see cref="Arc"/>) of guns_active x shot value x hit chance / reload.
    /// With <paramref name="pairRange"/> below 0 range is ignored; otherwise only batteries with range at least that far count. No hull ratio. rule: m4-battle-new §8.
    /// </summary>
    public double ArcsDps(in Combatant a, in Combatant t, int arcMask, double pairRange = -1)
    {
        var per = PerBattery(a.Class, t); // shot value x hit chance / reload of each battery against this kind of target (class and shield state only)
        double sum = 0;
        var batteries = a.Class.Batteries;
        for (int i = 0; i < batteries.Count; i++)
        {
            var b = batteries[i];
            if (!a.Class.IsSquadron && (arcMask & (1 << (int)b.Arc)) == 0) continue; // squadrons ignore arcs
            if (pairRange >= 0 && pairRange > b.Range) continue;
            double v = per[i];
            if (v <= 0) continue;
            int guns = GunsActive(b, a);
            if (guns <= 0) continue;
            sum += guns * v;
        }
        return sum;
    }

    /// <summary>Appends (range, dps x weight) of every battery in <paramref name="arcMask"/> that can hurt t and has guns active, in battery order. No hull ratio. rule: space-combat/11 section 4 (2).</summary>
    public void BatteryDps(in Combatant a, in Combatant t, int arcMask, List<(int Range, double Dps)> into, double weight = 1.0)
    {
        var per = PerBattery(a.Class, t);
        var batteries = a.Class.Batteries;
        for (int i = 0; i < batteries.Count; i++)
        {
            var b = batteries[i];
            if ((arcMask & (1 << (int)b.Arc)) == 0 || per[i] <= 0) continue;
            int guns = GunsActive(b, a);
            if (guns > 0) into.Add((b.Range, guns * per[i] * weight));
        }
    }

    /// <summary>True when some battery of the class can hurt a target of this class and shield state at any range and in any arc (a quick filter; static per class pair).</summary>
    public bool CanHurt(CombatClass shooter, in Combatant t)
    {
        foreach (var v in PerBattery(shooter, t)) if (v > 0) return true;
        return false;
    }

    private double[] PerBattery(CombatClass shooter, in Combatant t)
    {
        int key = (shooter.Index * Data.ClassList.Count + t.Class.Index) * 2 + (t.ShieldUp ? 1 : 0);
        var cached = _per[key];
        if (cached != null) return cached;
        var arr = new double[shooter.Batteries.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            var b = shooter.Batteries[i];
            double v = ShotValue(b.Weapon, b.ShotDamage, shooter, t);
            arr[i] = v <= 0 ? 0 : v * HitChance(b.Weapon, t.Class) / b.Reload;
        }
        _per[key] = arr;
        return arr;
    }

    public const int ForeMask = 1 << (int)Arc.Fore, PortMask = 1 << (int)Arc.Port, StarboardMask = 1 << (int)Arc.Starboard, AftMask = 1 << (int)Arc.Aft;

    /// <summary>Expected damage of the squadron's torpedoes still carried against t (all hit, one salvo): torpedoes x damage x multiplier x hit chance. rule: m4-battle-new §7, §12.</summary>
    public double TorpedoExpected(in Combatant a, in Combatant t)
    {
        if (a.Torpedoes <= 0 || !a.Class.IsSquadron) return 0;
        double v = ShotValue(WeaponKind.Torpedo, a.Class.TorpedoDamage, a.Class, t);
        return v <= 0 ? 0 : a.Torpedoes * v * HitChance(WeaponKind.Torpedo, t.Class);
    }

    /// <summary>
    /// facingDps: a ship's best facing (fore, or the better of port and starboard); a squadron's fore dps plus its torpedoes as damage over the
    /// torpedo window. Ignores range. rule: m4-battle-new §12.
    /// </summary>
    public double FacingDps(in Combatant a, in Combatant t)
    {
        int n = Data.ClassList.Count;
        long key = ((((long)a.Class.Index * 16 + Math.Min(a.WeaponHits, 15)) * n + t.Class.Index) * 2 + (t.ShieldUp ? 1 : 0)) * 16 + Math.Min(a.Craft, 15);
        key = key * 16 + Math.Min(a.Torpedoes, 15);
        if (_facing.TryGetValue(key, out var cached)) return cached;
        double r;
        if (a.Class.IsSquadron)
            r = ArcsDps(a, t, ForeMask) + TorpedoExpected(a, t) / P.KillWindow;
        else
        {
            double fore = ArcsDps(a, t, ForeMask), port = ArcsDps(a, t, PortMask), star = ArcsDps(a, t, StarboardMask);
            r = Math.Max(fore, Math.Max(port, star));
        }
        _facing[key] = r;
        return r;
    }

    /// <summary>shieldFactor = base + (1 - base) x shield / pool; 1 when the pool is 0. rule: m4-battle-new §1 (DEFAULT).</summary>
    public double ShieldFactor(CombatClass c, double shield) =>
        c.ShieldPool <= 0 ? 1.0 : P.ShieldFactorBase + (1.0 - P.ShieldFactorBase) * shield / c.ShieldPool;

    /// <summary>
    /// CombatStrength(own, versus): 0 if versus is empty; else the sum over own units of d(u) x hullRatio x shieldFactor with
    /// d(u) = sum over targets of cost(t) x facingDps(u, t) / W, W = sum of target costs. Targets of one class and shield state are summed first
    /// (same value, fewer evaluations). Own units "on the field" only: the caller leaves out jumping units. rule: m4-battle-new §12.
    /// </summary>
    public double Strength(IReadOnlyList<Combatant> own, IReadOnlyList<Combatant> versus)
    {
        if (versus.Count == 0) return 0;
        var groups = new List<(Combatant Rep, double Cost)>();
        double w = 0;
        foreach (var t in versus)
        {
            w += t.Class.Cost;
            int g = -1;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Rep.Class.Index == t.Class.Index && groups[i].Rep.ShieldUp == t.ShieldUp) { g = i; break; }
            if (g < 0) groups.Add((t, t.Class.Cost));
            else groups[g] = (groups[g].Rep, groups[g].Cost + t.Class.Cost);
        }
        if (w <= 0) return 0;
        double total = 0;
        foreach (var u in own)
        {
            double d = 0;
            foreach (var (rep, cost) in groups) d += cost * FacingDps(u, rep) / w;
            total += d * u.HullRatio * u.ShieldFactor;
        }
        return total;
    }
}
