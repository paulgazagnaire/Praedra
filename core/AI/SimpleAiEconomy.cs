using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Setup;

namespace Praedra.Core.AI;

/// <summary>The simple AI's economy, production and upkeep-crisis planning (moved unchanged from SimpleAi.cs).</summary>
public sealed partial class SimpleAi
{
    // ================================================================== economy

    private void PlanEconomy()
    {
        var eco = _h.Economy;
        _fortifyOrdered.Clear();
        TryFoundYard();
        foreach (var y in YardSystems(YardFamily.Construction))
        {
            if (!CanOrderAt(y, YardFamily.Construction)) continue;
            var choice = ChooseFacility(y);
            if (choice == null) continue;
            var (kind, dest) = choice.Value;
            if (!Affordable(kind)) continue;
            OrderFacility(y, kind, dest);
        }
    }

    /// <summary>
    /// FoundYardsBelow (AI trainer 2026-11-20; Paul: a losing side must "reproduce a military and fight until it's dying breath", smarter only):
    /// with fewer construction yards than that (finished and queued, a founding one included), the side founds one, one at a time, on its held,
    /// populated world farthest from the enemy it knows (then the most energy, then the name), where the rule allows it.
    /// rule: m2-construction §Founding a construction yard (ConstructionModule.CanFoundYardAt).
    /// </summary>
    private void TryFoundYard()
    {
        // Sibyl (Manage Production 0a, QA-116): only when the player's side has no construction yard, with her upkeep margin
        int below = _advisor ? _p.AdvisorFoundYardsBelow : _p.FoundYardsBelow;
        if (below <= 0 || CountYards(YardFamily.Construction) >= below) return;
        if (HeldSystems().Any(s => _h.Construction.ProjectAt(s, YardFamily.Construction) is { Founding: true })) return;
        string cls = FacilityKinds.ClassName(FacilityKind.ConstructionYard);
        if (!Available(cls) || Budget - Maint(cls) < (_advisor ? _p.AdvisorUpkeepMargin : 0)) return;
        var eco = _h.Economy;
        // the AI: the world farthest from the enemy it knows first; Sibyl reads only her own worlds (m6-advisor-sibyl §Manage Garrisons 9)
        string? site = HeldSystems().Where(s => eco.SlotsOf(s).Populated && !_h.Support.IsUprising(s) && eco.FreeEnergy(s) - _reservedEnergy.GetValueOrDefault(s) >= 1
                                                && _h.Construction.CanFoundYardAt(s))
            .OrderByDescending(s => _advisor ? 0 : HopsFromEnemy(s, FoundSafeHops)).ThenByDescending(s => Math.Min(eco.SlotsOf(s).Energy, FoundGoodEnergy))
                .ThenBy(s => FoundTie(s)).ThenByDescending(s => eco.SlotsOf(s).Energy).ThenBy(s => s, StringComparer.Ordinal).FirstOrDefault();
        if (site == null) return;
        long seq = _h.Construction.SubmitBuild(_side, site, YardFamily.Construction, cls, 1, site, byAdvisor: _advisor); // marked as hers: the screen splits refined use (RefinedUsePer30Days)
        Note(seq, "Build", $"{cls} founded at {site}");
        _plannedLoad += Maint(cls);
        _reservedEnergy[site] = _reservedEnergy.GetValueOrDefault(site) + 1;
    }

    private void OrderFacility(string yard, FacilityKind kind, string dest)
    {
        string cls = FacilityKinds.ClassName(kind);
        long seq = _h.Construction.SubmitBuild(_side, yard, YardFamily.Construction, cls, 1, dest, byAdvisor: _advisor);
        Note(seq, "Build", $"{cls} at {dest} from {yard}");
        _busy.Add((yard, YardFamily.Construction));
        _plannedLoad += Maint(cls);
        _reservedEnergy[dest] = _reservedEnergy.GetValueOrDefault(dest) + 1;
        if (kind == FacilityKind.Mine) _reservedRaw[dest] = _reservedRaw.GetValueOrDefault(dest) + 1;
    }

    /// <summary>
    /// The advisor's use of her side's idle construction yards (ordinal order): a training facility while the garrisons are short and every
    /// training facility is busy (Manage Garrisons 9), else with Manage Production a construction yard while the side has few for its worlds
    /// (1a), else a mine or refinery (1b). rule: m6-advisor-sibyl (Paul 2026-11-16: "Both", "aggressively develop infrastructure").
    /// </summary>
    private void PlanAdvisorYards(bool garrisons, bool production)
    {
        // rule: m6-advisor-sibyl §Manage Production 0a (QA-116, 2026-11-20): with no construction yard left, she founds one (m2-construction §Founding)
        if (production) TryFoundYard();
        foreach (var y in YardSystems(YardFamily.Construction))
        {
            if (!CanOrderAt(y, YardFamily.Construction)) continue;
            // rule: m6-advisor-sibyl §Manage Production 0 (QA-107): short of room, mines and refineries come back first
            if (production && Budget < _p.AdvisorRebuildRoom && ChooseFacility(y) is { } rebuild) { OrderFacility(y, rebuild.Item1, rebuild.Item2); continue; }
            // rule: m6-advisor-sibyl §Manage Production 1a (QA-108): once the side has a training facility, more construction yards come first,
            // so one yard is not spent on training facilities back to back; only with room to spare and a finished training facility (QA-109)
            bool yardsFirst = garrisons && Budget >= _p.AdvisorYardsFirstRoom && FinishedYards(YardFamily.Training) > 0;
            if (production && yardsFirst && TryAdvisorConstructionYard(y)) continue;
            if (garrisons && TryAdvisorYard(y, FacilityKind.TrainingFacility, AdvisorTrainingWanted(), _p.AdvisorUpkeepMargin)) continue;
            if (!production) continue;
            if (!yardsFirst && TryAdvisorConstructionYard(y)) continue;
            if (ChooseFacility(y) is { } choice) OrderFacility(y, choice.Item1, choice.Item2);
        }
    }

    private int FinishedYards(YardFamily fam) => _h.Economy.Objects.Count(o => o.Completed && IsYardClass(o, fam) && _h.Economy.OwnerOf(o) == _side);

    private bool TryAdvisorConstructionYard(string from) =>
        _h.Economy.Refined(_side) >= _p.ExtraYardRefinedReserve && TryAdvisorYard(from, FacilityKind.ConstructionYard, AdvisorConstructionWanted(), _p.AdvisorUpkeepMargin);

    private bool TryAdvisorYard(string from, FacilityKind kind, int want, int room)
    {
        string cls = FacilityKinds.ClassName(kind);
        // count today's orders too: the rule counts "finished and queued", and a second idle yard must not order a yard the first one already
        // covered (the Hand starts with two construction yards since 2026-11-20). rule: m6-advisor-sibyl §Manage Garrisons 9, §Manage Production 1a
        var fam = Yards.FamilyOf(kind);
        int planned = _reservedYards.Where(r => r.Key.Item2 == fam).Sum(r => r.Value);
        if (CountYards(fam) + planned >= want || !Available(cls) || Budget - Maint(cls) < Math.Max(0, room)) return false;
        var site = PickSite(from, kind);
        if (site == null) return false;
        OrderFacility(from, kind, site);
        return true;
    }

    /// <summary>Training facilities wanted: none unless the garrisons are short and none is idle. rule: m6-advisor-sibyl §Manage Garrisons 9.</summary>
    private int AdvisorTrainingWanted()
    {
        if (_garrisonShort <= 0 || YardSystems(YardFamily.Training).Any(t => CanOrderAt(t, YardFamily.Training))) return 0;
        int built = _h.Economy.Objects.Count(o => o.Completed && IsYardClass(o, YardFamily.Training) && _h.Economy.OwnerOf(o) == _side);
        int per = Math.Max(1, _p.AdvisorShortPerTrainingFacility);
        return Math.Min(_p.AdvisorMaxTrainingFacilities, built + (_garrisonShort + per - 1) / per);
    }

    /// <summary>Construction yards wanted: one plus one per few populated worlds. rule: m6-advisor-sibyl §Manage Production 1a.</summary>
    private int AdvisorConstructionWanted() =>
        Math.Min(_p.AdvisorMaxConstructionYards, 1 + HeldSystems().Count(s => _h.Economy.SlotsOf(s).Populated) / Math.Max(1, _p.AdvisorWorldsPerConstructionYard));

    private (FacilityKind, string)? ChooseFacility(string from)
    {
        var eco = _h.Economy;
        var order = new List<FacilityKind>();
        int refined = eco.Refined(_side);
        int ships = CountYards(YardFamily.Shipyard), train = CountYards(YardFamily.Training), cons = CountYards(YardFamily.Construction);
        // rule: ai tuning, SimpleAiParams.RoomFloor (AI trainer 2026-11-26, QA-127): short of upkeep room, mines and refineries come first
        if (LowRoom && PairChoice(from) is { } pair) return pair;
        // rule: m6-advisor-sibyl §Manage Production 1: the advisor's yards come from PlanAdvisorYards, so here only mines and refineries
        if (!_advisor && ships == 0) order.Add(ShipyardKind());
        if (!_advisor && train == 0) order.Add(FacilityKind.TrainingFacility);
        // rule: ai tuning, SimpleAiParams.FortifyWorlds (AI trainer 2026-11-26): the key worlds' shields and batteries come right after the first yards
        foreach (var kind in order)
            if (Available(FacilityKinds.ClassName(kind)) && Affordable(kind) && PickSite(from, kind) is { } first) return (kind, first);
        order.Clear();
        if (FortifyChoice(from) is { } fort) return fort;
        // rule: m2-construction §Build time: a yard adds 1 point per cycle, so throughput is the number of yards; refined that floats is turned into yards.
        if (!_advisor && refined >= _p.ExtraYardRefinedReserve)
        {
            // the construction yards build everything else: the first ones come before the other yards
            if (cons < _p.EarlyConstructionYards) order.Add(FacilityKind.ConstructionYard);
            var wants = new List<(FacilityKind Kind, int Have, int Want)>
            {
                (ShipyardKind(), ships, _p.DesiredShipyards),
                (FacilityKind.TrainingFacility, train, TrainingFacilitiesWanted()),
                (FacilityKind.ConstructionYard, cons, ConstructionYardsWanted()),
            };
            // GarrisonsFirst: with garrisons short, a training facility wanted comes first
            foreach (var w in wants.Where(w => w.Have < w.Want).OrderBy(w => _p.GarrisonsFirstHoldsNavy && _garrisonShort > 0 && w.Kind == FacilityKind.TrainingFacility ? 0 : 1)
                         .ThenBy(w => w.Have * 100 / w.Want).ThenBy(w => (int)w.Kind)) order.Add(w.Kind);
        }
        int mines = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Mine" && eco.OwnerOf(o) == _side);
        int refs = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Refinery" && eco.OwnerOf(o) == _side);
        // rule: m2-maintenance §Capacity (capacity = 50 x min(mines, refineries)): keep the two balanced.
        if (mines <= refs) { order.Add(FacilityKind.Mine); order.Add(FacilityKind.Refinery); }
        else { order.Add(FacilityKind.Refinery); order.Add(FacilityKind.Mine); }
        foreach (var kind in order)
        {
            if (!Available(FacilityKinds.ClassName(kind))) continue;
            if (!Affordable(kind)) continue;
            var site = PickSite(from, kind);
            if (site != null) return (kind, site);
        }
        return null;
    }

    /// <summary>
    /// Fortify (SimpleAiParams.FortifyWorlds, AI trainer 2026-11-26): the first shield or battery one of the side's key worlds still lacks, for the
    /// yard at <paramref name="from"/>; null when every key world has its share (finished, building, or ordered today). The capital first; the other
    /// key worlds only from refined that floats (ExtraYardRefinedReserve). rule: m3-assault check 4 (two shields forbid an assault), §1 (batteries).
    /// </summary>
    private (FacilityKind, string)? FortifyChoice(string from)
    {
        int worlds = _p.FortifyWorlds + ShellHolesOn; // ShellHoles: the bolt-holes come after the Hearth's world (FortifyTargets)
        if (_advisor || worlds <= 0) return null;
        var eco = _h.Economy;
        int n = 0;
        foreach (var w in FortifyTargets())
        {
            if (n++ >= worlds) break;
            if (n > 1 && eco.Refined(_side) < _p.ExtraYardRefinedReserve) break;
            if (_h.Support.IsUprising(w) || eco.FreeEnergy(w) - _reservedEnergy.GetValueOrDefault(w) < 1) continue;
            if (Ticks(from, w, UnratedSpeed) == int.MaxValue) continue;
            var (shields, batteries) = OwnDefencesAt(w);
            FacilityKind? kind = shields < _p.FortifyShields ? ShieldKind() : batteries < _p.FortifyBatteries ? FacilityKind.Lien150 : null;
            if (kind is not { } k || !Available(FacilityKinds.ClassName(k)) || !Affordable(k)) continue;
            _fortifyOrdered[(w, k == FacilityKind.Lien150)] = _fortifyOrdered.GetValueOrDefault((w, k == FacilityKind.Lien150)) + 1;
            return (k, w);
        }
        return null;
    }

    /// <summary>Fortify orders given today, by (system, battery): so two yards do not both fill the same gap. Lookup only.</summary>
    private readonly Dictionary<(string, bool), int> _fortifyOrdered = new();

    /// <summary>ShellHoles while it applies: the Collective with fog and SafeHearth, not the advisor; else 0.</summary>
    private int ShellHolesOn => _side == Side.Collective && _view != null && _p.SafeHearth && !_advisor ? Math.Max(0, _p.ShellHoles) : 0;

    /// <summary>Shell game: the side's complete shield generators at the system (m3-assault check 4 counts complete ones).</summary>
    private int CompleteShieldsAt(string system)
    {
        var def = _h.Data.Defenses;
        return _h.Economy.Objects.Count(o => o.System == system && o.Completed && o.Category == ObjectCategory.Facility && _h.Economy.OwnerOf(o) == _side
            && def.TryGetClass(o.Class, out var c) && c.Job == Praedra.Core.Defenses.DefenseJob.ShieldGenerator);
    }

    /// <summary>
    /// Shell game: the ShellHoles bolt-holes for a Hearth at hearth: held populated worlds with HqMinLanes lanes or more (as PlanHqHide), the
    /// most complete shields first (walls already built are kept), then the most lanes from the Hand (capped at 8), the least risk, ordinal name.
    /// </summary>
    private IEnumerable<string> BoltHoles(string hearth, List<string> held)
    {
        if (_boltHolesDay == Today && _boltHoles != null) return _boltHoles;
        _boltHolesDay = Today;
        return _boltHoles = BoltHolesNow(hearth, held);
    }
    private List<string>? _boltHoles; private int _boltHolesDay = -1; // the day's bolt-holes (lookup only; the same inputs all day)

    private List<string> BoltHolesNow(string hearth, List<string> held)
    {
        var g = _h.Data.Galaxy; var room = HopsFromEnemy(); var risk = Risk();
        return held.Where(s => s != hearth && g.TryFindSystem(s, out var i) && g.NearOf(i).Count >= _p.HqMinLanes)
            .Select(s => (s, sh: Math.Min(CompleteShieldsAt(s), _p.FortifyShields), hops: g.TryFindSystem(s, out var i) ? Math.Min(room[i], 8) : 0, r: risk.GetValueOrDefault(s)))
            .OrderByDescending(t => t.sh).ThenByDescending(t => t.hops).ThenBy(t => t.r).ThenBy(t => t.s, StringComparer.Ordinal)
            .Take(ShellHolesOn).Select(t => t.s).ToList();
    }

    /// <summary>The better shield generator the side can build: Escrow II once researched, else Escrow I.</summary>
    private FacilityKind ShieldKind() => Available(FacilityKinds.ClassName(FacilityKind.EscrowII)) ? FacilityKind.EscrowII : FacilityKind.EscrowI;

    /// <summary>The side's shield generators and batteries at the system: finished, building (unfinished objects there), and ordered today.</summary>
    private (int Shields, int Batteries) OwnDefencesAt(string system)
    {
        var def = _h.Data.Defenses;
        int sh = _fortifyOrdered.GetValueOrDefault((system, false)), bat = _fortifyOrdered.GetValueOrDefault((system, true));
        foreach (var o in _h.Economy.Objects)
        {
            if (o.System != system || o.Category != ObjectCategory.Facility || !def.TryGetClass(o.Class, out var c) || _h.Economy.OwnerOf(o) != _side) continue;
            if (c.Job == Praedra.Core.Defenses.DefenseJob.ShieldGenerator) sh++;
            else if (c.Job == Praedra.Core.Defenses.DefenseJob.Battery) bat++;
        }
        return (sh, bat);
    }

    /// <summary>
    /// The side's key worlds in Fortify order: the capital (the Hand's Charter or its biggest world; the Collective's Hearth world with fog, else
    /// its biggest world), then the held worlds with the most yards, then the most mines (ties: ordinal name). Populated held worlds only.
    /// </summary>
    private List<string> FortifyTargets()
    {
        var eco = _h.Economy;
        var held = HeldSystems().Where(s => eco.SlotsOf(s).Populated).ToList();
        var list = new List<string>();
        string? cap = _side == Side.Collective && _view?.KnownHq?.System is { } hq && held.Contains(hq) ? hq : Capital();
        if (cap != null && held.Contains(cap)) list.Add(cap);
        if (ShellHolesOn > 0 && cap != null) list.AddRange(BoltHoles(cap, held));
        int Count(string s, Func<EconObject, bool> f) => eco.Objects.Count(o => o.System == s && o.Category == ObjectCategory.Facility && f(o));
        list.AddRange(held.Where(s => !list.Contains(s))
            .Select(s => (s, yards: Count(s, o => FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)), mines: Count(s, o => o.Class == "Mine")))
            .Where(t => t.yards + t.mines > 0)
            .OrderByDescending(t => t.yards).ThenByDescending(t => t.mines).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s));
        return list;
    }

    /// <summary>
    /// Room for a facility: its upkeep fits the budget; with YardsFromMargin (the AI, not Sibyl) a construction yard may also use the upkeep
    /// margin, so a side at its room cap can still build what grows its room. rule: ai tuning, SimpleAiParams.YardsFromMargin (AI trainer 2026-11-26).
    /// </summary>
    private bool Affordable(FacilityKind kind)
    {
        int m = Maint(FacilityKinds.ClassName(kind));
        if (m <= 0 || Budget >= m) return true;
        return !_advisor && _p.YardsFromMargin && !LowRoom && kind == FacilityKind.ConstructionYard && Budget + _p.MaintenanceMargin >= m;
    }

    /// <summary>
    /// RoomFloor (AI trainer 2026-11-26, QA-127: the Medium Hand ran out of upkeep room by day 100-200 with refined unspent and free mine sites,
    /// its mine and refinery pairs bombarded down and never rebuilt): the side's free upkeep room, less what it ordered today, is under the floor.
    /// Not the advisor (Sibyl has her own rule, m6-advisor-sibyl §Manage Production 0).
    /// </summary>
    private bool LowRoom => !_advisor && _p.RoomFloor > 0 && _h.Economy.Free(_side) - _plannedLoad < _p.RoomFloor;

    /// <summary>RoomFloor: a mine or a refinery, the one the side has fewer of first (capacity = 50 x min(mines, refineries), m2-maintenance §Capacity).</summary>
    private (FacilityKind, string)? PairChoice(string from)
    {
        var eco = _h.Economy;
        int mines = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Mine" && eco.OwnerOf(o) == _side);
        int refs = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.Class == "Refinery" && eco.OwnerOf(o) == _side);
        var order = mines <= refs ? new[] { FacilityKind.Mine, FacilityKind.Refinery } : new[] { FacilityKind.Refinery, FacilityKind.Mine };
        foreach (var kind in order)
            if (Available(FacilityKinds.ClassName(kind)) && Affordable(kind) && PickSite(from, kind) is { } site) return (kind, site);
        return null;
    }

    /// <summary>Construction yards wanted: DesiredConstructionYards, or one per WorldsPerConstructionYard populated held worlds plus one, capped. rule: ai tuning (AI trainer 2026-11-26).</summary>
    private int ConstructionYardsWanted()
    {
        if (_p.WorldsPerConstructionYard <= 0) return _p.DesiredConstructionYards;
        int worlds = HeldSystems().Count(s => _h.Economy.SlotsOf(s).Populated);
        return Math.Max(_p.DesiredConstructionYards, Math.Min(_p.MaxConstructionYards, 1 + worlds / _p.WorldsPerConstructionYard));
    }

    /// <summary>The shipyard the AI orders: an Advanced Shipyard once researched with AdvancedShipyards on (half the cycle, m2-construction §Families), else an Orbital Shipyard.</summary>
    private FacilityKind ShipyardKind() =>
        _p.AdvancedShipyards && Available(FacilityKinds.ClassName(FacilityKind.AdvancedShipyard)) ? FacilityKind.AdvancedShipyard : FacilityKind.OrbitalShipyard;

    private static bool IsYardKind(FacilityKind k) => FacilityKinds.IsYard(k);

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
                int cap = fam == YardFamily.Shipyard && _p.ShipyardsPerSystem > 0 ? _p.ShipyardsPerSystem : _p.YardsPerSystem;
                if (here >= cap) continue;
                if (here > 0) score += 40 * here;
                if (!_advisor) score -= BorderDanger(s) * 15; // the advisor reads only her own worlds (m6-advisor-sibyl §Manage Garrisons 9)
            }
            else score -= YardsAnyAt(s) * 3; // keep room on the yard systems for more yards
            if (score > bestScore) { bestScore = score; best = s; }
        }
        if (best != null && yard) { var fam = FamilyOfKind(kind); _reservedYards[(best, fam)] = _reservedYards.GetValueOrDefault((best, fam)) + 1; }
        return best;
    }

    private int YardsAnyAt(string system) => _h.Economy.Objects.Count(o => o.System == system && o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k));

    /// <summary>FoundYardsBelow: steps on the Nearby graph out to here count as safer; farther is no safer. AI tuning (AI trainer 2026-11-20).</summary>
    private const int FoundSafeHops = 4;

    /// <summary>Among the safest worlds, those with at least this many energy slots are equally good founding sites (AI trainer 2026-11-20, QA-117):
    /// room for the yard and a few facilities. rule: ai tuning, SimpleAiParams.FoundYardsBelow.</summary>
    private const int FoundGoodEnergy = 3;

    /// <summary>
    /// QA-117 (AI trainer 2026-11-20; Paul 2026-11-19: the enemy must not be predictable): with the game plan on (the Collective), the tie among the
    /// safest good founding sites is broken by this game's plan number, so the rebuilt yard is not at the same world every game; the same game seed
    /// founds at the same world (the plan number is read from GameRng, never drawn). The Hand machine, with no plan, keeps most energy, then name.
    /// rule: ai tuning, SimpleAiParams.PlanSectorSpreadPercent.
    /// </summary>
    private ulong FoundTie(string system) => PlanOn ? Mix(PlanNumber() ^ Hash(system)) : 0;

    /// <summary>Steps (m1-galaxy-data Nearby) from <paramref name="system"/> to the nearest system the side believes the enemy holds, up to
    /// <paramref name="cap"/> (no enemy that close: cap). Breadth first, neighbours in the galaxy's order.</summary>
    private int HopsFromEnemy(string system, int cap)
    {
        var g = _h.Data.Galaxy;
        if (!g.TryFindSystem(system, out var start)) return cap;
        var seen = new HashSet<int> { start }; var ring = new List<int> { start };
        for (int d = 0; d < cap; d++)
        {
            var next = new List<int>();
            foreach (var i in ring)
                foreach (var n in g.NearOf(i))
                {
                    if (!seen.Add(n)) continue;
                    if (IsEnemyHeld(g.Systems[n].Name)) return d;
                    next.Add(n);
                }
            ring = next;
        }
        return cap;
    }

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
        if (_view != null && _side == Side.Hand) PlanSpeculatorBuild();
        PlanGarrison();
        PlanInvasionRegiments();
        PlanAgentForceBuild(); // after the regiments: a training yard goes to the garrisons first
        PlanHarvesterBuild(); // before the ships: it takes its yard and its budget first (m5-harvester §6.1)
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
        long seq = _h.Construction.SubmitBuild(_side, yard, YardFamily.Training, cls, count, dest, byAdvisor: _advisor); // m2-construction §Redirect training
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
        _garrisonReserve = 0; _garrisonShort = 0;
        string cls = _p.GarrisonRegiment(_side);
        if (!Available(cls)) return;
        var pending = PendingRegiments();
        var held = HeldSystems();
        var redirected = new HashSet<string>(StringComparer.Ordinal);
        // far ahead, the side needs its garrisons more than new warships; the advisor always puts uprisings first (m6-advisor-sibyl §Manage Garrisons 4)
        bool first = _advisor || (_p.GarrisonsFirst && Finishing());
        // GarrisonsFirst: the worlds in uprising, then the fewest regiments, get the upkeep first (ties: ordinal name)
        if (first) held = held.OrderByDescending(_h.Support.IsUprising).ThenBy(_h.Support.RegimentCount).ThenBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var s in held)
        {
            int need = GarrisonTarget(s);
            if (need == 0) continue;
            int deficit = need - _h.Support.RegimentCount(s) - pending.GetValueOrDefault(s);
            if (deficit <= 0) continue;
            if (_advisor && _h.Support.IsUprising(s)) deficit -= RedirectForUprising(s, cls, deficit, pending, redirected);
            if (deficit <= 0) continue;
            int count = Math.Min(deficit, _p.GarrisonBatch);
            // rule: m6-advisor-sibyl §Manage Garrisons 6: the advisor keeps a margin on every order, uprisings included (QA-106)
            int room = Budget - (_advisor ? _p.AdvisorUpkeepMargin : 0);
            if (first) while (count > 1 && room < count * Maint(cls)) count--;
            var yard = room < count * Maint(cls) ? null : TrainingYardNear(s);
            if (yard == null)
            {
                // rule: m6-advisor-sibyl §Messages: an uprising she cannot pay for because of the margin is said, so the player can free room
                if (_advisor && _h.Support.IsUprising(s) && room < count * Maint(cls)) _advisorNotes.Add(s);
                if (!first) { if (Budget < count * Maint(cls)) continue; return; } // every training yard is busy
                _garrisonShort += deficit;
                _garrisonReserve += deficit * Maint(cls); // GarrisonsFirst: unpaid or waiting for a yard, its upkeep is still kept from the ships
                continue;
            }
            OrderRegiments(yard, cls, count, s);
            if (first) { _garrisonReserve += (deficit - count) * Maint(cls); _garrisonShort += deficit - count; }
        }
    }

    /// <summary>
    /// The advisor sends her own regiments still in training to a world in uprising: training facilities by travel time to the world (unrated
    /// speed; ties: ordinal), one redirect per facility a day, never from an order bound for another world in uprising. The world they were
    /// bound for no longer counts them. Returns how many were redirected. rule: m6-advisor-sibyl §Manage Garrisons 8; m2-construction §Redirect training.
    /// </summary>
    private int RedirectForUprising(string world, string cls, int deficit, Dictionary<string, int> pending, HashSet<string> redirected)
    {
        int moved = 0;
        foreach (var t in YardSystems(YardFamily.Training).OrderBy(t => Ticks(t, world, UnratedSpeed)).ThenBy(t => t, StringComparer.Ordinal))
        {
            if (moved >= deficit) break;
            if (redirected.Contains(t)) continue;
            var pr = _h.Construction.ProjectAt(t, YardFamily.Training);
            if (pr == null || pr.Side != _side) continue;
            string from = pr.Destination;
            if (from == world || (Held(from) && _h.Support.IsUprising(from))) continue;
            int k = Math.Min(_h.Construction.AdvisorUnitsInTraining(t), deficit - moved);
            if (k <= 0) continue;
            long seq = _h.Construction.SubmitRedirectTraining(_side, t, k, world);
            Note(seq, "Redirect", $"{k} x {cls} in training at {t} redirected from {from} to {world}");
            redirected.Add(t);
            pending[world] = pending.GetValueOrDefault(world) + k;
            pending[from] = pending.GetValueOrDefault(from) - k;
            moved += k;
        }
        return moved;
    }

    /// <summary>GarrisonsFirst: upkeep the garrisons still short of their need would take, kept from new ships (set by PlanGarrison).</summary>
    private int _garrisonReserve;
    /// <summary>Regiments the garrisons are still short after the day's orders, for want of a facility or of upkeep room (set by PlanGarrison). rule: m6-advisor-sibyl §Manage Garrisons 9.</summary>
    private int _garrisonShort;
    /// <summary>Worlds in uprising the advisor left without new regiments today for want of upkeep room (ordinal order of planning). rule: m6-advisor-sibyl §Messages.</summary>
    private readonly List<string> _advisorNotes = new();
    internal IReadOnlyList<string> AdvisorUnpaidUprisings => _advisorNotes;
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
        // InvasionReadyAtRally (Collective): only the spares at the rally system, where the strike piles load, are ready (rule: ai tuning, AI trainer 2026-11-07)
        int spares = _p.InvasionReadyAtRally && _side == Side.Collective ? SpareRegimentsAt(rally)
                     : HeldSystems().Select(SpareRegimentsAt).Where(n => !_p.InvasionBatchesOnly || n >= _p.MinLaunchRegiments).Sum();
        int have = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.Regiment && o.Side == _side && o.AboardShip != 0) + spares;
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
        // the world PlanHqHide sends the Hearth to today keeps all its regiments: the HQ only goes in transit in this Step, so SpecialSystem
        // does not cover it yet, and a fleet loading them would leave the Hearth arriving on a bare world (SafeHearth, seed 2 on 2026-11-16)
        if (system == _hqMoveTo) return new List<int>();
        var ids = _h.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == _side && o.System == system && o.AboardShip == 0)
            .Select(o => o.Id).ToList();
        int keep = Held(system) ? GarrisonTarget(system) : 0;
        return ids.Count > keep ? ids.Skip(keep).ToList() : new List<int>();
    }

    private string? _hqMoveTo; // PlanHqHide's destination today (null when the HQ stays)

    // The Classic AI's garrison wish (B6b §3, EXE-own 0x434e4d..0x43507c): below 70 support min((79 - s) / 10, 6), doubled in an uprising (after the cap);
    // the special world's base is 9 (the claimed-world base 3 and the base drift are the Classic AI's, not built here).
    private const int WishBelowSupport = 70, WishTop = 79, WishStep = 10, WishCap = 6, SpecialBase = 9, UprisingFactor = 2;

    /// <summary>
    /// The world the Classic AI's special base (9) applies to: Charter for the Hand; for the Collective the Hearth's world, or while the HQ is on
    /// the way, its destination (its own HQ: the side knows where it goes), so regiments are ordered there when the move starts and the HQ does
    /// not arrive on an unguarded world (QA-10, Dev lead 2026-10-18). rule: ai tuning, B6b §3 ("special" system).
    /// </summary>
    private string? SpecialSystem()
    {
        if (CriticalSystem() is { } c) return c;
        var hq = _h.Game.State.Scenario.Hq;
        return _side == Side.Collective && hq.Status == Praedra.Core.Fog.HqStatus.InTransit && hq.Path.Count > 0 ? hq.Path[^1] : null;
    }

    /// <summary>
    /// Regiments the AI wants standing on a held world: built toward (PlanGarrison) and never moved off (SpareRegimentIds). With
    /// <see cref="SimpleAiParams.ClassicGarrisonFloor"/> (DEFAULT): max(the rule's need, the Classic wish, 9 on the special world). Off: the rule's need,
    /// <see cref="SimpleAiParams.CriticalGarrison"/> on the critical world, and never the last regiment below 60 support (the B5a guard, Dev lead
    /// 2026-10-17). rule: ai tuning, B6b §3 and §11 (Dev lead 2026-10-18).
    /// </summary>
    internal int GarrisonTarget(string system)
    {
        int need = _h.Support.RequiredGarrison(system);
        if (_advisor) // rule: m6-advisor-sibyl §Manage Garrisons 2: the rule's need, and a calm world near a step keeps one more (the buffer)
            return _h.Support.IsUprising(system) ? need : Math.Max(need, Praedra.Core.Support.SupportModule.RequiredGarrison(_h.Support.Params,
                _h.Economy.SupportOf(system, _side) - _p.AdvisorBufferSupport, false, _side == Side.Hand && _h.Support.IsHarvesterSector(system)));
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
    private string? BestWarship(string yardSystem, string? fighter, bool anyRoom = false)
    {
        var eco = _h.Economy;
        int yards = Math.Max(1, YardsDoneAt(yardSystem, YardFamily.Shipyard));
        double fighterStr = fighter != null ? ClassStrength(fighter) : 0;
        if (_holdGap > 0 && Pick(true) is { } holder) return holder; // HoldShipPercent: a ship that pins a leaving enemy first
        return Pick(false);

        string? Pick(bool holdersOnly)
        {
        string? best = null; double bs = 0;
        foreach (var n in ShipClassNames(ObjectCategory.CapitalShip))
        {
            if (n == EconomyModule.HarvesterClass) continue;
            if (holdersOnly && !HoldsLeavingShips(n)) continue;
            var sc = _h.Fleets.ShipClasses[n]; var r = eco.Costs.Get(n);
            // rule: m2-construction §Build time: ceil(cost / yards) x 4 days per unit (standard yards)
            if ((r.Cost + yards - 1) / yards * 4 > _p.MaxBuildDays) continue;
            if (!anyRoom && Budget < r.Maintenance) continue;
            double value = ClassStrength(n) + fighterStr * sc.FighterCapacity * _p.FighterTrustPercent / 100;
            if (value <= 0) continue;
            double score = value / (r.Cost + _p.UpkeepWeight * r.Maintenance); // upkeep is the lasting limit (m2-maintenance), so it weighs more than the one-time cost
            if (score > bs + 1e-9) { bs = score; best = n; }
        }
        return best;
        }
    }

    /// <summary>HoldShipPercent: Hold-ship orders still wanted this review (set by PlanShips; 0 = none or off).</summary>
    private int _holdGap;

    /// <summary>
    /// Does a ship of this class hold a leaving enemy ship in battle? Only in the New battle with HoldBeamsHoldRetreat on, and only a Hold beam of
    /// at least HoldBeamMinPower (DEFAULT 6: the Edict, the Barricade, the harvester). rule: m4-retreat-damage-repair §6.
    /// </summary>
    private bool HoldsLeavingShips(string cls)
    {
        var b = _h.Settings.Battle ?? new Praedra.Core.Battle.BattleParams();
        if (b.Ruleset != Praedra.Core.Battle.BattleRuleset.New || !b.New.HoldBeamsHoldRetreat) return false;
        return _h.Fleets.ShipClasses.TryGetValue(cls, out var sc) && sc.HoldBeamPower > 0 && sc.HoldBeamPower >= b.New.HoldBeamMinPower;
    }

    /// <summary>HoldShipPercent: Hold ships wanted beyond those built or ordered, for the side's armed warships (built or ordered, the harvester and transports aside).</summary>
    private int HoldShipGap()
    {
        if (_advisor || _p.HoldShipPercent <= 0) return 0;
        var eco = _h.Economy;
        int armed = 0, holders = 0;
        foreach (var o in eco.Objects)
        {
            if (o.Category != ObjectCategory.CapitalShip || eco.OwnerOf(o) != _side || o.Class == EconomyModule.HarvesterClass || o.Class == _p.Transport(_side)) continue;
            if (ClassStrength(o.Class) <= 0) continue;
            armed++;
            if (HoldsLeavingShips(o.Class)) holders++;
        }
        // the next warship counts too, so the first Hold ship comes once the navy has a few ships
        return Math.Max(0, ((armed + 1) * _p.HoldShipPercent + 99) / 100 - holders);
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
        _holdGap = HoldShipGap();
        // GarrisonsFirst while far ahead: with garrisons short, no new warships and no refit, so the refined pool trains the regiments first
        // (AI trainer 2026-11-26: with the best navy the far-ahead Hand ordered 41 regiments in 500 days instead of 70 to 90)
        bool garrisonsShort = _p.GarrisonsFirstHoldsNavy && _garrisonShort > 0 && !_advisor; // rule: ai tuning, SimpleAiParams.GarrisonsFirstHoldsNavy
        if (!garrisonsShort) PlanRefit(fighter);

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
            else if (!garrisonsShort)
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
            if (_holdGap > 0 && HoldsLeavingShips(cls)) _holdGap -= count;
            if (cr.Category == ObjectCategory.CapitalShip) { troopCap += count * _h.Fleets.ShipClasses[cls].TroopCapacity; berths += count * _h.Fleets.ShipClasses[cls].FighterCapacity; }
            else if (cr.Category == ObjectCategory.Fighter) fighters += count;
        }
    }

    /// <summary>
    /// RefitBelowPercent (AI trainer 2026-11-26; Paul: the best navy possible): when the best warship a free shipyard can build does not fit the
    /// room but the refined pool pays for it twice over, scrap idle armed warships (at a held system, not moving, no pending bombardment or
    /// assault) whose strength per upkeep is below RefitBelowPercent of that ship's, worst first, until its upkeep is freed or
    /// RefitShipsPerReview are gone. The room is used at a later review. Transports, carriers and the harvester are never refitted, nor a
    /// fleet's last ship while leaders ride it or a prisoner is held aboard.
    /// rule: ai tuning, SimpleAiParams.RefitBelowPercent; m2-maintenance (scrapping frees the upkeep).
    /// </summary>
    private void PlanRefit(string? fighter)
    {
        if (_advisor || _p.RefitBelowPercent <= 0) return;
        var eco = _h.Economy;
        // the free shipyard system with the most finished shipyards: the biggest ship finishes there (m2-construction §Build time)
        string? yard = YardSystems(YardFamily.Shipyard).Where(y => CanOrderAt(y, YardFamily.Shipyard))
            .OrderByDescending(y => YardsDoneAt(y, YardFamily.Shipyard)).ThenBy(y => y, StringComparer.Ordinal).FirstOrDefault();
        if (yard == null) return;
        string? best = BestWarship(yard, fighter, anyRoom: true);
        if (best == null) return;
        var br = eco.Costs.Get(best);
        int need = br.Maintenance - Math.Max(0, Budget - _garrisonReserve);
        if (need <= 0 || eco.Refined(_side) < 2 * br.Cost || br.Maintenance <= 0) return;
        double bestUse = ClassStrength(best) * 1000 / br.Maintenance;
        var cands = new List<(double Use, int Id, int Maint, Praedra.Core.Fleets.Fleet Fleet)>();
        foreach (var o in eco.Objects)
        {
            if (!o.Completed || o.Category != ObjectCategory.CapitalShip || eco.OwnerOf(o) != _side || o.Class == EconomyModule.HarvesterClass || o.Class == _p.Transport(_side)) continue;
            int m = Maint(o.Class);
            double str = ClassStrength(o.Class);
            if (m <= 0 || str <= 0) continue; // unarmed ships (transports, carriers) have other jobs
            double use = str * 1000 / m;
            if (use * 100 >= bestUse * _p.RefitBelowPercent) continue;
            var f = _h.Fleets.FleetOfShip(o.Id);
            if (f == null || f.Transit != null || f.At == null || !Held(f.At) || Pending(f)) continue;
            cands.Add((use, o.Id, m, f));
        }
        var guarded = cands.Select(c => c.Fleet.Id).Distinct().Where(f => _h.Characters.InPlay.Any(ch => ch.AttachedFleet == f || ch.HeldAboard == f)).ToHashSet();
        foreach (var id in RefitScraps(cands, need, _p.RefitShipsPerReview, guarded))
        {
            long seq = _h.Game.Submit(sq => new ScrapObjectOrder(sq, _side, id));
            Note(seq, "Scrap", $"object {id} (refit for {best})");
        }
    }

    /// <summary>
    /// The refit's scraps, worst use first (then id), until <paramref name="need"/> upkeep is freed or <paramref name="max"/> are chosen. The
    /// fleet's last ship dissolves it (m3-fleets invariant 1): its riders would only be set down, but a prisoner held aboard would go free
    /// (characters §Riding, QA-7), so a <paramref name="guarded"/> fleet (leaders or prisoners aboard) keeps at least one ship, counted over the
    /// whole review, squadrons or not. rule: ai tuning, SimpleAiParams.RefitBelowPercent.
    /// </summary>
    internal static List<int> RefitScraps(IEnumerable<(double Use, int Id, int Maint, Praedra.Core.Fleets.Fleet Fleet)> cands, int need, int max, IReadOnlySet<int> guarded)
    {
        var picked = new List<int>();
        var left = new Dictionary<int, int>(); // lookup only; the scraps go in candidate order
        foreach (var c in cands.OrderBy(c => c.Use).ThenBy(c => c.Id))
        {
            if (need <= 0 || picked.Count >= max) break;
            var f = c.Fleet;
            int ships = left.TryGetValue(f.Id, out var l) ? l : f.Ships.Count;
            if (ships <= 1 && guarded.Contains(f.Id)) continue; // kept even with squadrons: the simple, safe rule (a fleet left with squadrons would survive)
            left[f.Id] = ships - 1;
            picked.Add(c.Id);
            need -= c.Maint;
        }
        return picked;
    }

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
            bool keep = _p.UseHarvester && IsHarvesterShip(o); // rule: m5-harvester §6.2: scrapped last, after everything else
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
}
