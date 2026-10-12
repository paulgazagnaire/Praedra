using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Support;

namespace Rebellion.Core.Setup;

/// <summary>
/// TEST SCENARIO, NOT THE REAL GAME START (M5 deals the real one). A scripted two-sided campaign on the real galaxy for the soak tests and
/// the runner: each side holds about 17 systems in its own region, with support values, mines, refineries, yards, regiments as garrison,
/// a standing reserve fleet per side for maintenance load and a few more fleets; then a fixed, seedless list of orders runs by day (build orders at yards, fleet
/// moves, one fleet parked on an enemy system to blockade it) and one scripted capture stands in for the M3 assault.
///
/// Everything here is data or a plain table: no random number is drawn by this class (the only draws are the rules' own, and
/// <see cref="EconomyModule.SetupNewGame"/> arms the two incident timers, <see cref="Research.ResearchModule.SetupNewGame"/> the two research ticks). The same seed and settings always give the same game.
/// The scripted orders are a function of the day only (<see cref="SubmitScriptFor"/>), so a game saved between two Steps and loaded through
/// <see cref="Load"/> continues exactly as the uninterrupted game does.
///
/// Placement follows the starting-facilities data list where it fits: a row whose system is in a region is placed there (a "Neutral" row
/// goes to whoever holds the system here); rows for systems outside both regions (Fondor, Rendili, Mon Calamari, Bilbringi) are not used.
/// Regiments per system are the sheet's requirement N for the system's support (<see cref="SupportModule.RequiredGarrison(SupportParams, int, bool, bool)"/>)
/// minus a shortfall, so a few systems start under-garrisoned and rise. rule: m2-uprisings-and-garrison §Required garrison N.
/// </summary>
public sealed class TestCampaign
{
    /// <summary>The day of the scripted capture (stand-in for an M3 assault, slot 5.2).</summary>
    public const int CaptureDay = 400;
    /// <summary>The Alliance system the Empire captures on <see cref="CaptureDay"/>.</summary>
    public const string CaptureSystem = "Denon";
    /// <summary>The Alliance system the Empire's interdiction fleet parks at, from day <see cref="BlockadeFrom"/>.</summary>
    public const string BlockadedSystem = "Kinyen";
    public const int BlockadeFrom = 60;
    public const string InterdictionFleet = "Interdiction";
    /// <summary>
    /// The day of the one scripted bombardment (m3-bombardment, slot 5.1): the Empire's interdiction fleet, parked at <see cref="BlockadedSystem"/>
    /// since <see cref="BlockadeFrom"/>, shells that system with a General bombardment. No Alliance fleet is there on that day (the relief fleet
    /// arrives later), so the order is accepted. The system holds a KDY-150 battery (placed by <see cref="Deal"/>), so the return-fire hook runs too.
    /// </summary>
    public const int BombardDay = 200;
    /// <summary>
    /// The day of the one scripted assault (m3-assault, slot 5.2): fifty days after the bombardment, the interdiction fleet lands the
    /// <see cref="AssaultRegiments"/> Stormtrooper regiments it has carried since the start on the system it blockades and has shelled. The
    /// Alliance relief fleet only arrives on day 300, so no enemy fleet is there and the order is accepted.
    /// </summary>
    public const int AssaultDay = 250;
    public const int AssaultRegiments = 2; // the Victory Destroyer carries 2
    /// <summary>The name of a side's standing reserve fleet (stays at its capital), e.g. "Empire Reserve".</summary>
    public static string ReserveFleet(Side side) => $"{side} Reserve";

    public GameHost Host { get; }
    public Game Game => Host.Game;

    private TestCampaign(GameHost host)
    {
        Host = host;
        // Slot 5.2 (assaults, M3): the one scripted capture. A hook is not saved, so every path that makes a campaign registers it.
        host.Game.RegisterHook("5.2", ctx =>
        {
            if (ctx.Day == CaptureDay) host.Control.SetHolder(ctx, CaptureSystem, Side.Empire, CaptureCause.Debug);
        });
    }

    /// <summary>A new campaign at day 0. Settings null = every default (support Ruleset Classic, incidents on, ...).</summary>
    public static TestCampaign New(ulong seed, GameSettings? settings = null, GameData? data = null)
    {
        var host = GameHost.New(seed, settings, data);
        Deal(host);
        return new TestCampaign(host);
    }

    /// <summary>A campaign from a save made between two Steps of a campaign game (through <see cref="GameHost.Load"/>).</summary>
    public static TestCampaign Load(string saveJson, GameSettings? settings = null, GameData? data = null) =>
        new(GameHost.Load(saveJson, settings, data));

    /// <summary>One day: the scripted orders of the current day, then one Step.</summary>
    public void Step()
    {
        SubmitScriptFor(Game.Day);
        Game.Step();
    }

    public void RunTo(int day) { while (Game.Day < day) Step(); }

    // ================================================================== the layout

    /// <summary>Region of each side: (system, Alliance support, mines, refineries, regiments short of N, regiment class, extra regiments).</summary>
    private sealed record Site(string System, int AllianceSupport, int Mines, int Refineries, int Short, string Regiment, int Extra = 0);

    private const string Army = "Imperial Army Regiment", Storm = "Stormtrooper Regiment", ImpFleet = "Imperial Fleet Regiment";
    private const string RebArmy = "Alliance Army Regiment", RebFleet = "Alliance Fleet Regiment";

    // Empire: Coruscant and its neighbours, Kuat and Corellia (starting yards). Alliance support is low where the Empire is popular.
    // Delaya starts 2 regiments short (uprising from the first day). Taanab is garrisoned with fleet regiments (weight 0.6 in New).
    private static readonly Site[] EmpireRegion =
    {
        new("Coruscant", 10, 3, 3, 0, Storm, 2), new("Byss", 15, 3, 2, 0, Army), new("Corellia", 65, 2, 2, 0, Army),
        new("Kuat", 20, 2, 2, 0, Army), new("Corulag", 25, 0, 0, 0, Army), new("Delaya", 70, 0, 0, 2, Army),
        new("Pantolomin", 30, 2, 1, 0, Army), new("Skako", 40, 0, 0, 0, Army), new("Taanab", 55, 0, 0, 0, ImpFleet),
        new("Anaxes", 45, 0, 0, 0, Army), new("Commenor", 20, 3, 3, 0, Army), new("Corfai", 30, 2, 2, 0, Army),
        new("Duros", 55, 0, 0, 0, Army), new("Fedalle", 25, 2, 2, 0, Army), new("Hosnian Prime", 40, 2, 2, 0, Army),
        new("Loronar", 35, 0, 0, 0, Army), new("Nubia", 45, 0, 0, 0, Army), new("Balmorra", 25, 2, 2, 0, Army),
    };

    // Alliance: the Sullust and Thyferra cluster. Terminus starts 2 regiments short. Denon (support 55) is the capture target.
    private static readonly Site[] AllianceRegion =
    {
        new("Sullust", 80, 2, 2, 0, RebArmy, 2), new("Eriadu", 75, 2, 1, 0, RebArmy), new("Malastare", 70, 0, 0, 0, RebArmy),
        new("Qat Chrystac", 65, 0, 0, 0, RebArmy), new("Stobar", 80, 2, 2, 0, RebArmy), new("Clak'dor VII", 90, 0, 0, 0, RebArmy),
        new("Denon", 55, 2, 2, 0, RebArmy, 1), new("Derra IV", 85, 0, 0, 0, RebArmy), new("Inusagi", 85, 0, 0, 0, RebArmy),
        new("Kinyen", 50, 2, 2, 0, RebArmy), new("Milagro", 70, 0, 0, 0, RebArmy), new("Ord Ibanna", 95, 0, 0, 0, RebArmy),
        new("Terminus", 35, 0, 0, 2, RebArmy), new("Thyferra", 60, 2, 2, 0, RebArmy), new("Woostri", 75, 0, 0, 0, RebArmy),
        new("Yag'Dhul", 65, 1, 2, 0, RebFleet),
    };

    // Yards beyond the starting-facilities list (that list gives Kuat, Corellia and Sullust their Orbital Shipyard here).
    private static readonly (string System, FacilityKind Kind)[] ExtraYards =
    {
        ("Coruscant", FacilityKind.OrbitalShipyard), ("Coruscant", FacilityKind.TrainingFacility),
        ("Coruscant", FacilityKind.ConstructionYard), ("Coruscant", FacilityKind.ConstructionYard),
        ("Kuat", FacilityKind.ConstructionYard), ("Hosnian Prime", FacilityKind.TrainingFacility),
        ("Sullust", FacilityKind.TrainingFacility), ("Sullust", FacilityKind.ConstructionYard),
        ("Denon", FacilityKind.TrainingFacility), ("Denon", FacilityKind.ConstructionYard),
        ("Kinyen", FacilityKind.TrainingFacility), ("Thyferra", FacilityKind.OrbitalShipyard),
        ("Kinyen", FacilityKind.Kdy150), // the battery of the scripted bombardment (m3-planetary-defenses)
    };

    // Standing "navy": the ships of each side's reserve fleet. They carry maintenance load like every fleet ship (one registry, m3-fleets).
    private static readonly (Side Side, string Class, int Count)[] Navy =
    {
        (Side.Empire, "Imperial Star Destroyer", 3), (Side.Empire, "Victory Destroyer", 2), (Side.Empire, "Carrack Light Cruiser", 1),
        (Side.Empire, "Interdictor Cruiser", 1),
        (Side.Alliance, "Nebulon-B Frigate", 2), (Side.Alliance, "Corellian Corvette", 2), (Side.Alliance, "Mon Calamari Cruiser", 1),
    };

    private static void Deal(GameHost host)
    {
        var fleets = host.Fleets; var eco = host.Economy;

        void Place(Side side, Site s)
        {
            fleets.SetOwner(s.System, side);
            eco.SetAllianceSupport(s.System, s.AllianceSupport);
            for (int i = 0; i < s.Mines; i++) AddFacility(host, s.System, FacilityKind.Mine);
            for (int i = 0; i < s.Refineries; i++) AddFacility(host, s.System, FacilityKind.Refinery);
        }
        foreach (var s in EmpireRegion) Place(Side.Empire, s);
        foreach (var s in AllianceRegion) Place(Side.Alliance, s);

        // Starting-facilities data: placed where the system is in a region (Neutral rows go to the holder, rows for the other side are skipped).
        foreach (var f in host.Data.StartingFacilities)
        {
            var holder = fleets.OwnerOf(f.System);
            if (holder == null || (f.Side != null && f.Side != holder)) continue;
            AddFacility(host, f.System, f.Kind);
        }
        foreach (var (system, kind) in ExtraYards) AddFacility(host, system, kind);

        // Garrisons: N regiments of the sheet's formula for the holder's support, less the shortfall, plus extras.
        void Garrison(Side side, Site s)
        {
            int support = eco.SupportOf(s.System, side);
            int n = Math.Max(0, SupportModule.RequiredGarrison(host.Support.Params, support, false, false) - s.Short) + s.Extra;
            for (int i = 0; i < n; i++) eco.AddObject(side, s.Regiment, s.System);
        }
        foreach (var s in EmpireRegion) Garrison(Side.Empire, s);
        foreach (var s in AllianceRegion) Garrison(Side.Alliance, s);

        // The standing navy is one reserve fleet per side at its capital (ships in a fleet, so they pay upkeep like any ship: m3-fleets).
        foreach (var side in new[] { Side.Empire, Side.Alliance })
            fleets.AddFleet(side, ReserveFleet(side), Navy.Where(n => n.Side == side).SelectMany(n => Enumerable.Repeat(n.Class, n.Count)),
                side == Side.Empire ? "Coruscant" : "Sullust");

        // Fleets (by name; the script finds them by name).
        fleets.AddFleet(Side.Empire, "Home Fleet", new[] { "Imperial Star Destroyer", "Imperial Star Destroyer" }, "Coruscant");
        fleets.AddFleet(Side.Empire, "Kuat Picket", new[] { "Victory Destroyer" }, "Kuat");
        int interdiction = fleets.AddFleet(Side.Empire, InterdictionFleet, new[] { "Interdictor Cruiser", "Victory Destroyer" }, "Hosnian Prime");
        // The landing force of the scripted assault rides the Victory Destroyer (placed aboard directly: the load order needs a held system).
        int carrier = host.Game.State.Fleets[interdiction].Ships.Last();
        for (int i = 0; i < AssaultRegiments; i++)
        {
            var reg = eco.Find(eco.AddObject(Side.Empire, Storm, null))!;
            reg.AboardShip = carrier; reg.System = null;
        }
        fleets.AddFleet(Side.Empire, "Patrol", new[] { "Carrack Light Cruiser" }, "Coruscant");
        fleets.AddFleet(Side.Alliance, "Sullust Fleet", new[] { "Nebulon-B Frigate", "Corellian Corvette" }, "Sullust", new[] { "X-wing" });
        fleets.AddFleet(Side.Alliance, "Thyferra Picket", new[] { "Corellian Corvette", "Corellian Corvette" }, "Thyferra");
        fleets.AddFleet(Side.Alliance, "Relief", new[] { "Nebulon-B Frigate" }, "Sullust");

        host.Construction.GrantResearch(Side.Alliance, "Nebulon-B Frigate"); // research order 1: the script builds one, so the test setup grants it (m5-research §Availability test step 3)
        eco.SetupNewGame(); // stockpile 0/0 (the economy's DEFAULT), then the two incident timers armed
        host.Research.SetupNewGame(); // then the two research tick arming draws (m5-research §Random draws (a)); the only draws of setup
    }

    private static void AddFacility(GameHost host, string system, FacilityKind kind)
    {
        if (host.Economy.CheckPlace(system, kind) != null)
            throw new InvalidOperationException($"Test campaign layout: no free slot for {kind} on {system}.");
        host.Economy.AddFacility(system, kind);
    }

    // ================================================================== the script

    /// <summary>A build order issued on <c>First</c>, <c>First + Every</c>, ... (Every 0 = once).</summary>
    private sealed record BuildStep(int First, int Every, Side Side, string Yard, YardFamily Family, string Class, int Count, string? Destination = null);
    /// <summary>A move order for a fleet (by name) issued on <c>First</c>, <c>First + Every</c>, ...</summary>
    private sealed record MoveStep(int First, int Every, Side Side, string Fleet, string To);

    // Periods are chosen so a yard's project is usually finished before the next order for it (a new order replaces the old project, which is
    // also a legal outcome and is exercised now and then). Facility orders name systems with free slots (checked by the soak tests).
    private static readonly BuildStep[] Builds =
    {
        // Empire
        new(10, 120, Side.Empire, "Coruscant", YardFamily.Training, Army, 2, "Delaya"),
        new(30, 150, Side.Empire, "Hosnian Prime", YardFamily.Training, Storm, 2, "Corellia"),
        new(5, 250, Side.Empire, "Kuat", YardFamily.Shipyard, "Carrack Light Cruiser", 1, "Coruscant"),
        new(130, 250, Side.Empire, "Kuat", YardFamily.Shipyard, "TIE Fighter", 4),
        new(50, 400, Side.Empire, "Coruscant", YardFamily.Shipyard, "Victory Destroyer", 1, "Kuat"),
        new(20, 500, Side.Empire, "Coruscant", YardFamily.Construction, "Mine", 1, "Duros"),
        new(120, 500, Side.Empire, "Coruscant", YardFamily.Construction, "Refinery", 1, "Duros"),
        new(250, 700, Side.Empire, "Coruscant", YardFamily.Construction, "Training Facility", 1, "Nubia"),
        new(70, 500, Side.Empire, "Kuat", YardFamily.Construction, "Mine", 1, "Skako"),
        new(220, 500, Side.Empire, "Kuat", YardFamily.Construction, "Refinery", 1, "Skako"),
        // Alliance
        new(10, 100, Side.Alliance, "Sullust", YardFamily.Training, RebArmy, 3, "Terminus"),
        new(40, 90, Side.Alliance, "Kinyen", YardFamily.Training, RebArmy, 2, "Kinyen"),
        new(30, 150, Side.Alliance, "Sullust", YardFamily.Shipyard, "Corellian Corvette", 2),
        new(20, 400, Side.Alliance, "Thyferra", YardFamily.Shipyard, "Nebulon-B Frigate", 1, "Sullust"),
        new(200, 400, Side.Alliance, "Thyferra", YardFamily.Shipyard, "X-wing", 3),
        new(50, 600, Side.Alliance, "Sullust", YardFamily.Construction, "Refinery", 1, "Eriadu"),
        new(150, 600, Side.Alliance, "Sullust", YardFamily.Construction, "Mine", 1, "Eriadu"),
        new(380, 0, Side.Alliance, "Denon", YardFamily.Construction, "Mine", 1, "Denon"),   // still building when Denon falls: cancelled
        new(380, 0, Side.Alliance, "Denon", YardFamily.Training, RebArmy, 2, "Denon"),      // same
    };

    private static readonly MoveStep[] Moves =
    {
        // Empire: the interdiction fleet parks at an Alliance system (blockade), leaves on day 700; a patrol shuttles Coruscant - Corellia.
        new(BlockadeFrom, 0, Side.Empire, InterdictionFleet, BlockadedSystem),
        new(700, 0, Side.Empire, InterdictionFleet, "Hosnian Prime"),
        new(20, 80, Side.Empire, "Patrol", "Corellia"),
        new(60, 80, Side.Empire, "Patrol", "Coruscant"),
        // Alliance: a picket shuttles Thyferra - Yag'Dhul; a relief fleet goes to the blockaded system on day 300 and home on day 800.
        new(30, 100, Side.Alliance, "Thyferra Picket", "Yag'Dhul"),
        new(80, 100, Side.Alliance, "Thyferra Picket", "Thyferra"),
        new(300, 0, Side.Alliance, "Relief", BlockadedSystem),
        new(800, 0, Side.Alliance, "Relief", "Sullust"),
    };

    /// <summary>A load or unload order for a fleet (by name) issued on <c>First</c>, <c>First + Every</c>, ...: <c>Count</c> regiments (the lowest ids that qualify).</summary>
    private sealed record TroopStep(int First, int Every, Side Side, string Fleet, bool Load, int Count);

    // The Home Fleet (2 ISD, 6 slots) carries two regiments Coruscant -> Corellia and back; the Alliance reserve (one Mon Calamari Cruiser, 1 slot)
    // loads and unloads one at Sullust. Exercises m3-troops-and-special-forces loading, unloading and the "aboard is not garrison" rule.
    private static readonly TroopStep[] Troops =
    {
        new(100, 300, Side.Empire, "Home Fleet", true, 2), new(170, 300, Side.Empire, "Home Fleet", false, 2),
        new(180, 300, Side.Empire, "Home Fleet", true, 2), new(250, 300, Side.Empire, "Home Fleet", false, 2),
        new(120, 100, Side.Alliance, "Alliance Reserve", true, 1), new(130, 100, Side.Alliance, "Alliance Reserve", false, 1),
    };
    private static readonly MoveStep[] TroopMoves =
    {
        new(101, 300, Side.Empire, "Home Fleet", "Corellia"), new(181, 300, Side.Empire, "Home Fleet", "Coruscant"),
    };

    private static bool Due(int day, int first, int every) => day >= first && (every == 0 ? day == first : (day - first) % every == 0);

    /// <summary>
    /// Submit the scripted orders of <paramref name="day"/> (a function of the day alone). Call between Steps, before the Step of that day;
    /// <see cref="Step"/> does. Orders are validated by the modules in step 1 like any other (a rejected one is an OrderRejected event).
    /// </summary>
    public void SubmitScriptFor(int day)
    {
        foreach (var b in Builds)
            if (Due(day, b.First, b.Every))
                Host.Construction.SubmitBuild(b.Side, b.Yard, b.Family, b.Class, b.Count, b.Destination);
        foreach (var m in Moves.Concat(TroopMoves))
            if (Due(day, m.First, m.Every) && FleetByName(m.Fleet) is { } f)
                Host.Fleets.SubmitMove(m.Side, f.Id, m.To);
        foreach (var t in Troops)
            if (Due(day, t.First, t.Every) && FleetByName(t.Fleet) is { } f && f.At != null)
            {
                var ids = t.Load
                    ? Game.State.Economy.Objects.Values.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == t.Side && o.System == f.At && o.AboardShip == 0)
                        .Select(o => o.Id).Take(t.Count).ToList()
                    : Host.Troops.RegimentsAboard(f).Select(a => a.Regiment.Id).Take(t.Count).ToList();
                if (ids.Count == 0) continue;
                if (t.Load) Host.Troops.SubmitLoad(t.Side, f.Id, ids); else Host.Troops.SubmitUnload(t.Side, f.Id, ids);
            }
        // rule: m3-bombardment §Order and preconditions (the interdiction fleet shells the system it blockades)
        if (day == BombardDay && FleetByName(InterdictionFleet) is { } bomber)
            Host.Bombardment.SubmitBombard(Side.Empire, bomber.Id, BlockadedSystem, BombardCategory.General);
        // rule: m3-assault §Order and preconditions (the same fleet lands its regiments on the system it has shelled)
        if (day == AssaultDay && FleetByName(InterdictionFleet) is { } lander)
            Host.Assault.SubmitAssault(Side.Empire, lander.Id, BlockadedSystem);
    }

    /// <summary>A fleet by its (unique) name, or null.</summary>
    public Fleet? FleetByName(string name) => Game.State.Fleets.Values.FirstOrDefault(f => f.Name == name);
}
