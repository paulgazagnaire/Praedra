using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Data;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;

namespace Rebellion.Core.Research;

/// <summary>
/// Research and tech unlocks: three tracks per side (Ship, Troop, Facility), a level and a stock of points each. An item is researched when its
/// research order is at most the side's level in its track. Points come from the facility tick (day slot 6.9: one point per completed yard of
/// the track's family on a held system, at a 30 + rand(0..30) day timer) and from research missions (m5-missions calls
/// <see cref="CanResearch"/>, <see cref="RollMissionPoints"/> and <see cref="AddMissionPoints"/>; missions are not built yet, so nothing calls
/// them in a game today). Holds data and settings only; state lives in <see cref="GameState.Research"/>. Attach after repair (slot 6.9 comes
/// after 6.8) and before victory (6.11, last). Draws: two at setup, one per tick firing, one per successful mission member.
/// rule: m5-research (whole sheet).
/// </summary>
public sealed class ResearchModule
{
    private readonly Game _game;
    private readonly EconomyModule _eco;
    private readonly FleetModule _fleets;
    private readonly int _systemsInPlay;
    // Ladders by (side, kind): classes with cost > 0 and the side's flag, sorted by research order then catalogue order. Built once; lists only.
    private readonly List<ResearchRow>[] _ladders = new List<ResearchRow>[6];
    // Class name -> ladder row (lookup only, never iterated).
    private readonly Dictionary<string, ResearchRow> _byName = new(StringComparer.Ordinal);

    public ResearchData Data { get; }
    public ResearchRules Rules => Data.Rules;
    public ResearchParams Params { get; }

    /// <summary>The fixed track order Ship, Troop, Facility. rule: m5-research §State per side (arrays fixed order).</summary>
    public static readonly IReadOnlyList<ResearchKind> Kinds = new[] { ResearchKind.Ship, ResearchKind.Troop, ResearchKind.Facility };
    /// <summary>
    /// Side id ascending as the sheet gives it: Alliance then Empire (m5-research §Random draws (a), RS21, RS25). Our <see cref="Side"/> enum
    /// numbers Empire 0, so the order is written out here.
    /// </summary>
    public static readonly IReadOnlyList<Side> SideOrder = new[] { Side.Alliance, Side.Empire };

    private ResearchModule(Game game, EconomyModule eco, FleetModule fleets, ResearchData data, ResearchParams p)
    {
        _game = game; _eco = eco; _fleets = fleets; Data = data; Params = p;
        _systemsInPlay = fleets.Galaxy.Systems.Count;

        var rows = new List<ResearchRow>(data.Rows);
        // rule: m5-research §Sensor Station tiers: off = no row; on = a Facility row at order SensorTier2Order for both sides, and the engine
        // refuses to start without a difficulty (Missing; no invented default).
        if (p.SensorTier2Research)
        {
            if (p.SensorTier2Difficulty is not int sd)
                throw new DataLoadException("SensorTier2Research is on but no research difficulty for the Advanced Sensor Station is supplied (m5-research §Sensor Station tiers: Missing, Paul must supply it).");
            rows.Add(new ResearchRow(rows.Count, ResearchKind.Facility, ResearchData.AdvancedSensorStation, true, true,
                Rules.SensorTier2BuildCost, Rules.SensorTier2Order, sd));
        }
        foreach (var r in rows) _byName[r.Name] = r;
        foreach (var side in SideOrder)
            foreach (var kind in Kinds)
            {
                var ladder = rows.Where(r => r.Kind == kind && r.Cost > 0 && (side == Side.Empire ? r.Empire : r.Alliance))
                    .OrderBy(r => r.Order).ThenBy(r => r.Catalogue).ToList();
                // rule: m5-research §Rule (New) 1 (OneClassPerRung: a startup data check refuses two classes of one side on one rung)
                if (p.OneClassPerRung)
                    for (int i = 1; i < ladder.Count; i++)
                        if (ladder[i].Order > 0 && ladder[i].Order == ladder[i - 1].Order)
                            throw new DataLoadException($"{ResearchData.LadderFileName}: {side} {kind} rung {ladder[i].Order} has two classes ('{ladder[i - 1].Name}', '{ladder[i].Name}') and OneClassPerRung is on.");
                _ladders[Index(side, kind)] = ladder;
            }
    }

    /// <summary>Register slot 6.9. rule: m5-research §Where it runs in the day Step.</summary>
    public static ResearchModule Attach(Game game, EconomyModule eco, FleetModule fleets, ResearchData data, ResearchParams? p = null)
    {
        var m = new ResearchModule(game, eco, fleets, data, p ?? new ResearchParams());
        game.RegisterHook("6.9", m.Tick);
        return m;
    }

    private static int Index(Side side, ResearchKind kind) => (side == Side.Empire ? 0 : 3) + (int)kind;
    private ResearchState R => _game.State.Research;

    // ================================================================== setup (between Steps)

    /// <summary>
    /// Setup only: fix the two percents (from the scale settings; "systems in play" = every system of the loaded galaxy unless given) and arm
    /// the facility tick of each side, Alliance then Empire: TickDue = arming day + delay + rand(0..span). Two draws.
    /// rule: m5-research §Facility income (Arming), §Scaling with galaxy size, §Random draws (a).
    /// </summary>
    public void SetupNewGame(int? systemsInPlay = null)
    {
        int systems = systemsInPlay ?? _systemsInPlay;
        R.CostPercent = Percent(Params.ResearchCostScale, systems, Params.ResearchCostPercentFixed);
        R.MissionPercent = Percent(Params.MissionPointsScale, systems, Params.MissionPointsPercentFixed);
        foreach (var side in SideOrder)
            R.Of(side).TickDue = Rules.TickArmingDay + Rules.TickDelayDays + _game.Rng.NextInt(0, Rules.TickSpanDays);
    }

    private static int Percent(ResearchScale scale, int systems, int fixedPercent) => scale switch
    {
        ResearchScale.SystemCount => systems,
        ResearchScale.Fixed => fixedPercent,
        _ => 100,
    };

    /// <summary>Scenarios and tests only: set a side's level in a track (no event, no check). rule: m5-research §State per side (SetLevel).</summary>
    public void SetLevel(Side side, ResearchKind kind, int level)
    {
        if (level < 0) throw new ArgumentOutOfRangeException(nameof(level));
        R.Of(side).Level[(int)kind] = level;
    }

    /// <summary>Tests only: set a side's points in a track (no check runs).</summary>
    public void SetPoints(Side side, ResearchKind kind, int points)
    {
        if (points < 0) throw new ArgumentOutOfRangeException(nameof(points));
        R.Of(side).Points[(int)kind] = points;
    }

    // ================================================================== queries (read-only; the UI progress bar, m5-research §Rule (New) 4)

    public int Points(Side side, ResearchKind kind) => R.Of(side).Points[(int)kind];
    public int Level(Side side, ResearchKind kind) => R.Of(side).Level[(int)kind];
    public bool Exhausted(Side side, ResearchKind kind) => R.Of(side).Exhausted[(int)kind];
    public int TickDue(Side side) => R.Of(side).TickDue;
    public int CostPercent => R.CostPercent;
    public int MissionPercent => R.MissionPercent;

    /// <summary>The side's ladder of a track: classes with cost &gt; 0 and the side's flag, by research order then catalogue order.</summary>
    public IReadOnlyList<ResearchRow> Ladder(Side side, ResearchKind kind) => _ladders[Index(side, kind)];

    /// <summary>The track of a class, null if the class has no ladder row. rule: m5-research §Which track an item belongs to.</summary>
    public ResearchKind? KindOf(string className) => _byName.TryGetValue(className, out var r) ? r.Kind : null;

    /// <summary>The next rung above the side's level (order, scaled cost, first class in catalogue order), null when nothing is left.</summary>
    public (int Order, int Cost, string Class)? NextRung(Side side, ResearchKind kind)
    {
        var next = Next(side, kind);
        return next == null ? null : (next.Order, Scaled(next.Difficulty), next.Name);
    }

    /// <summary>
    /// Is the class's research order at most the side's level in its track? A class without a ladder row is not. This is step 2 of the
    /// availability test; the switch and the grants (steps 1 and 3) stay in <see cref="ConstructionModule.IsResearched"/>.
    /// rule: m5-research §Availability test.
    /// </summary>
    public bool OrderReached(Side side, string className) =>
        _byName.TryGetValue(className, out var r) && r.Order <= R.Of(side).Level[(int)r.Kind];

    // ================================================================== slot 6.9: the facility tick

    // rule: m5-research §Facility income (Firing, Counting); sides Alliance then Empire, tracks Ship, Troop, Facility
    private void Tick(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var side in SideOrder)
        {
            var sr = R.Of(side);
            if (sr.TickDue == 0 || sr.TickDue != t) continue;
            if (Params.FacilityResearch)
            {
                var counts = CountYards(side);
                foreach (var kind in Kinds)
                {
                    int add = counts[(int)kind] * Rules.PointsPerYard;
                    if (add == 0) continue; // a count of 0 adds nothing and runs no check
                    sr.Points[(int)kind] += add;
                    CheckLevel(ctx, side, kind);
                }
            }
            // re-arm: exactly one draw per firing, after the counts are added (also with FacilityResearch off)
            sr.TickDue = t + Rules.TickDelayDays + ctx.Rng.NextInt(0, Rules.TickSpanDays);
        }
    }

    /// <summary>
    /// Completed, installed yards per track on systems the side holds: shipyards feed Ship, training facilities Troop, construction yards
    /// Facility; Advanced yards count the same; mines, refineries and defenses nothing; queued or delivering yards are not completed.
    /// rule: m5-research §Facility income (Counting).
    /// </summary>
    public int[] CountYards(Side side)
    {
        var counts = new int[3];
        foreach (var o in _eco.Objects) // order does not matter: counts only
        {
            if (o.Category != ObjectCategory.Facility || !o.Completed || o.System == null) continue;
            if (!FacilityKinds.TryParse(o.Class, out var k) || !FacilityKinds.IsYard(k)) continue;
            if (_fleets.OwnerOf(o.System) != side) continue;
            if (Params.BlockadeStopsResearch && _fleets.IsEconomicBlockade(o.System, side)) continue; // rule: m5-research §Facility income (Counting), Paul 2026-10-16
            counts[(int)TrackOf(Yards.FamilyOf(k))]++;
        }
        return counts;
    }

    private static ResearchKind TrackOf(YardFamily f) => f switch
    {
        YardFamily.Shipyard => ResearchKind.Ship,
        YardFamily.Training => ResearchKind.Troop,
        _ => ResearchKind.Facility,
    };

    private static YardFamily FamilyOf(ResearchKind k) => k switch
    {
        ResearchKind.Ship => YardFamily.Shipyard,
        ResearchKind.Troop => YardFamily.Training,
        _ => YardFamily.Construction,
    };

    // ================================================================== mission income: the interface m5-missions calls

    /// <summary>
    /// May a research mission of this kind run at this system? Null = yes. NothingToResearch when the track is exhausted; NoMatchingYard
    /// when the system is not the side's or has no completed yard of the matching family (Advanced counts). Called by m5-missions when the
    /// mission is ordered and at the start of every iteration. rule: m5-research §Mission income (1).
    /// </summary>
    public string? CanResearch(Side side, ResearchKind kind, string system)
    {
        if (R.Of(side).Exhausted[(int)kind]) return RejectReason.NothingToResearch;
        if (_fleets.OwnerOf(system) != side) return RejectReason.NoMatchingYard;
        var fam = FamilyOf(kind);
        bool yard = _eco.Objects.Any(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == fam);
        return yard ? null : RejectReason.NoMatchingYard;
    }

    /// <summary>
    /// Points of one successful mission member: 1 + rand(0..6), then x MissionPointsPercent / 100 (integer division). One draw per call. Called
    /// by m5-missions right after each successful member's roll, members in ascending character id. rule: m5-research §Mission income (2).
    /// </summary>
    public int RollMissionPoints(StepContext ctx) => ScaleMissionPoints(Rules.MissionPointsBase + ctx.Rng.NextInt(0, Rules.MissionPointsSpan), R.MissionPercent);

    /// <summary>p x percent / 100, integer division. rule: m5-research §Mission income (2).</summary>
    public static int ScaleMissionPoints(int basePoints, int percent) => basePoints * percent / 100;

    /// <summary>
    /// Add a mission iteration's total to the side's points of the track and run the level check; returns the levels gained (for the mission
    /// report). A total of 0, or MissionResearch off, adds nothing. Called by m5-missions once per iteration, at its end.
    /// rule: m5-research §Mission income (3).
    /// </summary>
    public int AddMissionPoints(StepContext ctx, Side side, ResearchKind kind, int total)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (total == 0 || !Params.MissionResearch) return 0;
        R.Of(side).Points[(int)kind] += total;
        return CheckLevel(ctx, side, kind);
    }

    // ================================================================== the level check

    private ResearchRow? Next(Side side, ResearchKind kind)
    {
        int level = R.Of(side).Level[(int)kind];
        // the ladder is sorted by order then catalogue: the first row above the level is the first class of the lowest higher rung (empty rungs skipped)
        foreach (var r in _ladders[Index(side, kind)]) if (r.Order > level) return r;
        return null;
    }

    private int Scaled(int difficulty) => difficulty * R.CostPercent / 100;

    /// <summary>
    /// Climb as many rungs as the points pay for; one ResearchLevelReached per rung, ascending; no next rung = Exhausted (no event).
    /// Returns the levels gained. Draws nothing. rule: m5-research §The level check.
    /// </summary>
    private int CheckLevel(StepContext ctx, Side side, ResearchKind kind)
    {
        var sr = R.Of(side);
        int k = (int)kind, gained = 0;
        while (true)
        {
            var next = Next(side, kind);
            if (next == null) { sr.Exhausted[k] = true; return gained; }
            int cost = Scaled(next.Difficulty);
            if (sr.Points[k] < cost) return gained;
            sr.Level[k] = next.Order;
            sr.Points[k] -= cost;
            gained++;
            int level = next.Order;
            var classes = new ValueList<string>(_ladders[Index(side, kind)].Where(r => r.Order == level).Select(r => r.Name)
                .OrderBy(n => n, StringComparer.Ordinal).ToList());
            ctx.Emit((i, d) => new ResearchLevelReached(i, d, side, kind, level, classes));
        }
    }
}
