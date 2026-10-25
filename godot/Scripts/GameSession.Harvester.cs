using System;
using System.Collections.Generic;
using System.Linq;
using Praedra.Core.Clock;
using Praedra.Core.Harvester;
using Praedra.Core.Fleets;
using Praedra.Core.Missions;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The harvester as the screens see it (rule: m5-harvester): reading the core, queueing its order, and turning its events into plain lines.
/// No rule lives here; every decision is the core's (<see cref="Praedra.Core.Harvester.HarvesterModule"/>).
/// </summary>
public sealed partial class GameSession
{
    // The day each destroyed system was struck, as the player's side was told (events only; a loaded game does not know it).
    readonly Dictionary<string, int> _destroyedDay = new(StringComparer.Ordinal);
    string _enemyHarvesterAt; // where the last "New harvester Deployed" notice put the enemy's harvester (it may have moved since)

    /// <summary>Does the player's side know the system was destroyed by the harvester? From the side's own picture (both sides are told at once). rule: m5-harvester §2 effect 6.</summary>
    public bool IsDestroyed(string system) => Picture(system)?.Destroyed == true;

    /// <summary>The day the system was destroyed, when this session saw the notice; null otherwise (a loaded game).</summary>
    public int? DestroyedDay(string system) => _destroyedDay.TryGetValue(system, out int d) ? d : null;

    /// <summary>The words for the panel and the hover line.</summary>
    public string DestroyedWords(string system) =>
        DestroyedDay(system) is int d ? $"Destroyed by the harvester on day {d}" : "Destroyed";

    /// <summary>Does this fleet hold a completed harvester? (The core's own test.)</summary>
    public bool HoldsHarvester(Fleet f) =>
        f.Ships.Any(id => Host.Economy.Find(id) is { Completed: true } o && o.Class == HarvesterModule.ShipClass);

    /// <summary>Is one of the player's harvesters in a fleet at the system?</summary>
    public bool HasOwnHarvesterAt(string system) => Host.Harvester.HasHarvester(Player, system);

    /// <summary>Queue Destroy System for the fleet. The core judges it when the next day starts; a refusal comes back in the messages.</summary>
    public long SubmitDestroySystem(Fleet f) =>
        TrackFleet(f.Id, Host.Harvester.SubmitDestroySystem(Player, f.Id), $"Destroy System {f.At} with {f.Name}");

    /// <summary>For the sabotage planner: where the enemy harvester was last reported (null when never, or since destroyed).</summary>
    public string EnemyHarvesterNote =>
        _enemyHarvesterAt is string at
            ? $"The last report put the enemy harvester at {at}. It moves slowly, but it may have left: the team must go where it stands now."
            : "No enemy harvester has been reported yet. The team must go to the system where it stands.";

    static string HarvesterLossWords(string cause) => cause switch
    {
        "Battle" => "in battle",
        "Sabotage" => "by sabotage",
        "Harvester" => "with a system another harvester destroyed",
        _ => cause,
    };

    IEnumerable<Message> DescribeHarvester(GameEvent e)
    {
        int d = e.Day;
        switch (e)
        {
            case HarvesterDeployed x when Mine(x.Side):
                yield return new Message(d, $"New harvester ready at {x.System}. Select its fleet and press Destroy System to use it.", x.System, MsgKind.Good);
                break;
            case HarvesterDeployed x:
                _enemyHarvesterAt = x.System;
                yield return new Message(d, $"New harvester deployed by the {NameOf(x.Side)} at {x.System}!", x.System, MsgKind.Bad);
                break;
            case HarvesterDestroyed x:
            {
                bool mine = Mine(x.Side);
                if (!mine) _enemyHarvesterAt = null;
                string at = x.System == null ? "" : $" at {x.System}";
                yield return new Message(d, mine ? $"Your harvester was destroyed{at} ({HarvesterLossWords(x.Cause)})." : $"The enemy harvester was destroyed{at} ({HarvesterLossWords(x.Cause)}).",
                    x.System, mine ? MsgKind.Bad : MsgKind.Good);
                break;
            }
            case SystemDestroyed x:
            {
                _destroyedDay[x.System] = d;
                string text = x.AttackerSide == Player ? $"Your harvester destroyed {x.System}."
                    : x.PriorHolder == Player ? $"The harvester destroyed {x.System}, one of your systems."
                    : $"The harvester destroyed {x.System}.";
                var kind = x.AttackerSide == Player ? MsgKind.Info : x.PriorHolder == Player ? MsgKind.Bad : MsgKind.Warn;
                yield return new Message(d, text, x.System, kind);
                break;
            }
            case FleetDestroyedByHarvester x:
                yield return new Message(d, x.FleetSide == Player ? $"Your fleet {FleetName(x.Fleet)} was destroyed at {x.System} by the harvester."
                    : $"The {NameOf(x.FleetSide)} fleet at {x.System} was destroyed with the system.", x.System, x.FleetSide == Player ? MsgKind.Bad : MsgKind.Good);
                break;
            case SectorStruck x:
                if (x.Strike <= 1)
                    yield return new Message(d, $"The {x.Sector} turns against the Hand: {x.Systems.Items.Count()} system(s) there lose support for it. A second strike in this sector would turn it all to the Collective.",
                        null, Player == Side.Hand ? MsgKind.Bad : MsgKind.Good);
                else
                    yield return new Message(d, $"Revolt in the {x.Sector}! Every system left there now backs the Collective, and the Hand ones rise up.",
                        null, Player == Side.Hand ? MsgKind.Bad : MsgKind.Good);
                break;
            case DestroySystemCancelled x when Mine(x.Side):
                yield return new Message(d, $"Destroy System at {x.System} by {FleetName(x.Fleet)} was cancelled ({Why(x.Reason)}).", x.System, MsgKind.Warn);
                break;
        }
    }
}
