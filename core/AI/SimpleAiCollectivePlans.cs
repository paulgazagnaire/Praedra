using Praedra.Core.Clock;
using Praedra.Core.Economy;
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
    private bool ShakeTarget(string system, int power, bool wreck = false)
    {
        if (!NeutraliseOn || power <= 0 || !IsEnemyHeld(system)) return false;
        if (SupportSeen(system) is not int sup || sup < _p.NeutraliseMinSupport) return false;
        int d = DefendersAt(system);
        if (d < 1 || d > _p.NeutraliseMaxDefenders) return false;
        if (TargetFleetsAt(system).Count > 0) return false;
        return ShieldsBeaten(system, power, wreck) && RecentBombardments(system) < _p.NeutraliseShellsPer30Days;
    }

    /// <summary>The fleets' combined bombardment power (m3-bombardment §1: the fleets of one side at a system shell together).</summary>
    private int ShellPower(IEnumerable<Fleet> fleets) => fleets.Sum(x => _h.Bombardment.Power(x));

    /// <summary>
    /// Neutralise in PickTarget: a world it can shell bare, worth a plain invasion target there times NeutraliseFrontPercent inside a Neutralise
    /// front, NeutraliseElsewherePercent elsewhere (0 = not a target). Needs no troops aboard: no landing follows.
    /// </summary>
    private long NeutraliseScore(Praedra.Core.Map.StarSystem sys, int power, int ticks, bool wreck = false)
    {
        string s = sys.Name;
        if (s == WinTarget() || !ShakeTarget(s, power, wreck)) return 0;
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

    // ================================================================== Refit away (keeps the navy renewing while Neutralise shells)

    /// <summary>RefitAway: a fleet on a world the side does not hold may lose a weak warship to the refit when no enemy fleet is seen there,
    /// no regiment is aboard, no landing of it is pending (m3-assault: the troops aboard need their ships) and the ship itself carries nothing
    /// (a scrapped ship sets its squadrons and special forces down where it stands, which away from home is enemy ground; Ponytail #421).
    /// rule: ai tuning, SimpleAiParams.RefitAway.</summary>
    private bool RefitAwayOk(Fleet f, int ship) => _p.RefitAway && _h.Economy.ObjectsAboard(ship).Count == 0 && !_h.Assault.HasPending(f.Id)
        && EnemyFleetsAt(f.At!).Count == 0 && _h.Troops.RegimentsAboard(f).Count == 0;

    // ================================================================== Wreck the shields (special forces aboard fleets, #412)

    private bool WreckShieldsOn => _p.WreckShields && _side == Side.Collective && _p.UseCharactersAndMissions && _h.Missions.Params.OrbitMissions;
    private bool RaidShieldsOn => _p.RaidShieldsSeen && _side == Side.Collective;

    /// <summary>
    /// The pile's shelling gets through the shields seen (m3-bombardment §2: hits = max(0, P - S)), or the shields are no bar because the pile
    /// carries Wreckers to sabotage them from orbit first (<paramref name="wreck"/>, WreckShields).
    /// </summary>
    private bool ShieldsBeaten(string system, int power, bool wreck) { int sh = ShieldStrengthSeen(system); return power > sh || (wreck && sh > 0); }

    /// <summary>
    /// RaidShieldsSeen: a raid (Civilian shelling) is worth it only where the pile's power gets through the shields seen, or the pile carries
    /// Wreckers (WreckShields). Off = raids ignore the shields (DEFAULT, the AI before 2026-11-27: a pile under the shields shelled for no hits).
    /// </summary>
    private bool RaidShieldsOk(string system, int power, bool wreck) => !RaidShieldsOn || ShieldsBeaten(system, power, wreck);

    /// <summary>A free special force of a class that can sabotage (its mission bit in Sabotage's special-force mask, m5-missions §Special forces).</summary>
    private bool IsWrecker(EconObject o) => _h.Troops.Data.SpecialForce(o.Class) is { } k && (k.MissionBit & _h.Missions.Data.Get(Praedra.Core.Missions.MissionKind.Sabotage).SpecialForceMask) != 0;

    /// <summary>The Wreckers aboard the fleets, out on a mission or not (m5-missions §Spying from orbit 1: a team out keeps its seat).</summary>
    private List<EconObject> WreckersAboard(IEnumerable<Fleet> fleets) =>
        fleets.SelectMany(x => _h.Missions.SpecialForcesAboard(x)).Where(IsWrecker).OrderBy(o => o.Id).ToList();

    /// <summary>WreckShields: the pile carries a Wrecker not out on a mission (it can be launched at the target).</summary>
    private bool PileWrecks(IEnumerable<Fleet> fleets) => WreckShieldsOn && WreckersAboard(fleets).Any(o => !_h.Missions.IsSpecialForceBusy(o.Id));

    /// <summary>
    /// WreckShields at the target, before any shelling or landing: the shields seen there stop what the fleets here came for (their power does
    /// not beat them, or two or more generators forbid the landing, m3-planetary-defenses), no enemy fleet is seen, and Wreckers ride the
    /// fleets here. One Wrecker is launched from orbit on a Sabotage (m5-missions §Spying from orbit 2 and 3: no travel, half the spot chance;
    /// §6 Sabotage, DefencesFirst: a shield or battery falls) and the fleets wait while one is out. True when the fleets wait (an order went
    /// in today, or a Wrecker is out). With no Wrecker left, or none that may start, false: the usual choices follow.
    /// </summary>
    private bool WreckHere(Fleet f, string at, List<Fleet> mineHere, int aboard)
    {
        if (!WreckShieldsOn || !IsEnemyHeld(at)) return false;
        int sh = ShieldStrengthSeen(at);
        if (sh <= 0 || (ShellPower(mineHere) > sh && !(aboard > 0 && AssaultForbiddenSeen(at)))) return false;
        var mi = _h.Missions;
        var all = WreckersAboard(mineHere);
        if (all.Count == 0) return false;
        if (all.Any(o => mi.IsSpecialForceBusy(o.Id))) return true; // one is out: wait for its end
        if (_wreckDay != Today) { _wreckDay = Today; _wreckAt.Clear(); }
        if (_wreckAt.Contains(at)) return true; // a fleet here launched one today: wait with it
        // one launch for the fleets here: the first Wrecker in id order that may start (any fleet here may order it: the team is launched from
        // the fleet it rides, m5-missions §Spying from orbit 2); none may start: the fleets do not wait (Ponytail #426)
        foreach (var o in all)
        {
            var order = new Praedra.Core.Missions.StartMissionOrder(0, _side, Praedra.Core.Missions.MissionKind.Sabotage, new List<int>(), new List<int> { o.Id }, new List<int>(), at);
            if (mi.CheckStart(order).Reason != null) continue;
            long seq = _h.Game.Submit(sq => order with { Sequence = sq });
            Note(seq, "MissionSabotage", $"{o.Class} {o.Id} from fleet {f.Id} over {at}, shields {sh} (wreck the shields)");
            _wreckAt.Add(at);
            return true;
        }
        return false;
    }

    // WreckHere: the worlds where a Wrecker was ordered today (reset each day; lookups only, never iterated, so no memory between days)
    private int _wreckDay = int.MinValue;
    private readonly HashSet<string> _wreckAt = new(StringComparer.Ordinal);

    /// <summary>
    /// WreckShields, daily before the fleet review: (1) each armed own fleet standing still on a held world takes aboard the free Wreckers
    /// standing there, up to WreckShieldsForces aboard (m5-missions §Spying from orbit 1, one load order per fleet); (2) every
    /// WreckGatherEveryDays, while fewer than WreckShieldsPool Wreckers ride fleets or stand at the rally world, free Wreckers elsewhere walk
    /// to the rally, where the strike fleets gather (a MoveGroup, m5-missions §Orders). Fleets and forces in id order.
    /// </summary>
    private void PlanWreckers()
    {
        var mi = _h.Missions;
        var free = FreeAgentForces().Where(t => IsWrecker(t.O)).Select(t => t.O).ToList();
        if (free.Count == 0) return;
        var taken = new HashSet<int>(); // lookup only, never iterated
        int cap = Math.Min(_p.WreckShieldsForces, mi.Params.FleetSpecialForcesMax);
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Ships.Count > 0 && f.Transit == null && f.At != null && Held(f.At)).OrderBy(f => f.Id))
        {
            if (Str(f) <= 0 || _h.Bombardment.Power(f) <= 0) continue;
            int room = Math.Min(cap - WreckersAboard(new[] { f }).Count, mi.Params.FleetSpecialForcesMax - mi.SpecialForcesAboard(f).Count);
            if (room <= 0) continue;
            var load = free.Where(o => o.System == f.At && !taken.Contains(o.Id)).Take(room).Select(o => o.Id).ToList();
            if (load.Count == 0) continue;
            var order = new Praedra.Core.Missions.LoadSpecialForcesOrder(0, _side, f.Id, load);
            if (mi.CheckLoadSpecialForces(order) != null) continue;
            long seq = _h.Game.Submit(sq => order with { Sequence = sq });
            Note(seq, "LoadSpecialForces", $"{load.Count} Wreckers board fleet {f.Id} at {f.At} (wreck the shields)");
            taken.UnionWith(load);
        }
        if (_p.WreckShieldsPool <= 0 || Today % WreckGatherEveryDays != 0 || Rally() is not { } rally) return;
        int have = S.Fleets.Values.Where(f => f.Side == _side && f.Ships.Count > 0).Sum(f => WreckersAboard(new[] { f }).Count)
            + free.Count(o => o.System == rally && !taken.Contains(o.Id));
        var send = free.Where(o => o.System != rally && !taken.Contains(o.Id)).Take(Math.Max(0, _p.WreckShieldsPool - have)).Select(o => o.Id).ToList();
        if (send.Count == 0) return;
        var walk = new Praedra.Core.Missions.MoveGroupOrder(0, _side, new List<int>(), send, rally);
        long ws = _h.Game.Submit(sq => walk with { Sequence = sq });
        Note(ws, "MoveGroup", $"{send.Count} Wreckers walk to the rally {rally} (wreck the shields)");
    }

    /// <summary>Days between two Wrecker walks to the rally (AI tuning; keeps no memory, so a loaded game walks as the uninterrupted one).</summary>
    private const int WreckGatherEveryDays = 10;

    // ================================================================== Shields felt (the fog hides the shields from a fleet in orbit)

    /// <summary>
    /// ShieldsFeltDays (fog): the shields the side's own last shelling of <paramref name="system"/> met as its player would read them
    /// (m3-bombardment §2, hits = max(0, P - S): with hits the shields were P - hits = S; with none, fleets of the shelling fleets' power do
    /// nothing there, so that power is noted and only a stronger pile tries again), when that shelling is newer than the picture's facilities (<paramref name="pictureStamp"/>,
    /// m5-fog-and-hq §F2: a fleet in orbit sees no facilities) and within ShieldsFeltDays; 0 after an own Sabotage there succeeded since (a shield
    /// may have fallen: the next shelling tells). Null = the picture stands.
    /// </summary>
    /// <summary>ShieldsFelt: the bombardment power of these own fleets now (gone fleets count 0), read when a strike with no hits is noted.</summary>
    private int FleetsShellPower(IReadOnlyList<int> fleets) => fleets.Sum(id => S.Fleets.TryGetValue(id, out var f) && f.Side == _side ? _h.Bombardment.Power(f) : 0);

    private bool ShieldsFeltOn => _p.ShieldsFeltDays > 0 && _view != null && _side == Side.Collective;

    private int? ShieldsFelt(string system, int? pictureStamp)
    {
        if (!ShieldsFeltOn) return null;
        foreach (var n in NotesSince(AiNoteKind.ShieldsFelt, Today - _p.ShieldsFeltDays))
        {
            if (n.System != system) continue;
            if (pictureStamp is int ps && n.Day < ps) return null; // a look since then: the picture is newer
            return Math.Max(0, n.Value);
        }
        return null;
    }

    // ================================================================== Fighters wait aboard (no squadrons left on the ground at the yards)

    /// <summary>
    /// FighterWaitMax: a new fighter order only while fewer than FighterWaitMax of the side's squadrons wait on the ground, based on a world
    /// and not aboard a ship (m3-fleets: a squadron moves only aboard a carrier, so one built for carriers that are out raiding waits at the
    /// yard, and a based squadron with no ship there fights any enemy fleet that comes, m4-battle-classic §1.3). Only squadrons set down within
    /// the last FighterWaitDays count (a FighterGrounded note): nothing fetches a squadron left at a yard the fleets never visit, so without the
    /// window two strays would stop every fighter order for the rest of the game (Ponytail #432). The Hand reads FighterWaitMaxHand instead.
    /// Off (0) = no cap.
    /// </summary>
    private bool FightersMayWait()
    {
        int max = FighterWaitCap;
        if (max <= 0) return true;
        var recent = new HashSet<int>(); // lookups only
        foreach (var n in NotesSince(AiNoteKind.FighterGrounded, Today - FighterWaitDays)) if (n.Day >= Today - FighterWaitDays) recent.Add(n.Value);
        if (recent.Count < max) return true;
        int waiting = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.Fighter && o.Side == _side && o.AboardShip == 0 && o.System != null && recent.Contains(o.Id));
        return waiting < max;
    }

    private int FighterWaitCap => _side == Side.Collective ? _p.FighterWaitMax : _p.FighterWaitMaxHand;

    /// <summary>
    /// FighterWaitMax: days a squadron set down on the ground counts as waiting for a carrier (AI tuning, Rooster 2026-11-28, Ponytail #432:
    /// Medium mirror, 96 seeds: 86% of the Collective's squadron waits on the ground ended (boarded or lost) within 120 days (6672 of 7716); one left longer is a
    /// stray that no longer holds back new orders).
    /// </summary>
    private const int FighterWaitDays = 120;

    // ================================================================== Forget Charter's walls

    private bool WallsForgetOn => _p.WinWallsForgetDays > 0 && _side == Side.Collective && _view != null;

    /// <summary>
    /// The facilities picture (group C) the AI reads walls from: with WinWallsForgetDays (the Collective, fog), a look at the system that wins
    /// the game older than that many days reads as none (unknown), unless an own landing there was refused for the shields since that look
    /// (WallsRefused: the walls stand, Ponytail #436). Otherwise the picture as it is.
    /// </summary>
    private Praedra.Core.Fog.Seen<IReadOnlyDictionary<string, int>>? WallsSeen(string system)
    {
        var fac = _view!.Picture(system)?.Facilities;
        if (fac != null && WallsForgetOn && system == WinTarget() && AgeOf(fac) > _p.WinWallsForgetDays && !WallsRefusedSince(system, fac.Stamp)) return null;
        return fac;
    }

    /// <summary>
    /// WinWallsForgetDays: an own assault at the system was refused because it is fully shielded (AiNoteKind.WallsRefused, m3-assault check 4)
    /// after <paramref name="lookStamp"/> (the day of the side's last look at its facilities; null = never looked) and within WinWallsForgetDays.
    /// A refused landing teaches the AI as a shelling does; without it a fleet would land, be refused and land again every day.
    /// </summary>
    private bool WallsRefusedSince(string system, int? lookStamp)
    {
        if (!WallsForgetOn) return false;
        int since = Today - _p.WinWallsForgetDays;
        foreach (var n in NotesSince(AiNoteKind.WallsRefused, since))
            if (n.System == system && n.Day >= since && (lookStamp == null || n.Day > lookStamp)) return true;
        return false;
    }

    private string? AssaultAt(long sequence) => _assaultAt.TryGetValue(sequence, out var s) ? s : null;
    // Not saved, and need not be (Ponytail #436): filled in PlanDay, read back by the next Remember or by CatchUp before a save; the game takes
    // no save between PlanDay and its Step, so a loaded game never misses a refusal of an order it holds.
    private readonly Dictionary<long, string> _assaultAt = new(); // WallsRefused: own assault orders not yet read back (lookup only, never iterated)

    // ================================================================== Siege Charter (the walled win target)

    private bool WinSiegeOn => _p.WinSiege && _p.SiegeShields && _view != null && _side == Side.Collective;

    /// <summary>WinSiege in PickTarget: the walled win target may be picked by a pile whose shelling beats the shields seen there by the margin.</summary>
    private bool WinSiegeFits(string system, int shellPower) =>
        WinSiegeOn && system == WinTarget() && shellPower > ShieldStrengthSeen(system) + _p.WinSiegeMarginPower;

    /// <summary>
    /// WinSiege: the walls of the win target are believed down. An own shelling there that got hits (ShieldsHit, within ShieldsFeltDays) and
    /// is newer than the last look at its facilities met shields below the least that the assault gate's generators give (m3-assault check 4,
    /// m3-bombardment §2: S sums the completed generators), and no own landing there was refused for the shields since that shelling
    /// (WallsRefused, so WinWallsForgetDays must be on). Only a shelling with hits tells the true S; one with none tells only the power that
    /// failed (ShieldsFelt), which is no proof the walls fell.
    /// </summary>
    private bool WinWallsFeltDown(string system, int? lookStamp)
    {
        if (!WinSiegeOn || !WallsForgetOn || system != WinTarget()) return false;
        int since = Today - _p.ShieldsFeltDays;
        AiNote? hit = null;
        foreach (var n in NotesSince(AiNoteKind.ShieldsHit, since))
            if (n.System == system && n.Day >= since) { hit = n; break; }
        if (hit == null || (lookStamp is int ls && hit.Day < ls) || hit.Value >= GateShieldsLeast()) return false;
        foreach (var n in NotesSince(AiNoteKind.WallsRefused, hit.Day))
            if (n.System == system && n.Day >= hit.Day) return false;
        return true;
    }

    /// <summary>The least shields the assault gate's number of shield generators can give (the weakest generator class, that many times; rule: m3-assault check 4).</summary>
    private int GateShieldsLeast()
    {
        int least = int.MaxValue;
        foreach (var c in _h.Data.Defenses.Classes)
            if (c.Job == Praedra.Core.Defenses.DefenseJob.ShieldGenerator && c.Shield > 0) least = Math.Min(least, c.Shield);
        return least == int.MaxValue ? 0 : least * Math.Max(1, _h.Defenses.Params.AssaultGateShields);
    }
}
