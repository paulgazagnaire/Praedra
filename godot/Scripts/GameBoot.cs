using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Rebellion.Core.Clock;
using Rebellion.Core.Setup;

using Side = Rebellion.Core.Clock.Side;
using FileAccess = Godot.FileAccess;

namespace RebellionViewer;

/// <summary>How hard the computer opponent plays. Only a label until the core's NewGameOptions takes it.</summary>
public enum Preset { Easy, Medium, Hard }

/// <summary>What the computer opponent does once per day, before the core's Step.</summary>
public interface IOpponent
{
    void PlanDay();
}

/// <summary>Opponent that does nothing (used until the core's SimpleAi exists).</summary>
public sealed class NoOpOpponent : IOpponent
{
    public void PlanDay() { }
}

/// <summary>
/// THE ADAPTER. Everything the UI needs from the not-yet-finished parts of the core (new-game setup, the opponent AI, the victory check)
/// goes through this one file, so the lead can switch to the real ones in a few lines. Search this file for "TODO(lead)".
///
/// Today: the game is built the way core/Setup/TestCampaign.cs builds its test game (two regions, a few fleets), the opponent does nothing,
/// and there is no winner.
/// </summary>
public static class GameBoot
{
    /// <summary>The seed of the placeholder game. TODO(lead): NewGameOptions may carry its own seed.</summary>
    public const ulong DefaultSeed = 1998UL;

    /// <summary>True while the start position is the placeholder campaign and the opponent does nothing; the start screen says so.
    /// TODO(lead): set to false once NewGame.Create and SimpleAi are wired in.</summary>
    public const bool PlaceholderMode = true;

    static GameData _data;

    /// <summary>
    /// Loads the game data once. The exported game keeps the tables inside its package as data/design/*.txt and data/interim/*.txt
    /// (Godot drops .csv from exports); the core wants real files, so they are copied to the user folder as .csv first.
    /// </summary>
    public static GameData LoadData()
    {
        if (_data != null) return _data;
        string root = ProjectSettings.GlobalizePath("user://gamedata");
        foreach (var sub in new[] { "design", "interim" })
        {
            string dir = Path.Combine(root, "data", sub);
            Directory.CreateDirectory(dir);
            foreach (var file in DirAccess.GetFilesAt("res://data/" + sub))
            {
                if (!file.EndsWith(".txt", StringComparison.Ordinal)) continue;
                using var f = FileAccess.Open($"res://data/{sub}/{file}", FileAccess.ModeFlags.Read);
                if (f == null) throw new InvalidOperationException($"Cannot read res://data/{sub}/{file}: {FileAccess.GetOpenError()}");
                File.WriteAllText(Path.Combine(dir, file[..^4] + ".csv"), f.GetAsText());
            }
        }
        return _data = GameData.Load(root);
    }

    // ------------------------------------------------------------------ new game / load

    /// <summary>A new game for the player's side. TODO(lead): replace the body with
    /// <c>return NewGame.Create(data, new NewGameOptions { PlayerSide = player, Preset = preset, Seed = seed });</c></summary>
    public static GameHost NewGame(GameData data, Side player, Preset preset, ulong seed = DefaultSeed)
    {
        // TODO(lead): NewGame.Create(GameData, NewGameOptions) -> GameHost (see core/Setup/NewGame*).
        // Placeholder: the test campaign's deal (not the real start). It also registers a one-off capture of Denon on day 400 (test script).
        return TestCampaign.New(seed, null, data).Host;
    }

    /// <summary>A game from save text. TODO(lead): if NewGame has its own loader (it may need to re-register hooks), call it here.</summary>
    public static GameHost LoadGame(GameData data, string saveJson)
    {
        // TODO(lead): same switch as NewGame. TestCampaign.Load registers the test campaign's day-400 capture hook again.
        return TestCampaign.Load(saveJson, null, data).Host;
    }

    // ------------------------------------------------------------------ opponent

    /// <summary>The computer opponent for <paramref name="aiSide"/>. TODO(lead): replace the body with
    /// <c>return new SimpleAiAdapter(new SimpleAi(host, aiSide));</c> where the adapter calls <c>ai.PlanDay()</c>
    /// (or make SimpleAi implement <see cref="IOpponent"/>).</summary>
    public static IOpponent CreateOpponent(GameHost host, Side aiSide)
    {
        // TODO(lead): var ai = new SimpleAi(host, aiSide); return new DelegateOpponent(ai.PlanDay);
        return new NoOpOpponent();
    }

    // ------------------------------------------------------------------ victory

    /// <summary>The winning side, or null while the game goes on. TODO(lead): return <c>host.Victory?.Winner</c> once the core has it.</summary>
    public static Side? WinnerOf(GameHost host)
    {
        // TODO(lead): return host.Victory?.Winner;
        return null;
    }

    // ------------------------------------------------------------------ placeholders for facts the new setup will own

    /// <summary>Each side's headquarters system, drawn with a marker on the map. TODO(lead): read it from the new setup if it records one.</summary>
    public static string CapitalOf(Side side) => side == Side.Empire ? "Coruscant" : "Sullust";

    // ------------------------------------------------------------------ save files

    public static string SaveFolder => ProjectSettings.GlobalizePath("user://");
    public const string SaveSlot = "user://save1.json";
    public const string AutoSaveSlot = "user://autosave.json";

    public static bool SaveExists(string path) => FileAccess.FileExists(path);

    public static void WriteSave(string path, string json, Side player)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) throw new IOException($"Cannot write {path}: {FileAccess.GetOpenError()}");
        // First line: who the player is (the core's save holds the game only). Second line: the core's save.
        f.StoreLine($"REBELLION-SAVE 1 {player}");
        f.StoreString(json);
    }

    public static (Side player, string json)? ReadSave(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) return null;
        string all = f.GetAsText();
        int nl = all.IndexOf('\n');
        if (nl < 0) return null;
        var parts = all[..nl].Trim().Split(' ');
        if (parts.Length != 3 || parts[0] != "REBELLION-SAVE" || !Enum.TryParse(parts[2], out Side side)) return null;
        return (side, all[(nl + 1)..]);
    }
}
