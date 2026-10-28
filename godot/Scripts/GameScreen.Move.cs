using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Fleets;

namespace PraedraViewer;

/// <summary>
/// Map clicks and the move order (user stories B1-B6): pick a destination and the order bar at the bottom of the map shows the days, the
/// arrival day and how the path travels. A clean path is sent with one click. A path through a known trap is never sent by the click: the
/// bar turns red, names the trap and offers Go anyway, Go around (a path the game found that avoids every known trap, offered, never taken
/// for you) or Cancel. Shift-click adds a waypoint, Backspace drops it. rule: m1-travel §Path rule (3), (5); Paul 2026-10-16.
/// </summary>
public partial class GameScreen
{
    int _moveFleet = -1;
    bool _moveHq;
    List<int> _moveFighters;        // based fighters flying on their own (MoveSquadrons), null when not choosing
    string _moveFightersFrom;
    string _moveBanner;
    bool? _retreatPending;
    int _lastSelectedSystem = -2, _lastSelectedFleet = -2;

    // Waypoints of the fleet move being planned (shift-click adds one, Backspace drops the last).
    readonly List<string> _waypoints = new();
    bool _moveWarn;
    static readonly Color TrapRed = new(1f, 0.32f, 0.27f, 0.95f);
    /// <summary>A trapped destination the player clicked: the bar waits for Go anyway / Go around / Cancel.</summary>
    string _lockedTo;
    (List<string> Via, MovePlan Plan)? _around;

    // ================================================================== view events

    void OnViewChanged()
    {
        if (_moveFleet >= 0 && _lockedTo == null) UpdatePreview();
        if (_moveHq) UpdateHqPreview();
        if (_moveFighters != null) UpdateFighterPreview();
        if (_moveChar >= 0) UpdateCharPreview();
        if (_pickMission) UpdateMissionPreview();
        int h = _view.Hovered;
        if (h >= 0 && !InMoveMode) ShowHover(HoverText(h));
        else if (_view.HoveredSector >= 0 && !InMoveMode) ShowHover(SectorHoverText(_view.SectorName(_view.HoveredSector)));
        else ShowHover(null);

        if (_view.Selected != _lastSelectedSystem || _view.SelectedFleet != _lastSelectedFleet)
        {
            if (_view.SelectedFleet != _lastSelectedFleet) _splitSel.Clear();
            bool newSystem = _view.Selected != _lastSelectedSystem;
            _lastSelectedSystem = _view.Selected; _lastSelectedFleet = _view.SelectedFleet;
            if (_moveFleet >= 0 && _view.SelectedFleet != _moveFleet) CancelMove();
            if (_view.Selected >= 0 || _view.SelectedFleet >= 0)
            {
                if (_plan != null && !_pickMission) _plan = null; // a map selection closes the planner
                if (newSystem) _sysTab = SysTab.Overview;
                if (_drawer.Visible) ShowTab(DrawerTab.Selected); // a map click fills the selection card; the window follows only when open
                if (_view.Selected >= 0) SectorFollowSelection(G.Systems[_view.Selected].Name);
            }
            _panelDirty = true; _panelTimer = 0;
            RefreshSelectionCard();
        }
    }

    /// <summary>Pointing at a sector: its name, how much of it is yours, and what a click does.</summary>
    string SectorHoverText(string sector)
    {
        var sec = G.Sectors.FirstOrDefault(x => x.Name == sector);
        if (sec == null) return null;
        int mine = sec.Members.Count(m => S.OwnerOf(G.Systems[m].Name) == Me);
        return $"{sector}: {mine} of {sec.Members.Count} systems yours\nClick to open its table";
    }

    string HoverText(int h)
    {
        string hn = G.Systems[h].Name;
        var (known, owner) = S.KnownOwnerOf(hn);
        int? seen = S.LastSeenDay(hn);
        string age = owner == Me || S.Sees(hn) || seen == null ? "" : $"  (last seen day {seen})";
        string line1 = S.IsDestroyed(hn) ? $"{hn}  -  {S.DestroyedWords(hn).ToLowerInvariant()}"
            : $"{hn}  -  {(!S.Explored(hn) && owner != Me ? "unexplored" : !known ? "owner unknown" : owner == null ? "neutral" : S.You(owner.Value))}{age}";
        string sector = G.Systems[h].IsOutsideSectors ? "Outside the sectors" : G.Systems[h].Sector;
        string extra = "";
        if (owner == Me && !S.IsDestroyed(hn))
        {
            int have = S.Host.Support.RegimentCount(hn), need = S.Host.Support.RequiredGarrison(hn);
            extra = $"\nSupport {S.Host.Economy.SupportOf(hn, Me)}%, troops {have} of {need} needed" + (S.Host.Support.IsUprising(hn) ? ", UPRISING" : "");
        }
        return $"{line1}\n{sector}. {RouteWords(h)}{extra}";
    }

    /// <summary>The hyperspace routes through a system, for the hover line: named major routes, else "minor route", else open space.</summary>
    string RouteWords(int sys)
    {
        var links = G.NeighboursOf(sys);
        if (links.Count == 0) return "No route here: ships reach it by a free jump through open space";
        var named = links.SelectMany(nb => nb.Lane.Routes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        bool minor = links.Any(nb => nb.Lane.Tier != 1);
        if (named.Count == 0) return "Minor route (1.5x speed)";
        return $"{string.Join(", ", named)} (major route, 3x speed){(minor ? "; also minor routes (1.5x)" : "")}";
    }

    /// <summary>How a path travels, for the order bar: legs along a route versus free jumps through open space.</summary>
    static string LegWords(IReadOnlyList<bool> routeHop)
    {
        int onRoute = routeHop.Count(r => r);
        int free = routeHop.Count - onRoute;
        if (onRoute == 0) return free == 1 ? " (one free jump, no route)" : $" ({free} free jumps, no route)";
        string r = onRoute == 1 ? "1 stretch on a route" : $"{onRoute} stretches on routes";
        return free == 0 ? $" ({r})" : $" ({r}, {free} free {(free == 1 ? "jump" : "jumps")})";
    }

    // ================================================================== the fleet move

    void StartMove(Fleet f)
    {
        CancelMove();
        _moveFleet = f.Id;
        _waypoints.Clear(); _view.Waypoints = null;
        _view.PickMode = true;
        _moveBanner = $"Move {f.Name}: click a destination system (Esc cancels)";
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void CancelMove()
    {
        _moveFleet = -1; _moveHq = false; _moveChar = -1; _pickMission = false; _moveFighters = null; _view.PickMode = false; _view.PathPreview = null;
        _waypoints.Clear(); _view.Waypoints = null; _view.PathTraps = null; _view.PathRouteHop = null; _moveWarn = false;
        _lockedTo = null; _around = null;
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void UpdatePreview(string forcedTo = null)
    {
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f) || f.At == null) { CancelMove(); return; }
        _view.Waypoints = _waypoints.Count == 0 ? null : _waypoints.Select(w => G.TryFindSystem(w, out int i) ? i : -1).Where(i => i >= 0).ToList();
        int h = _view.Hovered;
        // nothing useful under the mouse: show the path through the waypoints so far, if any
        bool hovering = forcedTo != null || (h >= 0 && G.Systems[h].Name != f.At);
        string to = forcedTo ?? (hovering ? G.Systems[h].Name : _waypoints.Count > 0 ? _waypoints[^1] : null);
        var via = hovering ? _waypoints : _waypoints.Take(Math.Max(0, _waypoints.Count - 1)).ToList();
        string tail = _waypoints.Count == 0 ? "Click to send it. Shift-click adds a waypoint." : "Click to send it. Shift-click adds another waypoint, Backspace drops the last.";
        _moveWarn = false; _view.PathTraps = null; _around = null;
        if (to == null)
        {
            _view.PathPreview = null; _view.PathRouteHop = null;
            _moveBanner = $"Move {f.Name}: click a destination system (Esc cancels)";
        }
        else
        {
            var plan = S.Host.PlanMove(Me, f.Id, to, via);
            if (plan.Ok)
            {
                _view.PathPreview = plan.Path.Select(n => G.TryFindSystem(n, out int i) ? i : -1).ToList();
                _view.PathRouteHop = plan.RouteHop;
                string trip = $"{plan.HopTicks.Sum()} days, arrives day {plan.ArrivalDay}{LegWords(plan.RouteHop)}";
                string through = via.Count == 0 ? "" : " via " + string.Join(", ", via);
                if (plan.Red)
                {
                    _moveWarn = true;
                    _view.PathColor = TrapRed;
                    _view.PathTraps = plan.Traps.Select(t => G.TryFindSystem(t.System, out int i) ? i : -1).Where(i => i >= 0).ToList();
                    string where = string.Join(", ", plan.Traps.Select(t => $"{t.System} (seen day {t.SeenDay})"));
                    _moveBanner = $"TRAP: an enemy interdictor was seen at {where}. It can pull {f.Name} out of hyperspace there.\n"
                        + $"{f.Name} to {to}{through}: {trip}. " + (_lockedTo != null ? "Choose below." : "Click to choose, or Shift-click a waypoint to steer around it.");
                    if (_lockedTo != null) _around = GoAround(f, to, via, plan);
                }
                else
                {
                    _view.PathColor = new Color(1f, 1f, 1f, 0.9f);
                    _moveBanner = $"{f.Name} to {to}{through}: {trip}.\n{tail}";
                }
            }
            else { _view.PathPreview = null; _view.PathRouteHop = null; _moveBanner = $"No route from {f.At} to {to} for this fleet."; }
        }
        RefreshTop();
    }

    /// <summary>A way past every known trap on a red path: one more waypoint (or the game's own trap-free path as waypoints), never taken without a click.</summary>
    (List<string> Via, MovePlan Plan)? GoAround(Fleet f, string to, List<string> via, MovePlan red)
    {
        if (!G.TryFindSystem(f.At, out int from) || !G.TryFindSystem(to, out int dest)) return null;
        var avoid = red.Traps.Select(t => G.TryFindSystem(t.System, out int i) ? i : -1).Where(i => i >= 0 && i != dest).ToHashSet();
        var p = Fm.FindPathAvoiding(from, dest, Fm.SpeedOf(f), avoid);
        if (!p.Ok || p.Systems.Count < 3) return null;
        var mids = p.Systems.Skip(1).Take(p.Systems.Count - 2).Select(i => G.Systems[i].Name).ToList();
        // the simplest steer first: one waypoint, tried from the middle of the trap-free path outwards
        foreach (var w in mids.OrderBy(m => Math.Abs(mids.IndexOf(m) - mids.Count / 2)).ThenBy(m => m, StringComparer.Ordinal))
        {
            var v = via.Append(w).ToList();
            var plan = S.Host.PlanMove(Me, f.Id, to, v);
            if (plan.Ok && !plan.Red) return (v, plan);
        }
        var all = via.Concat(mids).ToList();
        var full = S.Host.PlanMove(Me, f.Id, to, all);
        return full.Ok && !full.Red ? (all, full) : null;
    }

    /// <summary>The order bar at the bottom of the map: what the move will do, and its buttons when a choice is needed.</summary>
    void RefreshOrderBar()
    {
        if (_orderBar == null) return;
        bool show = InMoveMode;
        _orderBar.Visible = show;
        if (_selCard != null) _selCard.Visible = !show && SelectionCardWanted; // the order bar takes the bottom centre while a move is planned
        if (!show) return;
        string text = _moveBanner ?? "";
        int nl = text.IndexOf('\n');
        _orderTitle.Text = nl < 0 ? text : text[..nl];
        _orderBody.Text = nl < 0 ? "" : text[(nl + 1)..];
        _orderBody.Visible = _orderBody.Text.Length > 0;
        bool red = _moveFleet >= 0 && _moveWarn;
        _orderBar.AddThemeStyleboxOverride("panel", Ui.Box(new Color(red ? Ui.DangerDeep : Ui.HudBg, 0.97f), 8, red ? Ui.Danger : new Color(1, 1, 1, 0.08f), red ? 2 : 1, 14, 10));
        _orderTitle.AddThemeColorOverride("font_color", red ? new Color("ffb4b4") : Ui.Text);
        Ui.Clear(_orderActions);
        if (_moveFleet >= 0 && _lockedTo != null && red)
        {
            var anyway = Ui.Button($"Go anyway", () => SendMove(_lockedTo, _waypoints.ToList()), "Send the fleet on the red path: it may be pulled out of hyperspace at the trap.", "DangerButton");
            _orderActions.AddChild(anyway);
            if (_around is { } a)
            {
                string w = a.Via.Count == _waypoints.Count + 1 ? a.Via[^1] : string.Join(", ", a.Via.Skip(_waypoints.Count));
                _orderActions.AddChild(Ui.Button($"Go around via {w}: {a.Plan.HopTicks.Sum()} days, no trap", () => SendMove(_lockedTo, a.Via),
                    $"Arrives day {a.Plan.ArrivalDay}{LegWords(a.Plan.RouteHop)}. No known trap on the way.", "Primary"));
            }
            else _orderActions.AddChild(Ui.Label("No way around every known trap was found.", "Hint", wrap: false));
        }
        _orderActions.AddChild(Ui.Grow());
        _orderActions.AddChild(Ui.Button("Cancel (Esc)", CancelMove));
        _orderActions.Visible = true;
    }

    /// <summary>Shift-click in move mode: the system becomes the next waypoint (not the fleet's own system, not twice in a row).</summary>
    void AddWaypoint(int idx)
    {
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f) || f.At == null) return;
        string w = G.Systems[idx].Name;
        if (w == f.At || (_waypoints.Count > 0 && _waypoints[^1] == w)) return;
        _waypoints.Add(w);
        _lockedTo = null;
        UpdatePreview();
    }

    void DropWaypoint()
    {
        if (_waypoints.Count == 0) return;
        _waypoints.RemoveAt(_waypoints.Count - 1);
        _lockedTo = null;
        UpdatePreview();
    }

    void OnSystemPicked(int idx)
    {
        if (_moveHq) { OnHqPicked(idx); return; }
        if (_moveFighters != null) { OnFightersPicked(idx); return; }
        if (_moveChar >= 0) { OnCharPicked(idx); return; }
        if (_pickMission) { OnMissionPicked(idx); return; }
        if (_moveFleet < 0) return;
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f) || f.At == null) { CancelMove(); return; }
        if (Input.IsKeyPressed(Key.Shift)) { AddWaypoint(idx); return; }
        string to = G.Systems[idx].Name;
        if (to == f.At) return;
        var via = _waypoints.ToList();
        var plan = S.Host.PlanMove(Me, f.Id, to, via); // the same path the order will fly
        if (plan.Ok && plan.Red)
        {
            // a known trap on the way: never sent by the click alone (the bar offers Go anyway / Go around / Cancel)
            _lockedTo = to;
            UpdatePreview(to);
            return;
        }
        SendMove(to, via);
    }

    void SendMove(string to, List<string> via)
    {
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f) || f.At == null) { CancelMove(); return; }
        var plan = S.Host.PlanMove(Me, f.Id, to, via);
        long seq = Fm.SubmitMove(Me, f.Id, to, via);
        S.TrackFleet(f.Id, seq, $"Move {f.Name} to {to}");
        if (plan.Ok) S.PlanMove(f.Id, new GameSession.PlannedMove(to, plan.Path.ToList(), plan.HopTicks.Sum(), plan.ArrivalDay));
        string viaWords = via.Count == 0 ? "" : " via " + string.Join(", ", via);
        string trapWords = plan.Red ? $" (warned: interdictor seen at {string.Join(", ", plan.Traps.Select(t => t.System))})" : "";
        S.Say($"Order sent: move {f.Name} to {to}{viaWords}{trapWords}.", plan.Red ? MsgKind.Warn : MsgKind.Info, to);
        AddLogItem(S.Messages[^1], false);
        Toast(plan.Ok ? $"{f.Name} to {to}: arrives day {plan.ArrivalDay}." : $"Order sent: move {f.Name} to {to}.", plan.Red ? MsgKind.Warn : MsgKind.Info);
        CancelMove();
        _fleetListDirty = true;
    }

    // ================================================================== the Hearth's move (Collective)

    void StartHqMove()
    {
        CancelMove();
        _moveHq = true;
        _view.PickMode = true;
        _moveBanner = "Move HQ: click one of your systems (Esc cancels)";
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void UpdateHqPreview()
    {
        string at = S.MyHqSystem;
        if (at == null) { CancelMove(); return; }
        int h = _view.Hovered;
        if (h < 0 || G.Systems[h].Name == at) { _view.PathPreview = null; _moveBanner = "Move HQ: click one of your systems (Esc cancels)"; }
        else
        {
            string to = G.Systems[h].Name;
            var pr = Fm.FindPath(at, to, S.Fog.HqRules.Speed);
            if (!pr.Ok) { _view.PathPreview = null; _moveBanner = $"No route from {at} to {to} for the HQ."; }
            else
            {
                _view.PathPreview = pr.Systems;
                _view.PathColor = new Color(Ui.Gold, 0.9f);
                bool held = S.OwnerOf(to) == Me;
                _moveBanner = held ? $"HQ to {to}: {pr.TotalTicks} days, arrives day {S.Game.Day + pr.TotalTicks}.\nClick to confirm. On the way the HQ cannot be attacked."
                                   : $"{to} is not yours: the HQ can only move to a system you hold.";
            }
        }
        RefreshTop();
    }

    void OnHqPicked(int idx)
    {
        string at = S.MyHqSystem;
        string to = G.Systems[idx].Name;
        if (at == null || to == at) return;
        S.SubmitMoveHq(to); // the core judges it at the next day: refusals come back in Messages with the reason
        S.Say($"Order sent: move the HQ to {to}.", MsgKind.Info, to);
        AddLogItem(S.Messages[^1], false);
        CancelMove();
    }

    // ================================================================== fighters flying on their own (rule: m3-fleets §Unescorted squadrons)

    void StartFighterMove(string from, List<int> squadrons)
    {
        CancelMove();
        _moveFighters = squadrons;
        _moveFightersFrom = from;
        _view.PickMode = true;
        _moveBanner = $"Fly {Ui.Count(squadrons.Count, "fighter squadron", "fighter squadrons")} from {from}: click a destination system (Esc cancels)";
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    /// <summary>The squadrons fly as one group at the speed of the fastest (rule: m3-fleets §Fleet speed, squadron-only fleet).</summary>
    int FighterSpeed => _moveFighters.Select(S.Host.Economy.Find).Where(o => o != null).Select(o => Fm.ClassOf(o)?.Hyperdrive ?? 0).DefaultIfEmpty(0).Max();

    void UpdateFighterPreview()
    {
        int h = _view.Hovered;
        string from = _moveFightersFrom;
        if (h < 0 || G.Systems[h].Name == from) { _view.PathPreview = null; return; }
        string to = G.Systems[h].Name;
        var pr = Fm.FindPath(from, to, FighterSpeed);
        if (!pr.Ok) { _view.PathPreview = null; _moveBanner = $"No way from {from} to {to} for these fighters."; }
        else
        {
            _view.PathPreview = pr.Systems;
            _view.PathColor = new Color(Ui.You, 0.9f);
            _moveBanner = $"Fighters to {to}: {pr.TotalTicks} days, arrive day {S.Game.Day + pr.TotalTicks}. Click to send. They land and stay based there.";
        }
        RefreshTop();
    }

    void OnFightersPicked(int idx)
    {
        string to = G.Systems[idx].Name;
        if (to == _moveFightersFrom) return;
        var ids = _moveFighters;
        S.Track(Fm.SubmitMoveSquadrons(Me, ids, to), $"Fly fighters to {to}");
        Sent($"fly {Ui.Count(ids.Count, "fighter squadron", "fighter squadrons")} from {_moveFightersFrom} to {to}", to);
        CancelMove();
        _fleetListDirty = true;
    }
}
