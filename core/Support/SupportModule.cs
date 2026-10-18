using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Map;

namespace Rebellion.Core.Support;

/// <summary>
/// Popular support, its two timers, the garrison requirement, uprisings and their incidents. Registers hooks 6.1 (garrison and
/// uprising refresh, arming and disarming timers), 6.2 (support timers due) and 6.3 (incident timers due) on a <see cref="Game"/>.
/// Holds data and settings only; the state lives in <see cref="GameState.Support"/> and <see cref="EconomyState.AllianceSupport"/>.
/// After <see cref="GameSave.Load"/>, attach again to the loaded game. Attach after the fleet and economy modules.
/// rule: m2-popular-support, m2-uprisings-and-garrison.
/// </summary>
public sealed class SupportModule
{
    private readonly Game _game;
    private readonly Galaxy _galaxy;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;

    public SupportParams Params { get; }
    public GarrisonWeights Weights { get; }

    /// <summary>Classic = flat regiment count, New = weighted. DEFAULT Classic (the ruleset switch). rule: m2-uprisings-and-garrison §Garrison G and the check.</summary>
    public GarrisonRuleset Ruleset { get; set; } = GarrisonRuleset.Classic;
    /// <summary>The 30-day presence (blockade) timer. DEFAULT on in both rulesets (the traced behaviour). rule: m2-popular-support §Defaults (blockade_support_effect).</summary>
    public bool BlockadeSupportEffect { get; set; } = true;

    private SupportModule(Game game, Galaxy galaxy, EconomyModule eco, FleetModule fleets, GarrisonWeights weights, SupportParams p)
    {
        _game = game; _galaxy = galaxy; _eco = eco; _fleets = fleets; Weights = weights; Params = p;
    }

    /// <summary>Register hooks 6.1 to 6.3 and wire the Death Star flag into <see cref="EconomyModule.IsDeathStarSector"/>.</summary>
    public static SupportModule Attach(Game game, Galaxy galaxy, EconomyModule eco, FleetModule fleets, GarrisonWeights? weights = null, SupportParams? p = null)
    {
        var prm = p ?? new SupportParams();
        var m = new SupportModule(game, galaxy, eco, fleets, weights ?? GarrisonWeights.Flat(prm.DefaultWeight10), prm);
        game.RegisterHook("6.1", m.RefreshGarrisons);
        game.RegisterHook("6.2", m.SupportTimers);
        game.RegisterHook("6.3", m.IncidentTimers);
        eco.IsDeathStarSector = m.IsDeathStarSector;
        return m;
    }

    private SupportState S => _game.State.Support;
    private static Side Other(Side s) => s == Side.Empire ? Side.Alliance : Side.Empire;

    // ================================================================== setup (between Steps; not orders, no events)

    /// <summary>
    /// Setup only: set or clear the Death Star flag on every system of a sector. M2 sets nothing itself (the Death Star is M4/M6).
    /// rule: m2-popular-support §Death Star sector flag.
    /// </summary>
    public void SetDeathStarSector(string sector, bool on)
    {
        var sec = _galaxy.Sectors.FirstOrDefault(s => s.Name == sector) ?? throw new ArgumentException($"Unknown sector '{sector}'.");
        foreach (var i in sec.Members) SetDeathStarSystem(_galaxy.Systems[i].Name, on);
    }

    /// <summary>Setup only: set or clear the flag on one system (tests with synthetic data).</summary>
    public void SetDeathStarSystem(string system, bool on)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (on) S.DeathStar.Add(system); else S.DeathStar.Remove(system);
    }

    // ================================================================== queries

    public bool IsDeathStarSector(string system) => S.DeathStar.Contains(system);
    public bool IsUprising(string system) => S.Systems.TryGetValue(system, out var s) && s.Uprising;
    public SystemSupportState? StateOf(string system) => S.Systems.TryGetValue(system, out var s) ? s : null;

    /// <summary>Required garrison N of a system now (0 for a neutral one). rule: m2-uprisings-and-garrison §Required garrison N.</summary>
    public int RequiredGarrison(string system)
    {
        if (_fleets.OwnerOf(system) is not Side holder) return 0;
        return RequiredGarrison(Params, _eco.SupportOf(system, holder), IsUprising(system), holder == Side.Empire && IsDeathStarSector(system));
    }

    /// <summary>
    /// N = 0 if s >= 60 else ceil((60 - s) / 10); x2 in uprising; then / 2 (truncating) for the Empire in a Death Star sector.
    /// Order 1, 2, 3 (DEFAULT). rule: m2-uprisings-and-garrison §Required garrison N (GNPRTB 7761, 7762, 7682, 7680).
    /// </summary>
    public static int RequiredGarrison(SupportParams p, int controllerSupport, bool uprising, bool empireInDeathStarSector)
    {
        int n = controllerSupport >= p.GarrisonFreeSupport ? 0 : (p.GarrisonFreeSupport - controllerSupport + p.GarrisonStep - 1) / p.GarrisonStep;
        if (uprising) n *= p.UprisingMultiplier;
        if (empireInDeathStarSector) n /= p.DeathStarGarrisonDivisor;
        return n;
    }

    /// <summary>
    /// G in tenths: the holder's completed regiments standing on the system (not aboard fleets, not special forces); 10 each in
    /// Classic, the weight file's value in New. rule: m2-uprisings-and-garrison §Garrison G and the check.
    /// </summary>
    public int Garrison10(string system)
    {
        if (_fleets.OwnerOf(system) is not Side holder) return 0;
        int sum = 0;
        foreach (var o in HolderRegiments(system, holder)) sum += Weight10(o);
        return sum;
    }

    /// <summary>R: the holder's completed regiments on the system, flat count (both rulesets). rule: m2-uprisings-and-garrison §Incident (Score).</summary>
    public int RegimentCount(string system) =>
        _fleets.OwnerOf(system) is Side h ? HolderRegiments(system, h).Count() : 0;

    private int Weight10(EconObject o) => Ruleset == GarrisonRuleset.New ? Weights.Weight10(o.Class) : Params.ClassicWeight10;

    private IEnumerable<EconObject> HolderRegiments(string system, Side holder) =>
        _eco.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.System == system && o.Side == holder);

    // ================================================================== ChangeSupport

    /// <summary>
    /// The one entry point for every change of support. <paramref name="amount"/> is from the point of view of <paramref name="side"/>.
    /// Alliance axis; Death Star damping of rises (truncating); clamp 0..100; <see cref="SupportChanged"/> if the value moved. Returns the
    /// new Alliance share. A destroyed system has no support value: nothing changes (rule: m5-death-star §2 effect 5).
    /// <paramref name="dampen"/> false skips step 2, the Death Star damping: the Death Star's own sector reaction passes false (rule: m5-death-star §2
    /// effect 7: "The Empire's Death Star damping does not apply to these two changes").
    /// rule: m2-popular-support §One entry point for every change (steps 1 to 4).
    /// </summary>
    public int ChangeSupport(StepContext ctx, string system, Side side, int amount, SupportCause cause, bool dampen = true)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        if (_eco.IsDestroyed(system)) return 0;
        int old = _eco.SupportOf(system, Side.Alliance);
        int d = side == Side.Alliance ? amount : -amount;                       // 1. Alliance axis
        if (dampen && d > 0 && IsDeathStarSector(system)) d /= Params.DeathStarDampingDivisor; // 2. damping, only rises, toward zero
        int now = Math.Clamp(old + d, Params.SupportMin, Params.SupportMax);    // 3. clamp
        if (now == old) return old;
        _game.State.Economy.AllianceSupport[system] = now;
        int day = ctx.Day + 1;
        ctx.Emit((i, _) => new SupportChanged(i, day, system, old, now, cause));  // 4. event
        AfterSupportChange?.Invoke(ctx, system); // every change runs the control recompute at once (B5a section 2; B5c section 3)
        return now;
    }

    /// <summary>
    /// Called inside <see cref="ChangeSupport"/> after the value moved, with (ctx, system): the control module recomputes the holder at once (the
    /// 1998 Loyalty notifier <c>0x5109f0</c>, called by the support setter). Null = nothing. rule: m2-control-and-capture §When the rule runs (B5a section 2).
    /// </summary>
    public Action<StepContext, string>? AfterSupportChange { get; set; }

    // ================================================================== 6.1 garrison and uprising refresh

    // rule: m2-uprisings-and-garrison §Uprising start, keep, end (systems in name order); m2-popular-support §Periodic effects
    // (timers armed on condition-true, disarmed on condition-false)
    private void RefreshGarrisons(StepContext ctx)
    {
        int t = ctx.Day + 1;
        var systems = new SortedSet<string>(_game.State.SystemOwners.Keys, StringComparer.Ordinal);
        foreach (var k in S.Systems.Keys) systems.Add(k);

        // One pass over the objects: holder's completed regiments per system, in tenths. Lookup only, never iterated.
        var g10 = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in _eco.Objects)
        {
            if (o.Category != ObjectCategory.Regiment || !o.Completed || o.System == null || o.Side == null) continue;
            if (_fleets.OwnerOf(o.System) != o.Side) continue;
            g10[o.System] = g10.GetValueOrDefault(o.System) + Weight10(o);
        }

        foreach (var sys in systems)
        {
            Side? holder = _fleets.OwnerOf(sys);
            var st = StateOf(sys);

            // A flag whose holder is gone or changed: cleared first (DEFAULT, capture sheet), then judged afresh for the new holder.
            if (st is { Uprising: true } && (holder == null || holder != Other(st.Rebels)))
                EndUprising(ctx, t, sys, st, holder == null ? "NoHolder" : "ControlChanged");

            if (holder is Side h)
            {
                bool on = st is { Uprising: true };
                int n = RequiredGarrison(Params, _eco.SupportOf(sys, h), on, h == Side.Empire && IsDeathStarSector(sys));
                int surplus10 = g10.GetValueOrDefault(sys) - 10 * n; // N in tenths; Classic weights are 10 each
                if (!on && surplus10 < 0)
                    StartUprising(ctx, t, sys, h);
                else if (on && surplus10 >= 0)
                    EndUprising(ctx, t, sys, st!, n == 0 ? "NoRequirement" : "Garrison");

                RefreshPresence(t, sys, h);
            }
            else
            {
                DisarmPresence(sys);
            }
            S.Prune(sys);
        }
    }

    private void StartUprising(StepContext ctx, int t, string sys, Side holder)
    {
        var st = S.Touch(sys);
        st.Uprising = true;
        st.Rebels = Other(holder);
        st.ErosionDue = t + Params.ErosionPeriod;                                                  // timer 0x384
        st.IncidentDue = t + Params.IncidentIntervalMin + ctx.Rng.NextInt(0, Params.IncidentIntervalSpread); // timer 0x38d, the one draw
        ctx.Emit((i, _) => new UprisingStarted(i, t, sys, holder));
    }

    private void EndUprising(StepContext ctx, int t, string sys, SystemSupportState st, string reason)
    {
        st.Uprising = false; st.ErosionDue = 0; st.IncidentDue = 0;
        ctx.Emit((i, _) => new UprisingEnded(i, t, sys, reason));
    }

    /// <summary>
    /// Start an uprising at a held system at once, whatever the garrison (the Death Star's second strike in a sector, Imperial systems). No-op without
    /// a holder or with an uprising already on. The timers are armed as in slot 6.1 (one draw for the incident timer); slot 6.1 later judges it by
    /// the normal garrison rule. rule: m5-death-star §2 effect 7 (Strike 2 or later); m2-uprisings-and-garrison §Uprising start, keep, end.
    /// </summary>
    public bool StartUprisingNow(StepContext ctx, string system)
    {
        if (_fleets.OwnerOf(system) is not Side holder || IsUprising(system)) return false;
        StartUprising(ctx, ctx.Day + 1, system, holder);
        return true;
    }

    /// <summary>
    /// For the capture package, right after the uprising was cleared: the garrison need for the new holder is recomputed and, if the surplus is below
    /// 0, the uprising starts at once (its one draw for the incident timer). No-op for a neutral system or one already in uprising. Returns true if one
    /// started. rule: m2-control-and-capture §What happens at the moment of capture (B5a section 4 step 2.7, <c>0x50b5a0</c> then <c>0x50b800</c>).
    /// </summary>
    public bool CheckUprisingNow(StepContext ctx, string system)
    {
        if (_fleets.OwnerOf(system) is not Side holder || IsUprising(system)) return false;
        int n = RequiredGarrison(Params, _eco.SupportOf(system, holder), false, holder == Side.Empire && IsDeathStarSector(system));
        if (Garrison10(system) - 10 * n >= 0) return false;
        StartUprising(ctx, ctx.Day + 1, system, holder);
        return true;
    }

    /// <summary>
    /// A system was destroyed: its uprising ends (UprisingEnded "SystemDestroyed"), its timers are disarmed and it leaves the Death Star sector flag
    /// table; its support value is dropped. rule: m5-death-star §2 effect 5 (holder none, no support value).
    /// </summary>
    public void OnSystemDestroyed(StepContext ctx, string system)
    {
        var st = StateOf(system);
        if (st is { Uprising: true }) EndUprising(ctx, ctx.Day + 1, system, st, "SystemDestroyed");
        if (st != null) { st.PresenceDue = 0; st.ErosionDue = 0; st.IncidentDue = 0; }
        S.Prune(system);
        S.DeathStar.Remove(system);
        _game.State.Economy.AllianceSupport.Remove(system);
    }

    /// <summary>
    /// For the capture package: a system changed hands, so clear the uprising state now (emits UprisingEnded "Captured" if it was on).
    /// Slot 6.1 then judges the new holder at the next refresh. rule: m2-control-and-capture §Capture steps (6), C8.
    /// </summary>
    public void ClearUprisingOnCapture(StepContext ctx, string system)
    {
        var st = StateOf(system);
        if (st is { Uprising: true }) EndUprising(ctx, ctx.Day + 1, system, st, "Captured");
        S.Prune(system);
    }

    /// <summary>
    /// For the capture package: the system's blockade flag was just recomputed, so arm or disarm its presence timer now (as slot 6.1 does), for the
    /// holder it has now; a neutral system has none. rule: m2-popular-support §Periodic effects (arm when the blockade flag is on, disarm when off);
    /// m2-control-and-capture §What happens at the moment of capture (B5a, 2026-10-17: 2.9 blockade recomputed).
    /// </summary>
    public void RefreshPresenceNow(StepContext ctx, string system)
    {
        if (_fleets.OwnerOf(system) is Side h) RefreshPresence(ctx.Day + 1, system, h);
        else DisarmPresence(system);
        S.Prune(system);
    }

    // rule: m2-popular-support §Periodic effects timer 0x386 and the arming rules (arm when the blockade flag is on, disarm when off)
    private void RefreshPresence(int t, string sys, Side holder)
    {
        bool on = BlockadeSupportEffect && _fleets.IsBlockadedClassic(sys); // flag as computed in M1 step 5
        if (!on) { DisarmPresence(sys); return; }
        Side blockader = Other(holder); // the Classic flag is raised by the other side than the owner
        var st = S.Touch(sys);
        if (st.PresenceDue == 0 || st.PresenceSide != blockader)
        {
            st.PresenceDue = t + Params.PresencePeriod; // a changed blockader counts as a new blockade (DEFAULT)
            st.PresenceSide = blockader;
        }
    }

    private void DisarmPresence(string sys)
    {
        if (S.Systems.TryGetValue(sys, out var st)) st.PresenceDue = 0;
    }

    // ================================================================== 6.2 support timers

    // rule: m2-popular-support §Where it runs: slot 6.2, 0x384 first, then 0x386, each in system name order
    private void SupportTimers(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var sys in S.Systems.Where(kv => kv.Value.ErosionDue == t).Select(kv => kv.Key).ToList()) FireErosion(ctx, t, sys);
        foreach (var sys in S.Systems.Where(kv => kv.Value.PresenceDue == t).Select(kv => kv.Key).ToList()) FirePresence(ctx, t, sys);
    }

    // timer 0x384: the holder loses 1 (GNPRTB 7697) through ChangeSupport (DEFAULT), then re-arms while the uprising lasts
    internal void FireErosion(StepContext ctx, int t, string sys)
    {
        var st = S.Systems[sys];
        ChangeSupport(ctx, sys, Other(st.Rebels), Params.ErosionAmount, SupportCause.UprisingErosion);
        st.ErosionDue = t + Params.ErosionPeriod; // condition still holds (6.1 disarms the timer when the uprising ends)
    }

    // timer 0x386: strictly more than 50 support for the blockader gives +1 (GNPRTB 7693), else -1 (7695)
    internal void FirePresence(StepContext ctx, int t, string sys)
    {
        var st = S.Systems[sys];
        Side b = st.PresenceSide;
        int amount = _eco.SupportOf(sys, b) > Params.PresenceMajorityAbove ? Params.PresenceGain : Params.PresenceLoss;
        ChangeSupport(ctx, sys, b, amount, SupportCause.Presence);
        st.PresenceDue = t + Params.PresencePeriod;
    }

    // ================================================================== 6.3 incidents

    // rule: m2-uprisings-and-garrison §Incident (systems in name order; draw order d1, d2, UPRIS1TB effect, UPRIS2TB effect, re-arm)
    private void IncidentTimers(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var sys in S.Systems.Where(kv => kv.Value.IncidentDue == t).Select(kv => kv.Key).ToList())
        {
            var st = S.Systems[sys];
            Side holder = Other(st.Rebels);
            var rng = ctx.Rng;
            int d1 = Params.DieBase + rng.NextInt(0, Params.DieSpread);
            int d2 = Params.DieBase + rng.NextInt(0, Params.DieSpread);
            int score = IncidentScore(sys, holder, d1, d2);
            int c1 = Lookup(Params.Table1, score), c2 = Lookup(Params.Table2, score);
            ctx.Emit((i, _) => new IncidentRolled(i, t, sys, score, c1, c2));
            ApplyEffect(ctx, t, sys, holder, c1);
            ApplyEffect(ctx, t, sys, holder, c2);
            IncidentFired?.Invoke(ctx, sys, holder);
            st.IncidentDue = t + Params.IncidentIntervalMin + rng.NextInt(0, Params.IncidentIntervalSpread);
        }
    }

    /// <summary>
    /// The incident score of a system now for its holder, with the dice given, plus <c>incite_term + subdue_term</c> from the missions module (0 when
    /// no mission module is attached). rule: m2-uprisings-and-garrison §Incident (Score); m5-missions §7.
    /// </summary>
    public int IncidentScore(string system, Side holder, int d1, int d2)
    {
        var regs = HolderRegiments(system, holder).ToList();
        int stormtroopers = regs.Count(o => o.Class == Params.StormtrooperClass);
        return IncidentScore(Params, d1, d2, _eco.SupportOf(system, holder), regs.Count, stormtroopers, holder == Side.Empire && IsDeathStarSector(system))
            + (AgentIncidentTerm?.Invoke(system) ?? 0);
    }

    /// <summary><c>incite_term(system) + subdue_term(system)</c> of the incident score; the missions module sets it. Null = 0. rule: m5-missions §7.</summary>
    public Func<string, int>? AgentIncidentTerm { get; set; }

    /// <summary>
    /// Called after each incident's effects (slot 6.3) with (ctx, system, holder): the missions module applies the per-incident Incite effect when
    /// <c>InciteSupportTiming = PerIncident</c>. Null = nothing. rule: m5-missions §6 (Incite Uprising).
    /// </summary>
    public Action<StepContext, string, Side>? IncidentFired { get; set; }

    /// <summary>
    /// End a system's uprising now (flag cleared, erosion and incident timers disarmed; emits UprisingEnded with the reason). Used by a Subdue success
    /// with <c>SubdueEndsUprising</c> on. No-op without an uprising. rule: m5-missions §6 Subdue Uprising (switch SubdueEndsUprising).
    /// </summary>
    public void EndUprisingNow(StepContext ctx, string system, string reason)
    {
        var st = StateOf(system);
        if (st is { Uprising: true }) EndUprising(ctx, ctx.Day + 1, system, st, reason);
        S.Prune(system);
    }

    /// <summary>
    /// score = d1 + d2 + ceil((60 - s) / 10) - k x R - T, k = 2 for the Empire in a Death Star sector else 1; R and T are flat counts in both
    /// rulesets (DEFAULT). rule: m2-uprisings-and-garrison §Incident (Score).
    /// </summary>
    public static int IncidentScore(SupportParams p, int d1, int d2, int controllerSupport, int regiments, int stormtroopers, bool empireInDeathStarSector)
    {
        int unrest = controllerSupport >= p.GarrisonFreeSupport ? 0 : (p.GarrisonFreeSupport - controllerSupport + p.GarrisonStep - 1) / p.GarrisonStep;
        int k = empireInDeathStarSector ? p.DeathStarScoreMultiplier : 1;
        return d1 + d2 + unrest - k * regiments - stormtroopers;
    }

    /// <summary>"Largest threshold at or below the score"; below the first threshold the code is 0. rule: m2-uprisings-and-garrison §Incident (Lookup).</summary>
    public static int Lookup(IReadOnlyList<(int MinScore, int Code)> table, int score)
    {
        int code = 0;
        foreach (var (min, c) in table) if (score >= min) code = c;
        return code;
    }

    /// <summary>
    /// Incident codes 3 (one free character of the holder injured), 4 (one prisoner freed) and 5 (all prisoners freed), given (ctx, hook time,
    /// system, holder, code). Null (no character module) = no-op, no draw. The character module sets it. rule: m5-characters-and-force
    /// §What other sheets' effects call; m2-uprisings-and-garrison §Incident.
    /// </summary>
    public Action<StepContext, int, string, Side, int>? CharacterIncident { get; set; }

    // rule: m2-uprisings-and-garrison §Incident (Effects on the holder). Codes 3 to 5 go to the character module (no-op without it).
    private void ApplyEffect(StepContext ctx, int t, string sys, Side holder, int code)
    {
        switch (code)
        {
            case 3: case 4: case 5:
                CharacterIncident?.Invoke(ctx, t, sys, holder, code);
                break;
            case 1: // one completed facility destroyed, uniform over facility ids (DEFAULT completed only)
                var fac = _eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == sys).ToList();
                if (fac.Count > 0) _eco.Destroy(ctx, t, fac[ctx.Rng.NextInt(0, fac.Count - 1)], DestroyReason.UprisingIncident);
                break;
            case 2: // one regiment of the holder on the system destroyed, uniform (not weighted)
                var regs = HolderRegiments(sys, holder).ToList();
                if (regs.Count == 0) break;
                var r = regs[ctx.Rng.NextInt(0, regs.Count - 1)];
                _eco.RemoveObject(r.Id);
                ctx.Emit((i, _) => new RegimentDestroyed(i, t, r.Id, r.Class, sys, holder));
                break;
        }
    }
}
