using System.Collections.Generic;
using System.Linq;
using Praedra.Core.Economy;
using Praedra.Core.Missions;

namespace PraedraViewer;

/// <summary>
/// Everything of the player's own that travels and is not a fleet, for the map (Paul 2026-11-17: "I want to see everything that moves on the
/// map"): leaders travelling alone, mission teams going out and coming home, built units and buildings on their way from the yard, regiments
/// sent alone, and the moving HQ. Own side only: the enemy's stay hidden by fog (rule: m5-fog-and-hq §F3). Read-only.
/// </summary>
public partial class GameScreen
{
    IEnumerable<Mover> Movers()
    {
        var me = S.Player;

        // leaders travelling alone (between days the trip has started once System is cleared). rule: m5-characters-and-talent §Where it runs (slot 2)
        foreach (var c in S.MyCharacters)
            if (c.AttachedFleet == 0 && c.System == null && c.Transit is { } t && t.Path.Count > 1)
            {
                string who = S.CharName(c.Id);
                yield return new Mover(MoverKind.Leader, who, t.Path, t.HopTicks, t.HopIndex, t.DueDay, Name: who, Point: $"{who} · day {GameSession.ArrivalDay(t)}");
            }

        // mission teams on their way out or home; the core keeps only the trip's total days. rule: m5-missions §1 (A = K + total ticks)
        foreach (var m in S.Missions.Missions.Where(m => m.Side == me && m.Departed && m.TotalTicks > 0 && m.Path.Count > 1).OrderBy(m => m.Id))
        {
            if (m.Phase != MissionPhase.Outbound && m.Phase != MissionPhase.Returning) continue;
            string team = S.TeamOf(m);
            string label = m.Kind == MissionKind.Move ? $"{team} to {m.Path[^1]}, day {m.ArrivalDay}"
                : m.Phase == MissionPhase.Outbound ? $"{GameSession.KindWords(m.Kind)}: {team} to {m.Target}, day {m.ArrivalDay}"
                : $"{team} home to {m.Path[^1]}, day {m.ArrivalDay}";
            // on the map: the kind of mission going out ("Espionage"), the team otherwise; under the mouse, who and the day
            string name = m.Kind != MissionKind.Move && m.Phase == MissionPhase.Outbound ? GameSession.KindWords(m.Kind) : team;
            string point = m.Kind == MissionKind.Move ? $"{team} · day {m.ArrivalDay}"
                : m.Phase == MissionPhase.Outbound ? $"{GameSession.KindWords(m.Kind)}: {team} · day {m.ArrivalDay}"
                : $"{team}, home · day {m.ArrivalDay}";
            yield return new Mover(m.Kind == MissionKind.Move ? MoverKind.Leader : MoverKind.Mission, label, m.Path, null, 0, m.ArrivalDay, m.TotalTicks,
                Name: name, Point: point);
        }

        // built units, buildings and lone regiments on their way: one marker for those flying together. rule: m2-construction §Completion and delivery
        var groups = S.Host.Construction.Deliveries.Where(d => d.Side == me && (d.InFlight || d.Held) && d.Path.Count > 0)
            .Select(d => (d, o: S.Host.Economy.Find(d.Object)))
            .GroupBy(x => (Kind: KindOf(x.o), x.d.To, At: x.d.Path[x.d.Index], x.d.Index, x.d.InFlight, x.d.Held, x.d.DueDay, Next: x.d.InFlight ? x.d.Path[x.d.Index + 1] : ""))
            .OrderBy(g => g.Min(x => x.d.Id));
        foreach (var g in groups)
        {
            var first = g.OrderBy(x => x.d.Id).First().d;
            var classes = g.Select(x => x.o?.Class ?? "unit").Distinct().ToList();
            int n = g.Count();
            string what = n == 1 ? classes[0] : classes.Count == 1 ? $"{classes[0]} and {n - 1} more" : $"{n} units";   // never "N x": one unit per card (Paul 2026-11-14)
            int arrive = first.DueDay;
            for (int h = first.Index + 1; h < first.HopTicks.Count; h++) arrive += first.HopTicks[h];
            string label = first.Held ? $"{what} to {first.To}, held at {first.Path[first.Index]}" : $"{what} to {first.To}, day {arrive}";
            string point = first.Held ? $"{what} · held at {first.Path[first.Index]}" : $"{what} · day {arrive}";
            yield return new Mover(g.Key.Kind, label, first.Path, first.HopTicks, first.Index, first.DueDay, Count: n, Held: first.Held, Name: what, Point: point);
        }

        // the player's own HQ while it moves (the Collective's Hearth). rule: m5-fog-and-hq §F5
        if (S.OwnHq is { Status: Praedra.Core.Fog.HqStatus.InTransit } hq && hq.Path.Count > 1)
            yield return new Mover(MoverKind.Hq, "HQ", hq.Path, hq.HopTicks, hq.HopIndex, hq.DueDay, Name: "HQ", Point: $"HQ · day {GameSession.HqArrivalDay(hq)}");
    }

    static MoverKind KindOf(EconObject o) => o?.Category switch
    {
        ObjectCategory.Facility => MoverKind.Facility,
        ObjectCategory.CapitalShip => MoverKind.Ship,
        ObjectCategory.Fighter => MoverKind.Fighter,
        _ => MoverKind.Regiment, // regiments and special forces
    };
}
