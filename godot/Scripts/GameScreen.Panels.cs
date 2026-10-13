using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Rebellion.Core.Battle;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Defenses;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Troops;

using Side = Rebellion.Core.Clock.Side;

namespace RebellionViewer;

/// <summary>The right-hand panel (system, fleet), the fleet list, and the move-order mode.</summary>
public partial class GameScreen
{
    int _moveFleet = -1;
    bool _moveHq;
    string _moveBanner;
    bool? _retreatPending;
    int _buildQty = 1;
    readonly HashSet<int> _splitSel = new();
    int _lastSelectedSystem = -2, _lastSelectedFleet = -2;

    Side Me => S.Player;
    FleetModule Fm => S.Host.Fleets;

    // ================================================================== view events, move mode

    void OnViewChanged()
    {
        if (_moveFleet >= 0) UpdatePreview();
        if (_moveHq) UpdateHqPreview();
        if (_moveChar >= 0) UpdateCharPreview();
        if (_pickMission) UpdateMissionPreview();
        int h = _view.Hovered;
        if (h >= 0 && _moveFleet < 0 && !_moveHq && _moveChar < 0 && !_pickMission)
        {
            string hn = G.Systems[h].Name;
            var (known, owner) = S.KnownOwnerOf(hn);
            int? seen = S.LastSeenDay(hn);
            string age = owner == Me || S.Sees(hn) || seen == null ? "" : $"  (last seen day {seen})";
            _hoverLabel.Text = $"{hn}  -  {(!S.Explored(hn) && owner != Me ? "unexplored" : !known ? "owner unknown" : owner == null ? "neutral" : S.You(owner.Value))}{age}";
            _hoverLabel.Visible = true;
        }
        else _hoverLabel.Visible = false;

        if (_view.Selected != _lastSelectedSystem || _view.SelectedFleet != _lastSelectedFleet)
        {
            if (_view.SelectedFleet != _lastSelectedFleet) _splitSel.Clear();
            _lastSelectedSystem = _view.Selected; _lastSelectedFleet = _view.SelectedFleet;
            if (_moveFleet >= 0 && _view.SelectedFleet != _moveFleet) CancelMove();
            if (_charTab && (_view.Selected >= 0 || _view.SelectedFleet >= 0)) { _charTab = false; RefreshCharButton(); } // a map selection closes the Characters list
            if (_missionTab && (_view.Selected >= 0 || _view.SelectedFleet >= 0)) { _missionTab = false; RefreshMissionButton(); } // and the Missions list
            if (_plan != null && !_pickMission && (_view.Selected >= 0 || _view.SelectedFleet >= 0)) _plan = null; // and the planner
            _panelDirty = true; _panelTimer = 0;
        }
    }

    void StartMove(Fleet f)
    {
        _moveFleet = f.Id;
        _view.PickMode = true;
        _moveBanner = "Click a destination system (Esc cancels)";
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void CancelMove()
    {
        _moveFleet = -1; _moveHq = false; _moveChar = -1; _pickMission = false; _view.PickMode = false; _view.PathPreview = null;
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void UpdatePreview()
    {
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f) || f.At == null) { CancelMove(); return; }
        int h = _view.Hovered;
        if (h < 0 || G.Systems[h].Name == f.At)
        {
            _view.PathPreview = null;
            _moveBanner = $"Move {f.Name}: click a destination system (Esc cancels)";
        }
        else
        {
            string to = G.Systems[h].Name;
            var pr = Fm.FindPath(f.At, to, Fm.SpeedOf(f));
            if (pr.Ok)
            {
                _view.PathPreview = pr.Systems;
                _view.PathColor = new Color(1f, 1f, 1f, 0.9f);
                _moveBanner = $"{f.Name} to {to}: {pr.TotalTicks} days, arrives day {S.Game.Day + pr.TotalTicks}. Click to confirm.";
            }
            else { _view.PathPreview = null; _moveBanner = $"No route from {f.At} to {to} for this fleet."; }
        }
        RefreshTop();
    }

    void OnSystemPicked(int idx)
    {
        if (_moveHq) { OnHqPicked(idx); return; }
        if (_moveChar >= 0) { OnCharPicked(idx); return; }
        if (_pickMission) { OnMissionPicked(idx); return; }
        if (_moveFleet < 0) return;
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f) || f.At == null) { CancelMove(); return; }
        string to = G.Systems[idx].Name;
        if (to == f.At) return;
        var pr = Fm.FindPath(f.At!, to, Fm.SpeedOf(f));
        long seq = Fm.SubmitMove(Me, f.Id, to);
        S.TrackFleet(f.Id, seq, $"Move {f.Name} to {to}");
        if (pr.Ok) S.PlanMove(f.Id, new GameSession.PlannedMove(to, pr.Systems.Select(i => G.Systems[i].Name).ToList(), pr.TotalTicks, S.Game.Day + pr.TotalTicks));
        S.Say($"Order sent: move {f.Name} to {to}.", MsgKind.Info, to);
        AddLogItem(S.Messages[^1]);
        CancelMove();
        _fleetListDirty = true;
    }

    // ================================================================== small UI helpers

    T Add<T>(T c) where T : Control { _pv.AddChild(c); return c; }

    Label Line(string text, Color? color = null, int size = 0)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (color is Color c) l.AddThemeColorOverride("font_color", c);
        if (size > 0) l.AddThemeFontSizeOverride("font_size", size);
        return Add(l);
    }

    void Heading(string text)
    {
        Add(new Control { CustomMinimumSize = new Vector2(0, 4) });
        Line(text, new Color(1f, 0.85f, 0.4f), 16);
        Add(new HSeparator());
    }

    Button Act(string text, Action a, string tip = null, bool disabled = false)
    {
        var b = Btn(text, a, tip);
        b.Disabled = disabled;
        b.Alignment = HorizontalAlignment.Left;
        b.ClipText = false;
        b.AutowrapMode = TextServer.AutowrapMode.WordSmart; // long entries wrap instead of widening the panel over the map
        b.CustomMinimumSize = new Vector2(0, 30);
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return Add(b);
    }

    void ClearPanel()
    {
        foreach (var c in _pv.GetChildren()) { _pv.RemoveChild(c); c.QueueFree(); }
    }

    static string Plural(int n, string one, string many) => n == 1 ? one : many;

    string Counts(IEnumerable<string> names)
    {
        var parts = names.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} x {g.Key}").ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    static readonly Color Dim = new(1, 1, 1, 0.6f);
    static readonly Color Bad = new(1f, 0.5f, 0.45f);
    static readonly Color Good = new(0.55f, 0.95f, 0.55f);

    // ================================================================== panel switch

    void RebuildPanel()
    {
        ClearPanel();
        if (_plan != null) { MissionPlanner(); return; }
        if (_missionTab) { MissionsPanel(); return; }
        if (_charTab) { CharactersPanel(); return; }
        // only own fleets can be selected: enemy fleets are known by what was seen, not by id (rule: m5-fog-and-hq §N8)
        if (_view.SelectedFleet >= 0 && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var fleet) && fleet.Side == Me) { FleetPanel(fleet); return; }
        if (_view.SelectedFleet >= 0) _view.SelectedFleet = -1;
        if (_view.Selected >= 0) { SystemPanel(_view.Selected); return; }
        HelpPanel();
    }

    void HelpPanel()
    {
        Line("Galaxy", null, 20);
        Add(new HSeparator());
        Line("Click a system to see it, and to build there if it is yours.");
        Line("Click one of your fleets (white-outlined triangles, or the list on the left) to give it orders.");
        Line("Press Characters (or C) to see your leaders. " + LeaderHelp);
        Line("Press Missions (or M) for your agents' missions. " + MissionAdvice);
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Line("Colours: orange = Alliance, green = Empire, grey = neutral. The thin ring is the sector. A big double ring marks a headquarters, a red ring an uprising, a yellow ring a blockade.", Dim);
        Line("Fog of war: you see live only the systems you hold or have a fleet at. Others show what you saw last (with the day); dim hollow dots are unexplored. Enemy fleets show only where you can see them.", Dim);
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Line("Space: play / pause     N: one day     1-4: speed     C: characters     M: missions\nWheel or +/-: zoom     drag or arrows: pan\nEsc: cancel / deselect", Dim);
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Line("Orders are carried out when the next day starts, so press Play (or +1 day) after giving one. If the core refuses an order, the reason appears in the messages.", Dim);
    }

    // ================================================================== system panel

    void SystemPanel(int idx)
    {
        var sys = G.Systems[idx];
        string name = sys.Name;
        var eco = S.Host.Economy;
        bool mine = S.OwnerOf(name) == Me; // own holdings are the side's own state (truth)
        var (ownerKnown, owner) = S.KnownOwnerOf(name);
        var pic = S.Picture(name);
        bool sees = S.Sees(name);

        Line(name, null, 22);
        string hqNote = name == S.MyHqSystem ? "   (YOUR HEADQUARTERS)"
            : name == S.EnemyHqSystem ? (Me == Side.Empire ? "   (REBEL HQ - take it to win)" : "   (ENEMY CAPITAL - take it to win)") : "";
        Line($"{(sys.IsOutsideSectors ? "Outside the sectors" : sys.Sector)}{hqNote}", Dim);
        Add(new HSeparator());

        // ---- fog of war: anything not yours shows only the side's picture, with its age. rule: m5-fog-and-hq §N2, §N8
        if (!mine && pic == null)
        {
            Line("Unexplored. You know nothing about this system yet: who holds it, what is built there or who is there.", Dim);
            Line("Send a fleet there to look. Systems you hold or have a fleet at are seen live; the rest show what you saw last.", Dim);
            CharactersAtSystem(name, false);
            FleetsHere(name, sees, null);
            return;
        }
        if (!mine)
        {
            int? last = S.LastSeenDay(name);
            Line(sees ? "Seen live now (you have a fleet here)." : last is int ld ? $"Last seen day {ld} ({S.Game.Day - ld} days ago). It may have changed since." : "Not seen since the start of the war.",
                sees ? Good : new Color(1f, 0.85f, 0.4f));
        }
        Line(!ownerKnown ? "Owner: unknown" : owner == null ? "Owner: nobody (neutral)" : $"Owner: {S.You(owner.Value)}", !ownerKnown || owner == null ? Dim : (mine ? Good : Bad));
        if (mine) Line($"Support: Alliance {eco.SupportOf(name, Side.Alliance)}%, Empire {eco.SupportOf(name, Side.Empire)}%");
        else if (pic.Owner is { } po) Line($"Support: Alliance {po.Value.AllianceSupport}% (as last seen)");
        if (mine || sees)
        {
            if (S.Host.Support.IsUprising(name)) Line(mine ? "UPRISING! The people are in revolt here: yards and mines stop until it ends." : "Uprising: the people here are in revolt.", Bad);
            if (S.Game.State.Blockades.TryGetValue(name, out var blockader)) Line(blockader == Me ? "Blockaded by you: mines and yards here stop." : $"Blockaded by the {S.NameOf(blockader)}: mines and yards stop.", Bad);
        }
        if (mine)
        {
            int have = S.Host.Support.RegimentCount(name), need = S.Host.Support.RequiredGarrison(name);
            Line($"Garrison: {have} regiment(s), {need} needed to keep order.", have < need ? Bad : Dim);
            var slots = eco.SlotsOf(name);
            Line($"Energy slots: {eco.EnergyUsed(name)} used of {slots.Energy}, {eco.FreeEnergy(name)} free");
            Line($"Raw material slots (for mines): {eco.RawUsed(name)} used of {eco.EffectiveRaw(name)}, {eco.FreeRaw(name)} free");
        }
        else if (pic.Status is { } st)
            Line($"Energy slots: {st.Value.Energy}   Raw material slots: {st.Value.Raw}{(st.Value.Populated ? "" : "   (unpopulated)")}", Dim);

        // facilities
        Heading("Facilities");
        if (mine)
        {
            var here = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == name).ToList();
            Line(here.Count(o => o.Completed) == 0 ? "none" : Counts(here.Where(o => o.Completed).Select(o => o.Class)));
            var building = here.Where(o => !o.Completed).ToList();
            if (building.Count > 0) Line($"Under construction: {Counts(building.Select(o => o.Class))}", Dim);
            foreach (var fam in new[] { YardFamily.Construction, YardFamily.Shipyard, YardFamily.Training })
            {
                var p = S.Host.Construction.ProjectAt(name, fam);
                if (p == null || p.Units.Count == 0) continue;
                var cur = eco.Find(p.Units[0]);
                int cost = cur != null && eco.Costs.TryGet(cur.Class, out var cr) ? cr.Cost : 0;
                Line($"{FamilyName(fam)} is building {Counts(p.Units.Select(u => eco.Find(u)?.Class ?? "?"))}; current one {p.Progress}/{cost}", Good);
                Act($"Stop building at {name} ({FamilyName(fam).ToLowerInvariant()})", () => StopBuild(name, fam));
            }
        }
        else if (pic.Facilities is { } fac)
            Line((fac.Value.Count == 0 ? "none" : string.Join(", ", fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))) + $"   (seen day {fac.Stamp})");
        else Line("unknown (nobody has looked at the ground yet)", Dim);

        // the Rebel HQ: the Alliance can move it (rule: m5-fog-and-hq §F6)
        if (mine && Me == Side.Alliance && name == S.MyHqSystem) HqSection(name);

        // the build menu comes right after the facilities so a new player does not have to scroll for it
        if (mine) BuildMenu(name);

        // troops
        Heading("Regiments on the planet");
        if (mine)
        {
            var regs = S.Host.Troops.RegimentsOnSystem(name);
            if (regs.Count == 0) Line("none", Dim);
            foreach (var g in regs.GroupBy(r => r.Side).OrderBy(g => g.Key))
                Line($"{(g.Key == null ? "Neutral" : S.You(g.Key.Value))}: {Counts(g.Select(r => r.Class))}");
        }
        else if (pic.Ground is { } gr)
        {
            if (gr.Value.All(x => x.Total == 0)) Line($"none seen (day {gr.Stamp})", Dim);
            foreach (var g in gr.Value.Where(x => x.Total > 0))
                Line($"{S.You(g.Side)}: {string.Join(", ", g.Units.Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))}   (seen day {gr.Stamp})");
        }
        else Line("unknown", Dim);

        CharactersAtSystem(name, mine);
        SpecialForcesAtSystem(name);
        if (CanSendFrom(name)) Act($"Send a mission from {name}...", () => OpenPlanner(name), "Pick a team standing here (characters and special forces), the kind of mission and the target.");
        FleetsHere(name, sees || mine, pic);

        if (mine) { }
        else if (S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == name))
            Line(owner != null ? "To take this system: bombard it, then invade with troops aboard your fleet (select your fleet)." : "Nobody holds this planet. Press Invade on your fleet with regiments aboard.", Dim);
        else Line(owner == null && ownerKnown ? "Nobody holds this planet. Send a fleet with regiments aboard, then press Invade. You can only build on systems you own." : "You can only build on systems you own. To take it, send a fleet with regiments aboard and press Invade.", Dim);
    }

    /// <summary>Own fleets here (clickable), then the enemy fleets the side sees now, or the ones it saw last time it looked.</summary>
    void FleetsHere(string name, bool live, Rebellion.Core.Fog.SystemPicture pic)
    {
        Heading("Fleets here");
        var ownHere = S.Game.State.Fleets.Values.Where(f => f.At == name && f.Side == Me).ToList();
        foreach (var f in ownHere)
        {
            var fl = f;
            Act($"{f.Name}  ({Counts(Fm.ClassNamesOf(f))})", () => SelectFleet(fl.Id, false), "Click to select this fleet");
        }
        if (live)
        {
            var enemy = S.VisibleEnemyFleetsAt(name);
            foreach (var e in enemy) Line($"[enemy] {S.NameOf(e.Side)} fleet: {e}", Bad);
            if (ownHere.Count == 0 && enemy.Count == 0) Line("none", Dim);
        }
        else
        {
            if (ownHere.Count == 0) Line("none of yours", Dim);
            if (pic?.Fleets is { } seen && seen.Value.Count > 0)
                foreach (var e in seen.Value) Line($"[enemy, seen day {seen.Stamp}] {S.NameOf(e.Side)} fleet: {e}", Dim);
            else Line("Enemy fleets: unknown (you have no eyes here).", Dim);
        }
    }

    // ================================================================== the Rebel HQ (Alliance)

    void HqSection(string at)
    {
        Heading("Headquarters");
        Line("Your HQ is here. If the Empire takes this system, the HQ is lost and you lose. The Empire does not know where it is unless its spies find it.", Dim, 13);
        if (S.PendingHqMove is string to) { Line($"Waiting for the next day: move the HQ to {to}.", new Color(1f, 0.85f, 0.4f)); return; }
        bool pinned = S.Fog.IsPinned(at);
        Act(_moveHq ? "Cancel HQ move" : "Move HQ...", () => { if (_moveHq) CancelMove(); else StartHqMove(); },
            "Then click one of your systems. The HQ travels on the lanes (it cannot be attacked on the way) and arrives on the day shown.");
        if (pinned) Line("A large enemy blockade here pins the HQ: the move will be refused until the blockade is broken.", Bad);
    }

    void StartHqMove()
    {
        _moveHq = true; _moveFleet = -1;
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
                _view.PathColor = new Color(1f, 0.95f, 0.5f, 0.9f);
                bool held = S.OwnerOf(to) == Me;
                _moveBanner = held ? $"HQ to {to}: {pr.TotalTicks} days, arrives day {S.Game.Day + pr.TotalTicks}. Click to confirm."
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
        AddLogItem(S.Messages[^1]);
        CancelMove();
    }

    static string FamilyName(YardFamily f) => f switch { YardFamily.Shipyard => "Shipyard", YardFamily.Training => "Training facility", _ => "Construction yard" };

    // ================================================================== build menu

    void StopBuild(string system, YardFamily fam)
    {
        long seq = S.Host.Construction.SubmitStop(Me, system, fam);
        S.Track(seq, $"Stop building at {system}");
        S.Say($"Order sent: stop the {FamilyName(fam).ToLowerInvariant()} work at {system}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1]);
        _panelDirty = true; _panelTimer = 0; RefreshTop();
    }

    // Which build sections are open (kept while the panel is rebuilt, and across systems).
    readonly HashSet<string> _openSections = new();

    static readonly string[] FacilityPriority = { "Mine", "Refinery", "Construction Yard", "Orbital Shipyard", "Training Facility" };

    static string SectionOf(ObjectCategory c) => c switch
    {
        ObjectCategory.Facility => "Facilities",
        ObjectCategory.CapitalShip => "Ships",
        ObjectCategory.Fighter => "Fighters",
        _ => "Troops",
    };

    void BuildMenu(string system)
    {
        var eco = S.Host.Economy; var cons = S.Host.Construction;
        var yards = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)).ToList();

        Heading("Build");
        Line("Click a heading to open it. Orders start when the next day begins.", Dim, 13);
        if (yards.Count > 0)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "How many: " });
            var qty = new SpinBox { MinValue = 1, MaxValue = 20, Value = _buildQty, Step = 1, CustomMinimumSize = new Vector2(80, 0) };
            qty.ValueChanged += v => _buildQty = (int)v;
            row.AddChild(qty);
            Add(row);
        }

        var p = cons.Params;
        var allRows = eco.Costs.Rows.Where(r => (Me == Side.Empire ? r.Empire : r.Alliance) && r.Cost > 0).ToList();
        foreach (var section in new[] { "Facilities", "Ships", "Fighters", "Troops" })
        {
            var rows = allRows.Where(r => SectionOf(r.Category) == section).ToList();
            if (rows.Count == 0) continue;
            var fam = Rebellion.Core.Construction.Yards.BuiltBy(rows[0].Category);
            var fy = yards.Where(y => FacilityKinds.TryParse(y.Class, out var k) && Rebellion.Core.Construction.Yards.FamilyOf(k) == fam).ToList();
            bool open = _openSections.Contains(section);
            string sec = section;
            var head = new Button { Text = $"{(open ? "v" : ">")}  {section}{(fy.Count == 0 ? $"   (needs a {FamilyName(fam).ToLowerInvariant()} here)" : "")}", Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, ToggleMode = false };
            head.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
            head.TooltipText = "Click to open or close this list";
            head.Pressed += () => { if (!_openSections.Remove(sec)) _openSections.Add(sec); _panelDirty = true; _panelTimer = 0; };
            Add(head);
            if (!open) continue;
            if (fy.Count == 0)
            {
                Line($"This planet has no {FamilyName(fam).ToLowerInvariant()}. {(fam == YardFamily.Construction ? "Build one from a planet that already has a construction yard." : "Build one under Facilities first.")}", Dim, 13);
                continue;
            }
            int adv = fy.Count(y => FacilityKinds.TryParse(y.Class, out var k) && Rebellion.Core.Construction.Yards.IsAdvanced(k));
            int std = fy.Count - adv;
            long refined = eco.Refined(Me);
            // Most useful first: things you can start now, then the cheap ones; mines and refineries lead the facility list.
            bool Usable(CostRow r) => cons.IsResearched(Me, r.Name) && !(r.Category == ObjectCategory.Facility && FacilityKinds.TryParse(r.Name, out var kk) && eco.CheckPlace(system, kk) != null);
            int Prio(CostRow r) { int i = Array.IndexOf(FacilityPriority, r.Name); return r.Category == ObjectCategory.Facility ? (i >= 0 ? i : 50) : 0; }
            foreach (var r in rows.OrderBy(r => Usable(r) ? 0 : 1).ThenBy(Prio).ThenBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal))
            {
                // About how long one unit takes: cost points, each yard adds one point per cycle (rule: m2-construction §Build time).
                long perDay = (long)std * p.AdvancedCycleDays + (long)adv * p.StandardCycleDays; // scaled by rateStd*rateAdv
                int days = (int)(((long)r.Cost * p.StandardCycleDays * p.AdvancedCycleDays + perDay - 1) / Math.Max(1, perDay));
                string hint = "";
                if (!cons.IsResearched(Me, r.Name)) hint = "  (not researched)";
                else if (r.Category == ObjectCategory.Facility && FacilityKinds.TryParse(r.Name, out var kind) && eco.CheckPlace(system, kind) != null) hint = "  (no free slot)";
                else if (r.Cost > refined) hint = "  (waits for refined)";
                var row = r;
                var fam2 = Rebellion.Core.Construction.Yards.BuiltBy(r.Category);
                var btn = Act($"{r.Name}   {r.Cost} refined, upkeep {r.Maintenance}, ~{days} days{hint}", () => SubmitBuild(system, fam2, row),
                    $"Costs {r.Cost} refined material in total and {r.Maintenance} upkeep. Takes about {days} days per unit once the refined material is there.");
                if (!Usable(r)) btn.Modulate = new Color(1, 1, 1, 0.55f);
            }
        }
    }

    void SubmitBuild(string system, YardFamily fam, CostRow row)
    {
        int n = Math.Max(1, _buildQty);
        string dest = row.Category == ObjectCategory.Facility ? system : null;
        long seq = S.Host.Construction.SubmitBuild(Me, system, fam, row.Name, n, dest);
        string what = $"build {n} x {row.Name} at {system}";
        S.Track(seq, char.ToUpper(what[0]) + what[1..]);
        S.Say($"Order sent: {what}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1]);
        RefreshTop();
    }

    // ================================================================== fleet panel

    static int ArrivalDay(FleetTransit t)
    {
        int finalDay = t.DueDay;
        for (int h = t.HopIndex + 1; h < t.HopTicks.Count; h++) finalDay += t.HopTicks[h];
        return finalDay;
    }

    string WhereIs(Fleet f)
    {
        if (f.Transit is { } t && f.At == null)
            return $"Travelling {t.Path[t.HopIndex]} -> {t.Path[t.HopIndex + 1]} (next stop day {t.DueDay}). Heading for {t.Path[^1]}, arriving day {ArrivalDay(t)}.";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"At {f.At}. Will leave when the next day starts: moving to {pm.To}, arrives day {pm.ArrivalDay}.";
        return $"At {f.At}.";
    }

    /// <summary>One short line for the fleet list: where it is, where it is going and when it gets there.</summary>
    string StatusOf(Fleet f)
    {
        if (f.Transit is { } t && f.At == null) return $"moving to {t.Path[^1]}, arrives day {ArrivalDay(t)}";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"at {f.At}; moving to {pm.To}, arrives day {pm.ArrivalDay}";
        if (S.PendingOf(f.Id).Any()) return $"at {f.At}; order waiting";
        return $"at {f.At}";
    }

    void FleetPanel(Fleet f)
    {
        bool own = f.Side == Me;
        var troops = S.Host.Troops;
        Line(f.Name, null, 22);
        Line(own ? $"Your fleet ({S.NameOf(f.Side)})" : $"Enemy fleet ({S.NameOf(f.Side)})", own ? Good : Bad);
        Add(new HSeparator());
        Line(WhereIs(f));
        foreach (var pend in S.PendingOf(f.Id)) Line($"Waiting for the next day: {pend}", new Color(1f, 0.85f, 0.4f));
        Line($"Strength: {Fm.StrengthOf(f):0.0}   Speed rating: {Fm.SpeedOf(f)} (lower is faster)");
        Line($"Ships: {Counts(f.Ships.Select(id => S.Host.Economy.Find(id)?.Class ?? "?"))}");
        var squads = Fm.ClassNamesOf(f).ToList();
        int shipCount = f.Ships.Count;
        var fighters = squads.Skip(shipCount).ToList();
        if (fighters.Count > 0) Line($"Fighters: {Counts(fighters)}");
        var aboard = troops.RegimentsAboard(f);
        Line($"Regiments aboard: {(aboard.Count == 0 ? "none" : Counts(aboard.Select(a => a.Regiment.Class)))}   (room for {Fm.FreeTroopSlots(f)} more)");

        if (!own) return;
        bool atSystem = f.At != null;
        string at = f.At;
        var owner = at == null ? null : S.OwnerOf(at);

        Heading("Orders");
        Act(_moveFleet == f.Id ? "Cancel move" : "Move...", () => { if (_moveFleet == f.Id) CancelMove(); else StartMove(f); },
            "Then click a destination system. The route and arrival day are shown before you confirm.", !atSystem);
        if (!atSystem) Line("The fleet is travelling; give orders when it arrives.", Dim);

        if (atSystem)
        {
            // troops
            if (owner == Me)
            {
                var avail = S.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == Me && o.System == at && o.AboardShip == 0)
                    .OrderBy(o => o.Id).Select(o => o.Id).ToList();
                int room = Fm.FreeTroopSlots(f);
                int nAll = Math.Min(avail.Count, room);
                int need = S.Host.Support.RequiredGarrison(at!); // rule: m2-support (garrison); taking regiments below it risks a revolt
                int nSafe = Math.Min(Math.Max(0, avail.Count - need), room);
                if (nAll == 0)
                    Act($"Load troops from {at} ({avail.Count} here, room for {room})", () => { }, "Needs regiments on the planet and free troop room on the fleet (transports have it).", true);
                if (nSafe > 0)
                    Act($"Load troops: {nSafe} ({avail.Count} here, room for {room}; the garrison stays)", () => LoadTroops(f, avail.Take(nSafe).ToList()),
                        $"Takes {nSafe} regiment(s) off the planet onto this fleet and leaves enough to keep order.");
                if (nAll > nSafe)
                    Act($"Load every regiment: {nAll} (the planet may revolt)", () => LoadTroops(f, avail.Take(nAll).ToList()),
                        "Takes all the regiments you have room for. Fewer than the garrison the planet needs can make the people rise up (yards and mines stop).");
            }
            if (aboard.Count > 0)
                Act($"Unload all troops at {at}", () => UnloadTroops(f, aboard.Select(a => a.Regiment.Id).ToList()),
                    "Lands the regiments on the planet (only on a system you hold).");

            // attack
            if (owner != Me)
            {
                bool attackPending = S.PendingOf(f.Id).Any(p => p.StartsWith("Bombard", StringComparison.Ordinal) || p.StartsWith("Invade", StringComparison.Ordinal));
                Heading($"Attack {at}");
                Line("Bombard weakens defences from orbit. Invade lands the regiments aboard.", Dim);
                foreach (var (cat, label, tip) in new[]
                {
                    (BombardCategory.Military, "Bombard: military targets", "Shoots at regiments and defence buildings."),
                    (BombardCategory.Civilian, "Bombard: civilian targets", "Hurts the planet's support for its owner."),
                    (BombardCategory.General, "Bombard: everything", "Shoots at everything."),
                })
                {
                    var c = cat;
                    Act(label, () => Bombard(f, c), tip, attackPending);
                }
                Act($"Invade {at} with {aboard.Count} regiment(s)", () => Invade(f), "Lands the regiments aboard to take the planet.", aboard.Count == 0 || attackPending);
                if (attackPending) Line("A fleet can carry out one attack order at a time. Press +1 day (or Play) so this one starts, then give the next.", Dim);
            }
        }

        OfficersSection(f);

        // battle behaviour
        Heading("When an enemy fleet arrives");
        bool retreat = _retreatPending ?? S.Host.Battles.OnContactOf(Me) == OnContactMode.Retreat;
        var cb = new CheckButton { Text = "Retreat instead of fighting (all your fleets)", ButtonPressed = retreat, FocusMode = FocusModeEnum.None, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cb.Toggled += on => SetRetreat(on);
        Add(cb);

        // split / merge
        if (atSystem)
        {
            Heading("Split or merge");
            if (f.Ships.Count > 1)
            {
                Line("Tick ships to split into a new fleet:", Dim);
                foreach (int id in f.Ships)
                {
                    int sid = id;
                    var cbs = new CheckBox { Text = $"{S.Host.Economy.Find(id)?.Class ?? "?"} #{id}", ButtonPressed = _splitSel.Contains(id), FocusMode = FocusModeEnum.None };
                    cbs.Toggled += on => { if (on) _splitSel.Add(sid); else _splitSel.Remove(sid); };
                    Add(cbs);
                }
                Act("Split the ticked ships into a new fleet", () => Split(f), null);
            }
            else Line("A fleet of one ship cannot be split.", Dim);
            var others = S.Game.State.Fleets.Values.Where(o => o.Id != f.Id && o.Side == Me && o.At == at).ToList();
            foreach (var o in others)
            {
                var other = o;
                Act($"Merge this fleet into {o.Name}", () => Merge(f, other), null);
            }
        }
    }

    void Sent(string what, string system = null)
    {
        S.Say($"Order sent: {what}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1]);
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void LoadTroops(Fleet f, List<int> ids)
    {
        if (ids.Count == 0) return;
        S.TrackFleet(f.Id, S.Host.Troops.SubmitLoad(Me, f.Id, ids), $"Load troops onto {f.Name}");
        Sent($"load {ids.Count} regiment(s) onto {f.Name}", f.At);
    }

    void UnloadTroops(Fleet f, List<int> ids)
    {
        S.TrackFleet(f.Id, S.Host.Troops.SubmitUnload(Me, f.Id, ids), $"Unload troops from {f.Name}");
        Sent($"unload {ids.Count} regiment(s) from {f.Name}", f.At);
    }

    void Bombard(Fleet f, BombardCategory cat)
    {
        S.TrackFleet(f.Id, S.Host.Bombardment.SubmitBombard(Me, f.Id, f.At!, cat), $"Bombard {f.At} with {f.Name}");
        Sent($"{f.Name} bombards {f.At} ({cat.ToString().ToLowerInvariant()})", f.At);
    }

    void Invade(Fleet f)
    {
        S.TrackFleet(f.Id, S.Host.Assault.SubmitAssault(Me, f.Id, f.At!), $"Invade {f.At} with {f.Name}");
        Sent($"{f.Name} invades {f.At}", f.At);
    }

    void SetRetreat(bool on)
    {
        _retreatPending = on;
        S.Track(S.Host.Battles.SubmitSetOnContact(Me, on ? OnContactMode.Retreat : OnContactMode.Simulate), "Change the battle behaviour");
        S.Say(on ? "Order sent: your fleets will retreat when an enemy arrives." : "Order sent: your fleets will fight when an enemy arrives.");
        AddLogItem(S.Messages[^1]);
        RefreshTop();
    }

    void Split(Fleet f)
    {
        var ids = _splitSel.Where(f.Ships.Contains).OrderBy(x => x).ToList();
        if (ids.Count == 0) { Toast("Tick at least one ship first."); return; }
        S.TrackFleet(f.Id, S.Host.Fleets.SubmitSplitFleet(Me, f.Id, ids), $"Split {f.Name}");
        _splitSel.Clear();
        Sent($"split {ids.Count} ship(s) off {f.Name}", f.At);
    }

    void Merge(Fleet f, Fleet into)
    {
        S.TrackFleet(f.Id, S.Host.Fleets.SubmitMergeFleets(Me, f.Id, into.Id), $"Merge {f.Name} into {into.Name}");
        Sent($"merge {f.Name} into {into.Name}", f.At);
    }

    // ================================================================== fleet list

    void RebuildFleetList()
    {
        foreach (var c in _fleetList.GetChildren()) { _fleetList.RemoveChild(c); c.QueueFree(); }
        var mine = S.Game.State.Fleets.Values.Where(f => f.Side == Me).ToList();
        _fleetListPanel.Visible = mine.Count > 0;
        foreach (var f in mine.Take(40))
        {
            var fl = f;
            var b = new Button { Text = $"{f.Name}\n   {StatusOf(f)}", Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, Flat = true, ClipText = true, TooltipText = $"{f.Name}: {StatusOf(f)}. Click to select." };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Pressed += () => SelectFleet(fl.Id, true);
            _fleetList.AddChild(b);
        }
    }

    public void SelectFleet(int id, bool centre)
    {
        if (!S.Game.State.Fleets.TryGetValue(id, out var f) || f.Side != Me) return;
        _view.SelectedFleet = id;
        string sys = f.At ?? f.Transit?.Path[f.Transit.HopIndex + 1];
        if (centre && sys != null && G.TryFindSystem(sys, out int idx)) _view.Centre(idx);
        OnViewChanged();
        _panelDirty = true; _panelTimer = 0;
    }

    public void SelectSystem(string name)
    {
        if (G.TryFindSystem(name, out int idx)) { _view.Select(idx); OnViewChanged(); }
    }
}
