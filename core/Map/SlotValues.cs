using Praedra.Core.Data;

namespace Praedra.Core.Map;

/// <summary>Energy, raw material and populated flag for one system. Source: data/design/slot-values-draft.csv (economy spec, draft).</summary>
public readonly record struct SystemSlots(string Name, int Energy, int Raw, bool Populated);

/// <summary>Per-system slot values, one record per system, checked against the planet list.</summary>
public sealed class SlotValues
{
    private readonly SystemSlots[] _bySystem;

    private SlotValues(SystemSlots[] bySystem) { _bySystem = bySystem; }

    /// <summary>Slots for a system, by its galaxy index.</summary>
    public SystemSlots Of(int systemIndex) => _bySystem[systemIndex];

    public int Count => _bySystem.Length;

    public const string File = "slot-values-draft.csv";

    public static SlotValues Load(string path, Galaxy galaxy) => Parse(System.IO.File.ReadAllText(path), galaxy);

    /// <summary>
    /// Every row must name a galaxy system (once), every galaxy system must have a row, and sector and group must agree
    /// with the planets file. energy and raw must be non-negative integers, populated 0 or 1.
    /// </summary>
    public static SlotValues Parse(string text, Galaxy galaxy)
    {
        var t = CsvTable.Parse(text, File);
        t.Require("name", "sector", "group", "energy", "raw", "populated");
        var slots = new SystemSlots?[galaxy.Systems.Count];
        for (int r = 0; r < t.RowCount; r++)
        {
            var name = t.Get(r, "name").Trim();
            if (!galaxy.TryFindSystem(name, out var idx))
                throw new DataLoadException($"{t.Where(r)}: system '{name}' is not in the planet list.");
            if (slots[idx] != null)
                throw new DataLoadException($"{t.Where(r)}: system '{name}' appears twice.");
            var sys = galaxy.Systems[idx];
            var sector = t.Get(r, "sector").Trim();
            if (sector != sys.Sector)
                throw new DataLoadException($"{t.Where(r)}: system '{name}' is in sector '{sector}' here but '{sys.Sector}' in the planet list.");
            var group = t.Get(r, "group").Trim();
            if (group != sys.Group)
                throw new DataLoadException($"{t.Where(r)}: system '{name}' has group '{group}' here but '{sys.Group}' in the planet list.");
            int energy = t.GetInt(r, "energy"), raw = t.GetInt(r, "raw"), pop = t.GetInt(r, "populated");
            if (energy < 0 || raw < 0)
                throw new DataLoadException($"{t.Where(r)}: system '{name}' has a negative energy or raw value.");
            if (pop != 0 && pop != 1)
                throw new DataLoadException($"{t.Where(r)}: system '{name}' has populated {pop}; it must be 0 or 1.");
            slots[idx] = new SystemSlots(name, energy, raw, pop == 1);
        }
        var result = new SystemSlots[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not { } s)
                throw new DataLoadException($"{File}: system '{galaxy.Systems[i].Name}' from the planet list has no row.");
            result[i] = s;
        }
        return new SlotValues(result);
    }
}
