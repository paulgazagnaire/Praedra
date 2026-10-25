using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Support;

namespace Praedra.Core.Setup;

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
/// goes to whoever holds the system here); rows for systems outside both regions (Incus, Castra, The Moorings, Ferraria) are not used.
/// Regiments per system are the sheet's requirement N for the system's support (<see cref="SupportModule.RequiredGarrison(SupportParams, int, bool, bool)"/>)
/// minus a shortfall, so a few systems start under-garrisoned and rise. rule: m2-uprisings-and-garrison §Required garrison N.
/// </summary>
public sealed class TestCampaign
{
    /// <summary>The day of the scripted capture (stand-in for an M3 assault, slot 5.2).</summary>
    public const int CaptureDay = 400;
    /// <summary>The Collective system the Hand captures on <see cref="CaptureDay"/>.</summary>
    public const string CaptureSystem = "Fiducia";
    /// <summary>The regiment class the scripted capture places on <see cref="CaptureSystem"/> under derived control (B5a).</summary>
    public const string CaptureRegiment = "Tranches";
    /// <summary>
    /// How many: 6, the most an assault lands (m3-assault §Landing, the cap 6 [EXE-own], B5a section 3). Under derived control the world stays the
    /// Hand's only while its regiments stand there, so the stand-in lands what a real assault would.
    /// </summary>
    public const int CaptureRegiments = 6;
    /// <summary>The Collective system the Hand's interdiction fleet parks at, from day <see cref="BlockadeFrom"/>.</summary>
    public const string BlockadedSystem = "Tierra Larga";
    public const int BlockadeFrom = 60;
    public const string InterdictionFleet = "Interdiction";
    /// <summary>
    /// The day of the one scripted bombardment (m3-bombardment, slot 5.1): the Hand's interdiction fleet, parked at <see cref="BlockadedSystem"/>
    /// since <see cref="BlockadeFrom"/>, shells that system with a General bombardment. No Collective fleet is there on that day (the relief fleet
    /// arrives later), so the order is accepted. The system holds a Mordant Lien-150 battery (placed by <see cref="Deal"/>), so the return-fire hook runs too.
    /// </summary>
    public const int BombardDay = 200;
    /// <summary>
    /// The day of the one scripted assault (m3-assault, slot 5.2): fifty days after the bombardment, the interdiction fleet lands the
    /// <see cref="AssaultRegiments"/> Stormtrooper regiments it has carried since the start on the system it blockades and has shelled. The
    /// Collective relief fleet only arrives on day 300, so no enemy fleet is there and the order is accepted.
    /// </summary>
    public const int AssaultDay = 250;
    public const int AssaultRegiments = 2; // the Decree carries 2
    /// <summary>The name of a side's standing reserve fleet (stays at its capital), e.g. "Hand Reserve".</summary>
    public static string ReserveFleet(Side side) => $"{side} Reserve";

    public GameHost Host { get; }
    public Game Game => Host.Game;

    private TestCampaign(GameHost host)
    {
        Host = host;
        // Slot 5.2 (assaults, M3): the one scripted capture. A hook is not saved, so every path that makes a campaign registers it.
        host.Game.RegisterHook("5.2", ctx =>
        {
            // Under derived control (B5a, DEFAULT) the capture is scripted as troops: the system's regiments become six Hand regiments.
            if (ctx.Day != CaptureDay) return;
            if (host.Control.Rule == ControlRule.Derived) host.Control.DebugApplyTroops(ctx, CaptureSystem, Side.Hand, CaptureRegiment, CaptureRegiments);
            else host.Control.SetHolder(ctx, CaptureSystem, Side.Hand, CaptureCause.Debug);
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

    /// <summary>Region of each side: (system, Collective support, mines, refineries, regiments short of N, regiment class, extra regiments).</summary>
    private sealed record Site(string System, int CollectiveSupport, int Mines, int Refineries, int Short, string Regiment, int Extra = 0);

    private const string Army = "Tranches", Storm = "Pacifiers", ImpFleet = "Ballast";
    private const string RebArmy = "Militia", RebFleet = "Deckhands";

    // Hand: Charter and its neighbours, Navalia and Classis (starting yards). Collective support is low where the Hand is popular.
    // Tranquilla starts 2 regiments short (uprising from the first day). Granero is garrisoned with fleet regiments (weight 0.6 in New).
    private static readonly Site[] HandRegion =
    {
        new("Charter", 10, 3, 3, 0, Storm, 2), new("Mirabel", 15, 3, 2, 0, Army), new("Classis", 65, 2, 2, 0, Army),
        new("Navalia", 20, 2, 2, 0, Army), new("Tutela", 25, 0, 0, 0, Army), new("Tranquilla", 70, 0, 0, 2, Army),
        new("Belmare", 30, 2, 1, 0, Army), new("Gnomon", 40, 0, 0, 0, Army), new("Granero", 55, 0, 0, 0, ImpFleet),
        new("Fusoria", 45, 0, 0, 0, Army), new("Porphyry", 20, 3, 3, 0, Army), new("Magnetite III", 30, 2, 2, 0, Army),
        new("Hiberna", 55, 0, 0, 0, Army), new("Vigilia", 25, 2, 2, 0, Army), new("Fiscus", 40, 2, 2, 0, Army),
        new("Vallum", 35, 0, 0, 0, Army), new("Ommata", 45, 0, 0, 0, Army), new("Tormentum", 25, 2, 2, 0, Army),
    };

    // Collective: the Caminus and Salus cluster. Endline starts 2 regiments short. Fiducia (support 55) is the capture target.
    private static readonly Site[] CollectiveRegion =
    {
        new("Caminus", 80, 2, 2, 0, RebArmy, 2), new("Cunabula", 75, 2, 1, 0, RebArmy), new("Wellhead", 70, 0, 0, 0, RebArmy),
        new("Glassfield", 65, 0, 0, 0, RebArmy), new("Halfpenny", 80, 2, 2, 0, RebArmy), new("Opsis", 90, 0, 0, 0, RebArmy),
        new("Fiducia", 55, 2, 2, 0, RebArmy, 1), new("Ribeira", 85, 0, 0, 0, RebArmy), new("Feriae", 85, 0, 0, 0, RebArmy),
        new("Tierra Larga", 50, 2, 2, 0, RebArmy), new("Clinker", 70, 0, 0, 0, RebArmy), new("Last Platform", 95, 0, 0, 0, RebArmy),
        new("Endline", 35, 0, 0, 2, RebArmy), new("Salus", 60, 2, 2, 0, RebArmy), new("Folio", 75, 0, 0, 0, RebArmy),
        new("Craterhold", 65, 1, 2, 0, RebFleet),
    };

    // Yards beyond the starting-facilities list (that list gives Navalia, Classis and Caminus their Orbital Shipyard here).
    private static readonly (string System, FacilityKind Kind)[] ExtraYards =
    {
        ("Charter", FacilityKind.OrbitalShipyard), ("Charter", FacilityKind.TrainingFacility),
        ("Charter", FacilityKind.ConstructionYard), ("Charter", FacilityKind.ConstructionYard),
        ("Navalia", FacilityKind.ConstructionYard), ("Fiscus", FacilityKind.TrainingFacility),
        ("Caminus", FacilityKind.TrainingFacility), ("Caminus", FacilityKind.ConstructionYard),
        ("Fiducia", FacilityKind.TrainingFacility), ("Fiducia", FacilityKind.ConstructionYard),
        ("Tierra Larga", FacilityKind.TrainingFacility), ("Salus", FacilityKind.OrbitalShipyard),
        ("Tierra Larga", FacilityKind.Kdy150), // the battery of the scripted bombardment (m3-planetary-defenses)
    };

    // Standing "navy": the ships of each side's reserve fleet. They carry maintenance load like every fleet ship (one registry, m3-fleets).
    private static readonly (Side Side, string Class, int Count)[] Navy =
    {
        (Side.Hand, "Mandate", 3), (Side.Hand, "Decree", 2), (Side.Hand, "Warrant", 1),
        (Side.Hand, "Subpoena", 1),
        (Side.Collective, "Mender", 2), (Side.Collective, "Dodger", 2), (Side.Collective, "Ark", 1),
    };

    private static void Deal(GameHost host)
    {
        var fleets = host.Fleets; var eco = host.Economy;

        void Place(Side side, Site s)
        {
            fleets.SetOwner(s.System, side);
            eco.SetCollectiveSupport(s.System, s.CollectiveSupport);
            for (int i = 0; i < s.Mines; i++) AddFacility(host, s.System, FacilityKind.Mine);
            for (int i = 0; i < s.Refineries; i++) AddFacility(host, s.System, FacilityKind.Refinery);
        }
        foreach (var s in HandRegion) Place(Side.Hand, s);
        foreach (var s in CollectiveRegion) Place(Side.Collective, s);

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
        foreach (var s in HandRegion) Garrison(Side.Hand, s);
        foreach (var s in CollectiveRegion) Garrison(Side.Collective, s);

        // The standing navy is one reserve fleet per side at its capital (ships in a fleet, so they pay upkeep like any ship: m3-fleets).
        foreach (var side in new[] { Side.Hand, Side.Collective })
            fleets.AddFleet(side, ReserveFleet(side), Navy.Where(n => n.Side == side).SelectMany(n => Enumerable.Repeat(n.Class, n.Count)),
                side == Side.Hand ? "Charter" : "Caminus");

        // Fleets (by name; the script finds them by name).
        fleets.AddFleet(Side.Hand, "Home Fleet", new[] { "Mandate", "Mandate" }, "Charter");
        fleets.AddFleet(Side.Hand, "Navalia Picket", new[] { "Decree" }, "Navalia");
        int interdiction = fleets.AddFleet(Side.Hand, InterdictionFleet, new[] { "Subpoena", "Decree" }, "Fiscus", listedOrder: true); // ids in this order whatever the names
        // The landing force of the scripted assault rides the Decree (placed aboard directly: the load order needs a held system).
        int carrier = host.Game.State.Fleets[interdiction].Ships.Last(id => eco.Find(id)!.Class == "Decree");
        for (int i = 0; i < AssaultRegiments; i++)
        {
            var reg = eco.Find(eco.AddObject(Side.Hand, Storm, null))!;
            reg.AboardShip = carrier; reg.System = null;
        }
        fleets.AddFleet(Side.Hand, "Patrol", new[] { "Warrant" }, "Charter");
        fleets.AddFleet(Side.Collective, "Caminus Fleet", new[] { "Mender", "Dodger" }, "Caminus", new[] { "Swift" });
        fleets.AddFleet(Side.Collective, "Salus Picket", new[] { "Dodger", "Dodger" }, "Salus");
        fleets.AddFleet(Side.Collective, "Relief", new[] { "Mender" }, "Caminus");

        host.Construction.GrantResearch(Side.Collective, "Mender"); // research order 1: the script builds one, so the test setup grants it (m5-research §Availability test step 3)
        eco.SetupNewGame(); // stockpile 0/0 (the economy's DEFAULT), then the two incident timers armed
        host.Research.SetupNewGame(); // then the two research tick arming draws (m5-research §Random draws (a)); the only draws of setup
        // (B5a, 2026-10-17) control is derived once after setup, as a new game does (no draw). rule: m2-control-and-capture §Who holds a system (B5a §Rebuild 2)
        host.Control.SetupNewGame();
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
        // Hand
        new(10, 120, Side.Hand, "Charter", YardFamily.Training, Army, 2, "Tranquilla"),
        new(30, 150, Side.Hand, "Fiscus", YardFamily.Training, Storm, 2, "Classis"),
        new(5, 250, Side.Hand, "Navalia", YardFamily.Shipyard, "Warrant", 1, "Charter"),
        new(130, 250, Side.Hand, "Navalia", YardFamily.Shipyard, "Gleaner", 4),
        new(50, 400, Side.Hand, "Charter", YardFamily.Shipyard, "Decree", 1, "Navalia"),
        new(20, 500, Side.Hand, "Charter", YardFamily.Construction, "Mine", 1, "Hiberna"),
        new(120, 500, Side.Hand, "Charter", YardFamily.Construction, "Refinery", 1, "Hiberna"),
        new(250, 700, Side.Hand, "Charter", YardFamily.Construction, "Training Facility", 1, "Ommata"),
        new(70, 500, Side.Hand, "Navalia", YardFamily.Construction, "Mine", 1, "Gnomon"),
        new(220, 500, Side.Hand, "Navalia", YardFamily.Construction, "Refinery", 1, "Gnomon"),
        // Collective
        new(10, 100, Side.Collective, "Caminus", YardFamily.Training, RebArmy, 3, "Endline"),
        new(40, 90, Side.Collective, "Tierra Larga", YardFamily.Training, RebArmy, 2, "Tierra Larga"),
        new(30, 150, Side.Collective, "Caminus", YardFamily.Shipyard, "Dodger", 2),
        new(20, 400, Side.Collective, "Salus", YardFamily.Shipyard, "Mender", 1, "Caminus"),
        new(200, 400, Side.Collective, "Salus", YardFamily.Shipyard, "Swift", 3),
        new(50, 600, Side.Collective, "Caminus", YardFamily.Construction, "Refinery", 1, "Cunabula"),
        new(150, 600, Side.Collective, "Caminus", YardFamily.Construction, "Mine", 1, "Cunabula"),
        new(380, 0, Side.Collective, "Fiducia", YardFamily.Construction, "Mine", 1, "Fiducia"),   // still building when Fiducia falls: cancelled
        new(380, 0, Side.Collective, "Fiducia", YardFamily.Training, RebArmy, 2, "Fiducia"),      // same
    };

    private static readonly MoveStep[] Moves =
    {
        // Hand: the interdiction fleet parks at a Collective system (blockade), leaves on day 700; a patrol shuttles Charter - Classis.
        new(BlockadeFrom, 0, Side.Hand, InterdictionFleet, BlockadedSystem),
        new(700, 0, Side.Hand, InterdictionFleet, "Fiscus"),
        new(20, 80, Side.Hand, "Patrol", "Classis"),
        new(60, 80, Side.Hand, "Patrol", "Charter"),
        // Collective: a picket shuttles Salus - Craterhold; a relief fleet goes to the blockaded system on day 300 and home on day 800.
        new(30, 100, Side.Collective, "Salus Picket", "Craterhold"),
        new(80, 100, Side.Collective, "Salus Picket", "Salus"),
        new(300, 0, Side.Collective, "Relief", BlockadedSystem),
        new(800, 0, Side.Collective, "Relief", "Caminus"),
    };

    /// <summary>A load or unload order for a fleet (by name) issued on <c>First</c>, <c>First + Every</c>, ...: <c>Count</c> regiments (the lowest ids that qualify).</summary>
    private sealed record TroopStep(int First, int Every, Side Side, string Fleet, bool Load, int Count);

    // The Home Fleet (2 ISD, 6 slots) carries two regiments Charter -> Classis and back; the Collective reserve (one Ark, 1 slot)
    // loads and unloads one at Caminus. Exercises m3-troops-and-special-forces loading, unloading and the "aboard is not garrison" rule.
    private static readonly TroopStep[] Troops =
    {
        new(100, 300, Side.Hand, "Home Fleet", true, 2), new(170, 300, Side.Hand, "Home Fleet", false, 2),
        new(180, 300, Side.Hand, "Home Fleet", true, 2), new(250, 300, Side.Hand, "Home Fleet", false, 2),
        new(120, 100, Side.Collective, "Collective Reserve", true, 1), new(130, 100, Side.Collective, "Collective Reserve", false, 1),
    };
    private static readonly MoveStep[] TroopMoves =
    {
        new(101, 300, Side.Hand, "Home Fleet", "Classis"), new(181, 300, Side.Hand, "Home Fleet", "Charter"),
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
            Host.Bombardment.SubmitBombard(Side.Hand, bomber.Id, BlockadedSystem, BombardCategory.General);
        // rule: m3-assault §Order and preconditions (the same fleet lands its regiments on the system it has shelled)
        if (day == AssaultDay && FleetByName(InterdictionFleet) is { } lander)
            Host.Assault.SubmitAssault(Side.Hand, lander.Id, BlockadedSystem);
    }

    /// <summary>A fleet by its (unique) name, or null.</summary>
    public Fleet? FleetByName(string name) => Game.State.Fleets.Values.FirstOrDefault(f => f.Name == name);
}
