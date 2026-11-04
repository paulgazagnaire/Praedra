using System;
using System.Collections.Generic;

namespace PraedraViewer;

/// <summary>
/// Each system's lore for its Lore tab (Paul 2026-11-04: "a lore tab for each system"): data/design/system-lore.csv, copied from the
/// Lore Master's specs/creative/08-planet-presets.json text. Screens only; no rule reads it. A system missing from the file has no lore yet.
/// </summary>
public static class SystemLore
{
    public sealed record Entry(string World, string FromSpace, string Moons, string People, string Resources, string Story, string History);

    static Dictionary<string, Entry> _all;

    /// <summary>The lore of a system, or null when the file has none yet.</summary>
    public static Entry Of(string system)
    {
        if (_all == null)
        {
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            string path = System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("user://gamedata"), "data", "design", "system-lore.csv");
            if (!System.IO.File.Exists(path)) Godot.GD.PushWarning($"System lore: {path} is missing, so every Lore tab says it is still to come.");
            else
            {
                var t = Praedra.Core.Data.CsvTable.Load(path);
                for (int r = 0; r < t.RowCount; r++)
                    map[t.Get(r, "system")] = new Entry(t.Get(r, "world"), t.Get(r, "from_space"), t.Get(r, "moons"), t.Get(r, "people"),
                        t.Get(r, "resources"), t.Get(r, "story"), t.Get(r, "history"));
            }
            _all = map;
        }
        return system != null && _all.TryGetValue(system, out var e) ? e : null;
    }
}
