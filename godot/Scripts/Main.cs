using System;
using System.Globalization;
using System.Linq;
using Godot;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Defenses;
using Praedra.Core.Fleets;
using Praedra.Core.Setup;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Root: shows the start screen or the game screen, and runs the command-line modes (--smoke, --screenshot).
/// All rules live in Praedra.Core; the screens only draw, submit orders and call the session's StepDay.
/// </summary>
public partial class Main : Control
{
    string[] _args;
    Control _screen;

    public override void _Ready()
    {
        _args = OS.GetCmdlineUserArgs();
        Theme = Ui.Theme;
        UiSettings.Load();
        if (!_args.Contains("--playtest") && DisplayServer.GetName() != "headless") UiSettings.ApplyScale(GetTree().Root);
        bool smoke = _args.Contains("--smoke") || (DisplayServer.GetName() == "headless" && !_args.Contains("--playtest"));
        GameBoot.LoadData();
        if (_args.Contains("--playtest")) { Playtest(); return; }
        if (smoke) { Smoke(); GetTree().Quit(); return; }

        string shot = null; int shotFrames = 30;
        for (int i = 0; i < _args.Length; i++)
            if (_args[i] == "--screenshot" && i + 1 < _args.Length)
            {
                shot = _args[i + 1];
                if (i + 2 < _args.Length && int.TryParse(_args[i + 2], out int fr)) shotFrames = fr;
            }

        if (_args.Contains("--menu") || (shot == null && !_args.Contains("--newgame"))) { ShowMenu(shot, shotFrames); return; }
        var side = Arg("--side") == "Hand" ? Side.Hand : Side.Collective;
        var preset = Enum.TryParse(Arg("--preset"), true, out Preset p) ? p : Preset.Medium;
        var rules = string.Equals(Arg("--rules"), "classic", StringComparison.OrdinalIgnoreCase) ? Praedra.Core.Battle.BattleRuleset.Classic : Praedra.Core.Battle.BattleRuleset.New; // DEFAULT New (m4-battle-new; Paul 2026-10-18)
        StartGame(GameSession.New(GameBoot.LoadData(), side, preset, rules, NewGameSeed()), shot, shotFrames, true);
    }

    /// <summary>The seed of a new game: `--seed N` if given, 1 in the automated playtest (it must stay reproducible), else null = a fresh seed per game.</summary>
    int? NewGameSeed() =>
        int.TryParse(Arg("--seed"), out int n) ? n : _args.Contains("--playtest") ? 1 : null;

    string Arg(string name)
    {
        int i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    void Swap(Control next)
    {
        if (_screen != null) { RemoveChild(_screen); _screen.QueueFree(); }
        _screen = next;
        AddChild(next);
    }

    void ShowMenu(string shot = null, int shotFrames = 30)
    {
        var menu = new StartMenu();
        menu.NewGameRequested += (side, preset, rules) => StartGame(GameSession.New(GameBoot.LoadData(), side, preset, rules, NewGameSeed()), newGame: true);
        menu.LoadRequested += LoadSave;
        menu.QuitRequested += () => GetTree().Quit();
        Swap(menu);
        if (shot != null) ScreenshotLater(shot, shotFrames);
    }

    void ScreenshotLater(string path, int frames)
    {
        var t = GetTree().CreateTimer(frames / 30.0);
        t.Timeout += () => { GetViewport().GetTexture().GetImage().SavePng(path); GD.Print("Saved screenshot " + path); GetTree().Quit(); };
    }

    void LoadSave(string path)
    {
        var save = GameBoot.ReadSave(path);
        if (save == null) { GD.PrintErr("No usable save at " + path); return; }
        try { StartGame(GameSession.Load(GameBoot.LoadData(), save.Value.player, save.Value.json, save.Value.level)); }
        catch (Exception e) { GD.PrintErr("Load failed: " + e.Message); }
    }

    void StartGame(GameSession session, string shot = null, int shotFrames = 30, bool applyArgs = false, bool newGame = false)
    {
        var gs = new GameScreen();
        gs.Init(session, newGame);
        gs.BackToMenu += () => ShowMenu();
        gs.LoadRequested += LoadSave;
        Swap(gs);
        if (applyArgs) ApplyArgs(gs);
        if (shot != null) { gs.ShotPath = shot; gs.ShotFrames = shotFrames; }
    }

    void ApplyArgs(GameScreen gs)
    {
        var g = gs.G;
        for (int i = 0; i < _args.Length; i++)
        {
            switch (_args[i])
            {
                case "--days" when i + 1 < _args.Length:
                    for (int d = int.Parse(_args[i + 1]); d > 0; d--) gs.StepOne();
                    break;
                case "--zoom" when i + 1 < _args.Length:
                    gs.View.SetZoom(float.Parse(_args[i + 1], CultureInfo.InvariantCulture));
                    break;
                case "--focus" when i + 1 < _args.Length:
                    if (g.TryFindSystem(_args[i + 1], out int idx))
                    {
                        float z = 4f;
                        if (i + 2 < _args.Length && float.TryParse(_args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float zz)) z = zz;
                        gs.View.FocusOn(idx, z);
                        gs.SelectSystem(_args[i + 1]);
                    }
                    break;
                case "--howto": gs.ShotShowHowTo(); break;
                case "--open" when i + 1 < _args.Length: gs.ShotOpenSection(_args[i + 1]); break;
                case "--moveto" when i + 1 < _args.Length: gs.ShotMove(_args[i + 1]); break;
                case "--preview" when i + 1 < _args.Length: gs.ShotPreview(_args[i + 1]); break;
                case "--trap" when i + 1 < _args.Length: gs.ShotTrap(_args[i + 1]); break;
                case "--waypoint" when i + 1 < _args.Length: gs.ShotWaypoint(_args[i + 1]); break;
                case "--lookat" when i + 2 < _args.Length: gs.ShotLookAt(_args[i + 1], float.Parse(_args[i + 2], CultureInfo.InvariantCulture)); break;
                case "--filter" when i + 1 < _args.Length: gs.ShotFilter(_args[i + 1]); break;
                case "--characters": gs.ShotCharacters(); break;
                case "--click" when i + 1 < _args.Length: gs.ShotClick(_args[i + 1]); break;
                case "--pick" when i + 1 < _args.Length: gs.PickSystem(_args[i + 1]); break;
                case "--hover" when i + 1 < _args.Length: gs.HoverTextFor(_args[i + 1]); break;
                case "--missions": gs.ShotMissions(); break;
                case "--layer" when i + 1 < _args.Length: gs.ShotLayer(_args[i + 1]); break;
                case "--sectors": gs.ShotSectors(); break;
                case "--sector" when i + 1 < _args.Length: gs.ShotSector(_args[i + 1]); break;
                case "--sectorpane" when i + 2 < _args.Length: gs.ShotSectorPane(_args[i + 1], _args[i + 2]); break;
                case "--goal": gs.ShotGoal(); break;
                case "--sectorhover" when i + 1 < _args.Length: gs.ShotSectorHover(_args[i + 1]); break;
                case "--routes" when i + 1 < _args.Length: gs.SetRoutesShown(_args[i + 1] != "off"); break;
                case "--deselect": gs.View.Select(-1); break;
                case "--mapclick" when i + 1 < _args.Length: if (g.TryFindSystem(_args[i + 1], out int mc)) gs.View.Select(mc); break;
                case "--close": gs.ShotCloseWindow(); break;
                case "--gamemenu": gs.ShotOpenMenu(); break;
                case "--fleet" when i + 1 < _args.Length:
                    foreach (var f in gs.S.Game.State.Fleets.Values) if (f.Name == _args[i + 1]) gs.SelectFleet(f.Id, true);
                    break;
            }
        }
    }

    // ================================================================== headless smoke test

    static void Check(bool ok, string what)
    {
        if (!ok) { GD.PrintErr("SMOKE FAILED: " + what); throw new InvalidOperationException("smoke: " + what); }
    }

    /// <summary>Plays 60 days of a new game headless with a couple of scripted orders, and checks that a save loads back to the same game.</summary>
    void Smoke()
    {
        var data = GameBoot.LoadData();
        var s = GameSession.New(data, Side.Collective, Preset.Medium, seed: 1);
        var me = s.Player;
        Check(data.Galaxy.Systems.Count > 100, "galaxy loaded");
        var mine = s.Game.State.SystemOwners.Where(kv => kv.Value == me).Select(kv => kv.Key).ToList();
        Check(mine.Count > 0, "player owns systems");

        // scripted orders: move a fleet, build something, and one order that must be refused (bombard own system)
        var fleet = s.Game.State.Fleets.Values.First(f => f.Side == me && f.At != null && s.Host.Fleets.SpeedOf(f) > 0);
        string dest = mine.First(m => m != fleet.At && s.Host.Fleets.FindPath(fleet.At!, m, s.Host.Fleets.SpeedOf(fleet)).Ok);
        s.Track(s.Host.Fleets.SubmitMove(me, fleet.Id, dest), $"Move {fleet.Name} to {dest}");
        var yard = s.Host.Economy.Objects.FirstOrDefault(o => o.Category == Praedra.Core.Economy.ObjectCategory.Facility && o.Completed && o.Side == null
            && o.Class == "Orbital Shipyard" && s.OwnerOf(o.System!) == me);
        if (yard != null) s.Track(s.Host.Construction.SubmitBuild(me, yard.System!, YardFamily.Shipyard, "Dodger", 1), "Build a corvette");
        s.Track(s.Host.Bombardment.SubmitBombard(me, fleet.Id, fleet.At!, BombardCategory.General), "Bombard own system (expected refusal)");

        int refusals = 0, saveDay = 30; string json = null, hash = null;
        for (int day = 0; day < 60; day++)
        {
            if (day == saveDay) { json = s.SaveText(); hash = s.Host.StateHash(); }
            var oc = s.StepDay();
            refusals += oc.New.Count(m => m.Kind == MsgKind.Bad && m.Text.StartsWith("Order refused", StringComparison.Ordinal));
        }
        Check(s.Game.Day == 60, "60 days");
        Check(refusals >= 1, "the bad order was refused with a message");
        GD.Print($"OK new game: {data.Galaxy.Systems.Count} systems, {s.Game.State.Fleets.Count} fleets, day {s.Game.Day}, {s.Messages.Count} messages, {refusals} refusal(s)");
        foreach (var m in s.Messages.Take(25)) GD.Print($"  Day {m.Day}: {m.Text}");

        // save / load round trip: load the day-30 save, run to day 60, same hash as the uninterrupted game
        var loaded = GameSession.Load(data, me, json);
        Check(loaded.Host.StateHash() == hash, "loaded save has the saved hash");
        string end = s.Host.StateHash();
        for (int i = 0; i < 30; i++) loaded.StepDay();
        // the loaded game never got the day-0 orders' bookkeeping but the orders are in the save/state, so the hashes must match
        Check(loaded.Host.StateHash() == end, "loaded game continues like the original");
        GD.Print("OK save/load round trip");
        GD.Print($"OK winner: {(GameBoot.WinnerOf(s.Host)?.ToString() ?? "none yet")}");
    }
}
