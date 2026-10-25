using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Support;
using Praedra.Core.Tests.Economy;

namespace Praedra.Core.Tests.Support;

/// <summary>An <see cref="EcoWorld"/> with the support module attached (real garrison weights file), plus short helpers.</summary>
internal sealed class SupWorld
{
    public static readonly Lazy<GarrisonWeights> Weights = new(() => GarrisonWeights.Load(Path.Combine(TestData.DesignDir, GarrisonWeights.FileName)));

    public EcoWorld W { get; }
    public SupportModule Sup { get; }
    public Game Game => W.Game;
    public StubRng Stub => W.Stub!;

    public SupWorld(GarrisonRuleset ruleset = GarrisonRuleset.Classic, bool stub = true, ulong seed = 1)
    {
        // Autoscrap is pushed out of reach so test garrisons are not scrapped for maintenance (that is the maintenance sheet's business).
        var p = new EconomyParams { ScrapPeriod = 1_000_000 };
        W = stub ? new EcoWorld(false, p) : new EcoWorld(seed, false, p);
        Sup = SupportModule.Attach(W.Game, TestData.RealGalaxy, W.Eco, W.Fleets, Weights.Value);
        Sup.Ruleset = ruleset;
    }

    public void Run(int steps) => W.Run(steps);
    public List<T> Events<T>() where T : GameEvent => W.Events<T>();

    /// <summary>Hold a system and set the holder's own support.</summary>
    public void Hold(string system, Side side, int holderSupport) => W.Hold(system, side, holderSupport);

    public int A(string system) => W.Eco.SupportOf(system, Side.Alliance);

    public void Regiments(string system, Side side, string cls, int n)
    {
        for (int i = 0; i < n; i++) W.Eco.AddObject(side, cls, system);
    }

    public static string ArmyOf(Side side) => side == Side.Empire ? "Tranches" : "Militia";

    /// <summary>Set an uprising running by hand (the sheet's tests start from "uprising on"). Rebels = the other side than the holder.</summary>
    public SystemSupportState Uprising(string system, Side holder, int erosionDue = 0, int incidentDue = 0)
    {
        var st = new SystemSupportState { Uprising = true, Rebels = holder == Side.Empire ? Side.Alliance : Side.Empire, ErosionDue = erosionDue, IncidentDue = incidentDue };
        Game.State.Support.Systems[system] = st;
        return st;
    }

    public void InStep(Action<StepContext> a) => W.InStep(a);
}
