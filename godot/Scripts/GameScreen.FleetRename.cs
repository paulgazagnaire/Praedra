using System;
using System.Linq;
using System.Text;
using Godot;
using Praedra.Core.Fleets;

namespace PraedraViewer;

/// <summary>
/// Renaming a fleet (Paul 2026-11-23: a double-click on a fleet's name renames it; Enter or a click elsewhere confirms). A double-click on the name in the fleet's window turns it into a box holding the name, all of it picked,
/// so typing replaces it. Enter, or a click anywhere outside the box, keeps what is typed; Esc keeps the old name, and so does an empty box.
/// At most 24 letters the game font can draw: an accented letter becomes its plain letter as it is typed, and any other letter it has not
/// got is left out. The core renames the fleet first thing next day; the new name shows everywhere at once (GameSession.NameOf).
/// rule: m3-fleets §Orders (RenameFleet). Screens only.
/// </summary>
public partial class GameScreen
{
    int _renaming = -1;      // the fleet whose name is being typed, or -1
    string _renameText;      // what is typed so far, kept if its window is rebuilt meanwhile
    bool _renameFresh;       // the box has just opened: its whole name is picked
    LineEdit _renameEdit;

    /// <summary>Whether this window's name is being typed: its head shows the box in place of the name.</summary>
    bool Renaming(MgWin w) => w.Kind == MgKind.Fleet && w.Fleet == _renaming;

    /// <summary>Whether this window shows the box now: it then waits to be rebuilt until the typing ends, so nothing typed is lost.</summary>
    bool RenameBoxIn(MgWin w) => Renaming(w) && _renameEdit != null && IsInstanceValid(_renameEdit) && _renameEdit.IsInsideTree() && w.Panel.IsAncestorOf(_renameEdit);

    /// <summary>The name in the head opens the box on a double-click on its words (the rest of the head still folds the window).</summary>
    void RenameOnDoubleClick(Label name, MgWin w, Fleet f, float wordsW)
    {
        name.MouseFilter = MouseFilterEnum.Pass;   // a press still reaches the head under it, which moves the window
        name.MouseDefaultCursorShape = CursorShape.Move;
        name.TooltipText = Ui.Wrap("Double-click the name to rename the fleet.");
        int id = f.Id;
        name.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } b || b.Position.X > wordsW + 8) return;
            name.AcceptEvent();
            StartRename(w, id);
        };
    }

    void StartRename(MgWin w, int fleet)
    {
        if (!_mgWins.Contains(w) || !S.Game.State.Fleets.TryGetValue(fleet, out var f) || f.Side != Me) return;
        if (_renaming >= 0 && _renaming != fleet) FinishRename(true);   // one box at a time
        _renaming = fleet;
        _renameText = S.NameOf(f);
        _renameFresh = true;
        w.Stale = true;   // the head turns into the box when the window is next rebuilt (the frame the button comes up), not while this label's input runs
    }

    /// <summary>The box the name turns into: the name's own face and size, a 1 px line under it, nothing else (the window look).</summary>
    Control RenameBox(Fleet f, int size)
    {
        var e = new LineEdit
        {
            Text = _renameText ?? S.NameOf(f),
            MaxLength = FleetModule.MaxFleetNameLength,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ContextMenuEnabled = false,
            CaretBlink = true,
            TooltipText = Ui.Wrap($"The fleet's new name, up to {FleetModule.MaxFleetNameLength} letters. Enter or a click elsewhere keeps it. Esc keeps the old name."),
        };
        e.SetMeta(UiTest.FullName, $"Fleet rename {f.Id}");
        foreach (var st in new[] { "normal", "focus", "read_only" }) e.AddThemeStyleboxOverride(st, Look.Box(Look.Bg, Look.Accent, bottom: 1));
        e.AddThemeFontOverride("font", Ui.NameFont(size, e.Text));
        e.AddThemeFontSizeOverride("font_size", size);
        e.AddThemeColorOverride("font_color", Look.Text);
        e.AddThemeColorOverride("caret_color", Look.Text);
        e.AddThemeColorOverride("selection_color", Look.AccentDeep);
        e.TextChanged += typed =>
        {
            string safe = FontSafe(typed);
            if (safe != typed)
            {
                int caret = Math.Clamp(e.CaretColumn - (typed.Length - safe.Length), 0, safe.Length);
                e.Text = safe;
                e.CaretColumn = caret;
            }
            _renameText = safe;
            e.AddThemeFontOverride("font", Ui.NameFont(size, safe));
        };
        e.TextSubmitted += _ => FinishRename(true);
        e.FocusExited += () => { if (_renameEdit == e) FinishRename(true); };   // the keyboard went elsewhere (Tab, another box): that is elsewhere too
        e.GuiInput += ev =>
        {
            if (ev is InputEventKey { Pressed: true, Keycode: Key.Escape }) { e.AcceptEvent(); FinishRename(false); }
        };
        _renameEdit = e;
        bool fresh = _renameFresh;
        _renameFresh = false;
        Callable.From(() =>
        {
            if (!IsInstanceValid(e) || !e.IsInsideTree() || _renameEdit != e) return;
            e.GrabFocus();
            if (fresh) e.SelectAll(); else e.CaretColumn = e.Text.Length;
        }).CallDeferred();
        return e;
    }

    /// <summary>What the game font can draw of a typed name: accented letters become plain ones as the core makes them, letters it has not
    /// got are left out, and it stops at 24 letters. Kept as typed otherwise (no trimming while typing: a space may come before a word).</summary>
    static string FontSafe(string typed)
    {
        var sb = new StringBuilder();
        foreach (char c in typed ?? "")
        {
            if (FleetModule.IsFontSafe(c)) { sb.Append(c); continue; }
            string plain = FleetModule.CleanFleetName(c.ToString());
            if (plain.Length > 0 && plain.All(FleetModule.IsFontSafe)) sb.Append(plain);
        }
        return sb.Length > FleetModule.MaxFleetNameLength ? sb.ToString(0, FleetModule.MaxFleetNameLength) : sb.ToString();
    }

    /// <summary>Ends the typing: keep sends the RenameFleet order (unless the name is empty or unchanged), else the old name stays.</summary>
    void FinishRename(bool keep)
    {
        if (_renaming < 0) return;
        int id = _renaming;
        var e = _renameEdit;
        string typed = e != null && IsInstanceValid(e) ? e.Text : _renameText;
        _renaming = -1;
        _renameEdit = null;
        _renameText = null;
        if (e != null && IsInstanceValid(e) && e.HasFocus()) e.ReleaseFocus();
        if (keep && S.Game.State.Fleets.TryGetValue(id, out var f) && f.Side == Me)
        {
            string old = S.NameOf(f);
            if (S.RenameFleet(f, typed) is { } name)
            {
                S.Say($"Order sent: {old} is renamed {name}.");
                AddLogItem(S.Messages[^1], false);
                _panelDirty = true; _panelTimer = 0;
                _fleets.QueueRedraw();   // its words on the map
            }
        }
        foreach (var w in _mgWins.Where(x => x.Kind == MgKind.Fleet && x.Fleet == id)) w.Stale = true;   // its head shows the name as words again
        MgRefreshSoon();   // and every other window that names it
    }

    /// <summary>A click anywhere outside the box keeps the name (read before any control takes the click, which then does its own job).</summary>
    void RenameClickElsewhere(InputEvent e)
    {
        if (_renaming < 0 || e is not InputEventMouseButton { Pressed: true } b || b.ButtonIndex is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        if (_renameEdit != null && IsInstanceValid(_renameEdit) && _renameEdit.IsInsideTree() && _renameEdit.GetGlobalRect().HasPoint(_renameEdit.GetGlobalMousePosition())) return;
        FinishRename(true);
    }
}
