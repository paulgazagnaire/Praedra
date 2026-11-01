using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;
/// <summary>The sector window, part two: one system tile and the numbers behind it. See GameScreen.SectorView.cs.</summary>
public partial class GameScreen
{
    // ================================================================== one tile

    // the defence buildings (Mordant and Lode guns and shields), shown as diamonds under a system
    static readonly FacilityKind[] DefenceKinds = { FacilityKind.Lien150, FacilityKind.CovenantI, FacilityKind.EscrowI, FacilityKind.CovenantII, FacilityKind.EscrowII };
    static readonly Color EnergyColor = new("cfe3f7");

    /// <summary>
    /// One system as a small tile (Paul, 2026-11-01: "very simple, a very high point of view"): its planet, its name, then lines of small
    /// marks with no numbers: loyalty as one line, energy slots and raw slots as dots (filled in use, hollow free), troops as squares and
    /// defences as diamonds, fleets as triangles on the planet. An uprising or a harvester circles the planet in red. A system you never
    /// looked at shows its planet and name only. The numbers are in the tooltip; a click opens the system's own window.
    /// </summary>
    PanelContainer SystemCardTile(string n)
    {
        bool dead = S.IsDestroyed(n);
        bool mine = !dead && S.OwnerOf(n) == Me;
        var pic = S.Picture(n);
        bool explored = Scouted(n);
        var (known, owner) = S.KnownOwnerOf(n);
        Side? holder = mine ? Me : dead ? null : owner;
        Color oc = dead ? Ui.Danger : !explored || (!known && !mine) ? Ui.Text3 : Ui.OwnerColor(holder, Me);
        bool focused = _secDetail.Visible && _secFocus == n;
        bool hq = n == S.MyHqSystem || (n == S.EnemyHqSystem && explored);

        var tile = new PanelContainer { CustomMinimumSize = new Vector2(TileWidth, TileHeight), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        tile.SetMeta(UiTest.FullName, "Sector card " + n);
        // the tile's ground is the planet's own black, so the picture has no box round it
        StyleBoxFlat Style(bool hover) => Ui.Box(hover ? Ui.CardBg : Ui.PlanetSky, 10, focused ? oc : hover ? new Color(1, 1, 1, 0.22f) : new Color(1, 1, 1, 0.06f), focused ? 2 : 1, 6, 6);
        tile.AddThemeStyleboxOverride("panel", Style(false));
        // a tile rebuilt under a still mouse (a new day) keeps its hover look
        Callable.From(() => { if (IsInstanceValid(tile) && tile.IsVisibleInTree() && tile.GetGlobalRect().HasPoint(tile.GetGlobalMousePosition())) tile.AddThemeStyleboxOverride("panel", Style(true)); }).CallDeferred();
        tile.MouseEntered += () => tile.AddThemeStyleboxOverride("panel", Style(true));
        tile.MouseExited += () => tile.AddThemeStyleboxOverride("panel", Style(false));
        tile.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
            // the tile is rebuilt by what the click does: do it after this input event is over
            if (mb.DoubleClick) { Later(() => ShowOnMap(n)); return; }
            Later(() => { if (focused) CloseSystemPane(); else OpenSystemPane(n, _secPane); });
        };
        tile.TooltipText = Ui.Wrap(TileTip(n, mine, explored, dead, pic, holder, known));

        var v = Ui.VBox(4);
        v.MouseFilter = MouseFilterEnum.Ignore;
        tile.AddChild(v);

        // ---- the planet, one screen pixel per dot so the dither stays crisp; fleets as triangles in its corner; trouble as a red ring
        var art = new Control { CustomMinimumSize = new Vector2(PlanetArt.Dots, PlanetArt.Dots), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(art);
        if (PlanetArt.Of(n) is Texture2D tex)
        {
            var tr = new TextureRect { Texture = tex, TextureFilter = TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            tr.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            if (dead) tr.Modulate = new Color(0.6f, 0.22f, 0.2f, 0.55f);
            else if (!explored) tr.Modulate = new Color(0.7f, 0.72f, 0.78f, 0.8f);
            art.AddChild(tr);
        }
        var alarms = Alarms(n, mine);
        if (alarms.Any(a => a.Item2 == Ui.Danger && a.Item1 != "ENEMY FLEET"))
        {
            var ring = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            ring.AddThemeStyleboxOverride("panel", Ui.Box(new Color(Ui.Danger, 0.08f), PlanetArt.Dots / 2, Ui.Danger, 2, 0, 0));
            ring.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            art.AddChild(ring);
        }
        if (!dead)
        {
            int own = S.Game.State.Fleets.Values.Count(f => f.Side == Me && f.At == n);
            int enemy = S.Sees(n) ? S.VisibleEnemyFleetsAt(n).Count : 0;
            int seenBefore = !S.Sees(n) && pic?.Fleets is { } pf ? pf.Value.Count : 0;
            if (own + enemy + seenBefore > 0)
            {
                var fleets = new SectorMarks { Side = 9, Gap = 3, Centered = false, Cap = FleetMarksCap }
                    .Add(SectorMarks.Shape.Triangle, Ui.You, true, own)
                    .Add(SectorMarks.Shape.Triangle, Ui.Enemy, true, enemy)
                    .Add(SectorMarks.Shape.Triangle, Ui.Enemy, false, seenBefore);
                fleets.Position = new Vector2(2, 2);
                art.AddChild(fleets);
            }
        }

        // ---- the name over the foot of the picture, with a dot in the holder's colour (gold for a capital or headquarters)
        var nameRow = Ui.HBox(6);
        nameRow.Alignment = BoxContainer.AlignmentMode.Center;
        nameRow.MouseFilter = MouseFilterEnum.Ignore;
        var dot = new SectorMarks { Side = 7 }.Add(SectorMarks.Shape.Dot, hq && !dead ? Ui.Gold : oc, explored || dead);
        dot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        nameRow.AddChild(dot);
        var nm = Ui.Label(n, null, dead ? Ui.Danger : explored ? Ui.Text : Ui.Text2, 13, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.AddThemeConstantOverride("outline_size", 5);
        nm.AddThemeColorOverride("font_outline_color", new Color(Ui.PlanetSky, 0.9f));
        // as wide as the name, cut with an ellipsis only when it would not fit the tile
        float nameW = nm.GetThemeFont("font").GetStringSize(n, HorizontalAlignment.Left, -1, 13).X;
        nm.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nm.ClipText = true;
        nm.CustomMinimumSize = new Vector2(Math.Min(nameW + 2, TileWidth - 30), 0);
        nameRow.AddChild(nm);
        nameRow.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        nameRow.OffsetTop = -18; nameRow.OffsetBottom = 0;
        art.AddChild(nameRow);
        if (!explored || dead) return tile; // a world never looked at, or harvested: its picture and its name, nothing more

        // ---- loyalty, one line: your share from the left, theirs on the right
        int you = mine ? S.Host.Economy.SupportOf(n, Me) : pic?.Owner is { } po ? (Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport) : -1;
        var loyal = new LoyaltyLine { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        loyal.Set(you, Ui.You, Ui.Enemy);
        v.AddChild(loyal);

        // ---- the marks: energy slots, raw slots, then troops and defences
        var eco = S.Host.Economy;
        int eAll, eUsed, rAll, mines, troops, defences, troopsShort = 0;
        Color troopColor;
        if (mine)
        {
            eAll = eco.SlotsOf(n).Energy; eUsed = eco.EnergyUsed(n);
            rAll = eco.EffectiveRaw(n); mines = eco.OfKindAt(n, FacilityKind.Mine);
            troops = S.Host.Support.RegimentCount(n);
            troopsShort = Math.Max(0, S.Host.Support.RequiredGarrison(n) - troops);
            defences = DefenceKinds.Sum(k => eco.OfKindAt(n, k));
            troopColor = Ui.You;
        }
        else
        {
            var fac = pic.Facilities?.Value;
            eAll = pic.Status?.Value.Energy ?? 0; rAll = pic.Status?.Value.Raw ?? 0;
            eUsed = fac?.Values.Sum() ?? 0;
            mines = fac != null && fac.TryGetValue(FacilityKinds.ClassName(FacilityKind.Mine), out int m) ? m : 0;
            defences = fac?.Where(kv => FacilityKinds.TryParse(kv.Key, out var k) && DefenceKinds.Contains(k)).Sum(kv => kv.Value) ?? 0;
            troops = pic.Ground?.Value.Where(g => g.Side != Me).Sum(g => g.Total) ?? 0;
            troopColor = Ui.Enemy;
        }
        v.AddChild(new SectorMarks { SizeFlagsHorizontal = SizeFlags.ExpandFill, Cap = MarksCap }
            .Add(SectorMarks.Shape.Dot, EnergyColor, true, Math.Min(eUsed, eAll))
            .Add(SectorMarks.Shape.Dot, EnergyColor, false, Math.Max(0, eAll - eUsed)));
        v.AddChild(new SectorMarks { SizeFlagsHorizontal = SizeFlags.ExpandFill, Cap = MarksCap }
            .Add(SectorMarks.Shape.Dot, Ui.Gold, true, Math.Min(mines, rAll))
            .Add(SectorMarks.Shape.Dot, Ui.Gold, false, Math.Max(0, rAll - mines)));
        var ground = new SectorMarks { SizeFlagsHorizontal = SizeFlags.ExpandFill, Cap = MarksCap }
            .Add(SectorMarks.Shape.Square, troopColor, true, troops)
            .Add(SectorMarks.Shape.Square, Ui.Danger, false, troopsShort)
            .Add(SectorMarks.Shape.Diamond, Ui.Text2, true, defences);
        v.AddChild(ground);
        return tile;
    }

    const int MarksCap = 15;      // a row of small marks holds at most this many (the slot cap; 15 fit a tile); the tooltip has the number
    const int FleetMarksCap = 8;  // fleet triangles are larger: 8 fit across the planet

    /// <summary>Whether your side knows this system beyond its picture and name: yours, or looked at (the map's own fog test, S.Explored).
    /// One rule for the tile and the system's window.</summary>
    bool Scouted(string n) => !S.IsDestroyed(n) && (S.OwnerOf(n) == Me || (S.Picture(n) != null && S.Explored(n)));

    /// <summary>The numbers behind the marks, for the tooltip: the tile itself shows none.</summary>
    string TileTip(string n, bool mine, bool explored, bool dead, SystemPicture pic, Side? holder, bool known)
    {
        if (dead) return $"{n}: harvested{(S.DestroyedDay(n) is int d ? $" on day {d}" : "")}. Nothing is left.";
        if (!explored) return $"{n}: unexplored. Send a fleet or a spy to see who holds it and what is there.";
        var eco = S.Host.Economy;
        var lines = new List<string>();
        string who = mine ? (n == S.MyHqSystem ? "yours, your headquarters" : "yours") : !known ? "owner not known" : holder == null ? "nobody's" : $"held by the {S.NameOf(holder.Value)}";
        lines.Add($"{n}: {who}.");
        foreach (var (word, _, tip) in Alarms(n, mine)) lines.Add($"{word}: {tip}");
        if (mine)
        {
            int sup = eco.SupportOf(n, Me);
            lines.Add($"Loyalty: {sup}% for you, {100 - sup}% for the {S.NameOf(S.Enemy)}.");
            var sl = eco.SlotsOf(n);
            lines.Add($"Energy: {eco.EnergyUsed(n)} of {sl.Energy} building slots in use.");
            lines.Add($"Raw: {eco.OfKindAt(n, FacilityKind.Mine)} mines on {eco.EffectiveRaw(n)} raw slots, about +{RawPerMonth(n)} raw and +{RefinedPerMonth(n)} refined every 30 days.");
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            lines.Add($"Troops: {have} regiments{(need > 0 ? $", {need} needed to keep order" : "")}. Defences: {DefenceKinds.Sum(k => eco.OfKindAt(n, k))}.");
            var (yards, _) = YardsWords(n);
            if (yards != "none") lines.Add($"Yards: {yards}.");
        }
        else
        {
            int? last = S.LastSeenDay(n);
            lines.Add(S.Sees(n) ? "Seen live now." : last is int l ? $"Last seen on day {l}." : "Not seen since the war began.");
            if (pic.Owner is { } po) { int y = Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport; lines.Add($"Loyalty: {y}% for you, {100 - y}% for the {S.NameOf(S.Enemy)}."); }
            if (pic.Status is { } ss) lines.Add($"Slots: {ss.Value.Energy} energy, {ss.Value.Raw} raw.");
            if (pic.Facilities is { } f) lines.Add($"Buildings seen: {f.Value.Values.Sum()}.");
            if (pic.Ground is { } g) lines.Add($"Troops seen: {g.Value.Where(x => x.Side != Me).Sum(x => x.Total)}.");
        }
        lines.Add("Click for its buildings, fleets and forces. Double-click to show it on the map.");
        return string.Join("\n", lines);
    }

    /// <summary>The words that must jump out: uprising, blockade, an enemy fleet, a harvester.</summary>
    List<(string, Color, string)> Alarms(string n, bool mine)
    {
        var a = new List<(string, Color, string)>();
        if (S.IsDestroyed(n)) return a;
        bool sees = S.Sees(n);
        if (sees && S.VisibleEnemyFleetsAt(n).Count > 0) a.Add(("ENEMY FLEET", Ui.Danger, $"Enemy fleet here now: {string.Join("; ", S.VisibleEnemyFleetsAt(n))}"));
        if (sees && S.Host.Support.IsUprising(n)) a.Add(("UPRISING", Ui.Danger, "The people are in revolt: yards and mines stop until it ends."));
        if (sees && S.Game.State.Blockades.TryGetValue(n, out var b)) a.Add(("BLOCKADE", b == Me ? Ui.You : Ui.Warn, b == Me ? "Your fleet blockades it: its mines and yards stop." : "Blockaded by the enemy: mines and yards here stop."));
        if (!mine && S.Picture(n)?.HarvesterQueued is { Value: true }) a.Add(("HARVESTER", Ui.Danger, "A harvester was being built here when you last looked."));
        return a;
    }

    // ================================================================== numbers the cards share

    /// <summary>Yards here with nothing to build.</summary>
    int IdleYards(string n) => IdleYardFamilies(n).Count;

    (string, Color) YardsWords(string n)
    {
        var eco = S.Host.Economy;
        var parts = new List<string>();
        var idleFams = IdleYardFamilies(n);
        bool idle = idleFams.Count > 0;
        foreach (var fam in YardFamiliesAt(n).Where(f => !idleFams.Contains(f)))
            parts.Add($"{eco.Find(S.Host.Construction.ProjectAt(n, fam).Units[0])?.Class ?? "?"} {DaysLeftOf(n, fam)}d");
        if (parts.Count == 0) return idle ? ("idle", Ui.Warn) : ("none", Ui.Text3);
        return (string.Join(", ", parts) + (idle ? " +idle" : ""), idle ? Ui.Warn : Ui.Text);
    }

    /// <summary>About how much raw the mines here dig in 30 days at today's support: one raw a cycle each, the cycle as the core times it
    /// (rule: m2-mines-refineries-pools §Cycle length). A stopped mine (blockade, uprising) digs nothing.</summary>
    int RawPerMonth(string n) => PerMonth(n, FacilityKind.Mine);

    /// <summary>The same for refineries: one refined a cycle each, when the side has raw to refine.</summary>
    int RefinedPerMonth(string n) => PerMonth(n, FacilityKind.Refinery);

    int PerMonth(string n, FacilityKind kind)
    {
        var eco = S.Host.Economy;
        var p = eco.Params;
        string cls = FacilityKinds.ClassName(kind);
        int sup = Math.Clamp(eco.SupportOf(n, Me), p.SupportFloor, p.SupportCeiling);
        double sum = 0;
        foreach (var o in eco.Objects)
        {
            if (o.Category != ObjectCategory.Facility || !o.Completed || o.System != n || o.Class != cls || eco.IsSuspended(o)) continue;
            int d = Math.Max(1, EconomyModule.CycleDays(p, sup, eco.ShareOf(o.Id)));
            sum += (double)MonthDays / d;
        }
        return (int)Math.Round(sum);
    }

    void ShowOnMap(string n)
    {
        CloseSectorView();
        if (!G.TryFindSystem(n, out int idx)) return;
        _view.Centre(idx);
        _view.Select(idx);
        OnViewChanged();
    }
}
