using Praedra.Core.Clock;
using Praedra.Core.Fog;
using Praedra.Core.Map;
using Praedra.Core.Setup;

namespace Praedra.Core.Tests.Fog;

/// <summary>
/// Map M of the fog sheet (m5-fog-and-hq §Tests): C0 (0,0) group core, sector SC; R1 (100,0), R2 (200,0), R3 (300,0), R4 (400,0) group
/// outer_rim, sector SR; lanes C0-R1, R1-R2, R2-R3, R3-R4, tier 3. Glasswood has no lane (an outsider, as in the real data). Day 0: Hand holds
/// C0 and R1, Collective holds R2, R3, R4; start_explored_all = C0; HQ at R2 (or as given); Hand fleet E1 of 3 Mandates at
/// R1; Collective support 70 at R2, R3, R4 (and 30 at C0 and R1). Every system is populated. A full GameHost on this map with debug orders on.
/// </summary>
internal sealed class FogWorld
{
    public const string C0 = "C0", R1 = "R1", R2 = "R2", R3 = "R3", R4 = "R4", Glasswood = "Glasswood";
    public const string Mandate = "Mandate", Corvette = "Dodger", Gleaner = "Gleaner";

    private static readonly Lazy<GameData> _data = new(() =>
    {
        var planets = "name,sector,group,map_x,map_y\n" +
                      "C0,SC,core,0,0\nR1,SR,outer_rim,100,0\nR2,SR,outer_rim,200,0\nR3,SR,outer_rim,300,0\nR4,SR,outer_rim,400,0\n" +
                      $"Glasswood,{Galaxy.OutsideSectorsLabel},,0,300\n";
        var lanes = "from,to,kind,routes\nC0,R1,generated,\nR1,R2,generated,\nR2,R3,generated,\nR3,R4,generated,\n";
        var g = GalaxyLoader.Load(planets, lanes, TestData.Tiers);
        var slots = "name,sector,group,energy,raw,populated\n" + string.Join("\n", g.Systems.Select(s => $"{s.Name},{s.Sector},{s.Group},5,3,1"));
        return GameData.Default with { Galaxy = g, Slots = SlotValues.Parse(slots, g) };
    });

    public static GameData Data => _data.Value;

    public GameHost H { get; }
    public Game Game => H.Game;
    public FogModule Fog => H.Fog;
    public int E1 { get; }

    public FogWorld(GameSettings? settings = null, string? hq = R2, bool r3Neutral = false, bool r4Neutral = false, ulong seed = 1,
        Action<FogWorld>? beforeSetup = null, Praedra.Core.Travel.TravelMode travel = Praedra.Core.Travel.TravelMode.Lanes)
    {
        var s = (settings ?? new GameSettings()) with { DebugOrdersEnabled = true, Travel = travel }; // Map M is a hand-made lane map: keep its hop-by-hop paths (Lanes) unless a test asks for another mode
        // No mines or refineries on Map M: keep the autoscrap (m2-maintenance) from scrapping the test fleets.
        if (s.Economy == null) s = s with { Economy = new Praedra.Core.Economy.EconomyParams { ScrapPeriod = 1_000_000 } };
        H = GameHost.New(seed, s, Data);
        Hold(C0, Side.Hand); Hold(R1, Side.Hand);
        Hold(R2, Side.Collective);
        if (!r3Neutral) Hold(R3, Side.Collective); else H.Economy.SetCollectiveSupport(R3, 50);
        if (!r4Neutral) Hold(R4, Side.Collective); else H.Economy.SetCollectiveSupport(R4, 50);
        Fog.PlaceHq(hq);
        E1 = H.Fleets.AddFleet(Side.Hand, "E1", new[] { Mandate, Mandate, Mandate }, R1);
        beforeSetup?.Invoke(this);
        Fog.SetupNewGame(new[] { C0 });
    }

    private void Hold(string sys, Side side)
    {
        H.Fleets.SetOwner(sys, side);
        H.Economy.SetCollectiveSupport(sys, side == Side.Collective ? 70 : 30);
    }

    public int Fleet(Side side, string system, string cls, int n, int squadrons = 0) =>
        H.Fleets.AddFleet(side, $"{side} {cls} x{n}", Enumerable.Repeat(cls, n), system, squadrons > 0 ? Enumerable.Repeat(Gleaner, squadrons) : null);

    public void Step(int n = 1) { for (int i = 0; i < n; i++) Game.Step(); }
    public void StepTo(int day) { while (Game.Day < day) Game.Step(); }

    public SystemKnowledge? Know(Side side, string sys) => Game.State.Fog.Of(side).Systems.TryGetValue(sys, out var k) ? k : null;
    public SystemPicture? Pic(Side side, string sys) => Fog.Picture(side, sys);
    public HqState Hq => Game.State.Scenario.Hq;
    public IReadOnlyList<GameEvent> EventsFor(Side side) => Fog.EventsFor(side);
    public List<T> Events<T>() where T : GameEvent => Game.Events.OfType<T>().ToList();

    /// <summary>Run <paramref name="a"/> inside the next Step at slot 6.8 (after the live refresh), so it may call methods that need a StepContext.</summary>
    public void InStep(Action<StepContext> a)
    {
        bool done = false;
        Game.RegisterHook("6.8", ctx => { if (!done) { done = true; a(ctx); } });
    }

    /// <summary>Submit, run one Step, return the rejection reason of that order (null = accepted).</summary>
    public string? TryMoveHq(Side side, string dest)
    {
        long seq = Fog.SubmitMoveHq(side, dest);
        int from = Game.Events.Count;
        Step();
        return Game.Events.Skip(from).OfType<OrderRejected>().FirstOrDefault(r => r.Sequence == seq)?.Reason;
    }
}
