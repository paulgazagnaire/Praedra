using Praedra.Core.Battle;
using Praedra.Core.Ships;

namespace Praedra.Core.Fleets;

/// <summary>
/// The one place that measures combat strength and decides "armed". Strength is the engine's own measure (m4-battle-new §12): the cost-weighted,
/// deflection-aware damage rate of a force against another, never a unit count. The strategic layer (forced stop, economic blockade) calls
/// <see cref="Of(IEnumerable{Combatant}, IEnumerable{Combatant})"/> with shields taken as full; the New battle calls the same arithmetic with live state.
/// rule: m4-battle-new §12; m1-forced-stop-and-blockade §Update 2026-10-12 (combat strength behind one function), §What "armed" means.
/// </summary>
public sealed class CombatStrength
{
    private readonly CombatModel _model;

    /// <param name="data">The per-weapon data (<see cref="NewBattleData.Default"/> for the repository's files).</param>
    public CombatStrength(NewBattleData data, NewBattleParams? p = null) { _model = new CombatModel(data, p); }

    public NewBattleData Data => _model.Data;

    /// <summary>A ship or squadron class as the strategic layer sees it: shields full, hull ratio and weapons counter as given. rule: m4-battle-new §12.</summary>
    public Combatant Unit(ShipClass cls, double hullRatio = 1.0, int weaponHits = 0) =>
        Combatant.Strategic(_model.Data.Class(cls.Name), hullRatio, weaponHits, _model.P.SquadronCraft);

    /// <summary>
    /// CombatStrength(own, versus): 0 if versus is empty, else sum over own units of d(u) x hullRatio x shieldFactor, d(u) the cost-weighted mean facing dps
    /// against versus. Strategic call: shields of every unit taken as full. rule: m4-battle-new §12.
    /// </summary>
    public double Of(IEnumerable<Combatant> own, IEnumerable<Combatant> versus) => _model.Strength(own.ToList(), versus.ToList());

    /// <summary>The same for plain classes at full health (a delivery, the reference fleet). rule: m4-battle-new §12.</summary>
    public double Of(IEnumerable<ShipClass> own, IEnumerable<ShipClass> versus) =>
        _model.Strength(own.Select(c => Unit(c)).ToList(), versus.Select(c => Unit(c)).ToList());

    /// <summary>A ship or squadron is armed when its overall_attack_strength is above 0. rule: m1-forced-stop-and-blockade §What "armed" means.</summary>
    public static bool IsArmed(ShipClass s) => s.OverallAttackStrength > 0;

    /// <summary>A force is armed if at least one ship or squadron in it is. rule: m1-forced-stop-and-blockade §What "armed" means.</summary>
    public static bool IsArmed(IEnumerable<ShipClass> forces) => forces.Any(IsArmed);
}
