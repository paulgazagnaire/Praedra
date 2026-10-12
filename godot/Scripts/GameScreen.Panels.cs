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
        int h = _view.Hovered;
        if (h >= 0 && _moveFleet < 0)
        {
            var owner = S.OwnerOf(G.Systems[h].Name);
            _hoverLabel.Text = $"{G.Systems[h].Name}  -  {(owner == null ? "neutral" : S.You(owner.Value))}";
            _hoverLabel.Visible = true;
        }
        else _hoverLabel.Visible = false;

        if (_view.Selected != _lastSelectedSystem || _view.SelectedFleet != _lastSelectedFleet)
        {
            if (_view.SelectedFleet != _lastSelectedFleet) _splitSel.Clear();
            _lastSelectedSystem = _view.Selected; _lastSelectedFleet = _view.SelectedFleet;
            if (_moveFleet >= 0 && _view.SelectedFleet != _moveFleet) CancelMove();
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
        _moveFleet = -1; _view.PickMode = false; _view.PathPreview = null;
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
        if (_moveFleet < 0) return;
        if (!S.Game.State.Fleets.TryGetValue(_moveFleet, out var f)) { CancelMove(); return; }
        string to = G.Systems[idx].Name;
        if (to == f.At) return;
        long seq = Fm.SubmitMove(Me, f.Id, to);
        S.Track(seq, $"Move {f.Name} to {to}");
        S.Say($"Order sent: move {f.Name} to {to}.", MsgKind.Info, to);
        AddLogItem(S.Messages[^1]);
        CancelMove();
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
        if (_view.SelectedFleet >= 0 && S.Game.State.Fleets.TryGetValue(_view.SelectedFleet, out var fleet)) { FleetPanel(fleet); return; }
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
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Line("Colours: orange = Alliance, green = Empire, grey = neutral. The thin ring is the sector. A big double ring marks a headquarters, a red ring an uprising, a yellow ring a blockade.", Dim);
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Line("Space: play / pause     N: one day     1-4: speed\nWheel or +/-: zoom     drag or arrows: pan\nEsc: cancel / deselect", Dim);
        Add(new Control { CustomMinimumSize = new Vector2(0, 6) });
        Line("Orders are carried out when the next day starts, so press Play (or +1 day) after giving one. If the core refuses an order, the reason appears in the messages.", Dim);
    }

    // ================================================================== system panel

    void SystemPanel(int idx)
    {
        var sys = G.Systems[idx];
        string name = sys.Name;
        var eco = S.Host.Economy;
        var owner = S.OwnerOf(name);
        bool mine = owner == Me;

        Line(name, null, 22);
        Line($"{(sys.IsOutsideSectors ? "Outside the sectors" : sys.Sector)}{(_hqs.Contains(idx) ? "   (headquarters)" : "")}", Dim);
        Add(new HSeparator());
        Line(owner == null ? "Owner: nobody (neutral)" : $"Owner: {S.You(owner.Value)}", owner == null ? Dim : (mine ? Good : Bad));
        Line($"Support: Alliance {eco.SupportOf(name, Side.Alliance)}%, Empire {eco.SupportOf(name, Side.Empire)}%");
        bool uprising = S.Host.Support.IsUprising(name);
        if (uprising) Line("UPRISING! The people are in revolt here: yards and mines stop until it ends.", Bad);
        if (S.Game.State.Blockades.TryGetValue(name, out var blockader)) Line($"Blockaded by the {S.NameOf(blockader)}: mines and yards stop.", Bad);
        if (owner != null)
        {
            int have = S.Host.Support.RegimentCount(name), need = S.Host.Support.RequiredGarrison(name);
            Line($"Garrison: {have} regiment(s), {need} needed to keep order.", have < need ? Bad : Dim);
        }

        var slots = eco.SlotsOf(name);
        Line($"Energy slots: {eco.EnergyUsed(name)} used of {slots.Energy}, {eco.FreeEnergy(name)} free");
        Line($"Raw material slots (for mines): {eco.RawUsed(name)} used of {eco.EffectiveRaw(name)}, {eco.FreeRaw(name)} free");

        // facilities
        Heading("Facilities");
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
            if (mine) Act($"Stop building at {name} ({FamilyName(fam).ToLowerInvariant()})", () => StopBuild(name, fam));
        }

        // troops
        Heading("Regiments on the planet");
        var regs = S.Host.Troops.RegimentsOnSystem(name);
        if (regs.Count == 0) Line("none", Dim);
        foreach (var g in regs.GroupBy(r => r.Side).OrderBy(g => g.Key))
            Line($"{(g.Key == null ? "Neutral" : S.You(g.Key.Value))}: {Counts(g.Select(r => r.Class))}");

        // fleets
        Heading("Fleets here");
        var fleetsHere = S.Game.State.Fleets.Values.Where(f => f.At == name).ToList();
        if (fleetsHere.Count == 0) Line("none", Dim);
        foreach (var f in fleetsHere)
        {
            var fl = f;
            Act($"{(f.Side == Me ? "" : "[enemy] ")}{f.Name}  ({Counts(Fm.ClassNamesOf(f))})", () => SelectFleet(fl.Id, false), "Click to select this fleet");
        }

        if (mine) BuildMenu(name);
        else if (owner != null && S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == name))
            Line("To take this system: bombard it, then invade with troops aboard your fleet (select your fleet).", Dim);
        else if (!mine) Line("You can only build on systems you own.", Dim);
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

    void BuildMenu(string system)
    {
        var eco = S.Host.Economy; var cons = S.Host.Construction;
        var yards = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == system
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k)).ToList();

        Heading("Build");
        if (yards.Count == 0) { Line("No yards here. Build a construction yard, shipyard or training facility first (needs a construction yard somewhere).", Dim); }
        else
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "How many: " });
            var qty = new SpinBox { MinValue = 1, MaxValue = 20, Value = _buildQty, Step = 1, CustomMinimumSize = new Vector2(80, 0) };
            qty.ValueChanged += v => _buildQty = (int)v;
            row.AddChild(qty);
            Add(row);
        }

        var p = cons.Params;
        foreach (var fam in new[] { YardFamily.Construction, YardFamily.Shipyard, YardFamily.Training })
        {
            var fy = yards.Where(y => FacilityKinds.TryParse(y.Class, out var k) && Rebellion.Core.Construction.Yards.FamilyOf(k) == fam).ToList();
            if (fy.Count == 0) continue;
            int adv = fy.Count(y => FacilityKinds.TryParse(y.Class, out var k) && Rebellion.Core.Construction.Yards.IsAdvanced(k));
            int std = fy.Count - adv;
            Heading($"{FamilyName(fam)} ({fy.Count})");
            var rows = eco.Costs.Rows.Where(r => Rebellion.Core.Construction.Yards.BuiltBy(r.Category) == fam && (Me == Side.Empire ? r.Empire : r.Alliance) && r.Cost > 0)
                .OrderBy(r => r.Category).ThenBy(r => r.ResearchOrder).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
            ObjectCategory? last = null;
            foreach (var r in rows)
            {
                if (r.Category != last)
                {
                    last = r.Category;
                    Line(r.Category switch
                    {
                        ObjectCategory.Facility => "Mines, refineries, yards and defences",
                        ObjectCategory.CapitalShip => "Ships",
                        ObjectCategory.Fighter => "Fighter squadrons",
                        ObjectCategory.Regiment => "Regiments",
                        _ => "Special forces",
                    }, Dim, 13);
                }
                // About how long one unit takes: cost points, each yard adds one point per cycle (rule: m2-construction §Build time).
                long perDay = (long)std * p.AdvancedCycleDays + (long)adv * p.StandardCycleDays; // scaled by rateStd*rateAdv
                int days = (int)(((long)r.Cost * p.StandardCycleDays * p.AdvancedCycleDays + perDay - 1) / Math.Max(1, perDay));
                string hint = "";
                if (!cons.IsResearched(Me, r.Name)) hint = "  (not researched)";
                else if (r.Category == ObjectCategory.Facility && FacilityKinds.TryParse(r.Name, out var kind) && eco.CheckPlace(system, kind) != null) hint = "  (no free slot)";
                var row = r;
                Act($"{r.Name}   {r.Cost} refined, upkeep {r.Maintenance}, ~{days} days{hint}", () => SubmitBuild(system, fam, row),
                    $"Costs {r.Cost} refined material in total and {r.Maintenance} upkeep. Takes about {days} days per unit once the refined material is there.");
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

    string WhereIs(Fleet f)
    {
        if (f.Transit is { } t && f.At == null)
        {
            int finalDay = t.DueDay;
            for (int h = t.HopIndex + 1; h < t.HopTicks.Count; h++) finalDay += t.HopTicks[h];
            return $"Travelling {t.Path[t.HopIndex]} -> {t.Path[t.HopIndex + 1]} (next stop day {t.DueDay}). Heading for {t.Path[^1]}, arriving day {finalDay}.";
        }
        return $"At {f.At}.";
    }

    void FleetPanel(Fleet f)
    {
        bool own = f.Side == Me;
        var troops = S.Host.Troops;
        Line(f.Name, null, 22);
        Line(own ? $"Your fleet ({S.NameOf(f.Side)})" : $"Enemy fleet ({S.NameOf(f.Side)})", own ? Good : Bad);
        Add(new HSeparator());
        Line(WhereIs(f));
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
                int n = Math.Min(avail.Count, room);
                Act($"Load troops from {at} ({avail.Count} here, room for {room})", () => LoadTroops(f, avail.Take(n).ToList()),
                    "Takes regiments off the planet onto this fleet. Warning: fewer regiments left to keep order on the planet.", n == 0);
            }
            if (aboard.Count > 0)
                Act($"Unload all troops at {at}", () => UnloadTroops(f, aboard.Select(a => a.Regiment.Id).ToList()),
                    "Lands the regiments on the planet (only on a system you hold).");

            // attack
            if (owner != Me)
            {
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
                    Act(label, () => Bombard(f, c), tip);
                }
                Act($"Invade {at} with {aboard.Count} regiment(s)", () => Invade(f), "Lands the regiments aboard to take the planet.", aboard.Count == 0);
            }
        }

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
        S.Track(S.Host.Troops.SubmitLoad(Me, f.Id, ids), $"Load troops onto {f.Name}");
        Sent($"load {ids.Count} regiment(s) onto {f.Name}", f.At);
    }

    void UnloadTroops(Fleet f, List<int> ids)
    {
        S.Track(S.Host.Troops.SubmitUnload(Me, f.Id, ids), $"Unload troops from {f.Name}");
        Sent($"unload {ids.Count} regiment(s) from {f.Name}", f.At);
    }

    void Bombard(Fleet f, BombardCategory cat)
    {
        S.Track(S.Host.Bombardment.SubmitBombard(Me, f.Id, f.At!, cat), $"Bombard {f.At} with {f.Name}");
        Sent($"{f.Name} bombards {f.At} ({cat.ToString().ToLowerInvariant()})", f.At);
    }

    void Invade(Fleet f)
    {
        S.Track(S.Host.Assault.SubmitAssault(Me, f.Id, f.At!), $"Invade {f.At} with {f.Name}");
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
        S.Track(S.Host.Fleets.SubmitSplitFleet(Me, f.Id, ids), $"Split {f.Name}");
        _splitSel.Clear();
        Sent($"split {ids.Count} ship(s) off {f.Name}", f.At);
    }

    void Merge(Fleet f, Fleet into)
    {
        S.Track(S.Host.Fleets.SubmitMergeFleets(Me, f.Id, into.Id), $"Merge {f.Name} into {into.Name}");
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
            string where = f.Transit is { } t && f.At == null ? $"to {t.Path[^1]}" : f.At;
            var b = new Button { Text = $"{f.Name}  ({where})", Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, Flat = true, ClipText = true };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Pressed += () => SelectFleet(fl.Id, true);
            _fleetList.AddChild(b);
        }
    }

    public void SelectFleet(int id, bool centre)
    {
        if (!S.Game.State.Fleets.TryGetValue(id, out var f)) return;
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
