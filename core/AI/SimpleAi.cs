using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Setup;

namespace Praedra.Core.AI;

/// <summary>One order the AI queued today (for logs and tests).</summary>
public sealed record SubmittedOrder(long Sequence, string Kind, string Detail);

/// <summary>
/// A simple computer opponent: a decent economy bot with a thin military (Classic-style). Call <see cref="PlanDay"/> once per day BEFORE
/// <c>Game.Step()</c>; it queues orders through the modules' Submit methods, exactly the orders a player can give. It never changes
/// state itself, and it keeps no plans between days: everything is re-derived from the game state and from the recent events of its side's
/// feed. Those events are copied into the side's <see cref="AiMemory"/>, which the host keeps and <see cref="GameHost.SaveWithSettings"/>
/// saves (a save holds no events), so a loaded game plays on exactly as the uninterrupted one (QA-1).
///
/// Randomness: the AI never draws from the game's generator (that would change the game's random stream). Its few tie-breaks use a
/// private <see cref="GameRng"/> seeded from (game day, side), so the same game state gives the same orders.
///
/// VISIBILITY: with <see cref="SimpleAiParams.UseFog"/> on, every look at the other side goes through the side's
/// <see cref="Praedra.Core.Fog.KnowledgeView"/> (m5-fog-and-hq §N8, the fairness contract): enemy fleets, garrisons, holders, facilities and slot
/// values come from the side's own picture (possibly stale), the event feed is the side's filtered one, and the Hand's
/// <see cref="KnownEnemyHq"/> is set only from the Hand's own picture of the HQ. With it off (DEFAULT for now) the AI still sees everything.
/// The looks are in one block below ("what the AI may see"), so nothing else reads the other side. With fog the AI also scouts, weighs stale
/// pictures with a margin, hunts the Hearth with a belief map (Hand) and moves its HQ by risk (Collective): see SimpleAiFog.cs. None of
/// that runs with fog off, so the fog-off AI is unchanged.
/// </summary>
public sealed partial class SimpleAi
{
    private readonly GameHost _h;
    private readonly Side _side;
    private readonly SimpleAiParams _p;
    private readonly List<SubmittedOrder> _submitted = new();

    // per-day scratch (rebuilt every PlanDay)
    private int _plannedLoad;
    private readonly HashSet<(string, YardFamily)> _busy = new();
    private readonly Dictionary<string, int> _reservedEnergy = new(StringComparer.Ordinal); // lookup only
    private readonly Dictionary<string, int> _reservedRaw = new(StringComparer.Ordinal);    // lookup only
    private readonly Dictionary<int, double> _strength = new();                              // lookup only
    private readonly Dictionary<(string, YardFamily), int> _reservedYards = new();           // lookup only
    private string? _capital, _rally; private bool _capitalDone, _rallyDone;
    private int[]? _hops;
    private GameRng _rng = new(0);

    public SimpleAi(GameHost host, Side side, SimpleAiParams? p = null) { _h = host; _side = side; _p = p ?? new SimpleAiParams(); }

    public Side Side => _side;
    public SimpleAiParams Params => _p;
    /// <summary>
    /// The enemy HQ, when known (the Hand hunts it first). Null = unknown. Without fog the caller may set it; with
    /// <see cref="SimpleAiParams.UseFog"/> it is overwritten every day from the Hand's own picture (m5-fog-and-hq §N5 KnownHq).
    /// </summary>
    public string? KnownEnemyHq { get; set; }
    /// <summary>The orders queued by the last <see cref="PlanDay"/>.</summary>
    public IReadOnlyList<SubmittedOrder> Submitted => _submitted;

    private Side Enemy => _side == Side.Hand ? Side.Collective : Side.Hand;
    private GameState S => _h.Game.State;
    private int Today => _h.Game.Day;

    // ================================================================== entry

    public void PlanDay()
    {
        _submitted.Clear(); _plannedLoad = 0; _busy.Clear(); _reservedEnergy.Clear(); _reservedRaw.Clear(); _reservedYards.Clear(); _strength.Clear();
        _capitalDone = _rallyDone = false; _hops = null; _finishing = null; _blind = null; _tfStrength = null; _threats = null; _offensives = -1; _lost = null; _lostBattles = null; _runners = null; _bounced = null; _loading.Clear();
        int day = Today, phase = (int)_side;
        _rng = new GameRng((ulong)day * 2 + (ulong)_side + 0x5A1D, 0xA1);
        _view = _p.UseFog ? _h.Fog.ViewFor(_side) : null;
        Remember();
        _seenForces.Clear(); ClearFogScratch();
        RememberStrongestFleet();
        if (_view != null && _side == Side.Hand) KnownEnemyHq = _view.KnownHq?.System; // only what the Hand's own picture shows
        PlanUpkeepCrisis();
        if (_view != null && _side == Side.Collective) PlanHqHide(); // fog: the HQ hides by risk (ai/07 §7), checked daily
        // characters and missions (SimpleAiAgents.cs); before the military review so an officer boards a fleet before it leaves today
        if (_p.UseCharactersAndMissions && (day + phase) % Math.Max(1, _p.AgentsEvery) == 0) PlanAgents();
        if ((day + phase) % _p.EconomyEvery == 0) PlanEconomy();
        if ((day + phase) % _p.ProductionEvery == 0) PlanProduction();
        if ((day + phase) % _p.MilitaryEvery == 0) PlanMilitary();
    }

    /// <summary>One line for logs: capital, rally, fleets and what they hold. Reads state only.</summary>
    public string Status()
    {
        var sb = new System.Text.StringBuilder($"{_side}: capital {Capital()}, rally {Rally()}, win target {WinTarget()}; ");
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Ships.Count > 0).OrderBy(f => f.Id))
            sb.Append($"[{f.Id}@{f.At ?? "->" + f.Transit?.Path[^1]} str {_h.Fleets.StrengthOf(f):F0} ships-only {Str(f):F0} ships {f.Ships.Count} aboard {_h.Troops.RegimentsAboard(f).Count} slots {_h.Fleets.FreeTroopSlots(f)} berths {_h.Fleets.FreeBerths(f)}] ");
        sb.Append($"spare {string.Join(",", HeldSystems().Select(h => (h, n: SpareRegimentsAt(h))).Where(t => t.n > 0).Select(t => t.h + ":" + t.n))}");
        return sb.ToString();
    }

    private void Note(long seq, string kind, string detail) => _submitted.Add(new SubmittedOrder(seq, kind, detail));

    // ================================================================== what the AI may see (fog goes here)

    private Praedra.Core.Fog.KnowledgeView? _view;
    private readonly Dictionary<string, List<ArmedForce>> _seenForces = new(StringComparer.Ordinal); // lookup only

    /// <summary>
    /// One enemy fleet as the AI weighs it: its capital ships as combat units. Without fog: the real fleet (FleetId set, hull and damage read).
    /// With fog: a fleet of the side's picture (class counts at full hull, FleetId null). At = the system it stands at, null on a lane.
    /// </summary>
    /// Gravity: a ship of the fleet has a gravity-well projector (it can pull passing fleets out of hyperspace).
    private sealed record ArmedForce(int? FleetId, string? At, List<Combatant> Units, bool Gravity = false);

    private ArmedForce ForceOf(Fleet f) => new(f.Id, f.At, ShipUnits(f),
        f.Ships.Any(id => _h.Economy.Find(id) is { } o && _h.Fleets.ShipClasses.TryGetValue(o.Class, out var c) && c.GravityWellProjector > 0));

    private ArmedForce ForceOf(string at, Praedra.Core.Fog.FleetPicture p)
    {
        var units = new List<Combatant>(); bool gravity = false;
        foreach (var (cls, n) in p.Ships) // ordinal class order
            if (_h.Fleets.ShipClasses.TryGetValue(cls, out var c)) { for (int i = 0; i < n; i++) units.Add(_h.Fleets.Strength.Unit(c)); gravity |= n > 0 && c.GravityWellProjector > 0; }
        return new ArmedForce(null, at, units, gravity);
    }

    /// <summary>Enemy fleets standing at a system. No fog: sees all. Fog: group E of the side's picture, unless the look is only Charted (too old).</summary>
    private List<ArmedForce> EnemyFleetsAt(string system)
    {
        if (_view == null) return _h.Fleets.FleetsAt(system).Where(f => f.Side != _side).Select(ForceOf).ToList();
        if (_seenForces.TryGetValue(system, out var cached)) return cached;
        var list = new List<ArmedForce>();
        var pic = _view.Picture(system);
        if (pic?.Fleets is { } e && _view.Level(system) is Praedra.Core.Fog.KnowledgeLevel.Held or Praedra.Core.Fog.KnowledgeLevel.Observed)
            foreach (var fp in e.Value) if (fp.Side != _side) list.Add(ForceOf(system, fp));
        return _seenForces[system] = list;
    }

    /// <summary>Regiments of any other side standing on a system. No fog: sees all. Fog: group D of the picture, or a guess when never seen.</summary>
    private int DefendersAt(string system)
    {
        if (_view == null) return _h.Troops.RegimentsOnSystem(system, _side).Count;
        return FogDefendersAt(system); // group D with a staleness margin, less own bombardment kills; a guess when never seen
    }

    /// <summary>
    /// Underground: systems where the side sees an enemy harvester: with fog, group E of a picture at most SpikeSeenDays old (Held or Observed
    /// look); without fog, the real fleets. Ordinal order.
    /// </summary>
    private List<string> HarvesterSeenAt()
    {
        var list = new List<string>();
        const string cls = Praedra.Core.Economy.EconomyModule.HarvesterClass;
        foreach (var sys in _h.Data.Galaxy.Systems.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            string s = sys.Name;
            bool seen;
            if (_view == null) seen = _h.Fleets.FleetsAt(s).Any(f => f.Side == Enemy && f.Ships.Any(id => _h.Economy.Find(id) is { Completed: true } o && o.Class == cls));
            else seen = _view.Picture(s)?.Fleets is { } e && AgeOf(e) <= _p.SpikeSeenDays && e.Value.Any(fp => fp.Side == Enemy && fp.Ships.ContainsKey(cls));
            if (seen) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// HarvesterYardValue: Hand worlds where the side sees a harvester under construction: with fog, a HarvesterQueued picture at most
    /// HarvesterBuildSeenDays old (m5-fog-and-hq §N6b); without fog, the enemy's shipyard projects. Ordinal order.
    /// </summary>
    private List<string> HarvesterBuildSeenAt()
    {
        var list = new List<string>();
        foreach (var sys in _h.Data.Galaxy.Systems.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            string s = sys.Name;
            bool seen;
            if (_view == null)
                seen = _h.Construction.ProjectAt(s, YardFamily.Shipyard) is { } p && p.Side == Enemy
                    && p.AllUnits().Any(id => _h.Economy.Find(id)?.Class == Praedra.Core.Economy.EconomyModule.HarvesterClass);
            else seen = _view.Picture(s)?.HarvesterQueued is { Value: true } q && AgeOf(q) <= _p.HarvesterBuildSeenDays;
            if (seen && IsEnemyHeld(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>Completed shipyards (both kinds) at a system: the truth without fog, group C of the picture with fog.</summary>
    private int ShipyardsSeenAt(string system)
    {
        static bool IsShipyard(string cls) => FacilityKinds.TryParse(cls, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == YardFamily.Shipyard;
        if (_view == null) return _h.Economy.Objects.Count(o => o.Completed && o.Category == ObjectCategory.Facility && o.System == system && IsShipyard(o.Class));
        return _view.Picture(system)?.Facilities?.Value.Where(kv => IsShipyard(kv.Key)).Sum(kv => kv.Value) ?? 0;
    }

    private bool HarvesterYardHuntOn => _p.HarvesterYardValue > 0 && _side == Side.Collective;

    /// <summary>The Collective's own Hearth world (its own object: m5-fog-and-hq §N5, the Collective always knows it); null for the Hand or when none.</summary>
    private string? OwnHearth() => _side == Side.Collective ? _h.HqSystem : null;

    /// <summary>Systems the other side holds. No fog: sees all. Fog: those its picture shows as enemy-held (ordinal order).</summary>
    private IEnumerable<string> EnemySystems() => _view == null
        ? S.SystemOwners.Where(kv => kv.Value == Enemy).Select(kv => kv.Key)
        : _h.Data.Galaxy.Systems.Select(s => s.Name).Where(IsEnemyHeld);

    /// <summary>Is the system held by the other side? No fog: the truth. Fog: as the picture shows it.</summary>
    private bool IsEnemyHeld(string system) => _view == null ? _h.Fleets.OwnerOf(system) == Enemy : _view.Picture(system)?.Owner?.Value.Owner == Enemy;

    /// <summary>Completed facilities at a system of another side. No fog: the truth. Fog: group C of the picture (0 when never seen).</summary>
    private int FacilitiesAt(string system) => _view == null
        ? _h.Economy.Objects.Count(o => o.Completed && o.Category == ObjectCategory.Facility && o.System == system)
        : _view.Picture(system)?.Facilities?.Value.Values.Sum() ?? 0;

    /// <summary>Slot values of a system not held. No fog: the truth. Fog: group A of the picture; never seen = populated, no slots (worth a look).</summary>
    private (bool Populated, int Energy, int Raw) SlotsSeen(string system)
    {
        if (_view == null) { var s = _h.Economy.SlotsOf(system); return (s.Populated, s.Energy, s.Raw); }
        return _view.Picture(system)?.Status?.Value ?? (true, 0, 0);
    }

    /// <summary>Two or more shield generators forbid an assault. No fog: the truth. Fog: from group C of the picture (unknown = not forbidden).</summary>
    private bool AssaultForbiddenSeen(string system)
    {
        if (_view == null) return _h.Defenses.AssaultForbidden(system);
        var fac = _view.Picture(system)?.Facilities;
        return fac != null && _h.Defenses.AssaultForbiddenBy(fac.Value);
    }

    /// <summary>The events the AI reads (its own losses, captures, bombardments). No fog: all. Fog: the side's filtered feed (§N8).</summary>
    private IReadOnlyList<GameEvent> Events => _view == null ? _h.Game.Events : _view.Events;

    private AiMemory _mem = new();

    /// <summary>
    /// Copy today's new feed events into the side's <see cref="AiMemory"/> (kept by the host and carried by a save, QA-1). Every look-back
    /// below reads the notes instead of the feed, with the same windows, so a loaded game decides as the uninterrupted one.
    /// </summary>
    private void Remember()
    {
        _mem = _h.AiMemoryFor(_side, _p.UseFog);
        _mem.KeepAtLeast(
            battle: Math.Max(_p.LossMemoryDays, _p.RunnerMemoryDays), systemLost: _p.RetakeDays,
            bombardment: _p.UseFog ? Math.Max(RecentBombardmentDays, _p.FogKillMemoryDays) : RecentBombardmentDays, // and with fog back to the last ground look (AiMemory.ForSave)
            lookFoiled: Math.Max(_p.SpyFoilMemoryDays, _p.HqRecheckFoilDays), hqMoved: _p.HqMoveCooldownDays, character: _p.AbductionInfoDays);
        _mem.Ingest(Events, IsArmedCapital);
        var mem = _mem;
        mem.CatchUp = () => mem.Ingest(_p.UseFog ? _h.Fog.ViewFor(_side).Events : _h.Game.Events, IsArmedCapital); // a save between Steps (AiMemory.CatchUp)
    }

    /// <summary>The newest notes of a kind first, while a look-back from <paramref name="sinceDay"/> still reaches them (see <see cref="AiNote.Floor"/>).</summary>
    private IEnumerable<AiNote> NotesSince(AiNoteKind kind, int sinceDay)
    {
        var notes = _mem.Notes;
        for (int i = notes.Count - 1; i >= 0 && notes[i].Floor >= sinceDay; i--)
            if (notes[i].Kind == kind) yield return notes[i];
    }

    private bool IsArmedCapital(int id) =>
        _h.Economy.Find(id) is { } o && o.Category == ObjectCategory.CapitalShip && _h.Fleets.ClassOf(o) is { } c && CombatStrength.IsArmed(c);

    // ---- characters and missions (SimpleAiAgents.cs reads the other side only through these)

    /// <summary>One unit a mission would meet on the detection walk (m5-missions §3): its detection and its officer's espionage (0 = none or unknown).</summary>
    private readonly record struct DefenderSeen(int Detection, int OfficerEspionage);

    /// <summary>
    /// The units of any other side a mission of this side at the system would meet. No fog: the truth (MissionModule.Defenders, officers
    /// included). Fog: from the side's picture (<see cref="FogMissionDefenders"/>; officers unknown).
    /// </summary>
    private List<DefenderSeen> MissionDefendersAt(string system) => _view == null
        ? _h.Missions.Defenders(_side, system).Select(d => new DefenderSeen(d.Detection, d.OfficerEspionage)).ToList()
        : FogMissionDefenders(system);

    /// <summary>The side's support at a system: its own systems (and every system without fog) the truth; fog: group B of the picture (null = never seen).</summary>
    private int? SupportSeen(string system)
    {
        if (_view == null || Held(system)) return _h.Economy.SupportOf(system, _side);
        var o = _view.Picture(system)?.Owner;
        if (o == null) return null;
        return _side == Side.Collective ? o.Value.CollectiveSupport : 100 - o.Value.CollectiveSupport;
    }

    /// <summary>
    /// The holder of a system and how old that knowledge is (days). Held or no fog: the truth, age 0. Fog: group B of the picture; false
    /// when the side has never seen the owner.
    /// </summary>
    private bool OwnerSeen(string system, out Side? owner, out int age)
    {
        owner = null; age = 0;
        if (_view == null || Held(system)) { owner = _h.Fleets.OwnerOf(system); return true; }
        var o = _view.Picture(system)?.Owner;
        if (o == null) return false;
        owner = o.Value.Owner; age = AgeOf(o);
        return true;
    }

    /// <summary>An own system in uprising (the side's own state).</summary>
    private bool OwnUprising(string system) => Held(system) && _h.Support.IsUprising(system);

    /// <summary>The side's own missions (its own state), id order.</summary>
    private IEnumerable<Praedra.Core.Missions.MissionGroup> OwnMissions() => _h.Missions.Missions.Where(m => m.Side == _side);

    /// <summary>
    /// Enemy characters the side knows to stand at a system, by character id. No fog: every free enemy character located at a system (the
    /// truth). Fog: from the side's own event feed (only at systems it sees today) and its pictures' group D characters (<see cref="FogEnemyCharacters"/>).
    /// </summary>
    private List<(int Character, string System)> EnemyCharactersSeen()
    {
        if (_view != null) return FogEnemyCharacters();
        var list = new List<(int, string)>();
        foreach (var c in _h.Characters.InPlay)
            if (c.Side == Enemy && c.Status == Praedra.Core.Characters.CharacterStatus.Free && c.Transit == null && _h.Characters.LocationOf(c) is { } at)
                list.Add((c.Id, at));
        return list;
    }

    // ================================================================== helpers

    private bool Held(string s) => _h.Fleets.OwnerOf(s) == _side;
    /// <summary>Was the system destroyed by the harvester? Public to both sides (the galaxy notice "Planet Destroyed"); the AI never targets or counts one. rule: m5-harvester §2 effect 5.</summary>
    private bool Destroyed(string s) => _h.Game.State.Harvester.IsDestroyed(s);
    private IEnumerable<string> HeldSystems() => S.SystemOwners.Where(kv => kv.Value == _side).Select(kv => kv.Key); // sorted ordinal
    private int UnratedSpeed => _h.Construction.Params.UnratedDeliverySpeed;
    private int Budget => _h.Economy.Free(_side) - _plannedLoad - _p.MaintenanceMargin;
    private int Maint(string cls) => _h.Economy.Costs.Get(cls).Maintenance;
    private bool Suspended(string sys) => _h.Support.IsUprising(sys) || _h.Fleets.IsEconomicBlockade(sys, _side);

    private int Ticks(string a, string b, int speed)
    {
        if (a == b) return 0;
        var r = _h.Fleets.FindPath(a, b, speed);
        return r.Ok ? r.TotalTicks : int.MaxValue;
    }

    /// <summary>
    /// The capital ships of a fleet as combat units (hull ratio and weapons counter from the objects), WITHOUT the squadrons. The AI does not
    /// trust squadrons when it weighs a fight: a carrier full of Mauls measured 437 and was annihilated by two Mandates in the same
    /// build's battles (see SimpleAiParams.FighterTrustPercent).
    /// </summary>
    private List<Combatant> ShipUnits(Fleet f)
    {
        var list = new List<Combatant>();
        foreach (var id in f.Ships)
        {
            var o = _h.Economy.Find(id); var cls = o == null ? null : _h.Fleets.ClassOf(o);
            if (o == null || cls == null) continue;
            double ratio = cls.Hull > 0 ? Math.Min(1.0, (double)o.Hull / cls.Hull) : 1.0;
            list.Add(_h.Fleets.Strength.Unit(cls, ratio, o.Hits is { Count: > 1 } ? o.Hits[1] : 0));
        }
        return list;
    }

    private List<Combatant>? _refUnits;
    private List<Combatant> RefUnits() => _refUnits ??= _h.Fleets.Blockade.Reference(_h.Fleets.ShipClasses).Select(c => _h.Fleets.Strength.Unit(c)).ToList();

    /// <summary>A fleet's strength against the reference force REF (3 Mandate, m4-battle-new §12), capital ships only; percent-like scale of FleetModule.StrengthOf.</summary>
    private double Str(Fleet f)
    {
        if (!_strength.TryGetValue(f.Id, out var v)) _strength[f.Id] = v = _h.Fleets.Strength.Of(ShipUnits(f), RefUnits());
        return v;
    }

    private bool Available(string cls)
    {
        if (!_h.Economy.Costs.TryGet(cls, out var row) || row.Cost <= 0) return false;
        if (!(_side == Side.Hand ? row.Hand : row.Collective)) return false;
        return _h.Construction.IsResearched(_side, cls);
    }

    private static bool IsYardClass(EconObject o, YardFamily fam) =>
        o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == fam;

    /// <summary>Held systems with a finished, working yard of the family, ordinal order.</summary>
    private List<string> YardSystems(YardFamily fam) =>
        _h.Economy.Objects.Where(o => o.Completed && IsYardClass(o, fam) && o.System != null && Held(o.System) && !Suspended(o.System))
            .Select(o => o.System!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

    /// <summary>Yards of the family owned by the side, queued ones included.</summary>
    private int CountYards(YardFamily fam) => _h.Economy.Objects.Count(o => IsYardClass(o, fam) && _h.Economy.OwnerOf(o) == _side);

    private bool CanOrderAt(string system, YardFamily fam) => !_busy.Contains((system, fam)) && _h.Construction.ProjectAt(system, fam) == null;

    private string? Capital()
    {
        if (_capitalDone) return _capital;
        _capitalDone = true;
        if (_side == Side.Hand && Held(Praedra.Core.Control.ControlModule.CapitalSystem)) return _capital = Praedra.Core.Control.ControlModule.CapitalSystem;
        // No HQ in the core yet: the held system with the most finished facilities (DEFAULT).
        _capital = HeldSystems().Select(s => (s, n: _h.Economy.Objects.Count(o => o.Completed && o.Category == ObjectCategory.Facility && o.System == s)))
            .Where(t => t.n > 0).OrderByDescending(t => t.n).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s).FirstOrDefault();
        return _capital;
    }

    /// <summary>The staging system: a held shipyard system (not the capital if another exists) nearest the enemy's core. New warships gather here.</summary>
    private string? Rally()
    {
        if (_rallyDone) return _rally;
        _rallyDone = true;
        var yards = YardSystems(YardFamily.Shipyard);
        var cap = Capital();
        var cands = yards.Where(s => s != cap).ToList();
        if (cands.Count == 0) cands = yards;
        if (cands.Count == 0) return _rally = cap;
        string? anchor = KnownEnemyHq;
        anchor ??= EnemySystems().Select(s => (s, n: FacilitiesAt(s)))
            .OrderByDescending(t => t.n).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s).FirstOrDefault();
        if (anchor == null) return _rally = cands[0];
        _rally = cands.OrderBy(s => Ticks(s, anchor, 100)).ThenBy(s => s, StringComparer.Ordinal).First();
        return _rally;
    }
}
