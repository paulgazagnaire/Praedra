using System;
using Godot;

namespace PraedraViewer;

/// <summary>
/// The player's screen settings (user://settings.cfg): the size of everything on screen, and which events stop the clock. Screens only; the
/// game's rules never read these. The core's own pauses (battle, capture, invasion, harvest) stay on by default, as before; the others are
/// off until the player ticks them (user stories G3, L4).
/// </summary>
public static class UiSettings
{
    public static string Path = "user://settings.cfg";

    /// <summary>The UI size: 1 = 100 %. Applied to the whole window (map, text and panels grow together).</summary>
    public static float Scale = 1f;
    public static readonly float[] Scales = { 0.9f, 1f, 1.1f, 1.25f, 1.4f };

    // the core's pauses
    public static bool PauseBattle = true, PauseCapture = true, PauseInvasion = true, PauseHarvest = true;
    // pauses the player can add
    public static bool PauseEnemyAtMine, PauseUprising, PauseSensors, PauseCompleted, PauseMission;

    public static void Load()
    {
        var c = new ConfigFile();
        if (c.Load(Path) != Error.Ok) return;
        Scale = Math.Clamp((float)c.GetValue("ui", "scale", 1f), 0.75f, 1.6f);
        PauseBattle = (bool)c.GetValue("pause", "battle", true);
        PauseCapture = (bool)c.GetValue("pause", "capture", true);
        PauseInvasion = (bool)c.GetValue("pause", "invasion", true);
        PauseHarvest = (bool)c.GetValue("pause", "harvest", true);
        PauseEnemyAtMine = (bool)c.GetValue("pause", "enemy_at_mine", false);
        PauseUprising = (bool)c.GetValue("pause", "uprising", false);
        PauseSensors = (bool)c.GetValue("pause", "sensors", false);
        PauseCompleted = (bool)c.GetValue("pause", "completed", false);
        PauseMission = (bool)c.GetValue("pause", "mission", false);
    }

    public static void Save()
    {
        var c = new ConfigFile();
        c.SetValue("ui", "scale", Scale);
        c.SetValue("pause", "battle", PauseBattle);
        c.SetValue("pause", "capture", PauseCapture);
        c.SetValue("pause", "invasion", PauseInvasion);
        c.SetValue("pause", "harvest", PauseHarvest);
        c.SetValue("pause", "enemy_at_mine", PauseEnemyAtMine);
        c.SetValue("pause", "uprising", PauseUprising);
        c.SetValue("pause", "sensors", PauseSensors);
        c.SetValue("pause", "completed", PauseCompleted);
        c.SetValue("pause", "mission", PauseMission);
        var err = c.Save(Path);
        if (err != Error.Ok) GD.PrintErr($"Could not save the settings: {err}");
    }

    public static void ApplyScale(Window root)
    {
        if (root != null) root.ContentScaleFactor = Scale;
    }

    /// <summary>Does the clock stop for one of the core's pause reasons (StepOutcome.PauseReason)?</summary>
    public static bool PausesOn(string reason) => reason switch
    {
        "battle" => PauseBattle,
        "captured" => PauseCapture,
        "invaded" or "invasion" => PauseInvasion,
        "destroyed" => PauseHarvest,
        _ => true,
    };
}
