using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Map layers (user story A5) and a system in one row for the selection card; the sector window (A3) lives in GameScreen.SectorView.cs. A layer paints a soft halo behind each system for one question
/// (where is support low, which of my systems is in trouble, which yards are idle, how old is my picture of a place, where are the slots).
/// Every value comes from what the player's side may know: its own systems are read live, the rest from its picture with the picture's age.
/// </summary>
public partial class GameScreen
{
    enum MapLayer { Owners, Support, Trouble, Yards, Intel, Slots }

    MapLayer _layer = MapLayer.Owners;
    Button _routesChip, _deliveriesChip;
    Label _layerKey;
    PanelContainer _layerKeyCard;

    static readonly (MapLayer Layer, string Name, string Tip)[] Layers =
    {
        (MapLayer.Owners, "Owners", "Who holds each system: blue you, orange the enemy, grey neutral."),
        (MapLayer.Support, "Support", "How much the people of each system support you: blue high, grey even, orange low. Systems you don't see show the support you saw last."),
        (MapLayer.Trouble, "Trouble", "Your systems with a problem: red for an uprising or an enemy fleet there, yellow for too few troops or a blockade."),
        (MapLayer.Yards, "Yards", "Your yards: yellow where a yard stands idle, blue where one is building."),
        (MapLayer.Intel, "Intel age", "How fresh your picture of each system is: bright where you see it now, fading the longer ago you looked. Your own systems are always live."),
        (MapLayer.Slots, "Slots", "Energy and raw material slots: the bigger the halo, the more a planet can hold (mines need raw slots)."),
    };

    PanelContainer _layersCard;
    readonly List<Button> _layerButtons = new();

    /// <summary>The map layers as a short list under the menu (Paul's mockup), then Routes and Legend. The list starts closed and the Layers
    /// switch opens it; the key of a layer other than Owners shows either way, so V never changes the map without saying how.</summary>
    void BuildMapTools()
    {
        // the layers list: the top bar hangs it under its Layers box (BuildTopBar)
        // in the bar's pixel words (Paul 2026-11-15, "Yes A"): the layers on one black cell, Routes and Legend on another, 4 px apart as
        // two boxes in the bar, no divider line; the layer on, Routes shown and the Legend open are in the light colour
        _layersCard = Ui.Hud(4, 4);
        _layersCard.Visible = false;   // closed at the start: the Layers switch opens it (Paul 2026-11-14, impeccable critique)
        var v = Ui.VBox(4);
        _layersCard.AddChild(v);
        var layers = Ui.VBox(0);
        v.AddChild(DropCell(layers));
        var group = new ButtonGroup();
        foreach (var (layer, name, tip) in Layers)
        {
            var l = layer;
            var b = DropRow(name, () => SetLayer(l), tip + " (V cycles the layers)");
            b.ToggleMode = true; b.ButtonGroup = group; b.ButtonPressed = layer == _layer;
            _layerButtons.Add(b);
            layers.AddChild(b);
        }
        var shows = Ui.VBox(0);
        v.AddChild(DropCell(shows));
        _routesChip = DropRow("Lanes", CycleLanes, "Show or hide the lanes (L)");
        _routesChip.ToggleMode = true; _routesChip.ButtonPressed = true;
        shows.AddChild(_routesChip);
        // Paul 2026-11-17 ("yes!"): units and buildings on their way can crowd the map in a build wave; this hides them (kept between games)
        _deliveriesChip = DropRow("Deliveries", () => SetDeliveriesShown(!UiSettings.ShowDeliveries),
            "Show or hide your new ships, troops and buildings on their way from the yard, and troops sent alone (leaders, mission teams and the HQ always show)");
        _deliveriesChip.ToggleMode = true; _deliveriesChip.ButtonPressed = UiSettings.ShowDeliveries;
        shows.AddChild(_deliveriesChip);
        _legendButton = DropRow("Legend", ToggleLegend, "What the dots, rings, lines and icons on the map mean");
        _legendButton.ToggleMode = true;
        shows.AddChild(_legendButton);
        // the key of the current layer, on its own small card at the top left so the map's names do not run through it
        _layerKey = Ui.Label("", "Hint", wrap: true);
        _layerKey.CustomMinimumSize = new Vector2(150, 0);
        _layerKeyCard = Ui.Hud(10, 7);
        _layerKeyCard.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _layerKeyCard.MouseFilter = MouseFilterEnum.Ignore;
        _layerKeyCard.Visible = false;
        _layerKeyCard.AddChild(_layerKey);
        _topLeft.AddChild(_layerKeyCard);
    }

    void SetLayer(MapLayer l)
    {
        _layer = l;
        for (int i = 0; i < _layerButtons.Count; i++) _layerButtons[i].SetPressedNoSignal(Layers[i].Layer == l);
        _layerKey.Text = l switch
        {
            MapLayer.Support => "Support for you:\norange low · grey half · blue high",
            MapLayer.Trouble => "Trouble:\nred uprising or enemy fleet\nyellow too few troops or blockade",
            MapLayer.Yards => "Yards:\nyellow idle · blue building",
            MapLayer.Intel => "Intel age:\nbright seen now · faded long ago",
            MapLayer.Slots => "Slots:\nbigger halo, more energy and raw slots",
            _ => "",
        };
        _layerKeyCard.Visible = l != MapLayer.Owners;
        _view.QueueRedraw();
    }

    void NextLayer() => SetLayer((MapLayer)(((int)_layer + 1) % Layers.Length));

    // The halos depend only on the day and the layer: worked out once, not on every frame the map is drawn.
    GalaxyView.MapMark?[] _marks;
    (int Day, MapLayer Layer, int Orders, long Next) _marksKey = (-1, MapLayer.Owners, -1, -1);

    GalaxyView.MapMark? LayerMarkOf(int i)
    {
        if (_layer == MapLayer.Owners) return null;
        var key = (S.Game.Day, _layer, S.QueuedOrders, S.Game.NextSequence);   // a Cancel then a new order keeps the count, not the next number
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
            if (S.Host.Construction.ProjectAt(system, fam) is { } p && p.Units.Count > 0 || QueuedBuilds(system, fam).Count > 0) busy++; else idle++;
        }
        return (idle, busy);
    }

    // Which yard families each system has, rebuilt once a day (facilities only change in the day step). Looked up by key, never iterated.
    Dictionary<string, int> _yardIndex;
    int _yardIndexDay = -1;
    /// <summary>The playtest planted a yard mid-day (test only): read the yards again. In a game they change only when a day runs.</summary>
    internal void ForgetYardIndex() => _yardIndexDay = -1;

    static readonly Praedra.Core.Construction.YardFamily[] YardFamilies =
        { Praedra.Core.Construction.YardFamily.Shipyard, Praedra.Core.Construction.YardFamily.Construction, Praedra.Core.Construction.YardFamily.Training };

    /// <summary>The yard families a system has (shipyard, construction, training, in that order).</summary>
    IEnumerable<Praedra.Core.Construction.YardFamily> YardFamiliesAt(string system) => YardFamilies.Where(f => HasYard(system, f));

    /// <summary>The yard families here with nothing to build and no build order waiting for the clock: an idle yard.</summary>
    List<Praedra.Core.Construction.YardFamily> IdleYardFamilies(string system) =>
        YardFamiliesAt(system).Where(f => S.Host.Construction.ProjectAt(system, f) is not { Units.Count: > 0 } && QueuedBuilds(system, f).Count == 0).ToList();

    /// <summary>Your build orders for a yard that wait for the clock (each can still be cancelled), oldest first. They are read from the
    /// core's own order queue, so they survive a save and load. rule: m1-clock-and-orders §Orders in (withdrawing a queued order).</summary>
    List<Praedra.Core.Clock.BuildOrder> QueuedBuilds(string system, Praedra.Core.Construction.YardFamily fam) =>
        S.Game.Queue.OfType<Praedra.Core.Clock.BuildOrder>().Where(o => o.Side == Me && o.System == system && o.Family == fam).OrderBy(o => o.Sequence).ToList();

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

    // ================================================================== a system in one row (the selection card)

    /// <summary>A system in a few words, from what the player's side knows: who holds it, support, troops, slots, what is going on, and
    /// how fresh the picture is. The selection card shows it.</summary>
    internal sealed class SystemRowData
    {
        public string System;
        public Side? Owner;
        public bool Known;
        public string Support = "", Troops = "", Slots = "", Activity = "", Info = "";
        public Color? SupportColor, TroopsColor, ActivityColor, InfoColor;
    }

    internal SystemRowData SectorRow(int i)
    {
        var sys = G.Systems[i];
        string n = sys.Name;
        var eco = S.Host.Economy;
        var row = new SystemRowData { System = n };
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
                if (p == null || p.Units.Count == 0)
                {
                    if (QueuedBuilds(n, fam).Count > 0) acts.Add($"{FamilyShort(fam)} ordered");
                    else { acts.Add($"{FamilyShort(fam)} idle"); row.ActivityColor = Ui.Warn; }
                }
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
            row.Info = info.Count == 0 ? "live" : string.Join(", ", info);
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
        row.Activity = seen == null ? "?" : seen.Count == 0 ? "none seen" : string.Join("; ", seen);
        if (seen is { Count: > 0 }) row.ActivityColor = Ui.Enemy;
        // the name's colour says who holds it (blue you, orange the enemy, grey nobody); the column says how fresh that is
        string fresh = live ? "live" : S.LastSeenDay(n) is int d ? $"day {d}" : "old";
        row.Info = known ? fresh : $"owner ?, {fresh}";
        row.InfoColor = live ? Ui.Text2 : Ui.Text3;
        if (n == S.EnemyHqSystem) row.Info = (Me == Side.Hand ? "COLLECTIVE HQ, " : "ENEMY CAPITAL, ") + row.Info;
        return row;
    }

    static string FamilyShort(Praedra.Core.Construction.YardFamily f) => f switch
    {
        Praedra.Core.Construction.YardFamily.Shipyard => "Shipyard",
        Praedra.Core.Construction.YardFamily.Training => "Training",
        _ => "Yard",
    };
}
