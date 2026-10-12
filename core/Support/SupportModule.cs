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
    /// new Alliance share. rule: m2-popular-support §One entry point for every change (steps 1 to 4).
    /// </summary>
    public int ChangeSupport(StepContext ctx, string system, Side side, int amount, SupportCause cause)
    {
        if (!_galaxy.TryFindSystem(system, out _)) throw new ArgumentException($"Unknown system '{system}'.");
        int old = _eco.SupportOf(system, Side.Alliance);
        int d = side == Side.Alliance ? amount : -amount;                       // 1. Alliance axis
        if (d > 0 && IsDeathStarSector(system)) d /= Params.DeathStarDampingDivisor; // 2. damping, only rises, toward zero
        int now = Math.Clamp(old + d, Params.SupportMin, Params.SupportMax);    // 3. clamp
        if (now == old) return old;
        _game.State.Economy.AllianceSupport[system] = now;
        int day = ctx.Day + 1;
        ctx.Emit((i, _) => new SupportChanged(i, day, system, old, now, cause));  // 4. event
        return now;
    }

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
    /// For the capture package: a system changed hands, so clear the uprising state now (emits UprisingEnded "Captured" if it was on).
    /// Slot 6.1 then judges the new holder at the next refresh. rule: m2-control-and-capture §Capture steps (6), C8.
    /// </summary>
    public void ClearUprisingOnCapture(StepContext ctx, string system)
    {
        var st = StateOf(system);
        if (st is { Uprising: true }) EndUprising(ctx, ctx.Day + 1, system, st, "Captured");
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
            st.IncidentDue = t + Params.IncidentIntervalMin + rng.NextInt(0, Params.IncidentIntervalSpread);
        }
    }

    /// <summary>The incident score of a system now for its holder, with the dice given. M5 adds the incite and subdue terms (0 here).</summary>
    public int IncidentScore(string system, Side holder, int d1, int d2)
    {
        var regs = HolderRegiments(system, holder).ToList();
        int stormtroopers = regs.Count(o => o.Class == Params.StormtrooperClass);
        return IncidentScore(Params, d1, d2, _eco.SupportOf(system, holder), regs.Count, stormtroopers, holder == Side.Empire && IsDeathStarSector(system));
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

    // rule: m2-uprisings-and-garrison §Incident (Effects on the holder). Codes 3 to 5 are M5 (characters, prisoners): no-op, no draw.
    private void ApplyEffect(StepContext ctx, int t, string sys, Side holder, int code)
    {
        switch (code)
        {
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
