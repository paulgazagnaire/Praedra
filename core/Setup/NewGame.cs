using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Economy;
using Praedra.Core.Support;
using Praedra.Core.Victory;

namespace Praedra.Core.Setup;

/// <summary>What a new game needs: a seed, one of the five day-one presets, optional setting overrides and data.</summary>
public sealed record NewGameOptions
{
    public int Seed { get; init; } = 1;
    public StartPreset Preset { get; init; } = StartPreset.MedA;
    /// <summary>Null = every default.</summary>
    public GameSettings? Settings { get; init; }
    /// <summary>Null = the repository's data.</summary>
    public GameData? Data { get; init; }
    /// <summary>Null = the repository's new-game data.</summary>
    public NewGameData? NewGameData { get; init; }
}

/// <summary>
/// Deals a real day-one position on the 360-system galaxy and returns a <see cref="GameHost"/> ready for <c>Game.Step()</c>.
/// Owners, Collective support, explored flags and the Hearth come from the Anchored dealer (DEFAULT, <see cref="GameSettings.DealMode"/>; draws from the
/// game's generator, m5-setup-and-victory §Mode Anchored) or, in File mode, from the day-one file of the preset (setup-victory/09 §1-5, PROPOSAL values).
/// Facilities, garrisons, fleets and stockpile come from <c>new-game-forces.csv</c> and are DEFAULT choices of the playable slice (the
/// specs give no starting force numbers); placement is a pure function of the data (no random draw: the only draws are the deal's, then the economy's
/// incident timers in <see cref="EconomyModule.SetupNewGame"/> and the research tick arming in <see cref="Research.ResearchModule.SetupNewGame"/>), so the same options give the same game.
/// Victory is switched on (<see cref="ScenarioState.Active"/>). rule: CLAUDE.md "Undecided rules are switches or data values".
/// </summary>
public static class NewGame
{
    public static GameHost Create(GameData data, NewGameOptions o)
    {
        var nd = o.NewGameData ?? NewGameData.Default;
        var host = GameHost.New((ulong)(uint)o.Seed, o.Settings, data);
        var fleets = host.Fleets; var eco = host.Economy; var galaxy = data.Galaxy;
        var settings = o.Settings ?? new GameSettings();
        // The deal draws first, from the game's one generator, before any later setup draw (characters, economy timers, research).
        // rule: m5-setup-and-victory §Mode Anchored (draws), §P1 Deal (DealMode)
        IReadOnlyList<DayOneRow> rows;
        if (settings.DealMode == DealMode.Anchored)
        {
            if (!nd.Totals.TryGetValue(o.Preset, out var totals)) throw new SetupException($"{BlocsTotals.FileName}: no row for preset '{StartPresets.Key(o.Preset)}'.");
            rows = AnchoredDealer.Deal(galaxy, host.Game.Rng, nd.Anchors, totals, settings.Deal ?? new DealParams()).Rows;
        }
        else rows = nd.DayOne[o.Preset];
        var forces = nd.Forces;
        var state = host.Game.State;

        // ---- owners, support, explored, HQ (setup-victory/09 §1-5). Rows for systems off the graph (Glasswood, Frostholm) are skipped.
        foreach (var r in rows)
        {
            if (!galaxy.TryFindSystem(r.Name, out _)) continue;
            if (r.Owner is Side owner) fleets.SetOwner(r.Name, owner);
            eco.SetCollectiveSupport(r.Name, r.CollectiveSupport);
            int mask = r.ExploredAll ? 3 : r.ExploredOwner && r.Owner is Side s ? ScenarioState.Bit(s) : 0;
            if (mask != 0) state.Scenario.Explored[r.Name] = mask;
            if (r.IsHq) state.Scenario.HqSystem = r.Name;
        }

        // Fixed owners (Decided) on top of the day-one rows, which ignore them. rule: m5-setup-and-victory §Fixed owners: strong holders,
        // so Hand support 60..90 (Collective support 25 here when the row was not already Hand); neutral = 50.
        // File mode only: the anchors file replaces this list in Anchored mode. rule: m5-setup-and-victory §Mode Anchored
        foreach (var (sys, owner) in settings.DealMode == DealMode.File ? nd.FixedOwners : Array.Empty<(string System, Side? Owner)>())
        {
            if (!galaxy.TryFindSystem(sys, out _)) continue;
            if (owner is Side fo)
            {
                if (fleets.OwnerOf(sys) != fo) { fleets.SetOwner(sys, fo); eco.SetCollectiveSupport(sys, fo == Side.Hand ? 25 : 75); }
            }
            else { fleets.SetOwner(sys, null); eco.SetCollectiveSupport(sys, 50); state.Scenario.Explored.Remove(sys); }
        }
        // Support bands that setup fixes (setup-victory/09 §2): Charter 0, The Shoal and the HQ 100.
        eco.SetCollectiveSupport("Charter", 0);
        eco.SetCollectiveSupport("The Shoal", 100);
        if (state.Scenario.HqSystem is { } hq) eco.SetCollectiveSupport(hq, 100);
        state.Scenario.Active = true;

        foreach (var f in data.StartingFacilities)
        {
            var holder = fleets.OwnerOf(f.System);
            if ((f.Side != null && f.Side != holder) || !galaxy.TryFindSystem(f.System, out _)) continue;
            Place(host, f.System, f.Kind);
        }

        foreach (var side in new[] { Side.Hand, Side.Collective })
            DealSide(host, forces, side, settings.MinGarrison);

        // P7 step 2: the fixed HQ forces (CMUNHQTB), completed, standing on the Hearth system (the table's first item is empty, so nothing is a
        // carrier); no capacity check, no draw. With min_garrison 0 they are the HQ's only troops. rule: m5-setup-and-victory §P7 ([EXE-own] 0x51ad90; B4 §7)
        // Behind HqFixedForces (DEFAULT off until the Classic setup, part B, turns it on together with MinGarrison 0).
        if (settings.HqFixedForces && state.Scenario.HqSystem is { } hqSys)
            foreach (var (side, cls, count) in forces.HqUnits)
                for (int i = 0; i < count; i++) eco.AddObject(side, cls, hqSys);

        // The full control refresh at the end of the deal (Classic setup stage 0x17): every holder is worked out from its regiments and support. The
        // garrisons above keep every held world held, and neutral support stays between 41 and 59, so nothing changes (B5a-14).
        // rule: m2-control-and-capture §How control changes (B5a section 9, Setup)
        host.Control.SetupNewGame();

        // P9 starting characters, after the forces and before the economy timers and the research arming (P10): the roster's creation draws,
        // then The Chair at Charter, Ravilla and the officers at random Hand locations, the Hand extras, Mae Abara at the HQ, The Shoal six,
        // the Collective extras. rule: m5-setup-and-victory §P9; m5-characters-and-force §Rolling the stats.
        host.Characters.SetupNewGame(state.Scenario.HqSystem);

        // Stage 0x17: fleets and capital ships get their numbered names, after the characters and before the day-0 pictures. rule: m5-setup-and-victory §P7 Names (B4 §12)
        NameAtSetup(host, forces);

        // Day-0 pictures: explored-for-all systems (A and B), own holdings (OwnLive), the StartFullCopy list. rule: m5-fog-and-hq §F4
        host.Fog.SetupNewGame(rows.Where(r => r.ExploredAll).Select(r => r.Name)
            .Where(n => state.Scenario.Explored.TryGetValue(n, out var m) && m == 3));

        // No research grant: every side starts at level 0 in every track and builds only order-0 classes (the Mender Frigates of the
        // starting fleet stay unbuildable until Ship level 1). rule: m5-research §State per side (no setup path preloads a level), RS1.
        eco.SetupNewGame(); // stockpile, then the two incident timers
        host.Research.SetupNewGame(); // the research percents, then the two research tick arming draws, Collective then Hand. rule: m5-research §Random draws (a)
        if (o.Settings?.Economy == null) // DEFAULT starting stockpile of the playable slice (the economy's own default is 0/0)
            foreach (var side in new[] { Side.Hand, Side.Collective })
                eco.SetPools(side, forces.Int(side, "start_raw"), forces.Int(side, "start_refined"));
        return host;
    }

    public static GameHost Create(NewGameOptions o) => Create(o.Data ?? GameData.Default, o);

    // ------------------------------------------------------------------ one side

    private static List<string> Held(GameHost host, Side side) =>
        host.Game.State.SystemOwners.Where(kv => kv.Value == side).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static void DealSide(GameHost host, ForcesTable forces, Side side, int? minGarrison)
    {
        var eco = host.Economy;
        var held = Held(host, side);
        if (held.Count == 0) return;
        string baseWanted = forces.Str(side, "base_system");
        string baseSys = held.Contains(baseWanted, StringComparer.Ordinal) ? baseWanted : BestEnergy(host, held, null)!;

        // yards at the base, then the extra yard sites (best free energy first)
        foreach (var (s, kind, count) in forces.BaseFacilities)
            if (s == side) for (int i = 0; i < count; i++) PlaceNear(host, held, baseSys, kind);
        var sites = new List<string>();
        // DEFAULT (2026-10-16, v5 galaxy): never the Hearth. The second fleet starts at the first site, and a fleet parked on the HQ both
        // gives the HQ away and gets kept there as home defence by the AI (the v5 deal put the richest Collective system on the HQ).
        string? hqSys = host.Game.State.Scenario.HqSystem;
        for (int n = 0, extra = forces.Int(side, "extra_yard_sites"); n < extra; n++)
        {
            var siteOrNull = BestEnergy(host, held.Where(h => h != baseSys && h != hqSys && !sites.Contains(h)).ToList(), null);
            if (siteOrNull is not { } site) break;
            sites.Add(site);
            foreach (var (s, kind, count) in forces.SiteFacilities)
                if (s == side && CountOf(eco, site, kind) == 0) for (int i = 0; i < count; i++) PlaceNear(host, held, site, kind);
        }

        // economy: mines and refineries on the systems richest in raw slots
        int mines = forces.Int(side, "mines_per_system"), refs = forces.Int(side, "refineries_per_system");
        foreach (var sys in held.Where(h => eco.EffectiveRaw(h) > 0).OrderByDescending(h => eco.EffectiveRaw(h)).ThenBy(h => h, StringComparer.Ordinal)
                     .Take(ScaledEconomySystems(host, forces.Int(side, "economy_systems"))).ToList())
        {
            for (int i = 0; i < mines; i++) if (eco.CheckPlace(sys, FacilityKind.Mine) == null) eco.AddFacility(sys, FacilityKind.Mine);
            for (int i = 0; i < refs; i++) if (eco.CheckPlace(sys, FacilityKind.Refinery) == null) eco.AddFacility(sys, FacilityKind.Refinery);
        }

        // garrison: the sheet's required N for the holder's support (N = ceil((60 - s) / 10) below 60, else 0), at least min_garrison (Classic = 0,
        // B4 §6: worlds at 60 or more, Charter, The Shoal and the HQ get none; 0 only with the Classic setup, part B, so 1 until then); extra regiments at the base to load and send
        // rule: m5-setup-and-victory §P6 ([EXE-own] 0x51ab20); m2-uprisings-and-garrison §Required garrison N (no system starts in uprising)
        string gClass = forces.Str(side, "garrison_class"); int minG = minGarrison ?? forces.Int(side, "min_garrison");
        foreach (var sys in held)
        {
            int n = Math.Max(minG, SupportModule.RequiredGarrison(host.Support.Params, eco.SupportOf(sys, side), false, false));
            for (int i = 0; i < n; i++) eco.AddObject(side, gClass, sys);
        }
        for (int i = 0, n = forces.Int(side, "base_regiments"); i < n; i++) eco.AddObject(side, forces.Str(side, "base_regiment_class"), baseSys);

        // fleets: in file order while the maintenance load stays within fill_percent of the capacity
        int budget = eco.Capacity(side) * forces.Int(side, "fill_percent") / 100;
        int load = eco.Load(side);
        string siteSys = sites.Count > 0 ? sites[0] : baseSys;
        foreach (var f in forces.Fleets.Where(f => f.Side == side))
        {
            int shipCost = f.Ships.Sum(c => eco.Costs.Get(c).Maintenance);
            if (load + shipCost > budget) continue;
            int berths = f.Ships.Sum(c => host.Data.Ships[c].FighterCapacity);
            var squads = f.Squadrons.Take(berths).ToList();
            if (load + shipCost + squads.Sum(c => eco.Costs.Get(c).Maintenance) > budget) squads.Clear();
            load += shipCost + squads.Sum(c => eco.Costs.Get(c).Maintenance);
            host.Fleets.AddFleet(side, f.Name, f.Ships, f.AtBase ? baseSys : siteSys, squads, listedOrder: true);
        }
    }

    /// <summary>
    /// Names every fleet "&lt;fleet_name&gt; n" (n counting per side from 1, fleet id order) and every capital ship "&lt;class&gt; n" (n counting per
    /// side and class from 1, object id order). The base names are data: <c>fleet_name</c> of new-game-forces.csv ("Fleet", FLEETSD id 4) and
    /// the class name of the ship data. Display only; no draw. rule: m5-setup-and-victory §P7 Names (B4 §12, [EXE-own] 0x51b7f0, 0x517760).
    /// </summary>
    internal static void NameAtSetup(GameHost host, ForcesTable forces)
    {
        var state = host.Game.State;
        var fleetCount = new SortedDictionary<Side, int>();
        foreach (var f in state.Fleets.Values.OrderBy(f => f.Id))
        {
            int n = fleetCount[f.Side] = fleetCount.GetValueOrDefault(f.Side) + 1;
            f.Name = $"{forces.Str(f.Side, "fleet_name")} {n}";
        }
        var shipCount = new SortedDictionary<(Side, string), int>();
        foreach (var o in state.Economy.Objects.Values.OrderBy(o => o.Id))
        {
            if (o.Category != ObjectCategory.CapitalShip || o.Side is not Side side) continue;
            int n = shipCount[(side, o.Class)] = shipCount.GetValueOrDefault((side, o.Class)) + 1;
            o.Name = $"{o.Class} {n}";
        }
    }

    /// <summary>The forces table's economy_systems is written for this many systems in sectors (the 360-system galaxy of 2026-10-12). DEFAULT.</summary>
    public const int EconomyBaseSystems = 360;

    /// <summary>
    /// economy_systems scaled to the galaxy's size, rounded half up, like the day-one totals (AnchoredDealer.TotalsBaseSystems): holdings and
    /// garrisons grew with the 555-system v5 galaxy, so the mines and refineries (the upkeep capacity) grow with them. DEFAULT (2026-10-16, v5):
    /// without it the Hand's 166 garrison regiments ate a third of an unchanged capacity of 1400.
    /// </summary>
    private static int ScaledEconomySystems(GameHost host, int perBase)
    {
        int inSectors = host.Data.Galaxy.Systems.Count(x => !x.IsOutsideSectors);
        return (int)(((long)perBase * inSectors * 2 + EconomyBaseSystems) / (2L * EconomyBaseSystems));
    }

    private static int CountOf(EconomyModule eco, string system, string kindName) =>
        eco.Objects.Count(x => x.Category == ObjectCategory.Facility && x.System == system && x.Class == kindName);

    /// <summary>The system of <paramref name="candidates"/> with the most free energy slots (ties by name), or null.</summary>
    private static string? BestEnergy(GameHost host, List<string> candidates, FacilityKind? _) =>
        candidates.OrderByDescending(h => host.Economy.FreeEnergy(h)).ThenBy(h => h, StringComparer.Ordinal).FirstOrDefault();

    private static void PlaceNear(GameHost host, List<string> held, string preferred, string kindName)
    {
        if (!FacilityKinds.TryParse(kindName, out var kind)) throw new InvalidOperationException($"new-game-forces.csv: unknown facility '{kindName}'.");
        if (host.Economy.CheckPlace(preferred, kind) == null) { host.Economy.AddFacility(preferred, kind); return; }
        foreach (var sys in held.OrderByDescending(h => host.Economy.FreeEnergy(h)).ThenBy(h => h, StringComparer.Ordinal))
            if (host.Economy.CheckPlace(sys, kind) == null) { host.Economy.AddFacility(sys, kind); return; }
    }

    private static void Place(GameHost host, string system, FacilityKind kind)
    {
        if (host.Economy.CheckPlace(system, kind) == null) host.Economy.AddFacility(system, kind);
    }
}
