using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rebellion.Core.Clock;

/// <summary>
/// Save file content. A save is made between Steps and holds state, Day, RNG state, the order queue and the next
/// sequence number; events are not saved. rule: m1-clock-and-orders §The clock
/// </summary>
public sealed record SaveData(GameState State, int Day, RngState Rng, IReadOnlyList<Order> Queue, long NextSequence);

public static class GameSave
{
    // Property order is declaration order; dictionaries are SortedDictionary (ordinal). Only integers and strings
    // are written, so culture cannot matter (InvariantGlobalization is also on).
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false, IncludeFields = false };

    public static SaveData Capture(Game g) => new(g.State, g.Day, g.Rng.Export(), g.Queue.ToList(), g.NextSequence);

    public static string Save(Game g) => JsonSerializer.Serialize(Capture(g), Options);

    public static Game Load(string json)
    {
        var d = JsonSerializer.Deserialize<SaveData>(json, Options) ?? throw new InvalidDataException("empty save");
        var state = new GameState();
        foreach (var kv in d.State.Markers) state.Markers[kv.Key] = kv.Value;
        return new Game(state, d.Day, GameRng.Import(d.Rng), d.Queue, d.NextSequence);
    }

    /// <summary>
    /// Canonical state hash (lower-case hex SHA-256): state, Day, RNG draw count, queue, next sequence.
    /// The RNG internal state is left out on purpose, so two seeds that were never drawn from hash alike (sheet K10).
    /// rule: m1-clock-and-orders §Seeded random numbers rule 10
    /// </summary>
    public static string StateHash(Game g)
    {
        var canonical = new
        {
            g.State, g.Day, g.Rng.Draws, Queue = g.Queue.OrderBy(o => o.Sequence).ToList(), g.NextSequence,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Options);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLower(CultureInfo.InvariantCulture);
    }
}
