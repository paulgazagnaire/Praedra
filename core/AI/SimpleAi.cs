using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Setup;

namespace Rebellion.Core.AI;

/// <summary>One order the AI queued today (for logs and tests).</summary>
public sealed record SubmittedOrder(long Sequence, string Kind, string Detail);

/// <summary>
/// A simple computer opponent: a decent economy bot with a thin military (Classic-style). Call <see cref="PlanDay"/> once per day BEFORE
/// <c>Game.Step()</c>; it queues orders through the modules' Submit methods, exactly the orders a player can give. It never changes
/// state itself, and it keeps no memory between days: everything is re-derived from the game state, so a save needs nothing extra.
///
/// Randomness: the AI never draws from the game's generator (that would change the game's random stream). Its few tie-breaks use a
/// private <see cref="GameRng"/> seeded from (game day, side), so the same game state gives the same orders.
///
/// VISIBILITY: with <see cref="SimpleAiParams.UseFog"/> on, every look at the other side goes through the side's
/// <see cref="Rebellion.Core.Fog.KnowledgeView"/> (m5-fog-and-hq §N8, the fairness contract): enemy fleets, garrisons, holders, facilities and slot
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
        _capitalDone = _rallyDone = false; _hops = null; _threats = null; _lost = null; _lostBattles = null; _runners = null; _bounced = null;
        int day = Today, phase = (int)_side;
        _rng = new GameRng((ulong)day * 2 + (ulong)_side + 0x5A1D, 0xA1);
        _view = _p.UseFog ? _h.Fog.ViewFor(_side) : null;
        _seenForces.Clear(); ClearFogScratch();
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

    private Rebellion.Core.Fog.KnowledgeView? _view;
    private readonly Dictionary<string, List<Force>> _seenForces = new(StringComparer.Ordinal); // lookup only

    /// <summary>
    /// One enemy fleet as the AI weighs it: its capital ships as combat units. Without fog: the real fleet (FleetId set, hull and damage read).
    /// With fog: a fleet of the side's picture (class counts at full hull, FleetId null). At = the system it stands at, null on a lane.
    /// </summary>
    private sealed record Force(int? FleetId, string? At, List<Combatant> Units);

    private Force ForceOf(Fleet f) => new(f.Id, f.At, ShipUnits(f));

    private Force ForceOf(string at, Rebellion.Core.Fog.FleetPicture p)
    {
        var units = new List<Combatant>();
        foreach (var (cls, n) in p.Ships) // ordinal class order
            if (_h.Fleets.ShipClasses.TryGetValue(cls, out var c)) for (int i = 0; i < n; i++) units.Add(_h.Fleets.Strength.Unit(c));
        return new Force(null, at, units);
    }

    /// <summary>Enemy fleets standing at a system. No fog: sees all. Fog: group E of the side's picture, unless the look is only Charted (too old).</summary>
    private List<Force> EnemyFleetsAt(string system)
    {
        if (_view == null) return _h.Fleets.FleetsAt(system).Where(f => f.Side != _side).Select(ForceOf).ToList();
        if (_seenForces.TryGetValue(system, out var cached)) return cached;
        var list = new List<Force>();
        var pic = _view.Picture(system);
        if (pic?.Fleets is { } e && _view.Level(system) is Rebellion.Core.Fog.KnowledgeLevel.Held or Rebellion.Core.Fog.KnowledgeLevel.Observed)
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
    private IEnumerable<Rebellion.Core.Missions.MissionGroup> OwnMissions() => _h.Missions.Missions.Where(m => m.Side == _side);

    /// <summary>
    /// Enemy characters the side knows to stand at a system, by character id. No fog: every free enemy character located at a system (the
    /// truth). Fog: from the side's own event feed, only at systems it sees today (<see cref="FogEnemyCharacters"/>).
    /// </summary>
    private List<(int Character, string System)> EnemyCharactersSeen()
    {
        if (_view != null) return FogEnemyCharacters();
        var list = new List<(int, string)>();
        foreach (var c in _h.Characters.InPlay)
            if (c.Side == Enemy && c.Status == Rebellion.Core.Characters.CharacterStatus.Free && c.Transit == null && _h.Characters.LocationOf(c) is { } at)
                list.Add((c.Id, at));
        return list;
    }

    // ================================================================== helpers

    private bool Held(string s) => _h.Fleets.OwnerOf(s) == _side;
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
        if (_side == Side.Empire && Held(Rebellion.Core.Control.ControlModule.CapitalSystem)) return _capital = Rebellion.Core.Control.ControlModule.CapitalSystem;
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
                (FacilityKind.TrainingFacility, train, _p.DesiredTrainingFacilities),
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

    /// <summary>0 when the system is two or more lanes from every enemy system, 2 when it is next to one (a yard there is a target).</summary>
    private int BorderDanger(string system)
    {
        var g = _h.Data.Galaxy;
        if (!g.TryFindSystem(system, out var i)) return 0;
        foreach (var n in g.NeighboursOf(i))
            if (IsEnemyHeld(g.Systems[n.System].Name)) return 2;
        return 0;
    }

    // ================================================================== production

    private void PlanProduction()
    {
        if (_view != null) PlanScoutBuild();
        PlanGarrison();
        PlanInvasionRegiments();
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
                d[pr.Destination] = d.GetValueOrDefault(pr.Destination) + pr.Units.Count;
        return d;
    }

    // rule: m2-uprisings-and-garrison §Required garrison N: keep N regiments on every system where the support is low.
    private void PlanGarrison()
    {
        string cls = _p.GarrisonRegiment(_side);
        if (!Available(cls)) return;
        var pending = PendingRegiments();
        foreach (var s in HeldSystems())
        {
            int need = _h.Support.RequiredGarrison(s);
            // the system whose loss ends the game keeps a standing garrison whatever the support says
            if (s == CriticalSystem()) need = Math.Max(need, _p.CriticalGarrison);
            if (need == 0) continue;
            int deficit = need - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s);
            if (deficit <= 0) continue;
            int count = Math.Min(deficit, _p.GarrisonBatch);
            if (Budget < count * Maint(cls)) continue;
            var yard = TrainingYardNear(s);
            if (yard == null) return;
            OrderRegiments(yard, cls, count, s);
        }
    }

    private void PlanInvasionRegiments()
    {
        var rally = Rally();
        string cls = _p.InvasionRegiment(_side);
        if (rally == null || !Available(cls)) return;
        int have = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == _side && o.AboardShip != 0) + HeldSystems().Sum(SpareRegimentsAt);
        have += PendingRegiments().GetValueOrDefault(rally);
        if (have >= _p.InvasionRegimentTarget) return;
        int count = Math.Min(_p.InvasionRegimentTarget - have, _p.GarrisonBatch);
        if (Budget < count * Maint(cls)) return;
        var yard = TrainingYardNear(rally);
        if (yard != null) OrderRegiments(yard, cls, count, rally);
    }

    /// <summary>Landed regiments of the side on a system above what its garrison needs.</summary>
    private List<int> SpareRegimentIds(string system)
    {
        var ids = _h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == _side && o.System == system && o.AboardShip == 0)
            .Select(o => o.Id).ToList();
        int keep = Held(system) ? Math.Max(_h.Support.RequiredGarrison(system), system == CriticalSystem() ? _p.CriticalGarrison : 0) : 0;
        return ids.Count > keep ? ids.Skip(keep).ToList() : new List<int>();
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
            while (count > 1 && Budget < count * Maint(cls)) count--;
            if (Budget < count * Maint(cls)) continue;
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
        var ev = Events; int since = Today - _p.LossMemoryDays;
        for (int i = ev.Count - 1; i >= 0 && ev[i].Day >= since; i--)
        {
            if (ev[i] is not BattleEnded b || d.ContainsKey(b.Result.System)) continue;
            var own = _side == Side.Alliance ? b.Result.Alliance : b.Result.Empire;
            if (own == BattleOutcome.Annihilated || (_p.RetreatCountsAsLoss && own == BattleOutcome.Retreated && OwnArmedIn(ev, i))) d[b.Result.System] = ev[i].Day;
        }
        return _lostBattles = d;
    }

    /// <summary>
    /// Did this side bring an armed capital ship to the battle that ended at ev[end]? Read from the battle's BattleStarted (same day, same system)
    /// and the side's OWN units only. An unarmed ship (a scout) always retreats from armed enemies (m4-battle-classic morale), which says nothing
    /// about the odds of a strike there. True when the start is not in the feed.
    /// </summary>
    private bool OwnArmedIn(IReadOnlyList<GameEvent> ev, int end)
    {
        var b = (BattleEnded)ev[end];
        for (int j = end - 1; j >= 0 && ev[j].Day == b.Day; j--)
        {
            if (ev[j] is not BattleStarted bs || bs.System != b.Result.System) continue;
            foreach (var id in (_side == Side.Alliance ? bs.AllianceUnits : bs.EmpireUnits).Items)
                if (_h.Economy.Find(id) is { } o && o.Category == ObjectCategory.CapitalShip && _h.Fleets.ClassOf(o) is { } c && CombatStrength.IsArmed(c)) return true;
            return false;
        }
        return true;
    }

    /// <summary>The odds (percent) asked before fighting at a system: the base, times <see cref="SimpleAiParams.LossOddsFactor"/> where the side was annihilated (or bounced, see LostBattles) lately.</summary>
    private double OddsAt(string system, int basePercent) => LostBattles().ContainsKey(system) ? basePercent * _p.LossOddsFactor : basePercent;

    private Dictionary<string, int>? _runners, _bounced;

    /// <summary>
    /// Battles of the last RunnerMemoryDays where the other side retreated (rule: m4-retreat-damage-repair §1, the morale retreat of
    /// m4-battle-classic) and this side held the field, counted by system. Read from the side's own event feed (BattleEnded goes to both
    /// sides, m5-fog-and-hq §N8), so the AI keeps no memory of its own: a fleet that only retreats is remembered for as long as the feed shows it.
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
        var ev = Events; int since = Today - _p.RunnerMemoryDays;
        for (int i = ev.Count - 1; i >= 0 && ev[i].Day >= since; i--)
        {
            if (ev[i] is not BattleEnded b) continue;
            var own = _side == Side.Alliance ? b.Result.Alliance : b.Result.Empire;
            var other = _side == Side.Alliance ? b.Result.Empire : b.Result.Alliance;
            string sys = b.Result.System;
            if (other == BattleOutcome.Retreated && own is not (BattleOutcome.Retreated or BattleOutcome.Annihilated)) _runners[sys] = _runners.GetValueOrDefault(sys) + 1;
            if (own == BattleOutcome.Retreated && OwnArmedIn(ev, i)) _bounced[sys] = _bounced.GetValueOrDefault(sys) + 1;
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
            foreach (var nb in g.NeighboursOf(i)) n += r.GetValueOrDefault(g.Systems[nb.System].Name);
        return n >= _p.RunnerRetreats;
    }

    /// <summary>Systems the side lost to the enemy lately (retake targets).</summary>
    private HashSet<string> LostSystems()
    {
        if (_lost != null) return _lost;
        var set = new HashSet<string>(StringComparer.Ordinal);
        var ev = Events; int since = Today - _p.RetakeDays;
        for (int i = ev.Count - 1; i >= 0 && ev[i].Day >= since; i--)
            if (ev[i] is Rebellion.Core.Control.SystemCaptured c && c.From == _side && c.To == Enemy) set.Add(c.System);
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

    private string? CriticalSystem() => _side == Side.Empire ? Rebellion.Core.Control.ControlModule.CapitalSystem : _h.HqSystem; // the system whose loss ends the game for the side
    /// <summary>
    /// The home guard stands at the system whose loss ends the game for the side, when the side holds it. New warships are only sent there
    /// when it has a shipyard (so the guard can be topped up); otherwise the guard is whatever fleet already stands there.
    /// </summary>
    private string? GuardSystem() { var c = CriticalSystem(); return c != null && Held(c) ? c : null; }
    // The Empire only strikes an HQ it has been told about (KnownEnemyHq); no peeking at the true HQ (economy-ai D4: the AI plays fair with fog of war).
    private string? WinTarget() => _side == Side.Empire ? KnownEnemyHq : Rebellion.Core.Control.ControlModule.CapitalSystem;

    private bool Pending(Fleet f) => _h.Bombardment.HasPending(f.Id) || _h.Assault.HasPending(f.Id);

    private bool Move(Fleet f, string dest)
    {
        if (f.At == null || dest == f.At || f.Transit != null) return false;
        int speed = _h.Fleets.SpeedOf(f);
        var path = _h.Fleets.FindPath(f.At, dest, speed);
        if (speed <= 0 || !path.Ok) return false;
        if (_p.AvoidBlockedPaths && PathBlockedAt(f, path) != null) return false; // stays: the way would end in a retreat
        long seq = _h.Fleets.SubmitMove(_side, f.Id, dest);
        Note(seq, "Move", $"fleet {f.Id} {f.At} -> {dest}");
        return true;
    }

    /// <summary>
    /// The first stop of the trip where it would end in a retreat, or null: a stop (the destination included) where this side's fleets retreated
    /// within RunnerMemoryDays (own event feed), or a stop on the way where enemy fleets the side sees now outmatch the fleets of the side at the
    /// start (RetreatOddsPercent). rule: m1-forced-stop-and-blockade (an armed enemy fleet stops a passing one; the order is cancelled), then the
    /// morale retreat of m4-battle-classic sends the fleet back: the same order again only repeats the bounce. The destination's fleets are
    /// weighed by the caller (PickTarget, PickRelief).
    /// </summary>
    private string? PathBlockedAt(Fleet f, Rebellion.Core.Travel.PathResult path)
    {
        var g = _h.Data.Galaxy;
        List<Fleet>? own = null;
        for (int k = 1; k < path.Systems.Count; k++)
        {
            string n = g.Systems[path.Systems[k]].Name;
            if (BouncedOff(n)) return n;
            if (k == path.Systems.Count - 1) break;
            var enemies = EnemyFleetsAt(n);
            if (enemies.Count == 0) continue;
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
        foreach (var g in idle.GroupBy(f => f.At!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = g.OrderBy(f => f.Id).ToList();
            if (!Held(g.Key))
            {
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
            }
            if (strike.Count == 0) continue;
            var leader = strike[0];
            for (int i = 1; i < strike.Count; i++) Merge(strike[i], leader);
            DecideOwn(MakePile(leader, strike));
        }
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
            var t = PickTarget(p, aboard, at);
            if (t != null && Move(f, t)) return;
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

    private int RecentBombardments(string system)
    {
        int n = 0, since = Today - 30;
        var ev = Events;
        for (int i = ev.Count - 1; i >= 0 && ev[i].Day >= since; i--)
            if (ev[i] is BombardmentResolved b && b.System == system) n++;
        return n;
    }

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
        if (AssaultForbiddenSeen(at)) { Leave(f); return; } // rule: m3-planetary-defenses (assault forbidden, 2+ shield generators)
        if (_view != null && !SlotsSeen(at).Populated) { Leave(f); return; } // fog: the target was picked unseen; an empty world cannot be assaulted
        // rule: m3-assault §Order and preconditions: a fleet has one order at a time, so shell one day and land the next.
        bool win = at == WinTarget();
        // fog: the shelling cannot be seen to work, so even the winning system is shelled a bounded number of times
        int maxShell = _view != null && win ? _p.FogWinBombardmentsPer30Days : _p.MaxBombardmentsPer30Days;
        if (d > 0 && _h.Bombardment.Power(f) > 0 && ((win && _view == null) || RecentBombardments(at) < maxShell))
        {
            long seq = _h.Bombardment.SubmitBombard(_side, f.Id, at, BombardCategory.Military);
            Note(seq, "Bombard", $"fleet {f.Id} shells {at}");
            return;
        }
        if (aboard >= 1 && aboard >= Need(d))
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

    private int[] Hops()
    {
        if (_hops != null) return _hops;
        var g = _h.Data.Galaxy; var dist = new int[g.Systems.Count]; Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        foreach (var s in HeldSystems()) if (g.TryFindSystem(s, out var i)) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (var n in g.NeighboursOf(c)) if (dist[n.System] == int.MaxValue) { dist[n.System] = dist[c] + 1; q.Enqueue(n.System); }
        }
        return _hops = dist;
    }

    /// <summary>
    /// Best system to send a pile to: an enemy fleet it beats (fleets first: they are what makes the other targets safe), an enemy or neutral
    /// system near the border with a weak defence (lost systems score double), or the system that wins the game when the pile is strong enough.
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
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (s == from || Held(s) || BouncedOff(s)) continue;
            bool isWin = s == win && strength >= _p.WinStrikeStrength;
            // fog: systems the picture shows enemy-held reach farther; the Empire's HQ candidates (belief map) from any distance
            bool seenEnemy = _view != null && IsEnemyHeld(s);
            int reach = seenEnemy ? Math.Max(_p.TargetHops, _p.FogEnemyTargetHops) : _p.TargetHops;
            int belief = search && seenEnemy ? Belief(s) : 0;
            bool isSearch = belief > 0;
            if (hops[sys.Index] > reach + 1 && !isWin && !isSearch) continue;
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
            if (hops[sys.Index] <= reach || isWin || isSearch)
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
                        if (lost.Contains(s)) value *= 2;
                        if (s == KnownEnemyHq) value += 200;
                        if (isWin) value += 400;
                        if (isSearch) value += _p.SearchValue * belief / Math.Max(1, BeliefMax);
                        score = Math.Max(score, value * 1000 / (ticks + 20));
                    }
                }
            }
            if (score > 0) scored.Add((s, score));
        }
        if (scored.Count == 0) return null;
        long best = scored.Max(t => t.Score);
        // tie-break (private generator): any target within 5% of the best
        var top = scored.Where(t => t.Score * 100 >= best * 95).OrderBy(t => t.Sys, StringComparer.Ordinal).ToList();
        return top[_rng.NextInt(0, top.Count - 1)].Sys;
    }
}
