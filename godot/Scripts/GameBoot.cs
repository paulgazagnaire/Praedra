using Praedra.Core.AI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Praedra.Core.Clock;
using Praedra.Core.Setup;

using Side = Praedra.Core.Clock.Side;
using FileAccess = Godot.FileAccess;

namespace PraedraViewer;

/// <summary>
/// How hard the game is: the Classic head start (StartPreset, setup-victory/02) and how well the computer opponent plays (the core's AiLevel,
/// ai/09 §6). One choice drives both.
/// </summary>
public enum Preset { Easy, Medium, Hard }

/// <summary>What the screens need from the core to start, load and save a game: the data, the new-game setup, the computer opponent and the winner.</summary>
public static class GameBoot
{
    /// <summary>DEFAULT false: the Hand AI does not know where the Hearth is. true = it is told (as in `sim aivai`), and strikes it at about day 240.</summary>
    public const bool HandAiKnowsHearth = false;

    /// <summary>
    /// DEFAULT true: the computer opponent reads the other side only through its own side's fog-of-war view (SimpleAiParams.UseFog).
    /// rule: economy-ai D4 "AI plays fair with fog of war" (Decided, also m5-fog-and-hq §N8: no difficulty widens the view).
    /// </summary>
    public const bool AiUsesFog = true;

    /// <summary>The Hand's capital (the Collective's goal), the core's own name for it. rule: m2-control-and-capture (capital system).</summary>
    public const string HandCapital = Praedra.Core.Control.ControlModule.CapitalSystem;

    static GameData _data;

    /// <summary>
    /// Loads the game data once. The exported game keeps the tables inside its package as data/design/*.txt and data/classic/*.txt
    /// (Godot drops .csv from exports); the core wants real files, so they are copied to the user folder as .csv first.
    /// </summary>
    public static GameData LoadData()
    {
        if (_data != null) return _data;
        string root = ProjectSettings.GlobalizePath("user://gamedata");
        foreach (var sub in new[] { "design", "design/day-one", "classic" })
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

    /// <summary>The core's start preset for a side and difficulty. Easy exists for the Collective only, so Hand/Easy plays MedE.</summary>
    public static StartPreset PresetFor(Side player, Preset p) => (player, p) switch
    {
        (Side.Collective, Preset.Easy) => StartPreset.Easy,
        (Side.Hand, Preset.Easy) => StartPreset.MedE,
        (Side.Collective, Preset.Medium) => StartPreset.MedA,
        (Side.Hand, Preset.Medium) => StartPreset.MedE,
        (Side.Collective, _) => StartPreset.HardA,
        _ => StartPreset.HardE,
    };

    /// <summary>Display name of a space battle ruleset (start screen, top bar, How to play, battle report).</summary>
    public static string RulesName(Praedra.Core.Battle.BattleRuleset r) => r == Praedra.Core.Battle.BattleRuleset.New ? "New (weapons matter)" : "Classic";

    /// <summary>
    /// The core settings for the chosen space battle rules: only BattleParams.Ruleset is set, every other battle value keeps its default.
    /// rule: m4-battle-new §Rule (New) (ruleset switch, DEFAULT New, Paul 2026-10-18)
    /// </summary>
    public static GameSettings SettingsFor(Praedra.Core.Battle.BattleRuleset r) =>
        new() { Battle = new Praedra.Core.Battle.BattleParams { Ruleset = r } };

    public static GameHost NewGame(GameData data, Side player, Preset preset, int seed = 1, Praedra.Core.Battle.BattleRuleset rules = Praedra.Core.Battle.BattleRuleset.New)
    {
        LoadData();
        return Praedra.Core.Setup.NewGame.Create(data, new NewGameOptions { Seed = seed, Preset = PresetFor(player, preset), Data = data, NewGameData = _newGameData, Settings = SettingsFor(rules) });
    }

    public static GameHost LoadGame(GameData data, string saveJson) => GameHost.Load(saveJson, null, data);

    // ------------------------------------------------------------------ opponent

    /// <summary>The computer opponent's level for a difficulty (ai/09 §6: Hard is smarter, never a cheat; every level plays with fog).</summary>
    public static AiLevel AiLevelFor(Preset p) => p switch { Preset.Easy => AiLevel.Easy, Preset.Hard => AiLevel.Hard, _ => AiLevel.Medium };

    /// <summary>The computer opponent for <paramref name="aiSide"/>: the simple AI (core/AI) at <paramref name="level"/>.</summary>
    public static SimpleAi CreateOpponent(GameHost host, Side aiSide, AiLevel level = AiLevel.Medium)
    {
        var ai = new SimpleAi(host, aiSide, AiLevels.For(level, new SimpleAiParams { UseFog = AiUsesFog }));
        host.Battles.SetControllers(handAi: aiSide == Side.Hand, collectiveAi: aiSide == Side.Collective); // rule: B6a §1 (the other side is the human)
        // DEFAULT: the Hand AI is NOT told where the Hearth is (economy-ai D4: the AI plays fair with fog of war). With fog on it reads
        // the HQ only from its own picture (m5-fog-and-hq §N5 KnownHq). It still conquers systems and wins if it captures the HQ.
        if (aiSide == Side.Hand && HandAiKnowsHearth) ai.KnownEnemyHq = host.HqSystem;
        return ai;
    }

    /// <summary>The winning side, or null while the game goes on.</summary>
    public static Side? WinnerOf(GameHost host) => host.Victory.Winner;

    public static string WinReason(GameHost host) => host.Victory.Reason;


    // ------------------------------------------------------------------ save files

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

    public static void WriteSave(string path, string json, Side player, AiLevel level = AiLevel.Medium, int day = -1)
    {
        using (var f = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            if (f == null) throw new IOException($"Cannot write {path}: {FileAccess.GetOpenError()}");
            // First line: who the player is and the opponent's level (the core's save holds the game only). Second line: the core's save.
            f.StoreLine(PlayerSaveHeader.Write(player, level));
            f.StoreString(json);
        }
        // A one-line note beside the save, so the save list can show the side, day and time without reading the whole game.
        using var meta = FileAccess.Open(path + ".meta", FileAccess.ModeFlags.Write);
        meta?.StoreLine($"{player}|{level}|{day}|{(long)Time.GetUnixTimeFromSystem()}");
    }

    // ------------------------------------------------------------------ save slots (user story L2)

    public const int Slots = 6;

    /// <summary>Slot 1 is the one save slot of older versions (so their saves show up as slot 1); the others sit beside it.</summary>
    public static string SlotPath(int n) => n <= 1 ? SaveSlot : SaveSlot.Replace("save1.json", $"save{n}.json");

    /// <summary>What a save list shows for a slot: "Hand, day 120, Medium · 2026-10-28 14:03", or null when the slot is empty.</summary>
    public static string SlotInfo(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        string side = null, level = null, day = null; long when = (long)FileAccess.GetModifiedTime(path);
        if (FileAccess.FileExists(path + ".meta"))
        {
            using var m = FileAccess.Open(path + ".meta", FileAccess.ModeFlags.Read);
            var parts = (m?.GetLine() ?? "").Split('|');
            if (parts.Length >= 4) { side = parts[0]; level = parts[1]; day = parts[2] == "-1" ? null : parts[2]; if (long.TryParse(parts[3], out long w)) when = w; }
        }
        if (side == null)
        {
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f != null && PlayerSaveHeader.Read(f.GetLine()) is var (ps, pl)) { side = ps.ToString(); level = pl.ToString(); }
        }
        string date = Time.GetDatetimeStringFromUnixTime(when + (long)(Time.GetTimeZoneFromSystem()["bias"].AsInt32() * 60), true).Replace('T', ' ');
        if (date.Length > 16) date = date[..16];
        return $"{side ?? "?"}{(day != null ? $", day {day}" : "")}{(level != null ? $", {level}" : "")}  ·  {date}";
    }

    /// <summary>A save's player, core save and opponent level (older saves without a level: Medium).</summary>
    public static (Side player, string json, AiLevel level)? ReadSave(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) return null;
        string all = f.GetAsText();
        int nl = all.IndexOf('\n');
        if (nl < 0) return null;
        if (PlayerSaveHeader.Read(all[..nl]) is not var (side, level)) return null;
        return (side, all[(nl + 1)..], level);
    }
}
