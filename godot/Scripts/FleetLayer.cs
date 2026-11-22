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
    /// <summary>Whether one of the player's fleets is Elusive (it pulls back when an enemy fleet arrives): its marker is an arrowhead.</summary>
    public Func<int, bool> Elusive { get; set; }
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
    /// <summary>Half the fleet marker's size (the 16 px pixel icon with its 1 px ring), and how far out an enemy group's count disc sits:
    /// past the arrow's wing (review 10: yours carry their number in the tail, the enemy's in a disc, so the shape says whose).</summary>
    const float MarkerR = 8.5f, EnemyBadgeR = 11f;
    // One dashed style per meaning (map declutter, Paul's All A 2026-11-18): a thing on its way in its own colour at 55%, 1.2 px, dash 4
    // (the selected fleet's at 100%, 1.6 px; the moving HQ keeps its gold); an order not started yet in white at 85%, 1.6 px, dash 6.
    const float WayAlpha = 0.55f, WayWidth = 1.2f, WaySelWidth = 1.6f, WayDash = 4f;
    static readonly Color PlannedColor = new(1f, 1f, 1f, 0.85f);
    const float PlannedWidth = 1.6f, PlannedDash = 6f;
    static readonly Color HqGold = Ui.Gold;   // the capital gold, so the Hearth's pixel icon gets its four tones

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

    /// <summary>Words on every fleet and mover: from the zoom where your system names come in, or while Alt is held (Paul 2026-11-18,
    /// pick 2). Otherwise only the selected fleet and the glyph under the mouse have words.</summary>
    bool WordsOn => Map.Zoom >= GalaxyView.NameZoom || Map.ShowAllNames;

    // The fleets of one side in orbit at a system share one marker at every zoom (Paul 2026-11-22: "an icon when there are more than one
    // fleet in orbit of a system, instead of having multiple fleet icons"; it used to be zoomed out only). Filled by Layout: the fleets in
    // orbit by system and side, in id order (looked up by key, never iterated). The enemy's are grouped by DrawnEnemy.
    readonly Dictionary<(string System, Side Side), List<int>> _orbit = new();

    /// <summary>The fleets that share this fleet's marker as last laid out: its side's fleets in orbit at its system, or itself alone.</summary>
    IReadOnlyList<int> Sharing(Fleet f) => f.At != null && _orbit.TryGetValue((f.At, f.Side), out var ids) ? ids : new[] { f.Id };

    /// <summary>Visual position (screen) of a fleet and its heading (unit vector), or false if it cannot be placed.</summary>
    bool Place(Fleet f, out Vector2 pos, out Vector2 heading)
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
            var key = (f.At, f.Side);
            if (!_orbit.TryGetValue(key, out var ids)) _orbit[key] = ids = new List<int>();
            ids.Add(f.Id);
            pos = ParkedAt(s, f.Side == (Player ?? Side.Hand) ? 0 : 1);   // yours first, the other side's above
            heading = ParkedHeading;
            return true;
        }
        return false;
    }

    /// <summary>Own fleets (all fleets when there is no fog source).</summary>
    IEnumerable<(Fleet f, Vector2 pos, Vector2 heading)> Placed() => Layout().Own;

    /// <summary>Own fleets, then the enemy markers in the slots after them at each system.</summary>
    (List<(Fleet f, Vector2 pos, Vector2 heading)> Own, List<(string System, FleetPicture Fleet, Vector2 pos)> Enemy) Layout()
    {
        _orbit.Clear();
        var own = new List<(Fleet, Vector2, Vector2)>();
        foreach (var f in _game.State.Fleets.Values)               // fleet id order
            if ((EnemyFleets == null || f.Side == Player) && Place(f, out var p, out var h)) own.Add((f, p, h));
        var enemy = new List<(string, FleetPicture, Vector2)>();
        if (EnemyFleets != null)
            foreach (var (sys, pic) in EnemyFleets())                  // systems in ordinal order
                if (PlaceAt(sys, out var p)) enemy.Add((sys, pic, p));
        return (own, enemy);
    }

    bool PlaceAt(string system, out Vector2 pos)
    {
        pos = Vector2.Zero;
        if (!_galaxy.TryFindSystem(system, out int s)) return false;
        pos = ParkedAt(s, 1);
        return true;
    }

    /// <summary>Where a parked marker goes: above the dot, just outside the system's outermost ring (on an HQ or an uprising the marker
    /// used to sit across the rings); the enemy's (slot 1) one row above yours (slot 0).</summary>
    Vector2 ParkedAt(int system, int slot)
    {
        var map = Map;
        // the marker reaches about 7 px below its centre (with its ring); 2 px of air above the ring
        float first = Mathf.Max(13f, map.SystemRadius + map.RingOut(system) + 9f);
        return map.SystemScreen(system) + new Vector2(-6f, -first - 16f * slot);
    }

    /// <summary>Your fleet markers drawn now: the fleet each one stands for and how many fleets share it (for the playtest).</summary>
    public IReadOnlyList<(int Fleet, int Count)> OwnMarkers() =>
        _game == null ? Array.Empty<(int, int)>() : DrawnOwn(Layout().Own).Select(o => (o.f.Id, o.count)).ToList();

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
            if (d < bestD || (best < 0 && d <= bestD)) { bestD = d; best = f.Id; } // a group picks its first fleet (lowest id); GroupOf gives the rest
        }
        return best;
    }

    // ================================================================== what is drawn: one marker per fleet on its way, one per system in orbit

    /// <summary>The own fleet markers drawn: one per fleet on its way, one per system for the fleets in orbit there (the selected fleet's,
    /// else the first) with their count.</summary>
    List<(Fleet f, Vector2 p, Vector2 h, int count)> DrawnOwn(List<(Fleet f, Vector2 pos, Vector2 heading)> own)
    {
        var map = Map;
        var list = new List<(Fleet, Vector2, Vector2, int)>();
        var drawnStacks = new HashSet<(string, Side)>(); // membership only
        foreach (var (f, p, h) in own.OrderBy(x => map.SelectedFleet == x.f.Id ? 0 : 1).ThenBy(x => x.f.Id))
        {
            int count = 1;
            if (f.At != null)
            {
                if (!drawnStacks.Add((f.At, f.Side))) continue;
                count = Sharing(f).Count;
            }
            list.Add((f, p, h, count));
        }
        return list;
    }

    /// <summary>The enemy markers drawn: one per system, with its count; a group's picture is all its fleets together, the ships and
    /// squadrons seen summed (review 10: "2 fleets · 7 ships", only what is already seen).</summary>
    List<(FleetPicture pic, Vector2 p, int count)> DrawnEnemy(List<(string System, FleetPicture Fleet, Vector2 pos)> enemy)
    {
        var list = new List<(FleetPicture pic, Vector2 p, int count)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal); // a system's row in the list; looked up, never iterated
        foreach (var (sys, pic, p) in enemy)                           // systems in ordinal order
        {
            if (!at.TryGetValue(sys, out int i)) { at[sys] = list.Count; list.Add((pic, p, 1)); continue; }
            var (first, fp, n) = list[i];
            var sum = n > 1 ? first : new FleetPicture
            {
                Side = first.Side, Ships = new(first.Ships, StringComparer.Ordinal), Squadrons = new(first.Squadrons, StringComparer.Ordinal),
            };
            foreach (var (k, v) in pic.Ships) sum.Ships[k] = (sum.Ships.TryGetValue(k, out int a) ? a : 0) + v;
            foreach (var (k, v) in pic.Squadrons) sum.Squadrons[k] = (sum.Squadrons.TryGetValue(k, out int a) ? a : 0) + v;
            list[i] = (sum, fp, n + 1);
        }
        return list;
    }

    static Rect2 Around(Vector2 p, float r) => new(p - new Vector2(r, r), new Vector2(2 * r, 2 * r));

    /// <summary>The markers' own boxes (fleets, enemy fleets, movers and their count badges).</summary>
    List<Rect2> MarkerBoxes((List<(Fleet f, Vector2 pos, Vector2 heading)> Own, List<(string System, FleetPicture Fleet, Vector2 pos)> Enemy) layout,
        List<(Mover m, Vector2 pos, Vector2 heading)> movers)
    {
        var map = Map;
        var list = new List<Rect2>();
        foreach (var (_, p, _, _) in DrawnOwn(layout.Own)) list.Add(Around(p, MarkerR));
        foreach (var (_, p, count) in DrawnEnemy(layout.Enemy))
        {
            list.Add(Around(p, MarkerR));
            if (count > 1) list.Add(Around(p + EnemyBadgeAt, 8.5f));
        }
        foreach (var (m, p, _) in movers)
        {
            list.Add(Around(p, MoverRoom(m.Kind)));
            if (m.Count > 1) list.Add(Around(p + MoverBadgeAt(m.Kind), 8.5f));
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

        // several fleets in orbit share a marker (Paul 2026-11-22): its words are their number; the selected one's own words and how many
        // others are with it ("Fleet 3 · day 14 +1", review 10), so Tab shows on the map; under the mouse with none selected, their names
        bool Sel(Fleet f) => Sharing(f).Contains(map.SelectedFleet);
        bool Pt(Fleet f) => pointed.Kind == 0 && Sharing(f).Contains(pointed.Id);
        string OwnWords(Fleet f, int count, bool full)
        {
            if (count <= 1) return FleetWords(f, full);
            if (!full) return $"{count} fleets";
            if (map.SelectedFleet == f.Id) return $"{FleetWords(f, true)} +{count - 1}";   // the selected fleet is the one drawn
            var names = Sharing(f).Select(id => _game.State.Fleets[id].Name).ToList();
            return string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" +{names.Count - 3}" : "");
        }
        // an enemy group: how many, and the ships and squadrons seen in all of them together
        string EnemyWords((FleetPicture pic, Vector2 p, int count) e) => e.count > 1 ? $"{e.count} fleets · {EnemyText(e.pic)}" : EnemyText(e.pic);
        float EnemyRoom(int count) => count > 1 ? 12f : 0f;   // keeps the words off the count disc

        // the selected fleet and the pointed glyph first: they always show, with the day
        foreach (var (f, p, _, count) in own)
            if (Sel(f) || Pt(f)) Put(OwnWords(f, count, true), p, MarkerR, ColorOf(f.Side), 13, true, f.At != null, 0f);
        if (pointed.Kind == 1) { var e = enemy[pointed.Id]; Put(EnemyWords(e), e.p, MarkerR, ColorOf(e.pic.Side), 13, true, true, EnemyRoom(e.count)); }
        if (pointed.Kind == 2) { var (m, p, _) = movers[pointed.Id]; Put(m.Point ?? m.Label, p, MoverRoom(m.Kind), MoverColour(m), 12, true, false, m.Count > 1 ? 12f : 0f); }
        if (!all) return outp;

        // then every other glyph's short name
        foreach (var (f, p, _, count) in own)
            if (!Sel(f) && !Pt(f)) Put(OwnWords(f, count, false), p, MarkerR, ColorOf(f.Side), 13, false, f.At != null, 0f);
        for (int i = 0; i < enemy.Count; i++)
            if (pointed != (1, i)) Put(EnemyWords(enemy[i]), enemy[i].p, MarkerR, ColorOf(enemy[i].pic.Side), 13, false, true, EnemyRoom(enemy[i].count));
        for (int i = 0; i < movers.Count; i++)
            if (pointed != (2, i)) { var (m, p, _) = movers[i]; Put(m.Name ?? m.Label, p, MoverRoom(m.Kind), MoverColour(m), 12, false, false, m.Count > 1 ? 12f : 0f); }
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

        var drawn = new HashSet<int>();
        foreach (var (f, p, h, count) in DrawnOwn(placed))
        {
            drawn.Add(f.Id);
            var col = ColorOf(f.Side);
            // selected or picked with Shift+click: a group shows it when any fleet in it is
            bool selected = Sharing(f).Any(id => map.SelectedFleet == id || map.PickedFleets.Contains(id));
            bool own = f.Side == Player;
            // Defensive or Elusive (Paul 2026-11-22, his sketches: a square behind the arrow, or a chevron); several fleets in orbit here
            // show their number in that place instead (Paul 2026-11-22), and the list a click opens tells them apart
            bool elusive = own && Elusive != null && Elusive(f.Id);
            int light = RimLight(f.Id, h.X > 0.01f, h.Y > 0.01f);
            var (key, fx, fy) = Look(count, own, elusive, h);
            DrawMarker(key, p, fx, fy, key.StartsWith("fleet-group-", StringComparison.Ordinal) ? TopLeft : light, col, selected);
            if (count > 1 && !own) BadgeAt(font, p + EnemyBadgeAt, count, col);   // not yours: the bare arrow and a count disc
        }
        // a fleet out of sight (lost, merged, in a stack) starts afresh when it shows again: no turn from where it was last seen
        foreach (int gone in _facing.Keys.Where(k => !drawn.Contains(k)).ToList()) { _facing.Remove(gone); _turns.Remove(gone); }

        // enemy fleets the player's side sees now: the bare arrow (their orders are hidden), no name: what was seen is in its words
        foreach (var (pic, p, count) in DrawnEnemy(layout.Enemy))
        {
            var col = ColorOf(pic.Side);
            DrawMarker("fleet-unknown", p, true, false, TopLeft, col, false);   // parked up-right, as yours
            if (count > 1) BadgeAt(font, p + EnemyBadgeAt, count, col);         // a group: its count in a disc past the wing (review 10)
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

    /// <summary>The icon of several fleets of one side in orbit at a system: the bare arrow with their number where Defensive has its square
    /// (Paul 2026-11-22); ten or more show a plus.</summary>
    static Vector2 EnemyBadgeAt => new(EnemyBadgeR * 0.95f, -EnemyBadgeR * 0.95f);

    /// <summary>
    /// The icon a fleet marker draws and whether it is mirrored (to the diagonal nearest its heading). Yours carry a mark in the tail: the
    /// Defensive square, the Elusive chevron, or for several in orbit their number; the enemy's arrow is bare (review 10, rule C6). A group's
    /// icon is drawn pointing up-right, as a parked marker shows, and is never mirrored, so its number reads upright.
    /// </summary>
    internal static (string Key, bool Fx, bool Fy) Look(int count, bool own, bool elusive, Vector2 heading)
    {
        if (count > 1 && own) return (GroupIcon(count), false, false);
        return (!own ? "fleet-unknown" : elusive ? "fleet-elusive" : "fleet-defensive", heading.X > 0.01f, heading.Y > 0.01f);
    }

    internal static string GroupIcon(int count) => count is >= 2 and <= 9 ? $"fleet-group-{count}" : "fleet-group-many";

    /// <summary>The fleets that share a fleet's marker, read fresh from the game (for a click and the playtest; drawing uses the table
    /// <see cref="Layout"/> fills): every fleet of its side in orbit at its system, by id; the fleet alone when it is on its way (or unknown).</summary>
    public IReadOnlyList<int> GroupOf(int fleet)
    {
        if (_game == null || !_game.State.Fleets.TryGetValue(fleet, out var f) || f.At == null) return new[] { fleet };
        return _game.State.Fleets.Values.Where(x => x.Side == f.Side && x.At == f.At).Select(x => x.Id).OrderBy(id => id).ToList();
    }

    /// <summary>
    /// A fleet's marker: a 16 x 16 pixel icon from <see cref="PixelIconData.Map"/> (code only, Paul 2026-11-18), drawn up-left and mirrored to
    /// the diagonal nearest its heading (whole pixels only: a turned pixel icon smears), its rim lit by <see cref="RimLight"/>. The middle of
    /// its shape sits on the fleet's point. A 1 px dark ring around the shape keeps it off routes and dots; a selected (or picked) fleet gets
    /// a second, white ring outside it, so it stands 2 px larger, as the old triangle grew when selected.
    /// </summary>
    void DrawMarker(string key, Vector2 p, bool fx, bool fy, int light, Color col, bool selected)
    {
        var grid = MarkerTones(key, fx, fy, light);
        int n = grid.Length;
        char Tone(int x, int y) => x < 0 || y < 0 || x >= n || y >= n ? '.' : grid[y][x];
        int x0 = n, y0 = n, x1 = -1, y1 = -1;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (Tone(x, y) != '.') { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 < 0) return;
        var at = (p - new Vector2((x0 + x1 + 1) / 2f, (y0 + y1 + 1) / 2f)).Round();
        // how far an empty pixel is from the shape (1 = touching, 2 = one pixel further out), up to `rings`
        int rings = selected ? 2 : 1;
        int Gap(int x, int y)
        {
            for (int r = 1; r <= rings; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                        if (Tone(x + dx, y + dy) != '.') return r;
            return 0;
        }
        var dark = new Color(0, 0, 0, 0.85f);
        for (int y = y0 - rings; y <= y1 + rings; y++)
            for (int x = x0 - rings; x <= x1 + rings; x++)
            {
                if (Tone(x, y) != '.') continue;
                int g = Gap(x, y);
                if (g > 0) DrawRect(new Rect2(at.X + x, at.Y + y, 1, 1), g == 1 ? dark : Colors.White);
            }
        PixelIcons.Draw(this, grid, at, 1, PixelIcons.RampOf(col), col);
    }

    // ------------------------------------------------------------------ the markers' light
    // Paul 2026-11-22: parked fleets point up-right; a moving one points along its route; "a very subtle movement in the pixels to reflect the
    // light changing orientation when a moving fleet icon turns". The light stays where every pixel icon has it, the top left of the screen
    // (pixel-icons skill). The tones inside a marker turn with it (the look Paul approved); only its outline answers the screen's light, so a
    // turn moves a few rim pixels, never the whole shading. On a turn the rim's light starts where it was on the ship and steps round to the top
    // left, 45 degrees every LightStepMs: the few pixels slide instead of jumping.

    /// <summary>Where a parked fleet points: up-right (Paul 2026-11-22).</summary>
    internal static readonly Vector2 ParkedHeading = new Vector2(1, -1).Normalized();
    /// <summary>The light's eight directions, clockwise from the top left (screen y grows down).</summary>
    static readonly Vector2I[] LightDirs = { new(-1, -1), new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0) };
    const int TopLeft = 0;
    /// <summary>Each 45-degree step of the rim's light after a turn: a quarter turn takes two steps, about a fifth of a second.</summary>
    const double LightStepMs = 90;
    /// <summary>How square an outline pixel must face the light (or away from it) to be lit (or dark); cosine of about 68 degrees.</summary>
    const float RimFacing = 0.38f;
    readonly Dictionary<int, (bool Fx, bool Fy)> _facing = new();
    readonly Dictionary<int, (int From, int Dir, int Steps, ulong Start)> _turns = new();
    static readonly Dictionary<(string Key, bool Fx, bool Fy, int Light), string[]> ToneCache = new();

    /// <summary>The light on a fleet's rim this frame (an index in <see cref="LightDirs"/>), starting a turn's steps when its marker flips.</summary>
    internal int RimLight(int fleet, bool fx, bool fy, ulong? at = null)
    {
        ulong now = at ?? Time.GetTicksMsec();
        if (_facing.TryGetValue(fleet, out var was) && was != (fx, fy))
        {
            // the light as it lay on the ship before the turn, carried onto the new drawing, then round to the top left the short way
            var from = new Vector2I(was.Fx != fx ? 1 : -1, was.Fy != fy ? 1 : -1);
            int i = Array.IndexOf(LightDirs, from), cw = (TopLeft - i + 8) % 8;
            if (cw != 0) _turns[fleet] = (i, cw <= 4 ? 1 : -1, cw <= 4 ? cw : 8 - cw, now);
        }
        _facing[fleet] = (fx, fy);
        if (!_turns.TryGetValue(fleet, out var t)) return TopLeft;
        int k = (int)((now - t.Start) / LightStepMs);
        if (k >= t.Steps) { _turns.Remove(fleet); return TopLeft; }
        return ((t.From + t.Dir * k) % 8 + 8) % 8;
    }

    /// <summary>
    /// A marker's tones pointing along fx/fy with the rim's light from LightDirs[light]: the icon's own tones mirrored with it, then each outline
    /// pixel that faces the light differently from the light it was drawn with takes the light's tone (3 facing it, 1 facing away, 2 side on).
    /// Pointing up-left with the light at the top left it is the icon as drawn. Cached per icon (fleets and movers), way it faces and light.
    /// </summary>
    internal static string[] MarkerTones(string key, bool fx, bool fy, int light)
    {
        if (ToneCache.TryGetValue((key, fx, fy, light), out var done)) return done;
        var grid = PixelIconData.Map[key];
        int n = grid.Length;
        var rows = new char[n][];
        for (int y = 0; y < n; y++)
        {
            rows[y] = new char[n];
            for (int x = 0; x < n; x++) rows[y][x] = grid[fy ? n - 1 - y : y][fx ? n - 1 - x : x];
        }
        bool On(int x, int y) => x >= 0 && y >= 0 && x < n && y < n && rows[y][x] != '.';
        int? Facing(int x, int y, Vector2I l)
        {
            // the outline's outward direction: the sum of the steps to the empty pixels around it
            int nx = 0, ny = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dy != 0) && !On(x + dx, y + dy)) { nx += dx; ny += dy; }
            if (nx == 0 && ny == 0) return null;
            float d = (nx * l.X + ny * l.Y) / (Mathf.Sqrt(nx * nx + ny * ny) * Mathf.Sqrt(l.X * l.X + l.Y * l.Y));
            return d > RimFacing ? 1 : d < -RimFacing ? -1 : 0;
        }
        var drawn = new Vector2I(fx ? 1 : -1, fy ? 1 : -1);   // the light the icon's tones were drawn with, mirrored with it
        var output = rows.Select(r => (char[])r.Clone()).ToArray();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (rows[y][x] is '.' or '4') continue;   // the shine on the nose stays
                if (Facing(x, y, LightDirs[light]) is not int now || now == Facing(x, y, drawn)) continue;
                output[y][x] = now > 0 ? '3' : now < 0 ? '1' : '2';
            }
        return ToneCache[(key, fx, fy, light)] = output.Select(r => new string(r)).ToArray();
    }

    /// <summary>
    /// Each mover's pixel icon in <see cref="PixelIconData.Map"/> (Paul 2026-11-22, "using the same style as the fleet icon, draw all the
    /// others", max 16 px): a leader is one person, a mission team two (Paul: not the same diamond), a regiment a helmet, a lone ship a hull
    /// and fighters a small jet (both nose left), a building a house, the moving Hearth a gold flame. Smaller than a fleet's arrow, so fleets
    /// stay the main thing on the map.
    /// </summary>
    internal static string MoverIcon(MoverKind k) => k switch
    {
        MoverKind.Leader => "mover-leader", MoverKind.Mission => "mover-team", MoverKind.Regiment => "mover-regiment",
        MoverKind.Ship => "mover-ship", MoverKind.Fighter => "mover-fighter", MoverKind.Facility => "mover-building", MoverKind.Hq => "mover-hearth",
        _ => throw new ArgumentOutOfRangeException(nameof(k), k, "a mover kind with no icon"),
    };

    static readonly Dictionary<MoverKind, Vector2> HalfCache = new();
    /// <summary>Half a mover icon's width and height with its 1 px ring.</summary>
    static Vector2 MoverHalf(MoverKind k)
    {
        if (HalfCache.TryGetValue(k, out var half)) return half;
        var g = PixelIconData.Map[MoverIcon(k)];
        int x0 = 99, y0 = 99, x1 = -1, y1 = -1;
        for (int y = 0; y < g.Length; y++)
            for (int x = 0; x < g[y].Length; x++)
                if (g[y][x] != '.') { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        return HalfCache[k] = new Vector2((x1 - x0 + 1) / 2f + 1f, (y1 - y0 + 1) / 2f + 1f);
    }

    /// <summary>The room a mover's words keep from its middle: half its icon's larger side.</summary>
    static float MoverRoom(MoverKind k) => Math.Max(MoverHalf(k).X, MoverHalf(k).Y);

    /// <summary>Where a mover's count badge sits: off its icon's top right corner, the 7.5 px disc overlapping it by at most 2 px, so the
    /// badge never hides the icon (a lone ship is wide and flat).</summary>
    static Vector2 MoverBadgeAt(MoverKind k) => new Vector2(MoverHalf(k).X + 3.9f, -MoverHalf(k).Y - 3.9f);

    /// <summary>
    /// The player's own movers: a pixel icon per kind (<see cref="MoverIcon"/>), moving like a fleet, and the rest of the route as a dashed
    /// line. Their words are placed with the fleets' (<see cref="Words"/>).
    /// </summary>
    void DrawMovers(Font font, List<(Mover m, Vector2 pos, Vector2 heading)> movers)
    {
        var map = Map;
        foreach (var (m, p, h) in movers)
        {
            var col = m.Kind == MoverKind.Hq ? HqGold : ColorOf(Player ?? Side.Hand);
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
            // its pixel icon (Paul 2026-11-22: every mover in the fleet icon's style, max 16 px). Ships and fighters face the way they go
            // (drawn nose left, mirrored when heading right, their rim re-lit from the top left); the others stand upright.
            bool faceRight = m.Kind is MoverKind.Ship or MoverKind.Fighter && h.X > 0.01f;
            DrawMarker(MoverIcon(m.Kind), p, faceRight, false, TopLeft, col, false);
            if (m.Count > 1) BadgeAt(font, p + MoverBadgeAt(m.Kind), m.Count, col);
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

    void BadgeAt(Font font, Vector2 at, int count, Color col)
    {
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
