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
    public const string FileName = "name-migrations.csv", WordsFileName = "word-migrations.csv";
    private readonly Dictionary<string, string> _map;
    private readonly System.Text.RegularExpressions.Regex? _words; // word-migrations.csv word and identifier rows (tools/names/rename_words.py)
    private readonly Dictionary<string, string> _wordMap = new(StringComparer.Ordinal);

    private NameMigrations(Dictionary<string, string> map, IEnumerable<(string Kind, string Old, string New)>? words = null)
    {
        _map = map;
        var parts = new List<string>();
        // Same rules as rename_words.py word_pattern: identifiers whole, words inside identifiers, longest first.
        foreach (var (kind, from, to) in (words ?? Array.Empty<(string, string, string)>()).Where(w => w.Kind is "word" or "identifier")
            .OrderByDescending(w => w.Old.Length).ThenBy(w => w.Old, StringComparer.Ordinal))
        {
            if (!_wordMap.TryAdd(from, to)) throw new DataLoadException($"{WordsFileName}: '{from}' appears twice.");
            string e = System.Text.RegularExpressions.Regex.Escape(from);
            parts.Add(kind == "identifier" ? $@"\b{e}\b"
                : char.IsUpper(from[0]) && from.Any(char.IsLower) ? $"{e}(?![a-z])"
                : from.All(c => !char.IsLower(c)) ? $"(?<![A-Z]){e}(?![A-Z])"
                : $"(?<![A-Za-z]){e}(?![a-z])");
        }
        if (parts.Count > 0) _words = new System.Text.RegularExpressions.Regex(string.Join("|", parts), System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public static readonly NameMigrations None = new(new Dictionary<string, string>(StringComparer.Ordinal));

    public int Count => _map.Count;

    public static NameMigrations Parse(string text, string source = FileName, string? wordsText = null)
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
        var words = new List<(string, string, string)>();
        if (wordsText != null)
        {
            var w = CsvTable.Parse(wordsText, WordsFileName);
            w.Require("kind", "old", "new");
            for (int r = 0; r < w.RowCount; r++) words.Add((w.Get(r, "kind"), w.Get(r, "old"), w.Get(r, "new")));
        }
        return new NameMigrations(map, words);
    }

    /// <summary>The name table, with the word table (<see cref="WordsFileName"/>) from the same folder when it is there.</summary>
    public static NameMigrations Load(string path)
    {
        string words = Path.Combine(Path.GetDirectoryName(path) ?? ".", WordsFileName);
        return Parse(File.ReadAllText(path), Path.GetFileName(path), File.Exists(words) ? File.ReadAllText(words) : null);
    }

    /// <summary>
    /// An identifier (a save's property name, an enum value) with its old words replaced (HandWinsOnHqLost -> HandWinsOnHqLost, Hand ->
    /// Hand). Only text that looks like an identifier is touched, so a name with spaces never is.
    /// </summary>
    public string CurrentIdentifier(string id) =>
        _words == null || !IsIdentifier(id) ? id : _words.Replace(id, m => _wordMap[m.Value]);

    private static bool IsIdentifier(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>The name to use today for <paramref name="name"/> (itself when it was never renamed).</summary>
    public string Current(string name) => _map.TryGetValue(name, out var n) ? n : name;

    /// <summary>The save's JSON with every string value and every object key that is an old name replaced by its new name.</summary>
    public string Apply(string json)
    {
        if (_map.Count == 0 && _words == null) return json;
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
                    copy[CurrentIdentifier(Current(key))] = Rename(value);
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
                return JsonValue.Create(_map.ContainsKey(s) ? Current(s) : CurrentIdentifier(s));
            default:
                return node;
        }
    }
}
