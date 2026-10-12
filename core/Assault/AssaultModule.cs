using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;
using Rebellion.Core.Troops;

namespace Rebellion.Core.Assault;

/// <summary>
/// Planetary assault: fleet <paramref name="Fleet"/> lands its regiments on <paramref name="System"/>. Checked in step 1 (rejection) and
/// again at slot 5.2 (cancellation). The handler lives in <see cref="AssaultModule"/>. rule: m3-assault §Order and preconditions.
/// </summary>
public sealed record AssaultOrder(long Sequence, Side Side, int Fleet, string System) : Order(Sequence, Side);

/// <summary>How collateral rounds are counted. rule: m3-assault §Defaults (CollateralMode).</summary>
public enum CollateralMode { Truncate, PerDuelChance }

/// <summary>
/// Numbers and switches of the assault sheet, with their tags. rule: m3-assault §Numbers, §Defaults for undecided items.
/// </summary>
public sealed record AssaultParams
{
    /// <summary>The duel die is rand(0..this), 11 equal values. Traced; the 0..10 range is the units/07 assumption (Open question 3).</summary>
    public int DieMax { get; init; } = 10;
    /// <summary>Each full this-many points of General leadership add one to the duel (GNPRTB 0x201 and 0x202). Traced.</summary>
    public int LeadershipStep { get; init; } = 30;
    /// <summary>x at or above this destroys the defender (GNPRTB 0x203). Traced.</summary>
    public int DefenderDiesAtLeast { get; init; } = 6;
    /// <summary>x at or below this destroys the attacker (GNPRTB 0x204). Traced.</summary>
    public int AttackerDiesAtMost { get; init; } = 4;
    /// <summary>Collateral rounds = duels * this / 100 (GNPRTB 0x205). Traced.</summary>
    public int CollateralPercent { get; init; } = 8;
    /// <summary>At most this many attackers land on a win, lowest id first. Lead (Open question 1), DEFAULT 6.</summary>
    public int LandingCap { get; init; } = 6;
    /// <summary>Passes over the attackers. DEFAULT 1; 0 = repeat passes until one side is empty (the alternative reading). Open question 2.</summary>
    public int Passes { get; init; } = 1;
    /// <summary>DEFAULT Truncate; PerDuelChance adds a round with this percent after each duel (one rand(0..99) per duel). Open question 4.</summary>
    public CollateralMode CollateralMode { get; init; } = CollateralMode.Truncate;
    /// <summary>
    /// Check 5: an opposing fleet at the system forbids the assault (any fleet, armed or not). DEFAULT on (Open question 7). Off lets
    /// two sides both land on one system (needed for the sheet's A21 case, since both fleets then stand at it).
    /// </summary>
    public bool EnemyFleetBlocks { get; init; } = true;
}

/// <summary>
/// The Assault order and its resolution at work-list slot 5.2. Holds settings only; the orders accepted this Step wait in a list that lives
/// from step 1 to slot 5.2 of the same Step (so it is empty whenever a game is saved). Attach last, after the defense, bombardment, troop and
/// control modules. All draws come from the one game generator, in the sheet's fixed order.
/// rule: m3-assault (the whole sheet). Tags: units/02 Round 3, high confidence, never played-checked.
/// </summary>
public sealed class AssaultModule
{
    private readonly Game _game;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;
    private readonly TroopModule _troops;
    private readonly ControlModule _control;
    private readonly DefenseModule _def;
    private readonly BombardmentModule _bomb;
    private readonly List<AssaultOrder> _pending = new(); // accepted this Step; consumed at slot 5.2 (the fleet flag 0x80, Traced)

    public AssaultParams Params { get; }

    /// <summary>
    /// Leadership of the General of a fleet (0 if none). Characters are M5, so the default answers 0; tests and M5 set it.
    /// rule: m3-assault §Resolution 3 (GeneralLeadership returns 0 until M5).
    /// </summary>
    public Func<Fleet, int> FleetGeneralLeadership { get; set; } = _ => 0;

    /// <summary>Leadership of the system's General, who leads the defenders (0 if none). Default 0 until M5. rule: m3-assault §Resolution 3.</summary>
    public Func<string, int> SystemGeneralLeadership { get; set; } = _ => 0;

    private AssaultModule(Game game, EconomyModule eco, FleetModule fleets, TroopModule troops, ControlModule control, DefenseModule def,
        BombardmentModule bomb, AssaultParams p)
    {
        _game = game; _eco = eco; _fleets = fleets; _troops = troops; _control = control; _def = def; _bomb = bomb; Params = p;
    }

    /// <summary>Register the Assault handler (step 1) and the slot 5.2 hook on <paramref name="game"/>, and tell the bombardment module about this one's pending list (check 6).</summary>
    public static AssaultModule Attach(Game game, EconomyModule eco, FleetModule fleets, TroopModule troops, ControlModule control,
        DefenseModule def, BombardmentModule bomb, AssaultParams? p = null)
    {
        var m = new AssaultModule(game, eco, fleets, troops, control, def, bomb, p ?? new AssaultParams());
        game.RegisterOrderHandler<AssaultOrder>(m.HandleOrder);
        game.RegisterHook("5.2", m.Resolve);
        bomb.OtherOrderPending = m.HasPending; // rule: m3-bombardment check 6 and m3-assault check 6 (no other order pending for the fleet)
        return m;
    }

    /// <summary>Queue an Assault order (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitAssault(Side side, int fleet, string system) =>
        _game.Submit(seq => new AssaultOrder(seq, side, fleet, system));

    /// <summary>True while an accepted assault of this fleet waits for slot 5.2 (the pending flag).</summary>
    public bool HasPending(int fleet) => _pending.Any(o => o.Fleet == fleet);

    // ================================================================== pure rules (A1 to A5)

    /// <summary>The General's bonus: leadership / 30, integer division (0 to 29 gives 0, 120 gives 4). rule: m3-assault §Resolution 3 (A5).</summary>
    public int LeadershipSteps(int leadership) => leadership / Params.LeadershipStep;

    /// <summary>x = roll + attack + L_att / 30 - defense - L_def / 30. rule: m3-assault §Resolution 2c (A1, A4).</summary>
    public int DuelValue(int roll, int attack, int defense, int attackerLeadership = 0, int defenderLeadership = 0) =>
        roll + attack + LeadershipSteps(attackerLeadership) - defense - LeadershipSteps(defenderLeadership);

    /// <summary>What x decides: at least 6 kills the defender, at most 4 the attacker, 5 nobody. rule: m3-assault §Resolution 2d (A1).</summary>
    public string DuelResult(int x) =>
        x >= Params.DefenderDiesAtLeast ? DuelOutcome.DefenderDestroyed : x <= Params.AttackerDiesAtMost ? DuelOutcome.AttackerDestroyed : DuelOutcome.NobodyDies;

    // ================================================================== the order (step 1)

    /// <summary>
    /// The checks of the table, in its order; null = all hold. Run at step 1 (<paramref name="atOrder"/>, with check 6) and again at slot 5.2.
    /// rule: m3-assault §Order and preconditions.
    /// </summary>
    private string? Check(Side side, int fleetId, string system, bool atOrder)
    {
        if (!_game.State.Fleets.TryGetValue(fleetId, out var f)) return RejectReason.UnknownFleet;               // 1
        if (f.Side != side) return RejectReason.NotYours;
        if (f.At == null) return RejectReason.InTransit;
        if (f.At != system) return AssaultReject.NotAtSystem;
        if (_troops.RegimentsAboard(f).Count == 0) return AssaultReject.NoTroops;                                // 2 (special forces never count)
        if (_fleets.OwnerOf(system) == side) return AssaultReject.OwnSystem;                                     // 3 (enemy or neutral allowed)
        if (_def.AssaultForbidden(system)) return AssaultReject.FullyShielded;                                   // 4 (both sides, DEFAULT)
        if (Params.EnemyFleetBlocks && _fleets.FleetsAt(system).Any(x => x.Side != side)) return AssaultReject.EnemyFleetPresent; // 5 (DEFAULT)
        if (atOrder && (HasPending(fleetId) || _bomb.HasPending(fleetId))) return AssaultReject.OrderPending;   // 6 (DEFAULT)
        return null;
    }

    private OrderResult HandleOrder(StepContext ctx, AssaultOrder o)
    {
        var reason = Check(o.Side, o.Fleet, o.System, atOrder: true);
        if (reason != null) return OrderResult.Reject(reason);
        _pending.Add(o); // sets the fleet flag 0x80 only (Traced)
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, Fleet: o.Fleet));
    }

    // ================================================================== slot 5.2

    /// <summary>The sheet numbers the sides 1 = Alliance, 2 = Empire (side 3 is neutral): "side 1 first". rule: m3-assault §Resolution (two sides, one neutral system).</summary>
    private static int SideRank(Side s) => s == Side.Alliance ? 1 : 2;

    private sealed class Att { public required AboardRegiment R; public bool Dead; }

    // rule: m3-assault §Where it runs (slot 5.2, system name, then side id)
    private void Resolve(StepContext ctx)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.OrderBy(o => o.System, StringComparer.Ordinal).ThenBy(o => SideRank(o.Side)).ThenBy(o => o.Fleet).ToList();
        _pending.Clear(); // the pending flag of every fleet involved is cleared, win or not (step 6, Traced)
        int day = ctx.Day + 1;
        foreach (var group in batch.GroupBy(o => (o.System, o.Side))) // GroupBy keeps first-seen order: system, then side
        {
            var fleets = new List<Fleet>();
            foreach (var o in group)
            {
                var reason = Check(o.Side, o.Fleet, o.System, atOrder: false); // each side re-checked now (check 3 sees a new holder)
                if (reason != null) { ctx.Emit((i, _) => new AssaultCancelled(i, day, o.Fleet, o.System, reason)); continue; }
                fleets.Add(_game.State.Fleets[o.Fleet]);
            }
            if (fleets.Count > 0) Fight(ctx, day, group.Key.System, group.Key.Side, fleets);
        }
    }

    /// <summary>Safety bound for Passes = 0 (repeat until empty); the draws make an endless loop impossible in practice.</summary>
    private const int MaxPasses = 100_000;

    private void Fight(StepContext ctx, int day, string system, Side side, List<Fleet> fleets)
    {
        // 0. Setup: attackers = regiments aboard the pending fleets by id; defenders = landed regiments of every other side by id.
        var attackers = fleets.SelectMany(f => _troops.RegimentsAboard(f)).OrderBy(a => a.Regiment.Id).Select(a => new Att { R = a }).ToList();
        var defenders = _troops.RegimentsOnSystem(system, side);
        int a0 = attackers.Count, d0 = defenders.Count;
        int lDef = SystemGeneralLeadership(system);
        var lAtt = new Dictionary<int, int>(); // fleet id -> leadership (lookup only)
        foreach (var f in fleets) lAtt[f.Id] = FleetGeneralLeadership(f);
        ctx.Emit((i, _) => new AssaultStarted(i, day, system, side, new ValueList<int>(fleets.Select(f => f.Id).OrderBy(x => x).ToList()), a0, d0));

        int A() => attackers.Count(x => !x.Dead);

        void KillAttacker(Att x, string reason)
        {
            x.Dead = true;
            int id = x.R.Regiment.Id; string cls = x.R.Regiment.Class;
            _eco.RemoveObject(id);
            ctx.Emit((i, _) => new RegimentDestroyed(i, day, id, cls, system, side, reason));
        }

        // 1. Defense facilities fire (completed, not the attacker's, id order, while A > 0): r, then v on a hit.
        foreach (var shot in _def.BatteryShots(system, side))
        {
            if (A() == 0) break;
            int r = ctx.Rng.NextInt(0, 99);
            if (r >= shot.ChancePercent) continue;
            var alive = attackers.Where(x => !x.Dead).ToList();
            var victim = alive[ctx.Rng.NextInt(0, alive.Count - 1)];
            int fac = shot.Facility, reg = victim.R.Regiment.Id;
            ctx.Emit((i, _) => new BatteryHit(i, day, fac, reg));
            KillAttacker(victim, "AssaultBattery");
        }

        // 2. Duels: attackers in id order, skipping the dead; stop the moment A = 0 or D = 0.
        int duels = 0, extraRounds = 0;
        if (d0 > 0)
            for (int pass = 0; (Params.Passes == 0 || pass < Params.Passes) && pass < MaxPasses && A() > 0 && defenders.Count > 0; pass++)
                foreach (var a in attackers.ToList())
                {
                    if (A() == 0 || defenders.Count == 0) break;
                    if (a.Dead) continue;
                    int e = ctx.Rng.NextInt(0, d0 - 1);
                    if (e >= defenders.Count) continue; // skips: chance D / D0 of a duel (Traced; the integer draw is DEFAULT)
                    int j = ctx.Rng.NextInt(0, defenders.Count - 1);
                    int roll = ctx.Rng.NextInt(0, Params.DieMax);
                    var d = defenders[j];
                    var ar = _troops.Data.Regiment(a.R.Regiment.Class)!; var dr = _troops.Data.Regiment(d.Class)!;
                    int x = DuelValue(roll, ar.Attack, dr.Defense, lAtt[a.R.Fleet.Id], lDef);
                    string outcome = DuelResult(x);
                    int aid = a.R.Regiment.Id, did = d.Id;
                    ctx.Emit((i, _) => new DuelResolved(i, day, aid, did, x, outcome));
                    duels++;
                    if (outcome == DuelOutcome.DefenderDestroyed)
                    {
                        defenders.RemoveAt(j);
                        string cls = d.Class; Side dside = d.Side!.Value;
                        _eco.RemoveObject(did);
                        ctx.Emit((i, _) => new RegimentDestroyed(i, day, did, cls, system, dside, "AssaultDuel"));
                    }
                    else if (outcome == DuelOutcome.AttackerDestroyed) KillAttacker(a, "AssaultDuel");
                    if (Params.CollateralMode == CollateralMode.PerDuelChance && ctx.Rng.NextInt(0, 99) < Params.CollateralPercent) extraRounds++;
                }

        // 4. Collateral.
        int rounds = Params.CollateralMode == CollateralMode.Truncate ? duels * Params.CollateralPercent / 100 : extraRounds;
        for (int round = 1; round <= rounds; round++) Collateral(ctx, day, system, round);

        // 5. Landing and 6. clear (the pending list was emptied at the top of the slot).
        int aLeft = A(), dLeft = defenders.Count;
        bool won = aLeft > 0 && dLeft == 0;
        if (won)
        {
            var landing = attackers.Where(x => !x.Dead).Take(Params.LandingCap).ToList();
            foreach (var x in landing) TroopModule.Land(x.R.Regiment, system);
            ctx.Emit((i, _) => new AssaultLanded(i, day, system, side, new ValueList<int>(landing.Select(x => x.R.Regiment.Id).ToList())));
        }
        ctx.Emit((i, _) => new AssaultResolved(i, day, system, side, won ? "Won" : "Failed", a0, aLeft, d0, dLeft, duels, rounds));
        if (won) _control.SetHolder(ctx, system, side, CaptureCause.Assault); // rule: m3-assault §What a win does
    }

    // rule: m3-assault §Resolution 4: the completed facilities by id, then Energy, then Raw; k = rand(0..n-1).
    private void Collateral(StepContext ctx, int day, string system, int round)
    {
        var facilities = _eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system).ToList();
        int n = facilities.Count + 2;
        int k = ctx.Rng.NextInt(0, n - 1);
        if (k < facilities.Count)
        {
            var fac = facilities[k]; int id = fac.Id;
            _eco.Destroy(ctx, day, fac, DestroyReason.Assault); // outright: no roll, no bombardment defense
            ctx.Emit((i, _) => new CollateralDamage(i, day, round, "Facility", id, true));
            return;
        }
        bool energy = k == facilities.Count;
        var sl = _eco.SlotsOf(system);
        bool changed = (energy ? sl.Energy : sl.Raw) > 0; // a round on a zero value does nothing and is not re-drawn
        string kind = energy ? "Energy" : "Raw";
        ctx.Emit((i, _) => new CollateralDamage(i, day, round, kind, 0, changed));
        if (!changed) return;
        _eco.SetSlots(system, energy ? sl.Energy - 1 : sl.Energy, energy ? sl.Raw : sl.Raw - 1);
        _eco.EnforceCapacity(ctx, system); // m2-slots-and-resources over-capacity check; its picks draw rand(0..n-1) each
    }
}
