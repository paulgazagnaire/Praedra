using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Clock;
using Praedra.Core.Economy;

namespace PraedraViewer;

/// <summary>
/// Scrapping (story C9): ships from the fleet window, fighters, regiments and buildings from the system window. The order is the core's
/// ScrapObject, one per object; the core removes it when the next day starts and gives back half its refined cost.
/// rule: m2-maintenance §Scrapping and refunds, m2-construction §Scrapping (special forces cannot be scrapped).
/// </summary>
public partial class GameScreen
{
    CostRow CostRowOf(EconObject o) => S.Host.Economy.Costs.TryGet(o.Class, out var r) ? r : null;
    int RefundOf(EconObject o) => (CostRowOf(o)?.Cost ?? 0) / S.Host.Economy.Params.RefundDivisor;
    int UpkeepOf(EconObject o) => CostRowOf(o)?.Maintenance ?? 0;

    static bool Scrappable(EconObject o) => o.Completed && o.Category != ObjectCategory.SpecialForce;

    /// <summary>Ask first, then send one scrap order per object. <paramref name="what"/> names them ("2 x Mandate").</summary>
    void AskScrap(IReadOnlyList<EconObject> objects, string what, string system)
    {
        var list = objects.Where(Scrappable).ToList();
        if (list.Count == 0) { Toast("Nothing here can be scrapped.", MsgKind.Warn); return; }
        int refund = list.Sum(RefundOf), upkeep = list.Sum(UpkeepOf);
        string body = $"{what} will be taken apart when the next day starts. You get {refund} refined back (half the cost) and {upkeep} upkeep is freed. This cannot be undone.";
        if (list.Any(o => o.Category == ObjectCategory.CapitalShip) && list.Any(o => S.Host.Economy.Objects.Any(x => x.AboardShip == o.Id)))
            body += $" Fighters and regiments aboard stay at {system}.";
        // a mine or a refinery is upkeep room: each pair gives 50 (rule: m2-maintenance §Capacity)
        int roomLost = list.Count(o => FacilityKinds.TryParse(o.Class, out var k) && k is FacilityKind.Mine or FacilityKind.Refinery);
        if (roomLost > 0) body += " Mines and refineries are what give you upkeep room: scrapping one lowers your room by 50 when it was part of a pair.";
        // rule: m2-construction §Scrapping (a scrapped yard loses its running cycle; the last yard of a kind cancels the project)
        if (list.Any(o => o.Category == ObjectCategory.Facility && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)))
            body += " A scrapped yard stops what it is building, and the work in progress is lost.";
        Confirm($"Scrap {what}?", body, "Scrap", () => Scrap(list, what, system), danger: true);
    }

    void Scrap(List<EconObject> list, string what, string system)
    {
        foreach (var o in list)
        {
            int id = o.Id;
            S.Track(S.Game.Submit(seq => new ScrapObjectOrder(seq, Me, id)), $"Scrap {o.Class}");
        }
        _splitSel.Clear();
        Sent($"scrap {what}", system);
        _panelDirty = true;
    }

    /// <summary>"3 x Mandate" for a group of objects of one class.</summary>
    static string CountWords(IReadOnlyCollection<EconObject> g) => g.Count == 1 ? g.First().Class : $"{g.Count} x {g.First().Class}";

    /// <summary>One line per class with how many, and a quiet "Scrap one..." on the right.</summary>
    void ScrapRows(IEnumerable<EconObject> objects, string system)
    {
        foreach (var g in objects.Where(Scrappable).GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var group = g.OrderBy(o => o.Id).ToList();
            var row = Ui.Row(g.Key, group.Count.ToString(), null, $"Upkeep {UpkeepOf(group[0])} each. Scrapping one gives back {RefundOf(group[0])} refined.");
            var first = group[0];
            row.AddChild(Ui.Button("Scrap one...", () => AskScrap(new[] { first }, $"one {first.Class}", system), "Take one apart for half its refined cost back, and free its upkeep.", "Link"));
            Add(row);
        }
    }
}
