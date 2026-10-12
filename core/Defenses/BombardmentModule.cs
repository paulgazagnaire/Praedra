using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;

namespace Rebellion.Core.Defenses;

/// <summary>What the player chose to shell. Original order names: "Target Military Facilities", "Target Civilian Facilities", "General Bombardment", "Destroy System"
/// (codes 0x220, 0x221, 0x222, 0x223, Traced). rule: m3-bombardment §Order and preconditions.</summary>
public enum BombardCategory { Military = 0, Civilian = 1, General = 2, DestroySystem = 3 }

/// <summary>
/// Orbital bombardment: fleet <paramref name="Fleet"/> shells <paramref name="System"/>. Checked in step 1 (rejection) and again at slot 5.1
/// (cancellation). The handler lives in <see cref="BombardmentModule"/>. rule: m3-bombardment §Order and preconditions.
/// </summary>
public sealed record BombardOrder(long Sequence, Side Side, int Fleet, string System, BombardCategory Category) : Order(Sequence, Side);

/// <summary>
/// Numbers of the bombardment sheet, with tags. rule: m3-bombardment §Numbers.
/// </summary>
public sealed record BombardParams
{
    /// <summary>Each full this-many points of Admiral leadership add one to the power multiplier (L / 40 + 1). Traced; integer division DEFAULT.</summary>
    public int AdmiralStep { get; init; } = 40;
    /// <summary>The per-hit die is rand(1..this). Traced.</summary>
    public int DieMax { get; init; } = 10;
    /// <summary>Bombardment defense of the system's Energy and Raw entries (a 10 percent chance each hit). Traced.</summary>
    public int EnergyDefense { get; init; } = 9;
    public int RawDefense { get; init; } = 9;
    /// <summary>Support change for the bomber when a civilian facility was destroyed, once per resolution (GNPRTB 7705). Traced; trigger inferred.</summary>
    public int CivilianSupportChange { get; init; } = -20;
}

/// <summary>
/// The Bombard order and its resolution at work-list slot 5.1. Holds settings only; the orders accepted this Step wait in a list that lives
/// from step 1 to slot 5.1 of the same Step (so it is empty whenever a game is saved). Attach after the fleet, economy and support modules.
/// All draws come from the one game generator: per hit <c>t</c> then <c>r</c>, over-capacity picks of that hit after its <c>r</c>.
/// rule: m3-bombardment (the whole sheet). Tags: medium-low confidence (units/02 Round 3): pool contents, fighter value, rounding and the
/// support trigger are DEFAULTs.
/// </summary>
public sealed class BombardmentModule
{
    private readonly Game _game;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;
    private readonly SupportModule _support;
    private readonly DefenseModule _def;
    private readonly List<BombardOrder> _pending = new(); // accepted this Step; consumed at slot 5.1

    public BombardParams Params { get; }

    /// <summary>
    /// Leadership L of the fleet's Admiral (0 if none). Characters are M5, so the default answers 0; tests and M5 set it.
    /// rule: m3-bombardment §Resolution 1 (AdmiralLeadership returns 0 until then).
    /// </summary>
    public Func<Fleet, int> AdmiralLeadership { get; set; } = _ => 0;

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

    /// <summary>The power multiplier L / 40 + 1, integer division. rule: m3-bombardment §Resolution 1 (B1).</summary>
    public int AdmiralMultiplier(int leadership) => leadership / Params.AdmiralStep + 1;

    /// <summary>
    /// The fleet's raw bombardment value: the capital ships' modifiers plus the value of each fighter squadron aboard its ships (once per
    /// squadron). Squadrons flying alone do not count. rule: m3-bombardment §Resolution 1 (B2 to B4).
    /// </summary>
    public int BaseValue(Fleet f)
    {
        int sum = 0;
        foreach (var shipId in f.Ships) // ids ascending
        {
            if (_eco.Find(shipId) is not { } ship) continue;
            sum += _fleets.ClassOf(ship)?.Bombardment ?? 0;
            foreach (var sq in _fleets.UnitsAboard(shipId, ObjectCategory.Fighter)) sum += _fleets.ClassOf(sq)?.Bombardment ?? 0;
        }
        return sum;
    }

    /// <summary>P = base value * (L / 40 + 1), an integer. rule: m3-bombardment §Resolution 1 (Traced, medium-low).</summary>
    public int Power(Fleet f) => BaseValue(f) * AdmiralMultiplier(AdmiralLeadership(f));

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
        if (_fleets.OwnerOf(system) == side) return BombardReject.OwnSystem;                                 // 2 (enemy or neutral allowed)
        if (_fleets.FleetsAt(system).Any(x => x.Side != side)) return BombardReject.EnemyFleetPresent;       // 3 (DEFAULT: both sides, any fleet)
        if (Power(f) <= 0) return BombardReject.NoBombardPower;                                              // 4 (DEFAULT)
        if (category == BombardCategory.DestroySystem) return RejectReason.NotAvailable;                     // 5 (M3: no Death Star spec)
        if (atOrder && _pending.Any(p => p.Fleet == fleetId)) return BombardReject.OrderPending;             // 6 (DEFAULT)
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

    private enum Kind { Regiment, Facility, Energy, Raw }
    private readonly record struct Target(Kind Kind, int Id, int Defense);

    // rule: m3-bombardment §Where it runs (slot 5.1, system name then fleet id)
    private void Resolve(StepContext ctx)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.OrderBy(o => o.System, StringComparer.Ordinal).ThenBy(o => o.Fleet).ToList();
        _pending.Clear(); // one-shot: the flag is cleared (DEFAULT, step 7)
        int day = ctx.Day + 1;
        foreach (var o in batch)
        {
            var reason = Check(o.Side, o.Fleet, o.System, o.Category, atOrder: false);
            if (reason != null) { ctx.Emit((i, _) => new BombardCancelled(i, day, o.Fleet, o.System, reason)); continue; }
            Bombard(ctx, day, o, _game.State.Fleets[o.Fleet]);
        }
    }

    private void Bombard(StepContext ctx, int day, BombardOrder o, Fleet fleet)
    {
        string system = o.System;
        Side? holder = _fleets.OwnerOf(system);
        int p = Power(fleet);
        int s = _def.ShieldStrength(system);                 // counted once, at the start (Traced, medium-low)
        int hits = Math.Max(0, p - s);                       // rule: m3-bombardment §Resolution 2
        var returnFire = _def.ReturnFireSet(system);         // the batteries at the start of the bombardment (hook only, DEFAULT)
        ctx.Emit((i, _) => new BombardmentStarted(i, day, system, fleet.Id, o.Side, o.Category, p, s, hits));

        int regiments = 0, facilities = 0, energy = 0, raw = 0;
        bool civilianFacilityDestroyed = false;
        for (int h = 0; h < hits; h++)
        {
            var pool = BuildPool(system, holder, o.Category); // rebuilt after every destroyed target
            if (pool.Count == 0) break;                       // the remaining hits are discarded without draws
            int t = ctx.Rng.NextInt(0, pool.Count - 1);       // draw order: t, then r (Traced)
            int r = ctx.Rng.NextInt(1, Params.DieMax);
            var target = pool[t];
            bool destroyed = r > target.Defense;              // chance (10 - defense) * 10 percent
            ctx.Emit((i, _) => new TargetHit(i, day, target.Kind.ToString(), target.Id, target.Defense, r, destroyed));
            if (!destroyed) continue;

            switch (target.Kind)
            {
                case Kind.Regiment:
                    var reg = _eco.Find(target.Id)!;
                    _eco.RemoveObject(reg.Id); // gone outright, no partial strength (destroyed reason 0xf, Traced)
                    ctx.Emit((i, _) => new RegimentDestroyed(i, day, reg.Id, reg.Class, system, reg.Side!.Value, "Bombardment"));
                    regiments++;
                    break;
                case Kind.Facility:
                    var fac = _eco.Find(target.Id)!;
                    if (!_def.Data.IsDefense(fac.Class)) civilianFacilityDestroyed = true; // rule: §Resolution 5 (any non-defense facility)
                    _eco.Destroy(ctx, day, fac, DestroyReason.Bombardment);               // frees its energy slot
                    facilities++;
                    break;
                case Kind.Energy:
                case Kind.Raw:
                    var sl = _eco.SlotsOf(system);
                    bool isEnergy = target.Kind == Kind.Energy;
                    _eco.SetSlots(system, isEnergy ? sl.Energy - 1 : sl.Energy, isEnergy ? sl.Raw : sl.Raw - 1); // floor 0: the pool holds it above 0
                    ctx.Emit((i, _) => new ResourceReduced(i, day, system, target.Kind.ToString()));
                    if (isEnergy) energy++; else raw++;
                    _eco.EnforceCapacity(ctx, system); // over-capacity check at once; its picks follow this hit's r
                    break;
            }
        }

        // Events in the sheet's order: return fire, then the support change, then the summary.
        if (returnFire.Count > 0) ctx.Emit((i, _) => new BatteryReturnFire(i, day, system, new ValueList<int>(returnFire)));
        if (civilianFacilityDestroyed) // once, to the bomber, applied now (slot 5.1), also on a neutral system
            _support.ChangeSupport(ctx, system, o.Side, Params.CivilianSupportChange, SupportCause.Bombardment);
        ctx.Emit((i, _) => new BombardmentResolved(i, day, system, hits, regiments, facilities, energy, raw));
    }

    /// <summary>
    /// The target pool of a category, in the sheet's order. Military: the holder's regiments on the system (not aboard ships), id order, then
    /// completed defense facilities, id order. Civilian: completed non-defense facilities, id order, then Energy, then Raw (while above 0).
    /// General: Military then Civilian. Special forces, characters, fighters, ships and unfinished facilities are never targets (DEFAULT).
    /// rule: m3-bombardment §Resolution 3.
    /// </summary>
    private List<Target> BuildPool(string system, Side? holder, BombardCategory category)
    {
        var pool = new List<Target>();
        bool military = category is BombardCategory.Military or BombardCategory.General;
        bool civilian = category is BombardCategory.Civilian or BombardCategory.General;
        if (military)
        {
            foreach (var o in _eco.Objects) // id order
                if (o.Category == ObjectCategory.Regiment && o.Completed && o.System == system && o.AboardShip == 0 && holder != null && o.Side == holder)
                    pool.Add(new Target(Kind.Regiment, o.Id, _def.Data.RegimentDefense(o.Class)));
            foreach (var (f, c) in _def.DefensesOf(system)) pool.Add(new Target(Kind.Facility, f.Id, c.BombardDefense));
        }
        if (civilian)
        {
            foreach (var o in _eco.Objects)
                if (o.Category == ObjectCategory.Facility && o.Completed && o.System == system && !_def.Data.IsDefense(o.Class))
                    pool.Add(new Target(Kind.Facility, o.Id, _def.Data.FacilityDefense(o.Class)));
            var sl = _eco.SlotsOf(system);
            if (sl.Energy > 0) pool.Add(new Target(Kind.Energy, 0, Params.EnergyDefense));
            if (sl.Raw > 0) pool.Add(new Target(Kind.Raw, 0, Params.RawDefense));
        }
        return pool;
    }
}
