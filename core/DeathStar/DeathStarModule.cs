using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;
using Rebellion.Core.Support;

namespace Rebellion.Core.DeathStar;

/// <summary>
/// The Death Star: the one-per-system build cap, the Destroy System order and its resolution at work-list slot 5.1 (after the bombardment's hook),
/// the sector flag at slot 5.6, and the two hooks the missions sheet calls (<see cref="HasDeathStar"/>, <see cref="Destroy"/>). Holds settings and the
/// orders accepted this Step (they wait from step 1 to slot 5.1 of the same Step, so the list is empty whenever a game is saved); the state lives in
/// <see cref="GameState.DeathStar"/> (destroyed systems, strikes per sector). Attach after the bombardment module and before the assault module
/// (it registers slot 5.1 after the bombardment hook); the character, mission and fog hooks are wired by the host after those modules exist.
/// After <see cref="GameSave.Load"/>, attach again. Draws random numbers only through the character module's roll (rule: m5-death-star §2 effect 3).
/// rule: m5-death-star (the whole sheet). Tags: Traced / Paul / DEFAULT as in the sheet.
/// </summary>
public sealed class DeathStarModule
{
    /// <summary>The Death Star ship class (136). Traced. rule: m5-death-star §1.</summary>
    public const string ShipClass = EconomyModule.DeathStarClass;

    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly SupportModule _support;
    private readonly ControlModule _control;
    private readonly ConstructionModule _construction;
    private readonly DefenseModule _def;
    private readonly List<DestroySystemOrder> _pending = new(); // accepted this Step; consumed at slot 5.1
    private string _lossCause = "Battle"; // why a Death Star is leaving the game while a unit is lost (set around our own removals)

    public DeathStarParams Params { get; }

    /// <summary>Ends the missions running at the destroyed system as failures (MissionModule.OnSystemDestroyed). Null = no missions attached. rule: m5-death-star §2 effect 3.</summary>
    public Action<StepContext, string>? MissionsEnded { get; set; }
    /// <summary>The roll on the characters of the system (CharacterModule.DeathStarStrike: ctx, system, attacker). Null = no characters attached. rule: m5-death-star §2 effect 3.</summary>
    public Action<StepContext, string, Side>? CharacterRoll { get; set; }
    /// <summary>Move the survivors (CharacterModule.DeathStarRelocate: ctx, system, attacker, attacking fleet id). Null = no characters attached. rule: m5-death-star §2 effect 3.</summary>
    public Action<StepContext, string, Side, int>? CharacterRelocate { get; set; }
    /// <summary>Tell every side's picture the system is destroyed and destroy an HQ at rest there (FogModule.OnSystemDestroyed). Null = no fog attached. rule: m5-death-star §2 effects 4 and 6.</summary>
    public Action<StepContext, string>? FogNotice { get; set; }

    private DeathStarModule(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ControlModule control,
        ConstructionModule construction, DefenseModule def, DeathStarParams p)
    {
        _game = game; _galaxy = galaxy; _fleets = fleets; _eco = eco; _support = support; _control = control; _construction = construction; _def = def; Params = p;
    }

    /// <summary>
    /// Register the Destroy System handler (step 1), the slot 5.1 hook (after the bombardment's, registered earlier) and the slot 5.6 flag hook, put the
    /// build cap on the construction module and listen for lost units. Throws <see cref="Characters.RuleMissingException"/> for a refused switch.
    /// rule: m5-death-star §1, §2, §3.
    /// </summary>
    public static DeathStarModule Attach(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ControlModule control,
        ConstructionModule construction, DefenseModule def, DeathStarParams? p = null)
    {
        p ??= new DeathStarParams();
        p.Validate();
        var m = new DeathStarModule(game, galaxy, fleets, eco, support, control, construction, def, p);
        game.RegisterOrderHandler<DestroySystemOrder>(m.HandleOrder);
        game.RegisterHook("5.1", m.Resolve);
        game.RegisterHook("5.6", m.RefreshSectorFlag);
        construction.ExtraBuildCheck = m.BuildCheck;
        eco.UnitLost += m.OnUnitLost;
        return m;
    }

    private DeathStarState S => _game.State.DeathStar;
    private GameState State => _game.State;
    private EconomyState E => _game.State.Economy;
    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

    // ================================================================== queries

    public bool IsDestroyed(string system) => S.IsDestroyed(system);

    /// <summary>Strikes counted in a sector so far (0 if none). rule: m5-death-star §2 effect 7.</summary>
    public int StrikesIn(string sector) => S.StrikesInSector.TryGetValue(sector, out var n) ? n : 0;

    private bool IsDeathStar(EconObject o) => o.Category == ObjectCategory.CapitalShip && o.Class == ShipClass;

    /// <summary>The completed Death Stars of a fleet, ship id order. A fleet member is always completed. rule: m5-death-star §2 legality 2.</summary>
    private IEnumerable<EconObject> DeathStarsIn(Fleet f) =>
        f.Ships.Select(id => E.Objects.TryGetValue(id, out var o) ? o : null).Where(o => o != null && o.Completed && IsDeathStar(o)).Select(o => o!);

    /// <summary>
    /// <c>HasDeathStar(owner, system)</c>: true when a completed Death Star of <paramref name="owner"/> is in a fleet at the system (not in hyperspace).
    /// Wired to <see cref="Missions.MissionModule.HasDeathStar"/>. rule: m5-death-star §5.
    /// </summary>
    public bool HasDeathStar(Side owner, string system) =>
        _fleets.FleetsAt(system).Any(f => f.Side == owner && DeathStarsIn(f).Any());

    // ================================================================== building it (§1)

    /// <summary>
    /// The one-per-system cap: a Death Star cannot be queued at a system where the side already has <see cref="DeathStarParams.CapPerSystem"/> in
    /// fleets there; an order for <c>Count</c> units counts them too. A new order replaces the yard's project, so the units of the old queue never
    /// count. No galaxy-wide cap. Reason <c>DeathStarLimit</c>. rule: m5-death-star §1 (One per system).
    /// </summary>
    private string? BuildCheck(BuildOrder o)
    {
        if (o.Class != ShipClass) return null;
        int held = _fleets.FleetsAt(o.System).Where(f => f.Side == o.Side).Sum(f => DeathStarsIn(f).Count());
        return held + o.Count > Params.CapPerSystem ? DeathStarReject.DeathStarLimit : null;
    }

    /// <summary>
    /// A unit came to exist for use (GameHost wires <see cref="ConstructionModule.UnitAppeared"/> here, after the fleet module's handler): a Death Star
    /// raises <see cref="DeathStarDeployed"/> to both sides. rule: m5-death-star §1 (New Death Star Deployed).
    /// </summary>
    public void OnUnitAppeared(StepContext ctx, EconObject obj, UnitAppearance how)
    {
        if (!IsDeathStar(obj) || !obj.Completed || obj.Side is not Side side) return;
        string? sys = _fleets.FleetOfShip(obj.Id)?.At ?? obj.System;
        if (sys == null) return;
        int day = ctx.Day + 1, id = obj.Id;
        ctx.Emit((i, _) => new DeathStarDeployed(i, day, side, id, sys));
    }

    // ================================================================== losing it (§4, §5)

    // A Death Star leaves the game through DestroyUnit (a battle, our own removals): report it with the cause in force. rule: m5-death-star §4, §5.
    private void OnUnitLost(StepContext ctx, EconObject o, int day)
    {
        if (!IsDeathStar(o) || !o.Completed || o.Side is not Side side) return;
        string? sys = _fleets.FleetOfShip(o.Id)?.At;
        int id = o.Id; string cause = _lossCause;
        ctx.Emit((i, _) => new DeathStarDestroyed(i, day, side, id, sys, cause));
    }

    /// <summary>
    /// <c>DeathStar.Destroy(system, Sabotage)</c>: removes a Death Star of <paramref name="owner"/> standing at the system (the lowest ship id if several)
    /// with the cause Sabotage; its fleet keeps its other ships; regiments aboard go with it, squadrons land (as in a battle loss). Event
    /// <see cref="DeathStarDestroyed"/>(Sabotage). Nothing happens if there is none. Wired to <see cref="Missions.MissionModule.DestroyDeathStar"/>.
    /// rule: m5-death-star §5 (Traced reason 6).
    /// </summary>
    public void Destroy(StepContext ctx, string system, Side owner)
    {
        var ship = _fleets.FleetsAt(system).Where(f => f.Side == owner).SelectMany(DeathStarsIn).OrderBy(o => o.Id).FirstOrDefault();
        if (ship == null) return;
        _lossCause = "Sabotage";
        try { LoseShip(ctx, ctx.Day + 1, ship); }
        finally { _lossCause = "Battle"; }
    }

    // A ship leaves with the regiments aboard (m3-fleets Q6, as BattleModule.DestroyUnit); fighters aboard land when the ship leaves its fleet.
    private void LoseShip(StepContext ctx, int day, EconObject ship)
    {
        foreach (var reg in E.Objects.Values.Where(r => r.AboardShip == ship.Id && r.Category == ObjectCategory.Regiment).OrderBy(r => r.Id).ToList())
            _eco.DestroyUnit(ctx, day, reg);
        _eco.DestroyUnit(ctx, day, ship);
    }

    // ================================================================== Destroy System (§2)

    /// <summary>Queue a Destroy System order (validated in the next Step). Returns its sequence number.</summary>
    public long SubmitDestroySystem(Side side, int fleet) => _game.Submit(seq => new DestroySystemOrder(seq, side, fleet));

    /// <summary>
    /// The legality list, in the sheet's order; null = all hold. Run at step 1 (<paramref name="atOrder"/> adds the pending check) and again at slot 5.1.
    /// No ownership, shield (unless <see cref="DeathStarParams.ShieldBlocksDestroy"/>) or neutrality check, and enemy fleets present never block it.
    /// rule: m5-death-star §2 Legality 1 to 6.
    /// </summary>
    private string? Check(Side side, int fleetId, bool atOrder)
    {
        if (!State.Fleets.TryGetValue(fleetId, out var f)) return RejectReason.UnknownFleet;                       // 1
        if (f.Side != side) return RejectReason.NotYours;
        if (f.At == null) return DeathStarReject.NotAtSystem;
        string system = f.At;
        if (!DeathStarsIn(f).Any()) return DeathStarReject.NoDeathStar;                                            // 2
        if (S.IsDestroyed(system)) return DeathStarReject.AlreadyDestroyed;                                        // 3
        // 4: no ownership, shield or neutrality check. 5: enemy fleets present do not block it.
        if (Params.ShieldBlocksDestroy && _fleets.OwnerOf(system) != side                                          // 6
            && _def.DefensesOf(system).Any(d => d.Class.Job == DefenseJob.DeathStarShield)) return DeathStarReject.ShieldProtected;
        if (atOrder && _pending.Any(p => p.Fleet == fleetId)) return DeathStarReject.OrderPending;
        return null;
    }

    private OrderResult HandleOrder(StepContext ctx, DestroySystemOrder o)
    {
        var reason = Check(o.Side, o.Fleet, atOrder: true);
        if (reason != null) return OrderResult.Reject(reason);
        _pending.Add(o);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, Fleet: o.Fleet));
    }

    // rule: m5-death-star §2 (slot 5.1, after the bombardment hook of the same slot; system name then fleet id, as the bombardment)
    private void Resolve(StepContext ctx)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.OrderBy(o => State.Fleets.TryGetValue(o.Fleet, out var f) ? f.At ?? "" : "", StringComparer.Ordinal).ThenBy(o => o.Fleet).ToList();
        _pending.Clear();
        int day = ctx.Day + 1;
        foreach (var o in batch)
        {
            var reason = Check(o.Side, o.Fleet, atOrder: false);
            if (reason != null)
            {
                string at = State.Fleets.TryGetValue(o.Fleet, out var cf) ? cf.At ?? "" : "";
                ctx.Emit((i, _) => new DestroySystemCancelled(i, day, o.Fleet, o.Side, at, reason));
                continue;
            }
            Strike(ctx, day, State.Fleets[o.Fleet]);
        }
    }

    // The effects, in the sheet's order. rule: m5-death-star §2 Effects 1 to 7 (all in slot 5.1).
    private void Strike(StepContext ctx, int day, Fleet attacker)
    {
        string system = attacker.At!;
        Side side = attacker.Side;
        Side? prior = _fleets.OwnerOf(system);

        // Running missions at the system end as failures, their characters stay as characters on the planet. rule: effect 3
        MissionsEnded?.Invoke(ctx, system);

        // 1. Ships: every fleet at the system that is not the attacker's side (enemy and neutral) dies with everything aboard.
        DestroyFleets(ctx, day, system, side);

        // 2. The planet: every facility (completed or queued), defense, shield, regiment, special force and based squadron, whoever owns it. No refund.
        _construction.CancelProjectsOnDestroy(ctx, system);
        RemovePlanet(ctx, day, system);

        // 3. Characters on the planet (any side): the roll on the minors, then the survivors are moved.
        CharacterRoll?.Invoke(ctx, system, side);
        CharacterRelocate?.Invoke(ctx, system, side, attacker.Id);

        // 5. The system itself becomes Destroyed: holder none, no support value, no slots, no production. It stays on the map as passable debris.
        S.Destroyed.Add(system);
        _fleets.SetOwner(system, null);
        _eco.ClearSlotsForDestroyedSystem(system);
        _support.OnSystemDestroyed(ctx, system);
        _fleets.RefreshBlockadeFlag(ctx, system, day);

        // 6. The event to both sides; every side's picture learns it, and 4. an HQ at rest there is destroyed (the victory step reads it as the
        // Empire capturing the HQ: m5-fog-and-hq §F7, m5-setup-and-victory §Victory).
        ctx.Emit((i, _) => new SystemDestroyed(i, day, system, side, prior));
        FogNotice?.Invoke(ctx, system);

        // 7. The sector reaction.
        SectorReaction(ctx, day, system);
    }

    // rule: effect 1. Fleets in id order; aboard units (squadrons, regiments) first, then the ships, then a squadron-only fleet's squadrons.
    // A Death Star among them goes with cause DeathStar. The attacker's own fleets are untouched.
    private void DestroyFleets(StepContext ctx, int day, string system, Side attacker)
    {
        foreach (var f in _fleets.FleetsAt(system).Where(f => f.Side != attacker).ToList())
        {
            var aboard = new List<EconObject>();
            foreach (var shipId in f.Ships.ToList())
                aboard.AddRange(E.Objects.Values.Where(u => u.AboardShip == shipId)); // id order
            var squads = f.Squadrons.Select(id => E.Objects[id]).ToList();
            var units = new List<int>();
            units.AddRange(f.Ships); units.AddRange(f.Squadrons); units.AddRange(aboard.Select(u => u.Id));
            int fid = f.Id; var fs = f.Side; var list = new ValueList<int>(units);
            ctx.Emit((i, _) => new FleetDestroyedByDeathStar(i, day, system, fid, fs, list));
            _lossCause = "DeathStar";
            try
            {
                foreach (var u in aboard) _eco.DestroyUnit(ctx, day, u);
                foreach (var shipId in f.Ships.ToList()) _eco.DestroyUnit(ctx, day, E.Objects[shipId]);
                foreach (var sq in squads) if (E.Objects.ContainsKey(sq.Id)) _eco.DestroyUnit(ctx, day, sq);
            }
            finally { _lossCause = "Battle"; }
        }
    }

    // rule: effect 2. Facilities (completed or still queued: construction already cancelled the projects, so only setup leftovers remain) go through
    // FacilityDestroyed with reason DeathStar; regiments, special forces and based squadrons go through PlanetUnitDestroyed. Id order.
    private void RemovePlanet(StepContext ctx, int day, string system)
    {
        foreach (var o in E.Objects.Values.Where(x => x.System == system && x.AboardShip == 0 && x.Category != ObjectCategory.CapitalShip).ToList())
        {
            if (o.Category == ObjectCategory.Facility) { _eco.Destroy(ctx, day, o, DestroyReason.DeathStar); continue; }
            int id = o.Id; string cls = o.Class; var owner = o.Side;
            _eco.RemoveObject(id);
            ctx.Emit((i, _) => new PlanetUnitDestroyed(i, day, system, id, cls, owner));
        }
    }

    // ================================================================== sector reaction (§2 effect 7)

    // Strikes are counted per sector for the whole game. Strike 1: every other non-destroyed system of the sector gets +SectorPenalty Alliance support.
    // Strike 2 or later: every other non-destroyed system goes to Alliance support 100; a neutral one becomes Alliance-held; an Imperial one starts an
    // uprising at once; an Alliance one just goes to 100. None of it is damped by the Death Star sector flag. Nothing outside the sector changes.
    // A system outside every sector has no sector: no strike is counted and nothing reacts (DEFAULT, the sheet names no such case).
    private void SectorReaction(StepContext ctx, int day, string system)
    {
        if (Params.Fallout == DeathStarFallout.Classic) { ClassicFallout(ctx, system); return; }
        var star = _galaxy.Get(system);
        if (star.IsOutsideSectors) return;
        int strike = StrikesIn(star.Sector) + 1;
        S.StrikesInSector[star.Sector] = strike;
        var sector = _galaxy.Sectors.First(s => s.Name == star.Sector);
        var changed = new List<string>();
        foreach (var idx in sector.Members) // name order
        {
            string name = _galaxy.Systems[idx].Name;
            if (name == system || S.IsDestroyed(name)) continue;
            changed.Add(name);
            if (strike < Params.RevoltFromStrike)
            {
                _support.ChangeSupport(ctx, name, Side.Alliance, Params.SectorPenalty, SupportCause.DeathStar, dampen: false);
                continue;
            }
            _support.ChangeSupport(ctx, name, Side.Alliance, Params.RevoltAllianceSupport, SupportCause.DeathStar, dampen: false); // clamped to the scale: 100
            // Under derived control (B5a section 9) the support change alone turns a neutral world, and an Imperial world with no regiment, Alliance;
            // an Imperial world still held (by its garrison) riots. Under AssaultOnly the old explicit capture of a neutral world.
            if (_control.Rule == ControlRule.AssaultOnly && _fleets.OwnerOf(name) == null) _control.SetHolder(ctx, name, Side.Alliance, CaptureCause.DeathStarRevolt);
            if (_fleets.OwnerOf(name) == Side.Empire) _support.StartUprisingNow(ctx, name);
        }
        string sec = star.Sector; var list = new ValueList<string>(changed);
        ctx.Emit((i, _) => new SectorStruck(i, day, sec, strike, list));
    }

    // Classic (1998) fallout: every populated, ever-held world of the galaxy (name order), core worlds -10 for the destroyer always, rim worlds -2
    // where the destroyer's support is below 90, through ChangeSupport with the normal damping (every event effect goes through it). The destroyed
    // world itself has no support value. No strike count, no revolt. rule: m5-death-star §2 effect 7 (DeathStarFallout = Classic; B5c section 3).
    private void ClassicFallout(StepContext ctx, string destroyed)
    {
        const Side destroyer = Side.Empire; // only the Empire has a Death Star; the losing side is the destroyer (inferred, B5c)
        foreach (var star in _galaxy.Systems)
        {
            string name = star.Name;
            if (name == destroyed || S.IsDestroyed(name) || !_eco.SlotsOf(name).Populated || !_control.EverHeld(name)) continue;
            bool core = star.Group == "core";
            int loss = core ? Params.ClassicCoreLoss : Params.ClassicRimLoss;
            int threshold = core ? Params.ClassicCoreThreshold : Params.ClassicRimThreshold;
            if (_eco.SupportOf(name, destroyer) >= threshold) continue;
            _support.ChangeSupport(ctx, name, destroyer, -loss, SupportCause.DeathStar);
        }
    }

    // ================================================================== the sector flag (§3, slot 5.6)

    // rule: m5-death-star §3 (m2-popular-support §Death Star sector flag): every system of a sector holding one of the Empire's Death Stars (in a fleet at a
    // system of that sector) carries the flag; all others are cleared. Recomputed once per day, replacing setup-only SetDeathStarSector calls.
    private void RefreshSectorFlag(StepContext ctx)
    {
        var sectors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in State.Fleets.Values)
            if (f.Side == Side.Empire && f.At is string at && DeathStarsIn(f).Any() && _galaxy.TryFindSystem(at, out _) && !_galaxy.Get(at).IsOutsideSectors)
                sectors.Add(_galaxy.Get(at).Sector);
        var want = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sector in _galaxy.Sectors.Where(s => sectors.Contains(s.Name)))
            foreach (var idx in sector.Members) want.Add(_galaxy.Systems[idx].Name);
        foreach (var s in State.Support.DeathStar.Where(s => !want.Contains(s)).ToList()) _support.SetDeathStarSystem(s, false);
        foreach (var s in want) _support.SetDeathStarSystem(s, true);
    }
}
