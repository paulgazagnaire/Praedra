using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Defenses;
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
/// values come from the side's own picture (possibly stale), the event feed is the side's filtered one, and the Empire's
/// <see cref="KnownEnemyHq"/> is set only from the Empire's own picture of the HQ. With it off (DEFAULT for now) the AI still sees everything.
/// The looks are in one block below ("what the AI may see"), so nothing else reads the other side. With fog the AI also scouts, weighs stale
/// pictures with a margin, hunts the Rebel HQ with a belief map (Empire) and moves its HQ by risk (Alliance): see SimpleAiFog.cs. None of
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
    /// The enemy HQ, when known (the Empire hunts it first). Null = unknown. Without fog the caller may set it; with
    /// <see cref="SimpleAiParams.UseFog"/> it is overwritten every day from the Empire's own picture (m5-fog-and-hq §N5 KnownHq).
    /// </summary>
    public string? KnownEnemyHq { get; set; }
    /// <summary>The orders queued by the last <see cref="PlanDay"/>.</summary>
    public IReadOnlyList<SubmittedOrder> Submitted => _submitted;

    private Side Enemy => _side == Side.Empire ? Side.Alliance : Side.Empire;
    private GameState S => _h.Game.State;
    private int Today => _h.Game.Day;

    // ================================================================== entry

    public void PlanDay()
    {
        _submitted.Clear(); _plannedLoad = 0; _busy.Clear(); _reservedEnergy.Clear(); _reservedRaw.Clear(); _reservedYards.Clear(); _strength.Clear();
        _capitalDone = _rallyDone = false; _hops = null; _finishing = null; _blind = null; _tfStrength = null; _threats = null; _offensives = -1; _lost = null; _lostBattles = null; _runners = null; _bounced = null;
        int day = Today, phase = (int)_side;
        _rng = new GameRng((ulong)day * 2 + (ulong)_side + 0x5A1D, 0xA1);
        _view = _p.UseFog ? _h.Fog.ViewFor(_side) : null;
        Remember();
        _seenForces.Clear(); ClearFogScratch();
        RememberStrongestFleet();
        if (_view != null && _side == Side.Empire) KnownEnemyHq = _view.KnownHq?.System; // only what the Empire's own picture shows
        PlanUpkeepCrisis();
        if (_view != null && _side == Side.Alliance) PlanHqHide(); // fog: the HQ hides by risk (ai/07 §7), checked daily
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
    private readonly Dictionary<string, List<Force>> _seenForces = new(StringComparer.Ordinal); // lookup only

    /// <summary>
    /// One enemy fleet as the AI weighs it: its capital ships as combat units. Without fog: the real fleet (FleetId set, hull and damage read).
    /// With fog: a fleet of the side's picture (class counts at full hull, FleetId null). At = the system it stands at, null on a lane.
    /// </summary>
    /// Gravity: a ship of the fleet has a gravity-well projector (it can pull passing fleets out of hyperspace).
    private sealed record Force(int? FleetId, string? At, List<Combatant> Units, bool Gravity = false);

    private Force ForceOf(Fleet f) => new(f.Id, f.At, ShipUnits(f),
        f.Ships.Any(id => _h.Economy.Find(id) is { } o && _h.Fleets.ShipClasses.TryGetValue(o.Class, out var c) && c.GravityWellProjector > 0));

    private Force ForceOf(string at, Praedra.Core.Fog.FleetPicture p)
    {
        var units = new List<Combatant>(); bool gravity = false;
        foreach (var (cls, n) in p.Ships) // ordinal class order
            if (_h.Fleets.ShipClasses.TryGetValue(cls, out var c)) { for (int i = 0; i < n; i++) units.Add(_h.Fleets.Strength.Unit(c)); gravity |= n > 0 && c.GravityWellProjector > 0; }
        return new Force(null, at, units, gravity);
    }

    /// <summary>Enemy fleets standing at a system. No fog: sees all. Fog: group E of the side's picture, unless the look is only Charted (too old).</summary>
    private List<Force> EnemyFleetsAt(string system)
    {
        if (_view == null) return _h.Fleets.FleetsAt(system).Where(f => f.Side != _side).Select(ForceOf).ToList();
        if (_seenForces.TryGetValue(system, out var cached)) return cached;
        var list = new List<Force>();
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
        return _side == Side.Alliance ? o.Value.AllianceSupport : 100 - o.Value.AllianceSupport;
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
    /// <summary>Was the system destroyed by the Death Star? Public to both sides (the galaxy notice "Planet Destroyed"); the AI never targets or counts one. rule: m5-death-star §2 effect 5.</summary>
    private bool Destroyed(string s) => _h.Game.State.DeathStar.IsDestroyed(s);
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
    /// trust squadrons when it weighs a fight: a carrier full of B-wings measured 437 and was annihilated by two Star Destroyers in the same
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

    /// <summary>A fleet's strength against the reference force REF (3 ISD, m4-battle-new §12), capital ships only; percent-like scale of FleetModule.StrengthOf.</summary>
    private double Str(Fleet f)
    {
        if (!_strength.TryGetValue(f.Id, out var v)) _strength[f.Id] = v = _h.Fleets.Strength.Of(ShipUnits(f), RefUnits());
        return v;
    }

    private bool Available(string cls)
    {
        if (!_h.Economy.Costs.TryGet(cls, out var row) || row.Cost <= 0) return false;
        if (!(_side == Side.Empire ? row.Empire : row.Alliance)) return false;
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
        if (_side == Side.Empire && Held(Praedra.Core.Control.ControlModule.CapitalSystem)) return _capital = Praedra.Core.Control.ControlModule.CapitalSystem;
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

    // ================================================================== economy

    private void PlanEconomy()
    {
        var eco = _h.Economy;
        foreach (var y in YardSystems(YardFamily.Construction))
        {
            if (!CanOrderAt(y, YardFamily.Construction)) continue;
            var choice = ChooseFacility(y);
            if (choice == null) continue;
            var (kind, dest) = choice.Value;
            string cls = FacilityKinds.ClassName(kind);
            int m = Maint(cls);
            if (m > 0 && Budget < m) continue;
            long seq = _h.Construction.SubmitBuild(_side, y, YardFamily.Construction, cls, 1, dest);
            Note(seq, "Build", $"{cls} at {dest} from {y}");
            _busy.Add((y, YardFamily.Construction));
            _plannedLoad += m;
            _reservedEnergy[dest] = _reservedEnergy.GetValueOrDefault(dest) + 1;
            if (kind == FacilityKind.Mine) _reservedRaw[dest] = _reservedRaw.GetValueOrDefault(dest) + 1;
        }
    }

    private (FacilityKind, string)? ChooseFacility(string from)
    {
        var eco = _h.Economy;
        var order = new List<FacilityKind>();
        int refined = eco.Refined(_side);
        int ships = CountYards(YardFamily.Shipyard), train = CountYards(YardFamily.Training), cons = CountYards(YardFamily.Construction);
        if (ships == 0) order.Add(FacilityKind.OrbitalShipyard);
        if (train == 0) order.Add(FacilityKind.TrainingFacility);
        // rule: m2-construction §Build time: a yard adds 1 point per cycle, so throughput is the number of yards; refined that floats is turned into yards.
        if (refined >= _p.ExtraYardRefinedReserve)
        {
            // the construction yards build everything else: the first ones come before the other yards
            if (cons < _p.EarlyConstructionYards) order.Add(FacilityKind.ConstructionYard);
            var wants = new List<(FacilityKind Kind, int Have, int Want)>
            {
                (FacilityKind.OrbitalShipyard, ships, _p.DesiredShipyards),
                (FacilityKind.TrainingFacility, train, TrainingFacilitiesWanted()),
                (FacilityKind.ConstructionYard, cons, _p.DesiredConstructionYards),
            };
            foreach (var w in wants.Where(w => w.Have < w.Want).OrderBy(w => w.Have * 100 / w.Want).ThenBy(w => (int)w.Kind)) order.Add(w.Kind);
        }
        int mines = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Mine" && eco.OwnerOf(o) == _side);
        int refs = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Refinery" && eco.OwnerOf(o) == _side);
        // rule: m2-maintenance §Capacity (capacity = 50 x min(mines, refineries)): keep the two balanced.
        if (mines <= refs) { order.Add(FacilityKind.Mine); order.Add(FacilityKind.Refinery); }
        else { order.Add(FacilityKind.Refinery); order.Add(FacilityKind.Mine); }
        foreach (var kind in order)
        {
            if (!Available(FacilityKinds.ClassName(kind))) continue;
            int m = Maint(FacilityKinds.ClassName(kind));
            if (m > 0 && Budget < m) continue;
            var site = PickSite(from, kind);
            if (site != null) return (kind, site);
        }
        return null;
    }

    private static bool IsYardKind(FacilityKind k) => k is FacilityKind.OrbitalShipyard or FacilityKind.TrainingFacility or FacilityKind.ConstructionYard;

    private static YardFamily FamilyOfKind(FacilityKind k) => Yards.FamilyOf(k);

    /// <summary>Yards of a family standing or queued on a system (the side's).</summary>
    private int YardsAt(string system, YardFamily fam) => _h.Economy.Objects.Count(o => o.System == system && IsYardClass(o, fam));

    private string? PickSite(string from, FacilityKind kind)
    {
        var eco = _h.Economy;
        bool mine = kind == FacilityKind.Mine, yard = IsYardKind(kind);
        string? best = null; int bestScore = int.MinValue;
        foreach (var s in HeldSystems())
        {
            var slots = eco.SlotsOf(s);
            if (!slots.Populated || _h.Support.IsUprising(s)) continue;
            if (eco.FreeEnergy(s) - _reservedEnergy.GetValueOrDefault(s) < 1) continue;
            if (mine && eco.FreeRaw(s) - _reservedRaw.GetValueOrDefault(s) < 1) continue;
            int ticks = Ticks(from, s, UnratedSpeed);
            if (ticks == int.MaxValue) continue;
            int score = slots.Energy * 2 + (mine ? slots.Raw * 3 : 0) + eco.SupportOf(s, _side) / 10 - ticks / 20;
            if (yard)
            {
                // stack on a system that already has yards of the family (they share a project), up to YardsPerSystem; keep yards off the border
                var fam = FamilyOfKind(kind);
                int here = YardsAt(s, fam) + _reservedYards.GetValueOrDefault((s, fam));
                if (here >= _p.YardsPerSystem) continue;
                if (here > 0) score += 40 * here;
                score -= BorderDanger(s) * 15;
            }
            else score -= YardsAnyAt(s) * 3; // keep room on the yard systems for more yards
            if (score > bestScore) { bestScore = score; best = s; }
        }
        if (best != null && yard) { var fam = FamilyOfKind(kind); _reservedYards[(best, fam)] = _reservedYards.GetValueOrDefault((best, fam)) + 1; }
        return best;
    }

    private int YardsAnyAt(string system) => _h.Economy.Objects.Count(o => o.System == system && o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k));

    /// <summary>0 when no nearby system (m1-galaxy-data Nearby) is enemy-held, 2 when one is (a yard there is a target).</summary>
    private int BorderDanger(string system)
    {
        var g = _h.Data.Galaxy;
        if (!g.TryFindSystem(system, out var i)) return 0;
        foreach (var n in g.NearOf(i))
            if (IsEnemyHeld(g.Systems[n].Name)) return 2;
        return 0;
    }

    // ================================================================== production

    private void PlanProduction()
    {
        if (_view != null) PlanScoutBuild();
        if (_view != null && _side == Side.Empire) PlanDroidBuild();
        PlanGarrison();
        PlanInvasionRegiments();
        PlanAgentForceBuild(); // after the regiments: a training yard goes to the garrisons first
        PlanDeathStarBuild(); // before the ships: it takes its yard and its budget first (m5-death-star §6.1)
        PlanShips();
    }

    private string? TrainingYardNear(string dest)
    {
        string? best = null; int bt = int.MaxValue;
        foreach (var t in YardSystems(YardFamily.Training))
        {
            if (!CanOrderAt(t, YardFamily.Training)) continue;
            int ticks = Ticks(t, dest, UnratedSpeed);
            if (ticks < bt) { bt = ticks; best = t; }
        }
        return best;
    }

    private void OrderRegiments(string yard, string cls, int count, string dest)
    {
        long seq = _h.Construction.SubmitBuild(_side, yard, YardFamily.Training, cls, count, dest);
        Note(seq, "Build", $"{count} x {cls} at {yard} for {dest}");
        _busy.Add((yard, YardFamily.Training));
        _plannedLoad += count * Maint(cls);
    }

    /// <summary>Regiments queued per destination (units of projects not yet finished).</summary>
    private Dictionary<string, int> PendingRegiments()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pr in S.Construction.AllProjects())
            if (pr.Side == _side && pr.Family == YardFamily.Training)
            {
                d[pr.Destination] = d.GetValueOrDefault(pr.Destination) + pr.Units.Count;
                foreach (var q in pr.Next ?? new List<QueuedOrder>()) d[q.Destination] = d.GetValueOrDefault(q.Destination) + q.Units.Count; // orders queued behind (B5b)
            }
        return d;
    }

    // rule: m2-uprisings-and-garrison §Required garrison N: keep N regiments on every system where the support is low.
    private void PlanGarrison()
    {
        _garrisonReserve = 0;
        string cls = _p.GarrisonRegiment(_side);
        if (!Available(cls)) return;
        var pending = PendingRegiments();
        var held = HeldSystems();
        bool first = _p.GarrisonsFirst && Finishing(); // far ahead, the side needs its garrisons more than new warships
        // GarrisonsFirst: the worlds in uprising, then the fewest regiments, get the upkeep first (ties: ordinal name)
        if (first) held = held.OrderByDescending(_h.Support.IsUprising).ThenBy(_h.Support.RegimentCount).ThenBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var s in held)
        {
            int need = GarrisonTarget(s);
            if (need == 0) continue;
            int deficit = need - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s);
            if (deficit <= 0) continue;
            int count = Math.Min(deficit, _p.GarrisonBatch);
            if (first) while (count > 1 && Budget < count * Maint(cls)) count--;
            var yard = Budget < count * Maint(cls) ? null : TrainingYardNear(s);
            if (yard == null)
            {
                if (!first) { if (Budget < count * Maint(cls)) continue; return; } // every training yard is busy
                _garrisonReserve += deficit * Maint(cls); // GarrisonsFirst: unpaid or waiting for a yard, its upkeep is still kept from the ships
                continue;
            }
            OrderRegiments(yard, cls, count, s);
            if (first) _garrisonReserve += (deficit - count) * Maint(cls);
        }
    }

    /// <summary>GarrisonsFirst: upkeep the garrisons still short of their need would take, kept from new ships (set by PlanGarrison).</summary>
    private int _garrisonReserve;
    /// <summary>
    /// Training facilities wanted: DesiredTrainingFacilities, and with GarrisonsFirst while far ahead one more per GarrisonShortPerTrainingFacility
    /// regiments the garrisons are short (queued ones count), up to MaxTrainingFacilities (one facility trains about 10 regiments in 250 days:
    /// m2-construction §Build time).
    /// </summary>
    private int TrainingFacilitiesWanted()
    {
        if (!_p.GarrisonsFirst || !Finishing()) return _p.DesiredTrainingFacilities;
        var pending = PendingRegiments();
        int shortBy = HeldSystems().Sum(s => Math.Max(0, GarrisonTarget(s) - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s)));
        return Math.Min(Math.Max(_p.DesiredTrainingFacilities, _p.MaxTrainingFacilities), _p.DesiredTrainingFacilities + shortBy / Math.Max(1, _p.GarrisonShortPerTrainingFacility));
    }

    private void PlanInvasionRegiments()
    {
        var rally = Rally();
        string cls = _p.InvasionRegiment(_side);
        if (rally == null || !Available(cls)) return;
        // InvasionBatchesOnly: a lone spare regiment on a far system is not an invasion force (a strike pile does not fly across the map for one)
        int have = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == _side && o.AboardShip != 0)
                   + HeldSystems().Select(SpareRegimentsAt).Where(n => !_p.InvasionBatchesOnly || n >= _p.MinLaunchRegiments).Sum();
        have += PendingRegiments().GetValueOrDefault(rally);
        if (have >= _p.InvasionRegimentTarget) return;
        int count = Math.Min(_p.InvasionRegimentTarget - have, _p.GarrisonBatch);
        if (Budget < count * Maint(cls)) return;
        var yard = TrainingYardNear(rally);
        if (yard != null) OrderRegiments(yard, cls, count, rally);
    }

    /// <summary>Landed regiments of the side on a system above what its garrison needs.</summary>
    internal List<int> SpareRegimentIds(string system)
    {
        var ids = _h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == _side && o.System == system && o.AboardShip == 0)
            .Select(o => o.Id).ToList();
        int keep = Held(system) ? GarrisonTarget(system) : 0;
        return ids.Count > keep ? ids.Skip(keep).ToList() : new List<int>();
    }

    // The Classic AI's garrison wish (B6b §3, EXE-own 0x434e4d..0x43507c): below 70 support min((79 - s) / 10, 6), doubled in an uprising (after the cap);
    // the special world's base is 9 (the claimed-world base 3 and the base drift are the Classic AI's, not built here).
    private const int WishBelowSupport = 70, WishTop = 79, WishStep = 10, WishCap = 6, SpecialBase = 9, UprisingFactor = 2;

    /// <summary>
    /// Regiments the AI wants standing on a held world: built toward (PlanGarrison) and never moved off (SpareRegimentIds). With
    /// <see cref="SimpleAiParams.ClassicGarrisonFloor"/> (DEFAULT): max(the rule's need, the Classic wish, 9 on the special world). Off: the rule's need,
    /// <see cref="SimpleAiParams.CriticalGarrison"/> on the critical world, and never the last regiment below 60 support (the B5a guard, Dev lead
    /// 2026-10-17). rule: ai tuning, B6b §3 and §11 (Dev lead 2026-10-18).
    /// </summary>
    /// <summary>
    /// The world the Classic AI's special base (9) applies to: Coruscant for the Empire; for the Alliance the Rebel HQ's world, or while the HQ is on
    /// the way, its destination (its own HQ: the side knows where it goes), so regiments are ordered there when the move starts and the HQ does
    /// not arrive on an unguarded world (QA-10, Dev lead 2026-10-18). rule: ai tuning, B6b §3 ("special" system).
    /// </summary>
    private string? SpecialSystem()
    {
        if (CriticalSystem() is { } c) return c;
        var hq = _h.Game.State.Scenario.Hq;
        return _side == Side.Alliance && hq.Status == Praedra.Core.Fog.HqStatus.InTransit && hq.Path.Count > 0 ? hq.Path[^1] : null;
    }

    internal int GarrisonTarget(string system)
    {
        int need = _h.Support.RequiredGarrison(system);
        int s = _h.Economy.SupportOf(system, _side); // its own world: the AI sees it
        if (!_p.ClassicGarrisonFloor)
        {
            if (system == CriticalSystem()) need = Math.Max(need, _p.CriticalGarrison);
            if (s < Praedra.Core.Control.ControlModule.JoinSupport) need = Math.Max(need, 1);
            return need;
        }
        int wish = s < WishBelowSupport ? Math.Min((WishTop - s) / WishStep, WishCap) : 0;
        if (_h.Support.IsUprising(system)) wish *= UprisingFactor;
        int @base = system == SpecialSystem() ? SpecialBase : 0;
        return Math.Max(need, Math.Max(wish, @base));
    }
    private int SpareRegimentsAt(string system) => SpareRegimentIds(system).Count;

    private readonly Dictionary<string, double> _classStrength = new(StringComparer.Ordinal); // lookup only; static data, so it survives days

    /// <summary>Strength of one ship or squadron of the class against the side's reference enemy line (FleetModule's CombatStrength, shields full).</summary>
    private double ClassStrength(string cls)
    {
        if (_classStrength.TryGetValue(cls, out var v)) return v;
        var ships = _h.Fleets.ShipClasses;
        var versus = _p.ReferenceEnemy(_side).Where(ships.ContainsKey).Select(n => ships[n]).ToList();
        v = ships.TryGetValue(cls, out var sc) && versus.Count > 0 ? _h.Fleets.Strength.Of(new[] { sc }, versus) : 0;
        return _classStrength[cls] = v;
    }

    private IEnumerable<string> ShipClassNames(ObjectCategory cat) =>
        _h.Fleets.ShipClasses.Keys.OrderBy(n => n, StringComparer.Ordinal)
            .Where(n => _h.Economy.Costs.TryGet(n, out var r) && r.Category == cat && Available(n));

    /// <summary>The fighter class with the most strength per refined cost plus upkeep. Null when none.</summary>
    private string? BestFighter()
    {
        string? best = null; double bs = 0;
        foreach (var n in ShipClassNames(ObjectCategory.Fighter))
        {
            var r = _h.Economy.Costs.Get(n);
            double sc = ClassStrength(n) / (r.Cost + r.Maintenance);
            if (sc > bs) { bs = sc; best = n; }
        }
        return best;
    }

    private int YardsDoneAt(string system, YardFamily fam) => _h.Economy.Objects.Count(o => o.Completed && o.System == system && IsYardClass(o, fam));

    /// <summary>
    /// The warship that gives the most strength per price (refined cost plus upkeep), counting each fighter berth as one squadron of the best
    /// fighter; only classes the yards of the system finish within MaxBuildDays. Null when nothing fits.
    /// </summary>
    private string? BestWarship(string yardSystem, string? fighter)
    {
        var eco = _h.Economy;
        int yards = Math.Max(1, YardsDoneAt(yardSystem, YardFamily.Shipyard));
        double fighterStr = fighter != null ? ClassStrength(fighter) : 0;
        string? best = null; double bs = 0;
        foreach (var n in ShipClassNames(ObjectCategory.CapitalShip))
        {
            if (n == EconomyModule.DeathStarClass) continue;
            var sc = _h.Fleets.ShipClasses[n]; var r = eco.Costs.Get(n);
            // rule: m2-construction §Build time: ceil(cost / yards) x 4 days per unit (standard yards)
            if ((r.Cost + yards - 1) / yards * 4 > _p.MaxBuildDays) continue;
            if (Budget < r.Maintenance) continue;
            double value = ClassStrength(n) + fighterStr * sc.FighterCapacity * _p.FighterTrustPercent / 100;
            if (value <= 0) continue;
            double score = value / (r.Cost + _p.UpkeepWeight * r.Maintenance); // upkeep is the lasting limit (m2-maintenance), so it weighs more than the one-time cost
            if (score > bs + 1e-9) { bs = score; best = n; }
        }
        return best;
    }

    private void PlanShips()
    {
        var eco = _h.Economy;
        var rally = Rally(); var cap = GuardSystem();
        int troopCap = 0, berths = 0, fighters = 0;
        foreach (var o in eco.Objects)
        {
            if (eco.OwnerOf(o) != _side) continue;
            if (o.Category == ObjectCategory.CapitalShip && _h.Fleets.ShipClasses.TryGetValue(o.Class, out var sc)) { if (o.Class == _p.Transport(_side)) troopCap += sc.TroopCapacity; berths += sc.FighterCapacity; } // only transports count as troop ships: warships carry a few, but stay with the fleet
            else if (o.Category == ObjectCategory.Fighter) fighters++;
        }
        double home = 0;
        if (cap != null) foreach (var f in _h.Fleets.FleetsAt(cap)) if (f.Side == _side) home += Str(f);
        bool homeWeak = home < _p.HomeDefenceStrength;
        string? fighter = BestFighter();
        bool transportOrdered = false;

        foreach (var y in YardSystems(YardFamily.Shipyard))
        {
            if (!CanOrderAt(y, YardFamily.Shipyard)) continue;
            string? dest = rally;
            if (dest != null && dest != y && Ticks(y, dest, UnratedSpeed) == int.MaxValue) dest = null;
            string? destArg = dest == y ? null : dest;

            string? cls = null; int count = 1;
            string transport = _p.Transport(_side);
            if (troopCap < _p.TroopCapacityTarget && Available(transport) && !transportOrdered)
            {
                cls = transport;
                int per = Math.Max(1, _h.Fleets.ShipClasses[transport].TroopCapacity);
                count = Math.Min(2, (_p.TroopCapacityTarget - troopCap + per - 1) / per);
                transportOrdered = true;
            }
            else if (fighter != null && fighters * 100 < berths * _p.FighterFillPercent && berths - fighters >= 1)
            {
                cls = fighter;
                count = Math.Clamp(berths * _p.FighterFillPercent / 100 - fighters, 1, Math.Min(berths - fighters, _p.FightersPerOrder));
            }
            else
            {
                cls = BestWarship(y, fighter);
                if (cls != null)
                {
                    // the home guard is topped up with armed ships (only where it has a shipyard to repair at)
                    if (homeWeak && cap != null && ClassStrength(cls) > 0 && Ticks(y, cap, UnratedSpeed) != int.MaxValue) { dest = cap; destArg = cap == y ? null : cap; }
                    int cost = eco.Costs.Get(cls).Cost;
                    count = Math.Clamp(eco.Refined(_side) / Math.Max(1, cost * 3), 1, _p.MaxShipsPerOrder);
                }
            }
            if (cls == null) continue;
            // rule: m2-construction §Build time: an order keeps the yard busy for count x ceil(cost / yards) x 4 days; cap it so the yard can answer news
            {
                int yardsHere = Math.Max(1, YardsDoneAt(y, YardFamily.Shipyard)); int unitDays = (eco.Costs.Get(cls).Cost + yardsHere - 1) / yardsHere * 4;
                count = Math.Max(1, Math.Min(count, _p.MaxOrderDays / Math.Max(1, unitDays)));
            }
            int budget = Budget - _garrisonReserve; // GarrisonsFirst: the garrisons are paid first
            while (count > 1 && budget < count * Maint(cls)) count--;
            if (budget < count * Maint(cls)) continue;
            long seq = _h.Construction.SubmitBuild(_side, y, YardFamily.Shipyard, cls, count, destArg);
            Note(seq, "Build", $"{count} x {cls} at {y} for {dest ?? y}");
            _busy.Add((y, YardFamily.Shipyard));
            _plannedLoad += count * Maint(cls);
            var cr = eco.Costs.Get(cls);
            if (cr.Category == ObjectCategory.CapitalShip) { troopCap += count * _h.Fleets.ShipClasses[cls].TroopCapacity; berths += count * _h.Fleets.ShipClasses[cls].FighterCapacity; }
            else if (cr.Category == ObjectCategory.Fighter) fighters += count;
        }
    }

    /// <summary>True when an enemy fleet sits on or flies toward the system.</summary>
    private bool UnderThreat(string? system) => system != null && IncomingEnemyStrength(system) > 0;

    // ================================================================== upkeep crisis

    /// <summary>
    /// rule: m2-maintenance §Overdraft and autoscrap. When the load is above the capacity (a capture took mines or refineries away) the game
    /// would scrap one random finished object every 10 days (a yard as likely as a regiment). The AI scraps its least useful items itself,
    /// right away, enough to be back under the capacity. Yards, mines and refineries are never offered.
    /// </summary>
    private void PlanUpkeepCrisis()
    {
        int over = -_h.Economy.Free(_side);
        if (over <= 0) return;
        var eco = _h.Economy;
        string? fighter = BestFighter();
        double fighterStr = fighter != null ? ClassStrength(fighter) : 0;
        var cands = new List<(double Use, int Id, int Maint)>();
        foreach (var o in eco.Objects.OrderBy(o => o.Id))
        {
            if (!o.Completed || eco.OwnerOf(o) != _side || o.Category == ObjectCategory.Facility || o.Category == ObjectCategory.SpecialForce) continue;
            int m = Maint(o.Class);
            bool keep = _p.UseDeathStar && IsDeathStarShip(o); // rule: m5-death-star §6.2: scrapped last, after everything else
            if (m <= 0) continue;
            double use;
            if (o.Category == ObjectCategory.Regiment)
            {
                if (o.AboardShip != 0 || o.System == null || !Held(o.System)) continue;
                if (!SpareRegimentIds(o.System).Contains(o.Id)) continue; // garrison regiments hold the system
                use = -1;
            }
            else
            {
                var f = _h.Fleets.FleetOfShip(o.Id);
                if (o.Category == ObjectCategory.CapitalShip && (f == null || f.Transit != null || Pending(f))) continue;
                if (o.Category == ObjectCategory.Fighter && o.AboardShip != 0 && _h.Fleets.FleetOfShip(o.AboardShip) is { Transit: not null }) continue;
                _h.Fleets.ShipClasses.TryGetValue(o.Class, out var sc);
                double v = ClassStrength(o.Class) + (sc != null ? 6 * sc.TroopCapacity + fighterStr * sc.FighterCapacity * _p.FighterTrustPercent / 100 : 0);
                use = v * 1000 / m;
                if (keep) use += 1e12;
            }
            cands.Add((use, o.Id, m));
        }
        foreach (var c in cands.OrderBy(c => c.Use).ThenBy(c => c.Id))
        {
            if (over <= 0) break;
            long seq = _h.Game.Submit(sq => new ScrapObjectOrder(sq, _side, c.Id));
            Note(seq, "Scrap", $"object {c.Id} (upkeep over capacity)");
            over -= c.Maint;
        }
    }

    // ================================================================== military

    private Dictionary<string, List<Force>>? _threats;
    private HashSet<string>? _lost;

    /// <summary>Held systems with enemy fleets standing on them or flying toward them (a stop on their path), by system. Fleets in id order.</summary>
    private Dictionary<string, List<Force>> Threats()
    {
        if (_threats != null) return _threats;
        var d = new Dictionary<string, List<Force>>(StringComparer.Ordinal);
        void Add(string sys, Force f) { if (!d.TryGetValue(sys, out var l)) d[sys] = l = new List<Force>(); l.Add(f); }
        if (_view != null)
        {
            // Fog: only the enemy fleets the side sees standing on its own systems (a fleet on a lane is invisible). rule: m5-fog-and-hq §F3
            foreach (var (sys, fp) in _view.VisibleFleets())
                if (fp.Side == Enemy && fp.ShipCount > 0 && Held(sys)) Add(sys, ForceOf(sys, fp));
            return _threats = d;
        }
        foreach (var f in S.Fleets.Values.Where(f => f.Side == Enemy && f.Ships.Count > 0).OrderBy(f => f.Id))
        {
            if (f.At != null) { if (Held(f.At)) Add(f.At, ForceOf(f)); }
            else if (f.Transit != null)
                for (int i = f.Transit.HopIndex + 1; i < f.Transit.Path.Count; i++)
                    if (Held(f.Transit.Path[i])) Add(f.Transit.Path[i], ForceOf(f));
        }
        return _threats = d;
    }

    private double IncomingEnemyStrength(string system) => Threats().TryGetValue(system, out var l) ? l.Sum(Str) : 0;

    /// <summary>An enemy force's strength against REF (capital ships only), as <see cref="Str(Fleet)"/>.</summary>
    private double Str(Force x) => x.FleetId is int id && S.Fleets.TryGetValue(id, out var f) ? Str(f) : _h.Fleets.Strength.Of(x.Units, RefUnits());

    private Dictionary<string, int>? _lostBattles;
    /// <summary>
    /// Systems where this side was annihilated in a battle lately, with the day. The measure of strength is only a guess, so a fight that
    /// went wrong makes the AI ask for much better odds there for a while (never repeat a suicide). With RetreatCountsAsLoss a battle its fleets
    /// left by the morale retreat (m4-battle-classic) counts too: the measure called the fight good and the battle disagreed.
    /// </summary>
    private Dictionary<string, int> LostBattles()
    {
        if (_lostBattles != null) return _lostBattles;
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in NotesSince(AiNoteKind.Battle, Today - _p.LossMemoryDays))
        {
            if (d.ContainsKey(b.System!)) continue;
            if (b.Own == BattleOutcome.Annihilated || (_p.RetreatCountsAsLoss && b.Own == BattleOutcome.Retreated && OwnArmedIn(b))) d[b.System!] = b.Day;
        }
        return _lostBattles = d;
    }

    /// <summary>
    /// Did this side bring an armed capital ship to the battle that ended at ev[end]? Read from the battle's BattleStarted (same day, same system)
    /// and the side's OWN units only. An unarmed ship (a scout) always retreats from armed enemies (m4-battle-classic morale), which says nothing
    /// about the odds of a strike there. True when the start is not in the feed.
    /// </summary>
    private bool OwnArmedIn(AiNote battle) => battle.Armed == null || battle.Armed.Any(IsArmedCapital); // noted: units armed then; a ship lost since no longer counts

    /// <summary>The odds (percent) asked before fighting at a system: the base, times <see cref="SimpleAiParams.LossOddsFactor"/> where the side was annihilated (or bounced, see LostBattles) lately.</summary>
    private double OddsAt(string system, int basePercent) => LostBattles().ContainsKey(system) ? basePercent * _p.LossOddsFactor : basePercent;

    private Dictionary<string, int>? _runners, _bounced;

    /// <summary>
    /// Battles of the last RunnerMemoryDays where the other side retreated (rule: m4-retreat-damage-repair §1, the morale retreat of
    /// m4-battle-classic) and this side held the field, counted by system. Read from the side's own event feed (BattleEnded goes to both
    /// sides, m5-fog-and-hq §N8; read through the AI memory, which a save carries): a fleet that only retreats is remembered for RunnerMemoryDays.
    /// </summary>
    private Dictionary<string, int> Runners()
    {
        if (_runners != null) return _runners;
        ScanRetreats();
        return _runners!;
    }

    /// <summary>Battles of the last RunnerMemoryDays where this side retreated with an armed capital ship, counted by system (own event feed).</summary>
    private Dictionary<string, int> Bounced()
    {
        if (_bounced != null) return _bounced;
        ScanRetreats();
        return _bounced!;
    }

    private void ScanRetreats()
    {
        _runners = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        _bounced = new Dictionary<string, int>(StringComparer.Ordinal); // lookup only
        foreach (var b in NotesSince(AiNoteKind.Battle, Today - _p.RunnerMemoryDays))
        {
            string sys = b.System!;
            if (b.Other == BattleOutcome.Retreated && b.Own is not (BattleOutcome.Retreated or BattleOutcome.Annihilated)) _runners[sys] = _runners.GetValueOrDefault(sys) + 1;
            if (b.Own == BattleOutcome.Retreated && OwnArmedIn(b)) _bounced[sys] = _bounced.GetValueOrDefault(sys) + 1;
        }
    }

    /// <summary>
    /// True when enemy fleets retreated from this side at the system or at a system one lane from it at least RunnerRetreats times lately: a
    /// retreating fleet jumps to a neighbour (m4-retreat-damage-repair §3), so hunting it there only starts the next jump. Its fleets there
    /// are not hunted for their own sake (the system itself stays a target for an assault).
    /// </summary>
    private bool EnemyRunsNear(string system)
    {
        var r = Runners();
        if (r.Count == 0 || _p.RunnerRetreats <= 0) return false;
        int n = r.GetValueOrDefault(system);
        var g = _h.Data.Galaxy;
        if (g.TryFindSystem(system, out var i))
            foreach (var nb in g.NearOf(i)) n += r.GetValueOrDefault(g.Systems[nb].Name);
        return n >= _p.RunnerRetreats;
    }

    /// <summary>Systems the side lost to the enemy lately (retake targets).</summary>
    private HashSet<string> LostSystems()
    {
        if (_lost != null) return _lost;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in NotesSince(AiNoteKind.SystemLost, Today - _p.RetakeDays)) set.Add(c.System!);
        return _lost = set;
    }

    /// <summary>
    /// Own power as a percent of the other force's: CombatStrength(own, enemy) over CombatStrength(enemy, own), shields full (m4-battle-new §12).
    /// A huge number when the enemy cannot hurt us, 0 when we cannot hurt it, 100 when nobody can.
    /// </summary>
    private double Duel(IEnumerable<Fleet> own, IEnumerable<Force> enemy)
    {
        var o = own.SelectMany(ShipUnits).ToList();
        var e = enemy.SelectMany(x => x.Units).ToList();
        if (e.Count == 0) return 1e9;
        if (o.Count == 0) return 0;
        double a = _h.Fleets.Strength.Of(o, e), b = _h.Fleets.Strength.Of(e, o);
        if (b <= 0) return a > 0 ? 1e9 : 100;
        return a * 100 / b;
    }

    private string? CriticalSystem() => _side == Side.Empire ? Praedra.Core.Control.ControlModule.CapitalSystem : _h.HqSystem; // the system whose loss ends the game for the side
    /// <summary>
    /// The home guard stands at the system whose loss ends the game for the side, when the side holds it. New warships are only sent there
    /// when it has a shipyard (so the guard can be topped up); otherwise the guard is whatever fleet already stands there.
    /// </summary>
    private string? GuardSystem() { var c = CriticalSystem(); return c != null && Held(c) ? c : null; }
    // The Empire only strikes an HQ it has been told about (KnownEnemyHq); no peeking at the true HQ (economy-ai D4: the AI plays fair with fog of war).
    private string? WinTarget() => _side == Side.Empire ? KnownEnemyHq : Praedra.Core.Control.ControlModule.CapitalSystem;

    private bool Pending(Fleet f) => _h.Bombardment.HasPending(f.Id) || _h.Assault.HasPending(f.Id);

    private int _offensives = -1;
    /// <summary>
    /// Offensives under way (MaxOffensives): armed own fleets flying to, or standing on, a system the side does not hold; counted once per day,
    /// plus the strikes launched today. Own state only.
    /// </summary>
    private int Offensives()
    {
        if (_offensives >= 0) return _offensives;
        int n = 0;
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Ships.Count > 0).OrderBy(f => f.Id))
        {
            string? dest = f.At ?? (f.Transit is { } t ? t.Path[^1] : null);
            if (dest != null && !Held(dest) && Str(f) > 0) n++;
        }
        return _offensives = n;
    }

    /// <summary>Marks a Move note whose order is the first leg of a way around; the trip's goal follows it.</summary>
    public const string OnTheWayTo = "on the way to ";

    private bool Move(Fleet f, string dest)
    {
        if (f.At == null || dest == f.At || f.Transit != null) return false;
        int speed = _h.Fleets.SpeedOf(f);
        var path = _h.Fleets.FindPath(f.At, dest, speed);
        if (speed <= 0 || !path.Ok) return false;
        if (_p.AvoidBlockedPaths && PathBlockedAt(f, path) is { } blocked)
        {
            // the way would end in a retreat: go around (no stop at a blocked system), one leg at a time; on arrival the next review plans
            // again. Without a way around the fleet stays. Proposal (lead 2026-10-16; the AI uses the router, movement-redesign-draft §D).
            var g = _h.Data.Galaxy; var avoid = new List<int>();
            for (int tries = 0; tries < 16 && blocked != null; tries++)
            {
                if (blocked == dest || !g.TryFindSystem(blocked, out int bi)) return false;
                avoid.Add(bi);
                path = _h.Fleets.FindPathAvoiding(g.Get(f.At).Index, g.Get(dest).Index, speed, avoid);
                if (!path.Ok) return false;
                blocked = PathBlockedAt(f, path);
            }
            if (blocked != null) return false;
            if (_p.DetourWithWaypoints)
            {
                // the whole way around in one order: the stops of the avoiding path become waypoints, used only when the order flies exactly
                // that path (a stretch between two stops could otherwise cut back through an avoided system). rule: m1-travel §Path rule (3).
                var stops = path.Systems.Skip(1).Take(path.Systems.Count - 2).Select(i => g.Systems[i].Name).ToList();
                var via = _h.Fleets.PlanPath(f, dest, stops);
                if (via.Ok && via.Systems.SequenceEqual(path.Systems))
                {
                    long ws = _h.Fleets.SubmitMove(_side, f.Id, dest, stops);
                    Note(ws, "Move", $"fleet {f.Id} {f.At} -> {dest} via {string.Join(", ", stops)}");
                    return true;
                }
            }
            string first = g.Systems[path.Systems[1]].Name;
            if (first != dest && !Held(first) && _p.DetourToHeldStop && DetourLeg(f, path, speed) is { } leg2) first = leg2;
            if (first != dest)
            {
                var leg = _h.Fleets.FindPath(f.At, first, speed);
                if (!leg.Ok || PathBlockedAt(f, leg) != null) return false;
                // the order is only the first leg of the way around: the note names the trip's goal, so a reader of the orders (a trace, a
                // test counting offensives) tells a stop on the way (a relief of a held system may stop on a neutral one) from a target
                long legSeq = _h.Fleets.SubmitMove(_side, f.Id, first);
                Note(legSeq, "Move", $"fleet {f.Id} {f.At} -> {first} {OnTheWayTo}{dest}");
                return true;
            }
        }
        long seq = _h.Fleets.SubmitMove(_side, f.Id, dest);
        Note(seq, "Move", $"fleet {f.Id} {f.At} -> {dest}");
        return true;
    }

    /// <summary>
    /// DetourToHeldStop: the leg of a detour that keeps the fleet's purpose. The AI keeps no memory between days, so a fleet that stops at a
    /// system it does not hold plans there as at a target (DecideAtTarget): with no business there it leaves for the rally system and the
    /// trip it was on is lost. Measured on the v5 map (Alliance vs passive Empire, seed 1): a strike pile fetching troops went around a blocked
    /// stop to an Imperial system, turned back to the rally, and did that again for 200 days with its troop holds empty. So the leg ends at
    /// the first stop of the way around that the side holds (there DecideOwn plans the rest of the trip again), when the direct trip there is
    /// clear. Null (no such stop) = keep the first stop, as the AI of before.
    /// </summary>
    private string? DetourLeg(Fleet f, Praedra.Core.Travel.PathResult around, int speed)
    {
        var g = _h.Data.Galaxy;
        string dest = g.Systems[around.Systems[^1]].Name;
        for (int k = 2; k < around.Systems.Count - 1; k++)
        {
            string n = g.Systems[around.Systems[k]].Name;
            if (!Held(n)) continue;
            var leg = _h.Fleets.FindPath(f.At!, n, speed);
            if (leg.Ok && PathBlockedAt(f, leg) == null) return n;
        }
        return null;
    }

    /// <summary>
    /// The first stop of the trip where it would end in a retreat, or null: a stop (the destination included) where this side's fleets retreated
    /// within RunnerMemoryDays (own event feed), or a stop on the way where enemy fleets the side sees now outmatch the fleets of the side at the
    /// start (RetreatOddsPercent). rule: m1-forced-stop-and-blockade (an enemy fleet that triggers a forced stop halts a passing one; the order is cancelled), then the
    /// morale retreat of m4-battle-classic sends the fleet back: the same order again only repeats the bounce. The destination's fleets are
    /// weighed by the caller (PickTarget, PickRelief).
    /// </summary>
    private string? PathBlockedAt(Fleet f, Praedra.Core.Travel.PathResult path)
    {
        var g = _h.Data.Galaxy;
        List<Fleet>? own = null;
        for (int k = 1; k < path.Systems.Count; k++)
        {
            string n = g.Systems[path.Systems[k]].Name;
            if (BouncedOff(n)) return n;
            if (k == path.Systems.Count - 1) break;
            // only the enemy fleets that would pull this fleet out of hyperspace there (the game's forced-stop trigger; DEFAULT GravityWellShip
            // since 2026-10-16: an ordinary fleet does not stop a passing one, so the AI no longer refuses trips past it)
            var enemies = EnemyFleetsAt(n);
            if (_view != null && _p.FogAvoidEnemyStops && IsEnemyHeld(n) && _view.Level(n) is not (Praedra.Core.Fog.KnowledgeLevel.Held or Praedra.Core.Fog.KnowledgeLevel.Observed))
                return n; // an enemy system whose fleets the side cannot see now: it may hide a gravity well
            if (enemies.Count == 0) continue;
            // pulled out by a gravity well, the fleet then fights every enemy fleet there
            if (_h.Fleets.Trigger == ForcedStopTrigger.GravityWellShip && !enemies.Any(e => e.Gravity)) continue;
            own ??= _h.Fleets.FleetsAt(f.At!).Where(x => x.Side == _side).ToList();
            if (Duel(own, enemies) < _p.RetreatOddsPercent) return n;
        }
        return null;
    }

    /// <summary>The side's fleets retreated from a battle at the system within RunnerMemoryDays: no trip goes there for now (AvoidBlockedPaths).</summary>
    private bool BouncedOff(string system) => _p.AvoidBlockedPaths && Bounced().ContainsKey(system);

    private sealed class Pile
    {
        public required Fleet Leader { get; init; }
        public required List<Fleet> Members { get; init; }
        public double Strength; public int Aboard, FreeSlots, Berths; public double HullPct = 100;
    }

    private void PlanMilitary()
    {
        var idle = S.Fleets.Values.Where(f => f.Side == _side && f.Transit == null && f.At != null && f.Ships.Count > 0 && !Pending(f)).ToList();
        if (_view != null) { var scouts = PlanScouts(idle); idle = idle.Where(f => !scouts.Contains(f.Id)).ToList(); } // fog: scouts go their own way
        var cap = GuardSystem();
        var dsFleets = PrepareDeathStar(idle);
        foreach (var g in idle.GroupBy(f => f.At!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.OrderBy(f => f.Id).ToList();
            var dsHere = list.Where(f => dsFleets.Contains(f.Id)).ToList();
            if (dsHere.Count > 0) list = list.Where(f => !dsFleets.Contains(f.Id)).ToList();
            if (!Held(g.Key))
            {
                foreach (var d in dsHere) DeathStarAway(d);
                foreach (var f in list) DecideAtTarget(f);
                continue;
            }
            var strike = list;
            if (g.Key == cap)
            {
                // The home defence: the lowest ids up to the home strength stay and merge; the rest are strike fleets.
                double acc = 0; var home = new List<Fleet>(); strike = new List<Fleet>();
                foreach (var f in list)
                {
                    if (acc < _p.HomeDefenceStrength && Str(f) > 0) { home.Add(f); acc += Str(f); } else strike.Add(f);
                }
                for (int i = 1; i < home.Count; i++) Merge(home[i], home[0]);
                if (home.Count == 1 && _p.SplitHomeSurplus) SplitHomeSurplus(home[0], acc);
            }
            if (dsHere.Count > 0) { strike = DeathStarHome(dsHere, strike); }
            else strike = DeathStarCall(g.Key, strike);
            if (strike.Count == 0) continue;
            var forces = TaskForces(strike);
            _tfClaims = forces.Count > 1 ? Claims() : null;
            foreach (var tf in forces)
            {
                var leader = tf[0];
                for (int i = 1; i < tf.Count; i++) Merge(tf[i], leader);
                DecideOwn(MakePile(leader, tf));
            }
            _tfClaims = null;
        }
    }

    /// <summary>Task forces: systems other piles of one group may not pick (lookup only); null when the group formed one pile.</summary>
    private HashSet<string>? _tfClaims;

    /// <summary>Task forces: the systems not held that own armed fleets are flying to (another task force already took them on).</summary>
    private HashSet<string> Claims()
    {
        var c = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Transit != null && f.Ships.Count > 0))
            if (!Held(f.Transit!.Path[^1]) && Str(f) > 0) c.Add(f.Transit.Path[^1]);
        return c;
    }

    /// <summary>
    /// TaskForces: the strike fleets idle on one held system as piles (fleet id order within each). One pile (all of them, as before) unless the
    /// switch is on and their strength holds at least two task forces (TaskForceStrength). Then, armed fleets strongest first (ties by id), each
    /// pile takes fleets until it reaches TaskForceStrength and the next starts, up to TaskForceMax; a pile left short joins the weakest full one;
    /// unarmed fleets join the pile with the fewest regiments aboard. A fleet that alone holds two task forces splits off ships worth one (its
    /// highest ship ids, keeping the rest at least one task force): the new fleet is a pile of its own from the next review.
    /// </summary>
    private List<List<Fleet>> TaskForces(List<Fleet> strike)
    {
        var one = new List<List<Fleet>> { strike };
        if (!_p.TaskForces || _p.TaskForceMax < 2) return one;
        double tfs = TaskForceStrength();
        if (tfs <= 0 || strike.Sum(Str) < 2 * tfs) return one;
        var big = strike.Where(f => Str(f) >= 2 * tfs && f.Ships.Count >= 2).OrderByDescending(Str).ThenBy(f => f.Id).FirstOrDefault();
        if (big != null) SplitTaskForce(big, tfs);
        var piles = new List<List<Fleet>>(); var cur = new List<Fleet>(); double acc = 0;
        foreach (var f in strike.Where(f => Str(f) > 0).OrderByDescending(Str).ThenBy(f => f.Id))
        {
            cur.Add(f); acc += Str(f);
            if (acc >= tfs && piles.Count < _p.TaskForceMax - 1) { piles.Add(cur); cur = new List<Fleet>(); acc = 0; }
        }
        if (cur.Count > 0)
        {
            if (piles.Count == 0 || acc >= tfs) piles.Add(cur);
            else piles.OrderBy(pl => pl.Sum(Str)).First().AddRange(cur);
        }
        if (piles.Count < 2) return one;
        foreach (var f in strike.Where(f => Str(f) <= 0).OrderBy(f => f.Id))
            piles.Select((pl, k) => (pl, k)).OrderBy(t => t.pl.Sum(x => _h.Troops.RegimentsAboard(x).Count)).ThenBy(t => t.k).First().pl.Add(f);
        return piles.Select(pl => pl.OrderBy(f => f.Id).ToList()).ToList();
    }

    /// <summary>Task forces: split ships worth at least one task force off a fleet that holds two (highest ship ids first).</summary>
    private void SplitTaskForce(Fleet f, double tfs)
    {
        var cls = f.Ships.Select(id => (Id: id, S: ClassStrength(_h.Economy.Find(id)!.Class))).ToList();
        double total = cls.Sum(c => c.S), strength = Str(f);
        if (total <= 0) return;
        var off = new List<int>(); double got = 0;
        foreach (var (id, cs) in cls.Where(c => c.S > 0).OrderByDescending(c => c.Id))
        {
            if (got >= tfs) break;
            double share = strength * cs / total;
            if (strength - got - share < tfs) continue;
            off.Add(id); got += share;
        }
        if (got < tfs || off.Count == 0 || off.Count == f.Ships.Count) return;
        off.Sort();
        long seq = _h.Fleets.SubmitSplitFleet(_side, f.Id, off);
        Note(seq, "Split", $"{off.Count} ships off fleet {f.Id} at {f.At} (task force)");
    }

    /// <summary>
    /// Task forces: the least strength of one task force, TaskForceMarginPercent of the strongest enemy fleet the side knows of (the picture's
    /// fleets with fog, older sightings fading by TaskForceFadePercent; every enemy fleet without), and at least LaunchStrength (cached per day).
    /// </summary>
    private double TaskForceStrength() => _tfStrength ??= Math.Max(_p.LaunchStrength, StrongestEnemyFleet() * _p.TaskForceMarginPercent / 100.0);
    private double? _tfStrength;

    /// <summary>
    /// The home guard is whole fleets, so new warships delivered to it while it was weak stay there for good once it is strong (seen on the
    /// v5 galaxy: an Alliance HQ guard of strength 119 sat still for 300 days while the strike pile never reached LaunchStrength). When one
    /// guard fleet is at least LaunchStrength above HomeDefenceStrength, its newest armed ships are split off (one order) while the guard keeps
    /// HomeDefenceStrength; the split fleet is a strike fleet from the next review. A ship's share of the fleet strength is in proportion to its
    /// class strength. DEFAULT (lead 2026-10-16, a bug fix of the home-defence rule above).
    /// </summary>
    private void SplitHomeSurplus(Fleet guard, double strength)
    {
        if (strength - _p.HomeDefenceStrength < _p.LaunchStrength || guard.Ships.Count < 2) return;
        var cls = guard.Ships.Select(id => (Id: id, S: ClassStrength(_h.Economy.Find(id)!.Class))).ToList();
        double total = cls.Sum(c => c.S);
        if (total <= 0) return;
        var off = new List<int>(); double left = strength;
        foreach (var (id, cs) in cls.Where(c => c.S > 0).OrderByDescending(c => c.Id))
        {
            double share = strength * cs / total;
            if (left - share < _p.HomeDefenceStrength) continue;
            off.Add(id); left -= share;
        }
        if (off.Count == 0 || off.Count == guard.Ships.Count || strength - left < _p.LaunchStrength) return;
        off.Sort();
        long seq = _h.Fleets.SubmitSplitFleet(_side, guard.Id, off);
        Note(seq, "Split", $"{off.Count} ships off the home guard {guard.Id} at {guard.At}");
    }

    private void Merge(Fleet from, Fleet into)
    {
        long seq = _h.Fleets.SubmitMergeFleets(_side, from.Id, into.Id);
        Note(seq, "Merge", $"fleet {from.Id} into {into.Id}");
    }

    private Pile MakePile(Fleet leader, List<Fleet> members)
    {
        var p = new Pile { Leader = leader, Members = members };
        double hullSum = 0; int ships = 0;
        foreach (var f in members)
        {
            p.Strength += Str(f);
            p.Aboard += _h.Troops.RegimentsAboard(f).Count;
            p.FreeSlots += _h.Fleets.FreeTroopSlots(f);
            p.Berths += _h.Fleets.FreeBerths(f);
            foreach (var id in f.Ships)
            {
                var o = _h.Economy.Find(id)!; var c = _h.Fleets.ClassOf(o)!;
                hullSum += c.Hull > 0 ? Math.Min(100.0, 100.0 * o.Hull / c.Hull) : 100; ships++;
            }
        }
        if (ships > 0) p.HullPct = hullSum / ships;
        return p;
    }

    private int Need(int defenders) =>
        defenders == 0 ? 1 : Math.Max(_p.MinLaunchRegiments, (defenders * _p.RegimentsPerDefenderPercent + 99) / 100);

    private bool HasShipyard(string system) => YardSystems(YardFamily.Shipyard).Contains(system);

    // A strike fleet at a system the side holds: take on regiments and fighters, then leave, wait, or go to the rally system.
    private void DecideOwn(Pile p)
    {
        var f = p.Leader; string at = f.At!;
        var rally = Rally() ?? Capital();

        // enemy fleets on this very system: fight on unless clearly outmatched (m4-retreat-damage-repair: a fleet may leave)
        if (Threats().TryGetValue(at, out var here) && here.Any(x => x.At == at))
        {
            var friends = _h.Fleets.FleetsAt(at).Where(x => x.Side == _side);
            var enemiesHere = here.Where(x => x.At == at).ToList();
            if (Duel(friends, enemiesHere) < _p.RetreatOddsPercent)
            {
                var safe = YardSystems(YardFamily.Shipyard).Where(sy => sy != at && !Threats().ContainsKey(sy)).OrderBy(sy => Ticks(at, sy, 100)).ThenBy(sy => sy, StringComparer.Ordinal).FirstOrDefault() ?? rally;
                if (safe != null && safe != at) { foreach (var m in p.Members) Move(m, safe); return; }
            }
            return;
        }

        // regiments waiting here above the garrison, and fighters based here
        int aboard = p.Aboard;
        var spare = SpareRegimentIds(at);
        int take = Math.Min(spare.Count, p.FreeSlots);
        if (take > 0)
        {
            long seq = _h.Troops.SubmitLoad(_side, f.Id, spare.Take(take));
            Note(seq, "LoadRegiments", $"{take} onto fleet {f.Id} at {at}");
            aboard += take;
        }
        int bt = p.Berths;
        if (bt > 0)
        {
            var sq = _h.Fleets.BasedSquadrons(at, _side).Select(o => o.Id).Take(bt).ToList();
            if (sq.Count > 0)
            {
                long seq = _h.Fleets.SubmitLoadSquadrons(_side, f.Id, sq);
                Note(seq, "LoadSquadrons", $"{sq.Count} onto fleet {f.Id} at {at}");
            }
        }

        bool damaged = p.HullPct < _p.RepairBelowPercent;
        // defence first: a threatened system we can relieve beats repairs
        var relief = PickRelief(p, at);
        if (relief != null && Move(f, relief)) return;
        if (damaged)
        {
            if (!HasShipyard(at) && rally != null) Move(f, rally); // repair happens at a shipyard
            return;
        }
        bool strong = p.Strength >= _p.LaunchStrength;
        if (strong)
        {
            // short of troops: fetch spare regiments waiting on another held system (the rally system may have moved since they were ordered)
            bool Fetch()
            {
                if (p.FreeSlots - take <= 0) return false;
                string? pick = null; long bestPick = 0; int speed = _h.Fleets.SpeedOf(f);
                foreach (var hs in HeldSystems())
                {
                    if (hs == at || Threats().ContainsKey(hs)) continue;
                    int n = SpareRegimentsAt(hs); if (n == 0) continue;
                    int tk = Ticks(at, hs, speed); if (tk == int.MaxValue) continue;
                    long sc = Math.Min(n, p.FreeSlots) * 1000L / (tk + 20); if (sc > bestPick) { bestPick = sc; pick = hs; }
                }
                return pick != null && Move(f, pick);
            }
            // the system that wins the game, believed (nearly) undefended and within reach: load the regiments it needs first, then go for it
            if (aboard < WinTroopsWanted(p, at) && Fetch()) return;
            bool limited = _p.MaxOffensives != int.MaxValue;
            var t = !limited || Offensives() < _p.MaxOffensives ? PickTarget(p, aboard, at) : null;
            if (t != null && Move(f, t)) { if (limited) _offensives++; return; }
            if (aboard < _p.MinLaunchRegiments && Fetch()) return;
        }
        if (rally != null && at != rally) Move(f, rally); // gather (and reload) at the rally system
    }

    /// <summary>
    /// Regiments the pile should carry before it strikes the system that wins the game, or 0 when no such strike is on: the win target is
    /// known and not held, the pile is at least WinStrikeStrength, it can reach it, an assault is allowed there as far as the side knows, its
    /// fleets there (as believed) are beaten at the attack odds, the pile did not bounce off it lately, the regiments believed there are at
    /// most WinRushDefenders, and the regiments needed fit aboard. (The hunt for fleets that only retreat must not keep an empty strike pile
    /// off an open capital: m6 chase fix.)
    /// </summary>
    private int WinTroopsWanted(Pile p, string from)
    {
        string? win = WinTarget();
        if (win == null || win == from || Held(win) || p.Strength < _p.WinStrikeStrength || BouncedOff(win)) return 0;
        int speed = _h.Fleets.SpeedOf(p.Leader);
        if (speed <= 0 || Ticks(from, win, speed) == int.MaxValue) return 0;
        if (AssaultForbiddenSeen(win) || !SlotsSeen(win).Populated) return 0;
        var enemies = TargetFleetsAt(win);
        if (enemies.Count > 0 && Duel(p.Members, enemies) < TargetOdds(win)) return 0;
        int d = DefendersAt(win);
        if (d > _p.WinRushDefenders) return 0;
        int need = Need(_h.Bombardment.Power(p.Leader) > 0 ? d / 2 : d);
        return need <= p.Aboard + p.FreeSlots ? need : 0;
    }

    /// <summary>A held system with enemy fleets on it or on the way that the pile (with the fleets already there) beats; the critical system first, then the nearest.</summary>
    private string? PickRelief(Pile p, string from)
    {
        int speed = _h.Fleets.SpeedOf(p.Leader);
        if (speed <= 0) return null;
        string? best = null; long bestScore = long.MinValue;
        string? crit = CriticalSystem();
        foreach (var kv in Threats().OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string s = kv.Key;
            if (s == from || BouncedOff(s)) continue;
            int ticks = Ticks(from, s, speed);
            if (ticks == int.MaxValue) continue;
            var friends = _h.Fleets.FleetsAt(s).Where(x => x.Side == _side).Concat(p.Members);
            if (Duel(friends, kv.Value) < OddsAt(s, _p.DefendOddsPercent)) continue;
            long score = 1_000_000L / (ticks + 10) * (s == crit ? 3 : 1);
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    /// <summary>Days RecentBombardments looks back (the AI's own tuning since the bombardment AI was written, not a rule of the game).</summary>
    private const int RecentBombardmentDays = 30;

    private int RecentBombardments(string system) => NotesSince(AiNoteKind.Bombardment, Today - RecentBombardmentDays).Count(b => b.System == system);

    // A fleet at a system it does not hold: fight on, bombard, assault, or go elsewhere.
    private void DecideAtTarget(Fleet f)
    {
        string at = f.At!;
        var rally = Rally() ?? Capital();
        var mineHere = _h.Fleets.FleetsAt(at).Where(x => x.Side == _side).ToList();
        var enemies = EnemyFleetsAt(at);
        if (enemies.Count > 0)
        {
            if (Duel(mineHere, enemies) < _p.RetreatOddsPercent && rally != null) Move(f, rally); // too strong for us: leave
            return; // else the battle takes its course
        }
        int aboard = _h.Troops.RegimentsAboard(f).Count;
        int d = DefendersAt(at);
        bool canShell = _h.Bombardment.Power(f) > 0;
        if (AssaultForbiddenSeen(at)) { Leave(f); return; } // rule: m3-planetary-defenses (assault forbidden, 2+ shield generators)
        if (_view != null && !SlotsSeen(at).Populated) { Leave(f); return; } // fog: the target was picked unseen; an empty world cannot be assaulted
        // rule: m3-assault §Order and preconditions: a fleet has one order at a time, so shell one day and land the next.
        bool win = at == WinTarget();
        // fog: the shelling cannot be seen to work, so even the winning system is shelled a bounded number of times
        int maxShell = _view != null && win ? _p.FogWinBombardmentsPer30Days : _p.MaxBombardmentsPer30Days;
        // NoBlindStrikes (QA-22): a Military strike with nothing to hit wastes every hit and still rolls collateral against the bomber (B7 §5.3)
        bool targetSeen = !_p.NoBlindStrikes || MilitaryTargetSeen(at);
        if (d > 0 && _h.Bombardment.Power(f) > 0 && targetSeen && ((win && _view == null) || RecentBombardments(at) < maxShell))
        {
            long seq = _h.Bombardment.SubmitBombard(_side, f.Id, at, BombardCategory.Military);
            Note(seq, "Bombard", $"fleet {f.Id} shells {at}");
            return;
        }
        // rule: ai tuning (m6 stall fix). PickTarget chose this system on Need(d / 2) (the shelling halves the defenders); the landing here asked for
        // Need(d) again. With fog the believed d never falls without own kills (the garrison seen is a guess or stale; an empty or small garrison
        // gives none), so the fleet shelled four times, left, and came back again and again, never landing. Shelled and armed: ask what the pick asked.
        // NoBlindStrikes: no target seen to shell, so land on what PickTarget asked, as after a shelling
        bool shelled = _p.ShelledLandingNeed && canShell && (RecentBombardments(at) > 0 || !targetSeen);
        if (aboard >= 1 && aboard >= Need(shelled ? d / 2 : d))
        {
            long seq = _h.Assault.SubmitAssault(_side, f.Id, at);
            Note(seq, "Assault", $"fleet {f.Id} lands on {at}");
            return;
        }
        Leave(f);
    }

    private void Leave(Fleet f)
    {
        var rally = Rally() ?? Capital();
        var p = MakePile(f, new List<Fleet> { f });
        var t = PickRelief(p, f.At!) ?? PickTarget(p, p.Aboard, f.At!);
        if (t != null && Move(f, t)) return;
        if (rally != null) Move(f, rally);
    }

    /// <summary>FinishWhenAheadRatio: the side holds at least that many times the systems it knows the enemy to hold (cached per day).</summary>
    private bool Finishing()
    {
        if (_p.FinishWhenAheadRatio <= 0) return false;
        if (_finishing is bool b) return b;
        int enemy = EnemySystems().Count();
        return (bool)(_finishing = enemy > 0 && HeldSystems().Count() >= _p.FinishWhenAheadRatio * enemy);
    }
    private bool? _finishing;

    private int[] Hops()
    {
        if (_hops != null) return _hops;
        var g = _h.Data.Galaxy; var dist = new int[g.Systems.Count]; Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        foreach (var s in HeldSystems()) if (g.TryFindSystem(s, out var i)) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (var n in g.NearOf(c)) if (dist[n] == int.MaxValue) { dist[n] = dist[c] + 1; q.Enqueue(n); }
        }
        return _hops = dist;
    }

    /// <summary>
    /// Best system to send a pile to: an enemy fleet it beats (fleets first: they are what makes the other targets safe), an enemy or neutral
    /// system near the border with a weak defence (lost systems score double), or the system that wins the game when the pile is strong enough;
    /// with fog and no enemy system on the picture (BlindHunt, QA-19), also a system the side has not looked at for long.
    /// </summary>
    private string? PickTarget(Pile pile, int aboard, string from)
    {
        var f = pile.Leader; double strength = pile.Strength;
        var g = _h.Data.Galaxy; var hops = Hops();
        int speed = _h.Fleets.SpeedOf(f);
        if (speed <= 0) return null;
        bool canBombard = _h.Bombardment.Power(f) > 0;
        string? win = WinTarget();
        var lost = LostSystems();
        var scored = new List<(string Sys, long Score)>();
        bool search = HqSearchOn && strength >= _p.WinStrikeStrength;
        bool finishing = Finishing();
        bool blind = BlindOn();
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (s == from || Held(s) || BouncedOff(s) || Destroyed(s)) continue;
            bool isWin = s == win && strength >= _p.WinStrikeStrength;
            if (_tfClaims != null && !isWin && _tfClaims.Contains(s)) continue; // task forces: another pile of the group took it on
            // fog, blind hunt (QA-19): the picture shows no enemy system, so a system not looked at for long is worth a visit
            int blindValue = blind ? BlindTargetValue(s, hops[sys.Index]) : 0;
            bool isBlind = blindValue > 0;
            // fog: systems the picture shows enemy-held reach farther; the Empire's HQ candidates (belief map) from any distance
            bool seenEnemy = _view != null && IsEnemyHeld(s);
            int reach = seenEnemy ? Math.Max(_p.TargetHops, _p.FogEnemyTargetHops) : _p.TargetHops;
            int belief = search && seenEnemy ? Belief(s) : 0;
            bool isSearch = belief > 0;
            if (hops[sys.Index] > reach + 1 && !isWin && !isSearch && !isBlind) continue;
            int ticks = _view != null ? TicksCached(from, s, speed) : Ticks(from, s, speed);
            if (ticks == int.MaxValue) continue;
            var enemies = TargetFleetsAt(s);
            double e = 0;
            if (enemies.Count > 0)
            {
                if (Duel(pile.Members, enemies) < TargetOdds(s)) continue;
                e = enemies.Sum(Str);
            }
            long score = 0;
            // the fleet itself: hunt it when the system is near the border and the pile is armed enough, unless it only runs from us (it
            // retreated from this side here or next door lately: the chase would go on with no losses on either side)
            if (e > 0 && ticks <= _p.HuntTicks && hops[sys.Index] <= _p.TargetHops && !EnemyRunsNear(s)) score = 3000L * (long)(e * 100 + 20) / (ticks + 20);
            // the system: needs regiments
            if (hops[sys.Index] <= reach || isWin || isSearch || isBlind)
            {
                var slots = SlotsSeen(s);
                bool ok = slots.Populated && !AssaultForbiddenSeen(s) && aboard >= 1;
                if (ok)
                {
                    int d = DefendersAt(s);
                    ok = aboard >= Need(canBombard ? d / 2 : d);
                    if (ok)
                    {
                        long value = 10 + slots.Energy + 2 * slots.Raw;
                        if (IsEnemyHeld(s)) value += 10 + 2 * FacilitiesAt(s);
                        if (finishing && IsEnemyHeld(s)) value = value * _p.FinishValuePercent / 100;
                        if (lost.Contains(s)) value *= 2;
                        if (s == KnownEnemyHq) value += 200;
                        if (isWin) value += 400;
                        if (isSearch) value += _p.SearchValue * belief / Math.Max(1, BeliefMax);
                        value += blindValue;
                        score = Math.Max(score, value * 1000 / (ticks + 20));
                    }
                }
            }
            if (score > 0) scored.Add((s, score));
        }
        if (scored.Count == 0) return null;
        string pick;
        // difficulty: pick noise (ai/09 §6), a random one of the three best targets
        if (_p.PickNoisePercent > 0 && _rng.NextInt(1, 100) <= _p.PickNoisePercent)
        {
            var top3 = scored.OrderByDescending(t => t.Score).ThenBy(t => t.Sys, StringComparer.Ordinal).Take(3).ToList();
            pick = top3[_rng.NextInt(0, top3.Count - 1)].Sys;
        }
        else
        {
            long best = scored.Max(t => t.Score);
            // tie-break (private generator): any target within 5% of the best
            var top = scored.Where(t => t.Score * 100 >= best * 95).OrderBy(t => t.Sys, StringComparer.Ordinal).ToList();
            pick = top[_rng.NextInt(0, top.Count - 1)].Sys;
        }
        _tfClaims?.Add(pick);
        return pick;
    }
}
