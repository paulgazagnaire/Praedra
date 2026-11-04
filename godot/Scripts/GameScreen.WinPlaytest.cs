using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using RejectReason = Praedra.Core.Clock.RejectReason;
using UnloadRegimentsOrder = Praedra.Core.Clock.UnloadRegimentsOrder;
using LoadRegimentsOrder = Praedra.Core.Clock.LoadRegimentsOrder;
using UnloadSquadronsOrder = Praedra.Core.Clock.UnloadSquadronsOrder;
using LoadSquadronsOrder = Praedra.Core.Clock.LoadSquadronsOrder;
using TransferShipsOrder = Praedra.Core.Clock.TransferShipsOrder;
using MergeFleetsOrder = Praedra.Core.Clock.MergeFleetsOrder;
using SplitFleetOrder = Praedra.Core.Clock.SplitFleetOrder;
using SendRegimentOrder = Praedra.Core.Clock.SendRegimentOrder;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>The management windows' hooks for the headless playtest and the screenshots. Moved from GameScreen.Windows.cs (Ponytail, moves only).</summary>
public partial class GameScreen
{
    // ================================================================== for the playtest

    /// <summary>Drop a chip on a zone as the mouse would: returns null when it was done, else why not.</summary>
    internal string MgDrop(string chipKey, string zoneKey)
    {
        NewDay();
        var z = _zones.FirstOrDefault(x => x.Key == zoneKey && IsInstanceValid(x.Box));
        if (z == null) return "no such zone: " + zoneKey;
        var all = Drag.ParseAll(chipKey);
        if (all.Count == 0) return "bad chip: " + chipKey;
        string why = Judge(z, all[0]);
        if (why != null) return why == "" ? "already there" : why;
        foreach (var d in all) if (Judge(z, d) == null) RunDrop(() => z.Drop(d));
        RefreshMgmt();
        foreach (var w in _mgWins.ToList()) RebuildMg(w);
        return null;
    }

    internal bool MgHasZone(string zoneKey) => _zones.Any(x => x.Key == zoneKey && IsInstanceValid(x.Box));

    internal string MgText(string key)
    {
        if (_mgWins.FirstOrDefault(w => w.Key == key) is not { } w) return "";
        RebuildMg(w);   // as the next frame would
        return LabelsIn(w.Head) + "\n" + string.Join("\n", w.Pages.Values.Select(p => PageLabels(p.Page))) + "\n" + LabelsIn(w.Body);
    }

    /// <summary>Every label of a tab, shown or not (the playtest reads tabs that are not in front).</summary>
    static string PageLabels(Control page)
    {
        bool was = page.Visible;
        page.Visible = true;
        string text = LabelsIn(page);
        page.Visible = was;
        return text;
    }

    internal string MgTab(string key) => _mgWins.FirstOrDefault(w => w.Key == key)?.Tab;
    internal IReadOnlyList<string> MgTabs(string key) => _mgWins.FirstOrDefault(w => w.Key == key)?.Pages.Keys.ToList() ?? new List<string>();

    /// <summary>For screenshots: show the drop zones lit as if this chip were being dragged.</summary>
    internal void MgShowDrag(string chipKey)
    {
        // "L:?" picks the first leader of yours standing where the first window is (screenshots don't know ids)
        if (chipKey == "L:?" && _mgWins.FirstOrDefault(x => x.System != null) is { } w)
            chipKey = S.MyCharacters.Where(c => S.Host.Characters.LocationOf(c) == w.System && c.AttachedFleet == 0).OrderBy(c => c.Id).Select(c => $"L:{c.Id}").FirstOrDefault() ?? chipKey;
        if (Drag.Parse(chipKey) is { } d) { _dragging = d; _dragCount = Drag.ParseAll(chipKey).Count; _shotDrag = d; LightZones(d); }
    }
    Drag? _shotDrag;   // a screenshot holds a drag without a mouse
}
