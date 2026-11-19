using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

// Picking several units at once (Paul 2026-11-19: "I need to be able to select units and ships by clicking on them, and while maintaining
// shift. Then drag and drop or right click to act on all of the selected units"). A click picks a unit card; Shift+click adds it to the
// pick or takes it out. Dragging a picked card carries every picked unit; right-clicking one offers to act on all of them. Escape, or a
// plain click on the only picked card, lets them go. Ships, regiments, fighter squadrons and special forces can be picked; leaders and
// whole fleets drag alone. Screens only: a drop still sends one order per unit, as before.
public partial class GameScreen
{
    readonly List<Drag> _picked = new();   // in the order they were picked

    static bool Pickable(Drag d) => d.Kind is 'R' or 'S' or 'Q' or 'F';

    /// <summary>The picked units that can still act: not dropped elsewhere or scrapped today, and still in the game.</summary>
    List<Drag> LivePicks()
    {
        _picked.RemoveAll(d => _moved.ContainsKey(d.Key) || _scrapped.Contains(d.Id)
            || (d.Kind == 'F' ? S.Host.Economy.Find(d.Id) == null : S.Host.Economy.Find(d.Id) is not { } o || o.Side != Me));
        return _picked.ToList();
    }

    bool IsPicked(Drag d) => _picked.Contains(d);

    /// <summary>A click on a unit card: Shift adds it to the pick or takes it out; a plain click picks only it (or lets it go if it was
    /// the only one picked).</summary>
    void PickClick(Drag d, bool shift)
    {
        if (!Pickable(d)) return;
        if (shift) { if (!_picked.Remove(d)) _picked.Add(d); }
        else if (_picked.Count == 1 && _picked[0] == d) _picked.Clear();
        else { _picked.Clear(); _picked.Add(d); }
        Callable.From(() => { RefreshMgmt(); }).CallDeferred();   // the cards are rebuilt with their new outline
    }

    /// <summary>Let every picked unit go. True if there were any.</summary>
    bool ClearPicks()
    {
        if (_picked.Count == 0) return false;
        _picked.Clear();
        RefreshMgmt();
        return true;
    }

    /// <summary>A picked card's outline: the full blue, 2 px (no fill; Paul 2026-11-14), same inner margins as a card's.</summary>
    static StyleBox PickedLook() => Ui.Box(new Color(0, 0, 0, 0), 0, Ui.You, 2, 4, 6);

    /// <summary>What a drag carries, as "R:12,13;S:40": every unit, grouped by kind in the order picked.</summary>
    static string Payload(IReadOnlyList<Drag> take) =>
        string.Join(";", take.GroupBy(x => x.Kind).Select(g => $"{g.Key}:{string.Join(",", g.Select(x => x.Id))}"));

    /// <summary>What a drag of a card takes: every picked unit when the card is picked (itself first); else, with Shift, the identical
    /// units beside it; else the card's own unit.</summary>
    List<Drag> TakeFor(IReadOnlyList<Drag> ds, bool shift, out bool picked)
    {
        var self = ds[0];
        var picks = Pickable(self) && IsPicked(self) ? LivePicks() : null;
        picked = picks is { Count: > 1 };
        return picked ? picks.OrderBy(x => x == self ? 0 : 1).ToList() : shift ? ds.ToList() : ds.Take(1).ToList();
    }

    /// <summary>What a plain drag of the card <paramref name="key"/> would carry now (for the playtest).</summary>
    internal string PlayDragPayload(string key) => Drag.Parse(key) is { } d ? Payload(TakeFor(new[] { d }, false, out _)) : "";

    /// <summary>A card clicked as the mouse would (for the playtest).</summary>
    internal void PlayPick(string key, bool shift)
    {
        if (Drag.Parse(key) is { } d) PickClick(d, shift);
    }

    /// <summary>For a screenshot (--pickunits 3): pick the first n unit cards of the open windows, as clicks and Shift+clicks would.</summary>
    internal void ShotPick(int n)
    {
        foreach (var k in MgmtKeys.ToList()) MgText(k);
        var keys = new List<Drag>();
        void Walk(Node node)
        {
            if (node is Control c && c.HasMeta(UiTest.FullName) && Drag.Parse(c.GetMeta(UiTest.FullName).AsString()) is { } d && Pickable(d) && !keys.Contains(d)) keys.Add(d);
            foreach (var ch in node.GetChildren()) Walk(ch);
        }
        Walk(this);
        _picked.Clear();
        _picked.AddRange(keys.Take(n));
        RefreshMgmt();
        foreach (var k in MgmtKeys.ToList()) MgText(k);
    }

    internal IReadOnlyList<string> PickedKeys => LivePicks().Select(d => d.Key).ToList();
}
