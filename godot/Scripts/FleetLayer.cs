using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Clock;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Map;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Draws fleets on top of the galaxy map, using the parent GalaxyView's transform. Read-only: it looks at the core's
/// fleet state (Fleet.At, Fleet.Transit) and never changes it. Positions are purely visual.
/// Fog of war: the player's own fleets come from the core's fleet state; enemy fleets only from what the player's side sees now
/// (<see cref="EnemyFleets"/> = the fog's VisibleFleets: class counts at systems it has live access to, no names, ids or routes).
/// rule: m5-fog-and-hq §F3, §N8.
/// </summary>
public partial class FleetLayer : Control
{
    /// <summary>The player's side: its fleets get a white outline.</summary>
    public Side? Player { get; set; }
    /// <summary>Move orders given but not started yet (fleet id to plan); drawn as a route so the player sees what is pending.</summary>
    public Func<IEnumerable<KeyValuePair<int, GameSession.PlannedMove>>> Planned { get; set; }
    /// <summary>The enemy fleets the player's side sees now (system, class counts). Null = no fog (every fleet drawn from the core).</summary>
    public Func<IReadOnlyList<(string System, FleetPicture Fleet)>> EnemyFleets { get; set; }
    /// <summary>The player's own HQ while it moves (path, the hop it is on, arrival day), or null.</summary>
    public Func<(IReadOnlyList<string> Path, int HopIndex, int ArrivalDay)?> HqRoute { get; set; }
    /// <summary>The player's own characters travelling alone (path, the hop they are on), and trips ordered that start next day (Planned).</summary>
    public Func<IEnumerable<(IReadOnlyList<string> Path, int HopIndex, bool Planned)>> CharacterRoutes { get; set; }
    /// <summary>The player's own things travelling that are not fleets (leaders alone, mission teams, built units and buildings on the way,
    /// regiments sent alone, the moving HQ). Own side only: fog hides the enemy's (rule: m5-fog-and-hq §F3).</summary>
    public Func<IEnumerable<Mover>> Movers { get; set; }
    /// <summary>Draw deliveries (units and buildings on their way from the yard, troops sent alone)? Layers > Deliveries.</summary>
    public bool ShowDeliveries { get; set; } = true;
    static bool IsDelivery(MoverKind k) => k is MoverKind.Regiment or MoverKind.Ship or MoverKind.Fighter or MoverKind.Facility;
    /// <summary>Screen rectangles the words must keep off (the boxes on the map), set by the owner.</summary>
    public Func<IEnumerable<Rect2>> Boxes { get; set; }
    const float PickRadiusPx = 13f;
    const float MoverR = 5.5f;
    /// <summary>Below this zoom the fleets of one side at a system share one marker with a count.</summary>
    const float StackZoom = 2.0f;
    // One dashed style per meaning (map declutter, Paul's All A 2026-11-18): a thing on its way in its own colour at 55%, 1.2 px, dash 4
    // (the selected fleet's at 100%, 1.6 px; the moving HQ keeps its gold); an order not started yet in white at 85%, 1.6 px, dash 6.
    const float WayAlpha = 0.55f, WayWidth = 1.2f, WaySelWidth = 1.6f, WayDash = 4f;
    static readonly Color PlannedColor = new(1f, 1f, 1f, 0.85f);
    const float PlannedWidth = 1.6f, PlannedDash = 6f;
    static readonly Color HqGold = new(1f, 0.95f, 0.5f);

    Game _game;
    Galaxy _galaxy;
    /// <summary>0..1: how far through the current day the real-time clock is (0 when paused). Smooths motion between Steps.</summary>
    public float DayFraction { get; set; }

    public GalaxyView Map => GetParent<GalaxyView>();

    public void Bind(Game game, Galaxy galaxy) { _game = game; _galaxy = galaxy; QueueRedraw(); }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta) => QueueRedraw(); // markers move with the real-time fraction

    /// <summary>Blue for the player's fleets, orange for the enemy's, whichever side the player is.</summary>
    Color ColorOf(Side s) => Ui.OwnerColor(s, Player ?? Side.Hand);

    /// <summary>Zoomed out, the fleets of one side at a system share one marker with a count (Tab steps through them); zoomed in, each has its own.</summary>
    bool Stacked => Map.Zoom < StackZoom;

    /// <summary>Words on every fleet and mover: from the zoom where your system names come in, or while Alt is held (Paul 2026-11-18,
    /// pick 2). Otherwise only the selected fleet and the glyph under the mouse have words.</summary>
    bool WordsOn => Map.Zoom >= GalaxyView.NameZoom || Map.ShowAllNames;

    // how many fleets share each stacked marker, filled by Layout (looked up by key, never iterated)
    readonly Dictionary<(string System, bool Own), int> _stack = new();

    int SlotFor(string system, bool own, Dictionary<string, int> slots)
    {
        if (Stacked)
        {
            var key = (system, own);
            _stack[key] = (_stack.TryGetValue(key, out int n) ? n : 0) + 1;
            return own ? 0 : 1;
        }
        slots.TryGetValue(system, out int slot);
        slots[system] = slot + 1;
        return slot;
    }

    /// <summary>Visual position (screen) of a fleet and its heading (unit vector), or false if it cannot be placed.</summary>
    bool Place(Fleet f, Dictionary<string, int> slots, out Vector2 pos, out Vector2 heading)
    {
        heading = Vector2.Up; pos = Vector2.Zero;
        var map = Map;
        if (f.Transit is { } t && f.At == null)
        {
            if (!_galaxy.TryFindSystem(t.Path[t.HopIndex], out int a) || !_galaxy.TryFindSystem(t.Path[t.HopIndex + 1], out int b)) return false;
            int ticks = Math.Max(1, t.HopTicks[t.HopIndex]);
            // days already flown in this hop: ticks - days still to go; plus the real-time part of the current day.
            float elapsed = ticks - (t.DueDay - _game.Day) + DayFraction;
            float k = Mathf.Clamp(elapsed / ticks, 0f, 1f);
            var pa = map.SystemScreen(a); var pb = map.SystemScreen(b);
            pos = pa.Lerp(pb, k);
            var d = pb - pa;
            if (d.LengthSquared() > 0.01f) heading = d.Normalized();
            return true;
        }
        if (f.At != null && _galaxy.TryFindSystem(f.At, out int s))
        {
            int slot = SlotFor(f.At, f.Side == Player || Player == null, slots);
            pos = ParkedAt(s, slot);
            return true;
        }
        return false;
    }

    /// <summary>Own fleets (all fleets when there is no fog source).</summary>
    IEnumerable<(Fleet f, Vector2 pos, Vector2 heading)> Placed() => Layout().Own;

    /// <summary>Own fleets, then the enemy markers in the slots after them at each system.</summary>
    (List<(Fleet f, Vector2 pos, Vector2 heading)> Own, List<(string System, FleetPicture Fleet, Vector2 pos)> Enemy) Layout()
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal); // counts only, never iterated
        _stack.Clear();
        var own = new List<(Fleet, Vector2, Vector2)>();
        foreach (var f in _game.State.Fleets.Values)               // fleet id order
            if ((EnemyFleets == null || f.Side == Player) && Place(f, slots, out var p, out var h)) own.Add((f, p, h));
        var enemy = new List<(string, FleetPicture, Vector2)>();
        if (EnemyFleets != null)
            foreach (var (sys, pic) in EnemyFleets())                  // systems in ordinal order
                if (PlaceAt(sys, slots, out var p)) enemy.Add((sys, pic, p));
        return (own, enemy);
    }

    bool PlaceAt(string system, Dictionary<string, int> slots, out Vector2 pos)
    {
        pos = Vector2.Zero;
        if (!_galaxy.TryFindSystem(system, out int s)) return false;
        int slot = SlotFor(system, false, slots);
        pos = ParkedAt(s, slot);
        return true;
    }

    /// <summary>Where a parked fleet's marker goes: above the dot, stacked upwards one row per fleet, after 6 rows a new column. The first
    /// row starts just outside the system's outermost ring: on an HQ or an uprising the triangle used to sit across the rings.</summary>
    Vector2 ParkedAt(int system, int slot)
    {
        var map = Map;
        const int rows = 6;
        float colWidth = map.Zoom >= GalaxyView.NameZoom ? 80f : 14f;   // room for a short name beside each column once names show
        // the triangle reaches about 7 px below its centre (base and outline); 2 px of air above the ring
        float first = Mathf.Max(13f, map.SystemRadius + map.RingOut(system) + 9f);
        return map.SystemScreen(system) + new Vector2(-6f + colWidth * (slot / rows), -first - 16f * (slot % rows));
    }

    /// <summary>The systems where an enemy fleet marker is drawn now (for the playtest).</summary>
    public IReadOnlyList<string> EnemyMarkerSystems() => _game == null ? Array.Empty<string>() : Layout().Enemy.Select(e => e.System).ToList();

    /// <summary>The fleets drawn now from the core's fleet state (only own fleets with fog on), by id (for the playtest).</summary>
    public IReadOnlyList<int> DrawnFleetIds() => _game == null ? Array.Empty<int>() : Layout().Own.Select(o => o.f.Id).ToList();

    /// <summary>The movers with their screen position and heading (unit vector). A held one sits just below its system.</summary>
    List<(Mover m, Vector2 pos, Vector2 heading)> PlacedMovers()
    {
        var list = new List<(Mover, Vector2, Vector2)>();
        if (Movers == null) return list;
        foreach (var m in Movers())
            if ((ShowDeliveries || !IsDelivery(m.Kind)) && PlaceMover(m, out var p, out var h)) list.Add((m, p, h));
        return list;
    }

    bool PlaceMover(Mover m, out Vector2 pos, out Vector2 heading)
    {
        pos = Vector2.Zero; heading = Vector2.Right;
        var map = Map;
        var pts = new List<Vector2>(m.Path.Count);
        foreach (var n in m.Path) { if (!_galaxy.TryFindSystem(n, out int i)) return false; pts.Add(map.SystemScreen(i)); }
        if (pts.Count == 0) return false;
        int hop = Math.Clamp(m.HopIndex, 0, pts.Count - 1);
        if (m.Held || pts.Count < 2 || hop >= pts.Count - 1) { pos = pts[hop] + new Vector2(9f, 9f); return true; }
        if (m.HopTicks != null)
        {
            // as a fleet: days flown in this hop, plus the real-time part of the current day
            int ticks = Math.Max(1, m.HopTicks[hop]);
            float k = Mathf.Clamp((ticks - (m.DueDay - _game.Day) + DayFraction) / ticks, 0f, 1f);
            pos = pts[hop].Lerp(pts[hop + 1], k);
            var d = pts[hop + 1] - pts[hop];
            if (d.LengthSquared() > 0.01f) heading = d.Normalized();
            return true;
        }
        // only the trip's total is known (a mission team): spread it over the path by length
        float total = 0f;
        for (int i = 0; i + 1 < pts.Count; i++) total += pts[i].DistanceTo(pts[i + 1]);
        int all = Math.Max(1, m.TotalTicks);
        float want = Mathf.Clamp((all - (m.DueDay - _game.Day) + DayFraction) / all, 0f, 1f) * total;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            float len = pts[i].DistanceTo(pts[i + 1]);
            if (want <= len || i + 2 == pts.Count)
            {
                pos = pts[i].Lerp(pts[i + 1], len > 0.01f ? Mathf.Clamp(want / len, 0f, 1f) : 1f);
                if (len > 0.01f) heading = (pts[i + 1] - pts[i]) / len;
                return true;
            }
            want -= len;
        }
        pos = pts[^1]; return true;
    }

    /// <summary>The labels of the movers drawn now, as whole sentences (for the playtest).</summary>
    public IReadOnlyList<string> MoverLabels() => _game == null ? Array.Empty<string>() : PlacedMovers().Select(x => x.m.Label).ToList();

    /// <summary>Where the marker of an own fleet is drawn now, or null (for the playtest).</summary>
    public Vector2? MarkerOf(int fleetId) => _game == null ? null : Layout().Own.Where(o => o.f.Id == fleetId).Select(o => (Vector2?)o.pos).FirstOrDefault();

    /// <summary>The words on the map's fleets and movers right now, as placed (for the playtest).</summary>
    public IReadOnlyList<string> WordsShown() => _game == null ? Array.Empty<string>() : Words(Layout(), PlacedMovers()).Select(w => w.Text).ToList();

    /// <summary>The words placed now with their boxes (for the playtest's overlap checks).</summary>
    public IReadOnlyList<MapWord> WordsPlaced() => _game == null ? Array.Empty<MapWord>() : Words(Layout(), PlacedMovers());

    /// <summary>Screen rectangles of the fleet and mover markers and their words, so the map keeps system names off them.</summary>
    public IEnumerable<Rect2> Obstacles()
    {
        if (_game == null) return Array.Empty<Rect2>();
        var layout = Layout();
        var movers = PlacedMovers();
        return MarkerBoxes(layout, movers).Concat(Words(layout, movers).Select(w => w.Box)).ToList();
    }

    /// <summary>"3 ships, 1 squadron": what the player's side sees of an enemy fleet. The colour and the down triangle already say whose.</summary>
    static string EnemyText(FleetPicture pic)
    {
        var parts = new List<string>(2);
        if (pic.ShipCount > 0 || pic.SquadronCount == 0) parts.Add(Ui.Count(pic.ShipCount, "ship", "ships"));
        if (pic.SquadronCount > 0) parts.Add(Ui.Count(pic.SquadronCount, "squadron", "squadrons"));
        return string.Join(", ", parts);
    }

    /// <summary>The system index of the enemy marker under a screen point, or -1.</summary>
    public int PickEnemySystem(Vector2 screen)
    {
        if (_game == null) return -1;
        string best = null; float bestD = PickRadiusPx * PickRadiusPx;
        foreach (var (sys, _, p) in Layout().Enemy)
        {
            float d = p.DistanceSquaredTo(screen);
            if (d <= bestD) { bestD = d; best = sys; }
        }
        return best != null && _galaxy.TryFindSystem(best, out int idx) ? idx : -1;
    }

    /// <summary>The fleet id under a screen point, or -1.</summary>
    public int Pick(Vector2 screen)
    {
        if (_game == null) return -1;
        int best = -1; float bestD = PickRadiusPx * PickRadiusPx;
        foreach (var (f, p, _) in Placed())
        {
            float d = p.DistanceSquaredTo(screen);
            if (d < bestD || (best < 0 && d <= bestD)) { bestD = d; best = f.Id; } // a stack picks its first fleet (lowest id)
        }
        return best;
    }

    // ================================================================== what is drawn: one marker per fleet (or per stack zoomed out)

    /// <summary>The own fleet markers drawn: every fleet, or zoomed out one per stack (the selected fleet's, else the first) with its count.</summary>
    List<(Fleet f, Vector2 p, Vector2 h, int count)> DrawnOwn(List<(Fleet f, Vector2 pos, Vector2 heading)> own)
    {
        var map = Map;
        var list = new List<(Fleet, Vector2, Vector2, int)>();
        var drawnStacks = new HashSet<string>(StringComparer.Ordinal); // membership only
        foreach (var (f, p, h) in own.OrderBy(x => map.SelectedFleet == x.f.Id ? 0 : 1).ThenBy(x => x.f.Id))
        {
            int count = 1;
            if (Stacked && f.At != null)
            {
                if (!drawnStacks.Add(f.At)) continue;
                count = _stack.TryGetValue((f.At, f.Side == Player || Player == null), out int n) ? n : 1;
            }
            list.Add((f, p, h, count));
        }
        return list;
    }

    /// <summary>The enemy markers drawn: one per system zoomed out, with its count.</summary>
    List<(FleetPicture pic, Vector2 p, int count)> DrawnEnemy(List<(string System, FleetPicture Fleet, Vector2 pos)> enemy)
    {
        var list = new List<(FleetPicture, Vector2, int)>();
        var drawn = new HashSet<string>(StringComparer.Ordinal); // membership only
        foreach (var (sys, pic, p) in enemy)
        {
            int count = 1;
            if (Stacked)
            {
                if (!drawn.Add(sys)) continue;
                count = _stack.TryGetValue((sys, false), out int n) ? n : 1;
            }
            list.Add((pic, p, count));
        }
        return list;
    }

    static Rect2 Around(Vector2 p, float r) => new(p - new Vector2(r, r), new Vector2(2 * r, 2 * r));
    static Rect2 BadgeBox(Vector2 p, float r) => Around(p + new Vector2(r * 0.95f, -r * 0.95f), 8.5f);

    /// <summary>The markers' own boxes (fleets, enemy fleets, movers and their count badges).</summary>
    List<Rect2> MarkerBoxes((List<(Fleet f, Vector2 pos, Vector2 heading)> Own, List<(string System, FleetPicture Fleet, Vector2 pos)> Enemy) layout,
        List<(Mover m, Vector2 pos, Vector2 heading)> movers)
    {
        var map = Map;
        var list = new List<Rect2>();
        foreach (var (f, p, _, count) in DrawnOwn(layout.Own))
        {
            float r = map.SelectedFleet == f.Id || map.PickedFleets.Contains(f.Id) ? 10f : 8.5f;
            list.Add(Around(p, r));
            if (count > 1) list.Add(BadgeBox(p, r));
        }
        foreach (var (_, p, count) in DrawnEnemy(layout.Enemy))
        {
            list.Add(Around(p, 8.5f));
            if (count > 1) list.Add(BadgeBox(p, 8.5f));
        }
        foreach (var (m, p, _) in movers)
        {
            list.Add(Around(p, MoverR + (m.Kind == MoverKind.Mission ? 3.5f : 1f)));
            if (m.Count > 1) list.Add(BadgeBox(p, MoverR));
        }
        return list;
    }

    // ================================================================== words

    /// <summary>One piece of text on the map: what it says, its baseline, its box, colour and size.</summary>
    public readonly record struct MapWord(string Text, Vector2 Base, Rect2 Box, Color Col, int Size);

    /// <summary>The glyph under the mouse: 0 own fleet (Id), 1 enemy marker (index in the drawn list), 2 mover (index); or (-1, -1).</summary>
    (int Kind, int Id) Pointed(List<(Fleet f, Vector2 p, Vector2 h, int count)> own, List<(FleetPicture pic, Vector2 p, int count)> enemy,
        List<(Mover m, Vector2 pos, Vector2 heading)> movers)
    {
        if (Map.MousePos is not { } mouse) return (-1, -1);
        (int, int) best = (-1, -1); float bestD = PickRadiusPx * PickRadiusPx;
        void Try(Vector2 p, int kind, int id) { float d = p.DistanceSquaredTo(mouse); if (d < bestD) { bestD = d; best = (kind, id); } }
        foreach (var o in own) Try(o.p, 0, o.f.Id);
        for (int i = 0; i < enemy.Count; i++) Try(enemy[i].p, 1, i);
        for (int i = 0; i < movers.Count; i++) Try(movers[i].pos, 2, i);
        return best;
    }

    /// <summary>
    /// Every word on the fleets and movers, placed once (map declutter, Paul's All A 2026-11-18). At the opening zoom there are none: the
    /// selected fleet and the glyph under the mouse get "Fleet 1 · day 6". From the zoom where your system names come in (or with Alt held)
    /// each glyph gets its short name ("Fleet 1", "The Chair", "Espionage"). A word sits beside its glyph (right, left, above or below; a
    /// parked fleet right or left only, next to its own row), never over another word, a marker, a system's rings, a box on the map or
    /// past the screen's edge; first it looks for room off the system dots too. A word with no room is left out, except the selected or
    /// pointed one. A word may cover a grey or unexplored dot when nothing else is free, never a blue or orange one. A glyph off the screen
    /// has no word.
    /// </summary>
    List<MapWord> Words((List<(Fleet f, Vector2 pos, Vector2 heading)> Own, List<(string System, FleetPicture Fleet, Vector2 pos)> Enemy) layout,
        List<(Mover m, Vector2 pos, Vector2 heading)> movers)
    {
        var map = Map;
        var font = Ui.Medium ?? ThemeDB.FallbackFont;
        var view = new Rect2(Vector2.Zero, map.Size);
        var own = DrawnOwn(layout.Own);
        var enemy = DrawnEnemy(layout.Enemy);
        var pointed = Pointed(own, enemy, movers);
        bool all = WordsOn;
        var outp = new List<MapWord>();
        if (!all && pointed.Kind < 0 && map.SelectedFleet < 0) return outp;   // the opening screen: no words

        var taken = new GalaxyView.LabelSpace();
        foreach (var b in MarkerBoxes(layout, movers)) taken.Add(b);
        if (Boxes != null) foreach (var b in Boxes()) taken.Add(b);
        var dots = new Discs();    // every system dot with its rings: kept clear if there is any room
        var keep = new Discs();    // the dots that are someone's (blue, orange) and every state ring (HQ, uprising, blockade): never covered
        var near = view.Grow(30);
        float rad = map.SystemRadius;
        for (int i = 0; i < _galaxy.Systems.Count; i++)
        {
            var q = map.SystemScreen(i);
            if (!near.HasPoint(q)) continue;
            float ro = map.RingOut(i);
            float r = rad + Mathf.Max(1.5f, ro + 0.5f);
            dots.Add(q, r + 2f);
            if (ro > 2f || map.IsHeld(i)) keep.Add(q, r);
        }

        void Put(string text, Vector2 p, float r, Color col, int fs, bool must, bool parked, float badge)
        {
            if (!view.HasPoint(p)) return;   // a glyph off the screen has no word
            var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
            var spots = new List<Vector2>
            {
                new(p.X + r + 4f + badge, p.Y - size.Y / 2f), new(p.X - r - 4f - size.X, p.Y - size.Y / 2f),
            };
            if (!parked) { spots.Add(new(p.X - size.X / 2f, p.Y - r - 3f - size.Y)); spots.Add(new(p.X - size.X / 2f, p.Y + r + 3f)); }
            Rect2? at = null;
            for (int pass = 0; pass < 2 && at == null; pass++)
                foreach (var sp in spots)
                {
                    var b = new Rect2(sp, size);
                    if (!view.Encloses(b)) continue;
                    if (taken.Hits(b.Grow(2f)) || keep.Hits(b) || (pass == 0 && dots.Hits(b))) continue;
                    at = b; break;
                }
            if (at == null && must)
            {
                var b = new Rect2(spots[0], size);   // the selected or pointed word always shows, kept on the screen
                b.Position = new Vector2(Mathf.Clamp(b.Position.X, 0, Mathf.Max(0, view.Size.X - size.X)), Mathf.Clamp(b.Position.Y, 0, Mathf.Max(0, view.Size.Y - size.Y)));
                at = b;
            }
            if (at is not { } box) return;
            taken.Add(box.Grow(3f));
            outp.Add(new MapWord(text, box.Position + new Vector2(0, font.GetAscent(fs)), box, col, fs));
        }

        var planned = new Dictionary<int, int>();   // fleet id -> arrival day of an order not started yet; looked up, never iterated
        if (Planned != null) foreach (var kv in Planned()) planned[kv.Key] = kv.Value.ArrivalDay;
        string FleetWords(Fleet f, bool full)
        {
            if (!full) return f.Name;
            if (planned.TryGetValue(f.Id, out int day)) return $"{f.Name} · day {day}";
            if (f.Transit is { } t && f.At == null) { int a = t.DueDay; for (int h = t.HopIndex + 1; h < t.HopTicks.Count; h++) a += t.HopTicks[h]; return $"{f.Name} · day {a}"; }
            return f.Name;
        }
        Color MoverColour(Mover m) => m.Kind == MoverKind.Hq ? HqGold : ColorOf(Player ?? Side.Hand);

        // the selected fleet and the pointed glyph first: they always show, with the day
        foreach (var (f, p, _, count) in own)
        {
            bool sel = map.SelectedFleet == f.Id, pt = pointed == (0, f.Id);
            if (sel || pt) Put(FleetWords(f, true), p, sel ? 10f : 8.5f, ColorOf(f.Side), 13, true, f.At != null, count > 1 ? 12f : 0f);
        }
        if (pointed.Kind == 1) { var e = enemy[pointed.Id]; Put(EnemyText(e.pic), e.p, 8.5f, ColorOf(e.pic.Side), 13, true, true, e.count > 1 ? 12f : 0f); }
        float MoverRoom(Mover m) => MoverR + (m.Kind == MoverKind.Mission ? 4f : 1f);   // a mission team's ring is wider than its diamond
        if (pointed.Kind == 2) { var (m, p, _) = movers[pointed.Id]; Put(m.Point ?? m.Label, p, MoverRoom(m), MoverColour(m), 12, true, false, m.Count > 1 ? 12f : 0f); }
        if (!all) return outp;

        // then every other glyph's short name
        foreach (var (f, p, _, count) in own)
            if (map.SelectedFleet != f.Id && pointed != (0, f.Id)) Put(FleetWords(f, false), p, 8.5f, ColorOf(f.Side), 13, false, f.At != null, count > 1 ? 12f : 0f);
        for (int i = 0; i < enemy.Count; i++)
            if (pointed != (1, i)) Put(EnemyText(enemy[i].pic), enemy[i].p, 8.5f, ColorOf(enemy[i].pic.Side), 13, false, true, enemy[i].count > 1 ? 12f : 0f);
        for (int i = 0; i < movers.Count; i++)
            if (pointed != (2, i)) { var (m, p, _) = movers[i]; Put(m.Name ?? m.Label, p, MoverRoom(m), MoverColour(m), 12, false, false, m.Count > 1 ? 12f : 0f); }
        return outp;
    }

    (int, int) _lastPointed = (-1, -1);

    /// <summary>Round things on the screen (system dots with their rings): a word's box hits one only where it really reaches the circle, so
    /// a name can sit at the corner of a ring's square. A coarse grid keeps it cheap.</summary>
    sealed class Discs
    {
        const float Cell = 64f;
        readonly Dictionary<(int, int), List<(Vector2 C, float R)>> _cells = new(); // looked up by key only, never iterated
        public void Add(Vector2 c, float r)
        {
            int x0 = Mathf.FloorToInt((c.X - r) / Cell), x1 = Mathf.FloorToInt((c.X + r) / Cell);
            int y0 = Mathf.FloorToInt((c.Y - r) / Cell), y1 = Mathf.FloorToInt((c.Y + r) / Cell);
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
            {
                if (!_cells.TryGetValue((x, y), out var list)) _cells[(x, y)] = list = new List<(Vector2, float)>();
                list.Add((c, r));
            }
        }
        public bool Hits(Rect2 b)
        {
            int x0 = Mathf.FloorToInt(b.Position.X / Cell), x1 = Mathf.FloorToInt(b.End.X / Cell);
            int y0 = Mathf.FloorToInt(b.Position.Y / Cell), y1 = Mathf.FloorToInt(b.End.Y / Cell);
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++)
                if (_cells.TryGetValue((x, y), out var list))
                    foreach (var (c, r) in list)
                    {
                        var near = new Vector2(Mathf.Clamp(c.X, b.Position.X, b.End.X), Mathf.Clamp(c.Y, b.Position.Y, b.End.Y));
                        if (near.DistanceSquaredTo(c) < r * r) return true;
                    }
            return false;
        }
    }

    // ================================================================== drawing

    public override void _Draw()
    {
        if (_game == null) return;
        var map = Map;
        var font = Ui.Medium ?? ThemeDB.FallbackFont;
        var layout = Layout();
        var movers = PlacedMovers();
        var placed = layout.Own;
        var mine = ColorOf(Player ?? Side.Hand);

        // A route's end has no ring and no text (Paul 2026-11-18, pick 3): the dashed line stops at the destination's dot, and the
        // arrival day is in the glyph's own words.
        void Dashes(IReadOnlyList<string> path, int fromIndex, Vector2? start, Color col, float width, float dash)
        {
            Vector2? from = start;
            for (int i = Math.Max(0, fromIndex); i < path.Count; i++)
            {
                if (!_galaxy.TryFindSystem(path[i], out int sys)) break;
                var to = map.SystemScreen(sys);
                if (from is Vector2 f0) DrawDashedLine(f0, to, col, width, dash, true, true);
                from = to;
            }
        }

        // the player's own HQ while it moves: gold
        if (HqRoute?.Invoke() is { } hq && hq.Path.Count > 1) Dashes(hq.Path, hq.HopIndex, null, new Color(HqGold, WayAlpha), WayWidth, WayDash);

        // the player's characters travelling alone; a trip ordered that has not started yet is an order: white
        if (CharacterRoutes != null)
            foreach (var cr in CharacterRoutes())
                if (cr.Planned) Dashes(cr.Path, 0, null, PlannedColor, PlannedWidth, PlannedDash);
                else Dashes(cr.Path, cr.HopIndex, null, new Color(mine, WayAlpha), WayWidth, WayDash);

        // the rest of each moving fleet's route, from its marker; the selected fleet's is brighter
        foreach (var (f, p, _) in placed)
        {
            if (f.Transit is not { } t || f.At != null) continue;
            bool sel = map.SelectedFleet == f.Id || map.PickedFleets.Contains(f.Id);
            Dashes(t.Path, t.HopIndex + 1, p, new Color(ColorOf(f.Side), sel ? 1f : WayAlpha), sel ? WaySelWidth : WayWidth, WayDash);
        }

        // orders given but not started yet (they begin when the next day does)
        if (Planned != null)
            foreach (var kv in Planned())
            {
                var hit = placed.FirstOrDefault(x => x.f.Id == kv.Key);
                if (hit.f == null) continue;
                Dashes(kv.Value.Path, 1, hit.pos, PlannedColor, PlannedWidth, PlannedDash);
            }

        DrawMovers(font, movers);

        foreach (var (f, p, h, count) in DrawnOwn(placed))
        {
            var col = ColorOf(f.Side);
            bool selected = map.SelectedFleet == f.Id || map.PickedFleets.Contains(f.Id);   // or picked with Shift+click
            float r = selected ? 10f : 8.5f;
            var perp = new Vector2(-h.Y, h.X);
            var tri = new[] { p + h * r, p - h * r * 0.7f + perp * r * 0.7f, p - h * r * 0.7f - perp * r * 0.7f };
            DrawColoredPolygon(tri, col);
            bool own = f.Side == Player;
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, selected ? Colors.White : own ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f), selected ? 2.5f : own ? 1.6f : 1.2f, true);
            if (count > 1) Badge(font, p, r, count, col);
        }

        // enemy fleets the player's side sees now: a pointed-down triangle with a dark outline (no name: what was seen is in its words)
        foreach (var (pic, p, count) in DrawnEnemy(layout.Enemy))
        {
            var col = ColorOf(pic.Side);
            const float r = 8.5f;
            if (count > 1) Badge(font, p, r, count, col);
            var h = Vector2.Down; var perp = new Vector2(-h.Y, h.X);
            var tri = new[] { p + h * r, p - h * r * 0.7f + perp * r * 0.7f, p - h * r * 0.7f - perp * r * 0.7f };
            DrawColoredPolygon(tri, col);
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, new Color(0, 0, 0, 0.85f), 1.2f, true);
        }

        // the words last, so no line or marker paints over them
        foreach (var w in Words(layout, movers))
        {
            DrawStringOutline(font, w.Base, w.Text, HorizontalAlignment.Left, -1, w.Size, 4, new Color(0, 0, 0, 0.9f));
            DrawString(font, w.Base, w.Text, HorizontalAlignment.Left, -1, w.Size, w.Col);
        }

        // the map's system names keep off these words: when the glyph under the mouse changes, the map places its names again
        var pt = Pointed(DrawnOwn(placed), DrawnEnemy(layout.Enemy), movers);
        if (pt != _lastPointed) { _lastPointed = pt; map.QueueRedraw(); }
    }

    /// <summary>
    /// The player's own movers: a small glyph per kind (leader diamond, mission team diamond in a ring, regiment square, building hexagon, ship or
    /// fighter hollow triangle, HQ gold disc), moving like a fleet, and the rest of the route as a dashed line. Their words are placed with
    /// the fleets' (<see cref="Words"/>).
    /// </summary>
    void DrawMovers(Font font, List<(Mover m, Vector2 pos, Vector2 heading)> movers)
    {
        var map = Map;
        foreach (var (m, p, h) in movers)
        {
            var col = m.Kind == MoverKind.Hq ? HqGold : ColorOf(Player ?? Side.Hand);
            var edge = new Color(1, 1, 1, 0.9f);
            // the route still ahead (a lone leader, with its hop times, and the HQ already have their own route line; a Move group does not)
            bool ownLine = m.Kind == MoverKind.Hq || (m.Kind == MoverKind.Leader && m.HopTicks != null);
            if (!m.Held && !ownLine && m.Path.Count > 1)
            {
                var faint = new Color(col, WayAlpha);
                var from = p;
                int next = m.HopTicks != null ? m.HopIndex + 1 : NextPoint(m, p);
                for (int i = next; i < m.Path.Count; i++)
                {
                    if (!_galaxy.TryFindSystem(m.Path[i], out int sys)) break;
                    var to = map.SystemScreen(sys);
                    DrawDashedLine(from, to, faint, WayWidth, WayDash, true, true);
                    from = to;
                }
            }
            float r = MoverR;
            switch (m.Kind)
            {
                case MoverKind.Leader:
                case MoverKind.Mission:
                {
                    var d = new[] { p + new Vector2(0, -r), p + new Vector2(r, 0), p + new Vector2(0, r), p + new Vector2(-r, 0) };
                    DrawColoredPolygon(d, col);
                    DrawPolyline(new[] { d[0], d[1], d[2], d[3], d[0] }, edge, 1.2f, true);
                    if (m.Kind == MoverKind.Mission) DrawArc(p, r + 3f, 0, Mathf.Tau, 20, col, 1.3f, true);
                    break;
                }
                case MoverKind.Regiment:
                {
                    var q = new[] { p + new Vector2(-r * 0.8f, -r * 0.8f), p + new Vector2(r * 0.8f, -r * 0.8f), p + new Vector2(r * 0.8f, r * 0.8f), p + new Vector2(-r * 0.8f, r * 0.8f) };
                    DrawColoredPolygon(q, col);
                    DrawPolyline(new[] { q[0], q[1], q[2], q[3], q[0] }, edge, 1.2f, true);
                    break;
                }
                case MoverKind.Facility:
                {
                    var hex = new Vector2[7];
                    for (int i = 0; i < 6; i++) hex[i] = p + Vector2.FromAngle(Mathf.Pi / 6f + i * Mathf.Pi / 3f) * r;
                    hex[6] = hex[0];
                    DrawColoredPolygon(hex[..6], col);
                    DrawPolyline(hex, edge, 1.2f, true);
                    break;
                }
                case MoverKind.Hq:
                    DrawCircle(p, r + 0.5f, col);
                    DrawArc(p, r + 0.5f, 0, Mathf.Tau, 20, new Color(0, 0, 0, 0.85f), 1.2f, true);
                    break;
                default: // ships and fighters flying alone: a hollow triangle, smaller for fighters
                {
                    float rr = m.Kind == MoverKind.Fighter ? r * 0.85f : r * 1.1f;
                    var perp = new Vector2(-h.Y, h.X);
                    var tri = new[] { p + h * rr, p - h * rr * 0.7f + perp * rr * 0.7f, p - h * rr * 0.7f - perp * rr * 0.7f, p + h * rr };
                    DrawColoredPolygon(tri[..3], Ui.MapBg);
                    DrawPolyline(tri, col, 1.6f, true);
                    break;
                }
            }
            if (m.Count > 1) Badge(font, p, r, m.Count, col);
        }
    }

    /// <summary>For a mover placed by length: the index of the first path system still ahead of it.</summary>
    int NextPoint(Mover m, Vector2 p)
    {
        var map = Map;
        float best = float.MaxValue; int at = 1;
        for (int i = 0; i + 1 < m.Path.Count; i++)
        {
            if (!_galaxy.TryFindSystem(m.Path[i], out int a) || !_galaxy.TryFindSystem(m.Path[i + 1], out int b)) break;
            var pa = map.SystemScreen(a); var pb = map.SystemScreen(b);
            float d = DistToSegment(p, pa, pb);
            if (d < best - 0.01f) { best = d; at = i + 1; }
        }
        return at;
    }

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        if (l2 < 0.0001f) return p.DistanceTo(a);
        float t = Mathf.Clamp((p - a).Dot(ab) / l2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>The number of fleets in a stacked marker, in a small dark disc at its top right.</summary>
    void Badge(Font font, Vector2 p, float r, int count, Color col)
    {
        var at = p + new Vector2(r * 0.95f, -r * 0.95f);
        DrawCircle(at, 7.5f, Ui.MapBg);
        DrawArc(at, 7.5f, 0, Mathf.Tau, 16, col, 1.4f, true);
        string t = count > 9 ? "9+" : count.ToString();
        var size = font.GetStringSize(t, HorizontalAlignment.Left, -1, 11);
        DrawString(font, at + new Vector2(-size.X / 2f, font.GetAscent(11) / 2f - 1f), t, HorizontalAlignment.Left, -1, 11, Colors.White);
    }
}

/// <summary>What a mover on the map is; picks its glyph.</summary>
public enum MoverKind { Leader, Mission, Regiment, Ship, Fighter, Facility, Hq }

/// <summary>
/// One of the player's own things travelling that is not a fleet, as the map draws it. Path is the whole trip (system names); HopTicks, HopIndex
/// and DueDay as in a fleet transit. HopTicks null = only the total is known (a mission team): it is placed by length along the path over the
/// days [DueDay - TotalTicks, DueDay]. Held = waiting at Path[HopIndex] (a delivery stopped by an enemy fleet or a lost destination).
/// </summary>
/// <remarks>Label is the whole sentence (the playtest reads it). On the map, Name is the short word shown when names show ("The Chair",
/// "Espionage") and Point the words under the mouse ("The Chair · day 20"); both fall back to Label.</remarks>
public sealed record Mover(MoverKind Kind, string Label, IReadOnlyList<string> Path, IReadOnlyList<int> HopTicks, int HopIndex, int DueDay,
    int TotalTicks = 0, int Count = 1, bool Held = false, string Name = null, string Point = null);
