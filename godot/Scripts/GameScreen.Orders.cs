using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Orders look done the moment they are given (Paul 2026-11-08, "Fix the screen"): each order still waiting for the clock shows as
/// "Ordered: ..." with a Cancel button, which takes it back as if it was never given. The game rules do not change: the core still
/// judges every order when the clock moves (the day's step 1, the same day). rule: m1-clock-and-orders §Orders in (withdrawing a queued order).
/// A window drop moves its chip at once, so cancelling one of its orders also puts the chips back and takes back the drop's other orders.
/// </summary>
public partial class GameScreen
{
    /// <summary>What one window drop changed on the screen, to put back if its orders are cancelled.</summary>
    sealed class DropUndo
    {
        public readonly List<long> Seqs = new();
        public readonly Dictionary<string, (string Words, string Zone)?> Moved = new(System.StringComparer.Ordinal); // chip -> before the drop
        public readonly Dictionary<int, int> PendingIn = new();
        public readonly Dictionary<string, int> Leaving = new(System.StringComparer.Ordinal);
        public readonly HashSet<int> Splits = new();
    }

    readonly Dictionary<long, DropUndo> _dropUndo = new(); // an order made by a drop -> that drop's undo (until the clock moves)

    /// <summary>A waiting order: its words, then a Cancel button.</summary>
    void OrderLine(long seq, string text, int size = 0)
    {
        var h = Ui.HBox(6);
        var l = Ui.Label(text, null, Pending, size);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(l);
        string what = S.WhatOf(seq) ?? text;
        var b = Ui.Button("Cancel", () => CancelOrder(seq), $"Take back: {what}. Possible until the clock moves.", "Chip");
        b.SetMeta(UiTest.FullName, $"Cancel: {what}");
        b.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        h.AddChild(b);
        Add(h);
    }

    /// <summary>Take back a waiting order (and the rest of its drop). Nothing happens to the game: the order is simply never given.</summary>
    void CancelOrder(long seq)
    {
        string what = S.WhatOf(seq);
        _dropUndo.TryGetValue(seq, out var u);
        bool any = false;
        foreach (var q in u?.Seqs.ToList() ?? new List<long> { seq }) any |= S.Withdraw(q);
        if (!any) { Toast("Too late: the clock has moved and the order is under way.", MsgKind.Warn); return; }
        if (u != null)
        {
            foreach (var (key, before) in u.Moved) { if (before is { } m) _moved[key] = m; else _moved.Remove(key); }
            foreach (var (f, n) in u.PendingIn) { int left = (_pendingIn.TryGetValue(f, out int had) ? had : 0) - n; if (left > 0) _pendingIn[f] = left; else _pendingIn.Remove(f); }
            foreach (var (sys, n) in u.Leaving) { int left = (_leaving.TryGetValue(sys, out int had) ? had : 0) - n; if (left > 0) _leaving[sys] = left; else _leaving.Remove(sys); }
            foreach (var f in u.Splits) _splits.Remove(f);
            foreach (var q in u.Seqs) _dropUndo.Remove(q);
        }
        S.Say($"Order cancelled: {what ?? "an order"}.", MsgKind.Info);
        AddLogItem(S.Messages[^1], false);
        Toast($"Cancelled: {what ?? "the order"}.");
        _panelDirty = true; _panelTimer = 0;
        _fleetListDirty = true;
        RefreshMgmt();
        RefreshSectorView();
        RefreshTop();
    }

    /// <summary>Run one drop and remember what it changed, so Cancel can put it back. A drop that took back an earlier drop's order
    /// (a split growing by one ship) takes over that drop's undo.</summary>
    void RecordDrop(System.Action drop)
    {
        long before = S.LastTracked;
        var moved = new Dictionary<string, (string Words, string Zone)>(_moved, System.StringComparer.Ordinal);
        var pin = new Dictionary<int, int>(_pendingIn);
        var leaving = new Dictionary<string, int>(_leaving, System.StringComparer.Ordinal);
        var splits = _splits.ToDictionary(kv => kv.Key, kv => kv.Value.Seq);
        drop();
        var u = new DropUndo();
        u.Seqs.AddRange(S.TrackedAfter(before));
        foreach (var (k, v) in _moved) if (!moved.TryGetValue(k, out var old) || old != v) u.Moved[k] = moved.TryGetValue(k, out var o) ? o : null;
        foreach (var (f, n) in _pendingIn) { int d = n - (pin.TryGetValue(f, out int was) ? was : 0); if (d > 0) u.PendingIn[f] = d; }
        foreach (var (sys, n) in _leaving) { int d = n - (leaving.TryGetValue(sys, out int was) ? was : 0); if (d > 0) u.Leaving[sys] = d; }
        foreach (var (f, s) in _splits) if (!splits.TryGetValue(f, out long was) || was != s.Seq) u.Splits.Add(f);
        // an earlier drop whose order this one took back: its changes become this drop's (they stand or fall together now)
        foreach (var old in _dropUndo.Where(kv => S.WhatOf(kv.Key) == null).Select(kv => kv.Value).Distinct().ToList())
        {
            foreach (var (k, v) in old.Moved) u.Moved[k] = v;   // the older "before" is the true one
            foreach (var (f, n) in old.PendingIn) u.PendingIn[f] = (u.PendingIn.TryGetValue(f, out int x) ? x : 0) + n;
            foreach (var (sys, n) in old.Leaving) u.Leaving[sys] = (u.Leaving.TryGetValue(sys, out int x) ? x : 0) + n;
            u.Splits.UnionWith(old.Splits);
            foreach (var q in old.Seqs) { _dropUndo.Remove(q); if (S.WhatOf(q) != null) u.Seqs.Add(q); }
        }
        foreach (var q in u.Seqs) _dropUndo[q] = u;
    }
}
