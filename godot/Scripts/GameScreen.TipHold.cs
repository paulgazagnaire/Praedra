using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// A hover card stays while you read it (Paul 2026-11-19: "the units tooltip disappear when a new day pass, that's a bug"). The windows,
/// the side panel and the sector window are rebuilt when a day passes, which frees the control under the mouse and with it its card.
/// While the mouse rests on a control with a card, long enough for the card to show and with no click since it got there, the part
/// of the screen holding that control waits to be rebuilt; it catches up as soon as the mouse moves off or clicks. Drawing only.
/// </summary>
public partial class GameScreen
{
    Control _tipCtl;          // the control under the mouse, as last seen
    ulong _tipSince;          // when the mouse came onto it (ms)
    ulong _lastClick;         // the last mouse button press or release (ms): a click hides the card, so nothing is held after one
    /// <summary>Playtest: pretend the mouse is resting on this control with its card showing (null: the real mouse).</summary>
    Control _shotTip;

    void TrackTip(InputEvent e)
    {
        if (e is InputEventMouseButton) _lastClick = Time.GetTicksMsec();
    }

    void TickTip()
    {
        var h = GetViewport().GuiGetHoveredControl();
        if (h != _tipCtl) { _tipCtl = h; _tipSince = Time.GetTicksMsec(); }
    }

    /// <summary>A hover card is showing over something inside this part of the screen: hold its rebuild.</summary>
    bool ReadingTip(Control root)
    {
        if (root == null) return false;
        if (_shotTip != null) return IsInstanceValid(_shotTip) && (root == _shotTip || root.IsAncestorOf(_shotTip));
        var c = _tipCtl;
        if (c == null || !IsInstanceValid(c) || !c.IsVisibleInTree() || (c != root && !root.IsAncestorOf(c))) return false;
        if (_lastClick >= _tipSince) return false;
        double delay = (double)ProjectSettings.GetSetting("gui/timers/tooltip_delay_sec", 0.5);
        if (Time.GetTicksMsec() - _tipSince < (ulong)(delay * 1000)) return false;   // its card is not up yet
        for (var n = c; n != null; n = n.GetParent() as Control)
        {
            if (!string.IsNullOrEmpty(n.TooltipText)) return true;
            if (n == root) break;
        }
        return false;
    }

    /// <summary>Playtest: rest the mouse on the first control with a card in a window, let a day pass, and say whether the window was held
    /// (the control still stands) and whether it catches up once the mouse moves off (the control is replaced).</summary>
    internal (bool Held, bool CaughtUp, string Tip) PlayTipHold(string key)
    {
        var w = _mgWins.Find(x => x.Key == key);
        if (w == null) return (false, false, null);
        static IEnumerable<Node> Walk(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach (var d in Walk(c)) yield return d; } }
        var card = Walk(w.Body).OfType<Control>().FirstOrDefault(c => !string.IsNullOrEmpty(c.TooltipText) && c.IsVisibleInTree());
        if (card == null) return (false, false, null);
        string tip = card.TooltipText;
        bool Gone() => !IsInstanceValid(card) || card.IsQueuedForDeletion() || !w.Body.IsAncestorOf(card);
        _shotTip = card;
        RefreshMgmt(); _mgTimer = 0; TickMgmt(1);   // what a new day does to the open windows
        bool held = !Gone() && w.Stale;
        _shotTip = null;
        TickMgmt(1);
        return (held, Gone() && !w.Stale, tip);
    }
}
