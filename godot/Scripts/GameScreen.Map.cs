using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Map layers (user story A5) and the sector windows under the map (A3). A layer paints a soft halo behind each system for one question
/// (where is support low, which of my systems is in trouble, which yards are idle, how old is my picture of a place, where are the slots).
/// Every value comes from what the player's side may know: its own systems are read live, the rest from its picture with the picture's age.
/// </summary>
public partial class GameScreen
{
    enum MapLayer { Owners, Support, Trouble, Yards, Intel, Slots }

    MapLayer _layer = MapLayer.Owners;
    readonly List<Button> _layerButtons = new();
    Button _routesChip;
    Label _layerKey;

    static readonly (MapLayer Layer, string Name, string Tip)[] Layers =
    {
        (MapLayer.Owners, "Owners", "Who holds each system: blue you, orange the enemy, grey nobody."),
        (MapLayer.Support, "Support", "How much the people of each system support you: blue high, grey even, orange low. Systems you don't see show the support you saw last."),
        (MapLayer.Trouble, "Trouble", "Your systems with a problem: red for an uprising or an enemy fleet there, yellow for too few troops or a blockade."),
        (MapLayer.Yards, "Yards", "Your yards: yellow where a yard stands idle, blue where one is building."),
        (MapLayer.Intel, "Intel age", "How fresh your picture of each system is: bright where you see it now, fading the longer ago you looked. Your own systems are always live."),
        (MapLayer.Slots, "Slots", "Energy and raw material slots: the bigger the halo, the more a planet can hold (mines need raw slots)."),
    };

    void BuildLayerChips()
    {
        var group = new ButtonGroup();
        var lab = Ui.Label("MAP", "StatLabel", wrap: false);
        lab.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _layerChips.AddChild(lab);
        foreach (var (layer, name, tip) in Layers)
        {
            var l = layer;
            var b = Ui.Chip(name, () => SetLayer(l), tip + " (V cycles the layers)", layer == _layer, group);
            _layerButtons.Add(b);
            _layerChips.AddChild(b);
        }
        _routesChip = Ui.Chip("Routes", CycleLanes, "Show or hide the hyperspace routes (L)", true);
        _layerChips.AddChild(new VSeparator());
        _layerChips.AddChild(_routesChip);
        _layerKey = Ui.Label("", "Hint", wrap: false);
        _layerKey.Visible = false;
        _layerChips.GetParent().AddChild(_layerKey);
        _layerChips.GetParent().MoveChild(_layerKey, _layerChips.GetIndex() + 1);
    }

    void SetLayer(MapLayer l)
    {
        _layer = l;
        for (int i = 0; i < _layerButtons.Count; i++) _layerButtons[i].SetPressedNoSignal(Layers[i].Layer == l);
        _layerKey.Text = l switch
        {
            MapLayer.Support => "Support for you:  orange low  ·  grey half  ·  blue high",
            MapLayer.Trouble => "Red: uprising or enemy fleet  ·  yellow: too few troops or blockade",
            MapLayer.Yards => "Yellow: yard idle  ·  blue: building",
            MapLayer.Intel => "Bright: seen now  ·  faded: seen long ago  ·  none: never seen",
            MapLayer.Slots => "Bigger halo: more energy and raw slots",
            _ => "",
        };
        _layerKey.Visible = l != MapLayer.Owners;
        _view.QueueRedraw();
    }

    void NextLayer() => SetLayer((MapLayer)(((int)_layer + 1) % Layers.Length));

    // The halos depend only on the day and the layer: worked out once, not on every frame the map is drawn.
    GalaxyView.MapMark?[] _marks;
    (int Day, MapLayer Layer, int Orders) _marksKey = (-1, MapLayer.Owners, -1);

    GalaxyView.MapMark? LayerMarkOf(int i)
    {
        if (_layer == MapLayer.Owners) return null;
        var key = (S.Game.Day, _layer, S.QueuedOrders);
        if (_marks == null || _marksKey != key)
        {
            _marksKey = key;
            _marks = new GalaxyView.MapMark?[G.Systems.Count];
            for (int k = 0; k < _marks.Length; k++) _marks[k] = ComputeMark(k);
        }
        return _marks[i];
    }

    /// <summary>The halo of a system under the current layer, or null for none.</summary>
    GalaxyView.MapMark? ComputeMark(int i)
    {
        string n = G.Systems[i].Name;
        if (S.IsDestroyed(n)) return null;
        bool mine = S.OwnerOf(n) == Me;
        var eco = S.Host.Economy;
        switch (_layer)
        {
            case MapLayer.Support:
            {
                int? sup = mine ? eco.SupportOf(n, Me) : S.Picture(n)?.Owner is { } po ? (Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport) : null;
                if (sup is not int s) return null;
                var c = s >= 50 ? Ui.Neutral.Lerp(Ui.You, (s - 50) / 50f) : Ui.Neutral.Lerp(Ui.Enemy, (50 - s) / 50f);
                return new GalaxyView.MapMark(c, 2.4f);
            }
            case MapLayer.Trouble:
            {
                if (!mine) return null;
                if (S.Host.Support.IsUprising(n) || S.VisibleEnemyFleetsAt(n).Count > 0) return new GalaxyView.MapMark(Ui.Danger, 2.8f);
                if (S.Host.Support.RegimentCount(n) < S.Host.Support.RequiredGarrison(n) || S.Game.State.Blockades.ContainsKey(n)) return new GalaxyView.MapMark(Ui.Warn, 2.5f);
                return null;
            }
            case MapLayer.Yards:
            {
                if (!mine) return null;
                var (idle, busy) = YardStateAt(n);
                if (idle > 0) return new GalaxyView.MapMark(Ui.Warn, 2.6f);
                if (busy > 0) return new GalaxyView.MapMark(Ui.You, 2.0f);
                return null;
            }
            case MapLayer.Intel:
            {
                if (mine || S.Sees(n)) return new GalaxyView.MapMark(Ui.LeaderRoute, 2.3f);
                if (S.LastSeenDay(n) is not int seen) return null;
                int age = Math.Max(0, S.Game.Day - seen);
                float k = Math.Clamp(1f - age / 180f, 0.12f, 0.85f);
                return new GalaxyView.MapMark(new Color(Ui.LeaderRoute, k), 2.3f);
            }
            case MapLayer.Slots:
            {
                int slots = mine ? eco.SlotsOf(n).Energy + eco.EffectiveRaw(n) : S.Picture(n)?.Status is { } st ? st.Value.Energy + st.Value.Raw : -1;
                if (slots <= 0) return null;
                return new GalaxyView.MapMark(Ui.Gold, 1.3f + Math.Min(slots, 16) / 7f);
            }
        }
        return null;
    }

    /// <summary>Own yards at a system: how many stand idle (no project for their family) and how many are building.</summary>
    (int Idle, int Busy) YardStateAt(string system)
    {
        int idle = 0, busy = 0;
        foreach (var fam in new[] { Praedra.Core.Construction.YardFamily.Construction, Praedra.Core.Construction.YardFamily.Shipyard, Praedra.Core.Construction.YardFamily.Training })
        {
            if (!HasYard(system, fam)) continue;
            if (S.Host.Construction.ProjectAt(system, fam) is { } p && p.Units.Count > 0) busy++; else idle++;
        }
        return (idle, busy);
    }

    // Which yard families each system has, rebuilt once a day (facilities only change in the day step). Looked up by key, never iterated.
    Dictionary<string, int> _yardIndex;
    int _yardIndexDay = -1;

    bool HasYard(string system, Praedra.Core.Construction.YardFamily fam)
    {
        if (_yardIndex == null || _yardIndexDay != S.Game.Day)
        {
            _yardIndexDay = S.Game.Day;
            _yardIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in S.Host.Economy.Objects)
                if (o.Category == ObjectCategory.Facility && o.Completed && o.System != null && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k))
                    _yardIndex[o.System] = (_yardIndex.TryGetValue(o.System, out int m) ? m : 0) | (1 << ((int)Praedra.Core.Construction.Yards.FamilyOf(k) & 15));
        }
        return _yardIndex.TryGetValue(system, out int mask) && (mask & (1 << ((int)fam & 15))) != 0;
    }

    // ================================================================== sector windows (A3)

    readonly List<SectorWindow> _sectorWindows = new();
    Label _sectorHint;

    void BuildSectorDock()
    {
        var row = Ui.HBox(8);
        _sectorDock.AddChild(row);
        var sectors = G.Sectors.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        for (int i = 0; i < 2; i++)
        {
            var w = new SectorWindow(this, sectors) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _sectorWindows.Add(w);
            row.AddChild(w);
        }
        // start with the sector of the headquarters, and a sector next door
        string home = S.MyHqSystem ?? GameBoot.HandCapital;
        string a = SectorOf(home);
        string b = FrontierSector(a) ?? sectors.FirstOrDefault(x => x != a);
        _sectorWindows[0].Show(a);
        _sectorWindows[1].Show(b);
    }

    internal string SectorOf(string system) =>
        G.TryFindSystem(system, out int i) && !G.Systems[i].IsOutsideSectors ? G.Systems[i].Sector : null;

    /// <summary>The nearest sector to <paramref name="sector"/> where the player does not hold everything (where the war is).</summary>
    string FrontierSector(string sector)
    {
        if (sector == null) return null;
        var mine = G.Sectors.FirstOrDefault(s => s.Name == sector);
        if (mine == null) return null;
        var c = mine.Members.Select(m => new Vector2(G.Systems[m].MapX, G.Systems[m].MapY)).Aggregate(Vector2.Zero, (a, b) => a + b) / Math.Max(1, mine.Members.Count);
        return G.Sectors.Where(s => s.Name != sector && s.Members.Any(m => S.OwnerOf(G.Systems[m].Name) != Me))
            .OrderBy(s => c.DistanceSquaredTo(s.Members.Select(m => new Vector2(G.Systems[m].MapX, G.Systems[m].MapY)).Aggregate(Vector2.Zero, (a, b) => a + b) / Math.Max(1, s.Members.Count)))
            .ThenBy(s => s.Name, StringComparer.Ordinal).Select(s => s.Name).FirstOrDefault();
    }

    void ToggleSectorDock()
    {
        _sectorDock.Visible = !_sectorDock.Visible;
        _sectorsButton.SetPressedNoSignal(_sectorDock.Visible);
        if (_sectorDock.Visible) RefreshSectorWindows();
    }

    void RefreshSectorWindows()
    {
        if (_sectorDock == null || !_sectorDock.Visible) return;
        foreach (var w in _sectorWindows) w.Refresh();
    }

    /// <summary>A system picked on the map shows its sector in a window: the window that does not already show it, the older one first.</summary>
    void SectorFollowSelection(string system)
    {
        if (_sectorDock == null || !_sectorDock.Visible || SectorOf(system) is not string sec) { RefreshSectorWindows(); return; }
        if (_sectorWindows.Any(w => w.Sector == sec)) { RefreshSectorWindows(); return; }
        var older = _sectorWindows.OrderBy(w => w.ShownAt).First();
        older.Show(sec);
        RefreshSectorWindows();
    }

    /// <summary>A row of a sector window was clicked: in a move, it is the destination; else it selects the system.</summary>
    internal void SectorRowPicked(string system)
    {
        if (!G.TryFindSystem(system, out int idx)) return;
        if (InMoveMode) { OnSystemPicked(idx); return; }
        _view.Centre(idx);
        _view.Select(idx);
        ShowTab(DrawerTab.Selected);
    }

    /// <summary>One row of a sector window, from what the player's side knows.</summary>
    internal SectorWindow.RowData SectorRow(int i)
    {
        var sys = G.Systems[i];
        string n = sys.Name;
        var eco = S.Host.Economy;
        var row = new SectorWindow.RowData { System = n };
        if (S.IsDestroyed(n)) { row.Owner = null; row.Info = "destroyed"; row.InfoColor = Ui.Danger; row.Support = "-"; row.Troops = "-"; row.Slots = "-"; row.Activity = "debris"; return row; }
        bool mine = S.OwnerOf(n) == Me;
        if (mine)
        {
            row.Owner = Me; row.Known = true;
            int sup = eco.SupportOf(n, Me);
            row.Support = $"{sup}%"; row.SupportColor = sup < 40 ? Ui.Enemy : sup >= 60 ? Ui.You : Ui.Text;
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            row.Troops = $"{have} / {need}"; row.TroopsColor = have < need ? Ui.Warn : Ui.Text;
            var slots = eco.SlotsOf(n);
            row.Slots = $"{eco.EnergyUsed(n)} / {slots.Energy}";
            var acts = new List<string>();
            foreach (var fam in new[] { Praedra.Core.Construction.YardFamily.Shipyard, Praedra.Core.Construction.YardFamily.Construction, Praedra.Core.Construction.YardFamily.Training })
            {
                if (!HasYard(n, fam)) continue;
                var p = S.Host.Construction.ProjectAt(n, fam);
                if (p == null || p.Units.Count == 0) { acts.Add($"{FamilyShort(fam)} idle"); row.ActivityColor = Ui.Warn; }
                else acts.Add($"{eco.Find(p.Units[0])?.Class ?? "?"} {DaysLeftOf(n, fam)}d");
            }
            int fleets = S.Game.State.Fleets.Values.Count(f => f.Side == Me && f.At == n);
            if (fleets > 0) acts.Add(Ui.Count(fleets, "fleet", "fleets"));
            row.Activity = acts.Count == 0 ? "-" : string.Join(", ", acts);
            var info = new List<string>();
            if (S.Host.Support.IsUprising(n)) { info.Add("UPRISING"); row.InfoColor = Ui.Danger; }
            else if (S.VisibleEnemyFleetsAt(n).Count > 0) { info.Add("enemy here"); row.InfoColor = Ui.Danger; }
            else if (S.Game.State.Blockades.ContainsKey(n)) { info.Add("blockade"); row.InfoColor = Ui.Warn; }
            else if (have < need) { info.Add($"{need - have} short"); row.InfoColor = Ui.Warn; }
            if (n == S.MyHqSystem) info.Insert(0, "HQ");
            row.Info = info.Count == 0 ? "yours, live" : string.Join(", ", info);
            return row;
        }
        var pic = S.Picture(n);
        if (pic == null) { row.Owner = null; row.Known = false; row.Support = "-"; row.Troops = "-"; row.Slots = "-"; row.Activity = "-"; row.Info = "unexplored"; row.InfoColor = Ui.Text3; return row; }
        var (known, owner) = S.KnownOwnerOf(n);
        row.Owner = owner; row.Known = known;
        if (pic.Owner is { } po)
        {
            int s = Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport;
            row.Support = $"{s}%"; row.SupportColor = Ui.Text2;
        }
        else row.Support = "?";
        row.Troops = pic.Ground is { } gr ? gr.Value.Where(g => g.Side != Me).Sum(g => g.Total).ToString() : "?";
        row.Slots = pic.Status is { } st ? $"? / {st.Value.Energy}" : "?";
        bool live = S.Sees(n);
        var seen = live ? S.VisibleEnemyFleetsAt(n).Select(f => f.ToString()).ToList() : pic.Fleets?.Value.Select(f => f.ToString()).ToList();
        row.Activity = seen == null ? "fleets unknown" : seen.Count == 0 ? "no fleets seen" : string.Join("; ", seen);
        if (seen is { Count: > 0 }) row.ActivityColor = Ui.Enemy;
        string who = !known ? "owner ?" : owner == null ? "nobody's" : "enemy";
        row.Info = live ? $"{who}, live" : S.LastSeenDay(n) is int d ? $"{who}, day {d}" : who;
        row.InfoColor = live ? Ui.Text2 : Ui.Text3;
        if (n == S.EnemyHqSystem) row.Info = "ENEMY HQ, " + row.Info;
        return row;
    }

    static string FamilyShort(Praedra.Core.Construction.YardFamily f) => f switch
    {
        Praedra.Core.Construction.YardFamily.Shipyard => "Shipyard",
        Praedra.Core.Construction.YardFamily.Training => "Training",
        _ => "Yard",
    };
}
