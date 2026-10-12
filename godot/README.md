# Rebellion Viewer (throwaway debug viewer)

A plain window that lets you WATCH the galaxy (and later the fleets) while the rules core runs. It is not the game UI. All rules come from `core/` (`Rebellion.Core`); this folder only draws and calls `Game.Step()`.

First pass: the map. Nothing moves yet; the day counter proves the clock loop. Fleets come in a second pass (see `Scripts/FleetLayer.cs`).

## Run it on the Mac (Apple Silicon or Intel)

1. Download `RebellionViewer-mac.zip` and double-click it to unzip. You get **Rebellion Viewer.app**.
2. The app is not signed by an Apple developer account, so macOS blocks it the first time. Open **Terminal** and paste:
   ```
   xattr -dr com.apple.quarantine ~/Downloads/"Rebellion Viewer.app"
   ```
   (Change the path if the app is not in Downloads.) Then double-click the app.
   Without Terminal: double-click once, close the warning, open **System Settings > Privacy & Security**, click **Open Anyway**.
3. Look around.

## Controls

| What | How |
|---|---|
| Zoom | mouse wheel (zooms at the pointer), or `+` / `-`, or pinch on a trackpad |
| Pan | drag with the mouse, or arrow keys |
| Reset view | `Home` or the button |
| System details | hover a system; click to pin it, click empty space to unpin |
| Play / Pause | `Space` |
| Step one day | `N` |
| Speed | buttons 1x / 2x / 5x / 10x days per second (keys `1` to `4`) |

What you see:
- One dot per system, coloured by sector (same colour every run). Ilum and Csilla have a white ring and no lanes.
- Lanes: thick gold = route (tier 1), medium blue = official (tier 2), faint thin = generated (tier 3).
- Zoomed out you see sector names; zoom in and system names appear.
- The panel shows name, sector, coordinates, number of lanes, and each neighbour with its travel days at speed 100 (computed by the core's `TravelFormula.HopTicks`).
- The game is created with a fixed seed (1998), so every run is the same game.

## For developers

Needs the **Godot 4.7.1 .NET (mono)** editor and the **.NET 8 SDK**. The project is standalone: it is NOT in `Rebellion.sln` (CI has no Godot SDK) and has its own `Directory.Build.props` so the strict core settings do not apply to it. It references `../core/Rebellion.Core.csproj`.

Data: `RebellionViewer.csproj` copies `data/design/*.csv` to `godot/data/*.txt` (git-ignored) before every build, because Godot imports `.csv` as translation tables and drops them from exports. The viewer reads them from `res://data/`.

```
cd godot
dotnet build RebellionViewer.csproj          # builds the viewer and the core
godot --path .                               # run (or open this folder in the Godot editor)
godot --headless --path . -- --smoke         # no window: loads the galaxy, steps 10 days, prints "OK <systems> systems, <lanes> lanes, day 10"
```

Command-line options (after `--`): `--screenshot out.png [frames]` saves a frame and quits; `--zoom Z`; `--focus "System Name" [zoom]` (also pins it); `--days N` runs N days first.

Screenshot with the software renderer (cloud machine, no GPU):
```
xvfb-run -a -s "-screen 0 1600x900x24" godot --path . --rendering-driver opengl3 --resolution 1600x900 -- --screenshot map.png 20
```

Export a macOS universal build (ad-hoc signed, no notarization). Export templates 4.7.1 mono must be installed.
```
cd godot
mkdir -p ../build/mac
godot --headless --path . --export-release macOS ../build/mac/RebellionViewer.zip
```
`export_presets.cfg` holds the preset; `RebellionViewer.sln` exists only because Godot's exporter requires a solution next to a C# project. Builds go under `build/` (git-ignored); never commit them or Godot binaries.

If `dotnet build` cannot find `Godot.NET.Sdk 4.7.1` (when the NuGet feed does not have it), point the restore at the nupkgs shipped inside the editor download: `-p:RestoreAdditionalProjectSources=<editor folder>/GodotSharp/Tools/nupkgs`.

Files: `Scripts/Main.cs` (top bar, clock loop, info panel, smoke/screenshot flags), `Scripts/GalaxyView.cs` (map drawing, camera, hover/click), `Scripts/FleetLayer.cs` (placeholder for fleets).
