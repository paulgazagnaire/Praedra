using Praedra.Core.Construction;

namespace Praedra.Core.AI;

public sealed partial class SimpleAi
{
    // ================================================================== per-pass caches (QA senior QS-1 speed, QS-5)

    /// <summary>
    /// Reads that cannot change while the AI plans, kept for one <see cref="PlanDay"/> or <see cref="PlanAdvisor"/>: the AI never changes the
    /// state (its orders wait in the queue until Game.Step), so one question gets one answer all through a planning pass. Made fresh at the
    /// start of each pass and dropped at its end; outside a pass there is none and every read is live. Speed only: the answers are the ones
    /// the live reads give.
    /// </summary>
    private sealed class PassCache
    {
        /// <summary>The side's held systems, ordinal order (<see cref="HeldSystems"/>).</summary>
        public List<string>? Held;
        /// <summary>The side's free upkeep room, EconomyModule.Free (the planned load of the pass is not in it).</summary>
        public int? Free;
        /// <summary>Held working yard systems by family, ordinal order (<see cref="YardSystems"/>). Lookup only.</summary>
        public readonly Dictionary<YardFamily, List<string>> Yards = new();
        /// <summary>The systems worth a scout's look today with their ScoutValue (<see cref="ScoutCandidates"/>).</summary>
        public List<(string System, int Value)>? Scouting;
    }

    private PassCache? _pass;

    private void BeginPass() => _pass = new PassCache();
    private void EndPass() => _pass = null;

    /// <summary>EconomyModule.Free for the side, once per pass.</summary>
    private int FreeRoom() => _pass is { } c ? c.Free ??= _h.Economy.Free(_side) : _h.Economy.Free(_side);

    /// <summary>
    /// The systems a scout may be sent to look at, galaxy order, with their ScoutValue: not held, not destroyed, value above 0. The same for
    /// every scout of the pass (ScoutValue reads only the side's picture of the day); each scout then leaves out its own system and the claimed ones.
    /// </summary>
    private List<(string System, int Value)> ScoutCandidates()
    {
        if (_pass?.Scouting is { } hit) return hit;
        var hops = Hops(); var list = new List<(string, int)>();
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            string s = sys.Name;
            if (Held(s) || Destroyed(s)) continue;
            int value = ScoutValue(s, hops[sys.Index]);
            if (value > 0) list.Add((s, value));
        }
        if (_pass != null) _pass.Scouting = list;
        return list;
    }
}
