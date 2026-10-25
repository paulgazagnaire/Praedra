using System.Text.Json.Nodes;
using Praedra.Core.Data;

namespace Praedra.Core.Setup;

/// <summary>
/// Old placeholder names and the Praedra names that replaced them (data/design/name-migrations.csv: kind, old, new). The data files and the
/// code already use the new names (tools/names/apply_names.py); this table only renames what an older save still holds, so a game saved
/// before the rename loads. rule: CLAUDE.md "Orders in, events out" (a save is the state; renaming its strings changes no rule).
/// </summary>
public sealed class NameMigrations
{
    public const string FileName = "name-migrations.csv";
    private readonly Dictionary<string, string> _map;

    private NameMigrations(Dictionary<string, string> map) => _map = map;

    public static readonly NameMigrations None = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public int Count => _map.Count;

    public static NameMigrations Parse(string text, string source = FileName)
    {
        var t = CsvTable.Parse(text, source);
        t.Require("kind", "old", "new");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int r = 0; r < t.RowCount; r++)
        {
            string from = t.Get(r, "old"), to = t.Get(r, "new");
            if (from.Length == 0 || to.Length == 0) throw new DataLoadException($"{t.Where(r)}: old and new names must both be filled.");
            if (!map.TryAdd(from, to)) throw new DataLoadException($"{t.Where(r)}: '{from}' appears twice.");
        }
        foreach (var (from, to) in map)
            if (map.ContainsKey(to)) throw new DataLoadException($"{source}: '{to}' is both a new name (for '{from}') and an old name.");
        return new NameMigrations(map);
    }

    public static NameMigrations Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>The name to use today for <paramref name="name"/> (itself when it was never renamed).</summary>
    public string Current(string name) => _map.TryGetValue(name, out var n) ? n : name;

    /// <summary>The save's JSON with every string value and every object key that is an old name replaced by its new name.</summary>
    public string Apply(string json)
    {
        if (_map.Count == 0) return json;
        var root = JsonNode.Parse(json);
        return root == null ? json : Rename(root)!.ToJsonString();
    }

    private JsonNode? Rename(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in o.ToList()) // keep the key order of the save
                {
                    o.Remove(key);
                    copy[Current(key)] = Rename(value);
                }
                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                foreach (var item in a.ToList()) { a.Remove(item); copy.Add(Rename(item)); }
                return copy;
            }
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(Current(s));
            default:
                return node;
        }
    }
}
