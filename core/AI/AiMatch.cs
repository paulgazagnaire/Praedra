using Rebellion.Core.Clock;
using Rebellion.Core.Setup;

namespace Rebellion.Core.AI;

/// <summary>
/// Drives one game with a <see cref="SimpleAi"/> on each side: every day both plan, then the game steps. Also counts the orders sent and
/// the orders the core rejected, by kind and reason (for the runner and the tests). It only reads state and events.
/// </summary>
public sealed class AiMatch
{
    public GameHost Host { get; }
    public SimpleAi Empire { get; }
    public SimpleAi Alliance { get; }
    /// <summary>Orders queued, by "Side Kind".</summary>
    public SortedDictionary<string, int> Sent { get; } = new(StringComparer.Ordinal);
    /// <summary>Orders rejected, by "Side Kind Reason".</summary>
    public SortedDictionary<string, int> Rejected { get; } = new(StringComparer.Ordinal);
    public int TotalSent { get; private set; }
    public int TotalRejected { get; private set; }

    private readonly Dictionary<long, string> _kinds = new(); // lookup only
    private int _eventsSeen;

    /// <summary>A side that gives no orders at all (a scripted passive player), or null when both sides play.</summary>
    public Side? Passive { get; }

    public AiMatch(GameHost host, SimpleAiParams? empire = null, SimpleAiParams? alliance = null, Side? passive = null)
    {
        Host = host; Passive = passive;
        Empire = new SimpleAi(host, Side.Empire, empire);
        Alliance = new SimpleAi(host, Side.Alliance, alliance);
        _eventsSeen = host.Game.Events.Count;
    }

    public void Step()
    {
        _kinds.Clear();
        foreach (var ai in new[] { Empire, Alliance })
        {
            if (ai.Side == Passive) continue;
            ai.PlanDay();
            foreach (var o in ai.Submitted)
            {
                string key = $"{ai.Side} {o.Kind}";
                Sent[key] = Sent.GetValueOrDefault(key) + 1; TotalSent++;
                _kinds[o.Sequence] = key;
            }
        }
        Host.Game.Step();
        var ev = Host.Game.Events;
        for (; _eventsSeen < ev.Count; _eventsSeen++)
            if (ev[_eventsSeen] is OrderRejected r && _kinds.TryGetValue(r.Sequence, out var k))
            {
                string key = $"{k} {r.Reason}";
                Rejected[key] = Rejected.GetValueOrDefault(key) + 1; TotalRejected++;
            }
    }

    public void RunTo(int day) { while (Host.Game.Day < day) Step(); }
}
