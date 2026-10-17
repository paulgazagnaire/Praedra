using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Economy;
using Rebellion.Core.Map;
using Rebellion.Core.Missions;
using Rebellion.Core.Setup;
using Rebellion.Core.Tests.Economy;

namespace Rebellion.Core.Tests.Missions;

/// <summary>
/// A full <see cref="GameHost"/> on a small map for the mission tests (m5-missions §Tests): H (0,20), T (15,20), X (50,20), Alderaan (15,40),
/// Bespin (15,0), group core; lanes H-T (3 ticks at speed 100), T-X (7 ticks), T-Alderaan and T-Bespin (4 ticks), all tier 3 (factor 100).
/// No owners unless set. A stub generator answers 0 unless a value is queued for the exact range (<see cref="StubRng.Expect"/>), so the
/// mission rolls (0..99) and the timers (0..spread) are steered separately. Incidents off and autoscrap out of reach.
/// </summary>
internal sealed class MissionWorld
{
    public const string H = "H", T = "T", X = "X", Alderaan = "Alderaan", Bespin = "Bespin";
    // character record ids (data/original/characters.csv)
    public const int MonMothma = 576, Leia = 577, Luke = 578, Han = 579, Palpatine = 640, Vader = 641, Wedge = 833, Lando = 834, Piett = 898, Veers = 899;
    // regiment classes and their detection (data/original/troop-regiments.csv)
    public const string ImpArmy = "Imperial Army Regiment" /*15*/, ImpFleet = "Imperial Fleet Regiment" /*20*/, Storm = "Stormtrooper Regiment" /*25*/,
        DarkTrooper = "Dark Trooper Regiment" /*30*/, WarDroid = "War Droid Regiment" /*5*/, RebArmy = "Alliance Army Regiment" /*10*/;
    public const string Bothan = "Bothan Spies", Infiltrators = "Infiltrators", Guerrillas = "Guerrillas", Longprobe = "Longprobe Y-wing Recon Team",
        Probe = "Imperial Probe Droid", Commandos = "Imperial Commandos", Noghri = "Noghri Death Commandos", Hunters = "Bounty Hunters";

    /// <summary>
    /// B2b: every check of a Foilable kind opens with one betrayal draw per member (team, then decoys), made even at chance 0 (loyalty 100). The
    /// value does not matter at loyalty 100; 99 is used. Returns <paramref name="members"/> such draws followed by <paramref name="rolls"/>.
    /// rule: m5-missions §3 Betrayal.
    /// </summary>
    public static int[] Bt(int members, params int[] rolls) => Enumerable.Repeat(99, members).Concat(rolls).ToArray();

    /// <summary>The synthetic decoy table of the sheet's tests: one row 0: 50.</summary>
    public static readonly TableRow[] DT = { new(0, 50) };

    private static readonly Lazy<GameData> _data = new(() =>
    {
        var planets = "name,sector,group,map_x,map_y\nH,S,core,0,20\nT,S,core,15,20\nX,S,core,50,20\nAlderaan,S,core,15,40\nBespin,S,core,15,0\n";
        var lanes = "from,to,kind,routes\nH,T,generated,\nT,X,generated,\nT,Alderaan,generated,\nT,Bespin,generated,\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},9,3,1"));
        return GameData.Default with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    });
    private static readonly Lazy<GameData> _decoyData = new(() => _data.Value with { Missions = _data.Value.Missions.WithDecoyTable(DT) });

    public static GameData Data => _data.Value;
    public static GameData DecoyData => _decoyData.Value;

    public StubRng? Stub { get; }
    public GameHost Host { get; private set; }
    public Game Game => Host.Game;
    public MissionModule M => Host.Missions;
    public CharacterModule Ch => Host.Characters;
    private readonly GameSettings _settings;
    private readonly GameData _gd;

    public MissionWorld(MissionParams? p = null, bool decoys = false, ulong? seed = null, IReadOnlyList<TableRow>? decoyTable = null,
        CharacterParams? characters = null)
    {
        _settings = new GameSettings { IncidentsEnabled = false, DebugOrdersEnabled = true, Economy = new EconomyParams { ScrapPeriod = 1_000_000 }, Missions = p,
            Characters = characters };
        _gd = decoyTable != null ? Data with { Missions = Data.Missions.WithDecoyTable(decoyTable) } : decoys ? DecoyData : Data;
        if (seed is ulong s) Host = GameHost.New(s, _settings, _gd);
        else { Stub = new StubRng(); Host = GameHost.New(Stub, _settings, _gd); }
    }

    /// <summary>Create a character (no game draw), place it, and set its skills: every skill 0 except those given.</summary>
    public CharacterRecord Agent(int id, string system, int esp = 0, int com = 0, int lead = 0, int dip = 0, int ship = 0, int troop = 0, int fac = 0)
    {
        var c = Ch.Get(id) ?? Ch.Create(Ch.Data.Get(id), new StubRng());
        Ch.Place(id, system);
        c.Skills[(int)Skill.Espionage] = esp; c.Skills[(int)Skill.Combat] = com; c.Skills[(int)Skill.Leadership] = lead;
        c.Skills[(int)Skill.Diplomacy] = dip; c.Skills[(int)Skill.ShipDesign] = ship; c.Skills[(int)Skill.TroopTraining] = troop;
        c.Skills[(int)Skill.FacilityDesign] = fac;
        return c;
    }

    public int Unit(Side side, string cls, string system, bool completed = true) => Host.Economy.AddObject(side, cls, system, completed);

    /// <summary>Owner and Alliance support of a system.</summary>
    public void Hold(string system, Side? side, int allianceSupport)
    {
        Host.Fleets.SetOwner(system, side);
        Host.Economy.SetAllianceSupport(system, allianceSupport);
    }

    public long Submit(Func<long, Order> make) => Game.Submit(make);

    public long Start(Side side, MissionKind kind, IEnumerable<int> chars, string? target, IEnumerable<int>? sfs = null, IEnumerable<int>? decoys = null,
        int? targetChar = null, int? facility = null, IEnumerable<int>? decoyChars = null) =>
        Submit(seq => new StartMissionOrder(seq, side, kind, chars.ToList(), (sfs ?? Array.Empty<int>()).ToList(), (decoys ?? Array.Empty<int>()).ToList(),
            target, targetChar, facility, decoyChars?.ToList()));

    public string? Reason(long seq) => Game.Events.OfType<OrderRejected>().FirstOrDefault(e => e.Sequence == seq)?.Reason;

    /// <summary>Submit an order, Step once, return its rejection reason (null = accepted).</summary>
    public string? Try(Func<long, Order> make) { long s = Submit(make); Game.Step(); return Reason(s); }

    /// <summary>The id of the mission started by the last accepted StartMission or MoveGroup.</summary>
    public int LastMission => Game.Events.OfType<MissionStarted>().Last().Mission;

    public void Run(int steps) { for (int i = 0; i < steps; i++) Game.Step(); }
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    /// <summary>Step until the mission has made one more iteration or has left the Working phase (at most 200 Steps).</summary>
    public void RunIteration(int mission)
    {
        int before = M.Get(mission)?.Iterations ?? 0;
        for (int i = 0; i < 200; i++)
        {
            var m = M.Get(mission);
            if (m == null || m.Phase is MissionPhase.ReturnPending or MissionPhase.Returning) return;
            if (m.Phase == MissionPhase.Working && m.Iterations > before) return;
            Game.Step();
        }
        throw new InvalidOperationException("The mission did not iterate in 200 Steps.");
    }

    /// <summary>Requests made to the stub since index <paramref name="from"/>.</summary>
    public List<(int Min, int Max)> RequestsSince(int from) => Stub!.Requests.Skip(from).ToList();

    /// <summary>Save, load and attach again (same settings and data).</summary>
    public void Reload() => Host = GameHost.Load(Host.Save(), _settings, _gd);
}
