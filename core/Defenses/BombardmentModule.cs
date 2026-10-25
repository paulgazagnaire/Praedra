using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;

namespace Praedra.Core.Defenses;

/// <summary>What the player chose to shell. Classic order names: "Target Military Facilities", "Target Civilian Facilities", "General Bombardment", "Destroy System"
/// (codes 0x220, 0x221, 0x222, 0x223, Traced). rule: m3-bombardment §Order and preconditions.</summary>
public enum BombardCategory { Military = 0, Civilian = 1, General = 2, DestroySystem = 3 }

/// <summary>
/// Orbital bombardment: fleet <paramref name="Fleet"/> shells <paramref name="System"/>. Checked in step 1 (rejection) and again at slot 5.1
/// (cancellation). The handler lives in <see cref="BombardmentModule"/>. rule: m3-bombardment §Order and preconditions.
/// </summary>
public sealed record BombardOrder(long Sequence, Side Side, int Fleet, string System, BombardCategory Category) : Order(Sequence, Side);

/// <summary>
/// Numbers and switches of the bombardment sheet, with tags. rule: m3-bombardment §Numbers; B7-bombardment §7 (every number [EXE-own] / [DAT-own]),
/// §Collisions (the switches: Dev lead decision 2026-10-19, DEFAULTs below are the same in Classic and New).
/// </summary>
public sealed record BombardParams
{
    /// <summary>Admiral step: each full this-many points of leadership add one to the fleet's power multiplier (L / 40 + 1, integer). GNPRTB 1537, 0x55e950.</summary>
    public int AdmiralStep { get; init; } = 40;
    /// <summary>General step of the batteries' return fire: damage x (L / 40 + 1), integer. GNPRTB 1536, 0x55e930. rule: B7-bombardment §4.3.</summary>
    public int GeneralStep { get; init; } = 40;
    /// <summary>The per-hit die is rand(DieMin..DieMax); the target is destroyed when the roll beats its defence. GNPRTB 1538, 1539 (0x55e970).</summary>
    public int DieMin { get; init; } = 1;
    public int DieMax { get; init; } = 10;
    /// <summary>Bombardment defence of the system's Energy and Raw entries (a 10 percent chance each hit). GNPRTB 1540, 1541.</summary>
    public int EnergyDefense { get; init; } = 9;
    public int RawDefense { get; init; } = 9;
    /// <summary>Support change for the bomber when a civilian facility, an energy or a raw slot was destroyed, once per strike. GNPRTB 7705. rule: B7-bombardment §6.</summary>
    public int CivilianSupportChange { get; init; } = -20;

    // ---- return fire: the critical-hit roll (B7-bombardment §4.4.5, 0x5585e0, 0x5018c0)
    /// <summary>Cap of every system counter raised by a critical hit, and the 4 of the current-shield formula S x (4 - c) / 4. GNPRTB 2048.</summary>
    public int CriticalCap { get; init; } = 4;
    /// <summary>The critical roll is rand(0..this) + pct(hull lost, hull lost + hull left). GNPRTB 2049.</summary>
    public int CriticalRollMax { get; init; } = 50;
    /// <summary>A critical roll of this or more hits a system. GNPRTB 2050.</summary>
    public int CriticalAt { get; init; } = 50;
    /// <summary>The system is CSCRHTTB(rand(0..this)). GNPRTB 2051.</summary>
    public int CriticalTableMax { get; init; } = 50;
    /// <summary>CSCRHTTB [DAT-own] (rows from thresholds 0, 16, 31, 41, 46): 0-15 shield recharge, 16-30 weapons, 31-40 Hold beam, 41-45 speed, 46-50 hyperdrive.</summary>
    public int CriticalShieldUpTo { get; init; } = 15;
    public int CriticalWeaponsUpTo { get; init; } = 30;
    public int CriticalHoldBeamUpTo { get; init; } = 40;
    public int CriticalSpeedUpTo { get; init; } = 45;

    // ---- switches (B7-bombardment §Collisions; Dev lead 2026-10-19)
    /// <summary>
    /// C1: the targets are counted once before the first hit; each hit picks t = rand(0..m0 - 1) and is wasted past the targets still standing.
    /// Off = the m3 rule (t over the live pool, no hit wasted). DEFAULT on in both modes. rule: B7-bombardment §5.4.
    /// </summary>
    public bool BombardWastedHits { get; init; } = true;
    /// <summary>
    /// C2: every working Mordant Lien-150 and LNR fires one shot at a random bombarding capital ship before any bomb falls. Off = the m3 hook event only.
    /// DEFAULT on in both modes. rule: B7-bombardment §4.
    /// </summary>
    public bool BatteryReturnFire { get; init; } = true;
    /// <summary>
    /// C3: all of a side's flagged fleets at a system strike together, each with its own Admiral; the shields subtract once from the total. Off = one
    /// strike per fleet. DEFAULT on in both modes. rule: B7-bombardment §1, §5.1, §5.2.
    /// </summary>
    public bool BombardFleetsTogether { get; init; } = true;
    /// <summary>
    /// C4: a ship counts bm - bm x hull damage / hull and a squadron v - v x lost craft / squadron size (truncated). Off = full value. DEFAULT on in
    /// both modes. rule: B7-bombardment §5.1.
    /// </summary>
    public bool BombardDamageScales { get; init; } = true;
    /// <summary>
    /// C6: a military strike with hits has one chance in (targets + 1) to roll on the first civilian facility (the Classic pick bug reproduced). Off = no
    /// collateral. DEFAULT on in both modes. rule: B7-bombardment §5.3.
    /// </summary>
    public bool MilitaryCollateral { get; init; } = true;
    /// <summary>
    /// C9: the Hearth is a Military target (defence ALLFACSD word 12 = 9). DEFAULT off in both modes: it collides with the protected movable HQ and
    /// what a destroyed HQ does was not read (B7-bombardment §10); with it on, a destroyed HQ calls <see cref="BombardmentModule.HqDestroyed"/>,
    /// which throws until that effect is traced. rule: B7-bombardment §3, §Collisions C9.
    /// </summary>
    public bool HqBombardable { get; init; }
}


/// <summary>
/// The Bombard order and its resolution at work-list slot 5.1. Holds settings only; the orders accepted this Step wait in a list that lives
/// from step 1 to slot 5.1 of the same Step (so it is empty whenever a game is saved). Attach after the fleet, economy and support modules.
/// One strike runs the four phases of B7-bombardment §1: counts, return fire, hits, aftermath. All draws come from the one game generator, in the
/// order of B7-bombardment §8 (return-fire targets, critical rolls, military collateral, then per hit t and r; over-capacity picks of a hit
/// after its r). rule: m3-bombardment (the whole sheet); B7-bombardment (settles its medium-low parts).
/// </summary>
public sealed class BombardmentModule
{
    private readonly Game _game;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;
    private readonly SupportModule _support;
    private readonly DefenseModule _def;
    private readonly List<BombardOrder> _pending = new(); // accepted this Step; consumed at slot 5.1

    /// <summary>DEFFACSD families of the two battery kinds. rule: B7-bombardment §4.1 (Mordant Lien-150 family 0x22, LNR I and II family 0x23).</summary>
    public const int KdyFamily = 0x22, LnrFamily = 0x23;

    public BombardParams Params { get; }

    /// <summary>
    /// Effective leadership L of the fleet's Admiral (0 if none). GameHost wires the character module; tests inject.
    /// rule: m3-bombardment §Resolution 1; B7-bombardment §5.1 (CommandKind 2 aboard any of the fleet's ships, 0x4fd790).
    /// </summary>
    public Func<Fleet, int> AdmiralLeadership { get; set; } = _ => 0;

    /// <summary>
    /// Effective leadership L of the holder's General standing on the system (0 if none): multiplies every battery shot by L / 40 + 1. GameHost wires
    /// the system General post. rule: B7-bombardment §4.3 (CommandKind 3 directly on the system, 0x509330).
    /// </summary>
    public Func<string, int> BatteryGeneralLeadership { get; set; } = _ => 0;

    /// <summary>Is the Hearth at this system? Read only with <see cref="BombardParams.HqBombardable"/>. GameHost wires the HQ system. rule: B7-bombardment §3 (C9).</summary>
    public Func<string, bool> IsHearth { get; set; } = _ => false;

    /// <summary>
    /// What a destroyed Hearth does. NOT TRACED (B7-bombardment §10: relocation, victory, loss of the HQ bonuses were not read), so the default throws.
    /// Reached only with <see cref="BombardParams.HqBombardable"/> on (DEFAULT off). Replace it once the effect is read; never guess it here.
    /// rule: B7-bombardment §Collisions C9, §10.
    /// </summary>
    public Action<StepContext, int, string> HqDestroyed { get; set; } = (_, _, system) =>
        throw new NotImplementedException($"Bombardment destroyed the Hearth at {system}: the effect is not traced (B7-bombardment §10). Keep HqBombardable off.");

    /// <summary>True while an accepted bombardment of this fleet waits for slot 5.1 (the fleet flag 0x40).</summary>
    public bool HasPending(int fleet) => _pending.Any(p => p.Fleet == fleet);

    /// <summary>Set by the assault module: does the fleet have an assault pending? Part of check 6 (no other order pending). rule: m3-bombardment check 6.</summary>
    public Func<int, bool> OtherOrderPending { get; set; } = _ => false;

    private BombardmentModule(Game game, EconomyModule eco, FleetModule fleets, SupportModule support, DefenseModule def, BombardParams p)
    {
        _game = game; _eco = eco; _fleets = fleets; _support = support; _def = def; Params = p;
    }

    /// <summary>Register the Bombard handler (step 1) and the slot 5.1 hook on <paramref name="game"/>.</summary>
    public static BombardmentModule Attach(Game game, EconomyModule eco, FleetModule fleets, SupportModule support, DefenseModule def, BombardParams? p = null)
    {
        var m = new BombardmentModule(game, eco, fleets, support, def, p ?? new BombardParams());
        game.RegisterOrderHandler<BombardOrder>(m.HandleOrder);
        game.RegisterHook("5.1", m.Resolve);
        return m;
    }

    /// <summary>Queue a Bombard order (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitBombard(Side side, int fleet, string system, BombardCategory category) =>
        _game.Submit(seq => new BombardOrder(seq, side, fleet, system, category));

    // ================================================================== power

    /// <summary>The power multiplier L / 40 + 1, integer division. rule: m3-bombardment §Resolution 1 (B1); B7-bombardment §5.1 (GNPRTB 1537).</summary>
    public int AdmiralMultiplier(int leadership) => leadership / Params.AdmiralStep + 1;

    /// <summary>
    /// A capital ship's bombardment value: bm - mul_div(bm, hull damage, hull) with <see cref="BombardParams.BombardDamageScales"/> (C4), else bm.
    /// rule: B7-bombardment §5.1 (0x501470).
    /// </summary>
    public int ShipValue(EconObject ship)
    {
        var cls = _fleets.ClassOf(ship);
        if (cls == null) return 0;
        int bm = cls.Bombardment;
        if (!Params.BombardDamageScales || cls.Hull <= 0) return bm;
        int damage = Math.Clamp(cls.Hull - ship.Hull, 0, cls.Hull);
        return bm - bm * damage / cls.Hull; // truncating
    }

    /// <summary>
    /// A squadron's bombardment value (the fighters.csv column read as the value): v - mul_div(v, lost craft, squadron size) with
    /// <see cref="BombardParams.BombardDamageScales"/> (C4), else v. rule: B7-bombardment §5.1 (0x5032a0; counted once per squadron).
    /// </summary>
    public int SquadronValue(EconObject squadron)
    {
        var cls = _fleets.ClassOf(squadron);
        if (cls == null) return 0;
        int v = cls.Bombardment;
        if (!Params.BombardDamageScales || cls.SquadronSize <= 0) return v;
        int lost = Math.Clamp(squadron.CraftLost, 0, cls.SquadronSize);
        return v - v * lost / cls.SquadronSize; // truncating
    }

    /// <summary>
    /// The fleet's raw bombardment value: its capital ships' values plus the value of each fighter squadron aboard its ships (once per squadron).
    /// Squadrons flying alone do not count. A ship in <paramref name="grounded"/> (ion damage left after its shields) counts 0; its squadrons still
    /// count. rule: m3-bombardment §Resolution 1 (B2 to B4); B7-bombardment §4.5, §5.1.
    /// </summary>
    public int BaseValue(Fleet f, IReadOnlySet<int>? grounded = null)
    {
        int sum = 0;
        foreach (var shipId in f.Ships) // ids ascending
        {
            if (_eco.Find(shipId) is not { } ship) continue;
            if (grounded == null || !grounded.Contains(shipId)) sum += ShipValue(ship);
            foreach (var sq in _fleets.UnitsAboard(shipId, ObjectCategory.Fighter)) sum += SquadronValue(sq);
        }
        return sum;
    }

    /// <summary>A fleet's power: value x (L / 40 + 1), an integer. rule: m3-bombardment §Resolution 1; B7-bombardment §5.1.</summary>
    public int Power(Fleet f) => Power(f, null);

    private int Power(Fleet f, IReadOnlySet<int>? grounded) => BaseValue(f, grounded) * AdmiralMultiplier(AdmiralLeadership(f));

    // ================================================================== the order (step 1)

    /// <summary>
    /// The checks of the table, in its order; null = all hold. Run at step 1 (<paramref name="atOrder"/>, with check 6) and again at slot 5.1.
    /// rule: m3-bombardment §Order and preconditions.
    /// </summary>
    private string? Check(Side side, int fleetId, string system, BombardCategory category, bool atOrder)
    {
        if (!_game.State.Fleets.TryGetValue(fleetId, out var f)) return RejectReason.UnknownFleet;           // 1
        if (f.Side != side) return RejectReason.NotYours;
        if (f.At == null) return RejectReason.InTransit;
        if (f.At != system) return BombardReject.NotAtSystem;
        if (_eco.IsDestroyed(system)) return RejectReason.SystemDestroyed;                                   // rule: m5-death-star §2 effect 5 (cannot be bombarded)
        if (_fleets.OwnerOf(system) == side) return BombardReject.OwnSystem;                                 // 2 (enemy or neutral allowed)
        if (_fleets.FleetsAt(system).Any(x => x.Side != side)) return BombardReject.EnemyFleetPresent;       // 3 (DEFAULT: both sides, any fleet)
        if (Power(f) <= 0) return BombardReject.NoBombardPower;                                              // 4 (DEFAULT)
        if (category == BombardCategory.DestroySystem) return RejectReason.NotAvailable;                     // 5 (Destroy System is its own order, DestroySystemOrder: m5-death-star §2)
        if (atOrder && (_pending.Any(p => p.Fleet == fleetId) || OtherOrderPending(fleetId))) return BombardReject.OrderPending;             // 6 (DEFAULT)
        return null;
    }

    private OrderResult HandleOrder(StepContext ctx, BombardOrder o)
    {
        var reason = Check(o.Side, o.Fleet, o.System, o.Category, atOrder: true);
        if (reason != null) return OrderResult.Reject(reason);
        _pending.Add(o); // the fleet flag 0x40 (Traced)
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, Fleet: o.Fleet));
    }

    // ================================================================== slot 5.1

    private enum Kind { Regiment, Facility, Energy, Raw, Hq }
    private readonly record struct Target(Kind Kind, int Id, int Defense);

    // rule: m3-bombardment §Where it runs (slot 5.1, system name then fleet id); B7-bombardment §1
    private void Resolve(StepContext ctx)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.OrderBy(o => o.System, StringComparer.Ordinal).ThenBy(o => o.Fleet).ToList();
        _pending.Clear(); // one-shot: every flag is cleared (B7-bombardment §6.2, confirmed)
        int day = ctx.Day + 1;
        if (!Params.BombardFleetsTogether)
        {
            // C3 off: one strike per fleet, each checked just before its own turn (the m3 rule)
            foreach (var o in batch)
            {
                var reason = Check(o.Side, o.Fleet, o.System, o.Category, atOrder: false);
                if (reason != null) { Cancel(ctx, day, o, reason); continue; }
                Strike(ctx, day, o.System, o.Side, o.Category, new List<int> { o.Fleet });
            }
            return;
        }
        // rule: B7-bombardment §1 (C3): every flagged fleet at the system takes part in the one strike. The first order in the m3 resolution order
        // (fleet id) starts it and gives the category; the other fleets join it (the strike clears their flags too, so their own rule events find
        // no flagged fleet). Each order is still checked at 5.1 (m3-bombardment §Order and preconditions).
        foreach (var group in batch.GroupBy(o => o.System, StringComparer.Ordinal))
        {
            var ok = new List<BombardOrder>();
            foreach (var o in group)
            {
                var reason = Check(o.Side, o.Fleet, o.System, o.Category, atOrder: false);
                if (reason != null) Cancel(ctx, day, o, reason); else ok.Add(o);
            }
            if (ok.Count == 0) continue;
            if (ok.Any(o => o.Side != ok[0].Side)) // rule: B7-bombardment §1 ("they must all be of one side, or nothing happens")
            {
                foreach (var o in ok) Cancel(ctx, day, o, BombardReject.MixedSides);
                continue;
            }
            Strike(ctx, day, group.Key, ok[0].Side, ok[0].Category, ok.Select(o => o.Fleet).ToList());
        }
    }

    private static void Cancel(StepContext ctx, int day, BombardOrder o, string reason) =>
        ctx.Emit((i, _) => new BombardCancelled(i, day, o.Fleet, o.System, reason));

    /// <summary>
    /// One strike of <paramref name="bomber"/>'s fleets on the system: counts, return fire, hits, aftermath. rule: B7-bombardment §1 (0x58d770),
    /// §2 to §6; m3-bombardment §Resolution.
    /// </summary>
    private void Strike(StepContext ctx, int day, string system, Side bomber, BombardCategory category, List<int> fleetIds)
    {
        Side? holder = _fleets.OwnerOf(system);

        // §2 counts: n = the bomber's capital ships in the flagged fleets (fleet id, then ship id order); m0 = the targets, counted once
        var ships = new List<EconObject>();
        foreach (var fid in fleetIds)
            foreach (var id in _game.State.Fleets[fid].Ships)
                if (_eco.Find(id) is { Category: ObjectCategory.CapitalShip } s) ships.Add(s);
        int m0 = Targets(system, holder, category).Count;

        // §4 return fire (C2)
        var grounded = new SortedSet<int>();
        bool stopped = false;
        if (Params.BatteryReturnFire)
        {
            ReturnFire(ctx, day, system, ships, grounded);
            stopped = ships.All(s => _eco.Find(s.Id) == null); // §4.7: no bombarding ship left, no hits
        }

        // §5.1, §5.2 power and shields
        int p = 0;
        foreach (var fid in fleetIds)
            if (_game.State.Fleets.TryGetValue(fid, out var f)) p += Power(f, grounded);
        int sh = _def.ShieldStrength(system);                // GenCore only (family 0x24), counted once, subtracted once from the total
        int hits = stopped ? 0 : Math.Max(0, p - sh);        // rule: m3-bombardment §Resolution 2
        var fleetList = new ValueList<int>(fleetIds.ToList());
        ctx.Emit((i, _) => new BombardmentStarted(i, day, system, fleetIds[0], bomber, category, p, sh, hits, fleetList));

        var tally = new Tally();
        // §5.3 military-only collateral (C6)
        if (Params.MilitaryCollateral && category == BombardCategory.Military && hits > 0) Collateral(ctx, day, system, m0, tally);

        // §5.4 hits
        for (int h = 0; h < hits; h++)
        {
            var live = Targets(system, holder, category); // the targets still standing (recounted after every destruction)
            if (live.Count == 0) break;                    // the remaining hits are discarded without draws
            Target target;
            if (Params.BombardWastedHits)
            {
                // rule: B7-bombardment §5.4 (C1): t over the STARTING count m0; a pick past the targets still standing is wasted, no die
                int t = Rand(ctx, m0 - 1);
                if (t >= live.Count)
                {
                    int standing = live.Count;
                    ctx.Emit((i, _) => new HitWasted(i, day, system, t, standing));
                    continue;
                }
                target = live[t];
            }
            else
                target = live[ctx.Rng.NextInt(0, live.Count - 1)]; // the m3 rule: t over the live pool, then r
            int r = ctx.Rng.NextInt(Params.DieMin, Params.DieMax);
            bool destroyed = r > target.Defense;           // chance (10 - defense) * 10 percent
            ctx.Emit((i, _) => new TargetHit(i, day, target.Kind.ToString(), target.Id, target.Defense, r, destroyed));
            if (destroyed) DestroyTarget(ctx, day, system, target, tally);
        }

        // §6 aftermath
        if (!Params.BatteryReturnFire) // the m3 hook: the batteries at the start, no ship changed
        {
            var returnFire = _def.ReturnFireSet(system);
            if (returnFire.Count > 0) ctx.Emit((i, _) => new BatteryReturnFire(i, day, system, new ValueList<int>(returnFire)));
        }
        if (tally.Civilian) // rule: B7-bombardment §6.1: once, to the bomber, applied now (slot 5.1), also on a neutral system
            _support.ChangeSupport(ctx, system, bomber, Params.CivilianSupportChange, SupportCause.Bombardment);
        // B7-bombardment §6.1 also names the sector effect of B5c (0x518ac0); that batch is not built yet, so no sector change here.
        ctx.Emit((i, _) => new BombardmentResolved(i, day, system, hits, tally.Regiments, tally.Facilities, tally.Energy, tally.Raw));
    }

    private sealed class Tally
    {
        public int Regiments, Facilities, Energy, Raw;
        /// <summary>The civilian flag (rule +0x28): a destroyed civilian facility, energy or raw. rule: B7-bombardment §5.4.5.</summary>
        public bool Civilian;
    }

    /// <summary>rand(0..b): no draw when b &lt;= 0 (the Classic helper 0x53e290). rule: B7-bombardment §Rules (shared helpers).</summary>
    private static int Rand(StepContext ctx, int b) => b <= 0 ? 0 : ctx.Rng.NextInt(0, b);

    // rule: B7-bombardment §5.4.4 (0x58d020): destroy one hit target
    private void DestroyTarget(StepContext ctx, int day, string system, Target target, Tally tally)
    {
        switch (target.Kind)
        {
            case Kind.Regiment:
                var reg = _eco.Find(target.Id)!;
                _eco.RemoveObject(reg.Id); // gone outright, no partial strength (destroyed reason 0xf, Traced)
                ctx.Emit((i, _) => new RegimentDestroyed(i, day, reg.Id, reg.Class, system, reg.Side!.Value, "Bombardment"));
                tally.Regiments++;
                break;
            case Kind.Facility:
                var fac = _eco.Find(target.Id)!;
                if (!_def.Data.IsDefense(fac.Class)) tally.Civilian = true; // families 0x28-0x2f: any non-defense facility
                _eco.Destroy(ctx, day, fac, DestroyReason.Bombardment);    // frees its energy slot
                tally.Facilities++;
                break;
            case Kind.Energy:
            case Kind.Raw:
                var sl = _eco.SlotsOf(system);
                bool isEnergy = target.Kind == Kind.Energy;
                _eco.SetSlots(system, isEnergy ? sl.Energy - 1 : sl.Energy, isEnergy ? sl.Raw : sl.Raw - 1); // floor 0: the entry exists only above 0
                ctx.Emit((i, _) => new ResourceReduced(i, day, system, target.Kind.ToString()));
                if (isEnergy) tally.Energy++; else tally.Raw++;
                tally.Civilian = true;             // rule: B7-bombardment §5.4.5, §6.1 (C7, gap fill): an energy or raw loss sets the civilian flag
                _eco.EnforceCapacity(ctx, system); // over-capacity check at once (m2-slots-and-resources stands); its picks follow this hit's r
                break;
            case Kind.Hq:
                HqDestroyed(ctx, day, system); // C9: NOT TRACED (B7-bombardment §10); the default throws
                break;
        }
    }

    /// <summary>
    /// The military-only collateral: x = rand(0..m0); on 0, the first civilian facility of the holder takes a roll (a pick rand(0..k - 1) is drawn
    /// and ignored: the Classic bug), and rand(1..10) is drawn even when there is none. No hit is used up. rule: B7-bombardment §5.3 (C6).
    /// </summary>
    private void Collateral(StepContext ctx, int day, string system, int m0, Tally tally)
    {
        if (Rand(ctx, m0) != 0) return;
        var civilian = CivilianFacilities(system);
        EconObject? target = null;
        if (civilian.Count > 0)
        {
            Rand(ctx, civilian.Count - 1);
            // Classic BUG, reproduced on purpose (MilitaryCollateral): the loop at 0x58d0f8 counts up to the pick without advancing, so the pick above
            // is ignored and the FIRST civilian facility is always the one rolled against. rule: B7-bombardment §5.3.
            target = civilian[0];
        }
        int r = ctx.Rng.NextInt(Params.DieMin, Params.DieMax);
        int defense = target == null ? 0 : _def.Data.FacilityDefense(target.Class);
        bool destroyed = target != null && r > defense;
        int id = target?.Id ?? 0;
        ctx.Emit((i, _) => new CollateralHit(i, day, system, id, defense, r, destroyed));
        if (destroyed) DestroyTarget(ctx, day, system, new Target(Kind.Facility, id, defense), tally);
    }

    // ================================================================== return fire (B7-bombardment §4)

    /// <summary>
    /// Every completed Mordant Lien-150, then every completed LNR, fires one shot at a random ship of <paramref name="ships"/>: rand(0..n - 1), damage
    /// attack x (L / 40 + 1). Then ship by ship: the current shield S = shield x (4 - c) / 4 x hull left / hull; ion eats S, turbolaser eats what is
    /// left of S then the hull (stays; at 0 the ship is destroyed); a survivor that lost hull rolls for a critical hit. A ship with ion left after its
    /// shields is grounded for this strike. Shield loss and the ion and turbolaser amounts end with the strike (nothing of them is stored).
    /// rule: B7-bombardment §4 (0x58dc10, 0x58d540, 0x5018c0, 0x5585e0).
    /// </summary>
    private void ReturnFire(StepContext ctx, int day, string system, List<EconObject> ships, SortedSet<int> grounded)
    {
        int n = ships.Count;
        var batteries = _def.DefensesOf(system).Where(d => d.Class.Family == KdyFamily)
            .Concat(_def.DefensesOf(system).Where(d => d.Class.Family == LnrFamily)).ToList(); // KDY first, then LNR; id order in each
        if (n == 0 || batteries.Count == 0) return;
        var firing = batteries.Select(b => b.Facility.Id).ToList();
        ctx.Emit((i, _) => new BatteryReturnFire(i, day, system, new ValueList<int>(firing)));

        int mult = BatteryGeneralLeadership(system) / Params.GeneralStep + 1;
        var ion = new long[n];
        var turbo = new long[n];
        foreach (var (fac, cls) in batteries)
        {
            int k = Rand(ctx, n - 1);
            int dmg = cls.Attack * mult; // DEFFACSD word 13: KDY 2000, LNR I 800, LNR II 5000
            bool isIon = cls.Family == KdyFamily;
            if (isIon) ion[k] += dmg; else turbo[k] += dmg;
            int shipId = ships[k].Id, facId = fac.Id;
            ctx.Emit((i, _) => new BatteryShot(i, day, system, facId, shipId, dmg, isIon));
        }

        for (int k = 0; k < n; k++) // §4.4: after all shots, ship by ship
        {
            if (ion[k] == 0 && turbo[k] == 0) continue;
            var ship = ships[k];
            var cls = _fleets.ClassOf(ship)!;
            int hullMax = cls.Hull, left = Math.Clamp(ship.Hull, 0, Math.Max(0, hullMax));
            int c = ship.Hits is { Count: > 0 } hs ? hs[0] : 0; // the shield-recharge critical level
            long shield = hullMax <= 0 ? 0
                : (long)cls.ShieldStrength * Math.Max(0, Params.CriticalCap - c) / Params.CriticalCap * left / hullMax; // §4.4.1: two truncating steps (0x501160)
            long s = shield;
            long ionLeft = ion[k] - s;                         // §4.4.2: ion eats S first; what is left stays on the ship for this strike
            if (ionLeft >= 0) s = 0; else { s = -ionLeft; ionLeft = 0; }
            long hullLoss = Math.Clamp(turbo[k] - s, 0, left); // §4.4.3, §4.4.4: turbolaser eats what is left of S, then the hull, capped at the hull
            ship.Hull = left - (int)hullLoss;
            bool destroyed = ship.Hull <= 0;
            bool isGrounded = !destroyed && ionLeft > 0;       // §4.5 (the object-bit exemption +0x24 & 0x30 was not read: never applied)
            if (isGrounded) grounded.Add(ship.Id);
            int critical = -1;
            if (!destroyed && hullLoss > 0) critical = CriticalHit(ctx, ship, (int)hullLoss);
            int sid = ship.Id, ionD = (int)ion[k], turD = (int)turbo[k], shD = (int)shield, lost = (int)hullLoss;
            ctx.Emit((i, _) => new BombardShipDamaged(i, day, system, sid, ionD, turD, shD, lost, destroyed, isGrounded, critical));
            if (destroyed) LoseShip(ctx, day, system, ship);
        }
    }

    /// <summary>
    /// The critical-hit roll of a ship that lost hull and survived: rand(0..50) + pct(lost, lost + left) &gt;= 50, then CSCRHTTB(rand(0..50)) picks the
    /// counter; each level goes up by 1, capped at 4; the hyperdrive goes to the backup when the primary is at the cap. Returns the counter raised
    /// (-1 none). rule: B7-bombardment §4.4.5 (0x5585e0, 0x5018c0).
    /// </summary>
    private int CriticalHit(StepContext ctx, EconObject ship, int hullLoss)
    {
        int score = Rand(ctx, Params.CriticalRollMax) + 100 * hullLoss / (hullLoss + ship.Hull); // pct(a, b) = 100 x a / b (0x53e1b0)
        if (score < Params.CriticalAt) return -1;
        int roll = Rand(ctx, Params.CriticalTableMax);
        int idx = roll <= Params.CriticalShieldUpTo ? 0 : roll <= Params.CriticalWeaponsUpTo ? 1 : roll <= Params.CriticalHoldBeamUpTo ? 2
            : roll <= Params.CriticalSpeedUpTo ? 3 : ObjectRegistry.HyperdriveCounter;
        ship.Hits ??= new List<int>(new int[ObjectRegistry.HitCounters]);
        if (idx == ObjectRegistry.HyperdriveCounter && ship.Hits[idx] >= Params.CriticalCap) idx = ObjectRegistry.HyperdriveCounter + 1; // the backup hyperdrive
        if (ship.Hits[idx] < Params.CriticalCap) ship.Hits[idx]++;
        return idx;
    }

    /// <summary>
    /// A ship destroyed by return fire (reason 15). B7-bombardment §10 did not read what happens to the units aboard, so the rebuild's one generic
    /// ship-loss path is used, as for a battle loss and the harvester (BattleModule.DestroyUnit, HarvesterModule.LoseShip): the regiments aboard
    /// are lost with it, the squadrons aboard land when it leaves its fleet. rule: B7-bombardment §4.4.4; m4-battle-classic §10.
    /// </summary>
    private void LoseShip(StepContext ctx, int day, string system, EconObject ship)
    {
        foreach (var reg in _eco.Objects.Where(r => r.AboardShip == ship.Id && r.Category == ObjectCategory.Regiment).OrderBy(r => r.Id).ToList())
        {
            var r = reg;
            ctx.Emit((i, _) => new RegimentDestroyed(i, day, r.Id, r.Class, system, r.Side!.Value, "ShipLost"));
            _eco.DestroyUnit(ctx, day, r);
        }
        _eco.DestroyUnit(ctx, day, ship);
    }

    // ================================================================== the targets (B7-bombardment §3)

    /// <summary>
    /// The targets of a category still standing, in the Classic walk order. Only the holder's completed objects standing directly on the system:
    /// Military = regiments (id order), then defence facilities (id order), then the Hearth (only with <see cref="BombardParams.HqBombardable"/>);
    /// Civilian = the non-defence facilities (id order); General = Military, then Civilian, then Energy (while above 0), then Raw (while above 0).
    /// Energy and Raw are never Civilian targets (C5, gap fill). Special forces, characters, fighters and ships are never targets.
    /// rule: B7-bombardment §1 (pool flags), §3; m3-bombardment §Resolution 3.
    /// </summary>
    private List<Target> Targets(string system, Side? holder, BombardCategory category)
    {
        var pool = new List<Target>();
        bool military = category is BombardCategory.Military or BombardCategory.General;
        bool civilian = category is BombardCategory.Civilian or BombardCategory.General;
        bool resources = category is BombardCategory.General; // rule: B7-bombardment §1 table (energy and raw: General and Destroy System only)
        if (military)
        {
            foreach (var o in _eco.Objects) // id order
                if (o.Category == ObjectCategory.Regiment && o.Completed && o.System == system && o.AboardShip == 0 && holder != null && o.Side == holder)
                    pool.Add(new Target(Kind.Regiment, o.Id, _def.Data.RegimentDefense(o.Class)));
            foreach (var (f, c) in _def.DefensesOf(system)) pool.Add(new Target(Kind.Facility, f.Id, c.BombardDefense));
            // rule: B7-bombardment §3 (C9): the Hearth (families 0x20-0x21, ALLFACSD word 12) is the holder's Military target; DEFAULT off
            if (Params.HqBombardable && holder == Side.Collective && IsHearth(system)) pool.Add(new Target(Kind.Hq, 0, _def.Data.HqDefense));
        }
        if (civilian)
            foreach (var o in CivilianFacilities(system))
                pool.Add(new Target(Kind.Facility, o.Id, _def.Data.FacilityDefense(o.Class)));
        if (resources)
        {
            var sl = _eco.SlotsOf(system);
            if (sl.Energy > 0) pool.Add(new Target(Kind.Energy, 0, Params.EnergyDefense));
            if (sl.Raw > 0) pool.Add(new Target(Kind.Raw, 0, Params.RawDefense));
        }
        return pool;
    }

    /// <summary>The completed non-defence facilities on the system, id order (families 0x28-0x2f). rule: B7-bombardment §3.</summary>
    private List<EconObject> CivilianFacilities(string system) =>
        _eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system && !_def.Data.IsDefense(o.Class)).ToList();
}
