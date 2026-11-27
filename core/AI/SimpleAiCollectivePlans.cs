using Praedra.Core.Clock;
using Praedra.Core.Fleets;

namespace Praedra.Core.AI;

/// <summary>
/// The Collective commander's later plans (AI trainer "Rooster", from 2026-11-27; design specs/engine/ai-notes/2026-11-26-adaptive-commander.md
/// §3). Each plan is a switch in SimpleAiParams.Collective.cs, off by DEFAULT. rule: ai tuning.
/// </summary>
public sealed partial class SimpleAi
{
    // ================================================================== Neutralise (plan 11)

    private bool NeutraliseOn => _p.Neutralise && _side == Side.Collective;

    /// <summary>
    /// A Hand world the pile can shell bare (Neutralise): seen enemy-held, Collective support seen at NeutraliseMinSupport or more, 1 to
    /// NeutraliseMaxDefenders regiments believed there, no enemy fleet seen, the pile's bombardment power above the shields seen (m3-bombardment
    /// §2: hits = P - S), and fewer than NeutraliseShellsPer30Days shellings of it lately. rule: m2-control-and-capture §Control (no regiments:
    /// support decides, 60 joins, 41 to 59 neutral).
    /// </summary>
    private bool ShakeTarget(string system, int power)
    {
        if (!NeutraliseOn || power <= 0 || !IsEnemyHeld(system)) return false;
        if (SupportSeen(system) is not int sup || sup < _p.NeutraliseMinSupport) return false;
        int d = DefendersAt(system);
        if (d < 1 || d > _p.NeutraliseMaxDefenders) return false;
        if (TargetFleetsAt(system).Count > 0) return false;
        return power > ShieldStrengthSeen(system) && RecentBombardments(system) < _p.NeutraliseShellsPer30Days;
    }

    /// <summary>The fleets' combined bombardment power (m3-bombardment §1: the fleets of one side at a system shell together).</summary>
    private int ShellPower(IEnumerable<Fleet> fleets) => fleets.Sum(x => _h.Bombardment.Power(x));

    /// <summary>
    /// Neutralise in PickTarget: a world it can shell bare, worth a plain invasion target there times NeutraliseFrontPercent inside a Neutralise
    /// front, NeutraliseElsewherePercent elsewhere (0 = not a target). Needs no troops aboard: no landing follows.
    /// </summary>
    private long NeutraliseScore(Praedra.Core.Map.StarSystem sys, int power, int ticks)
    {
        string s = sys.Name;
        if (s == WinTarget() || !ShakeTarget(s, power)) return 0;
        int pct = CampaignIn(sys.Sector) is { Plan: AiFrontPlan.Neutralise } ? _p.NeutraliseFrontPercent : _p.NeutraliseElsewherePercent;
        if (pct <= 0) return 0;
        var slots = SlotsSeen(s);
        if (!slots.Populated) return 0;
        long value = (20 + slots.Energy + 2L * slots.Raw + 2L * FacilitiesAt(s)) * PlanLike(sys) / 100;
        return value * pct / 100 * 1000 / (ticks + 20);
    }

    /// <summary>
    /// Neutralise at the target: the fleets here shell the garrison (Military) while the world fits ShakeTarget; true when the order went in
    /// (or another fleet here already shells it today). Never the game-winning system (a neutral Charter counts for nothing). A world below 60
    /// support that the troops aboard could take now is left to the landing (shelled bare it would only go neutral, Ponytail on #416); at 60 or
    /// more the shelling is preferred, since the world then joins with no garrison to leave behind.
    /// </summary>
    private bool ShakeHere(Fleet f, string at, List<Fleet> mineHere, int aboard, int defenders)
    {
        if (!NeutraliseOn || _h.Bombardment.Power(f) <= 0 || at == WinTarget() || !ShakeTarget(at, ShellPower(mineHere))) return false;
        if (aboard >= 1 && aboard >= Need(defenders) && SupportSeen(at) is int sup && sup < Praedra.Core.Control.ControlModule.JoinSupport) return false;
        if (CampaignAt(at) is not { Plan: AiFrontPlan.Neutralise } && _p.NeutraliseElsewherePercent <= 0) return false;
        if (mineHere.Any(x => x.Id < f.Id && _h.Bombardment.Power(x) > 0 && _h.Bombardment.HasPending(x.Id))) return true; // one shelling a day per world is enough
        long seq = _h.Bombardment.SubmitBombard(_side, f.Id, at, Praedra.Core.Defenses.BombardCategory.Military);
        Note(seq, "Bombard", $"fleet {f.Id} shells the garrison of {at} bare (Neutralise)");
        return true;
    }

    /// <summary>
    /// Neutralise's fit for a new front in the sector (PickFrontPlan): 60 per Hand world there it could shell bare, as the picture shows it
    /// (support and regiments; no pile is at hand, so any shield seen rules the world out).
    /// </summary>
    private long NeutraliseFit(string sector)
    {
        long n = 0;
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            if (sys.Sector != sector || Destroyed(sys.Name) || !IsEnemyHeld(sys.Name) || !SlotsSeen(sys.Name).Populated) continue;
            if (ShieldStrengthSeen(sys.Name) > 0 || SupportSeen(sys.Name) is not int sup || sup < _p.NeutraliseMinSupport) continue;
            int d = DefendersAt(sys.Name);
            if (d >= 1 && d <= _p.NeutraliseMaxDefenders) n++;
        }
        return 60 * n;
    }
}
