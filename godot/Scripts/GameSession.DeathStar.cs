using System;
using System.Collections.Generic;
using System.Linq;
using Praedra.Core.Clock;
using Praedra.Core.DeathStar;
using Praedra.Core.Fleets;
using Praedra.Core.Missions;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The Death Star as the screens see it (rule: m5-death-star): reading the core, queueing its order, and turning its events into plain lines.
/// No rule lives here; every decision is the core's (<see cref="Praedra.Core.DeathStar.DeathStarModule"/>).
/// </summary>
public sealed partial class GameSession
{
    // The day each destroyed system was struck, as the player's side was told (events only; a loaded game does not know it).
    readonly Dictionary<string, int> _destroyedDay = new(StringComparer.Ordinal);
    string _enemyDeathStarAt; // where the last "New Death Star Deployed" notice put the enemy's Death Star (it may have moved since)

    /// <summary>Does the player's side know the system was destroyed by the Death Star? From the side's own picture (both sides are told at once). rule: m5-death-star §2 effect 6.</summary>
    public bool IsDestroyed(string system) => Picture(system)?.Destroyed == true;

    /// <summary>The day the system was destroyed, when this session saw the notice; null otherwise (a loaded game).</summary>
    public int? DestroyedDay(string system) => _destroyedDay.TryGetValue(system, out int d) ? d : null;

    /// <summary>The words for the panel and the hover line.</summary>
    public string DestroyedWords(string system) =>
        DestroyedDay(system) is int d ? $"Destroyed by the Death Star on day {d}" : "Destroyed";

    /// <summary>Does this fleet hold a completed Death Star? (The core's own test.)</summary>
    public bool HoldsDeathStar(Fleet f) =>
        f.Ships.Any(id => Host.Economy.Find(id) is { Completed: true } o && o.Class == DeathStarModule.ShipClass);

    /// <summary>Is one of the player's Death Stars in a fleet at the system?</summary>
    public bool HasOwnDeathStarAt(string system) => Host.DeathStar.HasDeathStar(Player, system);

    /// <summary>Queue Destroy System for the fleet. The core judges it when the next day starts; a refusal comes back in the messages.</summary>
    public long SubmitDestroySystem(Fleet f) =>
        TrackFleet(f.Id, Host.DeathStar.SubmitDestroySystem(Player, f.Id), $"Destroy System {f.At} with {f.Name}");

    /// <summary>For the sabotage planner: where the enemy Death Star was last reported (null when never, or since destroyed).</summary>
    public string EnemyDeathStarNote =>
        _enemyDeathStarAt is string at
            ? $"The last report put the enemy Death Star at {at}. It moves slowly, but it may have left: the team must go where it stands now."
            : "No enemy Death Star has been reported yet. The team must go to the system where it stands.";

    static string DeathStarLossWords(string cause) => cause switch
    {
        "Battle" => "in battle",
        "Sabotage" => "by sabotage",
        "DeathStar" => "with a system another Death Star destroyed",
        _ => cause,
    };

    IEnumerable<Message> DescribeDeathStar(GameEvent e)
    {
        int d = e.Day;
        switch (e)
        {
            case DeathStarDeployed x when Mine(x.Side):
                yield return new Message(d, $"New Death Star ready at {x.System}. Select its fleet and press Destroy System to use it.", x.System, MsgKind.Good);
                break;
            case DeathStarDeployed x:
                _enemyDeathStarAt = x.System;
                yield return new Message(d, $"New Death Star deployed by the {NameOf(x.Side)} at {x.System}!", x.System, MsgKind.Bad);
                break;
            case DeathStarDestroyed x:
            {
                bool mine = Mine(x.Side);
                if (!mine) _enemyDeathStarAt = null;
                string at = x.System == null ? "" : $" at {x.System}";
                yield return new Message(d, mine ? $"Your Death Star was destroyed{at} ({DeathStarLossWords(x.Cause)})." : $"The enemy Death Star was destroyed{at} ({DeathStarLossWords(x.Cause)}).",
                    x.System, mine ? MsgKind.Bad : MsgKind.Good);
                break;
            }
            case SystemDestroyed x:
            {
                _destroyedDay[x.System] = d;
                string text = x.AttackerSide == Player ? $"Your Death Star destroyed {x.System}."
                    : x.PriorHolder == Player ? $"The Death Star destroyed {x.System}, one of your systems."
                    : $"The Death Star destroyed {x.System}.";
                var kind = x.AttackerSide == Player ? MsgKind.Info : x.PriorHolder == Player ? MsgKind.Bad : MsgKind.Warn;
                yield return new Message(d, text, x.System, kind);
                break;
            }
            case FleetDestroyedByDeathStar x:
                yield return new Message(d, x.FleetSide == Player ? $"Your fleet {FleetName(x.Fleet)} was destroyed at {x.System} by the Death Star."
                    : $"The {NameOf(x.FleetSide)} fleet at {x.System} was destroyed with the system.", x.System, x.FleetSide == Player ? MsgKind.Bad : MsgKind.Good);
                break;
            case SectorStruck x:
                if (x.Strike <= 1)
                    yield return new Message(d, $"The {x.Sector} turns against the Empire: {x.Systems.Items.Count()} system(s) there lose support for it. A second strike in this sector would turn it all to the Alliance.",
                        null, Player == Side.Empire ? MsgKind.Bad : MsgKind.Good);
                else
                    yield return new Message(d, $"Revolt in the {x.Sector}! Every system left there now backs the Alliance, and the Imperial ones rise up.",
                        null, Player == Side.Empire ? MsgKind.Bad : MsgKind.Good);
                break;
            case DestroySystemCancelled x when Mine(x.Side):
                yield return new Message(d, $"Destroy System at {x.System} by {FleetName(x.Fleet)} was cancelled ({Why(x.Reason)}).", x.System, MsgKind.Warn);
                break;
        }
    }
}
