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

    /// <summary>
    /// One system as a small tile, drawn from Paul's Paper frame "system-info" (2026-11-11; see Ui/SystemTile.cs): the name on top, the
    /// planet, a resources gauge and an energy gauge on its left, loyalty on its right, and four corner icons: buildings, fleets, troops,
    /// and the holder's emblem. An icon is lit in its side's colour when there is something there, faint when not; the troops icon turns
    /// red when the planet is short of its garrison, the buildings icon amber when a yard stands idle, the fleets icon orange when an
    /// enemy fleet is seen there. Colours are the factions' (Paul 2026-11-11): green the Hand, blue the Collective, whichever side you play. An uprising or a harvester circles the planet in red. A system you never looked at shows its planet and
    /// name only. The numbers are in the tooltip. Clicks: the buildings icon and the gauges open the Infrastructure window, the fleets
    /// icon your fleets there, anywhere else the Ground window; a double-click shows it on the map.
    /// </summary>
    PanelContainer SystemCardTile(string n)
    {
        bool dead = S.IsDestroyed(n);
        bool mine = !dead && S.OwnerOf(n) == Me;
        var pic = S.Picture(n);
        bool explored = Scouted(n);
        var (known, owner) = S.KnownOwnerOf(n);
        Side? holder = mine ? Me : dead ? null : owner;
        bool hq = n == S.MyHqSystem || (n == S.EnemyHqSystem && explored);

        var tile = new PanelContainer { CustomMinimumSize = new Vector2(TileWidth, TileHeight), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        tile.SetMeta(UiTest.FullName, "Sector card " + n);
        StyleBoxFlat Style(bool hover) => Ui.Box(hover ? new Color("17181c") : SystemTile.Ground, 0, hover ? new Color(1, 1, 1, 0.22f) : new Color(1, 1, 1, 0f), 1, 0, 0);
        tile.AddThemeStyleboxOverride("panel", Style(false));
        // a tile rebuilt under a still mouse (a new day) keeps its hover look
        Callable.From(() => { if (IsInstanceValid(tile) && tile.IsVisibleInTree() && tile.GetGlobalRect().HasPoint(tile.GetGlobalMousePosition())) tile.AddThemeStyleboxOverride("panel", Style(true)); }).CallDeferred();
        tile.MouseEntered += () => tile.AddThemeStyleboxOverride("panel", Style(true));
        tile.MouseExited += () => tile.AddThemeStyleboxOverride("panel", Style(false));

        var st = new SystemTile();
        tile.AddChild(st);
        var d = st.D;
        d.Name = n;
        d.You = SystemTile.Of(Me);
        d.Enemy = SystemTile.Of(S.Enemy);
        d.Faint = new Color(Ui.Text3, 0.28f);
        d.NameColor = dead ? Ui.Danger : !explored ? Ui.Text3 : (known || mine) && holder is Side hs ? SystemTile.NameOf(hs) : Ui.Text2;
        d.Planet = dead ? PlanetArt.HarvestedOf(n) ?? PlanetArt.Of(n) : PlanetArt.Of(n);
        // the planet drawn live by the Planet Maker's shader, turning with its own settings (Paul 2026-11-11: "the planets need to be
        // animated, like in the planet maker tool"); a harvested system shows the tool's harvested view live. A system never looked at
        // keeps its still, greyed picture (the fog is unchanged).
        if ((explored || dead) && _secPlanets.Of(n, dead) is Texture2D live)
        {
            d.Planet = live;
            _secPlanetKeys.Add(PlanetLiveSet.Key(n, dead));
        }
        else if (dead && PlanetArt.HarvestedOf(n) == null) d.PlanetTint = new Color(0.6f, 0.22f, 0.2f, 0.55f); // no remnant picture: tint the old one
        else if (!explored) d.PlanetTint = new Color(0.7f, 0.72f, 0.78f, 0.8f);
        var alarms = Alarms(n, mine);
        d.Ring = alarms.Any(a => a.Item2 == Ui.Danger && a.Item1 != "ENEMY FLEET");
        d.Gauges = explored && !dead;

        bool ownFleets = false;
        if (d.Gauges)
        {
            // ---- the gauges: raw slots and mines, energy slots and buildings
            var eco = S.Host.Economy;
            int troops, need = 0, buildings;
            if (mine)
            {
                d.EnergySlots = eco.SlotsOf(n).Energy; d.EnergyUsed = eco.EnergyUsed(n);
                d.RawSlots = eco.EffectiveRaw(n); d.Mines = eco.OfKindAt(n, FacilityKind.Mine);
                d.Loyalty = eco.SupportOf(n, Me);
                troops = S.Host.Support.RegimentCount(n);
                need = S.Host.Support.RequiredGarrison(n);
                buildings = eco.Objects.Count(o => o.Category == ObjectCategory.Facility && o.System == n);
            }
            else
            {
                var fac = pic.Facilities?.Value;
                d.EnergySlots = pic.Status?.Value.Energy ?? 0; d.RawSlots = pic.Status?.Value.Raw ?? 0;
                d.EnergyUsed = fac?.Values.Sum() ?? 0;
                d.Mines = fac != null && fac.TryGetValue(FacilityKinds.ClassName(FacilityKind.Mine), out int m) ? m : 0;
                d.Loyalty = pic.Owner is { } po ? (Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport) : -1;
                troops = pic.Ground?.Value.Where(g => g.Side != Me).Sum(g => g.Total) ?? 0;
                buildings = d.EnergyUsed;
            }
            Color side = SystemTile.Of(mine ? Me : S.Enemy);
            // ---- the corner icons: lit when something is there, in the side's colour; trouble in its own colour
            if (buildings > 0) d.Buildings = mine && IdleYardFamilies(n).Count > 0 ? Ui.Warn : side;
            if (troops < need) d.Troops = Ui.Danger;
            else if (troops > 0) d.Troops = side;
            int own = S.Game.State.Fleets.Values.Count(f => f.Side == Me && f.At == n);
            int enemy = S.Sees(n) ? S.VisibleEnemyFleetsAt(n).Count : !S.Sees(n) && pic?.Fleets is { } pf ? pf.Value.Count : 0;
            ownFleets = own > 0;
            if (enemy > 0) d.Fleets = SystemTile.Of(S.Enemy);
            else if (own > 0) d.Fleets = SystemTile.Of(Me);
            if (holder is Side h && (mine || known))
            {
                d.EmblemHand = h == Side.Hand;
                d.Emblem = hq ? Ui.Gold : SystemTile.Of(h);
            }
        }

        // three places to click (Paul, 2026-10-31: a link per system to its fleets, its infrastructure and its ground forces)
        string HotAt(Vector2 global)
        {
            var at = global - st.GetGlobalRect().Position;
            if (ownFleets && SystemTile.FleetIcon.Grow(3).HasPoint(at)) return "fleets";
            if (d.Gauges && SystemTile.InfraHot.HasPoint(at)) return "infra";
            return "ground";
        }
        tile.GuiInput += e =>
        {
            if (e is InputEventMouseMotion mm)
            {
                string h = HotAt(mm.GlobalPosition);
                tile.TooltipText = Ui.Wrap((h == "infra" ? "Click: the infrastructure window.\n" : h == "fleets" ? "Click: your fleets here.\n" : "") + TileTip(n, mine, explored, dead, pic, holder, known));
                return;
            }
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
            // the tile may be rebuilt by what the click does: do it after this input event is over
            if (mb.DoubleClick) { Later(() => ShowOnMap(n)); return; }
            string hot = HotAt(mb.GlobalPosition);
            Later(() => OpenFromTile(n, hot));
        };
        tile.TooltipText = Ui.Wrap(TileTip(n, mine, explored, dead, pic, holder, known));
        return tile;
    }

    /// <summary>A click on a tile: the fleets in orbit, the infrastructure, or the ground forces.</summary>
    void OpenFromTile(string n, string hot)
    {
        if (hot == "fleets")
        {
            var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).Take(3).ToList();
            if (own.Count > 0) { foreach (var f in own) OpenMgmt(MgKind.Fleet, fleet: f.Id); return; }
        }
        OpenMgmt(hot == "infra" ? MgKind.Infra : MgKind.Ground, n);
    }


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
        lines.Add("Click: the ground window (leaders, troops, fleets in orbit). Click the gauges or the buildings icon: infrastructure. Click the fleet icon: your fleets. Double-click: show it on the map.");
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
