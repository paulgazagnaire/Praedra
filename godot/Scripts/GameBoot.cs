using Rebellion.Core.AI;
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

/// <summary>Opponent that runs a callback once per day (the core's SimpleAi).</summary>
public sealed class DelegateOpponent : IOpponent
{
    private readonly System.Action _plan;
    public DelegateOpponent(System.Action plan) { _plan = plan; }
    public void PlanDay() => _plan();
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
    public const bool PlaceholderMode = false;

    /// <summary>DEFAULT false: the Empire AI does not know where the Rebel HQ is. true = it is told (as in `sim aivai`), and strikes it at about day 240.</summary>
    public const bool EmpireAiKnowsRebelHq = false;

    static GameData _data;

    /// <summary>
    /// Loads the game data once. The exported game keeps the tables inside its package as data/design/*.txt and data/interim/*.txt
    /// (Godot drops .csv from exports); the core wants real files, so they are copied to the user folder as .csv first.
    /// </summary>
    public static GameData LoadData()
    {
        if (_data != null) return _data;
        string root = ProjectSettings.GlobalizePath("user://gamedata");
        foreach (var sub in new[] { "design", "design/day-one", "interim" })
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
        _newGameData = NewGameData.Load(root);
        return _data = GameData.Load(root);
    }

    static NewGameData _newGameData;

    // ------------------------------------------------------------------ new game / load

    /// <summary>The core's start preset for a side and difficulty. Easy exists for the Alliance only, so Empire/Easy plays MedE.</summary>
    public static StartPreset PresetFor(Side player, Preset p) => (player, p) switch
    {
        (Side.Alliance, Preset.Easy) => StartPreset.Easy,
        (Side.Empire, Preset.Easy) => StartPreset.MedE,
        (Side.Alliance, Preset.Medium) => StartPreset.MedA,
        (Side.Empire, Preset.Medium) => StartPreset.MedE,
        (Side.Alliance, _) => StartPreset.HardA,
        _ => StartPreset.HardE,
    };

    public static GameHost NewGame(GameData data, Side player, Preset preset, int seed = 1)
    {
        LoadData();
        return Rebellion.Core.Setup.NewGame.Create(data, new NewGameOptions { Seed = seed, Preset = PresetFor(player, preset), Data = data, NewGameData = _newGameData });
    }

    public static GameHost LoadGame(GameData data, string saveJson) => GameHost.Load(saveJson, null, data);

    // ------------------------------------------------------------------ opponent

    /// <summary>The computer opponent for <paramref name="aiSide"/>: the simple AI (core/AI).</summary>
    public static IOpponent CreateOpponent(GameHost host, Side aiSide)
    {
        var ai = new SimpleAi(host, aiSide);
        // DEFAULT (playable slice): the Empire AI is NOT told where the Rebel HQ is (economy-ai D4: the AI plays fair with fog of war).
        // It still conquers systems and wins if it captures the HQ. Searching for the HQ comes with M5 (m5-fog-and-hq).
        if (aiSide == Side.Empire && EmpireAiKnowsRebelHq) ai.KnownEnemyHq = host.HqSystem;
        return new DelegateOpponent(ai.PlanDay);
    }

    /// <summary>The winning side, or null while the game goes on.</summary>
    public static Side? WinnerOf(GameHost host) => host.Victory.Winner;

    public static string WinReason(GameHost host) => host.Victory.Reason;

    /// <summary>Headquarters system of a side (drawn with a marker): the Rebel HQ from the setup, the Empire's is Coruscant.</summary>
    public static string CapitalOf(GameHost host, Side side) => side == Side.Empire ? "Coruscant" : (host.HqSystem ?? "Sullust");

    // ------------------------------------------------------------------ save files

    public static string SaveFolder => ProjectSettings.GlobalizePath("user://");
    // Not const: the headless playtest points them at throw-away files so it never touches a player's saves.
    public static string SaveSlot = "user://save1.json";
    public static string AutoSaveSlot = "user://autosave.json";
    public static string HowToFlag = "user://howto_seen.txt";

    /// <summary>True until the player has closed the "How to play" overlay once.</summary>
    public static bool HowToUnseen() => !FileAccess.FileExists(HowToFlag);

    public static void MarkHowToSeen()
    {
        try { using var f = FileAccess.Open(HowToFlag, FileAccess.ModeFlags.Write); f?.StoreLine("seen"); }
        catch (Exception e) { GD.PrintErr("Could not remember the how-to flag: " + e.Message); }
    }

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
