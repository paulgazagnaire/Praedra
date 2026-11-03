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

    // ---- the harvest report (rule: m5-harvester §7 Harvest report; wording: specs/creative/02-the-hand.md §5). Screens only.

    static Dictionary<string, long> _people;

    /// <summary>People per system (data/design/system-population.csv, from the lore's popB); 0 for a system not in the file.</summary>
    static long PeopleOf(string system)
    {
        if (_people == null)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            string path = System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("user://gamedata"), "data", "design", "system-population.csv");
            if (!System.IO.File.Exists(path)) Godot.GD.PushWarning($"Harvest report: {path} is missing, so every report shows no people.");
            else
            {
                var t = Praedra.Core.Data.CsvTable.Load(path);
                for (int r = 0; r < t.RowCount; r++) map[t.Get(r, "system")] = long.Parse(t.Get(r, "people"), System.Globalization.CultureInfo.InvariantCulture);
            }
            _people = map;
        }
        return _people.TryGetValue(system, out long n) ? n : 0;
    }

    /// <summary>A head count as people read it: from a billion with one decimal, from a million in whole millions, else the plain count. rule: m5-harvester §7.</summary>
    internal static string PeopleWords(long n)
    {
        if (n >= 1_000_000_000) { long t = n / 100_000_000; return (t % 10 == 0 ? $"{t / 10}" : $"{t / 10}.{t % 10}") + " billion"; }
        if (n >= 1_000_000) return $"{n / 1_000_000} million";
        return n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The popup for a destroyed system. The Hand counts only the people with a Hand number (its support share there, before the strike) as
    /// debt written off; everyone else is "Unregistered", with no unit on purpose. The Collective reads it plainly. rule: m5-harvester §7.
    /// </summary>
    internal (string Title, string Detail) HarvestReport(SystemDestroyed x)
    {
        long people = x.Populated ? PeopleOf(x.System) : 0;
        if (Player == Side.Collective)
            return ($"{x.System} is gone", people > 0 ? $"{x.System} is gone. {PeopleWords(people)} people." : $"{x.System} is gone.");
        string head = $"HARVEST COMPLETE: {x.System}";
        if (people == 0) return ("Harvest complete", head);
        long debt = people * Math.Clamp(x.HandSupport, 0, 100) / 100;
        return ("Harvest complete", $"{head}\nDebt written off: {PeopleWords(debt)} hands\nUnregistered: {PeopleWords(people - debt)}");
    }

    static string HarvesterLossWords(string cause) => cause switch
    {
        "Battle" => "in battle",
        "Sabotage" => "by sabotage",
        "SabotageInYard" => "sabotaged in the shipyard before it was finished",
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
                // the log reads the same lines as the report, one after the other (rule: m5-harvester §7)
                string text = string.Join(". ", HarvestReport(x).Detail.Split('\n').Select(l => l.TrimEnd('.'))) + ".";
                var kind = x.AttackerSide == Player ? MsgKind.Info : x.PriorHolder == Player ? MsgKind.Bad : MsgKind.Warn;
                yield return new Message(d, text, x.System, kind);
                break;
            }
            case FleetDestroyedByHarvester x:
                yield return new Message(d, x.FleetSide == Player ? $"Your {FleetName(x.Fleet)} was destroyed at {x.System} by the harvester."
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
