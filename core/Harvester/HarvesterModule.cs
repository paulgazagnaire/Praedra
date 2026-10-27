using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Map;
using Praedra.Core.Support;

namespace Praedra.Core.Harvester;

/// <summary>
/// The harvester: the one-per-system build cap, the Destroy System order and its resolution at work-list slot 5.1 (after the bombardment's hook),
/// the sector flag at slot 5.6, and the two hooks the missions sheet calls (<see cref="HasHarvester"/>, <see cref="Destroy"/>). Holds settings and the
/// orders accepted this Step (they wait from step 1 to slot 5.1 of the same Step, so the list is empty whenever a game is saved); the state lives in
/// <see cref="GameState.Harvester"/> (destroyed systems, strikes per sector). Attach after the bombardment module and before the assault module
/// (it registers slot 5.1 after the bombardment hook); the character, mission and fog hooks are wired by the host after those modules exist.
/// After <see cref="GameSave.Load"/>, attach again. Draws random numbers only through the character module's roll (rule: m5-harvester §2 effect 3).
/// rule: m5-harvester (the whole sheet). Tags: Traced / Paul / DEFAULT as in the sheet.
/// </summary>
public sealed class HarvesterModule
{
    /// <summary>The harvester ship class (136). Traced. rule: m5-harvester §1.</summary>
    public const string ShipClass = EconomyModule.HarvesterClass;

    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly SupportModule _support;
    private readonly ControlModule _control;
    private readonly ConstructionModule _construction;
    private readonly List<DestroySystemOrder> _pending = new(); // accepted this Step; consumed at slot 5.1
    private string _lossCause = "Battle"; // why a harvester is leaving the game while a unit is lost (set around our own removals)

    public HarvesterParams Params { get; }

    /// <summary>Ends the missions running at the destroyed system as failures (MissionModule.OnSystemDestroyed). Null = no missions attached. rule: m5-harvester §2 effect 3.</summary>
    public Action<StepContext, string>? MissionsEnded { get; set; }
    /// <summary>The roll on the characters of the system (CharacterModule.HarvesterStrike: ctx, system, attacker). Null = no characters attached. rule: m5-harvester §2 effect 3.</summary>
    public Action<StepContext, string, Side>? CharacterRoll { get; set; }
    /// <summary>Move the survivors (CharacterModule.HarvesterRelocate: ctx, system, attacker, attacking fleet id). Null = no characters attached. rule: m5-harvester §2 effect 3.</summary>
    public Action<StepContext, string, Side, int>? CharacterRelocate { get; set; }
    /// <summary>Tell every side's picture the system is destroyed and destroy an HQ at rest there (FogModule.OnSystemDestroyed). Null = no fog attached. rule: m5-harvester §2 effects 4 and 6.</summary>
    public Action<StepContext, string>? FogNotice { get; set; }

    private HarvesterModule(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ControlModule control,
        ConstructionModule construction, HarvesterParams p)
    {
        _game = game; _galaxy = galaxy; _fleets = fleets; _eco = eco; _support = support; _control = control; _construction = construction; Params = p;
    }

    /// <summary>
    /// Register the Destroy System handler (step 1), the slot 5.1 hook (after the bombardment's, registered earlier) and the slot 5.6 flag hook, put the
    /// build cap on the construction module and listen for lost units. Throws <see cref="Characters.RuleMissingException"/> for a refused switch.
    /// rule: m5-harvester §1, §2, §3.
    /// </summary>
    public static HarvesterModule Attach(Game game, Galaxy galaxy, FleetModule fleets, EconomyModule eco, SupportModule support, ControlModule control,
        ConstructionModule construction, HarvesterParams? p = null)
    {
        p ??= new HarvesterParams();
        p.Validate();
        var m = new HarvesterModule(game, galaxy, fleets, eco, support, control, construction, p);
        game.RegisterOrderHandler<DestroySystemOrder>(m.HandleOrder);
        game.RegisterHook("5.1", m.Resolve);
        game.RegisterHook("5.6", m.RefreshSectorFlag);
        construction.ExtraBuildCheck = m.BuildCheck;
        eco.UnitLost += m.OnUnitLost;
        return m;
    }

    private HarvesterState S => _game.State.Harvester;
    private GameState State => _game.State;
    private EconomyState E => _game.State.Economy;

    // ================================================================== queries

    public bool IsDestroyed(string system) => S.IsDestroyed(system);

    /// <summary>Strikes counted in a sector so far (0 if none). rule: m5-harvester §2 effect 7.</summary>
    public int StrikesIn(string sector) => S.StrikesInSector.TryGetValue(sector, out var n) ? n : 0;

    private bool IsHarvester(EconObject o) => o.Category == ObjectCategory.CapitalShip && o.Class == ShipClass;

    /// <summary>The completed harvesters of a fleet, ship id order. A fleet member is always completed. rule: m5-harvester §2 legality 2.</summary>
    private IEnumerable<EconObject> HarvestersIn(Fleet f) =>
        f.Ships.Select(id => E.Objects.TryGetValue(id, out var o) ? o : null).Where(o => o != null && o.Completed && IsHarvester(o)).Select(o => o!);

    /// <summary>
    /// <c>HasHarvester(owner, system)</c>: true when a completed harvester of <paramref name="owner"/> is in a fleet at the system (not in hyperspace).
    /// Wired to <see cref="Missions.MissionModule.HasHarvester"/>. rule: m5-harvester §5.
    /// </summary>
    public bool HasHarvester(Side owner, string system) =>
        _fleets.FleetsAt(system).Any(f => f.Side == owner && HarvestersIn(f).Any());

    /// <summary>
    /// The Spike the Harvester target test: a completed harvester of <paramref name="owner"/> at the system, or (with <see cref="HarvesterParams.SpikeInYard"/>)
    /// an unfinished one in its yard projects there. Wired to <see cref="Missions.MissionModule.HasHarvester"/>. rule: m5-harvester §5.
    /// </summary>
    public bool IsSpikeTarget(Side owner, string system) =>
        HasHarvester(owner, system) || (Params.SpikeInYard && _construction.QueuedUnitOf(owner, system, ShipClass) != null);

    // ================================================================== building it (§1)

    /// <summary>
    /// The one-per-system cap: a harvester cannot be queued at a system where the side already has <see cref="HarvesterParams.CapPerSystem"/> in
    /// fleets there; an order for <c>Count</c> units counts them too. A new order replaces the yard's project, so the units of the old queue never
    /// count. No galaxy-wide cap. Reason <c>HarvesterLimit</c>. rule: m5-harvester §1 (One per system).
    /// </summary>
    private string? BuildCheck(BuildOrder o)
    {
        if (o.Class != ShipClass) return null;
        int held = _fleets.FleetsAt(o.System).Where(f => f.Side == o.Side).Sum(f => HarvestersIn(f).Count());
        return held + o.Count > Params.CapPerSystem ? HarvesterReject.HarvesterLimit : null;
    }

    /// <summary>
    /// A unit came to exist for use (GameHost wires <see cref="ConstructionModule.UnitAppeared"/> here, after the fleet module's handler): a harvester
    /// raises <see cref="HarvesterDeployed"/> to both sides. rule: m5-harvester §1 (New harvester Deployed).
    /// </summary>
    public void OnUnitAppeared(StepContext ctx, EconObject obj, UnitAppearance how)
    {
        if (!IsHarvester(obj) || !obj.Completed || obj.Side is not Side side) return;
        string? sys = _fleets.FleetOfShip(obj.Id)?.At ?? obj.System;
        if (sys == null) return;
        int day = ctx.Day + 1, id = obj.Id;
        ctx.Emit((i, _) => new HarvesterDeployed(i, day, side, id, sys));
    }

    // ================================================================== losing it (§4, §5)

    // A harvester leaves the game through DestroyUnit (a battle, our own removals): report it with the cause in force. rule: m5-harvester §4, §5.
    private void OnUnitLost(StepContext ctx, EconObject o, int day)
    {
        if (!IsHarvester(o) || !o.Completed || o.Side is not Side side) return;
        string? sys = _fleets.FleetOfShip(o.Id)?.At;
        int id = o.Id; string cause = _lossCause;
        ctx.Emit((i, _) => new HarvesterDestroyed(i, day, side, id, sys, cause));
    }

    /// <summary>
    /// <c>Harvester.Destroy(system, Sabotage)</c>: removes a harvester of <paramref name="owner"/> standing at the system (the lowest ship id if several)
    /// with the cause Sabotage; its fleet keeps its other ships; regiments aboard go with it, squadrons land (as in a battle loss). Event
    /// <see cref="HarvesterDestroyed"/>(Sabotage). Nothing happens if there is none. Wired to <see cref="Missions.MissionModule.DestroyHarvester"/>.
    /// rule: m5-harvester §5 (Traced reason 6).
    /// </summary>
    public void Destroy(StepContext ctx, string system, Side owner)
    {
        var ship = _fleets.FleetsAt(system).Where(f => f.Side == owner).SelectMany(HarvestersIn).OrderBy(o => o.Id).FirstOrDefault();
        if (ship == null)
        {
            // rule: m5-harvester §5 (Spike in the yard): no finished one here, so the first unfinished one in the yards goes, cause SabotageInYard
            if (Params.SpikeInYard && _construction.QueuedUnitOf(owner, system, ShipClass) is int queued)
            {
                _construction.RemoveQueuedUnit(ctx, queued, "Sabotage");
                int day = ctx.Day + 1;
                ctx.Emit((i, _) => new HarvesterDestroyed(i, day, owner, queued, system, "SabotageInYard"));
            }
            return;
        }
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
    /// No ownership, shield (unless <see cref="HarvesterParams.ShieldBlocksDestroy"/>) or neutrality check, and enemy fleets present never block it.
    /// rule: m5-harvester §2 Legality 1 to 6.
    /// </summary>
    private string? Check(Side side, int fleetId, bool atOrder)
    {
        if (!State.Fleets.TryGetValue(fleetId, out var f)) return RejectReason.UnknownFleet;                       // 1
        if (f.Side != side) return RejectReason.NotYours;
        if (f.At == null) return HarvesterReject.NotAtSystem;
        string system = f.At;
        if (!HarvestersIn(f).Any()) return HarvesterReject.NoHarvester;                                            // 2
        if (S.IsDestroyed(system)) return HarvesterReject.AlreadyDestroyed;                                        // 3
        // 4: no ownership, shield or neutrality check. 5: enemy fleets present do not block it. 6: Classic's shield facility is cut
        // (data/design/cut-classes.csv; Decided Paul 2026-10-25: the harvester has no shield), so nothing protects a system.
        if (atOrder && _pending.Any(p => p.Fleet == fleetId)) return HarvesterReject.OrderPending;
        return null;
    }

    private OrderResult HandleOrder(StepContext ctx, DestroySystemOrder o)
    {
        var reason = Check(o.Side, o.Fleet, atOrder: true);
        if (reason != null) return OrderResult.Reject(reason);
        _pending.Add(o);
        return OrderResult.Accept((i, d) => new OrderAccepted(i, d, o.Sequence, Fleet: o.Fleet));
    }

    // rule: m5-harvester §2 (slot 5.1, after the bombardment hook of the same slot; system name then fleet id, as the bombardment)
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

    // The effects, in the sheet's order. rule: m5-harvester §2 Effects 1 to 7 (all in slot 5.1).
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
        // Hand capturing the HQ: m5-fog-and-hq §F7, m5-setup-and-victory §Victory).
        ctx.Emit((i, _) => new SystemDestroyed(i, day, system, side, prior));
        FogNotice?.Invoke(ctx, system);

        // 7. The sector reaction.
        SectorReaction(ctx, day, system);
    }

    // rule: effect 1. Fleets in id order; aboard units (squadrons, regiments) first, then the ships, then a squadron-only fleet's squadrons.
    // A harvester among them goes with cause Harvester. The attacker's own fleets are untouched.
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
            ctx.Emit((i, _) => new FleetDestroyedByHarvester(i, day, system, fid, fs, list));
            _lossCause = "Harvester";
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
    // FacilityDestroyed with reason Harvester; regiments, special forces and based squadrons go through PlanetUnitDestroyed. Id order.
    private void RemovePlanet(StepContext ctx, int day, string system)
    {
        foreach (var o in E.Objects.Values.Where(x => x.System == system && x.AboardShip == 0 && x.Category != ObjectCategory.CapitalShip).ToList())
        {
            if (o.Category == ObjectCategory.Facility) { _eco.Destroy(ctx, day, o, DestroyReason.Harvester); continue; }
            int id = o.Id; string cls = o.Class; var owner = o.Side;
            _eco.RemoveObject(id);
            ctx.Emit((i, _) => new PlanetUnitDestroyed(i, day, system, id, cls, owner));
        }
    }

    // ================================================================== sector reaction (§2 effect 7)

    // Strikes are counted per sector for the whole game. Strike 1: every other non-destroyed system of the sector gets +SectorPenalty Collective support.
    // Strike 2 or later: every other non-destroyed system goes to Collective support 100; a neutral one becomes Collective-held; a Hand one starts an
    // uprising at once; a Collective one just goes to 100. None of it is damped by the harvester sector flag. Nothing outside the sector changes.
    // A system outside every sector has no sector: no strike is counted and nothing reacts (DEFAULT, the sheet names no such case).
    private void SectorReaction(StepContext ctx, int day, string system)
    {
        if (Params.Fallout == HarvesterFallout.Classic) { ClassicFallout(ctx, system); return; }
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
                _support.ChangeSupport(ctx, name, Side.Collective, Params.SectorPenalty, SupportCause.Harvester, dampen: false);
                continue;
            }
            _support.ChangeSupport(ctx, name, Side.Collective, Params.RevoltCollectiveSupport, SupportCause.Harvester, dampen: false); // clamped to the scale: 100
            // Under derived control (B5a section 9) the support change alone turns a neutral world, and a Hand world with no regiment, Collective;
            // a Hand world still held (by its garrison) riots. Under AssaultOnly the old explicit capture of a neutral world.
            if (_control.Rule == ControlRule.AssaultOnly && _fleets.OwnerOf(name) == null) _control.SetHolder(ctx, name, Side.Collective, CaptureCause.HarvesterRevolt);
            if (_fleets.OwnerOf(name) == Side.Hand) _support.StartUprisingNow(ctx, name);
        }
        string sec = star.Sector; var list = new ValueList<string>(changed);
        ctx.Emit((i, _) => new SectorStruck(i, day, sec, strike, list));
    }

    // Classic fallout: every populated, ever-held world of the galaxy (name order), Heartland worlds -10 for the destroyer always, outer worlds -2
    // where the destroyer's support is below 90, through ChangeSupport with the normal damping (every event effect goes through it). The destroyed
    // world itself has no support value. No strike count, no revolt. rule: m5-harvester §2 effect 7 (HarvesterFallout = Classic; B5c section 3).
    private void ClassicFallout(StepContext ctx, string destroyed)
    {
        const Side destroyer = Side.Hand; // only the Hand has a harvester; the losing side is the destroyer (inferred, B5c)
        foreach (var star in _galaxy.Systems)
        {
            string name = star.Name;
            if (name == destroyed || S.IsDestroyed(name) || !_eco.SlotsOf(name).Populated || !_control.EverHeld(name)) continue;
            bool core = star.Group == "heartland";
            int loss = core ? Params.ClassicHeartlandLoss : Params.ClassicReachLoss;
            int threshold = core ? Params.ClassicHeartlandThreshold : Params.ClassicReachThreshold;
            if (_eco.SupportOf(name, destroyer) >= threshold) continue;
            _support.ChangeSupport(ctx, name, destroyer, -loss, SupportCause.Harvester);
        }
    }

    // ================================================================== the sector flag (§3, slot 5.6)

    // rule: m5-harvester §3 (m2-popular-support §harvester sector flag): every system of a sector holding one of the Hand's harvesters (in a fleet at a
    // system of that sector) carries the flag; all others are cleared. Recomputed once per day, replacing setup-only SetHarvesterSector calls.
    private void RefreshSectorFlag(StepContext ctx)
    {
        var sectors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in State.Fleets.Values)
            if (f.Side == Side.Hand && f.At is string at && HarvestersIn(f).Any() && _galaxy.TryFindSystem(at, out _) && !_galaxy.Get(at).IsOutsideSectors)
                sectors.Add(_galaxy.Get(at).Sector);
        var want = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var sector in _galaxy.Sectors.Where(s => sectors.Contains(s.Name)))
            foreach (var idx in sector.Members) want.Add(_galaxy.Systems[idx].Name);
        foreach (var s in State.Support.Harvester.Where(s => !want.Contains(s)).ToList()) _support.SetHarvesterSystem(s, false);
        foreach (var s in want) _support.SetHarvesterSystem(s, true);
    }
}
